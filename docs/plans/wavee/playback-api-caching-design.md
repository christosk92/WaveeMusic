# Playback API Caching — Technical Implementation Plan (v2)

Supersedes the first-pass "Playback API Waste Reduction Plan". Evidence: the `final.saz` Fiddler
capture (48 sessions, extracted + protobuf-decoded 2026-07-06) + the same-day playback log,
cross-checked against the code.

## 0. Evidence recap — what the log/capture prove

Capture inventory (per-endpoint, deduped by pattern): **6× head GET, 6× storage-resolve GET,
6× playplay license POST** across **5 unique fileIds** — `e63c24e6…` is the one duplicate, paying
the full triplet twice (sessions 17/18/20, then 34/35/36 on skip-back); **6× identical
`popular-release-segments-main-roles/artist_66CXW…` GETs**; 6× put-state, 5× herodotus,
3× color-lyrics + 3× amll TTML (one per unique track), 2× extended-metadata, 1× gabo — those are
the expected baseline, not waste.

Three facts decoded from the actual response bytes that the plan now builds on:

1. **`ttl_seconds` is real and large**: the storage-resolve protobuf carries
   `field 5 varint = 86400` (24 h), and all three signed mirror URLs
   (`audio-fa.scdn.co ?1783451149_…`, `audio-cf.spotifycdn.com ?verify=1783451149-…`,
   `audio-ak.spotifycdn.com ?__token__=exp=1783451149~…`) expire at **exactly**
   response-time + 86400 s. Server TTL == signature lifetime; the TTL field is trustworthy.
2. **HTTP-layer caching is explicitly disabled** on storage-resolve
   (`cache-control: private, max-age=0, no-cache`) — an HTTP cache would not help even if we had
   one; the proto TTL is the only caching contract on this route.
3. **The 6 artist-context responses are semantically identical**: byte-hashes differ (gzip
   framing) but all decompress to the same 2841-byte `SelectedListContent` — same 50 tracks, same
   order. Caching this is pure win; nothing dynamic was lost.

Track `7xoUc6faLbCqZO6fQEYprd` (file `e63c24e6…`) was played at generations **6, 9, 11**. Each
replay re-paid the full network cost:

| Generation | head GET | storage-resolve GET | PlayPlay license POST | derive |
| --- | --- | --- | --- | --- |
| 6 (first play) | ✔ 325ms | ✔ | ✔ | ✔ |
| 9 (skip-back) | ✔ 12ms *(warm just fetched it 20s earlier too)* | ✔ | ✔ | ✔ |
| 11 (skip-back) | ✔ 37ms | ✔ | ✔ | ✔ |

Same for `d504f562…` (gens 5, 7). The end-of-log warm burst is worse: when the foreground-quiet
window lapses, **5 deferred warms fire simultaneously** and re-fetch heads for `14cc3c75…`,
`e63c24e6…`, `ac6cd913…` — all fetched minutes earlier. The capture also shows **6 identical**
`popular-release-segments-main-roles/artist_66CXW…` GETs for one artist-context session.

Root causes, confirmed in code:

1. **`Resource.Revalidate()` is a force-fetch, used as a get.** `Resource.cs:86` starts a fetch
   whenever nothing is in flight — it never consults freshness. `HeadFileClient.GetAsync`
   (`HeadFileClient.cs:34`) and `AudioKeyResolver.GetKeyAsync` (`AudioKeyResolver.cs:72`) both call
   it unconditionally, so their `FreshnessPolicy.Immutable` caches *store* values but never *serve*
   them. There is no sequential-hit test (only concurrent coalescing).
2. **No storage-resolve cache.** `LiveTrackResolver.ResolveBodyAsync` (`LiveTrackResolver.cs:202`)
   hits spclient every call; the proto's `optional int64 ttl_seconds = 5` is ignored.
3. **Warm and resolve don't share work** — but only *because of* (1): warm's head fetch stores a
   value the resolve path then refuses to serve.
