using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AIBlackHole;

sealed class Job
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Kind { get; init; } = "";
    public string Status { get; set; } = "QUEUED";
    public string Message { get; set; } = "";
    public double Progress { get; set; }
    public object? Result { get; set; }
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Finished { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public CancellationTokenSource Cancel { get; } = new(TimeSpan.FromMinutes(30));
}
sealed record Preferences(bool AllowNetworkShares = false, string Language = "en", string[]? DiscoveryRoots = null, string[]? RuntimeExecutables = null, string? CodexExecutable = null, string? ArxExecutable = null);
sealed class ApplicationService : IAsyncDisposable
{
    public Paths Paths { get; }
    public ModelRegistry Models { get; }
    public Engine Engine { get; }
    public KeyStore Keys { get; }
    public Vault Vault { get; }
    public Providers Providers { get; }
    public List<EngineRecord> Engines { get; private set; } = [];
    public Hardware? Hardware { get; private set; }
    public Preferences Settings { get; private set; }
    public ConcurrentDictionary<string, Job> Jobs { get; } = new();
    public SemaphoreSlim Inference { get; } = new(1, 1);
    readonly SemaphoreSlim mutation = new(1, 1);
    DateTime lastAvailabilityCheck = DateTime.MinValue;
    public ApplicationService(Paths paths)
    {
        Paths = paths; Models = new(paths); Engine = new(paths); Keys = new(paths.Data("keys.json")); Vault = new(paths.VaultFile); Providers = new(Vault);
        Settings = File.Exists(paths.Data("preferences.json")) ? Json.Read<Preferences>(paths.Data("preferences.json")) : new();
    }
    public async Task Discover() { Hardware = await AIBlackHole.Hardware.Detect(); Engines = await EngineManager.Discover(Paths, Settings.RuntimeExecutables); }
    public void SaveSettings(Preferences value) { if (value.Language != "en") throw new ArgumentException("English is the complete beta interface; Romanian and German are planned."); Settings = value; Json.Save(Paths.Data("preferences.json"), value); }
    public Job Submit(string kind, Func<Job, CancellationToken, Task<object?>> work, bool exclusive = true)
    {
        if (Jobs.Values.Count(j => j.Finished == null) >= 4) throw new ArgumentException("Four jobs already active. Cancel or wait for completion.");
        foreach (var old in Jobs.Values.Where(j => j.Finished != null).OrderByDescending(j => j.Started).Skip(40)) Jobs.TryRemove(old.Id, out _);
        var job = new Job { Kind = kind }; Jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            bool locked = false;
            try
            {
                if (exclusive) { await mutation.WaitAsync(job.Cancel.Token); locked = true; }
                job.Status = "RUNNING"; job.Result = await work(job, job.Cancel.Token); job.Progress = 100; job.Status = "COMPLETED";
            }
            catch (OperationCanceledException) { job.Status = "CANCELLED"; job.Message = "Cancelled or time budget reached."; }
            catch (Exception e) { job.Status = "FAILED"; job.Message = e.Message; }
            finally { if (locked) mutation.Release(); job.Finished = DateTimeOffset.UtcNow; }
        }); return job;
    }
    public Job Verify(string id) => Submit("VERIFY", async (job, ct) => await Models.Verify(Models.Get(id), new Progress<double>(p => { job.Progress = p; job.Message = $"SHA-256 {p:F1}%"; }), ct));
    public Job Scan(string root, bool blobs = false) => Submit("SCAN", (job, ct) => Task.Run<object?>(() =>
    {
        var found = new List<string>(); var rejected = new List<string>();
        foreach (var path in Models.Scan(root, Settings.AllowNetworkShares, ct, blobs: blobs))
        {
            job.Message = "Inspecting " + Path.GetFileName(path);
            try { found.Add(Models.Add(path, Settings.AllowNetworkShares, ct).Id); } catch (OperationCanceledException) { throw; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { if (rejected.Count < 50) rejected.Add(Path.GetFileName(path) + ": " + e.Message); }
        }
        return new { found, rejected, limits = "10,000 entries, depth 8, two minutes; junctions skipped" };
    }, ct));
    public Job Start(string id, Profile profile, string? runtimeId = null) => Submit("START", async (job, ct) =>
    {
        await Inference.WaitAsync(ct);
        try { var model = Models.Get(id); var runtime = Engines.FirstOrDefault(e => runtimeId == null ? e.Kind == "llama.cpp" : e.Id == runtimeId) ?? throw new ArgumentException("Discover or select a compatible runtime first.");
            await Engine.Start(model, runtime, profile, ct); Models.Save(); return Engine.GetRuntimeStatus(); }
        finally { Inference.Release(); }
    });
    public async Task<object> Stop()
    {
        foreach (var job in Jobs.Values.Where(j => j.Finished == null && j.Kind is "START" or "CALIBRATE")) job.Cancel.Cancel();
        await Engine.Stop(); Models.Save(); return Engine.GetRuntimeStatus();
    }
    public Job Calibrate(string id) => Submit("CALIBRATE", async (job, ct) =>
    {
        await Inference.WaitAsync(ct); try { return await Calibrator.Run(this, Models.Get(id), job, ct); } finally { Inference.Release(); }
    });
    public async Task<JsonNode> Generate(string text, int tokens, CancellationToken ct)
    {
        if (!Engine.Ready || Engine.Model == null) throw new ArgumentException("Start a model first.");
        if (Engine.Model.Info!.Embedding) throw new ArgumentException("This is an embedding model. Use /v1/embeddings.");
        using var client = Engine.Client();
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = Engine.Model.Id, messages = new[] { new { role = "user", content = text } }, max_tokens = Math.Clamp(tokens, 1, 2048), temperature = 0, seed = 42, cache_prompt = false, stream = false }, ct);
        if (!response.IsSuccessStatusCode) throw new IOException("Inference returned HTTP " + (int)response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
    }
    public object Status()
    {
        if ((DateTime.UtcNow - lastAvailabilityCheck).TotalSeconds > 5) { lastAvailabilityCheck = DateTime.UtcNow; Models.Refresh(); }
        var memory = AIBlackHole.Hardware.Memory();
        return new { product = "AI BLACK HOLE", version = Paths.Version, runtime = Engine.GetRuntimeStatus(), hardware = Hardware, freeMemoryBytes = memory.free, models = Models.All(), engines = Engines.Select(e => e with { Help = "" }), jobs = Jobs.Values.OrderByDescending(j => j.Started).Take(12), connectors = Connectors.Manifests, cloud = Providers.PublicStatus() };
    }
    public async ValueTask DisposeAsync() { foreach (var job in Jobs.Values.Where(j => j.Finished == null)) job.Cancel.Cancel(); await Engine.DisposeAsync(); Vault.Dispose(); }
}

