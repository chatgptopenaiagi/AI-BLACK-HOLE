using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AIBlackHole;

record EngineRecord(string Id, string Kind, string Executable, string BackendDirectory, string Version, string Fingerprint, string[] Backends, string Help, string Status);
record Compatibility(string Status, string Reason, long EstimatedRamBytes, string Architecture, string EngineId);
interface IInferenceEngine : IAsyncDisposable
{
    Task Start(ModelRecord model, EngineRecord runtime, Profile profile, CancellationToken token = default, bool fallback = true);
    Task Stop();
    Task Restart(CancellationToken token = default);
    object GetRuntimeStatus();
    HttpClient Client(TimeSpan? timeout = null);
    long SampleMemory();
}
static class EngineManager
{
    public static async Task<List<EngineRecord>> Discover(Paths paths, IEnumerable<string>? selected = null)
    {
        var candidates = new List<string> { Path.Combine(paths.Root, "runtime", "cpu", "llama-server.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim('"'), "llama-server.exe"));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Programs", "Ollama", "lib", "ollama", "llama-server.exe"));
        if (selected != null) candidates.AddRange(selected);
        var result = new List<EngineRecord>();
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
        {
            try
            {
                string executable = Path.GetFullPath(candidate); string help = await Probe(executable, "--help"), version = await Probe(executable, "--version");
                if (!help.Contains("--api-key") || !help.Contains("--model") || !help.Contains("--host")) continue;
                string cpu = Path.GetDirectoryName(executable)!;
                string cuda = new[] { Path.Combine(Path.GetDirectoryName(cpu)!, "cuda"), Path.Combine(cpu, "cuda_v12") }.FirstOrDefault(d => File.Exists(Path.Combine(d, "ggml-cuda.dll"))) ?? "";
                var hashes = new List<string>();
                foreach (var f in Directory.GetFiles(cpu).Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p == executable).Concat(cuda == "" ? [] : Directory.GetFiles(cuda, "*.dll")).Order()) hashes.Add(Path.GetFileName(f) + ":" + await Json.FileHash(f));
                string hash = Json.Hash(string.Join("\n", hashes));
                result.Add(new(hash[..16], "llama.cpp", executable, cuda, version.Trim(), hash, cuda == "" ? ["cpu"] : ["cpu", "cuda"], help, "DETECTED; model compatibility requires a load test"));
            }
            catch (Exception e) { result.Add(new(Json.Hash(candidate)[..16], "unknown", candidate, "", "", "", [], "", "ERROR: " + e.Message)); }
        }
        return result;
    }
    public static async Task<string> Probe(string executable, string argument)
    {
        using var p = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        p.StartInfo.ArgumentList.Add(argument); p.Start(); var a = p.StandardOutput.ReadToEndAsync(); var b = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await p.WaitForExitAsync(timeout.Token); } catch { p.Kill(true); throw; }
        return (await a) + (await b);
    }
    public static Compatibility Check(ModelRecord model, EngineRecord engine, Profile profile, ulong available)
    {
        var info = model.Info ?? throw new ArgumentException("Inspect this GGUF first.");
        long kv = Math.Max(512L * 1024 * 1024, checked((long)Math.Max(1, info.Layers) * profile.Context * Math.Max(128, info.Number(info.Architecture + ".attention.head_count_kv", 8) * 128) * 4));
        long estimate = checked(model.Bytes + kv + 1024L * 1024 * 1024);
        string reason = "Architecture recognition and actual memory use must be verified by this engine's load test.";
        string status = "COMPATIBILITY UNKNOWN";
        if (info.Auxiliary) { status = "UNSUPPORTED"; reason = "Projector/auxiliary model cannot run independently."; }
        else if (model.Shards.Any(p => !File.Exists(p))) { status = "UNSUPPORTED"; reason = "Required GGUF shards are missing."; }
        else if (model.Shards.Length > 1) { status = "UNSUPPORTED"; reason = "Split files are identified; full-shard integrity and loading are not verified in this beta."; }
        else if (!engine.Backends.Contains(profile.Backend)) { status = "UNSUPPORTED"; reason = "Selected runtime does not contain that acceleration backend."; }
        else if ((ulong)(estimate + 3L * 1024 * 1024 * 1024) > available) { status = "UNSUPPORTED"; reason = "Insufficient available RAM for this conservative estimate plus 3 GiB system headroom."; }
        else if (info.Context > 0 && profile.Context > info.Context) { status = "UNSUPPORTED"; reason = "Requested context exceeds the model's declared context."; }
        else if (model.VerifiedEngine == engine.Fingerprint) { status = "COMPATIBLE"; reason = "This model previously completed inference on this exact engine."; }
        return new(status, reason, estimate, info.Architecture, engine.Id);
    }
    public static void Validate(Profile p)
    {
        if (p.Threads < 1 || p.Threads > Environment.ProcessorCount || p.Context is < 256 or > 32768 || p.GpuLayers is < 0 or > 999 || p.Batch is < 16 or > 2048 || p.UBatch < 16 || p.UBatch > p.Batch || p.Parallel != 1 || p.FlashAttention is not ("auto" or "on" or "off")) throw new ArgumentException("Invalid profile. This beta permits one slot and context 256–32768.");
    }
}
