# Wavee ↔ Spotify wire parity — final e2e plan

> **STATUS: PARTIALLY IMPLEMENTED** (working tree; see research corpus for evidence).
> Wire research corpus: [`spotify-wire-research/`](spotify-wire-research/README.md)
> (start at [`CORRECTIONS.md`](spotify-wire-research/CORRECTIONS.md)).
>
> Shipped (uncommitted at promotion time): Workstream A (wire correctness incl. the searchAlbums hash),
> B (artist/home harvest), C-data (row adornments + liked content-filter), D1 (tinted placeholders),
> E0 (search facets), Browse (directory + pages + taxonomy + home card), home facet chips, artist
> watch-feed portrait, track versions drawer with per-item audio-format override, popcount, and
> related tests. Several last-review UX items and deferred surfaces remain — see the inventory in
> conversation / `spotify-wire-research/README.md`.

## Context

Wavee's Spotify seam has drifted from the live desktop client. **16 Fiddler captures (~11,000 sessions)**
were decoded protobuf-first — `all.saz` 2290, `omg.saz` 2440, `VIDEO.saz` 1415, `spotify.saz` 1147,
`content-filters.saz` 646, `playback_remote.saz` 676, `concerts.saz` 156, `concerts_v2.saz` 113,
`playlists.saz` 88, `artist_more.saz` 72, `someMoreMaybe.saz` 58, `waveforms.saz` 54, `final.saz` 48,
`pre-release.saz` 21, `signals_2.saz`, `lyrics.saz`.

**Intended outcome:** close the gap between what Spotify's wire offers and what Wavee consumes — starting
with data already arriving on calls Wavee already makes, then the surfaces it has no client for at all.

**Evidence corpus:** [`spotify-wire-research/`](spotify-wire-research/README.md) — `E2E-DIFF.md`,
`GAP-ANALYSIS.md`, and raw workflow dumps under `research/01–10`.

A Liked Songs content-filter chip feature was designed against this same corpus and its payload proto has
already landed; it shares the extended-metadata (XM) layers with everything else, so it is folded in here
as Workstream C. **This doc supersedes [`liked-songs-content-filters-plan.md`](liked-songs-content-filters-plan.md)**
(kept with a pointer, not deleted).

---

## ⚠️ Corrections to my own earlier analysis — read this first

I got three things wrong in earlier passes. All three are now settled against the wire.

**1. My decoder was silently truncating responses at 64 KB.** Spotify's XM/Pathfinder responses are
**multi-frame zstd**. `ZstdDecompressor().decompressobj().decompress()` stops after frame one; you must use
`stream_reader(body, read_across_frames=True)`. Measured on `omg.saz`: the home response is 241,679 bytes
and I was reading 65,536 — losing 73%. Every extension-kind count I reported before this point was an
undercount. Wavee's own `SpotifyZstd.cs:9` already documents this hazard for the playlist path; my
throwaway tooling didn't inherit it.

**2. `home`'s hash is NOT stale — retracted.** Wavee's `9052ac65…` appears **4 times across the corpus,
every one HTTP 200** (`all.saz` sid 0029, `playback_remote.saz` sids 002/037, `signals_2.saz` sid 053).
Spotify keeps old persisted-query documents alive. The only thing wrong with Wavee's home call is the
`timeZone` variable.

**3. Kind 222 is not Mix-lens-gated — retracted.** Corrected census: **11,559 payloads**, not the ~40 I saw
through the truncating decoder.

The important structural consequence of (2): **"Wavee's hash differs from the capture" does not mean
"broken."** It means *unverified*. Only a real HTTP 400 in `wavee.log` proves breakage.

---

## Hash status — measured, not assumed

