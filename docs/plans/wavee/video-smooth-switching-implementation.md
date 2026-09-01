# Smooth video switching — APP implementation plan (WaveeMusic)

Sibling engine plan: `C:\wavee\fluent-gpu\docs\plans\video-smooth-switching-implementation.md`. This plan builds ONLY
after the engine plan has landed (warm-engine `OpenAsync` switch, non-blocking pump, mounted-surface poster overlay,
`DrmConfig.SourceDescriptor`).

## Problem (verified file:line)

- **A1** Duplicate resolve per toggle: `PlaybackBridge.CommitVideoSurface` (:455-479) fires BOTH `RequestKindRefresh`
  and `RequestPopOutSource`; reuse check races → 2 full resolve chains (+`RecomputeHasVideo` as a 3rd trigger).
- **A2** Up to 5 sequential network RTTs per resolve, deliberately uncached (`SpotifyVideoManifestResolver`
  :91-123 four sequential tiers + manifest GET).
- **A3** `VideoLoadPump` awaits `TeardownAsync` (5 s bound over a native 3 s join) before `BuildAndOpenAsync` —
  wedged native player = guaranteed 5 s dead-stop at every boundary.
- **A4** No next-track video preload: `PlaybackController.cs:3148` disables prepared-next when current is video.
- **A5** Controller `_lock` held across the whole video load (`RefreshCurrentMediaKindAsync` :586-:613); ~40 transport
  verbs queue → presses dead for seconds.
- **A6** `PlayerChanged(null)` on every switch unmounts the `MediaPlayerElement`, then remounts under a NEW stage key
  (`DockedVideoSurface.cs:347`); nothing pumps MF in the window.
- **A7** Card mounts at 16:9 fallback then re-lays-out the rail when `NaturalSize` arrives seconds later.
- **A8** `CompositeVideoResolver.ResolveAsync` runs `File.Exists` on the UI thread (UNC can block seconds).
  (SettingsPage roster IO is already off-thread — no change there.)
- **A9** Zero timing instrumentation on the video path (audio stack stopwatches every stage — the pattern).

## Cross-plan contract (pinned)

- Switch API = `MediaPlayer.OpenAsync(source, opts)` on a **long-lived player**; the app keeps ONE `MediaPlayer` per
  surface and never disposes/recreates per track. `MfMediaPlayer.OpenAsync` no longer throws for open failure (errors
  via `Player.Error`).
- Per-source DRM payload: `DrmConfig.SourceDescriptor = src.DrmDescriptor` (SEEDED in the engine) — the one warm
  `MfMediaPlayer(new ProtectedMediaBackend(defaultRelay: null, descriptor: null))` plays clear + DRM + local files.
- `PopOutVideoSource.NaturalWidth/NaturalHeight` — SEEDED already.
- Call `ProtectedMediaBackend.WarmupNative()` once at startup idle.
- DRM→DRM boundaries still pay CDM re-key + license POST (native session is create-per-open); measured, not hidden.

## Target sequence — video→video track change (~0 network, no unmount, no lock stall)

```
[UI]        Next → PlaybackController.LocalNextAsync → _lock → session.Next()
[_lock]     LoadAndPlayCurrentAsync: MetaResolver → SwitchCurrentMedia (same kind, no host swap)
[_lock]     StartVideoLoadDetached: Emit(TrackChanged), SchedulePreparedNext, spawn RunVideoLoadAsync, RETURN
            └── _lock RELEASED — hold ≈ meta resolve only
[pool]      bridge.ResolveVideoSourceForPlaybackAsync → memo HIT (prefetched during prev track) → 0 RTT
[pool]      FluentVideoMediaHost.LoadVideo → pump.Request
[pump]      ApplyAsync → VideoSwitchPolicy → Switch → SwitchInPlaceAsync: NO teardown, NO PlayerChanged(null);
            live.OpenAsync(source, opts) on the warm player
[UI]        surface never unmounts: stage key = "gen:"+Generation; old frame + Loading overlay
[ticker]    first progress → FirstFrame(key) → bridge.VideoLiveSourceKey → overlay drops (crossfade feel)
```

`PlayerChanged(null)` fires only for Stop (video off / swap to audio), dispose, and faulted-player rebuild.

## 1. VideoLoadPump + FluentVideoMediaHost (A3, A6)

- `VideoLoadPump` ctor: `(Func<long, Task> clearAsync, Func<TSource, long, Task> applyAsync, WaveeLogger log)`. The
  load branch awaits `_applyAsync(next, epoch)` directly — no teardown step, no `_isAlreadyLive` probe (deleted).
  Serialization/coalescing/epoch contract unchanged.
- NEW pure class `VideoSwitchPolicy` (`SpotifyLive/Audio/VideoSwitchPolicy.cs`):

