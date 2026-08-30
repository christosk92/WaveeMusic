# Wavee Lyrics — Line-Transition Timing Rework

> Source: a 20-agent `ultracode` research workflow (web research → code analysis → synthesis →
> adversarial verify → finalize), 2026-07-01. Every proposal below was checked by an independent
> skeptic against the project's hard constraints; the corrections from that pass are folded in.
>
> **One opinionated experience, no flags.** Every constant is a single decided default — no
> `WAVEE_LYRICS_*` env vars, no quality tiers, no per-control toggles.
>
> **Scope:** §2.2–§6 are app-side in `Wavee/Features/Player/LyricsView.cs`. The one engine change
> (§2.1) is small + bounded (`ScrollAnimator`). The render-thread seam / partial-submit (§7) is
> deferred multi-session engine work and out of scope.
>
> **Ground-truth correction (2026-07-01, post-workflow):** the "ANY PushLayer → whole-window layered
> submit" framing the workflow used (constraint #5) is **stale** — `FG_BACKBUFFER_LAYERS` (default ON)
> routes Blur/Opacity/EdgeFade to the cheap back-buffer-direct path; only **Acrylic** forces the
> canvas. BUT the per-layer blur **cost is route-independent**, and the cross-frame blur cache is
> **hard-disabled** (`_blurCache=false`). So §6.1's win is purely the **flicker fix** (no per-frame
> kernel breathing) — the "blur-cache hit for stationary neighbours" benefit is currently moot. §6.2's
> skip-submit benefit still holds at rest while playing (the rail is byte-identical → elided). See the
> `engine-pushlayer-whole-window-cost` memory ground-truth banner.

---

## 1. Diagnosis — why it feels buggy today

Five independent timing systems are uncoordinated; each is individually slightly wrong and the errors compound.

| # | Defect | Site | What goes wrong |
|---|--------|------|-----------------|
| 1 | **No anticipation** | `ResolveLine` (`LyricsView.cs:526`), called @386 | The line flips the instant `nowMs` crosses `StartMs`, and the emphasis springs start **from rest** only then — the line is fully readable ~300ms **after** the vocal onset. Reference players lead the eye ~120–300ms while keeping the wipe on true audio time. |
| 2 | **Two motions desync** | scroll = exp-ease (τ≈110ms, no velocity carry); swell = spring (resp 0.35s, ζ 0.75, bounces) @640-643 | Different shape/duration/settle frame; only scale bounces. The eye reads a quick glide **then** a slower springy swell — two stacked events, not one rise into focus. |
| 3 | **Scroll re-chases from standstill** | ProgrammaticMode exp-ease (`ScrollAnimator.cs:333-340`) | No velocity across re-targets; in dense sections the offset never reaches the focal band before the next retarget → the list perpetually trails the song. |
| 4 | **Hard clock re-anchor** | `OnFrame:373-384` | `nowMs` re-seated on **every** ~1Hz IPC snapshot; a delayed/corrected snapshot snaps `nowMs` → the active line jumps (even backward) and the wipe lurches/retreats. |
| 5 | **No gap state + wipe/line boundary mismatch** | `ResolveLine` ignores `EndMs`; wipe is syllable-timed, line is `StartMs`-timed | Instrumental gap → finished line stays 100% lit + fully wiped (frozen). Wipe edges don't track the voice (sits at 0 then jumps; saturates and sits). |
| 6 | **Blur-sigma churn (the flicker)** | held glow @440-442 (raw `nowMs`, 0.05 delta gate); per-line DoF spring @643 | Held glow steps irregularly under timer jitter → irregular `PaintDirty` on a self-blur node → whole-window layered re-submit + breathing. Per-line DoF: `DofSigma` is already an integer-distance **step**, so the spring just manufactures intermediate sigmas every settle frame, stepping a heavy kernel = the during-a-line shimmer. |

The fix is one coordinated motion model (§2) fed by one stable clock (§3), with the eye/voice split made explicit (§4), the wipe reconciled (§5), and both blur churns killed (§6).

---

## 2. The new timing model — one coordinated motion

