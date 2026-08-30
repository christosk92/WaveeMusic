# Wavee hydration — ONE entry point for every catalog metadata fetch

Binding rule: **nothing in the app fetches catalog metadata except through `IEntityHydrator`.** If you are about to
write an HTTP/GraphQL/extended-metadata call that ends in a store write, you are writing a ladder step or a trait
projector, not a service.

Canon: `docs/plans/wavee/hydration-facade-design.md` (the shapes) · `hydration-facade-plan.md` (phases + status) ·
`docs/plans/wavee/architecture.md` §4.2/§4.3/§6/§9 (the seam view) · `metadata-entry-points-inventory.md` (the ~110
entry points this replaced) · [wiring-discipline.md](wiring-discipline.md) (no nullable seams).

Layering (binding): **ports in `Wavee.Core/Hydration`** (zero deps) · **engine in `src/apps/Wavee/Backend/Hydration`**
(engine-free, `IStore`-level, auto-compiled into `Wavee.Tests` by the `Backend\**` glob) · **Spotify transports and
provider-concrete projectors in `src/apps/Wavee/SpotifyLive/Hydration`** (engine-free, its own test glob). Anything
that returns a FluentGpu type cannot live under either glob.

---

## 1. The five levels

`HydrationLevel` is **presence only** — how complete the entity in the store is. Age is not on the enum; it is the
`HydrationLedger`'s TTL per `(kind, level)`. `HydrationLevels.Of(entity)` is a **pure** function (no store, no clock,
no I/O) and returns the HIGHEST rung whose predicate holds.

| Kind | `Identity` | `Open` | `Rich` | `Full` |
|---|---|---|---|---|
| Track | a real title (not `title == uri`) | + named artists, named album, usable image, duration | ≡ Open | + an `Availability` verdict (getTrack / TrackV4 files) |
| Episode | a real title | + show name, image, duration | ≡ Open | + the description the episode page renders |
| Album | a name | a named tracklist is really here | + ©/℗ or release date (kind 183) | + the `getAlbum` envelope |
| Artist | a name | an ASSEMBLED discography (own stubs named, every facet total covered) | + the overview (Popular + releases column) | + the EXTENDED chart (> `ArtistPopularTracks.OverviewSeedCap` top tracks) |
| Playlist | a header name | ≡ Rich ≡ Full: + a membership baseline | ≡ Open | ≡ Open |
| Show | a name | + baseline whose first `min(300, members)` episodes are at Episode.Open | (same as Open, more pages pending) | ALL members resident |
| Owner | a name | ≡ every rung — there is no second transport for a profile | | |

Where a kind's rungs are equivalent, `Of` returns the **higher name** on purpose, so `EnsureAsync(trackUri, Rich)`
can terminate at all.

**This is the ONE "is it cold?" predicate.** It subsumes `IsAlbumOpenReady`, `IsAlbumComplete`, the four-clause artist
gate, `HasMembership`, both `NowPlayingReady` copies, `ArtistStatsCache.IsFresh` and LibrarySync's "unnamed ⇒ cold".
Do not add a seventh. If you need a new notion of thin, add a rung predicate or a row-gap primitive
(`HydrationLevels.{TitleMissing,TrackUnnamed,RefNeedsName}`) — `StoreEntityMerge.TitleMissing` already delegates
there, so the merge discipline and the fetch gate cannot drift.

---

## 2. Hydrating an entity

### From the UI — ask the catalog, not the hydrator

```csharp
var album = await svc.Library.GetAlbumAsync(uri, HydrationLevel.Rich, ct);   // IMusicLibrary
```

`IMusicLibrary.GetPlaylistAsync / GetAlbumAsync / GetArtistAsync / GetShowAsync` all take
`HydrationLevel level = HydrationLevel.Open`. `StoreLibrarySource` ensures that rung through the hydrator and only
then reads the store. Picking the rung IS the API: `DetailPage` asks `Rich`, `DetailTrailing` (below the fold) asks
`Full`, a sidebar hover asks `Identity` and pays nothing.

