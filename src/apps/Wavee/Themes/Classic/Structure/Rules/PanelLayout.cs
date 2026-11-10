// ── Themes/Classic/Structure/Rules/PanelLayout.cs — the numbers of Classic's right panel and the Player family's T6b views ─────
//
// What `Shell/Rail.UI.cs`, `Rail.Styles.UI.cs` and `OnMedia.UI.cs` held as literals beside the code, moved verbatim with the
// pictures: the panel's slide, the Art | Video toggle, the now-playing body's type and bars, the friends rows, the style flyout's
// grid and the on-media strip's buttons. They are data, not decisions: `Views\Panel\*.cs` and `Views\Player\ClassicOnMedia.cs`
// read them (WVTH003 allows literals only here, in `Rules\**`).
//
// WHERE THE POLICY IS. The panel's bounds (min and max width, full height, the mini player) are `PanelPolicy`'s, declared by
// `ClassicPolicies`, and its placement per window tier is `ClassicPanelTiers`' (Tiers.Panel.cs, TC2), because the core's width and
// placement machines run on them (`Shell.ClampRailWidth`, `PanelHost.FitWidthTo` / `FitWidthToFloor`). What this file adds is the look of
// the parts inside.

using FluentGpu.Animation;
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

    /// <summary>The docked video's resize handle under the card: a grabber pill (32 x 4, r2, FillControlStrong, the sheet grabber's size) at this
    /// opacity at rest, full while the band is hovered or dragged.</summary>
    public const float DockedGripW = 32f, DockedGripH = 4f, DockedGripRadius = 2f, DockedGripRest = 0.55f;

    /// <summary>The Art | Video toggle's height and each half's box.</summary>
    public const float ToggleH = 24f, ToggleHalfBox = 24f;

    /// <summary>A toggle half's glyph.</summary>
    public const float ToggleGlyph = 11f;

    /// <summary>The selected half's plate alpha.</summary>
    public const float TogglePressedAlpha = 0.16f;

    // ── the lyrics header's AI sparkle (from `AiLyrics.AiHeaderButton`, P3) ──

    /// <summary>One breath while the track is being timed.</summary>
    public const float SparklePulseMs = 1800f;

    /// <summary>1.0 → 0.55 → 1.0, eased both ways: a quiet breath, not a blink.</summary>
    public static readonly Keyframe[] SparklePulse =
        [new Keyframe(0f, 1f), new Keyframe(0.5f, 0.55f, Easing.EaseInOut), new Keyframe(1f, 1f, Easing.EaseInOut)];

    /// <summary>Back to full opacity from wherever the loop was, critically damped (never overshoots 1).</summary>
    public static readonly SpringParams SparkleSettle = SpringParams.FromResponse(0.35f, 1f);
}

/// <summary>The constants of Classic's now-playing hero and body.</summary>
internal static class ClassicHeroLayout
{
    // ── the edge-to-edge canvas ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The canvas is the panel's width x 16/9 from the panel's top. It dissolves into the panel's plain ground (its own, as with the
    /// canvas off): the picture has fully given way at min(canvas foot - 110, panel height - 300) (<see cref="BleedEnd"/>), after a 200 fade
    /// whose alpha follows an eased curve (the seven alphas below: slow off the picture, slow into the ground, so neither end shows an
    /// edge). Nothing moves and nothing is tinted by the art (the owner, 2026-11-10). A 150 band of the media scope's shade (75 %,
    /// `Deck.BleedShade`) keeps the header readable. The meta's top sits 24 under the fade's end.</summary>
    public const float BleedAspect = 16f / 9f, BleedEndLift = 110f, BleedEndRoom = 300f, BleedFadeH = 200f,
        BleedHeadScrimH = 150f, BleedHeadScrimA = 0.75f, BleedMetaDrop = 24f;

    /// <summary>The fade's curve: the ground's alpha at every sixth of the fade (a smootherstep). A gradient holds four stops, so the fade
    /// is two gradients, one over the other, of three sixths each (<see cref="BleedFadeThird"/>, <see cref="BleedFadeTwoThirds"/> inside
    /// each): the upper one runs A0..A3, the lower one A3..A6.</summary>
    public const float BleedFadeA0 = 0f, BleedFadeA1 = 0.035f, BleedFadeA2 = 0.21f, BleedFadeA3 = 0.5f, BleedFadeA4 = 0.79f, BleedFadeA5 = 0.965f,
        BleedFadeA6 = 1f, BleedFadeThird = 1f / 3f, BleedFadeTwoThirds = 2f / 3f;

    /// <summary>Where the canvas has fully given way to the ground, from the panel's top: <paramref name="canvasH"/> is the canvas's height,
    /// <paramref name="panelH"/> the panel's (0 before it is measured).</summary>
    public static float BleedEnd(float canvasH, float panelH)
        => MathF.Min(canvasH - BleedEndLift, (panelH > 0f ? panelH : canvasH) - BleedEndRoom);

    /// <summary>The ratio the meta's accent text (the art's hue) is solved to on the plain ground: AA's 4.5 with a little headroom.</summary>
    public const float ArtTextContrast = 4.8f;

