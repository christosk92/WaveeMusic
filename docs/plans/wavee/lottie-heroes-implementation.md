# Lottie heroes for the setup wizard

Approved 2026-09-02. Copied verbatim (§1–§7, §9) from the approved cross-repo plan
`logs-are-completely-useless-crispy-globe.md`; §8 (agent sequencing) is dropped here since it only mattered while
the work was in flight. The engine half (`FluentGpu.Lottie`, `FluentGpu.Controls.LottieView`) landed in the sibling
`fluent-gpu` checkout and is documented there.

## 1. Architecture

```
JSON bytes ─LottieParser─▶ LottieDocument ─LottieCompiler─▶ LottiePlan ─LottieView─▶ Element tree + AnimEngine.Keyframes
            (Utf8JsonReader,   (immutable model)             (immutable; PathData      (Controls; per mount: Recolor,
             Engine/Lottie/)                                   minted once; Keyframe[]    From/To/Loop, size, reduced motion)
                                                               tracks; cached per LottieSource)
```
- `FluentGpu.Engine/Lottie/` (ns `FluentGpu.Lottie`): `LottieDocument.cs`, `LottieParser.cs`, `LottieGeometry.cs`, `LottieCompiler.cs`, `LottiePlan.cs`, `LottieSource.cs`. Engine-only → unit-testable in `FluentGpu.Engine.Tests`.
- `FluentGpu.Controls/LottieView.cs`: `LottieView.Create/Preload`, `LottieOptions`, private `LottieViewComponent`.
- `FluentGpu.Engine/Foundation/Easing.cs`: new `Easing.Hold` (step-end) — one enum member + one arm in `Easings.Ease` (`:374`). Check `docs/design/SPEC-INDEX.md` for the owning canon doc and run `check-canon.ps1`.
- **No renderer / scene / reconciler change.**

## 2. Data model (`LottieDocument.cs`, `LottiePlan.cs`, `LottieSource.cs`)

```csharp
public readonly record struct LottieEase(float OutX, float OutY, float InX, float InY, bool Hold)
{ public EasingSpec ToSpec() => Hold ? EasingSpec.Named(Easing.Hold) : EasingSpec.CubicBezier(OutX, OutY, InX, InY); }
public sealed class ScalarKey { public float T, Value; public LottieEase Ease; }            // Ease = segment THIS→next
public sealed class Vec2Key   { public float T; public Point2 Value, To, Ti; public LottieEase EaseX, EaseY; }
public sealed class PathKey   { public float T; public LottieBezier Shape; public LottieEase Ease; }
public sealed class LottieBezier { public Point2[] V = [], I = [], O = []; public bool Closed; }  // I/O relative to V
public sealed class AnimScalar { public float Static; public ScalarKey[]? Keys; public bool IsAnimated => Keys is { Length: > 1 }; }
public sealed class AnimVec2   { public Point2 Static; public Vec2Key[]? Keys; … }
public sealed class AnimPath   { public LottieBezier Static = new(); public PathKey[]? Keys; … }
public sealed class LottieTransform { AnimVec2 Anchor, Position, Scale(100,100); AnimScalar? PositionX, PositionY; AnimScalar Rotation, Opacity(100); }
public enum LottieLayerType : byte { Precomp = 0, Solid = 1, Image = 2, Null = 3, Shape = 4, Text = 5, Unknown = 255 }
public sealed class LottieLayer { int Index, Parent = -1; string Name; LottieLayerType Type; float InPoint, OutPoint, StartTime, Stretch = 1;
                                  bool Hidden, IsMatteSource; byte MatteMode; bool HasDropEffect; LottieTransform Transform; LottieShape[] Shapes; string? RefId; float Width, Height; }
public enum LottieShapeType : byte { Group, Path, Rect, Ellipse, Fill, GradientFill, Stroke, GradientStroke, Trim, Transform, Merge, Repeater, Unknown }
public sealed class LottieGradient { bool Radial; Point2 Start, End; (float Off, ColorF C)[] Stops; ColorF MidStop; }
public sealed class LottieShape { LottieShapeType Type; string? Name; bool Hidden; LottieShape[] Items; LottieTransform? GroupTransform; AnimPath? Path;
                                  AnimVec2? Size, Center; AnimScalar? Radius; int Direction = 1; ColorF Color; AnimScalar Opacity(100); FillRule Rule;
                                  AnimScalar? Width; LineCap Cap; LineJoin Join; LottieGradient? Gradient; AnimScalar? TrimStart, TrimEnd, TrimOffset; byte TrimMode; }
public sealed class LottieDocument { float FrameRate, InPoint, OutPoint, Width, Height; string Version; LottieLayer[] Layers;
                                     Dictionary<string, LottieLayer[]> Precomps; int UnsupportedFeatures; }

public enum LottieNodeKind : byte { Group, FillPath, StrokePath }
public readonly record struct LottieTrack(AnimChannel Channel, Keyframe[] Keys);
public sealed class LottieNode { LottieNodeKind Kind; int Parent; string Name; float W, H; bool Clip;
                                 float OffsetX, OffsetY, ScaleX = 1, ScaleY = 1, Rotation, Opacity = 1, OriginX, OriginY;   // rest pose
                                 PathData? Geometry; ColorF Color; FillRule Rule; StrokeStyle Stroke; float TrimStart, TrimEnd = 1; byte TrimMode;
                                 LottieTrack[] Tracks; }
public sealed class LottiePlan { float DurationMs, Width, Height; LottieNode[] Nodes /* pre-order, Parent < index, root 0 */;
                                 int DroppedLayers, Approximations, GeometryCount; LottiePose SampleAt(float u); }

public sealed class LottieSource   // immutable handle; compile cached; thread-safe (own PathBuilder instance, PathContentEpoch.Mint is Interlocked)
{ static LottieSource FromUtf8(ReadOnlySpan<byte>, string? name); static FromString(string, string?); static FromFile(string path);
  string? DebugName; LottiePlan Plan { get { lock (_gate) return _plan ??= LottieCompiler.Compile(LottieParser.Parse(_utf8)); } } }
```