4. **Artist context resolve is uncached** (`LiveContextResolver.cs:216`).

Design principles that shape the plan:

- **Fix the engine once, not the call sites.** Everything above is a `Resource<K,V>` misuse or a
  missing `Resource`. One new read primitive on `Resource` fixes 1 and 3 and powers 2 and 4.
- **Cache by lifetime, not by workflow.** AES key + native seed: immutable per fileId. CDN URLs:
  signed, expiring. Head bytes: immutable but ~80 KB each. Three retention policies — not one
  "asset cache" blob.
- **A cache of signed URLs needs an invalidation story.** `AudioHostServer.cs:213` hands `CdnUrls`
  to the native host once; nothing re-resolves on a CDN 403 mid-stream. TTL safety margin +
  explicit `Invalidate` are mandatory, not optional polish.

---

## Phase 1 — `Resource` engine upgrade

**File: `src/apps/Wavee/Backend/Resource.cs`** (everything below is playback-agnostic).

### 1a. `GetAsync` — the awaitable read

```csharp
/// <summary>Await-able read: serve a resident fresh value without touching the network; otherwise
/// join/start ONE revalidation and return the outcome. Revalidate() remains the force-fetch;
/// Use() remains the reactive read. The caller's ct detaches the CALLER only — the shared fetch
/// always runs to completion and seeds the cache (skip-away → skip-back becomes a pure hit).</summary>
public async Task<Loaded<TValue>> GetAsync(TKey key, CancellationToken ct = default)
{
    var peek = Peek(key);
    if (peek.IsReady && !peek.IsStale) { Interlocked.Increment(ref _hitCount); return peek; }
    Interlocked.Increment(ref _missCount);
    await Revalidate(key).WaitAsync(ct).ConfigureAwait(false);
    return Peek(key);
}
```

Notes:
- `Peek` already snapshots under `_gate` (no torn reads) and already surfaces `Err` when the last
  fetch failed with nothing in flight — so `GetAsync` naturally retries errored entries
  (`Revalidate` starts a fresh attempt) and naturally returns `Err` if that attempt fails too.
  Callers keep their existing `!IsReady → typed throw` translation unchanged.
- `WaitAsync(ct)` (net10 built-in) is the whole cancellation design: OCE to the caller, fetch
  unharmed. Do **not** thread `ct` into `RunFetch`.

### 1b. Per-entry TTL

The `FreshnessPolicy` TTLs are per-Resource constants; storage-resolve's TTL arrives per response.
Add an optional constructor parameter and an `Entry` field:

```csharp
public Resource(Func<TKey, SessionContext, Task<TValue>> fetch, FreshnessPolicy fresh,
                Func<SessionContext> ctx,
                Func<TValue, TimeSpan?>? ttlOf = null,   // per-entry TTL, evaluated at store time
                int maxEntries = 0,                      // 0 = unbounded (today's behavior)
                string? name = null)                     // metrics/log tag
```

- `Entry` gains `public DateTime? ExpiresAt;`
- In `RunFetch`'s success block and in `Seed`:
  `e.ExpiresAt = _ttlOf?.Invoke(v) is { } t ? DateTime.UtcNow + t : null;`
- `IsStale(Entry e)` gets a pre-check **before** the policy switch:
  `if (e.ExpiresAt is { } exp && DateTime.UtcNow >= exp) return true;`
  This composes with every existing policy (an `Immutable` entry with `ExpiresAt` set still
  expires) and touches nothing when `ttlOf` is null.

### 1c. `Invalidate(key)`

```csharp
/// <summary>Drop the value so the next Get/Use fetches. Unlike MarkStale (stale-while-revalidate),
/// the dead value is never served again — for expired signed URLs. An in-flight fetch is left to
/// complete; its result lands normally (it is a FRESH fetch, so that is correct).</summary>
public void Invalidate(TKey key)
{
    lock (_gate)
    {
        if (!_cache.TryGetValue(key, out var e)) return;
        e.HasVal = false; e.Val = default; e.Error = null; e.ExpiresAt = null;
    }
}
```

