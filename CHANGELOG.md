# Changelog

Changes describe implementation scope, not test results. See [TEST-RESULTS.md](TEST-RESULTS.md) for validation boundaries.

## 0.10.0 — Preview

- Prepare a public repository with Chinese/English documentation, build/architecture guides, GPL-3.0-only project licensing, and third-party provenance.
- Add a manual asynchronous app-update check for GitHub Releases, including preview versions. No automatic download, replacement, installation, or startup registration.
- Pin bundled Mihomo to the official v1.19.31 Windows amd64-v1 release, with locked download and executable hashes.
- Keep build output under `dist/<version>`, historical local artifacts under `archive`, and the reusable Codex skill under `skills/codex-proxy`.
- Provide compile-only Windows CI. Runtime product, GUI, core, installer, and real Codex checks remain unperformed for this preview.

## 0.9.0

- Allow stopped-mode cached node browsing, persisted manual/Auto preselection, and last latency/test time.
- Use temporary isolated cores for stopped node testing/import without starting the main proxy.
- Retrieve subscription usage directly from response headers, independently of node preview/core availability.
- Persist URL-specific usage caches; distinguish missing successful metadata from failed requests and cancel stale requests on subscription changes.
- Compile/static checks only; no runtime validation.

## 0.8.0

- Start the main proxy stopped and provide separate start/stop and Launch Codex actions.
- Show current-node latency and subscription usage separately from local traffic.
- Keep Codex and its loopback bridge when stopping the owned main core.

## 0.7.0

- Move long work off the UI thread and use nonmodal settings windows.
- Separate cancellable Codex checks from conflicting configuration changes.

## 0.6.0

- Add a light-blue theme, bundled Mihomo, and selection among compatible local cores.
- Add manual core-version checks without automatic replacement.

## 0.5.0 and earlier

0.5 added Chinese/English UI and related usability work. Historical 0.2–0.4 work covered desktop launch/bridge, node flags, traffic, subscription editing, and installer fixtures. Evidence and limits are retained in [TEST-RESULTS.md](TEST-RESULTS.md).
