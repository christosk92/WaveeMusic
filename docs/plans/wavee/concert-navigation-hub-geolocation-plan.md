# Concert Navigation, Hub, and Generic Geolocation

Status: Phases 4-7 implemented and build/test-verified; awaiting manual user acceptance (Phase 8 gate).

Companion UI plan: [Concert UI Component and Engine Plan](concert-ui-component-plan.md)

## Verification protocol

Every phase is a separate review gate:

1. Implement only the phase scope.
2. Run focused tests and the normal app build.
3. Report changed files, results, limitations, and manual verification.
4. Stop for user verification and iteration.
5. Continue only after explicit approval.

## Captured Pathfinder contracts

The supplied SAZ contains 156 sessions and 32 Pathfinder requests. Concert data is served by
`POST https://api-partner.spotify.com/pathfinder/v2/query` using persisted operations.

| Operation | Hash | Variables |
| --- | --- | --- |
| `ArtistConcerts` | `ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b` | `artistUri`, nullable `geoHash`, `includeNearby` |
| `ArtistConcertsPageLocation` | `320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03` | none |
| `userLocation` | `079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2` | none |
| `inferredUserLocation` | `5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba` | none |
| `concertConcepts` | `a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72` | `geohash`, nullable `conceptUri` |
| `concertFeed` | `9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a` | location, date/concept filters, radius, opaque pagination key |
| `concertLocationDetails` | `b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681` | `geonameId`, `isAnonymous` |
| `searchConcertLocations` | `43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3` | `query` |
| `concertLocationsByLatLon` | `8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96` | `lat`, `lon` |
| `saveLocation` | `5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353` | `geonameId` |
| `concert` | `21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e` | `uri`, `authenticated` |

## Important ArtistConcerts response difference

The dedicated artist response does not put the schedule under `artistUnion.goods.concerts`.

- `data.artistUnion` contains artist identity and artwork only.
- `data.nearby.locationName` describes the active nearby location.
- `data.nearby.concerts.items[]` contains the nearby subset.
- `data.concerts.concerts.items[]` contains the complete schedule.
- Both `nearby` and `concerts` can be empty objects rather than absent/null.
- Event summaries include artist items, location, local-offset ISO start time, title, and URI.

Mappers must read each response by operation-specific shape. The older artist-overview mapper remains valid for its own
`goods.concerts.items` response and must not be reused blindly.

## Product routes

- `concerts`: global Concert Hub.
- `artist-concerts:<spotify:artist:...>`: complete artist schedule.
- `concert:<spotify:concert:...>`: rich concert detail and ticket offers.

Concert navigation is never playback. Ticket URLs are external actions and are validated before `InputHooks.OpenUri`.

## Architecture

### Provider-neutral domain

Wavee.Core owns compact and rich concert records, artists, places, offers, concepts, schedules, feed sections, and opaque
pagination keys. Provider-specific typename strings, GeoNames assumptions, and Pathfinder JSON do not enter page code.

### Spotify adapter

The SpotifyLive layer owns operation constants, request variables, parsing, response quirks, deduplication, status
normalization, URI validation, and stale-query cancellation. The rest of the app consumes provider-neutral interfaces.

### Generic geolocation

FluentGpu receives a reusable one-shot geolocation abstraction with:

- a platform-neutral request/result contract;
- distinct permission-denied, unavailable, timeout, canceled, and failed outcomes;
- a Windows implementation using the platform location API;
- a deterministic headless implementation for tests;
- no Spotify or concert dependencies.

Wavee only requests OS location after the user activates **Use my location**. Manual search always remains available.

### Data behavior

- Default discovery radius is the captured 100 km.
- `dateRange` remains null initially.
- Concept selection sends zero or one concept URI.
- Pagination keys are replayed unchanged.
- Location/concept changes cancel visible requests, reset pagination, and ignore late completions.
- Feed append deduplicates by canonical concert URI.
- Empty branches and optional detail fields remove UI sections rather than creating placeholders.

## Implementation phases

### Phase 0 - Sanitized contracts and component proofs

Implemented in the current change:

- provider-neutral domain additions;
- all captured persisted-operation names/hashes;
- sanitized artist, feed, detail, concept, and location fixtures;
- regression tests for the new artist sibling branches, mixed feed wrappers, opaque pagination, offers, and sanitization;
- shared UI proof components and responsive metric tests.

Gate: focused tests, complete test suite, Wavee build, and user review.

### Phase 1 - Generic FluentGpu geolocation

Implemented in the current change:

- engine-level one-shot request, position, result, status, and provider contracts;
- a deterministic scripted headless provider with explicit pending completions;
- a Windows provider using the current `Windows.Devices.Geolocation` API through the existing TerraFX WinRT ABI path;
- provider-neutral mapping for success, denial, unavailable, timeout, cancel, and failure;
- engine-harness checks for every result plus timeout winning over a late platform completion.

Gate: engine harness reports `ALL CHECKS PASSED`; no Wavee UI or automatic permission prompt yet.

### Phase 2 - Concert services and mappers

Implemented in the current change:

- provider-neutral `IConcertService`, feed query, and location snapshot contracts;
- provider-neutral sequential page append with canonical concert/playlist deduplication and opaque-token replacement;
- exact captured request-variable writers, including explicit nulls, `geohash` casing, one-concept selection, 100 km
  default radius, and saved-place-versus-geohash precedence;
- a Web Player Pathfinder service adapter covering every captured concert, feed, concept, and location operation;
- defensive operation-specific mappers for the new artist sibling branches, mixed feed wrappers, detail offers,
  concepts, saved/inferred/manual/reverse locations, and optional/malformed data;
- local-offset `DateTimeOffset` preservation and cancellation propagation;
- fixture-backed tests for rich, empty, malformed, request, mapper, service wiring, cancellation, deduplication, and
  pagination behavior.

Gate result: 21 focused concert tests pass and the Wavee build succeeds. The full suite reports 1,008/1,015 passing;
the seven failures are non-concert PlayPlay environment, allocation-threshold, and Home/Recent accent tests outside the
Phase 2 files.
No product routes or UI were added in this phase.

### Phase 3 - Routing and clickability baseline

Implemented in the current change:

- provider-neutral `concerts`, `artist-concerts:<opaque artist id>`, and `concert:<opaque concert id>` route helpers;
- route-aware shell titles/history identity and minimal Concert Hub, artist-schedule, and concert-detail destinations;
- the artist tour banner now navigates to the artist schedule instead of playing the first concert URI;
- existing artist-page concert cards keep the original compact stub visuals (user-reviewed) and navigate to concert detail;
- explicit button roles, focus stops, retained-engine focus margins, pointer cursors, and the engine's native Enter/Space
  activation path on both entry points;
- focused route tests covering exact hub matching, opaque identifier round trips, non-Spotify identifiers, and malformed
  route rejection;
- a source scan confirming the old concert playback callback is gone.

Gate result: 29 focused concert tests pass and the Wavee build succeeds. The full suite reports 1,016/1,023 passing;
the same seven non-concert PlayPlay environment, allocation-threshold, and Home/Recent accent tests fail outside the
Phase 3 files. Manual pointer, keyboard, back/forward, and focus-ring verification remains the user review gate before
Phase 4.

### Phase 4 - Artist schedule UI

Implemented in the current change:

