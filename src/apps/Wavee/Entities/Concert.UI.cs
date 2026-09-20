// ── Entities/Concert.UI.cs ─────────────────────────────────────────────────────────────────────────────────────────
// tiles, date blocks, pills, split hero, flyout panels, picker
//
// Role: UI
// Owner: N (stream N-C)
// Wave: 5
// Budget: 900 lines
// Spec: ch 17 §1.4 (the 0.3 node table), §2 W6-W10 + W23, §3 tokens, §4 colour, §5 motion, §6 interaction, §9
//
// PRESENTATIONAL ONLY. Every builder here is a pure function of VALUES (a tile's text, a date, a label, a signal); the
// pages in Concert.Page.cs read the tables and hand these builders what to paint. Ports of 0.2.9
// `Components/ConcertUi.cs` (EventTile, BrowseAllCard, DateBlock, SplitEditorialHero, LocationButton, FilterToken,
// WherePill, RestDatePill, SegmentedPill + SegmentedPillStyle, MoreToken, ConcertLocationPickerPanel) and
// `Features/Concerts/ConcertDateFlyout.cs` (the when + where flyouts). The dead 0.2.9 compositions (ch 17 §9: Hero,
// HeroStatsLine, SpotlightCard, BigDateBlock, ScheduleRow, TimeMeta, StatusPill-for-rows, CityLine) are not ported.
//
// THE SEGMENTED-PILL GRAMMAR IS PUBLIC (`FusedPill` + `SegmentedPillStyle`): the Home strip and the sidebar speak it at
// their own registers. Both re-derived it privately in 0.3 (`Home.UI.cs` FusedPill, `Shell/Sidebar.UI.LibraryV3.cs`
// FusedMotion/SegmentDock/FusedPill) — reported as a duplication, not edited here.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Pal;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Everything about a fused pill that is SURFACE, not grammar (0.2.9 <c>SegmentedPillStyle</c>, verbatim). The
/// grammar — outer capsule → raised segment → value → trailing glyph, the shared-key morph, the segment dock — is fixed
/// in <see cref="Concert.FusedPill"/>. Every preset is a PROPERTY: <c>Tok.*</c> and <c>Elevation.*</c> are live theme
/// reads, and a cached instance would freeze last theme's colours into the pill.</summary>
public sealed record SegmentedPillStyle(
    float Height, float SegmentHeight, Edges4 Padding, Edges4 SegmentPadding, float Gap, float SegmentGap,
    float TextSize, float CheckSize, float TrailingSize,
    ColorF Fill, ColorF HoverFill, ColorF PressedFill,
    ColorF SegmentFill, ShadowSpec SegmentShadow, ColorF SegmentInk,
    ColorF ValueInk, ColorF TrailingInk, string TrailingGlyph,
    float BorderWidth = 0f, ColorF BorderColor = default)
{
    /// <summary>The Concerts register: a 32-DIP accent capsule carrying an OPAQUE raised segment (<c>FillControlSolid</c>
    /// — never <c>FillCardDefault</c>, which in dark is 5 % white and read as accent-on-accent ink).</summary>
    public static SegmentedPillStyle Accent => new(
        Height: 32f, SegmentHeight: 26f, Padding: new Edges4(3f, 3f, 12f, 3f), SegmentPadding: new Edges4(11f, 0f, 11f, 0f),
        Gap: 8f, SegmentGap: 5f, TextSize: 14f, CheckSize: 12f, TrailingSize: 10f,
        Fill: Tok.AccentDefault, HoverFill: Tok.AccentSecondary, PressedFill: Tok.AccentTertiary,
        SegmentFill: Tok.FillControlSolid, SegmentShadow: Elevation.Flyout, SegmentInk: Tok.AccentTextPrimary,
        ValueInk: Tok.TextOnAccentPrimary, TrailingInk: Tok.TextOnAccentPrimary, TrailingGlyph: Icons.ChevronDown);

    /// <summary>The Home tab-strip register: a neutral bordered 28-DIP pill whose SEGMENT is the accent; an ✕ clears.</summary>
    public static SegmentedPillStyle Strip => new(
        Height: 28f, SegmentHeight: 22f, Padding: new Edges4(3f, 3f, 10f, 3f), SegmentPadding: new Edges4(9f, 0f, 9f, 0f),
        Gap: 6f, SegmentGap: 4f, TextSize: 14f, CheckSize: 11f, TrailingSize: 9f,
        Fill: Tok.FillControlDefault, HoverFill: Tok.FillControlSecondary, PressedFill: Tok.FillControlTertiary,
        SegmentFill: Tok.AccentDefault, SegmentShadow: Elevation.Card, SegmentInk: Tok.OnAccent,
        ValueInk: Tok.TextPrimary, TrailingInk: Tok.TextSecondary, TrailingGlyph: Icons.Cancel,
        BorderWidth: 1f, BorderColor: Tok.StrokeControlDefault);

    /// <summary>The sidebar register: the Accent capsule at the rail's 28-DIP scale; an ✕ clears the qualifier.</summary>
    public static SegmentedPillStyle Sidebar => new(
        Height: 28f, SegmentHeight: 22f, Padding: new Edges4(3f, 3f, 9f, 3f), SegmentPadding: new Edges4(8f, 0f, 8f, 0f),
        Gap: 6f, SegmentGap: 4f, TextSize: 12f, CheckSize: 10f, TrailingSize: 9f,
        Fill: Tok.AccentDefault, HoverFill: Tok.AccentSecondary, PressedFill: Tok.AccentTertiary,
        SegmentFill: Tok.FillControlSolid, SegmentShadow: Elevation.Card, SegmentInk: Tok.AccentTextPrimary,
        ValueInk: Tok.OnAccent, TrailingInk: Tok.OnAccent, TrailingGlyph: Icons.Cancel);
}

