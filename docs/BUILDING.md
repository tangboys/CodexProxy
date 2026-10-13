# Building Codex Proxy

The source targets .NET 9 and WPF on Windows x64. Install a .NET 9 SDK and PowerShell. Published builds include the .NET runtime; users do not need a separate SDK or Python for the desktop app.

## Compile and publish

From the repository root:

```powershell
dotnet build .\src\CodexProxy\CodexProxy.csproj -c Release
.\build.ps1
```

The publish script targets `win-x64`, self-contained single-file output under `dist/<project-version>/`. For 0.12.0, the application is `dist/0.12.0/CodexProxy.exe`. It includes Mihomo as an embedded resource and copies documentation, licenses, and `skills/codex-proxy` alongside the app.

Without `-CorePath`, the script uses `tools/fetch-mihomo.ps1` and `tools/mihomo.lock.json` to retrieve and verify the fixed official Mihomo v1.19.31 asset. It does not silently select the latest upstream version. See [THIRD-PARTY.md](../THIRD-PARTY.md) for provenance and corresponding-source material.

To explicitly supply a compatible local Mihomo binary:

```powershell
.\build.ps1 -CorePath 'C:\path\to\mihomo.exe' -OutputDirectory .\dist\custom
```

An explicit binary is the builder's responsibility: retain its license, provenance, hash, and matching source. Do not label a custom binary as the verified bundled release. Existing build outputs and historical releases are local artifacts; they do not belong in source commits.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/CodexProxy` | WPF app, language resources, tray, and local icons |
| `src/CodexProxy.Core` | Core lifecycle, subscription/state handling, desktop launch, and probes |
| `tests/CodexProxy.Tests` | Existing integration runner and isolated fixtures |
| `skills/codex-proxy` | Codex skill entry and supporting scripts |
| `tools` | Icon generation and pinned Mihomo retrieval |
| `licenses` | Third-party licenses, notices, and corresponding Mihomo source |
| `installer` | Inno Setup source for a future installer |
| `updates/latest.json` | Public metadata fallback, maintained by the release-published workflow |
| `dist` / `archive` | Local current output / historical artifacts, not source |

## Validation boundaries

`dotnet build` and `dotnet publish` compile code; they do not establish successful proxying, Codex login, or model calls. The Windows CI publishes the app and compiles the integration-fixture project only. It does not run the product, integration tests, core, or installer, and does not automatically publish a Release.

The integration runner requires an installed Codex Desktop and an explicit `MIHOMO_CORE` path, and can start Mihomo and local fixture processes. Running it is a separate deliberate action. Use isolated state and test data, never committed real subscriptions or account credentials. Current manual acceptance and historical evidence are recorded in [TEST-RESULTS.md](../TEST-RESULTS.md); the real Codex message check is step 5 and remains last.

Release tags use `vMAJOR.MINOR.PATCH`; previews use GitHub's prerelease flag. Publishing a Release runs `.github/workflows/release-metadata.yml` to update the public fallback manifest on main. It uses only the ephemeral Actions token and requires permission to write repository contents. Verify that job succeeds; a failed job leaves the previous manifest available but stale. The build workflow does not publish Releases or execute this update itself.

If you deliberately want to run the existing integration runner after meeting those prerequisites:

```powershell
$env:MIHOMO_CORE = 'C:\path\to\mihomo.exe'
dotnet run --project .\tests\CodexProxy.Tests\CodexProxy.Tests.csproj -c Release
```

This command is documented for manual use and has not been executed for the preview.

## Optional later packaging

The 0.12 preview Release provides the raw EXE with license/source materials. No installer or product ZIP is produced. Future portable distribution should include the full published directory as `CodexProxy-<version>-win-x64.zip`, including licenses and corresponding-source material. Installer preparation is described in [installer/README.md](../installer/README.md).

For icon changes, edit `src/CodexProxy/Assets/app.svg` and the local generator `tools/generate-icon.ps1` consistently. Do not add generated runtime caches, credentials, or machine-specific skill bindings to Git.