- a provider-neutral `SwitchableConcertService`/`NullConcertService` pair (mirrors the What's-New seam): required-dependency
  wiring, ctor-initialized to the offline Null service, installed with the live `SpotifyConcertService` in `LiveSessionHost`
  where the Pathfinder resource is created, and reset to Null in `GoOffline`; the Null service returns null/empty for reads
  and an honest `false` from `SaveLocation` (no silent success);
- `WindowsGeolocationProvider` hand-wired in the composition root as the required `Services.Geolocation` seam (constructing
  it prompts nothing; OS location is requested only on explicit "Use my location");
- a dedicated `ArtistSchedulePage` replacing the artist-schedule stub branch of `ConcertRoutePage`, with one page `ScrollEl`
  (`Grow = 1`, `MinHeight = 0`, stable `ScrollKey`, `PlayerDock.Reserve` padding) and a seeded `Skel.Region` derived from the
  real row subtree — pending, error (real Retry), empty ("No upcoming concerts"), and loaded states owned by the engine;
- header (artist identity + location control), an optional bounded nearby shelf (only when `Nearby` is non-empty; measured
  `PagedShelf`, ≤16), and the full chronological schedule as keyed vertical rows (stale-generation-guarded async loads via
  `UseAsyncResource`; canonical concert-URI keys; no nested vertical scroller; no playback anywhere);
- schedule breakpoint hysteresis (`Responsive.Of` + `ConcertLayout.ScheduleWide`, enter ≥760 / leave <720);
- the shared anchored location picker: `ConcertUi.LocationButton` opens an `Overlay.Service` panel hosting
  `ConcertLocationPickerPanel` (fresh mount per open, live page-owned signals) — debounced city search, select → `SaveLocation`
  then schedule reload, and "Use my location" (explicit click only) → OS one-shot → `ReverseLocation`, with distinct
  permission-denied/unavailable/timeout messages;
- a quieter Fluent date/row visual pass: `ConcertUi.DateBlock` is now a neutral layered fill with small accent month text and
  a primary day numeral (no large saturated blocks), and the schedule row leads with venue (city + optional "Near you" pill
  secondary), every text column ellipsis-clamped so nothing truncates mid-word;
- focused pure-logic tests: chronological sort/dedup, geolocation-status → message mapping, schedule hysteresis, and the
  Null/Switchable concert-service behaviour;
- the nearby shelf uses the venue-primary schedule row (the user-reviewed stub shape), not the title-primary shelf card.

Gate result: 43 focused concert tests pass and the Wavee build succeeds. Manual wide/narrow schedule acceptance remains
the user review gate.

### Phase 5 - Concert detail UI

Implemented in the current change:

- a dedicated `ConcertDetailPage` replacing the concert-detail stub branch of `ConcertRoutePage` (route-keyed mount), with
  one page `ScrollEl` (`Grow = 1`, `MinHeight = 0`, stable `ScrollKey`, `PlayerDock.Reserve` padding) and a seeded
  `Skel.Region` derived from the real subtree — pending, error (real Retry), unavailable (null details), and loaded states;
- event identity + facts with the provider's PRESERVED local offset (formatted from the stored clock time, never converted
  to the machine zone): date/time, doors-open when present, venue + deduplicated city/region/country line, age restriction
  when present, and status only as a notable-state pill (cancelled/postponed/...; UNKNOWN/CONFIRMED/SCHEDULED are hidden);
  absent sections are omitted entirely — no placeholders;
- ticket offers: provider name, availability, invariant-digit price range with the offer's own currency CODE (no locale
  symbol guessing), and sale window when present; BUY is a validated absolute-http(s) external link through the platform
  `OpenUri` seam only — a missing/invalid URL renders quiet non-actionable text, never a dead button; no playback anywhere;
- detail breakpoint hysteresis (`Responsive.Of` + the existing `ConcertLayout.DetailWide`, enter ≥920 / leave <860): wide
  mode places a 320-DIP ticket panel beside the main column (riding the page scroll — no nested vertical viewport), narrow
  mode inlines the ticket section after the facts;
- lineup rows with avatars: names carrying a canonical `spotify:artist:` URI navigate to the artist page; billing-text-only
  names stay plain rows with no dead affordance;
- a related-concerts shelf (different artists → title-primary `ConcertUi.ShelfCard` family with the neutral date block) in
  a bounded measured `PagedShelf` (≤16, keyed by canonical URI) under the shared accent eyebrow caption;
- pure-logic helpers in an engine-free file (`ConcertDetailModel.cs`) with focused tests: ticket-URL validation, price /
  availability / sale-window formatting, status humanization, location-line dedup, and detail hysteresis.

Gate result: 72 focused concert tests pass and the Wavee build succeeds. Manual detail-state and external-link
acceptance remains the user review gate.

### Phase 6 - Concert Hub and location UI

Implemented in the current change:

- a dedicated `ConcertHubPage` on the `concerts` route (the route switch now mounts all three dedicated pages), with one
  page `ScrollEl` (`Grow = 1`, `MinHeight = 0`, stable `ScrollKey`, `PlayerDock.Reserve` padding) and a seeded
  `Skel.Region` for the INITIAL feed load only;
