# WinRebuilder

Current version: **v.0.1.a-beta**

Current stage: **early beta / development build**

WinRebuilder is an early Windows 11 environment reconstruction CLI. It validates a restricted YAML profile, creates a deterministic plan, installs packages, and backs up and applies registry values. There is no GUI, reboot support, rollback command, or settings restoration yet.

## Architecture

- `WinRebuilder.Core` parses and validates profiles, orders operations, runs the execution loop, and defines interfaces. It has no Windows API calls.
- `WinRebuilder.Windows` contains winget, registry, privilege, download, process, detection, backup, state, and file logging implementations.
- `WinRebuilder.Cli` provides `validate`, `plan`, and `apply`.
- Core tests use fakes and run on macOS. Windows runtime behavior needs Windows CI and manual Windows 11 testing.

## Build and use

Requires the .NET 10 SDK. On macOS, `validate`, `plan`, and Core tests work. `apply` requires Windows; an HKLM profile requires elevation.

```sh
dotnet restore WinRebuilder.slnx
dotnet build WinRebuilder.slnx
dotnet test WinRebuilder.slnx
dotnet run --project src/WinRebuilder.Cli -- --version
dotnet run --project src/WinRebuilder.Cli -- validate profiles/example.yaml
dotnet run --project src/WinRebuilder.Cli -- plan profiles/example.yaml
dotnet run --project src/WinRebuilder.Cli -- apply profiles/example.yaml --dry-run
```

After publishing the CLI, invoke the `winrebuilder` executable directly:

```sh
winrebuilder --version
winrebuilder validate profiles/example.yaml
winrebuilder plan profiles/example.yaml
winrebuilder apply profiles/example.yaml --dry-run
winrebuilder apply profiles/example.yaml
```

On Windows, omit `--dry-run` to apply. `plan` shows declared operations and phase order. `apply --dry-run` reads installed package and registry state and reports `INSTALL`, `CHANGE`, or `SKIP` without WinRebuilder writes. Real apply logs `FAIL` and stops at the first failed operation. `WARNING` identifies GitHub release assets without a pinned hash.

## Winget behavior

Windows 11 needs `winget.exe` from Windows Package Manager/App Installer on `PATH`. Before a winget operation, WinRebuilder runs `winget --version` and checks `winget list --help` and `winget install --help` for the flags it uses. It reports a missing executable, a launch failure, or malformed/unsupported output separately. There is no fixed version floor; the required options are checked on the installed client.

Detection runs `winget list --id <ID> --exact --disable-interactivity`. WinRebuilder uses winget's success or documented no-applications-found exit code, not localized table columns or similar package names. Any other exit code fails the operation. Installation passes separate process arguments: `install --id <ID> --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity`. After exit code zero, WinRebuilder runs exact-ID detection again. Only a confirmed detection records `INSTALL` as complete; a second apply then records `SKIP`. `--silent` asks winget for a silent installer but cannot guarantee that every third-party installer avoids prompts.
The detection and install flags follow Microsoft's [list](https://learn.microsoft.com/en-us/windows/package-manager/winget/list) and [install](https://learn.microsoft.com/en-us/windows/package-manager/winget/install) documentation; the no-applications-found code is defined in [AppInstallerErrors.h](https://github.com/microsoft/winget-cli/blob/master/src/AppInstallerSharedLib/Public/AppInstallerErrors.h).

Dry run may run the read-only version, help, and list queries. It does not run `winget install`, accept agreements, write WinRebuilder execution state, or download installers. Winget itself may refresh metadata cache during queries. Windows CI compiles the full solution and runs fake-backed adapter tests and a process-cancellation test; it never installs or removes packages. Actual winget availability, exact-ID detection, installer behavior, and second-run idempotency still require a disposable Windows 11 test machine.
If winget requires a source agreement before `list`, dry run fails without accepting it. Review and accept that agreement outside WinRebuilder before retrying. Ctrl+C requests cancellation and stops the child process tree where supported.

