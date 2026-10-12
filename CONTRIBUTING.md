# Contributing

Small, focused issues and pull requests are welcome. Discuss larger behavior changes before implementing them.

## Useful reports

Include the app version, Windows x64 environment, stopped/running state, minimal reproduction, expected behavior, and actual result. Use synthetic subscription data. Redact credentials, URL tokens, account details, and raw core logs. Security-sensitive reports should follow [SECURITY.md](SECURITY.md).

## Code changes

- Keep proxy settings limited to the launched application. Do not introduce system proxy/TUN changes, startup registration, silent installs, or credential collection as incidental behavior.
- Preserve cancellation, process ownership, subscription-specific caches, and failure recovery.
- Keep both Chinese and English resources current. UI refreshes must preserve unsaved edits.
- Keep source changes narrow. Include meaningful validation evidence and state what was not run.
- Do not commit `dist`, `archive`, `bin`/`obj`, user state, real subscriptions, node secrets, or machine-specific skill paths.

See [BUILDING.md](docs/BUILDING.md) for compilation. Integration tests can start cores and fixture processes; running them is a separate deliberate action requiring isolated state. CI compilation is not a runtime test. The final real Codex message check remains manual and last.

## License and third-party material

By contributing project-owned changes, use the repository's **GPL-3.0-only** license. Retain existing copyright and third-party notices; do not change upstream license text. New dependencies must have documented provenance, license, and any required source/distribution materials.

The included local icon is project-owned. Do not submit OpenAI branding or external images without appropriate rights. This project is unofficial.