| Op | Wavee hash on the wire? | Verdict |
|---|---|---|
| `home` | **4× HTTP 200** | ✅ Fine. Don't touch the hash. |
| `queryArtistOverview` | **0 occurrences** | ⚠️ Unverified. Migrate to `ae0e2958…` (18× 200) — gains `onPlatformReputationTrait` + richer `discography.latest`/`popularReleasesAlbums`. |
| `searchAlbums` | **0 occurrences** | ⚠️ Unverified. Migrate to `64ae1fe6…` (2× 200). Highest risk of the three: nothing else validates it. |
| `queryNpvArtist` | 1× 200 (Wavee's) | ✅ Both live. `b2cedf7e…` (16×) is a strict superset adding the verified-artist badge. Optional. |
| **All 12 concert ops** | **byte-for-byte identical** | ✅ **Zero drift** (`concerts.saz` + `concerts_v2.saz` exercise every one). Only action: assert `ConcertCountHash` in `ConcertCaptureContractTests.cs` — the one of 12 not covered. |
| `searchTracks`, `searchArtists`, `searchPlaylists`, `searchSuggestions`, `searchTopResultsList`, `getAlbum`, `getTrack`, `queryAlbumMerch`, `similarAlbumsBasedOnThisTrack`, `queryWhatsNewFeed`, `feedBaselineLookup`, `fetchExtractedColors` | identical | ✅ No action. |

---

## Decisions locked

| Decision | Value |
|---|---|
| `home.timeZone` | Send real local **IANA** zone (`TryConvertWindowsIdToIanaId`, fallback `Etc/UTC`) |
| `locale` on `queryArtistOverview`/`getAlbum` | Match capture: send `""` ⚠️ may lose localized bios — first thing to revert if they regress |
| `app-platform` | Derive from `RuntimeInformation.ProcessArchitecture` |
| spclient `spotify-app-version` | Bump `129300667` → `129400583` |
| Platform routing | Mirror capture per-op — **held to PR2** (adds `spotify-app-version` to *every* Pathfinder call; Wavee sends none today and works) |
| Unknown XM kinds | Named from `Any.type_url` — all resolved |

---

## PR sequencing

- **PR1** = Workstreams A → B → E0 → C → D, one commit each, in that order.
- **PR2** = per-op Desktop/WebPlayer identity mirror, isolated so a regression is attributable.

---

## Workstream A — wire correctness (small, mechanical)

**Files:** `SpotifyLive/PathfinderClient.cs` (`PathfinderOps`), `SpotifyLive/LiveSessionHost.cs`
(`FetchHomeAsync` ~L941, `FetchSearchAsync` ~L726-761), `SpotifyLive/SpotifyArtistStatsService.cs` (~L31),
`SpotifyLive/SpotifyAlbumEnrichmentService.cs` (~L68), `Backend/Spotify/HttpAuth.cs`
(`PathfinderHeadersMiddleware` ~L140-174), `Backend/Audio/SpotifyRuntimeIdentity.cs` (L9).

1. `SearchAlbumsHash` → `64ae1fe6df380b038c0a65a2606d3361bc270de6870b2fdc99cf0848b1efa6d3`.
2. `QueryArtistOverviewHash` → `ae0e2958a4ab645b35ca19ac04d0495ae12d9c5d7b7286217674801a9aab281a`,
   and `preReleaseV2` `false` → `true` at **both** call sites.
3. **Leave `HomeHash` alone.** Fix only `timeZone`:
   ```csharp
   static string LocalIanaZone()
       => TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "Etc/UTC";
   ```
   Verify this survives NativeAOT — it reads ICU.
4. `locale` → `""` on `queryArtistOverview` + `getAlbum`.
5. `app-platform` derived from process architecture; `DefaultAppVersion` → `"129400583"`.
6. Assert `ConcertCountHash` in `ConcertCaptureContractTests.cs`.

**Gate:** no `pathfinder … -> HTTP 400 (stale persisted-query hash …)` in `wavee.log` — the string
`PathfinderClient.QueryBodyBytesAsync` already logs. This is also how you'd learn whether the *old*
searchAlbums hash was ever actually broken.

---

## Workstream B — harvest data already arriving

**Files:** `Wavee.Core/Spotify/SpotifyExportMapper.cs` (`MapArtist` ~L1077),
`Wavee.Core/Spotify/SpotifyHomeComposer.cs`.

**Artist overview** (new hash): map `watchFeedEntrypoint` (populated on every captured artist),
`relatedContent.discoveredOnV2`, `featuringV2`, `sharingInfo.shareUrl`, `saved`, `preReleaseV2.data`,
`onPlatformReputationTrait`. Map `relatedMusicVideos`/`unmappedMusicVideosV2` **defensively** — still
`totalCount: 0` on every captured artist, so the item schema remains unverified.

⚠️ **Preserve the STATS-ONLY write discipline** at both call sites (`TopAlbums`/`AppearsOn`/`*Total`
nulled before upsert). That guard exists because `MergeAlbumCards` treats a non-null list as authoritative
— it is what prevents the "every artist caps at 10 albums" regression.

**Home:** `Compose` reads only `sectionContainer`; also map `greeting` and `homeChips`
(`[{id, label.transformedLabel, subChips[]}]`). `homeChips[].id` is exactly the `facet` variable Wavee
already sends as `""`. One captured `HomeFeedBaselineSectionData` carries an **`Episode`** where the
baseline path assumes `Playlist` — verify it degrades rather than throws.

---

## Workstream E0 — search facets + adjacent ops

`LiveSessionHost.cs:785` **throws `NotSupportedException`** for two facets `SearchFacet` already declares.
All facet ops share the variable shape `FetchSearchAsync`'s `Vars` writer (L731-742) already emits, so each
is *hash constant + switch arm + mapper*.

| Op | Hash |
|---|---|
| `searchPodcasts` | `0195d9f61b43606d490bca64c3456e3593528cea6cc05c7e822c7c42beed0f4e` |
| `searchAudiobooks` | `e05ac765d02c084f8783d3c1572b23d57761c43f47eb8b87ce2f9ccced3fa068` |
| `searchUsers` | `d3f7547835dc86a4fdf3997e0f79314e7580eaf4aaf2f4cb1e71e189c5dfcb1f` |
| `searchAuthors` | `4a9d403a7cbc7e19da5520d619a865472b35382b043bfa458154e73a5c6f46bd` |
| `searchFullEpisodes` | `d54e35fafe7520cb53883b86d012911cbad75c14ac079a917951c24cdb07c60f` |

Two traps: **`searchAudiobooks` is the only op sending `includePreReleases: true`** — the shared writer
hardcodes `false` and needs a per-op override. **`searchFullEpisodes` has a completely different, minimal
variable shape** (`{searchTerm, offset, limit, includeEpisodeContentRatingsV2}`) — it cannot reuse `Vars`.

**Discography paging** — `queryArtistDiscographyOverview` + `queryArtistDiscographyAll`, both
`5e07d323febb57b4a56a42abbf781490e58764aa45feb6e3dc0591564fc56599`, `All` taking
`{uri, offset, limit: 20, order: "DATE_DESC"}`. This is the principled fix for the 10-release cap that
`MapArtist` currently works around.

⚠️ **One hash can host multiple named operations** — three pairs above prove it (`recentSearches`/
`saveRecentSearches` also share `2520a5aa…`). `PathfinderOps`' one-name-one-hash convention must allow two
names on one hash constant. `QueryAsync(operationName, hash, …)` already takes them separately.

**Search cadence:** the real client fires `searchSuggestions` per keystroke (22 calls to type "wasa") and
the heavy `searchTopResultsList` only on commit — rather than debouncing a single query.

---

## Workstream C — Liked Songs content-filter chips

Product choice **A**: exclusive chips (`All` + one tag), chip shown only if ≥1 liked track carries it,
search/funnel AND on top. Proto already landed at `SpotifyLive/Protos/extension_descriptor.proto`.

```
GET {spclient}/content-filter/v1/liked-songs?subjective=true&market=from_token
If-None-Match: <etag>  → 304
→ { "contentFilters": [ { "title": "Mellow", "query": "tags contains mellow" }, … ] }
```
ETag is `{iso}#{int}#{int}#{int}` — store and echo **verbatim**, don't parse. Only `tags contains <token>`
is supported; drop other query forms.

**✅ The plan's biggest caveat is now resolved.** The superseded plan warned kind 6 was "one sample, one
track" and that multi-descriptor parsing was unverified. With the corrected decoder there are **210
payloads**, and multi-descriptor is the *norm* — 1 to 33 descriptors per track, median ~13:

```
spotify:track:02BcXEH1zJYbXSabPtNlKf
  ('pop', 0.901, 'Pop')  ('dance pop', 0.858, 'Dance Pop')  ('fast', 0.785, 'Fast')
  ('energetic', 0.765, 'Energetic')  ('electropop', 0.756, 'Electropop')  … 12 total
```

Descriptors arrive **sorted by descending weight**, and `text` is the lowercase match token while
`display_name` is the Title-Case label. **Join on `text`, case-insensitively** — not `display_name`.
A real multi-tag fixture can now replace the synthetic test.

**Architecture — Approach R:** `TrackTags` side-table mirroring the `VideoAssociation` pattern
(hot dict + cold table + `ExtensionEtagCache`), Liked join stamping `Tags` like `AddedAt` does. Rejected:
denormalising onto `Track` (entity-JSON churn, pollutes non-Liked surfaces); parse-at-filter-time.

**Reuse:** `ExtendedMetadataSource.GetExtensionsWithHeadersAsync` already returns per-entity
`(Status, Etag, OfflineTtlSeconds, Payload)` and already chunks by body size. `SpotifyVideoService` is the
Ensure/Apply/negative-cache shape to mirror.

**Rules from the wire:** batch **300** (measured ceiling); persist the server's `cache_ttl` (86400) /
`offline_ttl` (2592000); Ensure only delta URIs on a heart; selection sticky **by tag token**; snap to
`All` when a chip's match set empties; hide zero-match chips; never auto-select.

**UI:** Liked only, above the track-list chrome, `RowChip`/`TypePill` language (not `Segmented`). Selection
must reach children via `Signal`/`Func`/context or a changed `Key` — **never a frozen field on an
`Embed.Comp` factory**.

---

## Workstream D — XM seam + high-value kinds

Build the tag plane as a **generic descriptor seam** — `(ExtensionKind, payload parser) → data` — not a
kind-6 special case.

**Corrected census** (multi-frame decode, over omg + all + VIDEO + waveforms + someMoreMaybe):

| n | Kind | Message | Wavee |
|---|---|---|---|
| 217,148 | 10 `TRACK_V4` | `metadata.Track` | ✅ |
| 138,613 | 182 | `ConsumptionExperienceTrait` | ✅ |
| 128,451 | 179 | `VisualIdentityTrait` | ❌ |
| 128,419 | 249 | `ContentExperienceTrait` | ❌ |
| 128,232 | 212 `PLAYBACK_TRAIT` | `PlaybackTrait` | ✅ |
| 127,877 | 178 | `IdentityTrait` | ❌ |
| 11,567 | 99 `VIDEO_ASSOCIATIONS` | | ✅ |
| **11,559** | **222** | `audio_attributes.v2.AudioAttributes` | ❌ |
| 11,323 | 85 `ORIGINAL_VIDEO` | | ✅ |
| 882 | 239 | `ContentCapabilityTrait` | ❌ |
| 747 | 16 `CANVAS_V1` | | ❌ |
| 215 | 6 `TRACK_DESCRIPTOR` | | ❌ (Workstream C) |

Append to `Protos/extension_kind.proto` (names derived from `type_url`, comment them as such):
`MIX_BEATS=217`, `MIX_VOCAL_ACTIVITY=218`, `MIXABILITY=219`, `ENTITY_TYPE_TRAIT=220`,
`AUDIO_ATTRIBUTES_V2=222`, `MIX_STATE=225`, `MIX_THREE_BAND_WAVEFORMS=237`,
`CONTENT_CAPABILITY_TRAIT=239`, `CURATION_EXPERIENCE_TRAIT=246`, `CONTENT_EXPERIENCE_TRAIT=249`.

### D1 — Tinted art placeholders (kind 179) — highest value-per-line in this plan

**Problem observed in the app:** every un-loaded cover in a track list is a blank grey square. With long
lists this is most of the visible surface while scrolling.

**Kind 179 `VisualIdentityTrait` already carries the colour**, in the *same payload* as the image URLs —
so there is **no extra request**, and the colour is available the instant the row exists, before any image
byte arrives:

```proto
VisualIdentityTrait {
  VisualIdentity f1 {
    repeated Image f1 { f1 { f1 url }, f2 size_kind }   // 1,2,3 = small/medium/large
    ColorSet f2 {
      f1 / f2 / f3   // three schemes (light / dark / high-contrast), each a set of RGBA roles
      f4 RGBA { uint32 r=1; uint32 g=2; uint32 b=3; uint32 a=4 }   // flat fallback
    }
  }
  f4 { canvaz url, further images + colour sets }
}
```

Verified: track `0spnMEFDuWTQRTsI941Q5n` → `RGBA(172,184,245,255)` = `#ACB8F5`;
track `1Z6ATl1884bjW8hl2cSFWR` → `RGBA(187,187,187,255)` = `#BBBBBB` (a genuinely greyscale cover — the
field is correct, not a default).

The nested schemes decode to the same role set `getDynamicColorsByUris` returns as JSON
(`backgroundBase`, `backgroundTintedBase`, `textBase`, `textSubdued`, `textBrightAccent`) — so 179 is the
**protobuf equivalent of that operation**, already in the row bundle. Prefer it; reserve
`getDynamicColorsByUris` for surfaces where no 179 was fetched.

**Apply to:** track-row thumbnails, card/grid thumbnails, album and playlist heroes, and the artist
person-picture — anywhere an image is pending or absent. Use `f4` flat RGBA for the fill; pick the scheme
by theme when a richer treatment is wanted. 128,451 payloads in the corpus, so coverage is broad.

**The three worth consuming now:**

- **239 `ContentCapabilityTrait`** — the server stating `music-video-disabled` / `other-video-disabled` /
  `offline`. This is **exactly the signal the video-detection campaign currently infers**. Highest-leverage
  item in this workstream.
- **16 `CANVAS_V1`** — looping canvas video behind now-playing: URL, poster renditions, artist attribution
  in one payload. ~29% coverage; a 404 is normal, not an error. The video surface already exists.
- **222 `AudioAttributes`** — `{tempo: double, key{name, mode, camelot{code, color}}}`. BPM + musical key
  per track, 11,559 payloads. Standalone value (track detail, tempo sort) with no DJ feature.

**Standing prohibition:** do not add `zstd` to any `Accept-Encoding` without wiring
`SpotifyZstd.MaybeDecompressZstd` — and note `HttpPools.cs:46` sets `DecompressionMethods.All`, which does
*not* include zstd, so today Wavee correctly negotiates gzip/br.

**Two decode traps for whoever implements this:** per-entity XM status codes include **451 and 400**, not
just 200/304/404; and a **200 with an absent `Any.value` is a valid "nothing here"** (kind 85 did this
10,069/10,069 times). Neither is a decode failure.

---

## Also available — documented, not scheduled

- `getDynamicColorsByUris` `f0f112945d6d745bd8ff790317bbf8d310036da75df33130490e9d6dc96c59d9` — pre-graded
  dark/light × contrast palette; deletes client-side contrast math from the cover-tint work. Takes
  `spotify:image:` URIs, **not** https URLs.
- `browseAll` `dbd8b55e09a58afc52eab438bc228ba28fd72ac2f2148c6c26354980e4579001` + `browsePage`
  `f5c4e6d668f5716464a231c1cc8b22c1cbf6ad68b09929fd7de813a30581298b` — the whole Browse/genre tab Wavee
  has in no form; renders through the existing home-section machinery.
- `GET /device-capabilities/v1/capabilities` — authoritative `supports_hifi` / `audio_quality` /
  `supports_dj`, instead of guessing quality tiers.
- `GET /recently-played/v3/recently-played` — the recents rail in one call; the only surface carrying
  `spotify:station:` contexts.
- Podcast NPV stack (`queryNpvEpisode`, `showItemsPlayedState`, transcripts, chapters, comments) — fully
  decoded, nothing blocked. Chapters need **no new parser**: `/playlist/v2/list/podcast-chapters/{uri}`
  returns the standard playlist list format.
- Waveforms: **kind 237 `ThreeBandWaveforms`** `{sample_rate: 44100, hop_ms: 20, band_low/mid/high: bytes}`
  — 50 Hz confirmed against a known 4:09 track (12466 B ÷ 50 = 249.3 s). Band ordering inferred from
  energy; `band_low` is 420 B longer than the other two, so a shared-timebase render drifts ~8 s.
- Pre-release save: XM kind 138 + `collection/v2/write`, zero GraphQL.
- gander unread + `ResetLatestCursor` — fixes a cross-device badge desync.

**Do not implement** (ad/telemetry/experiment/metering): melody msg batch, ads/v3 hpto, aet.spotify.com
beacons, desktop-update, capping-api, quicksilver. Also excluded: the `raw.githubusercontent` amll-ttml-db
traffic in one capture — that's a third-party Spicetify lyrics mod, not Spotify API surface.

---

## Still blocked on capture

| What | Why |
|---|---|
| `relatedMusicVideos` item schema | `totalCount: 0` on every captured artist across all 16 captures |
| `content-filter` 200 body | 304 in every capture (body known only from a prior separate run) |
| `popcount` semantics | field 7 reads 209 K / 3.1 M / 1 / 0 plausibly but **128,345,311** for one editorial playlist; one row has field 1 = exactly 2× field 7. Not safe as a follower badge |

---

# PART II — UI design & code changes

Everything above is the wire. This part turns it into surfaces. **Decisions locked with the user:**
Browse taxonomy = curated map in code, **localized**; Browse lives **inside Search**; track tint =
`uint? Tint` **on the `Track` record**; expansion fetch = **222 prefetched with the row bundle, 98/99/237
on expand**.

## The through-line

One idea connects all six surfaces: **the wire already knows more about each entity than Wavee draws.**
Colour ships with the image. Tempo ships with the row. Versions ship next to the track. So the UI work is
mostly *revealing* data already in flight — which is why almost none of it costs a new request.

Three rules hold across every surface below:

- **Props freeze at mount.** Anything that changes after first render reaches a child as a `Signal`/`Func`,
  via `Ctx.Provide`+`UseContext`, or through a changed `Key`. Never a plain field on an `Embed.Comp`
  factory. (`docs/design/subsystems/component-props-contract.md`; `FG_REUSE_GUARD=1` catches it.)
- **Zero managed alloc in frame phases 6–13.** Match sets, group maps and column arrays are precomputed
  off the hot path; no LINQ inside bind thunks.
- **Craft tokens only** — `Spacing.*`, `Tok.*`, `Radii.*`, `MotionTok.*`, `Icons.*`. No raw hex, no ad-hoc
  timers. Colours that come *from the wire* (cover tint, browse header) are the sole exception and enter
  as `WaveePalette.ToColor(argb)`.

---

## S1 — The track row (the highest-traffic surface in the app)

**File:** `src/apps/Wavee/Components/TrackRow.cs` — the single source of truth for every track cell
(detail list, library pane, artist Popular, search). Changing it once changes every surface, which is
exactly why it must be changed carefully.

### S1.1 Tinted art placeholder — fixes the blank squares

**The defect:** `MediaCard.cs:191` passes `placeholder: ColorF.Transparent`, and `TrackRow`'s art cell has
no tint at all. On a long list, un-loaded covers are most of the visible surface.

**The fix is one argument**, because `ImageEl` already accepts a placeholder colour *and* a `BlurHash`:

```csharp
// TrackRow.ArtCard / MediaCard shelf art
Image(cover?.Url ?? "", ImageFit.Cover, 1f, ShelfDecodePx, r,
      placeholder: t.Tint is { } argb ? WaveePalette.ToColor(argb) : Tok.FillCardSecondary)
```

Never `Transparent` again: fall back to `Tok.FillCardSecondary` so a track with no 179 still gets a
surface, not a hole. The tint arrives with the row bundle, so it is available **before the first image
byte** — the list paints coloured immediately and images resolve into place.

Apply at: `TrackRow.ArtCard`, `MediaCard` shelf + grid art, `ArtistPage.Hero`, detail heroes, and the S3
person picture.

### S1.2 Tempo · key column

New `ColumnSet` flag and cell key, following the existing pattern exactly:

```csharp
internal readonly record struct ColumnSet(bool Album, bool By, bool Date, bool Video, bool Plays,
                                          bool Heart, bool Thumb, bool Tempo = false,   // ← new
                                          bool Actions = true, int Tier = 0);

internal static class CellKey { …; internal const string Tempo = "c.tempo"; }
```

Cell renders `101.0 · A 11B` in `font-variant: tabular-nums`, `Tok.TextSecondary`, preceded by a 8px
rounded swatch filled with `key.camelot.color` — colour carries the identity so the text stays short.

**Breakpoint behaviour** (respects the existing tier ladder): Tempo is the *first* column dropped under
pressure — it is enrichment, not identity. Show at `Tier <= 3`, hide above. It sits between `Plays` and
`Duration`.

**Off by default except where it earns its place:** on by default in Liked Songs and playlist detail;
off in search results and artist Popular, where the row is already dense.

### S1.3 Chevron + expander — every version of a track

**User value.** Today a music video, a live take and a sped-up edit are three unrelated rows scattered
across the catalogue, or absent. The expander makes a track the thing you open, and its versions the
thing inside — which is where the differences (tempo, key, length) become visible and comparable.

**Row layout** — chevron at the **end**, after duration:

```
[#] [♥] [art] [title/artist] [album] [versions] [tempo·key] [duration] [›]
```

`›` rotates 90° on expand (`MotionTok` tween, `TransitionChannels.Rotation`). A `2 VERSIONS` pill sits
before the tempo column, present only when 98/99 returned anything — so the chevron never promises an
empty drawer. **Rows with no versions still expand** (to reach the format menu), but the pill is absent.

**Virtualization — the one genuinely hard part.** `DetailTracks` uses fixed-extent `Virtual.List`, whose
scroll math is O(1) precisely because every row is `RowHeightFor(density)`. An expanding row breaks that.

Do **not** switch to the legacy `Virtual.VariableList`. Use **`Virtual.Measured` with an
`IMeasuredVirtualLayout`** (`src/FluentGpu.Controls/Virtual.cs:56`), which is the seam built for this:
rows realize at the layout's estimate, correct to their measured extent on arrange, and **the engine
re-pins the scroll anchor across corrections** — that anchor-pinning is what stops the list jumping when a
row above the viewport expands. The layout is stateful: create it once in a `UseMemo`, never per-render.

Estimate stays `RowHeightFor(density)`, so a list with nothing expanded measures identically to today.

**Expanded content** (`TrackVersionsPanel`, new), indented to the title column:

| Group | Source | Row shape |
|---|---|---|
| *This track* | the row itself | dashed border, square art, waveform, tempo·key, split-play |
| *Music video* | kind **99** | **16:9** thumb — matching the 2560×1440 stills |
| *Alternate audio* | kind **98** | **square** thumb — matching the 600×600 covers |

The thumbnail aspect ratio *is* the type signal; no icon needed. Each entry carries a mini three-band
waveform (kind 237) and its own tempo·key, because that is the comparison the panel exists to make.

⚠️ **98/99 give a `target_uri` and artwork — no label.** "Live", "Remix", "Sped up" do not exist on the
wire. Deriving them needs a second XM hop to resolve each `target_uri` to its track name, then a
suffix parse of the parenthetical. **Phase this:** ship v1 with the resolved *track name* only and no
type pill; add labels in a follow-up once the resolve step exists. Do not ship a fabricated pill.

### S1.4 Split-play with format override

Each version row (and the *This track* row) ends with a split control: **play** on the left, **caret** on
the right opening a `MenuFlyout` of the formats kind 5 actually returned for that audio entity, with
`average_bitrate` as secondary text and a tick on the current choice.

```
AAC 24    24 kbps
OGG 96    90 kbps
OGG 160  151 kbps
OGG 320  319 kbps   ✓
FLAC     897 kbps
```

Footer line: `Overrides your default for this item only` + the device ceiling from
`device-capabilities/v1/capabilities` (`supports_hifi`, `audio_quality`). Formats above the device ceiling
render disabled with the reason, rather than being hidden — the user should see that FLAC exists and why
it is unavailable.

Reuse the existing `MenuFlyout`/`CommandBarFlyout` control rather than hand-rolling a popover, so
light-dismiss, focus return and keyboard nav come free.

---

## S2 — Browse, inside Search

**Decision:** Search's empty state *becomes* Browse. Type → results; don't type → browse. This gives the
currently-blank search page a job and adds no sidebar weight.

**File:** `src/apps/Wavee/Features/Search/SearchPage.cs` + new `Features/Browse/`.

### S2.1 The directory — `BrowseDirectory` (new)

Per the design: **a typographic directory, not a tile wall.** Eyebrow `BROWSE ALL`, title
*Discover something new*, then grouped columns of plain text links. No artwork, no colour blocks — the
page is a table of contents, and reads as one.

```
BROWSE ALL
Discover something new

TOP        Music        Podcasts     Audiobooks    Live Events
FOR YOU    Discover     EQUAL        Fresh Finds   GLOW    Made For You   New Releases
GENRES     Afro         Alternative  Ambient       Arab    Blues          Caribbean …
```

- Responsive `GridEl`, 6 columns wide → 3 → 2 under the existing breakpoint tiers.
- Items are `Focusable` `AutomationRole.Link`, `Tok.TextPrimary`, hover → `Tok.AccentTextPrimary`.
  Keyboard order follows reading order, not column order.
- Alphabetised **within** each group (as in the design), so the eye can bisect.

**The curated, localized taxonomy.** `browseAll` returns one flat section of 70 — the grouping is ours:

```csharp
// Features/Browse/BrowseTaxonomy.cs
// Maps a browse page uri -> group. Uri-keyed (stable) not title-keyed (localized, drifts).
// Unmapped uris fall to More, so a new Spotify category degrades instead of disappearing.
static readonly FrozenDictionary<string, BrowseGroup> Map = …;
enum BrowseGroup { Top, ForYou, Genres, MoodActivity, Charts, More }
```

**Group *labels* are localized; group *membership* is not.** New keys in
`src/apps/Wavee/assets/loc/en-US.json` under the existing lowercase-section convention:

```json
"browse": {
  "eyebrow": "Browse all",
  "title": "Discover something new",
  "top": "Top", "forYou": "For you", "genres": "Genres",
  "moodActivity": "Mood & activity", "charts": "Charts", "more": "More",
  "exploreAll": "Explore all categories"
}
```
The loc source-generator picks these up as `Strings.Browse.*` from the `FluentGpuLocBase` AdditionalFiles
entry (`Wavee.csproj:122`) — no generator change needed.

⚠️ Category **titles** stay server-supplied and already localized (`cardRepresentation.title.transformedLabel`
returned "Dutch music" for an nl market). Never localize those ourselves.

### S2.2 The category page — `BrowsePage` (new)

Editorial, per the design: eyebrow + big title over a soft wash of `header.color.hex`, then **shelves of
cards** — which is `Rail` (`Components/Rail.cs`), already built, already edge-faded and chevron-paged.

```
EDITORIAL
Recommendations                       ← header.title, wash = header.color.hex

Editors: best Dutch releases so far    [Rail of MediaCards]
Hand-picked new releases               [Rail of MediaCards]
Playlists from our Editors             [Rail of MediaCards]
```

Section → UI mapping, driven by `data.__typename`:

| `__typename` | Renders as |
|---|---|
| `BrowseGenericSectionData` | `Rail` of `MediaCard` |
| `BrowseGridSectionData` | condensed text-link group (same vocabulary as the directory) |
| `BrowseRelatedSectionData` | the trailing “related categories” block |

**Page foot**, exactly as designed: the condensed grouped links (FOR YOU / GENRES / CHARTS / MORE) then a
full-width **Explore all categories** button routing to the directory. That button is what makes the tree
navigable without a breadcrumb.

**Four guards the mapper needs** — every one observed on the wire:

1. A page can return **HTTP 200 with `data.browse` carrying only `__typename`** — no header, no sections.
   Render `EmptyState`, never throw.
2. `header.color` is **null on some pages** (Made For You). Fall back to `Tok.FillLayerDefault`.
3. A section item can be **`NotFound`** mixed among playlists. Skip it; do not render a broken card.
4. **Two independent paging axes.** `pagePagination` pages *sections*; `browseSection` pages *items within
   one section*. A "Show all" on a shelf calls `browseSection` and must not touch page paging. Music
   returned 10 of 14 sections, and its embedded grid 21 of 57 items.

**Live Events is not a page.** It is a `BrowseClientFeature` with `featureUri: spotify:concerts` — route it
straight into Wavee's existing Concerts hub (`ConcertRoutes`). One tile, zero new code.

### S2.3 Routing

`browse` and `browse:{pageUri}` join the route table in `Features/Shell/ContentHost.cs` beside the existing
`DiscographyRoute.Is(r)` / `ConcertRoutes.Is(r)` arms, with `BrowseRoutes.Is(r)` following that idiom.
Keep-alive caching applies as it does for other routes, so back-navigation from a category is instant.

---

## S3 — Artist: the watch feed inside the profile picture

**User value.** `watchFeedEntrypoint` is populated on **every captured artist**, and today it is dropped.
The artist page has a hero band but no profile picture at all.

**File:** `Features/Detail/ArtistPage.Hero.cs`, new `Components/PersonPicture.cs`.

A circular `PersonPicture` (88px on wide tiers, 64px narrow) sits over the hero band. When
`watchFeedEntrypoint.video` is present it loops that clip **inside the circle**; otherwise it shows
`visuals.avatarImage` — the same control, one code path.

- **Clipping:** `ClipToBounds` + `Corners = CornerRadius4.All(r)` on the *video surface itself*, not only
  the parent, so the round mask survives the compositor path the video host uses.
- **Hover:** scale 1.045 (`MotionTok`), an accent inset ring, and a play scrim fading in. Focusable, with
  Enter/Space activating — it navigates to the watch feed.
- **Cheap by construction:** the clip only decodes while the picture is on screen. Bind the existing
  visibility/occlusion signal the way the video surfaces already do, and stop the decoder on exit —
  an artist page scrolled past must not keep a video pipeline warm.
- **Reduced motion:** render `thumbnailImage` as a still and never start the decoder.

`VideoPlacementHost`/`VideoSurfaceRegistry` already own surface placement — this is a new *placement*, not
a new pipeline.

---

## S4 — Home: composite facet chips

**Files:** `Features/Home/HomePage.cs`, reusing `Components/ConcertUi.cs` vocabulary.

`homeChips[]` carries `subChips[]`, which is natively the two-level shape the **Concerts filter bar**
already implements (`ConcertFilterBar.cs`). Reuse that grammar rather than inventing one:

- `ConcertUi.FilterToken` for the loose chip
- `ConcertUi.SegmentedDatePill` is the *fused* two-segment pill — generalise it to
  `ConcertUi.SegmentedPill(name, value, onClick)` and use it for both surfaces
- Thin dividers between groups; `FILTER BY` eyebrow paired with a live count

Flow: **Music** → its `Following` sub-chip appears → picking it **fuses** into `[Music │ Following ✕]`,
carrying its value and reopening on click. Selecting a chip writes `homeChips[].id` into the `facet`
request variable — already in the request, currently always `""`.

Keep `ConcertFilterBar`'s fly-into-the-pill transition (`LayoutTransition` + `Exit(Dx: -56f)`,
`ConcertFilterBar.cs:127`) so the two surfaces feel like one system.

