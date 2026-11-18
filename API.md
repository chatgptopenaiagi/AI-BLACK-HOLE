# Local API

ABH binds only `127.0.0.1` on a dynamically selected port. API Connections displays the actual base URL. Requests require `Authorization: Bearer <scoped-key>`. Keys can be generated, rotated and revoked; stored records contain hashes. Keys have request-rate and total-request limits. Browser administration uses a separate session token obtained from a one-use launch invitation.

| Route | Scope | Behavior |
|---|---|---|
| GET /v1/models | models | Registered model IDs and capabilities; identifies the loaded model |
| POST /v1/chat/completions | chat | Active model only; JSON or SSE |
| POST /v1/responses | responses | Supported local Responses subset, including function namespaces |
| POST /v1/embeddings | chat | Embedding model only; real embedding acceptance remains untested |
| GET /v1/abh/models | models | Inspection and capability evidence |
| GET /v1/abh/runtime | runtime | Process, port, selected profile and memory |
| GET /v1/abh/calibration/{id} | calibration | Saved measurements |
| POST /v1/abh/tasks | tasks | Bounded local/evidence workflow |
| GET /v1/abh/tasks/{id} | tasks | Task lifecycle |
| POST /v1/abh/tasks/{id}/cancel | tasks | Cancellation |
| POST /mcp | mcp.read or mcp | MCP protocol tools; see MCP.md |

Local requests must name the active registered ID. A busy inference slot returns 429. Missing models return 503. Unsupported options and invalid arguments return structured errors. Requests are bounded by body size, output tokens and time. Aborting the request propagates cancellation.

Responses compatibility is deliberately limited: function namespaces are mapped and restored with exact identifiers and arguments; custom/freeform and hosted tools are rejected, never silently discarded. Stateful `previous_response_id`, conversation handles and background mode are unsupported. Existing paths do not imply complete OpenAI API compatibility.

The internal `/api/*` browser routes are not available to ordinary scoped keys. They support model management, jobs, conversations, settings, explicit provider requests and connector controls. Provider native request schemas are documented in AGENT-CONNECTORS.md. No remote binding switch is provided in this beta.
