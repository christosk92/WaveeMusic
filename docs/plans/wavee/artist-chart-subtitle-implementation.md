# Artist chart subtitle on `SpanTextEl`; delete the wrap-separator machinery

2026-09-09. The artist page's Popular chart row grew a second, flex-based way to render a metadata line
(`MetaLine` + `WrapSeparator` + a `MaxHeight`-as-line-budget overload). It was the direct cause of the broken
rows in the screenshots: the `MaxHeight = 2` overload clamped the meta box to 2 px in the mid column
(`ClampMain`), so the title centred against a 2 px box and the real text spilled below the card; the collapsed
"·" still painted because a glyph run never clipped to its own 0×0 box.

The engine already has the right primitive and the app already uses it correctly: `TrackRowTemplate.MetaLine`
renders the playlist row's subtitle as ONE `SpanTextEl` paragraph (`NoWrap`, `CharacterEllipsis`, `MaxLines=1`,
index-resolved link clicks through the pure `RowSpanLayout`). The chart row now does the same, and the invented
machinery is deleted outright.

## Target

```
┌─ cell: fixed 56 (48 classic) ───────────────────────────────────────────────┐
│  1   [art]   WannaCry                                            ♡    3:39  │
│              [E] feat. Ninajirachi · 🎞 · 6.3M plays                        │
└─────────────────────────────────────────────────────────────────────────────┘
               ^title: TextEl 14/600, 1 line, ellipsis (unchanged)
               ^subtitle: [E badge, fixed] + ONE SpanTextEl 12/16, NoWrap, ellipsis, MaxLines 1
                 spans: "feat. " · "Ninajirachi"(link) · ", " · "Madeon"(link) · " · " · Movie glyph · " · " · "6.3M plays"
```

- Row height is fixed; nothing wraps, nothing grows, no separator can dangle (a separator is text inside a run
  the shaper trims).
- Priority is order: featured artists first, video glyph, plays last (first to be ellipsised on a 264 px cell).
- Album name dropped from the chart row (Spotify parity; for singles it repeats the title).
- Plays always compact; the `cellW >= 300` full-digits tier is gone. Pending plays: the span is absent until the
  count lands. Failed/Offline: `—`.
- All featured artists are link spans; `ArtistMoreButton` ("+N" flyout) is deleted.

## App

`Features/Detail/RowHandlers.cs` — `MetaSpanKind` gains `Label` and `Glyph`; `RowSpanLayout.ChartSubtitle`
is the pure, engine-free slot layout (unit-tested in `RowSpanLayoutTests`).

`Features/Detail/ArtistPopular.cs` — `Subtitle(...)` builds the `SpanTextEl` from the slots (artist spans
`IsLink`, glyph span in `Theme.IconFont`, everything else tertiary); `Row` takes the page artist uri + `go`
instead of `featLine`/`fullPlays`; `body` and the ZStack root are `Height = rowHeight`; `FittedRowH`,
`MidColumn`, `Dot`, `FeatLine`, `ArtistMoreButton` are deleted.

## Engine (`..\fluent-gpu`)

Deleted: `FluentGpu.Controls/MetaLine.cs`; `Element.WrapSeparator` / `WrapMaxLines`; `LayoutInput.WrapSeparator`
/ `WrapMaxLines`; the reconciler copy / stamp / equality terms; the layout hash mixes; in `FlexLayout` every
separator / line-budget branch of `MeasureWrap` / `ArrangeWrap` plus `NextNonSeparator`,
`WrapSeparatorCollapses`, `IsCollapsedWrapSeparator`, `WrapMaxLines`, `CollapseWrapTail`; the `MaxH` skip in
`Measure`; gates 29c/d/f–k.

Kept: `MeasureWrap` measuring children against the LINE width (an over-long `MaxLines=1` child ellipsises to the
line instead of overflowing, gate 29e), per-line grow distribution, and the `SceneRecorder` rule that a text node
with zero area paints nothing.

## Verification

1. `dotnet build src/FluentGpu.slnx` Debug + Release; VerticalSlice `--suite layout`.
2. `dotnet build Wavee.slnx` Debug + Release; `dotnet test Wavee.Tests`.
3. Launch, open an artist with featured credits, check the chart at one- and two-column widths: title paired
   with its subtitle, no text outside the card, no trailing dot, ellipsis only at the tail.
