# Entity completeness & healing — one store-level seam

> **SUPERSEDED** by [`entity-data-layer-redesign.md`](entity-data-layer-redesign.md). Do **not** implement the
> heal-queue / completeness-registry / alias-table design below — that complexity was rejected in favor of five
> chokepoint root fixes. This file is retained only for the audit evidence and file:line citations the redesign
> still references.

Status: SUPERSEDED. Evidence: full-tree audit 2026-07-29; every claim carries file:line in the
audit record; the load-bearing ones are restated here. Prompted by the Liked-album-name bug ("Boulevard of Broken
Dreams" with an empty Album cell), whose point fix — `LikedAlbumNameBackfill` — is one surface's costume over a
generic problem.

## The three structural gaps (why this is systemic, not a one-off)

1. **The protective merge covers 3 of 6 entity kinds.** `StoreEntityMerge` (Store.cs:105-286) guards Track/Album/
   Artist field-by-field (`NonEmpty` — a known name can never be blanked). `UpsertPlaylist/Show/Episode`
   (Store.cs:530-534) are `_dict[uri] = value`: every playlist writer hand-rolls read-modify-write, and the ones
   that don't, clobber (playlist header fetch resets `IsPublic`/`BasePermissionRevision`/`Tuning`/`Collaborators`;
   a thin `Episode.ShowName` write erases a known one; `Show.Episodes` is never written by anyone → every podcast
   page reads "0 episodes").
2. **Nothing knows an entity is thin.** Acceptance is unconditional; thin-on-first-write is legitimate (several
   wire shapes genuinely lack fields — `queryArtistOverview`'s `albumOfTrack` has no name), but no signal, queue,
   or counter records that a heal is owed. Healing is per-surface and opportunistic; the gap matrix (audit §3)
   lists eight thin-entity × surface combinations with NO healer.
3. **Freshness is seeded on batch membership, not outcome.** MetadataService.cs:61 seeds `_res` for every miss in
   the batch — including entities that returned Missing/empty — then the 1h Etag TTL hard-blocks every healer
   (`SyncAllAsync` skip at :52-53). `ExtensionEtagCache.Fold:298-301` additionally persists an ETag for a
   `MissingValue`, creating a 304-forever loop. And re-requesting the same (uri, kind) cannot heal cross-entity
   thinness anyway: `Track.Album.Name` lives on AlbumV4 under the ALBUM uri — the seam must route
   field → owning (entity, kind), which is exactly what `LikedAlbumNameBackfill` already does for one field.

Predicate drift is the fourth, softer gap: nine hand-rolled "good enough?" checks with four different notions of
"empty" (audit §2). The now-playing repair missed `Album.Name` for exactly this reason, and
`PlaybackProjection.MergeClusterTrack` is a fourth merge implementation that is STRICTER than the store's
(`Title == uri` counts as missing; images compared by quality) — the store is currently the weaker guard.

## The design — three pieces, shipped in this order

### Piece 3 first: fix the freshness root (~5 lines, unblocks the healers that already exist)

- MetadataService.cs:61 — seed `_res` only for entities whose store version actually advanced (the version is
  already read there); a nothing-landed fetch gets a short negative TTL, not an hour of "fresh".
- Expose `MetadataService.MarkStale(uri)` (Resource.MarkStale:227-234 + IsStale:301 already support it).
- ExtensionEtagCache: don't persist an ETag on the 404/empty-200 fold paths (costs one full body at the next TTL
  expiry; closes the 304-forever loop).

### Piece 1: `EntityCompleteness` — one definition per kind (pure, zero-alloc, no store dependency)