    /// <summary>The header over the canvas: "Playing from album" (11/14, 14 % tracking, white at 72 %) over the album (15/20 semibold); the chip
    /// a white 10 % plate under a 22 % hairline (no blur: a frost cannot sample the video, a separate DirectComposition surface).</summary>
    public const float BleedEyebrowSize = 11f, BleedEyebrowLine = 14f, BleedEyebrowA = 0.72f, BleedSourceSize = 15f, BleedSourceLine = 20f,
        BleedChipFillA = 0.10f, BleedChipStrokeA = 0.22f;

    public const ushort BleedSourceWeight = 600, BleedTitleWeight = 600;

    /// <summary>The meta under the fade: the 64 cover (r10, flat on the ground: no glow, the owner, 2026-11-10), 14 to the title (22/26
    /// semibold) over the artist in the art's hue as text (14/20) and "album · year" (12/16, the primary ink at 70 %), 3 apart; the saved
    /// heart in that hue on a 40 circle of it at 14 %.</summary>
    public const float BleedCover = 64f, BleedCoverRadius = 10f, BleedMetaGap = 14f,
        BleedTitleSize = 22f, BleedTitleLine = 26f, BleedLinesGap = 3f, BleedYearA = 0.70f, BleedHeart = 40f, BleedHeartA = 0.14f;

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

    // ── "Next up" (from `Rail.NextUpRow`, P3) ──

    /// <summary>A "Next up" row's floor: 52 — the media surface's row shape's 8-DIP padding lifts the 40-art row to 56, and the
    /// hidden-artwork row (the 32 play glyph) stays on this floor. The rows stack 2 DIP apart.</summary>
    public const float NextUpRowH = 52f, NextUpGap = 2f;

    // ── the lyrics peek (from `Lyrics.PeekReel`, P3) ──

    /// <summary>One peek row, the reel (two rows), the accent spine's width and corner, the note's half-height spine.</summary>
    public const float PeekRowH = 56f, PeekReelH = PeekRowH * 2f, PeekSpineW = 3f, PeekSpineRadius = 1.5f, PeekNoteSpineH = PeekRowH * 0.5f;

    /// <summary>The faded peek row's opacity, and the note's text size.</summary>
    public const float PeekOpacity = 0.38f, PeekNoteText = 12f;

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

/// <summary>The constants of Classic's style chip and its in-place picker (canvas-design `Classic.dc.html`, `Styles.dc.html`): pills.</summary>
internal static class ClassicStylePickerLayout
{
    /// <summary>The node keys `find` reads.</summary>
    public const string ChipKey = "classic-npv-chip", CancelKey = "classic-npv-cancel", DoneKey = "classic-npv-done", StripKey = "classic-npv-strip",
        PrevKey = "classic-npv-prev", NextKey = "classic-npv-next", SwatchPrefix = "classic-npv-finish:", DotPrefix = "classic-npv-dot:", DotsKey = "classic-npv-dots";

    /// <summary>The chip: a 32 pill with a hairline, 12 in before the name and 8 after the chevron, 6 between them; the name 13/20, the chevron 12.</summary>
    public const float ChipH = 32f, ChipGap = 6f, ChipTextSize = 13f, ChipTextLine = 20f, ChipGlyph = 12f;

    public static readonly Edges4 ChipPadding = new(12f, 0f, 8f, 0f);

    /// <summary>The strip under the art (12 above it): 12 between its rows; the 40 round arrows either side of the style's name (16/22 bold) over
    /// its line (13/18, two lines reserved), 2 apart; the finish swatches (28, a 2 ring) 12 apart (their 40 hit boxes side by side) in a 40 row there for every style; the dots
    /// (6 tall, 18 wide on the focused style, 24 apart in ONE 40-tall target whose click picks the dot under it); a swatch's hit box is 40 (the Compact floor).</summary>
    public const float StripGap = 12f, StripTop = 12f, ArrowBox = 40f, ArrowGlyph = 14f, RowGap = 8f, NameSize = 16f, NameLine = 22f, NameGap = 2f,
        LineSize = 13f, LineLine = 18f, SwatchRowH = 40f, Swatch = 28f, SwatchRing = 2f, SwatchGap = 0f, SwatchHit = 40f, DotBox = 24f, DotHitH = 40f, DotH = 6f, DotW = 6f,
        DotOnW = 18f;

    public const ushort NameWeight = 700;

    /// <summary>The name block: the name's line, the gap and two lines of description.</summary>
    public const float NameBlockH = NameLine + NameGap + 2f * LineLine;

    public static readonly Edges4 StripPadding = new(0f, StripTop, 0f, 0f);

    /// <summary>A dot's width easing to the focused one's.</summary>
    public static readonly LayoutTransition DotMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.EaseInOut), SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>The swatch row opening to a style with finishes and closing to one without.</summary>
    public static readonly LayoutTransition SwatchRowMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.EaseInOut), SizeMode.Reflow, Axes: SizeAxes.Height);
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