## 3. Parser — `Utf8JsonReader` forward walk (the `Localization/JsonResourceReader.cs:64-151` posture)

No DOM, no reflection (AOT-safe). One `ReadX(ref Utf8JsonReader)` per JSON object kind, entered on `StartObject`, unknown properties → `reader.Skip()`, malformed → `JsonException` (loud).

```
ReadDocument: v fr ip op w h · assets[] → ReadAsset(id + layers[]; an asset without layers = image → Skip, Unsupported++) · layers[] → ReadLayer · markers/fonts/chars → Skip
ReadLayer:    ty ind parent nm ip op st sr hd td tt refId w h · ks → ReadTransform · shapes[] → ReadShape · ef[]: any ty 29 (Gaussian Blur) | 21 (Fill) → HasDropEffect · masksProperties → Skip, Unsupported++ · t → Skip
ReadTransform: a p s → ReadAnimVec2 (p.s==true → x/y ReadAnimScalar) · r o → ReadAnimScalar · sk sa rx ry rz or → Skip (Approximations++ if non-zero)
ReadAnimScalar {a, k: number|[n]|[keys], x→Skip} · ReadAnimVec2 {a, k:[x,y(,z)]|[keys]} · ReadAnimPath {a, k:{i,o,v,c}|[keys]}
ReadKey: t h s[] e[](Skip) i{x,y} o{x,y} to ti — x/y number or number[] (per-axis) → float[3] broadcast; h:1 → Hold; a trailing t-only key copies the previous s
ReadShape: ty → gr(it[] recurse) sh(ks→AnimPath) rc(p s r d) el(p s) fl/st(c o r w lc lj) gf/gs(t s e g{p,k}) tm(s e o m) tr(ReadTransform) mm/rp(Type, Skip) else Unknown+Skip
ReadColor: [r,g,b(,a)]; any > 1 → /255; animated colour → first key, Approximations++ · ReadGradient: p stops from k (+ optional alpha stops), MidStop = stop@0.5 or lerp
```
Keys keep layer-local `t`; the compiler owns time.

## 4. Compiler — the Lottie → element/channel mapping

