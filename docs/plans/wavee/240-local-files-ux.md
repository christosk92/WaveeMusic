# Local files (#240): the user experience, every case and flow

Owner request (2026-11-23): "beautiful insights, telemetry for the user and in general a great user experience, so
the user always sees what is happening and nothing just randomly appears". This file is the UX source of truth for
the local files work; the plan (`240-local-playlist-files-implementation.md`) owns the mechanics. "Telemetry" here
means **insights shown to the user about their own library**: everything is computed and kept on this PC, and
nothing in this file is sent anywhere.

## Principles

1. **Always say what is happening.** Any work the library does (scanning, reading tags, extracting covers, watching a
   drive) has a visible, quiet status. Work never happens silently and never blocks the app.
2. **Nothing appears or disappears without being announced.** New songs arrive in announced batches; a change while
   the user is looking at a list never moves the row under their pointer (a "4 new songs" pill instead); removals
   are shown as "missing", never silently dropped.
3. **Explain, then offer one action.** Every problem state says why, in plain words, and offers the one thing the
   user can do: Add a folder, Locate, Reconnect, Download, Rescan.
4. **Your library, at a glance.** An Insights page answers "what do I have, how good is it, what needs attention".
5. **Calm motion.** Rows enter with the theme's own list stagger; counters tick; nothing jumps (owner rule: reserve or
   animate). Progress is a determinate bar once the total is known, indeterminate only for the first listing second.
6. **Private by design.** A visible line on Insights: "Everything here stays on this PC."

## Surfaces

### S1. The library activity indicator (shell, always available)
- A small status chip at the foot of the sidebar's Library section, beside "Local Files" (and the matching place in
  Zune; Classic uses the WinUI layout): an icon plus one line.
  - Idle and current: no chip text, just the entry (nothing to report).
  - Scanning: a ring + "Scanning · 1,930 of 6,016".
  - Updating after a change: "Updating · 2 folders".
  - Attention: an amber dot + "2 need attention" (missing files, an offline drive, unreadable files).
- Clicking it opens the **Activity flyout** (S2). The chip's width is reserved; its text changes in place.

### S2. Activity flyout
- Header: "Music on this PC" with the state ("Up to date", "Scanning", "Paused on battery saver").
- Live phase list for a running scan, each with a tick when done: Listing folders · Reading tags · Matching
  playlists · Finding covers. The current phase shows its rate ("312 files/s") and an ETA ("about 40 s left").
- "What changed" since the app opened: "+46 added · 3 moved · 2 missing · 1 changed", each a link that filters
  Local Files.
- Attention items (each one line + one action): "D:\\Music\\Live is not readable — Fix permissions", "\\\\nas\\share is
  offline · 1,204 songs kept — Retry", "12 files can't be played (Opus) — See which".
- Footer: "Last full scan 2 min ago · took 1.8 s" · "Rescan now" · "Open insights".

### S3. Settings › Storage › Music on this PC (the plan's §3.8 wireframes, plus)
- Each folder row: path, "4,812 songs · 12 can't be played", a per-folder state icon (ok · scanning ring · offline ·
  no access) and "⋯" (Show in Explorer, Rescan this folder, Remove).
- Under the folders: the status line ("Scanned 2 minutes ago", or the live progress bar during a scan) and a link
  "View library insights".
- Options (collapsed "More options"): "Watch folders for changes" (on), "Pause scanning on battery saver" (on),
  "Read tags of cloud-only files (downloads them)" (off), "Keep missing files for" 30 / 60 / 90 days.

### S4. First run and the first scan
- **Local Files, empty:** an illustration-free empty state: "Your music on this PC" · "Add a folder and Wavee plays
  everything in it, offline too. Songs your playlists list from this PC light up as well." · [Add my Music folder]
  [Choose a folder…] · "Or drop files here".
- **After adding:** the page switches at once to the scanning header: a determinate bar, "Reading your music ·
  1,930 of 6,016 · 212 albums so far", and the list fills **folder by folder**, each new batch entering with the
  list stagger at the end of the list (sorted views insert only below the fold while scanning; the visible part
  never reorders). The header collapses to the normal count line when done, with a one-time summary toast:
  "6,016 songs from 2 folders · 41 playlist songs now play".