record CandidateResult(Profile Profile, string Status, double StartupSeconds, double ElapsedSeconds, double? PromptTokensPerSecond, double? OutputTokensPerSecond, long PeakRamBytes, int PeakDeviceVramMiB, string? Answer, string? Error, bool QualityPassed)
{
    public string? QualityAnswer { get; init; }
    public double? WarmRequestSeconds { get; init; }
    public object? BenchmarkUsage { get; init; }
}
record CalibrationReport(string ModelSha256, string EngineFingerprint, string HardwareFingerprint, DateTimeOffset Date, List<CandidateResult> Candidates, Profile? Safe, Profile? Balanced, Profile? FastestTested, string Conditions);
static class Calibrator
{
    public static string ReportPath(ApplicationService app, ModelRecord model) => app.Paths.Data("calibration/" + model.Id + ".json");
    public static async Task<CalibrationReport> Run(ApplicationService app, ModelRecord model, Job job, CancellationToken token)
    {
        var hardware = await Hardware.Detect(); var runtime = app.Engines.FirstOrDefault(e => e.Kind == "llama.cpp") ?? throw new ArgumentException("No supported installed engine.");
        if (model.Sha256 == null) { job.Message = "Verifying model integrity before calibration"; await app.Models.Verify(model, new Progress<double>(p => job.Progress = p / 5), token); }
        int context = (int)Math.Min(2048, model.Info!.Context > 0 ? model.Info.Context : 2048);
        var safe = Profile.Safe with { Context = context }; var candidates = new List<Profile> { safe, safe with { Name = "CPU candidate", Threads = Math.Max(1, safe.Threads / 2), Batch = 256, UBatch = 128 } };
        if (runtime.Backends.Contains("cuda") && hardware.FreeVramMiB > 1500 && model.Info.Layers > 0)
        {
            int layers = Math.Clamp((int)((hardware.FreeVramMiB - 1024) * 1048576.0 * .7 / Math.Max(1, model.Bytes) * model.Info.Layers), 1, model.Info.Layers);
            candidates.Add(safe with { Name = "GPU candidate", Backend = "cuda", GpuLayers = layers, Context = (int)Math.Min(4096, model.Info.Context > 0 ? model.Info.Context : 2048), Batch = 256, UBatch = 128 });
        }
        var results = new List<CandidateResult>(); Profile? lastGood = null;
        try
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var profile = candidates[i]; job.Progress = 20 + 75.0 * i / candidates.Count; job.Message = $"Candidate {i + 1}/{candidates.Count}: {profile.Name}";
                await app.Engine.Stop(); var watch = Stopwatch.StartNew(); string? answer = null, qualityAnswer = null; double? prefill = null, output = null; int vram = 0; bool quality = false; double? warmSeconds = null;
                using var sampler = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task sampling = Task.CompletedTask;
                try
                {
                    var check = EngineManager.Check(model, runtime, profile, Hardware.Memory().free); if (check.Status == "UNSUPPORTED") throw new IOException(check.Reason);
                    await app.Engine.Start(model, runtime, profile, token, false);
                    var loadedHardware = await Hardware.Detect(); vram = loadedHardware.VramMiB - loadedHardware.FreeVramMiB; app.Engine.SampleMemory();
                    sampling = Task.Run(async () => { while (!sampler.IsCancellationRequested) { app.Engine.SampleMemory(); var current = await Hardware.Detect(); vram = Math.Max(vram, current.VramMiB - current.FreeVramMiB); await Task.Delay(1000, sampler.Token); } }, sampler.Token);
                    JsonNode reply;
                    if (model.Info.Embedding)
                    {
                        using var client = app.Engine.Client(); using var response = await client.PostAsJsonAsync("/v1/embeddings", new { model = model.Id, input = "ABH calibration" }, token); response.EnsureSuccessStatusCode(); reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
                        quality = reply["data"]?[0]?["embedding"] is JsonArray vector && vector.Count > 0; answer = quality ? "Nonempty embedding vector returned" : "Invalid embedding response";
                    }
                    else
                    {
                        reply = await app.Generate("Write a concise paragraph about how a library organizes books. Aim for about 60 words.", 64, token);
                        answer = reply["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
                        var warm = Stopwatch.StartNew(); var evaluation = await app.Generate("What is 7 + 5? Reply with only the number.", 24, token); warmSeconds = warm.Elapsed.TotalSeconds;
                        qualityAnswer = evaluation["choices"]?[0]?["message"]?["content"]?.GetValue<string>(); quality = qualityAnswer?.Trim().Trim('.') == "12";
                    }
                    prefill = reply["timings"]?["prompt_per_second"]?.GetValue<double>(); output = reply["timings"]?["predicted_per_second"]?.GetValue<double>();
                    if (string.IsNullOrWhiteSpace(answer)) throw new IOException("Inference returned no usable output.");
                    var measuredHardware = await Hardware.Detect(); vram = Math.Max(vram, measuredHardware.VramMiB - measuredHardware.FreeVramMiB); app.Engine.SampleMemory();
                    results.Add(new(profile, "VERIFIED", app.Engine.StartupSeconds, watch.Elapsed.TotalSeconds, prefill, output, app.Engine.PeakWorkingSet, vram, answer, null, quality) { QualityAnswer = qualityAnswer, WarmRequestSeconds = warmSeconds, BenchmarkUsage = reply["usage"]?.DeepClone() });
                    lastGood = profile; model.VerifiedEngine = runtime.Fingerprint;
                    model.Capabilities[model.Info.Embedding ? "EMBEDDINGS" : "TEXT_GENERATION"] = "TESTED";
                    if (!model.Info.Embedding) model.Capabilities["CHAT"] = "TESTED";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { results.Add(new(profile, "FAILED", app.Engine.StartupSeconds, watch.Elapsed.TotalSeconds, prefill, output, app.Engine.PeakWorkingSet, vram, answer, e.Message, false)); await app.Engine.Stop(); }
                finally { sampler.Cancel(); try { await sampling; } catch (OperationCanceledException) { } }
            }
        }
        finally
        {
            await app.Engine.Stop();
            // Persist partial measurements even on cancellation. Never start a new load after user cancellation.
            Json.Save(app.Paths.Data("calibration/" + model.Id + ".attempt.json"), new { date = DateTimeOffset.UtcNow, results, cancelled = token.IsCancellationRequested });
            if (!token.IsCancellationRequested && lastGood != null) { job.Message = "Restoring last verified profile"; await app.Engine.Start(model, runtime, lastGood); }
        }
        var passed = results.Where(r => r.Status == "VERIFIED").ToArray();
        var fastest = passed.Where(r => r.OutputTokensPerSecond > 0).OrderByDescending(r => r.OutputTokensPerSecond).FirstOrDefault()?.Profile ?? passed.LastOrDefault()?.Profile;
        var report = new CalibrationReport(model.Sha256!, runtime.Fingerprint, hardware.Fingerprint, DateTimeOffset.UtcNow, results, passed.FirstOrDefault(r => r.Profile.Backend == "cpu")?.Profile, passed.LastOrDefault()?.Profile, fastest, "Bounded candidates; one deterministic paragraph capped at 64 output tokens plus a separate warm arithmetic request capped at 24 tokens (embedding vector for embedding models). Temperature 0, seed 42, prompt cache disabled. First load may use OS disk cache; not a controlled cold-disk benchmark. RAM: engine working set; VRAM: sampled total device usage including other applications, with synchronous samples before and after requests. Quality requires exactly 12: a tiny instruction-following sanity check, not general accuracy. Fastest only among measured candidates. KV cache and attention defaults are not exhaustively optimized.");
        Json.Save(ReportPath(app, model), report); model.State = passed.Length > 0 ? "CALIBRATED" : "ERROR"; app.Models.Save();
        if (passed.Length == 0) throw new IOException("No calibration candidate completed inference. Inspect candidate errors.");
        return report;
    }
    public static Profile Select(ApplicationService app, ModelRecord model, string name)
    {
        if (name == "SAFE") return Profile.Safe with { Context = (int)Math.Min(2048, model.Info?.Context > 0 ? model.Info.Context : 2048) };
        string file = ReportPath(app, model); if (!File.Exists(file)) throw new ArgumentException("Run AUTO CALIBRATE before selecting a measured profile.");
        var report = Json.Read<CalibrationReport>(file);
        if (report.ModelSha256 != model.Sha256 || !app.Engines.Any(e => e.Fingerprint == report.EngineFingerprint) || report.HardwareFingerprint != app.Hardware?.Fingerprint) throw new ArgumentException("Calibration identity changed. Recalibrate this model and hardware.");
        return (name == "FASTEST TESTED" ? report.FastestTested : report.Balanced) ?? throw new ArgumentException("That profile has no successful test.");
    }
}
