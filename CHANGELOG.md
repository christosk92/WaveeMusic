# Changelog

All notable changes to **Wavee** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Releases are cut from the `wavee-v*` tag prefix — see `docs/guide/releasing-wavee.md`. (The FluentGpu engine/gallery
versions separately under `v*` and is not tracked in this file.)

## [Unreleased]

Scroll feel and the recording defects of 2026-09-16. The scroll work is engine-side (wheel plan, input pacing,
notch scale, header routing) and measured with a screen-capture probe before and after; the investigation and
the design are in `docs/plans/wavee/scroll-feel-and-recording-defects-2026-09-16-implementation.md`.

### Added

- **`Entities.Invalidate` / `InvalidateEdge` — the planner's fifth mark, `Stale`.** A known group can now be declared
  out of date without blanking it: the row keeps rendering, the planner re-asks through the ordinary demand path, the
  next answer at any authority lands, and the mark clears itself. (#n)
- **`frame.slack` and a `slackFrames=` count in `scroll.trace`.** When the loop did not run for more than 12 ms while a
  scroll was live, the frame line now says whether the gap was a GC pause, the wake model sleeping or pre-emption, so a
  hole mid-drag names itself instead of showing up as an unattributed `dtMax`. (#n)
- **`scroll.trace` separates a resting finger, clamp pins and structural rebases from real stalls.** A drag contact that
  is down but not moving for three frames or more is reported as `holdFrames=` instead of zero-motion stalls; a frame
  that pinned a body at a clamp carries a `p` marker and is counted in `pins=`; a frame that rebased a body (an anchor
  shift or a clamp rebase, which is not motion) carries `s`, is counted in `structural=`, and is left out of the stall,
  dip and jitter figures. Neither a held nor a structural frame can trigger `present-hitch` or `stalled-frames`. (#n)
- **A per-frame scroll trace in the always-on log.** Every wheel/drag burst now ends with a `scroll.trace` line: the
  kernel's per-frame displacement, the wheel notches applied, the refreshes that passed with no frame WHILE the list was
  moving, zero-motion frames, velocity dips, late frames and the step-jitter median, plus the shift sequence itself and
  a one-word verdict. The screen-capture probe that diagnosed the stepping needed synthetic input; this needs only a
  hand scroll. `ops/tools/scroll-reference.html` prints the same figures for Chromium on the same machine, so the
  browser's scroll and ours compare as numbers. The `scroll.frames` line also names which planner input made the
  sidebar re-plan during the burst (`sidebarReplans= railBumps= causes=`).

### Fixed

- **The daylist stuck at 00:00:00 and kept showing the previous edition.** Both countdowns disarmed at zero and nothing
  re-asked Spotify for the new window, so the hero card and the playlist page carried the old title, description, cover
  and tracks until a relaunch — and after a relaunch the persisted header outranked the fresh feed's. The rollover is
  now a scheduled data event: 30 s after the window ends the daylist row, its tracks and its home band are marked stale
  and re-fetched (retrying at 60 s, 2 min and 5 min while Spotify still serves the old edition, and again on wake from
  sleep or window activation), the digits read "Updating your daylist…" until the new window lands and then restart on
  it, a later edition outranks whoever wrote the earlier one, and the countdown re-anchors to the wall clock every 15 s
  so a sleep no longer desyncs it. Opening the daylist page also no longer erases the hero's masthead. (#n)
- **Your own playlists showed an empty tile in the side rail until their page had been opened once.** A playlist with
  no cover of its own renders a 2×2 mosaic of its first four album covers, but the sidebar only ever fetched the
  membership — which lands bare track uris, no album art — and left the tracks themselves to the playlist page, so the
  rail (and a pinned cover-less playlist) stayed dark after every launch. The sidebar now asks for the first eight
  member tracks' album and image itself, at prefetch priority in one batched request per rebuild, only for visible
  cover-less rows whose mosaic is still short; tracks that ever answered fill from the on-disk cache without a request,
  so a relaunch paints the mosaic as soon as the membership lands. Editorial playlists with a real cover are
  unaffected. (#n)
- **`dotnet build Wavee.slnx -c Release` shipped the Debug engine in the app's output.** MSBuild unsets the configuration
  for project references that live outside the solution, so the engine built as Debug and its assemblies were copied into
  `bin\Release`; that engine runs a full-scene parity audit every frame (two thirds of all CPU, about 500 MB/s of
  allocation) and made every JIT run look pathologically slow. References now keep the parent configuration, and the
  startup log names the engine flavor (`engine=release|diag`). (#n)
- **Long lists cold-mounted rows in the middle of a fling.** The virtualized window was sized exactly to the frame's
  desired range, so every direction change, deceleration or budget-limited frame tore down the receding rows and
  mounted fresh ones on the leading side a few frames later (8–12 cold mounts in one frame on a 200-row playlist). The
  slot pool now keeps its high-water mark — surplus slots park detached and are taken back before anything mounts — and
  the realized range is retained on the receding side while the list is moving, so a fling recycles rows and mounts
  none. (#n)
- **Track table rows rebuilt their whole element tree on every recycle.** The virtualized row read its presentation
  inside the component's render, so each recycle during a fling re-rendered ~40 element records (40–65 KB per row; up
  to 40 MB and two gen2 collections in one fling on a long playlist). The row grid is now a bound template built once
  per slot, with every per-item value a bound property over the row's presentation, and the spinner, equalizer,
  marquee and withheld branches real mounts; a recycle allocates nothing. The tempo figure and the release date join
  the row's format caches. (#n)
- **A cold playlist open showed empty rows between the shimmer and the tracks, and the detail rail snapped in.** The
  track table's skeleton released the moment the tracklist edge answered with row ids, before the rows' own fetch had
  landed, so the real grid mounted over 1,494 rows with no data (zebra plates, blank titles, `—` durations) for a beat
  and the reveal ramp ran over them. The table's reveal gate now also waits until the first page of rows knows its face
  (title, album, duration, explicit mark, art — the credit line rides its own edge and fills in place, so a warm open
  still reveals at once), a row past that band keeps its placeholder until its own data lands, and a batch that settles
  without a row (`Fetch.Settled`) releases the gate instead of hanging it. The two-column detail rail was a dark square
  and one meta bar that snapped to content: it is now one skeleton boundary shaped like the loaded rail (cover, owner or
  eyebrow row, two title lines, meta, the Play cluster, the blurb) that cross-dissolves once when the header answers —
  album, playlist, show and Liked share it, and the hero band pends on the same header flag. (#n)
- **A fast touchpad swipe could throw the app out of its frame loop, and swipes overshot by an order of magnitude.**
  Packets stamped out of order made the precise-stream resampler clamp with min > max (an unhandled exception in
  `Tick`), and packets stamped sub-millisecond apart made its slope read tens of thousands of DIP/s, so 8 DIP packets
  drew as 180–980 DIP frames and every coast started at the velocity cap. Stamps are folded monotone, the resampler
  interpolates between bracketing samples only and never moves a frame more than the packets it received, and the
  release velocity comes from the raw packet totals. (#n)
- **A coast lost a step whenever a frame repeated its clock stamp.** The host handed the kernel dt = 0 for a frame whose
  frame stamp had not advanced although the wall clock had; the step is now credited from the wall clock in whole
  refresh intervals. `scroll.trace` names any remaining zero coast frame by cause (`zero=dt/skip/pin/other`) and marks
  repaired frames with `r`. (#n)
- **A fling lost a frame of travel whenever the list grew under it.** When a coast reached the end of what was laid
  out so far, the frame it hit the clamp was pinned while its velocity still decayed, and the next frame resumed one
  step slower without ever re-applying the pinned distance — a visible hitch on artist and album pages as their sections
  measured in. The pinned travel is now applied the moment layout gives it room. (#n)
- **Every fling ended with a second of sub-pixel crawl.** The exponential coast ran down to 13 DIP/s, moving 20 DIP over
  the last half second in steps too small to read as motion. Below 60 DIP/s the coast now hands off to a short landing
  that walks a fixed horizon in equal steps at the hand-off speed and stops exactly, about 75 ms later. (#n)
- **Leaving a page mid-fling kept the whole app in "scrolling" mode.** A page parked by navigation while its list was
  still coasting never settled, so live motion stayed on for as long as the page stayed parked (41 s in one recording):
  the frame budget throttled virtual lists on every frame, publishing dropped to every fourth frame and the cover
  colours stopped grading. A parked body settles at once and is excluded from the live-motion summary. (#n)
- **The end of every touchpad swipe hiccuped.** The precise-stream drag went quiet for one to six frames before the
  coast began, so the list stopped dead and then resumed at speed. The stream now releases into the coast on the first
  frame without a sample, extrapolating up to 16 ms, and a late packet resumes the drag instead of re-grabbing. (#n)
- **Touchpad packets bunched into a dead frame and a double frame.** Precise-stream packets were applied 1:1 as they
  arrived; they are now resampled against the frame clock the way wheel notches already were, so 60 Hz packets at
  120 Hz frames give even shifts. (#n)
- **Pages crept for a dozen frames after opening.** The programmatic scroll restore chased its target asymptotically in
  sub-pixel steps; it now lands exactly, with the same displacement floor and distance snap the wheel glide has. (#n)
- **`scroll.trace` blamed the idle gap before a burst on its second frame.** Missed vblanks are read one frame after the
  present they belong to, so every burst's second frame carried the pre-burst idle as "held while moving" and was graded
  a present hitch. The delta is attributed to the frame it measured and the pre-burst one is dropped. (#n)
- **Playing another track from the same album or playlist re-asked autoplay and appended fifty rows each time.** The
  queue rebuild kept only the user's queued rows; the autoplay tail is now kept too when the landing context is the one
  already on the deck, along with its next page. (#n)
- **The queue panel showed grey bars and no "Playing from" after a restart.** The restored rows' titles were asked for in
  the boot catalog scope and lost at sign-in, the panel's refresh key collided across the scope switch, and the context
  itself was never fetched. Every re-laid row is asked again when the deck is rebound, the panel keys on the scope, and
  the context entity is fetched for its name. (#n)
- **Autoplay suggestions appeared only after a skip, and never for a restored session.** A restored deck now reports its
  context complete; an autoplay ask made offline is retried when the session comes online instead of being marked
  exhausted; and suggestions — like a context's next page — are asked when about three quarters of the current run has
  played (or three rows remain), following the autoplay answer's own next page instead of re-asking with the same seeds.
  (#n)
- **The next track was not warmed for a restored session.** Prefetch (head file, CDN mirrors, key) now runs for a parked
  deck and two rows ahead, and is re-issued once the session is online. (#n)
- **Artist and album pages flashed the app accent before their own colour.** Play, links, the pivot underline, hearts and
  the queue's Shuffle/Repeat/Autoplay chips painted blue for a few frames and then flipped to the cover's colour. Cover
  colours are now kept in the local store (warm on relaunch), the album row carries Spotify's extracted cover colour like
  the artist header already did, and every accent surface holds the previous page's colour until its own is known,
  cross-fading when the grading lands. (#n)
- **The artist hero popped in at full brightness under a blue veil.** The photograph now fades up, and the veil and wash
  take the artist's own header colour on the first frame — neutral when there is none — instead of the app accent. (#n)
- **The album page's right column jumped when its sections landed.** The placeholder is sized once from what the row
  already knows (a single reserves no rows block) and is never re-keyed by a later answer, the remaining difference eases
  instead of snapping, the About card no longer stretches to fill a short page's spare height and then shrinks as
  "More by" fills in, and the watch-video card and "Fans also like" are decided together with the tracklist rather than
  a beat later. (#n)
- **Playing a track in Wavee while another device had playback did nothing.** The click forwarded a bare `play`
  (a resume) to the owner. It now sends the desktop `play` command naming the context and the row, from a row click
  and from a page's Play button alike, so the phone starts what you chose. Every accepted put-state whose echo does
  not name Wavee as the active device now logs a warning, so a lost Connect state can be traced.
- **The queue vanished on restart.** The session document now carries the queue rows themselves (up to 200), not just
  the current track and its context, so a restart puts the whole queue back — including autoplay rows and anything
  queued by hand — and asks the catalog for their titles.
- **Spotify Connect lost Wavee as the active device every couple of minutes.** The access-point socket's read
  timeout (90 s) was shorter than Spotify's keep-alive ping (120 s), so an idle channel timed out on schedule: the
  session dropped, re-logged in, reconnected the dealer and re-registered the device — hundreds of times a day — and
  every other client saw Wavee leave. The timeout now outlasts two pings; the server-clock probe that always answered
  401 now carries the session's tokens.
- **The Queue panel's now-playing title is a link again.** It opens the page the track plays from, or the track's
  album when the context has no page of its own.
- **Song and artist radio started immediately.** 0.2.9 behaviour restored: the seed resolves to its radio playlist,
  which becomes the new context while the current track finishes; a radio that leads with the playing track skips that
  duplicate; the "Radio started / Open playlist" and "Couldn't start radio" toasts are back.
- **The player bar kept an empty hole where the video button would be.** The video split slot now exists only while
  the current track has a video; volume, lyrics and the rest reclaim its width otherwise, and the bar eases between
  the two widths instead of leaving a gap.
- **Discography cards painted their hover plate and selection outline 20 DIP past the meta line.** The card grew into
  the grid's row gap; it is pinned to its own height.
- **An expanded discography drawer showed a grey stub pill under its rows.** The floating selection bar was mounted
  around an empty command lane at zero selected; it now follows the live selection count.
- **A single's "Watch the official video" card read "0 songs".** An album answer without disc rows claimed the track
  count as known, and the header commit overwrote a count an earlier answer knew; both ends now treat 0 as unknown.
- **A tooltip outlived the page it described.** Pages are kept alive across navigation, so a tooltip opened from a row
  that navigated away under a still pointer stayed open; every route commit now closes the open tooltip.
- **Grid cards mounted their hover chrome eagerly, and no card revealed its play button on hover.** The play FAB, its
  tooltip and the corner menu (three components per card) mounted on every card of a page; on the artist page that was
  18 ms and 1.3 MB of the navigation frame. They now mount when the card is hovered, focused or relates to what is
  playing — and they actually appear: the engine's hover reveal stopped at the tooltip wrapper around the FAB, so shelf
  cards lit the button only when the pointer landed on it and discography cards never did. The tooltip wrapper is now
  transparent to the hover scope, a button mounting into an already-hovered card fades in at once, and a card the
  keyboard visited cools again when focus leaves it. (#n)
- **`nav.frames` reported idle refreshes as missed vblanks.** The counter counts every refresh between two presents, so a
  page the user was reading logged "missed=301"; the navigation rollup keeps only the figures that count frames that
  ran long, and the scroll rollup keeps the cadence counter where the loop is continuously live.
- **Wheel scrolling stepped and pulsed instead of gliding.** Every notch restarted a critically damped chase from
  zero velocity, so at a steady detent cadence the list slowed to ~2 px a frame and re-accelerated on each click.
  The wheel now plans each notch from the live velocity and the click cadence (Chromium-style velocity continuity,
  Firefox-style cadence regime), lands with a displacement floor instead of a sub-pixel creep, and travels three
  rows per notch on lists that declare a row height.
- **The frame right after a wheel click was late.** Wheel packets broke the display-paced wait mid-refresh and the
  glide ran the list realize unbudgeted. Wheel messages now wait for the compositor tick like pointer motion, and
  a wheel glide arms the same frame budget a fling gets.
- **Touchpad and high-resolution wheel streams moved a fraction of the distance and updated at 60 Hz.** The
  hi-res path scaled by a one-machine calibration constant; it now carries notch units and shares the notch scale.
- **The wheel did nothing over a track list's column header.** Headers route the wheel to their list as a glide.
- **Search painted its facet tabs a beat before the results.** The tab row now holds its skeleton until the query's
  first body has answered and enters with the same rise-and-fade, so both arrive on the same frame.
- **The player bar's transport jumped and the seek bar breathed when a track started.** Every right-cluster slot is
  now reserved by the tier and only its face fades with playback state; the right cluster is a fixed block.
- **Leaving the now-playing title mid-scroll left it cut off.** The marquee glides back to its start on
  deactivation instead of parking, and the bar's title scrolls on its own at a slower cadence.
- **Artist biographies showed `&#34;` and `&#8217;` as text.** One HTML-entity decoder now handles numeric, hex and
  the named set for the bio lead, the About card and rich text alike.
- **The verified check on the album's About-the-artist card sat at the far right.** The name shrinks to its text
  with the check directly after it.
- **Emoji rendered as grey silhouettes.** Colour fonts are translated into their layer runs and drawn through the
  glyph atlas tinted with their palette colours.
- **The search box showed a grey completion while it was not focused.** The inline ghost now shows only while the
  editor has focus and clears on blur.
- **The tab's label changed before its page did.** The label follows the page and switches after the outgoing
  page's exit leg.
- **Album track lists dropped their shimmer for two rows and then filled in.** The list stays in shimmer until its
  first page is in (twelve rows, or all of a shorter list).
- **The album's About and Featured-on sections appeared one after the other.** The band waits for both, or 400 ms,
  before its one swap, and asks for them at the tracklist's priority.
- **Scrolling stuttered while covers were downloading.** Every rendered frame published entity changes, and with
  covers and palettes streaming in something was always dirty, so track rows, cards, tooltips and section hosts
  re-rendered on every scroll frame. Publications are now paced to every fourth frame while a scroll is live, the
  cover-palette pump pauses during scrolls, album hosts and the discography grid gate on the values they paint, and
  card and overlay props compare by data instead of delegate identity.
- **Cover uploads hitched the frame.** A landed cover was copied again on the UI thread into a large-object buffer
  and uploaded through a freshly created GPU heap inside the present turn. The decode buffer now travels to the GPU
  queue without a copy, upload heaps are pooled and budgeted per turn, the image pump keeps a slice of every frame,
  off-screen rows request covers at overscan priority, and the disk image cache trims as designed.
- **An artist's Top tracks could shimmer forever after a network hiccup.** A batch where one route answered and
  another was refused was delivered as a success, sealing the refused fields for the whole session, and the band
  had no failed branch to fall into. Refused groups are now re-askable, an auth refusal is re-planned when the
  session comes back online, the band shows its Retry vacancy when the ask concluded without an answer, and Retry
  re-asks everything the band needs.
- **A liked track Spotify no longer resolves rendered as an empty, playable row.** A per-entity 404 in a batched
  metadata answer was dropped silently. Such tracks are now ruled unavailable: dimmed, "Unavailable" in the duration
  column, no play affordance, and hidden by the playable-only filter.
- **A failed load left the player stuck until another song was played.** Resume refused while an error was set, a
  click on the same row only toggled, and the bar disabled skipping. Clicking the dead row now retries it, and
  Previous / Next stay armed in the error state so you can skip past it.
- **The sidebar, the playlist page, its facts cards and the lyrics rows re-rendered on every entity publication.**
  Each now gates on the values it paints; tooltips inside them use stable factories; lyrics rows retarget their
  opacity spring without a render.
- **Cover downloads churned the shared array pool.** Response buffers are sized from Content-Length and rented from
  a dedicated pool that decoded buffers return to.
- **A relinked liked track showed as blank and could not play.** Spotify serves some old track ids under a newer
  canonical id; the batched answer was filed under the canonical id only, so the liked row never got its metadata,
  and at play time the old id's market-restricted file was chosen and its licence refused. The requested id is now
  aliased to the canonical row (title, art, artists, duration follow it), and the file picker honours the market
  restriction and plays an allowed alternative.
- **Playback stopped at a dead row.** Next / Previous and the natural advance now skip unplayable rows, a terminal
  load failure during an automatic advance skips to the next playable row (at most three in a row), and the queue
  labels such rows "Unavailable" instead of showing loading bars.

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
