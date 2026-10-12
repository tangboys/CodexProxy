$ErrorActionPreference = 'Stop'
if ($args.Count -gt 0 -and $args[0] -in @('desktop', 'panel')) {
    $desktopCandidates = @()
    $desktopPathFile = Join-Path $PSScriptRoot 'desktop-path.txt'
    if (Test-Path -LiteralPath $desktopPathFile) {
        $desktopCandidates += (Get-Content -LiteralPath $desktopPathFile -Raw).Trim()
    }
    $desktopCandidates += @((Join-Path $PSScriptRoot 'desktop\CodexProxy.exe'),
                          (Join-Path $PSScriptRoot '..\..\CodexProxy.exe'))
    $desktop = $desktopCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $desktop) { throw 'CodexProxy.exe not found. Extract the desktop package and put its absolute EXE path in desktop-path.txt beside this script.' }
    Start-Process -FilePath $desktop -WindowStyle Hidden
    exit 0
}
$scriptFile = Join-Path $PSScriptRoot 'scripts\codex_proxy.py'
$candidates = @()
$pyCommand = Get-Command py -ErrorAction SilentlyContinue
if ($pyCommand) { $candidates += ,@($pyCommand.Source, '-3') }
$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
if ($pythonCommand -and $pythonCommand.Source -notlike '*\WindowsApps\*') {
    $candidates += ,@($pythonCommand.Source)
}
$bundled = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
if (Test-Path -LiteralPath $bundled) { $candidates += ,@($bundled) }
foreach ($candidate in $candidates) {
    $pythonFile = $candidate[0]
    $prefix = @($candidate | Select-Object -Skip 1)
    & $pythonFile @prefix -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' 2>$null
    if ($LASTEXITCODE -eq 0) {
        & $pythonFile @prefix $scriptFile @args
        exit $LASTEXITCODE
    }
}
throw 'Python 3.10+ is required. Install Python or use the Codex bundled Python runtime.'