### 1d. Bounded entries (LRU)

Only when `maxEntries > 0`:
- `Entry` gains `public long LastUse;` stamped with `Stopwatch.GetTimestamp()` in `Use`, `Peek`
  (hit path), `Seed`, and `RunFetch` success — all already under `_gate`.
- After inserting/storing in `RunFetch`/`Seed`, if `_cache.Count > maxEntries`, evict the
  smallest-`LastUse` entries with `InFlight == null` until at/below the cap (linear scan is fine:
  caps here are ≤ 64 and stores are per-network-fetch, not per-frame).
- Never evict an entry with an in-flight fetch.

Motivation: making the head cache actually hit means ~80 KB entries now deliberately persist for
the session. They already persist today by accident, but this plan leans on it — cap it (the app
already has a known unbounded-data-growth problem; do not add another accumulator).

### 1e. Metrics in the engine, once

Next to `FetchCount`: `long _hitCount, _missCount` + `public long HitCount/MissCount` (Interlocked,
incremented in `GetAsync` as shown; `Use` increments hit when serving resident non-stale). This
replaces scattered per-call-site counters — every Resource-backed surface (metadata, library,
lyrics, plus the three new ones) is instrumented identically. Optional: a debug log hook
`Action<string>?` logging `resource {name}: hit|miss` — wire it only where a `_log` already exists.

### Phase 1 tests — new `src/apps/Wavee.Tests/Backend/ResourceTests.cs`

| Test | Assertion |
| --- | --- |
| `GetAsync_SequentialSameKey_Immutable_FetchesOnce` | 2× `GetAsync` ⇒ `FetchCount == 1`, `HitCount == 1` |
| `GetAsync_PerEntryTtl_ExpiryRefetches` | `ttlOf: _ => 50ms`; get, wait 80ms, get ⇒ `FetchCount == 2` |
| `GetAsync_PerEntryTtl_FreshHits` | get, get within TTL ⇒ `FetchCount == 1` |
| `Invalidate_DropsValue_NextGetFetches` | get, invalidate, `Peek` not Ready, get ⇒ `FetchCount == 2` |
| `GetAsync_CallerCancelled_FetchStillSeedsCache` | fetch delayed 100ms; `GetAsync(ct: cancelled@10ms)` throws OCE; await quiescence; next `GetAsync` ⇒ `FetchCount == 1` total |
| `GetAsync_ErroredEntry_RetriesAndSurfacesErr` | failing fetch: first get ⇒ `Err`; fetch fixed: second get ⇒ Ready, `FetchCount == 2` |
| `MaxEntries_EvictsLru_NotInFlight` | cap 2; get A,B,C ⇒ A evicted (get A refetches); in-flight entry survives a cap pass |

---

## Phase 2 — Wire the three playback caches

### 2a. Head files — `src/apps/Wavee/SpotifyLive/Audio/HeadFileClient.cs`

```csharp
// ctor: bounded — heads are ~80 KB each and immutable; 32 ≈ 2.5 MB session ceiling
_cache = new Resource<string, HeadBytes>(FetchAsync, new FreshnessPolicy.Immutable(), ctx,
    maxEntries: 32, name: "audio.head");

public async Task<HeadBytes> GetAsync(string fileIdHex, CancellationToken ct = default)
{
    var loaded = await _cache.GetAsync(fileIdHex, ct).ConfigureAwait(false);
    if (loaded.IsReady) return loaded.Value!;
    throw new InvalidOperationException("head file fetch failed: " + (loaded.Error ?? "unknown"));
}
```

This alone kills: the warm→resolve double head (`60fe48bf…` at 36ms then 18ms), every skip-back
head refetch, and the entire end-of-quiet warm-burst refetch storm.

### 2b. Audio keys — `src/apps/Wavee/SpotifyLive/Audio/AudioKeyResolver.cs`

