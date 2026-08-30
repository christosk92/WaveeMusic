# Wavee entity data layer — root fixes at the chokepoints, nothing new to operate

Status: SHIPPING (supersedes `entity-completeness-healing-design.md`). Produced by a 3-design competition
(demand-driven read-through vs typed-references vs minimal-delta), synthesized by a judge scored on NET COMPLEXITY
REMOVED, then adversarially verified — verdict SHIP-WITH-AMENDMENTS; all amendments are folded in below and marked
[AMENDED]. Every claim was verified against the tree; file:line citations live in the competition record.

---

## Status 2026-08-16 — superseded at the surface edge by the hydration façade

This design's **store-side** rules (protective merges per kind, outcome-not-membership freshness seeding, the
ETag-on-`Missing` rule, `CanonicalUri` as an adornment) still hold and still live where they landed. What has
changed is everything above them: the *surface edge* — "who decides to fetch, and how much" — is now implemented
by the hydration façade (`hydration-facade-design.md` / `hydration-facade-plan.md`), and several artifacts this
doc argued about no longer exist. Read this file for the merge/freshness reasoning; read the façade docs for the
fetch path.

| This doc said | Where it stands now |
|---|---|
| "the whether-to-refetch predicates die (call `SyncAllAsync`; freshness dedups)" | **Done, and further.** `HydrationLevels.Of(entity)` is now THE predicate — one pure, per-kind, store-free function that subsumes `IsAlbumOpenReady`, the four-clause artist gate, `HasMembership`, both `NowPlayingReady` copies, `ArtistStatsCache.IsFresh` and LibrarySync's "unnamed ⇒ cold". Presence is the rung; **age** is the separate `HydrationLedger` seal keyed `(locale, uri, level)`. |
| "the which-rows pickers collapse onto one ~15-line `StoreEntityGaps` static" | **`StoreEntityGaps` is deleted.** Its bodies moved into `HydrationLevels` (`TitleMissing` / `TrackUnnamed` / `RefNeedsName`) and `StoreEntityMerge.TitleMissing` delegates there, so the merge discipline and the fetch gate now share one notion of "thin" by construction rather than by convention. |
| "`IsAlbumComplete` stays, documented as envelope semantics" | **Gone.** It is `HydrationLevels.Of(Album) == Full` — the getAlbum envelope — with `Rich` (©/℗ from kind 183) and `Open` (a named tracklist) as the rungs below it. `CachedStore`'s thin restore reads back as `Rich`, which is what makes the below-the-fold envelope re-fetch inside its 10-minute cache. |
| "[AMENDED C5] the now-playing site KEEPS one `StoreEntityGaps` gate" | **Moot.** `NowPlayingProjection` now asks the façade for `Open` and the ledger's **Exhausted** seal is what stops a re-ask — one `getTrack` per thin row instead of one per cluster heartbeat. No bespoke gate, no `TrackResolver` Func. |
| "rule 3: the hydrate chokepoint closes references one level" | **Kept, moved.** The ref-closure is a ladder step that recurses through the façade (so every recursive ask goes through the same ledger, which is what bounds it) and lands its follow-up work on the bounded `HydrationPump` instead of an ad-hoc `Task.Run`. |
| "rule 4: every surface touches the chokepoint on open" | **Kept, made a table.** `OpenPolicy.For(kind)` is the one place that says which rung a page open blocks on and which it enqueues — instead of ~14 call sites each deciding. |
| "warts: permanently-omitted entities re-request once per hydrate touch" | Now bounded by the shared session `NegativeMemo` (capped at 65,536 `(uri, kind)` pairs, refuses to grow rather than evicting) plus the extension cache's durable 24h negative. |

Pointers: `hydration-facade-design.md` (the shapes), `hydration-facade-plan.md` (phases + status),
`docs/plans/wavee/architecture.md` §4.2/§6/§9 (the seam view), `.claude/skills/wavee/hydration.md` (how-to).

## Philosophy

The store is not broken and does not need a new shape. `StoreEntityMerge` is a carefully reasoned accretion
discipline (NonEmpty guards, null-means-unknown adornments, hydration levels, authoritative discriminators) that
never got finished: three of six kinds never received it, one line seals freshness on batch membership instead of
outcome, healing was built per-surface instead of inside the one method every surface already funnels through
(`SyncAllAsync`), and linking data the app already pays to download is discarded by the lean parser. Fix those
roots and **ordinary hydration becomes the healer**: no queue, no router, no thinness ledger, no completeness
registry, no per-row read-through, no parallel write plane. Thin writes stay legal (the wire is genuinely thin);
the system's answer to "a blank was written" is the same as its answer to everything else — the next batch through
the chokepoint closes it, the store's change signal re-runs the page's existing re-map, and `Skel.Region`/
`Loadable` render the gap meanwhile.

## The model — five rules, all in existing files