- location resolution on open: `GetUserLocation` + `IsUserLocationInferred`; an inferred place labels the control
  "Near {name} (approximate)", no resolvable place keeps the hub rendering with a prominent set-location prompt (the
  empty state carries a "Set location" action that opens the picker) and whatever the feed returns for a null location;
- the concepts row: `GetConcepts(geoHash)` only when the active place carries a geohash, rendered as a `SelectorBar`
  inside a horizontal edge-fade scroller (the page's only non-vertical viewport; the selector never scrolls/wraps
  itself); zero-or-one selection — tapping the active concept clears it — and every selection change re-queries the feed;
- the feed by section kind: Nearby/Recommended as bounded measured `PagedShelf`s (≤16, URI-keyed) of the title-primary
  card family (many artists → title-primary is correct; neutral date block); playlist promotions as playlist-look cards
  navigating the existing `pl:` route WITHOUT a play affordance (every `MediaCard` variant hard-mounts the play FAB, and
  no concert surface may wire playback); All Events as URI-keyed `ConcertUi.GridCard` children in `Ui.AutoGrid(300, gap,
  112)`;
- load-more: a standard button shown only while `PaginationKey` is non-null; append is a SEPARATE small state (the
  button disables to "Loading...", never the page skeleton), replays the opaque key unchanged, and merges through the
  tested `ConcertFeedPage.Append` (order-preserving dedup + token replacement); a quiet append failure re-arms the button;
- stale-response discipline: location/concept changes bump one query generation, cancel the visible request (one CTS per
  generation, shared by the sibling concepts/feed/append fetches), reset accumulated pagination, and every completion
  publishes through a `UsePost` closure that re-checks its captured generation — a late page from generation N can never
  publish into N+1;
- the shared `ConcertLocationController` extraction: the picker signals + debounced search + explicit-click geolocation +
  save-selection handlers now live in one class owned by both the artist schedule and the hub (the presentational
  `ConcertLocationPickerPanel` is unchanged); on save the hub keeps the selected concept and re-queries against the new
  place, clearing the selection only when the new location's concepts list no longer offers it;
- pure-logic helpers (`ConcertHubModel.cs`) with focused tests: concept toggle semantics, concept survival across a
  location change, the inferred "(approximate)" label, and feed emptiness.

Gate result: 83 focused concert tests pass and the Wavee build succeeds (one missing-using compile fix applied in
verification). The full suite reports 1,065/1,072 passing; the seven failures are the same pre-existing non-concert
PlayPlay environment, allocation-threshold, and Home/Recent accent tests. Manual hub/filter/location and
stale-response acceptance remains the user review gate.

### Phase 7 - Home editorial destination

Implemented in the current change:

- ONE wide `ConcertUi.WideEditorialDestination` card on Home, below the greeting and before the dynamic shelves, opening
  the `concerts` hub route — no unrelated Home section changed;
- the card is deliberately inert on the hot Home surface: static copy ("LIVE MUSIC" eyebrow / "Concerts near you" /
  one-line subtitle / "Explore concerts") plus a single navigation action — no signals, timers, effects, or playback —
  and a stable key so Home re-renders reconcile the mounted responsive card instead of remounting it;
- the Phase 0 component's constraints re-verified at the call site: one layered responsive tree
  (`Responsive.Of` + the tested `ConcertLayout.WideEditorial` metrics), cover-cropped right-pane artwork, no acrylic,
  no self-blur, no root scale, no image zoom; one focus stop with Button role, focus visual margin, hand cursor, and
  the engine's native Enter/Space activation;
- no editorial/portrait artwork source exists in the app yet (no bundled asset, no domain field), so the card passes
  null artwork and the component's neutral right pane stands in — no network fetch was invented for this phase;
- copy is hardcoded, matching the concert surfaces shipped in Phases 4-6; no new tests (the editorial metrics are
  already covered by the Phase 0 tests, and the card adds no new pure logic).

