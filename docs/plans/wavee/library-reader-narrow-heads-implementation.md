# Library › Artists reader — narrow-pane band and album heads (`Artist.Reader`) — implementation plan

Status: **PLAN, awaiting owner approval** (2026-10-01). Runs as **track L** of
`shared-media-surface-implementation.md` (round 1, beside waves 0 and 1) — that file's header holds the round plan and
the owner decisions. Issue: **#158** (filed 2026-10-01); the CHANGELOG bullet ends ` (#158)` and the commit body carries `Fixes #158`.

**Shared pieces live in the surface plan, not here:** `Controls.TrimTip` (§3.5 below is its spec) is built by wave 0c
in `Platform/Controls.TrimTip.cs`; `TextFit` lives in `Platform/Surface.Rules.cs` (wave 0a) and its facts are tested in
`SurfaceRulesTests`. Track L consumes both and defines neither; `Artist.Reader.Shape.cs` holds `ReaderShape` only.

## 0. The defect in one paragraph

The reader is ONE scroller whose item 0 is the artist band and whose blocks are `cover | head + rows`. Both the band and
the head are single flex ROWS in which every control is `Shrink 0` and the text is the only flexible child. Under
~600 DIP the controls alone consume the row: at the owner's screenshot width the band leaves the 32-px artist name
**79 DIP** (`Tro…`) and the album head leaves the title **3–42 DIP** (`P…`, `Ta…`); at 360 the head row OVERFLOWS and
the `…` button is clipped off the pane (the lane is `ClipToBounds`). The compact arm added on 2026-09-18 (W6) swapped a
labelled Play for a FAB and dropped the ↗ circle, saving ~170 DIP, which is not enough: the fix is a geometry change
(stack), not a thinner row.

## 1. The arithmetic (all numbers are LAYOUT DIP — zoom-invariant)

### 1.1 Which widths are in play

- **Zoom.** `FluentApp.Zoom` folds into `Scale` (`Win32Platform.cs:837`: px per DIP = OS DPI × zoom). At the owner's
  110 % zoom a window W physical DIP lays out at `W / 1.1`. A 1920-px maximized window at 100 % DPI is **1745** layout
  DIP wide. The owner's "~430" pane measured in screen pixels is **≈391 layout DIP** — and 391 is exactly the width at
  which the band arithmetic below prints `Tro…` (3 glyphs). Everything below is layout DIP.
- **Shell.** content = viewport − sidebar (`NavCompactW` 56 | pane 180–460, default `NavPaneW` 240) − rail
  (0 | `RailMinW` 200–`RailMaxW` 500, default `RailDefaultW` 340, permitted only while `sidebar + rail + MinContentW
  480 ≤ viewport`, `Shell.cs:1153`). Window floor `MinWidth = 300` (`Shell.Host.cs:258`).
- **Library page.** `LibraryLayoutBreakpoints.Collapsed` (`User.cs:606`): page < 640 ⇒ collapsed (reader = the whole
  page); ≥ 640 (exit ≥ 664) ⇒ wide: reader = page − `LeftW` (`library.artists.leftw`, default **280**, grip range
  240–560, `User.Page.Library.cs:991`) − grip `Splitter.StripW` **16**. `ReadingPane` has no padding (`User.UI.cs:394`).
- **Reader arms today** (`Artist.Reader.cs:534`, `OnBounds` :1158): `narrow` = reader < 640 (exit 664): cover 120→88 and
  the compact band; `spineOff` = reader < 720 (exit 744). `narrow` is in `ReaderMountPolicy.MountKey`; `spineOff` is not.

| Window (px @110 %) | layout | sidebar | rail | page | page arm | LeftW+grip | **reader** |
|---|---|---|---|---|---|---|---|
| 1920 maximized | 1745 | 240 | 340 | 1165 | wide | 296 | **869** |
| 1920, sidebar dragged to 460 | 1745 | 460 | 340 | 945 | wide | 296 | **649** |
| ≈1394 (the screenshot) | 1267 | 240 | 340 | 687 | wide | 296 | **391** |
| 1366 | 1242 | 240 | 340 | 662 | wide | 296 | **366** |
| 1280 | 1164 | 240 | 340 | 584 | collapsed | — | **584** |
| 1280, rail closed | 1164 | 240 | 0 | 924 | wide | 296 | **628** |
| 960 (half of 1920) | 873 | 56 | 0 (56+340+480 > 873) | 817 | wide | 296 | **521** |
| any, LeftW dragged to 560 at page 664 | — | — | — | 664 | wide | 576 | **88** (pathological) |

So the WIDE library arm routinely hands the reader 344–650 DIP (page 640–950 with the default navigator), i.e. the
reader's own `narrow` arm is the common case on a non-maximized 1080p window with the rail open — not an edge case.

### 1.2 Fixed widths of the non-shrinking children

Glyph estimates (Segoe UI Variable; ± 10 %): display 32/700 tracking −12‰ ≈ **17.9 DIP/char**, 28/700 ≈ 15.7,
title 20/600 ≈ **10.4**, button label 14/600 ≈ 7.3, caption 12 ≈ 6.0, DenseMeta 13 ≈ 6.6.

**Band, compact arm (< 640)** `Artist.Reader.cs:1187-1195, 1210-1214`: pad 16+16 · avatar 56 · PlayFab `BandCircle`
36 · Shuffle `IconAction` 32 · Follow slot `MinWidth FollowSlotW` 108 · 4 gaps × `Spacing.M` 12 = **312**.
Name column = W − 312.
**Band, wide arm (≥ 640)**: pad 20+20 · 5 gaps × 16 · avatar 72 · `PlayButton` floor `PrimaryMinWidth` 120 · 32 ·
108 · ↗ 32 = **484**. Name column = W − 484.
**Album head** `:1899-1930`: body = W − 16 (block pad L) − cover (88 | 120) − 16 (gap) − 20 (pad R) = **W − 52 −
cover**. Row: title `Shrink 1` · 10 · meta `Shrink 0` (≈ 102 for "10 songs · 32 min", 72 for "1 liked song", 126 for
"12 songs · 1 hr 2 min") · 10 · spacer · 10 · ▶ 32 · 10 · ♡ 32 · 10 · … 32 = **248 + body overhead** ⇒ title = W −
388 (narrow) / W − 420 (wide).

### 1.3 Resulting text widths

