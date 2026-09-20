# Wavee 0.3 — entity identity memory: the measured options and a recommendation

2026-09-12 · worktree `feat/0.3-structure` · bench `.tmp-id-bench/` (mirrors `..\fluent-gpu\.tmp-simd-bench`)

**The decision in one sentence:** replace the `StringId` uri identity with a 24-byte packed `EntityId` (128-bit
payload + kind + provider + form) and an open-addressed `int[]` index *now*, while `Entities.cs` is the only file
that spells identity — it cuts a track row from **282 B to ~120 B** (measured), makes the wire→slot path 2–2.5×
faster with zero allocation, and after Waves 2–5 the same change touches every file in the tree.

Everything below is measured on this machine unless marked **UNVERIFIED**. Every claim about existing code cites
`file:line` in this worktree.

---

## 1. Baseline — what a track row costs today, and where it goes

### 1.1 Method (re-runnable)

```
dotnet publish .tmp-id-bench/IdBench.csproj -c Release -r win-arm64 -o .tmp-id-bench/publish   # needs vswhere on PATH
.tmp-id-bench/publish/IdBench.exe 10000 all      # footprint, lookup, equality, text, growth, LOH crossing, chunked reads
.tmp-id-bench/publish/IdBench.exe 50000 loh      # the LOH fragmentation probe, in its own process
```

The bench references `Wavee.V3.csproj`, so "today" is the real `TrackTable`, `Table.Slot`, `UriKeys` and the
engine `StringTable` — not a re-statement. Footprint is `GC.GetTotalMemory(true)` after two forced compacting
collections, exactly `FootprintGateTests.Settle()` (`src/apps/Wavee.V3.Tests/FootprintGateTests.cs:103-110`).
Timing is six A/B-alternating rounds, median of the middle two, with `GC.GetAllocatedBytesForCurrentThread` per
round. Three full runs were taken (JIT Release, NativeAOT run 1, NativeAOT run 2); AOT run 1 drifted (the same
`int ==` loop measured 2.3 ns then 5.1 ns a few lines apart — another process was building), so where the runs
disagree a range is given and AOT run 2 is the primary. Raw output: `.tmp-id-bench/results-*.txt`.

Machine: `arch=Arm64`, `.NET 10.0.5` (AOT) / `10.0.8` (JIT), `Vector128.IsHardwareAccelerated=True`. Same
architecture as the gate's numbers. The corpus is 10,000 (and 50,000) distinct `spotify:track:<22 base62>` uris
whose ids are real-shaped — random 16-byte gids encoded — not the gate's `{i:D22}` digit strings
(`FootprintGateTests.cs:41`).

### 1.2 The numbers (10,000 rows; 50,000 in parentheses)

| what | bytes | B/row | source |
|---|--:|--:|---|
| **columns, exact** (`EnsureCapacity(rows+1)` first, as the gate does at `:50`) | 888,792 | **88.9** (89.0) | 7 bookkeeping columns = 25 B (`Entities.cs:423-438`: `Version` 4, `Known` 4, `Authority` 1, `FetchedAt` 4, `Touched` 4, `Inflight` 4, `Uri` 4) + 20 `TrackTable` columns = 64 B (`Track.cs:120-161`: 8×`StringId`/`int`/`uint` hot+cold ints = 4 B each ×15 = 60, `Year`+`Tempo` 2 B ×2, `Key`/`Camelot`/`IdentityAuthority`/`ExtrasAuthority` 1 B ×4). Theory 89.0; measured 88.9. **No slack in the gate's number.** |
| **`ByUri` map** — `Dictionary<StringId,int>` (`Entities.cs:444`) | 350,432 | **35.0** (30.2) | grown from empty: the prime ladder lands on 17,519 entries × (16 B entry + 4 B bucket). Presized: 202,192 B = 20.2 B/row (prime 10,007). |
| layout total (the gate's line) | 1,239,224 | 123.9 (119.1) | reproduces the gate's `1,239,224` exactly |
| **uri text in the engine `StringTable`** | 1,581,800 | **158.2** (154.0) | per entry: the 36-char `string` object 96 B + `Dictionary<string,int>` entry 24 B + bucket 4 B (× the same 1.75 prime slack ≈ 49 B) + chunk slot 8 B + refcount 4 B + pin 4 B (`..\fluent-gpu\src\FluentGpu.Engine\Foundation\StringTable.cs:36-43`, chunks of 4096 at `:30`). Composition derived from the code; the total is measured. |
| **total per track row today** | 2,821,024 | **282.1** (273.1) | identity (map + text) = **193 B = 68 %** of the row |

The gate reported 148.8 B/row for the text; this bench says 158.2. Four of those bytes are a gate bug:
`FootprintGateTests.cs:45` subtracts `Rows * sizeof(int)` for the `ids` array, but that array is allocated at
`:35`, *before* `textBefore` at `:37`, so it is never inside the delta — the subtraction under-reports by 4 B/row.
The remaining ~5 B/row is **UNVERIFIED** (xunit host allocations in the same process are the likely explanation).

### 1.3 What the uri text is actually for

Nothing on the paint path reads it. A row's identity on every hot path is its **slot** (`Track` is one `int`,
`Track.cs:199-201`; `Equals`/`GetHashCode` are the slot, `:264-268`). The uri text is read in exactly four places:

1. wire → slot (`Table.Slot(ReadOnlySpan<byte>)` / `TryGetSlot`, `Entities.cs:532-561`): transcode UTF-8 → UTF-16
   on the stack, hash 36 chars, `SequenceEqual` against the resolved string — **86–122 ns per uri, 0 B**;
2. `ByUri[StringId]` (every `Alloc`, `FreeSlot`, `Slot(StringId)`): `UriKeys.GetHashCode` must `Resolve` and hash
   the text (`Entities.cs:638`) because the span alternate-lookup contract requires content hashes to agree —
   **52 ns per key hit**; the "compact 4-byte key" (`:441-443`) buys bytes, not time;
3. `EntityUri.Of(kind, StringId)` re-parses the provider from the text on **every** `.Uri` read
   (`Entities.cs:151-152`, `Track.cs:215`): **45–100 ns**;
4. the store and the planner resolve the string per row to hand to sqlite / the request bucket
   (`Store.cs:833`, `:1058`, `Fetch.cs:348`, `Fetch.cs:159-174` keeps a `string[]` per bucket).

---

## 2. The base62 claim, verified against this repo's code

`src/apps/Wavee/Backend/Spotify/Base62.cs:11-24` (`Encode`): 16 big-endian bytes → `UInt128` → 22 chars over
`0-9a-zA-Z`, zero-padded to width 22. `:26-36` (`Decode`): `BigInteger` accumulate, then copy the **low** 16 bytes —
a 22-char string whose value is ≥ 2^128 is **silently truncated**, not rejected (by inspection). The arithmetic:
62^21 ≈ 4.4·10^37 < 2^128 ≈ 3.4·10^38 < 62^22 ≈ 2.7·10^39, so 22 chars is the minimum width that covers every
128-bit gid and ~87 % of 22-char strings are *not* the encoding of any gid. The bench's decoder carries an overflow
guard; it verifies round-trip on all 10,000/50,000 random gids, that `2^128−1` ↔ `7N42dgm5tFLK9N8MT7fHC7`, that
`zzzzzzzzzzzzzzzzzzzzzz` is refused, and that the fast encoder agrees with a re-statement of the 0.2.9 loop on
1,000 gids (`.tmp-id-bench/Types.cs`, `Base62Fast`).

The wire's native identity for the six catalog kinds **is the 16-byte gid, not the uri**: every 0.2.9 metadata
decode manufactures the uri string from it — `Backend/Metadata/ExtendedMetadataSource.cs:249, 328, 342, 368, 407,
416, 429, 437, 445, 604, 613` (track, album, album-tracks, artist, artist-albums, show, show-episodes, episode,
episode-show, artist memo ×2) and `Backend/PlaybackController.cs:1706, 1756`. So Wave 2's decoders will, under
today's shape, base62-**encode** every gid into staging text (0.2.9's loop: **569 ns** per gid, measured; the fast
pair: 81 ns) and then pay the 86–122 ns text lookup — for an identity that was already 16 bytes on the wire.

