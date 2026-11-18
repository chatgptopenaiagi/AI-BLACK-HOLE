using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AIBlackHole;

record ConnectorManifest(string Id, string Direction, string[] Implemented, string[] Unsupported, string Status, bool SendsCloudContent);
interface IAgentConnector
{
    ConnectorManifest Manifest { get; }
    Task<object> Send(string text, CancellationToken token);
}
static class Connectors
{
    public static ConnectorManifest[] Manifests => [
        new("local", "ABH → registered model", ["text", "chat", "streaming", "cancellation", "usage"], ["capabilities require per-model tests"], "Available when model running", false),
        new("codex", "ABH → Codex App Server", ["stdio", "initialize", "threads", "turns", "events", "approval decisions", "interrupt"], ["cloud authentication is not copied", "custom/freeform tools through local provider"], "Optional installed executable; isolated local profile", false),
        new("codex-local-provider", "Codex → ABH", ["Responses", "function namespace mapping", "exact call IDs"], ["hosted tools", "custom tools", "general compatibility guarantee"], "Per-model acceptance required", false),
        new("OpenAI", "ABH → OpenAI API", ["text", "usage", "timeout", "cancellation", "bounded calls"], ["streaming", "structured output", "tools"], "Disabled by default; mock acceptance only", true),
        new("Claude", "ABH → Anthropic Messages", ["text blocks", "usage", "timeout", "cancellation", "bounded calls"], ["streaming", "structured output", "tools"], "Disabled by default; mock acceptance only", true),
        new("Grok", "ABH → xAI Chat Completions", ["text", "usage", "timeout", "cancellation", "bounded calls"], ["streaming", "structured output", "tools"], "Disabled by default; mock acceptance only", true),
        new("DRAGONHYDRA", "Read-only versioned API / explicit JSON import → local analysis", ["health", "identity", "capabilities", "analysis export"], ["database editing", "automatic writeback"], "Live service availability checked separately", false),
        new("ARX", "Machine DNA import/export", ["schema evidence exchange", "installed product detection"], ["compatibility verdict without ARX execution"], "Schema exchange; actual scanner separately tested", false),
        new("mcp-server", "MCP clients → ABH", ["stdio", "Streamable HTTP", "registered model tools"], ["arbitrary filesystem access", "shell execution"], "Authenticated local HTTP / process-owner stdio", false),
        new("mcp-client", "ABH → selected MCP HTTP server", ["discovery", "list tools", "explicit tool call"], ["automatic recursive tool use", "remote server commands"], "Loopback approved endpoint only", false),
        new("generic-local", "ABH → selected local endpoint", ["model listing", "chat completions", "timeout", "cancellation"], ["protocol equivalence guarantee"], "Loopback endpoint only", false)
    ];
    public static Uri LocalUri(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) throw new ArgumentException("Select an http://127.0.0.1:port endpoint. Remote access is not enabled.");
        return uri;
    }
    public static async Task<object> Generic(string endpoint, string model, string text, CancellationToken token)
    {
        var uri = LocalUri(endpoint); using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
        using var response = await http.PostAsJsonAsync(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions"), new { model, messages = new[] { new { role = "user", content = text } }, max_tokens = 256, stream = false }, token);
        response.EnsureSuccessStatusCode(); return new { source = "generic-local", endpoint = uri.Authority, body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) };
    }
    public static object ArxEvidence(Hardware h) => new
    {
        generated_at = DateTimeOffset.UtcNow.ToString("O"), os = new { name = "Windows", version = Environment.OSVersion.Version.ToString(), architecture = "x64" },
        tools = new { abh = new { version = Paths.Version, required_host_runtime = false } }, cpu = new { name = h.Cpu, logical_processors = h.Threads },
        resource_pressure = new { total_ram_bytes = h.TotalRam, available_ram_bytes = h.FreeRam },
        gpu_compute = new { gpus = new[] { new { name = h.Gpu, vram_mib = h.VramMiB, free_vram_mib = h.FreeVramMiB } }, driver = h.Driver },
        evidence = new[] { new { id = "abh-native", provenance = "observed", source = "Windows memory API, CPU registry, NVIDIA query" } },
        machine_fingerprint = h.Fingerprint, limitations = "ABH native evidence in ARX Machine DNA format. Not an ARX scanner execution or compatibility verdict."
    };
    public static JsonNode ImportArx(string json)
    {
        if (json.Length > 1024 * 1024) throw new ArgumentException("Machine DNA exceeds 1 MiB.");
        var value = JsonNode.Parse(json) ?? throw new ArgumentException("Invalid JSON.");
        foreach (string key in new[] { "generated_at", "os", "tools", "evidence" }) if (value[key] == null) throw new ArgumentException("Machine DNA is missing " + key);
        return value;
    }
}
record WorkflowRequest(string Mode, string[] Agents, string Prompt, int MaxSteps = 3, int MaxTokens = 256, int TimeSeconds = 180, int MaxConcurrent = 2, decimal CloudBudget = 0);
static class Coordinator
{
    public static Job Submit(ApplicationService app, WorkflowRequest request)
    {
        if (request.MaxSteps is < 1 or > 6 || request.Agents.Length is < 1 or > 4 || request.Agents.Length > request.MaxSteps || request.MaxTokens is < 1 or > 512 || request.TimeSeconds is < 5 or > 900 || request.MaxConcurrent is < 1 or > 2 || request.Prompt.Length is < 1 or > 12000) throw new ArgumentException("Workflow limits exceeded.");
        if (request.Agents.Any(a => a is not ("local" or "ARX" or "DRAGONHYDRA"))) throw new ArgumentException("Automatic workflows allow local model, ARX evidence and DRAGONHYDRA health. Use an explicit provider handoff or Codex task for other agents; cloud budget remains zero.");
        if (request.CloudBudget != 0) throw new ArgumentException("Cloud spending is disabled for automatic workflows in this beta.");
        if (request.Mode is not ("LOCAL MODEL ONLY" or "SEQUENTIAL HANDOFF" or "PARALLEL RESPONSE COMPARISON" or "REVIEW AND REFINE" or "ARX HARDWARE DIAGNOSTICS" or "DRAGONHYDRA DATA ANALYSIS" or "MANUAL MULTI-AGENT WORKFLOW")) throw new ArgumentException("Unsupported workflow mode.");
        return app.Submit("WORKFLOW", async (job, outer) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer); deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeSeconds));
            var responses = new System.Collections.Concurrent.ConcurrentBag<object>(); string current = request.Prompt; int steps = 0;
            async Task<string> Run(string agent, string input)
            {
                if (Interlocked.Increment(ref steps) > request.MaxSteps) throw new ArgumentException("Step budget exhausted.");
                try
                {
                    object result;
                    if (agent == "local") { await app.Inference.WaitAsync(deadline.Token); try { result = await app.Generate(input, request.MaxTokens, deadline.Token); } finally { app.Inference.Release(); } }
                    else if (agent == "ARX") result = Connectors.ArxEvidence(await Hardware.Detect());
                    else result = await app.Providers.DiscoverLocalBridges();
                    responses.Add(new { agent, status = "COMPLETED", result }); return Json.Text(result);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { responses.Add(new { agent, status = "FAILED", error = e.Message }); return "Previous stage failed: " + e.Message; }
            }
            if (request.Mode == "PARALLEL RESPONSE COMPARISON")
            {
                using var slots = new SemaphoreSlim(request.MaxConcurrent); await Task.WhenAll(request.Agents.Select(async a => { await slots.WaitAsync(deadline.Token); try { await Run(a, current); } finally { slots.Release(); } }));
            }
            else foreach (var agent in request.Agents) { deadline.Token.ThrowIfCancellationRequested(); current = await Run(agent, current.Length > 12000 ? current[..12000] : current); job.Progress = 100.0 * steps / request.Agents.Length; }
            var report = new { request.Mode, steps, maxSteps = request.MaxSteps, maxToolCalls = 0, maxTotalOutputTokens = request.MaxTokens * request.MaxSteps, cloudSpent = 0, responses = responses.ToArray(), timestamp = DateTimeOffset.UtcNow };
            Json.Save(app.Paths.Data("workflows/" + job.Id + ".json"), report); return report;
        }, false);
    }
}
