// ── Themes/Zune/Structure/Views/Home/ZuneDiscoveryKit.cs — the words and small parts Zune's Home, Browse and Search share (P5 Z5) ──────
//
// Boards Z-Home, Z-Browse, Z-Search: every section of a discovery page opens on the same HEADING ROW (the 26/32 light heading, its subtitle in
// the tertiary 13 voice 14 after it, the row's link word — "see all", "listening history", "edit" — at its end), a link is a bare word in the
// secondary ink that lights to the primary on hover (never a button plate), the play affordances are the 36 RING (a filled accent disc for
// play, a 2-dip outline for the rest) with its lowercase word beside it, and a flat colour PLATE (a Browse or mood tile) carries its name at the
// bottom left in the on-media ink. Words the theme writes take their casing from the case roles (`R.Case`); a name is never re-cased.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Roles;
using H = Wavee.Themes.Zune.ZuneHomeRules;

using FluentGpu.Signals;

namespace Wavee.Themes.Zune;

/// <summary>The words and parts Zune's discovery pages share.</summary>
internal static class ZuneDiscoveryKit
{
    /// <summary>A section heading (26/32 light), lowercased by the heading case role; one line.</summary>
    public static TextEl HeadingText(string text)
        => R.Type.Heading(text) with
        {
            Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };

    /// <summary>A quickplay group's head (26/32 light, as Z-Home and Z-Frame-Home draw pins, history and new), lowercased.</summary>
    public static TextEl GroupText(string text)
        => R.Type.Heading(text) with
        {
            Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 0f,
        };

    /// <summary>A secondary caption (13/18, tertiary), cased as a label; one line.</summary>
    public static TextEl Caption(string text, bool cased = true)
        => R.Type.DenseMeta(text) with
        {
            Color = Tok.TextTertiary, Case = cased ? R.Case.For(LabelKind.Label) : TextCase.AsWritten,
            MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f, Shrink = 1f,
        };

    /// <summary>A name under a cover (13, primary): a name, never re-cased.</summary>
    public static TextEl Name(string text)
        => R.Type.DenseMeta(text) with { Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>The line under a name (12, secondary): data, never re-cased.</summary>
    public static TextEl Sub(string text)
        => R.Type.MicroMeta(text) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f };

    /// <summary>A link word: the 13 voice in the secondary ink, the primary on hover, a hand and a hyperlink role. <paramref name="target"/> is the
    /// tier's minimum hit target (40 at Compact, 0 elsewhere): the word is centred in a box at least that tall and wide.</summary>
    public static Element Link(string key, string text, Action go, float target = 0f)
        => new BoxEl
        {
            Key = key, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinWidth = target, MinHeight = target,
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, AllowFocusOnInteraction = false, OnClick = go,
            Children =
            [
                R.Type.DenseMeta(text) with
                {
                    Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command),
                    MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
            ],
        };

