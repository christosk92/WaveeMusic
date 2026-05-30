# WaveeMusic v0.1.0-alpha.1 - alpha tester guide

Welcome to the WaveeMusic experimental alpha. This document covers system
requirements, installation, known limitations, and how to file a useful bug
report.

## System requirements

| | |
|---|---|
| **OS** | Windows 11 version 24H2 (build **26100**) or later. Earlier Windows 11 / Windows 10 are unsupported because the on-device AI features depend on the 24H2 Windows AI runtime. |
| **Architecture** | x64 or ARM64. The audio host runs as x64 even on ARM64 Windows under built-in x64 emulation. |
| **Spotify account** | **Spotify Premium is required.** Wavee shows a non-dismissible banner if you sign in with a Free account; playback will not work. |
| **Network** | Wavee is online-only. Cached metadata helps Library and Home load faster, but starting playback or syncing the library needs a live connection. |
| **AI features (optional)** | Copilot+ PC. On other machines the AI affordances stay hidden. Region-gated, not available in China. The master toggle in Settings -> On-device AI is off by default. |

## Installing the alpha

WaveeMusic ships as a signed **MSIX** package from GitHub Releases. There is
no Microsoft Store listing yet.

1. Download the matching MSIX from the alpha release on
   [GitHub Releases](https://github.com/christosk92/WaveeMusic/releases):
   `Wavee.UI.WinUI_0.1.0.1001_x64.msix` or the ARM64 package.
2. Double-click the MSIX. Windows App Installer opens. A fresh publisher may
   still show a SmartScreen warning; choose **More info** -> **Run anyway** if
   you trust this build.
3. Click **Install**. The app appears in the Start menu as **Wavee (Alpha)**.
4. On first launch you will be sent to Spotify's OAuth page in your default
   browser. After granting access, you are returned to the app. Credentials are
   encrypted on disk via Windows DPAPI.

## Updates

Alpha builds are pre-release builds. The MSIX package version for
`v0.1.0-alpha.1` is `0.1.0.1001`; later alpha, beta, and stable packages use a
higher numeric MSIX version so Windows can update forward.

Every experimental release now ships `.appinstaller` auto-update manifests, so
there are two ways to stay current:

- **Automatic (recommended):** install from `Wavee.Experimental.<arch>.appinstaller`
  (attached to each release and to the rolling
  [`experimental-latest`](https://github.com/christosk92/WaveeMusic/releases/tag/experimental-latest)
  release). Windows App Installer then silently checks for, downloads, and stages
  newer builds in the background (no prompt) and applies them on the next restart;
  **Settings → About** shows a "restart to apply" nudge once a build is staged.
- **Manual:** download the newer `Wavee.UI.WinUI_<version>_<arch>.msix` and install
  it over the old one. Package versions only increase, so Windows updates forward.

## What's included

See [CHANGELOG.md](CHANGELOG.md) for the full feature list. Highlights:

- Home / Browse / Search / Library / Artist / Album / Playlist / Show /
  Episode pages.
- Spotify Connect as both controller and target.
- Out-of-process audio engine with EQ, normalization, and crossfade.
- Lyrics with shader effects and CJK romanization.
- Music videos via WebView2 EME.
- On-device AI on Copilot+ PCs, opt-in.
- System Media Transport Controls.
- Playlist mutations and drag/drop.
- Shell-level offline and Premium-required banners.
- Crash report packager in Settings -> About.

## Known limitations

- **Local media library** - code is in the tree behind the
  `WAVEE_ENABLE_LOCAL_FILES` env-var feature flag for internal testing, but is
  intentionally not exposed in this build.
- **Localization beyond English** - `en-US` and `ko-KR` resource files exist;
  most UI strings are currently English-only.
- **Alpha stability** - this is an experimental build. Please include logs when
  reporting crashes, startup issues, playback failures, or memory growth.

## Filing a useful bug report

The best path is **Settings -> About -> Report an issue on GitHub**. The button
packages recent crash logs and app logs into a zip, opens File Explorer with the
zip selected, and launches the GitHub issue page with version and OS info
pre-filled.

Useful reports include:

1. What you were doing when it happened: page name, track URI if you remember
   it, and the last few actions.
2. What you expected vs. what happened.
3. The zip from the Report on GitHub button.
4. Whether the bug reproduces every time, sometimes, or only once.

Redact your Spotify username from log lines if you do not want to share it.
The redactor catches passwords, tokens, and IDs, but not every username string.

## Privacy

Wavee is a third-party Spotify client. To make Recently Played, play counts,
and recommendations work, it sends the same minimum playback event fields the
official client sends: track URI, context URI, milliseconds played, reason
started / ended, and audio format.

- No third-party analytics.
- No Wavee-operated crash reporting server.
- Crash logs stay on your machine until you explicitly attach them to an issue.
- Spotify credentials are encrypted on disk via Windows DPAPI.
- AI features run entirely on-device via Phi Silica on Copilot+ PCs.

## License

WaveeMusic is MIT-licensed open source. See [LICENSE](LICENSE).
