// ── Themes/Zune/Structure/Views/Library/ZuneLibraryAlbumPane.cs — Zune's `Library.AlbumPane` (themes plan 6.3; P5 Z7) ───────────────────────
//
// Boards Z-Library-Albums (the 380 column right of the covers): the album's title (22 light, a link that opens the page), the billed artists, the meta line (12), the
// verbs as words (the 36 rings: play with its word, shuffle, the heart, the "…", and "open album" at the end), then the songs as the shared embedded table (40 rows) and, as
// the table's trailing band, the "also by <artist> in your library" line of links. The host (`Album.Pane`, a `[CoreHost]`) keeps the demand, the four-state readiness
// (Header · Rows · Failed · Ready), the keys and every verb.
//
//  1. SELECTION IS A CROSSFADE, NOT A CUT. The component PERSISTS (the page re-pushes its props, whose equality ignores the callback) and this view returns a ZStack host
//     whose single child is keyed by slot: a key change is a real unmount, so the engine orphans the leaving pane and fades it UNDER the entering one, which rises 6 DIP in
//     (`ZuneLibraryKit.PaneSwap`). Nothing reads a frame clock; reduced motion is the engine's.
//  2. LOADING → READY IS A CROSS-DISSOLVE. The rows area is one `Skel.Region`: pending ⇒ the engine paints and breathes the DERIVED shimmer of the counted rows; Ready ⇒ it
//     mounts the real table and blur-reveals it row by row while the shimmer fades out beneath. The two `Loadable<int>` are the host's CACHED instances, one per state.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using System.Runtime.CompilerServices;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneLibraryKit;
using LR = Wavee.Themes.Zune.ZuneLibraryRules;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Zune;

/// <summary>Zune's library album pane.</summary>
internal static class ZuneLibraryAlbumPane
{
    /// <summary>The registered template of <see cref="ViewKeys.LibraryAlbumPane"/>.</summary>
    public static Element Template(LibraryAlbumPaneProps props, in ViewContext ctx)
    {
        var cell = ctx.Hooks.UseRef(new PaneCell()).Value;
        if (props.Vacant) return new BoxEl();   // the page owns the no-selection vacancy
        cell.Props = props;
        cell.Prepare();

        // The Compact pane (the drill-in's leaf) draws its commands 40 tall: the targets of a one-column page.
        float box = ctx.Tier().Content == WidthTier.Compact ? LR.CompactCommandBox : LR.CommandBox;
        // The "open album" word needs room the Small pane (300) lacks: there the verb stays in the "..." menu instead of shrinking to "o...". On the Short rung the pane folds
        // its header and verbs into one row (the title opens the album).
        var layout = ZuneLibraryTiers.Of(ctx.Tier(), single: ctx.Tier().Content == WidthTier.Compact);
        BoxEl child = props.State == AlbumPaneState.Header ? K.HeaderSkeleton() : Body(props, cell, box, layout.OpenWord, layout.PaneBrief);
        // The host persists; only the child is keyed, so the swap is an orphaned fade under a rising entrance instead of a remount of this component (which would lose the
        // table's scroll and re-ask the model).
        return new BoxEl
        {
            ZStack = true, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true,
            Children = [child with { Key = props.BodyKey, Animate = K.PaneSwap }],
        };
    }

    /// <summary>What the pane keeps across renders: the latest props and the stable thunks the skeleton region and the table were handed.</summary>
    sealed class PaneCell
    {
        public LibraryAlbumPaneProps Props = null!;
        public Func<Element>? Shimmer, Trailing;
        public Func<int, Element>? Rows;

        public void Prepare()
        {
            Shimmer ??= () => CountedShimmer(Props);
            // The trailing band is a host of its own: it reads the model in ITS render, so a save landing re-renders the band alone. Its props carry the selection's uri, so a
            // new album re-reads its model.
            Trailing ??= () => Embed.Comp(new TrailingProps(this, Props.Uri), static () => new AlsoByTrailing());
            Rows ??= _ => Props.Table(Trailing);
        }
    }

