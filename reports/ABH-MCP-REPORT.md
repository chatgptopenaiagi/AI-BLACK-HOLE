# MCP report

VERIFIED: official TypeScript SDK 1.32.2 initialized and invoked real tools over STDIO and Streamable HTTP using negotiated protocol 2025-11-25. Unknown model IDs were rejected. Separate native tests exercise 2026-07-28 metadata, discovery and tool responses. ABH exposes 11 bounded registered-model/status tools.

The actual Codex App Server used the STDIO adapter and received live runtime results. Read-only keys cannot invoke model mutations. External remote HTTP endpoints, arbitrary filesystem access and shell execution are unsupported. See the SDK and security JSON reports for exact checks.
