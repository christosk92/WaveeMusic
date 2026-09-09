# Pinned band drop-zone cues — implementation plan

**Status:** planned, not started. **Scope:** `src/apps/Wavee/Features/Sidebar/{Pane,Data,Shared}`, `assets/loc/en-US.json`,
`Wavee.Tests`. Self-contained UI/interaction work — no backend or sync dependency. A pin dropped here still only
updates `SidebarPinStore` until `pin-spotify-sync-implementation.md` lands; that is fine (the sync plan hooks
`SidebarPinStore.Pin/Insert/Unpin`, which every path below already ends in).

---

## 0. RCA — verified 2026-09-05

| Symptom | Cause | Where |
|---|---|---|
| Dragging a pinnable item over a pinned band that already has ≥1 pin shows no drop-zone card. | The 56-DIP `SidebarPinDropZone` is only the **Empty** row of a Pinned section. | `Pane/SidebarPaneSlot.cs:997-1000` (`EmptyRow`), planner `Data/SidebarRowPlanner.cs:410` |
| No insertion caret above/below a pinned row; only a faint plate under the exact row. | `SlotFor` returns `Into` for every non-rootlist accept ("a pin-band insertion … is a whole-row INTO"); the row's `DropPlate` draws at 18 % alpha for `Into`. | `Pane/SidebarPane.cs:2020-2030` (`SlotFor`), `Pane/SidebarPaneSlot.cs:1322-1343` (`DropPlate`) |
| The caret/line machinery exists but never lights for pins. | `InsertionLine()` IS mounted on every entity row (`SidebarPaneSlot.cs:1297-1298`, `:1302`) and reads `_o.DropSlotFor(i).Kind`; pins simply never publish `Before`/`After`. **(RCA correction: the earlier note "a pinned row has no insertion line" is wrong — the line is there, it is just never armed.)** | `Pane/SidebarPaneSlot.cs:1364-1407` |
| The `Reorderable` never draws its own line either. | `ShowInsertionLine = false`, `LiveProject = false` — displacement is the only same-band cue (a deliberate, correct decision for a flat mixed-height plan). | `Pane/SidebarPane.cs:1693-1707` |
| A pinned **folder that is expanded** makes every pinned row refuse a pin with the bare 🚫 glyph. | Any descendant row in a Pinned section adds the section to `_pinnedSubtrees`; `RebuildBands` then skips the whole section, so `PinSlot` returns `-1` (`SidebarPaneSlot.cs:1504-1506`), `PinSpec` returns **null** (no drop target at all on non-playlist pinned rows) and on playlist pinned rows `ResourceDropSpec(slot:-1)` → `Compatible` false → refusal with `WhyRefused` returning null. | `Pane/SidebarPane.cs:959-1006` (band rebuild), `:2118-2146` (`Transparent`/`WhyRefused`), `Pane/SidebarPaneSlot.cs:1495-1502` (`PinSpec`) |
| No end-of-band target: dropping below the last pin lands nowhere. | The PlaylistTree has `TreeEnd` (`SidebarRowKind.TreeEnd = 14`, `SidebarRowPlanner.cs:530`, rendered by `TreeEndRow` at `SidebarPaneSlot.cs:1447-1470`); Pinned has no equivalent. | — |

Existing machinery this plan reuses rather than re-inventing: `SidebarDropKind.Before/After/EndOfList`
(`Data/RootlistSlotResolver.cs:28-41`), `SidebarDropSlot` + the "line ⟺ Before/After/EndOfList, plate ⟺ Into" invariant
(`SidebarDropCue`, `:272-304`, pinned by `SidebarDropCueTests`), the `_dropSlot` publish/consume signal
(`SidebarPane.DropSlotFor`, `:2226-2230`), `RootlistSlotResolver.Resolve` (`:163-177`), the pointer→`t` math in
`RootlistSlotFor` (`SidebarPane.cs:2305-2326`), `AcceptPinDrop(payload, slot)` (`:1932-1949`) and the
`SidebarPinDropZone` component (`Shared/SidebarPinDropZone.cs`).