Gate result: the Wavee build succeeds and the full suite reports 1,065/1,072 passing (same seven pre-existing
non-concert failures). Manual crop/typography/focus/theme/narrow-width acceptance remains the user review gate.

### Phase 8 - Integration hardening

Automated portion run (2026-07-13):

- complete test suite: 1,065/1,072 passing; the seven failures are the pre-existing non-concert PlayPlay environment,
  allocation-threshold, and Home/Recent accent tests (unchanged since before Phase 2);
- Wavee build: clean (0 errors, pre-existing warnings only);
- engine harness: the concert phases changed no `src/` engine file; the VerticalSlice run against the current working
  tree shows 2-3 failing gates (`gate.touch.flick-seed-gap-invariant`, `gate.touch4.sip.alloc-zero`,
  `gate.ctx.invoke-anchors-source` — count varies between runs, so at least one is flaky) that belong to the
  pre-existing uncommitted engine work in the tree, not to this feature.

Remaining for manual acceptance: visual matrix, keyboard checks, repeated popup/navigation smoke tests, KeepAlive,
scroll restoration, late completions, and no-playback-regression verification.

Gate: final user acceptance.

#### Post-phase iteration (2026-07-14)

A user-feedback visual pass (both prior directions — big accent date blocks / title-primary cards, and a 1:1 Apple Music
floating-chip clone — were rejected), plus a bug fix and mapper additions. All on Wavee's own Fluent terms:

- **Date as typography, not a badge.** A new `ConcertUi.VerticalCard` replaces `ShelfCard`/`GridCard` (both deleted, along
  with the shared horizontal `EventCard` core): a cover-cropped square fills the card's rounded top; below it a padded text
  block leads with an accent date *caption* (`"FRI, AUG 21 · 19:00"`, current culture, upper-cased — the CONCERT/LINEUP/NEAR
  eyebrow voice), then the title (`TrackTitle`, one line, `NoWrap` + `CharacterEllipsis` — never wrapped) and venue · city.
  It carries the standard card chrome (FillCardDefault, hairline stroke, hover/pressed fills, Card corners, Button role,
  focus visual, Hand cursor, URI key). Rolled out to the hub Nearby/Recommended shelves, the hub All-Events `AutoGrid`
  (row height switched to `float.NaN` = auto so the taller card self-sizes; column min 240), and the detail related shelf.
- **Dynamic colour.** A concert with no artwork now renders a softly tinted pane (its extracted dark colour lerped over
  FillCardSecondary) with the neutral `DateBlock` centred, instead of a bare date block. Page **heroes** (`ConcertUi.Hero`,
  reusing the ArtistPage photo + bottom-edge-fade + black legibility scrim recipe — no acrylic, self-blur, or root scale):
  the artist schedule page shows one when `ArtistConcertSchedule.HeaderImage` is set (eyebrow + name overlaid bottom-left,
  the location control on its baseline; the plain header stays the fallback), and the concert detail page shows one sourced
  from the first lineup artist's new `HeaderImage` (Identity then drops its headline to avoid repeating the title). A
  low-alpha extracted-colour wash bridges the hero into the page seam.
- **Overlap bug fix.** On the artist nearby shelf the "Near you" `StatusPill` painted over the city ("Sin~Nearklaas~").
  Root cause: the city label had `Basis = 0f`; the engine weights flex-shrink by base size (`FlexShrink × baseMain`), so a
  zero-basis item has **zero shrink weight and can never give space back**. When the narrow card is thinner than the pill,
  free space goes negative, the city stays pinned at width 0 at x=0, and the unshrinkable pill is placed immediately after
  it — i.e. also at x≈0 — so the two paint on top of each other. Fixed by giving the city real shrink weight
  (`Grow = 1f, Shrink = 1f, MinWidth = 0f`, no `Basis = 0`) and the pill `Shrink = 0f`, so the city always ellipsizes to
  exactly the leftover width and the pill sits after it — overlap impossible at any width. The pill is also dropped from the
  artist nearby shelf entirely (redundant under its "NEAR {CITY}" caption), via a new `showNearPill` parameter.
- **Home.** The "Concerts near you" editorial card moved from below the greeting to after the dynamic feed groups (still
  outside the groups `Skel.Region`, same `home-concerts-editorial` key, same inert discipline).
