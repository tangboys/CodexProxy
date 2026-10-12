param(
    [string]$CorePath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\CodexProxy\CodexProxy.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$version = $projectXml.SelectSingleNode('//PropertyGroup/Version').InnerText
$output = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $PSScriptRoot ('dist\' + $version) }
if (-not $CorePath) { $CorePath = & (Join-Path $PSScriptRoot 'tools\fetch-mihomo.ps1') }
$bundledCore = (Resolve-Path -LiteralPath $CorePath).Path
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:BundledCorePath=$bundledCore" -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.en.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TEST-RESULTS.md') -Destination $output
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'licenses')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $output -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $output -Recurse -Force
$skillDirectory = Join-Path $output 'skills\codex-proxy'
foreach ($relativeFile in @('SKILL.md', 'codex-proxy.ps1', 'agents\openai.yaml', 'scripts\codex_proxy.py', 'scripts\test_codex_proxy.py', 'references\usage.md')) {
    $destination = Join-Path $skillDirectory $relativeFile
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('skills\codex-proxy\' + $relativeFile)) -Destination $destination -Force
}
