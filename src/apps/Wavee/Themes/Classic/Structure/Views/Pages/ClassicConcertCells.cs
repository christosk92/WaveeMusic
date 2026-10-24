// ── Themes/Classic/Structure/Views/Pages/ClassicConcertCells.cs — Classic's concert cells (themes plan 6.1, T10b3) ────────────────────────────
//
// From `Entities\Concert.UI.cs` §1-§4 (0.2.9 `Components/ConcertUi.cs`: EventTile, BrowseAllCard, DateBlock, SplitEditorialHero, LocationButton, FilterToken,
// WherePill, RestDatePill, SegmentedPill + SegmentedPillStyle, MoreToken) and the page frame pieces `Concert.Page.cs` §2 (the under-band cut, the sub-page scroll
// frame, the skeleton region): the pieces the hub, the artist's schedule, the event page and the artist page's "Upcoming concerts" shelf share.
//
// PRESENTATIONAL ONLY. Every builder is a pure function of VALUES (a tile's text, a date, a label); the hosts read the tables and the page views hand these what to
// paint. The tile's facts are `Concert.TileData` (core: `Concert.TileOf`, `SeedTile`), so a skeleton derives its shimmer from the SAME builder the loaded page
// renders (§0 #11). The numbers are `Rules\ClassicConcertRules.cs`; the colours are the style's roles (`R.Layer.EventPlate`, `R.Layer.EventHeroPlate`,
// `R.Wash.ConcertMediaMix`: P21 — the two `DarkOr` branches of the old file).
//
// THE SEGMENTED-PILL GRAMMAR (`FusedPill` + `SegmentedPillStyle`) is Concerts' only user today (the Accent register); the old `Strip` register, the Home tab-strip's, had no caller
// and was not moved (Home's fused facet pill, ch 10 §3, brings its own).

using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Classic.ClassicConcertRules;

namespace Wavee.Themes.Classic;

/// <summary>Everything about a fused pill that is SURFACE, not grammar (0.2.9 <c>SegmentedPillStyle</c>, verbatim). The
/// grammar — outer capsule → raised segment → value → trailing glyph, the shared-key morph, the segment dock — is fixed
/// in <see cref="ClassicConcertCells.FusedPill"/>. Every preset is a PROPERTY: <c>Tok.*</c> and <c>Elevation.*</c> are live theme
/// reads, and a cached instance would freeze last theme's colours into the pill.</summary>
internal sealed record SegmentedPillStyle(
    float Height, float SegmentHeight, Edges4 Padding, Edges4 SegmentPadding, float Gap, float SegmentGap,
    float TextSize, float CheckSize, float TrailingSize,
    ColorF Fill, ColorF HoverFill, ColorF PressedFill,
    ColorF SegmentFill, ShadowSpec SegmentShadow, ColorF SegmentInk,
    ColorF ValueInk, ColorF TrailingInk, string TrailingGlyph,
    float BorderWidth = 0f, ColorF BorderColor = default)
{
    /// <summary>The Concerts register at the hub's pill <paramref name="height"/> (the tuned 32, the Compact 40): the segment keeps its padding inside.</summary>
    public static SegmentedPillStyle AccentAt(float height)
        => height.Equals(Rules.AccentPillH) ? Accent : Accent with { Height = height, SegmentHeight = height - 2f * Rules.AccentPillPadT };

    /// <summary>The Concerts register: a 32-DIP accent capsule carrying an OPAQUE raised segment (<c>FillControlSolid</c>
    /// — never <c>FillCardDefault</c>, which in dark is 5 % white and read as accent-on-accent ink).</summary>
    public static SegmentedPillStyle Accent => new(
        Height: Rules.AccentPillH, SegmentHeight: Rules.AccentPillSegmentH,
        Padding: new Edges4(Rules.AccentPillPadL, Rules.AccentPillPadT, Rules.AccentPillPadR, Rules.AccentPillPadT),
        SegmentPadding: new Edges4(Rules.AccentPillSegPad, 0f, Rules.AccentPillSegPad, 0f),
        Gap: Rules.AccentPillGap, SegmentGap: Rules.AccentPillSegmentGap, TextSize: Rules.AccentPillText, CheckSize: Rules.AccentPillCheck,
        TrailingSize: Rules.AccentPillTrailing,
        Fill: Tok.AccentDefault, HoverFill: Tok.AccentSecondary, PressedFill: Tok.AccentTertiary,
        SegmentFill: Tok.FillControlSolid, SegmentShadow: Elevation.Flyout, SegmentInk: Tok.AccentTextPrimary,
        ValueInk: Tok.TextOnAccentPrimary, TrailingInk: Tok.TextOnAccentPrimary, TrailingGlyph: Icons.ChevronDown);
}

