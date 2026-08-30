# Wavee

A Spotify desktop client for Windows 11, built on [FluentGpu](https://github.com/christosk92/fluent-gpu) — a
from-scratch, NativeAOT, GPU-rendered UI engine for .NET 10. One ~30 MB MSIX, no bundled runtime, Mica, dynamic
accent, 10k-row virtualized lists, synced lyrics, video, out-of-process playback modules (YouTube / Twitch / radio).

> Wavee is an independent client for your own Spotify **Premium** account. It is not made by, endorsed by, or
> affiliated with Spotify AB. Privacy statement: [PRIVACY.md](PRIVACY.md).

## Install

Download the `.appinstaller` for your architecture from the rolling
[`wavee-stable`](https://github.com/christosk92/WaveeMusic/releases/tag/wavee-stable) release (arm64 or x64) and
open it. Updates are checked in the app (Settings › About) and silently by App Installer on launch. Release notes:
[CHANGELOG.md](CHANGELOG.md) and the **What's new** page inside the app.

## Build

The engine is a **sibling checkout**, not a package:

```powershell
git clone https://github.com/christosk92/fluent-gpu C:\wavee\fluent-gpu
git clone https://github.com/christosk92/WaveeMusic C:\wavee\WaveeMusic
cd C:\wavee\WaveeMusic
git config core.hooksPath .githooks
dotnet build Wavee.slnx -c Release
dotnet run --project src/apps/Wavee              # or:  -- --fake  for the offline demo data
```

Requirements: .NET SDK 10+ (`global.json` rolls forward), Visual Studio Build Tools with MSVC (NativeAOT link),
Windows 11. The Spotify playback derivation (`src/apps/Wavee.PlayPlay`) lives in a private repository and is
junctioned in per checkout — a checkout without it builds the public-only variant.

## Repository layout

| Path | What |
|---|---|
| `src/apps/Wavee` | the app (composition root, features, backend, platform) |
| `src/apps/Wavee.Core` | engine-free app logic (release notes, versioning, update policy, notifications) |
| `src/apps/Wavee.Sdk`, `src/apps/modules/*` | the playback-module SDK and the first-party modules |
| `src/apps/Wavee.Tests` | xUnit tests (pure logic — never source-text tests) |
| `src/apps/Wavee.ReleaseTool` | validates/renders release notes for a release |
| `ops/build`, `ops/release` | MSIX packaging, signing, the local release runbook and its E2E harness |
| `docs/guide`, `docs/plans` | app guides (releasing, playback modules, sidebar platform) and plans |

Releasing: [`docs/guide/releasing-wavee.md`](docs/guide/releasing-wavee.md). Agent guidance: `CLAUDE.md`, `AGENTS.md`.

## License

MIT — see [LICENSE](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
