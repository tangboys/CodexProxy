<img src="src/CodexProxy/Assets/app.svg" width="64" height="64" alt="Codex Proxy icon">

# Codex Proxy

[简体中文](README.md) · [Releases](https://github.com/tangboys/CodexProxy/releases) · [Build](docs/BUILDING.md) · [Validation](TEST-RESULTS.md)

A C# / WPF tool that launches Codex Desktop with a dedicated proxy on Windows x64. It provides multiple saved subscriptions with one active selection, node selection and testing, subscription usage, live traffic, and Chinese/English UI.

This is an unofficial community project, with no affiliation to OpenAI, v2rayN, Mihomo, or Clash Verge Rev. Install the official Codex Desktop separately. Codex, accounts, and proxy subscriptions are not included.

## Get it

Get the **0.11.0 preview** from [Releases](https://github.com/tangboys/CodexProxy/releases). `CodexProxy.exe` runs directly with the .NET runtime and Mihomo embedded. Licenses, notices, and corresponding source are provided with the Release and in this repository. The preview has not received runtime validation.

No product ZIP or installer is provided for this version. Follow [BUILDING.md](docs/BUILDING.md) to produce `dist/0.11.0/CodexProxy.exe` yourself. Future portable releases should use a complete `CodexProxy-<version>-win-x64.zip`; redistribution must keep the license and corresponding-source materials available. GitHub's source download is not a runnable application.

## Quick start

1. Fully exit the older Codex Proxy, then open `CodexProxy.exe`. The dedicated proxy starts **stopped** and Codex is not launched automatically. Closing the window hides it in the tray; use **Exit app** to quit.
2. Open **Subscriptions** in **Settings → General** and add a name and HTTP(S) URL. **Save** keeps a backup; **Save and enable** selects it. Only one subscription is active at a time; nodes are not merged. Saved URLs can be edited or hidden.
3. Browse cached nodes, last availability/latency, and test time on **Nodes**. Select a node or Auto for the next start. Untested or previously unavailable nodes may be preselected; this does not establish their availability.
4. Use **Test nodes** or **Refresh nodes** on **Nodes**, then **Start proxy** on **Overview**. Testing/import while stopped uses a short-lived isolated core and leaves the main proxy stopped. Starting applies the saved selection. **Check Codex** currently requires the main proxy to be running.
5. **Test real Codex last:** save your work and fully exit the existing Codex Desktop instance. Click **Launch Codex** and send a message to confirm your account and model connection. The app detects the desktop path or accepts a manually selected desktop EXE. An existing instance or a failed node check blocks launch.

**Stop proxy** stops the main core owned by this tool and keeps Codex and its local bridge available for a restart on the same ports. Existing streaming requests may need a retry. **Exit app** also closes the bridge but leaves Codex running. Other proxy applications are not terminated.

## Pages

- **Overview:** current node and latency, proxy/Codex status, subscription usage, proxy start/stop, and Launch Codex.
- **Nodes:** selection, Auto, node tests, Codex checks, and refresh.
- **Logs:** sampled destinations and routes, with a clear action.
- **Settings:** General contains language, status refresh interval, and Codex path. Core selects compatible Mihomo. Updates displays local/published versions, publication date, and comparison inline. The core is a proxy engine; no interception driver is installed.

Status refresh can be saved as 5, 10, 30, or 60 seconds; traffic and logs still sample every second. Tab navigation keeps subtle blue focus feedback without the default dotted frame.

## Nodes, subscription usage, and local traffic

- Node snapshots persist per subscription URL, with the last results and test time. Flags are inferred from node names and do not verify server geography.
- Core settings support bundled **Mihomo v1.19.31** or another compatible local Mihomo. Applying a core while stopped keeps it stopped; switching a running core can briefly interrupt connections. Clash GUI, Xray, and sing-box are not drop-in cores.
- Subscription usage comes from provider response headers and can be refreshed while stopped. A failed request keeps the last cache for the same URL and displays a warning; a successful response without valid fields displays unknown. An unknown or zero total does not mean unlimited.
- **Refresh usage** updates usage only. **Refresh nodes** imports nodes and separately retrieves usage. Usage loading does not depend on a successful node preview or a valid core.
- The lower-left traffic rate and totals cover this local core run, separately from subscription totals. The in-memory log samples destinations, ports, and node routes each second and retains 300 entries; short connections can be missed.
- Node latency is not model response time. Unauthenticated Codex checks cannot guarantee regional support, account permissions, or model calls. The WSL backend has not been validated.

## Proxy scope

Proxy environment variables and Chromium flags apply to the newly launched Codex process and children that honor them. HTTPS uses a local CONNECT tunnel without TLS decryption.

The tool does not change Windows system proxy, WinHTTP, routes, TUN, or global environment variables, and does not configure unrelated apps. It is not a process firewall. Child tools may implement networking differently; an external OAuth browser does not automatically inherit these settings. Existing system proxies and TUN remain controlled by their original applications.

## Privacy and updates

State defaults to `%LOCALAPPDATA%\codex-proxy`. Subscription URLs, node credentials, and local proxy secrets are stored as **local plaintext** with access restricted to the current user; they are not encrypted. Do not upload the state directory, raw core logs, or credential-bearing URLs. The UI connection log excludes request paths, query strings, and authentication headers.

Usage retrieval makes a direct GET to the subscription URL, reads response headers only, and uses the fixed UA `clash-verge/v2.5.9`. Standard and hyphen-prefixed `subscription-userinfo` headers support case-insensitive names and partial fields. Opening the app, changing subscriptions, or refreshing can trigger this request; cached data is not live data.

First opening **Settings → Updates** checks this repository's GitHub Releases asynchronously, including previews; a manual check is also available. Anonymous API limits are shared by public IP. Rate limits and network/service errors fall back to public `updates/latest.json` maintained after Release publication. The UI labels this source; CDN caching may delay it. Successful metadata is cached locally for 5 minutes. Manual checks bypass that cache but respect server retry times. If both sources fail, known metadata remains visible with its original read time and a failure status.

The page displays versions or opens a release page; it does not download, replace, or install software automatically. It sends no account password, Codex token, subscription URL, or GitHub token. App-update queries reuse the dedicated proxy while running and the system default proxy while stopped, without starting a core. **Check core update** still queries Mihomo's official API and can independently encounter rate limits; it does not use this project's manifest. The app does not configure startup at sign-in.

See [SECURITY.md](SECURITY.md) for data boundaries and reporting.

## Validation and contributing

See [TEST-RESULTS.md](TEST-RESULTS.md) for current compile/static checks; compilation is not runtime validation. Historical 0.2–0.4 isolated checks and 0.5 user feedback do not validate current versions. The real Codex message test remains step 5, performed last.

[Architecture](docs/ARCHITECTURE.md) · [Contributing](CONTRIBUTING.md) · [Changelog](CHANGELOG.md) · [Installer notes](installer/README.md)

## License

Project-owned source is licensed **GPL-3.0-only**; see [LICENSE](LICENSE). Copyright (c) 2026 Codex Proxy contributors. Third-party components retain their respective copyrights and licenses; see [THIRD-PARTY.md](THIRD-PARTY.md).
