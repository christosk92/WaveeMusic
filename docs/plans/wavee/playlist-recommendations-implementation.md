# "Recommended songs" stuck loading + never recomputes — implementation plan

**Status:** planned, not started. **Scope:** `src/apps/Wavee/Features/Detail/DetailTracks.cs` (the `TrackList` recs
section), `src/apps/Wavee/Backend/Playlists/{PlaylistExtenderClient.cs, PlaylistMutationDiagnostics.cs}`, a new
engine-free `Features/Detail/RecsRefetchPolicy.cs`, `assets/loc/en-US.json`, `Wavee.Tests`. Independent of the two
sibling plans; the fingerprint in §2.1 reacts to whatever membership the store publishes, so it benefits from
`playlist-sync-convergence-implementation.md` without depending on it.

---

## 0. RCA — verified 2026-09-05 (every line number below is current)

The feature is not a component of its own: `TrackList` appends one `RecHeader` row and N `RecRow`s to the same bound
`ItemsView` as the track rows (`DetailTracks.cs:872-905` gate, `:928-951` the bound list, `:2849-2888` `RowOrRecContent`).
Data: `PlaylistExtenderClient.ExtendAsync` (`PlaylistExtenderClient.cs:30-42`) — `POST spclient.wg/playlistextender/extendp/`
with `{playlistURI, trackSkipIDs, numResults:20}`; the server infers the seed tracks itself, no revision is sent.

| Symptom | Cause | Where |
|---|---|---|
| **Stuck on the spinner forever** (any playlist). | `_recState` is a 3-state `Signal<int>` — `0 idle · 1 loading · 2 loaded` — with no failed/timed-out state, no request supersession and **no path back to 0**. `FetchRecs` returns early whenever the state is `1`, so a request that never completes wedges the lazy trigger, the Refresh button and the auto-refill in `AddRec` for the component's whole life. | `:180` (field), `:2985` `if (_recState.Peek() == 1) return;`, `:2987`, `:3002`. |
| The 30 s `HttpClient.Timeout` does not bound it. | `HttpClientExchange` sends with `ResponseHeadersRead`, so the timeout covers headers only; the body streams under the caller's token. For recs that token is `_recCts.Token`, cancelled **only at unmount**. `ExtendAsync` has no deadline of its own. | `Backend/Spotify/HttpPools.cs:12-17` (30 s), `Backend/Spotify/HttpClientExchange.cs:30-43`, `SpotifyLive/LiveDealerTransport.cs:63-76` (`CopyToAsync(ms, ct)`), `DetailTracks.cs:182, 635, 2990`. |
| A stall, a 200-with-empty-body, a parse failure and a healthy batch are indistinguishable in the logs. | Only a non-2xx gets `ExtendFailed`; `Parse` swallows every exception into an empty list; `FetchRecs`'s `catch` swallows everything including `OperationCanceledException` and still posts `loaded`. | `PlaylistExtenderClient.cs:38-41, 61-75`, `DetailTracks.cs:2996-2997`. |
| **Never recomputes after a track is added/removed.** | Exactly three triggers: the header's mount effect with a **constant** dep key (fires once per mount, and `force:false` no-ops unless idle), the Refresh button, and `AddRec` when the batch empties. Nothing reads `model.Tracks`, a count or a membership revision. | `:2908-2912` (`UseEffect(…, "rec-header-once")`), `:2914-2918`, `:3036-3037`. |
| The section's copy is hardcoded and its loc keys are dead. | `"Recommended songs"` and `"No suggestions right now"` are literals; `detail.recommended` / `detail.noSuggestions` exist in `en-US.json:362-363` and are read by nothing. | `:2935`, `:2922`. |

### Discrepancy check — the "Based on the songs of this playlist" subtitle + shimmer bars

Grepped the whole repo (all of `src`, case-insensitive: "based on the songs", "based on", "recommended", "shimmer",
"skeleton", the two loc keys). Result: **there is exactly one component that renders recommended TRACKS — this one.**
Every other "recommend"-flavoured surface is a different thing on a different page: the album page's "Featured on"
playlist shelf (`DetailTrailing.cs:457-461`, `Skel.Region` skeleton), Home's baseline recommendations shelf
(`HomeModules.cs:399-420`, `HomeSkeleton`), Home's queued-suggestions module (`HomeModules.cs:262`), the concerts hub's
"Recommended for you" (`ConcertHubPage.cs:226-235`). None of them shows a "Based on the songs…" subtitle — that string does
not exist anywhere in the repo, in any locale — and the playlist recs header has **no subtitle element and no shimmer**:
while loading it shows one 32-DIP `TrackRow.Spinner()` (`:2923-2924`); `ShimmerRow` (`:2579-2591`) belongs to the reveal
ramp and is deliberately blank.

