# Test results

Authoritative machine-readable evidence is in `reports/`. Development and final-package tests are distinguished by executable/build fingerprints. Compilation alone never marks a package verified.

Real Qwen and SmolLM2 inference has passed through the generic ABH engine, authenticated APIs, streaming browser chat and the native selector. Original weight identities remained unchanged. Qwen CPU/CUDA calibration passed; the measured CPU profile was faster than the conservative GPU configuration while a separate application occupied most VRAM. SmolLM2 produced genuine text but failed the strict arithmetic sanity check. Quality and compatibility are reported separately.

The official TypeScript MCP SDK 1.32.2 communicated over STDIO and Streamable HTTP. Codex App Server 0.169.0 returned a local coding proposal and completed a real MCP runtime query round trip. Exact once-only tool following was partial: three calls were observed. Provider protocol tests use mocks only. The optional pinned ARX 4.0.1b1 CLI produced a real deep machine scan.

Synthetic tests cover registry corruption, identity change/reconnection, cancellation, scoped authentication/rotation/revocation, encrypted vault behavior, namespace preservation, unsupported tools, origin rejection, MCP validation, bounded workflow execution and provider protocol/error/budget behavior. Real-model recovery tests separately exercise invalid candidates, rollback, crash detection, restart and clean stop.

Packaging, installation, relocation and final verification outcomes are recorded in their dedicated reports. No USB hardware was available during initial discovery; fixed-disk relocation must not be called a USB test. Live DRAGONHYDRA operations and paid-provider connections are not inferred from mocks or schema inspection. See the final mission report for remaining partial/unsupported capabilities.
