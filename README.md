# WinRebuilder

Current version: **v.0.2.a-beta**

Current stage: **early beta / development build**

WinRebuilder is an early Windows 11 environment reconstruction CLI. It reads `config.yml`, installs configured applications, applies restricted registry values, and restores a supported subset of ExplorerPatcher settings. Registry changes are backed up and verifiable. There is no GUI or reboot support.

## Architecture

- `WinRebuilder.Core` parses and validates profiles, orders operations, runs the execution loop, and defines interfaces. It has no Windows API calls.
- `WinRebuilder.Windows` contains winget, registry, privilege, download, process, detection, backup, state, and file logging implementations.
- `WinRebuilder.Cli` provides `validate`, `plan`, `apply`, `backups`, and `rollback`.
- Core tests use fakes and run on macOS. Windows runtime behavior needs Windows CI and manual Windows 11 testing.

## Build and use

Requires the .NET 10 SDK. On macOS, `validate`, `plan`, and Core tests work. `apply` requires Windows; an HKLM profile requires elevation.

```sh
dotnet restore WinRebuilder.slnx
dotnet build WinRebuilder.slnx
dotnet test WinRebuilder.slnx
dotnet run --project src/WinRebuilder.Cli -- --version
dotnet run --project src/WinRebuilder.Cli -- validate config.yml
dotnet run --project src/WinRebuilder.Cli -- plan config.yml
dotnet run --project src/WinRebuilder.Cli -- apply config.yml --dry-run
dotnet run --project src/WinRebuilder.Cli -- backups
dotnet run --project src/WinRebuilder.Cli -- rollback <backup-id> --dry-run
```

After publishing the CLI, invoke the `winrebuilder` executable directly:

```sh
winrebuilder --version
winrebuilder validate config.yml
winrebuilder plan config.yml
winrebuilder apply config.yml --dry-run
winrebuilder apply config.yml
winrebuilder backups
winrebuilder rollback <backup-id> --dry-run
winrebuilder rollback <backup-id>
```

On Windows, omit `--dry-run` to apply. `plan` shows declared operations and phase order. `apply --dry-run` reads installed package and registry state and reports `INSTALL`, `CHANGE`, or `SKIP` without WinRebuilder writes. Real apply logs `FAIL` and stops at the first failed operation. `WARNING` identifies GitHub release assets without a pinned hash.

## Winget behavior

Windows 11 needs `winget.exe` from Windows Package Manager/App Installer on `PATH`. Before a winget operation, WinRebuilder runs `winget --version` and checks `winget list --help` and `winget install --help` for the flags it uses. It reports a missing executable, a launch failure, or malformed/unsupported output separately. There is no fixed version floor; the required options are checked on the installed client.

Detection runs `winget list --id <ID> --exact --disable-interactivity`. WinRebuilder uses winget's success or documented no-applications-found exit code, not localized table columns or similar package names. Any other exit code fails the operation. Installation passes separate process arguments: `install --id <ID> --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity`. After exit code zero, WinRebuilder runs exact-ID detection again. Only a confirmed detection records `INSTALL` as complete; a second apply then records `SKIP`. `--silent` asks winget for a silent installer but cannot guarantee that every third-party installer avoids prompts.
The detection and install flags follow Microsoft's [list](https://learn.microsoft.com/en-us/windows/package-manager/winget/list) and [install](https://learn.microsoft.com/en-us/windows/package-manager/winget/install) documentation; the no-applications-found code is defined in [AppInstallerErrors.h](https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerSharedLib/Public/AppInstallerErrors.h).

Dry run may run the read-only version, help, and list queries. It does not run `winget install`, accept agreements, write WinRebuilder execution state, or download installers. Winget itself may refresh metadata cache during queries. Windows CI compiles the full solution and runs fake-backed adapter tests and a process-cancellation test; it never installs or removes packages. Actual winget availability, exact-ID detection, installer behavior, and second-run idempotency still require a disposable Windows 11 test machine.
If winget requires a source agreement before `list`, dry run fails without accepting it. Review and accept that agreement outside WinRebuilder before retrying. Ctrl+C requests cancellation and stops the child process tree where supported.

## Configuration format (version 1)

