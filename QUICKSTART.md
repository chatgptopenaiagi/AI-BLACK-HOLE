# Quick start

Run the root `AI-BLACK-HOLE.exe` from the complete extracted Windows x64 portable folder. Keep its runtime, web and native library files together. No administrator privileges, global Python or global .NET installation are required. The installer creates shortcuts and stores writable data under the current user's local application-data folder.

Choose **ADD MODEL**. Files can remain on another disk, an external SSD, or an explicitly enabled network share. Extensionless GGUF blobs can be selected using All files. **SCAN FOLDER** performs bounded discovery; it skips directory links. **REFRESH REGISTRY** detects missing or changed files. Removing an entry leaves its file untouched. Reconnect requires the same verified fingerprint.

Use **VERIFY FILE**, then **AUTO CALIBRATE**. Cancel is available during long jobs. If the model is too large for current free memory, close another owned application or select a smaller model. ABH does not stop another application's inference engine. **SAFE** uses CPU. Measured profiles are invalidated after material model, engine or hardware changes.

**START MODEL** loads the selection; **OPEN CHAT** opens the offline workspace. The browser receives a short-lived launch invitation, which is removed from its address bar. Conversations persist in the selected ABH data directory. Stop generation cancels a request; Stop model unloads the engine. New conversation, regenerate, copy, export and conversation selection are available.

To obtain an optional small model, open Hugging Face Explorer, paste a repository URL, choose an exact GGUF, review its size and choose a destination. Downloads pin a revision and verify the repository's SHA-256 identity. Existing local inference remains usable after a failed download. No model download occurs merely by opening ABH.

Open **API Connections** to create scoped keys. Copy a new key once, store it privately, and rotate or revoke it there. Cloud API credentials are distinct and stored in a passphrase-encrypted vault. A ChatGPT subscription or Codex login is not an OpenAI API credential.

For recovery, inspect Diagnostics, select SAFE, or reconnect an unavailable model. Help and About are available offline in both interfaces. Back up `data/` and `vault/` privately; never publish them.
