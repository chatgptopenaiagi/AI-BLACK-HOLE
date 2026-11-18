using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIBlackHole;

sealed class Gateway(ApplicationService service) : IAsyncDisposable
{
    WebApplication? web; readonly Dictionary<string, DateTimeOffset> tickets = new();
    public CodexAppServer Codex { get; } = new();
    public string Session { get; } = Json.Secret();
    public string Url { get; private set; } = "";
    public string ApiUrl => Url + "/v1";
    public string ChatUrl
    {
        get { lock (tickets) { foreach (var key in tickets.Where(p => p.Value < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) tickets.Remove(key); if (tickets.Count > 16) tickets.Clear(); string ticket = Json.Secret(); tickets[ticket] = DateTimeOffset.UtcNow.AddMinutes(1); return Url + "/#launch=" + ticket; } }
    }
    public async Task Start()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = service.Paths.Root });
        builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, 0); k.Limits.MaxRequestBodySize = 1048576; k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10); k.Limits.MaxConcurrentConnections = 48; });
        web = builder.Build();
        web.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.CacheControl = "no-store"; ctx.Response.Headers["Referrer-Policy"] = "no-referrer"; ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (Url == "" || ctx.Request.Host.Host != "127.0.0.1" || ctx.Request.Host.Port != new Uri(Url).Port) { ctx.Response.StatusCode = 400; return; }
            string origin = ctx.Request.Headers.Origin.ToString(); if (origin != "" && origin != Url) { ctx.Response.StatusCode = 403; return; }
            try { await next(); }
            catch (Exception e) when (!ctx.Response.HasStarted)
            { ctx.Response.StatusCode = e is ArgumentException or System.Text.Json.JsonException ? 400 : e is OperationCanceledException ? 504 : 502; await ctx.Response.WriteAsJsonAsync(new { error = new { message = e is ArgumentException or IOException ? e.Message : e is OperationCanceledException ? "Operation cancelled or timed out." : "Request failed. Check local diagnostics.", type = "abh_error" } }); }
        });
        foreach (string file in new[] { "index.html", "app.js", "style.css", "en.json" })
        {
            string asset = file;
            web.MapGet(asset == "index.html" ? "/" : "/" + asset, async ctx => { ctx.Response.ContentType = asset.EndsWith(".css") ? "text/css" : asset.EndsWith(".js") ? "text/javascript" : asset.EndsWith(".json") ? "application/json" : "text/html; charset=utf-8"; await ctx.Response.SendFileAsync(Path.Combine(service.Paths.Root, "web", asset)); });
        }
        web.MapPost("/api/session", async ctx =>
        {
            var body = await Body(ctx); string ticket = S(body, "ticket"); bool allowed; lock (tickets) allowed = tickets.Remove(ticket, out var until) && until >= DateTimeOffset.UtcNow;
            if (!allowed) { ctx.Response.StatusCode = 401; return; } await ctx.Response.WriteAsJsonAsync(new { token = Session });
        });
        web.MapGet("/api/status", async ctx => { if (await Auth(ctx, "models", true)) await ctx.Response.WriteAsJsonAsync(new { state = service.Status(), apiUrl = ApiUrl, codex = Codex.Status }); });
        web.MapGet("/api/keys", async ctx => { if (await Auth(ctx, "models", true)) await ctx.Response.WriteAsJsonAsync(service.Keys.List().Select(k => new { k.Id, k.Label, k.Scopes, k.Revoked, k.Created, k.RequestsPerMinute, k.UsedRequests, k.MaxTotalRequests })); });
        web.MapGet("/api/settings", async ctx => { if (await Auth(ctx, "models", true)) await ctx.Response.WriteAsJsonAsync(service.Settings); });
        web.MapGet("/api/calibration/{id}", async ctx => { if (!await Auth(ctx, "models", true)) return; string path = Calibrator.ReportPath(service, service.Models.Get(ctx.Request.RouteValues["id"]!.ToString()!)); if (!File.Exists(path)) { ctx.Response.StatusCode = 404; return; } await ctx.Response.WriteAsJsonAsync(Json.Read<CalibrationReport>(path)); });
        web.MapGet("/api/diagnostics", async ctx =>
        {
            if (!await Auth(ctx, "models", true)) return;
            var logs = Directory.GetFiles(service.Paths.Data("logs"), "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3).Select(p => new { name = Path.GetFileName(p), lines = File.ReadLines(p).TakeLast(50).ToArray() });
            await ctx.Response.WriteAsJsonAsync(new { engine = service.Engine.GetRuntimeStatus(), jobs = service.Jobs.Values, logs });
        });
        web.MapPost("/api/action/{action}", async ctx =>
        {
            if (!await Auth(ctx, "models", true)) return; var body = await Body(ctx); string action = ctx.Request.RouteValues["action"]!.ToString()!;
            object result = await Action(action, body, ctx.RequestAborted); await ctx.Response.WriteAsJsonAsync(result);
        });
        web.MapGet("/v1/models", async ctx =>
        { if (await Auth(ctx, "models")) await ctx.Response.WriteAsJsonAsync(new { @object = "list", data = service.Models.All().Where(m => !m.Info!.Auxiliary).Select(m => new { id = m.Id, @object = "model", owned_by = "local", created = 0, name = m.Info?.Name, loaded = service.Engine.Ready && service.Engine.Model?.Id == m.Id, capabilities = m.Capabilities }) }); });
        foreach (var endpoint in new[] { (path: "/v1/chat/completions", scope: "chat"), (path: "/v1/responses", scope: "responses"), (path: "/v1/embeddings", scope: "chat") })
        {
            var e = endpoint; web.MapPost(e.path, async ctx => { if (await Auth(ctx, e.scope)) await Forward(ctx, e.path); });
        }
        web.MapGet("/v1/abh/runtime", async ctx => { if (await Auth(ctx, "runtime")) await ctx.Response.WriteAsJsonAsync(service.Engine.GetRuntimeStatus()); });
        web.MapGet("/v1/abh/models", async ctx => { if (await Auth(ctx, "models")) await ctx.Response.WriteAsJsonAsync(service.Models.All().Select(m => new { m.Id, m.Info, m.State, m.Sha256, m.Capabilities })); });
        web.MapGet("/v1/abh/calibration/{id}", async ctx => { if (!await Auth(ctx, "calibration")) return; string path = Calibrator.ReportPath(service, service.Models.Get(ctx.Request.RouteValues["id"]!.ToString()!)); if (!File.Exists(path)) { ctx.Response.StatusCode = 404; return; } await ctx.Response.WriteAsJsonAsync(Json.Read<CalibrationReport>(path)); });
        web.MapGet("/v1/abh/tasks/{id}", async ctx => { if (!await Auth(ctx, "tasks")) return; if (!service.Jobs.TryGetValue(ctx.Request.RouteValues["id"]!.ToString()!, out var job)) { ctx.Response.StatusCode = 404; return; } await ctx.Response.WriteAsJsonAsync(job); });
        web.MapPost("/v1/abh/tasks", async ctx => { if (await Auth(ctx, "tasks")) await ctx.Response.WriteAsJsonAsync(Coordinator.Submit(service, (await Body(ctx)).Deserialize<WorkflowRequest>(Json.Options)!)); });
        web.MapPost("/v1/abh/tasks/{id}/cancel", async ctx => { if (!await Auth(ctx, "tasks")) return; if (service.Jobs.TryGetValue(ctx.Request.RouteValues["id"]!.ToString()!, out var job)) job.Cancel.Cancel(); else ctx.Response.StatusCode = 404; });
        var mcp = new McpProtocol(service);
        web.MapPost("/mcp", async ctx =>
        {
            if (!await Auth(ctx, "mcp")) return;
            var body = await Body(ctx); string version = ctx.Request.Headers["MCP-Protocol-Version"].ToString();
            if (!McpProtocol.Versions.Contains(version)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(McpProtocol.Error(body["id"], -32022, "Unsupported or missing protocol version.", new { supportedVersions = McpProtocol.Versions })); return; }
            if (!ctx.Request.Headers.Accept.ToString().Contains("application/json") || !ctx.Request.Headers.Accept.ToString().Contains("text/event-stream")) { ctx.Response.StatusCode = 406; return; }
            if (version == McpProtocol.Current)
            {
                string? bodyVersion = body["params"]?["_meta"]?["io.modelcontextprotocol/protocolVersion"]?.GetValue<string>(); string method = body["method"]?.GetValue<string>() ?? "";
                if (bodyVersion != version || ctx.Request.Headers["Mcp-Method"] != method || method == "tools/call" && ctx.Request.Headers["Mcp-Name"] != body["params"]?["name"]?.GetValue<string>()) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(McpProtocol.Error(body["id"], -32020, "MCP body/header mismatch.")); return; }
            }
            var result = await mcp.Handle(body, ctx.RequestAborted);
            if (result == null) ctx.Response.StatusCode = 202;
            else { if (version == McpProtocol.Current && result["error"]?["code"]?.GetValue<int>() == -32601) ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(result); }
        });
        web.MapMethods("/mcp", ["GET", "DELETE"], ctx => { ctx.Response.StatusCode = 405; return Task.CompletedTask; });
        await web.StartAsync(); Url = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }
    public async Task<object> Action(string action, JsonObject body, CancellationToken token)
    {
        switch (action)
        {
            case "register": return service.Models.Add(S(body, "path"), service.Settings.AllowNetworkShares, token);
            case "inspect": var inspected = service.Models.Get(S(body, "id")); return service.Models.Add(inspected.Path, service.Settings.AllowNetworkShares, token);
            case "refresh": service.Models.Refresh(); return service.Models.All();
            case "remove": service.Models.Remove(S(body, "id")); return new { removedFromRegistry = true, fileDeleted = false };
            case "scan": return service.Scan(S(body, "path"), body["blobs"]?.GetValue<bool>() == true);
            case "verify": return service.Verify(S(body, "id"));
            case "reconnect": var reconnect = service.Models.Get(S(body, "id")); return service.Submit("RECONNECT", async (_, ct) => await service.Models.Reconnect(reconnect, S(body, "path"), service.Settings.AllowNetworkShares, ct));
            case "start": var model = service.Models.Get(S(body, "id")); Profile profile = body["custom"]?.Deserialize<Profile>(Json.Options) ?? Calibrator.Select(service, model, S(body, "profile", "SAFE")); return service.Start(model.Id, profile, body["engineId"]?.GetValue<string>());
            case "stop": return await service.Stop();
            case "restart": await service.Stop(); return service.Start(service.Engine.Model?.Id ?? throw new ArgumentException("Select a model."), service.Engine.Active, service.Engine.Runtime?.Id);
            case "calibrate": return service.Calibrate(S(body, "id"));
            case "discover": return service.Submit("DISCOVERY", async (_, _) => { await service.Discover(); return service.Engines.Select(e => e with { Help = "" }); });
            case "cancel": if (!service.Jobs.TryGetValue(S(body, "id"), out var job)) throw new ArgumentException("Unknown job."); job.Cancel.Cancel(); return new { cancelled = job.Id };
            case "hf-list": return await HuggingFace.List(S(body, "url"), token);
            case "hf-download": var selection = body["file"]!.Deserialize<HubFile>(Json.Options)!; return service.Submit("DOWNLOAD", async (j, ct) => await HuggingFace.Download(service, selection, S(body, "destination"), j, ct));
            case "key-create": var created = service.Keys.Create(S(body, "label"), scopes: body["scopes"]?.Deserialize<string[]>()); return new { created.record.Id, created.secret };
            case "key-rotate": var rotated = service.Keys.Rotate(S(body, "id")); return new { rotated.record.Id, rotated.secret };
            case "key-revoke": service.Keys.Revoke(S(body, "id")); return new { revoked = true };
            case "settings": service.SaveSettings(body.Deserialize<Preferences>(Json.Options)!); return service.Settings;
            case "vault-unlock": service.Vault.Unlock(S(body, "passphrase")); return new { unlocked = true };
            case "vault-lock": service.Vault.Lock(); return new { unlocked = false };
            case "provider-save": service.Vault.Put(body.Deserialize<Credential>(Json.Options)!); return service.Providers.PublicStatus();
            case "provider-remove": service.Vault.Remove(S(body, "provider")); return service.Providers.PublicStatus();
            case "provider-call": if (body["approved"]?.GetValue<bool>() != true) throw new ArgumentException("Explicit approval is required for this exact outbound content."); return await service.Providers.Call(S(body, "provider"), S(body, "prompt"), token);
            case "dragon-discover": return await service.Providers.DiscoverLocalBridges();
            case "dragon-capabilities": return await service.Providers.Dragon(true);
            case "arx-export": return Connectors.ArxEvidence(await Hardware.Detect());
            case "arx-import": var dna = Connectors.ImportArx(S(body, "json")); Json.Save(service.Paths.Data("arx-import.json"), dna); return dna;
            case "generic-call": return await Connectors.Generic(S(body, "endpoint"), S(body, "model"), S(body, "prompt"), token);
            case "mcp-client": return await McpProtocol.ClientCall(S(body, "endpoint"), S(body, "key"), S(body, "method"), body["params"]?.AsObject() ?? new(), token);
            case "workflow": return Coordinator.Submit(service, body.Deserialize<WorkflowRequest>(Json.Options)!);
            case "codex-connect": return await Codex.Connect(service, this, token);
            case "codex-task": return await Codex.Task(S(body, "workspace"), S(body, "prompt"), token);
            case "codex-interrupt": return await Codex.Interrupt(token);
            case "codex-approval": await Codex.Approve(S(body, "id"), body["accept"]?.GetValue<bool>() == true, token); return new { answered = true };
            case "codex-disconnect": await Codex.DisposeAsync(); return new { connected = false };
            default: throw new ArgumentException("Unknown action.");
        }
    }
    static string S(JsonObject body, string key, string? fallback = null) => body[key]?.GetValue<string>() ?? fallback ?? throw new ArgumentException("Required field: " + key);
    static async Task<JsonObject> Body(HttpContext ctx) => (await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted))?.AsObject() ?? throw new ArgumentException("JSON object required.");
    async Task<bool> Auth(HttpContext ctx, string scope, bool browserOnly = false)
    {
        string auth = ctx.Request.Headers.Authorization.ToString(); string secret = auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : "";
        int status = secret.Length <= 200 && Json.Equal(secret, Session) ? 200 : browserOnly ? 401 : service.Keys.Authorize(secret, scope);
        if (status == 200) return true; ctx.Response.StatusCode = status; await ctx.Response.WriteAsJsonAsync(new { error = new { message = "Authentication, scope or request budget rejected.", type = "auth_error" } }); return false;
    }
    async Task Forward(HttpContext ctx, string endpoint)
    {
        if (!service.Engine.Ready) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsJsonAsync(new { error = new { message = "No model is ready. Select, verify and start a model.", type = "model_unavailable" } }); return; }
        if (!await service.Inference.WaitAsync(0, ctx.RequestAborted)) { ctx.Response.StatusCode = 429; await ctx.Response.WriteAsJsonAsync(new { error = new { message = "The local inference slot is busy.", type = "busy" } }); return; }
        try
        {
            var body = await Body(ctx); if (body["model"]?.GetValue<string>() != service.Engine.Model!.Id) throw new ArgumentException("Requested model is not the active registered model ID.");
            if (service.Engine.Model.Info!.Embedding != (endpoint == "/v1/embeddings")) throw new ArgumentException("This endpoint does not match the active model modality.");
            if (body.ToJsonString().Length > 131072) throw new ArgumentException("Inference request exceeds the local size limit.");
            foreach (string unsupported in new[] { "previous_response_id", "conversation", "background" }) if (body[unsupported] != null && body[unsupported]!.ToJsonString() != "false") throw new ArgumentException("Unsupported local Responses option: " + unsupported);
            if (endpoint != "/v1/embeddings") { string limit = endpoint == "/v1/responses" ? "max_output_tokens" : "max_tokens"; int n = body[limit]?.GetValue<int>() ?? 256; if (n is < 1 or > 2048) throw new ArgumentException("Use 1–2048 output tokens."); body[limit] = n; }
            ResponseNamespaces? map = null;
            if (endpoint == "/v1/responses") { map = ResponseNamespaces.Prepare(body); ResponseCompatibility.Normalize(body); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var client = service.Engine.Client(); using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); ctx.Response.StatusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode) { await ctx.Response.WriteAsJsonAsync(new { error = new { message = "The engine rejected this model request. Check context, modality and supported tool formats.", type = "engine_error" } }); return; }
            ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            if (map?.HasAliases == true)
            {
                if (body["stream"]?.GetValue<bool>() == true)
                {
                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
                    while (await reader.ReadLineAsync(timeout.Token) is { } line) { if (line.StartsWith("data: ") && line != "data: [DONE]") { var value = JsonNode.Parse(line[6..]); map.Restore(value); line = "data: " + value!.ToJsonString(); } await ctx.Response.WriteAsync(line + "\n", timeout.Token); await ctx.Response.Body.FlushAsync(timeout.Token); }
                }
                else { var value = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token)); map.Restore(value); await ctx.Response.WriteAsync(value!.ToJsonString(), timeout.Token); }
            }
            else
            {
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token); byte[] buffer = new byte[8192]; int n;
                while ((n = await input.ReadAsync(buffer, timeout.Token)) > 0) { await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), timeout.Token); await ctx.Response.Body.FlushAsync(timeout.Token); }
            }
        }
        finally { service.Inference.Release(); }
    }
    public async ValueTask DisposeAsync() { await Codex.DisposeAsync(); if (web != null) { await web.StopAsync(); await web.DisposeAsync(); } }
}