/// <summary>Wavee Classic's concert cells.</summary>
internal static class ClassicConcertCells
{
    static readonly Edges4 s_focusMargin = Edges4.All(Rules.FocusMargin);

    // ══ 1. THE EVENT TILE, THE STUB, BROWSE-ALL (W1, W19, W23; §0 #6) ════════════════════════════════════════════════

    /// <summary>The vertical event card (0.2.9 <c>EventTile</c>): height = cell width + <see cref="ClassicConcertTiers.EventChrome"/>
    /// exactly, every line single-line clamped, <c>Interaction.Subtle</c>, a 2-DIP focus margin. No play, no drag (§0 #12).</summary>
    public static Element Tile(in Concert.TileData d, Action? onClick)
    {
        var text = new Element[d.Place.Length > 0 ? 3 : 2];
        text[0] = R.Type.Eyebrow(d.Caption) with
        { Color = Tok.AccentTextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        text[1] = R.Type.TrackTitle(d.Title) with
        { MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        if (d.Place.Length > 0)
            text[2] = R.Type.TrackMeta(d.Place) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        return new BoxEl
        {
            Key = "concert:" + d.Key,
            Direction = 1, MinWidth = 0f, ClipToBounds = true, Gap = Spacing.S,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M), Corners = Radii.CardAll,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = s_focusMargin,
            Cursor = CursorId.Hand, OnClick = onClick,
            Children = [TileArt(d.ImageUrl, d.Accent), new BoxEl { Direction = 1, MinWidth = 0f, Gap = Rules.TileTextGap, Children = text }],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The rounded-top square: the cover, or the accent-tinted pane (§4 row 1) with a centred 38-DIP calendar.</summary>
    static Element TileArt(string? url, uint accent)
    {
        if (url is { Length: > 0 })
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true, Corners = Radii.CardAll,
                Children = [Ui.Image(url, ImageFit.Cover, 1f, Rules.TileArtDecode, Radii.Card, Tok.FillCardSecondary)],
            };
        ColorF baseFill = R.Layer.EventPlate;
        return new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, AspectRatio = 1f, Corners = Radii.CardAll,
            Fill = accent != 0 ? ColorF.Lerp(baseFill, ColorScience.ToColor(accent), Rules.TileAccentMix) : baseFill,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Icon(Icons.Calendar, Rules.TileGlyph, Tok.TextSecondary with { A = Rules.GlyphAlpha })],
        };
    }

    // The §4 base fills that are NOT token swaps (a fixed near-black in dark, <c>FillCardSecondary</c> in light) are the style's LAYER roles
    // <c>R.Layer.EventPlate</c> (the tile and the card) and <c>R.Layer.EventHeroPlate</c> (the split hero).