**Flag for the user:** a screenshot showing text-shaped shimmer bars under a "Based on the songs of this playlist"
subtitle was not produced by this code. Either it is from a different page (the Home recommendations shelf is the only
place with both a skeleton and per-card subtitles), from another client, or from a build this repo does not contain.
This plan covers the playlist-detail recs section as it actually exists and **does not add** that subtitle; the copy fix
is limited to routing the two existing literals through their dead loc keys (§2.6).

---

## 1. Target behaviour

```
   membership changes (add / remove / foreign edit lands)      user
   ──────────────────────────────────────────────────────      ────
   model.Tracks → fingerprint ──► UseDebouncedValue 750 ms ──► RecsRefetchPolicy.Decide ──► Fetch / Supersede / None
                                                                      ▲                          │
   header mounts (scrolled to the bottom) ── arm ─────────────────────┘                          │ epoch++ · cancel previous CTS
   Refresh button ───────────── force ────────────────────────────────┘                          │ linked CTS, CancelAfter(15 s)
   AddRec empties the batch ──── force ───────────────────────────────┘                          ▼
                                                                                     ExtendAsync(uri, skip, 20, ct)
                                                                                      │ ok (epoch current) → Loaded, batch
                                                                                      │ ok (epoch stale)   → dropped, logged
                                                                                      │ timeout            → Failed, Refresh shown
                                                                                      └ error              → Failed, Refresh shown
```

Header states:

```
  ┌───────────────────────────────────────────────────────────────────────────┐
  │ Recommended songs                                                    (⟳) │  Idle / Loaded (Refresh button)
  │ Recommended songs                                                    (◌) │  Loading (spinner)
  │ Recommended songs                        No suggestions right now    (⟳) │  Loaded, empty batch
  │ Recommended songs                Couldn’t load suggestions           (⟳) │  Failed / timed out — Refresh recovers it
  └───────────────────────────────────────────────────────────────────────────┘
```

A batch that is in flight when the membership changes is superseded (cancelled, its result dropped); the rows already on
screen stay until the new batch lands, so the section never flashes empty on an add.

---

## 2. Changes

### 2.1 Pure decision — `Features/Detail/RecsRefetchPolicy.cs` (new, engine-free, source-included by `Wavee.Tests`)