In `GetKeyAsync`, replace lines 72–74 with:

```csharp
var loaded = await _cache.GetAsync(cacheKey, ct).ConfigureAwait(false);
if (loaded.IsReady) return loaded.Value!.Key;
```

Keep the latch fast-path `Peek` block above it verbatim (it exists to serve a cached key while the
per-file failure latch is engaged — different concern). Construct with `name: "audio.key"`.

Hygiene fixes in the same file:
- `_pendingGids` is never cleaned → add `_pendingGids.TryRemove(cacheKey, out _);` in a `finally`
  around `FetchKeyAsync`'s body (safe: the fetch reads it once at the top; concurrent callers for
  the same cacheKey coalesce into one fetch, and a new call re-writes the entry before
  revalidating).
- **Do NOT move `ct` into the fetch** — `FetchKeyAsync` deliberately uses `CancellationToken.None`
  for AP/license/derive so a shared result can't be poisoned; `GetAsync`'s `WaitAsync` gives the
  caller its cancellation instead.

Native-seed invariant to pin with a test: `_nativeCdnSeeds[fileHex]` is written only inside the
fetch, but on a key **cache hit** the dictionary still holds the seed from the original fetch, so
`GetNativeCdnSeed` keeps working. Test asserts a second body resolve gets a non-empty seed with
`der.Calls == 1`.

### 2c. Storage-resolve — `src/apps/Wavee/SpotifyLive/LiveTrackResolver.cs`

New value type + Resource (keyed by `fileIdHex` — format-agnostic, matches the route):

```csharp
public sealed record CdnResolve(string[] Urls);

readonly Resource<string, CdnResolve> _cdnCache;   // ctor:
_cdnCache = new Resource<string, CdnResolve>(FetchCdnAsync, new FreshnessPolicy.Immutable(), ctx,
    ttlOf: _ => _cdnTtl, maxEntries: 64, name: "audio.cdn");
```

`FetchCdnAsync(fileIdHex, ctx)` = the current lines 202–216 verbatim (request, parse,
`Restricted → throw AudioPlaybackException(Restricted)`, empty → throw `Network`). Failures become
Resource error entries → natural retry on next access, **never** cached as values (a `Restricted`
today can be a region/licensing hiccup tomorrow; and more importantly an error value must not
suppress the typed throw).

TTL: capture the response TTL inside the fetch and stash it in a field the `ttlOf` lambda reads —
or simpler and race-free, put it on the value: `CdnResolve(string[] Urls, TimeSpan Ttl)` with
`ttlOf: r => r.Ttl`. Computation:

```csharp
// Grounded by the capture: ttl_seconds = 86400 and equals the signed-URL lifetime exactly (all
// three mirrors carry exp = responseTime + ttl). Trust it, minus a 0.8 margin: never hand the
// native host a URL near expiry — it has NO re-resolve path after supply_body (AudioHostServer
// passes urls once, and a track streams for minutes after the handoff). Fallback 30 min only if
// the optional field is ever absent. 86400 × 0.8 ≈ 19 h — effectively session-length.
var ttlSec = r.HasTtlSeconds && r.TtlSeconds > 0 ? r.TtlSeconds : 1800;
var ttl = TimeSpan.FromSeconds(ttlSec * 0.8);
```

`ResolveBodyAsync` becomes:

```csharp
var cdnLoaded = await _cdnCache.GetAsync(m.FileIdHex, ct).ConfigureAwait(false);
if (!cdnLoaded.IsReady) throw /* translate cdnLoaded.Error → AudioPlaybackException, preserving
                                 the Restricted vs Network reason (parse like AudioKeyResolver
                                 does: Enum.TryParse<AudioKeyFailureReason>(loaded.Error)) */;
var cdnUrls = cdnLoaded.Value!.Urls;
_log?.Invoke($"storage-resolve {m.FileIdHex}: {cdnUrls.Length} cdn url(s); fetching key");
var key = await _keys.GetKeyAsync(m.FileId, m.FileGid, ct).ConfigureAwait(false);
// … unchanged: nativeSeed, AudioStreamHandle(…, cdn: cdnUrls[0], cdnUrls)
```

