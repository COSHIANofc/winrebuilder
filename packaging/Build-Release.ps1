$ErrorActionPreference = 'Stop'
[xml]$props = Get-Content Directory.Build.props
$version = [string]$props.Project.PropertyGroup.InformationalVersion
if ($version -ne 'v.0.3.c-beta') { throw 'Unexpected public version.' }
if (-not $env:WINDOWS_SIGNING_CERT_PFX_BASE64 -or -not $env:WINDOWS_SIGNING_CERT_PASSWORD -or -not $env:WINDOWS_SIGNING_TIMESTAMP_URL) {
    throw 'Production signing is not configured. Set all three WINDOWS_SIGNING_* secrets before publishing.'
}
$project = 'src/WinRebuilder.UI/WinRebuilder.UI.csproj'
$variants = @(
    @{ Name = 'baseline'; Extra = @() },
    @{ Name = 'compressed'; Extra = @('-p:EnableCompressionInSingleFile=true') },
    @{ Name = 'readytorun-off'; Extra = @('-p:EnableCompressionInSingleFile=true', '-p:PublishReadyToRun=false') },
    @{ Name = 'readytorun-on'; Extra = @('-p:EnableCompressionInSingleFile=true', '-p:PublishReadyToRun=true') },
    @{ Name = 'framework-dependent'; Extra = @(); SelfContained = 'false'; SingleFile = 'false' }
)
function Measure-Startup([string]$exe, [string]$directory) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath (Resolve-Path $exe).Path -WorkingDirectory (Resolve-Path $directory).Path -PassThru
    try {
        while ($watch.Elapsed.TotalSeconds -lt 20) {
            Start-Sleep -Milliseconds 100
            $process.Refresh()
            if ($process.HasExited) { throw "Startup crashed: $exe, exit $($process.ExitCode)" }
            if ($process.MainWindowHandle -ne 0) { break }
        }
        if ($process.MainWindowHandle -eq 0) { throw "Main WPF window did not open: $exe" }
        return [math]::Round($watch.Elapsed.TotalMilliseconds)
    }
    finally {
        if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force } }
        $process.Dispose()
    }
}
$report = @()
foreach ($variant in $variants) {
    $directory = "publish/$($variant.Name)"
    $selfContained = if ($variant.SelfContained) { $variant.SelfContained } else { 'true' }
    $singleFile = if ($variant.SingleFile) { $variant.SingleFile } else { 'true' }
    $arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', $selfContained,
        "-p:PublishSingleFile=$singleFile", '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $directory) + $variant.Extra
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($variant.Name)" }
    $exe = Join-Path $directory 'WinRebuilder.exe'
    if (-not (Test-Path $exe) -or (Get-Item $exe).Length -le 0) { throw "Missing executable: $($variant.Name)" }
    if (Get-ChildItem $directory -Filter '*.pdb' -Recurse) { throw "PDB in $directory" }
    Copy-Item config.yml (Join-Path $directory 'config.yml')
    $first = Measure-Startup $exe $directory
    $warm = Measure-Startup $exe $directory
    $report += [pscustomobject]@{ Configuration = $variant.Name; Bytes = (Get-Item $exe).Length; FirstStartupMs = $first; WarmStartupMs = $warm; Selected = ($variant.Name -eq 'readytorun-off') }
}
$report | Format-Table | Out-String | Write-Host
$selected = $report | Where-Object Selected
$r2r = $report | Where-Object Configuration -EQ 'readytorun-on'
if ($selected.FirstStartupMs -gt 8000 -or $selected.WarmStartupMs -gt 4000 -or
    $selected.WarmStartupMs -gt (2 * $r2r.WarmStartupMs + 300)) {
    throw 'Selected compressed, ReadyToRun-disabled build has unacceptable startup time; review publish choice.'
}
$report | ConvertTo-Json | Set-Content -Path publish/size-report.json -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content $env:GITHUB_STEP_SUMMARY '### Publish size and first window timing'
    Add-Content $env:GITHUB_STEP_SUMMARY '| Configuration | Bytes | First window ms | Warm window ms | Selected |'
    Add-Content $env:GITHUB_STEP_SUMMARY '|---|---:|---:|---:|---|'
    foreach ($row in $report) { Add-Content $env:GITHUB_STEP_SUMMARY "| $($row.Configuration) | $($row.Bytes) | $($row.FirstStartupMs) | $($row.WarmStartupMs) | $($row.Selected) |" }
}
$final = 'publish/readytorun-off/WinRebuilder.exe'
$pe = [IO.File]::ReadAllBytes((Resolve-Path $final).Path)
$peOffset = [BitConverter]::ToInt32($pe, 0x3c)
if ([BitConverter]::ToUInt16($pe, $peOffset + 24 + 68) -ne 2) { throw 'Executable is not a Windows GUI subsystem application.' }
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$signTool = Get-ChildItem $sdk -Filter signtool.exe -Recurse -File | Where-Object FullName -Match '\\x64\\signtool.exe$' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signTool) { throw 'Windows SDK SignTool was not found.' }
$certificate = Join-Path $env:RUNNER_TEMP ('winrebuilder-signing-' + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [IO.File]::WriteAllBytes($certificate, [Convert]::FromBase64String($env:WINDOWS_SIGNING_CERT_PFX_BASE64))
    Remove-Item Env:WINDOWS_SIGNING_CERT_PFX_BASE64
    & $signTool sign /f $certificate /p $env:WINDOWS_SIGNING_CERT_PASSWORD /fd SHA256 /tr $env:WINDOWS_SIGNING_TIMESTAMP_URL /td SHA256 $final
    if ($LASTEXITCODE -ne 0) { throw 'SignTool signing failed.' }
}
finally {
    if (Test-Path $certificate) {
        $length = (Get-Item $certificate).Length
        [IO.File]::WriteAllBytes($certificate, (New-Object byte[] $length))
        Remove-Item $certificate -Force
    }
    Remove-Item Env:WINDOWS_SIGNING_CERT_PFX_BASE64 -ErrorAction SilentlyContinue
    Remove-Item Env:WINDOWS_SIGNING_CERT_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:WINDOWS_SIGNING_TIMESTAMP_URL -ErrorAction SilentlyContinue
}
function Verify-Signature([string]$path) {
    & $signTool verify /pa /v $path
    if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed: $path" }
    $signature = Get-AuthenticodeSignature $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'COSHIAN') { throw "Signer identity is not COSHIAN: $path" }
    if (-not $signature.TimeStamperCertificate) { throw "RFC 3161 timestamp missing: $path" }
}
Verify-Signature $final
$fileInfo = (Get-Item $final).VersionInfo
if ($fileInfo.FileVersion -ne '0.3.1.1' -or $fileInfo.ProductVersion -ne $version) { throw 'PE version metadata mismatch.' }
New-Item -ItemType Directory -Path release-assets, release-package -Force | Out-Null
Copy-Item $final release-assets/WinRebuilder-portable.exe
Verify-Signature 'release-assets/WinRebuilder-portable.exe'
if ((Get-FileHash $final -Algorithm SHA256).Hash -ne (Get-FileHash release-assets/WinRebuilder-portable.exe -Algorithm SHA256).Hash) { throw 'Portable copy differs from signed executable.' }
Copy-Item $final release-package/WinRebuilder.exe
Verify-Signature 'release-package/WinRebuilder.exe'
Copy-Item README.md, config.yml, config.example.yml release-package/
Compress-Archive -Path release-package/* -DestinationPath release-assets/WinRebuilder.zip -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path release-assets/WinRebuilder.zip).Path)
try {
    $expected = @('README.md', 'WinRebuilder.exe', 'config.yml', 'config.example.yml') | Sort-Object
    $actual = @($zip.Entries | ForEach-Object FullName | Sort-Object)
    if (($actual -join '|') -ne ($expected -join '|')) { throw "Unexpected ZIP manifest: $($actual -join ', ')" }
    foreach ($entry in $zip.Entries) { if ($entry.Length -le 0) { throw "Empty ZIP entry: $($entry.FullName)" }; Write-Host "$($entry.FullName): $($entry.Length) bytes" }
}
finally { $zip.Dispose() }
$assetNames = @(Get-ChildItem release-assets -File | Select-Object -ExpandProperty Name | Sort-Object)
if ($assetNames.Count -ne 2 -or $assetNames -notcontains 'WinRebuilder-portable.exe' -or $assetNames -notcontains 'WinRebuilder.zip') { throw 'Unexpected release assets.' }
