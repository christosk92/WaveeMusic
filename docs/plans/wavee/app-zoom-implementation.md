# App zoom — implementation (browser-style Ctrl+± over the engine's ZoomLadder)

## Context

The engine (fluent-gpu) grew an application-zoom seam: the effective window scale becomes
**OS DPI scale × zoom** (`IPlatformWindow.Scale`), and everything downstream — layout DIP viewport, glyph
raster, damage, popups, IME, input DIP conversion — consumes that one product. Wavee's half is the policy and
chrome: the chord set, the Ctrl+wheel hook, persistence, the Settings row, the palette verbs and the About
receipt. This doc records the shipped shape.

## Engine surface consumed (all in `..\fluent-gpu`)

```csharp
// FluentGpu.Foundation.ZoomLadder — Chromium's discrete step set; allocation-free.
static readonly float[] Steps;            // 0.5 … 2.5, 12 steps; 1f at index 5
const float Min = 0.25f, Max = 5f, Default = 1f;
static float Clamp(float);                // sanitize (NaN/∞/≤0 → Default)
static float In(float); Out(float);       // one rung up/down, saturating
static float Snap(float);                 // quantize onto the nearest rung
static int Percent(float);                // 0.67f → 67, for UI display

// FluentGpu.FluentApp (UI thread)
AppOptions.Zoom { get; init; } = 1f;      // startup seed, applied before the first frame
static float Zoom;                        // the live factor
static void SetZoom(float);               // Clamp-ed, live, no-change writes dropped
static event Action<float>? ZoomChanged;

// FluentGpu.Hooks.InputHooks
Func<float, bool>? ZoomWheel;             // Ctrl+wheel, signed notch (>0 = in); return true = consume
```