```csharp
namespace Wavee;

/// <summary>The recs section's request state. <see cref="Failed"/> is new: a timed-out or faulted fetch used to be
/// indistinguishable from "loading" and wedged the section for the component's life.</summary>
public enum RecsState : byte { Idle = 0, Loading = 1, Loaded = 2, Failed = 3 }

public enum RecsAction : byte
{
    /// <summary>Nothing to do (not armed, already current, or the user must press Refresh).</summary>
    None = 0,
    /// <summary>Start a fetch; no request is in flight.</summary>
    Fetch = 1,
    /// <summary>Cancel the in-flight request and start a new one (its result is stale before it lands).</summary>
    Supersede = 2,
}

/// <summary>The PURE decision behind every recs fetch. Engine-free (System only) so RecsRefetchPolicyTests pins the
/// whole matrix against production code.
/// <para>Inputs: the current state; whether the header has ever mounted (<paramref name="armed"/> — the section is
/// lazy, nothing fetches until the user scrolls to it); whether the membership fingerprint the CURRENT batch was
/// fetched for still matches (<paramref name="fingerprintCurrent"/>); and whether this is an explicit user/refill
/// request (<paramref name="force"/> — the Refresh button, or AddRec emptying the batch).</para></summary>
public static class RecsRefetchPolicy
{
    /// <summary>The in-flight deadline. Headers are bounded by HttpClient.Timeout (30 s); the BODY is not (ResponseHeadersRead),
    /// so this is what turns a stalled stream into <see cref="RecsState.Failed"/> instead of a forever-spinner.</summary>
    public static readonly TimeSpan FetchDeadline = TimeSpan.FromSeconds(15);

    /// <summary>How long the membership must be quiet before a changed fingerprint re-fetches. A drag-drop of five
    /// tracks is five store bumps; one request, not five.</summary>
    public const float DebounceMs = 750f;

    public static RecsAction Decide(RecsState state, bool armed, bool fingerprintCurrent, bool force)
    {
        if (!armed) return RecsAction.None;                                   // lazy: nothing until the header has realized
        if (force) return state == RecsState.Loading ? RecsAction.Supersede : RecsAction.Fetch;
        if (!fingerprintCurrent) return state == RecsState.Loading ? RecsAction.Supersede : RecsAction.Fetch;
        return state == RecsState.Idle ? RecsAction.Fetch : RecsAction.None; // Loading: wait · Loaded: current · Failed: user retries
    }

    /// <summary>The membership fingerprint: context uri + row count + an order-insensitive 64-bit fold of the row uris.
    /// Order-insensitive on purpose — a reorder changes nothing the extender sees (it infers seeds from membership).
    /// Cheap (one pass, no allocation) and VALUE-MEANINGFUL, which the engine's debounced-thunk memo requires: an
    /// in-place refresh that republishes the same rows produces the same value and re-arms nothing.</summary>
    public static long Fingerprint(string? contextUri, System.Collections.Generic.IReadOnlyList<Wavee.Core.Track> tracks)
    {
        ulong h = 1469598103934665603UL;                                      // FNV-1a offset basis
        h = Fnv(h, (ulong)(uint)(contextUri?.GetHashCode() ?? 0));            // string.GetHashCode is per-process; fine for an in-memory key
        h = Fnv(h, (ulong)tracks.Count);
        ulong fold = 0;
        for (int i = 0; i < tracks.Count; i++) fold += (ulong)(uint)string.GetHashCode(tracks[i].Uri, StringComparison.Ordinal);
        h = Fnv(h, fold);
        return (long)h;
    }

    static ulong Fnv(ulong h, ulong v) { h ^= v; return h * 1099511628211UL; }
}
```

(`string.GetHashCode` is randomized per process — acceptable: the fingerprint is compared only within one process and
never persisted. If a stable hash is wanted later, swap in `XxHash64`; the tests below assert equality/inequality, not
values.)

### 2.2 `TrackList` state — `Features/Detail/DetailTracks.cs:173-196`

```csharp
readonly Signal<IReadOnlyList<Track>> _recs = new(Array.Empty<Track>());
readonly Signal<RecsState> _recState = new(RecsState.Idle);   // was Signal<int> 0/1/2 — now carries Failed
readonly HashSet<string> _recShown = new(StringComparer.Ordinal);
readonly System.Threading.CancellationTokenSource _recCts = new();   // the LIFETIME token (unmount) — per-fetch tokens link to it
System.Threading.CancellationTokenSource? _recInflight;               // the CURRENT fetch's linked CTS; cancelled by Supersede / unmount
int _recEpoch;                                                         // monotonically increments per fetch; a completion with a stale epoch is dropped
long _recFetchedFp;                                                    // the membership fingerprint the CURRENT batch (or in-flight fetch) is for
bool _recArmed;                                                        // the header has realized at least once (the lazy gate)
readonly Signal<long> _membershipFp = new(0);                          // written in the layout effect below; the debounce source
```

Compute the fingerprint where the count signals are already written (`:899-905`), so it rides the same effect and the
same `DepKey`:

```csharp
long membershipFp = RecsRefetchPolicy.Fingerprint(model.ContextUri, model.Tracks);   // O(N), once per model change; Render already walks the rows
UseLayoutEffect(() =>
{
    _visibleCount.Value = visible;
    _listCount.Value = listTotal;
    _verticalFacts.Value = verticalFacts;
    _verticalItemCount.Value = DetailVerticalLayout.ItemCount(visible, verticalFacts);
    _membershipFp.Value = membershipFp;      // equality-gated: a same-membership re-render writes nothing
}, DepKey.From(HashCode.Combine(visible, listTotal, verticalFacts, membershipFp)));
```

Unmount (`:635`) also cancels the in-flight linked CTS:

```csharp
Context.UseSignalEffect(() => Reactive.OnCleanup(() =>
{
    try { _recInflight?.Cancel(); _recInflight?.Dispose(); _recCts.Cancel(); _recCts.Dispose(); } catch { }
}));
```