---

## 3. The options, measured

All at 10,000 rows (50,000 where it differs). "B/row" is the identity's cost only; add the 89 B of value columns
for the row total. Lookup is one uri of a 300-uri UTF-8 batch resolved to a slot.

| # | representation | B/row presized | B/row grown ×2 | wire hit | wire miss | key hit | text out | equality | alloc |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|
| **0** | **do nothing**: `Column<StringId>` + `Dictionary<StringId,int>` + engine `StringTable` | **193.2** (184.2) | — | **86–122 ns** (186–276 ns @50k) | 70 ns (91) | 52 ns | 4.9 ns `Resolve`; 45–100 ns `EntityUri.Of` | 1.85 ns (slot) | 0 |
| **1** | `Column<Gid128>` (16 B) + open-addressed `int[]` index (load 0.61) | **22.6** (26.5) | 32.8 (31.5) | **37–57 ns** (109 @50k) | 29–32 ns | **3.0 ns** | 81 ns encode into `Span<char>`, 0 B | 1.85 ns (2×`ulong`) | 0 |
| **2** | **hybrid `EntityId` 24 B** (gid *or* `StringId` in `Lo`, + kind + provider + form) + the same `int[]` index; text-form rows keep a small `Dictionary<StringId,int>` | **30.6** = 24.0 column (measured) + 6.6 index (measured as E − 16 B column) | ~40 (derived: F + E2's index share) | as 1 for gid rows; as 0/3 for text rows | as 1 | 3.0 ns | 81 ns gid form / 4.9 ns text form | 1.88 ns (2×`ulong` + `uint`) | 0 |
| **3** | UTF-8 **arena** + `ArenaRef` (offset,len 8 B) + byte-keyed `int[]` index — no base62 at all | 50.6 (54.5) | not measured | **20–28 ns** (44 @50k) | not measured | — | a slice, 0 B (UTF-16 needs a transcode) | 1.85 ns (slot) | 0 |

Row totals: **0 → 282 B · 1 → 112 B (−60 %) · 2 → 120 B (−57 %) · 3 → 140 B (−50 %)**.

Components that explain the table (AOT run 2): `string.GetHashCode` over 36 chars 46.6 ns; base62 decode of 22
chars 15.5 ns; prefix check (`Length == 36 && StartsWith("spotify:track:")`) 2.2 ns; decode + probe without the
prefix check 29.5 ns. The 0.2.9-style encode is 569 ns; the two-`UInt128`-division encoder is 81 ns.

Two honest notes on the table. Option 3's wire path is the fastest because a 36-byte hash + SIMD `SequenceEqual`
beats a 22-step base62 decode; it loses on memory (2.2× option 1) and its arena needs a compaction pass on trim.
Option 2's per-row number is arithmetic on two measured parts (the 24-byte column alone, and the index measured
inside option 1), not a single measurement of the composite — the composite prototype was not built.

### 3.1 The five requirements, against option 2

1. **Wire → slot with no allocation (P14).** A `spotify:<kind>:<22>` uri: prefix walk (the existing
   `ProviderOf`, `Entities.cs:230`) → `TryDecode` → probe the gid index against the column: 37–57 ns, 0 B, measured.
   A protobuf gid (16 raw bytes, `ExtendedMetadataSource.cs:249` et al.): copy + probe, **3 ns** ("key hit").
   A non-Spotify uri: today's span alternate-lookup on the (now small) text dictionary — unchanged code path.
2. **Round-trip to text.** `EntityId.Format(Span<char>)`: `"spotify:" + kind name + ":" + base62` — 81 ns into a
   stack span, 0 B; a `string` when the call site needs one (deep link, `PutState`, sqlite key, log, copy link) is one
   allocation at that cold call site. Text-form rows `Resolve` as today (4.9 ns).