Edit [config.yml](config.yml) to add or remove applications. [config.example.yml](config.example.yml) is an empty starting point distributed with the release. Pass a path explicitly; WinRebuilder does not search parent directories. Top level allows only `version`, `packages`, `registry`, and `explorerPatcher`. Unknown keys, duplicate keys, YAML anchors, aliases, tags, and merge keys are rejected. A config is limited to 64 KiB. Only version `1` is supported. Each package has a display `name`, exact lowercase `provider`, and optional `phase` (`normal` by default, or `shell`). Normal packages run before normal registry values; shell packages run later, followed by ExplorerPatcher settings. Duplicate operations are rejected. Operation IDs are deterministic hashes of operation configuration; config hash is SHA-256 of normalized YAML text and, when ExplorerPatcher settings are enabled, also incorporates the .reg file bytes.

Example:

```yaml
version: 1
packages:
  - name: 7-Zip
    provider: winget
    id: 7zip.7zip
  - name: ExplorerPatcher
    provider: github
    repository: valinet/ExplorerPatcher
    asset: ep_setup.exe
    installer: exe
    silentMode: silent
    uninstallDisplayName: ExplorerPatcher
    phase: shell
registry: []
explorerPatcher:
  enabled: true
  settingsFile: settings/explorerpatcher.reg
```

To remove an application, remove its `packages` entry; this does not uninstall software already on the machine. To disable settings restoration, set `explorerPatcher.enabled: false` and omit `settingsFile`. When enabled, the official ExplorerPatcher GitHub package must also be present in the shell phase. `settingsFile` is resolved relative to the config directory; absolute paths, traversal, and symbolic-link traversal are rejected.

Package providers:

| Provider | Required fields | Optional fields | Behavior |
| --- | --- | --- | --- |
| `winget` | `id` | none | Exact package ID; silent installation and package/source agreements accepted. |
| `github` | `repository` (`owner/repo`), `asset`, `installer`, `uninstallDisplayName`; `silentMode` if exe | `sha256` | Downloads the exact asset from the latest GitHub release. |
| `url` | `url` (HTTPS), `sha256`, `installer`, `uninstallDisplayName`; `silentMode` if exe | none | Downloads a pinned direct URL. |

Winget IDs are limited to 128 characters, with at most eight dot-separated segments of 1–32 characters. Whitespace, control and format characters, and Windows path metacharacters are rejected. A single segment is allowed for Store-style IDs. This follows the safety limits of Microsoft's [manifest identifier schema](https://github.com/microsoft/winget-cli/blob/master/schemas/JSON/manifests/latest/manifest.singleton.latest.json) while retaining separate process arguments for valid punctuation.

`installer` is `msi` or `exe`. MSI uses fixed `/i /qn /norestart` switches; exe requires a `silentMode` of `none`, `s`, `silent`, or `verysilent`, mapped to fixed switches. These modes depend on the installer; test each package on Windows. No free-form installer arguments or commands are supported. Download package detection matches an exact Windows uninstall display name. GitHub latest releases without `sha256` are mutable and should be reviewed and pinned for stronger reproducibility. ExplorerPatcher must use `valinet/ExplorerPatcher`, `ep_setup.exe`, and `phase: shell`.

## ExplorerPatcher settings files

WinRebuilder parses the complete `.reg` file before any operation. It supports the `Windows Registry Editor Version 5.00` header, UTF-8 or UTF-16 LE text, comments, quoted REG_SZ strings, eight-digit hexadecimal REG_DWORD values, and named-value deletion (`"Name"=-`). It does not invoke `regedit`, `reg.exe`, or PowerShell. Before the first ExplorerPatcher registry write, it inspects every setting, saves and reloads every needed backup, and checks that the inspected values have not changed. Each subsequent change is reread and verified. A runtime failure after writing begins can leave earlier values changed; their individual backups remain available for rollback. Dry-run parses the whole file and reads state without installing ExplorerPatcher, writing backups, changing the registry, or saving execution state.

