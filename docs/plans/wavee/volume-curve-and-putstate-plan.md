# Volume: perceptual taper + put-state storm — detailed technical plan

Status: **Implemented** — the perceptual taper is `src/apps/Wavee/Backend/Audio/VolumeTaper.cs` (covered by
`src/apps/Wavee.Tests/Audio/VolumeTaperTests.cs`) and the put-state coalescing is
`src/apps/Wavee/Backend/TrailingCoalescer.cs`. The deferred polish in §1.5 (gain ramp, mute-with-memory) is still open.
(Planned 2026-07-06.)

Two independent defects, one slider:

1. Slider 0.1 is far too loud — the slider value is applied as a **linear amplitude** multiplier.
2. One slider drag emits **~31 `put-state (VolumeChanged)` HTTP PUTs** (observed in the log) — one
   per pointer-move, with no coalescing or dedupe on the volume path.

---

## Part 1 — Perceptual volume taper

### 1.1 Current behavior (verified)

- `WasapiRenderer.SetVolume(float v)` stores the slider value verbatim (`WasapiRenderer.cs:120`);
  `Write` multiplies every sample by it (`WasapiRenderer.cs:159-162`).
- Linear amplitude ⇒ slider 0.1 = −20 dB. Human loudness perception is roughly "−10 dB sounds half
  as loud", so −20 dB is perceived as ~¼ loudness — not the ~1/10 the slider promises. That is
  exactly the reported symptom.
- Track normalization gain is applied **separately and earlier**, in the decode path
  (`AudioPlayEngine.cs:39,67,467` — `_gainLinear` from `NormalizationGainDb`). It is genuine dB
  and must stay linear. No interaction with the taper.
- EQ/DSP also runs in the engine, before the renderer multiply. No interaction.

### 1.2 The convergence point (why one edit suffices)

Every volume path terminates in `WasapiRenderer.SetVolume` with the **slider position** (0..1):

| Path | Route |
|---|---|
| UI slider / popup / mute | `PlayerBar.cs:415,841,1021`, `NowPlayingView.cs:164` → `PlaybackController.SetVolumeAsync` → `_host.SetVolume` |
| In-process host | `InProcessAudioHost.cs:45` → `AudioPlayEngine.SetVolume` (`:306`) → renderer |
| Out-of-process host | `SupervisedAudioHost.cs:230` → IPC `set_volume` → `AudioHostServer.cs:78,292` → engine → renderer |
| Host (re)spawn reconcile | `hello.Volume` → `AudioHostServer.cs:152` → engine → renderer |
| Remote controller changed OUR volume (cluster echo) | `PlaybackController.OnProjectionChanged` (`:299`) → `_host.SetVolume` |
| Launch seed (remember-volume) | `LiveSessionHost.cs:147` → projection + host hello |
| Silent fallback host | `AudioHost.cs:152` — no-op, nothing to do |

Everything upstream of the renderer — the `bridge.Volume` signal, `NowPlayingProjection.Volume`,
`ConnectStateBuilder` (× 65535 on the wire), `AppSettings.SavedVolume`, the IPC `VolumeCommand`,
the mute-glyph threshold (`PlayerBar.cs:824`, `v <= 0.001`) — keeps **slider semantics** and is
untouched. The taper is applied exactly once, at the amplitude boundary.

**Wire rule (do not break):** Spotify Connect's device volume is the *slider position* (0..65535);
every client applies its own taper locally. Keeping the wire linear means volume set from the
phone lands on our slider correctly and round-trips unchanged.

### 1.3 Curve choice

| Curve | amp(0.1) | amp(0.5) | Notes |
|---|---|---|---|
| linear (today) | −20 dB | −6 dB | perceptually top-heavy — the bug |
| squared `v²` | −40 dB | −12 dB | mild taper; fallback if cubic feels dead at the bottom |
| **cubic `v³` (chosen)** | −60 dB | −18 dB | the standard audio-taper approximation; exact 0→0, 1→1 |
| librespot log-60dB `10^(3(v−1))` | −54 dB | −30 dB | needs a hard-zero special case at v=0; no advantage over cubic here |

Cubic: allocation-free, branch-free, exact at both endpoints, and the exponent is a single
constant to retune after a listening pass.

