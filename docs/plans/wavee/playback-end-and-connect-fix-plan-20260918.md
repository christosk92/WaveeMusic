# Playback stuck at 4:04 + Connect invisibility — diagnosis and fix plan

## Context

The instance in the screenshot is **not** this checkout. The startup line says it is the Release arm64 publish of
`C:\WAVEE\wavee-0.3` (branch `feat/0.3-structure`, engine pinned to `C:\WAVEE\fluent-gpu-pin` via
`EngineRoot.local.props`). All fixes land there (+ the pinned engine), not in `C:\wavee\WaveeMusic`.
Logs: `%LOCALAPPDATA%\Wavee\logs\wavee-20260918.log` (+ the 14:20 roll), session `sid=92892d8d`.

### What happened (proven from logs, each link verified in code)

1. ~15:00–16:13 Wavee was a passive Connect follower. "Closer" (durMs=244960) became the mirrored track at
   16:13:35; then no cluster update for 2 h 23 min.
2. `DoTick` (`Playback/Playback.cs:2243`) folds `Position()` into `PosMs` every second with **no owner test**, and
   `MirrorRemote` (`:1974`) stamps the mirrored position with the *local* clock, discarding `r.TimestampMs`.
   `Position()` clamps to the duration (`:1021`) → the stale mirror ratchets to exactly 244960 ms and sits there.
3. 18:36:29 play pressed → `DoResume` loads with `LoadFromMs = s.PosMs` = the full duration. Log:
   `audio.seek target=10802736` (= 244.96 s × 44100) → `audio.seek.short … eof=1`.
4. `[gapless] arm remainMs=-119 … reason=4`: nothing prepared, nothing in flight. `NaturalNext`
   (`Playback.Transitions.cs:733`) returns nothing because `s.Cursor.IsNone`: `MirrorRemote` never writes
   `Context`/`Cursor`, and `Queue.DecideSeed` (`Entities/Queue.cs:348`) refuses to re-seed a queue that already left
   `Unknown` (it held the daylist rows). Same reason the panel shows "Playing from Liked Songs" (stale `s.Context`)
   over 49 daylist rows (last `Queue.Replace`) under a third, mirrored deck row. The ending-soon nudge is a
   once-per-load latch (`Playback.Audio.cs:2715`) — asked once, got nothing, never asks again.
5. **The hard end is unreachable on a real device.** `PcmAudioPlayer.cs:1377` publishes `Ended` only when
   `drained && sink.WritableFrames >= CapacityFrames`, but with zero voices `CrossfadeMixer.ReadableFrames` returns
   the full request and `RenderBlock` (`:1400`) keeps writing silence into WASAPI, so the sink is never empty.
   Log proof: `[audio-work] clock=28949280` (603 s of a 245 s track), `xruns=0`; and across today's logs **53/53
   successful transitions were crossfade commits, 0 `Ended`** — the Ended path has never worked on WASAPI. It was
   diagnosed and patched only for `--fake` (`PacedSilentEndpoint.Follow`, `Playback.Audio.cs:3349-3500`).
6. No state change ⇒ no put-state for 5.5 min (there is no heartbeat, by design), and the bar clamps at 4:04/0:00.

### Connect (proven + code-verified)

- **29 AP resets today** (`SocketException 10054`). Every `Dropped` — AP *or* dealer — does
  `CloseAll`, wipes `ConnectionId`, bumps the epoch (`Spotify/Spotify.cs:322-329`, `Spotify.Session.cs:250,418`),
  so a healthy dealer is torn down, we get a new connection id and re-announce as `NewDevice`. Dealer-initiated
  teardowns exit the AP thread silently (`Session.cs:604`) — the 11 unexplained extra `dealer connected` lines.
- While the id is empty (backoff + handshake + login, seconds→a minute) every announce is **silently dropped**
  (`Spotify.Connect.cs:281,296,447`); nothing is retained or logged.
- `PutSent` is posted only *after* a 2xx (`Connect.cs:481`), so on a failed PUT `ClaimMsgId==0` and `PutVerdict(false)`
  is a guaranteed no-op; and the response cluster is drained before `PutSent`, so P3/P4 verdicts degrade to the 5 s
  expiry. 0.2.x bound the msgId **before** sending.
