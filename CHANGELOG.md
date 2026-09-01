# Changelog

All notable changes to **Wavee** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases are cut from the `wavee-v*` tag prefix — see `docs/guide/releasing-wavee.md`. (The FluentGpu engine/gallery
versions separately under `v*` and is not tracked in this file.)

## [0.2.5] - 2026-09-01

### Fixed

- **Audio could glitch or stutter under disk or CPU load.** Writing a streamed chunk to the local cache used to
  hash it, check free disk space and fsync — all synchronously on the audio decode thread, for every 64 KB chunk.
  That work now runs on a background thread, and a CDN cache miss during playback no longer blocks the decode
  thread on a network fetch either.
- **Read-ahead buffering is now adaptive** instead of a fixed 256 KB window — it grows toward several minutes of
  audio on a fast connection (fewer chances to run dry) and shrinks on a metered one (less data used), based on
  measured throughput and the track's bitrate.
- Audio underruns are now logged individually, with timing and cause, instead of only as a running count.
- **Sign-in no longer blocks the app on every launch.** Wavee now shows your library immediately from the last
  session and reconnects to Spotify quietly behind it; if your credentials were revoked, it still falls back to
  the sign-in screen.
- **Microsoft Store builds** — "Check for updates" was a silent no-op; it now opens the Store listing. The
  after-update "What's new" plate also now appears correctly on Store installs, not only sideloaded ones.

## [0.2.4] - 2026-09-01

### Added

- **Wavee is on the Microsoft Store** — the What's-new page and the after-update dialog now announce the Store
  listing to sideloaded installs, with a button that opens it. Store-installed copies (which already get updates
  through the Store) don't see the announcement.

### Fixed