3. **Non-Spotify providers, no branch per read.** `wavee:local:file:<b64>`, `wavee:module:<id>:<b64>`, `fake:*`,
   `tr7`, `spotify:user:<name>`, `spotify:concert:<hex>`, the synthetic `wavee:search:*` / home subjects all take the
   text form: `Lo = StringId`, `Hi = 0`, `Form = Text`. Equality and hashing are word compares with no form test
   (measured 1.88 ns vs 1.85). Only `Format`/`Text` branch on the form, and only the wire lookup branches on the
   prefix — which it already does today (`ProviderOf`).
4. **Cross-kind identity.** The key carries `Kind` and `Provider`, so a queue row, a search "All" hit, a route
   subject, a history entry or a pin can hold one `EntityId` with no table pointer. Today this is unresolved in the
   foundation: `Edges.SearchResult` is `EdgeTable<NoEdge>` (`Edges.cs:593`) and the "All" facet is documented as
   "entity slots" (`Search.cs:7`) with nothing saying which table each slot indexes; the queue holds `Track` slots
   only (`Queue.cs:223-251`) and an episode rides as a `Podcast`-flagged track (`Track.cs:89-93`).
5. **Equality and hashing as cheap as an int compare.** The hot-path identity stays the **slot** (unchanged).
   Where the key itself is compared, 24 bytes cost 1.88 ns vs 1.85 ns for an `int` — measured, not a rounding.

---

## 4. What else the measurement exposed

### 4.1 The uri map

`Dictionary<StringId,int>` costs **20–35 B/row** depending on where the prime ladder lands (35.0 grown at 10k, 30.2
at 50k, 20.2 presized) and **52 ns per key hit** because the comparer hashes the resolved text (`Entities.cs:638`).
An open-addressed `int[]` of slots (power-of-two capacity, keys compared against the column, no key copy) costs
**6.6 B/row** at load 0.61 and **3.0 ns per key hit**; over the 300-uri wire batch the whole path is 2.1–2.5× faster
at 10k and 2.5× at 50k. A SIMD group probe (Swiss-table style) was **not built** — UNVERIFIED whether it adds
anything at load ≤ 0.75 where a linear probe already hits in one or two slots; it would matter for the miss path at
high load, which the planner does not exercise (it asks for rows it already allocated).

### 4.2 Column growth slack

The gate's 123.9 B/row has **no slack in it** — it presizes (`FootprintGateTests.cs:50`) and the columns measure
88.9 vs 89.0 theory; the whole difference to the gate is the map (§1.2). Real growth through `Alloc` (`Column.
EnsureCapacity`, `Entities.cs:358-363`: ×2 from 16, `Array.Resize`):

| rows | capacity | slack | resident | B/row (exact 89.0) | gross allocated | time |
|--:|--:|--:|--:|--:|--:|--:|
| 10,000 | 16,384 | ×1.64 | 1,459,328 | 145.9 | 2.00× resident | 0.19 ms |
| 50,000 | 65,536 | ×1.31 | 5,833,856 | 116.7 | 2.00× resident | 1.5 ms |
| 200,000 | 262,144 | ×1.31 | 23,331,968 | 116.7 | 2.00× resident | 21 ms |

×2-never-shrink is the right policy for the hot path (P5) and its worst case is bounded (≤ 2×, and the doubling
chain's garbage equals the final size). At library scale the cheap fix is not a policy change but a **presize from
the store's row count at warm** (`Store.cs` warm already runs a `SELECT` per kind; one `EnsureCapacity` before the
warm read removes the ×1.31–1.64 entirely). Every commit already presizes its batch (`Track.cs:351`).

`GC.AllocateUninitializedArray` on the growth chain: 27 columns × (16 → 262,144) measured **13.67 → 12.79 ms**
(JIT 14.2 → 10.4; AOT run 1 30.3 → 22.9): 7–27 % of a cost that totals ~20 ms per 200k rows per session. It saves
**zero bytes**, and it would break `Alloc`'s `Version[slot]++` and `Free`-less slot reuse, which rely on a zeroed
tail (`Entities.cs:466-476`, "++ not = 1"). Not worth it.

### 4.3 LOH

Under ×2 growth from 16 the first LOH array per column width (array header 24 B counted):