1. **Freshness records outcome, not membership.** A batched uri is sealed fresh only if projection reports it
   landed; a uri the response omitted stays a miss. An ETag is never persisted onto a `Missing` row; a 304 can
   never re-confirm one past its TTL. [AMENDED C1/P1: this rule applies to BOTH seal points —
   `MetadataService.SyncAllAsync` AND `ExtensionEtagCache.GetAsync`, whose absent-from-response fold seals a
   24h Missing today (worse than the 1h being fixed) and is what wedges the shipped video recovery. Seed from
   projection-reported landed uris, NOT store-version deltas — hearts/membership/association bumps share the
   version counter and would race.]
2. **Every entity kind gets the protective merge.** `StoreEntityMerge.{Playlist,Show,Episode}` join
   Track/Album/Artist; the strictest rules found anywhere (the cluster merge's `Title == uri` echo test, its
   quality-aware image pick) are lifted INTO `StoreEntityMerge.Track`; the duplicate merge implementations die.
   [AMENDED C2: the Playlist merge is NOT blanket-NonEmpty — it is an explicit per-field policy. The header
   writer is AUTHORITATIVE for Name/Description/Cover/Capabilities (absence = removal, so `ClearDescription`/
   `ClearPicture` and the dead-letter rollback keep working); IsPublic+BasePermissionRevision use the
   discriminator (adopt only when both present — the permission writer always writes both); Tuning/Owner/Tracks
   decided per-field with the clobber cases as repro asserts (ClearDescription, ClearPicture, rollback,
   Episode.ShowName, IsPublic).]
   [AMENDED P6: Episode merge needs an explicit `ProgressMs` rule — inventory the progress writer first; a
   `>0 ? incoming : current` guard makes a legitimate reset unrepresentable.]
   [AMENDED P3: the `ChooseBetter` image fold gets a same-source carve-out so a genuine cover change stays
   adoptable.]
   Companion: `CachedStore` persists the MERGED value for the three new kinds (re-read hot after upsert, as
   Track/Album/Artist already do) or disk diverges from memory.
3. **The hydrate chokepoint closes references one level.** After `SyncAllAsync` lands (or skips-as-fresh) a set of
   track uris, it scans those resident rows for refs with known identity and blank display
   (`Album.Uri != "" && Album.Name == ""`) and fire-and-forgets those album uris back through itself.
   Depth-bounded by construction (the second wave carries Album kinds only, never re-scanned for track refs);
   capped and session-deduped with exactly the bounds the deleted backfill shipped (300/batch, ≤900/pass,
   once-per-session set, every-512-rows lock yield). AlbumV4's projection rewrites every resident row of that
   album, so one fetch heals the playlist row, the now-playing panel, and the liked list at once.
   [AMENDED C3: there is NO ArtistRef arm — `ProjectArtist` writes only the Artist entity and nothing
   back-propagates names into resident tracks' denormalized artist lists, so an artist fetch cannot heal the row.
   Instead, a track whose OWN display is thin (per `StoreEntityGaps`) is re-entered as a TRACK uri — a TrackV4
   refetch carries named artists.]