```csharp
public enum VideoSwitchAction { None, SeekOnly, Switch, Rebuild }
public readonly record struct VideoSwitchInput(
    bool HasPlayer, bool LiveFaulted, string LiveKey, string RequestKey, long StartAtMs);
public static class VideoSwitchPolicy
{
    public static VideoSwitchAction Plan(in VideoSwitchInput i) =>
        !i.HasPlayer || i.LiveFaulted ? VideoSwitchAction.Rebuild
        : string.Equals(i.LiveKey, i.RequestKey, StringComparison.Ordinal)
            ? (i.StartAtMs > 0 ? VideoSwitchAction.SeekOnly : VideoSwitchAction.None)
        : VideoSwitchAction.Switch;
}
```

- `FluentVideoMediaHost`: ONE long-lived `MediaPlayer` built for all families (`BuildProtectedPlayer`/clear split
  collapses; DRM payload travels on the source via `DrmConfig.SourceDescriptor`). `ApplyAsync` dispatches on the
  policy; NEW `SwitchInPlaceAsync(req, epoch)`: extract shared `ResetPerLoadState`/`BuildMediaSource` from
  `BuildAndOpenAsync`, `RetractLiveWindow()`, signal Buffering, `StartTicker()`, then
  `live.OpenAsync(source, OpenOptionsFor(src)).AsTask().WaitAsync(OpenTimeoutMs)`. Timeout → watchdog owns the load.
  Any other exception → log + ONE bounded degrade to teardown+rebuild. Log `video-host switch ok key=… switchMs=…`.
- NEW `event Action<string>? FirstFrame` raised from `Tick` on first demonstrable progress per source, with
  `video-host first frame key=… sinceLoadMs=…`.
- `TeardownAsync`/`Stop`/`DisposeAsync` unchanged (the only `PlayerChanged(null)` producers). `IsAlreadyLive` deleted.

## 2. PlaybackBridge (A1, A2-consumer, A4-half)

- **Dedupe:** delete the `RequestPopOutSource` fire in `CommitVideoSurface` (:478). `RecomputeHasVideo`'s kick stays.
- **Memo:** NEW engine-free `SingleFlightMemo<T>` (`App/SingleFlightMemo.cs`: keyed single-flight + 10-min TTL +
  `Invalidate`/`Clear`, injectable clock). `ResolveMemoizedAsync(uri, ct)` fronts `ResolveVideoSource` for ALL
  consumers; the shared flight runs on `CancellationToken.None` (a cancelled caller detaches, never kills the flight).
- `ResolveVideoSourceForPlaybackAsync` gains a `_videoResolveGen` fence (its "controller serializes under one lock"
  premise dies with the lock restructure) — publish onto `PopOutVideoSource` only if still current generation.
- **Prefetch:** `PrefetchVideoSource(trackUri)` — dedup'd, CTS-cancelled on preview change, skipped on metered
  (`NetworkPolicy.ShouldDeferPrefetch`, same gate as audio warm); on success `VideoCdnWarm.WarmInit(src)` (NEW
  `App/VideoCdnWarm.cs`: fire-and-forget init-segment GET / clear HEAD, body discarded — DNS/TLS/edge warm). Log
  `video prefetch <uri>: ready key=… elapsed=…ms`.
- NEW `Signal<string> VideoLiveSourceKey` ("" = none) + `NotifyVideoFirstFrame(key)` (posted to UI).
- Invalidation: `InvalidateVideoSource`/`ApplyVideoMediaEnded` also evict the memo; logout `ClearVideoResolveMemo()`.
- Wiring in `LiveConnect.WireVideoMedia`: `Controller.PrefetchVideo = t => bridge.PrefetchVideoSource(t.Uri);`
  `_videoHost.FirstFrame += k => bridge.NotifyVideoFirstFrame(k);`.

## 3. PlaybackController (A4, A5)

- Extract the audio tail of `LoadAndPlayCurrentAsync` into `LoadAudioCurrentLockedAsync(...)` (byte-identical body;
  caller holds `_lock`).
- Video branch → `StartVideoLoadDetached(...)`: under the lock only Emit TrackChanged + `MaybeStartContinuationFetch`
  + `SchedulePreparedNext("video-start")` + spawn `RunVideoLoadAsync`, return true. **Resolve/open never runs under
  `_lock`.** `RunVideoLoadAsync` is `_contextGeneration`-fenced at every re-entry; no-playable-video → audio fallback
  re-enters the lock for the state mutation only (`SwitchHost(_audioHost)` + `LoadAudioCurrentLockedAsync`).
- `SchedulePreparedNext`: stop zeroing the preview when current is video; when the upcoming playable will play as
  video, call NEW hook `public Action<Track>? PrefetchVideo`. Audio prepare stays video-fenced per
  `PreparedNextPolicy`.
