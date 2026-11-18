using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AIBlackHole;

// Native protocol payloads are retained. A tool proposal never executes automatically.
interface IProviderAdapter
{
    string Name { get; }
    HttpRequestMessage Build(Credential credential, JsonObject options, bool stream);
    string Text(JsonNode body);
    string Delta(JsonNode body);
    bool Complete(JsonNode body);
}
abstract class ChatProviderAdapter : IProviderAdapter
{
    public abstract string Name { get; }
    protected abstract string Endpoint { get; }
    protected virtual string TokenField => "max_tokens";
    public HttpRequestMessage Build(Credential credential, JsonObject options, bool stream)
    {
        ProviderPayload.Validate(options, ["messages", "tools", "tool_choice", "response_format"]);
        var body = options.DeepClone().AsObject(); body["model"] = credential.Model; body[TokenField] = credential.MaxTokens; body["stream"] = stream;
        if (stream) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret); return request;
    }
    public string Text(JsonNode body) => body["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
    public string Delta(JsonNode body) => body["choices"]?[0]?["delta"]?["content"]?.GetValue<string>() ?? "";
    public bool Complete(JsonNode body) => body["choices"]?[0]?["finish_reason"] != null;
}
sealed class OpenAiAdapter : ChatProviderAdapter
{
    public override string Name => "OpenAI";
    protected override string Endpoint => "https://api.openai.com/v1/chat/completions";
    protected override string TokenField => "max_completion_tokens";
}
sealed class GrokAdapter : ChatProviderAdapter
{
    public override string Name => "Grok";
    protected override string Endpoint => "https://api.x.ai/v1/chat/completions";
}
sealed class ClaudeAdapter : IProviderAdapter
{
    public string Name => "Claude";
    public HttpRequestMessage Build(Credential credential, JsonObject options, bool stream)
    {
        ProviderPayload.Validate(options, ["messages", "system", "tools", "tool_choice", "output_config"]);
        var body = options.DeepClone().AsObject(); body["model"] = credential.Model; body["max_tokens"] = credential.MaxTokens; body["stream"] = stream;
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("x-api-key", credential.Secret); request.Headers.Add("anthropic-version", "2026-10-21"); return request;
    }
    public string Text(JsonNode body) => string.Join("\n", (body["content"]?.AsArray() ?? []).Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()));
    public string Delta(JsonNode body) => body["type"]?.GetValue<string>() == "content_block_delta" && body["delta"]?["type"]?.GetValue<string>() == "text_delta" ? body["delta"]?["text"]?.GetValue<string>() ?? "" : "";
    public bool Complete(JsonNode body) => body["type"]?.GetValue<string>() == "message_stop";
}
static class ProviderPayload
{
    public static void Validate(JsonObject body, string[] allowed)
    {
        if (body.ToJsonString().Length > 32000 || body.Any(p => !allowed.Contains(p.Key))) throw new ArgumentException("Unsupported provider option or request larger than 32,000 characters.");
        if (body["messages"] is not JsonArray messages || messages.Count is < 1 or > 32) throw new ArgumentException("Supply 1–32 native provider messages.");
        if (body["tools"] is JsonArray tools && tools.Count > 16) throw new ArgumentException("At most 16 tool definitions are supported.");
        // Text and user supplied tool results only. Remote URLs/files and hosted tools require a separate explicit integration.
        foreach (var message in messages)
        {
            if (message?["role"]?.GetValue<string>() is not ("user" or "assistant" or "system" or "developer" or "tool")) throw new ArgumentException("Unsupported message role.");
            if (message["content"] is JsonArray blocks)
                foreach (var block in blocks) if (block?["type"]?.GetValue<string>() is not ("text" or "tool_use" or "tool_result")) throw new ArgumentException("Only text and explicit tool blocks are supported.");
        }
        if (body["tools"] is JsonArray definitions)
            foreach (var tool in definitions) if (tool?["type"] is JsonNode type && type.GetValue<string>() != "function") throw new ArgumentException("Hosted tools are not enabled.");
    }
    public static JsonObject Prompt(string prompt) => new() { ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }) };
}
