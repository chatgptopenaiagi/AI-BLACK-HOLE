using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIBlackHole;

sealed class ConversationStore(Paths paths)
{
    readonly object sync = new();
    string FileFor(string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9-]{36}$")) throw new ArgumentException("Invalid conversation identifier.");
        return paths.Data("conversations/" + id + ".json");
    }
    public object[] List()
    {
        string directory = paths.Data("conversations"); if (!Directory.Exists(directory)) return [];
        lock (sync) return Directory.GetFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(100).Select(p =>
        {
            var value = JsonNode.Parse(File.ReadAllText(p))!; string title = value["messages"]?.AsArray().FirstOrDefault(m => m?["role"]?.GetValue<string>() == "user")?["content"]?.GetValue<string>() ?? "New conversation";
            return (object)new { id = Path.GetFileNameWithoutExtension(p), title = title.Length > 70 ? title[..70] : title, modified = File.GetLastWriteTimeUtc(p) };
        }).ToArray();
    }
    public JsonNode Read(string id) { lock (sync) { string path = FileFor(id); if (!File.Exists(path)) throw new ArgumentException("Conversation not found."); return JsonNode.Parse(File.ReadAllText(path))!; } }
    public void Save(string id, JsonObject value)
    {
        if (value.ToJsonString().Length > 900000 || value["messages"] is not JsonArray messages || messages.Count > 500) throw new ArgumentException("Conversation exceeds storage limits.");
        foreach (var message in messages) if (message?["role"]?.GetValue<string>() is not ("user" or "assistant") || message["content"] is not JsonValue) throw new ArgumentException("Invalid conversation message.");
        lock (sync) Json.Save(FileFor(id), new { schema = "abh-conversation/1", id, updated = DateTimeOffset.UtcNow, messages });
    }
}
