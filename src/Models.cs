using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace AIBlackHole;

record GgufInfo(uint Version, ulong TensorCount, Dictionary<string, object?> Metadata, long HeaderBytes)
{
    public string Architecture => Value("general.architecture");
    public string Name => Value("general.name");
    public string Value(string key) => Metadata.GetValueOrDefault(key)?.ToString() ?? "";
    public long Number(string key, long fallback = 0) => long.TryParse(Value(key), out var n) ? n : fallback;
    public int Layers => (int)Number(Architecture + ".block_count");
    public long Context => Number(Architecture + ".context_length");
    public string Quantization => Number("general.file_type", -1) switch { 0 => "F32", 1 => "F16", 2 => "Q4_0", 3 => "Q4_1", 7 => "Q8_0", 8 => "Q5_0", 9 => "Q5_1", 10 => "Q2_K", 11 => "Q3_K_S", 12 => "Q3_K_M", 13 => "Q3_K_L", 14 => "Q4_K_S", 15 => "Q4_K_M", 16 => "Q5_K_S", 17 => "Q5_K_M", 18 => "Q6_K", 30 => "BF16", var other => "GGUF file type " + other };
    public bool Auxiliary => Architecture is "clip" or "whisper" || Value("general.type") == "adapter";
    public bool Embedding => Architecture is "bert" or "nomic-bert" or "nomic-bert-moe";
}

static class GgufInspector
{
    const long MaxHeader = 256L * 1024 * 1024;
    public static GgufInfo Inspect(string path, CancellationToken token = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (stream.Length < 24 || reader.ReadUInt32() != 0x46554747) throw new InvalidDataException("Not a GGUF file: missing GGUF header.");
        uint version = reader.ReadUInt32();
        if (version is not (2 or 3)) throw new InvalidDataException("Only little-endian GGUF versions 2 and 3 are supported by this inspector.");
        ulong tensors = reader.ReadUInt64(), entries = reader.ReadUInt64();
        if (entries > 100000 || tensors > 1000000) throw new InvalidDataException("GGUF counts exceed safety limits.");
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (ulong i = 0; i < entries; i++)
        {
            token.ThrowIfCancellationRequested();
            string key = ReadString(reader, true);
            if (key.Length > 4096 || metadata.ContainsKey(key)) throw new InvalidDataException("Invalid or duplicate GGUF metadata key.");
            metadata.Add(key, ReadValue(reader, reader.ReadUInt32(), true, 0, token));
            Check(stream);
        }
        // Tensor descriptors are bounded and inspected, never tensor weights.
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (ulong i = 0; i < tensors; i++)
        {
            token.ThrowIfCancellationRequested();
            string name = ReadString(reader, true); uint dims = reader.ReadUInt32();
            if (dims is < 1 or > 4 || !names.Add(name)) throw new InvalidDataException("Invalid tensor descriptor.");
            for (int d = 0; d < dims; d++) if (reader.ReadUInt64() == 0) throw new InvalidDataException("Zero tensor dimension.");
            reader.ReadUInt32(); ulong offset = reader.ReadUInt64();
            if (offset >= (ulong)stream.Length) throw new InvalidDataException("Tensor offset exceeds file length.");
            Check(stream);
        }
        if (tensors == 0) throw new InvalidDataException("GGUF contains no tensors.");
        if (stream.Position >= stream.Length) throw new InvalidDataException("GGUF tensor payload is missing.");
        return new(version, tensors, metadata, stream.Position);
    }
    static void Check(Stream s) { if (s.Position > s.Length || s.Position > MaxHeader) throw new InvalidDataException("Truncated or oversized GGUF metadata."); }
    static string ReadString(BinaryReader r, bool keep)
    {
        ulong n = r.ReadUInt64();
        if (n > 16 * 1024 * 1024 || n > (ulong)(r.BaseStream.Length - r.BaseStream.Position)) throw new InvalidDataException("Invalid GGUF string length.");
        if (!keep) { r.BaseStream.Seek((long)n, SeekOrigin.Current); return ""; }
        return new UTF8Encoding(false, true).GetString(r.ReadBytes((int)n));
    }
    static object? ReadValue(BinaryReader r, uint type, bool keep, int depth, CancellationToken token)
    {
        if (depth > 1) throw new InvalidDataException("Nested GGUF arrays exceed limits.");
        switch (type)
        {
            case 0: return r.ReadByte(); case 1: return r.ReadSByte(); case 2: return r.ReadUInt16(); case 3: return r.ReadInt16();
            case 4: return r.ReadUInt32(); case 5: return r.ReadInt32(); case 6: return r.ReadSingle(); case 7: return r.ReadByte() != 0;
            case 8: return ReadString(r, keep);
            case 10: return r.ReadUInt64(); case 11: return r.ReadInt64(); case 12: return r.ReadDouble();
            case 9:
                uint element = r.ReadUInt32(); ulong count = r.ReadUInt64();
                if (count > 2000000 || element == 9) throw new InvalidDataException("Invalid GGUF array.");
                var sample = new List<object?>();
                for (ulong i = 0; i < count; i++) { if (i % 4096 == 0) { token.ThrowIfCancellationRequested(); Check(r.BaseStream); } var v = ReadValue(r, element, keep && i < 4, depth + 1, token); if (keep && i < 4) sample.Add(v); }
                return new { elementType = element, count, sample }; // Never retain full tokenizer arrays.
            default: throw new InvalidDataException("Unknown GGUF metadata type: " + type);
        }
    }
}

