# Changelog

All notable changes to WaveeMusic are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0-alpha.1] — first experimental alpha

Everything below ships in the initial release. There's no shipped predecessor
to diff against.

### Features
- **WaveeMusic desktop app** — a clean-room reimplementation of Spotify's
  Access Point, Mercury, Connect (Dealer WebSocket), SpClient, and Pathfinder
  protocols, wrapped in a WinUI 3 desktop app. Requires a Spotify Premium
  account.
- **Home / Browse / Search / Library / Artist / Album / Playlist / Show /
  Episode pages**, with personalized shelves, top tracks, discography,
  biographies, related content, color extraction from artwork.
- **Browser-style tabs**, pinnable + drag-and-drop, with an omnibar for
  search, suggestions, and fast navigation.
- **Spotify Connect** — full Dealer WebSocket implementation (cluster
  state, transfer, volume, queue edits, remote commands); works as both
  controller and target.
- **Out-of-process audio** — BASS for decode + DSP, NVorbis for Ogg
  Vorbis, PortAudio for output. A crash in the audio engine doesn't take
  down the UI.
- **10-band EQ, normalization, compressor/limiter, crossfade**.
- **Lyrics** — synced lyrics with shader effects, multi-language
  detection, and CJK romanization (pinyin / kana).
- **Music videos** — Spotify music videos play through WebView2 EME with
  selectable quality.
- **Now-playing surfaces** — compact player bar, expandable right panel,
  mini video player, full video page, floating player window.
- **On-device AI on Copilot+ PCs (opt-in)** — explain a lyric line or
  summarize a track's themes with Phi Silica running locally on the NPU.
  Off by default; nothing leaves the machine.
- **System Media Transport Controls (SMTC)** — Wavee shows up on the
  Windows volume-flyout media tile and the lock-screen now-playing card;
  hardware media keys (headphone / Bluetooth / keyboard play-pause /
  next / previous) drive playback.
- **Auto-update via `.appinstaller`** — install `Wavee.appinstaller` once,
  Windows then pulls every subsequent signed MSIX from GitHub Releases
  automatically every 24h.
- **Playlist mutations** — create / rename / change cover / delete / add /
  remove / reorder. Track context-menu "Add to playlist" and "Add to new
  playlist" work across every track surface. Drag-drop tracks onto a
  playlist sidebar entry, or drag a playlist into a folder.
- **Album multi-select toolbar** — select multiple tracks on an album
  and Play / Play after / Add to queue / Add to playlist together.
  Same selection commands work in Liked Songs and on Playlist pages.
- **Sleep timer** — duration-based or end-of-track; pauses playback when
  it fires.
- **Podcast controls** — back-15 / forward-15 buttons and a playback-speed
  flyout (0.5× – 3.0×) visible only on episode rows.
- **Crash report packager** in Settings → About — zips your recent logs
  and opens GitHub Issues with version + OS pre-filled.
- **Privacy and third-party-notices cards** in Settings → About.
- **Shell-level offline / reconnecting / "Premium required" banners**
  that react to live session and auth state.
- **Drag-and-drop everywhere** — track → playlist / queue / now-playing,
  playlist → folder, sidebar reorder. Every successful drop fires a
  destination-named confirmation toast.
- **System-tray icon + minimize-to-tray** — opt-in Settings → Playback
  toggle. When on, the close button hides Wavee into the tray; playback
  keeps going. Tray menu: Show, Play/Pause, Next, Previous, Quit.
- **Private listening session** — Settings → Playback toggle. When on,
  this device announces `is_private_session=true` to Spotify's Connect
  surfaces AND Wavee suppresses the gabo `RawCoreStream` /
  `RawCoreStreamSegment` events so this session doesn't show up in
  Recently Played, friend activity, or recommendations.
- **Dedicated "Recently Played" page** — a grid of every album / playlist
  / artist / show you've recently played, sourced from the same
  `RecentlyPlayedService` that powers the Home shelf.
- **Right-click context menu on friend rows** — Play next / Add to queue
  / Go to track / Go to artist / Go to album for whatever the friend is
  currently playing.
- **Hide song / Don't play this artist** on every track context menu.
  Toggles Spotify's server-side `ban` and `artistban` collections — the
  same lists the official client uses, so changes sync across devices
  and Spotify's recommendation surfaces (Home, autoplay) hide blocked
  content automatically.
- **Sidebar folder rename + delete** — right-click any sidebar folder to
  rename or delete it. Deleting a folder lifts its nested playlists to
  the top level (matches Spotify desktop's behavior).
- **Per-page error states with Retry** — Album, Home, Search, and Liked
  Songs pages surface a full-page error card when the initial fetch
  fails; the Retry button re-runs the fetch.
- **CDN URL cache persisted across launches** — playback startup skips a
  storage-resolve roundtrip when the cached URL is still inside its
  expiry window.

### Engineering
- LAF (Limited Access Feature) tokens for Phi Silica wired for both dev
  and production publishers — AI features stay enabled in
  Azure Artifact Signing release builds.
- BASS decoder supports mid-track seeking.
- `DropTargetBehavior` wraps WinUI async-void drop handlers in try/catch
  so a malformed drag from a foreign app can't escape into the
  unhandled-exception path.
- Context-menu builders no longer Debug.WriteLine when commands aren't
  wired — they invoke real `IPlaybackService` / `IPlaylistMutationService`
  / `IPinService` defaults, or omit the entry entirely when no sensible
  default exists.
- `PageErrorState` reusable control for full-page error surfaces (wired
  into `AlbumPage`, `HomePage`, `SearchPage` with per-VM Retry commands).
- `SystemMediaTransportControlsService` mounted from `MainWindow`
  activation. Album art passes through `SpotifyImageHelper.ToHttpsUrl`
  so the Windows lock-screen / volume-flyout tile renders the actual
  artwork instead of the package icon.
- Per-process `SleepTimerService` in DI.
- `DeviceStateManager.SetPrivateSessionAsync(bool)` mutates the in-memory
  DeviceInfo and re-publishes PutState. `EventService.SuppressPlayHistory`
  gates `RawCoreStream`/`RawCoreStreamSegment` posting at the queue layer.
- `IPlaylistDragDropMediator.GetDisplayNameAsync(uri)` resolves Spotify
  entity names so drop-toasts can say "Added 3 tracks to {playlist
  name}" instead of "Adding 3 tracks…".
