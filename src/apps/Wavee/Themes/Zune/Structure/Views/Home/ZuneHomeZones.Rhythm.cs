// ── Themes/Zune/Structure/Views/Home/ZuneHomeZones.Rhythm.cs — the Home rhythm templates in Zune's voice (solution A, #a-zune) ─────
//
// Owner 2026-11-16: "go with A, the issue is not shorter page its mono all same stuff". Each section's template follows what it holds and
// no two neighbours share one (SectionChooser + SectionRhythm). No cards: a Zune group stands on the ground.
//
//   LeadGrid       the lead's art (no corners) over its name in the 40 light voice, lowercase, on one line that runs off the column's edge
//                  under a fade (never shrunk, never an ellipsis), its line and the play ring; beside a grid of the next covers (half
//                  each; Compact stacks the lead over the grid at 16 : 8)
//   CompactGrid    small rows, the cover beside the name and its line, no plate; two to five columns, two rows
//   ArtistCircles  one row of round covers, the name centred under each
//   CappedShelf    one row of covers with two title lines; "see all" in the heading instead of paging
//
// Every count is the content tier's `ZuneHomeLayout` (LeadBeside/LeadCols/LeadRows, Compact, Circles, Capped); every grid shows columns x rows
// (never a ragged row). A width change resizes each cell in place (TileResize).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using H = Wavee.Themes.Zune.ZuneHomeRules;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;

namespace Wavee.Themes.Zune;

internal static partial class ZuneHomeZones
{
    static SurfaceShape CircleShape => Shape.Grid with { CaptionLines = 0 };
    static SurfaceShape CappedShape => Shape.Grid with { TitleLines = H.CappedTitleLines };
    static SurfaceShape CompactShape(ZuneHomeLayout lay) => Shape.Row(lay.CompactArt);

    static Element Resizes(Element cell) => Design.Reduced ? cell : cell with { Layout = ZuneMotion.TileResize };

    static Element CoverCard(HomeCard c, string prefix, SurfaceShape shape, IOverlayService? host, bool circular, bool caption)
    {
        string sub = caption ? HomeCards.PlainText(c.Subtitle) : "";
        var data = RhythmCards.Data(in c, sub.Length > 0 ? K.Sub(sub) : null, host, circular);
        return Resizes(new BoxEl { Key = prefix + RhythmCards.KeyOf(c), Direction = 1, MinWidth = 0f, Children = [Controls.Surface(data, shape)] });
    }