    /// <summary>The artist page's "Upcoming concerts" card (0.2.9 <c>ArtistPage.ConcertStub</c>, ch 08 §9): the accent month
    /// over a 28/36 day numeral, venue and pin + city. <paramref name="width"/> is the shelf's fitted card width.</summary>
    public static Element Stub(Concert c, Action onClick, float width)
    {
        var date = c.LocalDate;
        var (venue, city) = Concert.StubFacts(c);
        return new BoxEl
        {
            Key = "concert-stub:" + c.Slot.ToString(CultureInfo.InvariantCulture),
            Direction = 0, Width = width, Shrink = 0f, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = Edges4.All(Spacing.M), Corners = Radii.CardAll, Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = s_focusMargin, Cursor = CursorId.Hand,
            OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, Width = Rules.StubDateW, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        R.Type.Eyebrow(date.ToString("MMM", CultureInfo.CurrentCulture)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
                        new TextEl(date.Day.ToString(CultureInfo.CurrentCulture)) { Size = Rules.StubDaySize, LineHeight = Rules.StubDayLine, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Rules.StubTextGap,
                    Children =
                    [
                        new TextEl(venue)
                        { Size = Rules.StubVenueSize, LineHeight = Rules.StubVenueLine, Weight = 600, Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        {
                            Direction = 0, Gap = Rules.StubPinGap, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Children =
                            [
                                Icon(Icons.MapPin, Rules.StubPinSize, Tok.TextSecondary) with { Shrink = 0f },
                                new TextEl(city) { Size = Rules.StubCitySize, LineHeight = Rules.StubCityLine, Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The related shelf's lead cell (W19): the tile footprint over a layered glyph pane — Calendar 30 top-left,
    /// MapPin 44 centred, Calendar 22 bottom-right, all at <c>TextSecondary</c> α 0.30 — routing to the hub.</summary>
    public static Element BrowseAllCard(Action onClick)
    {
        ColorF glyph = Tok.TextSecondary with { A = Rules.BrowseAllGlyphAlpha };
        return new BoxEl
        {
            Key = "browse-all-concerts",
            Direction = 1, MinWidth = 0f, ClipToBounds = true, Gap = Spacing.S,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M), Corners = Radii.CardAll,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = s_focusMargin, Cursor = CursorId.Hand,
            OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    AlignSelf = FlexAlign.Stretch, AspectRatio = 1f, ZStack = true, ClipToBounds = true,
                    Corners = Radii.CardAll, Fill = R.Layer.EventPlate,
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
                        R.Type.Eyebrow(Loc.Get(Strings.Concerts.LiveMusic)) with { Color = Tok.AccentTextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        R.Type.TrackTitle(Loc.Get(Strings.Concerts.BrowseAll)) with { MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The accent eyebrow every concert section heads with (§4 row 11).</summary>
    public static TextEl SectionCaption(string label)
        => R.Type.Eyebrow(label) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

    // ══ 2. DATE BLOCKS, THE SPLIT HERO, THE LOCATION BUTTON (W13, W13b/c, W19) ════════════════════════════════════════

    /// <summary>56×60 (compact 48×52): a neutral plate, the accent month over the primary day — the stored clock.</summary>
    public static Element DateBlock(DateTimeOffset date, bool compact = false)
    {
        var culture = CultureInfo.CurrentCulture;
        return new BoxEl
        {
            Width = compact ? Rules.DateBlockCompactW : Rules.DateBlockW, Height = compact ? Rules.DateBlockCompactH : Rules.DateBlockH, Shrink = 0f, Gap = 1f,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                R.Type.Eyebrow(date.ToString("MMM", culture)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
                BodyStrong(date.Day.ToString(culture)) with { Color = Tok.TextPrimary, MaxLines = 1 },
            ],
        };
    }

    /// <summary>The grounded split card (§0 #8): copy on <c>FillCardDefault</c>, the photo in an adjacent clipped pane with a
    /// seam gradient only. Wide is a 56/44 row at 320; narrow stacks a 180-DIP pane above the copy. No photo ⇒ the
    /// accent-tinted plate with ONE centred 52-DIP calendar (W13c); no accent ⇒ the flat base (W13b).</summary>
    public static Element SplitHero(string? imageUrl, uint accent, Element copy, bool wide)
        => SplitHero(imageUrl, accent, copy, ClassicConcertTiers.EditorialHero(wide));

    /// <summary>The split card at the tier's <paramref name="metrics"/> (the event page's records, TC6: split at 240 at Small and at 400 at Huge, the 220 stacked
    /// pane at Compact): a row when the metrics have a height of their own, the stacked pane otherwise.</summary>
    public static Element SplitHero(string? imageUrl, uint accent, Element copy, EditorialHeroMetrics metrics)
    {
        bool wide = metrics.Split;
        ColorF baseFill = R.Layer.EventHeroPlate;
        ColorF mediaFill = accent != 0 ? ColorF.Lerp(baseFill, ColorScience.ToColor(accent), R.Wash.ConcertMediaMix) : baseFill;

        Element photo = imageUrl is { Length: > 0 }
            ? new ImageEl
            {
                Source = imageUrl, Fit = ImageFit.Cover, FocusY = Rules.HeroFocusY, DecodePx = Rules.HeroDecode, Placeholder = mediaFill,
                RevealTransition = ImageTransition.Fade(Rules.HeroRevealMs),
            }
            : new BoxEl
            {
                Fill = mediaFill, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Calendar, Rules.HeroGlyph, Tok.TextSecondary with { A = Rules.GlyphAlpha })],
            };
        Element seam = new BoxEl
        {
            HitTestPassThrough = true,
            Gradient = wide
                ? LinearGradient(0f, new GradientStop(0f, Tok.FillCardDefault), new GradientStop(Rules.HeroSeamWide, Tok.FillCardDefault with { A = 0f }))
                : GradientDown(new GradientStop(Rules.HeroSeamNarrow, Tok.FillCardDefault with { A = 0f }), new GradientStop(1f, Tok.FillCardDefault)),
        };
        Element media = new BoxEl
        {
            Grow = wide ? metrics.MediaFraction : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f,
            Height = metrics.MediaHeight, ZStack = true, ClipToBounds = true, Children = [photo, seam],
        };
        Element grounded = new BoxEl
        {
            Direction = 1, Grow = wide ? Rules.HeroCopyGrow : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f,
            Padding = Edges4.All(metrics.Padding), Justify = FlexJustify.Center, Children = [copy],
        };
        return new BoxEl
        {
            Direction = (byte)(wide ? 0 : 1), MinWidth = 0f, Height = wide ? metrics.Height : float.NaN,
            ClipToBounds = true, Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = wide ? [grounded, media] : [media, grounded],
        };
    }

    /// <summary>The hero's location control (§3): MinH 32, pad (12,4,12,4), r4, the control ramp, the ELEVATION border
    /// brush — the one concert control that uses it. A <see cref="BoxEl"/> so the caller attaches its anchor capture.</summary>
    public static BoxEl LocationButton(string label, Action onClick) => new()
    {
        Direction = 0, Grow = 1f, Basis = 0f, MinHeight = Rules.LocationMinH, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        AlignItems = FlexAlign.Center, Gap = Spacing.S, Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
        Corners = Radii.ControlAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new BoxEl { Width = Rules.LocationGlyphBox, Height = Rules.LocationGlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.MapPin, Rules.LocationPin, Tok.TextSecondary)] },
            BodyStrong(label) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new BoxEl { Width = Rules.LocationChevronBoxW, Height = Rules.LocationGlyphBox, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.ChevronDown, Rules.PillChevron, Tok.TextSecondary)] },
        ],
    };

    // ══ 3. THE FILTER BAR'S PILLS AND TOKENS (W6; §0 #3, #4) ═════════════════════════════════════════════════════════

    static LayoutTransition WidthReflow(float ms) => new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(ms, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    static readonly LayoutTransition s_tokenReflow = WidthReflow(Rules.TokenReflowMs), s_pillReflow = WidthReflow(Rules.PillReflowMs);

    /// <summary>The segment docks in FROM the chip's side (Dx +56, α 0.4, 300 ms) as the chip exits toward the pill.</summary>
    static readonly LayoutTransition s_segmentDock = new(
        TransitionChannels.Position | TransitionChannels.Opacity, TransitionDynamics.Tween(Rules.SegmentDockMs, Easing.SmoothOut),
        Enter: new EnterExit(Dx: Rules.ChipTravel, Opacity: Rules.SegmentDockFrom, Active: true));

    /// <summary>The "This weekend" chip's exit leg: INTO the pill (Dx −56), 220 ms FluentAccelerate.</summary>
    public static readonly LayoutTransition ChipExit = new(
        TransitionChannels.Position | TransitionChannels.Opacity, TransitionDynamics.Tween(Rules.ChipExitMs, Easing.FluentAccelerate),
        Exit: new EnterExit(Dx: -Rules.ChipTravel, Opacity: 0f, Active: true));

    /// <summary>A multi-select genre token: the stock <see cref="ToggleButton.Controlled"/> (Workstream B's "filter /
    /// mode toggles" row — 32/r4, checked = accent, WinUI's own state ramp) — no check glyph, so the strip never walks
    /// under the cursor. The label is the key (a re-ordered list reuses nodes); <paramref name="onClick"/> ignores the
    /// toggle's own next-state argument because the PAGE, not the control, owns the multi-select set.
    /// <para><c>// Workstream B</c>: was a hand-rolled capsule (<c>Radii.FullAll</c>, custom hover/press fills); the
    /// engine's own toggle ramp draws the identical selected/unselected states now.</para></summary>
    /// <param name="animate">Overrides the token's own reflow motion (e.g. the "This weekend" chip's exit-into-the-
    /// pill leg, <see cref="ChipExit"/>) — <c>Animate</c> rides the toggle's own root via <c>Parts[PartRoot]</c>
    /// (F20/control-fidelity §6), never a caller-side <c>with { Animate = … }</c>: the return type here is the base
    /// <see cref="Element"/>, and <c>Animate</c> is a <c>BoxEl</c>-only property.</param>
    /// <param name="size">The toggle's size: Large is the Compact 40 target (the hub's pill height there).</param>
    public static Element FilterToken(string label, bool selected, Action onClick, LayoutTransition? animate = null, ControlSize size = ControlSize.Medium)
        // Key + Shrink + the width reflow ride the toggle's own root via Parts[PartRoot] (F20/control-fidelity §6) —
        // no one-child host box.
        => ToggleButton.Controlled(label, selected, _ => onClick(), parts: animate is null ? s_tokenParts : TokenParts(animate.Value), size: size)
            with { Key = "filter-token:" + label };

    /// <inheritdoc cref="FilterToken"/>
    static readonly TemplateParts s_tokenParts = TokenParts(s_tokenReflow);

    static TemplateParts TokenParts(LayoutTransition animate) => new()
    {
        [ToggleButton.PartRoot] = b => b with { Shrink = 0f, Animate = animate },
    };

    /// <summary>"+N genres" / "Show less": Workstream B's LINK grammar — a <see cref="HyperlinkButton"/>, not a plated
    /// token; the chevron rides alongside it as a plain icon (a hyperlink has no icon slot).
    /// <para><c>// Workstream B</c>: was a dashed-border pill (<c>Interaction.Subtle</c>); "+N genres" reads as
    /// navigation, not a toggle, so it belongs on the link rung, not the button ladder.</para></summary>
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

    /// <summary>The where pill: MapPin 14, the label (BodyStrong), chevron 10; <paramref name="height"/> tall (the tier's: 32, the Compact 40), fully
    /// rounded, the control ramp.</summary>
    public static BoxEl WherePill(string label, Action onClick, float height) => new()
    {
        Direction = 0, Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Rules.PillGap,
        Padding = new Edges4(Rules.PillPadX, Rules.PillPadY, Rules.PillPadX, Rules.PillPadY), Corners = Radii.FullAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Icon(Icons.MapPin, Rules.PillGlyph, Tok.TextSecondary) with { Shrink = 0f },
            BodyStrong(label) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Icon(Icons.ChevronDown, Rules.PillChevron, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>The when-area at rest: Calendar, "Dates", chevron — keyed <c>when-pill</c>, the SAME key as the fused
    /// pill, so the node is reused and its width reflows over 260 ms (§9 #10).</summary>
    public static BoxEl RestDatePill(Action onClick, float height) => new()
    {
        Key = "when-pill", Animate = s_pillReflow,
        Direction = 0, Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = Rules.PillGap,
        Padding = new Edges4(Rules.PillPadX, Rules.PillPadY, Rules.PillPadX, Rules.PillPadY), Corners = Radii.FullAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Icon(Icons.Calendar, Rules.PillGlyph, Tok.TextSecondary) with { Shrink = 0f },
            Body(Loc.Get(Strings.Concerts.Filter.Dates)) with { Color = Tok.TextPrimary, MaxLines = 1 },
            Icon(Icons.ChevronDown, Rules.PillChevron, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>THE FUSED PILL (the grammar): outer capsule → the raised segment (✓ name) docking in → the value → the
    /// trailing glyph. <paramref name="key"/> MUST equal the loose shape's key at the call site — the shared key is the
    /// whole morph (§9 #10); the segment is keyed <c>{key}:seg</c>.</summary>
    public static BoxEl FusedPill(string key, SegmentedPillStyle s, string name, string value, Action onClick) => new()
    {
        Key = key, Animate = s_pillReflow,
        Direction = 0, Height = s.Height, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = s.Gap,
        Padding = s.Padding, Corners = Radii.FullAll,
        Fill = s.Fill, HoverFill = s.HoverFill, PressedFill = s.PressedFill,
        BorderWidth = s.BorderWidth, BorderColor = s.BorderColor,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
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

    // ══ 4. THE PAGE FRAME (Concert.Page.cs §2) ═══════════════════════════════════════════════════════════════════════

    /// <summary>A node that scrolls under the masthead: cut at the trail's reserve (the Concerts pages' heads hold the trail only), feathered 24 DIP only while the cut
    /// is engaged (§4 row 10: nothing is softened at rest) — the clip's own composite-time feather (EdgeFadeSpec.WhileStuck),
    /// switched on the render turn that poses the cut. It used to be a re-render off the clip's engaged signal, which lands
    /// two presents after the cut (RCA 2026-09-25 F(ii)).</summary>
    public static Element UnderBand(BoxEl node)
        => (node with { EdgeFade = new EdgeFadeSpec(EdgeMask.Top, BrowseMastheadMetrics.ClipFadeBand) { WhileStuck = true } })
            .StickyClip(ClassicPageHeadRules.HeadReserve(PageHeadForm.Hero));

    /// <summary>The schedule/detail page frame (§1.2, §1.3): the trail's reserve + 12 as a SPACER (the page's hero is its title), then
    /// the body inset by the page <paramref name="gutter"/> and cut under the band, one vertical viewport.</summary>
    public static ScrollEl SubPageScroll(string scrollKey, Element region, float gutter) => ScrollView(new BoxEl
    {
        Direction = 1,
        Children =
        [
            new BoxEl { Height = ClassicPageHeadRules.HeadReserve(PageHeadForm.Hero) + ClassicPageHeadRules.HeroGap, HitTestVisible = false },
            UnderBand(new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Padding = new Edges4(gutter, 0f, gutter, R.Space.DockReserve + Rules.SubPageBottomExtra), Children = [region],
            }),
        ],
    }) with { Grow = 1f, MinHeight = 0f, ScrollKey = scrollKey };

    /// <summary>A region that is its shimmer seed while pending and the content once ready (§0 #11: SkelReveal.Soft).</summary>
    public static Element Region(Func<bool> pending, Func<bool> failed, Func<Element> content, Func<Element> shimmer, Func<Element>? onFailed, string group)
        => new SkelRegionEl(pending, failed, content, shimmer, onFailed, SkelReveal.Soft, SkeletonStyle.Default, group, SmoothResize: false);
}
