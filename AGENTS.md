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
- WinRebuilder.Cli
- WinRebuilder.Windows
- WinRebuilder.Core.Tests
- WinRebuilder.Windows.Tests

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
- use the official GitHub repository as its source
- not rely on undocumented registry assumptions

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

Do not modify unrelated files.

Before reporting completion:

- inspect git diff
- run relevant tests
- run relevant builds
- review for security problems
- report anything that could not be verified