### S5. "What changed" after a background update
- A change while the user is not on Local Files: no interruption; S1's chip shows "Updated · +4" for a few seconds.
- A change while Local Files is open and scrolled: a pill at the top of the list, "4 new songs · 1 moved", which
  scrolls to and briefly highlights them; nothing moves under the pointer. At the top of the list, new rows enter
  with the stagger and a "New" tag that stays for the session.
- A moved or renamed file keeps its row, position in queues and playlists; its "File info" shows "Moved from
  …\\Old Name.flac" for the session.

### S6. Missing files
- A file that disappeared stays in the list, dimmed, with "Missing" in the duration column and the tooltip "Not
  found at D:\\Music\\…\\Song.flac since 12 Nov".
- A banner on Local Files when any are missing: "3 songs are missing" · [Review].
- **Review sheet:** the missing files with their last-known path and date; per row [Locate…] (pick the file or the
  folder it moved to; Wavee re-pairs and keeps the row) and [Remove]; a footer "Missing songs are removed after 60
  days unless a playlist uses them".

### S7. Offline drive and cloud-only files
- **Offline drive:** its songs stay listed with a small "drive offline" glyph and are not playable; the folder row
  says "Drive offline · 1,204 songs kept"; a banner offers [Retry]. When it comes back: the glyph clears with a
  fade, and S1 says "\\\\nas\\share is back".
- **Cloud-only (OneDrive):** rows show a cloud glyph and the file name as title until downloaded; playing one shows
  "Downloading from OneDrive…" in the player bar with progress; after download the row fills in its tags and cover
  with a crossfade. Insights counts them ("238 songs are cloud-only"). This applies to files inside a music folder
  that OneDrive manages (for example a OneDrive\\Music folder the user added); Wavee never adds OneDrive by itself.