---

## 1. Target behaviour

```
  Pinned                                          Pinned
  ┌────────────────────────────────┐              ┌────────────────────────────────┐
  │ ● Discover Weekly              │              │ ● Discover Weekly              │
  │ ● Release Radar                │   pointer    │●━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━ │ ← Before caret (top 30 % band)
  │ ● Chill Mix          ← drag →  │   over the   │ ● Release Radar                │
  │ ▸ Road trip (folder)           │   band       │ ● Chill Mix                    │
  ├ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ┤ 24 DIP rest  │ ▸ Road trip (folder)           │
  └────────────────────────────────┘              │╭ ─ ─ ─ ─ 📌 Drop to pin ─ ─ ─ ╮│ ← PinEnd gutter, lit (32 DIP)
                                                  └╰ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ╯┘
   rest: hairline gutter only                       live compatible drag
```

Per row (pin payload, i.e. `p.CanPin`):

| Pointer zone (row height h, edge = `RootlistSlotResolver.EdgeFor(h)` = clamp(0.3h, 10, 16)) | Row kind | Published slot | Cue | Drop |
|---|---|---|---|---|
| top edge band | any pinned row | `Before` @ row depth | caret above the row | insert at `bandSlot` |
| bottom edge band | any pinned row | `After` @ row depth | caret below the row | insert at `bandSlot + 1` |
| centre | pinned **editable playlist** and payload `CanCopyTracks` | `Into` | plate | deposit tracks (unchanged gesture) |
| centre | everything else | nearest of `Before`/`After` (t < 0.5 → Before) | caret | as above |
| whole row | `PinEnd` gutter | `EndOfList` @ depth 0 | caret at the gutter's top + the lit card | append (`band.Count`) |

Already-pinned payload dropped on a caret = **move** to that slot (`AcceptPinDrop` already does this, `:1938-1945`).
Same-band `ReorderPayload` (a pin row lifted from the band itself) keeps the `Reorderable`'s displacement feedback and is
untouched — `CommitDrop`'s D9 hoist (`SidebarPane.cs:2074`) still returns first.

Pinned folder expanded (band disabled): every pinned row still gets a drop target; a pin payload is **refused with a
sentence** — `"Collapse the pinned folder to pin here"` — instead of the bare glyph.

---

## 2. Changes

### 2.1 Pure slot rule — `Data/PinBandSlots.cs` (new, engine-free, test-included)

```csharp
namespace Wavee;

/// <summary>The pinned band's drop geometry, as pure functions over the SAME resolver the rootlist uses. A pinned row
/// has no depth ladder and no folder semantics for a PIN payload — the only questions are "which edge" and, for an
/// editable-playlist row, "is the centre a track deposit". Engine-free so <c>PinBandSlotsTests</c> drives the real
/// rule (Wavee.Tests source-includes Features/Sidebar/Data).</summary>
public static class PinBandSlots
{
    /// <summary>The row facts a pinned row hands the resolver. Never a folder (a pinned folder is a pin, not a
    /// filing target, for a PIN payload); <paramref name="centerDeposits"/> is "this row is an editable playlist AND
    /// the payload offers tracks".</summary>
    public static SidebarRowFacts Facts(int depth, bool centerDeposits) => new(
        IsFolder: false, FolderExpanded: false, FolderHasChildren: false,
        Depth: depth, NextVisibleDepth: depth,          // no outdent ladder: After stays at the row's own depth
        CenterAccepts: centerDeposits,
        SourceIsSelf: false,                            // a pin dragged onto itself is a MOVE, never a refusal
        SortedNonCustom: false, RootlistLoaded: true);

    /// <summary>Pointer → slot for one pinned row. Delegates the zone geometry (edge bands, centre) to
    /// <see cref="RootlistSlotResolver.Resolve"/> so the two surfaces can never disagree about where an edge is.</summary>
    public static SidebarDropSlot Resolve(int planIndex, float t, float rowHeight, int depth, bool centerDeposits)
        => RootlistSlotResolver.Resolve(planIndex, t, xInRow: float.PositiveInfinity, rowHeight,
                                        Facts(depth, centerDeposits), SidebarDropSlot.None);

    /// <summary>The whole-gutter slot of the band's closing row.</summary>
    public static SidebarDropSlot EndSlot(int planIndex) => new(planIndex, SidebarDropKind.EndOfList, 0, SidebarDropRefusal.None);

    /// <summary>The pin-store index a published slot means, for a row at <paramref name="bandSlot"/> in a band of
    /// <paramref name="bandCount"/>. <c>Into</c> on a pinned row is a deposit, not a pin — callers must not ask.</summary>
    public static int InsertIndex(in SidebarDropSlot slot, int bandSlot, int bandCount) => slot.Kind switch
    {
        SidebarDropKind.Before => bandSlot,
        SidebarDropKind.After => bandSlot + 1,
        SidebarDropKind.EndOfList => bandCount,
        _ => -1,
    };
}
```

