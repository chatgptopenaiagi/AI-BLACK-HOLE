# MISSION 007 — AI BLACK HOLE 2.0.0-beta.2

**Universal two-model execution: VERIFIED. Windows beta artifacts: VERIFIED. Expanded mission coverage: PARTIAL.** Publication is confirmed separately by `reports/ABH-GITHUB-PUBLICATION.json` after the release upload.

ABH selected Qwen27B and SmolLM2 through the same native model selector, used the same model-independent llama.cpp adapter, produced genuine local text and browser responses, and shut its engine down cleanly. The models use different GGUF architectures (`qwen35` and `llama`). Their original files remained at their selected locations with matching SHA-256 fingerprints. This proves the demonstrated multi-model capability; it does not establish compatibility with every GGUF or correctness of every answer.

| Model | Bytes | SHA-256 |
|---|---:|---|
| Qwen27B | 16,810,714,464 | f5f1dd8920d417aac2718b0bda3403da274301efdd6760b4f0f4b864ff2ad57d |
| SmolLM2-135M-Instruct-Q8_0.gguf | 144,811,360 | 5a1395716f7913741cc51d98581b9b1228d80987a9f7d3664106742eb06bba83 |

The only downloaded weights were the explicitly selected SmolLM2 Q8_0 file from the pinned Hugging Face revision recorded in MODEL-COMPATIBILITY.md. The small model's strict arithmetic test failed, including incorrect browser answers. Its real generation and transport compatibility passed. These outcomes are deliberately separate.

## Executed acceptance

- 59 synthetic/security/provider protocol checks passed on the final application build.
- Real CPU/CUDA calibration passed for both model architectures. Qwen's measured CPU configuration was faster than its conservative CUDA candidate under the observed shared-GPU conditions. FASTEST TESTED is bounded to measured candidates.
- Actual native controls selected, calibrated, started, switched and stopped models. Native Help/About opened; all twelve browser pages navigated; local conversations persisted.
- Both official MCP SDK transports passed. Real Codex App Server 0.169.0 returned a coding proposal and invoked ABH's runtime tool with exact result identity. It made three calls despite an exactly-once instruction, so that instruction-following result is PARTIAL.
- Real recovery tests rejected an invalid candidate, restored a verified profile, detected an intentionally terminated owned engine, restarted inference, cancelled calibration and ended with clean shutdown.
- Optional ARX 4.0.1b1 performed a real isolated CLI deep scan. The native core did not require Python.
- The self-contained application ran after relocation to another physical fixed disk in a path with spaces, with Python/.NET excluded from its child PATH and a nonexistent DOTNET_ROOT. Host installations were not uninstalled.
- The ZIP passed integrity testing, extraction and SHA-256 comparison of all 110 files, then actual inference from the extracted directory.
- The installer installed into a selected independent directory, created its shortcut, ran inference/browser chat and uninstalled. External model identity, registry and a user-data sentinel survived uninstallation.
- A separate optional package copied only SmolLM2, verified both source and copy, retained a relative model reference and ran after moving that copied package to a Unicode path. It is not distributed as a release asset.

## Component coverage

| Component | Status | Scope or limitation |
|---|---|---|
| Product identity and preserved predecessor | VERIFIED | Independent ABH source; QWEN repository clean, original release ZIP and model fingerprints unchanged |
| Native machine discovery | VERIFIED | Windows/CPU/RAM/GPU/driver/storage/runtime observations; no NPU claim |
| External model registry and identity | VERIFIED | Read-only registration, bounded scanning, changed/missing detection, exact manual reconnection, removal without deletion |
| Automatic drive-letter search/reconnection | PARTIAL | Recorded volume identity and explicit exact-file reconnection; broader automatic selected-volume matching remains future work |
| GGUF inspection | VERIFIED | Bounded v2/v3 metadata/tensor descriptors; model identity is distinct from inference |
| Split/projector execution | UNSUPPORTED | Related structures recognized; complete shard and multimodal execution not verified |
| Inference engine manager | VERIFIED | Exact installed/bundled llama.cpp CPU/CUDA combination; other managed backend implementations remain future work |
| Bounded automatic calibration and rollback | VERIFIED | Actual measurements, profile fingerprints, cancellation and recovery |
| Hugging Face explorer | PARTIAL | Exact revision/file download and verification passed; interrupted resume and complete split sets not acceptance-tested |
| Local runtime and model switching | VERIFIED | One active model per application instance; resource-aware multi-session scheduling remains future work |
| Native and browser workspaces | VERIFIED | Major controls and offline Help/About/chat; additional DPI/accessibility/dialog coverage remains NOT TESTED |
| Authenticated API gateway | VERIFIED | Scoped keys, streaming and tested Responses subset; complete OpenAI protocol equivalence is not claimed |
| Independent connector architecture | VERIFIED | Separate provider adapters, Codex, MCP and machine/data bridges |
| Codex integration | PARTIAL | Real local App Server/provider/tool path; general shell/custom tools and independent account-backed fallback remain unsupported |
| MCP | VERIFIED | Real official client over STDIO/HTTP; current-version native tests; remote endpoints and external STDIO client launch unsupported |
| OpenAI / Claude / Grok | PARTIAL | Full supported native protocol mocks passed; live paid access NOT TESTED |
| DRAGONHYDRA | BLOCKED | Inspected read-only contracts; configured live services unavailable during bounded probes |
| ARX | VERIFIED | Real optional pinned scanner plus report exchange; not a model-compatibility verdict |
| Agent coordinator | PARTIAL | Finite local/evidence workflows; Codex/cloud handoffs remain explicit separate actions |
| Capability matrix | PARTIAL | Text/chat evidence tested per model; embedding/vision/audio and broad tool quality remain unknown or unsupported |
| Portable core and optional model copy | VERIFIED | Real fixed-disk relocation, Unicode copied-package test, external original models preserved |
| Actual removable USB media | NOT TESTED | No removable media available; fixed-disk relocation is not a USB test |
| Windows installer | VERIFIED | Install, shortcut, actual inference and preserving uninstall |
| Storage, credentials and history | VERIFIED | Atomic versioned documents, exclusive state lease, encrypted vault and persisted conversations |
| Source research and provenance | VERIFIED | Pinned completed snapshot, official upstream research and runtime file hashes |
| Public GitHub source and prerelease | NOT TESTED | Replaced with publication receipt after authenticated upload and readback |

## Artifact traceability

The portable root's `build-info.json` records the exact compiled source commit, clean-tree status and executable SHA-256. The external release manifest identifies both packaged assets and their hashes. Later documentation commits may add verification receipts without changing the compiled application. Earlier acceptance reports retain their own build identities; final-package evidence is identified separately.

Release assets contain application/runtime files, documentation and sanitized evidence. They exclude weights, private paths, vaults, keys, conversations and unrelated workstation logs. The optional large-model workflow is separate. Both primary assets are below GitHub's individual 2 GiB limit.

Original model hashes, original QWEN release ZIP hash, clean predecessor source state, preserved Codex configuration timestamp and installed ARX executable identity are recorded in ABH-PRESERVATION-TESTS.json. Full private execution logs remain local. No claim is made that unavailable providers, USB media, unsupported architectures or every planned feature passed.
