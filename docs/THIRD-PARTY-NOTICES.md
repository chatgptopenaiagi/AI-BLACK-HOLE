# Third-party components

ABH source and reused owner-authored QWEN SATELLITE components are MIT licensed. Provenance identifies the completed snapshot used. No original project files were overwritten.

The bundled native runtime retains its llama.cpp, GGML, cpp-httplib and vendor license notices under `runtime/cpu/`. Ollama's MIT notice is included here. The self-contained .NET runtime has its own MIT license and third-party notices. Microsoft Visual C++ runtime files and NVIDIA CUDA/cuBLAS libraries retain their respective distribution terms; they are not relicensed by ABH's source license. NVIDIA libraries are supplied solely as runtime components of ABH's inference functionality, not as a standalone SDK. No NVIDIA endorsement is implied.

ARX source is optional, MIT licensed, and not a mandatory bundled runtime. Its notice is included for report-format/source integration provenance. The MCP SDK is a development-only dependency under its upstream license; it is not embedded in the native launcher. Inno Setup builds the installer and retains its own license. Model weights are excluded from the core and remain subject to their model repository licenses.

Upstream sources: https://github.com/ggml-org/llama.cpp · https://github.com/ollama/ollama · https://github.com/dotnet/runtime · https://docs.nvidia.com/cuda/eula/ · https://github.com/chatgptopenaiagi/ARX · https://github.com/modelcontextprotocol/typescript-sdk · https://jrsoftware.org/isinfo.php