    // ── LeadGrid ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element LeadGrid(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay)
    {
        var cards = zone.Items;
        if (cards.Count == 0) return K.Section(Heading(zone, lay), new BoxEl());
        bool beside = lay.LeadBeside;
        var host = HostOf(overlay);
        int n = RhythmCards.Shown(lay.LeadCols, lay.LeadRows, cards.Count - 1);
        var cells = new Element[n];
        for (int i = 0; i < n; i++) cells[i] = CoverCard(cards[i + 1], "zune-lead-grid:", Shape.Grid, host, circular: cards[i + 1].Kind == HomeCardKind.Artist, caption: true);
        Element grid = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Grow = beside ? 1f : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN, AlignItems = FlexAlign.Stretch,
            Children = [GridFill.Of("lead-cells:" + zone.Key, _ => lay.LeadCols, (fit, w) => GridFill.Build(cells, GridFill.Columns(n, fit), w, H.ShelfGap, H.ShelfGap), 0f)],
        };
        // The lead beside the grid and the lead over it are different arrangements: each its own key inside an unkeyed box, so a crossing between
        // them cross-fades (the declared swap) instead of sliding every card across the zone.
        var form = new BoxEl
        {
            Key = (beside ? "zune-lead-grid:beside:" : "zune-lead-grid:stacked:") + zone.Key, Direction = (byte)(beside ? 0 : 1), Gap = H.ShelfGap,
            MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Enter = ZuneMotion.SwapFade, Exit = ZuneMotion.SwapFade, Transition = ZuneMotion.SwapThroughMotion,
            Children = [Lead(cards[0], beside, lay), grid],
        };
        var body = new BoxEl { Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [form] };
        return K.Section(Heading(zone, lay), body);
    }

    /// <summary>The lead: its header art (its cover where it has none) filling the space the grid beside it leaves (16 : 8 stacked), its name in
    /// the 40 light voice (the word is the art: never shrunk, it runs off the edge under a fade), its line, the play ring.</summary>
    static Element Lead(HomeCard c, bool beside, ZuneHomeLayout lay)
    {
        var cc = c;
        // The DJ (AI DJ) leads as its own card states it: its colour as one flat block with what the DJ says on it, the list's own line below.
        bool dj = RhythmCards.DrawsDj(in c);
        string line = HomeCards.PlainText(dj ? c.PlaylistDescription ?? c.Subtitle : c.Subtitle);
        var art = new BoxEl
        {
            ZStack = true, MinWidth = 0f, MinHeight = beside ? H.LeadArtMin : 0f, Grow = beside ? 1f : 0f,
            AspectRatio = beside ? float.NaN : H.LeadArtAspect, ClipToBounds = true,
            Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in cc),
            Fill = dj ? RhythmCards.DjBack(c.DjColors) : ColorF.Transparent,
            Children = dj
                ? [new BoxEl
                {
                    Direction = 1, Justify = FlexJustify.End, MinWidth = 0f, HitTestPassThrough = true, Padding = Edges4.All(H.DjWordsPad),
                    Children = [R.Type.PickQuote(HomeCards.PlainText(c.Subtitle)) with
                    {
                        Color = RhythmCards.DjInk(c.DjColors), MaxLines = H.DjWordsLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    }],
                }]
                : [Controls.CoverFill(c.HeaderImageUrl ?? c.ImageUrl, 0f, H.LeadArtDecodePx)],
        };
        var name = new BoxEl
        {
            Direction = 0, MinWidth = 0f, ClipToBounds = true, EdgeFade = new EdgeFadeSpec(EdgeMask.Right, H.TilePadX * 2f),
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => HomeCardNav.Open(in cc),
            Children = [R.Type.HeroTitle(c.Title) with
            {
                Color = Tok.TextPrimary, HoverColor = Tok.TextSecondary, Case = R.Case.For(LabelKind.Navigation),
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.None, Shrink = 0f,
            }],
        };
        var copy = new BoxEl
        {
            Direction = 1, MinWidth = 0f, Shrink = 0f, Gap = H.LeadCopyGap, Padding = new Edges4(0f, H.LeadCopyTop, 0f, 0f),
            Children =
            [
                name,
                K.Caption(line.Length > 0 ? line : " ", cased: false) with { Color = Tok.TextSecondary },
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Margin = new Edges4(0f, H.LeadRingTop - H.LeadCopyGap, 0f, 0f),
                    Children = [K.Ring("zune-lead-play:" + RhythmCards.KeyOf(c), Icons.Play, Loc.Get(Strings.Home.Play), showLabel: true, primary: true,
                                       () => HomeCardNav.Play(in cc), lay.RingSize, lay.RingTarget)],
                },
            ],
        };
        return Resizes(new BoxEl
        {
            Key = "zune-lead:" + RhythmCards.KeyOf(c), Direction = 1, MinWidth = 0f,
            Grow = beside ? 1f : 0f, Shrink = 1f, Basis = beside ? 0f : float.NaN,
            // Its art's top meets the covers' beside it (a grid cover card stands its plate inset above the cover), its foot their labels' foot.
            Margin = new Edges4(0f, SurfaceGeometry.ShelfPlatePad, 0f, SurfaceGeometry.ShelfPlatePad),
            Children = [art, copy],
        });
    }

    // ── the capped grids ────────────────────────────────────────────────────────────────────────────────────────────────

    static Element CappedGrid(Zone zone, ZuneHomeLayout lay, ZuneCappedBand band, float gap, float rowH, Func<HomeCard, Element> cell)
    {
        var cards = zone.Items;
        if (cards.Count == 0) return K.Section(Heading(zone, lay), new BoxEl());
        float first = band.MinCol * band.MaxCols + gap * (band.MaxCols - 1);
        Element grid = GridFill.Of("zune-rhythm:" + zone.Key, w => band.Columns(w, gap), (c, width) =>
        {
            // Every card the tier's band can show is mounted, the ones this width does not fit hidden: a width that fits a column fewer (the frame a
            // window steps across the tier edge, laid out at the old tier) hides the last card instead of dropping its node.
            int n = RhythmCards.Shown(c, band.Rows, cards.Count), most = Math.Max(n, RhythmCards.Shown(band.MaxCols, band.Rows, cards.Count));
            var cells = new Element[most];
            for (int i = 0; i < most; i++) cells[i] = cell(cards[i]);
            return GridFill.Build(cells, GridFill.Columns(n, c), width, gap, gap, rowH, shown: n);
        }, first);
        return K.Section(Heading(zone, lay), grid);
    }

    static Element CompactGrid(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay)
    {
        var host = HostOf(overlay);
        var shape = CompactShape(lay);
        return CappedGrid(zone, lay, lay.Compact, H.CompactGap, shape.MinHeight, c =>
        {
            string sub = HomeCards.PlainText(c.Subtitle);
            var data = RhythmCards.Data(in c, sub.Length > 0 ? K.Sub(sub) : null, host, circular: c.Kind == HomeCardKind.Artist);
            return Resizes(new BoxEl { Key = "zune-compact:" + RhythmCards.KeyOf(c), Direction = 1, MinWidth = 0f, Children = [Controls.Surface(data, shape)] });
        });
    }

    static Element ArtistCircles(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay)
    {
        var host = HostOf(overlay);
        return CappedGrid(zone, lay, lay.Circles, H.ShelfGap, float.NaN, c => CoverCard(c, "zune-circle:", CircleShape, host, circular: true, caption: false));
    }

    static Element CappedShelf(Zone zone, IOverlayService? overlay, ZuneHomeLayout lay)
    {
        var host = HostOf(overlay);
        return CappedGrid(zone, lay, lay.Capped, H.ShelfGap, float.NaN,
            c => CoverCard(c, "zune-capped:", CappedShape, host, circular: c.Kind == HomeCardKind.Artist, caption: true));
    }
}
