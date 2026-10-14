// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryAlbumPane.cs — Classic's `Library.AlbumPane` (themes plan 6.1, T10c1) ──────────────────────
//
// From `Entities\Album.Pane.cs` (`Render`, `Body`, the counted shimmer, the "Also by" strip and its tile) and `Entities\Track.Table.Chrome.cs` (`TableHeaderShim`, the host-free header the library
// panes shimmer under): the library master-detail's right column for an album — ONE anatomy at every width (ch 15 §0 amendment 2026-09-17), the counted shimmer, and the "Also by … in your
// library" strip. The host (`Album.Pane`, a `[CoreHost]`) keeps the demand, the four-state readiness (Header · Rows · Failed · Ready), the keys and every verb.
//
// ── HOW IT FEELS (the three complaints this file answers) ────────────────────────────────────────────────────────────
//
//  1. SELECTION IS A CROSSFADE, NOT A CUT. The component PERSISTS (the page re-pushes its props, whose equality ignores the callback) and this view returns a ZStack host whose single child is
//     keyed by slot — a key change is a real unmount, so the engine orphans the leaving pane and fades it UNDER the entering one, which rises 6 DIP in (`ClassicLibraryPaneKit.PaneSwap`,
//     after Concert.Page.cs's `s_presence`). Nothing reads a frame clock; reduced motion is the engine's.
//  2. LOADING → READY IS A CROSS-DISSOLVE, not a swap. The rows area is one `Skel.Region`: pending ⇒ the engine paints and breathes the DERIVED shimmer of the counted rows; Ready ⇒ it mounts the
//     real table and blur-reveals it row by row while the shimmer fades out beneath. The two `Loadable<int>` are the host's CACHED instances, one per state, because the region's thunks close over the
//     instance we hand it — a fresh one per render would re-arm the boundary instead of flipping it.
//  3. THE HEADER NEVER SKELETONS once the navigator knows the row: only the Header state paints bars, and the real header paints at once, its art arriving through `Controls.Artwork`'s own image
//     reveal. It is also one STATED height (`ClassicLibraryPaneRules.PaneHeaderHeight`) in both states, so a two-line title cannot push the tracklist down.
//  4. THE PANE SCROLLS ONCE. Header and commands are fixed; everything below — the column header, the rows, then the "Also by" strip — is the embedded table's own trailing arm: ONE scroller, one
//     scrollbar, one edge fade. The strip is therefore handed to the table as its trailing band instead of being laid out beside it, which is what used to trap a 5-track EP in a half-height inner
//     viewport with the strip pinned underneath.
//
// The shimmer thunks, the rows thunk and the trailing thunk the old component kept as fields live in a per-template-instance cell (`ctx.Hooks.UseRef`): the region and the table are handed the SAME
// delegates on every render, exactly as before.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's library album pane.</summary>
internal static class ClassicLibraryAlbumPane
{
    /// <summary>The registered template of <see cref="ViewKeys.LibraryAlbumPane"/>.</summary>
    public static Element Template(LibraryAlbumPaneProps props, in ViewContext ctx)
    {
        var cell = ctx.Hooks.UseRef(new PaneCell()).Value;
        if (props.Vacant) return new BoxEl();   // the page owns the no-selection vacancy (W5)
        cell.Props = props;
        cell.Prepare();

        BoxEl child = props.State == AlbumPaneState.Header ? ClassicLibraryPaneKit.HeaderSkeleton() : Body(props, cell);
        // The host persists; only the child is keyed, so the swap is an orphaned fade under a rising entrance instead of a remount of this component (which would lose the table's scroll and
        // re-ask the model).
        return new BoxEl
        {
            ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
            Children = [child with { Key = props.BodyKey, Animate = ClassicLibraryPaneKit.PaneSwap }],
        };
    }

    /// <summary>What the old component kept in fields: the latest props and the stable thunks the skeleton region and the table were handed.</summary>
    sealed class PaneCell
    {
        public LibraryAlbumPaneProps Props = null!;
        public Func<Element>? Shimmer, Trailing;
        public Func<int, Element>? Rows;

        public void Prepare()
        {
            Shimmer ??= () => CountedShimmer(Props);
            Trailing ??= () => AlsoBy(Props.AlsoBy());
            Rows ??= _ => Props.Table(Trailing);
        }
    }

