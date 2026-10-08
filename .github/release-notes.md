WinRebuilder v.0.2.a-beta is an early beta build. Windows-specific behavior is still being validated.

- Manage applications and settings through `config.yml` using winget, GitHub releases, or pinned HTTPS URLs.
- Restore a supported, allowlisted subset of ExplorerPatcher `.reg` settings after the shell-phase package.
- Back up and verify registry changes, then restore a selected value with `winrebuilder rollback`.
- Download a self-contained, single-file `winrebuilder.exe` for Windows x64.

Review `config.example.yml` and the README before applying changes. ExplorerPatcher key deletions and virtualized settings are not supported by this importer.