### 2.3 `FetchRecs` — `:2980-3005` becomes the one place a fetch starts, superseding by epoch

```csharp
/// <summary>Start (or supersede) a recs fetch per <see cref="RecsRefetchPolicy"/>. force:false = the lazy trigger / a
/// membership change; force:true = Refresh / auto-refill. Every fetch gets its own epoch and a linked, deadline-bound
/// token; a completion whose epoch is no longer current is dropped, so a superseded batch can never overwrite a newer one.</summary>
void FetchRecs(Services svc, Action<Action> post, string uri, bool force)
{
    if (svc.RealExtender is not { } extender) return;
    long fp = _membershipFp.Peek();
    var action = RecsRefetchPolicy.Decide(_recState.Peek(), _recArmed, fingerprintCurrent: fp == _recFetchedFp, force);
    if (action == RecsAction.None) return;

    if (action == RecsAction.Supersede)
    {
        PlaylistMutationDiagnostics.ExtendSuperseded(uri, _recEpoch);
        try { _recInflight?.Cancel(); } catch { }
    }
    _recInflight?.Dispose();
    var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_recCts.Token);
    cts.CancelAfter(RecsRefetchPolicy.FetchDeadline);
    _recInflight = cts;
    int epoch = ++_recEpoch;
    _recFetchedFp = fp;
    _recState.Value = RecsState.Loading;

    string[] skip = _recShown.Count == 0 ? Array.Empty<string>() : new string[_recShown.Count];
    if (skip.Length > 0) _recShown.CopyTo(skip);
    var ct = cts.Token;
    long started = Environment.TickCount64;
    _ = Run();

    async System.Threading.Tasks.Task Run()
    {
        IReadOnlyList<Track>? batch = null;
        bool timedOut = false;
        try { batch = await extender.ExtendAsync(uri, skip, RecBatch, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_recCts.IsCancellationRequested) { return; }   // unmount — nothing to post to
        catch (OperationCanceledException) { timedOut = !ct.IsCancellationRequested || cts.IsCancellationRequested; }
        catch (Exception ex) { PlaylistMutationDiagnostics.ExtendFaulted(uri, epoch, ex); }

        post(() =>
        {
            if (epoch != _recEpoch) return;                     // superseded while in flight — a newer fetch owns the state
            if (batch is null)
            {
                if (timedOut) PlaylistMutationDiagnostics.ExtendTimedOut(uri, epoch, Environment.TickCount64 - started);
                _recState.Value = RecsState.Failed;             // the old batch (if any) stays on screen; Refresh is offered
                return;
            }
            for (int i = 0; i < batch.Count; i++) { var id = batch[i].Id; if (id.Length > 0) _recShown.Add(id); }
            _recs.Value = batch;
            _recState.Value = RecsState.Loaded;
        });
    }
}
```

The distinction between "superseded" and "timed out" is the epoch, not the exception: a superseded fetch's `Cancel()`
throws `OperationCanceledException` too, but by the time its `post` runs `_recEpoch` has moved on and the result is
dropped. A timeout is the same exception with the epoch still current → `Failed`. (The `timedOut` flag is therefore only
for the log line; the state decision is the epoch check.)

### 2.4 Triggers — `RecHeader.Render` (`:2899-2925`) and `AddRec` (`:3036-3037`)

