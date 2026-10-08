# WinRebuilder

Current public version: **v.0.3.b-beta**. Internal Version is `0.3.1`; AssemblyVersion and FileVersion are `0.3.1.0`. The unfinished `v.0.3.a-beta` was superseded before release and was never published.

WinRebuilder reconstructs a Windows 11 environment from [config.yml](config.yml). The CLI, WPF GUI, planner, and executor share the same Core model. There is no separate software database. Windows runtime behavior still needs testing on a disposable Windows 11 machine.

## Install and run

Download `WinRebuilder.exe`, `WinRebuilder.Cli.exe`, `wrb.cmd`, and `config.example.yml` from the [prerelease](https://github.com/COSHIANofc/winrebuilder/releases) into one user-writable folder. Verify the executable checksums, then copy `config.example.yml` to `config.yml` in that folder and review it. The binaries are self-contained single-file `win-x64` builds with trimming disabled. They do not require an installed .NET runtime.

`wrb` is the terminal alias, supplied as a small `wrb.cmd` beside `WinRebuilder.Cli.exe`. The shim quotes the CLI path and forwards arguments. It does not install a service or modify PATH. Run it from its folder, or add that folder to your **user** PATH yourself, then open a new terminal. To remove the alias, delete `wrb.cmd` and remove that folder from user PATH if you added it. No `wrb.exe` is published.

```text
wrb --help
wrb --version
wrb validate config.yml
wrb plan config.yml
wrb apply config.yml --dry-run
wrb apply config.yml
wrb backups
wrb rollback <backup-id> --dry-run
wrb rollback <backup-id>
wrb ui
```

`wrb ui` opens `WinRebuilder.exe` from the same folder. The GUI reads and edits that folder's `config.yml`. `--help` and `-h` exit successfully and show commands. Invalid commands return a nonzero exit code and direct users to `wrb --help`. On macOS, `validate` and `plan` work, and `apply --dry-run` gives a plan-only preview without checking Windows state. Applying changes and the GUI require Windows.

## GUI

The native .NET 10 WPF GUI shows configured software by name, provider, package ID, and status. Add accepts a display name and exact winget package ID; Core rejects malformed or duplicate entries before saving. Remove deletes the selected entry from `config.yml` and **does not uninstall** software. Reload rereads the file; a failed reload leaves the last valid in-memory configuration available.

Install Selected and Install All run asynchronously through the Core planner and Windows executor. Install All runs normal packages before ExplorerPatcher in the final shell phase. Status and bounded log output show results. Cancel requests cancellation of an active operation. ExplorerPatcher controls enable or disable its package, select a `.reg` file with a native picker, validate and copy it into `settings/`, and apply its supported settings. The GUI has no direct winget, YAML, or registry implementation. It uses simple WPF controls, no polling, no local server, and no extra UI framework.

Selecting ExplorerPatcher also checks or installs normal configured packages first. Applying its settings follows the same package order before any registry change.

## Configuration

The normal default software set is exactly **7-Zip**, **CrystalDiskInfo**, and **ExplorerPatcher**. The first two are ordinary winget packages; ExplorerPatcher is represented once under `explorerPatcher` and is synthesized as one shell-phase operation. Current verified IDs are `7zip.7zip`, `CrystalDewWorld.CrystalDiskInfo`, and `valinet.ExplorerPatcher`. Their identities come from the [7-Zip manifest](https://github.com/microsoft/winget-pkgs/tree/master/manifests/7/7zip/7zip), [CrystalDiskInfo manifest](https://github.com/microsoft/winget-pkgs/tree/master/manifests/c/CrystalDewWorld/CrystalDiskInfo), and [ExplorerPatcher manifest](https://github.com/microsoft/winget-pkgs/tree/master/manifests/v/valinet/ExplorerPatcher/26100.8457.70.3) in Microsoft's winget-pkgs repository.

[config.example.yml](config.example.yml) contains those three products and no registry changes. ExplorerPatcher settings restoration is optional, so the example validates without a `.reg` file. Edit `config.yml` directly or use the GUI; no C# changes are needed to add or remove software. Pass an explicit path to CLI commands. Profiles are limited to 64 KiB, version `1`, and known fields. Duplicate keys, unknown fields, YAML anchors, aliases, tags, merge keys, duplicate package names, and duplicate winget IDs are rejected. No command or script profile field exists. Normal package operations precede normal registry operations; shell package operations and ExplorerPatcher settings follow.

Supported package providers are `winget` with an exact `id`, `github` with an exact release asset and installer metadata, and direct HTTPS `url` with SHA-256. Winget IDs use Core validation and separate process arguments. Direct downloads require SHA-256 and move temporary files into place only after validation. Downloaded installers use fixed switches; arbitrary arguments are not accepted.

`ConfigurationWorkspace` validates before saving, writes and flushes a temporary file, validates it again, and atomically replaces `config.yml`. At most one `config.yml.bak` is retained. A failed validation leaves the original file unchanged. Concurrent edits on disk are detected before replacement. The GUI never edits YAML itself.

## ExplorerPatcher source chain

ExplorerPatcher is a high-risk shell modification. Its [upstream repository](https://github.com/valinet/ExplorerPatcher) identifies its own GitHub Releases as the official distribution channel. WinRebuilder invokes winget, pinned to package `valinet.ExplorerPatcher`, version `26100.8457.70.3`, and the `winget` source. Microsoft's [installer manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/v/valinet/ExplorerPatcher/26100.8457.70.3/valinet.ExplorerPatcher.installer.yaml) points to `https://github.com/valinet/ExplorerPatcher/releases/download/26100.8457.70.3/ep_setup.exe` with SHA-256 `8146DB4D3A87201FB80AD1D3712BA8F56883E9A0811758BD39F62D73A9F2C586`. Its [metadata](https://github.com/microsoft/winget-pkgs/blob/master/manifests/v/valinet/ExplorerPatcher/26100.8457.70.3/valinet.ExplorerPatcher.locale.en-US.yaml) names ExplorerPatcher and VALINET Solutions SRL. Before installation, WinRebuilder asks winget to show that exact package and refuses installation unless the returned ID, URL, and hash match. It does not fall back to a direct download. A future upstream release requires explicit review and a new pin.

WinRebuilder also verifies that the local `winget` source exports Microsoft's `https://cdn.winget.microsoft.com/cache` endpoint and expected source identity before asking winget to show the pinned manifest.

## ExplorerPatcher settings and registry recovery

The GUI's native picker accepts `.reg` files. Core reads the complete file as strict UTF-8 (with or without BOM) or UTF-16 LE with BOM, validates every statement and ExplorerPatcher-specific allowlist entry, then copies valid bytes to a safe relative path under `settings/`. The original file is unchanged. Absolute paths, traversal, UNC paths, device paths, symbolic-link traversal, malformed encodings, unsupported syntax, HKLM, Run/RunOnce, recursive key deletion, and update source overrides are rejected. There is no generic `.reg` import and no `regedit`, `reg.exe`, or shell invocation for registry import. The normal `registry:` Policies-only allowlist remains separate.

Before any ExplorerPatcher registry mutation, Core reads all target values, creates typed backups for changes, persists and reloads every backup, verifies the original values have not changed, and only then starts writing. Every changed value is reread and verified before completion. A later runtime failure may leave earlier verified values changed; each has its own backup for rollback. A File Explorer restart or sign-out may be needed for visual effects. The importer supports only the [official allowlisted settings subset](https://github.com/valinet/ExplorerPatcher/blob/master/ep_gui/resources/settings.reg), not every setting in an upstream export.

Normal registry entries are restricted to `HKLM\SOFTWARE\Policies` or `HKCU\Software\Policies`, with DWORD or string values. Backups are saved under `%ProgramData%\WinRebuilder\backups`, written via temporary files, reloaded, and validated before writes. `wrb backups` lists backup IDs; `wrb rollback <backup-id>` restores one value and verifies it, without recursively removing keys. Rollback refuses to overwrite unrelated later edits. HKLM writes and rollback require elevation. Backups are validated for structure, but are not cryptographically authenticated against deliberate local modification.

Dry run reads state and may run winget's read-only queries, but WinRebuilder does not install packages, download artifacts, write registry values, backups, logs, or execution state. Winget itself may refresh its metadata cache during queries. The Windows process runner bounds captured output and supports cancellation. Installation stops at the first failure. Windows CI uses fakes and does not install software; actual winget behavior, WPF interaction, installer switches, ExplorerPatcher effects, and second-run idempotency still need a disposable Windows 11 test machine.

## Development and release

```sh
dotnet restore WinRebuilder.slnx
dotnet build WinRebuilder.slnx
dotnet test WinRebuilder.slnx
dotnet run --project src/WinRebuilder.Cli -- --version
dotnet run --project src/WinRebuilder.Cli -- validate config.example.yml
dotnet run --project src/WinRebuilder.Cli -- plan config.example.yml
dotnet run --project src/WinRebuilder.Cli -- apply config.example.yml --dry-run
```

The release workflow validates the tag against the exact public version, builds and tests on Windows, verifies both published executables are nonempty, writes SHA-256 files, and publishes a prerelease with `WinRebuilder.exe`, `WinRebuilder.Cli.exe`, their checksums, `wrb.cmd`, and `config.example.yml`. It uses the GitHub-provided token with job-scoped write permission. No long-lived token, NativeAOT, trimming, or heavyweight UI dependency is used.
