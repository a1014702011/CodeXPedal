# CodeXPedal

> If macOS says the software cannot be opened, open “System Settings → Privacy & Security”, scroll down to CodeXPedal, and click “Open Anyway”.

A lightweight Codex / Claude quota monitor with a native Windows taskbar/floating window and a native macOS menu bar/floating strip.

When both are installed, the taskbar, floating window, menu bar and strip show Codex and Claude side by side; when only one is installed, only that one is shown. The Claude Code installation is discovered automatically.

Shows actual five-hour and seven-day windows returned by the official service, not assumptions based on plan names. No monthly quota. HH:mm:ss is the last valid observation time; ≈ denotes an old snapshot.

## Download and requirements

[Releases](https://github.com/a1014702011/CodeXPedal/releases). The first release is under validation. Automation produces candidate packages and a draft Release; drafts are not public downloads. GitHub source ZIPs are not application packages.

| Platform | Package | Requirements |
|---|---|---|
| Windows 10/11 x64 | Portable ZIP; run app/CodeXPedal.exe after extracting everything | Bundled .NET runtime and native core |
| macOS 13+ Apple Silicon | arm64 ZIP / DMG | See each release for signing/notarization status |
| macOS 13+ Intel | x86_64 ZIP / DMG | See each release for signing/notarization status |

### First launch on macOS

If macOS says CodeXPedal cannot be opened, open “System Settings → Privacy & Security”, scroll down to CodeXPedal, click “Open Anyway”, and then restart the app.

Live quotas require an installed, signed-in official Codex CLI and network access. A CLI bundled with the official desktop application can be reused. CodeXPedal does not bundle the CLI, sign users in, or read credentials. Set `CODEXPEDAL_CODEX_PATH` when discovery fails; macOS also offers a path setting. An API key alone does not imply ChatGPT subscription quotas.

### Claude quotas

Claude quotas require **Claude Code (the CLI)** to be installed and signed in; the Claude desktop app alone is not enough. If CodeXPedal sees that you use Claude but Claude Code is missing or signed out, it asks once at startup. “Install and sign in” opens a visible terminal that runs the official installer (Windows: `irm https://claude.ai/install.ps1 | iex`; macOS: `curl -fsSL https://claude.ai/install.sh | bash`) and `claude auth login`; you only click “Authorize” in the browser. After sign-in, live reading turns on automatically. The same action is available in Settings, and “Don't ask again” suppresses the prompt. CodeXPedal never enters credentials or reads passwords.

Claude has no official quota command comparable to the Codex app-server, so CodeXPedal offers two sources. Bridge data is used automatically when live reading fails:

1. **Live reading (on by default)**: CodeXPedal reads the local Claude Code sign-in token (Windows: `~/.claude/.credentials.json`; macOS: Keychain item “Claude Code-credentials”, which prompts for permission; if you deny it, reading pauses without prompting again) and calls Anthropic's usage endpoint at most once a minute. The endpoint backs Claude Code's `/usage` but is undocumented and may change. The token is never stored or sent elsewhere and is never refreshed by CodeXPedal; open Claude Code once when it expires. You can turn this off in Settings.
2. **Claude status-line bridge (fallback)**: reads no credentials, for when you would rather not grant token access. Click “Enable Claude status-line bridge” in Settings, then run `claude` in a terminal and send a message (the desktop app may not update it); the quota appears on the next refresh. CodeXPedal points Claude Code's `statusLine` at itself and writes only the documented `rate_limits` five-hour/seven-day used percentage and reset time to `~/.claude/codexpedal-rate-limits.json`. Any existing status line is backed up and restored on disable. Pro/Max only, updated only after you send a message; values older than about 3 minutes show ≈. To avoid token access entirely, also turn off live reading.

Discovery order: `CODEXPEDAL_CLAUDE_PATH` → native install (`~/.local/bin`) → npm → WinGet / Homebrew → Claude desktop-managed folder → PATH. A CLI or an existing Claude config directory (`CLAUDE_CONFIG_DIR` supported) counts as installed. Use `python scripts/claude-quota-probe.py` (add `--oauth` for the live endpoint) to check paths and quotas on your machine.

End users do not need Rust, Python, Node, .NET SDK, or Xcode. Windows local-history fallback is not live; missing data is shown as `--`. The Mac client does not scan conversation content. Neither client uploads account or usage data.

## Features

- Window durations determine single/dual quotas; snapshots never mix times or buckets.
- Detects installed Codex / Claude; when both exist each gets its own row with its own sample time and ≈ marker.
- Windows: taskbar-only mode, compact/full floating window, details and settings.
- macOS: menu-bar-only mode, 20–42pt strip, details and settings.
- Shared timeout/error/age semantics; failed reads do not change observation time.
- Native platform UIs with one actual Rust business core through a versioned C ABI.

## Build

Windows: Rust 1.97.1, MSVC x64 tools, .NET 10 SDK, PowerShell 7.

```powershell
.\windows\scripts\build.ps1
.\windows\scripts\publish.ps1
```

Mac: Rust 1.97.1, Swift 5.9+ / Xcode Command Line Tools, Python 3.

```sh
make source-check
make test
make build
make verify
make probe
make run
```

[Shared core](docs/SHARED_CORE.md) · [Testing](docs/TESTING.md) · [Release gates](docs/RELEASE.md) · [Distribution](DISTRIBUTION.md) · [中文](README.md)

## Validation and license

CI builds/tests, native UI verification, live-account comparison, and signing/notarization are separate gates. Unexecuted checks remain NOT_RUN. An unavailable Mac mini is not a passed device test.

MIT. Required notices are retained in [attribution](docs/BRANDING.md) and [LICENSE](LICENSE). This is not an official OpenAI or Anthropic product.
