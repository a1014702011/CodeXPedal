# CodeXPedal

A lightweight Codex quota monitor with a native Windows taskbar/floating window and a native macOS menu bar/floating strip.

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

End users do not need Rust, Python, Node, .NET SDK, or Xcode. Windows local-history fallback is not live; missing data is shown as `--`. The Mac client does not scan conversation content. Neither client uploads account or usage data.

## Features

- Window durations determine single/dual quotas; snapshots never mix times or buckets.
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

MIT. Required notices are retained in [attribution](docs/BRANDING.md) and [LICENSE](LICENSE). This is not an official OpenAI product.
