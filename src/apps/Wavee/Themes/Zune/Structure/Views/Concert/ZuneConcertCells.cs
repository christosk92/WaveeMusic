// ── Themes/Zune/Structure/Views/Concert/ZuneConcertCells.cs — Zune's concert cells (themes plan 6.3; P5 Z6b2) ────────────────────────────
//
// The pieces the hub, the artist's schedule, the event page and the artist page's shelves share, in Zune's grammar (boards Z-Concert and its
// tiers; the hub and the schedule have no board of their own and are built from Zune's parts, owner Q18):
//
//   · the EVENT TILE: a square 2-radius cover, then the accent date caption, the title at 15 and "venue · city" at 13 — no plate, no border;
//   · the DATE CARD (Z-Concert's related concerts): a light numeral over a lowercase month, the name and the venue beside it, on the raised plate;
//   · the PILLS of the filter bar: round, outlined, 1 dip; the fused accent pill keeps the segment grammar (an accent capsule that carries a raised
//     segment) of the earlier register;
//   · the SECTION HEADING (26/32 light, lowered by the heading case role; a count and a link word beside it);
//   · the page frame: the sub-page scroll column under the band (no masthead: the band is outside the viewport) and the skeleton region.
//
// PRESENTATIONAL ONLY. Every builder is a pure function of VALUES (a tile's text, a date, a label); the hosts read the tables and the page views hand
// these what to paint. The tile's facts are `Concert.TileData` (core: `Concert.TileOf`, `SeedTile`), so a skeleton derives its shimmer from the SAME
// builder the loaded page renders. The numbers are `Rules\ZuneShowRules.cs` (`ZuneConcertRules`); the colours are the style's roles
// (`R.Layer.EventPlate`, `R.Layer.EventHeroPlate`).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using K = Wavee.Themes.Zune.ZuneDetailKit;
using Rules = Wavee.Themes.Zune.ZuneConcertRules;
using FluentGpu.Signals;

namespace Wavee.Themes.Zune;

/// <summary>Everything about a fused pill that is SURFACE, not grammar. The grammar — outer capsule → raised segment → value → trailing glyph, the
/// shared-key morph, the segment dock — is fixed in <see cref="ZuneConcertCells.FusedPill"/>. Every preset is a PROPERTY: <c>Tok.*</c> and
/// <c>Elevation.*</c> are live theme reads, and a cached instance would freeze last theme's colours into the pill.</summary>
internal sealed record ZuneSegmentedPillStyle(
    float Height, float SegmentHeight, Edges4 Padding, Edges4 SegmentPadding, float Gap, float SegmentGap,
    float TextSize, float CheckSize, float TrailingSize,
    ColorF Fill, ColorF HoverFill, ColorF PressedFill,
    ColorF SegmentFill, ShadowSpec SegmentShadow, ColorF SegmentInk,
    ColorF ValueInk, ColorF TrailingInk, string TrailingGlyph)
{
    /// <summary>The accent register at the hub's pill <paramref name="height"/> (32, the Compact 40): the segment keeps its padding inside.</summary>
    public static ZuneSegmentedPillStyle AccentAt(float height)
        => height.Equals(Rules.AccentPillH) ? Accent : Accent with { Height = height, SegmentHeight = height - 2f * Rules.AccentPillPadT };

    /// <summary>A 32-dip accent capsule carrying an OPAQUE raised segment (<c>FillControlSolid</c> — never a translucent card fill, which in dark is
    /// 5 % white and reads as accent-on-accent ink).</summary>
    public static ZuneSegmentedPillStyle Accent => new(
        Height: Rules.AccentPillH, SegmentHeight: Rules.AccentPillSegmentH,
        Padding: new Edges4(Rules.AccentPillPadL, Rules.AccentPillPadT, Rules.AccentPillPadR, Rules.AccentPillPadT),
        SegmentPadding: new Edges4(Rules.AccentPillSegPad, 0f, Rules.AccentPillSegPad, 0f),
        Gap: Rules.AccentPillGap, SegmentGap: Rules.AccentPillSegmentGap, TextSize: Rules.AccentPillText, CheckSize: Rules.AccentPillCheck,
        TrailingSize: Rules.AccentPillTrailing,
        Fill: R.Ink.AccentAction, HoverFill: Tok.AccentSecondary, PressedFill: Tok.AccentTertiary,
        SegmentFill: Tok.FillControlSolid, SegmentShadow: Elevation.Flyout, SegmentInk: R.Ink.AccentAction,
        ValueInk: Tok.TextOnAccentPrimary, TrailingInk: Tok.TextOnAccentPrimary, TrailingGlyph: Icons.ChevronDown);
}