## Profile format (version 1)

See [profiles/example.yaml](profiles/example.yaml). Top level allows only `version`, `packages`, and `registry`. Unknown keys, duplicate keys, YAML anchors, aliases, tags, and merge keys are rejected. A profile is limited to 64 KiB. Only version `1` is supported. Each package has a display `name`, exact lowercase `provider`, and optional `phase` (`normal` by default, or `shell`). Normal packages run before normal registry values; all shell packages run last. Duplicate operations are rejected. Operation IDs are deterministic hashes of operation configuration; profile hash is SHA-256 of source text with normalized line endings.

Package providers:

| Provider | Required fields | Optional fields | Behavior |
| --- | --- | --- | --- |
| `winget` | `id` | none | Exact package ID; silent installation and package/source agreements accepted. |
| `github` | `repository` (`owner/repo`), `asset`, `installer`, `uninstallDisplayName`; `silentMode` if exe | `sha256` | Downloads the exact asset from the latest GitHub release. |
| `url` | `url` (HTTPS), `sha256`, `installer`, `uninstallDisplayName`; `silentMode` if exe | none | Downloads a pinned direct URL. |

Winget IDs are limited to 128 characters, with at most eight dot-separated segments of 1–32 characters. Whitespace, control and format characters, and Windows path metacharacters are rejected. A single segment is allowed for Store-style IDs. This follows the safety limits of Microsoft's [manifest identifier schema](https://github.com/microsoft/winget-cli/blob/master/schemas/JSON/manifests/latest/manifest.singleton.latest.json) while retaining separate process arguments for valid punctuation.

`installer` is `msi` or `exe`. MSI uses fixed `/i /qn /norestart` switches; exe requires a `silentMode` of `none`, `s`, `silent`, or `verysilent`, mapped to fixed switches. These modes depend on the installer; test each package on Windows. No free-form installer arguments or commands are supported. Download package detection matches an exact Windows uninstall display name. GitHub latest releases without `sha256` are mutable and should be reviewed and pinned for stronger reproducibility. ExplorerPatcher must use `valinet/ExplorerPatcher`, `ep_setup.exe`, and `phase: shell`.

Registry entries require `path`, `name`, `type`, and `value`. Version 1 permits only subkeys under `HKLM\SOFTWARE\Policies` or `HKCU\Software\Policies`; this excludes startup and other executable registry locations. `type` supports `DWORD` (unsigned decimal 32-bit integer) and `STRING`; other existing value types are refused rather than overwritten. Registry backups always record prior existence, type, and value before a write. Backups, state, artifacts, and JSON-lines logs reside under `%ProgramData%\WinRebuilder`. Backups are retained for a future rollback tool. State records profile hash, application version, completed and failed operation IDs with timestamps; it does not suppress fresh installed-state checks.

## Security and limitations

Profiles are untrusted input. No PowerShell, cmd, shell, script, generic command provider, or arbitrary installer argument field exists. Processes receive separate argument tokens. Direct downloads require HTTPS and SHA-256; download redirects must remain HTTPS and downloads are capped at 2 GiB. Downloads use temporary files and pinned artifacts are moved into place only after hash validation. GitHub downloads may omit a hash, so integrity depends on GitHub TLS and release ownership. Dry run never creates backups, artifacts, logs on disk, or WinRebuilder execution state; winget package detection may use its own metadata cache. Console output shows package names and IDs; file logs record operation IDs and outcomes. Neither includes raw winget stdout/stderr or registry values.

Windows integration is currently unproven at runtime. Winget exact-ID detection can vary by client or package metadata, and uninstall display name detection for other providers may vary by machine. Installer switches can differ between products. Registry rollback is not implemented, though backup metadata is saved. There is no reboot/resume execution or settings restoration yet. Review profiles, particularly ExplorerPatcher and HKLM changes, before applying.