- **Data layer.** `Concert` gained `uint? AccentColor` and `ConcertArtist` gained `Image? HeaderImage` + `uint? AccentColor`
  (extracted colours stored as opaque ARGB `uint`, matching `Palette`'s channels; `WaveePalette.ToColor` lifts to a renderer
  colour at the UI boundary). `MapConcert` now prefers a concert-level `images.items[0]` (cover + its `colorDark` hex, parsed
  via the shared `SpotifyExportMapper.HexToArgb`) over the first-artist avatar, falling back to the first artist's banner
  accent. `MapArtists` reads the lineup `headerImage.data` banner and the related/feed `visuals.headerImage` banner (+ its
  accent); `MapImage` now also accepts `maxWidth`/`maxHeight` source keys. Fixtures + `ConcertPathfinderTests` extended to
  cover feed image preference + accent, both header-image branches, and the maxWidth/maxHeight sources.

Iteration verification: Wavee build clean; 83 focused concert tests pass (mapper assertions extended in place); full
suite 1,065/1,072 with the same seven pre-existing non-concert failures. Visual acceptance remains with the user.

Verification (build + test + visual) left to the orchestrator.

##### R3 artist-schedule rework (2026-07-14)

A user-approved redesign ("R3") of the **artist schedule page** replaces the flat chronological row list + separate nearby
shelf with a hero-stats → next-show spotlight → month-boards topology. All states/loading/location plumbing is unchanged
(one page `ScrollEl` + `PlayerDock.Reserve`, seeded `Skel.Region`, `ErrorState`/`EmptyState`, the location picker, the
`ScheduleWide` hysteresis); no playback anywhere. The new topology:

- **Hero + tour stats.** `ConcertUi.Hero` gains an optional `stats` element; the schedule page derives a stats line —
  `"{N} shows · {M} cities · MMM – MMM yyyy"` (distinct non-empty cities; cities segment omitted when all empty;
  single-month span collapses to `"MMM yyyy"`) — from the loaded schedule and renders it in white ink under the artist
  name (or `TextSecondary` under the plain header fallback).
- **Next-show spotlight.** `ConcertUi.SpotlightCard` renders the first upcoming concert prominently: a `BigDateBlock`
  (month / big day numeral / weekday), a `"NEXT SHOW · IN {relative}"` accent caption (`RelativeTime(date, now)` →
  "tomorrow" / "in 5 days" / "in 6 weeks"), venue, "City · HH:mm", and a trailing accent **Tickets** button — both the
  card and the button navigate to `ConcertRoutes.Detail` (offers live on the detail page; no offer data is invented). With
  a hero it tucks up into the hero's faded bottom band via a shallow negative top margin (kept shallow so it never occludes
  the hero's bottom-anchored location control). The spotlight is excluded from the boards, so no event appears twice.
- **Month boards** (`MonthBoard`, new stateful component) replace the row list. Concerts group by calendar month
  (preserved local offset); each board is a tracked "MONTH" + "N shows" header (layer-2 fill) over hairline-separated
  tiles (day column + venue + "City · HH:mm" + chevron, whole tile = a Button-role detail link). **Near-you is a property**:
  a tile whose URI is in the schedule's `Nearby` set gets an accent-subtle fill + a map-pin glyph (the separate nearby
  shelf is removed). **Run collapse**: consecutive-night same-venue shows merge into one "N nights" tile that expands in
  place (per-board state). **6-tile cap**: a run counts as one tile; overflow collapses behind a "Show all N" footer
  (per-month state). Boards lay out side-by-side on wide widths (≥ ~255dip columns) via `Responsive.Of` + round-robin
  column-stacks and are **top-aligned** — `AutoGrid` is deliberately NOT used because its auto row height stretches a
  short board to its tallest row-mate.
