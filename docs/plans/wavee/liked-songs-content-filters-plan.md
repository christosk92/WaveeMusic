# Liked Songs content-filter chips — final technical plan (FluentGpu Wavee)

> **SUPERSEDED** by [`spotify-wire-parity-plan.md`](spotify-wire-parity-plan.md) (Workstream C) and the
> durable research corpus at [`spotify-wire-research/`](spotify-wire-research/README.md).
> Kept for history — do not extend this file; put new work in the parity plan / research tree.

**Status (historical):** evidence-locked; **proto landed** (`src/apps/Wavee/SpotifyLive/Protos/extension_descriptor.proto`, builds clean). Phases 1-5 were awaiting approval when this was folded into the master plan.
**Product choice:** **A** — Desktop / WaveeMusic parity: exclusive chips (`All` + one tag); chip listed only if ≥1 liked track carries that tag; search / funnel filters AND on top.
**Primary evidence:** `content-filters.saz` (646 sessions), re-analysed with a **protobuf-aware** scanner rather than text grep — this is what changed the conclusions below. Secondary: WaveeMusic `TrackDescriptorFetcher` / `LikedSongsFilterDto` / `SpClient.GetLikedSongsContentFiltersAsync`.

---

## 0. What the re-analysis changed vs. the draft plan

The draft was directionally right. Five things are now **measured** rather than assumed, and two were **wrong**:

| # | Draft said | Wire says | Impact |
|---|---|---|---|
| 1 | tags live at `descriptors[].text` | ✅ correct, **plus two fields the known proto lacks**: `concept_uri` (f4) and `display_name` (f5) | Ported-verbatim proto would be **lossy**; see §2.2 |
| 2 | "batch ≤300 (house cap); WaveeMusic uses 500" | **300 is a real server-side ceiling** — 5 distinct kinds hit exactly 300, none ever exceeded it | Use 300. WaveeMusic's 500 is wrong/untested |
| 3 | negative-cache TTL "~24h like WaveeMusic" | Server itself returns `cache_ttl=86400` (24h) + `offline_ttl=2592000` (30d) | TTL is the wire's, not our invention — use the response header values |
| 4 | kind 172 / 19 "absent from SAZ (0 sessions)" | ✅ **confirmed by proto decode**, not grep: 0 of 4,995 extension queries | Verdict now trustworthy — see §2 |
| 5 | Pathfinder tags "out of scope" | ✅ 11 Pathfinder ops decoded; **none** tag/filter-related | Genuinely uninvolved |

Also corrected: kind-6 responses are **`content-encoding: zstd`**, not gzip. Any XM client that only handles gzip will silently fail to read the tag plane. (Our existing XM path must be checked for this — see §5, Phase 2.)

---

## 1. Wire contracts (locked, with evidence)

### 1.1 Chip list — `content-filter/v1/liked-songs`

```
GET https://spclient.wg.spotify.com/content-filter/v1/liked-songs?subjective=true&market=from_token
Accept: application/json
Authorization: Bearer …
client-token: …
If-None-Match: <etag>            → 304 Not Modified (empty body)
```

- SAZ sessions 606 (OPTIONS, 200) + 612 (GET, **304**). Forced-200 replay gave the body.
- ETag shape: `2026-07-28T12:49:02.178266481Z#-1547928215#1137299681#3241` — opaque, store verbatim.
- Body: `{ "contentFilters": [ { "title": "Mellow", "query": "tags contains mellow" }, … ] }` — 15 chips this account.
- **Only supported query form is `tags contains <token>`.** Anything else → treat chip as unsupported and drop it (WaveeMusic `IsSupported`).
- This is the **only** `content-filter/*` endpoint in the entire capture.

### 1.2 Per-track tags — XM kind 6 `TRACK_DESCRIPTOR`

Request (`BatchedEntityRequest`, `POST /extended-metadata/v0/extended-metadata`, `Content-Encoding: gzip`):

```
header(f1) { country, catalogue, task_id }
entity_request(f2)* { entity_uri(f1), query(f2)* { extension_kind(f1)=6, etag(f2) } }
```

Response (`Content-Encoding: zstd`, HTTP 200), session 417 verbatim:

```
extended_metadata(f2) {
  header(f1) { status_code=200, cache_ttl=86400, offline_ttl=2592000 }
  extension_kind(f2) = 6
  extension_data(f3) {
    header(f1) { status_code=200, etag="3138d9e5d36be0a739307a115c9acdc5", cache_ttl=86400, offline_ttl=2592000 }
    entity_uri(f2) = "spotify:track:4mlQPWeSMkwJQcYTr9razZ"
    extension_data(f3) = Any {
      type_url = "type.googleapis.com/spotify.descriptorextension.ExtensionDescriptorData"
      value    = { descriptors: [ { text:"k-pop", weight:0.9749, types:[1,7,9,10,11],
                                    concept_uri:"spotify:concept:0JOcC1ypWJCg2qQqoEpYL9",
                                    display_name:"K-Pop" } ] }
```

**The join key is `text`, not `display_name`.** Chip query `tags contains k-pop` matches `text="k-pop"`; `display_name="K-Pop"` is the presentation form and equals the chip's `title`. Match case-insensitively on `text`.

### 1.3 Batch ceiling — measured

Max `entity_request` per POST across all 140 XM sessions:

| kind | max batch | batches |
|---|---|---|
| 149, 178, 179, 182, 212, 225, 249 | **300** | 22 |
| 10 (`TRACK_V4`) | 153 | 113 |
| 99 / 85 | 82 | 3 |
| **6 (`TRACK_DESCRIPTOR`)** | **1** | **1** |

300 recurs across independent kinds and is never exceeded → server-side cap. **Use 300.**

> Caveat, stated plainly: kind 6 appears **once, for one track**, in this capture. Every structural claim about the *payload* is from that single sample. `descriptors` being repeated is inherited from the authoritative definition, not proven here; a multi-tag track was never observed. Treat multi-descriptor parsing as the expected-but-unverified path and cover it with a synthetic test (§5, Phase 0).

---

## 2. Out-of-scope items — adjudicated

The ask was "figure out / fix the out-of-scope parts too — maybe it's similar?". Answer: **structurally yes, actionably no.** All four ride the same XM pipe, so the *plumbing* generalises — but there is no evidence to implement any of them, and inventing their payload protos would be fabrication.

| Item | Evidence | Verdict |
|---|---|---|
| **kind 172 `TRACK_CONTENT_FILTER`** | 0 of 4,995 decoded extension queries. In WaveeMusic it exists **only** as a generated enum entry — zero consuming code | **Not implementable.** No payload shape, no reference impl, no wire sample. Do not speculate a proto |
| **kind 19 `TRACK_DESCRIPTOR_SIGNATURES`** | 0 queries; enum-only in WaveeMusic | Same. Name suggests a signing/provenance sidecar for kind 6, but that is a guess |
| **kind 94 `TRACK_EXTRA_DESCRIPTORS`** | 0 queries; enum-only | Same — and the likeliest future sibling of kind 6 |
| **Pathfinder tags** | 11 ops decoded (`home`, `lookupChildEntities`, `feedBaselineLookup`, `fetchExtractedColors`). **None** tag- or filter-related | **Uninvolved.** Chips are pure spclient + XM |
| **Playback mute / `IPlaybackContentFilter`** | No such endpoint anywhere in 646 sessions | **Different system.** A local UI-preference store, unrelated to this endpoint. Do not conflate |
| **Multi-select chips** | Wire carries only `title` + `query` per chip; no grouping, exclusivity, or selection-state field | **Client-side product decision, not a wire capability.** Exclusive (choice A) is a free choice; multi-select would just be ANDing/ORing tag sets locally |

**How this shapes the design (the "similar" part that *is* actionable):** build the tag plane as a **generic descriptor-extension seam**, not a kind-6 special case — `(ExtensionKind, payload parser) → tags`. Adding kind 94/19/172 later then costs one payload message + one registration, with the batching, etag, zstd, negative-cache and store layers already in place. That is the whole "maybe it's similar" dividend; it costs nothing now and requires no speculative protos.

---

## 3. Proto — authored and landed

`src/apps/Wavee/SpotifyLive/Protos/extension_descriptor.proto` (new). Auto-compiles via the existing `SpotifyLive\Protos\**\*.proto` glob in `Wavee.csproj`. **`dotnet build src/apps/Wavee/Wavee.csproj` → 0 errors** (48 warnings, all pre-existing).

Decisions worth knowing:

- **Package `spotify.descriptorextension`** — must match the `Any.type_url` exactly, else dispatch/validation breaks.
- **proto3, while sibling XM protos are proto2** — deliberate. `types` is *packed* on the wire and carries values **9, 10, 11** outside the known enum. proto3 enums are **open** (unknown values round-trip); a proto2 **closed** enum would silently drop them into unknown fields. The file imports nothing, so the syntax difference is local.
- **Fields 4/5 added** beyond the 1.1.73.517-era definition. Field numbers and wire types are verified; the *names* `concept_uri` / `display_name` are best-effort, and the file says so — matching the `video_associations.proto` documentation precedent.
- Enum values 8+ are left **unnamed on purpose**. 9/10/11 were observed; their semantics are not established, so no guessed names.
- **C# API gotcha:** `repeated types` generates as **`Types_`** (trailing underscore — collides with a reserved member). Verified in generated output.

---

## 4. Architecture — Approach R (side-table), confirmed

Unchanged from the draft and still right: a `TrackTags` side-table mirroring the `VideoAssociation` pattern (hot dict + cold table + `ExtensionEtagCache`), with the Liked join stamping `Tags` onto the read model the way `AddedAt` already is.

Rejected: denormalising onto `Track` (entity-JSON churn on every XM land, pollutes non-Liked surfaces) and parse-at-filter-time from raw blobs (re-parses thousands of protos per chip rebuild).

Data flow, cache policy, and the liked-set change matrix (heart / dealer / remove / offline) stand as drafted — including: **Ensure only delta URIs**, never a full re-fetch on a heart; selection sticky **by tag token**, not index; snap to `All` when the selected chip's match set empties.

One addition from the wire: persist the server's `cache_ttl` / `offline_ttl` per entity rather than hardcoding 24h, since the response supplies both.

---

## 5. Phases

| Phase | Content | Gate |
|---|---|---|
| **0 ✅** | `extension_descriptor.proto` authored + compiling | Build clean — **done** |
| **0b** | Parse tests: session-417 bytes → `("k-pop", 0.9749, [1,7,9,10,11], concept, "K-Pop")`; **synthetic multi-descriptor** track (the unproven path); chip-JSON parse; `tags contains` token extraction; unsupported-query rejection | `Wavee.Tests` green |
| **1** | `ContentFilterClient.GetAsync(ifNoneMatch)` → 200/304; persist etag + payload across restart | 304 on second open |
| **2** | Generic descriptor seam + `TrackDescriptorService.EnsureAsync(uris)`, batch **300**, `ExtensionEtagCache`, negative cache, side-table upsert. **Verify our XM client decodes `zstd`** — kind 6 responses are zstd, and a gzip-only path fails silently here | Fake-XM tests: cache hit skips network; negative TTL honoured |
| **3** | Liked-only chip row; exclusive selection; hide zero-match; fold into `DetailTracks` filter key; shimmer while first Ensure in flight; sticky selection | Slice + tests green |
| **4** | Liked-set delta polish (added-URI Ensure, selected-chip death → `All`) | Tests |
| **5** | Feel-check (user-run): chips match Desktop for the same account; add/remove liked updates facets without a library-wide refetch | User verdict |

Ship slice: **0-3**, with 4 folded in if delta math stays "Ensure missing only".

---

## 6. Risks & standing rules

- **No `FG_*` kill switches** for this feature (user ruling) — the new path is the unconditional default.
- **Zero alloc in frame phases 6-13**: chip selection via signals; precompute match sets off the hot path; no per-frame LINQ in bind thunks.
- **Component props freeze at mount** — chip selection must reach children via `Signal`/`Func`/context or a changed `Key`, never a frozen field on an `Embed.Comp` factory.
- Large libraries: first Ensure is many 300-batches — apply progressively and rebuild chips incrementally; never block list paint.
- Craft: `Spacing.*`, `Tok.*`, `MotionTok.*` only; reuse `RowChip` / `TypePill` language, not `Segmented`.
- Don't conflate with funnel filters or playback content-hide (§2).

---

## 7. Verification

```powershell
dotnet build src/FluentGpu.slnx                      # clean
dotnet run --project src/FluentGpu.VerticalSlice     # ALL CHECKS PASSED
```
plus the new `Wavee.Tests` content-filter + descriptor suites, then the Phase-5 feel-check.
