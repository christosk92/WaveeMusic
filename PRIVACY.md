# Wavee privacy

Wavee is an independent Spotify desktop client. It is **not affiliated with, endorsed by, or sponsored by
Spotify AB**. Spotify and the Spotify logo are trademarks of Spotify AB.

Wavee has **no analytics, no crash-reporting service, no telemetry of its own, and no server**. The author
of Wavee receives nothing from your installation — not a ping, not an ID, not an error report.

## What stays on your machine

Everything Wavee stores lives under `%LOCALAPPDATA%\Wavee` and never leaves the device unless you copy it
out yourself:

| What | Where | Notes |
|---|---|---|
| Your Spotify credential | `store.json` | Protected at rest with Windows DPAPI (per-user, per-machine). It is the reusable credential Spotify issues at login — never your password. |
| Library database | `library.db` | Albums, artists, playlists, sync state, and the metadata cache. |
| Encrypted audio cache | `Wavee\Cache` (relocatable in Settings › Storage) | Encrypted CDN chunks and saved license keys, so you do not re-download tracks you already streamed. |
| Image cache | `cache\images` | Album art and other artwork, keyed by URL hash. |
| Logs | `logs\wavee.log` | Rolled at 10 MB, 7 files kept. Written to disk only, never uploaded. |
| Crash reports | written next to the logs on a fatal error | A local text file. Wavee shows you where it is; sending it anywhere is your choice. |
| Playback session & navigation history | `session.json` | So Wavee reopens where you left off. |
| Local play log | under `%LOCALAPPDATA%\Wavee` | Powers the local "recently played" surfaces. |
| Settings | Windows registry, `HKCU` | Typed keys only. |
| Dealer WebSocket archive | off by default | A debugging capture of Spotify's push frames, enabled only in Settings › Diagnostics. Local file. |

## What leaves your machine

Only these, and only to the parties named:

- **Spotify.** Everything you would expect a Spotify client to send: login, catalog and playlist requests,
  playlist edits, Connect device state, and audio streaming. This includes **play telemetry to Spotify's
  event receiver ("gabo")** — the same events the official client sends. This is not optional: it is what
  makes Recently Played, play counts, and your listening history work at all. If Wavee did not send it,
  your Spotify account would simply stop recording what you played.
- **Lyrics providers**, when a track has no Spotify lyrics and you have lyrics enabled. Wavee queries them
  with the track title, artist, and duration — not your account. The providers are: `lrclib.net`,
  the AMLL TTML database on `raw.githubusercontent.com`, `apic-desktop.musixmatch.com` (Musixmatch),
  `lyrics.kugou.com` (KuGou), `music.163.com` (NetEase), and `y.qq.com` / `c.y.qq.com` (QQ Music).
- **GitHub**, for update checks and release notes. Wavee fetches a static `.appinstaller` file from the
  `wavee-stable` release at `github.com/christosk92/WaveeMusic/releases/download/wavee-stable/`, reads the
  version number in it, and — when you choose to update — asks Windows to download and install that release's
  `.msix` package. It also fetches the "What's new" notes (and the public issue titles they reference) from the
  same repository. It sends no identifiers; GitHub sees ordinary anonymous file downloads (your IP and user
  agent, as with any download).
- **Spotify's image CDN**, to fetch album art.

Nothing else. In particular: no data goes to the author of Wavee, and there is no third-party analytics,
advertising, or crash SDK anywhere in the app.

## Your Spotify account

Your relationship with Spotify — including what Spotify does with the requests above — is governed by
[Spotify's own privacy policy](https://www.spotify.com/legal/privacy-policy/). Wavee is a client; it does
not change what Spotify collects.

## How to erase everything

**Settings › Storage › Factory reset** signs you out and permanently deletes all local Wavee data on the
PC — login, library, metadata, settings, caches, and history — and restarts Wavee on the first-launch
screen. The app itself stays installed. Individual caches can also be cleared on their own from the same
page, and **Settings › About › Open data folder** shows you exactly what is there.

## Contact

Questions or a privacy problem: open an issue at
<https://github.com/christosk92/WaveeMusic/issues>.
