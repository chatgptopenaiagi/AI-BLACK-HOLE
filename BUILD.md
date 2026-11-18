# Build and reproduce

Use PowerShell 7, Windows x64 and .NET SDK 8.0.427 (pinned in `global.json`). Restore uses the checked-in lock file and .NET runtime 8.0.34. Node is required only for browser syntax checks and the optional official MCP SDK tests.

```powershell
./scripts/Build.ps1 -OutputDirectory ./dist/core
```

The build publishes a self-contained executable with native sidecars and copies offline web/help files. A source-only build can run synthetic/API tests without model weights or a bundled engine. For inference, place a verified runtime under `runtime/cpu/` and its matching CUDA libraries under `runtime/cuda/`, or select an existing compatible llama-server.exe in the app. Preserve upstream notices. Engine fingerprints include all loaded runtime DLL candidates.

The release uses the preserved, independently tested installed llama.cpp distribution, version `0.9.0-dev`, build 1, commit `6c01da627`. Its complete file manifest accompanies the package. Rebuilding ABH does not automatically reproduce third-party binaries; supply the same verified runtime manifest to reproduce that combination.

For a WinExe, use `Start-Process -Wait -PassThru -WindowStyle Hidden` in PowerShell and inspect ExitCode; direct invocation may return before completion. Relevant commands:

```text
--self-test --state <isolated-dir> --output <report.json>
--inspect <GGUF> --output <report.json>
--register <GGUF> --state <isolated-dir> --output <model.json>
--acceptance --model-file <GGUF> --calibrate --browser --state <isolated-dir> --output <report.json>
--recovery-test <small-GGUF> --state <isolated-dir> --output <report.json>
--protocol-test <absolute-tests/mcp-client.mjs> --state <isolated-dir> --output <report.json>
--ui-test --state <prepared-two-model-registry> --output <report.json>
--arx-scan --state <configured-optional-ARX-state> --output <report.json>
```

`--acceptance` accepts repeated `--model-file` arguments. `--codex` opts into an isolated installed App Server test. Model and connector acceptance can consume minutes; do not launch duplicate tests merely because PowerShell returned early. Tests never use paid cloud credentials.

Build setup with Inno Setup 7 and `scripts/Installer.iss`, defining BuildDir as the verified clean portable core and ReleaseDir as a new release directory. Test setup and uninstall in a dedicated folder. ZIP packaging and SHA-256 manifests are separate from compilation. GitHub Actions builds and runs synthetic/MCP tests; it does not certify a workstation GPU or real-model inference.
