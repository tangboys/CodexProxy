# Security and privacy

## Local data

Codex Proxy stores state under `%LOCALAPPDATA%\codex-proxy` by default. Subscription URLs, node credentials, and local proxy/controller secrets are **plaintext**, protected by directory permissions rather than encryption. The state also contains configuration, cached nodes/usage, an extracted core, and a raw core log.

Do not attach this directory, full subscription links, or raw logs to issues. Redact URL paths/query tokens, credentials, private addresses, and any account information. Hashed cache filenames separate subscriptions; they do not encrypt the contents.

The UI connection history is in memory, limited to 300 sampled entries, and excludes HTTP paths, query strings, and authentication headers. Raw core logs are a separate file and may contain sensitive connection or provider details.

## Network behavior

- Subscription import downloads node data through Mihomo. Usage retrieval separately makes a direct GET with fixed `clash-verge/v2.5.9` UA and reads response headers without consuming the body. If the URL contains HTTP Basic credentials, these are used for that provider request. Use HTTPS for credential-bearing URLs.
- Node tests contact the configured test endpoint. Codex checks make unauthenticated requests to ChatGPT/OpenAI endpoints; they do not read Codex login files or send model requests.
- Manual app-update checks query public `tangboys/CodexProxy` GitHub Releases, including previews. Manual core-update checks query Mihomo's official releases. These requests do not include subscription URLs, Codex tokens, or account passwords. App-update queries use the dedicated authenticated proxy while running and the system default proxy while stopped, without starting a core.
- Version checks do not automatically download, replace, or install software. The app does not configure startup at sign-in or install a TUN driver.

## Scope

Proxy settings are applied to newly launched Codex processes and children that honor them. The app does not set Windows system proxy, WinHTTP, routes, global environment variables, or TUN. It does not intercept TLS, install a certificate, or terminate unrelated proxy processes.

Loopback listeners are not a process firewall. Code running under the same user may read local plaintext state or explicitly connect to local endpoints. Existing system-wide proxies and TUN are outside this app's control.

## Reporting

Use the repository's [Security page](https://github.com/tangboys/CodexProxy/security) if it offers a private vulnerability-reporting option. No private channel or response deadline is promised here. Otherwise, start an issue requesting contact without publishing exploit details or sensitive data; agree on an appropriate channel before sharing those details.

For ordinary bugs, include the app version, Windows version, whether the main proxy was stopped or running, a minimal redacted reproduction, and the observed result. Never include real subscriptions or account credentials. Only the current source is actively changed; historical binaries are not a support commitment.

Validation limits are documented in [TEST-RESULTS.md](TEST-RESULTS.md). A successful build or reachability probe does not establish secure operation, account eligibility, or successful model calls.