| reader W | band name TODAY | ≈ chars @32 | head title TODAY (meta 102) | ≈ chars @20 |
|---|---|---|---|---|
| 360 | 48 (compact) | 2 | **−28 → row overflows, `…` clipped** | 0 |
| 391 | 79 | 3 (`Tro…`) | 3 (`P…`) | 0 |
| 430 | 118 | 5 | 42 (`Ta…`) | 2–3 |
| 520 | 208 | 10 | 132 | 12 |
| 600 | 288 | 15 | 212 | 20 |
| 640 (wide) | 156 | 8 | 220 (cover 120) | 21 |
| 664 | 180 | 9 | 244 | 23 |
| 700 | 216 | 11 | 280 | 27 |
| 744 | 260 | 14 | 324 | 31 |
| 800 | 316 | 17 | 380 | 36 |
| 1000 | 516 | 28 | 580 | 55 |

Two conclusions the design must answer: (1) under 640 no inline row can work — stacking is the only geometry that
gives the text the pane; (2) the WIDE band is already marginal at 640–720 (8–11 glyphs for a 32-px display name).

## 2. Alternatives

Wireframes are at 430 (narrow) and 800 (wide). Legend: `(▶)` 36 accent FAB, `[⤨]` 32 icon action, `[♡ Follow]`
the labelled toggle in its 108 slot, `[↗]` Go to artist, `(▶)(♡)(…)` the head's 32 circles.

### (a) STACKED arm — recommended

```
430 — band stacked (168 tall), head stacked (72), cover 88
┌───────────────────────────────────────────────┐
│ ┌────┐  Florence + The Machine                │  row 1 (100): avatar 56 + name column centred;
│ │ 56 │                                        │   name 32/700 → auto-fits down to 28 → wraps to 2 lines
│ └────┘  In your library: 3 albums · 35 songs  │   → ellipsis + tooltip only past that
│ (▶) [⤨] [♡ Follow    ] [↗]                    │  row 2 (36): 244 wide, fits ≥ 276
├───────────────────────────────────────────────┤
│ in your library · all · 51      newest  oldest  a–z │ pinned sub-rail (unchanged)
├───────────────────────────────────────────────┤
│ ┌──────┐  Tell All Your Friends (Deluxe Ed…   │  head row 1 (30): title 1 line, 286 DIP ≈ 27 chars, tooltip if trimmed
│ │  88  │          10 songs · 32 min (▶)(♡)(…) │  head row 2 (32): meta (Shrink 1) right-aligned beside the circles
│ └──────┘  1  Cute Without the ‘E’     ♡  3:27 │  rows 36 — unchanged
│2002·Album 2  There’s No ‘I’ in Team      3:45 │
```
```
800 — band wide (100, unchanged), head inline (44), cover 120
┌──────────────────────────────────────────────────────────────────────────────────┐
│ ┌──────┐  Florence + The Machine        [▶ Play all] [⤨] [♡ Follow    ] [↗]      │
│ │  72  │  In your library: 3 albums · 35 songs                                   │
│ └──────┘                                                                         │
├──────────────────────────────────────────────────────────────────────────────────┤
│ ┌────────┐  Tell All Your Friends                     10 songs · 32 min (▶)(♡)(…)│  title fills, floor 120; meta beside the circles
│ │  120   │   1  Cute Without the ‘E’ (Cut From the Team)              ♡  3:27    │
```
Name column: W − 100 (360→260, 391→291, 430→330, 520→420, 640→540) — 14–30 glyphs per line, two lines.
Title: W − 144 (360→216, 391→247, 430→286, 520→376) — 20–36 glyphs. Row-2 meta: W − 266 (360→94 trims
"10 songs · 32 m…", 391→125 fits). Controls never crush: 276 ≤ the 300 window floor.
Cost: +68 band DIP under 720; +28 per block with ≥ 2 rows under 640 (head 44 → 72); the estimator changes by one
constant per arm (§3.4).

### (b) Secondary controls into `…`, keep Play + Follow/Save
Band: no band menu exists; Shuffle/↗ would need a new `ContextMenuModel` host. Head: `SaveButton` is a FACT display
(filled heart = saved; memory *missing-UI-means-check-the-fact*) — hiding it hides state. Saves 44 (band) / 42 (head)
DIP: at 391 the title would still be 45 DIP. Rejected.

### (c) Hover/overlay reveal of the controls
Touch and keyboard have no hover; the `…` is "the one affordance that must not be invisible" (`Controls.cs:1027`); the
rows already spend hover-reveal on the trailing heart. Layout-stable but undiscoverable. Rejected.

### (d) Title wraps to 2 lines in place, caption stacked, circles inline
Stated 2-line head = 26·2 + 4 = 56 → `HeadH` 60 everywhere (+16 per block incl. wide). The WIDTH is the constraint,
not the line count: at 430 the title still gets 42 DIP → 2 lines × 4 glyphs. Rejected for the head; for the BAND the
2-line name IS adopted inside (a).

### (e) Icon-only Follow
`FollowToggle` (`Controls.cs:952`) is `ToggleButton.Controlled(label, …, parts: RootNoShrink)` — no icon-only mode.
Adding one = a `parts` override that drops `PartLabel`, a verified glyph pair (only `Icons.Heart`/`HeartFill` are
confirmed in the repo; `Icons.Add` is used by `Show.Page.cs:1028`; no checkmark/person-add glyph was found), and a
`Width 32` slot: ~40 lines in `Controls.cs` + a tooltip for the state word. Compact fixed 312 → 236: name 155 at 391
(8 glyphs) — insufficient alone; the prototype's W6 `(✓)` was exactly this and deviation 10 already rejected it. Not
needed once (a) gives row 2 the width (244 of ≥ 276). Not chosen; reconsider only if a locale's "Following" exceeds
the slot.

### (f) Reference patterns (from memory — not re-verified this session)
Spotify desktop: in panes narrower than ~600 px the header puts the action row UNDER the title and shrinks the title
through size tiers (6 rem → 3 rem → 2 rem) before wrapping; the Now-Playing side panel's artist card stacks name and
Follow. WinUI responsive design: "Reposition" (stack) at the 641 `CompactModeThresholdWidth` — the origin of the
library's 640. Apple Music and the new Windows Media Player stack the ART above the title at narrow widths; we keep
the cover beside the text because the sticky cover and the spine depend on it (and 88 DIP of art beside 286 DIP of
title is a better use of a 430 pane than a 288-tall stack).

### Evaluation matrix