⚠️ **Do not map the wire `greeting`.** Wavee already has `home.goodMorning/goodAfternoon/goodEvening` and
computes it locally — which is correct once `timeZone` is fixed (Workstream A) and avoids a redundant
server string that would fight the app's own localization.

---

## S5 — Search: the facets that currently throw

`LiveSessionHost.cs:785` throws `NotSupportedException` for two facets `SearchFacet` already declares.
This is pure completion work: each facet is *hash constant + switch arm + mapper*, and the tab strip in
`SearchPage.cs` already renders from the facet enum.

New tabs: **Podcasts**, **Audiobooks**, **Profiles**, **Episodes**. Result cards reuse `MediaCard` with
round art for profiles (the existing `.round` treatment) and publisher/author as secondary text.

**Cadence, matching the real client:** `searchSuggestions` per keystroke (22 calls to type "wasa"),
`searchTopResultsList` only on commit. Today Wavee debounces one query for both — splitting them makes
the suggestion dropdown feel instant while the heavy op stays rare.

---

## S6 — Now playing

**Files:** `Features/Player/NowPlayingPanel.cs`, `Features/Shell/SeekBar.cs`.

- **Canvas** (kind 16) behind the art, blurred and de-saturated so text contrast survives. 404 is the
  normal case (~29% coverage) — absence is not an error state.
