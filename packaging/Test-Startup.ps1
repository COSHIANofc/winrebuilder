param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path $Executable).Path
$directory = Split-Path $resolved -Parent
if (-not (Test-Path (Join-Path $directory 'config.yml'))) { Copy-Item config.yml (Join-Path $directory 'config.yml') }
$bytes = [IO.File]::ReadAllBytes($resolved)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
if ([BitConverter]::ToUInt16($bytes, $peOffset + 24 + 68) -ne 2) { throw 'Executable is not a Windows GUI subsystem application.' }
$watch = [Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $resolved -WorkingDirectory $directory -PassThru
try {
    while ($watch.Elapsed.TotalSeconds -lt 20) {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw "GUI exited during startup: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne 0) { break }
    }
    if ($process.MainWindowHandle -eq 0) { throw 'Main WPF window did not open.' }
    Write-Host "First main window: $([math]::Round($watch.Elapsed.TotalMilliseconds)) ms"
}
finally {
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force } }
    $process.Dispose()
}
