<p align="center">
  <img src="assets/readme/promo/home.jpg" alt="Wavee — Home, rendered by FluentGpu on Windows 11" width="820" />
</p>

<h1 align="center">Wavee</h1>

<p align="center">
  A Spotify desktop client for Windows 11, built on <a href="https://github.com/christosk92/fluent-gpu">FluentGpu</a> —
  a from-scratch, NativeAOT, GPU-rendered UI engine for .NET 10.
</p>

<p align="center">
  <a href="https://github.com/christosk92/WaveeMusic/releases/tag/wavee-stable"><img src="https://img.shields.io/github/v/release/christosk92/WaveeMusic?filter=wavee-v*&label=release&color=0A6CC0" alt="Latest release" /></a>
  <a href="https://github.com/christosk92/WaveeMusic/releases"><img src="https://img.shields.io/github/downloads/christosk92/WaveeMusic/total?label=downloads&color=0A6CC0" alt="Downloads" /></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10" />
  <a href="https://github.com/christosk92/fluent-gpu"><img src="https://img.shields.io/badge/engine-FluentGpu-0078D4?logo=windows11&logoColor=white" alt="FluentGpu engine" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-2ea44f" alt="MIT License" /></a>
</p>

<p align="center">
  <a href="https://github.com/christosk92/WaveeMusic/releases/download/wavee-stable/Wavee.x64.appinstaller">
    <picture>
      <source media="(prefers-color-scheme: light)" srcset="assets/readme/DownloadInstaller-x64-dark.png" height="64" />
      <img src="assets/readme/DownloadInstaller-x64-light.png" height="64" alt="Download Wavee for x64" /></picture></a>
  &ensp;
  <a href="https://github.com/christosk92/WaveeMusic/releases/download/wavee-stable/Wavee.arm64.appinstaller">
    <picture>
      <source media="(prefers-color-scheme: light)" srcset="assets/readme/DownloadInstaller-arm64-dark.png" height="64" />
      <img src="assets/readme/DownloadInstaller-arm64-light.png" height="64" alt="Download Wavee for ARM64" /></picture></a>
  &ensp;
  <a href="https://apps.microsoft.com/detail/9NJPVWTQPT9H">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" height="64" alt="Get Wavee from the Microsoft Store" /></a>
</p>


One ~30 MB MSIX, no bundled runtime, Mica, dynamic accent, 10k-row virtualized lists, synced lyrics, video,
out-of-process playback modules (YouTube / Twitch / radio).

> Wavee is an independent client for your own Spotify **Premium** account. It is not made by, endorsed by, or
> affiliated with Spotify AB. Privacy statement: [PRIVACY.md](PRIVACY.md).

## A quick look

| | |
|---|---|
| ![Artist pages with live lyrics](assets/readme/promo/artist.jpg) | ![The official video — docked, mini player, or its own window](assets/readme/promo/video.jpg) |
| ![Liked Songs with weekly stats and tempo curves](assets/readme/promo/liked.jpg) | ![Queue, autoplay and Spotify Connect](assets/readme/promo/queue.jpg) |
| ![Playlists that show tempo, key and recommendations](assets/readme/promo/playlist.jpg) | ![Home on Windows 11 — Mica, dynamic accent, tabs](assets/readme/promo/home.jpg) |

## Install

Two ways in, same app:

- **Microsoft Store** — [apps.microsoft.com/detail/9NJPVWTQPT9H](https://apps.microsoft.com/detail/9NJPVWTQPT9H):
  install and update through the Store.
- **Direct download** — the buttons above; signed MSIX from this repository's releases, silent auto-updates.

Click the button for your architecture (x64 for most PCs, ARM64 for Snapdragon / Surface Pro X-class devices) and
open the downloaded `.appinstaller` — Windows App Installer installs the signed package. Updates are checked in the
app (Settings › About) and applied silently by App Installer on launch; the rolling
[`wavee-stable`](https://github.com/christosk92/WaveeMusic/releases/tag/wavee-stable) release is what every install
polls. Prefer a manual install? Every version's `.msix` is on its own
[release](https://github.com/christosk92/WaveeMusic/releases), next to its symbols. Release notes:
[CHANGELOG.md](CHANGELOG.md) and the **What's new** page inside the app.

Requires Windows 11 and a Spotify Premium account.

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