| width | first LOH capacity | allocated when rows exceed | exact-fit column crosses at |
|--:|--:|--:|--:|
| 1 B | 131,072 | 65,536 | 85,000 |
| 2 B | 65,536 | 32,768 | 42,500 |
| 4 B | 32,768 | 16,384 | 21,250 |
| 8 B | 16,384 | 8,192 | 10,625 |
| 16 B | 8,192 | 4,096 | 5,312 |
| 24 B | 4,096 | 2,048 | 3,541 |

So a 4-byte column is an LOH object from ~16k rows; the 24-byte `EntityId` column from ~2k rows; a CSR arena
(`Edges.cs:89-91`, `int` targets) from ~16k edges. Fragmentation, measured in an isolated process after a
**non-compacting** gen2 (what the runtime's own gen2 does to the LOH):

| grown to | LOH size | free inside the LOH | live column bytes | committed |
|--:|--:|--:|--:|--:|
| 50,000 rows | 7,866,616 | 2,361,064 (30 %) | 5,832,704 | 11.8 MB |
| 200,000 rows | 29,757,376 | 6,425,912 (22 %) | 23,330,816 | 33.6 MB |

The free space is the previous doublings; it is bounded by the last doubling, is reused by the next LOH allocation,
and is never returned. `GC.Collect(…, compacting: true)` left both numbers identical — the LOH is compacted only
under `GCSettings.LargeObjectHeapCompactionMode`, which nothing sets. The "after the table is dropped" reading is
**UNVERIFIED** (`GetGCMemoryInfo` returned the same record; the probe needs a different read).

A **chunked column** (`T[][]` of 4096 rows, index = `slot >> 12`, `slot & 4095`) reads at the same speed as a flat
one — **2.41 vs 2.41 ns** random, 2.39 vs 2.38 sequential (AOT run 2; JIT 2.40/2.39) — so the read cost is zero. It
is still not worth doing now: it breaks `Column.Span` and with it the contiguous `Vector128` loads of `ScanMissing`
(`Entities.cs:934-965`) and every CSR `Targets`/`Payload` slice (`Edges.cs:116-123`), and the fragmentation it would
prevent is a bounded 22–30 % of a heap that the memory governor can already shed whole on a scope switch
(`Entities.Switch`, `:856-863`). Revisit when a shed-able column arena is wanted.

### 4.4 Other things the numbers say

- **Trim can never reclaim text.** `FreeSlot` removes the map entry (`Entities.cs:509-521`) but nothing in
  `Entities/` calls `StringTable.AddRef`/`Release` (grep: zero sites), and a never-AddRef'd string is permanent
  (`StringTable.cs:26`). The store's trim (R2) therefore frees the 89 B of columns and leaks the 158 B of uri text —
  and the title/image/artist-line text with it. A scope's memory floor only ever rises. Option 2 removes the uri
  half of this outright (a gid row has no text); the title half is a separate fix (AddRef at commit, Release at
  FreeSlot, or an app-owned arena for entity text).
- **`EntityUri.Of` re-parses on every read** (§1.3 item 3). The provider byte in the key ends that.
- **Two spellings, two rows.** `spotify:user:<u>:playlist:<id>` and `spotify:playlist:<id>` intern to different
  `StringId`s and therefore allocate two `PlaylistTable` rows for one playlist (`Slot(bytes)` → `Intern` → `ByUri`,
  `Entities.cs:532-540, 466-480`); only the Liked collection's spellings are folded (`:181-197`). UNVERIFIED whether
  the 0.3 wire path still carries the user-namespaced form for playlists (0.2.9's comment at `:267-269` says Home
  and recents do). A gid-keyed identity folds them by construction if the parser canonicalises.
- **The planner holds a `string` per slot per bucket** (`Fetch.cs:159, 165-174, 348`) — free today because the
  string is interned, but a per-row reference and a `string[]` per bucket that a packed key formats straight into
  the request buffer instead.

---

## 5. Recommendation

**Do option 2 now, in Wave 1's file.** A `readonly struct EntityId { ulong Lo, Hi; uint Meta; }` (24 B) replaces
`Column<StringId> Uri`; `EntityUri` becomes a view over it; `Table.ByUri` becomes an `int[]` index over the column
plus a `Dictionary<StringId,int>` that only text-form rows enter; `Table.Slot(ReadOnlySpan<byte>)` keeps its
signature and gains a `Slot(EntityKind, ReadOnlySpan<byte> gid16)` twin for protobuf. The base62 pair moves into
`Entities.cs` with the overflow guard. Measured effect on a Spotify track row: **282 → 120 B**; wire→slot 86–122 →
37–57 ns; `ByUri` key hit 52 → 3 ns; `.Uri` read 45–100 ns → a field load; and Wave 2's decoders write 16 bytes
where 0.2.9 wrote a 569 ns encode plus a 100 ns lookup.

Why 24 and not 16: the 8 extra bytes per row (80 KB per 10k rows, 3 % of the row) buy one identity type that carries
its own kind and provider — which requirement 4 needs somewhere anyway (a mixed list would otherwise carry
(kind, gid) = the same 24 bytes beside a 16-byte column), and which kills the `EntityUri.Of` re-parse. If 8 B/row
ever matters, `Column<Gid128>` + kind-from-table is the squeeze, and `EntityId` stays the carrier type.

Why not option 3 (the arena): it is the fastest wire path measured and has no base62 edge cases, but it is 2.2× the
memory of option 1, it needs an arena compaction on trim that today's `StringTable` also lacks, and it keeps the
identity as *text* — so every gid the wire hands us is still encoded to text before it can be looked up.

### 5.1 What it costs now versus after Waves 2–5

**Now** (one agent, disjoint from nothing — Wave 1 is landed and nobody else is in `Entities/`):

- `Entities.cs` §2 (`EntityUri`, `:110-333`) and §5 (`Table.Uri`/`ByUri`/`Alloc`/`FreeSlot`/`Slot`/`TryGetSlot`,
  `:438-561`; `UriKeys`, `:632-641`). This is the whole design change.
- The twelve `Table` subclasses' `Uri`/`UriId` properties, two lines each (`Track.cs:215-216`, `Album.cs:168-169`,
  `Artist.cs:225-226`, `Playlist.cs:320-321`, `Show.cs:77-78`, `Episode.cs:94-95`, `User.cs:135-136`,
  `Concert.cs:239-240`, `Home.cs:282, 337`, `Browse.cs:128`, `Search.cs:141`). Commits are untouched:
  `t.Slot(s.Utf8(row.Uri))` keeps its signature (`Track.cs:358`, `Album.cs`, `Playlist.cs:580`, `User.cs:324`,
  `Home.cs:481`, `Browse.cs:225`).
