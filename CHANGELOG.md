# Changelog

All notable changes to **Wavee** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases are cut from the `wavee-v*` tag prefix — see `docs/guide/releasing-wavee.md`. (The FluentGpu engine/gallery
versions separately under `v*` and is not tracked in this file.)

## [0.3.1] - unreleased

### Added

- **Nineteen visualizers in full screen.** The visualizer gallery now has five groups. Lyrics has Verse, where the
  song's words land as they are sung, held notes grow and a returning chorus lights up. Fluent has Bloom, Bars, Ring,
  Orbit, Aurora and Timeline. Classics, after Winamp and Windows Media Player, has Classic, Warp, Tunnel, Ambience,
  Kaleido, Scope and Drift. Zune has Type, Mosaic and Spotlight, and iTunes has Magneto and Flow. Every face takes its
  colours from the cover, works in light and dark mode, and keeps moving from the song's own waveform when Spotify
  Connect plays elsewhere. Press `[` and `]` to step through the faces and `G` to open the gallery; the gallery
  animates only the previews you can see, and a preview you rest the pointer on comes to life.
- **Change with the music.** Every eight bars, on the downbeat, the visualizer rotates the cover's colours with a short
  cross-fade, and a returning chorus does the same. A new song cross-fades to its own colours. Turn it off in the
  gallery or in Settings > Appearance > "Change with the music".
- **A new Artist pane in full screen.** The artist's photo fills the band, with monthly listeners and world rank, Follow
  and Go to artist. Below it: the full biography, a photo strip, this song's credits grouped by person, the five most
  popular songs, where people listen and related artists.
- **Word-by-word lyrics with on-device AI.** On a Copilot+ PC with a Snapdragon NPU, Settings › Appearance ›
  Lyrics can download the AI models once (about 700 MB, English; Spanish optional). Wavee then times every word
  of a song's line-synced lyrics on the NPU while the song plays, so the lyrics wipe word by word. The song and
  its lyrics never leave the PC, and a ✦ in the lyrics header shows when the word timing is AI-generated. (#175)

### Changed

- **Motion runs at your display's refresh rate.** The buffering spinner, loading shimmers, the now-playing equalizer,
  the decks, the full-screen visualizer and the full-screen lyrics caption were held to 30 frames a second (24 on
  battery), which looked choppy on a 120 or 144 Hz screen. They now run at the panel rate, and Wavee no longer slows
  its animations down when its window loses focus. With Windows Energy Saver on, all motion is capped at 30 frames a
  second; scrolling and dragging never are.
- **Context menus open faster.** A menu that fits inside the window is drawn in the window instead of in a separate
  popup window, so a track's right-click menu reaches the screen in less than half the time.
- **Full-screen Now Playing uses a big screen.** On an ultrawide or a large monitor the cover grows to fill its
  column, the lyrics grow with the stage, and Up next rows get larger art and type. The Up next list stops at a
  readable width, opens with the next song set apart, splits into sections with their counts, numbers each row and
  marks explicit tracks.
- **The fullscreen visualizer redraws only what moves.** The drifting background and the visualizer bars no longer
  redraw the whole screen on every frame, the frosted panels stop re-blurring when nothing under them changed, and the
  background is drawn at a quarter of the screen resolution (it is a soft gradient, so it looks the same). Before, every
  frame of the visualizer redrew all of a 3440 x 1440 screen, which took about half of the GPU at 120 Hz on a
  Snapdragon X laptop.
- **The fullscreen stage no longer draws the app hidden behind it.** While the stage is open, the pages under it are
  left out of every frame, and the stage's background (the blurred cover, the drifting colours and the dark tint) is
  drawn as one layer instead of three. In the offline demo on a Snapdragon X laptop at 120 Hz, the stage takes 1.69 ms
  of GPU time per frame instead of 2.35 ms (Task Manager: about 22-25% GPU instead of 32%).
- **Opening pages leaves less memory behind.** Spotify's answers (the Home feed, artist and album pages, track
  metadata, playlists, Connect updates) are now read into reused buffers and decoded in place instead of being copied
  into fresh memory several times over. A large answer used to leave two to five times its own size of garbage in the
  part of memory that is the slowest to clean up and the easiest to fragment: about 2.7 MB for the Home feed and up to
  10 MB for a large metadata batch. The play history is also written to disk without building the whole file in
  memory first.

### Fixed

- **Light mode in full screen.** The player bar, the now-playing card and the visualizer gallery stayed dark with
  dark text in light mode, and a few controls kept white outlines. They now follow the theme.
