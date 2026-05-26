# WaveeMusic v0.1.0-beta — beta tester guide

Welcome to the WaveeMusic public beta. This document covers system
requirements, installation, what works, what's coming, and how to file a
useful bug report.

---

## System requirements

| | |
|---|---|
| **OS** | Windows 11 version 24H2 (build **26100**) or later. Earlier Windows 11 / Windows 10 are unsupported because the on-device AI features depend on the 24H2 Windows AI runtime. |
| **Architecture** | x64 or ARM64 (both shipped). The audio host runs as x64 even on ARM64 Windows (under built-in x64 emulation). |
| **Spotify account** | **Spotify Premium is required.** Wavee shows a non-dismissible banner if you sign in with a Free account; playback won't work. |
| **Network** | Wavee is online-only. Cached metadata helps Library / Home load faster, but starting playback or syncing the library needs a live connection. |
| **AI features (optional)** | Copilot+ PC (Snapdragon X, Intel Core Ultra Series 2, AMD Ryzen AI 300+). On other machines the AI affordances stay hidden. Region-gated (not available in China). Master toggle in Settings → On-device AI is **off** by default — flip it on if you want lyrics explanations. |

---

## Installing the beta

WaveeMusic ships as a signed **MSIX** package from GitHub Releases. There
is no Microsoft Store listing yet.

1. Download the matching MSIX from the latest release on
   [GitHub Releases](https://github.com/christosk92/WaveeMusic/releases) —
   pick `WaveeMusic-{version}-x64.msix` or `-arm64.msix` for your machine.
2. Double-click the MSIX. Windows App Installer opens. If it warns about
   the publisher being unknown, that's the normal SmartScreen warning for a
   newly-signed installer — see the [SmartScreen note](#smartscreen-warning)
   below.
3. Click **Install**. The app appears in the Start menu as **Wavee**.
4. On first launch you'll be sent to Spotify's OAuth page in your default
   browser. After granting access you're returned to the app. Credentials
   are encrypted on disk via Windows DPAPI — you only sign in once.

### SmartScreen warning

A freshly-signed installer earns Microsoft Defender SmartScreen reputation
over the first few weeks of distribution. Until then you may see a
"Windows protected your PC" dialog. Click **More info** → **Run anyway**.
We're not signing with a different identity to reset reputation each
release — we keep distributing the same signed package so reputation
accumulates against the production publisher.

### Updates

Two paths:

1. **One-click auto-update via `.appinstaller`** (recommended). Each
   release ships a `Wavee.appinstaller` file alongside the MSIX. Install
   `Wavee.appinstaller` once; Windows then checks the GitHub Releases page
   every 24 hours on launch and pulls any newer signed MSIX automatically.
2. **Manual MSIX download**. If you prefer to control your update cadence,
   download the matching `WaveeMusic-{version}-x64.msix` and double-click
   to install. The app's built-in update checker still notifies you on
   launch when a newer version is available.

---

## What's included in v0.1.0-beta

See [CHANGELOG.md](CHANGELOG.md) for the full feature list. Highlights:

- Home / Browse / Search / Library / Artist / Album / Playlist / Show /
  Episode pages.
- Spotify Connect (controller + target).
- Out-of-process audio engine with 10-band EQ, normalization, crossfade.
- Lyrics with shader effects and CJK romanization.
- Music videos via WebView2 EME.
- On-device AI (Phi Silica) on Copilot+ PCs — opt-in.
- System Media Transport Controls — lock screen, hardware media keys.
- Playlist mutations (create / add / remove / reorder / rename / cover).
- Shell-level offline + Premium-required banners.
- Crash report packager in Settings → About.

---

## Known limitations

Intentional gaps in this build — please don't file separate issues unless
you have specific UX feedback to add.

- **Local media library** — code is in the tree behind the
  `WAVEE_ENABLE_LOCAL_FILES` env-var feature flag for internal testing,
  but is intentionally not exposed in this build.
- **Localization beyond English** — `en-US` and `ko-KR` resource files
  exist; most UI strings are currently English-only.

---

## Filing a useful bug report

The best way: **Settings → About → "Report an issue on GitHub"**. The
button packages your most recent crash log + app logs into a zip, opens
File Explorer with the zip pre-selected so you can drag it onto the
issue form, and launches the GitHub Issues new-issue page with version +
OS info pre-filled.

Things that help us reproduce:

1. **What you were doing** when it happened — page name, track URI if
   you remember it, last few actions.
2. **What you expected** vs. **what happened**.
3. **The zip from the Report on GitHub button** dropped onto the issue.
4. Whether the bug is reproducible — every time, sometimes, once.

Things to redact before attaching: your Spotify username appears in some
log lines (the redactor catches passwords, tokens, and IDs but not the
username string). Replace it with `<username>` if you want.

---

## Privacy

Wavee is a third-party Spotify client. To make Recently Played, play
counts, and recommendations work, it sends the same minimum set of
playback events Spotify's official client sends — track URI, context
URI, ms played, reason started / ended, audio format. Nothing else.

- No third-party analytics.
- No Wavee-operated server. There isn't one to leak data to.
- No remote crash reporting. Crash logs stay on your machine until you
  explicitly send them via the GitHub flow above.
- Your Spotify credentials are encrypted on disk via Windows DPAPI —
  bound to your Windows user account.
- AI features run entirely on-device via Phi Silica on Copilot+ PCs;
  no lyric text, prompt, or model output is sent anywhere.

See Settings → About → Privacy for the full statement.

---

## License

WaveeMusic is MIT-licensed open source. See [LICENSE](LICENSE).

Third-party components (BASS, NVorbis, Microsoft.Windows.AI, etc.) carry
their own licenses; the full list is in Settings → About → Third-party
notices.
