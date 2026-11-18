# Portability

The portable edition resolves resources from its running executable. Keep the complete extracted directory together. Writable settings live in `data/` and `vault/`; `--state <directory>` selects another writable location. The installed edition uses current-user local application data, never a protected installation directory. Only one ABH instance may write a given data directory.

External model registration never copies weights. Volume identity and a relative volume path are recorded where available. Missing media produces MODEL UNAVAILABLE. Reconnect verifies the registered SHA-256; matching filenames are insufficient. Directory junctions are not followed during bounded scans. Network shares require explicit opt-in.

Relocation acceptance checks another directory, spaces in paths, a different physical fixed disk, an external model on another drive, and self-contained execution with a PATH excluding Python/.NET. This is not proof of operation on physical removable USB media. See `reports/ABH-PORTABILITY-TESTS.json` for actual results and untested media.

`scripts/Package-Model.ps1 -CoreDirectory <clean-core> -ModelFile <selected-GGUF> -Destination <new-folder>` explicitly copies one selected model, verifies source and destination SHA-256, and registers the application-relative copy. It refuses an existing destination or a core containing user data. Run acceptance after copying and after relocation. No model-inclusive archive is part of the core GitHub release; weights have separate licenses and storage costs.

The installer offers a destination folder, shortcuts and uninstall. It refuses to overwrite a nonempty installation folder. Uninstallation removes installed application files and preserves application data and externally registered model files. The beta executable is unsigned; signing is planned. Installation and relocation results must be read separately from successful compilation.