To preserve the typed-reason contract, change `FetchCdnAsync` to throw
`InvalidOperationException(reason.ToString())` (the `AudioKeyResolver` convention — `Resource`
stores `ex.Message` as the error string) and translate back at the call site. Add
`StorageResolveRestricted_Throws_Typed_AndRetriesNextCall` to the tests.

**Invalidation hook** for the no-recovery-in-host constraint:

```csharp
/// <summary>Drop the cached CDN mirrors for a file — call when the host/stream reports a body
/// fetch failure so the next resolve gets fresh signed URLs instead of the dead ones.</summary>
public void InvalidateCdn(string fileIdHex) => _cdnCache.Invalidate(fileIdHex);
```

Wire it at the managed body-failure boundary: `FastTrackPlayback.FinishBodyAsync`'s catch
(`AudioPlaybackStack.cs:370`) currently just logs and rethrows — it can't tell key from CDN
failure, but the typed exception can: on `AudioKeyFailureReason.Network` (the storage/CDN bucket),
call `InvalidateCdn(fileIdHex)` before rethrowing (requires passing the resolver or a
`Action<string> invalidateCdn` into `FastTrackPlayback` — take the delegate, it keeps the class
decoupled). Controller-level retry of a failed track already exists above this seam; it will now
re-resolve fresh URLs instead of replaying the cached dead ones.

**Expected capture delta after Phase 2**: replay/skip-back of any track already played this
session ⇒ **0** head, **0** storage-resolve, **0** license calls (was: full triplet each time).
Gen-9/gen-11 style replays become metadata-cache + three cache hits.

### Phase 2 tests

`src/apps/Wavee.Tests/Audio/AudioKeyResolverTests.cs` — add:

| Test | Assertion |
| --- | --- |
| `SequentialRequests_SameFile_ServeCachedKey` | 2× `GetKeyAsync` (sequential) ⇒ `lic.Calls == 1`, `der.Calls == 1`, same key bytes |
| `SequentialRequests_DifferentFiles_FetchEach` | files A,B ⇒ `der.Calls == 2` (no false sharing) |
| `NativeSeed_SurvivesKeyCacheHit` | fetch once, second call is a hit ⇒ `GetNativeCdnSeed(fileHex)` non-empty |

New `src/apps/Wavee.Tests/Audio/HeadFileClientTests.cs` (mock `IHttpExchange`, same fake style as
`FakeLicense`):

| Test | Assertion |
| --- | --- |
| `SequentialGets_SameFile_OneHttpCall` | 2× `GetAsync` ⇒ http mock `Calls == 1`, same bytes |
| `FailedFetch_RetriesOnNextGet` | first 500 → throws; second 200 → succeeds, `Calls == 2` |

New `src/apps/Wavee.Tests/Audio/LiveTrackResolverCdnCacheTests.cs` (fake `ITransport` counting
storage-resolve requests; fake `IAudioKeySource`):

| Test | Assertion |
| --- | --- |
| `ResolveBody_SecondCall_SkipsStorageResolve` | 2× `ResolveBodyAsync(meta)` ⇒ 1 transport call, both handles carry 3 urls |
| `ResolveBody_TtlExpired_Refetches` | serve `ttl_seconds = 1` (→ 800ms effective); wait; resolve ⇒ 2 transport calls |
| `ResolveBody_TtlFromResponse_IsHonored` | serve `ttl_seconds = 86400` (the real value) ⇒ entry `ExpiresAt` ≈ now + 19.2h |
| `InvalidateCdn_ForcesRefetch` | resolve, `InvalidateCdn`, resolve ⇒ 2 transport calls |
| `Restricted_Throws_NotCachedAsValue` | restricted response → typed throw; next call hits transport again |