    /// <summary>A section's heading row: the heading, its subtitle (when the tier shows it), then the link at the end.</summary>
    public static BoxEl Heading(string title, string? subtitle, Element? tools, bool cased = true)
    {
        var words = new List<Element>(2) { cased ? HeadingText(title) : HeadingText(title) with { Case = TextCase.AsWritten } };
        if (subtitle is { Length: > 0 }) words.Add(Caption(subtitle) with { Margin = new Edges4(H.HeadInlineGap, 0f, 0f, 0f) });
        // The title group takes the row's room and yields it (Grow 1, Shrink 1, MinWidth 0), the tools keep their own size 14 after it: "see all"
        // never glues onto the subtitle, and a narrow row ellipsises the words before it moves the link (A6).
        var kids = new List<Element>(2)
        {
            new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Grow = 1f, Shrink = 1f, MinWidth = 0f, Children = words.ToArray() },
        };
        // The tools end the row, so a link's word ends on the column's right edge (Z-Search, Z-Browse-Category: "see all" flush at 1392), its target
        // reaching left of it, never centred 9 DIP in.
        if (tools is not null)
            kids.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, Margin = new Edges4(H.HeadToolsGap, 0f, 0f, 0f),
                Children = [tools is BoxEl box ? box with { Justify = FlexJustify.End } : tools],
            });
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = kids.ToArray() };
    }

    /// <summary>A section: its heading row over its body, <see cref="ZuneHomeRules.HeadGap"/> apart.</summary>
    public static BoxEl Section(Element heading, Element body)
        => new() { Direction = 1, Gap = H.HeadGap, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Children = [heading, body] };

    /// <summary>The ring (36, the tier's <paramref name="size"/>: 40 at Compact): a filled accent disc (<paramref name="primary"/>) or a 2-dip
    /// outline in the primary ink, its glyph centred.</summary>
    public static BoxEl RingDisc(string glyph, bool primary, float size = H.RingSize) => new()
    {
        Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(size / 2f),
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = primary ? Prop.Of(static () => R.Ink.AccentAction) : ColorF.Transparent,
        BorderWidth = primary ? 0f : H.RingStroke, BorderColor = Tok.TextPrimary,
        HoverFill = primary ? ColorF.Transparent : R.Layer.ChromeHover, PressedFill = primary ? ColorF.Transparent : R.Layer.ChromePressed,
        Children =
        [
            new TextEl(primary && glyph == Icons.Play ? ZunePlayerLayout.PlaySolid : glyph)   // the accent disc's triangle is the filled one (the boards')
            {
                Size = H.RingGlyph, FontFamily = Tok.Definition.Fonts.Icon,
                Color = primary ? Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)) : Tok.TextPrimary,
            },
        ],
    };

    /// <summary>The shelf pager's chevrons as 40 x 40 hit boxes (round G; the engine's circle is 32): pass as <c>parts:</c> to <c>PagedShelf.Create</c>.
    /// The circle keeps its look inside the box, and the box overhangs it by 4 on each side with a negative margin, so the header's layout is the
    /// engine's. The box takes the click, the fade of a disabled chevron and the hand; the circle is the picture.</summary>
    public static TemplateParts ChevronTargets { get; } = MakeChevronTargets();

    private static TemplateParts MakeChevronTargets()
    {
        var parts = new TemplateParts();
        parts[PagedShelf.PartChevronPrev] = static circle => ChevronTarget(circle, DormantPrev);
        parts[PagedShelf.PartChevronNext] = static circle => ChevronTarget(circle, DormantNext);
        return parts;
    }

    private static BoxEl ChevronTarget(BoxEl circle, BoxEl dormant) => circle.OnClick is null ? dormant : new()
    {
        Width = H.ChevronTarget, Height = H.ChevronTarget, Shrink = 0f, Margin = Edges4.All(-(H.ChevronTarget - H.ChevronCircle) / 2f),
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Role = AutomationRole.Button, Cursor = circle.OnClick is null ? CursorId.Arrow : CursorId.Hand,
        OnClick = circle.OnClick, Opacity = circle.Opacity,
        Children = [circle with { OnClick = null, Opacity = 1f, Cursor = CursorId.Arrow }],
    };

    /// <summary>A chevron with nowhere to go takes no room (the board's shelves have none: their eight covers fill the page and "see all" ends the row
    /// on the column's right edge): zero wide, its margin cancelling the header row's item gap before it, and the previous one's also the gap before
    /// the row's spacer (the engine's header row is title, spacer, prev, next, <see cref="H.ShelfHeadGap"/> apart). One node either way (a box), so it
    /// never remounts when paging wakes it.</summary>
    private static readonly BoxEl DormantPrev = new() { Width = 0f, Height = H.ChevronTarget, Shrink = 0f, Margin = new Edges4(-2f * H.ShelfHeadGap, 0f, 0f, 0f), HitTestVisible = false };

    /// <inheritdoc cref="DormantPrev"/>
    private static readonly BoxEl DormantNext = new() { Width = 0f, Height = H.ChevronTarget, Shrink = 0f, Margin = new Edges4(-H.ShelfHeadGap, 0f, 0f, 0f), HitTestVisible = false };

    /// <summary>A ring action: the disc and its lowercase word (none for a glyph-only action, which is named for assistive tech instead).</summary>
    public static Element Ring(string key, string glyph, string label, bool showLabel, bool primary, Action onClick, float size = H.RingSize,
                               float target = 0f, bool stableName = false)
    {
        var kids = new List<Element>(2) { RingDisc(glyph, primary, size) };
        if (showLabel)
            kids.Add(R.Type.TrackTitle(label) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap });
        Element box = new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Gap = H.DaylistActionLabelGap, Shrink = 0f,
            // The hit box (Compact: a 36 disc in a 40 target) is the clickable node; the disc is centred in it.
            Justify = target > 0f ? FlexJustify.Center : FlexJustify.Start, MinWidth = target, MinHeight = target,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, BlocksDragArm = true,
            OnClick = onClick, Children = kids.ToArray(),
        };
        // `stableName`: the ring's shape (wrapper and all) is the same with and without its word, so a tier that drops or shows the word keeps the
        // ring's node (the wrapper is always the tooltip component; its text is empty where the word is on the ring).
        if (stableName)
        {
            string? tip = showLabel ? null : label;
            return ToolTip.Wrap(box, (Prop<string?>)tip);
        }
        return showLabel ? box : Controls.Named(box, label);
    }

    /// <summary>A flat plate (a Browse or mood tile): its own colour (the accent where it has none), or its art where it has no colour, its name
    /// at the bottom left in the on-media ink (15, or the 22 light voice on a <paramref name="big"/> plate, the 40/48 one on a
    /// <paramref name="hero"/> plate). The whole plate opens it.</summary>
    public static Element Plate(string key, string name, uint? colour, string? art, float height, Action open, bool big = false, bool hero = false)
    {
        bool hasColour = colour is { } c && c != 0u;
        bool hasArt = !hasColour && art is { Length: > 0 };
        // A pale category colour takes the dark ink (white on a light plate was the white-on-white tile label): the on-media white holds
        // wherever it reads at 3:1 or better, so the board's saturated plates keep their white word.
        ColorF plate = hasColour ? ColorScience.ToColor(colour!.Value) : default;
        Prop<ColorF> labelInk = hasColour && H.PlateWantsDarkInk(plate)
            ? ColorContrast.NearBlackInk
            : Prop.Of(static () => R.OnMedia.Ink);
        var layers = new List<Element>(3);
        if (hasArt)
        {
            layers.Add(Controls.CoverFill(art, 0f, (int)(height * 2f)));
            // The bottom scrim (0 -> 45 % black over the lower half) keeps the on-media label readable on light art and in the light theme.
            layers.Add(new BoxEl
            {
                Direction = 1, Justify = FlexJustify.End, HitTestVisible = false,
                Children = [new BoxEl { Height = height * H.PlateScrimShare, Shrink = 0f, HitTestVisible = false, Gradient = H.PlateScrim }],
            });
        }
        layers.Add(new BoxEl
        {
            Direction = 1, Justify = FlexJustify.End, MinWidth = 0f, Padding = new Edges4(H.TilePadX, H.TilePadY, H.TilePadX, H.TilePadY),
            HitTestVisible = false,
            Children =
            [
                (hero ? R.Type.HeroTitle(name) : big ? R.Type.QuickplayHead(name) : R.Type.TrackTitle(name)) with
                {
                    Color = labelInk, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                },
            ],
        });
        return new BoxEl
        {
            Key = key, Height = height, MinWidth = 0f, ZStack = true, ClipToBounds = true,
            Corners = R.Shape.All(ShapeRole.Tile),
            Cursor = CursorId.Hand, Role = AutomationRole.Hyperlink, Focusable = true, OnClick = open,
            Children = layers.ToArray(),
        }.Interactive(Interaction.Tile) with
        {
            // The recipe paints the card fill and a card stroke over the plate's own colour (a white plate under the on-media label in the light
            // kind), so the plate's fill and its flat, stroke-less edge go back on after it; hover lifts the plate toward white a step.
            Fill = hasColour ? plate : hasArt ? Prop.Of(static () => R.Layer.FloatingPane) : Prop.Of(static () => Tok.AccentDefault),
            HoverFill = hasColour ? H.PlateHover(plate)
                : hasArt ? Prop.Of(static () => R.Layer.FloatingPane)
                : Prop.Of(static () => H.PlateHover(Tok.AccentDefault)),
            PressedFill = hasColour ? H.PlatePressed(plate)
                : hasArt ? Prop.Of(static () => R.Layer.FloatingPane)
                : Prop.Of(static () => H.PlatePressed(Tok.AccentDefault)),
            BorderWidth = 0f,
        };
    }
}