- **Three-band waveform** (kind 237) replaces the plain `SeekBar` fill: bands drawn low→high with the
  played portion at full opacity and the remainder dimmed. 50 Hz, one byte per 20 ms per band.
- **Tempo · key chip** beside the title.

⚠️ **237's `band_low` is 420 bytes longer than `band_mid`/`band_high`** (which are exactly equal). Render
all three against a **shared timebase derived from the shortest band**, or the low band drifts ~8 s by
track end. And band *ordering* is inferred from energy, not named in the proto — if a future capture
contradicts it, only the colour mapping changes.

---

## Code changes — inventory

**Domain (`Wavee.Core/Domain/Models.cs`)**
```csharp
public sealed record Track(…, string? Isrc = null,
    uint? Tint = null,          // kind 179 ColorSet.f4 RGBA — art placeholder
    double? TempoBpm = null,    // kind 222 tempo
    string? MusicalKey = null,  // kind 222 key.name  ("A")
    string? CamelotCode = null, // kind 222 key.camelot.code  ("11B")
    uint? CamelotColor = null); // kind 222 key.camelot.color
```
Merge rule: 0/null is unknown — never let a thin projection clobber a populated tint (the
`StoreEntityMerge` `Has()` discipline that already protects discography counts).

New records: `TrackVersion(string Uri, VersionKind Kind, Image? Art, ...)`,
`AudioFormatOption(int FormatEnum, string Label, int AverageBitrate, bool AvailableOnDevice)`,
`BrowseCategory(string Uri, string Title, uint? Color, BrowseGroup Group, bool IsClientFeature)`,
`BrowseSection(string Uri, string? Title, BrowseSectionKind Kind, int Returned, int Total)`.