- `User.cs:230, 240, 270-277` (`Add`/`Remove` take an `EntityId` instead of `StringId targetUri`), `Store.cs:770,
  833, 1058` (format instead of `Resolve` — on the UI thread into a pooled buffer, or carry the `EntityId[]` and
  format on the store thread), `Fetch.cs:159-174, 348` (provider from the key; `Demand` loses its `string[]`).
  `Palette.cs` is untouched (`spotify:image:<hex>` keys stay `StringId`; `Palette.cs:133-140`).
- Tests: 18 of the 21 files in `Wavee.V3.Tests` name uri identity (`UriId`, `ByUri`, `.Full`, `Intern`) at ~100
  sites, mechanical; new facts listed in §7.
- The grep that bounds it: 166 lines in `Entities/*.cs` mention `Uri`/`UriId`/`ByUri`/`.Full`/`Intern`; ~40 of them
  change, the rest are `row.Uri.IsEmpty` on staged `TextRef`s that stay as they are.

**After Waves 2–5** the type of `EntityUri.Full` / `UriId` is bound by everything that names an entity: the plan's
route table (`Route(RouteKind, EntityUri Subject, …)`, `wavee-0.3-implementation.md:829`), `Playback.State`, the
`PutState` builder, pins, the history log, the jump list, sqlite intent payloads, and every page's now-playing
match — the 57 `Entities/`, 8 `Spotify/`, 5 `Playback/`, 27 `Shell/` and 22 `Screens/` files of §2 of the plan.
That is the same change with a 6–10× larger diff and a migration of every persisted uri-keyed row that anybody
wrote in the meantime.

