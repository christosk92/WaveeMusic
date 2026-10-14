// ── Themes/Classic/Structure/Rules/PanelLayout.cs — the numbers of Classic's right panel and the Player family's T6b views ─────
//
// What `Shell/Rail.UI.cs`, `Rail.Styles.UI.cs` and `OnMedia.UI.cs` held as literals beside the code, moved verbatim with the
// pictures: the panel's slide, the Art | Video toggle, the now-playing body's type and bars, the friends rows, the style flyout's
// grid and the on-media strip's buttons. They are data, not decisions: `Views\Panel\*.cs` and `Views\Player\ClassicOnMedia.cs`
// read them (WVTH003 allows literals only here, in `Rules\**`).
//
// WHERE THE POLICY IS. The panel's bounds (min and max width, the content it must leave to stay inline, full height, the mini
// player) are `PanelPolicy`'s, declared by `ClassicPolicies`, because the core's width and fit machines run on them
// (`Shell.ClampRailWidth`, `Shell.CanFitRail`, `PanelHost.FitWidthTo`). What this file adds is the look of the parts inside.

using FluentGpu.Controls;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's panel frame.</summary>
internal static class ClassicPanelLayout
{
    /// <summary>The open/close slide: ONE TranslateX track on the host node, 300 ms.</summary>
    public const float SlideMs = 300f;

    /// <summary>The slide's easing.</summary>
    public static readonly EasingSpec SlideEasing = EasingSpec.CubicBezier(0f, 0.35f, 0.15f, 1f);

    /// <summary>A header glyph button's glyph (row 1 of the icon-button table pairs the 32-square with a 16-DIP glyph, never 12).</summary>
    public const float HeaderGlyph = 16f;

    /// <summary>The Art | Video toggle's height and each half's box.</summary>
    public const float ToggleH = 24f, ToggleHalfBox = 24f;

    /// <summary>A toggle half's glyph.</summary>
    public const float ToggleGlyph = 11f;

    /// <summary>The selected half's plate alpha.</summary>
    public const float TogglePressedAlpha = 0.16f;
}

/// <summary>The constants of Classic's now-playing hero and body.</summary>
internal static class ClassicHeroLayout
{
    // ── the track meta ───────────────────────────────────────────────────────────────────────────────────────────────
    public const float ArtistSize = 14f, ArtistLine = 20f, AlbumSize = 12f, AlbumLine = 16f;
    public const float TitleColumnGap = 4f, MetaLinesGap = 2f;
    public const float SaveGlyph = 16f, SaveBox = 36f;

    // ── the cover ────────────────────────────────────────────────────────────────────────────────────────────────────
    public const float CoverDecodePx = 512f;

    // ── the Cover | Player switch (WinUI's SelectorBarItem builds a 47-DIP bar; the strip is 36: compress to the 30-DIP pill) ──
    public static readonly Edges4 SelectorItemPadding = new(12f, 3f, 12f, 2f);
    public const float SelectorItemGap = 7f;

    // ── the cities: at most 5; the longest bar is 260 and the shortest never below 8 % of it ──────────────────────────
    public const int CityRows = 5;
    public const float CityTextSize = 12f, CityTextLine = 16f;
    public const float CityBarH = 4f, CityBarRadius = 2f, CityBarMax = 260f, CityBarMinFraction = 0.08f;

    // ── merch: at most 4 ─────────────────────────────────────────────────────────────────────────────────────────────
    public const int MerchRows = 4;
    public const float MerchArt = 56f, MerchTextGap = 2f;
    public const float MerchNameSize = 14f, MerchNameLine = 20f, MerchPriceSize = 12f, MerchPriceLine = 16f;

    // ── the pending About card ───────────────────────────────────────────────────────────────────────────────────────
    public const float LoadingTitleH = 18f, LoadingTitleW = 180f, LoadingLineH = 12f, LoadingLineAW = 240f, LoadingLineBW = 210f;
}

/// <summary>The constants of Classic's friends rows.</summary>
internal static class ClassicFriendsLayout
{
    /// <summary>The avatar (and the skeleton's circle).</summary>
    public const float Avatar = 40f;

    /// <summary>The presence dot and its ring (the ring is the opaque content rung, so the dot reads as a hole).</summary>
    public const float PresenceDot = 12f, PresenceRing = 2f;

    public const float NameSize = 14f, NameLine = 20f, DetailSize = 12f, DetailLine = 16f;

    /// <summary>A row's entry and exit travel.</summary>
    public const float EnterDy = 6f, ExitDy = 4f;

    /// <summary>The trailing slot (the equalizer or the relative time) and the equalizer's height.</summary>
    public const float TrailW = 44f, EqualizerH = 14f;

    /// <summary>The skeleton: five rows of a circle and two bars.</summary>
    public const int SkeletonRows = 5;
    public const float SkeletonNameW = 120f, SkeletonDetailW = 160f, SkeletonBarH = 12f;
}

/// <summary>The constants of Classic's deck style flyout.</summary>
internal static class ClassicDeckFlyoutLayout
{
    /// <summary>The flyout's content width (four 74.5-DIP cards, three gaps, the padding) and the gap between cards.</summary>
    public const float Width = 340f, CardGap = 6f;

    /// <summary>A card's padding and corner radius, and the selection ring.</summary>
    public const float CardPad = 4f, CardRadius = 6f, SelectedRing = 2f;

    public const float TitleSize = 14f, TitleLine = 20f, LabelSize = 12f, LabelLine = 16f;

    /// <summary>An option row's gap and its label's floor.</summary>
    public const float OptionGap = 10f, OptionLabelW = 60f;

    /// <summary>A swatch's ring and dot.</summary>
    public const float SwatchRing = 30f, SwatchDot = 22f;

    /// <summary>The compact Segmented of the option rows: 28 tall, 12 pt, items ≥ 56 — four choices share ~240 DIP.</summary>
    public static Segmented.Style CompactSeg => Segmented.DefaultStyle with
    {
        Height = 28f, FontSize = 12f, ItemMinWidth = 56f, CornerRadius = 5f, ItemCornerRadius = 4f,
    };
}

/// <summary>The constants of Classic's on-media strip.</summary>
internal static class ClassicOnMediaLayout
{
    /// <summary>A transport glyph button: its box, glyph and the hover and press plates.</summary>
    public const float GlyphBox = 36f, GlyphSize = 16f, GlyphHoverAlpha = 0.14f, GlyphPressedAlpha = 0.22f;

    /// <summary>The play disc: its box, glyph and its three plates.</summary>
    public const float PlayBox = 44f, PlayGlyph = 18f, PlayAlpha = 0.18f, PlayHoverAlpha = 0.28f, PlayPressedAlpha = 0.36f;

    /// <summary>The volume popup: a slider card over the video hole.</summary>
    public const float VolumePopupW = 52f, VolumePopupH = 168f, VolumeSliderLength = 124f, VolumeSliderThickness = 32f;

    public static readonly Edges4 VolumePopupPadding = new(10f, 14f, 10f, 14f);
}