### S8. Playlist rows: matched, unmatched, and why
- Matched local rows play in place and carry a small file glyph before the artist (the plan's board).
- **"Why?"** on an unmatched row's tooltip or row menu opens a small popover: what Wavee looked for ("Savin' Me · no
  artist · 4:12"), what it found closest ("Savin Me.mp3 · 4:15 · 3 s too long"), and the actions [Add a folder…]
  [Locate…]. On a matched row the same popover reads "Playing D:\\Music\\…\\Savin' Me.mp3 · matched by title and
  length".

### S9. File info (track menu › File info, also from the row drawer)
- Path (with "Show in Explorer"), format, bitrate, sample rate, bit depth, channels, size, length (exact or
  estimated), tags (title, artists, album, album artist, track/disc, year), cover source (embedded · folder
  "cover.jpg" · none), added and last scanned dates, playlists that list this file, and the match tier.

### S10. Library insights (a page: Local Files › Insights tab, and from S2/S3)
- **Hero numbers:** songs, albums, artists, total length ("312 hours"), size on disk.
- **Formats & quality:** a bar of FLAC / MP3 / AAC / Ogg / other; lossless share; a bitrate histogram for lossy files;
  sample-rate badges (44.1 / 48 / 96 kHz).
- **Covers:** "92% of albums have a cover" · [Show the 41 without one].
- **Tag health:** untagged files, missing album artist, missing track numbers, likely duplicates (same title, artist
  and length), each with a count and a [Show] link.
- **In your playlists:** "56 local songs in 9 playlists · 52 play · 4 not found" · [Show the 4].
- **Can't be played:** count by format with the reason ("Opus isn't supported yet").
- **Folders:** a small table per folder (songs, size, last scan, state).
- **Scan performance:** last full scan time, files per second, folders skipped as unchanged, last change applied in
  ("0.4 s after the file changed").
- **Private line:** "Everything here is computed on this PC and never sent anywhere."

### S11. Problems that need the user
- **No access to a folder:** folder row state "Can't read this folder" · [Fix permissions] (opens the folder's
  Security tab) · [Remove]. A subfolder Wavee cannot read is shown nested under its music folder; there [Remove]
  means "skip this subfolder" (it stays on disk and is left out of scans).
- **Unreadable files:** "8 files couldn't be read" in S2 and Insights, listing each file and the reason ("damaged
  tag", "not an audio file").
- **Folder inside another:** adding it shows the existing toast from the plan; adding a parent replaces the children
  with a toast "Combined into D:\\Music".
- **Scanning paused:** on battery saver or when the user pauses: S1 "Paused · battery saver" with [Resume].

### S12. Drop anywhere (plan §3.15)
- **While dragging over the window:** the app dims slightly (the engine's spotlight scrim) and every valid precise
  target lights up: the Queue panel, sidebar playlists that accept the payload, the Local Files page, the player bar.
  Over empty space, a **drop panel** fades in at the centre with two to four large zones that fit what is dragged, each
  with an icon and a one-line consequence:
  - audio files: **Play now** ("Replaces what's playing") · **Play next** · **Add to queue** ("12 songs");
  - folders: **Play** · **Add as a music folder** ("Wavee keeps it up to date") · **Add to queue**;
  - an `.m3u`/`.pls`: **Play as a queue** ("38 of 40 songs found");
  - Spotify links: **Open** · **Play**.
- A small chip follows the pointer with the count and kind ("12 songs · 2 folders").
- **Precise targets:** over the queue a drop line shows where songs land; over a sidebar playlist it highlights with
  "Add to <playlist>" (or the refusal "Adding local files to a Spotify playlist comes later"); over the player bar
  "Play now".
- **Refused:** a not-allowed glyph and the reason in the chip ("Wavee can't play .wma files yet"). Nothing dims.
- **After the drop:** big drops show a toast with progress ("Adding 1,240 songs… 312") that turns into a summary
  ("Added 1,240 songs to the queue · 3 skipped"), with [Undo] for queue changes. The first song plays at once on
  Play. Nothing appears without that toast or the queue's own entering animation.

### S13. Network folders (NAS, SMB, mapped drives) (plan §3.16)
- **Adding:** "Add a folder…" browses Network and mapped drives; a picked `Z:\Music` is shown as "Z:\Music
  (\\nas\share\Music)" and stored as the network path, so it keeps working if the letter changes.
- **Folder row:** a network glyph, "1,204 songs · network", and the live state:
  - **Online**: "Checked 12 min ago · checks every 45 min and on reconnect";
  - **Waking up**: a ring + "Waking up \\nas… (disks spinning up)", up to 20 s, never an error;
  - **Slow**: "Slow connection · scanning gently";
  - **Sign in needed**: "\\nas needs you to sign in" · [Sign in] (opens the Windows sign-in dialog; Wavee never sees
    the password);
  - **Offline**: "Offline · 1,204 songs kept" · [Retry].
- **Playing from a NAS:** the player bar shows its normal buffering cue with "Buffering from \\nas…" only when the
  network is behind; the next song preloads, so joins stay gapless. A NAS that drops mid-song: the buffered part plays
  out, then the song pauses with "\\nas went offline" [Retry]; nothing freezes.
- **Insights → Folders** shows per folder: network or local, reachability, the last scan's duration and speed ("1,204
  files in 2.4 s over the network").
- **Coming later** (shown greyed in Settings' "Add" menu as "Music server (Navidrome, Jellyfin…) · coming later"):
  music servers and "Keep a copy on this PC".

## Flows to draw
F1 First run: empty Local Files → Add my Music folder → scanning header, list filling → done toast.
F2 A playlist's local row: unmatched (grey, Why?) → Add a folder → scan → the row lights up in place.
F3 A file moved in Explorer: watcher → rescan of two folders → the row keeps its place, "Moved" in File info.
F4 A file deleted: row → Missing → Review → Remove, or 60 days later it is purged.
F5 A USB/NAS drive unplugged and replugged: offline glyphs → Retry/auto → back online.
F6 A OneDrive cloud-only song played: cloud glyph → Downloading… → tags and cover crossfade in.
F7 Retagged in a tag editor while Wavee is open: the row's text and cover crossfade to the new values within 1 s.
F8 A folder dragged from Explorer: drag over → drop panel (Play · Add as a music folder · Add to queue) → "Add as a
music folder" → the first-scan header on Local Files.
F9 Songs dragged onto the queue: drag over the Queue panel → drop line → drop → rows enter at the line, toast with
[Undo].
F10 A NAS folder added and played: Add a folder → Network → \\nas\share\Music → Waking up → scanning → play →
"Buffering from \\nas…" briefly → plays; the NAS sleeps later → "\\nas went offline" → Retry → plays on.

## Themes
WinUI: Fluent cards, InfoBar for banners, ProgressBar/ProgressRing, the existing flyout. Zune: large lowercase type,
text verbs, banners as full-width bands, numbers in display type on Insights. **Classic uses the WinUI layout**
(the owner is converging the two; no Classic-only design). The content and states are the same in every theme.
