# Codex bridge report

VERIFIED: installed Codex App Server 0.169.0 initialized over STDIO in an isolated ABH profile. The local Qwen model returned a Python addition function. It executed 3 real abh_runtime_status MCP calls with preserved call IDs and arguments; 3 results matched the live PID, port and model ID. The final answer reproduced those values. The owned model shut down cleanly.

PARTIAL: the prompt requested exactly one tool call but three occurred. General coding/custom-tool compatibility is not established. Custom/freeform and hosted tools are rejected explicitly. The normal Codex configuration was not modified. See ABH-CODEX-TOOL-EVIDENCE.json.