    /// <summary>The trailing band's props: the pane's cell (the latest props) and the selection's uri (the band re-reads the model when the album changes).</summary>
    sealed record TrailingProps(PaneCell Cell, string Uri)
    {
        public bool Equals(TrailingProps? o) => o is not null && ReferenceEquals(Cell, o.Cell) && Uri == o.Uri;
        public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Cell), Uri);
    }

    /// <summary>The rows' trailing band: the "also by" line. It reads the model in its own render (the table host's subscriptions do not reach a save landing).</summary>
    sealed class AlsoByTrailing : Component
    {
        public override Element Render()
        {
            var p = UseProps<TrailingProps>();
            return new BoxEl { Key = "also:band", Direction = 1, Shrink = 0f, ClipToBounds = true, Animate = Design.Reveal.Resize, Children = [AlsoBy(p.Cell.Props.AlsoBy())] };
        }
    }

    static BoxEl Body(LibraryAlbumPaneProps p, PaneCell cell, float box, bool openWord, bool brief)
    {
        Element rows = p.State == AlbumPaneState.Failed
            // The Error voice supplies its own subtitle and Retry label; only the headline is ours.
            ? Controls.Vacancy(Controls.VacancyVoice.Error, Controls.VacancyScale.Compact, title: Loc.Get(Strings.Library.SongsFailed), onAction: p.Retry)
            // smoothResize off: the rows area is a fixed flex slot, so there is no height to ease.
            : Skel.Region(p.Region, cell.Shimmer!, cell.Rows!, SkelReveal.StaggerRows, smoothResize: false);
        return new BoxEl
        {
            Direction = 1, Grow = 1f, ClipToBounds = true,
            Children =
            [
                // ONE head for both height rungs (the words and the verbs keep their nodes, K.PaneHead), then an empty slot that keeps the rows area in its place, so the
                // rows keep their node (and the table its scroll) across a height crossing.
                K.PaneHead(p.Title, p.Open, K.ArtistLine(p.Artists), p.Meta, Verbs(p, box, openWord, brief), box, brief),
                new BoxEl { Shrink = 0f },
                p.Notice!,
                // ONE flex slot, in every state: the shimmer, the Retry strip and the real table all grow into it, so the header and the commands above never move.
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true, Children = [rows] },
            ],
        };
    }

    /// <summary>The verbs: the play ring (with its word on the Regular rung), the shuffle ring, the heart, the "…", then "open album" at the end (Regular only: on the Short
    /// rung the title opens the album). A command is at least <paramref name="box"/> tall (a 40 target at Compact). The link is the row's one SHRINKING child, so a narrow pane
    /// never clips it off the edge. The same slots in both forms: what the Short rung does not draw is hidden, so a height crossing rebuilds none of them.</summary>
    static Element[] Verbs(LibraryAlbumPaneProps p, float box, bool openWord, bool brief) =>
    [
        Target(ZuneDiscoveryKit.Ring("lib-pane-play", Icons.Play, Loc.Get(Strings.Home.Play), showLabel: !brief, primary: true, p.Play, stableName: true), box),
        Target(ZuneDiscoveryKit.Ring("lib-pane-shuffle", Icons.Shuffle, Loc.Get(Strings.Detail.Shuffle), showLabel: false, primary: false, p.Shuffle), box),
        K.Save(p.Uri, p.Title, box) with { Key = p.SaveKey },
        K.MoreButton(p.More, box),
        new BoxEl { Grow = 1f, MinWidth = 0f, Visible = !brief },
        new BoxEl
        {
            Key = "lib-pane-open", Visible = openWord && !brief, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 1f, MinWidth = 0f, MinHeight = box,
            Role = AutomationRole.Hyperlink, Focusable = true, Cursor = CursorId.Hand, AllowFocusOnInteraction = false, OnClick = p.Open,
            Children = [K.WordText(Loc.Get(Strings.Library.OpenAlbum), active: false) with { Shrink = 1f, MinWidth = 0f, Trim = TextTrim.CharacterEllipsis }],
        },
    ];

    /// <summary>A ring verb at least <paramref name="box"/> tall: a command is a target.</summary>
    static Element Target(Element ring, float box) => new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinHeight = box, Shrink = 0f, Children = [ring] };

    // ══ THE COUNTED SHIMMER ══════════════════════════════════════════════════════════════════════════════════════════
    //
    // This tree is the region's DERIVATION INPUT, never painted as written: the engine maps its leaves to pulsing bars and cross-dissolves the real table over it. The
    // header band is CHROME the table paints itself, so it stands as an empty band of the header's height (the rows land where the bars stood); the rows are the table's
    // own tracks, gaps and pitch with bars where the number, title and clock go.

    static Element CountedShimmer(LibraryAlbumPaneProps p)
    {
        var set = p.Columns;
        var tracks = Track.EmbeddedTracks(in set);
        var kids = new Element[p.ShimRows + 1];
        kids[0] = new BoxEl { Height = Track.TableRules.HeaderHeightFor(set.Classic), Shrink = 0f };
        for (int i = 0; i < p.ShimRows; i++) kids[i + 1] = ShimRow(in set, tracks, p.RowHeight, i);
        return new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, ClipToBounds = true, Children = kids };
    }

    /// <summary>One placeholder row at the real row's pitch and lanes: the # bar, the title bar (four lengths in rotation), a compilation's artist bar and the clock bar. One
    /// cell per track, in the tracks' own order (<see cref="Track.TracksFor"/>).</summary>
    static Element ShimRow(in Track.ColumnSet set, TrackSize[] tracks, float rowH, int i)
    {
        var cells = new List<Element>(tracks.Length)
        {
            Cell(Controls.PendingBar(LR.ShimNumW, LR.ShimBarH), FlexJustify.Center),
        };
        if (set.Heart) cells.Add(new BoxEl());
        cells.Add(Cell(Controls.PendingBar(LR.ShimRowTitleW(i), LR.ShimBarH), FlexJustify.Start));
        if (set.Artist) cells.Add(Cell(Controls.PendingBar(LR.ShimArtistW, LR.ShimBarH), FlexJustify.Start));
        cells.Add(Cell(Controls.PendingBar(LR.ShimClockW, LR.ShimBarH), FlexJustify.End));
        if (set.Actions) cells.Add(new BoxEl());
        float padX = Track.RowMetrics.PadXFor(set.Tier);
        return new GridEl
        {
            Key = "sh:" + FormatCache.Int(i),
            // NOT Grow: in this column it would hand each row a share of the slack and the pitch would stop matching the real rows'. Shrink 0 for the same reason the other way.
            Columns = tracks, ColGap = Track.RowMetrics.ColGapFor(set.Tier), RowHeight = rowH, Shrink = 0f,
            Padding = new Edges4(padX, 0f, padX, 0f), Children = cells.ToArray(),
        };
    }

    static Element Cell(Element bar, FlexJustify justify) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Justify = justify, MinWidth = 0f, ClipToBounds = true, Children = [bar],
    };

    // ══ "ALSO BY ... IN YOUR LIBRARY" ═════════════════════════════════════════════════════════════════════════════════

    /// <summary>The other saved albums billed to this album's FIRST billed artist, as one line: "also by <artist> in your library" and then each album's title as a link that selects
    /// it IN PLACE (the page's callback, the one library-internal jump that stays). Absent when there are none.</summary>
    static Element AlsoBy(LibraryAlsoByModel? model)
    {
        if (model is null) return new BoxEl();
        var kids = new Element[model.Tiles.Count + 1];
        kids[0] = R.Type.DenseMeta(Strings.Library.AlsoBy(model.ArtistName)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), Shrink = 0f };
        for (int i = 0; i < model.Tiles.Count; i++)
        {
            var card = model.Tiles[i].Card;
            kids[i + 1] = new BoxEl
            {
                Key = "also:" + FormatCache.Int(model.Tiles[i].Slot), Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center,
                Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, OnClick = card.OnClick,
                Children = [R.Type.DenseMeta(card.Title) with { Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap }],
            };
        }
        return new BoxEl
        {
            Direction = 0, Wrap = true, Gap = LR.AlsoByGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f,
            Padding = new Edges4(0f, LR.AlsoByTop, 0f, LR.AlsoByBottom), Children = kids,
        };
    }
}