### 1.4 Changes

**New file `Wavee/Backend/Audio/VolumeTaper.cs`** (Backend, so `Wavee.Tests` compiles it
automatically via the `Backend\**\*.cs` source-include — `Wavee.Tests.csproj:70`):

```csharp
namespace Wavee.Backend.Audio;

/// <summary>Slider position (0..1) → linear amplitude. Cubic audio taper: perceived loudness tracks
/// the slider (0.5 ≈ −18 dB, 0.1 ≈ −60 dB), exact at 0 and 1. The Connect wire, the persisted
/// setting, and every UI surface keep the untapered slider position — this runs ONLY at the
/// amplitude boundary (WasapiRenderer).</summary>
public static class VolumeTaper
{
    public static float Amplitude(float slider01)
    {
        float v = Math.Clamp(slider01, 0f, 1f);
        return v * v * v;
    }
}
```

**`WasapiRenderer.cs:120`** — the only call-site change:

```csharp
// before
public void SetVolume(float v) => _volume = Math.Clamp(v, 0f, 1f);
// after
public void SetVolume(float v) => _volume = VolumeTaper.Amplitude(v);
```

`_volume` semantics change from "slider" to "amplitude"; it is write-only from outside and the
hot `Write` loop is untouched (still one `float` multiply per sample). `volatile float` publish
is unchanged.

Add `using Wavee.Backend.Audio;` to `WasapiRenderer.cs`. The renderer is compiled into both the
app and the audio-host process from the same source, so the out-of-process path gets the taper
for free — **no IPC contract change** (`VolumeCommand.Volume` stays slider-valued).

**Explicit non-changes** (each would double-apply or corrupt semantics — do not "fix" these):
- `ForwardVolumeAsync` (`PlaybackController.cs:392`) — wire stays `slider × 65535`.
- `ConnectStateBuilder.cs:108` — announced DeviceInfo.Volume stays slider-valued.
- `SupervisedAudioHost._volume`, `hello.Volume`, `VolumeCommand` — slider-valued pass-through.
- `AppSettings.SavedVolume` / `LiveSessionHost` seed / `WaveeApp.cs:118` — slider-valued; no
  migration needed, an existing saved 0.7 now simply *sounds* quieter (correct).
- `AudioPlayEngine._gainLinear` (normalization) and the EQ — independent, linear-dB, untouched.

### 1.5 Deferred polish (explicitly out of scope for this pass)

- **Gain ramp** (~20–50 ms linear interpolation of `_volume` inside `Write`) to eliminate zipper
  steps on fast drags. With 300 ms WASAPI buffers, a drag already quantizes to buffer-fill
  boundaries; ship the taper first and only add the ramp if a fast drag audibly clicks.
- Mute-with-memory (`ToggleMute` restores hardcoded 0.7, `PlayerBar.cs:840`) — separate,
  already annotated in the code as the device-panel pass.

### 1.6 Tests (`Wavee.Tests/Audio/VolumeTaperTests.cs`, xunit v3)

- `Amplitude(0) == 0`, `Amplitude(1) == 1` (exact).
- Strictly monotonic over `linspace(0,1,101)`.
- `Amplitude(0.5)` ≈ 0.125 (−18.06 dB) within 1e-6.
- Clamps: `Amplitude(-0.5) == 0`, `Amplitude(1.5) == 1`.

---

## Part 2 — VolumeChanged put-state storm

### 2.1 Current behavior (verified)

The storm has two compounding layers:

1. **Per-pointer-move commands.** `Slider.Bind`/`Slider.Ranged` invoke their callback on every
   value change during a drag (`PlayerBar.cs:415`, `:1021`, `NowPlayingView.cs:164`), and each
   callback runs the full `SetVolumeAsync` (`PlaybackController.cs:380`):
   `NoteLocalCommand` → `SetLocalVolume` (signal fan-out — wanted, keeps the UI live) →
   `_host.SetVolume` (wanted, live audio) → **`EmitState(EvKind.VolumeChanged)`** (the problem).