**New components** — `Components/PersonPicture.cs`, `Components/TrackVersionsPanel.cs`,
`Components/FormatSplitButton.cs`, `Components/WaveformBar.cs`, `Components/BrowseLinkGroup.cs`.

**New feature folder** — `Features/Browse/{BrowseDirectory,BrowsePage,BrowseTaxonomy,BrowseRoutes}.cs`.

**Changed** — `TrackRow.cs` (ColumnSet + Tempo cell + chevron + tinted art), `MediaCard.cs:191`
(placeholder), `DetailTracks.cs` (`Virtual.Measured` + expansion state), `SearchPage.cs` (facets + browse
empty state), `HomePage.cs` (facet chips), `ArtistPage.Hero.cs` (person picture), `ContentHost.cs`
(routes), `ConcertUi.cs` (`SegmentedDatePill` → `SegmentedPill`), `Rail.cs` (unchanged — reused as-is).

**New services (`SpotifyLive/`)** — `SpotifyBrowseService`, `TrackDescriptorService` (Workstream C),
`TrackAssociationsService` (98/99), `AudioFormatService` (kind 5 + device-capabilities),
`WaveformService` (237, on-demand, **not** cached to disk at 38 KB/track without a budget).

**Extension kinds added to the row bundle:** 179 (tint) and 222 (tempo) join the existing 300-entity
batch. 98/99/237 are on-demand only.