---

## Phase 3 — Warm path: unification is emergent

The v1 plan's `PlaybackAssetCache` is **dropped**. After Phase 2, `WarmAsync` calling
`ResolveMetaAsync` + `heads.GetAsync` *is* seeding the same caches `ResolveFastAsync` reads.
Remaining work is scheduling only, in `FastTrackPlayback` (`AudioPlaybackStack.cs`):

1. **Extend warm to the body** — after the head line in `WarmAsync` (line 333):

   ```csharp
   if (string.IsNullOrEmpty(meta.ExternalUrl))
   {
       var h = await _resolver.ResolveBodyAsync(meta, CancellationToken.None).ConfigureAwait(false);
       _log?.Invoke($"fast-warm {track.Uri}: body ok cdn={h.CdnUrls?.Length ?? 0}");
   }
   ```

   The surrounding try/catch already swallows warm failures. Warm only ever targets the upcoming
   track (one `Warm` per start, 6 s delay + foreground-quiet window — see the log), so this is one
   extra license POST per *new* upcoming track, in exchange for the entire storage+license+derive
   latency vanishing from the audible track change. Failure semantics are correct by construction:
   the PlayPlay per-file latch and the Resource error entry are shared with the foreground path,
   which is what we want (a genuinely restricted file shouldn't be re-derived by the foreground
   either — and a transient failure retries because errors are never cached as values).
2. **The warm burst self-heals** — the 5-simultaneous-deferred-warms burst still fires, but every
   head call in it is now a cache hit (log will show `resource audio.head: hit` ×5, zero CDN GETs).
   Keep `_warmInFlight` exactly as is; add nothing.
3. **Warm `elapsed` log lie** — warm logs `elapsed=17147ms` including the deferral sleep; restart
   the stopwatch after the quiet loop so the number means fetch time. (Cosmetic, one line.)

No new tests beyond an assertion piggybacked on the Phase 2 resolver test: warm-equivalent call
order (`ResolveMetaAsync` → `heads.GetAsync` → `ResolveBodyAsync`, then the fast-resolve order) ⇒
transport/http/license mocks each called exactly once.

---

## Phase 4 — Artist context cache

**File: `src/apps/Wavee/SpotifyLive/LiveContextResolver.cs`.**

Critical detail: `ResolveArtistAsync` computes the skip-to start index (line 243) **after** the
fetch — so the cacheable artifact is the **wire result**, not the finished `ResolvedContext` (which
embeds a per-request `start`). Hydration is already cached by `MetadataService`, so re-hydrating on
a cache hit is near-free (that comment at line 258 is already true — the missing piece is the wire
fetch itself).

```csharp
sealed record ArtistContextWire(List<QueuedRef> Refs, Dictionary<string, string> Metadata);

readonly Resource<string, ArtistContextWire> _artistCache;   // ctor:
_artistCache = new Resource<string, ArtistContextWire>(FetchArtistWireAsync,
    new FreshnessPolicy.Etag(TimeSpan.FromMinutes(15)), ctx, maxEntries: 16, name: "connect.context.artist");
```

- Key: the artist **id** (from `spec.Uri`) — never the skip params.
- `FetchArtistWireAsync` = current lines 218–240 + 245–253 (request → zstd → `SelectedListContent`
  → `ParseContents` → refs + metadata dict). Failure/0-tracks → throw (error entry ⇒ retry next
  play, and `ResolveArtistAsync` maps `!IsReady` → `ResolvedContext.Empty` exactly as today).
- `ResolveArtistAsync` becomes: `GetAsync(id)` → hydrate refs → `FindStartIndex(spec…)` → assemble
  `ResolvedContext` per request.
- 15 min TTL is conservative for a popular-releases list (changes on release cadence, not
  per-skip). No push channel exists for it; if one is added later, `MarkStale` is the right verb
  (SWR — instant play, silent refresh).

