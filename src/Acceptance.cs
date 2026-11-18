using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIBlackHole;

static class Acceptance
{
    public static void Fixture(string path, string architecture = "llama", uint version = 3)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var writer = new BinaryWriter(File.Create(path));
        void String(string s) { byte[] bytes = Encoding.UTF8.GetBytes(s); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
        writer.Write(0x46554747u); writer.Write(version); writer.Write(1UL); writer.Write(5UL);
        foreach (var entry in new[] { ("general.architecture", architecture), ("general.name", "ABH synthetic format fixture"), ("tokenizer.chat_template", "{{ messages }}") }) { String(entry.Item1); writer.Write(8u); String(entry.Item2); }
        String(architecture + ".block_count"); writer.Write(4u); writer.Write(2u); String("general.file_type"); writer.Write(4u); writer.Write(7u);
        String("test.weight"); writer.Write(1u); writer.Write(1UL); writer.Write(0u); writer.Write(0UL); writer.Write(new byte[128]);
    }
    public static async Task<int> SelfTest(ApplicationService app, Gateway gateway, string? output)
    {
        var checks = new List<object>(); int failed = 0;
        void Check(string name, bool pass, object? detail = null) { checks.Add(new { name, status = pass ? "VERIFIED" : "FAILED", detail }); if (!pass) failed++; }
        async Task Reject(string name, Func<Task> action) { bool rejected = false; try { await action(); } catch { rejected = true; } Check(name, rejected); }
        string dir = app.Paths.Data("fixtures/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "Unicode 模型 with spaces.gguf"); Fixture(file);
        var info = GgufInspector.Inspect(file); Check("GGUF metadata and tensor descriptor", info.Architecture == "llama" && info.TensorCount == 1 && info.Quantization == "Q8_0");
        var model = app.Models.Add(file); Check("Registry direct file with Unicode and spaces", model.Path == Path.GetFullPath(file) && app.Models.Add(file).Id == model.Id);
        string hash = await app.Models.Verify(model, null, CancellationToken.None); Check("Full fingerprint and cached identity", hash.Length == 64 && model.LastVerification != null && ModelRegistry.QuickHash(file) == model.QuickFingerprint);
        var reloaded = new ModelRegistry(app.Paths); Check("Atomic registry persistence", reloaded.Get(model.Id).Sha256 == hash);
        string moved = Path.Combine(dir, "reconnected.gguf"); File.Move(file, moved); app.Models.Refresh(); Check("Missing model marked unavailable", model.State == "MODEL UNAVAILABLE");
        Check("Reconnection requires verified exact content", await app.Models.Reconnect(model, moved, false, CancellationToken.None));
        string fake = Path.Combine(dir, "different.gguf"); Fixture(fake, "mistral"); Check("Different model never silently selected", !await app.Models.Reconnect(model, fake, false, CancellationToken.None));
        File.AppendAllText(moved, "changed"); app.Models.Refresh(); Check("Changed model invalidates fingerprint", model.Sha256 == null && model.State == "ERROR");
        app.Models.Remove(model.Id); Check("Registry removal preserves model file", File.Exists(moved));
        string corrupt = Path.Combine(dir, "corrupt.gguf"); File.WriteAllText(corrupt, "not GGUF"); await Reject("Corrupted header rejected", () => Task.Run(() => GgufInspector.Inspect(corrupt)));
        Fixture(corrupt, version: 99); await Reject("Unsupported GGUF version rejected", () => Task.Run(() => GgufInspector.Inspect(corrupt)));
        await Reject("Network paths require opt-in", () => Task.Run(() => ModelRegistry.SafePath(@"\\server\models\test.gguf")));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Reject("Hash cancellation", () => ModelRegistry.HashFile(fake, null, cancelled.Token));
        Check("Bounded selected-folder discovery", app.Models.Scan(dir, false, CancellationToken.None, maxFiles: 1).Count() <= 1);
        await Reject("Invalid GPU profile rejected", () => Task.Run(() => EngineManager.Validate(Profile.Safe with { GpuLayers = -1 })));
        var synthetic = app.Models.Add(fake); var runtime = new EngineRecord("fixture", "llama.cpp", "fixture", "", "test", "hash", ["cpu"], "", "fixture");
        Check("Low-memory configuration refused", EngineManager.Check(synthetic, runtime, Profile.Safe, 1).Status == "UNSUPPORTED");
        Check("Uninstalled GPU backend refused", EngineManager.Check(synthetic, runtime, Profile.Safe with { Backend = "cuda" }, ulong.MaxValue).Status == "UNSUPPORTED");
        var created = app.Keys.Create("selftest", 30, 2, ["models"]); Check("Scoped key authorization", app.Keys.Authorize(created.secret, "chat") == 403 && app.Keys.Authorize(created.secret, "models") == 200);
        var rotated = app.Keys.Rotate(created.record.Id); Check("Key rotation invalidates previous secret", app.Keys.Authorize(created.secret, "models") == 401 && app.Keys.Authorize(rotated.secret, "models") == 200); app.Keys.Revoke(rotated.record.Id); Check("Key revocation", app.Keys.Authorize(rotated.secret, "models") == 401);
        string vaultFile = Path.Combine(dir, "test.vault"); using (var vault = new Vault(vaultFile)) { vault.Unlock("ABH synthetic test passphrase"); vault.Put(new("OpenAI", "test", "SYNTHETIC_CREDENTIAL", true)); vault.Lock(); await Reject("Wrong vault passphrase rejected", () => Task.Run(() => vault.Unlock("wrong passphrase long enough"))); vault.Unlock("ABH synthetic test passphrase"); Check("Portable encrypted vault round trip", vault.Get("OpenAI")?.Secret == "SYNTHETIC_CREDENTIAL" && !File.ReadAllText(vaultFile).Contains("SYNTHETIC_CREDENTIAL")); }
        using (var vault = new Vault(Path.Combine(dir, "mock.vault")))
        {
            vault.Unlock("ABH provider mock passphrase");
            foreach (string name in new[] { "OpenAI", "Claude", "Grok" }) vault.Put(new(name, "mock-model", "mock-secret", true, 64, 1, 1m, .1m));
            var mock = new ProviderMock(); var providers = new Providers(vault, mock);
            foreach (string name in new[] { "OpenAI", "Claude", "Grok" }) { var result = await providers.Call(name, "Synthetic test input"); Check(name + " native protocol mock", result.Text == "MOCK_OK" && mock.Valid); await Reject(name + " session cost exposure bounded by call limit", () => providers.Call(name, "second request")); }
        }
        await ProviderAcceptance.Run(app.Paths, Check);
        var body = JsonNode.Parse("""{"tools":[{"type":"namespace","name":"engineering","tools":[{"type":"function","name":"sum","parameters":{"type":"object"}}]}],"input":[{"role":"developer","content":"test"},{"role":"user","content":"test"}]}""")!.AsObject();
        var mapping = ResponseNamespaces.Prepare(body); ResponseCompatibility.Normalize(body); string alias = body["tools"]![0]!["name"]!.GetValue<string>();
        var call = new JsonObject { ["type"] = "function_call", ["name"] = alias, ["call_id"] = "preserve-exact-17", ["arguments"] = "{\"a\":1}" }; mapping.Restore(call); Check("Namespaced function preserves call ID and arguments", call["name"]!.GetValue<string>() == "sum" && call["namespace"]!.GetValue<string>() == "engineering" && call["call_id"]!.GetValue<string>() == "preserve-exact-17" && call["arguments"]!.GetValue<string>() == "{\"a\":1}");
        await Reject("Unsupported custom tools explicitly rejected", () => Task.Run(() => ResponseNamespaces.Prepare(JsonNode.Parse("""{"tools":[{"type":"custom","name":"shell"}]}""")!.AsObject())));
        await Reject("Late system message never promoted", () => Task.Run(() => ResponseCompatibility.Normalize(JsonNode.Parse("""{"input":[{"role":"user","content":"x"},{"role":"system","content":"y"}]}""")!.AsObject())));
        await gateway.Start(); using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(gateway.Url) };
        Check("HTTP unauthorized request rejected", (await client.GetAsync("/v1/models")).StatusCode == HttpStatusCode.Unauthorized);
        var api = app.Keys.Create("HTTP selftest", scopes: ["models", "mcp"]); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.secret);
        Check("Authenticated model listing", (await client.GetAsync("/v1/models")).IsSuccessStatusCode);
        Check("Scoped key cannot access browser administration", (await client.GetAsync("/api/keys")).StatusCode == HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("Origin", "https://example.invalid"); Check("Foreign browser origin rejected", (await client.GetAsync("/v1/models")).StatusCode == HttpStatusCode.Forbidden); client.DefaultRequestHeaders.Remove("Origin");
        var response = await McpProtocol.ClientCall(gateway.Url + "/mcp", api.secret, "tools/list", new(), CancellationToken.None); Check("Current MCP HTTP tools list", response["result"]?["tools"] is JsonArray tools && tools.Count == 11 && response["result"]?["resultType"]?.GetValue<string>() == "complete");
        var tool = await McpProtocol.ClientCall(gateway.Url + "/mcp", api.secret, "tools/call", new() { ["name"] = "abh_runtime_status", ["arguments"] = new JsonObject() }, CancellationToken.None); Check("MCP HTTP tool execution", tool["result"]?["isError"]?.GetValue<bool>() == false);
        var rejectedTool = await McpProtocol.ClientCall(gateway.Url + "/mcp", api.secret, "tools/call", new() { ["name"] = "abh_model_info", ["arguments"] = new JsonObject { ["path"] = "C:/private" } }, CancellationToken.None); Check("MCP rejects arbitrary path arguments", rejectedTool["error"]?["code"]?.GetValue<int>() == -32602);
        app.Keys.Revoke(api.record.Id);
        await Reject("Automatic workflow rejects unapproved cloud agent", () => Task.Run(() => Coordinator.Submit(app, new("SEQUENTIAL HANDOFF", ["OpenAI"], "Synthetic", CloudBudget: 1))));
        await Reject("Workflow enforces finite step limit", () => Task.Run(() => Coordinator.Submit(app, new("SEQUENTIAL HANDOFF", ["ARX", "ARX"], "Synthetic", MaxSteps: 1))));
        var workflow = Coordinator.Submit(app, new("PARALLEL RESPONSE COMPARISON", ["ARX", "ARX"], "Synthetic", MaxSteps: 2)); while (workflow.Finished == null) await Task.Delay(50); Check("Parallel evidence workflow completes within limits", workflow.Status == "COMPLETED");
        app.Models.Remove(synthetic.Id);
        Json.Save(output ?? app.Paths.Data("selftest.json"), new { status = failed == 0 ? "VERIFIED" : "FAILED", failed, checks, timestamp = DateTimeOffset.UtcNow, scope = "Synthetic fixtures, real local HTTP and provider mock transports. No real inference or paid provider call in this suite." }); return failed == 0 ? 0 : 1;
    }
    public static async Task<int> Models(ApplicationService app, Gateway gateway, string[] args, Func<string,string?> value, string? output)
    {
        var reports = new List<object>(); var watch = Stopwatch.StartNew(); await app.Discover(); await gateway.Start();
        var selected = args.Select((a, i) => (a, i)).Where(p => p.a == "--model-file").Select(p => args[p.i + 1]).ToArray(); if (selected.Length == 0) throw new ArgumentException("Supply one or more --model-file paths.");
        var clientKey = app.Keys.Create("bounded acceptance", 100, 100, ["models", "chat", "responses", "mcp", "runtime"]);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(gateway.Url), Timeout = TimeSpan.FromMinutes(10) }; client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", clientKey.secret);
        int failed = 0;
        try
        {
            foreach (string path in selected)
            {
                var model = app.Models.Add(path); string hash = model.Sha256 ?? await app.Models.Verify(model, null, CancellationToken.None);
                if (value("--expected-hash") is { } expected && hash != expected) throw new IOException("Expected model SHA-256 does not match.");
                object? calibration = null;
                if (args.Contains("--calibrate")) { var job = new Job { Kind = "CALIBRATE" }; calibration = await Calibrator.Run(app, model, job, job.Cancel.Token); }
                else await app.Engine.Start(model, app.Engines.First(), Calibrator.Select(app, model, value("--profile") ?? "SAFE"));
                model.VerifiedEngine = app.Engine.RuntimeHash;
                var body = new { model = model.Id, messages = new[] { new { role = "user", content = "What is 7 + 5? Reply with only the number." } }, max_tokens = 32, temperature = 0, seed = 42, stream = false };
                using var reply = await client.PostAsJsonAsync("/v1/chat/completions", body); string raw = await reply.Content.ReadAsStringAsync(); var data = JsonNode.Parse(raw); string? text = data?["choices"]?[0]?["message"]?["content"]?.GetValue<string>(); bool inference = reply.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(text);
                using var streamReply = await client.PostAsJsonAsync("/v1/chat/completions", new { model = model.Id, messages = new[] { new { role = "user", content = "Say hello in one short sentence." } }, max_tokens = 24, stream = true }); string stream = await streamReply.Content.ReadAsStringAsync(); bool streaming = streamReply.IsSuccessStatusCode && stream.Contains("data: ") && stream.Contains("[DONE]");
                using var responseReply = await client.PostAsJsonAsync("/v1/responses", new { model = model.Id, input = "Say hello.", max_output_tokens = 24, stream = false }); string responses = await responseReply.Content.ReadAsStringAsync();
                object? browser = args.Contains("--browser") ? await BrowserTest.Run(app.Paths, gateway) : null;
                object? codex = null;
                if (args.Contains("--codex"))
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                    try
                    {
                        var handshake = await gateway.Codex.Connect(app, gateway, deadline.Token);
                        string workspace = app.Paths.Data("codex-acceptance-workspace"); Directory.CreateDirectory(workspace);
                        var turn = await gateway.Codex.Task(workspace, "Write a Python function add(a, b) that returns their sum. Reply with only the function definition. Do not call tools.", deadline.Token);
                        while (!Json.Text(gateway.Codex.Status).Contains("turn/completed")) await Task.Delay(500, deadline.Token);
                        var coding = JsonSerializer.SerializeToNode(gateway.Codex.Status)!;
                        var toolTurn = await gateway.Codex.Task(workspace, "Call the abh_runtime_status MCP tool exactly once. Return a JSON object containing the exact pid, port and modelId from its result. Do not guess these values or use any other tool.", deadline.Token);
                        while (!Json.Text(gateway.Codex.Status).Contains("\"id\": \"" + gateway.Codex.TurnId + "\",\n        \"items\""))
                        {
                            var snapshot = JsonSerializer.SerializeToNode(gateway.Codex.Status)!;
                            if (snapshot["events"]!.AsArray().Any(e => e?["method"]?.GetValue<string>() == "turn/completed" && e["params"]?["turn"]?["id"]?.GetValue<string>() == gateway.Codex.TurnId)) break;
                            await Task.Delay(500, deadline.Token);
                        }
                        codex = new { handshake, turn, coding, toolTurn, final = gateway.Codex.Status, expectedRuntime = new { app.Engine.Pid, app.Engine.Port, modelId = model.Id }, scope = "Actual local App Server coding proposal and bounded runtime MCP tool round trip; results require event verification" };
                    }
                    catch (Exception e) { codex = new { status = "PARTIAL", error = e.Message, events = gateway.Codex.Status }; }
                    finally { await gateway.Codex.DisposeAsync(); }
                    Json.Save(app.Paths.Data("codex-appserver-acceptance.json"), codex);
                }
                int pid = app.Engine.Pid, port = app.Engine.Port; double startup = app.Engine.StartupSeconds; var profile = app.Engine.Active;
                await app.Engine.Stop(); var stopped = JsonNode.Parse(File.ReadAllText(app.Paths.Data("last-stop.json"))); bool clean = stopped?["graceful"]?.GetValue<bool>() == true;
                bool alive; try { using var p = Process.GetProcessById(pid); alive = !p.HasExited; } catch { alive = false; }
                bool browserPassed = browser == null || JsonSerializer.SerializeToNode(browser)!["status"]?.GetValue<string>() == "PASS"; bool pass = inference && streaming && responseReply.IsSuccessStatusCode && browserPassed && clean && !alive; if (!pass) failed++;
                model.State = pass ? "TESTED" : "ERROR"; if (inference) { model.Capabilities["CHAT"] = "TESTED"; model.Capabilities["TEXT_GENERATION"] = "TESTED"; } app.Models.Save();
                reports.Add(new { status = pass ? "VERIFIED" : "FAILED", model = new { model.Id, name = model.Info?.Name, architecture = model.Info?.Architecture, model.Bytes, model.Sha256 }, engine = app.Engines.First().Fingerprint, profile, startup, inference = new { success = inference, text, usage = data?["usage"], timings = data?["timings"], arithmeticSanityPassed = text?.Trim().Trim('.') == "12", intelligenceClaim = "No general intelligence or accuracy claim" }, streaming, responses = new { httpStatus = (int)responseReply.StatusCode, body = JsonNode.Parse(responses) }, calibration, browser, codex, shutdown = new { clean, pid, port, processExited = !alive, detail = stopped } });
                Json.Save(output ?? app.Paths.Data("model-acceptance.json"), new { status = failed == 0 ? "VERIFIED_SO_FAR" : "FAILED", reports });
            }
            Json.Save(output ?? app.Paths.Data("model-acceptance.json"), new { status = failed == 0 ? "VERIFIED" : "FAILED", timestamp = DateTimeOffset.UtcNow, elapsedSeconds = watch.Elapsed.TotalSeconds, reports, hardware = await Hardware.Detect(), testedDistinctArchitectures = app.Models.All().Where(m => m.Capabilities.GetValueOrDefault("TEXT_GENERATION") == "TESTED").Select(m => m.Info?.Architecture).Distinct().ToArray() });
            return failed == 0 ? 0 : 1;
        }
        finally { app.Keys.Revoke(clientKey.record.Id); await app.Engine.Stop(); }
    }
    sealed class ProviderMock : HttpMessageHandler
    {
        public bool Valid { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; bool claude = request.RequestUri!.Host == "api.anthropic.com";
            Valid = body["model"]?.GetValue<string>() == "mock-model" && body["messages"]?[0]?["content"]?.GetValue<string>() == "Synthetic test input" && (claude ? request.Headers.Contains("x-api-key") && request.Headers.Contains("anthropic-version") : request.Headers.Authorization?.Scheme == "Bearer");
            return new(HttpStatusCode.OK) { Content = new StringContent(claude ? """{"model":"mock-model","content":[{"type":"text","text":"MOCK_OK"}],"usage":{"input_tokens":3,"output_tokens":2}}""" : """{"model":"mock-model","choices":[{"message":{"content":"MOCK_OK"}}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""", Encoding.UTF8, "application/json") };
        }
    }
}
