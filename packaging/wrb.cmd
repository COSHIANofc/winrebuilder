@echo off
setlocal DisableDelayedExpansion
"%~dp0WinRebuilder.Cli.exe" %*
exit /b %errorlevel%