**Time** (verified: layers DO carry non-zero `st`, e.g. Eula `Checkmark st=−31 ip=40`, Patch `Mini Squares - Top st=15`): `U(t) = (t·sr + st + Σ st_precompAncestors − ip_root) / (op_root − ip_root)`; `DurationMs = (op−ip)/fr·1000` (3500). Keys with `U` outside [0,1] are kept — `AnimEngine.Sample` clamps/interpolates (`AnimScheduler.Timeline.cs:116-127`); engine `loop` wraps like Lottie.

**Layer → boxes** (all in Lottie units; the root is scaled once):

| Lottie | Element / channel |
|---|---|
| `a` anchor | `TransformOriginX/Y = a/W, a/H` on the layer's **xform** `BoxEl` (W×H ZStack — the `HeroMotion.PivotGroup` idiom); out-of-range origins are fine (`SceneRecorder.cs:1251` plain math) |
| `p` position | static `OffsetX/Y = p − a`; animated `TranslateX/Y` with values `p(t) − a` (a live Translate row replaces the static offset — `HeroMotion.cs:51-56` caveat) |
| `s` scale % | `ScaleX/Y = s/100`, per-axis easing from `i.x[k]/o.x[k]` |
| `r` deg | `Rotation` (engine degrees) |
| `o` % | `Opacity` on a separate **content** box (a nested child inherits parent opacity in the recorder, AE parenting inherits transform only) |
| `ip/op` not covering the root range | **vis** box with `Opacity` keys `[(0, in?1:0), (U(ip),1,Hold), (U(op),0,Hold)]` |
| `parent` | child xform box nested under the parent's xform box, after the parent's content box; siblings bottom→top (descending `ind`); if a child's `ind` > parent's (painted below its parent) fall back to a replicated transform chain sharing the same `Keyframe[]`s |
| precomp `ty 0` | xform box `w×h`, `ClipToBounds = true`, children = ref comp layers with `PrecompStSum += st` |
| null `ty 3` | xform box only |
| **drop** | `td==1` ∨ `tt != null` ∨ `hd` ∨ `HasDropEffect` ∨ name matches `_emb|_shdw|shdw|emb_msk|Emboss|Shadow` (ordinal, ignore-case) → `DroppedLayers++`; a dropped layer that is a transform parent re-parents its non-dropped children with its chain replicated |
| transform order | engine `translate·about(origin){R·S}` vs Lottie `T(p)·R·S·T(−a)`: identical for uniform scale; non-uniform+rotation on one node → `Approximations++` (none in these assets) |

**Keyframes**: segment `k→k+1` easing = `CubicBezier(k.o.x, k.o.y, k.i.x, k.i.y)` stored on engine key `k+1` (its `Easing` is "the segment leading into it"); `h:1` → `Easing.Hold`. **Spatial** `p` (`to/ti` ≠ 0): the segment is a cubic; bake `M = clamp(round(segMs/40), 4, 12)` linear sub-keys at arc-length fractions of the eased progress (32-entry length LUT); `Approximations++`. Animated anchor: static first key + `Approximations++`.