### From backend / playback — ask the façade directly

```csharp
// one entity, blocking, attributed to a surface
var outcome = await svc.Hydrator.EnsureAsync(uri, HydrationLevel.Open,
    new HydrationOptions(Surface: TraitSurface.NowPlaying, Priority: 1), ct);

// a batch (queue, context, recents) — ONE POST for the whole mixed-kind set
await hydrator.EnsureManyAsync(uris, HydrationLevel.Identity,
    new HydrationOptions(Surface: TraitSurface.Context), ct);

// fire-and-forget warm-up: background, lowest priority, sheds first under pressure
_ = hydrator.EnsureAsync(uri, HydrationLevel.Open, HydrationOptions.Prefetch);
```

- `HydrationMode.Blocking` returns when the level is reached **or the ladder is exhausted** — it never hangs on a
  background continuation. `Background` returns the level resident at the caller's instant and enqueues on the pump;
  you then repaint off `IStore.Changes`. **Never** poll.
- A transport failure is an **outcome** (`HydrationStatus.Failed`), never an exception out of the façade. `Partial`
  = the ladder ran and could not get there. `Unsupported` = structurally impossible (offline, no owning source, a
  kind with no ladder) — not an error.
- `HydrationBatchOutcome.Missing` is what a caller that must render something (a queue, a context) uses to pick its
  placeholder.
- `LevelOf(uri)` is synchronous, presence-only and store-backed — cheap enough to consult in a render pass.
- `Invalidate(uri)` is the ONLY escape hatch from an Exhausted seal. Call it where a known-better answer arrives out
  of band (a dealer push, video canonical recovery) — i.e. wherever the old code called `MarkStale`.

### Who decides how much to block on

`OpenPolicy.For(kind, hasBaseline)` — the one table, returning `OpenPlan(Blocking, Background, Revalidate)`. Album
awaits `Rich`; artist awaits `Open` (the overview costs a second transport, so only the artist page asks `Rich`);
a playlist WITH a baseline revalidates in the background and blocks on nothing; a show awaits `Open` and pages the
rest on the pump. Change the open behaviour there, not at a call site.

Freshness/TTLs are the sibling table `HydrationPolicy` (identity/open 1h, artist Rich 12h, album Full 10min,
exhausted-playable 10min, exhausted-album-Rich 24h). A run that swallowed a **transport** failure reports it
(`ctx.ReportTransient(uri)`) and takes the SHORT exhausted window — "we could not ask properly" is not "there is
nothing to get".

---

## 3. Traits — the per-row facets

A **trait** decorates a row already in the store; it never mints a row and never carries a row's identity. All of a
surface's wanted kinds ride ONE POST.

```csharp
_ = svc.Hydrator.EnsureTraitsAsync(uris, TraitSurface.PlaysToggle);        // policy picks the set
await hydrator.EnsureTraitsAsync(uris, TraitSet.PlayCount, surface, ct);   // explicit set (toggles)
```

`TraitPolicy.For(surface)` is the surface → `TraitSet` table; `TraitSurfaces.ClientFeatureId(surface)` is the
surface → `client-feature-id` attribution table. Two pure tables, nothing else keys off `TraitSurface`.

