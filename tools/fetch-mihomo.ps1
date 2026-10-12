[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'mihomo.lock.json') -Raw | ConvertFrom-Json
$cache = Join-Path $PSScriptRoot ('.cache\mihomo\' + $lock.version)
$archive = Join-Path $cache $lock.asset
$extracted = Join-Path $cache 'extracted'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri $lock.url -OutFile $archive -Headers @{'User-Agent'='CodexProxy-Build'}
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $lock.sha256) {
    throw 'Mihomo archive checksum mismatch. Replace the cached archive with the official file before retrying.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $extracted -Force
$executables = @(Get-ChildItem -LiteralPath $extracted -Filter '*.exe' -File -Recurse)
if ($executables.Count -ne 1) { throw 'The official Mihomo archive must contain exactly one executable.' }
if ((Get-FileHash -LiteralPath $executables[0].FullName -Algorithm SHA256).Hash -ine $lock.executableSha256) {
    throw 'Extracted Mihomo executable checksum mismatch.'
}
Write-Output $executables[0].FullName