One static class beside `StoreEntityMerge`: per kind, a `MissingFields` flags enum + `IsComplete`.
- Track: Title non-empty AND != Uri (adopt PlaybackProjection's stricter rule), Artists.Count > 0,
  DurationMs > 0, Image != null, and `Album.Uri != "" ⇒ Album.Name != ""` (identity-known-name-absent is the
  healable state; identity-absent is not).
- Album (display tier): Name, Cover, Year > 0, Artists. Deliberately SEPARATE from `IsAlbumComplete`
  (detail-envelope completeness is a different question and stays where it is).
- Artist: Name, Image. Show: Name, Publisher, Cover. Episode: Title, ShowName, Image, Duration.
  Playlist: Name, OwnerName.
All nine per-surface predicates either call this (now-playing repair ×2 duplicated sites, discography ×3 — which
also pick up the Cover/Year checks they currently miss, StoreLibrarySource artist display half) or are documented
as deliberately different (IsAlbumComplete = envelope; LibrarySync's membership/capability checks = container
facts; MergeClusterTrack = transient slab, but its Title/image rules get LIFTED into StoreEntityMerge.Track so the
store stops being the weaker guard).

### Piece 2: write-time detection + ONE healer (LikedAlbumNameBackfill, promoted)

In the store upserts, after merge, under `_gate`: `if (!IsComplete(merged, out var missing)) _healQueue.Offer(uri,
missing)` — branch-only, no allocation, suppressed while `_bulkDepth > 0` (bulk syncs pay the check, not the
queue). One bounded dedup queue (≈4096) drained by the promoted backfill service: same `_attempted`
once-per-session guard, BatchSize 300 / MaxPerPass 900, fire-and-forget with cts, one summary log line
(`store.heal` tag), drained only after `hydrated.Task` (the DiscographyPrefetcher ordering), coalesced via the
existing `TrailingCoalescer`, OFF the LibrarySync loop. The new logic is the router:
- Track.Album.Name → AlbumV4 on Album.Uri (the liked backfill's move, generalized to all surfaces)
- Track display fields → TrackV4; already-attempted → escalate Pathfinder getTrack (the now-playing repair's
  existing 3-tier encoded once)
- Album/Artist display → AlbumV4/ArtistV4; Episode.ShowName → ShowV4
- Show.Episodes → the one genuinely NEW fetch path (split out as its own change; most user-visible hole)

Plus: add `StoreEntityMerge.Playlist/Show/Episode` so the three blind-replace upserts merge like the others
(kills the ShowName clobber, the playlist header resets; lets three hand-rolled RMWs delete). Fix
CachedStore.cs:475-488 to persist the MERGED value (the Track/Album/Artist paths already re-read `_hot`;
Playlist/Show/Episode persist the raw input — fix together or the disk diverges from memory).

## Migration (summary — full table in the audit)

- `LikedAlbumNameBackfill` → becomes THE healer; its liked-specific filter is replaced by the router; the
  cold-start sweep survives for pre-fix caches; the per-hydrate `Schedule` call deletes.
- Duplicated stub-vs-rich fold (Store.cs:229 / ArtistOverview.cs:243) → one shared function.
- Deliberately excluded from EntityCompleteness itself: kind-222/179 adornments (Tempo etc.),
  VideoAssociation/Override — separate pipelines by design; a track with no tempo is complete. Confirmed against
  SpotifyTrackAdornmentService (:172 refuses to create rows) and the null-means-unknown merge rules.
  **But see §Side-table planes below — excluded from the PREDICATE is not excluded from the SEAM.**

## Side-table planes & URI identity (folded in from the linked-tracks/video investigation)

Live case (WP-ι instrumentation in flight): a playlist whose tracks show "no video" everywhere, while SEARCHING
the same tracks shows videos. The association plane is uri-keyed (`GetVideoAssociation(uri)`), and the candidate
roots are the same family as the entity gaps: (H1) uri identity aliasing — Spotify track linking means the same
logical track carries different uris per context (playlist = market-relinked, search = canonical), so a uri-keyed
lookup written under one alias misses under the other; (H2) per-surface request coverage — only some surfaces
request the association kind for their tracks; (H3) the SAME freshness-sealing root as Piece 3 (if associations
flow through MetadataService/ExtensionEtagCache, a nothing-landed batch seals them "fresh"); (H4) staleness by
design — `VideoPresence` is deliberately not a signal, so associations arriving after a page's `HasVideo` roll-up
never invalidate it until re-navigation. WP-ι's logging discriminates these; its report finalizes this section.

What folds into the seam regardless of which hypothesis wins:
1. **Piece 3 applies verbatim to side-table planes** if they ride the shared metadata service — the freshness fix
   must not be entity-kinds-only.
2. **Request-coverage principle:** any surface that DISPLAYS a plane's data ensures its context REQUESTS that
   plane (or the heal queue learns plane-kinds alongside entity-kinds — the router gains rows like
   `Track.videoAssociation missing-and-displayed → request kind-99 for the context's uris`). Whether "missing" is
   distinguishable from "genuinely has no video" is exactly the negative-caching question the adornment pipeline
   already answers (session negative cache) — reuse that discipline, never re-fetch known-negatives.
3. **URI identity is a store-wide concern, not a video concern.** If H1 confirms, EVERY uri-keyed side table
   (saved-state, play counts, adornments, associations) can miss through an alias. The design answer is a single
   alias table in the store (canonical ↔ relinked, populated from whatever linking data the wire provides —
   TrackV4 alternatives / linked_from, pending WP-ι's mapping) consulted by side-table lookups at ONE chokepoint —
   never per-surface alias handling. If the wire gives us no linking data, the fallback is request-time
   canonicalization (resolve-then-key). DECISION DEFERRED until WP-ι reports what linking data actually exists.
4. **Invalidation (H4):** if associations can arrive post-roll-up, the page-level `HasVideo` needs a cheap
   recompute trigger (an association-plane epoch/counter the detail page reads — NOT making VideoPresence a
   signal; the per-row probe stays allocation-free).

## Risks

- Piece 3: lowest risk, highest value-per-line. Ship first, alone if need be.
- Piece 1: behavioral tightening — predicates that currently pass thin entities will fire on-open fetches more
  often at first (SWR/etag makes them cheap; measure).
- Piece 2: cold-10k-library fetch storm is the main hazard — every write during InitialHydrate is thin by
  construction; the `_bulkDepth` suppression + post-hydrate drain + MaxPerPass caps are the mitigations. One
  flags computation per upsert on the hot path: keep branch-only.
- Show.Episodes needs a new fetch: its own change, not buried in the seam.

## Verification

Unit: EntityCompleteness per kind (Wavee.Tests beside the existing StoreEntityMerge/IsAlbumComplete coverage);
merge additions for Playlist/Show/Episode incl. clobber cases; the freshness seed change (no-op fetch must not
seed fresh). Integration (user runs): cold start on the real library with `store.heal` logging — watch batch
counts and the deferred field; a playlist row with a thin album heals without opening the album; a podcast page
shows real episodes; playlist privacy/tuning survives a header refetch.