/// <summary>Zune's concert cells.</summary>
internal static class ZuneConcertCells
{
    static readonly Edges4 s_focusMargin = Edges4.All(Rules.FocusMargin);

    // ══ 1. THE EVENT TILE AND BROWSE-ALL ═════════════════════════════════════════════════════════════════════════════

    /// <summary>The vertical event card: the cover, then three single lines whose heights are STATED (the lazy grid's row extra is the arithmetic of
    /// these: <see cref="Rules.TileChrome"/> over the cover). Flat on the ground; the wash is the hover. No play, no drag.</summary>
    public static Element Tile(in Concert.TileData d, Action? onClick)
    {
        var text = new Element[d.Place.Length > 0 ? 3 : 2];
        text[0] = R.Type.MicroMeta(d.Caption) with
        {
            Size = Rules.TileCaptionSize, LineHeight = Rules.TileCaptionLine, Height = Rules.TileCaptionLine, Color = Prop.Of(static () => R.Ink.AccentDecor),
            MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
        };
        text[1] = R.Type.TrackTitle(d.Title) with
        {
            LineHeight = Rules.TileTitleLine, Height = Rules.TileTitleLine, MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        };
        if (d.Place.Length > 0)
            text[2] = R.Type.TrackMeta(d.Place) with
            {
                LineHeight = Rules.TilePlaceLine, Height = Rules.TilePlaceLine, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            };
        return new BoxEl
        {
            Key = "concert:" + d.Key,
            Direction = 1, MinWidth = 0f, Gap = Rules.TileGap,
            HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, FocusVisualMargin = s_focusMargin,
            Cursor = CursorId.Hand, OnClick = onClick,
            Children = [TileArt(d.ImageUrl, d.Accent), new BoxEl { Direction = 1, MinWidth = 0f, Gap = Rules.TileTextGap, Children = text }],
        };
    }