2. **No coalescing/dedupe on the publisher.** `DeviceStatePublisher.OnEvent` maps every
   `VolumeChanged` to a `PublishAsync` (`DeviceStatePublisher.cs:107,111`), and the
   `_lastPublishKey` short-circuit applies **only** to `PlayerStateChanged`
   (`DeviceStatePublisher.cs:130`) — identical VolumeChanged publishes go to the wire every time.

Also affected, same shape: when a **remote** device is active, each drag tick fires the dedicated
volume PUT (`ForwardVolumeAsync` → `/connect-state/v1/connect/volume/...`,
`PlaybackController.cs:74-79`) — a per-pointer-move storm against spclient.

Not a cause (checked, no feedback loop): the cluster echo path enters via projection change
(`OnProjectionChanged`, epsilon-guarded at `:299`, never calls `EmitState`), and the settings
write is already debounced by the 2 s poll timer (`WaveeApp.cs:127-133`).

### 2.2 Design: keep audio instant, coalesce the wire

Local audio volume and the UI signal must keep applying per-tick (live feedback while dragging).
Only the *network* effects coalesce:

- **Layer A — epsilon no-op guard** at the intent entry (`SetVolumeAsync`): drop calls where the
  value didn't actually move. Kills same-value repeats (drag stalls, wheel at the rail ends,
  double mute-toggles) before they touch anything.
- **Layer B — leading+trailing debounce** of the wire call (~400 ms quiet period):
  - the **first** change publishes immediately (remote UIs see the drag start right away),
  - subsequent changes inside the window coalesce into **one trailing** publish carrying the
    final value (snapshot built at send time, so it is always the latest state).
  Applied in two places: `DeviceStatePublisher` (local-active put-state) and
  `PlaybackController` (remote-active volume PUT).
- **Layer C — extend the `_lastPublishKey` dedupe** to `VolumeChanged`. Volume is already in the
  key at 1 % resolution (`DeviceStatePublisher.cs:129`); this makes post-debounce echoes of an
  unchanged state free.

Result: a drag = 2 PUTs (leading + trailing) instead of ~31; a single click/wheel-detent = 1.

### 2.3 New shared primitive — `Wavee/Backend/TrailingCoalescer.cs`

One small class used by both call-sites (Backend ⇒ auto-covered by tests):

```csharp
namespace Wavee.Backend;

/// <summary>Leading+trailing coalescer for fire-and-forget side effects. Post() runs the action
/// immediately if the channel has been quiet for the window; otherwise it schedules ONE trailing
/// run for after the window, always executing the latest posted action. Thread-safe; Dispose
/// cancels any pending trailing run.</summary>
public sealed class TrailingCoalescer : IDisposable
{
    readonly int _windowMs;
    readonly Func<long> _now;                                   // injectable clock (tests)
    readonly Func<int, CancellationToken, Task> _delay;         // injectable delay (tests)
    readonly object _gate = new();
    long _lastRunMs;
    Action? _pending;
    CancellationTokenSource? _cts;

    public TrailingCoalescer(int windowMs, Func<long>? now = null,
                             Func<int, CancellationToken, Task>? delay = null) { ... }

    public void Post(Action action)
    {
        lock (_gate)
        {
            long t = _now();
            if (_cts is null && t - _lastRunMs >= _windowMs) { _lastRunMs = t; }   // leading
            else { _pending = action; ArmTrailing(); return; }                     // coalesce
        }
        action();
    }

    void ArmTrailing()   // under _gate: cancel+replace is NOT needed — one timer, latest _pending wins
    {
        if (_cts is not null) return;
        var cts = _cts = new CancellationTokenSource();
        _ = RunTrailingAsync(cts);
    }

    async Task RunTrailingAsync(CancellationTokenSource cts)
    {
        try { await _delay(_windowMs, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        Action? run;
        lock (_gate) { run = _pending; _pending = null; _cts = null; _lastRunMs = _now(); cts.Dispose(); }
        run?.Invoke();
    }

    public void Dispose() { lock (_gate) { _cts?.Cancel(); _cts = null; _pending = null; } }
}
```

Design notes:
- **One timer, latest-wins** — a new `Post` during the window only replaces `_pending`; it does
  not reset the timer. This bounds worst-case wire latency during a long drag to `windowMs`
  (a pure trailing-reset debounce would starve the wire until the drag ends; leading+bounded
  trailing is what remote UIs want).
