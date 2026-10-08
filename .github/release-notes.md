WinRebuilder v.0.3.b-beta adds a native WPF interface and a `wrb` terminal alias while keeping WinRebuilder-branded executables.

- Edit `config.yml` through the GUI; add or remove winget software, install selected or all configured packages, and view results.
- Install ExplorerPatcher through a pinned, source-verified winget manifest; optionally validate and apply its allowlisted `.reg` settings.
- Back up and verify registry changes, with one-value rollback via `wrb rollback`.
- Download both self-contained Windows x64 executables, SHA-256 files, `wrb.cmd`, and `config.example.yml` into one folder.

Review the default 7-Zip, CrystalDiskInfo, and ExplorerPatcher configuration before applying. Windows runtime behavior still requires testing on a disposable Windows 11 machine.
