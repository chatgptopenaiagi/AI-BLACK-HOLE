# Agent connectors

| Connector | Implemented direction | Evidence and boundary |
|---|---|---|
| Local GGUF | Applications → active ABH model | Real two-model inference; scoped API |
| Codex App Server | ABH → installed Codex | Real initialize, threads, turns, events, cancellation/approval protocol; isolated local profile |
| Codex local provider | Codex → ABH Responses | Qwen coding proposal and actual MCP runtime tool round trip; no general custom-tool compatibility claim |
| OpenAI | ABH → Chat Completions API | Native bearer, token limit, SSE, function/tool-result messages, JSON-schema request; mock tests only |
| Claude | ABH → Anthropic Messages | Native x-api-key/version, text/tool blocks, SSE and output_config; mock tests only |
| Grok | ABH → xAI Chat Completions | Separate endpoint and token configuration; native tool/structured requests and SSE; mock tests only |
| DRAGONHYDRA | ABH → read-only versioned discovery | Identity checks and capability endpoint; unavailable services remain unverified |
| ARX | Optional scanner → ABH | Real pinned ARX 4.0.1b1 deep scan through isolated Python; import/export also supported |
| MCP | Both directions | Real official SDK interoperability; client actions explicitly selected |
| Generic local | ABH → configured loopback HTTP | Explicit chat request with a manually entered model ID; remote model listing is unsupported; no guarantee of every server dialect |

Cloud is disabled by default. Configure each provider's separate credential in the portable encrypted vault, model ID, token/call limits and positive session/per-request spending reservations. Review the exact content before every request. Reservations are retained after failure/cancellation and bound the number of approved requests; they are not an invoice or guaranteed provider billing cap. Configure provider-side limits for hard spending limits. Provider usage and native errors are retained. No automatic retries occur.

The advanced provider editor accepts native `messages`, `tools`, `tool_choice` and either `response_format` (OpenAI/Grok) or `system`/`output_config` (Claude). Model/stream/token limits are set by the adapter. Hosted tools, media and arbitrary request options are rejected. Tool IDs, JSON arguments and results remain in the native payload, including SSE fragments. A proposed tool is not executed by ABH. Cancellation closes the request; providers may bill already consumed work.

The real Codex test returned a Python addition function and then queried live ABH runtime state through MCP. Its final PID, port and model ID matched the tool result. It called the runtime tool three times despite an exactly-once instruction: protocol compatibility is verified, exact tool-count instruction following is partial. Custom/freeform/hosted tools remain explicitly unsupported. Normal Codex configuration and authentication are not imported or changed.

ARX is optional. In Settings select an existing Python executable and a verified ARX source directory. Run optional ARX deep scan from Hardware. Python runs with `-I`, imports only the selected source explicitly, and writes its report inside ABH data. The native launcher remains Python-free. Machine evidence is not itself an inference compatibility verdict.

The bounded coordinator supports local, ARX evidence and DRAGONHYDRA discovery in sequential, comparison and review modes, with finite steps, tokens, time and concurrency. Cloud budget and automatic tool budget are zero. Codex/cloud engineering handoffs are explicit connector actions; autonomous cross-provider recursion is not implemented. New adapters can implement the internal provider/engine interfaces without coupling failure to local inference.
