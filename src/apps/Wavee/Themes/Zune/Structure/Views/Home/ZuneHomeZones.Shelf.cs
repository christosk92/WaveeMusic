// ── Themes/Zune/Structure/Views/Home/ZuneHomeZones.Shelf.cs — Zune's Spine Shelf ("because you like") ─────────────────────────────────────
//
// The concept's Zune skin: every spine a flat square block of its cover's colour with a huge light lowercase title turned to read upward
// (display type, never shrunk: a long title runs off the spine's top), the number in the corner in the block's own ink; the open card flat
// on the card fill, its cover flat, its title in the 36 light voice, the reason in the label case, the play and save rings. The heading
// carries the page counter ("01/03") while the shelf pages. Mechanics and motion are core (Home/SpineShelf); numbers are ZuneShelfRules.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.HomeUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using K = Wavee.Themes.Zune.ZuneDiscoveryKit;
using Z = Wavee.Themes.Zune.ZuneShelfRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneHomeZones
{
    /// <summary>"because you like" as the Spine Shelf: the heading (with the page counter while it pages) over the shelf. The section owns its pager.</summary>
    static Element Shelf(Zone zone, ZuneHomeLayout lay)
        => Embed.Comp(new ShelfProps(zone, lay), static () => new ShelfSection()) with { SkeletonProxy = () => ShelfTree(zone, lay, null) };

    sealed record ShelfProps(Zone Zone, ZuneHomeLayout Layout);

    /// <summary>Owns the section's ONE pager controller (stable for its life) and registers it for the inspector.</summary>
    sealed class ShelfSection : Component
    {
        readonly ShelfController _controller = new();

        public override Element Render()
        {
            var p = UseProps<ShelfProps>();
            string key = p.Zone.Key;
            UseEffect(() => GridFill.Register(key, _controller));
            return ShelfTree(p.Zone, p.Layout, _controller);
        }
    }

    static Element ShelfTree(Zone zone, ZuneHomeLayout lay, ShelfController? controller)
    {
        int n = zone.Clusters?.Count ?? 0;
        var body = SpineShelfView.Of(zone, Z.Of(lay.Tier, lay), s_skin, controller, Icons.ChevronLeft, Icons.ChevronRight);
        return K.Section(ClustersHeading(zone, lay, SpineShelfRules.Pages(n) > 1 ? controller : null), body);
    }

    static readonly SpineShelfSkin s_skin = new(ShelfSpine, ShelfHead, ShelfCaption, ShelfSpineWash, ShelfSpineHoverWash, ShelfFaceWash,
        static () => Tok.AccentDefault, static () => R.OnMedia.Ink, static () => Tok.FillSubtleSecondary);

    // ── the spine ──

    static ColorF SpineColour(ShelfItem item) => item.Accent != 0u ? ColorScience.ToColor(item.Accent) : Tok.FillSolidBase;

    /// <summary>The block's ink: whichever of the near-black and the white reads better on its colour (the concept's luminance flip, by contrast).</summary>
    static ColorF SpineInk(ShelfItem item) => ColorContrast.PickContrast(SpineColour(item));

    static TextEl SpineTitle(ShelfItem item, ColorF ink) => R.Type.Heading(item.Title) with
    {
        Size = Z.SpineTitleSize, LineHeight = Z.SpineTitleLine, Case = R.Case.For(LabelKind.Heading), Color = ink,
        MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.None, Shrink = 0f,
    };

    static Element ShelfSpine(ShelfItem item, int index, SpineShelfGeometry g)
    {
        var ink = SpineInk(item);
        var number = K.Caption(item.Number, cased: false) with { Color = ink, Shrink = 0f };
        if (g.Standing)
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f, Height = g.RowH, ClipToBounds = true,
                Padding = new Edges4(Z.RowPad, 0f, Z.RowPad, 0f),
                Children = [number, SpineTitle(item, ink)],
            };
        float run = g.Height - Z.SpineTop - Z.SpineBottom;
        return new BoxEl
        {
            ZStack = true, Width = g.SpineW, Height = g.Height, MinWidth = 0f,
            Children =
            [
                new BoxEl { AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(Z.NumberInset, Z.NumberInset, 0f, 0f), Children = [number] },
                new BoxEl
                {
                    AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Center, Margin = new Edges4(0f, 0f, 0f, Z.SpineBottom),
                    Children = [SpineShelfView.Sideways(SpineTitle(item, ink), run, Z.SpineTitleLine)],
                },
            ],
        };
    }

    // ── the open card's head ──

    static Element ShelfHead(ShelfItem item, SpineShelfGeometry g)
    {
        var h = item.Header;
        var lay = (ZuneHomeLayout)g.Tier!;
        float cover = g.Standing ? Z.HeadCoverStanding : Z.HeadCover;
        var reason = item.Reason is { } r
            ? new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    K.Caption(r.Lead) with { Shrink = 0f, Trim = TextTrim.None },
                    new BoxEl
                    {
                        Direction = 0, MinWidth = 0f, Shrink = 1f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                        OnClick = () => HomeVerbs.OpenEntity(r.Uri, r.Name),
                        Children = [K.Name(r.Name) with { HoverColor = Tok.TextSecondary, Shrink = 1f }],
                    },
                ],
            }
            : (Element)K.Caption(item.Over);
        var words = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.Center, ClipToBounds = true,
            Children =
            [
                reason,
                new BoxEl
                {
                    Direction = 0, MinWidth = 0f, Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true,
                    OnClick = () => HomeCardNav.Open(in h),
                    Children = [R.Type.Heading(item.Title) with
                    {
                        Size = Z.HeadTitleSize, LineHeight = Z.HeadTitleLine, Case = R.Case.For(LabelKind.Heading), HoverColor = Tok.TextSecondary,
                        MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.None, Shrink = 0f,
                    }],
                },
                K.Caption(item.Meta),
            ],
        };
        var rings = new BoxEl
        {
            Direction = 0, Gap = lay.RingsGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 0f,
            Children =
            [
                K.Ring("zune-shelf-play:" + h.Uri, Icons.Play, Loc.Get(Strings.Home.Play), showLabel: false, primary: true,
                       () => HomeCardNav.Play(in h), lay.RingSize, lay.RingTarget),
                ZuneHomeDaylist.SaveRing(h.Uri, h.Title, lay with { RingLabels = false }, "zune-shelf-save:" + h.Uri),
            ],
        };
        var art = new BoxEl
        {
            Width = cover, Height = cover, Shrink = 0f, Cursor = CursorId.Hand, OnClick = () => HomeCardNav.Open(in h),
            Children = [Controls.Artwork(h.ImageUrl, cover, cover, 0f)],
        };
        if (g.Standing)
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S, MinWidth = 0f, Grow = 1f,
                Children = [new BoxEl { Direction = 0, Gap = Z.HeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [art, words] }, rings],
            };
        return new BoxEl { Direction = 0, Gap = Z.HeadGap, AlignItems = FlexAlign.Center, MinWidth = 0f, Grow = 1f, Children = [art, words, rings] };
    }

    // ── a song tile's words ──

    static Element ShelfCaption(ShelfSong song, int index, SpineShelfGeometry g) => new BoxEl
    {
        Direction = 1, Grow = 1f, MinWidth = 0f, Justify = FlexJustify.SpaceBetween, Padding = Edges4.All(Z.CaptionPad), ClipToBounds = true,
        Children =
        [
            K.Caption((index + 1).ToString(CultureInfo.InvariantCulture), cased: false) with { Color = Prop.Of(static () => R.OnMedia.InkSecondary) },
            new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    R.Type.Heading(song.Title) with
                    {
                        Size = Z.CaptionTitleSize, LineHeight = Z.CaptionTitleLine, Case = R.Case.For(LabelKind.Heading),
                        Color = Prop.Of(static () => R.OnMedia.Ink), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                    K.Sub(song.Artist.Length > 0 ? song.Artist : " ") with { Color = Prop.Of(static () => R.OnMedia.InkSecondary) },
                ],
            },
        ],
    };

    // ── the washes: a flat block of the cover's colour closed, the flat card open ──

    static GradientSpec ShelfSpineWash(ShelfItem item)
    {
        var c = SpineColour(item);
        return new GradientSpec(GradientShape.Linear, 0f, [new GradientStop(0f, c), new GradientStop(1f, c)]);
    }

    static GradientSpec ShelfSpineHoverWash(ShelfItem item)
    {
        var c = ColorF.Lerp(SpineColour(item), R.OnMedia.Ink, Z.HoverMix);
        return new GradientSpec(GradientShape.Linear, 0f, [new GradientStop(0f, c), new GradientStop(1f, c)]);
    }

    static GradientSpec ShelfFaceWash(ShelfItem item)
    {
        var c = Tok.FillSolidBase;
        return new GradientSpec(GradientShape.Linear, 0f, [new GradientStop(0f, c), new GradientStop(1f, c)]);
    }
}
