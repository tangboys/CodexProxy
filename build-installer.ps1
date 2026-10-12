[CmdletBinding()]
param(
    [string]$CompilerPath,
    [string]$ReleaseDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist\installers'),
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\CodexProxy\CodexProxy.csproj') -Raw)
$version = $project.SelectSingleNode('//PropertyGroup/Version').InnerText
if (-not $ReleaseDirectory) { $ReleaseDirectory = Join-Path $PSScriptRoot ('dist\' + $version) }
$ReleaseDirectory = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$manifest = Join-Path $PSScriptRoot 'installer\files.iss'
$files = foreach ($line in Get-Content -LiteralPath $manifest) {
    if ($line -match '^Source: "\{#ReleaseDir\}\\([^"]+)";') { $Matches[1] }
}
if ($files.Count -ne 19) { throw 'The installer product file manifest must contain 19 explicit files.' }
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath (Join-Path $ReleaseDirectory $file) -PathType Leaf)) {
        throw "Missing product file: $file. Run build.ps1 and refresh the bundled skill first."
    }
}
$icon = Join-Path $PSScriptRoot 'src\CodexProxy\Assets\app.ico'
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw 'Missing app.ico.' }
$productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $ReleaseDirectory 'CodexProxy.exe')).ProductVersion
if ($productVersion.Split('+')[0] -ne $version) { throw "Publish version $version before building its installer." }
if ($ValidateOnly) {
    Write-Output "Validated $($files.Count) installer files for Codex Proxy $version."
    return
}
if (-not $CompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $candidates = @(
        (Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\InnoSetup6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    if ($command) { $candidates += $command.Source }
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Inno Setup ISCC.exe was not found. Download from https://jrsoftware.org/isdl.php and pass -CompilerPath. See installer/README.md for portable setup.'
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
& $CompilerPath /Qp "/DAppVersion=$version" "/DReleaseDir=$ReleaseDirectory" "/O$OutputDirectory" (Join-Path $PSScriptRoot 'installer\CodexProxy.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$outputFile = Join-Path $OutputDirectory "CodexProxy-$version-setup-win-x64.exe"
Get-Item -LiteralPath $outputFile | Select-Object FullName, Length
Get-FileHash -LiteralPath $outputFile -Algorithm SHA256