**Shapes** (`LottieGeometry.cs`): walk items with a running static affine `M` (every group `tr` in the three files is static; an animated group `tr` gets a Group node via the layer path): `sh/rc/el` → geometry (rc: kappa 0.5523 corners, r ≤ min(w,h)/2; el: 4 cubics; `d==3` reverses); `gr` → recurse with `M·Affine(tr)` and `groupAlpha·tr.o`; `fl/gf/st` → a paint over the shapes listed **before** it; `tm` → trim for this group's paints; `mm/rp` → `Approximations++` (unmerged). Emit paints in reverse list order (first-listed on top). Geometry via the public `PathBuilder` (`Render/PathDataParser.cs:18`: `MoveTo/LineTo/CubicTo/Close`, `Finish(PathContentEpoch.Mint(), rule)`) — not an SVG string (no alloc, exact floats, and `PathDataParser.Parse` uses a UI-thread static builder that would break `Preload`). All contours of one paint → one `PathData`.
- `fl` → `PathEl { Geometry, Fill = Recolor(c)·o·groupAlpha, Rule }`; `st` → `PathEl { StrokeColor, Stroke = StrokeStyle(w, lc→Cap, lj→Join) }`; animated `o` → `Opacity` track; **animated stroke width** (Eula `comp_2:Line` w 0→2.5 — no engine channel) → static `w_last` + `Opacity = w(t)/w_last` proxy (`Approximations++`).
- `tm` → `TrimStart/End = s/100, e/100` or `StrokeTrimStart/End` tracks; `m:1→TrimMode 0`, `m:2→1`; static `o≠0` added (clamped) + `Approximations++`; animated `o` ignored + `Approximations++`; trim on a fill ignored. (All 7 Eula trims: `o=0`, `m=1`, on strokes.)
- **Animated bezier `sh`** (Eula `paper_fold` 3 keys, Patch `Mini Squares - Middle/Back` 2 keys each; the rest are on dropped layers): per paint, union the animated paths' key times, add `clamp(round(segMs/33), 1, 6)` sub-samples per segment, cap 12 total; each sample = lerped `V/I/O` (vertex counts must match, else hold key 0 + `Approximations++`) baked through `M` into its own `PathData`; emit a **Switch** Group of N paint nodes where sample *i* has step `Opacity` keys `[(0, i==0?1:0)] + [(U_j, i==j?1:0, Hold)…]`. Never morph per frame; N epochs minted once at compile. (The recorder does not cull opacity-0 subtrees — N−1 cached `FillPath` cmds per frame, ~100 tris each: acceptable, noted.)
- **Gradient fills** (2–4 per file, all `p=3` with a 0.5 stop): **v1 = solid `MidStop`** (alpha from `o`). Rationale: a real `PathEl.FillGradient` is a new `FillPathGradient` DrawList opcode through `DrawList/DrawOp`, the 11 dispatch sites gated by `gate.path.stream.sizes`, `Scene PathSpec`, `Reconciler`, `SceneRecorder.cs:2094`, `D3D12/PathPipeline.cs` (reuse `GradientPipeline` stop eval), headless RHI, canon — ~350 LOC/8 files + a screenshot check, for decorative sheens at 192 px; the `BoxEl.ClipPath + Gradient` stencil route is hard-edged (`Element.cs:374`) and rejected. The parsed gradient stays in the document so (a) is a later compiler switch.

**Root**: `Nodes[0]` = Group 552×552, origin (0,0); at mount `ScaleX/Y = fit = min(size/W, size/H)` inside a `size×size` `BoxEl { ClipToBounds }`, centred. `scaleQ` constant per mount → one tessellation per geometry per DPI.

**Static pose**: `LottiePlan.SampleAt(u)` evaluates every track (Hold-aware) into `LottiePose` (per-node channel values) — used for the first frame, `AutoPlay=false`, and reduced motion (`SnapEnd` → pose at `To`, no tracks wired; `KeepFade` → only Opacity tracks). Reduced motion is a value at mount (no hook branch); the wizard remounts the hero per page.

## 5. `LottieView` (Controls)