sealed record ModelRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..16];
    public string Path { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string VolumeIdentity { get; set; } = "";
    public string VolumeRelativePath { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string QuickFingerprint { get; set; } = "";
    public string? Sha256 { get; set; }
    public GgufInfo? Info { get; set; }
    public string State { get; set; } = "DISCOVERED";
    public string LastKnownLocation { get; set; } = "";
    public DateTimeOffset? LastVerification { get; set; }
    public DateTimeOffset? LastSuccessfulLaunch { get; set; }
    public string? VerifiedEngine { get; set; }
    public string[] AuxiliaryFiles { get; set; } = [];
    public string[] Shards { get; set; } = [];
    public Dictionary<string, string> Capabilities { get; set; } = new();
    public string? Error { get; set; }
    public string? Repository { get; set; }
    public string? Revision { get; set; }
    public string[] MatchingModelIds { get; set; } = [];
    public string? PortableRelativePath { get; set; }
    public override string ToString() => $"{(string.IsNullOrEmpty(Info?.Name) ? OriginalFileName : Info.Name)} · {Info?.Quantization} · {Bytes / 1073741824.0:F2} GiB · {State}";
}
record RegistryDocument(int Schema, List<ModelRecord> Models);

sealed class ModelRegistry
{
    readonly string file; readonly string root; readonly object sync = new(); List<ModelRecord> models;
    public ModelRegistry(Paths paths)
    {
        file = paths.Data("models.json"); root = Path.GetFullPath(paths.Root);
        if (!File.Exists(file)) models = [];
        else { var doc = Json.Read<RegistryDocument>(file); if (doc.Schema != 1) throw new IOException("Unknown registry schema. Backup retained; migration required."); models = doc.Models; }
        foreach (var model in models.Where(m => m.PortableRelativePath != null))
        {
            string resolved = Path.GetFullPath(Path.Combine(root, model.PortableRelativePath!));
            if (!resolved.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Portable model path escapes the application folder.");
            model.Path = resolved; model.Shards = model.Info == null ? [resolved] : FindShards(resolved, model.Info);
        }
    }
    public ModelRecord[] All() { lock (sync) return models.ToArray(); }
    public ModelRecord Get(string id) { lock (sync) return models.SingleOrDefault(m => m.Id == id) ?? throw new ArgumentException("Unknown registered model."); }
    public void Save() { lock (sync) { if (File.Exists(file)) File.Copy(file, file + ".bak", true); Json.Save(file, new RegistryDocument(1, models)); } }
    public void Remove(string id) { lock (sync) { models.RemoveAll(m => m.Id == id); Save(); } }
    public static string SafePath(string path, bool allowNetwork = false)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\.\") || path.StartsWith(@"\\?\")) throw new ArgumentException("Select a normal absolute file or folder path.");
        var full = System.IO.Path.GetFullPath(path);
        if (full.StartsWith(@"\\") && !allowNetwork) throw new ArgumentException("Network paths require Enable network shares in Settings.");
        var item = new FileInfo(full);
        if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Select the real model file, not a filesystem link.");
        return full;
    }
    public ModelRecord Add(string path, bool allowNetwork = false, CancellationToken token = default)
    {
        path = SafePath(path, allowNetwork); var fileInfo = new FileInfo(path); var info = GgufInspector.Inspect(path, token);
        string quick = QuickHash(path);
        lock (sync)
        {
            var old = models.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (old != null && old.Bytes == fileInfo.Length && old.LastWriteUtc == fileInfo.LastWriteTimeUtc && old.QuickFingerprint == quick) return old;
            var model = old ?? new ModelRecord();
            model.Path = path; model.LastKnownLocation = path; model.OriginalFileName = fileInfo.Name; model.Bytes = fileInfo.Length; model.LastWriteUtc = fileInfo.LastWriteTimeUtc;
            model.PortableRelativePath = path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetRelativePath(root, path) : null;
            model.QuickFingerprint = quick; model.Sha256 = null; model.LastVerification = null; model.VerifiedEngine = null; model.Info = info;
            model.VolumeIdentity = VolumeId(path); model.VolumeRelativePath = System.IO.Path.GetRelativePath(System.IO.Path.GetPathRoot(path)!, path);
            model.State = info.Auxiliary ? "UNSUPPORTED" : "COMPATIBILITY UNKNOWN"; model.Error = info.Auxiliary ? "Auxiliary/projector file; not a standalone text model." : null;
            model.Capabilities = new() { ["TEXT_GENERATION"] = info.Auxiliary || info.Embedding ? "UNSUPPORTED" : "UNKNOWN", ["CHAT"] = info.Auxiliary || info.Embedding ? "UNSUPPORTED" : info.Metadata.ContainsKey("tokenizer.chat_template") ? "DECLARED" : "UNKNOWN", ["EMBEDDINGS"] = info.Embedding ? "DETECTED" : "UNKNOWN", ["TOOL_CALLING"] = "UNKNOWN", ["VISION"] = "UNKNOWN", ["AUDIO"] = "UNKNOWN", ["STRUCTURED_JSON"] = "UNKNOWN", ["CODE_ASSISTANCE"] = "UNKNOWN" };
            model.Shards = FindShards(path, info);
            if (old == null) models.Add(model); Save(); return model;
        }
    }
    public void Refresh()
    {
        lock (sync) foreach (var model in models)
        {
            var f = new FileInfo(model.Path);
            if (!f.Exists) { model.State = "MODEL UNAVAILABLE"; continue; }
            if (f.Length != model.Bytes || f.LastWriteTimeUtc != model.LastWriteUtc) { model.State = "ERROR"; model.Error = "File changed. Inspect and verify again."; model.Sha256 = null; model.VerifiedEngine = null; }
            else if (model.State == "MODEL UNAVAILABLE") model.State = "INSPECTED";
        }
        Save();
    }
    public async Task<string> Verify(ModelRecord model, IProgress<double>? progress, CancellationToken token)
    {
        var before = new FileInfo(model.Path); long length = before.Length; DateTime stamp = before.LastWriteTimeUtc;
        if (length != model.Bytes || stamp != model.LastWriteUtc) throw new IOException("Model changed. Inspect it again before verification.");
        string hash = await HashFile(model.Path, progress, token);
        var after = new FileInfo(model.Path);
        if (after.Length != length || after.LastWriteTimeUtc != stamp) throw new IOException("Model changed during verification.");
        model.Sha256 = hash; model.LastVerification = DateTimeOffset.UtcNow;
        lock (sync) { var matching = models.Where(m => m.Sha256 == hash).ToArray(); foreach (var match in matching) match.MatchingModelIds = matching.Where(m => m.Id != match.Id).Select(m => m.Id).ToArray(); }
        Save(); return hash;
    }
    public static async Task<string> HashFile(string path, IProgress<double>? progress, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        try { int n; long done = 0; while ((n = await stream.ReadAsync(buffer, token)) > 0) { hash.AppendData(buffer, 0, n); done += n; progress?.Report(100.0 * done / stream.Length); } return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    public static string QuickHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(stream.Length)); byte[] buffer = new byte[65536];
        foreach (long pos in new[] { 0L, Math.Max(0, stream.Length / 2 - 32768), Math.Max(0, stream.Length - 65536) }.Distinct()) { stream.Position = pos; int count = stream.Read(buffer); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    public static string VolumeId(string path)
    {
        var root = System.IO.Path.GetPathRoot(path)!; var name = new StringBuilder(1024);
        return GetVolumeNameForVolumeMountPoint(root, name, name.Capacity) ? name.ToString() : "unavailable:" + root;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, int length);
    static string[] FindShards(string path, GgufInfo info)
    {
        int count = (int)info.Number("split.count", 1); if (count <= 1) return [path];
        if (count > 1024) throw new InvalidDataException("Split count exceeds limits.");
        var match = Regex.Match(path, @"^(.*)-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidDataException("Split GGUF needs standard -00001-of-000NN filenames.");
        return Enumerable.Range(1, count).Select(i => $"{match.Groups[1].Value}-{i:D5}-of-{count:D5}.gguf").ToArray();
    }
    public IEnumerable<string> Scan(string root, bool allowNetwork, CancellationToken token, int maxFiles = 10000, int maxDepth = 8, bool blobs = false)
    {
        root = SafePath(root, allowNetwork); var queue = new Queue<(string path, int depth)>(); queue.Enqueue((root, 0)); int examined = 0;
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (queue.Count > 0)
        {
            token.ThrowIfCancellationRequested(); if (DateTime.UtcNow > deadline || examined >= maxFiles) yield break;
            var (dir, depth) = queue.Dequeue(); if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            string[] files, dirs; try { files = Directory.GetFiles(dir); dirs = depth < maxDepth ? Directory.GetDirectories(dir) : []; } catch (UnauthorizedAccessException) { continue; } catch (IOException) { continue; }
            foreach (string filePath in files)
            {
                token.ThrowIfCancellationRequested(); if (++examined > maxFiles) yield break;
                if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0) continue;
                if (filePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || blobs && new FileInfo(filePath).Length > 1024 * 1024) yield return filePath;
            }
            foreach (string sub in dirs) queue.Enqueue((sub, depth + 1));
        }
    }
    public async Task<bool> Reconnect(ModelRecord model, string candidate, bool allowNetwork, CancellationToken token)
    {
        candidate = SafePath(candidate, allowNetwork);
        if (model.Sha256 == null) throw new IOException("A full fingerprint is required before reconnecting a missing model.");
        if (new FileInfo(candidate).Length != model.Bytes || QuickHash(candidate) != model.QuickFingerprint || await HashFile(candidate, null, token) != model.Sha256) return false;
        model.Path = candidate; model.LastKnownLocation = candidate; model.LastWriteUtc = File.GetLastWriteTimeUtc(candidate); model.VolumeIdentity = VolumeId(candidate); model.VolumeRelativePath = System.IO.Path.GetRelativePath(System.IO.Path.GetPathRoot(candidate)!, candidate); model.State = "INSPECTED"; Save(); return true;
    }
}