    static BoxEl Body(LibraryAlbumPaneProps p, PaneCell cell)
    {
        Element rows = p.State == AlbumPaneState.Failed
            // The Error voice supplies its own subtitle and Retry label; only the headline is ours.
            ? Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact,
                title: Loc.Get(Strings.Library.SongsFailed), onAction: p.Retry)
            // smoothResize off: the rows area is a fixed flex slot (both branches Grow), so there is no height to ease — and marking a pane-sized region BoundsAnimated would put a Reflow pass on
            // every swap.
            : Skel.Region(p.Region, cell.Shimmer!, cell.Rows!, SkelReveal.StaggerRows, smoothResize: false);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children =
            [
                ClassicLibraryPaneKit.PaneHeader(p.CoverUrl, p.Eyebrow, p.Title, p.Open, p.Attribution!, p.Meta),
                ClassicLibraryPaneKit.PaneCommands(p.Play, p.Shuffle,
                    Embed.Comp(() => new Controls.SaveButton { Uri = p.Uri, Name = p.Title, Box = ClassicLibraryPaneRules.CommandBox }) with { Key = p.SaveKey },
                    Detail.MoreButton(p.More, ClassicLibraryPaneRules.CommandBox), p.Open),
                p.Notice!,
                // ONE flex slot, in every state: the shimmer, the Retry strip and the real table all grow into it, so the header and the commands above never move. "Also by" is NOT a
                // sibling here any more — it is the table's trailing band, inside the table's scroller.
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = [rows] },
            ],
        };
    }

    // ══ THE COUNTED SHIMMER ══════════════════════════════════════════════════════════════════════════════════════════
    //
    // This tree is the region's DERIVATION INPUT, never painted as written: the engine maps its leaves to pulsing bars and cross-dissolves the real table over it. Two consequences shape it.
    //  · The header is CHROME, not data (W5 paints the real "#  Title  ♥  ⏱" while the rows shimmer), so it goes to the deriver as its own override — `Skel(head)` — which returns it verbatim.
    //  · The table's blank placeholder row (`ClassicTrackChrome.Placeholder`) cannot BE the row: its cells are bare `new BoxEl()`, the table's pre-reveal blank — it paints nothing, and the
    //    deriver maps a childless box with no declared extent to an empty spacer, so the pane would load as a void (the complaint itself). The row below is that row's EXTENT — the same tracks,
    //    `ColGapFor`/`PadXFor(EmbeddedTier)` and pitch — with three cells that declare a bar.

    static Element CountedShimmer(LibraryAlbumPaneProps p)
    {
        var head = TableHeaderShim(compact: true);                // pays its own PadXFor: no wrapper padding
        var kids = new Element[p.ShimRows + 1];
        kids[0] = head.Skel(head);
        for (int i = 0; i < p.ShimRows; i++) kids[i + 1] = ShimRow(p.RowHeight, i);
        return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = kids };
    }

    /// <summary>One placeholder row at the real row's pitch and lanes: the # bar, the title bar (four lengths in rotation, so a column of rows reads as text and not as a fence) and the clock
    /// bar. The ♥ and "…" lanes hold nothing — they carry no content to promise.</summary>
    static Element ShimRow(float rowH, int i)
    {
        var set = Track.EmbeddedColumns;
        var tracks = Track.EmbeddedTracks;
        var cells = new Element[tracks.Length];
        int k = 0;
        cells[k++] = Cell(Controls.PendingBar(ClassicLibraryPaneRules.ShimNumW, ClassicLibraryPaneRules.ShimBarH), FlexJustify.Center);
        if (set.Heart && k < cells.Length) cells[k++] = new BoxEl();
        if (k < cells.Length) cells[k++] = Cell(Controls.PendingBar(ClassicLibraryPaneRules.ShimTitleW(i), ClassicLibraryPaneRules.ShimBarH), FlexJustify.Start);
        if (k < cells.Length) cells[k++] = Cell(Controls.PendingBar(ClassicLibraryPaneRules.ShimClockW, ClassicLibraryPaneRules.ShimBarH), FlexJustify.End);
        while (k < cells.Length) cells[k++] = new BoxEl();
        float padX = Track.RowMetrics.PadXFor(set.Tier);
        return new GridEl
        {
            Key = "sh:" + FormatCache.Int(i),
            // NOT Grow (the placeholder row's own Grow is for a ROW-direction slot): in this column it would hand each row a share of the slack and the pitch would stop matching the real
            // rows'. Shrink 0 for the same reason the other way — more rows than fit overflow into the clip, they never squeeze.
            Columns = tracks, ColGap = Track.RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Shrink = 0f,
            Padding = new Edges4(padX, 0f, padX, 0f), Children = cells,
        };
    }

    static Element Cell(Element bar, FlexJustify justify) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, MinWidth = 0f, ClipToBounds = true, Children = [bar],
    };

    /// <summary>The embedded table's column header, WITHOUT a host: <c>#</c> (centred between the same two caret slots the real <c>IndexSortCell</c> reserves, so it sits over the row numbers),
    /// an empty ♥ lane, "Title", the clock and an empty "…" lane, then the band's single 1-DIP hairline. Inert on purpose — there is nothing to sort yet.
    /// <para><paramref name="compact"/> is the register: true = the pane's (no air above the header, because no toolbar sits over it — the embedded arm), false = the full table's two-column
    /// chrome, which pays <c>Spacing.S</c> above it. The horizontal padding is the chrome's own <c>PadXFor(tier)</c> in both cases, so a caller adds NONE of its own.</para>
    /// <para>The colours are the default sort's (Index): the <c>#</c> reads as the active header, everything else as tertiary — exactly what the real header paints the instant it replaces
    /// this one.</para></summary>
    static Element TableHeaderShim(bool compact)
    {
        var set = Track.EmbeddedColumns;
        var tracks = Track.EmbeddedTracks;
        var cells = new List<Element>(tracks.Length);
        cells.Add(new BoxEl
        {
            Key = Track.CellKey.Num, Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, ClipToBounds = true,
            Children =
            [
                new BoxEl { Width = Track.Lane.NumCaretSlot, Shrink = 0f },
                new BoxEl
                {
                    Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [ShimLabel(Loc.Get(Strings.Detail.Column.Number), active: true)],
                },
                new BoxEl { Width = Track.Lane.NumCaretSlot, Shrink = 0f },
            ],
        });
        if (set.Heart) cells.Add(new BoxEl { Key = Track.CellKey.Heart });
        cells.Add(new BoxEl
        {
            Key = Track.CellKey.Title, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
            MinWidth = 0f, ClipToBounds = true,
            Children = [ShimLabel(Loc.Get(Strings.Detail.Column.Title), active: false)],
        });
        cells.Add(new BoxEl
        {
            Key = Track.CellKey.Duration, Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.End,
            MinWidth = 0f, ClipToBounds = true,
            Children = [Icon(Icons.Clock, ClassicLibraryPaneRules.ShimClockGlyph, Tok.TextTertiary)],
        });
        if (set.Actions) cells.Add(new BoxEl { Key = Track.CellKey.More });

        float padX = Track.RowMetrics.PadXFor(set.Tier);
        return new BoxEl
        {
            Key = "header:shim", Direction = 1, ClipToBounds = true,
            Padding = new Edges4(padX, compact ? 0f : Spacing.S, padX, 0f),
            Children =
            [
                new GridEl
                {
                    Columns = tracks, ColGap = Track.RowMetrics.ColGapFor(set.Tier),
                    RowHeight = Track.TableRules.HeaderHeightFor(classic: false), Children = cells.ToArray(),
                },
                new BoxEl { Height = 1f, Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
            ],
        };
    }

    /// <summary>The header label at the Modern rung (12/600), in the real header's two colours.</summary>
    static TextEl ShimLabel(string text, bool active) => Ui.Caption(text) with
    {
        Weight = 600, Color = active ? Tok.TextSecondary : Tok.TextTertiary,
        MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
    };

    // ══ "ALSO BY … IN YOUR LIBRARY" ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The other saved albums billed to this album's FIRST billed artist, as the shared media surface (<see cref="Shape.Shelf"/> over a 96-px cover) in the saved edge's own order, each
    /// entering on the staggered cascade. Absent when there are none. A tile selects IN PLACE through the page's callback — the one library-internal jump that stays.</summary>
    static Element AlsoBy(LibraryAlsoByModel? model)
    {
        if (model is null) return new BoxEl();
        var tiles = new Element[model.Tiles.Count];
        for (int i = 0; i < tiles.Length; i++) tiles[i] = Tile(model.Tiles[i], i);
        return new BoxEl
        {
            Direction = 1, Gap = ClassicLibraryPaneRules.AlsoByGap, Shrink = 0f,
            Padding = new Edges4(Spacing.XL, Spacing.S, Spacing.XL, Spacing.XL),
            Children =
            [
                R.Type.Eyebrow(Strings.Library.AlsoBy(model.ArtistName)) with { Color = Tok.TextTertiary },
                // The house shape for a horizontal rail: a stated height (the surface's own shelf extent — a ScrollEl is a viewport with no content height to grow from), Grow 0, and the
                // edge fade instead of a scrollbar rail under 96-px art (Concert.Page.cs:736, Sidebar.UI.Library.cs).
                ScrollView(new BoxEl { Direction = 0, Gap = Spacing.M, Children = tiles }, horizontal: true) with
                {
                    Grow = 0f, Shrink = 0f, Height = ClassicLibraryPaneRules.AlsoByStripH, SuppressScrollBar = true,
                    AutoEdgeFade = true, AutoEdgeFadeBand = ClassicLibraryPaneRules.AlsoByFadeBand,
                },
            ],
        };
    }

    /// <summary>One tile: the shared surface at <see cref="ClassicLibraryPaneRules.AlsoByCardW"/> (its 8-DIP plate padding around a 96 cover) — hover plate, play FAB, the corner "…" with the
    /// home card menu, the album drag, the hand cursor and the focus stop. The entrance cascade rides a wrapper (an element's own `Animate` belongs on a box).</summary>
    static Element Tile(LibraryAlsoByTile tile, int i) => new BoxEl
    {
        Key = "also:" + FormatCache.Int(tile.Slot), Direction = 1, Shrink = 0f, Animate = Design.Entrance.Row(i),
        // A year-less tile keeps its neighbours' plate height.
        Children = [Controls.Surface(tile.Card with { Height = ClassicLibraryPaneRules.AlsoByCardH }, Shape.Shelf(), ClassicLibraryPaneRules.AlsoByCardW)],
    };
}
