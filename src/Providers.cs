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
    public object PublicStatus() => Names.Select(p => new { provider = p, configured = vault.Get(p) != null, enabled = vault.Get(p)?.Enabled == true, model = vault.Get(p)?.Model ?? "", limit = vault.Get(p)?.MaxCallsPerSession ?? 0, calls = Used(p) }).ToArray();
    int Used(string provider) { lock (sync) return counts.GetValueOrDefault(provider); }
    public async Task<ProviderResult> Call(string provider, string prompt, CancellationToken token = default)
    {
        var credential = vault.Get(provider) ?? throw new ArgumentException("Unlock the vault and configure this provider in API Keys & Connections.");
        if (!credential.Enabled || provider == "DRAGONHYDRA") throw new ArgumentException("This cloud adapter is not enabled.");
        if (prompt.Length is < 1 or > 16000) throw new ArgumentException("Cloud input must contain 1–16,000 characters.");
        if (credential.MaxTokens is < 1 or > 512 || credential.MaxCallsPerSession is < 1 or > 20 || string.IsNullOrWhiteSpace(credential.Model)) throw new ArgumentException("Set a model, 1–512 output tokens and 1–20 calls per session.");
        lock (sync) { int count = counts.GetValueOrDefault(provider); if (count >= credential.MaxCallsPerSession) throw new ArgumentException("Provider session call limit reached. Restart deliberately to reset it."); counts[provider] = count + 1; }
        using var request = Build(credential, prompt);
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new ArgumentException($"{provider} returned HTTP {(int)response.StatusCode}. Check API access, quota and model ID. Consumer subscriptions do not provide API credentials. No automatic retry was made.");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
        string text = provider == "Claude" ? string.Join("\n", (body["content"]?.AsArray() ?? []).Where(x => x?["type"]?.GetValue<string>() == "text").Select(x => x!["text"]!.GetValue<string>())) : body["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        return new(provider, body["model"]?.GetValue<string>() ?? credential.Model, text, DateTimeOffset.UtcNow, body["usage"]?.DeepClone(), null, "Authorized outbound provider API; cost unknown, usage returned when available");
    }
    internal static HttpRequestMessage Build(Credential credential, string prompt)
    {
        string url = credential.Provider switch { "OpenAI" => "https://api.openai.com/v1/chat/completions", "Claude" => "https://api.anthropic.com/v1/messages", "Grok" => "https://api.x.ai/v1/chat/completions", _ => throw new ArgumentException("Unknown provider.") };
        var body = new JsonObject { ["model"] = credential.Model, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }), ["stream"] = false };
        body[credential.Provider == "OpenAI" ? "max_completion_tokens" : "max_tokens"] = credential.MaxTokens;
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (credential.Provider == "Claude") { request.Headers.Add("x-api-key", credential.Secret); request.Headers.Add("anthropic-version", "2023-06-01"); }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
        return request;
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
        return new { schema = "satellite-bridge-discovery/1", timestamp = DateTimeOffset.UtcNow, observations, policy = "No service start, database access, provider acquisition or credential reuse" };
    }
}
