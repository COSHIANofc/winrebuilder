# WinRebuilder Development Instructions

## Project purpose

WinRebuilder is a Windows 11 environment reconstruction tool.

Its purpose is to automate post-clean-install setup, including:

- application installation
- registry configuration
- configuration restoration
- package downloading
- environment reconstruction

Development is performed primarily on macOS.
The final application runs on Windows 11.

`config.yml` is the canonical user configuration. `config.example.yml` is the conservative release example.
The WPF GUI edits `config.yml` through WinRebuilder.Core; the GUI, planner and executor use that same model.
The public version is the exact `InformationalVersion` in `Directory.Build.props`; `Version`, `AssemblyVersion`, and `FileVersion` stay numeric.

## Priorities

In order of importance:

1. Safety
2. Recoverability
3. Correctness
4. Idempotency
5. Maintainability
6. Convenience

Never trade safety for shorter code.

## Architecture

Keep Windows-specific behavior isolated from platform-independent logic.

Preferred projects:

- WinRebuilder.Core
- WinRebuilder.Windows
- WinRebuilder.UI
- WinRebuilder.Core.Tests
- WinRebuilder.Windows.Tests
- WinRebuilder.UI.Tests

Core must not directly depend on Windows APIs.

Use interfaces for:

- registry access
- process execution
- package installation
- downloads
- filesystem operations where appropriate
- application detection

## Windows targeting

Development occurs on macOS.

Windows-only projects may target net10.0-windows.

If required to allow compilation from macOS, configure:

<EnableWindowsTargeting>true</EnableWindowsTargeting>

Do not pretend that Windows-specific behavior has been runtime-tested when only compiled on macOS.

## Security

Profile files must be treated as untrusted input.

Never allow YAML profiles to execute arbitrary:

- PowerShell
- cmd.exe commands
- shell commands
- scripts

Do not introduce generic "command" or "script" profile fields.

Remote downloads must use HTTPS.

Support SHA-256 verification.

Downloads should use temporary files and only move them into place after successful completion and validation.

Avoid command injection.

Avoid path traversal.

Never log secrets.

## Registry

Registry changes must be recoverable.

Before changing a registry value:

1. Read the current value.
2. Preserve enough information to restore it.
3. Write backup metadata.
4. Apply the change.
5. Verify the result.

Registry backup must not be optional in the initial implementation.

Never delete registry keys recursively unless explicitly designed, reviewed, and justified.

## Idempotency

Re-running the same profile must be safe.

Examples:

- already installed package -> skip
- matching registry value -> skip
- valid existing download -> reuse or skip

Operations need deterministic IDs where practical.

## Package providers

Initial package provider types:

- winget
- GitHub Releases
- direct HTTPS URL

Do not create a generic arbitrary-command provider.

## ExplorerPatcher

Treat ExplorerPatcher as a high-risk shell modification.

It must:

- run in the shell/final phase
- never run before ordinary package installation
- be installed through its verified winget package
- verify the Microsoft winget-pkgs manifest, package metadata, and installer URL before using the ID
- require its installer to resolve to the official `valinet/ExplorerPatcher` GitHub Releases infrastructure
- fail safely if the installed winget manifest does not match the verified source and hash
- use exact package identity and the existing WinRebuilder winget provider
- retain post-install package verification
- not rely on undocumented registry assumptions
- never bypass the normal WinRebuilder execution, logging, state, or safety infrastructure

Do not use the GitHub Releases provider as the normal ExplorerPatcher installation method.

The ExplorerPatcher winget package ID must be verified against Microsoft winget-pkgs before being hardcoded or included in the default configuration. Do not guess package identifiers or silently fall back to a direct download.

ExplorerPatcher `.reg` restoration is a special-purpose, strict, allowlisted parser.

Never:

- implement a generic `.reg` import feature
- invoke `regedit.exe`
- invoke `reg.exe`
- invoke PowerShell
- invoke `cmd.exe`
- accept arbitrary registry paths from an ExplorerPatcher `.reg` file

The complete `.reg` file must be parsed and validated before any registry mutation occurs.

If any entry is unsupported, malformed, or outside the ExplorerPatcher-specific allowlist, reject the complete file before mutation.

The ExplorerPatcher registry allowlist must remain separate from the normal `registry:` configuration policy.

Normal `registry:` entries retain their existing restricted Policies-path safety rules.

Every ExplorerPatcher registry mutation must use the normal WinRebuilder recoverability guarantees:

1. read the current registry state
2. create a typed backup
3. persist the backup successfully
4. reload and verify the persisted backup
5. verify that the registry value has not changed since backup
6. apply the mutation
7. reread the registry
8. verify the result
9. only then mark the operation complete

ExplorerPatcher `.reg` restoration must remain compatible with WinRebuilder rollback infrastructure.

If the selected `.reg` file originates outside the WinRebuilder configuration directory, validate it first and copy it into a safe configuration-local path such as:

`settings/explorerpatcher.reg`

Store only the safe relative path in `config.yml`.

Do not store arbitrary absolute, UNC, device, or traversal paths in configuration.

## Release

WinRebuilder release artifacts target Windows 11 x64.

WinRebuilder is GUI-only. The graphical application executable is:

`WinRebuilder.exe`

Release builds should use:

- `Release` configuration
- `win-x64`
- self-contained deployment
- trimming disabled

Prefer single-file publishing where it has been verified to work correctly for the relevant project.

Do not enable trimming merely to reduce artifact size.

Do not use NativeAOT unless it is explicitly introduced and fully validated in a later milestone.

The tag-release workflow must:

- validate the Git tag against the application's exact public version
- fail before publishing if the tag and application version differ
- run restore, build, and tests before release publication
- verify expected release artifacts exist and are non-empty
- use only the GitHub-provided `GITHUB_TOKEN`
- use job-scoped `contents: write` only where release publication requires it
- avoid long-lived repository or personal access tokens
- create prereleases for alpha/beta versions
- never overwrite historical release assets or tags

Release assets should include the required executable artifacts and conservative configuration examples.

The only manually uploaded release assets are `WinRebuilder.zip` and `WinRebuilder-portable.exe`.
The ZIP must contain exactly `README.md`, `WinRebuilder.exe`, `config.yml`, and `config.example.yml`.
The portable executable must be a byte-for-byte copy of the final published executable.
GitHub provides source code archives automatically.

Do not publish a default configuration that performs unexpected or destructive machine changes.

## Dry run

Dry-run behavior is a first-class feature.

Dry-run must not modify:

- registry
- filesystem state
- installed applications
- execution state

unless a clearly documented read-only/cache exception exists.

## Testing

Core tests must run on macOS.

Windows-specific runtime tests belong on Windows.

GitHub Actions should test at least:

- platform-independent tests
- Windows compilation/tests

Use fakes or mocks for Windows dependencies in Core tests.

## Git

Do not commit or push unless the user explicitly asks.
For authorized successful implementation tasks, commit and push after all required local validation passes.
When authorized to release: validate locally, inspect the staged diff, push main, then push the matching annotated version tag; verify the GitHub Actions release and assets. Never bypass failed validation.

Do not modify unrelated files.

Before reporting completion:

- inspect git diff
- run relevant tests
- run relevant builds
- review for security problems
- report anything that could not be verified