- `RefreshCurrentMediaKindAsync` keeps its shape — hold now spans MetaResolver + kickoff only.

## 4. Resolver (A2, A7-half, A8, A9)

- `SpotifyVideoManifestResolver.ResolveManifestIdAsync`: tiers 1+2 collapse into ONE batched
  `GetExtensionsWithHeadersAsync` call (TrackV4 + VIDEO_ASSOCIATIONS in one request). Stopwatch-stamped
  `[video] … elapsed=…ms` logs on every tier + the manifest GET (mirroring audio `fast-resolve`). Rewrite the
  "deliberately UNCACHED" ctor doc — caching lives in the bridge memo; resolver stays stateless.
- Set `NaturalWidth/NaturalHeight` on the returned `PopOutVideoSource` from the manifest's highest video profile.
- `CompositeVideoResolver.ResolveAsync` becomes genuinely `async`; `svc.Decide` runs in `Task.Run` (File.Exists off
  the UI thread).

## 5. Surfaces (A6, A7)

- Stage key = player identity only: `stageKey = "gen:" + binding.Generation` (drop `src?.Key`) in
  `DockedVideoSurface`, `InWindowVideoPip`, `PopOutVideoWindow`, `VideoFullscreenSurface` (audit each). The `:t0/:t1`
  transport suffix stays.
- Switching overlay: `BuildVideoArea` keeps the stage mounted + pumping while
  `src.Key != b.VideoLiveSourceKey.Value`, overlaying `LoadingOverlay()` in a ZStack — the engine holds the previous
  frame, so a skip reads as a crossfade. `VideoSurfaceMount` unchanged.
- Aspect at mount: docked/PiP fit effects fall back to `PopOutVideoSource.{NaturalWidth,NaturalHeight}` when the
  player hasn't reported — `DockedVideoHeight` right at mount; `NaturalSize` becomes a confirm. No LayoutTransition on
  the docked card (existing rule).

## 6. Tests

- NEW: `SingleFlightMemoTests` (one flight per key under concurrency; TTL; caller-cancel survives; invalidate
  mid-flight; failure not cached); table-driven `VideoSwitchPolicyTests`. Add pure files to `Wavee.Tests.csproj`
  includes (next to `VideoLoadPump.cs`/`VideoStartWatchdog.cs`).
- CHANGED: `VideoLoadSupersessionTests` rewritten for the clear/apply pump contract (latest-wins + epoch staleness
  preserved; add "apply for a new key does not invoke clear").

## 7. Work split (disjoint files; only the orchestrator builds/tests/launches)

- **H — host/pump:** `SpotifyLive/Audio/{FluentVideoMediaHost,VideoLoadPump}.cs`, NEW `VideoSwitchPolicy.cs`,
  `Wavee.Tests/VideoLoadSupersessionTests.cs`, NEW `VideoSwitchPolicyTests.cs`, csproj includes.
- **C — controller:** `Backend/PlaybackController.cs` + controller tests.
- **B — bridge/resolvers/wiring:** `App/PlaybackBridge.cs`, NEW `App/SingleFlightMemo.cs` + tests,
  `App/CompositeVideoResolver.cs`, `SpotifyLive/SpotifyVideoManifestResolver.cs`, `SpotifyLive/LiveConnect.cs`,
  `SpotifyLive/LiveSessionHost.cs`, NEW `App/VideoCdnWarm.cs`.
- **S — surfaces:** `Features/Video/{DockedVideoSurface,InWindowVideoPip,PopOutVideoWindow,VideoFullscreenSurface}.cs`.

## 8. Verification

1. `dotnet build` Debug + Release clean (after the engine plan landed in `..\fluent-gpu`).
2. `dotnet test src/apps/Wavee.Tests` green (baseline + new suites).
3. ONE purposeful live run (deep-link nav + PrintWindow): music-video playlist → toggle video on → Next ×3 → off/on →
   video→audio→video. Per boundary: exactly one `[video] resolve begin` per (track, gen); `video prefetch … ready`
   BEFORE the boundary from the 2nd skip on; `video-host switch ok … switchMs=…` with no teardown lines between
   video→video skips; `video-host first frame … sinceLoadMs=…` (< ~1.5 s DRM, < ~500 ms clear); `docked cap fit …
   natural=WxH` once per source (no 16:9→real double line); Pause during a boundary logged within ~50 ms.

## 9. Risks

DRM→DRM still rebuilds the native session (bounded, off-lock; `SwitchInPlaceAsync` catch degrades to rebuild if the
CDM wedges on re-key). Memo staleness vs signed CDN URLs (10-min TTL + eviction on open failure). "No video" surfaces
slightly later (outside the lock) — capped at one occurrence per playable by memo + dead-uri latch. `RunVideoLoadAsync`
after takeover — generation-fenced at every re-entry.
