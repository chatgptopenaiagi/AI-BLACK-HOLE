# Source research and reuse

Reviewed 2026-11-18. Installed evidence and immutable source snapshots take priority over assumptions about latest versions.

| Source | Finding and engineering use |
|---|---|
| [llama.cpp](https://github.com/ggml-org/llama.cpp) and [GGUF specification](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md) | Native server/reference format; bounded independent GGUF reader; compatibility decided by actual load |
| [Ollama](https://github.com/ollama/ollama) | Existing verified native runtime reused in a separate ABH directory; original installation untouched |
| [KoboldCpp](https://github.com/LostRuins/koboldcpp) | Alternative runtime investigated; no claim of a managed adapter or equivalent APIs |
| [SourceForge mirror](https://sourceforge.net/projects/llama-cpp.mirror/) | Inspected as an upstream mirror; no unrelated codebase merged |
| [Hugging Face downloads](https://huggingface.co/docs/huggingface_hub/en/guides/download) | Exact file/revision selection, selective transfer, resumable partial file and final integrity verification |
| [Codex App Server](https://learn.chatgpt.com/docs/app-server) and [source](https://github.com/openai/codex) | Installed 0.169.0 schema generated locally; STDIO initialize/threads/turns/events/approvals and cancellation implemented |
| [MCP specification](https://modelcontextprotocol.io/specification/2026-07-28/basic) | Current per-request metadata/discovery and POST transport; older SDK negotiation retained and independently tested |
| [OpenAI Chat Completions](https://platform.openai.com/docs/api-reference/chat/create) | Native request/token/tool/SSE formats; provider mock tests, no paid request |
| [Anthropic API migration](https://platform.claude.com/docs/en/api/migration-2026-10-21) and [structured outputs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs) | Current version header, no removed sampling parameters, native blocks/SSE/output_config |
| [xAI Chat Completions](https://docs.x.ai/developers/rest-api-reference/inference/chat-completions) | Separate documented adapter; API is a supported predecessor of Responses, not a claim of identical provider behavior |
| [ARX pinned source](https://github.com/chatgptopenaiagi/ARX/tree/bca7232bce200bc22f9cd8fee34fe17fc6a0b604) | Actual optional isolated `arx.cli` deep scan; schema_version 0.1 envelope accepted |

QWEN SATELLITE components were taken from completed commit `1b074bf82a6d1a4700e06841f5ce46b90c17d9cf`, never edited in place. Component SHA-256/provenance is recorded separately. The new registry, GGUF inspection, calibration binding, model session selection, native controls, browser workspace, engine abstraction and connector architecture are ABH-specific development, not a product-name substitution.

The primary runtime reports `0.9.0-dev`, build 1, commit `6c01da627`; complete runtime DLL hashes identify the tested binary combination. ARX is optional and is not bundled as a mandatory Python environment. DRAGONHYDRA versioned contracts were inspected read-only; no source/database changes were required.
