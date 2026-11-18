# MCP

ABH provides STDIO and authenticated loopback Streamable HTTP. HTTP uses POST `/mcp`; GET and DELETE are refused. Versions `2026-07-28` and the implemented older negotiated versions are checked explicitly. Current protocol requests carry per-request metadata and matching `MCP-Protocol-Version`, `Mcp-Method`, and tool-call `Mcp-Name` headers. The official TypeScript SDK 1.32.2 was tested using its negotiated `2025-11-25` protocol over both transports. Separate tests exercise the current protocol.

For STDIO launch `AI-BLACK-HOLE.exe --mcp` with `ABH_MCP_URL` set to the running ABH `/mcp` URL and `ABH_MCP_KEY` supplied in the process environment. Do not put secrets in command arguments. The local provider's isolated Codex configuration supplies a read-only MCP key and exposes only runtime status.

| Tool | Required arguments | Effect |
|---|---|---|
| abh_list_models | none | Registered models and capability evidence |
| abh_model_info | modelId | Information for that registered ID |
| abh_hardware_info | none | Native machine evidence |
| abh_calibration_status | modelId | Existing calibration evidence |
| abh_start_model | modelId | Start a registered model with SAFE |
| abh_stop_model | none | Stop the ABH-owned model |
| abh_generate | text | Local text inference, bounded to 256 output tokens |
| abh_analyze_text | text | Local text analysis, bounded to 256 output tokens |
| abh_runtime_status | none | Runtime state |
| abh_cancel_task | taskId | Cancel an ABH task |
| abh_connection_status | none | Connector capability manifests |

`tools/list` is the authoritative JSON schema for the installed build. Unknown arguments and arbitrary paths are rejected. `mcp.read` permits evidence/status tools; full `mcp` is required for mutations or generation. A failed tool is an error, not successful text pretending to be a tool result.

The MCP client accepts a user-selected loopback HTTP endpoint and explicit discovery/list/call actions. It does not execute arbitrary server commands or recursively call tools. Remote endpoints and external STDIO client launch are not implemented.