- A new connection id while still `Online` is adopted but never announced (`Spotify.cs:319`).
- `Flush` runs unserialised on 4 api workers; no content key / no repair of a lost PUT.
- Hypothesis (not provable from logs): the RSTs are provoked by our keepalive — immediate Pong, PongAck ignored —
  where librespot (`core/src/session.rs`) waits 60 s and watches the ack.
- The ownership verdict is not in the log at all (only on the Diagnostics screen), so "were we `Us`?" cannot be answered today.

## Fix plan (in `C:\WAVEE\wavee-0.3` + `C:\WAVEE\fluent-gpu-pin`)

Rules honoured: decisions in pure, engine-free classes with unit tests; no env switches; no legacy paths; each fix
gets an issue (`Fixes #n`, CHANGELOG ` (#n)`) — issues filed via `gh` only after the user approves the calls.
Implementation by Sonnet subagents on disjoint files; orchestrator builds/tests.

### Wave A — playback can always end and never starts at the end
| # | Change | Files |
|---|---|---|
| A1 | Engine: never render a drained mixer's filler. In `RenderBlock`, after `DrainMixerCmds()`, zero voices + no pending ⇒ `PublishDrained`, return 0. Extract `DrainVerdict.Decide(mixerDrained, cmdsPending, pendingFrames, writable, capacity)` used by `RenderBlock` and `Advance`. Then delete the `--fake`-only workaround (`PacedSilentEndpoint.Follow`/`IsTrailingFillerLocked`/`FollowSilentEndpoint`) and retarget `PlaybackAudioTests.cs:621` at the general rule. Engine test on the RT-feed + buffered-sink path (today only the single-thread/null-sink path is tested). | `fluent-gpu-pin/.../PcmAudioPlayer.cs`, `CrossfadeMixer.cs`; `Playback/Playback.Audio.cs` |
| A2 | Mirror position is the owner's fact: `MirrorSnapshot.Project(posAsOf, wireTs, serverTs, playing, durMs)` extrapolates from the **cluster's** timestamp once, marks a snapshot older than its remaining duration stale ⇒ mirrored as `Paused`; `DoTick` folds only when `Own.Kind == Us`. | `Playback/Playback.cs` |
| A3 | `ResumeStart.For(posMs, durMs)`: within ~1.5 s of the end ⇒ start the next row (or 0 if none). Used by `DoResume` and `DoPlay`. Shared `SeekTarget.Clamp` reserving a tail guard for `DoSeek`. | `Playback/Playback.cs`, `Playback.Transitions.cs` |
| A4 | Takeover adopts the session atomically: on claiming a mirrored row, re-seed context + prev/next + cursor from the cluster unconditionally (takeover overrides `DecideSeed`'s first-writer rule; update `QueueSeedTests.cs:97` accordingly). Panel header/rows then describe one session. | `Playback/Playback.Host.Remote.cs`, `Entities/Queue.cs`, `Playback/Playback.cs` |
| A5 | Endgame is a per-tick pure policy, not latches: `EndgamePlan.Decide(pos, dur, fade, prepared, inFlight, lastAskMs, now) → Ask/Wait/Commit`, re-asking every few seconds while unprepared. | `Playback/Playback.Audio.cs` |

### Wave B — Connect stays registered and says what it is doing
| # | Change | Files |
|---|---|---|
| B1 | Bind before send: post `PutSent(msgId, isActive)` immediately before `Api.Send`; drain-order test (response after PutSent ⇒ `ClaimRejected`). Serialise `Flush` (one gate, mint inside). | `Spotify/Spotify.Connect.cs` |
| B2 | Owed-announce latch instead of three silent returns: pure `PublishGate.Decide(hasConnId, reason, held)`; one slot, newest wins, drained after the hello; log `put-state held (reason)`. Content key latched only on 2xx so a failed PUT is re-sent. | `Spotify/Spotify.Connect.cs` |
| B3 | Announce is owed to a **new connection id**, not a phase transition (`SpotifySessionTests.cs:124` updated). | `Spotify/Spotify.cs` |
| B4 | Decouple the transports: `ApDropped` / `DealerDropped` with independent phases + epochs in the pure `Step`; an AP reset no longer closes the dealer or clears the connection id. Log the epoch-cancelled exits. | `Spotify/Spotify.cs`, `Spotify.Session.cs` |
| B5 | Always-on log parity: put-state line gains `track pos playing paused ctx owner claim startedAt hasBeenMs origin` and the real `NewDevice` vs `NewConnection`; a `connect.owner from→to (cause) fx=` line on every ownership transition; one line each for takeover `fromMs` provenance, `ArmNext` verdict, `DecideSeed` result. | `Spotify.Connect.cs`, `Playback/Playback.Host.cs` |
| B6 | AP keepalive state machine ported from librespot as a pure `ApKeepAlive.Step` (60 s pong delay, ack watchdog). Lands **after** B4/B5 so the log shows whether the reset rate actually drops — it is the one hypothesis here. | `Spotify/Spotify.Session.cs`, `Spotify.cs` |

Order: A1 → A2/A3 → A4/A5 → B1–B3 → B5 → B4 → B6.

---

# Part 2 — Podcast show page + episode detail: HTML/Mica prototype

Requested mid-session. Deliverable now = **a prototype only** (no app code): one self-contained file
`C:\WAVEE\wavee-0.3\docs\plans\wavee\podcast-show-episode-mica.html`, following the conventions of
`library-rework-mica.html` (`:root` tokens named after `Tok.*`, light + two dark blocks, lab bar, fake 1440×900 `.win`
with 48/1fr/72 rows, Segoe UI Variable Text/Display, inline 16 px SVG icons, prose "decisions" + "Zune: taken /
adapted / left" table after the mock). Published as an Artifact too (precedent: the library prototype). Built
independently of Part 1 — it goes first because it is fast and blocks nothing.

## What exists today (0.3)
Two-pane `Detail.Frame(Config.Show)`: rail 280 (cover 256, "Podcast" eyebrow, title, `Publisher · N episodes`,
Play pill + follow/share/⋯, 6-line blurb) │ right column: status + order SelectorBars, "Listen next" resume banner,
bordered episode cards (56 art, title, blurb, date·min, 3-px progress rule, 40 play disc), load-more pill.
No episode page, no route (`Shell.cs` plays an episode uri instead of navigating), no per-episode menu, no
now-playing treatment, no mark-played. The page is identical whether you have never seen the show or are 40 episodes in.

## The design: one frame, the right column answers the visitor's question

Keep the two panes and the rail geometry. The **rail is identity** (stable), the **right column is a reader**
whose first screen changes with the relationship to the show — decided by one pure rule
`ShowVisit.Of(followed, anyProgress, newSinceLastPlayed)` → `New | Returning | CaughtUp` (derived on the model, not
probed by the UI).

**A) New to the show — "what is this, where do I start?"**
- Rail CTA: **Follow** is the accent pill; Play is secondary. Meta reads `publisher · 128 episodes · weekly` (cadence
  derived from publish dates of resident episodes; hidden when < 4 episodes).
- Right, in order: **start here** — up to three large "door" cards chosen by rule: *trailer* (if type=TRAILER) ·
  *latest episode* · *episode 1* (promoted to first when `consumption_order = SEQUENTIAL`). Then **about** (the blurb
  moves here at full measure, expandable — the rail's 6-line clamp is the wrong place to sell a show) → **episodes**.

**B) Returning — "where was I, what's new?"**
- Rail CTA: **Resume · 23 min left** (or "Play latest" when nothing is in progress). Under the meta line a quiet
  ledger: `12 played · 3 in progress · 5 new`, with a thin segmented bar — the Zune "collection stat" voice.
- Right, in order: **continue** (one hero card: art 96, title, "23 min left", progress, Resume; a second in-progress
  episode collapses to a row under it) → **new since you were here** (episodes published after the last play; count
  in the header, "mark all played" text action) → **episodes**. About collapses to a one-line word link.

**C) Caught up** — the continue/new blocks disappear; a single line "you're all caught up · next usually Thursdays"
then episodes. No empty boxes.