The special allowlist is limited to exact HKCU keys and named values from [ExplorerPatcher's official settings definitions](https://github.com/valinet/ExplorerPatcher/blob/master/ep_gui/resources/settings.reg): `Software\ExplorerPatcher`, `...\Explorer\Advanced`, `...\Explorer\StartPage`, `...\Explorer\ExplorerPatcher`, `...\Search`, and `Control Panel\Desktop`. Generic `registry:` entries retain the separate Policies-only rule. The parser rejects HKLM, Run/RunOnce, services, unrelated value names, unsupported syntax, REG_BINARY, whole-key deletion, virtualized pseudo-settings, and custom or local ExplorerPatcher update sources. An official export containing any of those features fails as a whole; use ExplorerPatcher's own Properties import for features outside this subset. Its [settings-management documentation](https://github.com/valinet/ExplorerPatcher/wiki/Settings-management) notes that some settings need extra application logic beyond a Registry Editor merge. A File Explorer restart or sign-out may be needed before supported changes become visible; WinRebuilder does not restart Explorer.

Registry entries require `path`, `name`, `type`, and `value`. Version 1 permits only subkeys under `HKLM\SOFTWARE\Policies` or `HKCU\Software\Policies`; this excludes startup and other executable registry locations. `type` supports `DWORD` (unsigned 32-bit integer) and `STRING`. Other existing value kinds are refused before mutation. The Windows adapter uses the 64-bit registry view on Windows 11.

## Registry backups and rollback

Before each changed registry value, WinRebuilder reads its current state and writes a backup under `%ProgramData%\WinRebuilder\backups\<backup-id>.json`. IDs are generated 32-character hexadecimal identifiers, never profile text. The file is written to a temporary file, flushed, and moved into place without overwriting an existing backup. WinRebuilder reloads and validates the saved backup before any registry write. A failed write or reload stops the change. It also rereads the original value after backup and refuses to write if it changed. After writing the target value, it rereads again; only a match marks the operation complete. A failure leaves the backup available and reports its ID.

The strict JSON backup schema has `SchemaVersion: 1`, `BackupId`, `ProfileHash`, `ApplicationVersion`, `OperationId`, `Timestamp`, `Hive`, `KeyPath`, `View`, `Name`, `Previous`, and `Target`. Values encode existence, kind, a numeric DWORD or exact string. Unknown, duplicate, missing, malformed, or future-version fields are rejected. This detects malformed backups; it does **not** authenticate them against deliberate alteration. Protect ProgramData backup files with appropriate Windows file permissions.

`winrebuilder backups` lists IDs and locations. `winrebuilder rollback <backup-id>` restores one selected value and verifies it by rereading. If the value did not exist before apply, rollback deletes only that value; it never recursively removes a key or touches other values. A second rollback returns `SKIP`. Rollback refuses to overwrite a value that no longer matches either the backed-up original or the recorded applied target. HKLM rollback requires elevation. `winrebuilder rollback <backup-id> --dry-run` reads and reports the action without writing. `apply --dry-run` does not create backup files, directories, or execution state. Console and JSON-lines logs include IDs, paths, actions, and outcomes but omit registry contents.

State, artifacts, and JSON-lines logs reside under `%ProgramData%\WinRebuilder`. State records profile hash, application version, completed and failed operation IDs with timestamps; it does not suppress fresh installed-state checks. Backups from the earlier unversioned format are not accepted by the new rollback command.

## Security and limitations

Profiles are untrusted input. No PowerShell, cmd, shell, script, generic command provider, or arbitrary installer argument field exists. Processes receive separate argument tokens. Direct downloads require HTTPS and SHA-256; download redirects must remain HTTPS and downloads are capped at 2 GiB. Downloads use temporary files and pinned artifacts are moved into place only after hash validation. GitHub downloads may omit a hash, so integrity depends on GitHub TLS and release ownership. Dry run never creates backups, artifacts, logs on disk, or WinRebuilder execution state; winget package detection may use its own metadata cache. Console output shows package names and IDs; file logs record operation IDs and outcomes. Neither includes raw winget stdout/stderr or registry values.

## Windows executable and release

The tag-triggered GitHub Actions release workflow validates the tag against the public version, builds and tests on Windows, then publishes a self-contained single-file `win-x64` `winrebuilder.exe` with trimming disabled. Its prerelease assets are `winrebuilder.exe`, `winrebuilder.exe.sha256`, and `config.example.yml`. The SHA-256 file contains a lowercase hash followed by two spaces and the executable name. Download the EXE and config from the [GitHub Releases page](https://github.com/COSHIANofc/winrebuilder/releases), verify the checksum, copy the example to `config.yml`, review it, then run `winrebuilder validate config.yml` and `winrebuilder apply config.yml --dry-run` before applying.

To publish locally for review: `dotnet publish src/WinRebuilder.Cli/WinRebuilder.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false`. The target Windows installation does not need the .NET runtime. Windows integration is still unproven at runtime: winget behavior, installer switches, Registry adapter behavior, and ExplorerPatcher effects need testing on a disposable Windows 11 machine. There is no reboot/resume execution. Review config, especially ExplorerPatcher and HKLM changes, before applying.
