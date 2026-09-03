# Changelog

All notable changes to **Wavee** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases are cut from the `wavee-v*` tag prefix — see `docs/guide/releasing-wavee.md`. (The FluentGpu engine/gallery
versions separately under `v*` and is not tracked in this file.)

## [0.2.8] - unreleased

### Fixed

- Album pages no longer sit on blank rows with a nonsense running time ("31 songs · 6 min") until something else
  happens to repair them. `getAlbum`'s named tracklist was being discarded by an adoption gate that compared row
  COUNTS only, so a resident list of gid-only AlbumV4 rows tied the named list that arrived and won — the album
  then sealed at its rung and every later open short-circuited before the repair or the thin-row tripwire could
  run. Equal length is now decided on quality: the resident list only wins a tie when it is no thinner than what
  landed. (#90)
- The detail hero no longer leaves a band of dead space under the action row. The identity column is pinned to the
  cover's edge and the slack is spread across its own gaps instead of pooling in one hole, so the actions and the
  blurb under them land on the cover's baseline while the cover's top stays on the title's. (#78)
- The hero title is sized from the space AND the title itself instead of a six-step ladder keyed on width alone: a
  short name grows to fill the height the cover sets, a long one takes a second line rather than ellipsizing, and
  the authored line height no longer fights the face's natural line box (which the engine was silently using
  anyway, discarding every paired value above 20). (#79)
- The narrow detail hero puts the cover beside the copy from 424 DIP instead of 540, so a ~470-DIP page stops
  wasting ~157 DIP to the right of a 280-DIP cover and stops pushing the first track ~280 DIP down the page. (#80)
- The artist hero is 452 DIP at Compact and 476 at Narrow, down from 644 and 628 — the name caps at two lines, the
  stats go back on one row, and the copy sits at the bottom of its band instead of floating centred in it, so Top
  tracks is no longer entirely below the fold. (#81)
- Clicking a Mixview neighbour re-centres the graph and moves the surrounding pane with it. The ring was rebuilt
  from data captured at mount — `Responsive.Of` freezes its build closure — so the click wrote a signal nothing
  read. (#83)
- The top bar's search box holds still when the tab title changes width. The merged row centres between its two
  clusters rather than in the window, so a 90-DIP tab swing moved the box by up to 45; the tab lane now reserves a
  quantised width, the pin button keeps its slot when it declines, and the profile name is capped. (#88)
- The What's New viewer dims the whole window again and shows its caption. The overlay root shrink-wrapped to the
  plate, so the veil covered only the plate's own box, and the caption's scroll viewport measured zero tall and
  was clipped away entirely. (#89)
- Overlay veils no longer tint under the cursor and flash on press. A full-bleed dismiss surface carries an
  `OnClick`, so the recorder interpolated it toward an unset hover brush — the engine's own scrim pins both, and
  Wavee's did not. (#91)

### Changed

- Library V3 can reach Liked Songs, Albums, Artists, Podcasts and Local files. They were absent from the design
  entirely. They arrive as a row of words under Home — not five stacked rows and not icon-only tiles, both of
  which were tried and rejected: labels stay visible at every pane width, the active word carries the count and a
  2-DIP accent underline, and the rail scrolls with the last word peeking past a fade. The collapsed 56-DIP rail
  carries them too. Pinned rows now show a pin marker, the "+" accepts drops, and a refusal to reorder under a
  sorted lens offers to switch to Custom order. (#85)
- The sidebar resizes down to 180 DIP instead of stopping at 240: grid strips shed a column rather than shrink
  their cells, section titles ellipsize instead of pushing, and the splitter still resists and dims from 240 down
  before it snaps to the rail. (#84)
- "Your top artists" fills the width it is given — the avatar ramp is solved from the measured width instead of
  three hard-coded sizes — and the surface is tightened throughout. Artists are reachable from it at last, by
  context menu or double-click, from both the podium and the Mixview graph. (#82)
- The Concerts and Browse cards on Home have artwork: a procedurally drawn, endlessly drifting panel per card
  rather than an empty grey rectangle, on looping animation tracks at co-prime durations under the 30 Hz ambient
  cap, and reduced motion swaps the keyframes rather than branching. (#86)
- The artist pick loses its "Artist pick" heading and keeps its column beside Top tracks — the card already says
  what it is. (#87)

## [0.2.7] - 2026-09-03

### Changed

- **The Library V3 sidebar is rebuilt around one left edge.** Home is fixed chrome above "Your Library" instead of
  a "Shortcuts" section inside the filtered list, so filters and search never hide it and the rail keeps its tile.
  The search field is inline when the pane has room — transparent, borderless, beside the full "Recents" pill — and
  collapses to a magnifier on a narrow pane that morphs open in place, clears on one Escape, closes on a second or
  on an empty blur, and is never remembered across launches. Filter pills sit on a bordered control surface, keep
  their width when selected, use contrast-picked ink on the accent, and picking a kind slides in a round ✕ that
  clears everything; a qualifier fuses into the shared segmented pill with an opaque segment and an ✕. Every row of
  every design now shares one art column: glyph rows get a real art-wide column, the tree's reserved chevron cell is
  gone (the folder chevron moved to the trailing edge), a pinned folder sits flush with its siblings, the
  multi-select lane no longer pushes rootlist rows right, and the header glyph, magnifier and first chip line up with
  the rows' art. (#71)
- **The artist page's album drawer is compact, instant and unmistakable.** 32-DIP rows under a 40-DIP header (was 44
  under 56), two track columns on wide windows so a whole album fits without scrolling, a "Show all" row past 12 / 24
  tracks, a caret pointing at the cover you clicked and that cover in the drawer's header, and the clicked row always
  scrolled to the same spot under the tabs with one 200 ms open — no more skeleton flash for an album you already
  opened, no more previous album's tracks under the new cover, no more drawer hopping between rows while the page
  scrolls after it, no more section-wide reflow on every click. (#77)

### Fixed

- The Library V3 search box no longer cross-fades between two controls, swallows Escape, stays open forever or
  reopens after a relaunch: it is one control that morphs in place, Escape clears then closes, an empty blur closes,
  the ✕ clears, and typing is debounced so a keystroke no longer rebuilds the whole library projection. (#69)
- Library V3 filter pills: the fused "Playlists │ By you" segment is legible again (an opaque segment instead of a
  translucent card on the accent), selected ink follows the live accent's contrast, the label no longer shifts when a
  pill is selected, resting pills have a visible border, the trailing glyph is an ✕ because tapping clears, and a
  leading ✕ clears every filter at once. (#70)
- The sidebar's "Recents" sort follows what you played, not what you clicked: opening a playlist no longer moves it
  to the top, playing one (here or on another device) does, and never-played items keep their added-date order
  below. (#72)
- Sidebar rows share one left edge in every design: glyph rows, pinned covers, playlist covers, folders and the V3
  chrome all start on the same art column, and a rootlist with a folder no longer indents the whole list. (#73)
- **Switching the output device (or its sample rate) mid-track no longer breaks the next track.** The gapless hand-off
  used to be scheduled from the old device's clock, so the next song started up to minutes late while the bar counted
  past the end, the time readout flicked between two values, and the pre-decoded next song played slow and flat at the
  old rate. The join now follows the live session clock, a pre-decoded song is re-prepared for the new rate, and the
  readout is clamped on every path. (#65)
- Row size now scales the cover art with the row: Cozy rows get 40-DIP art and Comfortable rows 48-DIP, instead of a
  32-DIP square floating in a 64-DIP row; the Settings › Appearance density preview follows. Dragging the Row size
  slider no longer bounces between levels (the toolbar button relabelled itself mid-drag and dragged its flyout
  sideways under the pointer), and the thumb tip says Compact / Default / Cozy / Comfortable instead of 0–3. (#66)
- The setup sign-in page no longer cuts off its last line: the page body now really scrolls (and shows its rail) when
  it overflows, the QR code respects its 80-DIP box instead of growing to 111, and the scan card's text fits one
  line. (#67)
- "Report a problem…" could open with Question selected; the requested kind now travels with the request and is part
  of the form's identity, and every open is logged. (#68)
- Selecting an artist (or album/podcast) in Your Library no longer throws the list back to the top and then scrolls
  it back: the navigator keeps its scroll position across every refresh, and a refresh that changes nothing no longer
  rebuilds the list at all. (#74)
- "Recents" in Your Library › Artists / Albums / Podcasts — and in an artist's discography column — now means
  recently *played*: what you listened to most recently comes first (including plays from other devices, via your
  listening history), everything you have not played keeps its old order below. "Recently added" now really is
  newest-added first (it listed the oldest first), and the direction chevron works for every sort. (#75)
- **The album page's "About this release" tiles no longer re-arrange themselves as details arrive.** The block is a
  fixed two-column grid from its first paint — Songs · Length over a full-width release date — the date refines in
  place from year to full date, the label joins the notes below instead of becoming a fourth tile, nothing slides or
  overlaps, and a long date or label can no longer run past its tile (it wraps or ellipsizes inside it). The same
  stat tile now serves the module page, the pre-release countdown and the track facts strip. (#76)

## [0.2.6] - 2026-09-02

### Added

- **Report a problem from inside Wavee.** Settings › About has "Report a problem…" and "Suggest a feature…", the
  About tab lists recent crash reports with a Report button, and after a crash the next launch offers to file
  it. Wavee opens the matching GitHub form with the version, install source, architecture and Windows build filled
  in, and copies a redacted report (personal paths, account details, secrets and addresses removed; track names
  kept) to the clipboard and to a `wavee-report-<date>.txt` file beside the logs for you to paste. The "closed
  unexpectedly" prompt is offered once per run that left no report or dump behind — a process stopped from the IDE
  or Task Manager on every run no longer re-asks after every "Not now" — and "Don't ask again after a crash" now
  silences that evidence-free prompt entirely (a real crash report or dump still surfaces as a quiet toast).
- **A real Logs tab.** Settings › Logs is a full-height log viewer: a command bar (refresh, copy, export, open the
  log folder, clear; newest-first, group repeats, and a Verbose switch that captures Debug/Trace for the running
  session), a search box with level and category filters, rows that expand on a single click to show their fields
  and exception, and a session picker that actually lists your previous runs. (#55)

### Changed

- **Setup is three screens** — welcome & terms, sign in, local playback — instead of seven. The appearance,
  sidebar, sound and notification tour pages are gone; those choices live in Settings with the same defaults the
  wizard used to pick, and "Run setup again" is gone with them. Signing in continues straight to the local playback
  step without an "Is this you?" stop. (#53)
- **Settings is regrouped** into General · Appearance · Playback · Notifications · Storage · Logs · About, and every
  row now carries its own icon instead of repeating its section's. "Disable marquee text" / "Disable color washes"
  are now the on-switches "Marquee text" / "Color washes". (#54)
- **The title bar's "…" menu is gone.** Pin to sidebar, Settings, notifications and Friends are direct buttons in
  the top-right cluster (the notification badge moved from your avatar to a bell), all on the same geometry as the
  theme toggle. (#58)
- **Setup heroes are real animations.** The welcome, sign-in and local-playback pages play the Lottie scenes Rise
  Media Player's setup uses, recoloured to your accent and played the same way (first half once, then hold). (#53)
- **Setup looks like a WinUI dialog.** A 762×490 plate, the Lottie beside a title and plain text with settings
  cards, a progress bar and an Accept/Continue footer — no more hero cards, chips or captions. (#53)
- **Setup opens straight away.** The "Welcome to Wavee · Start setup" splash before the dialog is gone; a first run
  shows the terms page immediately. The sign-in primary is the standard accent button (the Spotify-green one was too
  harsh in dark mode), the local-playback step says "Checking…" on its button while the long status lives on the
  page, and the sign-in page fits the dialog: a smaller QR, one-line card text, and "Wavee needs Spotify Premium ·
  Sign up" on one row. Any setup page that still overflows shows the scrollbar rail. (#53)

### Removed

- The palette picker (Settings and the profile menu), the Mica / Mica Alt choice, "Limit page color to the hero"
  and "Track page layout": Wavee always uses the neutral palette, Mica, the automatic page layout and full-page
  tone. (#54)
- The `WAVEE_LOG_LEVEL` / `WAVEE_LOG_FILE_LEVEL` / `WAVEE_LOG_RING` environment overrides — use Settings › Logs. (#55)

### Fixed

- Setup showed the raw Spotify account id instead of your display name on the account card and the final page. (#51)
- The "Local playback needs a one-time setup" toast and banner no longer pop up on top of the setup wizard, whose
  own local-playback step is that prompt. (#52)
- Report a problem: scrolling the form no longer paints a dark band over the fields under the title. (#56)
- **Dialog text that vanished after scrolling.** In "Report a problem" (and any dialog with a long body), scrolling
  the body down could blank the last checkbox's label and the "Report on GitHub" / "Not now" labels while their
  buttons stayed: the renderer's per-frame text budget filled up on the report preview and silently dropped every
  glyph after it. The budget now grows to what the frame needs and the frame repaints whole. (#56)
- **Text from the previous screen showing through the setup dialog.** Page text underneath an opaque dialog could
  be painted over it after a partial repaint; the renderer now keeps paint order whenever a fill covers earlier
  text. (#53)
- Search: clicking an artist link on the top-result card (or in any result row's subtitle) navigated *and* played the
  song; a link click is now only the link. The top-result card also gained the right-click menu, a "…" button and
  drag-to-playlist that every other result already had. (#57)
- The pop-out video window now fits the video to the window and keeps the transport bar visible at any size,
  instead of pushing it below the fold until the window was stretched. (#59)
- Audio quality now applies to every track, not only ones Wavee had never seen before: the chosen bitrate was
  cached per track for the whole session. The bitrate Wavee picked is now in the log at Info level. (#60)
- Equalizer changes (on/off, preset, dragging a band) apply immediately to the playing track instead of on the next
  track, and the equalizer now also applies to local files, radio and module playback. (#60)
- The title bar said "Sign in" while you were already signed in (the shell mounts before the silent resume
  finishes), and pressing it signed you in as the demo account on top of the real resume. The chip now follows the
  shell's real auth state — Connecting… / Reconnect / Sign in — and Sign in runs the real resume. (#61)
- A track restored paused at launch showed the buffering bar sweeping forever, and its elapsed/remaining readout
  could show a position past the track's own length (e.g. "35:32 / −0:00" on a 2:54 song) — the position clock now
  clamps to `[0, duration]` on every read, paused or playing, instead of only while playing. (#62)
- The profile menu showed your Spotify account id instead of your name when the profile arrived before go-live. (#63)
- Crash reports are written again. The report writer could not read the live log (a sharing violation, swallowed
  silently), so the report was lost and the next launch only knew about the Windows dump; the second handler no longer
  overwrites the first, a failed write is logged, and a crash whose report failed still prompts on the next launch. The
  crash prompt itself now says "Reading the last crash report…" while it reads, stacks its title over its message,
  and shows a taller preview; the redactor no longer rewrites "Wavee" or a GitHub handle inside URLs when they merely
  contain a device or display name. (#64)
- Home could show only the notification feed, a failed Charts card and Concerts/Browse — no shelves, no "nothing
  here yet" either — for a moment right after launch, and the same empty read could also flash a stray "Nothing
  here yet" card over a perfectly normal account. Home now waits for a real feed (or a confirmed-empty one, once
  the live session has actually had its say) before it paints anything — and could stay waiting forever on some
  launches, because the wait only re-checked on a feed-cache bump and the session going live published none of its
  own; Home now also re-reads the instant sign-in actually completes, and force-releases whatever feed it has after
  8 s no matter what, so the skeleton can never hang indefinitely. (#53)
- **Home opened in three jolts**: the cached "Jump back in" grid and library sections appeared while Wavee was still
  connecting, a lone "No charts right now" row painted under them, the new-releases timeline popped in once the session
  went live, and a second later the live feed replaced everything — chips and the hero appeared, the grid dropped
  350 px and every row remounted. Home now keeps its loading shimmer until the session has had its say (live, or
  confirmed offline) and the Charts deck and notification feeds have landed too (capped at 1.5 s), then reveals the
  settled page once; later refreshes swap in place and never re-run the reveal or re-skeletonize a row. Offline, the
  cached shelves still reveal once from cache; the Charts deck no longer shows a failed card offline. (#53)

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
