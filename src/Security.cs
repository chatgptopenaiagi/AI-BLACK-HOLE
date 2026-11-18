using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIBlackHole;

record ClientKey(string Id, string Label, string Hash, string[] Scopes, int RequestsPerMinute, DateTimeOffset Created, bool Revoked = false, int MaxTotalRequests = 0, int UsedRequests = 0);
sealed class KeyStore
{
    readonly string file; readonly object sync = new(); List<ClientKey> keys;
    readonly Dictionary<string, (DateTimeOffset minute, int count)> windows = new();
    public KeyStore(string file) { this.file = file; keys = File.Exists(file) ? Json.Read<List<ClientKey>>(file) : []; }
    public IReadOnlyList<ClientKey> List() { lock (sync) return keys.ToArray(); }
    public (ClientKey record, string secret) Create(string label, int limit = 30, int maxTotalRequests = 0, string[]? scopes = null)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 80 || limit is < 1 or > 600) throw new ArgumentException("Choose a label (1–80 characters) and 1–600 requests/minute.");
        scopes ??= ["models", "chat", "responses"];
        if (maxTotalRequests is < 0 or > 1000 || scopes.Length == 0 || scopes.Any(s => s is not ("models" or "chat" or "responses" or "runtime" or "calibration" or "tasks" or "mcp" or "mcp.read"))) throw new ArgumentException("Invalid total request budget or API scopes.");
        lock (sync)
        {
            string secret = "abh_" + Json.Secret();
            var record = new ClientKey(Guid.NewGuid().ToString("N"), label, Json.Hash(secret), scopes.ToArray(), limit, DateTimeOffset.UtcNow, MaxTotalRequests: maxTotalRequests);
            var next = keys.Append(record).ToList(); Json.Save(file, next); keys = next; return (record, secret);
        }
    }
    public void Revoke(string id) { lock (sync) { var next = keys.Select(k => k.Id == id ? k with { Revoked = true } : k).ToList(); Json.Save(file, next); keys = next; } }
    public (ClientKey record, string secret) Rotate(string id)
    {
        lock (sync)
        {
            var old = keys.Single(k => k.Id == id && !k.Revoked);
            string secret = "abh_" + Json.Secret();
            var fresh = old with { Id = Guid.NewGuid().ToString("N"), Hash = Json.Hash(secret), Created = DateTimeOffset.UtcNow, UsedRequests = 0 };
            var next = keys.Select(k => k.Id == id ? k with { Revoked = true } : k).Append(fresh).ToList();
            Json.Save(file, next); keys = next; return (fresh, secret);
        }
    }
    public int Authorize(string secret, string scope)
    {
        if (secret.Length > 200) return 401;
        lock (sync)
        {
            var key = keys.FirstOrDefault(k => !k.Revoked && Json.Equal(k.Hash, Json.Hash(secret)));
            if (key == null) return 401;
            if (!key.Scopes.Contains(scope) && !(scope == "mcp.read" && key.Scopes.Contains("mcp"))) return 403;
            if (key.MaxTotalRequests > 0 && key.UsedRequests >= key.MaxTotalRequests) return 429;
            var now = DateTimeOffset.UtcNow;
            var window = windows.GetValueOrDefault(key.Id);
            if ((now - window.minute).TotalSeconds >= 60) window = (now, 0);
            if (window.count >= key.RequestsPerMinute) return 429;
            if (key.MaxTotalRequests > 0)
            {
                var next = keys.Select(k => k.Id == key.Id ? k with { UsedRequests = k.UsedRequests + 1 } : k).ToList();
                Json.Save(file, next); keys = next;
            }
            windows[key.Id] = (window.minute, window.count + 1); return 200;
        }
    }
}

record VaultEnvelope(int Version, string Kdf, int Iterations, string Salt, string Nonce, string Tag, string Ciphertext);
record Credential(string Provider, string Model, string Secret, bool Enabled, int MaxTokens = 256, int MaxCallsPerSession = 3, decimal SessionBudgetUsd = 0, decimal RequestReservationUsd = 0);
sealed class Vault : IDisposable
{
    const int Iterations = 600000;
    static readonly byte[] Aad = Encoding.UTF8.GetBytes("ABH-VAULT/1");
    readonly string file; byte[]? key, salt;
    Dictionary<string, Credential> values = new();
    public bool Unlocked => key != null;
    public bool Exists => File.Exists(file);
    public Vault(string file) { this.file = file; }
    public void Unlock(string password)
    {
        if (password.Length < 12) throw new ArgumentException("Use a passphrase of at least 12 characters. It is required on every PC and cannot be recovered.");
        Lock();
        if (!Exists) { salt = RandomNumberGenerator.GetBytes(16); key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32); Save(); return; }
        var e = Json.Read<VaultEnvelope>(file);
        if (e.Version != 1 || e.Kdf != "PBKDF2-SHA256" || e.Iterations != Iterations) throw new CryptographicException("Unsupported vault format.");
        var s = Convert.FromBase64String(e.Salt); var candidate = Rfc2898DeriveBytes.Pbkdf2(password, s, e.Iterations, HashAlgorithmName.SHA256, 32);
        byte[] plain = new byte[Convert.FromBase64String(e.Ciphertext).Length];
        try
        {
            using var aes = new AesGcm(candidate, 16);
            aes.Decrypt(Convert.FromBase64String(e.Nonce), Convert.FromBase64String(e.Ciphertext), Convert.FromBase64String(e.Tag), plain, Aad);
            values = JsonSerializer.Deserialize<Dictionary<string, Credential>>(plain, Json.Options)!;
            key = candidate; salt = s;
        }
        catch { CryptographicOperations.ZeroMemory(candidate); throw new CryptographicException("Wrong passphrase or damaged vault. No credentials were loaded."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public Credential? Get(string provider) => Unlocked ? values.GetValueOrDefault(provider) : null;
    public void Put(Credential credential) { if (!Unlocked) throw new InvalidOperationException("Unlock the vault first."); values[credential.Provider] = credential; Save(); }
    public void Remove(string provider) { if (!Unlocked) throw new InvalidOperationException("Unlock the vault first."); values.Remove(provider); Save(); }
    void Save()
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12), plain = JsonSerializer.SerializeToUtf8Bytes(values, Json.Options), cipher = new byte[plain.Length], tag = new byte[16];
        try { using var aes = new AesGcm(key!, 16); aes.Encrypt(nonce, plain, cipher, tag, Aad); Json.Save(file, new VaultEnvelope(1, "PBKDF2-SHA256", Iterations, Convert.ToBase64String(salt!), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher))); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Lock() { if (key != null) CryptographicOperations.ZeroMemory(key); key = null; salt = null; values.Clear(); }
    public void Dispose() => Lock();
}
