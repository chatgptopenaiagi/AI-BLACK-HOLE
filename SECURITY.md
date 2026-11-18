# Security

ABH binds loopback only and validates Host and browser Origin. Engine and gateway keys are separate. Engine secrets pass through a private inherited pipe/environment, never process arguments. Browser launch tickets are short-lived and consumed once; the address fragment is cleared. External API keys are scoped, hashed, rate-limited, rotatable and revocable. Diagnostics must not be published without review.

Provider credentials use AES-GCM with a passphrase-derived key (PBKDF2-SHA256, 600,000 iterations) and require unlocking each session. The passphrase is not persisted. Anyone controlling the user account or running process can access unlocked memory; this vault is not a sandbox against a compromised host. Portable backups of data and vault belong in private storage.

Model discovery/inspection/calibration are read-only. Registry removal is not file deletion. Optional model copying is explicit and hash-verified. Downloads pin an HF revision and expected size/hash, restrict redirects to supported HTTPS storage hosts, and refuse an unrelated existing destination file. No remote model code is executed.

MCP does not expose arbitrary paths or shell commands. Codex runs with isolated configuration and a read-only workspace policy. Unsupported tools fail explicitly. Provider output, model output and imported reports are untrusted content. Cloud requests require explicit selection and approval; credentials are never given to another agent. Native provider tool proposals are displayed as data.

Resource estimates and job limits reduce accidental exhaustion; other workloads can still change free memory. Process jobs contain owned engine children. Tests separate deliberate owned-process failure from clean shutdown. The application is a beta, not a security boundary against hostile local administrators.

Report vulnerabilities through the repository's private security reporting facility when available; otherwise open an issue containing no secrets or private data and request a private contact path. Do not post API keys, vaults, local paths or conversation exports.