| Surface | Trait set |
|---|---|
| `AlbumOpen` | `RowBundle` + `PlayCount` + `Publishing` (the Plays star IS the album surface's identity) |
| `PlaylistOpen`, `LikedSongs` | `RowBundle` + `PlayCount` **only when the Plays column is actually rendered** |
| `ShowOpen` | `RowBundle` (185 is a track trait; episodes have no play count) |
| `ArtistPopular` | `RowBundle` + `PlayCount` (counts are the ordering) |
| `Queue`, `Search` | `RowBundle` |
| `Recents` | `IdentityTraits` + `VisualIdentity` (the one surface attributed `mdata_esperanto`) |
| `NowPlaying` | `Video` only |
| `PlaysToggle` | `PlayCount` (the column just came on for rows that already have their bundle) |
| `TrackExpansion`, `Credits`, `PreRelease`, `UserProfiles` | `None` — these are display-only **reads** (§5) |
| `Prefetch`, `Context`, `None` | `None` — identity-only waves, one catalogue POST and nothing more |

`RowBundle = Video | AudioAttributes | Descriptors | VisualIdentity` (a real flags-OR, outside the table).

Adding a surface is ONE line in `TraitPolicy` (+ one in `TraitSurfaces` if it needs its own attribution).

---

## 4. Adding a trait (a new extension kind on a row)

One projector. Not a service, a cap, a memo and a caller list.

1. **Add the flag** to `TraitSet` (`Wavee.Core/Hydration/Traits.cs`), with the extension kind in its doc comment.
2. **Write the projector** in `Backend/Hydration/Projectors/` (store-only) or `SpotifyLive/Hydration/` (only if the
   target plane is a Spotify concrete, like `CoverColorPlane`). Implement `ITraitProjector`:
   - `Trait` — the flag a surface must want.
   - `Kind` — the `Xm.ExtensionKind` you decode. Its identity in the plan, the memo and the log tally.
   - `Companions` (optional) — extra kinds that ride the SAME uri group in the SAME POST because you need them to
     decide (video: 182 next to 99). Not planned/memoized/tallied on their own.
   - `AppliesTo(EntityKind)` — **always** `TraitApplicability.Applies(Kind, kind)`. Do not re-derive a table, and do
     not write a `spotify:track:` prefix test (that is what dropped every episode).
   - `AlreadyHas(store, uri, now)` — the mark. Pure, store-only, no allocation, no transport: it runs once per uri
     per pass. Answering `true` is what keeps a warm page at **zero** requests.
   - `Project(batch, uri, in payloads)` — fold into the store, writing **through `batch.Write(...)`** so the page
     coalesces into ONE lazy bulk signal. Return `Applied` / `Unchanged` / `Negative` / `NotResident`.
   - `CompleteBatchAsync` (optional) — an aggregate follow-up (video's canonical-alias recovery).
3. **Register it** in `TraitProjectors.Default` — registration ORDER is the plan/request/tally order, which is what
   makes a `traits.batch` log line diffable between runs.
4. **Extend `TraitApplicability`** if the wire probe covers a new pairing. Rule: a pairing the probe never covered is
   **ask-once** (`true`) — the 404 is the cheap authoritative answer and the negative memo makes it cost exactly one
   request per session. Guessing "never" is how episodes ended up with no traits at all.
5. **Add it to a surface** in `TraitPolicy`.

Payload rules that bite:

- `payloads.Get(kind) is null` ⇒ **the wire did not answer** ⇒ `NotResident`, which is **never memoized**. Absent is
  not missing; inventing a negative is a 24-hour wedge.
- `payloads.Missing(kind)` ⇒ an explicit 404/empty ⇒ `Negative`, memoized for the session.
- An **empty body** may be a real answer (a descriptor message with no descriptors is zero bytes). Use `Get`, not
  `Payload`, when empty is meaningful, and write the real "has none" value (`[]`) rather than skipping.
- Never mint an entity from a trait. A minted row is a row with no title, which every surface paints as a
  placeholder.

Wire the store merge to keep your value: e.g. `PlayCount > 0 ? incoming : current`, so a later TrackV4 write cannot
zero a count a trait landed.

---

## 5. Adding a display-only read (a drawer, not a row)

If the payload decorates a **drawer** and never writes the store, it is an `IExtensionReader` read, not a trait:
credits (186), pre-release (138), the expand drawer's 99/98/5/237, user profiles (15).

```csharp
var credits = await reader.ReadAsync(trackUri, Xm.ExtensionKind.CreditsV2Trait,
                                     ProjectCredits, TraitSurface.Credits, ct);
await reader.ReadManyAsync(uris, kind, parse, surface, ct);   // many uris, ONE kind, one POST per 300
await reader.ReadRawAsync(reqs, surface, ct);                 // multi-KIND raw bytes (the expand drawer)
reader.Seed(uri, kind, answer);                               // publish an answer you already know
```

What the reader gives you for free, and what you must therefore not rebuild: the parsed answer cached **including
null** ("this track has no credits" is an answer); concurrent opens sharing ONE load; a nav-away detaching only that
caller's await (the load runs on `CancellationToken.None`); the `client-feature-id` stamp; and the **shared**
`NegativeMemo` — a "no" learned by a drawer stops the row pass re-asking, and vice-versa.

Guard the polymorphic kinds at the call site *before the request exists* (kind 186 404s on albums/artists/playlists),
the way `SpotifyTrackCreditsService` does.

GraphQL display services (merch, similar, NPV, concerts, browse, popcount, what's-new, friends, user-top) stay
**return-only** and untouched — they do not write the store, so they do not belong on the ladder.

---

## 6. Adding a kind ladder

Implement `IKindHydration` in `Backend/Hydration/` and register it in the `ladders` list the composition root passes
to `SpotifyProviderHydrator` (a kind with no ladder answers `Unsupported` — not an error, and not a silent success).

- `Kind` — the `EntityKind` this ladder owns. One class may register twice (`PlayableHydration` is both Track and
  Episode; the only difference is that a thin track has a `getTrack` repair).
- `LevelOf(uri)` — store-backed `HydrationLevels.Of`.
- `ExtraCatalogKinds(in uri, level, into)` — extension kinds to FUSE into the shared step-0 catalogue POST. Usually
  empty; the batch stays one POST either way.
- `ContinueAsync(uris, level, opts, ctx, ct)` — repairs, second transports, assembles, awaited traits. **Batched, not
  per-entity**: one repair call for every unnamed row in the wave, not one per row. Post-steps the level does not
  wait on go on `ctx.Pump`. Recurse through `ctx.Hydrator` (the façade), never by calling another ladder directly —
  that is what routes every recursive ask through the same ledger and bounds the ref-closure. On a swallowed
  transport failure call `ctx.ReportTransient(uri)`.

The hydrator's shape is fixed and worth knowing before you add steps:

```
parse → drop what has no ladder → drop what is fresh (ledger seal)
      → ONE catalogue POST for the whole mixed batch  (step 0, SHARED across kinds)
      → per-kind ContinueAsync (sequential)
      → seal each uri at what it actually reached
```

**Adding a second provider:** implement the transport ports (`ICatalogFetch`, `IEnvelopeFetch`, `IArtistChartFetch`,
`IPlaylistOpener`, `IUserProfileFetch`) for that provider, build its own provider hydrator with its own ladders, and
expose it as that source's `ICatalogSource.Hydrator`. Nothing in `Backend/Hydration`'s shape changes. The P4
`HydrationRouter` groups a batch by `SourceRegistry.OwnerOf(uri)` and forwards each group — the same ownership answer
routing already uses, so there is no second notion of who owns a uri. A complete-at-construction source (export,
local files, the fake, user playlists, every test fake) implements nothing: the `Hydrator` DIM defaults to
`CompleteEntityHydrator`.

---

## 7. The rules

1. **No `spotify:track:` (or any uri) string tests.** `EntityUri` is THE uri vocabulary — `Parse`, `Kind`, `IdOf`,
   `Provider`, `IsPlayable`, `IsSpotify`. A grep gate fails the build for `StartsWith("spotify:track:")` or an `IdOf`
   outside `EntityUri`. Prefix tests are what dropped episodes everywhere.
2. **No per-service memo, cap, etag-or-raw fork, or 300-constant.** There is ONE negative memo (shared by the
   pipeline and the reader, bounded at 65,536, stops adding rather than evicting), ONE chunker
   (`MetadataChunking.MaxEntitiesPerRequest` / `ExtensionRanges`), ONE conditional path (`ExtensionEtagCache` — it is
   REQUIRED, never a fallback around it), ONE ledger.
3. **Store-writing = ladder or projector. Return-only = service.** If it writes the store it belongs on the ladder;
   if it returns a value to one screen it stays a plain service and never gains a cache of its own.
4. **The playlist plane belongs to LibrarySync.** `PlaylistHydration` never writes membership — it only asks through
   `IPlaylistOpener`. The ledger **never TTL-seals a playlist Open**: LibrarySync's in-flight set, 5-minute window
   and dirty set remain the freshness authority. A test pins this.
5. **Transient failures are reported, not swallowed silently.** `ctx.ReportTransient(uri)` from a `catch` — otherwise
   one 503 seals an album's `Rich` rung for 24 hours and costs a day of ©/℗ and row bundles.
6. **Wiring discipline** ([wiring-discipline.md](wiring-discipline.md)): no nullable seam, ever. A source with
   nothing to fetch gets `CompleteEntityHydrator`; an unowned uri gets `NotOwnedEntityHydrator`; the go-live seam is
   `SwitchableEntityHydrator` (one stable reference, volatile inner, `OfflineEntityHydrator` when logged out). Every
   go-live install registers its teardown through `LiveWiring`; `GoOffline` is `Uninstall()` in reverse order and
   `AssertCovers(Services.LiveSeams)` names any seam installed without one.
7. **Delete outright.** No legacy path, no compat shim, no second entry point "for now".
8. **Engine-free** under `Backend/**` and `SpotifyLive/Hydration/**` (both are compiled into `Wavee.Tests`);
   NativeAOT-clean; `TreatWarningsAsErrors`; new persisted records ride the existing `EntityJson` source-gen context.

---

## 8. Request-count expectations (`Wavee.Tests/ApiWaste/HydrationWasteTests`)

These numbers ARE the design. They run the whole stack a page open traverses — `StoreLibrarySource` →
`SpotifyProviderHydrator` → the ladders → `XmCatalogFetch` → `ExtensionEtagCache` → `ExtendedMetadataSource` → a fake
exchange — and decode the real gzipped `BatchedEntityRequest`. If your change moves a count, it fails here and
nowhere else; state the new count in the PR.

| Scenario | Expected |
|---|---|
| Album open, cold, `Rich` | **1** catalogue POST (AlbumV4) + **1** trait POST carrying the whole bundle for the album (183) and its rows — and **no** `getAlbum` |
| Album open, second time | **0** requests, and no second trait pass |
| Album with gid-only disc rows | **+1** — ONE batched TrackV4 repair for every unnamed row in the wave, never one per row |
| Album `Full` (below the fold) | 1 `getAlbum`, 10-minute cached |
| Liked / playlist, 10k rows | 1 trait POST per 300 rows; re-open with marks warm = 0 |
| Queue change / search | only the uris that are not fresh |
| Show open (300 episodes) | 1 POST of the ask-once kinds; the 404s are memoized (session) + persisted (24h); then 0 |
| Now-playing thin row | TrackV4 → `getTrack` **once**; then sealed Exhausted, so a cluster re-pushing the same row every second re-fetches nothing |
| Two surfaces, same uris, concurrently | ONE in-flight pass (the ledger's `Claim` shares it; each caller's own token detaches only its wait) |
| Any session | **no `(uri, kind)` pair is ever requested twice** |
| A fully warm page | zero requests AND zero store change signals (the bulk window is opened lazily on the first write) |

Related suites: `HydrationLevelsTests` (table-driven rungs), `HydrationLedgerTests`, `HydrationPumpTests`,
`HydrationSharedRunTests`, `HydrationTransientFailureTests`, the per-ladder `*HydrationTests`,
`Hydration/{TraitPipelineTests,ExtensionReaderTests}`, `TraitPolicyTests`.

```powershell
dotnet build src/FluentGpu.slnx ; dotnet build src/FluentGpu.slnx -c Release
dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj
```