---

## Verification

```powershell
dotnet build src/FluentGpu.slnx                      # clean
dotnet run --project src/FluentGpu.VerticalSlice     # ALL CHECKS PASSED
```

**UI-specific gates beyond the standard ones:**
- **Zero-alloc phases 6–13 must stay green with a row expanded** — the expander is the most likely
  regression source in this plan, since it adds per-frame work to the busiest list.
- **Scroll-anchor test:** expand a row above the viewport, confirm the list does not jump
  (`Virtual.Measured`'s anchor re-pin).
- `--screenshot` diffs for: tinted placeholders (the blank-square fix), the browse directory at 3
  breakpoints, the fused home chip, and the circular person picture's clip.
- `FG_REUSE_GUARD=1` over the new components — chip and expansion state are the exact shape that trips
  the props-freeze rule.
Plus **Wavee.Tests**: chip JSON parse + `tags contains` extraction + unsupported-query rejection; a **real
multi-descriptor fixture** (now available); Ensure delta-only; cache hit skips network; negative TTL;
chip hide-zero; selection sticky; artist-overview mapper covers new fields without clobbering discography;
`ConcertCountHash` asserted.

Then the 400-gate in `wavee.log`, and a feel-check: home greeting/shelves reflect local time-of-day,
Albums + Podcasts + Audiobooks search tabs all return results, Liked chips match Desktop for the same
account, heart add/remove updates chips without a library-wide refetch.

`arena.alloc-zero` is known-flaky in FULL-suite runs — don't trust a single failure.

---

## Risks & standing rules

- **No `FG_*` kill switches** — new behaviour is the unconditional default.
- **Zero managed alloc in frame phases 6-13** — chip selection via signals, match sets precomputed off the
  hot path, no per-frame LINQ in bind thunks.
- **Component props freeze at mount.**
- Large libraries: first Ensure is many 300-batches — apply progressively, never block list paint.
- Craft: `Spacing.*`, `Tok.*`, `MotionTok.*` only.
- Session-local `tmp/saz-analysis/graphql-samples/*.req.txt` may carry **live bearer + client-token**
  headers. Those dumps were **not** promoted into `docs/plans/wavee/spotify-wire-research/` — leave
  them in gitignored `tmp/` (or delete them). Never commit capture request bodies.

---

## Known outstanding

1. **`PlayPlayLicenseTests.SpotifyHeaders_AppVersion_IsNineDigits` fails** — it hardcodes the OLD app
   version `129300667`, which this work intentionally bumped to `129400583`. The file lives at
   `src/apps/Wavee.PlayPlay/Tests/`, which CLAUDE.md fences off from this workspace, so the one-line fix
   belongs in the `wavee-playplay-private` repo. Every other pin-mirror test was updated in place.
2. **The moving watch-feed clip is not wired.** `ArtistWatchFeedPicture` renders the watch feed's own
   still, correctly circular-clipped, with the full hover/focus/keyboard affordances and the route into the
   feed. Playing the clip *inside* the circle needs a SECOND video-surface slot, which is the exact pattern
   that previously produced the grey-video regression — it wants its own change with a DRM smoke test, not
   a rider on this one.
3. **`relatedMusicVideos` mapping is defensive only** — every captured artist returned `totalCount: 0`, so
   the item schema is still unverified. The mapper tolerates it; no UI asserts its shape.
4. **`popcount` is still not shipped as a follower badge** — field 7 reads 128 M for an editorial playlist.
   Needs one UI-readable number to calibrate against.
