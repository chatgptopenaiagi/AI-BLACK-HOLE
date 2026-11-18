using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AIBlackHole;

static class ProviderAcceptance
{
    public static async Task Run(Paths paths, Action<string, bool, object?> check)
    {
        using var vault = new Vault(paths.Data("fixtures/provider-advanced-" + Guid.NewGuid().ToString("N") + ".vault")); vault.Unlock("ABH isolated adapter fixture passphrase");
        foreach (string name in new[] { "OpenAI", "Claude", "Grok" })
        {
            vault.Put(new(name, "mock-model", "mock-secret", true, 64, 10, 1m, .1m)); var handler = new NativeMock(); var providers = new Providers(vault, handler);
            bool claude = name == "Claude";
            var options = ProviderPayload.Prompt("Synthetic tool test");
            options["tools"] = JsonNode.Parse(claude ? """[{"name":"sum","description":"Add two integers","input_schema":{"type":"object","properties":{"a":{"type":"integer"}},"required":["a"],"additionalProperties":false}}]""" : """[{"type":"function","function":{"name":"sum","parameters":{"type":"object","properties":{"a":{"type":"integer"}},"required":["a"],"additionalProperties":false}}}]""");
            options[claude ? "output_config" : "response_format"] = JsonNode.Parse(claude ? """{"format":{"type":"json_schema","schema":{"type":"object","properties":{"answer":{"type":"integer"}},"required":["answer"],"additionalProperties":false}}}""" : """{"type":"json_schema","json_schema":{"name":"answer","strict":true,"schema":{"type":"object","properties":{"answer":{"type":"integer"}},"required":["answer"],"additionalProperties":false}}}""");
            var response = await providers.Exchange(name, options, false, null, CancellationToken.None);
            var call = claude ? response["content"]![0] : response["choices"]![0]!["message"]!["tool_calls"]![0];
            check(name + " native structured-output request and tool ID", handler.Valid && call!["id"]!.GetValue<string>() == "call_exact_17", new { mock = true, response });
            var followup = ProviderPayload.Prompt("Synthetic tool test");
            if (claude)
            {
                followup["messages"]!.AsArray().Add(new JsonObject { ["role"] = "assistant", ["content"] = response["content"]!.DeepClone() });
                followup["messages"]!.AsArray().Add(JsonNode.Parse("""{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_exact_17","content":"2"}]}"""));
            }
            else
            {
                followup["messages"]!.AsArray().Add(response["choices"]![0]!["message"]!.DeepClone());
                followup["messages"]!.AsArray().Add(JsonNode.Parse("""{"role":"tool","tool_call_id":"call_exact_17","content":"2"}"""));
            }
            await providers.Exchange(name, followup, false, null, CancellationToken.None); check(name + " native tool-result round trip preserves exact ID", handler.RoundTrip, null);
            int emitted = 0; var stream = await providers.Exchange(name, options, true, _ => { emitted++; return Task.CompletedTask; }, CancellationToken.None);
            check(name + " SSE text, usage and tool fragments", emitted >= 2 && stream["complete"]!.GetValue<bool>() && stream.ToJsonString().Contains("call_exact_17") && stream["text"]!.GetValue<string>() == "OK", new { mock = true, stream });
            handler.Fail = true; bool rejected = false; try { await providers.Exchange(name, options, false, null, CancellationToken.None); } catch (IOException) { rejected = true; } check(name + " provider HTTP failure isolated", rejected, null); handler.Fail = false;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); int before = handler.Calls; try { await providers.Exchange(name, options, true, null, cancelled.Token); } catch (OperationCanceledException) { } check(name + " cancelled request makes no outbound call", handler.Calls == before, null);
            vault.Put(new(name, "mock-model", "mock-secret", true, 64, 10, .01m, .1m)); rejected = false; before = handler.Calls;
            try { await providers.Exchange(name, options, false, null, CancellationToken.None); } catch (ArgumentException) { rejected = true; } check(name + " spending reservation blocks before transport", rejected && before == handler.Calls, null);
        }
    }
    sealed class NativeMock : HttpMessageHandler
    {
        public bool Valid, RoundTrip, Fail; public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; if (Fail) return new(HttpStatusCode.TooManyRequests);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!; bool claude = request.RequestUri!.Host == "api.anthropic.com", openai = request.RequestUri.Host == "api.openai.com";
            Valid = body["model"]!.GetValue<string>() == "mock-model" && body[openai ? "max_completion_tokens" : "max_tokens"]!.GetValue<int>() == 64 && (claude ? request.Headers.GetValues("anthropic-version").Single() == "2026-10-21" && body["output_config"]?["format"]?["type"]?.GetValue<string>() == "json_schema" : body["response_format"]?["type"]?.GetValue<string>() == "json_schema");
            RoundTrip |= body["messages"]!.AsArray().Last()?[claude ? "content" : "tool_call_id"]?.ToJsonString().Contains("call_exact_17") == true;
            if (body["stream"]!.GetValue<bool>())
            {
                string events = claude
                    ? "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"call_exact_17\",\"name\":\"sum\",\"input\":{}}}\n\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"a\\\":2}\"}}\n\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}\n\ndata: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":4}}\n\ndata: {\"type\":\"message_stop\"}\n\n"
                    : "data: {\"choices\":[{\"delta\":{\"content\":\"OK\",\"tool_calls\":[{\"index\":0,\"id\":\"call_exact_17\",\"type\":\"function\",\"function\":{\"name\":\"sum\",\"arguments\":\"{\\\"a\\\":2}\"}}]}}]}\n\ndata: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":4}}\n\ndata: [DONE]\n\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(claude ? """{"model":"mock-model","content":[{"type":"tool_use","id":"call_exact_17","name":"sum","input":{"a":2}}],"usage":{"input_tokens":5,"output_tokens":4}}""" : """{"model":"mock-model","choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_exact_17","type":"function","function":{"name":"sum","arguments":"{\"a\":2}"}}]}}],"usage":{"prompt_tokens":5,"completion_tokens":4}}""", Encoding.UTF8, "application/json") };
        }
    }
}