`xInRow = +∞` makes `PickDepth` return `Max` (the row's own depth) without touching the ladder — see
`RootlistSlotResolver.PickDepth :236-263` (`min >= max` short-circuits anyway because `NextVisibleDepth == Depth`).

### 2.2 `SidebarPane.ResourceDropSpec` — the pin arm publishes a real slot

`Pane/SidebarPane.cs:1959` gains one parameter and three edits.

```csharp
internal DropTargetSpec ResourceDropSpec(string sectionId, int slot, string? playlistUri, string? playlistName,
                                         WaveeResourceDragPayload? rootTarget = null,
                                         int rootPlanIndex = -1,
                                         Action? onSpringLoad = null,
                                         string? railCueUri = null,
                                         bool isPlaylistRow = false,
                                         SidebarRowFacts rootFacts = default,
                                         bool pinBandDisabled = false)      // NEW: this IS a pinned row, but the band is off
{
    …
    // A PIN placement: this row sits in an armed pinned band and the payload can pin. Rootlist filings win first
    // (a rootlist item dragged inside the pinned band is still a pin, because rootTarget is null for pinned rows).
    bool Pinning(WaveeResourceDragPayload source) => slot >= 0 && source.CanPin && !Filing(source);

    SidebarDropSlot SlotFor(WaveeResourceDragPayload p, DragSession s)
    {
        if (!Compatible(p)) return SidebarDropSlot.None;
        if (rootTarget is not null && Filing(p) && rootPlanIndex >= 0)
            return RootlistSlotFor(rootPlanIndex, FactsFor(p), p, s.Position);
        if (Pinning(p) && rootPlanIndex >= 0)                                          // NEW
        {
            if (!TryRowT(rootPlanIndex, s.Position, out float t, out float extent))
                return new SidebarDropSlot(rootPlanIndex, SidebarDropKind.None, 0, SidebarDropRefusal.Unavailable);
            int depth = TryRowEntry(rootPlanIndex, out var e) ? e.Depth : 0;
            return PinBandSlots.Resolve(rootPlanIndex, t, extent, depth, canDepositHere && p.CanCopyTracks);
        }
        // Every other accepted destination — a track deposit, a rail tile — is a whole-row INTO (the plate).
        return rootPlanIndex >= 0
            ? new SidebarDropSlot(rootPlanIndex, SidebarDropKind.Into, 0, SidebarDropRefusal.None)
            : SidebarDropSlot.None;
    }
```

`TryRowT` is the pointer→row-fraction block lifted verbatim out of `RootlistSlotFor` (`:2308-2319`) so both arms share it:

```csharp
/// <summary>Pointer → (t in [0,1] inside the row, the row's extent). False when the geometry is degenerate (no
/// viewport / scene / plan row) — the caller refuses with Unavailable rather than guessing (D17).</summary>
bool TryRowT(int planIndex, Point2 pointer, out float t, out float extent)
{
    t = 0f; extent = 0f;
    var viewport = _listController.Viewport;
    var scene = Context.Scene;
    if (planIndex < 0 || scene is null || viewport.IsNull || !scene.IsLive(viewport)) return false;
    var rect = scene.AbsoluteRect(viewport);
    float contentY = pointer.Y - rect.Y + _listController.ScrollOffset;
    float top = SidebarRowGeometry.ContentYOf(planIndex, Plan.Rows.Count, RowExtentOf);
    extent = MathF.Max(1f, RowExtentOf(planIndex));
    t = Math.Clamp((contentY - top) / extent, 0f, 1f);
    return true;
}
```

`RootlistSlotFor` then becomes `if (!TryRowT(...)) return Unavailable…; float xInRow = pointer.X - rect.X; …` (keep
its x channel — rootlist still needs the depth ladder).

**Caption** (`CaptionFor :2044-2055`) — make it cue-driven so an album over a pinned editable playlist says the right
thing per zone:

```csharp
if (cue.Kind == SidebarDropKind.Into && canDepositHere && p.CanCopyTracks) return Strings.Drag.AddTo(playlistName ?? "");
if (Pinning(p) && cue.IsArmed) return Strings.Drag.Pin(p.Name);          // Before / After / EndOfList
```

**Commit** (`CommitDrop :2066-2106`) — the deposit arm must only fire for an `Into` cue, and the pin arm consumes the
published slot:

```csharp
if (cue.Kind == SidebarDropKind.Into && playlistUri is { Length: > 0 } target && Acts is { } acts
    && WaveeResourceDrop.CanDepositTracks(s.Payload))
{ WaveeResourceDrop.DepositTracks(acts, target, playlistName ?? "", s.Payload, insertionIndex: null); return; }

if (Pinning(source))
{
    if (cue.PlanIndex != rootPlanIndex || !cue.IsArmed)
    { RefuseDrop(SidebarDropRefusal.Unavailable, $"pin cue={cue.Kind}/{cue.PlanIndex} row={rootPlanIndex}"); return; }
    int at = PinBandSlots.InsertIndex(in cue, slot, BandFor(sectionId).Count);
    if (at < 0) { RefuseDrop(SidebarDropRefusal.Unavailable, "pin cue was Into"); return; }
    AcceptForeign(sectionId, s.Payload, at);
}
```

(`RefuseDrop` and `BandFor` already exist — `:2090`, `:1906`.)

**Refusal sentence** — `Transparent` (`:2118-2130`) and `WhyRefused` (`:2132-2146`):

```csharp
bool Transparent(WaveeResourceDragPayload source)
{
    …
    if (slot >= 0 && source.CanPin) return false;
    if (pinBandDisabled && source.CanPin) return false;          // NEW — aimed at the band, owed a reason
    …
}

string? WhyRefused(WaveeResourceDragPayload source)
{
    var refusal = PayloadRefusal(source);
    if (refusal != SidebarDropRefusal.None) return RefusalSentence(refusal);
    if (pinBandDisabled && source.CanPin && !Filing(source))
        return Loc.Get(Strings.Drag.CollapsePinnedFolderToPin);   // NEW
    …unchanged…
}
```

`Compatible` already returns false for `slot < 0`, so the engine shows the not-allowed glyph and now reads this
caption beside it (`refusalCaption: WhyRefused`, `:2152`).

### 2.3 `SidebarPaneSlot` — every pinned row keeps a target, and the band gets a closing gutter

`Pane/SidebarPaneSlot.cs:1495-1506`:

```csharp
DropTargetSpec? PinSpec(SidebarSectionSpec section, string sectionId, int index, Action? onSpringLoad = null)
{
    if (section.Kind != SidebarSectionKind.Pinned) return null;
    int slot = PinSlot(sectionId, index);
    // slot < 0 with pins present = the band is disabled by an expanded pinned folder. The row still owns a target so
    // the drag chip can SAY why ("Collapse the pinned folder to pin here") instead of showing a bare glyph.
    return _o.ResourceDropSpec(sectionId, slot, null, null, rootPlanIndex: index, onSpringLoad: onSpringLoad,
                               pinBandDisabled: slot < 0 && _o.PinBandDisabled(sectionId));
}
```

The playlist-row call site (`:398-405`) passes the same flag:
`pinBandDisabled: section.Kind == SidebarSectionKind.Pinned && PinSlot(row.SectionId, index) < 0 && _o.PinBandDisabled(row.SectionId)`.
`SidebarPane` exposes `internal bool PinBandDisabled(string sectionId) => _pinnedSubtrees.Contains(sectionId);`.

**The gutter** — `Pane/SidebarPaneSlot.cs:80` switch gains `SidebarRowKind.PinEnd => PinEndRow(section, row, index),`:

```csharp
/// <summary>The pinned band's closing gutter — the TreeEnd idea (SidebarPaneSlot.TreeEndRow) for pins: 24 DIP at
/// rest that owns the one "append at the end" slot, and the compact form of the empty-state card while a compatible
/// drag is live, so a band that already has pins still SHOWS where a new one goes.</summary>
Element PinEndRow(SidebarSectionSpec section, in SidebarRow row, int index)
{
    string sectionId = row.SectionId;
    var owner = _o;
    // The zone component owns its UseDragState (re-renders itself, never the pane) and the accept → AcceptPinDrop
    // at band.Count. The insertion line beneath it is the same bound caret every row carries, lit by the EndOfList
    // slot this row publishes on hover.
    return ZStack(
        Embed.Comp(() => new SidebarPinDropZone(
            (p, _) => owner.AcceptPinDrop(p, owner.BandCountOf(sectionId)),
            compact: true,
            onHover: cue => owner.PublishPinEndSlot(index, cue))) with { Key = "pin-end" },
        InsertionLine());
}
```

`SidebarPane` adds `internal int BandCountOf(string sectionId) => BandFor(sectionId).Count;` and
`internal void PublishPinEndSlot(int planIndex, bool over) => _dropSlot.SetIfChanged(over ? PinBandSlots.EndSlot(planIndex) : (_dropSlot.Peek().PlanIndex == planIndex ? SidebarDropSlot.None : _dropSlot.Peek()));`.

### 2.4 `SidebarPinDropZone` — a `compact` variant (`Shared/SidebarPinDropZone.cs`)

```csharp
public SidebarPinDropZone(Action<object?, int> accept, bool compact = false, Action<bool>? onHover = null) { … }

public const float CompactRestHeight = SidebarRowGeometry.PinEndHeight;   // 24
public const float CompactActiveHeight = 32f;
```

Render differences when `compact`:
- rest: `Height = 24`, no text, transparent fill, a 1-DIP **dashed** `Tok.StrokeCardDefault` hairline centred
  vertically (`BorderWidth = 0`; draw the hairline as a child `BoxEl { Height = 1, BorderDashOn = Spacing.XS, … }`) —
  the quiet "there is a slot here" affordance the user asked for, visible at all times;
- compatible drag live or hovering: `Height = 32`, dashed `Tok.AccentDefault` border, `Tok.AccentSubtle` fill, the
  pin glyph (14) + `Strings.Sidebar.DropToPin` in 11f — same tokens as the full card, one line;
- `onHover?.Invoke(true/false)` from `onEnter/onOver/onLeave/onDrop` so the pane can arm `EndOfList`.
- Size change rides the existing `Resize` `LayoutTransition` (measured reflow) — same mechanism as 56 ↔ 72 today.
  `SidebarRowExtents` seeds 24 (`PinEndHeight`) and the measured seam corrects the transient, exactly as the Empty row's
  56 ↔ 72 already does (`SidebarRowGeometry.cs:164-166` says so).

### 2.5 Planner + geometry

- `Data/SidebarRowPlanner.cs:25-55`: `PinEnd = 15,` — **appended, never inserted** (the kind is the ItemsView recycle
  pool; see the comment block at `:43-54`).
- `PlanPinned` (`:386-412`): after the loop, `if (count > 0 && !grid) Add(ref st, Chrome(SidebarRowKind.PinEnd, s, depth));`
  (grid presentation has no vertical band to close; the Empty branch stays as is).
- `Data/SidebarRowExtents.cs:77-78` sibling: `case SidebarRowKind.PinEnd: return SidebarRowGeometry.PinEndHeight;`
- `Data/SidebarRowGeometry.cs:191-193` sibling: `public const float PinEndHeight = 24f;`
- `RebuildBands` (`SidebarPane.cs:983-1006`): `PinEnd` is not an `IsReorderableRow` kind (`:1070-1072`), so it
  terminates the band — the `Reorderable`'s `ItemCount` (`:1728`) stays the pin count. Nothing to change; add a
  planner test that pins it (§3).
- Rail plan (`SidebarRowPlanner.cs:809-811`, `RailFrom`) reads `input.Pins`, not the expanded rows — the gutter never
  reaches the rail. Verify `RailFrom` does not enumerate `st.Rows`.
- `SidebarPaneBand`/`PinSlot` unchanged.

### 2.6 Strings

`assets/loc/en-US.json` `"drag"` block (`:4-38`): `"collapsePinnedFolderToPin": "Collapse the pinned folder to pin here"`
→ generated `Strings.Drag.CollapsePinnedFolderToPin`. Reuse `Strings.Drag.Pin` (`"Pin {name}"`) and
`Strings.Sidebar.DropToPin` (`:1460`) — no other copy.

---

## 3. Tests

| Test | Covers |
|---|---|
| **new** `PinBandSlotsTests` | `Resolve`: t in top band → `Before` @ depth; bottom band → `After` @ depth; centre with `centerDeposits:false` → `Before` for t<0.5 / `After` otherwise; centre with `centerDeposits:true` → `Into`; edge = `EdgeFor(h)` for h ∈ {32, 40, 48}; degenerate h → `Unavailable`. `InsertIndex`: Before→slot, After→slot+1, EndOfList→count, Into→-1. `EndSlot` is armed, draws the line (`SidebarDropCue.DrawsLine`). |
| `SidebarDropCueTests` | Unchanged — the invariant already covers the kinds pins now publish. |
| `SidebarRowPlannerTests` (+) | A Pinned section with N>0 pins plans `…N item rows…, PinEnd`; with 0 pins plans `Empty` only; grid presentation plans no `PinEnd`; the `PinEnd` row's extent is 24 (`SidebarRowExtents`). Existing sequence assertions for Pinned sections gain the trailing `PinEnd`. |
| `SidebarPaneInvariantTests` | Already pins that the line/plate read the live slot index — no change; run it. |
| `WaveeDragRulesTests` / `RootlistRefusalTests` | No change. |

There is no test for `ResourceDropSpec` itself (it is engine-bound); the pure halves above carry the decisions, which
is the repo's rule.

---

## 4. Order of work (one PR, ~½ day)

1. `PinBandSlots` + tests (pure, no UI).
2. Planner: `PinEnd` kind, extent, `PlanPinned`; planner tests.
3. `SidebarPane`: `TryRowT` extraction (refactor, behaviour-neutral for rootlist), `Pinning` arm in `SlotFor`/`CaptionFor`/`CommitDrop`, `pinBandDisabled` in `Transparent`/`WhyRefused`, `PinBandDisabled`/`BandCountOf`/`PublishPinEndSlot`.
4. `SidebarPaneSlot`: `PinSpec` always returns a spec for Pinned; `PinEndRow`; switch arm.
5. `SidebarPinDropZone` compact variant; string key.
6. Manual pass with `--fake`: Classic + Library V3 + Curated pinned sections; drag an artist from a card over the band
   (caret above/below, gutter lights, drop lands at the caret), drag an album over a pinned editable playlist (edges
   pin, centre deposits), expand a pinned folder and drag (sentence on the chip), drag a pinned row within the band
   (displacement only, no caret — D9 unchanged).
7. Debug + Release build, `Wavee.Tests`, CHANGELOG `(#n)`.