```csharp
public override Element Render()
{
    var svc = UseContext(Services.Slot);
    var post = UsePost();
    var state = _o._recState.Value;                      // subscribe → spinner ↔ refresh ↔ failed note
    int count = _o._recs.Value.Count;

    // Arm + lazy first fetch when THIS header realizes (scrolled to bottom). Constant dep ⇒ once per mount; the policy
    // makes a recycle remount a no-op when the batch is still current.
    UseEffect(() =>
    {
        _o._recArmed = true;
        if (svc?.RealExtender is not null && _o._model.ContextUri is { Length: > 0 } uri) _o.FetchRecs(svc, post, uri, force: false);
    }, "rec-header-once");

    // Membership-driven re-fetch: the fingerprint, debounced 750 ms (equality-gated — unrelated re-renders do not
    // restart the timer). Keyed on the debounced VALUE, so it runs once per settled change, never per store bump.
    var settledFp = UseDebouncedValue(_o._membershipFp, RecsRefetchPolicy.DebounceMs);
    UseEffect(() =>
    {
        if (svc?.RealExtender is not null && _o._model.ContextUri is { Length: > 0 } uri) _o.FetchRecs(svc, post, uri, force: false);
    }, DepKey.From(settledFp.Value));

    void Refresh()
    {
        if (svc?.RealExtender is not null && _o._model.ContextUri is { Length: > 0 } uri) _o.FetchRecs(svc, post, uri, force: true);
    }

    var trailing = new List<Element>(2);
    if (state == RecsState.Loaded && count == 0)
        trailing.Add(new TextEl(Loc.Get(Strings.Detail.NoSuggestions)) { Size = 12f, Color = Tok.TextTertiary });
    else if (state == RecsState.Failed)
        trailing.Add(new TextEl(Loc.Get(Strings.Detail.RecsFailed)) { Size = 12f, Color = Tok.TextTertiary });
    trailing.Add(state == RecsState.Loading
        ? new BoxEl { Width = 32f, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [TrackRow.Spinner()] }
        : RefreshButton(Refresh));

    return new BoxEl
    {
        Key = "rec:header-row",
        Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinHeight = _rowH,
        Padding = new Edges4(TrackRow.PadX, 0f, TrackRow.PadX, 0f),
        Children =
        [
            Ui.BodyStrong(Loc.Get(Strings.Detail.Recommended)) with { Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            .. trailing,
        ],
    };
}
```

`UseDebouncedValue(IReadSignal<T>, float)` is the engine hook at `FluentGpu.Engine/Hooks/RenderContext.Timers.cs:182`
(the `IReadSignal` overload — we already have a signal, so the thunk/memo form at `:214` is unnecessary; the signal write
in §2.2 is itself equality-gated). At mount the debounced output is seeded with the source's current value and does NOT
arm (`:197`), so the second effect's first run coincides with the arm effect and `Decide` collapses them into one fetch
(`fingerprintCurrent` is false only until `_recFetchedFp` is set by the first).

`RowOrRecContent` (`:2874-2877`) and the gate (`:883-892`) are untouched: they read `_recs.Value.Count`, not the state.
`AddRec`'s auto-refill (`:3036-3037`) stays `force: true`; through the policy that is `Fetch` (Loaded) or `Supersede`
(Loading), never a wedge.

### 2.5 Client logging — `Backend/Playlists/PlaylistExtenderClient.cs:30-42, 61-75`

```csharp
public async Task<IReadOnlyList<Track>> ExtendAsync(string playlistUri, IReadOnlyList<string> skipTrackIds, int numResults, CancellationToken ct = default)
{
    long started = Environment.TickCount64;
    PlaylistMutationDiagnostics.ExtendStarted(playlistUri, skipTrackIds.Count, numResults);
    var body = BuildBody(playlistUri, skipTrackIds, numResults);
    var r = await _transport.Request(Channel.SpclientWg, "/playlistextender/extendp/", body, ct, "POST", JsonHeaders).ConfigureAwait(false);
    if (!r.Ok)
    {
        PlaylistMutationDiagnostics.ExtendFailed(playlistUri, r.Status);
        return Array.Empty<Track>();
    }
    var bytes = SpotifyZstd.MaybeDecompressZstd(r.Body);
    var tracks = Parse(playlistUri, bytes);
    PlaylistMutationDiagnostics.ExtendOk(playlistUri, tracks.Count, bytes.Length, Environment.TickCount64 - started);
    return tracks;
}

static IReadOnlyList<Track> Parse(string playlistUri, byte[] bytes)
{
    try { /* existing body of Parse (:61-73) */ }
    catch (Exception ex)
    {
        PlaylistMutationDiagnostics.ExtendParseFailed(playlistUri, bytes.Length, ex);   // was: silent empty list
        return Array.Empty<Track>();
    }
}
```

New events in `PlaylistMutationDiagnostics.cs`, beside `ExtendFailed` (`:23-25`), same shape:

```csharp
public static void ExtendStarted(string playlistUri, int skip, int want) =>
    WaveeLog.Instance.Info(Category, "playlistextender.extend.start", "POST playlistextender/extendp",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("skip", skip), WaveeLogField.Of("want", want));

public static void ExtendOk(string playlistUri, int count, int bytes, long ms) =>
    WaveeLog.Instance.Info(Category, "playlistextender.extend.ok", "playlistextender batch received",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("count", count), WaveeLogField.Of("bytes", bytes), WaveeLogField.Of("ms", ms));

public static void ExtendParseFailed(string playlistUri, int bytes, Exception ex) =>
    WaveeLog.Instance.Warn(Category, "playlistextender.extend.parsefailed", "playlistextender body could not be parsed — treated as empty",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("bytes", bytes), WaveeLogField.Of("error", ex.GetType().Name));

public static void ExtendSuperseded(string playlistUri, int epoch) =>
    WaveeLog.Instance.Info(Category, "playlistextender.extend.superseded", "in-flight recs fetch cancelled — membership changed or Refresh pressed",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("epoch", epoch));

public static void ExtendTimedOut(string playlistUri, int epoch, long ms) =>
    WaveeLog.Instance.Warn(Category, "playlistextender.extend.timeout", "recs fetch exceeded its deadline — section marked failed",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("epoch", epoch), WaveeLogField.Of("ms", ms));

public static void ExtendFaulted(string playlistUri, int epoch, Exception ex) =>
    WaveeLog.Instance.Warn(Category, "playlistextender.extend.faulted", "recs fetch threw — section marked failed",
        WaveeLogField.Of("uri", playlistUri), WaveeLogField.Of("epoch", epoch), WaveeLogField.Of("error", ex.GetType().Name));
```

A healthy fetch is now `start → ok(count, bytes, ms)`; an empty 200 is `ok(count=0, bytes=N)`; a stall is `start` then
`timeout` ~15 s later; a superseded one is `start → superseded → start → ok`.

### 2.6 Copy / loc — `assets/loc/en-US.json`, the `detail` object (`:362-363` already has the two dead keys)

Route the two literals through their existing keys (`Strings.Detail.Recommended`, `Strings.Detail.NoSuggestions`) and
add one:

```json
"recommended": "Recommended songs",
"noSuggestions": "No suggestions right now",
"recsFailed": "Couldn’t load suggestions",
```

No subtitle is added (see the discrepancy check in §0). `nl.json` / `ko-KR.json` have none of the three keys and fall back
to the base — unchanged behaviour, now via the generator rather than a literal.

---

## 3. Tests — `Wavee.Tests` (engine-free; add `..\Wavee\Features\Detail\RecsRefetchPolicy.cs` to `Wavee.Tests.csproj`
beside `PlaylistListState.cs` at `:118`)

`RecsRefetchPolicyTests` (xUnit v3, `namespace Wavee.Tests`, the `ShutdownUpdatePolicyTests` shape):

