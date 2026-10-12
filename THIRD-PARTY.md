# Third-party components

Project-owned source is **GPL-3.0-only**, Copyright (c) 2026 Codex Proxy contributors; see [LICENSE](LICENSE). This does not replace the licenses or copyrights of third-party components.

## Mihomo v1.19.31

| Item | Source / value |
| --- | --- |
| Upstream | [MetaCubeX/mihomo v1.19.31](https://github.com/MetaCubeX/mihomo/tree/v1.19.31) |
| License | GPL v3; preserve the upstream [LICENSE](https://github.com/MetaCubeX/mihomo/blob/v1.19.31/LICENSE) and bundled [full terms](licenses/mihomo-GPL-3.0.txt) |
| Official binary asset | [mihomo-windows-amd64-v1-v1.19.31.zip](https://github.com/MetaCubeX/mihomo/releases/download/v1.19.31/mihomo-windows-amd64-v1-v1.19.31.zip) |
| Asset SHA-256 | `D89C9BD746E8AACFF89B2EDF674813E25E8BD2DC565F4E12DC3B4526DD2B3177` |
| Extracted EXE SHA-256 | `9F2AD8968022EAE9B972C01A87DE1A4DC19C949834DB899D732BC0129B103478` |
| Corresponding version source | [licenses/mihomo-v1.19.31-source.zip](licenses/mihomo-v1.19.31-source.zip), upstream [tag archive](https://github.com/MetaCubeX/mihomo/archive/refs/tags/v1.19.31.zip) |
| Verified source archive SHA-256 | `EF491B55A920449C5AA46D5903BBB7007839A1FCF6BAAAC277016945CBC3E79E` |

The official release asset was downloaded and its SHA-256 matched GitHub's asset digest; the extracted executable hash is recorded above. It replaces the earlier development copy obtained from a local Verge installation. No machine-specific path is part of the public provenance.

The retained source archive's SHA-256 exactly matches the downloaded official v1.19.31 tag archive. The tag points to commit `ab405bad5beeeac8b003bb01f60f134f6df54471`. This verifies archive identity, not a byte-for-byte rebuild of the released executable.

Mihomo is embedded as an unmodified resource and extracted into the user's state directory before running as a separate process. Its source is not compiled into the C# assembly. Locked download details are maintained in `tools/mihomo.lock.json` and verified by `tools/fetch-mihomo.ps1`. A version/hash check is not a reproducible-build verification.

Keep the license, notices, and matching machine-readable corresponding source/build materials available with binary distribution. The source archive contains the upstream project and build files, including `Makefile`, `go.mod`, and `go.sum`. Preserve upstream notices and identify modifications if distributing a changed core. Embedding a binary does not remove its distribution obligations.

## .NET 9 / Windows Desktop Runtime

Microsoft and .NET contributors provide the runtime. MIT license and additional notices are retained in:

- [dotnet-LICENSE.txt](licenses/dotnet-LICENSE.txt)
- [windowsdesktop-LICENSE.txt](licenses/windowsdesktop-LICENSE.txt)
- [dotnet-THIRD-PARTY-NOTICES.txt](licenses/dotnet-THIRD-PARTY-NOTICES.txt)

The retained development runtime notices correspond to 9.0.19: [runtime](https://github.com/dotnet/runtime/tree/v9.0.19), [WPF](https://github.com/dotnet/wpf/tree/v9.0.19), and [WinForms](https://github.com/dotnet/winforms/tree/v9.0.19). Release builders must retain notices matching the actual SDK/runtime they publish. The runtime is not modified.

## Reference projects and names

[v2rayN](https://github.com/2dust/v2rayN) and [Clash Verge Rev](https://github.com/clash-verge-rev/clash-verge-rev) informed the separation of desktop UI, subscriptions, and an external core. Their GUI programs or application source are not bundled. Mihomo retains its own upstream license.

Codex Desktop is installed separately by the user. This distribution includes no Codex program, account, or subscription. OpenAI and the other upstream names are used to describe compatibility; this project has no official affiliation.

The local icon and project-authored assets belong to Codex Proxy contributors and follow the repository license.