- **Shy month pill** (`ShyMonthPill` + `ShyMonthTracker`, new): a floating top-center indicator showing the month at the
  viewport top, revealed while scrolling and dismissed ~1s after scrolling stops. Isolated so per-frame scroll updates
  re-render only the pill; reads the page-published scroll offset + board-header geometry via `SceneStore.AbsoluteRect`
  (no ancestor relationship to the scroller). Non-interactive and exempt from `InputDispatcher.ScrollableUnder` via
  `HitTestVisible = false` + `HitTestPassThrough` on the overlay root; reveal/dismiss ride the declarative transition
  surface (reduced-motion degrades to instant); the 1s linger runs on the engine animation clock (a re-armed invisible
  duration track), never the wall clock.
- **Pure logic + tests.** `ConcertScheduleShaping` (in `ConcertScheduleModel.cs`) adds spotlight selection, month
  grouping, run detection, cap math, stats derivation, relative-time phrasing, and the board column count — all taking
  `now` explicitly. `ConcertSchedulePageTests` extended: year-boundary grouping, run collapse (consecutive / gap /
  different venue), the 6-vs-7 cap boundary, stats with empty cities, relative time, and spotlight exclusion.

**R3.1 polish (2026-07-14).** User visual review of the first R3 build rejected the look ("disconnected"); six fixes:

- **Near-you guard.** Real captures showed the `Nearby` branch containing ALL shows → every tile tinted (an
  information-free accent wall). `ConcertScheduleShaping.NearIsInformative` (0 < near ≤ max(3, ⌈shows/3⌉)) +
  `GuardedNearSet` (folds `IsNearUser` in; empties the whole set when uninformative) now gate the tint/pin, and
  `MonthBoard` consumes ONLY the guarded set. Tested (6-of-18 on, 7-of-18 off, all-near off, the small-tour floor of 3).
- **Venue-less tiles.** ArtistConcerts events routinely have no venue and a title equal to the page artist's name — the
  old venue→title fallback repeated the artist down every board. `ConcertScheduleShaping.TileText` (pure, tested):
  venue-primary, else CITY-primary (secondary = "HH:mm" plus `SupportActs` — the lineup minus the page artist,
  case-insensitive, max 2 names then "+n more"), title only when venue AND city are both empty. Run tiles follow the
  same rule (city-primary ⇒ "n nights" chip + support acts); the spotlight card applies the same city-primary fallback
  with a time-only meta so the city is never printed twice.
- **Hero restructure — the copy comes OFF the photo** (schedule + concert-detail, shared `ConcertUi.Hero`). The photo is
  now a ~192dip atmosphere BAND whose bottom dissolves completely into the page (EdgeFade inside the media box, so the
  photo and the low-alpha extracted-colour wash fade together); the black legibility scrim is DELETED — no text, buttons,
  or scrim on the photo at all. The eyebrow/title/stats render below the band in normal theme ink (accent / primary /
  secondary), with the location control trailing the title row as standard chrome; the no-image fallback is now the same
  copy block minus the band (cohesion by construction). Plain adjacency at zero gap — not a negative-margin overlap —
  places the title row against the band's faded zone. This supersedes R3's "spotlight rides the hero seam" placement.
- **Spotlight cohesion.** The card fill is the concert's extracted colour lerped over `FillCardDefault` at 0.11 (the
  `CardArtwork` idiom), standard card shadow, and the gap to the title row tightened to `WaveeSpace.M`.
- **Shy pill discipline.** `ResolveMonth` no longer falls back to the first month: it returns null until a board header
  has actually crossed the viewport top, and the pill deactivates immediately on a null resolve — it never exists at
  rest or anywhere above the boards region. Still `HitTestVisible = false` + `HitTestPassThrough`, still passive.
- **Stats nit.** The cities segment appears only when 2 ≤ distinct cities < shows ("18 shows · 18 cities" was
  redundant; "· 1 city" is noise). Tested.

R3.1 verification: Wavee build clean; 100 focused concert tests pass; full suite 1,082/1,089 with the same seven
pre-existing non-concert failures. The ArtistConcerts mapper populates per-event lineups (`MapConcert` → `MapArtists`),
so support-act secondaries render from live data. Visual acceptance remains with the user.

## Scope boundaries

Deferred: map rendering, inline ticket purchase, concert saving, nested vertical virtualization, speculative engine
primitives, automatic background location permission, and decoding/interpreting the opaque pagination token.