4. **Every surface that shows entity data touches the chokepoint on open.** Playlists/albums/artists/contexts/
   now-playing already do. The two that don't get the same touch: Liked Songs (a fire-and-forget hydrate over its
   members on open — the existing `DetectVideos` hook pattern) and shows (below).
   [AMENDED P2: the liked-open touch MUST page at 300 (reuse CollectionFetcher's paging) — one
   `SyncAllConditionalAsync` over 10k members re-serializes all cached payloads into a single tens-of-MB array.]
5. **Track linking is an adornment, not a table.** `Track.CanonicalUri` (nullable; null = unknown-or-self) rides
   the existing null-coalesce merge like `Isrc`, persists free through `EntityJson`, and is populated from data
   the app already receives: `canonical_uri` is field 36 on the full TrackV4 message and the recovery pass proves
   the server sends it — the lean parser just drops it. One proto line + ~5 lines in `ProjectTrack` stamp it on
   every bulk hydrate; the shipped video recovery promotes what it derives onto the resident row instead of
   discarding it. Uri-keyed side planes (video associations, saved sets) bridge through it ONLY on a read miss,
   inside the store, under the already-held lock — zero cost on hits, no API change. Writes never canonicalize
   (playback licensing and playlist ops untouched).
   [AMENDED C6: the HEARTS bridge is read-display only until unsave routes to `row.CanonicalUri` — otherwise the
   bridge lights a heart the aliased surface cannot turn off (local unsave no-ops, server remove targets a uri
   not in the collection). Write-side canonicalization of unsave is a PRECONDITION of the hearts flag.]
   [AMENDED C7, honesty: the video miss-bridge does NOT fix the live playlist bug — the alias plane usually holds
   a fresh `None` ROW, not a miss. The live bug's fix is recovery + rule 1's un-wedging. The bridge helps
   never-detected aliases only.]

## The five audit gaps → why each becomes impossible-or-trivial

- **Blind-replace kinds:** merges (rule 2) make the ShowName clobber and the playlist header resets
  unrepresentable.
- **"0 episodes" forever:** ShowV4 already carries `repeated Episode episode = 70` and the lean parser drops it.
  Parse gid-only refs → `SetMembership(showUri, …)` (generic, persisted) → `GetShowAsync` joins membership to
  resident Episode entities (UI unchanged) → show-open hydrates the first episode page through the chokepoint.
  The playlist pattern verbatim. [AMENDED P4: precede with a kind-blind-consumer audit of the playlists/pin
  tables (GcSweepMemberships, NoteAdopted accounting), not just a field-70 size measurement.]
- **Thin writers / unhealed combos:** rules 3+4 make coverage structural — the closure scans ALL requested uris'
  resident rows, fresh-skips included, so rows cached thin by an earlier session heal on the next touch of any
  surface that shows them. [AMENDED C4: ONE combo is structurally out of reach and is named a wart, not hidden:
  artist appears-on stubs past the 20-item hydrate cap. Fix later with a windowed hydrate-on-shelf-scroll if it
  ever matters.]
- **Freshness lies:** rule 1 (both seal points + the ETag-on-Missing rule). This is the keystone — it also
  un-wedges the shipped video recovery.
- **Nine predicates / four merges:** the whether-to-refetch predicates die (call `SyncAllAsync`; freshness
  dedups); the which-rows pickers collapse onto one ~15-line `StoreEntityGaps` static (one notion of empty);
  render-local em-dash guards stay (presentation); `IsAlbumComplete` stays, documented as envelope semantics.
  [AMENDED C5: the now-playing site KEEPS one `StoreEntityGaps` gate — deleting both early-outs would ungate the
  Pathfinder getTrack fallback into one extra round-trip per track change.]
  `MergeClusterTrack` + its helpers are deleted AFTER their stricter rules are folded into the store merge; the
  ArtistOverview/Store stub-fold duplicate becomes one shared function ([AMENDED P5] via a small shape adapter —
  the two record types differ).

## What DIES

`LikedAlbumNameBackfill` (whole class + 3 wiring sites, ~145 lines) · `MergeClusterTrack`/`TitleMissing`/private
`MergeAlbumRef` (~25) · the stub-fold duplicate (~10) · the fetch-gating predicates (~50, one gate kept per C5) ·
the "0 episodes", header-clobber, ShowName-clobber, and 304-forever bug classes · **the entire incumbent design
unbuilt** (heal queue, thinness detector, field router, per-kind completeness registry, alias table; ~800-1,200
planned lines) · **designs A/B's machinery unbuilt** (per-read resolver overlay; RefHint plane + writer migration).

## What's NEW (complete)

Outcome seeding at both seal points (~17 lines) · three merges + Track strictness + CachedStore merged-persist
(~85) · chokepoint closure (~45, net −100 vs the backfill it deletes) · `StoreEntityGaps` (~15) · liked-open paged
touch (~10) · shows via membership (proto line + ~60) · `CanonicalUri` adornment + recovery promotion + store
miss-bridges (~45). Net ≈ +275 / −230 with zero new services, zero queues, no schema migration (membership,
video_assoc, EntityJson absorb everything; CanonicalUri is omit-null).

## Migration — independently shippable stages

- **S0 (hours, ship first, valuable alone):** rule 1 at BOTH seal points, projection-reported seeding, the
  ETag-on-Missing rule. Every existing opportunistic heal — including the shipped video recovery — starts working.
- **S1:** the three merges (per-field Playlist policy + ProgressMs rule + ChooseBetter carve-out) + delete
  `MergeClusterTrack` + stub-fold dedup + the five repro asserts + CachedStore merged-persist.
- **S2:** chokepoint closure (track-uri re-entry, no artist arm) + DELETE `LikedAlbumNameBackfill` + collapse the
  predicates onto `StoreEntityGaps` (keep the now-playing gate) + liked-open paged touch + relevance-arm
  broadening.
- **S3:** shows (preceded by the pin-table audit).
- **S4:** linked tracks — proto field 36 + `ProjectTrack` stamp + recovery promotion + video miss-bridge; hearts
  bridge ONLY behind write-side unsave canonicalization.

Each stage is green alone; S0-S2 touch no models and are safe against in-flight work; S3/S4 add omit-null fields
and proto lines only.

## Named warts (kept visible, not designed around)

1. An alias for which neither TrackV4 nor kind-212 yields a canonical keeps a dark video cell while search's row
   works — no design of any size fixes that.
2. Appears-on stubs past the 20-item cap stay thin until a windowed hydrate exists.
3. Permanently-omitted entities re-request once per hydrate touch until the session set trips — bounded,
   observable, ETag-cheapened.

## Verification

Unit: merge repro asserts (five clobber cases incl. rollback), `StoreEntityGaps`, outcome-seeding (an omitting
batch leaves the uri unsealed; a Missing row cannot 304 past TTL). Integration (user runs): the WP-ι
`video.assoc.*` capture recipe before/after S0 (the recovery counters must move); a playlist row with a thin album
heals without opening the album; a podcast page shows real episodes after S3; playlist privacy/tuning survives a
header refetch; `ClearDescription`/`ClearPicture` still clear.