    /// <summary>The square: the cover at the Cover shape, or the accent-tinted plate with a centred calendar.</summary>
    static Element TileArt(string? url, uint accent)
    {
        if (url is { Length: > 0 })
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, AspectRatio = 1f, ZStack = true, ClipToBounds = true, Corners = R.Shape.All(ShapeRole.Cover),
                Children = [Ui.Image(url, ImageFit.Cover, 1f, Rules.TileArtDecode, R.Shape.For(ShapeRole.Cover), Tok.FillCardSecondary)],
            };
        ColorF baseFill = R.Layer.EventPlate;
        return new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, AspectRatio = 1f, Corners = R.Shape.All(ShapeRole.Cover),
            Fill = accent != 0 ? ColorF.Lerp(baseFill, ColorScience.ToColor(accent), Rules.TileAccentMix) : baseFill,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Icon(Icons.Calendar, Rules.TileGlyph, Tok.TextSecondary with { A = Rules.GlyphAlpha })],
        };
    }

    /// <summary>The related shelf's lead cell: the tile footprint over a layered glyph pane routing to the hub.</summary>
    public static Element BrowseAllCard(Action onClick)
    {
        ColorF glyph = Tok.TextSecondary with { A = Rules.BrowseAllGlyphAlpha };
        return new BoxEl
        {
            Key = "browse-all-concerts",
            Direction = 1, MinWidth = 0f, Gap = Rules.TileGap,
            HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
            Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, FocusVisualMargin = s_focusMargin, Cursor = CursorId.Hand,
            OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, AspectRatio = 1f, ZStack = true, ClipToBounds = true,
                    Corners = R.Shape.All(ShapeRole.Cover), Fill = Prop.Of(static () => R.Layer.EventPlate),
                    Children =
                    [
                        new BoxEl { Padding = Edges4.All(Spacing.M), AlignItems = FlexAlign.Start, Justify = FlexJustify.Start, Children = [Icon(Icons.Calendar, Rules.BrowseAllSmallGlyph, glyph)] },
                        new BoxEl { AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.MapPin, Rules.BrowseAllPinGlyph, glyph)] },
                        new BoxEl { Padding = Edges4.All(Spacing.M), AlignItems = FlexAlign.End, Justify = FlexJustify.End, Children = [Icon(Icons.Calendar, Rules.BrowseAllTinyGlyph, glyph)] },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Gap = Rules.TileTextGap,
                    Children =
                    [
                        R.Type.MicroMeta(Loc.Get(Strings.Concerts.LiveMusic)) with
                        {
                            Size = Rules.TileCaptionSize, LineHeight = Rules.TileCaptionLine, Height = Rules.TileCaptionLine, Color = Prop.Of(static () => R.Ink.AccentDecor),
                            Case = R.Case.For(LabelKind.Label), MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                        },
                        R.Type.TrackTitle(Loc.Get(Strings.Concerts.BrowseAll)) with
                        {
                            Case = R.Case.For(LabelKind.Label), LineHeight = Rules.TileTitleLine, Height = Rules.TileTitleLine, MinWidth = 0f, Wrap = TextWrap.NoWrap,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            ],
        };
    }

    // ══ 2. HEADINGS, THE DATE CARD, THE LOCATION BUTTON ══════════════════════════════════════════════════════════════

    /// <summary>A section heading (26/32 light, lowered): the word, an optional quiet count beside it, an optional link word at the far end.</summary>
    public static Element SectionHead(string title, string? count = null, Element? link = null)
    {
        var kids = new List<Element>(4) { K.Heading(title) };
        if (count is { Length: > 0 })
            kids.Add(R.Type.DenseMeta(count) with
            {
                Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f,
                Margin = new Edges4(0f, 0f, 0f, Rules.CountLift),
            });
        if (link is not null)
        {
            kids.Add(new BoxEl { Grow = 1f, MinWidth = Spacing.M });
            kids.Add(link);
        }
        return new BoxEl
        {
            Direction = 0, Gap = Rules.HeadGap, AlignItems = FlexAlign.End, MinWidth = 0f, Shrink = 0f, Children = kids.ToArray(),
        };
    }

    /// <summary>The date numeral over its lowercase month (Z-Concert's related card: a 32 light numeral over an 11 month), centred in a fixed column
    /// so the words beside it never move.</summary>
    public static Element DateColumn(DateTimeOffset date, float width, float daySize, float dayLine)
    {
        var culture = CultureInfo.CurrentCulture;
        return new BoxEl
        {
            Width = width, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                R.Type.Panorama(date.Day.ToString(culture)) with
                {
                    Size = daySize, LineHeight = dayLine, CharSpacing = 0f, Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
                R.Type.MicroMeta(date.ToString("MMM", culture)) with
                {
                    Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
            ],
        };
    }

    /// <summary>The hero's location control: a round 1-dip outline of the pill height, a pin, the place, a chevron; a <see cref="BoxEl"/> so the caller
    /// attaches its anchor capture.</summary>
    public static BoxEl LocationButton(string label, Action onClick, float height) => new()
    {
        Direction = 0, Grow = 1f, Basis = 0f, MinHeight = height, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        AlignItems = FlexAlign.Center, Gap = Spacing.S, Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
        Corners = CornerRadius4.All(height / 2f),
        HoverFill = Prop.Of(static () => R.Layer.ChromeHover), PressedFill = Prop.Of(static () => R.Layer.ChromePressed),
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new BoxEl { Width = Rules.LocationGlyphBox, Height = Rules.LocationGlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.MapPin, Rules.LocationPin, Tok.TextSecondary)] },
            R.Type.DenseTitle(label) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis },
            new BoxEl { Width = Rules.LocationChevronBoxW, Height = Rules.LocationGlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.ChevronDown, Rules.PillChevron, Tok.TextSecondary)] },
        ],
    };

    // ══ 3. THE FILTER BAR'S PILLS AND TOKENS ═════════════════════════════════════════════════════════════════════════

    static LayoutTransition WidthReflow(float ms) => new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(ms, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    static readonly LayoutTransition s_tokenReflow = WidthReflow(Rules.TokenReflowMs), s_pillReflow = WidthReflow(Rules.PillReflowMs);

    /// <summary>The segment docks in FROM the chip's side as the chip exits toward the pill.</summary>
    static readonly LayoutTransition s_segmentDock = new(
        TransitionChannels.Position | TransitionChannels.Opacity, TransitionDynamics.Tween(Rules.SegmentDockMs, Easing.SmoothOut),
        Enter: new EnterExit(Dx: Rules.ChipTravel, Opacity: Rules.SegmentDockFrom, Active: true));

    /// <summary>The "this weekend" chip's exit leg: INTO the pill, accelerating.</summary>
    public static readonly LayoutTransition ChipExit = new(
        TransitionChannels.Position | TransitionChannels.Opacity, TransitionDynamics.Tween(Rules.ChipExitMs, Easing.FluentAccelerate),
        Exit: new EnterExit(Dx: -Rules.ChipTravel, Opacity: 0f, Active: true));

    /// <summary>A multi-select genre token: the stock <see cref="ToggleButton.Controlled"/> (the style makes it Zune's) — no check glyph, so the strip
    /// never walks under the cursor. The label is the key (a re-ordered list reuses nodes); <paramref name="onClick"/> ignores the toggle's own
    /// next-state argument because the PAGE, not the control, owns the multi-select set. A genre is a name the catalogue wrote: as written.</summary>
    /// <param name="animate">Overrides the token's own reflow motion (the "this weekend" chip's exit-into-the-pill leg, <see cref="ChipExit"/>).</param>
    /// <param name="size">The toggle's size: Large is the Compact 40 target (the hub's pill height there).</param>
    public static Element FilterToken(string label, bool selected, Action onClick, LayoutTransition? animate = null, ControlSize size = ControlSize.Medium)
        => ToggleButton.Controlled(label, selected, _ => onClick(), parts: animate is null ? s_tokenParts : TokenParts(animate.Value), size: size)
            with { Key = "filter-token:" + label };

    static readonly TemplateParts s_tokenParts = TokenParts(s_tokenReflow);

    static TemplateParts TokenParts(LayoutTransition animate) => new()
    {
        [ToggleButton.PartRoot] = b => b with { Shrink = 0f, Animate = animate },
    };

    /// <summary>"+N genres" / "show less": the link grammar — a <see cref="HyperlinkButton"/>, not a plated token; the chevron rides alongside it as a
    /// plain icon (a hyperlink has no icon slot).</summary>
    public static Element MoreToken(string label, bool expanded, Action onClick, float height) => new BoxEl
    {
        Key = "genre-more", Animate = s_tokenReflow,
        Direction = 0, Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Rules.PillGap,
        Children =
        [
            HyperlinkButton.Create(label, onClick),
            Icon(expanded ? Icons.ChevronUp : Icons.ChevronDown, Rules.PillChevron, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>The pill's shared chrome: round, a 1-dip outline, the wash for hover and press, a hand.</summary>
    static BoxEl Pill(float height, Action onClick) => new()
    {
        Direction = 0, Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Rules.PillGap,
        Padding = new Edges4(Rules.PillPadX, 0f, Rules.PillPadX, 0f), Corners = CornerRadius4.All(height / 2f),
        HoverFill = Prop.Of(static () => R.Layer.ChromeHover), PressedFill = Prop.Of(static () => R.Layer.ChromePressed),
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
    };

    /// <summary>The where pill: a pin, the label, a chevron; <paramref name="height"/> tall (the tier's: 32, the Compact 40). The place is a name as
    /// written (the label carries the radius and the "approximate" words, which are loc text lowered by the label case).</summary>
    public static BoxEl WherePill(string label, Action onClick, float height) => Pill(height, onClick) with
    {
        Children =
        [
            Icon(Icons.MapPin, Rules.PillGlyph, Tok.TextSecondary) with { Shrink = 0f },
            R.Type.DenseTitle(label) with { Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis },
            Icon(Icons.ChevronDown, Rules.PillChevron, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>The when-area at rest: a calendar, "dates", a chevron — keyed <c>when-pill</c>, the SAME key as the fused pill, so the node is reused
    /// and its width reflows.</summary>
    public static BoxEl RestDatePill(Action onClick, float height) => Pill(height, onClick) with
    {
        Key = "when-pill", Animate = s_pillReflow,
        Children =
        [
            Icon(Icons.Calendar, Rules.PillGlyph, Tok.TextSecondary) with { Shrink = 0f },
            R.Type.DenseTitle(Loc.Get(Strings.Concerts.Filter.Dates)) with
            {
                Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
            },
            Icon(Icons.ChevronDown, Rules.PillChevron, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>THE FUSED PILL (the grammar): outer capsule → the raised segment (✓ name) docking in → the value → the trailing glyph.
    /// <paramref name="key"/> MUST equal the loose shape's key at the call site — the shared key is the whole morph; the segment is keyed
    /// <c>{key}:seg</c>.</summary>
    public static BoxEl FusedPill(string key, ZuneSegmentedPillStyle s, string name, string value, Action onClick) => new()
    {
        Key = key, Animate = s_pillReflow,
        Direction = 0, Height = s.Height, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = s.Gap,
        Padding = s.Padding, Corners = CornerRadius4.All(s.Height / 2f),
        Fill = s.Fill, HoverFill = s.HoverFill, PressedFill = s.PressedFill,
        Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new BoxEl
            {
                Key = key + ":seg", Animate = s_segmentDock,
                Direction = 0, Height = s.SegmentHeight, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = s.SegmentGap,
                Padding = s.SegmentPadding, Corners = CornerRadius4.All(s.SegmentHeight / 2f),
                Fill = s.SegmentFill, Shadow = s.SegmentShadow,
                Children =
                [
                    Icon(Icons.Check, s.CheckSize, s.SegmentInk) with { Shrink = 0f },
                    new TextEl(name) { Size = s.TextSize, Weight = 600, Color = s.SegmentInk, MaxLines = 1 },
                ],
            },
            new TextEl(value) { Size = s.TextSize, Weight = 600, Color = s.ValueInk, MaxLines = 1 },
            Icon(s.TrailingGlyph, s.TrailingSize, s.TrailingInk) with { Shrink = 0f },
        ],
    };

    /// <summary>The 1×20 group divider of the filter row.</summary>
    public static Element BarDivider() => new BoxEl { Width = 1f, Height = Rules.BarDividerH, Shrink = 0f, Fill = Tok.StrokeSurfaceDefault };

    // ══ 4. THE PAGE FRAME ════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The schedule/detail page frame: ONE vertical viewport whose body stands on the frame's <paramref name="gutter"/>, centred and capped at
    /// 2400 (P5 decision 2), ending past the player dock. The band is outside the viewport, so nothing is cut or reserved under it.</summary>
    public static ScrollEl SubPageScroll(string scrollKey, Element region, float gutter) => ScrollView(PageColumn(region, gutter)) with
    {
        Grow = 1f, MinHeight = 0f, ScrollKey = scrollKey,
    };

    /// <summary>The centred, capped page column (the same stance as the detail frame's): a row that centres a column of at most the page cap plus its
    /// gutters, the column holding <paramref name="body"/> between the gutters.</summary>
    public static Element PageColumn(Element body, float gutter) => new BoxEl
    {
        Direction = 0, MinWidth = 0f, Justify = FlexJustify.Center,
        Children =
        [
            new BoxEl
            {
                Direction = 1, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = ZuneDetailRules.PageCap + 2f * gutter,
                Padding = new Edges4(gutter, Rules.PageTop, gutter, R.Space.DockReserve + Rules.PageBottom), Children = [body],
            },
        ],
    };

    /// <summary>A region that is its shimmer seed while pending and the content once ready (soft reveal).</summary>
    public static Element Region(Func<bool> pending, Func<bool> failed, Func<Element> content, Func<Element> shimmer, Func<Element>? onFailed, string group)
        => new SkelRegionEl(pending, failed, content, shimmer, onFailed, SkelReveal.Soft, SkeletonStyle.Default, group, SmoothResize: false);
}