Test (`LiveContextResolverTests` or new file, fake `ITransport`):
`ArtistResolve_SecondPlay_OneWireFetch_SkipParamsStillApply` — resolve twice with different
`SkipToIndex` ⇒ 1 transport call, two different `StartIndex` values.

**Expected capture delta**: artist-radio session ⇒ 1 `popular-release-segments` GET (was 6).

---

## Phase 5 — Verification

1. `dotnet build` clean + `dotnet test` (all Phase 1–4 tests above green). *(Tests are agent-runnable;
   app-level verification below is Chris's — no claims from a stale binary.)*
2. **Manual Fiddler script** (same shape as the original capture): startup → play 3 tracks → skip
   back to track 1 → skip forward → let one track play past the 6 s warm window → play an artist
   context and skip within it 3×. Success criteria:
   - exactly **one** `head/{fileId}`, **one** `storage-resolve/.../{fileId}`, **one**
     `playplay/v1/key/{fileId}` per **unique** fileId for the whole session;
   - zero network on skip-back; log shows `resource audio.head/audio.key/audio.cdn: hit` lines;
   - one `popular-release-segments` GET per artist context;
   - warm-burst window shows head cache hits, no CDN GETs.
3. **Negative check**: Fiddler autoresponder → 403 on the audio CDN body host mid-session ⇒
   confirm `InvalidateCdn` fires on the body-failure path and the retry re-resolves fresh URLs.
4. **TTL check**: leave the app idle past 0.8×TTL, replay the same track ⇒ exactly one fresh
   storage-resolve, still zero head/license calls.

---

## Sequencing, risk, out-of-scope

**Land order = phase order.** Phase 1 is pure addition (no existing `Resource` caller changes
behavior — `Use`/`Revalidate`/`Peek`/`Seed`/`MarkStale` semantics untouched; `IsStale`'s new
`ExpiresAt` pre-check is inert while `ttlOf` is null). Phase 2 is three call-site swaps, each
independently revertible. Phases 3–4 are independent of each other.

Edge cases accounted for:
- **Quality/format switch mid-session** → different fileId → separate cache entries everywhere
  (all three caches key on fileId, not track uri). Correct by construction.
- **External MP3 / episodes** → `ResolveBodyAsync` early-returns before the CDN cache; unaffected.
- **Key cacheKey includes trackGid** (`fileHex:gidHex`) — alternative-track relinking keeps its
  distinct key; the latch stays keyed by fileHex alone. Unchanged.
- **AP session latch** (`_apDisabled`) — intentional, untouched.

Out of scope (unchanged from v1): PlayPlay runtime/license internals (`src/apps/Wavee.PlayPlay/**`,
separate repo, agent-fenced); CDN body byte caching / offline; the `IpcPipeTransport`
`ObjectDisposedException` startup race (real bug — the first remote call races startup
provisioning in `SupervisedAudioHost.RunRemoteAsync`; file separately, it produces no duplicate
HTTP).

## Why this shape beats v1

| v1 | v2 | Why |
| --- | --- | --- |
| `GetOrRevalidate` helper per call site | `Resource.GetAsync` in the engine | storage-resolve, context, and future surfaces get hits + caller-decoupled cancellation for free; one test surface |
| One `PlaybackAssetCache` table | Three Resources with per-artifact retention | key immutable / URLs expiring / heads big — one blob is wrong for at least two of them |
| Warm/resolve sharing as new machinery | Emergent from Phase 2 + 3-line body warm | no second source of truth; the warm burst self-heals |
| TTL cache, no failure story | TTL×0.8 + `Invalidate` + failure-path hook | the native host cannot recover from an expired signed URL |
| Cache `ResolvedContext` for artist | Cache the wire result, apply skip per request | the start index is per-request; caching it would replay stale skip targets |
| Per-call-site counters | Hit/miss counters inside `Resource` | every surface instrumented identically |
| Silent on memory | LRU caps (head 32, cdn 64, artist 16) | ~80 KB/head entry, session-long; known unbounded-growth problem |
