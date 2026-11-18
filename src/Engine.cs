using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIBlackHole;

record HostRequest(string Engine, string Model, string State, string Root, int Port, string Secret, Profile Profile, string Alias, string Cuda, bool Embedding, string Help);
sealed class Engine : IInferenceEngine
{
    public ModelRecord? Model { get; private set; } public EngineRecord? Runtime { get; private set; }
    readonly Paths paths; readonly SemaphoreSlim lifecycle = new(1, 1);
    Process? host; IntPtr job; CancellationTokenSource? startup;
    public string Secret { get; private set; } = "";
    public int Port { get; private set; }
    public int Pid { get; private set; }
    public bool Ready { get; private set; }
    public Profile Active { get; private set; } = Profile.Safe;
    public string Status { get; private set; } = "Stopped";
    public double StartupSeconds { get; private set; }
    public string RuntimeHash { get; private set; } = "";
    public long PeakWorkingSet { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public event Action<string>? Changed;
    public Engine(Paths paths) { this.paths = paths; }
    void Set(string status) { Status = status; Changed?.Invoke(status); }
    public HttpClient Client(TimeSpan? timeout = null)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(BaseUrl), Timeout = timeout ?? TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret); return client;
    }
    public async Task Start(ModelRecord model, EngineRecord runtime, Profile profile, CancellationToken cancellation = default, bool fallback = true)
    {
        EngineManager.Validate(profile);
        await lifecycle.WaitAsync(cancellation);
        try
        {
            if (Ready && Model?.Id == model.Id && Active == profile) return;
            if (host != null) await StopCore();
            startup?.Dispose(); startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation); startup.CancelAfter(TimeSpan.FromMinutes(10));
            var token = startup.Token;
            var file = new FileInfo(model.Path);
            if (!file.Exists) throw new IOException("MODEL UNAVAILABLE: reconnect the registered drive or select another model.");
            if (file.Length != model.Bytes || file.LastWriteTimeUtc != model.LastWriteUtc || ModelRegistry.QuickHash(model.Path) != model.QuickFingerprint) throw new IOException("Model changed. Inspect and verify it again.");
            if (model.Sha256 == null) throw new IOException("Verify the model SHA-256 once before its first launch.");
            var hardware = await Hardware.Detect();
            var compatibility = EngineManager.Check(model, runtime, profile, hardware.FreeRam);
            if (compatibility.Status == "UNSUPPORTED") throw new ArgumentException(compatibility.Reason);
            if (profile.Backend == "cuda" && hardware.FreeVramMiB < 1024) { if (!fallback) throw new IOException("CUDA has insufficient available VRAM."); profile = Profile.Safe; }
            Model = model; Runtime = runtime; RuntimeHash = runtime.Fingerprint; PeakWorkingSet = 0;
            try { await StartCore(profile, token); }
            catch when (profile.Backend != "cpu" && fallback && !token.IsCancellationRequested)
            { await StopCore(); Set("GPU load failed; trying conservative CPU fallback."); await StartCore(Profile.Safe, token); }
            model.LastSuccessfulLaunch = DateTimeOffset.UtcNow; model.State = "RUNNING";
        }
        catch (Exception e) { await StopCore(); model.State = "ERROR"; model.Error = e.Message; Set("ERROR: " + e.Message); throw; }
        finally { lifecycle.Release(); }
    }
    public async Task Restart(CancellationToken token = default) { var model = Model ?? throw new InvalidOperationException("Select a model."); var runtime = Runtime!; var profile = Active; await Stop(); await Start(model, runtime, profile, token); }
    public object GetRuntimeStatus() => new { modelId = Model?.Id, modelName = Model?.Info?.Name, ready = Ready, status = Status, pid = Pid, port = Port, profile = Active, startupSeconds = StartupSeconds, memoryBytes = SampleMemory(), peakWorkingSet = PeakWorkingSet, engineFingerprint = RuntimeHash };
    async Task StartCore(Profile profile, CancellationToken token)
    {
        Active = profile; Port = Native.FreePort(); Secret = Json.Secret(); Ready = false;
        Set($"Loading {Model!.Info!.Name} · {profile.Backend.ToUpperInvariant()} · context {profile.Context}…");
        var watch = Stopwatch.StartNew();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = paths.Root };
        info.ArgumentList.Add("--engine-host");
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase) || k.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        info.Environment["DOTNET_EnableDiagnostics"] = "0";
        host = Process.Start(info) ?? throw new IOException("Engine helper could not start.");
        job = Native.KillOnCloseJob(host);
        await host.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new HostRequest(Runtime!.Executable, Model!.Path, paths.State, paths.Root, Port, Secret, profile, Model.Id, Runtime.BackendDirectory, Model.Info!.Embedding, Runtime.Help)));
        await host.StandardInput.FlushAsync();
        string? line = await host.StandardOutput.ReadLineAsync(token);
        if (line == null || !line.StartsWith("PID:")) throw new IOException("Engine helper failed. Check the portable logs.");
        Pid = int.Parse(line[4..]);
        using var client = Client(TimeSpan.FromSeconds(3));
        while (!token.IsCancellationRequested)
        {
            if (host.HasExited) throw new IOException("Engine exited during model loading. Check diagnostics for memory or driver errors.");
            try
            {
                using var response = await client.GetAsync("/health", token);
                if (response.IsSuccessStatusCode)
                {
                    // Engine authentication is mandatory even though only loopback is bound.
                    using var unauth = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(4) };
                    using var reject = await unauth.GetAsync(BaseUrl + "/v1/models", token);
                    if (reject.StatusCode != HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Engine does not enforce its private API key. Startup refused.");
                    Ready = true; StartupSeconds = watch.Elapsed.TotalSeconds;
                    Set($"Ready · {profile.Backend.ToUpperInvariant()} · {profile.Context} context · {profile.GpuLayers} GPU layers"); return;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
            SampleMemory();
            await Task.Delay(800, token);
        }
        token.ThrowIfCancellationRequested();
    }
    public long SampleMemory()
    {
        try { using var process = Process.GetProcessById(Pid); process.Refresh(); PeakWorkingSet = Math.Max(PeakWorkingSet, process.PeakWorkingSet64); return process.WorkingSet64; } catch { if (Ready) { Ready = false; Set("Engine exited unexpectedly. Check media connection, free memory and logs before restarting."); } return 0; }
    }
    public async Task Stop()
    {
        startup?.Cancel(); await lifecycle.WaitAsync();
        try { await StopCore(); Set("Stopped · model memory released"); } finally { lifecycle.Release(); }
    }
    async Task StopCore()
    {
        Ready = false;
        if (Model != null && Model.State == "RUNNING") Model.State = Model.VerifiedEngine != null ? "READY" : "INSPECTED";
        if (host == null) return;
        bool graceful = false; int? exit = null;
        try
        {
            if (!host.HasExited)
            {
                await host.StandardInput.WriteLineAsync("STOP"); await host.StandardInput.FlushAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                try { await host.WaitForExitAsync(timeout.Token); graceful = host.ExitCode == 0; } catch (OperationCanceledException) { }
            }
            if (host.HasExited) exit = host.ExitCode;
        }
        catch (IOException) { }
        finally
        {
            if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
            Json.Save(paths.Data("last-stop.json"), new { timestamp = DateTimeOffset.UtcNow, pid = Pid, port = Port, graceful, exitCode = exit, method = graceful ? "CTRL_C_EVENT" : "job cleanup after timeout/exit" });
            host.Dispose(); host = null; Pid = 0; Secret = "";
        }
    }
    public async ValueTask DisposeAsync() => await Stop();

    // This helper's stdin is a private inherited pipe. No secrets are sent through arguments or logs.
    public static async Task<int> Host()
    {
        Stream input = Console.OpenStandardInput(); Stream output = Console.OpenStandardOutput();
        using var reader = new StreamReader(input); using var writer = new StreamWriter(output) { AutoFlush = true };
        var request = JsonSerializer.Deserialize<HostRequest>((await reader.ReadLineAsync())!)!;
        Native.AllocConsole(); Native.ShowWindow(Native.GetConsoleWindow(), 0);
        Native.ConsoleHandler handler = _ => true; Native.SetConsoleCtrlHandler(handler, true);
        var profile = request.Profile;
        var info = new ProcessStartInfo(request.Engine) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Path.GetDirectoryName(request.Engine)!, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        var arguments = new List<string> { "--model", request.Model, "--host", "127.0.0.1", "--port", request.Port.ToString(), "--alias", request.Alias, "--ctx-size", profile.Context.ToString(), "--threads", profile.Threads.ToString(), "--gpu-layers", profile.GpuLayers.ToString(), "--batch-size", profile.Batch.ToString(), "--ubatch-size", profile.UBatch.ToString(), "--parallel", "1" };
        void Option(string flag, string? value = null) { if (!request.Help.Contains(flag)) return; arguments.Add(flag); if (value != null) arguments.Add(value); }
        Option("--offline"); Option("--no-webui"); Option("--no-ui-mcp-proxy"); Option("--no-cors-credentials"); Option("--cache-ram", "256");
        Option("--chat-template-kwargs", "{\"enable_thinking\":false}"); Option("--timeout", "600"); Option("--log-prefix"); Option("--flash-attn", profile.FlashAttention);
        if (!profile.Mmap) Option("--no-mmap");
        if (request.Embedding) Option("--embedding");
        Option("--device", profile.Backend == "cuda" ? "CUDA0" : "none");
        foreach (var arg in arguments) info.ArgumentList.Add(arg);        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        info.Environment["LLAMA_API_KEY"] = request.Secret;
        info.Environment["LLAMA_CACHE"] = Path.Combine(request.State, "data", "engine-cache");
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["CUDA_CACHE_DISABLE"] = "1";
        info.Environment["CUDA_VISIBLE_DEVICES"] = "0";
        info.Environment["PATH"] = Path.GetDirectoryName(request.Engine) + ";" + request.Cuda + ";" + Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (profile.Backend == "cuda") info.Environment["GGML_BACKEND_PATH"] = Path.Combine(request.Cuda, "ggml-cuda.dll");
        using var child = Process.Start(info)!;
        await writer.WriteLineAsync("PID:" + child.Id);
        var logFile = Path.Combine(request.State, "data", "logs", "engine-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + child.Id + ".log");
        using var log = new StreamWriter(logFile) { AutoFlush = true }; var logLock = new SemaphoreSlim(1, 1);
        async Task Capture(StreamReader stream)
        {
            while (await stream.ReadLineAsync() is { } line)
            {
                // Engine debug output may quote prompts. Do not log request content or auth values.
                if (line.Contains(request.Secret, StringComparison.Ordinal) || line.Contains("Authorization", StringComparison.OrdinalIgnoreCase) || line.Contains("prompt:", StringComparison.OrdinalIgnoreCase)) continue;
                await logLock.WaitAsync(); try { await log.WriteLineAsync(line); } finally { logLock.Release(); }
            }
        }
        var capture1 = Capture(child.StandardOutput); var capture2 = Capture(child.StandardError);
        var command = reader.ReadLineAsync(); var exit = child.WaitForExitAsync();
        if (await Task.WhenAny(command, exit) == command && !child.HasExited)
        {
            // EOF means the owning launcher died; also drain the engine in that case.
            Native.GenerateConsoleCtrlEvent(0, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(115));
            try { await child.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { child.Kill(true); await child.WaitForExitAsync(); }
        }
        await exit; await Task.WhenAll(capture1, capture2); GC.KeepAlive(handler);
        return child.ExitCode;
    }
}
