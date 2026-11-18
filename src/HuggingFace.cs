using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIBlackHole;

record HubFile(string Repository, string Revision, string Name, long Bytes, string? Sha256);
static class HuggingFace
{
    static readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
    public static (string repository, string revision, string? file) Parse(string input)
    {
        string repository, revision = "main"; string? file = null;
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != "https" || uri.Host != "huggingface.co" || uri.UserInfo != "") throw new ArgumentException("Use an HTTPS huggingface.co repository or resolve file URL.");
            var pieces = uri.AbsolutePath.Trim('/').Split('/'); if (pieces.Length < 2) throw new ArgumentException("Repository owner/name required.");
            repository = pieces[0] + "/" + pieces[1];
            if (pieces.Length >= 4 && pieces[2] is "resolve" or "blob" or "tree") { revision = Uri.UnescapeDataString(pieces[3]); if (pieces.Length > 4) file = Uri.UnescapeDataString(string.Join('/', pieces.Skip(4))); }
        }
        else repository = input.Trim();
        if (!Regex.IsMatch(repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")) throw new ArgumentException("Use repository owner/name.");
        return (repository, revision, file);
    }
    public static async Task<HubFile[]> List(string input, CancellationToken token)
    {
        var (repo, revision, selected) = Parse(input);
        using var response = await http.GetAsync($"https://huggingface.co/api/models/{repo}/revision/{Uri.EscapeDataString(revision)}?blobs=true", token); response.EnsureSuccessStatusCode();
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!; string commit = data["sha"]!.GetValue<string>();
        if (!Regex.IsMatch(commit, "^[a-f0-9]{40}$")) throw new IOException("Repository did not return an immutable revision.");
        return (data["siblings"]?.AsArray() ?? []).Where(f => f?["rfilename"]?.GetValue<string>().EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true)
            .Select(f => new HubFile(repo, commit, f!["rfilename"]!.GetValue<string>(), f["size"]?.GetValue<long>() ?? f["lfs"]?["size"]?.GetValue<long>() ?? 0, f["lfs"]?["sha256"]?.GetValue<string>())).Where(f => selected == null || f.Name == selected).ToArray();
    }
    static async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
    {
        for (int redirects = 0; redirects < 6; redirects++)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme != "https" || !(uri.Host == "huggingface.co" || uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase))) throw new IOException("Download redirected outside approved Hugging Face hosts.");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location ?? throw new IOException("Missing redirect location.");
            var next = new HttpRequestMessage(HttpMethod.Get, location.IsAbsoluteUri ? location : new Uri(uri, location)); next.Headers.Range = request.Headers.Range; response.Dispose(); request.Dispose(); request = next;
        }
        throw new IOException("Too many download redirects.");
    }
    public static async Task<ModelRecord> Download(ApplicationService app, HubFile selection, string destination, Job job, CancellationToken token)
    {
        // Re-read the immutable revision; UI supplied sizes and hashes are never trusted.
        var files = await List($"https://huggingface.co/{selection.Repository}/tree/{selection.Revision}", token);
        var selected = files.SingleOrDefault(f => f.Name == selection.Name) ?? throw new ArgumentException("File absent from the selected revision.");
        if (selected.Bytes <= 0 || selected.Sha256 == null || !Regex.IsMatch(selected.Sha256, "^[a-f0-9]{64}$")) throw new IOException("This beta requires a known size and LFS SHA-256 before downloading.");
        if (Regex.IsMatch(selected.Name, @"-\d{5}-of-\d{5}\.gguf$")) throw new ArgumentException("Split models are inspected but automated shard download is not supported in this beta. Select a complete single-file variant.");
        destination = ModelRegistry.SafePath(destination, app.Settings.AllowNetworkShares); Directory.CreateDirectory(destination);
        string name = Path.GetFileName(selected.Name); string final = Path.Combine(destination, name); string partial = final + ".part"; string metadata = partial + ".json";
        if (File.Exists(final))
        {
            if (new FileInfo(final).Length == selected.Bytes && await ModelRegistry.HashFile(final, null, token) == selected.Sha256) return app.Models.Add(final, app.Settings.AllowNetworkShares, token);
            throw new IOException("Destination exists with different content. Choose a different folder; it will not be overwritten.");
        }
        long offset = 0;
        if (File.Exists(partial))
        {
            if (!File.Exists(metadata) || Json.Read<HubFile>(metadata) != selected) throw new IOException("Existing partial download belongs to another identity. Choose another directory.");
            offset = new FileInfo(partial).Length; if (offset > selected.Bytes) throw new IOException("Partial download is larger than the expected file.");
        }
        if (new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace < selected.Bytes - offset + 128L * 1024 * 1024) throw new IOException("Insufficient free space at the selected destination.");
        Json.Save(metadata, selected);
        if (offset < selected.Bytes)
        {
            var url = $"https://huggingface.co/{selected.Repository}/resolve/{selected.Revision}/{string.Join('/', selected.Name.Split('/').Select(Uri.EscapeDataString))}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url); if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await Send(request, token); response.EnsureSuccessStatusCode();
            if (offset > 0 && (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != offset)) throw new IOException("Server did not honor resume range; existing partial preserved.");
            await using var input = await response.Content.ReadAsStreamAsync(token); await using var output = new FileStream(partial, offset == 0 ? FileMode.CreateNew : FileMode.Append, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            byte[] buffer = new byte[1024 * 1024]; int n;
            while ((n = await input.ReadAsync(buffer, token)) > 0) { if (offset + n > selected.Bytes) throw new IOException("Server sent more bytes than the pinned identity."); await output.WriteAsync(buffer.AsMemory(0, n), token); offset += n; job.Progress = 90.0 * offset / selected.Bytes; job.Message = $"Downloading {offset / 1048576.0:F1}/{selected.Bytes / 1048576.0:F1} MiB"; }
        }
        if (new FileInfo(partial).Length != selected.Bytes) throw new IOException("Incomplete download; resume it from the same selection.");
        job.Message = "Verifying downloaded SHA-256";
        if (await ModelRegistry.HashFile(partial, new Progress<double>(p => job.Progress = 90 + p / 10), token) != selected.Sha256) throw new IOException("Downloaded SHA-256 mismatch; partial retained for diagnosis, not registered.");
        GgufInspector.Inspect(partial, token); File.Move(partial, final, false);
        var model = app.Models.Add(final, app.Settings.AllowNetworkShares, token); model.Sha256 = selected.Sha256; model.LastVerification = DateTimeOffset.UtcNow; model.Repository = selected.Repository; model.Revision = selected.Revision; app.Models.Save(); return model;
    }
}
