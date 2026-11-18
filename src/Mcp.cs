using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AIBlackHole;

sealed class McpProtocol(ApplicationService app)
{
    public const string Current = "2026-07-28";
    public static readonly string[] Versions = [Current, "2025-11-25", "2025-06-18", "2025-03-26"];
    public static JsonObject Metadata() => new() { ["io.modelcontextprotocol/protocolVersion"] = Current, ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "abh", ["version"] = Paths.Version }, ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject() };
    static JsonObject Schema(params (string name, string type)[] fields) => new() { ["type"] = "object", ["properties"] = new JsonObject(fields.Select(f => KeyValuePair.Create<string, JsonNode?>(f.name, new JsonObject { ["type"] = f.type }))), ["required"] = new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f.name)).ToArray()), ["additionalProperties"] = false };
    public static object[] Tools => [
        Tool("abh_list_models", "List registered model identities and availability; no directory access.", Schema(), true),
        Tool("abh_model_info", "Inspect metadata for an already registered model ID.", Schema(("modelId","string")), true),
        Tool("abh_hardware_info", "Read observed Windows CPU, RAM and GPU evidence.", Schema(), true),
        Tool("abh_calibration_status", "Read the last calibration report for a registered model.", Schema(("modelId","string")), true),
        Tool("abh_runtime_status", "Read the active model session.", Schema(), true),
        Tool("abh_connection_status", "List connector capabilities and limitations.", Schema(), true),
        Tool("abh_start_model", "Start a registered model using its safe profile; one model at a time.", Schema(("modelId","string")), false),
        Tool("abh_stop_model", "Stop the owned inference engine and release memory.", Schema(), false),
        Tool("abh_generate", "Generate bounded local text with the active model.", Schema(("text","string")), false),
        Tool("abh_analyze_text", "Analyze supplied text using the active local model.", Schema(("text","string")), false),
        Tool("abh_cancel_task", "Cancel an ABH task by its opaque ID.", Schema(("taskId","string")), false)
    ];
    static object Tool(string name, string description, JsonObject schema, bool read) => new { name, description, inputSchema = schema, annotations = new { readOnlyHint = read, destructiveHint = false, openWorldHint = false } };
    public static JsonObject Error(JsonNode? id, int code, string message, object? data = null) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["data"] = data == null ? null : System.Text.Json.JsonSerializer.SerializeToNode(data) } };
    public async Task<JsonObject?> Handle(JsonObject request, CancellationToken token)
    {
        var id = request["id"]; string? method = request["method"]?.GetValue<string>(); var parameters = request["params"] as JsonObject ?? new();
        if (request["jsonrpc"]?.GetValue<string>() != "2.0" || method == null) return Error(id, -32600, "Invalid JSON-RPC request.");
        if (id == null) return null;
        string? version = parameters["_meta"]?["io.modelcontextprotocol/protocolVersion"]?.GetValue<string>();
        bool current = version == Current;
        if (version != null && !Versions.Contains(version)) return Error(id, -32022, "Unsupported protocol version.", new { supportedVersions = Versions });
        if (current && (parameters["_meta"]?["io.modelcontextprotocol/clientInfo"] == null || parameters["_meta"]?["io.modelcontextprotocol/clientCapabilities"] == null)) return Error(id, -32602, "Required per-request client metadata is missing.");
        object result;
        try
        {
            switch (method)
            {
                case "initialize":
                    string wanted = parameters["protocolVersion"]?.GetValue<string>() ?? "2025-11-25";
                    result = new { protocolVersion = Versions.Contains(wanted) && wanted != Current ? wanted : "2025-11-25", capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "AI BLACK HOLE", version = Paths.Version }, instructions = "Only registered model IDs. No arbitrary paths or commands. Local inference only." }; break;
                case "server/discover":
                    if (!current) return Error(id, -32602, "Current protocol metadata is required for discovery.");
                    result = new { capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "AI BLACK HOLE", version = Paths.Version }, instructions = "Bounded local model tools; no cloud transmission." }; break;
                case "ping": result = new { }; break;
                case "tools/list": result = new { tools = Tools }; break;
                case "tools/call":
                    string name = parameters["name"]?.GetValue<string>() ?? ""; var arguments = parameters["arguments"] as JsonObject ?? new();
                    string[] allowed = name is "abh_model_info" or "abh_start_model" or "abh_calibration_status" ? ["modelId"] : name is "abh_generate" or "abh_analyze_text" ? ["text"] : name == "abh_cancel_task" ? ["taskId"] : [];
                    if (arguments.Any(p => !allowed.Contains(p.Key)) || allowed.Any(p => arguments[p] is not JsonValue)) return Error(id, -32602, "Unexpected or missing tool arguments.");
                    try { var value = await Call(name, arguments, token); result = new { content = new[] { new { type = "text", text = Json.Text(value) } }, isError = false }; }
                    catch (Exception e) when (e is not OperationCanceledException) { result = new { content = new[] { new { type = "text", text = e.Message } }, isError = true }; }
                    break;
                default: return Error(id, -32601, "Method not found.");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { return Error(id, -32602, e.Message); }
        var node = System.Text.Json.JsonSerializer.SerializeToNode(result, Json.Options)!.AsObject(); if (current) node["resultType"] = "complete";
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = node };
    }
    async Task<object> Call(string name, JsonObject args, CancellationToken token)
    {
        string Value(string key) => args[key]?.GetValue<string>() ?? throw new ArgumentException("Required " + key);
        switch (name)
        {
            case "abh_list_models": return app.Models.All().Select(m => new { m.Id, name = m.Info?.Name, architecture = m.Info?.Architecture, m.State, m.Capabilities });
            case "abh_model_info": var model = app.Models.Get(Value("modelId")); return new { model.Id, model.Info, model.Bytes, model.Sha256, model.State, model.Capabilities };
            case "abh_hardware_info": return await Hardware.Detect();
            case "abh_runtime_status": return app.Engine.GetRuntimeStatus();
            case "abh_connection_status": return Connectors.Manifests;
            case "abh_calibration_status": string path = Calibrator.ReportPath(app, app.Models.Get(Value("modelId"))); return File.Exists(path) ? Json.Read<CalibrationReport>(path) : new { status = "NOT TESTED" };
            case "abh_start_model": return app.Start(Value("modelId"), Profile.Safe);
            case "abh_stop_model": return await app.Stop();
            case "abh_cancel_task": if (!app.Jobs.TryGetValue(Value("taskId"), out var job)) throw new ArgumentException("Unknown task."); job.Cancel.Cancel(); return new { cancelled = job.Id };
            case "abh_generate": case "abh_analyze_text":
                string text = Value("text"); if (text.Length is < 1 or > 12000) throw new ArgumentException("Text length must be 1–12000.");
                await app.Inference.WaitAsync(token); try { return await app.Generate(text, 256, token); } finally { app.Inference.Release(); }
            default: throw new ArgumentException("Unknown tool.");
        }
    }
    public static async Task<int> Stdio()
    {
        string? endpoint = Environment.GetEnvironmentVariable("ABH_MCP_URL"), key = Environment.GetEnvironmentVariable("ABH_MCP_KEY");
        if (endpoint == null || key == null) { await Console.Error.WriteLineAsync("Configure ABH_MCP_URL and scoped ABH_MCP_KEY in the MCP client's environment. Start ABH first."); return 2; }
        var uri = Connectors.LocalUri(endpoint); using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var input = new StreamReader(Console.OpenStandardInput()); using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var active = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>(); var workers = new List<Task>(); var gate = new SemaphoreSlim(1, 1);
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length > 262144) continue;
            JsonObject request; try { request = JsonNode.Parse(line)!.AsObject(); } catch { await output.WriteLineAsync(Error(null, -32700, "Invalid JSON.").ToJsonString()); continue; }
            if (request["method"]?.GetValue<string>() == "notifications/cancelled") { string? id = request["params"]?["requestId"]?.ToJsonString(); if (id != null && active.TryGetValue(id, out var cancel)) cancel.Cancel(); continue; }
            string? requestId = request["id"]?.ToJsonString(); var token = new CancellationTokenSource(TimeSpan.FromMinutes(5)); if (requestId != null) active[requestId] = token;
            if (workers.Count >= 32) { if (requestId != null) { active.TryRemove(requestId, out _); await gate.WaitAsync(); try { await output.WriteLineAsync(Error(request["id"], -32603, "Concurrent request limit reached.").ToJsonString()); } finally { gate.Release(); } } token.Dispose(); continue; }
            workers.Add(Task.Run(async () =>
            {
                try
                {
                    using var message = Request(uri, request); using var response = await http.SendAsync(message, token.Token); string json = await response.Content.ReadAsStringAsync(token.Token);
                    if (requestId != null) { await gate.WaitAsync(); try { await output.WriteLineAsync(json.Length == 0 ? Error(request["id"], -32603, "Empty HTTP response.").ToJsonString() : json); } finally { gate.Release(); } }
                }
                catch (OperationCanceledException) { }
                catch { if (requestId != null) { await gate.WaitAsync(); try { await output.WriteLineAsync(Error(request["id"], -32603, "ABH HTTP connection failed.").ToJsonString()); } finally { gate.Release(); } } }
                finally { if (requestId != null) active.TryRemove(requestId, out _); token.Dispose(); }
            }));
            workers.RemoveAll(t => t.IsCompleted);
        }
        foreach (var cancel in active.Values) cancel.Cancel(); await Task.WhenAll(workers); return 0;
    }
    public static HttpRequestMessage Request(Uri uri, JsonObject body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        string version = body["params"]?["_meta"]?["io.modelcontextprotocol/protocolVersion"]?.GetValue<string>() ?? body["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-11-25";
        request.Headers.Add("MCP-Protocol-Version", version);
        if (version == Current) { request.Headers.Add("Mcp-Method", body["method"]!.GetValue<string>()); if (body["method"]!.GetValue<string>() == "tools/call") request.Headers.Add("Mcp-Name", body["params"]!["name"]!.GetValue<string>()); }
        return request;
    }
    public static async Task<JsonNode> ClientCall(string endpoint, string key, string method, JsonObject parameters, CancellationToken token)
    {
        if (method is not ("server/discover" or "tools/list" or "tools/call")) throw new ArgumentException("Unsupported MCP client operation.");
        var uri = Connectors.LocalUri(endpoint); using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key); parameters["_meta"] = Metadata();
        using var request = Request(uri, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = parameters });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token)); string data = "";
            while (await reader.ReadLineAsync(token) is { } line) { if (line.StartsWith("data:")) data += line[5..].TrimStart() + "\n"; else if (line.Length == 0 && data.Length > 0) { var value = JsonNode.Parse(data)!; data = ""; if (value["id"] != null) return value; } }
            throw new IOException("MCP stream ended before a result.");
        }
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
    }
}