---

## 6. What it does NOT solve, and the risks

**Not solved**

- The other three `StringId`s per row (`Title`, `Image`, `ArtistLine`) and their permanence in the `StringTable`
  (§4.4). Shared across rows by design (P6), but never reclaimed. Separate fix.
- The 89 B of value columns. A row is still 89 B of columns + 31 B of identity ≈ 120 B; the next lever is the
  column set itself, not identity.
- Growth slack and LOH residency (§4.2–4.3): unchanged by the key type; the 24-byte column crosses into the LOH
  earlier (2k rows) than the 4-byte one it replaces (16k).
- Non-Spotify rows get **no** memory win: they keep a `StringId` + text; they just stop paying the text hash on
  every map probe (the text dictionary shrinks to their population).

**Risks**

- **Base62 edge cases.** A 22-char id ≥ 2^128 is not a gid: the decoder must refuse it (the bench's does; 0.2.9's
  `Decode` truncates silently, `Base62.cs:26-36`) and the row must fall to the text form rather than alias another
  row. Alphabet is case-sensitive (`Base62.cs:9`), as today's ordinal compare is. Distribution of real gids across
  `Lo` is **UNVERIFIED** (the index mixes with a multiply, so structure would cost probes, not correctness).
- **Ids that are not 22 chars.** `spotify:user:<name>`, `spotify:concert:<hex>` (`EntitiesTests` corpus:
  `spotify:concert:3ab7ff`), test fixtures (`spotify:track:abc`, `spotify:album:4Xy2`, `spotify:playlist:1a2b`),
  the demo catalog's `tr7`/`fake:*` (`Entities.cs:314-333`), Spotify's own `spotify:local:…` local-file namespace
  (`Backend/ContextResolver.cs:147`), `spotify:folder:<hex>` (`Entities.cs:118, 203`) and `spotify:collection:
  tracks` (`:114`) all take the text form. The form is decided once at parse; nothing downstream sees it.
- **`spotify:prerelease:<22>`** resolves to an *album* row (`Entities.cs:307`) with a **different** gid from the
  album it becomes, and `Format` must reproduce `prerelease:`, not `album:` — one flag bit in `Meta` (there are 8
  spare), and `Album.PreReleaseUri` (`Album.cs:196`) stays a `StringId` column.
- **`spotify:user:<u>:playlist:<id>`.** Canonicalising to the gid form fixes the two-rows defect (§4.4) but changes
  the text a round-trip produces; Connect and deep links accept the canonical spelling (0.2.9 already folds the
  Liked spellings the same way, `Entities.cs:181-197`), but it is a decision to write down, not an accident.
- **sqlite.** Keys stay `uri TEXT` (`Store.cs:715-719`, `edge.parent/child TEXT` at `:728`): the store formats the
  key on write and parses on read, and the schema does not change — **no migration**. The cost is a 36-char string
  per row per store read/write where today the interned string was free; it lands on the store thread, or on the
  UI thread's snapshot at `Store.cs:829-834` if left there — move it. If a BLOB(16)+kind key is preferred later, the
  fingerprint drops the file (`Store.cs:743-757`) and the provider re-answers, which is the plan's stated policy
  ("a v2 file is deleted, not migrated", `Store.cs:6`).
- **Hash quality of text-form rows** in the shared index is a non-issue only because they stay in their own
  dictionary; putting both forms in one `int[]` index would need a content hash for the wire probe of text rows.

---

## 7. If we do this — the sketch

**`Entities/Entities.cs`**
- §2: `public readonly struct EntityId` — `Lo`, `Hi`, `Meta` (`Kind` byte, `Provider` byte, `Form` byte
  {None, Gid, Text}, flags byte {Prerelease, …}); `Equals`/`GetHashCode` over the three words; `IsEmpty`;
  `static bool TryParse(ReadOnlySpan<byte>, out EntityId)` (the existing `ProviderOf` walk + `Base62.TryDecode` for
  the six gid kinds, `Intern` for everything else — UI thread, as `Parse` is today); `static EntityId Gid(EntityKind,
  ReadOnlySpan<byte> gid16)` (thread-safe, pure); `int Format(Span<char>)` / `Format(Span<byte>)`; `string Text`.
  `EntityUri` collapses to `EntityId` (keep the name as a `using`-style alias for one wave if the churn matters).