- The action runs **outside** `_gate` (it does an async HTTP fire-and-forget).
- `_now`/`_delay` injectable ⇒ fully deterministic tests (no real sleeps).
- `Post` after `Dispose` degrades to leading-only behavior — acceptable for shutdown.

### 2.4 Changes, call-site by call-site

**`PlaybackController.SetVolumeAsync` (`PlaybackController.cs:380-395`):**

```csharp
double _lastIntentVolume = double.NaN;                 // last value accepted by SetVolumeAsync
readonly TrailingCoalescer _remoteVolumeTx = new(400); // remote-active volume PUT coalescer

public Task SetVolumeAsync(double volume01, CancellationToken ct = default)
{
    volume01 = Math.Clamp(volume01, 0, 1);
    if (!double.IsNaN(_lastIntentVolume) && Math.Abs(volume01 - _lastIntentVolume) < 0.0005)
        return Done;                                   // Layer A: value didn't move — drop everything
    _lastIntentVolume = volume01;

    _projection.NoteLocalCommand();
    _projection.SetLocalVolume(volume01);              // UI signal — still per-tick, wanted
    if (RouteLocal()) return Local(() => { _host.SetVolume(volume01);   // audio — still per-tick, wanted
                                           EmitState(EvKind.VolumeChanged); });
    var target = _projection.ActiveDeviceId;
    if (_outbound is null || string.IsNullOrEmpty(target)) return Done;
    _remoteVolumeTx.Post(() => _ = ForwardVolumeAsync(target, volume01, CancellationToken.None)); // Layer B (remote)
    return Done;
}
```

- Epsilon 0.0005 < one wire step (1/65535 ≈ 1.5e-5 is too tight for float slider math; 0.0005 is
  half of the 0.1 %-of-range granularity any UI gesture produces, and mirrors the 0.0009 pattern
  already used at `:299`).
- Guard sits **before** `NoteLocalCommand` so no-op drags don't extend the optimistic
  anti-snap-back window artificially.
- `_lastIntentVolume` must also be updated by the cluster-echo path (`OnProjectionChanged:299`
  already tracks `_lastVolume` — set `_lastIntentVolume = vol;` there too) so a remote change to
  X followed by a local drag back to X isn't swallowed.
