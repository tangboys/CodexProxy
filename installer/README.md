# Installer preparation

No 0.11.1 installer is provided. The current preview release distributes an EXE and license/source materials; installation, upgrade, and uninstall behavior has not been run for 0.9–0.11. Historical 0.4 installer evidence remains in [TEST-RESULTS.md](../TEST-RESULTS.md).

The Inno Setup source is retained for a later packaged release. The current explicit manifest contains 19 product files, including the app, root LICENSE, Chinese/English docs, build/architecture guides, third-party notices/source, and `skills/codex-proxy`. Before compiling, confirm the manifest still matches the published directory. Do not include user state, credentials, PDBs, old artifacts, or machine-specific skill bindings.

## Intended installation scope

- Current-user installation under `%LOCALAPPDATA%\Programs\CodexProxy`.
- Chinese/English installer language, Start menu launch/uninstall entries, and an optional desktop shortcut.
- Separate app state under `%LOCALAPPDATA%\codex-proxy`, retained on uninstall.
- No system proxy, TUN driver, or startup registration.
- Ask the user to fully exit the manager before replacing/removing running product files; do not terminate Codex or unrelated proxy software.

The embedded core is extracted to user state at runtime; it is not a separate `bin/mihomo.exe` product file. Running-process checks and the file manifest must match the final layout before a new installer is distributed.

## Later build

From the repository root, after publishing and checking the current file manifest:

```powershell
.\build-installer.ps1 -ReleaseDirectory .\dist\0.11.1 -OutputDirectory .\dist\installers -ValidateOnly
.\build-installer.ps1 -ReleaseDirectory .\dist\0.11.1 -OutputDirectory .\dist\installers
```

Use an [official Inno Setup compiler](https://jrsoftware.org/isdl.php); `-CompilerPath` can specify `ISCC.exe`. Obtain and verify build tools separately. These commands describe later packaging; they have not been executed for this preview. Default input is `dist/<project-version>` and output is `dist/installers`. The expected installer naming convention is `CodexProxy-<version>-setup-win-x64.exe`.

Simplified Chinese installer translation credits remain in `Languages/ChineseSimplified.isl`.

## Manual acceptance for a future installer

1. Install with each language, check current-user destination, Start menu entries, and the unchecked/checked desktop-shortcut options.
2. Fully exit via **Exit app** before upgrading. Closing the window only hides it in the tray. Confirm the existing directory and saved subscription/language/node selection are retained.
3. With the installed manager running, attempt upgrade/uninstall. The installer should request exit rather than terminate Codex or unrelated processes.
4. Exit, uninstall, and confirm product files, shortcuts, and uninstall registration are removed while the user-state directory remains.
5. Reinstall and confirm retained state loads. Complete the main application's manual checks, with a real Codex message last.

These are expected behaviors to verify, not a claim that the current installer source or package has passed them.