```csharp
public readonly record struct LottieOptions
{
    public bool Loop { get; init; }                     public bool AutoPlay { get; init; }
    public float From { get; init; }                    public float To { get; init; }          // normalized 0..1
    public ReducedMotionPolicy ReducedMotion { get; init; }                                     // default SnapEnd
    public Func<ColorF, ColorF>? Recolor { get; init; } // applied once at mount to every fill/stroke/mid-stop (RGB; A kept)
    public static LottieOptions Default => new() { Loop = true, AutoPlay = true, From = 0f, To = 1f };
    /// Rise Media Player's setup behaviour: the first half of the timeline, once, then hold (AnimatedVisualPlayer.PlayAsync(0, 0.5, false)).
    public static LottieOptions RiseSetup => Default with { Loop = false, To = 0.5f };
}
public static class LottieView
{
    public static Element Create(LottieSource source, float size, LottieOptions? options = null);   // null → Default
    public static Task Preload(LottieSource source) => Task.Run(() => { _ = source.Plan; });        // ~1–2 ms per 47 KB file; nicety
}
```
`LottieViewComponent`: fields `_plan, _size, _o, NodeHandle[] _handles, Action<NodeHandle>[] _capture` (one closure per tracked node, created once in the ctor — never per render), `Keyframe[][]? _remapped` (From/To ≠ (0,1): `Offset' = (Offset−From)/(To−From)`, duration `×(To−From)`, built at mount). `Render()`: `UseLayoutEffect` wires `anim.Keyframes(handle, channel, keys, dur, loop: _o.Loop)` for every live tracked node unless static-pose mode; a non-looping run holds its last value (engine `loop:false` semantics — verify in `AnimScheduler.Timeline.cs`; if it snaps back, add `ClampEnd` handling). Tree per node: Group → `BoxEl { ZStack, Width=n.W, Height=n.H, ClipToBounds=n.Clip, Offset/Scale/Rotation/Opacity from pose, TransformOrigin, OnRealized=_capture[i] }`; FillPath/StrokePath → `PathEl { Width/Height = comp size (no shrink-wrap), Geometry, Fill|StrokeColor = Recolor(color), Rule|Stroke, Trim*, Opacity, OnRealized }`. Rest pose from `_plan.SampleAt(_o.From)` so the first frame is right before the slab advances.

## 6. Engine gates and tests

`src/FluentGpu.VerticalSlice/Suites/LottieSuite.cs` (+ `Assets/lottie/{eula,connect,patch}.json` as Content; `SuiteRegistry.All += new("lottie", "lottie", LottieSuite.Run)`; modelled on `PathSuite.cs:1611-1835` incl. the `HeroAllocZeroGate` shape):

| Gate | Assertion |
|---|---|
| `gate.lottie.assets.parse [eula\|connect\|patch]` | `Plan` builds; `Nodes.Length>0`, `GeometryCount>0`, every geometry `VerbCount>0`; `DroppedLayers ≥ {12, 3, 8}`; `Approximations` reported |
| `gate.lottie.assets.parse-time` | parse+compile of all three < 50 ms (informational) |
| `gate.lottie.mount.alloc-zero [x]` | mount `LottieView.Create(src, 192)`, 2 `RunFrame()`, then `GC.GetAllocatedBytesForCurrentThread()` delta over 60 frames == 0 **and** `PathRealizationCache.Shared.TessellationCount` unchanged |
| `gate.lottie.mount.channels [x]` | tracked handles have tracks; no `LayoutW/H`/`SizeW/H` rows |
| `gate.lottie.synthetic.parent` / `.visibility` / `.trim` / `.precomp` / `.drop-rules` | a synthetic 3-layer doc (Null A `p` 0→100 with `a=(10,0)`; Shape B `parent A`, `ip 30`, stroke + `tm e` 0→100 with `o={.2,0} i={.8,1}`; Precomp C `st 30` with a 2-key `sh`; plus `x_shdw`, `tt:1`, `td:1`, `ef ty 29` layers): B nested under A; A `TranslateX` == `[(0,−10),(1,90)]`; B vis `Opacity` == `[(0,0),(0.5,1,Hold),(1,0,Hold)]`; B `StrokeTrimEnd` keys 0/1 with `CubicBezier(.2,0,.8,1)`; C `Clip`, Switch group N≥2 distinct epochs, first Hold boundary at 0.5; `DroppedLayers == 4` |
| `gate.lottie.easing.hold` | `Ease(Hold, 0.999) == 0 && Ease(Hold, 1) == 1` |

`FluentGpu.Engine.Tests/LottieParserTests.cs` (string fixtures): header; static vs animated scalar/vec2; easing → `LottieEase`; `h:1`; `to/ti`; colours (`/255`, 3-component A=1, `o:50` → A .5); drop flags + name rule; `sh/rc/el/tm(m:2)/gf(MidStop)`; unknown `ty` skipped; garbage → `JsonException`. `LottieCompilerTests.cs`: time base with `st` sums; animated-path sample counts (2 keys/333 ms → 7; cap 12); spatial sub-keys (M∈[4,12], endpoints exact); per-axis scale easing; stroke-width proxy; `SampleAt(0.5)`; sibling order.