| criterion | (a) stacked | (b) overflow | (c) hover | (d) wrap in place | (e) icon Follow |
|---|---|---|---|---|---|
| name/title legible at 360–640 | yes (216–540 DIP) | no (≤ 90) | yes but controls hidden | no (42) | no (155) |
| discoverability | all verbs visible, same set as wide | shuffle/↗ hidden | poor (touch/kbd) | same as today | state word lost |
| layout stability (follow flip, resize) | Follow slot 108 `Justify Start`: nothing moves; arms flip on the existing 640/720 hysteresis signals | same | same | same | 32 slot, stable |
| scroll jitter | extents are constants per arm; band correction via `CorrectMeasuredExtent(0)` | — | — | +16/block | — |
| tooltips for trimmed text | `Controls.TrimTip` (§3.5) on name + titles | n/a | n/a | same | needed for state |
| keyboard order | name → ▶ → ⤨ → Follow → ↗ ; title → ▶ → ♡ → … | menu depth | hover-gated | same | same |
| localisation | labelled Follow in a MinWidth slot; subline/meta ellipsize | — | — | — | glyph pair per locale |
| RTL | engine has BiDi text only (`Seams/Text/Itemizer.cs`), no layout mirroring — nothing to do, same for all |
| shimmer parity | stacked shimmer face mirrors the real tree (§3.3) | — | — | — | — |
| estimator cost | one constant per arm (`HeadHeight`, `BandHeight`) | none | none | one constant | none |

## 3. Recommendation — the design, with code

One pure, engine-free rule set (`ReaderShape`, moved into its own file as the header of `Artist.Reader.cs:17-19`
already owes) decides BOTH arms from the reader width; the renderer declares the same constants the estimator reads,
so renderer == estimator by identity, and the pure tests pin the sums and the thresholds.

### 3.1 Arms and thresholds (derived, not chosen)

- **Blocks** keep the `narrow` edge (640 / +24): `narrow` ⇒ cover 88 **and the stacked head**. Derivation: inline
  title ≥ `TitleFloorW` 120 needs body ≥ 120 + 136 + meta 126 = 382 ⇒ W ≥ 382 + 52 + 120 = 554 with the wide cover —
  so at 640 the inline head has ≥ 206 DIP for the title and the existing edge is sufficient; one signal, already in
  `MountKey`, already in `ExtentOf`.