```csharp
// The recs section's ONE fetch decision. Pinned here because the bug it replaces — a 3-state machine with no exit from
// "loading" — was invisible in every log and permanent for the component's life.
public class RecsRefetchPolicyTests
{
    [Theory]
    [InlineData(RecsState.Idle)] [InlineData(RecsState.Loading)] [InlineData(RecsState.Loaded)] [InlineData(RecsState.Failed)]
    public void NotArmed_NeverFetches_EvenWhenForced(RecsState s)
    {
        Assert.Equal(RecsAction.None, RecsRefetchPolicy.Decide(s, armed: false, fingerprintCurrent: false, force: true));
        Assert.Equal(RecsAction.None, RecsRefetchPolicy.Decide(s, armed: false, fingerprintCurrent: true, force: false));
    }

    [Fact] public void Armed_Idle_Current_Fetches()
        => Assert.Equal(RecsAction.Fetch, RecsRefetchPolicy.Decide(RecsState.Idle, true, fingerprintCurrent: true, force: false));

    [Theory] [InlineData(RecsState.Loaded)] [InlineData(RecsState.Failed)]
    public void Armed_Current_NotForced_LoadedOrFailed_DoesNothing(RecsState s)
        => Assert.Equal(RecsAction.None, RecsRefetchPolicy.Decide(s, true, fingerprintCurrent: true, force: false));

    [Fact] public void Armed_Loading_Current_Waits()
        => Assert.Equal(RecsAction.None, RecsRefetchPolicy.Decide(RecsState.Loading, true, fingerprintCurrent: true, force: false));

    [Theory] [InlineData(RecsState.Idle)] [InlineData(RecsState.Loaded)] [InlineData(RecsState.Failed)]
    public void FingerprintChanged_NotLoading_Fetches(RecsState s)
        => Assert.Equal(RecsAction.Fetch, RecsRefetchPolicy.Decide(s, true, fingerprintCurrent: false, force: false));

    [Fact] public void FingerprintChanged_WhileLoading_Supersedes()
        => Assert.Equal(RecsAction.Supersede, RecsRefetchPolicy.Decide(RecsState.Loading, true, fingerprintCurrent: false, force: false));

    [Fact] public void Force_WhileLoading_Supersedes_TheUsersEscapeHatch()
        => Assert.Equal(RecsAction.Supersede, RecsRefetchPolicy.Decide(RecsState.Loading, true, fingerprintCurrent: true, force: true));

    [Theory] [InlineData(RecsState.Idle)] [InlineData(RecsState.Loaded)] [InlineData(RecsState.Failed)]
    public void Force_NotLoading_Fetches(RecsState s)
        => Assert.Equal(RecsAction.Fetch, RecsRefetchPolicy.Decide(s, true, fingerprintCurrent: true, force: true));

    // Fingerprint: same rows in a different order → same value (a reorder is not a membership change); one row more,
    // fewer, or swapped → different; the context uri participates.
    [Fact] public void Fingerprint_IsOrderInsensitive_AndMembershipSensitive()
    {
        var a = T("a"); var b = T("b"); var c = T("c");
        long ab = RecsRefetchPolicy.Fingerprint("spotify:playlist:p", new[] { a, b });
        Assert.Equal(ab, RecsRefetchPolicy.Fingerprint("spotify:playlist:p", new[] { b, a }));
        Assert.NotEqual(ab, RecsRefetchPolicy.Fingerprint("spotify:playlist:p", new[] { a, b, c }));
        Assert.NotEqual(ab, RecsRefetchPolicy.Fingerprint("spotify:playlist:p", new[] { a, c }));
        Assert.NotEqual(ab, RecsRefetchPolicy.Fingerprint("spotify:playlist:q", new[] { a, b }));
    }

    static Track T(string id) => /* the Wavee.Core Track factory the other Detail tests use */ ;
}
```

`PlaylistExtenderClientTests` (new — there are none today): drive `ExtendAsync` over a scripted `ITransport`
(`ScriptedTransport` from `MutationOpRebaseTests.cs:133`): a 200 with a valid body returns N tracks; a 200 with an empty
body returns an empty list (not an exception); a 500 returns empty; a token cancelled mid-`Request` propagates
`OperationCanceledException` (the client must **not** swallow it — that is what lets `FetchRecs` tell a supersede from a
result). The epoch/CTS handling in `FetchRecs` itself is engine-bound (`post`, signals) and is verified live, not
unit-tested — the policy carries the decision matrix, which is the part that was wrong.

---

## 4. Gates and files

`dotnet build Wavee.slnx` Debug + Release clean (the `Signal<int>` → `Signal<RecsState>` change touches only the five
`_recState` sites at `:180, 2903, 2985-2987, 3002`); `Wavee.Tests` green; live: open an owned playlist, scroll to the
bottom, add two tracks from the recs — the batch refreshes once ~750 ms after the second add (`superseded`/`start`/`ok` in
the log), remove a track — same; throttle or block `spclient.wg.spotify.com` mid-fetch — the spinner turns into "Couldn't
load suggestions" + Refresh within 15 s and Refresh recovers.

| File | Change |
|---|---|
| `Features/Detail/RecsRefetchPolicy.cs` | new (§2.1); add to `Wavee.Tests.csproj` |
| `Features/Detail/DetailTracks.cs` | §2.2 fields + layout effect + unmount; §2.3 `FetchRecs`; §2.4 `RecHeader.Render`, loc literals |
| `Backend/Playlists/PlaylistExtenderClient.cs` | §2.5 start/ok/parse-failed logging, `Parse(uri, bytes)` |
| `Backend/Playlists/PlaylistMutationDiagnostics.cs` | §2.5 six events |
| `assets/loc/en-US.json` | `detail.recsFailed`; the two existing keys become live |
| `Wavee.Tests/RecsRefetchPolicyTests.cs`, `PlaylistExtenderClientTests.cs` | §3 |