Docs: `docs/guide/components-elements-layout.md` LottieView section (supported/dropped features); `docs/plans/winui-parity-sweep.md:1072` → landed as `LottieView`; skill "Explicit control timelines" row.

## 7. Wavee side

- **Assets**: `src/apps/Wavee/assets/lottie/{eula,connect,patch}.json` = byte copies of `docs/plans/wavee/onboarding-v2-assets/{Eula,Connect,Patch}Lottie.json` (already `Content` via `Wavee.csproj:196`).
- `Features/Setup/WaveeLottie.cs`: `For(SetupPage)` → cached `LottieSource.FromFile(Path.Combine(AppContext.BaseDirectory, "assets", "lottie", name + ".json"))` (the `AppLocale.cs:19` idiom); `Options => LottieOptions.RiseSetup with { Recolor = WaveeLottieRecolor.Apply }` (the configurable seam — `Loop`/`To` are one edit here); `Warm()` = `Preload` ×3 from `SetupPreAuthRoot`/`SetupChrome` mount.
- `Features/Setup/WaveeLottieRecolor.cs` (pure; `Wavee.Tests.csproj` `Compile Include`): exact `#0078D4` → `Tok.AccentDefault`, `#002B67` (Patch navy) → `Tok.AccentTextPrimary`-class deep accent, blue–violet hue band (H 195–285°, S ≥ .35: the gradient mid-stops `#6139DA/#5B41D3`, Eula `#2741AB`) → hue-rotated by `H(accent) − H(#0078D4)` keeping S/L; neutrals (`#FFFFFF #EEEEEE #F1F0EF #E0DEDC`, teal) untouched; alpha preserved. Testable overload `Apply(c, accent, accentDeep)`; `Wavee.Tests/WaveeLottieRecolorTests.cs` (exact, navy, hue band keeps S/L, neutrals, alpha).
- `HeroView.cs`: `Art(SetupPage page, float size) => LottieView.Create(WaveeLottie.For(page), size, WaveeLottie.Options)`; `SetupStage.Rail(page, height)` passes `height ?? SetupLayout.HeroArtSize` (Welcome 192, LocalPlayback `StageArtSize` 140). Keep the card chrome.
- **Delete** `HeroWelcome.cs`, `HeroConnect.cs`, `HeroPatch.cs`, `HeroMotion.cs`; reword the `HeroMotion.Geo` mention in `Features/Detail/LikedHeart.cs:15` to "the `PathGeometryTable.Register` idiom". Engine `PathSuite.cs:1620-1656` keeps its own hero corpus — untouched.
- **Notices**: `ops/build/notices-extra.json` (merged by `generate-third-party-notices.ps1:45,177`) entry: name "Windows 11 OOBE onboarding animations (eula / connect / patch Lottie)", version "Bodymovin 5.6.5 exports, as redistributed by Rise Media Player", license "UNVERIFIED — Microsoft-owned artwork; no licence located", url `https://github.com/Rise-Software/Rise-Media-Player`, note "user accepted the redistribution risk on 2026-09-02".
- CHANGELOG `Changed`: "Setup heroes are the real Lottie animations (Windows-style EULA / connect / patch scenes), recoloured to your accent, played like Rise Media Player's setup (#53)" — rides the onboarding issue; plan doc `docs/plans/wavee/lottie-heroes-implementation.md` (this file's §1–§7).

## 9. Verification

Engine: `dotnet build src/FluentGpu.slnx` + `-c Release` clean; `dotnet run --project src/FluentGpu.VerticalSlice -- --suite lottie,path,anim` while iterating, then the full run → `ALL CHECKS PASSED` with `gate.lottie.*`; `dotnet test src/FluentGpu.Engine.Tests`; `powershell -File docs\design\check-canon.ps1` if a canon doc changed.
App: `dotnet build Wavee.slnx` + `-c Release`; `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj`; `dotnet run --project src/apps/Wavee -- --fake` → Welcome / Sign in / Local playback heroes play the first half once and hold, recoloured to the accent, 192 / 140-DIP rails, no missing-asset log line; Windows "animation effects" off → static end pose after a remount. (Screenshots: the harness capture is black under async render — the user eyeballs the window.)
