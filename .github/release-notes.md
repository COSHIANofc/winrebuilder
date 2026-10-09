WinRebuilder v.0.3.c-beta is a GUI-only Windows 11 release with a redesigned native WPF interface, configuration editing, ExplorerPatcher settings controls, and registry backup restoration.

Download `WinRebuilder.zip` for the standard distribution or `WinRebuilder-portable.exe` to keep configuration beside a single executable. Both distributions use the same application bytes. Review `config.yml` before installing software.

- Edit `config.yml` through the GUI; add or remove winget software, install selected or all configured packages, and view results.
- Install ExplorerPatcher through a pinned, source-verified winget manifest; optionally validate and apply its allowlisted `.reg` settings.
- Back up and verify registry changes, with one-value rollback in the Configuration view.
- Use the self-contained Windows x64 ZIP or portable executable; both open the GUI directly.

Review the default 7-Zip, CrystalDiskInfo, and ExplorerPatcher configuration before applying. Real package installation and registry effects still require testing on a disposable Windows 11 machine.
