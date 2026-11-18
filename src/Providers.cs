using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AIBlackHole;

record ProviderResult(string Provider, string Model, string Text, DateTimeOffset Timestamp, object? Usage, decimal? ObservedCost, string Provenance);
sealed class Providers
{
    readonly Vault vault; readonly HttpClient http; readonly Dictionary<string, int> counts = new(); readonly object sync = new();
    public static readonly string[] Names = ["OpenAI", "Claude", "Grok", "DRAGONHYDRA"];
    public Providers(Vault vault, HttpMessageHandler? handler = null)
    {
        this.vault = vault; http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(90) };
    }
    public object PublicStatus() => Names.Select(p => new { provider = p, configured = vault.Get(p) != null, enabled = vault.Get(p)?.Enabled == true, model = vault.Get(p)?.Model ?? "", limit = vault.Get(p)?.MaxCallsPerSession ?? 0, calls = Used(p), reservedUsd = Reserved(p), budgetUsd = vault.Get(p)?.SessionBudgetUsd ?? 0 }).ToArray();
    decimal Reserved(string provider) { lock (sync) return reserved.GetValueOrDefault(provider); }
    int Used(string provider) { lock (sync) return counts.GetValueOrDefault(provider); }
    readonly Dictionary<string, decimal> reserved = new();
    static readonly IProviderAdapter[] adapters = [new OpenAiAdapter(), new ClaudeAdapter(), new GrokAdapter()];
    public async Task<ProviderResult> Call(string provider, string prompt, CancellationToken token = default)
    {
        if (prompt.Length is < 1 or > 16000) throw new ArgumentException("Cloud input must contain 1–16,000 characters.");
        var reply = await Exchange(provider, ProviderPayload.Prompt(prompt), false, null, token);
        var adapter = adapters.Single(a => a.Name == provider);
        return new(provider, reply["model"]?.GetValue<string>() ?? vault.Get(provider)!.Model, adapter.Text(reply), DateTimeOffset.UtcNow, reply["usage"]?.DeepClone(), null, "Explicit provider API request; native usage returned. Session reservations are spending controls, not a provider invoice.");
    }
    public async Task<JsonNode> Exchange(string provider, JsonObject options, bool stream, Func<JsonNode, Task>? emit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var credential = vault.Get(provider) ?? throw new ArgumentException("Unlock the vault and configure this provider first.");
        if (!credential.Enabled) throw new ArgumentException("Provider is disabled.");
        var adapter = adapters.FirstOrDefault(a => a.Name == provider) ?? throw new ArgumentException("Unknown cloud adapter.");
        if (credential.MaxTokens is < 1 or > 512 || credential.MaxCallsPerSession is < 1 or > 20 || string.IsNullOrWhiteSpace(credential.Model)) throw new ArgumentException("Set a model, 1–512 output tokens and 1–20 calls per session.");
        using var request = adapter.Build(credential, options, stream);
        lock (sync)
        {
            int count = counts.GetValueOrDefault(provider); decimal total = reserved.GetValueOrDefault(provider);
            if (count >= credential.MaxCallsPerSession) throw new ArgumentException("Provider session call limit reached.");
            if (credential.RequestReservationUsd <= 0 || credential.SessionBudgetUsd <= 0 || total + credential.RequestReservationUsd > credential.SessionBudgetUsd) throw new ArgumentException("Set a positive per-request spending reservation and session budget. No request was sent.");
            counts[provider] = count + 1; reserved[provider] = total + credential.RequestReservationUsd;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new IOException($"{provider} returned HTTP {(int)response.StatusCode}. Check credential, quota and native model capability. No automatic retry was made; the reservation is retained.");
        if (!stream)
        {
            string content = await response.Content.ReadAsStringAsync(deadline.Token); if (content.Length > 1024 * 1024) throw new IOException("Provider response exceeds 1 MiB.");
            return JsonNode.Parse(content) ?? throw new IOException("Empty provider response.");
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream") throw new IOException("Expected provider SSE stream.");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var events = new JsonArray(); var text = new StringBuilder(); var data = new StringBuilder(); bool complete = false; int bytes = 0;
        async Task Dispatch()
        {
            string value = data.ToString(); data.Clear(); if (value.Length == 0) return;
            if (value.Trim() == "[DONE]") { complete = true; return; }
            var item = JsonNode.Parse(value)!;
            if (item["error"] != null || item["type"]?.GetValue<string>() == "error") throw new IOException("Provider reported an error during streaming.");
            complete |= adapter.Complete(item); text.Append(adapter.Delta(item)); events.Add(item.DeepClone());
            if (events.Count > 4096) throw new IOException("Provider stream event limit reached.");
            if (emit != null) await emit(item);
        }
        while (await reader.ReadLineAsync(deadline.Token) is { } line)
        {
            bytes += line.Length; if (bytes > 1024 * 1024) throw new IOException("Provider stream exceeds 1 MiB.");
            if (line.Length == 0) await Dispatch();
            else if (line.StartsWith("data:")) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart()); }
        }
        await Dispatch(); if (!complete) throw new IOException("Provider stream ended before completion.");
        return new JsonObject { ["provider"] = provider, ["model"] = credential.Model, ["text"] = text.ToString(), ["events"] = events, ["complete"] = true, ["provenance"] = "External provider stream; tool IDs and argument fragments retained verbatim; no tools executed" };
    }
    public async Task<object> Dragon(bool authenticated)
    {
        const string root = "http://127.0.0.1:18821/dragon/api/v1";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var health = await http.GetAsync(root + "/health/live", timeout.Token);
        health.EnsureSuccessStatusCode();
        var identity = JsonNode.Parse(await health.Content.ReadAsStringAsync(timeout.Token));
        if (identity?["product"]?.GetValue<string>() != "DRAGONHYDRA CORE NEXT") throw new ArgumentException("Unexpected product identity at the local endpoint; connection refused.");
        if (!authenticated) return new { status = "discovered; authentication not yet tested", endpoint = root, identity };
        var credential = vault.Get("DRAGONHYDRA") ?? throw new ArgumentException("Supply the owner-approved DRAGONHYDRA bearer credential in the vault. ABH does not read another project's secrets.");
        if (!credential.Enabled) throw new ArgumentException("Enable the DRAGONHYDRA connection first.");
        using var request = new HttpRequestMessage(HttpMethod.Get, root + "/modules"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
        using var result = await http.SendAsync(request, timeout.Token);
        if (!result.IsSuccessStatusCode) throw new ArgumentException("DRAGONHYDRA authentication failed: HTTP " + (int)result.StatusCode);
        var modules = JsonNode.Parse(await result.Content.ReadAsStringAsync(timeout.Token));
        if (modules?["schema_version"]?.GetValue<string>() != "core-next.modules/1") throw new ArgumentException("Unsupported DRAGONHYDRA capabilities schema.");
        return new { status = "authenticated read-only capability discovery", endpoint = root, identity, modules };
    }
    public async Task<object> DiscoverLocalBridges()
    {
        var observations = new List<object>();
        foreach (var endpoint in new[] { (Name: "DRAGONHYDRA CORE NEXT", Url: "http://127.0.0.1:18821/dragon/api/v1/health/live"), (Name: "DRAGONHYDRA ODDS MIRROR", Url: "http://127.0.0.1:18965/api/status") })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                using var response = await http.GetAsync(endpoint.Url, timeout.Token); response.EnsureSuccessStatusCode();
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                bool identity = body?["product"]?.GetValue<string>() == endpoint.Name;
                observations.Add(new { service = endpoint.Name, endpoint = endpoint.Url, status = identity ? "DISCOVERED_READ_ONLY" : "IDENTITY_MISMATCH", version = body?["version"]?.GetValue<string>(), capabilities = identity ? body : null });
            }
            catch { observations.Add(new { service = endpoint.Name, endpoint = endpoint.Url, status = "OFFLINE_OR_UNAVAILABLE" }); }
        }
        return new { schema = "abh-bridge-discovery/1", timestamp = DateTimeOffset.UtcNow, observations, policy = "No service start, database access, provider acquisition or credential reuse" };
    }
}