**Principle (Apple/AMLL/SwiftUI `.smooth`):** a line transition is **one physical event**. Position (scroll) travels on a near-critical, bounce-free spring; scale swells on a slightly-underdamped spring; both share one ~0.55s timeline and start on the same `OnFrame`.

### 2.1 Scroll: velocity-continuous critically-damped — **land it in the engine**

> The original app-side `OffsetY` stepper was **dropped** by verification: (1) **paused freeze** — `ArmScroll` keeps the engine animator (hence the frame loop) awake until settle; an app spring stepped only in `OnFrame` advances ~one 16ms step on a paused seek and freezes partway. (2) **dual-writer fight** — a user wheel/fling arms the engine animator while the app spring also writes `OffsetY` → stutter.

Keep the `ArmScroll` handoff in `ScrollActiveIntoView` (no app-side stepper). Make the **engine** integrator velocity-continuous and critically-damped (`ScrollAnimator.Tick`, ProgrammaticMode branch ~333-340), reusing `ScrollState.FlingVelocity` as the carried velocity:

```csharp
// ProgrammaticSpringHalflifeMs = 95f  (≈ old τ=110ms feel, but velocity-continuous + bounce-free)
// y = 2*ln2 / halflife  — the engine's own critically-damped regime; closed-form in dt ⇒ dt-deterministic
const float hl = 0.095f;
float y = 1.3863f / hl;
dt = MathF.Min(dt, 0.05f);              // clamp coalesced frames
float j0 = off - tgt;
float j1 = vel + j0 * y;
float e  = MathF.Exp(-y * dt);
off = e * (j0 + j1 * dt) + tgt;
vel = e * (vel - j1 * y * dt);
if (MathF.Abs(off - tgt) < 0.5f && MathF.Abs(vel) < settle) { off = tgt; vel = 0f; }
```

Term-for-term identical to `Generators.EvalSpring`'s critically-damped regime (verified). Fixes the dense-section re-chase for free; keeps `HasActive`/`WakeReasons.ScrollAnim` alive (paused travel completes) and the single-writer guarantee. Keep the first-show `RestorePending` and large-jump instant-snap branches.

**Gates:** `dotnet build src/FluentGpu.slnx` clean + VerticalSlice "ALL CHECKS PASSED" (dt-determinism preserved). Reconcile the `ScrollAnimator` notes in `design/` + run `check-canon.ps1` (the `ProgrammaticEaseTauMs` semantics change).

> **Escape hatch:** if the engine must stay untouched, ship the emphasis half (§2.2) only — do **not** substitute the app-side stepper.

### 2.2 Emphasis: retune per-line springs to co-settle (`LyricLineView.Render` ~640-643)

Same persistent-node rebase path (keyed `DepKey.From(dist)`, re-rendered in place via `_activeLine.Value`) — no remount, no `RequestRerender`, zero all-lines-pulse risk.

- **Scale** (`ScaleX`/`ScaleY`): `SpringParams.FromResponse(0.55f, 0.90f)` — near-critical, deliberate, a whisper of bounce.
- **Opacity**: `SpringParams.FromResponse(0.55f, 1.0f)` — critical, no bounce on dim. (Do **not** leave on `Default` while scale moves to 0.55s.)
- **BlurSigma**: **delete the spring** — §6.1 makes it a static bucketed step. (Depends on §6.1 landing together.)

---

## 3. Clock dejitter — smooth, monotonic re-anchor

Replace the hard re-anchor (`OnFrame:373-379`) with deadband / clamped-slew / snap + a monotonic-while-playing guard. Pure scalar math in `OnFrame`, `Peek()` only, zero alloc. With the deadband, steady-state extrapolation stays byte-identical to today (~16ms/frame) — only backward jumps are suppressed.

**State fields (replace the four clock fields @32-34):**
```csharp
long  _baseWall;                    // monotonic wall anchor (Environment.TickCount64)
long  _basePos;                     // playback position at _baseWall
float _offset;                      // additive slew correction
long  _lastAuthMs = long.MinValue;  // last IPC PositionMs reacted to
long  _lastDisplay;                 // last displayed nowMs (monotonic guard)
```

