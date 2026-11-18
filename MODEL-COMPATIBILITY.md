# Model compatibility

| Actual model | Architecture | Size | Quantization | Evidence |
|---|---|---:|---|---|
| Qwen27B, metadata name Qwen3.8 27B 0814 | qwen35 | 16,810,714,464 bytes | Q4_K_M | Local CPU/CUDA inference, calibration, API, browser, native switching, Codex/MCP round trip |
| SmolLM2-135M-Instruct-Q8_0.gguf | llama | 144,811,360 bytes | Q8_0 | Local CPU/CUDA inference, calibration, API, browser and native switching |

Qwen SHA-256: `f5f1dd8920d417aac2718b0bda3403da274301efdd6760b4f0f4b864ff2ad57d`.

SmolLM2 SHA-256: `5a1395716f7913741cc51d98581b9b1228d80987a9f7d3664106742eb06bba83`. Source: [bartowski/SmolLM2-135M-Instruct-GGUF](https://huggingface.co/bartowski/SmolLM2-135M-Instruct-GGUF), pinned revision `09816acd5d99df7be770d85ea30822623dab342c`. Only the selected Q8_0 file was downloaded.

Other Llama, Qwen, Mistral, Gemma, Phi and DeepSeek-derived GGUFs require individual engine load and capability tests. They are **NOT TESTED** by this release. Architecture support is ultimately decided by the selected runtime. An inspected header is not successful inference.

GGUF v2/v3 metadata includes architecture, quantization, tensor count, layer/context/tokenizer details and chat-template declarations when present. Capability evidence distinguishes DECLARED, DETECTED, TESTED, UNSUPPORTED and UNKNOWN. Embedding metadata selects an embedding route rather than forcing chat, but no real embedding model was available for acceptance. Projectors and split files are recognized and explicitly refused when the verified execution path is unavailable. Vision, audio and multimodal inference remain unsupported in this beta.

Registry states include DISCOVERED/INSPECTED, COMPATIBILITY UNKNOWN, COMPATIBLE, TESTED, CALIBRATED, READY, RUNNING, UNSUPPORTED, ERROR and MODEL UNAVAILABLE. Runtime state and measured capabilities remain separate from identification.