- **Music videos: smooth switching, no more freezes.** Turning video on, or skipping tracks while video was
  playing, could freeze the whole app for seconds — and every track change tore the video player down and rebuilt
  it from scratch. Video now switches in place: the previous frame holds until the next video's first frame
  arrives, the next track's video is looked up ahead of time during playback, and play/pause/seek stay responsive
  through the whole switch. Also fixed along the way: the docked video card letterboxed wider-than-16:9 videos
  (it now fits the video's real shape), the video controls could lay out wider than their card and stick that way,
  and two loading spinners could stack on top of each other while a video started.
- The What's-new highlight cards could cut their text off mid-line — cards in a row were sized against a wider
  text wrap than they actually got. Rows of equal-width cards now measure at their real share of the width, so the
  cards fit their text.

## [0.2.3] - 2026-08-31

### Fixed

- Fixed rare text rendering glitches (a letter occasionally drawing incorrectly) that could persist on some screens.
- A background page could still scroll while a dialog was open on top of it.

## [0.2.2] - 2026-08-31

### Added

- **Settings > About** — the graphics adapter in use is now shown, with a picker to select a different GPU (applies
  live). Useful on hybrid-GPU laptops/desktops.
- **App zoom** — Ctrl+Plus/Minus/0 (top row and numpad), Ctrl+mouse-wheel, and three new command-palette entries
  (Zoom in/out/reset) scale the whole app UI.

### Fixed

- **Crash navigating to an artist page.** A placeholder layout shape built while an artist's real data is still
  loading could overflow an internal index and crash the app.
- **Stability and smoothness on integrated/weak GPUs** (e.g. hybrid laptops) — the app could intermittently freeze
  (visuals stuck, audio still playing) during heavy navigation or long sessions on some integrated GPUs. Frame
  pacing, texture upload, and memory-budget handling were hardened so a GPU hiccup now recovers automatically in
  under a second instead of freezing the window; large album art / cover images that could get stuck as blank
  placeholders on affected hardware now load correctly.
- **Correct GPU selection** — on machines with more than one GPU (e.g. a laptop with both an integrated and a
  dedicated graphics card), Wavee now reliably picks the faster one instead of sometimes landing on the weaker
  integrated GPU.
- Playlist/album pages now show a clear, in-place notice when Spotify reports content as deleted, revoked, or still
  loading (minified), instead of silently failing to update.

## [0.2.1] - 2026-08-30

### Fixed

- **Tabs** — clicking a tab in the tab strip now shows that tab's page. Activating a tab is a restore, not a new
  navigation: it no longer pushes onto Back history or rewrites the destination's breadcrumb origin, and closing a
  background tab no longer navigates.
- **Breadcrumbs** — a section opened from Home (Weekly Song Charts, Concerts, ...) now reads
  `Home › Browse › ...` instead of claiming you came from Browse.
- **Browse, Concerts, artist schedules** — page content no longer scrolls up through the breadcrumb band; the
  Concerts filter card docks below the crumb instead of over it.
- **Search box** — typing a letter no longer flashes "No results found" before the suggestions arrive. Suggestions
  survive a re-mount of the search field, a superseded request can no longer leave the box stuck loading, and a
  transport failure now says "Couldn't load suggestions" with a retry instead of pretending there were no results.
- **Queue** — every upcoming row (Next in queue, Next up, Autoplay) can be drag-reordered within its own section; a
  queue row dragged around the queue can no longer land as "Added to <playlist>".
- **Recents** — expanding a row reveals every track played from that context (the full list, numbered 1..n, with the
  time it was played) with a smooth reveal instead of a stalled sliver that snaps open.
- **Liked Songs** — the left rail is resizable and remembers its width (podcast shows too).
- **Home cards** — the hover play button on the Recents rail was wired to nothing; it now plays. A card's play button
  no longer pauses/resumes an unrelated context just because the playing track shares an artist, and it stays under
  the pointer while you press it.
- **Liked Songs sync** — the local collection could silently lose its newest likes after a truncated snapshot and
  then never recover. Snapshots are now verified before anything is swept or the sync token advances, the app
  reconciles the collection against the server at start, on reconnect and periodically, fresh likes are shielded from
  a sweep, and the header, sidebar and stats all report the same membership count. Existing installs repair
  themselves on first launch.
- **Settings › Playback** — the "On metered connections" card shows whether Windows currently reports a metered
  network, updating the moment the network cost changes.
- **Crash on the Liked Songs page** — opening Liked Songs before its tag data had hydrated (every track untagged)
  crashed the app in the blend card; the empty "Other" tail is now a real empty list.
- **Crash reports** now carry the build commit, module base and frame offsets, and every release keeps its symbol
  map, so a NativeAOT crash can be resolved to a method.
- **Playlist owner names/avatars** — a user-profile payload that is not plain JSON is decoded instead of being
  memoised as "no profile" for the session.
- **Icons** — the icon font ships inside the app, so glyphs no longer depend on the Windows version (the "Tune"
  and "What's new" icons rendered as boxes on Windows 10).
- **Opening a playlist** — a cold open no longer flashes "You no longer have access to this playlist", "0 songs" or
  "Nothing here yet" while the header and tracks are still loading; it shows the skeleton until they land.
- **Home facets** — the Music / Podcasts / Audiobooks chips now swap the whole feed: a facet is its own document
  (server sections in server order, its own scroll position), the feed carries the facet it was read for so a stale
  poll can never land under a newer chip, and shelves re-describe on every feed change (a facet swap, the periodic
  refresh, a daylist rollover) instead of keeping the first feed they mounted with. The Home pill's trailing mark is
  an X that clears the sub-facet, not a chevron that promised a menu.
- **Hero layout** — the cover art no longer renders detached at the bottom-left of the page, and the facts bento (this
  week's likes, tempo, top artists) moved from the hero column to the page footer so the first track is above the
  fold; the two-column layout keeps it in the rail.
- **Concert filter pills** — the pill's trailing glyph reflects what a tap does (reopen the chooser vs clear).
- **Lyrics** — credit and metadata rows that some providers ship inside the lyric body (lyricist / composer /
  arranger headers, boilerplate, stray tags) no longer reach the screen or the ranking. The cleaner first honours what
  the provider marks structurally, then trims leading/trailing lines that align to nothing in the reference document
  (language-agnostic), and only then falls back to a "key: value" grammar — never a bare word list.

## [0.2.0] - 2026-08-30

### Added

- **Developer mode** — an explicit Settings toggle that gates the diagnostic surfaces. Off by default, so a normal
  install no longer exposes developer-only tooling.
- Updates: **What's new** — a page that shows the release highlights, the full changelog, and the current GitHub
  state of every issue a release references. The first launch after an update opens a short summary of what changed;
  a checkbox turns that off for good.
- Updates: **Release channel and update timing** — Settings › About names the channel this build follows. Updates
  apply on the next launch; optionally Wavee installs a waiting update as it closes (“Install a waiting update when
  I quit Wavee”, off by default) so the new version is simply there next time. Metered connections are left alone
  unless you opt in.
- **In-app update check** — Settings › About checks for a newer published release and reports what it finds, alongside
  the installed version.
- **Privacy policy and third-party notices** — reachable from Settings › About. `THIRD-PARTY-NOTICES.txt` is generated
  at packaging time and ships both inside the package and as a release asset.
- **Start on login** — an opt-in setting to launch Wavee when Windows starts.
- **`wavee://` protocol and toast activation declared in the MSIX manifest** — deep links and notification clicks now
  activate the packaged app through its registered identity/AUMID instead of the unpackaged HKCU fallback.
- **Crash notice on next launch** — if the previous run died, the next launch says so and points at the crash report
  and log, rather than failing silently.

### Changed

- Updates: **Updates download in the background and apply on the next launch.** “Update now” downloads, stages and
  restarts with real progress; the app no longer hands you to App Installer and hopes.
- **Dealer archive is off by default.** The raw dealer-message archive is a debugging aid; it now writes nothing unless
  it is turned on.
- **Lossless removed from the audio-quality picker** until it actually ships. Offering an option that silently did not
  apply was worse than not offering it.
- **API console, lyrics inspector, and test notifications moved behind developer mode.** They remain fully available —
  just not on the default Settings surface.
- **Image cache moved under `%LOCALAPPDATA%\Wavee\cache`**, joining the rest of the app's per-user state in one place
  that a factory reset can clear.

### Fixed

- Updates: **no more blank "App Installer" window at every launch.** The feed's on-launch check now runs silently in
  the background; a new version is staged while you keep using Wavee and applied the next time it starts.
- App icon: **no more white corners on the taskbar.** The icon's rounded corners are now genuinely transparent
  instead of white pixels left over from the artwork's export.
- Updates: **The "What's new" plate no longer opens on top of the setup wizard.** After a sign-in or a setup rerun it
  waits until setup is done, then appears in the same session.
- Setup: **The Terms step's full agreement opens in place and stays in its column.** The summary card grows into the
  scrollable agreement with a proper Close button (and Escape), instead of a separate panel that could paint over the
  page's own heading at some window widths.
- Updates: **A background update check that cannot reach the feed no longer interrupts you.** It is still shown in
  Settings › About (with Retry); only a check you started yourself, or a failed install, raises a toast.
- Updates: **Installing an update on quit no longer trips Windows' hang detector.** Wavee kept answering Windows while
  the update installed, instead of going quiet and being closed as an unresponsive app on the way out.
- Updates: **“Check for updates” could never find a release.** The check pointed at the repository's global latest
  release — the FluentGpu gallery's — so a published Wavee build was invisible to it. It now reads Wavee's own feed.
- **Fabricated listening history on fresh installs.** A brand-new profile showed recently-played entries that had never
  been played; a fresh install now starts empty.
- **The first-run wizard could be dismissed with `Esc`,** dropping the user into an unconfigured app.
- **"Cancel" while signing in quit the app** instead of returning to the previous step.
- **Setup › Local playback showed a stray chip row** with nothing behind it.
- **Hard-coded Premium pre-launch gate.** The account-tier check no longer depends on a compiled-in assumption about
  the signed-in account.

### Removed

- **Environment-variable switches.** `WAVEE_SETUP_START_PAGE`, `WAVEE_FPS`, and `WAVEE_DEALER_ARCHIVE` are gone, as are
  the `--free` flag and `WAVEE_FORCE_FREE`. Behaviour is configured in Settings (or reported by the diagnostics pages),
  never by an env var a shipped build would have to honour.
- Updates: **The `ms-appinstaller:` hand-off.** Windows disables that protocol by default on consumer installs, so the
  button opened nothing and then reported success. Wavee downloads and stages the update itself.

### Known limitations

- No dedicated **episode** or **profile** pages.
- **`nl` and `ko-KR`** localizations are partial and therefore hidden from the language picker.
- No **system tray** integration.
- No **podcast playback-speed** control.
- **FLAC seek** is not implemented.
- The **UI Automation tree** is incomplete — screen-reader coverage is partial.

[0.2.0]: https://github.com/christosk92/WaveeMusic/releases/tag/wavee-v0.2.0
