# Wavee — Microsoft Store listing copy (en-US)

Paste into Partner Center → Store listings → English (United States). Keep the first sentence of the description
as is: the not-affiliated line is what keeps the listing on the right side of policy 11.2.

## Product name

Wavee

## Short description (≤ 100 characters is what the Store card shows)

A native, GPU-rendered Spotify client for Windows 11 — for your own Premium account.

## Description

Wavee is a third-party Spotify client for Windows 11, built for people who want the desktop app Windows deserves. It
plays music from your own Spotify Premium account. Wavee is not made by, endorsed by, or affiliated with Spotify AB.

Fast by construction. Wavee runs on FluentGpu, a GPU-rendered UI engine built from scratch for Windows: the app
opens in well under a second, scrolls a 10,000-row library without a hitch, and holds the full refresh rate of your
display while you navigate. Mica, dynamic accent, light and dark — it looks and moves like part of Windows 11.

What you get
• Home, Browse, Search and Charts as you know them, in a native window with tabs.
• Artist pages with hero art, top tracks, discography and live lyrics beside the player.
• Albums with credits, related music and the official video docked next to the queue.
• Playlists that show their data: tempo, key, added-by, recommendations, and a resizable rail of facts.
• A three-pane library for artists, albums and playlists; Liked Songs with weekly stats and tempo curves.
• Recents grouped by day, with every play listed.
• Queue, autoplay and drag-reorder; Spotify Connect to and from your other devices.
• Synced lyrics (syllable-level where available), concerts near you, podcasts and audiobooks.
• Out-of-process playback modules for YouTube, Twitch and radio.
• One ~30 MB package, no runtime to install, updates through the Store.

Requirements
• Windows 11 (x64 or ARM64).
• A Spotify Premium account. Free accounts cannot sign in — Spotify's playback terms require Premium.

Privacy
Wavee talks to Spotify with your own credentials and keeps its cache on your device. It contains no ads and no
tracking. Privacy statement: https://cproducts.dev/privacy

Open source under the MIT licence: https://github.com/christosk92/WaveeMusic

## What's new in this version (submission field)

See the in-app What's new page and CHANGELOG.md for the itemised list — paste the [0.2.1] "Fixed" bullets here,
trimmed to the user-facing ones (tabs, search box, queue reorder, Liked Songs sync, icons on Windows 10, crash fix).

## Search terms (≤ 7)

spotify client, music player, spotify, lyrics, music, playlists, podcasts

## Category / properties

Category: Music. Privacy policy: https://cproducts.dev/privacy. Website: https://cproducts.dev.
Support: https://cproducts.dev/contact (hello@cproducts.dev). "This app accesses the internet": yes.

## Screenshots (0.3.9 set: 3840×2160 PNG, no headline on the image)

Shot from a verify Wavee on the real account by `ops/release/tools/Capture-StoreSet.ps1` (the three themes, the mini player,
the trailer's clips) and composed on an ambient field from each picture's own colours. Microsoft: "Don't add additional
logos, icons, or marketing messages to your screenshots", so the selling sentence is the caption. The compositor (a
MotionKit-style scene kit) lived in `C:\WAVEE\scratch\store26\kit` for this round; the delivered set is in
`C:\WAVEE\scratch\store26\deliver`.

| # | File | Caption (≤ 200 chars) |
|---|---|---|
| 1 | 01-hero.png | Wavee is a fast, native Windows app for your own Spotify Premium account: home, library, lyrics and a mini player that stays on top, in Windows 11's own look. |
| 2 | 02-lyrics.png | Lyrics that follow every word, full screen or beside the player. When a song has no word timing, Wavee can make it on your PC's NPU. |
| 3 | 03-themes.png | Three looks, one app: Wavee Classic, WinUI and a Zune-inspired theme with big type and pivots. Switch any time in Settings. |
| 4 | 04-zune-album.png | Albums with the people behind them: who wrote, produced and played on every record, and the music videos that go with it. |
| 5 | 05-mini.png | A mini player that stays on top in any shape you drag it to: cover, lyrics, Canvas, a visualizer or a turntable, in your theme. |
| 6 | 06-visualizer.png | Nineteen visualizers, tinted from the album art, with the lyrics running underneath. |
| 7 | 07-video.png | Music videos beside the queue, in a mini player or full screen, and Theatre mode with the artist, lyrics and Up next. |
| 8 | 08-winui-artist.png | Artist pages with their best songs, releases and videos, and Up next always at hand. |
| 9 | 09-zune-home.png | Pivots across the top, your pins beside them, and home built from what you play. |

Upload: the release run takes them. `wavee-store-submit.ps1 -ScreenshotDir <folder of NN-name.png + NN-name.caption.txt>`
replaces every en-US screenshot in the same zip as the package. The trailer and the super hero art are not in that script:
add them in Partner Center (or extend the script) with the release.

Trailer: `wavee-0.3.9-trailer.mp4` (38.7 s, 1920×1080, 60 fps, H.264 High, closed GOP, AAC 48 kHz stereo; real footage,
whip cuts, a 3D theme carousel, measured page times in real time, soft UI sounds made from math) +
`wavee-0.3.9-trailer-thumb.png`. Title: "Wavee 0.3.9". It shows at the top only with a 16:9 super hero art uploaded.

Store logos: `tile-300.png` (1:1 app tile), super hero `super-hero-3840x2160.png` (16:9, no text, no UI; replaces
`hero-1920x1080.png`).

## Notes for certification (Submission options)

Packaged desktop application (runFullTrust). Third-party Spotify client that streams the signed-in user's own
Spotify Premium content; no purchases, no ads. Sign in requires a Spotify Premium account — test account:
<email> / <password> (a free account shows the "Premium required" sign-in error by design). Privacy policy:
https://cproducts.dev/privacy. Restricted capabilities: runFullTrust only.
