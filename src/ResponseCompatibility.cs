using System.Text.Json.Nodes;

namespace AIBlackHole;

sealed class ResponseNamespaces
{
    readonly Dictionary<string, (string Space, string Name)> aliases = new(StringComparer.Ordinal);
    public bool HasAliases => aliases.Count > 0;
    public static ResponseNamespaces Prepare(JsonObject body)
    {
        var map = new ResponseNamespaces();
        if (body["tools"] is not JsonArray tools) return map;
        var flattened = new JsonArray(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool?["type"]?.GetValue<string>() == "function")
            {
                string name = tool["name"]?.GetValue<string>() ?? throw new ArgumentException("Tool name required.");
                if (!names.Add(name)) throw new ArgumentException("Duplicate tool name.");
                flattened.Add(tool.DeepClone()); continue;
            }
            if (tool?["type"]?.GetValue<string>() != "namespace" || tool["tools"] is not JsonArray nested || nested.Count == 0) throw new ArgumentException("Only functions and namespaces containing functions are supported.");
            string space = tool["name"]?.GetValue<string>() ?? throw new ArgumentException("Namespace name required.");
            foreach (var function in nested)
            {
                if (function?["type"]?.GetValue<string>() != "function") throw new ArgumentException("Custom and hosted namespace tools are unsupported.");
                string name = function["name"]?.GetValue<string>() ?? throw new ArgumentException("Function name required.");
                string alias = "abhns_" + Json.Hash(space + "\0" + name)[..24];
                if (!names.Add(alias) || !map.aliases.TryAdd(alias, (space, name))) throw new ArgumentException("Duplicate or conflicting namespaced tool.");
                var copy = function.DeepClone().AsObject(); copy["name"] = alias;
                copy["description"] = space + "." + name + " — " + (function["description"]?.GetValue<string>() ?? "");
                flattened.Add(copy);
            }
        }
        if (flattened.Count > 32) throw new ArgumentException("At most 32 local functions are supported.");
        body["tools"] = flattened;
        if (body["input"] is JsonArray items)
            foreach (var item in items)
                if (item?["type"]?.GetValue<string>() == "function_call") map.ToEngine(item.AsObject());
        if (body["tool_choice"] is JsonObject choice && choice["type"]?.GetValue<string>() == "function") map.ToEngine(choice);
        return map;
    }
    void ToEngine(JsonObject call)
    {
        string? space = call["namespace"]?.GetValue<string>();
        if (space == null) return;
        string name = call["name"]?.GetValue<string>() ?? throw new ArgumentException("Namespaced function name required.");
        string alias = "abhns_" + Json.Hash(space + "\0" + name)[..24];
        if (!aliases.ContainsKey(alias)) throw new ArgumentException("History refers to an undeclared namespaced function.");
        call["name"] = alias; call.Remove("namespace");
    }
    public void Restore(JsonNode? node)
    {
        if (node is JsonArray array) { foreach (var item in array) Restore(item); return; }
        if (node is not JsonObject obj) return;
        string? type = obj["type"]?.GetValue<string>();
        if ((type == "function_call" || type == "response.function_call_arguments.done") && obj["name"] is JsonValue value && value.TryGetValue<string>(out var alias) && aliases.TryGetValue(alias, out var original))
        { obj["name"] = original.Name; obj["namespace"] = original.Space; }
        foreach (var property in obj.ToArray()) if (property.Value is JsonArray or JsonObject) Restore(property.Value);
    }
}

static class ResponseCompatibility
{
    // The bundled Qwen template accepts a single leading system message. Preserve
    // instruction order and priority; never promote later messages or tool data.
    public static void Normalize(JsonObject body)
    {
        if (body["tools"] is JsonArray tools && tools.Any(t => t?["type"]?.GetValue<string>() != "function"))
            throw new ArgumentException("This local runtime supports function tools only. Use the isolated ABH Codex profile; namespace/custom/hosted tools are unsupported.");
        if (body["input"] is not JsonArray input) return;
        if (input.Count > 80) throw new ArgumentException("Local Responses accepts at most 80 input items.");
        var system = new List<string>(); var developer = new List<string>();
        if (body["instructions"] is JsonValue instruction && instruction.TryGetValue<string>(out var root) && !string.IsNullOrEmpty(root)) system.Add(root);
        var rest = new JsonArray(); bool encounteredContent = false;
        foreach (var item in input)
        {
            string? role = item?["role"]?.GetValue<string>();
            if (role is "system" or "developer")
            {
                if (encounteredContent) throw new ArgumentException("This model requires system/developer instructions at the beginning. Later instruction changes cannot be translated safely.");
                string text = Text(item?["content"]);
                (role == "system" ? system : developer).Add(text);
            }
            else { encounteredContent = true; rest.Add(item?.DeepClone()); }
        }
        if (system.Count == 0 && developer.Count == 0) return;
        string combined = string.Join("\n\n", system);
        if (developer.Count > 0) combined += (combined.Length > 0 ? "\n\nDeveloper instructions (subordinate to the system instructions above):\n" : "") + string.Join("\n\n", developer);
        rest.Insert(0, new JsonObject { ["type"] = "message", ["role"] = "system", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = combined }) });
        body["input"] = rest; body.Remove("instructions");
    }
    static string Text(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        if (content is JsonArray parts && parts.All(p => p?["type"]?.GetValue<string>() is "input_text" or "output_text"))
            return string.Join("\n", parts.Select(p => p?["text"]?.GetValue<string>() ?? ""));
        throw new ArgumentException("Leading local instructions must contain text only.");
    }
}