- **Band** stacks under the `medium` edge (720 / +24, today's `_spineOff` renamed): derivation `WideBandFixedW` 484 +
  `NameMinW` 232 (≈ 13 display glyphs, "Florence + The…") = 716 ≤ 720, pinned by a test. The band's arm is NOT in
  `MountKey` (R2: the key is what the layout freezes); its extent is corrected in `Settle` like a block whose row
  count landed.

### 3.2 `Entities/Artist.Reader.Shape.cs` (NEW — §1 moved out of `Artist.Reader.cs` verbatim, plus these members)

```csharp
public static class ReaderShape
{
    // ── the two reader-width edges (moved from Reader: SpineHideBelow/NarrowBelow/BreakHysteresis) ───────────────
    public const float NarrowBelow = 640f, MediumBelow = 720f, BreakHysteresis = 24f;
    /// <summary>Under 640 the block cover drops 120 → 88 and the album head STACKS; 24-DIP hysteresis on exit.
    /// An unmeasured width keeps the previous answer (the library page's own rule).</summary>
    public static bool Narrow(float w, bool was) => w <= 0f ? was : was ? w < NarrowBelow + BreakHysteresis : w < NarrowBelow;
    /// <summary>Under 720 the cover spine is gone AND the band folds to its stacked arm.</summary>
    public static bool Medium(float w, bool was) => w <= 0f ? was : was ? w < MediumBelow + BreakHysteresis : w < MediumBelow;

    // ── block geometry (literals, not Spacing.*: this class is engine-free) ──────────────────────────────────────
    public const float BlockPadTop = 16f, BlockPadBottom = 8f, CoverEdge = 120f, CoverEdgeNarrow = 88f, RowH = 36f, RowsPadBottom = 8f, Divider = 1f;
    public const float BlockPadLeft = 16f, BlockCoverGap = 16f, BlockPadRight = 20f;
    public static float CoverOf(bool narrow) => narrow ? CoverEdgeNarrow : CoverEdge;
    /// <summary>What the block leaves its head + rows: the reader width less its padding, the cover and the gap.</summary>
    public static float BodyWidth(float readerW, bool narrow) => readerW - BlockPadLeft - CoverOf(narrow) - BlockCoverGap - BlockPadRight;

    // ── the album head: one row (wide) or title over meta + commands (narrow) ────────────────────────────────────
    public const float HeadH = 44f;                                   // the inline row (unchanged)
    public const float HeadCircle = 32f, HeadGlyph = 15f, HeadGap = 10f;
    public const float HeadTitleLine = 26f, HeadLinkInsetY = 4f;      // 20/26/600 + the link's 2+2 vertical inset
    public const float HeadTitleRowH = HeadTitleLine + HeadLinkInsetY;                                  // 30
    public const float HeadStackGap = 6f, HeadStackPadBottom = 4f;
    public const float HeadStackedH = HeadTitleRowH + HeadStackGap + HeadCircle + HeadStackPadBottom;   // 72 = 2 × RowH
    public static float HeadHeight(bool narrow) => narrow ? HeadStackedH : HeadH;
    public const float HeadCommandsW = 3f * HeadCircle + 2f * HeadGap;                                  // 116
    public const float TitleFloorW = 120f;                            // the inline title never goes under this; meta shrinks next
    /// <summary>The title's own width: the whole body (stacked) or the body less the meta and the commands (inline).</summary>
    public static float TitleAvailW(float readerW, bool narrow, float metaW)
        => narrow ? BodyWidth(readerW, true) - 4f                                                        // the link's 4-DIP horizontal inset
                  : BodyWidth(readerW, false) - HeadGap - metaW - HeadGap - HeadCommandsW;
    public static float StackedMetaAvailW(float readerW) => BodyWidth(readerW, true) - HeadGap - HeadCommandsW;

    // ── the band: one row (wide) or avatar+name over the controls (stacked) ─────────────────────────────────────
    public const float AvatarEdge = 72f, AvatarEdgeStacked = 56f, BandCircle = 36f, IconActionW = 32f, FollowSlotW = 108f;
    public const float PlayButtonMinW = 120f;                         // = Controls.PrimaryMinWidth (ButtonRules.PrimaryWidthNominal)
    public const float BandPadX = 20f, BandPadTop = 20f, BandPadBottom = 8f, BandGap = 16f;             // wide
    public const float BandStackPadX = 16f, BandStackPadTop = 16f, BandStackGap = 12f, BandStackRowGap = 8f;
    public const int   NameLines = 2;
    public const float NameLine = 40f, NameSubGap = 2f, SubLine = 18f;                                 // 32/40 display, 13/18 subline
    public const float NameColumnH = NameLines * NameLine + NameSubGap + SubLine;                       // 100
    public const float BandH = BandPadTop + AvatarEdge + BandPadBottom;                                // 100 (unchanged)
    public const float BandStackedH = BandStackPadTop + NameColumnH + BandStackRowGap + BandCircle + BandPadBottom;   // 168
    public static float BandHeight(bool stacked) => stacked ? BandStackedH : BandH;
    public const float WideBandFixedW = 2f * BandPadX + 5f * BandGap + AvatarEdge + PlayButtonMinW + IconActionW + FollowSlotW + IconActionW;   // 484
    public const float StackedControlsW = BandCircle + IconActionW + FollowSlotW + IconActionW + 3f * BandStackGap;                           // 244
    public const float NameMinW = 232f;                               // ≈ 13 display glyphs: the floor the wide arm must leave the name
    public const float MinReaderW = 300f;                             // Shell.Host's window MinWidth
    public static float NameAvailW(float readerW, bool stacked)
        => stacked ? readerW - 2f * BandStackPadX - AvatarEdgeStacked - BandStackGap : readerW - WideBandFixedW;

    public static float ExtentOf(in ReaderBlock b, bool narrow)
    {
        float cover = CoverOf(narrow) + 8f + 16f;                     // cover + gap + the "2022 · Album" caption
        float body = HeadHeight(narrow) + b.Rows * RowH + RowsPadBottom;
        return BlockPadTop + MathF.Max(cover, body) + BlockPadBottom + Divider;
    }
    // ShimmerRows, SortOf, Capacity, Build, OrderKey: unchanged, moved verbatim.
}

/// <summary>The two measurable proxies for "is this text trimmed" — the engine's TextMetrics has no trimmed flag.</summary>
public static class TextFit
{
    /// <summary>A single-line run is trimmed when its natural width exceeds the slot (half a DIP of slack).</summary>
    public static bool Overflows(float naturalW, float slotW) => slotW > 0f && naturalW > slotW + 0.5f;
    /// <summary>A wrapping run is trimmed when laid out UNBOUNDED at the slot width it needs more lines than the cap.</summary>
    public static bool Trimmed(int unboundedLines, int maxLines) => maxLines > 0 && unboundedLines > maxLines;
}
```

### 3.3 The trees (`Artist.Reader.cs`)

**The band.** The compact arm is DELETED; `Band` becomes two trees selected by `stacked`, each root KEYED (a child of
`ReaderItem`'s root, so the arm swap is a real remount — rule 14) and each DECLARING `ReaderShape.BandHeight(stacked)`.

```csharp
Element Band(Artist a, bool named, ReaderShapeKey shape, bool stacked)
{
    string uri = a.IsValid ? a.Uri.Text : "";
    if (!stacked)
        return new BoxEl
        {
            Key = "band:w", Direction = 0, Gap = ReaderShape.BandGap, AlignItems = FlexAlign.Center, Shrink = 0f,
            Height = ReaderShape.BandH, MinWidth = 0f,
            Padding = new Edges4(ReaderShape.BandPadX, ReaderShape.BandPadTop, ReaderShape.BandPadX, ReaderShape.BandPadBottom),
            Children =
            [
                Avatar(a, named, ReaderShape.AvatarEdge),
                NameColumn(a, named, shape, stacked: false),
                Controls.PlayButton(Tok.AccentDefault, _playAll, Loc.Get(Strings.Library.PlayAll)) with { Shrink = 0f },
                Album.CommandCircle(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), _shuffleAll),
                Follow(uri, named ? a.Name : null, FlexJustify.End),
                Album.CommandCircle(Icons.OpenInNewWindow, Loc.Get(Strings.Detail.GoToArtist), _goArtist),
            ],
        };
    // STACKED (< 720): row 1 = avatar + the name column (two lines at most, centred on the avatar); row 2 = the SAME
    // verbs as the wide arm, left-aligned — 244 DIP, which fits the 300-DIP window floor with 56 to spare
    // (ReaderShape.StackedControlsW; ArtistReaderHeadLayoutTests pins it).
    return new BoxEl
    {
        Key = "band:s", Direction = 1, Shrink = 0f, MinWidth = 0f, Gap = ReaderShape.BandStackRowGap,
        Height = ReaderShape.BandStackedH,
        Padding = new Edges4(ReaderShape.BandStackPadX, ReaderShape.BandStackPadTop, ReaderShape.BandStackPadX, ReaderShape.BandPadBottom),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = ReaderShape.BandStackGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Height = ReaderShape.NameColumnH,
                Children = [Avatar(a, named, ReaderShape.AvatarEdgeStacked), NameColumn(a, named, shape, stacked: true)],
            },
            new BoxEl
            {
                Direction = 0, Gap = ReaderShape.BandStackGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Height = ReaderShape.BandCircle,
                Children =
                [
                    Controls.Named(Controls.PlayFab(_playAll, Icons.Play, ReaderShape.BandCircle), Loc.Get(Strings.Library.PlayAll)),
                    Album.CommandCircle(Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), _shuffleAll),
                    Follow(uri, named ? a.Name : null, FlexJustify.Start),      // left edge pinned: ↗ never moves on a flip
                    Album.CommandCircle(Icons.OpenInNewWindow, Loc.Get(Strings.Detail.GoToArtist), _goArtist),
                ],
            },
        ],
    };
}

static Element Follow(string uri, string? name, FlexJustify justify) => new BoxEl
{
    Shrink = 0f, MinWidth = ReaderShape.FollowSlotW, Direction = 0, Justify = justify,
    Children = [Embed.Comp(() => new Controls.FollowToggle { Uri = uri, Name = name }) with { Key = "follow:" + uri }],
};

/// <summary>The name LINK over the subline. STACKED: the name may take two 40-DIP lines, auto-fitting 32 → 28 first
/// (`ArtistCompactTitle` carries `MinSize = 28`; Wrap + MaxLines make it live — the hero at Artist.UI.cs:506-508 is
/// the precedent), ellipsis after that, with a tooltip only when trimmed (`Controls.TrimTip`). The column is the
/// stated <see cref="ReaderShape.NameColumnH"/> and centres its content, so a one-line name sits level with the
/// avatar and a two-line one fills the box — the band's height never depends on the name.</summary>
Element NameColumn(Artist a, bool named, ReaderShapeKey shape, bool stacked)
{
    string name = named ? a.Name : "…";
    TextEl nameText = Design.Type.ArtistCompactTitle(name) with
    {
        Color = Tok.TextPrimary, HoverColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster,
        Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
        Wrap = stacked ? TextWrap.Wrap : TextWrap.NoWrap,
        MaxLines = stacked ? ReaderShape.NameLines : 1,
        LineHeight = ReaderShape.NameLine,
    };
    Element link = new BoxEl
    {
        Corners = Radii.ControlAll, Shrink = 1f, MinWidth = 0f,
        Padding = new Edges4(Spacing.S, 0f, Spacing.S, 0f), Margin = new Edges4(-Spacing.S, 0f, -Spacing.S, 0f),
        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button, OnClick = _goArtist,
        Children = [nameText],
    }.Interactive(Interaction.Subtle);
    return new BoxEl
    {
        Direction = 1, Grow = stacked ? 0f : 1f, Basis = stacked ? float.NaN : 0f, MinWidth = 0f, Gap = ReaderShape.NameSubGap,
        Height = stacked ? ReaderShape.NameColumnH : float.NaN, Justify = stacked ? FlexJustify.Center : FlexJustify.Start,
        Children =
        [
            Controls.TrimTip(link, name, nameText),                      // a column child: cross-stretched, so it can shrink (§3.5)
            Design.Type.DenseMeta(named ? LibraryLine(shape) : "") with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };
}
```
In the stacked band the name column is the row's `Grow 1 / Basis 0 / MinWidth 0` child exactly as today (the avatar
is `Shrink 0`), so `NameAvailW(W, stacked) = W − 100`.

**The head.** Both faces get the arm; both roots declare `ReaderShape.HeadHeight(narrow)` as `Height` (not
`MinHeight`: a locale whose metrics overrun clips, it never reflows — `Album.UI.cs:1037-1043`'s stance). The meta
becomes `Shrink 1 / MinWidth 0` and sits beside the circles in BOTH arms (a deliberate change from W3's
"meta beside the title": the title is now the row's flexible filler, which is what lets the tooltip wrapper sit
inside it — §3.5).

```csharp
Element Head(bool real, Album a, int slot, string uri, string title, bool narrow)
{
    if (!real) return ShimmerHead(narrow);
    string meta = Block.LikedOnly ? Strings.Library.NLikedSongs(_likedCount) : AlbumMeta(a, slot);   // the two branches as today (:1876-1898)
    Element metaEl = Ui.Caption(meta) with { Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 1f, MinWidth = 0f };
    Element play = Controls.Named(Controls.PlayFab(() => Playback.PlayContext(LibraryRows.IdOf(EntityKind.Album, slot)), Icons.Play, ReaderShape.HeadCircle),
                                  Strings.Library.PlayAlbum(title));
    Element save = Embed.Comp(() => new Controls.SaveButton { Uri = uri, Name = title, Glyph = ReaderShape.HeadGlyph, Box = ReaderShape.HeadCircle })
                   with { Key = "save:" + uri };
    Element more = Detail.MoreButton(_menu, ReaderShape.HeadCircle, ReaderShape.HeadGlyph, round: true);
    Element titleSlot = TitleSlot(title, slot, narrow);

    if (!narrow)
        return new BoxEl
        {
            Direction = 0, Gap = ReaderShape.HeadGap, AlignItems = FlexAlign.Center, Height = ReaderShape.HeadH, MinWidth = 0f, ClipToBounds = true,
            Children = [titleSlot, metaEl, play, save, more],          // title fills (floor 120) → meta shrinks → circles never
        };
    return new BoxEl
    {
        Direction = 1, Height = ReaderShape.HeadStackedH, MinWidth = 0f, ClipToBounds = true,
        Children =
        [
            titleSlot,                                                  // Height = HeadTitleRowH, cross-stretched
            new BoxEl
            {
                Direction = 0, Gap = ReaderShape.HeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Height = ReaderShape.HeadCircle,
                Margin = new Edges4(0f, ReaderShape.HeadStackGap, 0f, ReaderShape.HeadStackPadBottom),
                Children = [new BoxEl { Grow = 1f, MinWidth = 0f }, metaEl, play, save, more],
            },
        ],
    };
}

/// <summary>The title link inside its tooltip, inside a slot that the FLEX algorithm sizes: inline it is the row's
/// `Grow 1 / Basis 0` filler floored at <see cref="ReaderShape.TitleFloorW"/>; stacked it is a column child of the
/// stated row height. In both the ToolTip wrapper is a COLUMN child (cross-stretched to the slot's width — a wrapper
/// is `Shrink 0` and can never be a shrinking ROW child, rule 11), and the link inside it is `Shrink 1 / MinWidth 0`
/// so a short title keeps a content-hugging hover plate and a long one ellipsizes.</summary>
Element TitleSlot(string title, int slot, bool narrow)
{
    TextEl text = new TextEl(title)
    {
        Size = 20f, LineHeight = ReaderShape.HeadTitleLine, Weight = 600, Color = Tok.TextPrimary,
        HoverColor = Tok.AccentTextPrimary, BrushTransitionMs = Design.Motion.Faster,
        MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
    };
    Element link = new BoxEl
    {
        Corners = Radii.ControlAll, MinWidth = 0f, Shrink = 1f,
        Padding = new Edges4(Spacing.XS, 2f, Spacing.XS, 2f), Margin = new Edges4(-Spacing.XS, 0f, 0f, 0f),
        Cursor = CursorId.Hand, Focusable = true, Role = AutomationRole.Button,
        OnClick = () => { var al = new Album(slot); if (al.IsValid) Shell.GoTo(Shell.For(al.Uri, al.Title)); },
        Children = [text],
    }.Interactive(Interaction.Subtle);
    return new BoxEl
    {
        Direction = 1, MinWidth = 0f,
        Grow = narrow ? 0f : 1f, Basis = narrow ? float.NaN : 0f,
        Height = narrow ? ReaderShape.HeadTitleRowH : float.NaN,
        MinWidth = narrow ? 0f : ReaderShape.TitleFloorW, Justify = FlexJustify.Center,
        Children = [Controls.TrimTip(link, title, text)],
    };
}

/// <summary>The shimmer face, same geometry per arm, sized boxes instead of text and components (`Skel.Region` forbids
/// mounting stateful components during load; an empty title would derive a zero-width bar).</summary>
Element ShimmerHead(bool narrow)
{
    Element Circle() => new BoxEl { Width = ReaderShape.HeadCircle, Height = ReaderShape.HeadCircle, Shrink = 0f, Corners = Radii.Circle(ReaderShape.HeadCircle) };
    Element titleBar = new BoxEl { Width = 180f, Height = 26f, Corners = Radii.ControlAll, Shrink = 1f, MinWidth = 0f };
    Element metaBar = new BoxEl { Width = 90f, Height = 16f, Corners = Radii.ControlAll, Shrink = 1f, MinWidth = 0f };
    if (!narrow)
        return new BoxEl { Direction = 0, Gap = ReaderShape.HeadGap, AlignItems = FlexAlign.Center, Height = ReaderShape.HeadH, MinWidth = 0f,
                           Children = [titleBar, new BoxEl { Grow = 1f, MinWidth = 0f }, metaBar, Circle(), Circle(), Circle()] };
    return new BoxEl
    {
        Direction = 1, Height = ReaderShape.HeadStackedH, MinWidth = 0f,
        Children =
        [
            new BoxEl { Direction = 0, Height = ReaderShape.HeadTitleRowH, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [titleBar] },
            new BoxEl { Direction = 0, Gap = ReaderShape.HeadGap, AlignItems = FlexAlign.Center, Height = ReaderShape.HeadCircle, MinWidth = 0f,
                        Margin = new Edges4(0f, ReaderShape.HeadStackGap, 0f, ReaderShape.HeadStackPadBottom),
                        Children = [new BoxEl { Grow = 1f, MinWidth = 0f }, metaBar, Circle(), Circle(), Circle()] },
        ],
    };
}
```
`BlockCell.Build` passes `_r._narrow` and reads `ReaderShape.CoverOf(_r._narrow)` (`:1743`).

### 3.4 Renderer == estimator

- Blocks: `_extentOf` → `ReaderShape.ExtentOf(in _blocks[i − Prefix], _narrow)` (unchanged call) now contains
  `HeadHeight(narrow)`; the head root DECLARES `HeadHeight(narrow)`; both read one constant. `narrow` stays in
  `ReaderMountPolicy.MountKey` (a flip remounts the list — today's behaviour; `SeedExtents` reseeds from the same rule).
- Band: `_extentOf(0)` → `ReaderShape.BandHeight(_stacked)`; the band root declares the same. `_stacked` is set in
  `Render` from `_medium.Value` (the renamed `_spineOff`, already subscribed there via `spine = !_spineOff.Value`), and
  `Settle` adds, after the block loop:
  ```csharp
  _ = _medium.Value;
  float band = ReaderShape.BandHeight(_stacked);
  if (band != _bandExtent && (_ctl.CorrectMeasuredExtent(0, band) || _ctl.Viewport.IsNull)) _bandExtent = band;
  ```
  (`_bandExtent` seeded in `SeedExtents`). This is the sanctioned repair the blocks already use (`:1057-1061`); the
  persistent-prefix item 0 is a real row of the table. If live verification shows `CorrectMeasuredExtent(0, …)` does not
  rebase a persistent-prefix item, the fallback is to add `stacked` to `MountKey` (a remount at 720; the scroll key is
  unchanged so the offset restores) — decide from the `library.reader.chrome` line below, not by guessing.
- Truthfulness log (always-on, no switch): the band root and every head root get `OnBoundsChanged` → `_chromeCheck`
  which logs `library.reader.chrome` once per (index, declared, measured) when `|measured.H − declared| > 0.5`. Absent
  line = renderer == estimator; present line = the exact DIP of disagreement.

### 3.5 Tooltips for trimmed text — `Controls.TrimTip` (NEW `Platform/Controls.TrimTip.cs`)

The engine's `TextMetrics` (`Seams/Text/Text.cs:29`) has no trimmed flag and `TextEl` no trimmed callback, so the
decision is made from two things that exist: the slot's arranged width (`UseMeasuredWidth(4f)` — `Show.Page.cs:847`
is the precedent) and a seam measurement of the text (`TextSeam.Default.GetRangeRects` — `Lyrics.UI.cs:1023-1037` is
the precedent). Always-attaching a tooltip to visible text is noise (WinUI guidance); attaching only when trimmed is
the rule. Uses the BOUND `ToolTip.Wrap(Element, Prop<string?>)` overload: a null text mounts NO trigger wiring.

```csharp
public static partial class Controls
{
    /// <summary>Wrap <paramref name="target"/> (which contains the one <see cref="TextEl"/> <paramref name="style"/>
    /// describes) in a tooltip that exists ONLY while that text is trimmed in its slot. Put it where the flex
    /// algorithm sizes the slot — a COLUMN child, never a shrinking ROW child (the ToolTip wrapper is `Shrink 0`).</summary>
    public static Element TrimTip(Element target, string text, TextEl style)
        => Embed.Comp(new TrimTipProps(target, text, style.Size, style.ResolvedWeight, style.CharSpacing, style.LineHeight,
                                       style.Wrap, style.MaxLines, style.MinSize, style.FontFamily),
                      static () => new TrimTipHost());

    sealed record TrimTipProps(Element Target, string Text, float Size, ushort Weight, float CharSpacing, float LineHeight,
                               TextWrap Wrap, int MaxLines, float MinSize, string? Family)
    {
        public bool Equals(TrimTipProps? o) => o is not null && ReferenceEquals(Target, o.Target) && Text == o.Text
            && Size == o.Size && Weight == o.Weight && MaxLines == o.MaxLines && Wrap == o.Wrap && MinSize.Equals(o.MinSize);
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Target), Text, Size, MaxLines);
    }

    sealed class TrimTipHost : Component, IPropsHost
    {
        TrimTipProps? _p;
        readonly Signal<TrimTipProps?> _props = new(null);
        string _measuredText = ""; float _measuredW = float.NaN; bool _trimmed;
        public void ApplyProps(object props) { _p = (TrimTipProps)props; _props.Value = _p; }

        public override Element Render()
        {
            _ = _props.Value;
            float w = UseMeasuredWidth(4f).Value;                        // written during layout → re-render next frame
            var p = _p!;
            if (w > 0f && (!ReferenceEquals(p.Text, _measuredText) || w != _measuredW))
            {
                _measuredText = p.Text; _measuredW = w; _trimmed = Measure(p, w);
            }
            string? tip = w > 0f && _trimmed ? p.Text : null;
            return ToolTip.Wrap(p.Target, (Prop<string?>)tip);
        }

        /// <summary>One seam query per (text, slot width): a single-line run compares its natural width to the slot;
        /// a wrapping run (the band name, auto-fit floor <c>MinSize</c>) is laid out UNBOUNDED at the slot width and
        /// counts its line fragments against <c>MaxLines</c>. No seam (headless) ⇒ treat as trimmed: a redundant
        /// tooltip beats a missing one.</summary>
        static bool Measure(TrimTipProps p, float w)
        {
            if (p.Text.Length == 0 || TextSeam.Default is not { } fonts) return p.Text.Length > 0;
            float size = float.IsNaN(p.MinSize) ? p.Size : MathF.Min(p.Size, p.MinSize);
            Span<RectF> rects = stackalloc RectF[8];
            if (p.Wrap == TextWrap.NoWrap || p.MaxLines <= 1)
            {
                var style = new TextStyle(default, p.Size, p.Weight, TextWrap.NoWrap, TextTrim.None, 0, p.CharSpacing, p.LineHeight);
                int n = fonts.GetRangeRects(p.Text, in style, float.PositiveInfinity, 0, p.Text.Length, rects);
                float natural = 0f; for (int i = 0; i < n; i++) natural += rects[i].W;
                return TextFit.Overflows(natural, w);
            }
            var wrap = new TextStyle(default, size, p.Weight, TextWrap.Wrap, TextTrim.None, 0, p.CharSpacing, p.LineHeight);
            int lines = fonts.GetRangeRects(p.Text, in wrap, w, 0, p.Text.Length, rects);
            return TextFit.Trimmed(lines == rects.Length ? int.MaxValue : lines, p.MaxLines);
        }
    }
}
```
Zero-alloc: one measure per width step per text, cached in the host; nothing per frame. ENGINE FOLLOW-UP (in
`..\fluent-gpu`, separate change): a `TextEl.OnTrimmedChanged`/`TextMetrics.Trimmed` signal would delete `Measure`
and keep the component shell — the clean long-term answer; this plan does not depend on it.

### 3.6 Reader plumbing (`Artist.Reader.cs`)

- `OnBounds` (:1158): `_narrowBand.SetIfChanged(ReaderShape.Narrow(r.W, _narrowBand.Peek()))`,
  `_medium.SetIfChanged(ReaderShape.Medium(r.W, _medium.Peek()))`.
- `Render` (:715-716): `_narrow = _narrowBand.Value; _stacked = _medium.Value; bool spine = !_stacked;`.
- `_extentOf` (:658): `i == 0 ? ReaderShape.BandHeight(_stacked) : …`.
- `ReaderItem.Render` (:1555): `bool stacked = _r._medium.Value; body = _r.Band(a, named, shape, stacked);`.
- `OnListMounted` log keeps `narrow`; add `stacked`.
- Delete: the compact arm (:1187-1195), consts `BandH/BandCompactH` (:521-522), `SpineHideBelow/NarrowBelow/
  BreakHysteresis` (:534), `FollowSlotW` (:544), `HeadCircle/HeadGlyph/BandCircle` (:497), `AvatarEdge/
  AvatarEdgeCompact` (wherever declared), the deviation-10 paragraph (:1177-1182), `BandCompactH` references
  (`:516-522` doc). Nothing stays "for compatibility".

### 3.7 Other surfaces (deliverable 4)

| surface | shares the code? | verdict |
|---|---|---|
| `Entities/Album.Pane.cs:226` → `Album.UI.cs:1044 PaneHeader` | no | fine: stated 160 height, title 24/30 × 2 lines, text column `Grow 1 Basis 0` — the title has W − 186 (205 at 391). No change. |
| `Album.UI.cs:1091-1115 PaneCommands` | no | SAME FAMILY, lower severity: Play 120 + 3 × 32 + 4 × 10 + "Open album ↗" (≈ 102) + pad 40 ≈ **398** fixed; below that the link is clipped off the pane (reader-style widths 344–398 occur). Fix in this plan: the link `Shrink = 1f, MinWidth = 0f`, its `DenseMeta` label `MaxLines 1, Trim ellipsis, MinWidth 0`, and `Controls.TrimTip` around it (tooltip = "Open album"). |
| `Show.Pane`/`Show.PaneHeader` | same stated-header pattern | NOT inspected this session (its command row may share the PaneCommands overflow) — verify. |
| Library navigator rows (`User.UI.cs NavRow`) | no | column ≥ 240, one-line name + "N albums · M songs": not affected. |
| Artist page hero (`Artist.UI.cs:506-508`) | no | already Wrap + MaxLines 2 on `ArtistCompactTitle` — the precedent for §3.3. |
| Library page grip max (`User.Page.Library.cs:991`, 560) | — | allows an 88-DIP reader at page 664. Out of scope here; note for a follow-up: cap the grip's max at `page − ReaderShape.MinReaderW`. |

## 4. Files and symbols

| file | change |
|---|---|
| `src/apps/Wavee/Entities/Artist.Reader.Shape.cs` | NEW: §1 of `Artist.Reader.cs` (`ReaderSort`, `ReaderBlock`, `ReaderShapeKey`, `ReaderShape`) moved verbatim + the §3.2 `ReaderShape` members. Engine-free. (`TextFit` is NOT here — wave 0a, `Surface.Rules.cs`.) |
| `src/apps/Wavee/Entities/Artist.Reader.cs` | §3.3 trees, §3.6 plumbing, the deletions; header budget note updated (the split is done). |
| `src/apps/Wavee/Platform/Controls.TrimTip.cs` | NEW: §3.5 — built by the surface plan's wave 0c, consumed here. |
| `src/apps/Wavee/Entities/Album.UI.cs` | `PaneCommands` link shrink + TrimTip (§3.7). |
| `src/apps/Wavee.Tests/ArtistReaderShapeTests.cs` | the narrow `ExtentOf` facts move to the new head (§5). |
| `src/apps/Wavee.Tests/ArtistReaderHeadLayoutTests.cs` | NEW (§5). |
| `docs/plans/wavee/library-reader-narrow-heads-implementation.md` | this plan; `library-rework-implementation.md` §13 gets an "as built" line (W6's one-row compact band is gone). |
| `CHANGELOG.md` | "Library › Artists: the artist band and album heads stack under 720/640 DIP instead of crushing the name/title to one letter; trimmed names and titles get a tooltip (#n)". |

Unchanged on purpose: `Artist.Reader.Policy.cs` (`MountKey` keeps `narrow` only), `Controls.FollowToggle` (no
icon-only mode), the sub-rail (`User.ScopeRail(..., () => _narrowBand.Value)` already folds), the rows.

## 5. Tests (pure; no source-text reads)

`ArtistReaderHeadLayoutTests` (xUnit, mirrors `LibraryLayoutBreakpointTests`):
- `Narrow_enters_below_640_and_holds_until_664`: `Narrow(639,false)` true, `Narrow(640,false)` false, `Narrow(663,true)`
  true, `Narrow(664,true)` false, `Narrow(0,was)` keeps `was`. Same for `Medium` at 720/744.
- `The_stacked_band_controls_fit_the_window_floor`: `StackedControlsW + 2·BandStackPadX (276) <= MinReaderW (300)`.
- `The_wide_band_leaves_the_name_its_floor_at_the_fold`: `NameAvailW(MediumBelow, false) (236) >= NameMinW (232)`;
  `NameAvailW(MediumBelow + BreakHysteresis, false) (260) >= NameMinW`.
- `Name_width_table`: `NameAvailW(360,true)=260`, `(391,true)=291`, `(430,true)=330`, `(520,true)=420`, `(800,false)=316`.
- `The_inline_title_keeps_its_floor_at_the_narrow_edge`: `TitleAvailW(NarrowBelow, false, metaW: 126) (206) >= TitleFloorW`.
- `The_stacked_title_at_the_window_floor_is_still_wider_than_the_inline_floor`: `TitleAvailW(MinReaderW, true, 0) (156) >= TitleFloorW`.
- `Title_width_table`: `TitleAvailW(391,true,102)=247`, `(430,true,102)=286`, `(800,false,102)=390`;
  `StackedMetaAvailW(391)=125`, `(360)=94`.
- `Heights_are_the_sum_of_their_parts`: `HeadStackedH == 72 == 2·RowH`, `HeadH == 44`, `BandH == 100`,
  `BandStackedH == 168`, `NameColumnH == 100 >= AvatarEdgeStacked`.
- `ExtentOf_narrow_uses_the_stacked_head`: rows 0 → 137 (cover still wins), 1 → 141, 2 → 177, 4 → 249, 12 → 537;
  wide unchanged 169/169/221/509; `ExtentOf(narrow) − ExtentOf(wide) == HeadStackedH − HeadH` once the body wins.
- `TextFit` (in `SurfaceRulesTests`, wave 0a — listed here for completeness): `Overflows(300, 290)` true, `Overflows(289.6, 290)` false, `Overflows(100, 0)` false;
  `Trimmed(3, 2)` true, `Trimmed(2, 2)` false, `Trimmed(1, 0)` false.
- `MountKey_ignores_the_band_arm`: `ReaderMountPolicy.MountKey(1,0,0,true)` is the same string whatever the band does
  (documents that `stacked` is NOT a mount input; the signature is unchanged).

`ArtistReaderShapeTests` edits: `The_cover_size_stops_mattering_once_the_rows_outgrow_it` becomes "the narrow block is
28 taller than the wide one once the body wins" with the numbers above; the comment line 31 formula updated.

## 6. Risks

1. **Auto-fit with an explicit `LineHeight`.** `TextEl.MinSize` doc says "leave LineHeight unset"; the hero
   (`Artist.UI.cs:506-508`) already combines `ArtistCompactTitle`'s `LineHeight 40` + `MinSize 28` + Wrap + MaxLines 2,
   so it is believed to work. If the live check shows the 28-px face misaligned in its 40 box, drop `MinSize` (set
   `MinSize = float.NaN` on the stacked name) — the stated height is unaffected.
2. **`CorrectMeasuredExtent(0, …)` on a persistent-prefix item** — unverified; the `library.reader.chrome` line tells.
   Fallback in §3.4.
3. **`TrimTip` measures with the default face** (`TextStyle(default, …)` as Lyrics does); the band name is the
   DisplayFace. A few-percent error can mis-decide a tooltip near the edge (never a layout error). Pass the interned
   family id if the seam's `StringId` interning is reachable from the app (verify); else accept.
4. **Flex intrinsic sizing of the title slot.** The slot is `Grow 1 / Basis 0` (inline) or `Height`-stated (stacked),
   so it never relies on content-based intrinsic width; the ToolTip wrapper is always a column child. If the engine's
   column arm still shrink-wraps the wrapper's width, add `AlignItems = FlexAlign.Stretch` explicitly on the slot.
5. **Meta moves beside the circles in the inline arm** — a visible change from the W3 prototype; called out for the
   owner. Reverting it would mean giving the title group a content-based basis, which reintroduces risk 4.
6. **Loc fallback**: `nl.json`/`ko-KR.json` have no `artist` section (`artist.follow`/`following` fall back to
   English); `library.inYourLibrary` is English in all three. Dutch "In je bibliotheek: 3 albums · 35 nummers" ≈ 270
   DIP fits ≥ 370 and ellipsizes below (meta, no tooltip). Follow labels in nl/de/fr/ko are all ≤ "Following".
7. **Vertical cost**: 168 vs 100 for the band under 720; ~5 % of a 110 %-zoom 1080p reader. Accepted for the
   surface's subject; `NameLines` is one constant if the owner prefers one line (`BandStackedH` recomputes).

## 7. Verification

Build: `dotnet build Wavee.slnx` and `-c Release`; `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` Debug and
Release. Live (memory *live-verification-workflow*): build with `-o` to a verify folder, launch (`--fake` is fine if
the fake library has artists), open Library › Artists via the deep link in the `wavee` skill, and drive the window
through reader widths 300 · 360 · 391 · 430 · 520 · 600 · 640 · 664 · 700 · 720 · 744 · 800 · 1000 (physical px =
DIP × 1.1 at the owner's zoom; use `Drive-WaveeWindow -Out` for screen copies — PrintWindow is stale at idle).
Check per width: no `…` clipped; Follow flip moves nothing (slot `Justify Start`); the tooltip appears on a trimmed
name/title and NOT on a short one; dark and light; `nl` locale.
Logs (`%LOCALAPPDATA%\Wavee\logs`): `library.reader.mount narrow= stacked=` on the 640 flips only; NO
`library.reader.chrome` line (renderer == estimator); `library.reader.shape` unchanged in cadence; `frame.stall` /
`nav.frames` quiet during a grip drag across 640 and 720 (24-DIP hysteresis, no oscillation).

## 8. Not verified this session

- Exact glyph advances (±10 %); the `Icons` class location (only `Icons.Heart/HeartFill/Add/Shuffle/OpenInNewWindow/
  Play/More` are confirmed by use); the seam's `StringId` interning API for the DisplayFace; whether
  `CorrectMeasuredExtent` rebases a persistent-prefix item; the Spotify/Apple/WMP behaviours (recollection);
  `Show.Pane`'s command row; the FakeData library contents for the live check; the owner's screenshot width (inferred
  as 391 from the arithmetic matching `Tro…`).