public readonly partial struct Concert
{
    static readonly Edges4 s_focusMargin = new(2f, 2f, 2f, 2f);

    // ══ 1. THE EVENT TILE, THE STUB, BROWSE-ALL (W1, W19, W23; §0 #6) ════════════════════════════════════════════════

    /// <summary>What an event tile paints, as a value: built from a handle (<see cref="TileOf"/>) or from a seed, so a
    /// skeleton derives its shimmer from the SAME builder the loaded page renders (§0 #11).</summary>
    internal readonly record struct TileData(string Key, string Caption, string Title, string Place, string? ImageUrl, uint Accent);

    /// <summary>A concert row as a tile value: the accent date caption in the provider's clock, the title (or the
    /// "Concert" fallback — never the venue twice), "Venue · City".</summary>
    internal static TileData TileOf(Concert c)
    {
        var s = Entities.Strings;
        string title = s.Resolve(c.TitleId), venue = s.Resolve(c.VenueId), city = s.Resolve(c.CityId);
        return new TileData(c.Slot.ToString(CultureInfo.InvariantCulture),
            ConcertTime.DateCaption(c.LocalDate, CultureInfo.CurrentCulture),
            string.IsNullOrWhiteSpace(title) ? Loc.Get(Strings.Concerts.Detail.Concert) : title,
            venue.Length == 0 ? city : city.Length == 0 ? venue : venue + " · " + city,
            Controls.ArtUrl(c.ImageId), c.Accent);
    }

    /// <summary>A placeholder tile for a shimmer seed (none of its text is ever shown).</summary>
    internal static TileData SeedTile(string key) =>
        new(key, "Fri, Jan 1 · 20:00", "Concert placeholder", "Venue placeholder · City", null, 0);

    /// <summary>The vertical event card (0.2.9 <c>EventTile</c>): height = cell width + <see cref="ConcertLayout.EventChrome"/>
    /// exactly, every line single-line clamped, <c>Interaction.Subtle</c>, a 2-DIP focus margin. No play, no drag (§0 #12).</summary>
    internal static Element Tile(in TileData d, Action? onClick)
    {
        var text = new Element[d.Place.Length > 0 ? 3 : 2];
        text[0] = Design.Type.Eyebrow(d.Caption) with
        { Color = Tok.AccentTextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        text[1] = Design.Type.TrackTitle(d.Title) with
        { MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        if (d.Place.Length > 0)
            text[2] = Design.Type.TrackMeta(d.Place) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        return new BoxEl
        {
            Key = "concert:" + d.Key,
            Direction = 1, MinWidth = 0f, ClipToBounds = true, Gap = Spacing.S,
            Padding = new Edges4(Spacing.S, Spacing.S, Spacing.S, Spacing.M), Corners = Radii.CardAll,
            Role = AutomationRole.Button, Focusable = true, FocusVisualMargin = s_focusMargin,
            Cursor = CursorId.Hand, OnClick = onClick,
            Children = [TileArt(d.ImageUrl, d.Accent), new BoxEl { Direction = 1, MinWidth = 0f, Gap = 3f, Children = text }],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The rounded-top square: the cover, or the accent-tinted pane (§4 row 1) with a centred 38-DIP calendar.</summary>
    static Element TileArt(string? url, uint accent)
    {
        if (url is { Length: > 0 })
            return new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, ZStack = true, ClipToBounds = true, Corners = Radii.CardAll,
                Children = [Ui.Image(url, ImageFit.Cover, 1f, 320f, Radii.Card, Tok.FillCardSecondary)],
            };
        ColorF baseFill = DarkOr(0x1C, 0x1C, 0x1E);
        return new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, AspectRatio = 1f, Corners = Radii.CardAll,
            Fill = accent != 0 ? ColorF.Lerp(baseFill, Design.Palette.ToColor(accent), 0.32f) : baseFill,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Icon(Icons.Calendar, 38f, Tok.TextSecondary with { A = 0.72f })],
        };
    }

    /// <summary><c>#RRGGBB</c> in dark theme, <c>FillCardSecondary</c> in light — the §4 base fills that are NOT token swaps.</summary>
    static ColorF DarkOr(byte r, byte g, byte b) => Tok.Theme == ThemeKind.Dark ? ColorF.FromRgba(r, g, b) : Tok.FillCardSecondary;

    /// <summary>The artist page's "Upcoming concerts" card (0.2.9 <c>ArtistPage.ConcertStub</c>, ch 08 §9): the accent month
    /// over a 28/36 day numeral, venue and pin + city. <paramref name="width"/> is the shelf's fitted card width.</summary>
    public static Element Stub(Concert c, Action onClick, float width)
    {
        var s = Entities.Strings;
        var date = c.LocalDate;
        string venue = s.Resolve(c.VenueId), city = s.Resolve(c.CityId);
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
                    Direction = 1, Width = 48f, Shrink = 0f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        Design.Type.Eyebrow(date.ToString("MMM", CultureInfo.CurrentCulture)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
                        new TextEl(date.Day.ToString(CultureInfo.CurrentCulture)) { Size = 28f, LineHeight = 36f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1 },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f,
                    Children =
                    [
                        new TextEl(venue.Length > 0 ? venue : c.TitleOrVenue)
                        { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        {
                            Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, MinWidth = 0f,
                            Children =
                            [
                                Icon(Icons.MapPin, 12f, Tok.TextSecondary) with { Shrink = 0f },
                                new TextEl(city) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>The related shelf's lead cell (W19): the tile footprint over a layered glyph pane — Calendar 30 top-left,
    /// MapPin 44 centred, Calendar 22 bottom-right, all at <c>TextSecondary</c> α 0.30 — routing to the hub.</summary>
    internal static Element BrowseAllCard(Action onClick)
    {
        ColorF glyph = Tok.TextSecondary with { A = 0.30f };
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
                    Corners = Radii.CardAll, Fill = DarkOr(0x1C, 0x1C, 0x1E),
                    Children =
                    [
                        new BoxEl { Padding = Edges4.All(Spacing.M), AlignItems = FlexAlign.Start, Justify = FlexJustify.Start, Children = [Icon(Icons.Calendar, 30f, glyph)] },
                        new BoxEl { AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.MapPin, 44f, glyph)] },
                        new BoxEl { Padding = Edges4.All(Spacing.M), AlignItems = FlexAlign.End, Justify = FlexJustify.End, Children = [Icon(Icons.Calendar, 22f, glyph)] },
                    ],
                },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Gap = 3f,
                    Children =
                    [
                        Design.Type.Eyebrow(Loc.Get(Strings.Concerts.LiveMusic)) with { Color = Tok.AccentTextPrimary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        Design.Type.TrackTitle(Loc.Get(Strings.Concerts.BrowseAll)) with { MinWidth = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        }.Interactive(Interaction.Subtle);
    }

    /// <summary>The accent eyebrow every concert section heads with (§4 row 11).</summary>
    internal static TextEl SectionCaption(string label)
        => Design.Type.Eyebrow(label) with { Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };

    // ══ 2. DATE BLOCKS, THE SPLIT HERO, THE LOCATION BUTTON (W13, W13b/c, W19) ════════════════════════════════════════

    /// <summary>56×60 (compact 48×52): a neutral plate, the accent month over the primary day — the stored clock.</summary>
    internal static Element DateBlock(DateTimeOffset date, bool compact = false)
    {
        var culture = CultureInfo.CurrentCulture;
        return new BoxEl
        {
            Width = compact ? 48f : 56f, Height = compact ? 52f : 60f, Shrink = 0f, Gap = 1f,
            Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll, Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                Design.Type.Eyebrow(date.ToString("MMM", culture)) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
                BodyStrong(date.Day.ToString(culture)) with { Color = Tok.TextPrimary, MaxLines = 1 },
            ],
        };
    }

    /// <summary>The grounded split card (§0 #8): copy on <c>FillCardDefault</c>, the photo in an adjacent clipped pane with a
    /// seam gradient only. Wide is a 56/44 row at 320; narrow stacks a 180-DIP pane above the copy. No photo ⇒ the
    /// accent-tinted plate with ONE centred 52-DIP calendar (W13c); no accent ⇒ the flat base (W13b).</summary>
    internal static Element SplitHero(string? imageUrl, uint accent, Element copy, bool wide)
    {
        var metrics = ConcertLayout.EditorialHero(wide);
        bool dark = Tok.Theme == ThemeKind.Dark;
        ColorF baseFill = DarkOr(0x1B, 0x1B, 0x1D);
        ColorF mediaFill = accent != 0 ? ColorF.Lerp(baseFill, Design.Palette.ToColor(accent), dark ? 0.30f : 0.18f) : baseFill;

        Element photo = imageUrl is { Length: > 0 }
            ? new ImageEl
            {
                Source = imageUrl, Fit = ImageFit.Cover, FocusY = 0.30f, DecodePx = 1024f, Placeholder = mediaFill,
                RevealTransition = ImageTransition.Fade(220f),
            }
            : new BoxEl
            {
                Fill = mediaFill, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Calendar, 52f, Tok.TextSecondary with { A = 0.72f })],
            };
        Element seam = new BoxEl
        {
            HitTestPassThrough = true,
            Gradient = wide
                ? LinearGradient(0f, new GradientStop(0f, Tok.FillCardDefault), new GradientStop(0.22f, Tok.FillCardDefault with { A = 0f }))
                : GradientDown(new GradientStop(0.68f, Tok.FillCardDefault with { A = 0f }), new GradientStop(1f, Tok.FillCardDefault)),
        };
        Element media = new BoxEl
        {
            Grow = wide ? 0.44f : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f,
            Height = metrics.MediaHeight, ZStack = true, ClipToBounds = true, Children = [photo, seam],
        };
        Element grounded = new BoxEl
        {
            Direction = 1, Grow = wide ? 0.56f : 0f, Basis = wide ? 0f : float.NaN, MinWidth = 0f,
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
    internal static BoxEl LocationButton(string label, Action onClick) => new()
    {
        Direction = 0, Grow = 1f, Basis = 0f, MinHeight = 32f, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
        AlignItems = FlexAlign.Center, Gap = Spacing.S, Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS),
        Corners = Radii.ControlAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new BoxEl { Width = 20f, Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.MapPin, 16f, Tok.TextSecondary)] },
            BodyStrong(label) with { Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new BoxEl { Width = 16f, Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [Icon(Icons.ChevronDown, 10f, Tok.TextSecondary)] },
        ],
    };

    // ══ 3. THE FILTER BAR'S PILLS AND TOKENS (W6; §0 #3, #4) ═════════════════════════════════════════════════════════

    static LayoutTransition WidthReflow(float ms) => new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(ms, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    static readonly LayoutTransition s_tokenReflow = WidthReflow(220f), s_pillReflow = WidthReflow(260f);

    /// <summary>The segment docks in FROM the chip's side (Dx +56, α 0.4, 300 ms) as the chip exits toward the pill.</summary>
    static readonly LayoutTransition s_segmentDock = new(
        TransitionChannels.Position | TransitionChannels.Opacity, TransitionDynamics.Tween(300f, Easing.SmoothOut),
        Enter: new EnterExit(Dx: 56f, Opacity: 0.4f, Active: true));

    /// <summary>The "This weekend" chip's exit leg: INTO the pill (Dx −56), 220 ms FluentAccelerate.</summary>
    internal static readonly LayoutTransition ChipExit = new(
        TransitionChannels.Position | TransitionChannels.Opacity, TransitionDynamics.Tween(220f, Easing.FluentAccelerate),
        Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true));

    /// <summary>A multi-select genre token: selected is the filled accent plate, the SAME width as unselected — no check
    /// glyph, so the strip never walks under the cursor. The label is the key (a re-ordered list reuses nodes).</summary>
    internal static BoxEl FilterToken(string label, bool selected, Action onClick) => new()
    {
        Key = "filter-token:" + label, Animate = s_tokenReflow,
        Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(14f, 5f, 14f, 5f), Corners = Radii.FullAll,
        Fill = selected ? Tok.AccentDefault : Tok.FillControlDefault,
        HoverFill = selected ? Tok.AccentSecondary : Tok.FillControlSecondary,
        PressedFill = selected ? Tok.AccentTertiary : Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = selected ? Tok.AccentDefault : Tok.StrokeControlDefault,
        BrushTransitionMs = Design.Motion.Fast,
        Role = AutomationRole.ToggleButton, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children = [Body(label) with { Color = selected ? Tok.TextOnAccentPrimary : Tok.TextPrimary, MaxLines = 1 }],
    };

    /// <summary>"+N genres" / "Show less": an accent LABEL over a neutral dashed (5/4) border, a secondary chevron.</summary>
    internal static Element MoreToken(string label, bool expanded, Action onClick) => new BoxEl
    {
        Key = "genre-more", Animate = s_tokenReflow,
        Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(14f, 5f, 14f, 5f), Corners = Radii.FullAll,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, BorderDashOn = 5f, BorderDashOff = 4f,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Body(label) with { Color = Tok.AccentTextPrimary, MaxLines = 1 },
            Icon(expanded ? Icons.ChevronUp : Icons.ChevronDown, 10f, Tok.TextSecondary) with { Shrink = 0f },
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>The where pill: MapPin 14, the label (BodyStrong), chevron 10; H 32, fully rounded, the control ramp.</summary>
    internal static BoxEl WherePill(string label, Action onClick) => new()
    {
        Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(12f, 5f, 12f, 5f), Corners = Radii.FullAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Icon(Icons.MapPin, 14f, Tok.TextSecondary) with { Shrink = 0f },
            BodyStrong(label) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            Icon(Icons.ChevronDown, 10f, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>The when-area at rest: Calendar, "Dates", chevron — keyed <c>when-pill</c>, the SAME key as the fused
    /// pill, so the node is reused and its width reflows over 260 ms (§9 #10).</summary>
    internal static BoxEl RestDatePill(Action onClick) => new()
    {
        Key = "when-pill", Animate = s_pillReflow,
        Direction = 0, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(12f, 5f, 12f, 5f), Corners = Radii.FullAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Icon(Icons.Calendar, 14f, Tok.TextSecondary) with { Shrink = 0f },
            Body(Loc.Get(Strings.Concerts.Filter.Dates)) with { Color = Tok.TextPrimary, MaxLines = 1 },
            Icon(Icons.ChevronDown, 10f, Tok.TextSecondary) with { Shrink = 0f },
        ],
    };

    /// <summary>THE FUSED PILL (public grammar): outer capsule → the raised segment (✓ name) docking in → the value → the
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
    internal static Element BarDivider() => new BoxEl { Width = 1f, Height = 20f, Shrink = 0f, Fill = Tok.StrokeSurfaceDefault };

    // ══ 4. FLYOUT CHROME (W7-W9) ══════════════════════════════════════════════════════════════════════════════════════

    internal static PopupOptions FlyoutOptions =>
        new(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Popup) { ConstrainToRootBounds = true };

    /// <summary>A flyout row: MinH 40, pad (12,4,12,4), r4, Body label, an optional lead 16 / trailing 14 glyph, Subtle.
    /// A radio row (<paramref name="toggle"/>) reserves its 14×14 check box, so the labels never shift.</summary>
    static Element FlyoutRow(string label, Action onClick, string? lead = null, string? trailing = null,
                             bool toggle = false, bool active = false)
    {
        var kids = new List<Element>(3);
        if (lead is not null) kids.Add(Icon(lead, 16f, Tok.TextSecondary) with { Shrink = 0f });
        kids.Add(Body(label) with
        {
            Color = active ? Tok.AccentTextPrimary : Tok.TextPrimary, Weight = (ushort)(active ? 600 : 400),
            Grow = 1f, Basis = 0f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
        });
        if (trailing is not null) kids.Add(Icon(trailing, 14f, Tok.TextSecondary) with { Shrink = 0f });
        if (toggle) kids.Add(active ? Icon(Icons.Check, 14f, Tok.AccentTextPrimary) with { Shrink = 0f } : new BoxEl { Width = 14f, Height = 14f, Shrink = 0f });
        return new BoxEl
        {
            Direction = 0, MinHeight = 40f, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(Spacing.M, Spacing.XS, Spacing.M, Spacing.XS), Corners = Radii.ControlAll,
            Role = toggle ? AutomationRole.ToggleButton : AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
            OnClick = onClick, Children = kids.ToArray(),
        }.Interactive(Interaction.Subtle);
    }

    static Element FlyoutDivider() => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, Padding = new Edges4(0f, Spacing.XS, 0f, Spacing.XS),
        Children = [new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeSurfaceDefault }],
    };

    // ══ 5. THE WHEN FLYOUT (W7, W8) ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The when-area drill-down (0.2.9 <c>ConcertDateFlyout</c>): ROOT (Anytime / Today / This weekend / Next
    /// weekend, a divider, this month + 3 drilling in) ⇄ MONTH LEAF (back, All of {month}, each Fri–Sun weekend, the Su–Sa
    /// calendar, Clear / Show events). The swap is keyed per view and slides forward on a drill, back on a return (250 ms,
    /// folded to instant under reduced motion). Mounts fresh per open; hands every choice to <c>onPick</c>.</summary>
    internal sealed class WhenFlyout(Action<ConcertWhen> onPick) : Component
    {
        readonly Signal<int> _view = new(-1);            // -1 root; 0..3 = the month leaf's offset from this month
        readonly Signal<DateOnly?> _start = new(null), _end = new(null);
        bool _forward = true;

        public override Element Render()
        {
            int view = _view.Value;
            var culture = CultureInfo.CurrentCulture;
            var today = DateOnly.FromDateTime(DateTime.Now);
            var first = new DateOnly(today.Year, today.Month, 1);
            return new BoxEl
            {
                Direction = 1, Width = 320f, ClipToBounds = true, Padding = Edges4.All(Spacing.S),
                Children =
                [
                    new BoxEl
                    {
                        Key = view < 0 ? "when-view:root" : "when-view:month:" + view.ToString(CultureInfo.InvariantCulture),
                        Animate = _forward ? MotionRecipes.PageSlideForward : MotionRecipes.PageSlideBack,
                        Direction = 1, MinWidth = 0f,
                        Children = [view < 0 ? Root(first, culture) : Month(first.AddMonths(view), today, culture)],
                    },
                ],
            };
        }

        Element Root(DateOnly first, CultureInfo culture)
        {
            var rows = new Element[9];
            rows[0] = FlyoutRow(Loc.Get(Strings.Concerts.Filter.Anytime), () => onPick(ConcertWhen.Any));
            rows[1] = FlyoutRow(Loc.Get(Strings.Concerts.Filter.Today), () => Preset(ConcertWhenKind.Today, Strings.Concerts.Filter.Today));
            rows[2] = FlyoutRow(Loc.Get(Strings.Concerts.Filter.ThisWeekend), () => Preset(ConcertWhenKind.ThisWeekend, Strings.Concerts.Filter.ThisWeekend));
            rows[3] = FlyoutRow(Loc.Get(Strings.Concerts.Filter.NextWeekend), () => Preset(ConcertWhenKind.NextWeekend, Strings.Concerts.Filter.NextWeekend));
            rows[4] = FlyoutDivider();
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                rows[5 + i] = FlyoutRow(first.AddMonths(i).ToString("MMMM yyyy", culture),
                    () => { _forward = true; _view.Value = index; }, trailing: Icons.ChevronRight);
            }
            return new BoxEl { Direction = 1, Gap = 2f, Children = rows };
        }

        void Preset(ConcertWhenKind kind, string key)
            => onPick(new ConcertWhen(kind, Loc.Get(key), ConcertHub.PresetRange(kind, DateTimeOffset.Now)));

        Element Month(DateOnly first, DateOnly today, CultureInfo culture)
        {
            var start = _start.Value;
            var end = _end.Value;
            string monthName = first.ToString("MMMM", culture);
            var last = new DateOnly(first.Year, first.Month, DateTime.DaysInMonth(first.Year, first.Month));
            var rows = new List<Element>(12)
            {
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 36f,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                            Corners = Radii.ControlAll, Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand,
                            OnClick = () => { _forward = false; _view.Value = -1; },
                            Children = [Icon(Icons.ChevronLeft, 14f, Tok.TextSecondary)],
                        }.Interactive(Interaction.Subtle),
                        BodyStrong(first.ToString("MMMM yyyy", culture)) with { Color = Tok.TextPrimary, MaxLines = 1 },
                    ],
                },
                FlyoutRow(Strings.Concerts.Filter.AllOf(monthName),
                    () => onPick(new ConcertWhen(ConcertWhenKind.Custom, monthName, new ConcertDateRange(first, last)))),
            };
            // Every Fri–Sun of the month (a weekend spilling past month-end keeps its Sunday).
            var friday = first;
            while (friday.DayOfWeek != DayOfWeek.Friday) friday = friday.AddDays(1);
            for (; friday.Month == first.Month; friday = friday.AddDays(7))
            {
                var range = new ConcertDateRange(friday, friday.AddDays(2));
                string weekend = Loc.Get(Strings.Concerts.Filter.Weekend);
                rows.Add(FlyoutRow(weekend + " · " + ConcertHub.WhenLabel(range, culture),
                    () => onPick(new ConcertWhen(ConcertWhenKind.Custom, weekend, range))));
            }
            rows.Add(Calendar(first, today, start, end, culture));
            rows.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S, MinHeight = 44f,
                Padding = new Edges4(Spacing.XS, Spacing.XS, Spacing.XS, 0f),
                Children =
                [
                    Button.Standard(Loc.Get(Strings.Concerts.Filter.Clear), () => { _start.Value = null; _end.Value = null; }),
                    new BoxEl { Grow = 1f },
                    Button.Accent(Loc.Get(Strings.Concerts.Filter.ShowEvents), Apply, isEnabled: start is not null),
                ],
            });
            return new ScrollEl
            {
                ContentSized = true, MaxHeight = 400f,
                Content = new BoxEl { Direction = 1, Gap = Spacing.XS, Children = rows.ToArray() },
            };
        }

        void Apply()
        {
            if (_start.Peek() is not { } s) return;
            onPick(new ConcertWhen(ConcertWhenKind.Custom, Loc.Get(Strings.Concerts.Filter.Custom),
                new ConcertDateRange(s, _end.Peek() ?? s)));
        }

        /// <summary>The Su–Sa grid: 38×20 two-letter headers, 38×32 days with 4-DIP gaps; past days inert and disabled
        /// ink; the chosen ends on the accent plate, the days between on <c>AccentSubtle</c> (W8).</summary>
        Element Calendar(DateOnly first, DateOnly today, DateOnly? start, DateOnly? end, CultureInfo culture)
        {
            int days = DateTime.DaysInMonth(first.Year, first.Month), lead = (int)first.DayOfWeek;
            var names = culture.DateTimeFormat.AbbreviatedDayNames;
            var grid = new List<Element>(7);
            var header = new Element[7];
            for (int i = 0; i < 7; i++)
                header[i] = new BoxEl
                {
                    Width = 38f, Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [Caption(names[i].Length <= 2 ? names[i] : names[i][..2]) with { Color = Tok.TextSecondary, MaxLines = 1 }],
                };
            grid.Add(new BoxEl { Direction = 0, Gap = 4f, Children = header });
            var week = new List<Element>(7);
            for (int i = 0; i < lead; i++) week.Add(new BoxEl { Width = 38f, Height = 32f, Shrink = 0f });
            for (int d = 1; d <= days; d++)
            {
                var day = new DateOnly(first.Year, first.Month, d);
                bool past = day < today;
                bool selected = day == start || day == end;
                bool inRange = start is { } s && end is { } e && day > s && day < e;
                var cell = new BoxEl
                {
                    Width = 38f, Height = 32f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = Radii.ControlAll, Fill = selected ? Tok.AccentDefault : inRange ? Tok.AccentSubtle : ColorF.Transparent,
                    Children = [Body(d.ToString(culture)) with { Color = selected ? Tok.TextOnAccentPrimary : past ? Tok.TextDisabled : Tok.TextPrimary, MaxLines = 1 }],
                };
                week.Add(past
                    ? cell with { HitTestVisible = false }
                    : cell with
                    {
                        HoverFill = selected ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
                        PressedFill = selected ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
                        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = () => TapDay(day),
                    });
                if (week.Count == 7) { grid.Add(new BoxEl { Direction = 0, Gap = 4f, Children = week.ToArray() }); week.Clear(); }
            }
            if (week.Count > 0)
            {
                while (week.Count < 7) week.Add(new BoxEl { Width = 38f, Height = 32f, Shrink = 0f });
                grid.Add(new BoxEl { Direction = 0, Gap = 4f, Children = week.ToArray() });
            }
            return new BoxEl { Direction = 1, Gap = 4f, Children = grid.ToArray() };
        }

        /// <summary>First tap = start; a second tap ≥ start closes the range; a tap before the start restarts it; a third
        /// tap starts fresh (ch 17 §6).</summary>
        void TapDay(DateOnly day)
        {
            var s = _start.Peek();
            if (s is null || _end.Peek() is not null) { _start.Value = day; _end.Value = null; }
            else if (day < s) _start.Value = day;
            else _end.Value = day;
        }
    }

    // ══ 6. THE WHERE FLYOUT (W9) ═════════════════════════════════════════════════════════════════════════════════════

    /// <summary>264 wide: Search cities ›, Use my location, a divider, the 25 / 50 / 100 km radios. A radius pick KEEPS the
    /// flyout open (the check moves, the pill re-labels); the two location rows close it and hand off.</summary>
    internal sealed class WhereFlyout(IReadSignal<int> radius, Action<int> onRadius, Action onSearch, Action onUseMine) : Component
    {
        public override Element Render()
        {
            int current = radius.Value;
            var options = ConcertHub.RadiusOptionsKm;
            var kids = new Element[3 + options.Length];
            kids[0] = FlyoutRow(Loc.Get(Strings.Concerts.Location.SearchCities), onSearch, lead: Icons.MapPin, trailing: Icons.ChevronRight);
            kids[1] = FlyoutRow(Loc.Get(Strings.Concerts.Location.UseMine), onUseMine, lead: Icons.MapPin);
            kids[2] = FlyoutDivider();
            for (int i = 0; i < options.Length; i++)
            {
                int km = options[i];
                kids[3 + i] = FlyoutRow(Strings.Concerts.Filter.WithinKm(km), () => onRadius(km), toggle: true, active: km == current);
            }
            return new BoxEl { Direction = 1, Width = 264f, Gap = 2f, Padding = Edges4.All(Spacing.S), Children = kids };
        }
    }

    // ══ 7. THE LOCATION PICKER (W10; ch 17 §6 "Location picker") ═══════════════════════════════════════════════════════

    /// <summary>The shared picker controller (0.2.9 <c>ConcertLocationController</c>): the live query / results / loading /
    /// error signals and the anchored open. One per page instance; every open RESETS the four signals first, so a prior
    /// session's error never greets the next. Results are PLACE SLOTS the host committed (Match rows).</summary>
    internal sealed class PlacePicker
    {
        public readonly Signal<string> Query = new("");
        public readonly Signal<int[]> Results = new([]);
        public readonly Signal<bool> Loading = new(false);
        public readonly Signal<string?> Error = new(null);
        /// <summary>Fired on the UI thread after a successful save, after the picker closed.</summary>
        public Action? Saved;
        OverlayHandle? _handle;

        public void Toggle(IOverlayService? overlay, Func<NodeHandle> anchor)
        {
            if (Controls.IsNullOverlay(overlay)) return;
            if (_handle is { IsOpen: true } open) { open.Close(); return; }
            Query.Value = "";
            Results.Value = [];
            Loading.Value = false;
            Error.Value = null;
            _handle = overlay.Open(anchor, () => Embed.Comp(() => new PlacePickerPanel(this)),
                FlyoutPlacement.BottomEdgeAlignedLeft, FlyoutOptions);
            _handle.ClosedAction = () => _handle = null;
        }

        internal void Close() => _handle?.Close();
    }

    /// <summary>360 wide: the 340×32 search field (220 ms debounce; an empty query clears without a call), "Use my
    /// location" (Standard; "Locating…" and disabled while loading — it FILLS the results, it never saves), the critical
    /// error line (wrap ≤ 3), then the results (MaxH 248) or one 48-high "Searching…" / "No locations found" row.</summary>
    sealed class PlacePickerPanel(PlacePicker picker) : Component
    {
        int _ticket;

        public override Element Render()
        {
            var post = UsePost();
            var debounced = UseDebouncedValue((IReadSignal<string>)picker.Query, 220f);
            UseSignalEffect(() =>
            {
                string q = debounced.Value;
                Reactive.Untrack(() => Search(q));
            });

            var results = picker.Results.Value;
            bool loading = picker.Loading.Value;
            string? error = picker.Error.Value;
            var rows = new Element[results.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                int slot = results[i];
                rows[i] = PlaceRow(slot, () => Select(slot));
            }
            var kids = new List<Element>(4)
            {
                Embed.Comp(() => new EditableText
                {
                    Placeholder = Loc.Get(Strings.Concerts.Location.SearchCities), Width = 340f, Height = 32f, Text = picker.Query,
                }),
                Button.Standard(Loc.Get(loading ? Strings.Concerts.Location.Locating : Strings.Concerts.Location.UseMine),
                    () => UseMyLocation(post), isEnabled: !loading),
            };
            if (!string.IsNullOrWhiteSpace(error))
                kids.Add(Body(error) with { Color = Tok.SystemFillCritical, Wrap = TextWrap.Wrap, MaxLines = 3 });
            kids.Add(rows.Length > 0
                ? new ScrollEl { ContentSized = true, MaxHeight = 248f, Content = new BoxEl { Direction = 1, Gap = 2f, Children = rows } }
                : new BoxEl
                {
                    Height = 48f, AlignItems = FlexAlign.Center, Direction = 0,
                    Children = [Design.Type.TrackMeta(Loc.Get(loading ? Strings.Concerts.Location.Searching : Strings.Concerts.Location.NoneFound))],
                });
            return new BoxEl { Direction = 1, Width = 360f, Gap = Spacing.S, Padding = Edges4.All(Spacing.M), Children = kids.ToArray() };
        }

        void Search(string query)
        {
            int ticket = ++_ticket;
            if (string.IsNullOrWhiteSpace(query))
            {
                picker.Results.Value = [];
                picker.Loading.Value = false;
                return;
            }
            picker.Loading.Value = true;
            ConcertHost.Current.SearchPlaces(query.Trim(), answer =>
            {
                if (ticket != _ticket) return;                    // superseded by a newer keystroke
                picker.Loading.Value = false;
                if (answer.Ok) picker.Results.Value = answer.Places;
                else picker.Error.Value = Loc.Get(Strings.Concerts.Location.SearchFailed);
            });
        }

        /// <summary>Explicit user action only (the OS consent prompt starts here, on the UI thread). No geolocation PAL in
        /// this build ⇒ the honest "unavailable" sentence.</summary>
        async void UseMyLocation(Action<Action> post)
        {
            picker.Error.Value = null;
            if (ConcertHost.Current.Geolocation is not { } geo)
            {
                picker.Error.Value = LocationErrors.ForStatus(GeolocationStatus.Unavailable);
                return;
            }
            picker.Loading.Value = true;
            GeolocationResult fix;
            try { fix = await geo.RequestAsync(GeolocationRequest.Default).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { fix = GeolocationResult.Failed; }
            post(() =>
            {
                if (!fix.IsSuccess)
                {
                    picker.Loading.Value = false;
                    picker.Error.Value = LocationErrors.ForStatus(fix.Status);
                    return;
                }
                ConcertHost.Current.ReversePlaces(fix.Position.Latitude, fix.Position.Longitude, answer =>
                {
                    picker.Loading.Value = false;
                    picker.Results.Value = answer.Places;
                    picker.Error.Value = !answer.Ok ? Loc.Get(Strings.Concerts.Location.LookupFailed)
                        : answer.Places.Length == 0 ? Loc.Get(Strings.Concerts.Location.NoMatches) : null;
                });
            });
        }

        void Select(int slot)
        {
            if (ConcertPlaces.From(slot) is not { } place || string.IsNullOrWhiteSpace(place.Id))
            {
                picker.Error.Value = Loc.Get(Strings.Concerts.Location.Invalid);
                return;
            }
            picker.Error.Value = null;
            picker.Loading.Value = true;
            ConcertHost.Current.SavePlace(slot, ok =>
            {
                picker.Loading.Value = false;
                if (!ok) { picker.Error.Value = Loc.Get(Strings.Concerts.Location.SaveFailed); return; }
                picker.Close();
                picker.Saved?.Invoke();
            });
        }

        static Element PlaceRow(int slot, Action onClick)
        {
            var place = ConcertPlaces.From(slot);
            string name = place?.Name ?? "";
            string detail = place is null ? ""
                : !string.IsNullOrWhiteSpace(place.Region) && !string.IsNullOrWhiteSpace(place.Country) ? place.Region + " - " + place.Country
                : place.Region ?? place.Country ?? "";
            return new BoxEl
            {
                Key = "place:" + slot.ToString(CultureInfo.InvariantCulture),
                Direction = 0, MinHeight = 48f, AlignItems = FlexAlign.Center, Gap = Spacing.M,
                Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS), Corners = Radii.ControlAll,
                Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
                Children =
                [
                    Icon(Icons.MapPin, 18f, Tok.TextSecondary) with { Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 1f,
                        Children = detail.Length > 0
                            ? [Design.Type.TrackTitle(name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, Design.Type.TrackMeta(detail) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }]
                            : [Design.Type.TrackTitle(name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                    },
                ],
            }.Interactive(Interaction.Subtle);
        }
    }
}