Zoom is deliberately **discrete** (ZoomLadder's own rationale): the glyph-atlas raster keys quantize the
effective scale at ×100, so a free-form slider would alias distinct zooms onto one raster bucket or churn the
atlas per drag-pixel. A fixed 50%…250% ladder keeps every step a distinct cache-friendly raster key and
matches browser muscle memory.

## System map

```
                 launch                                        runtime
┌──────────────────────────────────────┐   ┌────────────────────────────────────────────────┐
│ Program.cs                           │   │ WaveeShell (always-mounted)                    │
│   AppOptions.Zoom =                  │   │   8 KeyAccelerator chord boxes ─┐              │
│     ZoomLadder.Snap(                 │   │   InputHooks.ZoomWheel hook ────┼─► ZoomStep() │
│       settings.Get(ZoomLevel))       │   │ WaveeCommands (palette)         │   (static)   │
│   (pre-window: no startup jump,      │   │   SettingsVerb.Zoom{In,Out,Reset}┘      │      │
│    registry value snapped onto       │   └─────────────────────────────────────────┼──────┘
│    the ladder)                       │                                             ▼
└──────────────────────────────────────┘                    FluentApp.SetZoom(In/Out/Default)
┌──────────────────────────────────────┐                                             │
│ SettingsPage.General ▸ Appearance    │──► SetZoom(step) + IMMEDIATE settings write │
│   "Zoom" ComboBox over ZoomLadder    │                                             ▼
└──────────────────────────────────────┘        FluentApp.Zoom (live) ──► effective scale = dpi × zoom
┌──────────────────────────────────────┐             │                        │
│ WaveeApp zoom-save timer (2s poll)   │◄────────────┘         SettingsPage.About receipts: "Zoom NNN%"
│   |Δ| > 0.004 → Set(ZoomLevel)       │                       (piggybacks the existing 5s tick)
└──────────────────────────────────────┘
```

## The pieces

### Persistence — `WaveeSettings.ZoomLevel` (`appearance.zoom`, default 1f)

- **Seed**: `Program.cs` sets `AppOptions.Zoom = ZoomLadder.Snap(settings.Get(WaveeSettings.ZoomLevel))`
  before the window comes up (the ThemeMode "no startup flash" discipline). `Snap`, not `Clamp`: a persisted
  value that drifted off the ladder (hand-edited registry, an older ladder) re-enters the discrete step set,
  so Ctrl+± always steps between rungs.
- **Write-back**: `WaveeApp` roots a `zoomSaveTimer` beside the volume-save timer (same `UseRef` +
  `??= new System.Threading.Timer(...)` in the mount effect, 2 s cadence): each tick reads `FluentApp.Zoom`
  and writes `ZoomLevel` only when `MathF.Abs(z − stored) > 0.004f`. The chords/wheel/palette never touch the
  store themselves — a held-down chord or wheel spin must not write the registry once per rung. There is no
  "remember zoom" gate: zoom has no opt-out, the setting IS the memory.
- The key is mirrored verbatim in `Wavee.Tests/TestAppSettingsShim.cs` (the shim's production-parity rule).

### The one verb — `WaveeShell.ZoomStep(int dir)`

```csharp
internal static void ZoomStep(int dir)
    => FluentApp.SetZoom(dir == 0 ? ZoomLadder.Default
        : dir > 0 ? ZoomLadder.In(FluentApp.Zoom)
        : ZoomLadder.Out(FluentApp.Zoom));
```

Static and internal on purpose: the chord boxes, the wheel hook and the command palette
(`WaveeCommands.Invoke`, a static table dispatch with no shell instance) all land here.
`FluentApp.SetZoom` clamps and drops no-change writes itself.

### Chords — eight `KeyAccelerator` statics, eight zero-size boxes

A `KeyAccelerator` matches **exact** modifiers, so the browser chord set needs the full per-VK spread
(the same one Chromium registers):

| Chord | VK | Verb |
|---|---|---|
| Ctrl+= | `OemPlus` | `ZoomStep(+1)` |
| Ctrl+Shift+= | `OemPlus` (the '=' key types '+' only WITH Shift on many layouts) | `ZoomStep(+1)` |
| Ctrl+Numpad+ | `Add` (a distinct VK, not an OemPlus alias) | `ZoomStep(+1)` |
| Ctrl+- | `OemMinus` | `ZoomStep(-1)` |
| Ctrl+Shift+- | `OemMinus` | `ZoomStep(-1)` |
| Ctrl+Numpad− | `Subtract` | `ZoomStep(-1)` |
| Ctrl+0 | `D0` | `ZoomStep(0)` |
| Ctrl+Numpad0 | `NumPad0` | `ZoomStep(0)` |

Each rides the shell column's existing accelerator-host idiom — a zero-size, hit-test-free `BoxEl` with
`Accelerator = chord, OnClick = () => ZoomStep(±1/0)` — so focused routing gets first refusal exactly like
Ctrl+T/Ctrl+K (the WinUI `ProcessKeyboardAccelerators` order). Statics live beside `NewTabChord` in
`WaveeShell.cs`; the chord table in `.claude/skills/wavee/palette-shortcuts.md` is updated.

### Ctrl+mouse-wheel — `InputHooks.ZoomWheel`

Wired in `WaveeShell.Render` beside the `FluentApp.AppNavigationCommand` subscription (a
`UseEffect(..., DepKey.Empty)` for the shell's lifetime):

```csharp
Func<float, bool> zoomWheel = notch => { ZoomStep(notch > 0f ? +1 : -1); return true; };
hooks.ZoomWheel = zoomWheel;
return () => { if (ReferenceEquals(hooks.ZoomWheel, zoomWheel)) hooks.ZoomWheel = null; };
```

`ZoomWheel` is a single slot, not an event, so the cleanup restores only if still ours (the narrow drawer's
`KeyPreview` restore rule). The dispatcher invokes it for a Ctrl+wheel AFTER element-level `OnPointerWheel`
handlers declined and BEFORE the viewport scrolls; the Win32 backend synthesizes pinch-zoom from
Ctrl+touchpad/hi-res scroll before events reach the dispatcher, so the hook only ever sees detented mouse
wheels — exactly the population that wants discrete rungs.

### Settings ▸ Appearance — the Zoom row

`SettingsPage.General.cs`, after the Window-material row: a `SettingsRow` with a `ComboBox.Create` over
`ZoomLabels` — a **hoisted** `static readonly string[]` built once from `ZoomLadder.Steps` via
`ZoomLadder.Percent(step) + "%"` (percent labels never change with culture, unlike the Loc-backed label
builders around it). Selected index = `Array.IndexOf(ZoomLadder.Steps, ZoomLadder.Snap(FluentApp.Zoom))` —
the LIVE zoom, since chords may have moved it ahead of the debounced persist. The writer is the
`SetWindowMaterial` shape: `FluentApp.SetZoom(z)` live, then an **immediate** `settings.Set(ZoomLevel, z)`
(a deliberate pick from a deliberate control — no reason to wait out the debounce) and `Bump()`.
Labels: `Strings.Settings.Appearance.Zoom` / `.ZoomSub`.

### Command palette

`WaveeCommands.SettingsVerb` gains `ZoomIn, ZoomOut, ZoomReset`; `Invoke` routes them to
`WaveeShell.ZoomStep(+1/-1/0)`; `CreateBuiltins` gains `settings.zoomIn` / `settings.zoomOut` /
`settings.zoomReset` (`Strings.Settings.Appearance.ZoomIn/ZoomOut/ZoomReset`, glyphs `Icons.Add` /
`Icons.Remove` / `Icons.Undo` — no Zoom glyph exists in the bundled Fluent set). `BuiltinCount` 14 → 17.

### Diagnostics — Settings ▸ About receipt

`WaveeNowReceipts` gains a `_zoom` signal, a `ReceiptLine(_zoom, "Zoom")` under FPS, and a line in
`LastCopyText`. It piggybacks the existing 5 s tick (`ZoomLadder.Percent(FluentApp.Zoom) + "%"`) rather than
subscribing `ZoomChanged` — the component's lifecycle stays "one interval", nothing to unhook, and a fresh
Ctrl+= shows within a tick.

## Localization

Keys under `settings.appearance` in `assets/loc/*.json` (all three cultures): `zoom`, `zoomSub`, `zoomIn`,
`zoomOut`, `zoomReset` → generated consts `Strings.Settings.Appearance.Zoom/ZoomSub/ZoomIn/ZoomOut/ZoomReset`.

## What deliberately does NOT exist

- **No zoom slider** — the ladder is discrete for raster-cache reasons (see the engine rationale above).
- **No per-surface zoom** — the factor folds into the one effective window scale; pages never see it.
- **No ZoomChanged subscriber in Wavee** — the settings row reads the live value per render, the receipt per
  tick, the persist timer per poll; nothing needs the edge.
- **No env-var or legacy path** — one setting, one verb, one seed (the working rules).

## Files touched

| File | Change |
|---|---|
| `src/apps/Wavee/Platform/AppSettings.cs` | `WaveeSettings.ZoomLevel` (`appearance.zoom`, 1f) |
| `src/apps/Wavee.Tests/TestAppSettingsShim.cs` | key mirrored verbatim |
| `src/apps/Wavee/Program.cs` | `AppOptions.Zoom = ZoomLadder.Snap(…)` seed |
| `src/apps/Wavee/Features/Shell/WaveeShell.cs` | 8 chords + 8 accelerator boxes + `ZoomStep` + `ZoomWheel` hook |
| `src/apps/Wavee/WaveeApp.cs` | debounced `zoomSaveTimer` beside the volume timer |
| `src/apps/Wavee/Features/Shell/SettingsPage.General.cs` | Appearance ▸ Zoom ComboBox row |
| `src/apps/Wavee/Features/Shell/WaveeCommands.cs` | 3 palette verbs/entries; `BuiltinCount` 17 |
| `src/apps/Wavee/Features/Shell/SettingsPage.About.cs` | "Zoom" receipt line (5 s tick) |
| `.claude/skills/wavee/palette-shortcuts.md` | chord table rows |
