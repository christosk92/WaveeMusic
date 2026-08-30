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

## Screenshots (artifacts/store/listing, 1920×1080 PNG, made by ops/release/tools/New-StoreScreenshot.py)

| # | File | Caption (≤ 200 chars) |
|---|---|---|
| 1 | 01-home.png | Home — your mixes, releases and shelves, rendered natively on Windows 11. |
| 2 | 02-artist.png | Artist pages with hero art, top tracks, discography and live lyrics. |
| 3 | 03-liked.png | Liked Songs with weekly stats, tempo curve and top artists in a resizable rail. |
| 4 | 04-playlist.png | Playlists show tempo, key, date added and recommendations. |
| 5 | 05-albums.png | A three-pane library: list, selection, tracks. |
| 6 | 06-recents.png | Recents grouped by day, every play listed. |
| 7 | 07-album.png | Albums with play counts, credits and related music. |
| 8 | 08-concerts.png | Concerts near you: live shows and festival dates, filtered by city and genre. |
| 9 | 09-customize.png | Your sidebar, your layout: templates, sections, density and pins. |
| 10 | 10-search.png | Search songs, artists, albums, playlists, podcasts and audiobooks at once. |
| 11 | 11-lyrics.png | Synced lyrics beside the player, syllable-level where available. |
| 12 | 12-video.png (to capture + frame overlay) | The official video docked next to the queue. |
| 13 | 13-queue.png | Queue, autoplay and drag-reorder. |

Capture at the size you want shown (`Capture-WaveeWindow.ps1 -KeepSize -Route …`, 1828×1066 today) and compose with `--side top` — at that width a side headline would crop the window. `01-home` takes no headline.

Store logos: `tile-300.png` (1:1 app tile), `hero-1920x1080.png` (16:9 super hero, no text, no UI).

## Notes for certification (Submission options)

Packaged desktop application (runFullTrust). Third-party Spotify client that streams the signed-in user's own
Spotify Premium content; no purchases, no ads. Sign in requires a Spotify Premium account — test account:
<email> / <password> (a free account shows the "Premium required" sign-in error by design). Privacy policy:
https://cproducts.dev/privacy. Restricted capabilities: runFullTrust only.