- **The pop-out video window no longer slows down the main window, and video never slows the main window.** The pop-out
  used the main window's frame slot, so opening a flyout or resizing could freeze the app for up to 0.6 s while a video
  played in the pop-out. Each window now presents on its own, the pop-out's frame is presented before the main window
  waits for its own, and opening and closing the pop-out no longer shows an empty window. Nothing is throttled for it:
  the main window keeps its full frame rate whether or not a video plays, docked or popped out. (#171)
- **No more ghost edges around DRM video.** The picture and the hole the UI leaves for it now move in the same frame, the
  video is scaled smoothly instead of with blocky pixels, and the last frame of the previous video no longer shows
  through after a switch. (#172)
- **Switching videos and moving the video between docked, fullscreen and the pop-out is faster.** Turning video on keeps
  the music playing until the first video frame, an outdated load is cancelled, and leaving fullscreen no longer drops
  the video to its lowest quality. (#173)
- **Steadier video playback.** Lyrics, the player-bar text and the equalizer no longer redraw the window when they are
  hidden or the window is covered, the player-bar title glides at the display rate as before and keeps scrolling beside
  a video, and the diagnostics log now records dropped frames and how long each switch takes. (#174)

## [0.3.0] - 2026-10-02

Crest is Wavee rebuilt from the ground up. Every page, the playback engine, Spotify Connect and the local library were
rewritten on one data model, so pages open faster, cards and rows behave the same everywhere, and scrolling runs on a
new model shared by wheel, touchpad and scrollbar. On top of 0.2's features it adds a new Home, pages for podcast
episodes and for people, playback speed, a notification-area icon and opt-in crash reports you can read before
they're sent.

### Added

- **A new Home.** A pivot across the top switches between All, Music, Podcasts and Audiobooks, with a Following toggle
  beside it; the daylist gets its own card, and the page below is laid out in zones. Home has its own customizer, where
  sections can be shown, hidden and reordered, and the change shows on Home at once.
- **Podcast episodes have their own page.** An episode opens with its chapters on a timeline, its transcript, the
  discussion and its ratings. Where you stopped is kept with your Spotify account, so an episode resumes from the same
  place on every device.
- **Playback speed.** While an episode plays, the player bar has a speed control.
- **An Audiobooks page in your library.** Saved audiobooks get their own library page beside Podcasts, with its own
  filters.
- **A notification-area icon.** Wavee can sit in the corner of the taskbar: closing or minimizing the window can hide it
  there and keep the music playing, it can start hidden when it starts with Windows, and the icon's menu opens or hides
  Wavee, picks a device, saves the current song to Liked Songs and quits. Settings decides whether the icon shows
  always, only while Wavee is hidden, or never. (#152)
- **Profile pages.** A `spotify:user:` link, a playlist's owner, a user in search, a friend in the activity rail and
  the account menu's new Profile row now open the person's profile: a round avatar hero with their followers,
  following and public playlists, Follow, their public playlists and recently played artists, who they follow and who
  follows them, and — on your own profile — your top artists this month. Following and Followers open full list pages
  with filter chips, a find box and a letter strip. The page loads the same profile view the official client does and
  re-reads it on every visit without flashing back to its skeleton. (#161)
- **A real full-screen Now Playing.** The lyrics stage is now a true full-screen view: the window goes borderless on
  its monitor, the title bar and the player bar step aside, and one transport card sits over the music. A selector
  switches between Lyrics, Visualizer, Up next and Artist while the cover morphs between its hero and thumbnail
  places; the accent follows the album art on every control; the controls hide after three seconds of idleness and
  a hairline keeps the progress visible. Eight visualizers — Field, Halo, Horizon, Matrix, Aurora, Spectrum, Pulse,
  Tape — move on a real spectrum analysis of what is playing, aligned to what you actually hear (with a sync offset
  for Bluetooth), and fall back to Spotify's waveform and beat data when the audio is not local. A gallery shows all
  eight live, with sensitivity, lyrics-overlay and calmer-motion switches; the window adapts to ultrawide, portrait
  and small sizes. The rail's analyser decks now read the same real spectrum. (#166)
- **The sidebar resizes freely and folds into an icon rail.** Drag its edge anywhere between 180 and 460 DIP; it no
  longer collapses on its own when the window gets narrower. Dragging past the minimum snaps it into an icon rail in
  one of three sizes — Compact (48), Default (56) or Large (80) — which you can also pick from the right-click menu's
  "Collapsed rail size" or in Settings › Appearance › Sidebar. A double-click on the splitter collapses or expands it,
  and with the splitter focused the arrow keys step its width.
- **In a narrow window the sidebar opens as a drawer.** When the window is too narrow for the sidebar and the page side
  by side (under 660 DIP), the sidebar shows your icon rail and the full pane opens as a drawer over the page.
- **Opt-in crash reporting.** Off by default: the setup wizard asks once, and Settings › Privacy & diagnostics offers
  Off, Ask each time or Automatic. After a crash or a hang the next launch says what happened and lets you read the
  full, redacted report — and attach a memory snapshot if you like — before you send it. Settings › Privacy &
  diagnostics › Crash reports lists every saved report; each row's menu offers View, Copy, Send and Delete. When Wavee
  crashes twice in a row before its window appears, it opens a small recovery window instead that can start it again,
  send the report or reset Wavee. "Delete my data…" erases every report this PC ever sent. A report says "sent" only
  once the upload has succeeded; one made while you're offline waits on your PC and goes out when you're back online or
  at the next launch; a report you deleted is never sent; and a large report with a memory snapshot gets time in
  proportion to its size. A build you compiled yourself can't send reports automatically, so it offers Off and Ask each
  time and says why. Reports go to Wavee's own crash service at crash.cproducts.dev; `PRIVACY.md` lists exactly what
  one contains. (#165)
- **Crash reports resolve to method names.** Store, stable and beta builds all carry the crash service's address and
  key, and every release uploads its symbol map, the Microsoft Store packages included, so a report arrives with
  Wavee's own method names instead of bare offsets. A release can no longer ship without them. (#165)
- **Native crashes name the faulting module and Wavee's stack.** A crash inside a driver or a Windows library now
  reports which program file it happened in and where, plus the Wavee code that called into it, so these crashes
  group by cause instead of all landing in one bucket. Their memory snapshots keep the exception record, so a
  debugger opens straight on the faulting instruction. (#165)
- **Send a test crash report (Developer mode).** Settings › Privacy & diagnostics › Developer can now build a real
  crash report without crashing — a caught test exception thrown through Wavee's own code, the redacted log tail and a
  memory snapshot of the running app — and send it to the crash service the normal way, then say whether it arrived,
  with its report id. Wavee keeps running, and the next launch doesn't mistake the test for a crash. (#165)
- **A Privacy & diagnostics tab.** Settings gets one tab for what Wavee records and what leaves your PC, in place of
  the Logs tab and the rows that were scattered through General. It starts with what leaves this PC (Spotify, lyrics
  providers, GitHub, and the crash service only when you opt in) and the three-way crash-report choice, Off, Ask each
  time or Automatic, with the memory-snapshot switch beneath it; "Delete my data…" and the privacy policy sit beside
  your install id. Saved crash reports are one list, each with its own "…" menu. Under Logs it shows the session's
  events, warnings and errors as they change, and the two detail levels; Playback runtime, Spotify Connect and Realtime
  capture are together under Tools; and the developer switches share one group at the bottom.
- **The log viewer is a page of its own.** It opens from the tab or from `wavee://open?route=logs`, and Back or Esc
  returns to where you were. The session picker and Open folder sit in the header, and one toolbar holds the filter,
  the level segments with their counts ("Warnings · 368"), the category list, Copy, Export and a "…" menu for Newest
  first, Group repeats, Wrap long lines, Verbose, Clear view and Report this session…. The Time header sorts the list,
  and the footer says how many events are shown and loads more.
- **Scrubbing is audible.** Dragging the seek bar now plays what is under the thumb: short, soft snippets that follow
  the speed of your drag and fall silent 150 ms after you stop moving, with one seek landing when you let go. A music
  video shows the frame under the thumb as you drag. While a song is paused, or playing on another device, the drag
  stays visual and a single seek is sent when you let go. Audible scrubbing covers Ogg Vorbis and FLAC; other formats
  scrub silently. (#167)
- **Keyboard seeking has a speed ladder.** With the player bar or the full-screen view focused, Left and Right step 5
  seconds; keep a key held and the steps grow to 15 seconds after half a second and 30 seconds after a second and a
  half, at most one step every 150 ms, and Shift makes a fine 1-second step. The bar and its time label show where you
  are heading, and one seek lands 250 ms after the last press; a live podcast stays inside its window. (#167)
- **Normalization has modes.** Settings › Playback now offers Quiet, Normal and Loud (−23, −14 and −11 LUFS) and an
  Album option that keeps the loudness differences between an album's tracks. They apply to Spotify streams, lossless
  and local files (their ReplayGain tags), and a change is heard on the song that is playing within a moment instead of
  from the next one. A first run also starts at a volume of about −6 dB instead of −9 dB. (#167)
- **A "Playback health" card on the Diagnostics page.** It says whether this session's audio has been clean, how many
  dropouts there were and how long the longest one lasted, the likely cause (the decoder fell behind, the output device
  was late, a garbage-collection pause or a slow download), whether the audio threads got Windows's audio priority
  registration, how full the decode buffer is, and how long the last seek took and how it was served. Every dropout is
  also one `audio.glitch` line in the log, and each session ends with an `audio.session.summary` line. (#167)

### Changed

- **The full-screen view's lyrics stay sharp.** Lines other than the one being sung are no longer blurred there (the
  sung line keeps its glow), which was the view's biggest per-frame cost. The Winamp deck's oscilloscope option is
  gone: there was no waveform to draw, so the trace was synthesised. (#166)
- **Scrolling runs on a new model.** Every list and page scrolls through one motion plan per viewport, shared by the
  wheel, the touchpad, touch, the scrollbar and the keyboard, and posed at each frame the screen shows. In practice:
  the wheel glides from notch to notch instead of stepping and pulsing, and works over a track list's column header
  too; a touchpad swipe moves the distance your fingers did, without overshooting or a hiccup where the coast takes
  over; a fling ends cleanly instead of crawling for a second, and doesn't stall or drop rows when a page grows under
  it; leaving a page mid-fling no longer leaves the app in "scrolling" mode; and a page you return to lands on its
  position at once instead of creeping into it. Sticky headers, parallax and edge fades follow the same plan, and
  scroll positions are remembered per tab.
- **Spotify Connect follows the device that's playing.** While your phone plays, the Queue panel mirrors its queue and
  what it's playing from, and a click on one of its rows skips there. A song you start in Wavee starts that song on the
  phone instead of just resuming it, from a row or a page's Play button alike. Picking "This computer" in the device
  picker takes playback over, mute is one state that every mute button reads, and the connection recovers on its own
  after sleep or a network change.
- **The queue survives a restart.** The whole queue comes back — the songs you queued by hand and the autoplay
  suggestions — with their titles and "Playing from", instead of only the current song.
- **Wavee decodes the audio itself.** Ogg Vorbis and FLAC now play through Wavee's own decoders, and a seek lands
  exactly where you asked. Lossless is offered when it's available.
- **Decryption keys are kept between launches.** A song you have played before starts without fetching its key again;
  the keys are stored encrypted on your PC, tied to your account.
- **Browse sections load whole.** A section page loads all of its items at once instead of paging them in, the Charts
  card shows Spotify's real chart playlists, and a section that can't load offers Retry instead of an empty page.
- **A finished daylist says it is updating instead of counting to zero or keeping the old edition.** When a daylist
  window ends, Home and the daylist page show "Updating your daylist…" with a spinning ring and the next daypart on its
  way, and the hero card, the playlist page and its tracks move to the new edition as soon as Spotify has it — no
  relaunch needed. The first check goes out 5 seconds after the window ends, with a few more over the next minutes
  (and on wake from sleep or when you return to the window) while Spotify still serves the old one; if the new edition
  is still late after that, Home says so and offers Check again. A newer edition always replaces the saved one, the
  countdown stays in step with the clock after sleep, opening the daylist page no longer erases the hero's masthead,
  and the hero card shows the daylist's whole cover art. (#162)
- **Developer mode and the FPS overlay apply at once.** Turning either on now shows the lyrics inspector, the
  test-notification rows and the overlay straight away instead of after a restart, and the items under Developer grey
  out while developer mode is off.
- **The capture and file log levels left the viewer.** The two level lists that sat in the viewer's filter row are
  Settings › Privacy & diagnostics › Logs › Detail level, "In the viewer" and "In the log file"; the viewer keeps
  Verbose in its "…" menu.
- **Log files are kept for 7 days, up to 250 MB, instead of the newest 7 files.** At about 60 MB a day the old rule held
  little more than a day, so past sessions were usually gone. A new file still starts each day or at 10 MB, the file
  Wavee is writing to is never removed, and a size roll is named `wavee-yyyyMMdd-HHmmss.log` in local time.
- **Memory samples and the engine's pacing lines are no longer logged as warnings.** A memory sample warns only when
  the working set reaches a new peak, and the render-pace, wake and display lines are information, so the Warnings
  count in the viewer shows what needs a look and the log files stay small.
- **Cleaner sample-rate conversion and a transparent limiter.** A 44.1 kHz song on a 48 kHz output is now resampled with
  a 64-tap windowed-sinc filter instead of straight-line interpolation, so bright material no longer carries aliasing.
  The limiter that catches EQ boosts and loud masters looks 2 ms ahead of the music so it can ease peaks down in time,
  and it now sits before the volume control, so the tone no longer changes with the volume slider. (#167)

### Fixed

- **Audio no longer halts or stutters while the computer is busy.** The threads that decode ahead and keep time now run
  with Windows's audio scheduling priority and are exempt from power throttling, so a game, a build or a minimized
  window no longer starves them. The decode buffer is 2 seconds deep (it was half a second), with the last second kept
  behind the playhead, and when it does run dry the output fills the gap with a short silence and resumes behind a
  larger cushion instead of stopping and restarting the audio device over and over, which sounded like a buzzing
  stutter. A lost output device is noticed at once instead of leaving pause and seek hanging. (#167)
- **Seeking is instant inside what is already loaded, and the seek bar never jumps back.** A seek within the last second
  or the next two seconds of decoded audio is a 5 ms crossfade. A seek into data that is already downloaded opens a
  second decoder and swaps over while the old audio keeps playing until the new spot is ready; a far seek lets the old
  audio run for up to 80 ms, then goes quiet until the new position arrives. Several seeks in a row land only the last
  target, and a second seek can no longer end a Vorbis track early. The bar and the time label stay where you dropped
  them, in the player bar and the full-screen view alike and while another device plays over Spotify Connect (the bar
  jumps at once and one seek is sent), instead of stepping back to the old position until the next report. (#167)
- **Browse section pages failed to load, showed their title twice, and a decade lost its parent crumb.** Featured
  Charts, Food, drinks & music and every other section drill went straight to "Failed". The page now loads its
  section, shows its title once in the masthead, and a category tile on a category page drills with that page as its
  parent, so Decades › 00s keeps its breadcrumb. Home's "See all" carries the section's title and the Home crumb too.
  (#156)
- **Section and Browse grid cards had no hover, play button, menu or now-playing pill.** The section drill grid
  (Browse › a category › a section, Home "See all", Featured Charts) and the Browse category grid drew their own cards.
  Both now use the app's one shared card — hover plate, hand cursor, play button, "…" and right-click menu,
  now-playing pill, drag, and a tooltip on a title only while it is cut off — and their loading skeletons are the
  card's own shape. A click on a cut-off title inside a grid now opens it. (#157)
- **Cards and rows behaved differently depending on the page: some had no hover, no click, no menu or no play button.**
  Every media card, row and tile is now one shared surface, so the hover plate, hand cursor, focus ring, play button,
  "…" and right-click menu, now-playing pill, drag and a tooltip on a cut-off title are the same everywhere. A card
  lights up while its album, playlist, artist or show plays, and its play button then pauses instead of starting it
  again. The rail's "Next up" rows can be clicked (they skip to that song, exactly as the queue panel does); playlist
  recommendation rows get a row click, play button and menu; Home and Recents episode rows get the episode menu; album
  and artist music videos get a real play button, menu and drag; the album page's "More by" rows get their menu;
  Search and library search results, queue rows, concert and module cards follow the same rules; and the sidebar's
  pinned cards, grid tiles and collapsed-rail tiles gain hover, focus, the now-playing pill, drag and (on the rail) a
  right-click menu. Text-first tiles (podcast doors, show cards, Browse and chart tiles, the artist pick, a clickable
  detail cover) keep their own look but take the same hand cursor, keyboard focus and title tooltip. The play button
  now appears when you hover a card, not only when the pointer lands on the button itself, and a card's hover
  controls are only built when they're needed, which takes noticeable time off opening an artist page. (#160)
- **Library › Artists crushed the artist name and the album titles to one letter in a narrow reader.** The artist band
  and every album head were single rows in which only the text could shrink, so at a ~390 DIP reader the name read
  "Tro…" and titles "P…". Below 772 DIP the band stacks its controls under the name (which may take two lines), below
  640 DIP the album head puts the title on its own line above the length and the circles, the album pane's "Open album"
  link no longer clips, and a cut-off name or title shows its full text in a tooltip. (#158)
- **Shelf cards took two Tab stops, and arrow keys after a click started from the old card.** A card inside a shelf now
  leaves focus and the click to the shelf's slot: one Tab stop per card, arrows move from the card you clicked, and the
  card still lights up its play button and "…" while its slot has keyboard focus. (#159)
- **Podcast pages: filtering, right-click, overlapping cards and the toolbar.** A filter or a search now shows just
  the matching episodes with a count that matches the rows (sorting keeps the resume card and Up next); a search that
  matched only the resume episode no longer shows "1 of 326" over an empty list. Up next, the resume card, the
  new-visitor cards and More like this have a right-click menu; Up next shows covers when the episodes aren't
  numbered instead of a blank column, and no longer repeats what New since your last visit shows. Cards no longer
  overlap the next section when a filter changes the list's width. The toolbar measures itself: the search box folds
  to an icon before any filter chip is cut off. More like this loads once per visit instead of on every keystroke.
  (#164)
- **Covers no longer turn blurry or stay blank.** A sharper cover already on screen is never replaced by a softer copy
  (the soft podcast covers), and artwork past the first few of an artist's top tracks no longer stays blank.
- **Your own playlists showed an empty tile in the side rail until their page had been opened once.** A playlist with
  no cover of its own shows a 2×2 mosaic of its first four album covers; the sidebar now fetches those covers itself,
  in one batched request, and keeps them on disk, so the mosaic paints as soon as the playlist list loads after a
  relaunch. Editorial playlists with a real cover are unaffected.
- **A cold playlist open showed empty rows between the shimmer and the tracks, and the detail rail snapped in.** The
  track list now stays in its skeleton until the first page of rows knows its title, album, duration and art, so a
  cold open no longer flashes blank rows, and a row further down keeps its placeholder until its own data lands. The
  two-column detail rail has a skeleton shaped like the loaded rail (cover, owner, title, meta, Play, blurb) that
  cross-fades once when the header answers — album, playlist, show and Liked Songs share it.
- **Artist and album pages flashed the app accent before their own colour.** Play, links, the pivot underline, hearts
  and the queue's Shuffle/Repeat/Autoplay chips painted blue for a few frames and then flipped to the cover's colour.
  Cover colours are now kept on disk (warm on relaunch), the album carries Spotify's extracted cover colour like the
  artist header already did, and every accent surface holds the previous page's colour until its own is known,
  cross-fading when it lands.
- **The artist hero popped in at full brightness under a blue veil.** The photograph now fades up, and the veil and wash
  take the artist's own header colour on the first frame — neutral when there is none — instead of the app accent.
- **The album page's right column jumped when its sections landed.** The column reserves its space once from what the
  album already knows (a single reserves no rows block), the remaining difference eases instead of snapping, the About
  card no longer stretches to fill a short page and then shrinks as "More by" fills in, and the watch-video card and
  "Fans also like" arrive together with the tracklist rather than a beat later.
- **Album track lists dropped their shimmer for two rows and then filled in.** The list stays in shimmer until its
  first page is in (twelve rows, or all of a shorter list).
- **The album's About and Featured-on sections appeared one after the other.** The band waits for both, or 400 ms,
  before its one swap, and asks for them at the tracklist's priority.
- **An artist's Top tracks could shimmer forever after a network hiccup.** A partly refused answer no longer seals the
  missing fields for the whole session: they are asked again when the session comes back online, the band shows Retry
  when nothing answered, and Retry re-asks everything the band needs.
- **Search painted its facet tabs a beat before the results.** The tab row now holds its skeleton until the query's
  first results have answered and enters with the same rise-and-fade, so both arrive on the same frame.
- **The tab's label changed before its page did.** The label follows the page and switches after the outgoing
  page's exit.
- **A tooltip outlived the page it described.** A tooltip opened from a row that navigated away under a still pointer
  stayed open; every navigation now closes the open tooltip.
- **Discography cards painted their hover plate and selection outline 20 DIP past the meta line.** The card grew into
  the grid's row gap; it is pinned to its own height.
- **An expanded discography drawer showed a grey stub pill under its rows.** The floating selection bar now appears
  only while something is selected.
- **A single's "Watch the official video" card read "0 songs".** An unknown track count is no longer shown as zero.
- **Artist biographies showed `&#34;` and `&#8217;` as text.** One HTML-entity decoder now handles numeric, hex and
  the named set for the bio lead, the About card and rich text alike.
- **The verified check on the album's About-the-artist card sat at the far right.** The name shrinks to its text
  with the check directly after it.
- **Emoji rendered as grey silhouettes.** Colour emoji now draw in colour.
- **The search box showed a grey completion while it was not focused.** The inline suggestion now shows only while the
  box has focus and clears when you leave it.
- **The title bar could stay on "Connecting…" after you were signed in.** It now turns into your avatar as soon as the
  account is ready, without needing a resize.
- **The Queue panel clipped its Shuffle/Repeat/Autoplay row and squeezed its rows in a narrow panel.** The chip row
  wraps instead of clipping, and queue rows keep the title and artist readable.
- **The player bar's transport jumped and the seek bar breathed when a track started.** Every slot on the right of the
  bar is now reserved up front and only its contents fade with playback state.
- **Leaving the now-playing title mid-scroll left it cut off.** The scrolling title glides back to its start when you
  move away, and the bar's title scrolls on its own at a slower pace.
- **Playing another track from the same album or playlist re-asked autoplay and appended fifty rows each time.** The
  autoplay suggestions already in the queue are kept when you play from the context that's already playing.
- **Autoplay suggestions appeared only after a skip, and never for a restored session.** Suggestions — like a context's
  next page — are now asked for when about three quarters of the current run has played (or a few rows remain), an
  ask made offline is retried once you're back online instead of giving up, and they follow on from Spotify's previous
  answer instead of repeating it.
- **The next track was not warmed for a restored session.** The next two songs are prepared ahead of time after a
  restart too, and again once the session is online.
- **Spotify Connect lost Wavee as the active device every couple of minutes.** The connection to Spotify timed out
  just before Spotify's own keep-alive arrived, so the session dropped and re-registered the device — hundreds of times
  a day — and every other client saw Wavee leave. It now outlasts two keep-alives.
- **A relinked liked track showed as blank and could not play.** Spotify serves some old track ids under a newer id;
  the old id now picks up the newer one's title, art, artists and duration, and playback picks a version that's
  allowed in your market.
- **A liked track Spotify no longer resolves rendered as an empty, playable row.** Such tracks are now marked
  unavailable: dimmed, "Unavailable" in the duration column, no play button, and hidden by the playable-only filter.
- **A failed load left the player stuck until another song was played.** Clicking the dead row now retries it, and
  Previous / Next stay available after an error so you can skip past it.
- **Playback stopped at a dead row.** Next / Previous and the natural advance now skip unplayable rows, a song that
  fails to load during an automatic advance skips to the next playable one (at most three in a row), and the queue
  labels such rows "Unavailable" instead of showing loading bars.
- **Music videos stuttered a few seconds in, and again every time the pop-out was reopened.** The video picked a low
  quality from a pessimistic guess, then rebuilt itself twice as it measured the real speed. It now measures fast
  connections correctly, never changes quality on a guess, and remembers the last measured speed across launches to
  pick the starting quality.
- **A track with a music video didn't always show the video badge.** Playlist tracks Spotify lists under an older id
  never showed it, a cached track could lose it for good, and the Classic track list never drew it. All three show it
  now.
- **The player bar kept an empty hole where the video button would be.** The video button's space now exists only
  while the current track has a video; volume, lyrics and the rest take its width otherwise, and the bar eases between
  the two widths.
- **Scrolling stuttered while covers were downloading.** With covers and colours streaming in, track rows, cards,
  tooltips, section headers, the sidebar, the playlist page and the lyrics rows re-rendered on every scroll frame.
  Updates are now paced while a scroll is live, and each of these redraws only when a value it actually shows changes.
- **Track rows rebuilt themselves on every recycle.** A row reused during a fling rebuilt its whole contents, up to
  40 MB of garbage in one fling on a long playlist; each row is now built once and only its values change.
- **Cover uploads hitched the frame.** A downloaded cover was copied again before it reached the GPU, through a freshly
  created upload buffer each time. It now goes straight to the GPU, upload buffers are reused and budgeted per frame,
  covers for rows just off screen are fetched at a lower priority, and the cover cache on disk trims as designed.
- **Cover downloads churned shared memory.** Download buffers are sized from the response and reused from a dedicated
  pool.
- **Storage › Delete old logs deleted the file Wavee was writing to.** It matched today's `wavee-<date>.log` along with
  the old ones, so the folder emptied until the next roll, past sessions were gone and a crash report's log tail came
  up empty. It now keeps the file Wavee is writing to, as its confirmation always said.
- **A crash report's log tail could be yesterday's file and miss the lines that led up to the crash.** The tail is now
  the day's file at the moment of the crash, read after the log queue is flushed, so it ends with those lines.
- **The log viewer's session picker looked empty without saying why.** It now says when it is reading, when there are
  no past sessions on disk, and when the log folder couldn't be read, and every walk of the folder logs one
  `log.sessions.listed` line.
- **Export session on the live log exported only the visible rows, without timestamps.** It now writes the whole
  session with timestamps whatever the filter, and a past session's footer says how much of it is shown ("last 4,096
  of 17,936 events").
- **A deep link that arrived with a trailing NUL carried it into route ids and log lines.** The NUL is trimmed before
  the link is parsed.
- **Diagnostics flagged handled playlist and collection updates as ignored.** The flight recorder marked every
  `hm://playlist/…` and `hm://collection/…` push as an ignored frame although the library applied it — hundreds of
  false ambers a day in the anomaly list. A frame is now marked ignored only when nothing took it, with whether that
  was on purpose. (#163)

### Known limitations

- **The API console is gone from Developer mode.**
- **Library edits made while offline are not replayed later.** 0.2 kept a queue of failed library changes and sent
  them at the next sign-in; 0.3 doesn't.
- **The output-device list only remembers your pick.** Audio follows the Windows default output device.

### Developer

- **`Entities.Invalidate` / `InvalidateEdge` — the planner's fifth mark, `Stale`.** A known group can now be declared
  out of date without blanking it: the row keeps rendering, the planner re-asks through the ordinary demand path, the
  next answer at any authority lands, and the mark clears itself. The daylist rollover is its first user. (#162)
- **Scroll diagnostics.** Every wheel/drag burst writes a `scroll.burst` line (the engine's own per-burst verdict,
  written while the scroll probe is at Summary or Trace), and Diagnostics gains a Scroll card: probe level, feel
  profile and a CSV export to `logs/scroll-<timestamp>.csv`, both settings persisted.
- **`frame.slack`.** When the loop did not run for more than 12 ms while a scroll was live, a `frame.slack` line now
  says whether the gap was a GC pause, the wake model sleeping or pre-emption, so a hole mid-drag names itself instead
  of showing up as an unattributed long frame.
- **`scroll.frames` names the sidebar's re-plans.** Every wheel/drag burst's `scroll.frames` line also says which
  planner input made the sidebar re-plan during the burst (`sidebarReplans= railBumps= causes=`).
- **`nav.frames` reported idle refreshes as missed vblanks.** The counter counts every refresh between two presents, so
  a page the user was reading logged "missed=301"; the navigation rollup keeps only the figures that count frames that
  ran long, and the scroll rollup keeps the cadence counter where the loop is continuously live.
- **`dotnet build Wavee.slnx -c Release` shipped the Debug engine in the app's output.** MSBuild unsets the
  configuration for project references that live outside the solution, so the engine built as Debug and its
  assemblies were copied into `bin\Release`; that engine runs a full-scene parity audit every frame (two thirds of all
  CPU, about 500 MB/s of allocation) and made every JIT run look pathologically slow. References now keep the parent
  configuration, and the startup log names the engine flavor (`engine=release|diag`).
- **Connect put-state warning.** Every accepted put-state whose echo does not name Wavee as the active device now logs
  a warning, so a lost Connect state can be traced.
- **Dutch and Korean crash and consent text are drafts.** The crash prompt, the recovery window, the saved-reports list, the
  privacy settings and the setup wizard's crash-reporting card have Dutch (nl) and Korean (ko-KR) drafts; both
  languages stay disabled in the language picker until their tables are reviewed. (#165)
- **`--stress-audio` load test.** `Wavee.exe --stress-audio --file <track> --burners 2 --memory-mib 2048 --minimize
  --seconds 60` plays through the real audio device while spin threads at every priority class, memory churn, a
  minimized window and (with `--battery-saver`) execution-speed throttling lean on the machine, then prints the glitch
  ledger and exits 0 only when there were no dropouts (`--allow-one` tolerates one), so "does it halt under heavy use"
  has a number instead of an opinion. (#167)

## [0.2.10] - 2026-09-12

Idle GPU and memory on the Snapdragon. Wavee sat at 50-68 % GPU and 520 MB-1 GB of working set with nothing on
screen moving, and the investigation behind this section is in `docs/plans/wavee/gpu-memory-investigation-2026-09-11.md`.
Almost all of it is engine work; the app side is the memory governor, the response buffers and the instruments.

### Fixed

- **Cached album pages could remain empty after startup.** An incomplete page opened before the live backend was
  ready now retries its normal catalog load when authentication finishes, preserving the cached header. (#118)
- **Playback did work for hidden meters and neutral effects.** Level analysis now follows visible meter demand,
  unity DSP passes skip their sample loops, and audio management sleeps until work arrives. Constant non-unity
  gain uses SIMD without changing ramps or PCM values. Snapshot text measurements no longer reserve a large
  payload for every non-text scene slot. (#118)
- **Artwork could flash during playback.** Image reveals now use shared wall time instead of the animation
  clock's clamped/resynced delta, so sparse UI publications cannot rewind a completed fade. (#118)
- **Paused immersive lyrics kept repainting the background.** The decorative drift timer now stops while paused
  and resumes from its held phase. (#118)
- **Pause could leave lyrics animating after audio stopped.** A late host position report can no longer overwrite
  pause intent while the asynchronous audio pause is still queued. Seek-bar position updates also avoid rebuilding
  its component tree. (#118)
- **Lyrics and scrolling triggered unnecessary full redraws.** Verified unchanged publications now preserve
  submission continuity, and off-window damage no longer counts toward the full-redraw cutoff. (#118)
- **An exit animation could disable its own cleanup backstop.** With animations owned by the render thread,
  an orphan could not request the UI frame needed to reclaim it. Ready or expired orphans now wake cleanup,
  and an otherwise idle window waits no longer than the existing two-second backstop. (#118)
- **Lyrics playback repainted static parts of the page.** The Liked Songs heart clip no longer rejects partial
  repaint when its mask and rendering layers are separate. Consumed damage now expires independently from new
  descendant updates, and an older snapshot cannot hold the damage history open after navigation. (#118)
- **The render loop never went idle.** Leaving a page while its scrollbar was still inside the two-second idle-hide
  after a scroll froze that row forever: it was skipped before the liveness check so it was never dropped, it was
  counted as needing a frame anyway, and the ticker's own parked-set was never cleared when the page was evicted —
  so a recycled node index inherited it and froze a *new* page's scrollbar too. The loop held at panel rate with
  nothing animating, for the rest of the session. Parking now lands the bar at rest and retires its row. A bar that
  was visible when you navigate away is gone when you come back, rather than resuming mid-fade. (#118)
- **Open lyrics held the UI thread at panel rate through every instrumental gap.** The per-frame stepper mounted on
  "is playing", so it ran for the whole of a track whether or not anything on the surface was moving: an instrumental
  intro with the panel open measured 4146 frames in 30 s, 4085 of them recorded and thrown away, to render 61. It now
  mounts only while a lane is actually in flight — a line being sung, a glow cross-fade, the σ ramp, a handoff
  cascade, the interlude dots, an owed follow landing or the detached/resync countdown — and an idle surface re-arms
  from a one-shot timer at the next syllable instead of polling for it. Nothing is lost across the gap: the wipe, the
  glow and the dots are read from the media clock rather than accumulated, so the first frame back lands exactly where
  it would have. An intro, a break between verses, an outro, a track with no timed lyrics at all, and a paused surface
  now cost the loop nothing. (#118)
- **Every awake frame repainted the whole window.** An animated node marked every ancestor up to the root as moved,
  and the renderer damages a moved node's subtree bounds — which at the root is the window. One scrolling title
  therefore repainted every pixel, at panel rate, against the renderer's own promise that "a spinner repaints a tiny
  region". A pose now damages only the node that actually moved: measured coverage for a small animated leaf goes
  from 100 % to 0.53 %. A row whose value did not change damages nothing at all, so a 60 Hz animation on a 120 Hz
  panel stops paying for the frames in between — and those frames now skip the scene recording as well as the
  present. (#118)
- **Two things moving in different corners forced a full repaint.** The partial-repaint path was capped at a single
  rectangle whenever anything on screen was mid-fade, so a scrolling title near the top and a playhead at the bottom
  were merged into one rectangle covering most of the window, which then failed the coverage test and repainted
  everything. They stay separate now. (#118)
- **A fading edge disabled partial repaint entirely.** Every list in Wavee has a soft fade at its edges, and that
  alone made a frame ineligible no matter how little of it had changed — so in practice almost every frame repainted
  the whole window. (#118)
- **A blur on screen did the same thing, for the whole of playback.** The lyrics surface softens the lines around the
  one you are on, and any blur at all disqualified the frame from partial repaint — so with lyrics open, every frame
  repainted every pixel at the panel's rate while a single wiping line was all that had actually changed. That was
  the difference between 69 % of the GPU and a strip. A blurred surface now repaints the strip: the renderer draws a
  blurred group's source slightly wider than the damaged region so the blur has real pixels to read at the edges,
  redraws only the damaged part, and grows a moved thing's damage by the reach of any blur above it so nothing stale
  is left ringing it. Verified pixel-for-pixel against a full redraw on the Snapdragon's GPU. (#118)
- **The weak-GPU memory budgets never applied.** The adapter was classified *after* the image pipeline had already
  been sized, and an unclassified adapter counts as a discrete one, so every UMA machine — the Snapdragon included —
  silently ran with desktop-GPU budgets: 32 MB of pixel pool instead of 16, 64 MB of image cache instead of 24,
  16 MB of blur cache instead of 8. (#118)
- **Album art was budgeted at the wrong size.** The cache counted decoded pixels while the GPU commits a rounded-up
  square texture, so a 150-pixel cover was counted as 90 KB and actually held 262 KB. The cache believed it was
  inside its cap while holding roughly three and a half times it. Covers are also now released when you navigate
  away from a page, instead of only when some unrelated image finished loading. (#118)
- **A video kept its decoder alive for the whole session.** Playing one video left a second graphics device, a Media
  Foundation engine and its decode surfaces resident until the app closed — none of it visible to any memory
  counter, because the app only ever receives a handle to it. It is now released after the video has been idle for
  half a minute, while skipping between tracks still costs nothing. (#118)
- **The memory governor could never fire.** It read whole-machine memory pressure, which on a 16-32 GB laptop stays
  near zero while Wavee holds a gigabyte, and nothing was registered at the level it sheds at when pressure is
  normal — so the thirty-second check freed nothing, ever, on either count. (#118)
- **A playlist could sit on placeholder rows forever.** Opening one from a link whose address carried an encoded
  trailing space sent that space all the way to Spotify, which rejected the request — and because the track list had
  no way to say "this failed", it went on shimmering as though the songs were still on their way. Nine minutes of it
  in one session, over a playlist that had opened perfectly a few minutes earlier. Link addresses are now trimmed
  after they are decoded rather than before, and a fetch that dies says so, with a Retry, instead of pretending to
  load. A list that already has songs never blanks itself into an error if a later refresh fails.
- **A pending tooltip kept the UI thread awake.** The tooltip's show delay, its five-second dwell and the menu and
  command-bar cascade timers were an invisible animation polled from a per-frame re-render, so hovering anything while
  scrolling pinned the loop at panel rate: one twelve-second scroll counted several hundred needless tooltip renders
  and 463 frames held awake by that poller alone. They are now one-shot host timers that fire once and never
  re-render; the delays are unchanged. (#118)
- **A fading edge leased a 56 MB scratch for a few strips.** The edge-fade snapshot stacked its four edge strips
  vertically at the widest strip's width, so a full-window fade asked for a 1792×8192 surface to hold under a
  tenth of that area. The strips now pack side by side and the same fade leases 28 MB. (#118)
- **A scrollbar's hover dwell kept the loop awake.** The conscious scrollbar counted its 400 and 500 ms expand and
  contract dwells by re-rendering a stepper every frame, so every hover and every scroll bought a run of frames in
  which nothing moved. The dwell is now a one-shot host timer; only the 167 ms width tween and a held page-repeat
  still step per frame. (#118)

### Changed

- **Large responses are no longer copied twice.** Each catalog response was buffered and then copied again, and
  compressed bodies were copied a third time purely to satisfy a constructor — every one of them on the large-object
  heap, which nothing in the app ever compacts. (#118)
- **The diagnostics can now see GPU memory the engine does not own.** `mem.sample` reports the adapter's own
  per-process figure beside the tracked total, so the difference — Media Foundation's surfaces, the driver's arenas,
  shader compilation — is a named number instead of an unexplained gap. Image memory is also broken down per texture
  size rather than collapsed into one row, and frame repaint coverage is reported as a percentage, since every
  successful partial frame used to round to "0.0". (#118)
- **The frame log now names what is keeping the loop awake.** When something subscribes to the frame clock the loop
  runs at panel rate for as long as it stays subscribed, and the log could only say *that* one had — never which one,
  which turned a one-line answer into an afternoon of guessing. The thirty-second line now ends with the count and
  the components themselves (`pollers=1:LyricsFrameStepper`), and a gate holds both halves: that the names are right,
  and that the count falls back to zero when they unmount.

## [0.2.9] - 2026-09-11

The engine underneath is FluentGpu's "Operation ultra-fast" work, and much of the app side is what that engine
exposed — retained pages, per-frame component churn, art slots that never repainted. On top of it: the Now Playing
player styles, one authority for who owns Spotify Connect playback, lyrics that sweep and a blur you can turn down,
a pop-out video window you can move again, and a pass over every flicker, jump and colour flash a frame-by-frame
look at the app found. It also lands the always-on frame instrumentation, without which none of it could be
measured.

### Added

- **Appearance › Lyrics blur.** A strength slider (0–100 %) for the lyrics depth-of-field and glow blur; 0 removes
  every lyrics blur layer. Auto picks 40 % on a weak GPU tier (the Snapdragon's Adreno) and 100 % elsewhere. The
  depth-of-field ramp now writes in the renderer's own 0.5 σ buckets, and the active line's glow never nests inside a
  blurred layer, so a line hand-off no longer re-blurs the voice row every frame. Moving the slider applies at once,
  playing or paused, instead of at the next line. (#141)
- **The track expander shows who added the track.** "Added by" carries the same avatar + name chip as the Added-by
  column instead of a bare name. (#135)
- **Sidebar pins sync with Spotify.** The Pinned section mirrors the account's own pins (Spotify's `ylpin` set):
  playlists, albums, artists, shows, Liked Songs and playlist folders,
  read on sign-in and on every push, and written back when you pin or unpin in Wavee. Liked Songs uses the spelling
  Spotify actually sends (`spotify:collection`), a folder syncs by its rootlist id and renders disabled with a reason
  until its rootlist entry arrives, and pins Wavee cannot show (Your Episodes, Local Files, audiobooks) are left
  untouched on the server. A pinned item no longer appears a second time in Playlists, Your Library, the Library V3
  list or the rail — in every design, and a Liked Songs pin that arrives from Spotify is a proper route pin:
  titled, clickable and the same height as its neighbours. In the collapsed rail a pinned folder is a folder tile
  that opens the folder, not a playlist cover that does nothing. (#102)
- **Player styles for Now Playing.** The Now Playing rail's cover gets a header with a Cover | Player switch and a Player style flyout: twelve
  players in three rows — Record, Cassette, Reel-to-reel, CD/MiniDisc; Turntable, iPod Classic, Winamp, Hi-fi VU; Zune, WMP visualizer, Canvas
  drift, Picture disc — each with its own options (vinyl finish, size and speed, cassette shell and label, iPod body, Winamp skin, …). The
  record's tonearm cues, lifts on pause, seeks, rides the run-out into a locked groove and returns to its rest the way a turntable does. The
  choice is remembered per user and is also reachable from the artwork's right-click menu, Settings › Appearance › Now playing and the command
  palette. (#142)

### Changed

- **Sidebar shortcut rows are 40 DIP, and the Shortcuts band has no header.** Glyph rows (Your Library, DevTools,
  any Curated shortcut section) used the subtitle pitch of 44 while never showing a subtitle, so they read taller
  than the pinned rows beside them; they now take the ladder's Cozy-without-subtitle height in the pane and the
  rail. Home sits directly under the title bar; the quick layout menu moved to the Pinned header. (#134)
- **The app measures its own frames.** `frame.slow` writes one line per frame over the panel's refresh interval
  — rate-limited but counted — with the phase split, GC deltas and, when the render census fired, which
  components rendered and which allocated. `nav.frames` rolls the four seconds after a route change up into fps,
  counts over budget / 33 ms / 100 ms, missed vblanks, time to first frame and the worst frame; `scroll.frames`
  does the same for a wheel or drag burst; `mem.sample` records the working set at each window close and at
  process end. The census is on unconditionally: a slow frame that cannot be attributed is a slow frame that does
  not get fixed. `ops/tools` gains the readers — perf-tour, perf-tour-analysis, nav-measure, nav-live-check,
  scroll-measure, perf-profile and perf-ws-attribution.
- Startup lines read as a timeline: `WaveeLog` anchors `SinceStartMs` to the real process start, and a Core or
  Window activation step that exceeds the 8.3 ms frame budget logs at Warning instead of passing unremarked.
- **The shell keeps three pages alive instead of eight.** A parked page holds its whole element tree and signal
  graph, so eight of them was most of a session's navigation history resident at once — over one perf tour that
  took scene nodes from 494 to 15668 and live components from 93 to 1479, with the working set never coming back
  down. Three is the live page plus a two-deep back stack. Parked pages already release their image pins, so a
  deeper back-navigation costs one rebuild, never a re-download.
- The player's elapsed-time label no longer re-renders its component on every playback tick. Reading the playhead
  in `Render` subscribed the whole component — box, hover and pressed fills, click handler and caption — to a
  signal that moves at tick rate, to produce a string that changes once a second, and two of these are mounted at
  all times. The label is now a bound channel: a tick writes one text value and nothing re-renders.
- Every shelf passes its collection rather than a count, following the engine's retained-shelf rework (21 call
  sites across 9 files). A shelf now does its own capping, so a card callback can no longer be handed an index
  past the end. Mechanical port: no shelf changes its reserved height, page size or measurement mode.
- Ambient motion is paced per source (`DefaultLoopHz`), not by a host-inferred cap.
- Scrolling and pointer motion feel closer to the finger: the present queue no longer holds a finished frame
  back a whole refresh, and the render loop picks the frame to show after the present slot opens rather than
  before. Roughly one to one and a half refreshes less latency at the same frame rate.

### Fixed

- **The render loop never went idle, and the GPU sat at ~70 % with the Lyrics panel open.** A pointer resting over a
  scrollable view held a scrollbar row forever, parked scroll bodies stayed "active", and layout re-posted an
  identical scroll frame every frame — each alone enough to keep every frame awake, and an awake frame re-submits the
  scene whenever a pixel moves. The engine now separates "needs a frame" from "has a row", ignores parked bodies for
  wake, posts a scroll frame only when it changed, names the term in its `[wake]` line, and reports the real
  `blurHeld` count. (#136)
- **Lyrics: Musixmatch's anti-scraping decoy could win.** 206 uniform four-second nonsense lines running to 13:56 on
  a 3:30 track were chosen because an ISRC match was verified by construction, tier beat score, and a better provider
  could never replace the first winner. A document longer than the track or with uniform line timing is rejected as a
  decoy, a verified candidate must align with the reference text, the background pass replaces an unverified winner,
  and Musixmatch's own status code (captcha / quota) is honoured with a token refresh. (#137)
- **The karaoke wipe stepped, even with lyrics blur at 0.** Lyrics extrapolated media time from the 15.6 ms system
  tick and applied every position sample as a jump, so at 120 Hz the wipe advanced 0 or 15.6 ms a frame and froze
  after a backward correction; and the engine's render-thread capture skipped a node written every frame until the
  next full capture, about five times a second. Lyrics now extrapolate from timestamped position samples to the
  frame's own present time and correct drift by a bounded rate change, never a step; the capture records every write.
  The record player's clock reads the same frame time. `lyrics.clock` logs zero-advance frames and the largest step
  every 30 s. (#143)
- Lyrics no longer open with the provider's title/credit header when it carries a bracketed annotation or a
  trailing "title - artist" line (Kugou's Chinese credit block). (#144)
- **The pop-out video window could not be moved.** It lost its title bar when it became chromeless and nothing took
  the title bar's place. Dragging the picture now moves the window through Windows' own move loop (Aero Snap, shake
  and the snap bar included); a click, double-click or right-click keeps its meaning and a press on a control never
  moves the window. (#145)
- **Video controls hid and showed like a flickering overlay.** Only real user activity shows them now — a slow mouse
  creep counts, a resting hand's jitter does not; buffering, a quality switch or a new track never pop them up;
  hovering them, an open menu, seeking or a pause keeps them; leaving the window hides them within half a second.
  They fade in 150 ms and out 400 ms from wherever they are (the fade used to start at its end value, so it cut), the
  seek bar keeps its colour and thumb while it fades, and in the pop-out the cursor hides with them. Main-window
  fullscreen controls respond to the mouse again — an invisible click shield sat on top of them. (#146)
- **Navigation no longer flashes the window's colour.** The chrome tint (sidebar, title bar, player bar) dropped to a
  darker neutral for a few frames at almost every navigation and sometimes jumped back to the previous page's colour:
  a page cleared the tint when it left, the next page's colour was not graded yet, "no colour" was painted as
  transparent black, and a page still fading out could republish over its successor. Pages now hand the tint over —
  the incoming page claims it and keeps the current colour until its own is ready, only the current owner may
  refresh it, pages with no colour of their own ease to a real neutral, and a clicked card's already-graded colour
  seeds the destination. (#147)
- **Page transitions no longer superimpose two pages.** The outgoing page faded on an accelerate curve and was still
  ~70 % opaque when the incoming one started, so every navigation showed a frame of mixed, unreadable text; it now
  fades out fast first, and the incoming page slides 8 DIP instead of 30. (#148)
- **Late data no longer moves what you are reading.** The player bar changes track in one step — no old artwork, no
  old position against the new duration, no Play glyph flicker, and a reserved slot for the video button; its title
  no longer ping-pong scrolls while idle. An album page no longer shows the "Minified album view" notice or a wrong
  total duration while its rows load, and "About this release" appears once, complete. Artist pages keep one
  play-count format and never re-create the Top tracks rows when the column width settles. A daylist's countdown
  ticks. A playlist or mix opened after it changed waits briefly for the fresh copy instead of painting yesterday's
  and swapping it, and its rows only animate for real edits. Sidebar clicks hand the page a preview like Home cards
  do. Search and Browse skeletons have the shape of the page they stand in for. (#149)
- What's new: the highlight cards are the same translucent cards as the rest of the page instead of a darker opaque
  grey, the viewer's page dots are centred under the picture, and stepping between highlights no longer makes the
  viewer grow and snap back — it keeps the tallest highlight's size and cross-fades the text in place.
- **Less motion that was not asked for.** Wide track rows no longer shrink when pressed, cards under a resting
  pointer no longer grow right after navigating back, the sidebar equaliser honours reduced motion, and a lyric line's
  hand-off dims and brightens at the same pace. (#150)
- **Connect: who owns playback is decided in one place.** Six incidents had one cause — two ownership authorities
  (the controller's own flag and active-device transitions, the projection's raw active id with a 5 s wall-clock
  grace) and several writers that told Spotify `is_active=true` without asking who owns playback. While a phone
  played, Wavee restarted its last track every ten seconds at the phone's position and stopped it again as "stray";
  picking "This computer" resumed Wavee's stale session at the phone's playhead while the bar still said "Playing on
  iPhone", and the play button then went to the phone (403); a launch announced Wavee active over a playing phone;
  the transport showed Play while audio played; a takeover that arrives as an empty frame and then the phone's was
  read as a stray. Ownership is now one state — this device, another device, or nobody — folded from the server's own
  timestamps, with the server's answer to our claim as the verdict; routing, the audio host, now playing and the wire
  all read it. Picking this computer while another device plays asks Spotify to transfer playback here, a claim that
  loses is stopped at once, and a click queued behind a takeover is dropped instead of pulling playback back.
  Settings › Playback links a Connect diagnostics page, and `connect.owner`, `connect.cluster`, `connect.echo` and
  `playback.load` lines log every decision. (#138)
- **The player bar repeated a line: "LP / LP", "Damiano David / Damiano David", or the title where the artist
  belongs.** When "Playing on <device>" appeared above the title — or went away — the title and artist lines had no
  identity of their own and the engine matched them by position, so one slot reused the other line (frozen at mount).
  Both lines are keyed now, and the engine matches unkeyed siblings by their order among unkeyed siblings, so a keyed
  line inserted or removed in front no longer shifts them. The track metadata was never wrong. (#107, #139)
- **Video: the previous video's duration was adopted for the new track on a host switch**, so the seek bar jumped.
  Duration events are accepted only for the host's current source. (#140)
- **Storage & cache cards overlapped on narrow windows.** The per-location cards kept their intrinsic width and,
  once a description wrapped, painted under the next card: their accent-bar wrapper was a row, so the card never
  stretched and its wrapped height never reached the layout. It is a column now. (#132)
- **Collaborators showed raw user ids.** The face pile captured the playlist model at mount and never read the
  re-mapped model that carries the hydrated profiles, while the Added-by column beside it did. It now reads the
  page's live model, like the owner row. (#133)
- **Command-line probes were blind in Release builds.** `--spotify-collection`, `--spotify-login` and the other
  `--spotify-*` / `--backend-selftest` / `--qr-dump` / `--connect-live` arms logged only to the daily file, exited
  before the log queue drained, and — worst — a probe whose stored login Spotify rejected wiped the app's own signed-in
  credential. They now echo to the terminal, flush before exiting, print `added_at` per collection item, and never
  clear the credential; `.claude/skills/wavee/probes.md` says how to run them. (#131)
- **Syllable-synced (karaoke) lyrics played a whole track with no wipe and no held-note glow, then worked on the
  next play.** A lyric row freezes its line at mount, and the upgrade gate compared the per-line text alone — so
  when the aggregator answered with Spotify's line-synced transcription first and published the word-synced
  document a second later with byte-identical text (`text=1.00 lcs=69/69` in one capture), the rows were kept
  holding a line that had no syllables at all. The next play worked only because the disk cache then held the
  word-synced document. Keeping the rows now requires every field a row actually freezes to match: the text, the
  word timing, and both secondary layers. (#125)
- The profile chip and flyout header rendered the avatar's URL as text inside the circle instead of showing the
  picture — the photo was passed where the control expects initials, which outrank everything but a group icon.
  An account with no photo still falls through to its generated initials. (#111)
- A rename or a new profile picture made anywhere else — the web player, the phone, the account page — now
  reaches a running client instead of waiting for the next sign-in. Spotify pushes these over the dealer and
  nothing subscribed, so every one was logged unhandled. The apply is coalesced, because the service pushes per
  keystroke: the first push lands immediately and the rest collapse into one trailing apply, so the chip never
  spells out a half-typed name. It also updates playlist owners, collaborator piles and the added-by column. A
  push carrying no image updates the name and leaves the picture standing — several pushes during a rename have
  no image yet, and assigning them wholesale would blank the avatar for the length of the rename. (#126)
- Art slots that paint their neutral tile directly — sidebar rows and pins, every artwork grid cell, the mosaic
  cells — stayed flat grey no matter how correctly the cover plane graded them. The placeholder colour was
  evaluated once at mount and frozen; a landed grading now repaints exactly that tile, never a component
  re-render and never a global fan-out. (#127)
- Play and pause no longer tear down and rebuild the entire equalizer subtree. The element keyed itself on the
  animating flag, so a transition that should only start or stop an animation changed the key and made the
  reconciler rebuild everything under it. (#128)
- **Plugging headphones in or out while "Default" is the output device no longer silences playback until the next
  track.** When the new endpoint was not ready yet — or the old one was invalidated by the jack switch — the engine
  adopted a dead sink and then asked itself to rebuild every 80 ms, which kept postponing its own 250 ms debounce
  forever. A sink failure now starts a rebuild without postponing it, a device that is not ready is retried at
  250 ms / 1 s / 3 s while the previous one keeps playing, a failed device open is logged with the step and HRESULT,
  and a device-format change that cannot be reopened in place keeps the track audible or offers Retry instead of
  going quiet. (#112)
- **Resuming a track at its saved position no longer hangs on an endless buffering bar.** The launch restore (and a
  video-to-audio swap mid-track) seeks to the saved position on the same serialized queue that attaches the encrypted
  body a moment later; the new engine's seek waits for decoded audio at the target, the fast-start session held only
  the clear head, so the seek blocked the queue waiting for the very attach queued behind it. A seek that arrives
  before the body is attached is now parked and applied the instant the body lands, and the player shows the
  restored position instead of 0:00 while it waits.
- The editable playlist title no longer runs the engine's shrink-to-fit search against a width it is never drawn
  at. The hover pill's title declared no width of its own, so layout measured it at the row's leftover and again at
  its arranged width — two cache keys, up to two eight-probe searches per invalidation — and fitted it to a measure
  28 DIP narrower than the type plan solved for, so an owned playlist could snap one size smaller than the same title
  read-only. It now declares the exact width it gets: one key, one search, both arms agree. (#92)
- Decode-ahead runs on a dedicated above-normal-priority thread again. The engine's per-voice producer had moved to
  a normal-priority task, so on a busy machine (a compiler, an indexer) the decoder lost its scheduling edge and
  playback stalled to rebuffer; the `xrun` log line's `ageMs` field, which always printed 0, now reports the real age.
- A detail page's track list could re-render every mounted row for no reason: the row shape is a record struct
  holding an array, and a record struct compares an array by reference, so an otherwise identical shape compared
  unequal whenever the backing array instance changed. It is compared by value now. The array is usually
  reference-stable today, so this closes a latent bug rather than winning back a measurable cost — it matters
  when the cache misses, which is exactly when the list is already busy.

## [0.2.8] - 2026-09-04

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
- Navigation no longer fills in behind itself. Two deliberate engine render budgets — a cold list growing 4 rows a
  frame, and a returning page replaying its parked render debt 24 components a frame — were the reason a page kept
  assembling after it arrived. Both are off; a page realizes in the flush that opens it.
- The Library V3 destination rail shows which destination you are on (its accent underline had no width and never
  drew), starts on the same left edge as the rows beneath it, and its words are reachable at a narrow pane through
  hover chevrons with fades that only appear on a side that actually has more to show.
- Playlist save counts read as counts. "18713647 saves" is now compact, through the same formatter the Plays column
  uses, and the meta line wraps to a second line instead of ellipsising the duration away.
- The What's New viewer's page dots sit under the middle of the image instead of at its leading edge.
- The player bar's indeterminate seek sweep spans the bar. It was a hard 2400-DIP literal, so on a 3440-DIP
  ultrawide it stopped 1040 DIP short. (#94)
- Settings' Zoom row no longer shows a stale percentage. The chords, the Ctrl+wheel hook and the palette all change
  the live zoom without touching the settings store, and nothing subscribed to the engine's own change event — so
  zooming with the page open left the picker reading whatever it read at mount, with the app visibly at 200 % over a
  control still saying 100 %. (#93)
- Overlay veils no longer tint under the cursor and flash on press. A full-bleed dismiss surface carries an
  `OnClick`, so the recorder interpolated it toward an unset hover brush — the engine's own scrim pins both, and
  Wavee's did not. (#91)

### Changed

- **Wavee sizes itself to the display.** The zoom is no longer a monitor-blind number seeded at 100 %: on a large
  screen it derives its own default from the window's DIP extent, so a 3440-DIP ultrawide opens at 150 % instead of
  spending twice the design width on the same fixed constants. It takes the smaller of the two axis ratios, so it can
  never buy size by giving up structure, and it snaps to the 100/125/150/175/200 % plateaus where Windows' 4-epx rule
  lands on whole pixels. A manual pick or a Ctrl+± still wins and pins the mode to Manual, and an install that already
  had a zoom set keeps it. (#94)
- Album art decodes at the size it is painted. The decode budgets were raw DIP literals, so at 150 % zoom every cover
  was decoded at two-thirds of the resolution it was drawn at. (#94)
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
- **The detail page's left rail can be one size for every page.** Settings › Appearance › Track page layout, on
  Automatic, gains "Keep left-rail same size": resize the rail once and every album, playlist, Liked Songs and
  podcast page uses it. With it off, each surface keeps its own remembered width as before, and a "Clear all
  remembered sizes" action forgets them.
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