**One helper so every re-anchor site seeds identically** (the original missed `PrepareDocument`/`SeekToLine`/`ClearDocument`):
```csharp
void RebaseClock(long pos)
{
    _baseWall = Environment.TickCount64;
    _basePos = pos; _offset = 0f;
    _lastAuthMs = pos; _lastDisplay = pos;   // guard takes the new (possibly backward) position now
}
```
- `PrepareDocument` → `RebaseClock(posMs)` (replaces 252-254).
- `SeekToLine` → `RebaseClock(ms)` (replaces 355-357) — fixes a backward click-seek frozen by the guard. (`RebaseClock` sets `_lastAuthMs=ms`, so the synchronous `PositionMs.Value=ms` write doesn't trip the snap branch next frame.)
- `ClearDocument` → `_lastAuthMs = long.MinValue; _lastDisplay = 0L;` (line 267).

**`OnFrame` clock block (replaces 368-384):**
```csharp
long auth    = b.PositionMs.Peek();
long wall    = Environment.TickCount64;
bool playing = b.IsPlaying.Peek();
if (auth != _lastAuthMs)
{
    _lastAuthMs = auth;
    long predicted = _basePos + (wall - _baseWall);
    long err = auth - (long)(predicted + _offset);
    long ae  = err < 0 ? -err : err;
    if (ae <= 12)        { /* deadband: ignore jitter */ }
    else if (ae <= 250)  { _offset += err * 0.5f; }   // slew: ~half per snapshot, no visible jump
    else                                              // snap: real seek/transfer
    {
        _baseWall = wall; _basePos = auth; _offset = 0f;
        _lastDisplay   = auth;        // bypass monotonic guard for a legitimate (maybe backward) seek
        _scrollSnapped = false;       // next ScrollActiveIntoView does the INSTANT-jump latch
        ResetWipeThrottle();
    }
}
long nowRaw = playing ? (long)(_basePos + (wall - _baseWall) + _offset) : auth;
long nowMs  = playing ? Math.Max(nowRaw, _lastDisplay) : nowRaw;   // monotonic only while playing
_lastDisplay = nowMs;
long wallMs = wall;   // name reused downstream
```
**Do not** write `_scrollVel`/`_stickyLine` on snap — they don't exist; `_scrollSnapped=false` is the correct lever. Thresholds: deadband 12ms, slew 12–250ms @ 0.5, snap >250ms.

---

## 4. Line-switch timing — anticipation, debounce, gap

### 4.1 Lead-in with a **split index**

> A one-line `active = ResolveLine(nowMs + Lead)` is wrong: `active` drives both emphasis/scroll **and** the wipe (`_lineNodes[active]` @398, `ComputeSplit(doc.Lines[active], nowMs)` @403). Lead-shifting it retargets the wipe to the not-yet-singing line. **Two indices.**

```csharp
const long LeadMs = 140;                                    // safe 100-500ms window, > one 16ms frame
int emphasisLine = ResolveLine(doc.Lines, nowMs + LeadMs);  // drives _activeLine + ScrollActiveIntoView
int voiceLine    = ResolveLine(doc.Lines, nowMs);           // drives wipe/glow, on TRUE audio time
```
Replace every `active` in the wipe/glow block (397–446) with `voiceLine`; keep `ComputeSplit`/`ComputeHeldGlowSigma` on raw `nowMs`.

**Wipe node ownership:** the wipe renders only where `_index == _activeLine.Value` (gated `isActive` @651). During the lead, emphasis is on N+1 but the wipe must paint on N. Add `readonly Signal<int> _voiceLine = new(-1);`, set it in `OnFrame`, pass to `LyricLineView`, and gate the wipe branch on `(_index == active || _index == _voiceLine.Value)` while keeping emphasis springs on `active`. In-place via signal rebase, zero alloc.

### 4.2 Sticky index — optional micro-opt
Keep `_stickyLine` only as an `O(log n)→O(1)` shortcut. **Drop the "prevents double-retarget" rationale** — a same-index re-resolve already no-ops (`Signal.Value` equality @34; `activeChanged` vs `_activeLine.Peek` @387). Low priority.

### 4.3 Instrumental-gap state — word-by-word only, via a parallel signal

> Verified traps: `(nextStart − EndMs) >= 4000` never fires for line-synced lyrics (`DeriveEnds` sets `EndMs = nextStart`, delta 0). `active = -2` slams **every** line to dist=6 (`@631 dist = active < 0 ? 6 : …`) = heavy all-lines DoF churn (constraint #7) + layered spike (#5).

```csharp
long sungOutPoint = line.IsWordByWord && line.Syllables.Count > 0
    ? line.Syllables[^1].EndMs : line.EndMs.GetValueOrDefault();
long nextStart = voiceLine + 1 < count ? lines[voiceLine + 1].StartMs : long.MaxValue;
bool interlude = nowMs >= sungOutPoint
              && (nextStart - sungOutPoint) >= 4000      // AMLL/Apple 4s threshold
              && nowMs < nextStart - 250;                // pre-arm next line 250ms early
```
Line-synced → `sungOutPoint == nextStart` → `interlude == false` (correct: no word timing, don't fake it). Encode via `readonly Signal<bool> _interlude = new(false);`; in `Render`, when `isActive && _interlude.Value`, retarget **only this line's** springs to a sung-out-dimmed target (scale ~0.92, opacity ~0.55, blur `DofSigma(1)=1`), folding the bool into the key: `DepKey.From(dist, _interlude.Value ? 1 : 0)`.

---

## 5. Karaoke wipe & syllable easing

### 5.1 Small **positive** wipe lead (`OnFrame` between split-compute @403 and quantize @408)

> The original `-0.12 + 1.12*rawSplit` is a **lag + dead-zone** (verified numerically), the opposite of a lead. `GlyphWipe.Split` is a single boundary point with a symmetric soft band (`GlyphRenderer.cs:272`); a large `+lead` renders glyphs fully-sung ahead of the voice. Keep it tiny.

```csharp
float split = LyricLineView.ComputeSplit(doc.Lines[voiceLine], nowMs);
const float WipeLead = 0.04f;                            // ~4% ahead; do NOT exceed ~0.06
if (split > 0f && split < 1f) split = Math.Clamp(split + WipeLead, 0f, 1f);
float runW = scene.AbsoluteRect(line).W;
if (runW > 1f) split = MathF.Round(split * runW) / runW; // pixel-quantize AFTER the lead
```
Body stays linear. Mirror onto the glow node's wipe.

### 5.2 Dropped — leave `ComputeSplit` and `soft` unchanged
- **Completion-guarantee tail ramp:** structural no-op — `ComputeSplit` already hits 1.0 the instant `now >= lastSyl.EndMs`; the ramp's guard opens at that same instant (`Math.Max(1.0, ramp)` inert), and a departing line's wipe is never recomputed.
- **Constant-em feather relocation:** `runW` isn't in scope at `Render()` time; relocating to `OnFrame` adds per-frame byte churn for a second-order difference.

---

## 6. Rendering optimizations

| Lever | Win | Feasibility | Verdict |
|---|---|---|---|
| **6.1 Static bucketed DoF sigma** (delete BlurSigma spring; static element `Blur`) | Kills during-a-line DoF breathing (#1 flicker); stationary neighbours hit the blur cache | app-side | **DO** |
| **6.2 Quantize held-glow sigma to 0.25 grid** | Held-note hold phase fully skip-submits; removes sustained-note shimmer | app-side | **DO** |
| 6.3 One focus-band variable-σ blur | pass-count, unproven | needs new non-separable pipeline | **DROP** |
| 6.4 Skip-submit while scrolling | cut whole-window resubmit while moving | needs deferred seam | **DEFER** |
| 6.5 Re-enable blur pin cache | none | app-side (non-action) | **KEEP OFF** |

### 6.1 Static bucketed DoF sigma (`LyricLineView.Render`)
`DofSigma` is an integer-distance step, so the spring only manufactures intermediate sigmas during the settle → `TransformDirty` defeats skip-submit and the raw sigma misses the compositor blur-cache pin every frame (kernel breathing).
1. **Delete** @643 `UseSpring(AnimChannel.BlurSigma, …)`.
2. Set `Blur = LyricsFx.DofSigma(dist)` as a static property on the **outer `BoxEl`** row root (@689) on the dimmed branch (active line keeps `Blur` unset). This is the node the spring drove (`HostNode`); the reconciler re-asserts `paint.BlurSigma = b.Blur` each reconcile, and with no spring nothing overrides it.

> Not `_lineNodes`/`OnFrame`: for a dimmed line `_lineNodes[i]` is the inner `TextEl`, and an imperative `NodePaint` write is clobbered by `Reconciler.cs:1942`.

**Honest accounting:** between line changes the line isn't reconciled → stable hash → blur-cache **hit** (Gaussian passes skipped), no breathing. It does **not** restore skip-submit during the settle (kept scale/opacity springs + scroll keep `AnyTransformWrote` set). The dist 7→6 boundary hard-pops blur 0→5 in one frame (acceptable, far periphery). Keep ScaleX/ScaleY/Opacity as springs.

### 6.2 Quantize held-glow sigma (`ComputeHeldGlowSigma`)
Snap the **result** to a 0.25 grid (drop the "snap envelope phase t" alternative — chunky on the steep rise):
```csharp
static float Q(float s) => MathF.Round(s * 4f) / 4f;   // 0.25 bucket
```
Apply to all three returns: `Q(baseSigma)` on no-bloom paths (745-756, 761), `Q(baseSigma + peakExtra*env)` on the bloom path (759). Leave the call-site 0.05 delta compare as-is (never re-trips on a held frame). `baseSigma` 4f/6f already sit on the grid. §3's slew further stabilizes the input `nowMs`.

---

## 7. Dropped / out-of-scope

- **App-side `OffsetY` spring stepper** → replaced by the engine change (§2.1); freezes when paused + dual-writer fight.
- **6.3 focus-band variable-σ blur** → a distance-ramped σ is **not separable**, so it's a genuinely new GPU pipeline + LayerKind + DrawList API + compositor + headless goldens + Metal port = deferred engine work (#6); also changes the look (index-distance ≠ pixel-distance; the active line's animated glow can't live in a sharp band hole) and re-blurs a larger area each frame (#7). Win unproven (rail blur ~0.06ms).
- **6.4 skip-submit while scrolling** → needs damage-driven partial submit + the render-thread seam (both deferred multi-session). The `recordStats.Damage` rect is only the acrylic backdrop-cache region, not a scissor/partial-submit path. Do **not** attempt app-side.
- **6.5 blur pin cache** → keep `_blurCache=false`; the region-pin win was never measurable (rail re-blurs ~0.06ms) and it's the likeliest heavy-DoF flicker source.
- **Wipe completion ramp / constant-em feather** → §5.2.

---

## 8. Implementation checklist (smallest-risk-first)

Throughout: never subscribe to `PositionMs.Value`; never `RequestRerender()` on a line change; always pixel-quantize per-frame values before write. After each, `dotnet build src/apps/Wavee/Wavee.csproj -c Debug` + screenshot-verify on a word-by-word track.

1. **§6.2** quantize held-glow sigma. *Lowest risk, immediate flicker win.*
2. **§6.1** static bucketed DoF sigma (+ land §2.2 emphasis retune with/after it). *Kills the #1 flicker.*
3. **§3** clock dejitter (fields + `RebaseClock` + 3 sites + `OnFrame` block). *Drop `_scrollVel`/`_stickyLine`.*
4. **§2.2** emphasis spring retune (depends on step 2).
5. **§4.1** lead-in split index (`emphasisLine`/`voiceLine` + `_voiceLine` signal + wipe gate). *Most code paths — do after the clock is stable.*
6. **§5.1** positive wipe lead.
7. **§2.1** engine scroll spring (`ScrollAnimator.Tick`). *VerticalSlice + dt-determinism gate + canon reconcile.* (Skip ⇒ ship 1–6 only; never the app-side stepper.)
8. **§4.3** instrumental-gap state (`_interlude` signal; word-by-word only; depends on `voiceLine`).