- §2: `static class Base62` — `TryDecode(ReadOnlySpan<byte>, out UInt128)` with the overflow guard,
  `Encode(UInt128, Span<char>)` / `Encode(UInt128, Span<byte>)` (the two-division form). The 0.2.9 class stays in
  `Backend/Spotify` until the fold deletes it.
- §5: `Column<EntityId> Id` replaces `Column<StringId> Uri`; `IdIndex` (`int[]`, power of two, load ≤ 0.75, keys
  compared against `Id`, tombstone-free removal by backward shift on `FreeSlot`) replaces `ByUri` for gid rows;
  `Dictionary<StringId,int> ByText` (with today's `UriKeys`) for text rows; `Slot(EntityId)`, `Slot(ReadOnlySpan<
  byte> utf8)` (parse → dispatch on form), `Slot(EntityKind, ReadOnlySpan<byte> gid16)`; `TryGetSlot` twins;
  `Alloc(EntityId)`; `FreeSlot` removes from whichever index the form names. `EnsureCapacity` presizes the index too.
- §8: the factories take `EntityId`; `Entities.Intern(bytes)` stays for text.

**Kind files** — `Uri`/`UriId` → `Id` (`EntityId`), twelve files, two lines each. `Album.cs`: the prerelease flag
when the parser sees `prerelease:`.

**`User.cs`** — `Add`/`Remove` (`:230, 240`) carry `EntityId`; `Like`/`Save`/`Follow` (`:270-277`) pass `.Id`.

**`Store.cs`** — `:770, 829-834, 1058`: format keys into a rented `char[]` on the UI thread (or carry `EntityId[]`
and format on the store thread); the reader's `Emit` (`:259-262`) parses `uri` back through `TryParse` on commit
(already the shape: uri text goes through the staging arena and `Slot(bytes)`).

**`Fetch.cs`** — `Demand` (`:150-174`) keeps slots only; `Queue` (`:343-357`) reads the provider from `Id[slot]`;
the request builder formats from the column into the request body.

**`Entities/Fetch.cs` / Wave 2's `Spotify.Decode.cs`** — TrackV4/album/artist/show/episode decoders write the
16-byte gid into staging (`StagedTrack.Uri` becomes an `EntityId`, or a `TextRef` *and* a `Gid` with the commit
preferring the gid) and the commit calls `Slot(kind, gid16)`: no encode, no text, 3 ns.

**Tests (`Wavee.V3.Tests`)**
- `EntitiesTests`: the existing corpus pins form per uri (gid for the six 22-char kinds; text for every other line
  of the corpus, including `spotify:track:abc` and the non-ASCII ids); `TryParse` refuses `zzzzzzzzzzzzzzzzzzzzzz`
  and takes `7N42dgm5tFLK9N8MT7fHC7` as `2^128−1`; `Format(TryParse(x)) == x` for every gid-form uri; the
  user-namespaced playlist decision, whichever way it goes; `EntityId` equality/hash across forms.
- `TableTests`: `Slot(bytes)` == `Slot(kind, gid16)` for the same entity; `FreeSlot` + re-`Slot` through both
  indexes; index growth keeps every slot findable; zero allocation on a 300-uri hit batch (both forms).
- `FootprintGateTests`: re-baseline — layout budget stays 1.5 MB (expected ~1.2 MB *including* identity for 10k
  gid rows), the text line becomes the text-form population only, and fix the `:45` subtraction.
- `StoreTests`: a gid-form row round-trips through `uri TEXT` unchanged; a text-form row likewise.
- `TrackTests`/`AlbumTests`/… : `UriId` → `Id` at the ~100 mechanical sites.
- One new file, `Base62Tests`, ported from `Wavee.Tests/CryptoTests.cs:161-192` plus the overflow vectors.

**Engine** — nothing. (`StringTable.Intern(ReadOnlySpan<byte>)`, plan §3.4, is still wanted for titles; it is
unrelated to identity.)