**Episodes list (all states) — Zune reader, not boxed cards**
- Word rails replace both SelectorBars: `all · unplayed · in progress · played` left, `newest · oldest` right
  (active 100 %, others 50 %, accent underline — same control as the library rework).
- Rows lose the 1-px card border: hairline dividers, month/season **group headers** in Display Light ("march 2026" /
  "season 3"), episode **number as a big light numeral** in the left gutter when `number` exists (art 56 otherwise).
- Row: title 14/600 (2 lines) · blurb 12 (2 lines) · meta `Mar 14 · 47 min` → `23 min left` in accent when in
  progress → a check + "played" at 60 % opacity when finished · badges `E`, `video`, `bonus`/`trailer`.
- Hover reveals: play disc, **add to queue**, **mark played**, ⋯. Whole row navigates to the episode page; the disc
  plays. Now-playing row gets the accent title + equalizer (the gap chapter 09 §9.4 calls out).
- Vertical (<540): the word rail stays (today the toolbar vanishes), scrolls horizontally, clips at the edge.

**Episode detail page (new `episode:` route) — same two panes**
- Rail: episode art · eyebrow = show name (link, with 24-px show cover) · title · `Mar 14 2026 · 47 min` · CTA
  `Play` / `Resume · 23 min left` + queue, mark played, share · progress ledger.