- The remote-active branch still emits **no** local `VolumeChanged` event (unchanged today —
  put-state describes *our* device; a remote device's volume is its own put-state).
- `ct` is deliberately not captured into the coalesced lambda (the trailing PUT may outlive the
  caller); `EmitState` still fires per accepted tick — the debounce for it lives in the
  publisher (next), which also covers any future non-slider callers.
- Dispose: add `_remoteVolumeTx.Dispose()` to `PlaybackController.Dispose()`.

**`DeviceStatePublisher` (`DeviceStatePublisher.cs`):**

```csharp
// ctor gains: int volumePublishWindowMs = 400, Func<int, CancellationToken, Task>? delay = null
readonly TrailingCoalescer _volumeTx;   // new(volumePublishWindowMs, clock, delay)

// OnEvent — the VolumeChanged arm routes through the coalescer instead of publishing directly:
var reason = ...;                                       // unchanged
if (reason == PutStateReasonKind.VolumeChanged)
    _volumeTx.Post(() => _ = PublishAsync(PutStateReasonKind.VolumeChanged,
        _state.CurrentTrack is not null));              // isActive recomputed at SEND time
else
    _ = PublishAsync(reason, isActive);
```

- `isActive` and the snapshot are computed **inside** the deferred action (`PublishAsync` →
  `BuildSnapshot` already reads live state), so the trailing publish always carries the final
  volume and current play state — no stale-capture bug.
- **Layer C** — widen the dedupe at `DeviceStatePublisher.cs:130`:

```csharp
if (reason is PutStateReasonKind.PlayerStateChanged or PutStateReasonKind.VolumeChanged
    && key == _lastPublishKey) return;
```

  (Key already contains volume at 1 % resolution — `:129`. `NewConnection`/`BecameInactive`
  stay always-send.)
- Ordering: `_messageId` is still allocated under `_gate` at send time, so a trailing volume
  publish that lands after a track-change publish gets a higher mid and a fresher snapshot —
  no regression on the server's last-writer-wins.
- Dispose: `_volumeTx.Dispose()` alongside `_connSub.Dispose()` (`:257`) — a pending trailing
  publish must not fire after teardown.
- `BecameInactive` / logout: no special flush needed — the inactive put carries the device volume
  anyway, and a cancelled pending volume publish is moot once inactive.

**Untouched on purpose:**
- The slider components and `bridge.Volume` signal — per-tick UI updates are the point.
- `SupervisedAudioHost.SetVolume` IPC per accepted tick (`:238`) — local named pipe, cheap, and
  live host audio during the drag is desired. Layer A already removes the duplicate-value calls.
  (Its `ConfigureProcess()` per call is pre-existing and out of scope.)
- The 2 s settings-save poll (`WaveeApp.cs:127`) — already debounced.

### 2.5 Tests

**`Wavee.Tests/Backend/TrailingCoalescerTests.cs`** — deterministic via injected clock+delay
(`TaskCompletionSource` the delay, tick the clock manually):

- First `Post` runs synchronously (leading).
- N rapid `Post`s inside the window ⇒ leading runs once, exactly one trailing runs after the
  delay completes, and it executes the **last** posted action.
- `Post` after the window has elapsed (clock advanced, no pending) ⇒ leading again.
- Long burst spanning multiple windows ⇒ ≤ 1 execution per window (latency bound).
- `Dispose` with a pending trailing ⇒ trailing never runs.

**`Wavee.Tests/Backend/DeviceStatePublisherVolumeTests.cs`** — the publisher's deps are all
injectable (`ITransport`, `IPlaybackState`, `IObservable<string?>`, the `_build` func, `clock`);
add a recording fake transport + a stub state with settable `Volume`:

- 30 `VolumeChanged` events in one window ⇒ exactly 2 transport publishes (leading + trailing),
  and the snapshot passed to `_build` on the trailing one carries the **final** volume.
- Same-key VolumeChanged after the window (volume unchanged at 1 % resolution) ⇒ 0 additional
  publishes (Layer C).
- `PlayerStateChanged` interleaved mid-window ⇒ publishes immediately (not queued behind the
  volume coalescer).
- Dispose with pending trailing ⇒ no publish after dispose.

**Layer A** (controller guard): `PlaybackController` construction is heavy (queue, resolver,
projection, host); cover the guard indirectly via the manual log check below rather than a unit
test — the logic is three lines and the publisher tests already pin the wire behavior. (If a
controller test harness appears later, add: repeated `SetVolumeAsync(same)` ⇒ one host call.)

### 2.6 Manual verification (Chris builds & runs — no agent-run builds)

1. Drag the PlayerBar slider end-to-end: log shows **exactly 2** `put-state (VolumeChanged…)`
   lines (one at drag start, one ≤ 400 ms after the last movement). Audio follows the thumb live.
2. Single click on the volume rail / one mute toggle: **1** line.
3. Loudness: 0.1 ≈ quiet background (−60 dB), 0.5 comfortable (−18 dB), 1.0 identical to today.
   The 0→0.05 region should now have a real "barely audible" zone.
4. Phone → Wavee volume change (cluster echo): our output level changes with the taper applied;
   no put-state feedback loop (echo path never emits `VolumeChanged`).
5. Wavee controlling a remote device: drag ⇒ 2 volume PUTs, remote device lands on the final value.
6. Restart with remember-volume on: restored slider position unchanged vs. today (only loudness
   at that position differs — expected).

---

## Order of work

1. `VolumeTaper` + renderer call-site + `VolumeTaperTests` (self-contained; biggest UX win).
2. `TrailingCoalescer` + its tests.
3. Publisher: coalescer wiring + dedupe widening + dispose + `DeviceStatePublisherVolumeTests`.
4. Controller: Layer A guard (+ `_lastIntentVolume` sync in `OnProjectionChanged`) + remote-PUT
   coalescer + dispose.
5. Listening pass on the exponent (cubic ⇄ squared is a one-constant change in `VolumeTaper`).

Risk notes: Part 1 is one line at a proven chokepoint (rollback = revert that line). Part 2's
only behavioral risk is *delaying* a wire message by ≤ 400 ms — local audio, UI, and persistence
are all untouched paths.
