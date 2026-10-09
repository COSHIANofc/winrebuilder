# WinRebuilder

Current public version: **v.0.3.c-beta**. Internal Version, AssemblyVersion, and FileVersion: `0.3.1.1`.

WinRebuilder is a native Windows 11 graphical application for rebuilding a reviewed software and settings configuration. [config.yml](config.yml) is the canonical profile; the GUI, planner, and executor share the same Core model.

## Requirements

- Windows 11 x64. Release executables are self-contained and need no separately installed .NET runtime.
- A user-writable folder for the executable and `config.yml`.
- Winget for winget packages; administrator permission when a selected registry operation targets HKLM.

## Installation

### Standard

1. Open the [latest GitHub Release](https://github.com/COSHIANofc/winrebuilder/releases).
2. Download `WinRebuilder.zip` and extract it into a user-writable folder.
3. Review `config.yml`, then run `WinRebuilder.exe` directly.
4. If Windows displays a security prompt, confirm the file came from the official GitHub release before continuing.

The ZIP contains exactly `README.md`, `WinRebuilder.exe`, `config.yml`, and `config.example.yml`, with no wrapper folder. `config.yml` is edited beside the executable. `config.example.yml` is an example; it is not loaded automatically.

### Portable

1. Download `WinRebuilder-portable.exe` from the same release.
2. Place it in a user-writable folder and run it directly.

The portable EXE is a byte-for-byte copy of the final published `WinRebuilder.exe`. On first launch, it creates `config.yml` beside itself from an embedded default profile if the file is absent. Later edits stay in that folder. If the folder cannot be written, the GUI reports the error; it does not silently redirect configuration to a system directory. `config.example.yml` is optional for portable use.

## Usage

Launch `WinRebuilder.exe`. The sidebar has Software, ExplorerPatcher, Configuration, and About views. Software displays configured package identity and operation status. **Check status** queries installed state on demand without applying changes. Add accepts an exact winget ID. Remove deletes an entry from `config.yml`; it does **not** uninstall the application from Windows. Install selected and Install all use the same Core planner and Windows executor, with ExplorerPatcher in the final shell phase. Activity shows status and a bounded log, and Cancel requests cancellation.

The Configuration view reloads the profile and lists registry backups on demand. Select a backup to restore its previous value. Rollback refuses to overwrite a later unrelated change. The UI does not enumerate winget, contact the network, or scan the registry at startup.

## Configuration

The default software is **7-Zip**, **CrystalDiskInfo**, and **ExplorerPatcher**, with verified winget IDs `7zip.7zip`, `CrystalDewWorld.CrystalDiskInfo`, and `valinet.ExplorerPatcher`. The [7-Zip manifest](https://github.com/microsoft/winget-pkgs/tree/master/manifests/7/7zip/7zip), [CrystalDiskInfo manifest](https://github.com/microsoft/winget-pkgs/tree/master/manifests/c/CrystalDewWorld/CrystalDiskInfo), and [ExplorerPatcher manifest](https://github.com/microsoft/winget-pkgs/tree/master/manifests/v/valinet/ExplorerPatcher/26100.8457.70.3) are in Microsoft's winget-pkgs repository.

`config.example.yml` has no normal registry changes. ExplorerPatcher settings restoration remains optional. Profiles are limited to 64 KiB, version `1`, and known fields. Duplicate keys, unknown fields, YAML anchors, aliases, tags, merge keys, duplicate package names, and duplicate winget IDs are rejected. Profiles cannot execute arbitrary commands or scripts. Supported package sources are winget, GitHub Releases with exact assets, and direct HTTPS URLs with SHA-256. Downloads use temporary files and verification before replacement.

The GUI saves through `ConfigurationWorkspace`, which validates before saving and atomically replaces `config.yml`. It retains one `config.yml.bak` and detects concurrent edits. Configuration and ExplorerPatcher settings paths cannot traverse symlinks or escape the configuration folder.

## ExplorerPatcher

ExplorerPatcher is a high-risk shell modification and runs in the final phase. WinRebuilder uses the verified winget package `valinet.ExplorerPatcher`, pinned to version `26100.8457.70.3`. Microsoft's [installer manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/v/valinet/ExplorerPatcher/26100.8457.70.3/valinet.ExplorerPatcher.installer.yaml) points to the official `valinet/ExplorerPatcher` GitHub release. WinRebuilder verifies the Microsoft winget source, package identity, installer URL, and SHA-256 before installation, and verifies installed-package detection afterward. There is no direct-download fallback.

The native picker accepts `.reg` files only for ExplorerPatcher settings. Core parses and validates the complete file against a separate strict allowlist before any mutation, then stores a validated copy in `settings/` beside `config.yml`. There is no generic registry import, and no `regedit.exe`, `reg.exe`, PowerShell, or cmd invocation for settings restoration.

## Registry safety and rollback

Normal `registry:` entries remain limited to `HKLM\SOFTWARE\Policies` and `HKCU\Software\Policies`, with DWORD or string values. For every changed value, WinRebuilder reads the previous state, writes a typed backup, reloads and verifies that backup, confirms the value has not changed since inspection, applies the change, and rereads the result before marking completion. Backups live under `%ProgramData%\WinRebuilder\backups`. The Configuration view can restore a selected value through the same rollback infrastructure. HKLM writes and rollback require elevation.

Dry-run APIs do not write registry, package, download, backup, log, or execution state. Read-only winget queries may refresh winget's own metadata cache. Windows CI uses fakes for destructive paths. Actual installation effects and second-run idempotency still need testing on a disposable Windows 11 machine.

## Building

```sh
dotnet restore WinRebuilder.slnx
dotnet build WinRebuilder.slnx
dotnet test WinRebuilder.slnx
dotnet publish src/WinRebuilder.UI/WinRebuilder.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false
```

Core tests run on macOS. Windows adapter and WPF tests run on Windows. A macOS build compiles WPF but does not establish that its window works at runtime. The release workflow runs a Windows startup sanity check without installing packages or changing registry values.

## Release and verification

The tag workflow requires the exact public version, builds and tests, measures five publish variants, verifies that the main WPF window opens, and packages a self-contained compressed single-file build with trimming and ReadyToRun disabled. WPF and YAML reflection paths have not been fully validated under trimming, so production trimming stays off. Release assets contain no PDB files.

Cross-publish measurements on macOS (Windows startup timing is measured in the release workflow):

| Configuration | EXE bytes | Chosen |
| --- | ---: | --- |
| Self-contained single-file baseline | 140,217,609 | No |
| Compressed single-file | 65,001,530 | No |
| Compressed, ReadyToRun off | 65,001,530 | Yes |
| Compressed, ReadyToRun on | 70,154,641 | No |
| Framework-dependent comparison | 162,304 launcher; 676 KiB folder | No |

The current SDK baseline already has ReadyToRun disabled, so the compressed and explicit ReadyToRun-off outputs match in size. The framework-dependent build requires the Windows Desktop .NET runtime on the target machine. Compression cuts the self-contained EXE by about 54%; CI rejects the choice if first or warm Windows startup is too slow.

The workflow copies the final published executable to the portable name, verifies that the bytes match, and inspects the ZIP manifest. Release executables are unsigned.

The only manually uploaded assets are `WinRebuilder.zip` and `WinRebuilder-portable.exe`. GitHub supplies Source code (zip) and Source code (tar.gz) from the tag. The release workflow uses only `GITHUB_TOKEN`, with `contents: write` confined to the publication job. It will not overwrite an existing release.