- Right: a tabbed reader + discovery shelves — full spec in the next section.

## Everything is designed in (user direction) — the WinUI app at `0d0429a0` proves each source

Inventoried from `git show 0d0429a0:…` (ShowPage/EpisodePage/YourEpisodes/PodcastBrowse + `PathfinderOperations.cs`,
`SpClient.cs`). Nothing below is speculative: each element had a live read path there.

**Show rail additions**
- `★ 4.8 · 12,431 ratings` under the publisher; tap → **rate flyout** (5 stars, shows *your* rating; wire carries
  `rating.canRate` + `rating.rating`, the old DTO dropped them). Hidden unless `showAverage && average > 0`.
- **Topic word-chips** (lowercase, wrap, ≤6 + "more") → podcast browse for that topic.
- Badges: `exclusive`, `E`, `video`, paywall lock. Publisher as plain text (old app's link was disabled — no destination exists).
- CTA row: Play/Resume/Follow (per visit state) · **trailer** play (when `trailerV2`) · notify-bell · share · ⋯
  (pin, add to queue, mark all played, copy link).
- Page wash + accents from the **show's own palette** (`extractedColorSet … backgroundTintedBase`), never brand green — kept rule.

**Show right column additions**
- "Listen next" ports the old algorithm verbatim as a pure rule (`ListenNext.Pick`): resume candidate <90 %, anchor on
  last completed/≥90 %, walk forward to 4; **new listener seeds from episode 1**; now branches on
  `consumptionOrderV2` (episodic ⇒ newest-first instead) — captured but never used before.
  Summary line: `start from the beginning` / `up next from your progress · 3 in progress · 41 unplayed · 84 played`.
- Episode rows gain: paywall/preview-only chip, transcript glyph, `#N`-prefix stripping, "progress unavailable"
  as a distinct state (never a false "unplayed"), ♥ = add to Your Episodes, mark played/unplayed.
- In-list **search** (Ctrl+F) and filter/sort word rails — **persisted per show** (the old app forgot to).
- **more like this** — similar-shows shelf at the foot (`internalLinkRecommenderShow`), cards: art · name · publisher · ★.

**Episode page additions** (two panes kept; rail = identity + actions, right = reader)
- Rail: art · show link · title · date · duration/time-left · `E`/`video`/paywall/preview · Play/Resume/Replay ·
  ♥ Your Episodes · queue · play next · mark played · share · **preview** button when only a preview is playable.
- Right word rail: `about · chapters · transcript · comments`
  - *about*: HTML description; `(12:34)` timestamps linkified → seek (new; snapped to chapters).
  - *chapters*: vertical timeline rail (timestamp │ dot+fill │ title/subtitle/tags), live fill only while this
    episode plays; tap = play+seek. Full paginated source (`queryNpvEpisodeChapters`), not the 10-cap one.
  - *transcript*: readable, sectioned, language picker, click-a-sentence-to-seek, follows playback; search within.
  - *comments*: count header, pinned first, avatar · name · age, top-3 reaction emoji + count → reactions sheet
    (per-emoji chips), lazy replies, composer (500 chars). Writes were stubs in the old app ⇒ composer ships
    disabled-with-reason until a write path exists; reads are real.
- Below the reader: **up next in this show** (prev/next doors, order-aware) → **more from this show** (6 rows, "see
  all") → **you might also like** (recommended episodes shelf — the old `PodcastEpisodeRecommendationCard` was built
  and never mounted).

**Companion scenes in the same prototype** (so the flows connect): *your episodes* (saved ↔ latest word rail,
recently-played pseudo-show, details pane) and *podcast browse* landing for a topic chip — as secondary scenes, lighter fidelity.

## Data legend (toggle in the prototype; drives the follow-up data plan)
| Tier | Elements | Source (from the old app) |
|---|---|---|
| ● today | cover, title, publisher, blurb, count, date, duration, local progress, follow, share | `ShowV4`/`EpisodeV4` |
| ◐ decode only | explicit, number, type, video, trailer uri, consumption order | same protos, more fields |
| ○ pathfinder | rating (+canRate/own), topics, palette, exclusive, html description, share url, saved | `queryShowMetadataV2` `aaad798a…` |
| ○ | similar shows | `internalLinkRecommenderShow` `6c369ff2…` |
| ○ | episode detail, transcripts list, paywall, preview, playedState | `getEpisodeOrChapter` `34169290…` |
| ○ | recommended episodes | `internalLinkRecommenderEpisode` `122f5c77…` |
| ○ | chapters (paged 50) | `queryNpvEpisodeChapters` `367f0e93…` |
| ○ | comments / replies / reactions | `getCommentsForEntity` `bba34fe5…` · `getReplies` `a2018b23…` · `getReactions` `0d209bf9…` |
| ○ spclient | **resume points, all episodes in one call** | `herodotus …CurrentStateService/ListCurrentStates` (write: `CreateResumePointRevision`) |
| ○ spclient | transcript text | `GET transcript-read-along/v2/episode/{id}` |
| ✎ no write path yet | rate, comment/reply/react, notifications, downloads | designed, marked "needs write path" |

Persisted-query hashes may have rotated since June; the data plan verifies each before building on it.
One completion threshold replaces the old app's three (90 s / 30 s / 0.995): **≥ 0.98 or ≤ 30 s left** in `Episode.Rules`.

Lab bar toggles: theme · width (1440/1120/760/vertical) · scene (show / episode / your episodes / browse) ·
visit (new/returning/caught-up) · episode tab · data tier · skeleton · show palette (3 sample shows).

After you approve the prototype: `podcast-show-rework-implementation.md` (same shape as
`library-rework-implementation.md`), an audit entry in `wavee-0.3-ui/09-show-episode-module.md` §11, and the
`episode:` route row in `00-index.md`.

## Verification

- Prototype: open the HTML directly in a browser; check every lab toggle, both themes, all four widths, keyboard
  focus order, and that no ○-tier element renders in the "today" tier.
- Engine: `dotnet build src/FluentGpu.slnx` Debug+Release and engine tests (`--blame-hang-timeout`) in `fluent-gpu-pin`; new RT-path test: drained mixer + buffered sink ⇒ `Ended` within one buffer.
- App: one Debug + one Release build of `wavee-0.3`, `dotnet test src/apps/Wavee.Tests` — new pure tests for `DrainVerdict`, `MirrorSnapshot`, `ResumeStart`, `EndgamePlan`, `PublishGate`, session `Step` (split drops, new-id announce), bind-before-send ordering.
- Live (side-folder publish, never touching the user's running instance): play a track with crossfade 0 and an empty queue tail ⇒ `[gapless] ended` then advance/autoplay; mirror a phone, pause the phone > one track length, press play in Wavee ⇒ `fromMs` log shows a sane position and a prepared next; confirm `put-state … owner=Us` after a forced dealer drop and that an AP reset leaves `dealer connected` count unchanged. Compare AP reset count/day before and after B6.

## As built

B4 and B6 (2026-09-19): see [as-built-20260919.md](as-built-20260919.md) — sections B4, B6.
