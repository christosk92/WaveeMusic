// ── Themes/Zune/Structure/Rules/ZuneBandRules.cs — the numbers of Zune's band and material (themes plan 6.3; P5 Z2b) ─────────────────
//
// Every number the band (`Shell.NavPane`) and the material (`Shell.Material` and its cover leaves) draw with, read off the boards
// (Z-Part-Band, Z-Frame-Home, Z-Artist, Z-Library-*, Z-Browse). The views under `Views\{Band,Material}` read them; WVTH003 allows literals
// only here, in `Rules\**`.
//
//   THE BAND (Z-Part-Band), per WINDOW tier (`ZuneBandLayout`):
//     tier       inset  row 1  row 2  under  pivot gap  pins  pin art  row-2 gap  row-2 end  title+actions  collapses  narrow
//     Compact    16     44     40     8      24         no    -        22         0          no             yes        rotate + fade
//     Small      24     44     32     8      24         no    -        20         24         yes            yes        fade
//     Standard   48     44     32     8      24         yes   32       20         48         yes            yes        -
//     Huge       48     44     32     8      24         yes   32       20         48         yes            no         -
//     Huge 4K    64     64     40     8      32         yes   40       20         64         yes            no         -
//   The band is row 1 + row 2 + the 8 under them: 84 at Standard (92 at Compact, 112 at Huge 4K); collapsed it keeps row 1 and the 8:
//   52 (72 at Huge 4K). The inset is the frame's gutter (`ZuneFrameLayout.Gutter`), so the first pivot stands on the page's x.
//   THE PAGE COLUMN (the T1 audit's A1, A2): the rows sit in the pages' column (`ZuneFrameRules.PageRow`): a leading inset (the gutter),
//   the rows capped at 2400 (`ZuneFrameRules.PageCap`) and a trailing inset (`Row2End`), the three centred, so the first pivot stands at
//   max(gutter, (W - 2400) / 2) (48 at 1440, 80 at 2560, 720 at 3840) and the pins and row 2's last action end where the page's content
//   ends (the trailing inset is the gutter from Small up; Compact's rows run to the window's edge under their fade). Both insets ease on a
//   crossing (size channels), and once the cap holds the two ease together, so the rows never move.
//   PINS (decision 4): Standard and Huge show them, Small and Compact do not, and the `bandPins` option can hide them everywhere; up to
//   six (`ShellCapabilities.MaxBandPins`, the core's cap), 8 apart, after the "pinned" word, ending on the page's right edge, each over a
//   4-dip playing dot (accent while the pin is the playing context).
//   ROW 2: the page's views (Library's kinds, Browse's bands, a page's in-page tabs), 20 apart; a detail page (no lit pivot) leads with
//   its title and a hairline, and its current view takes the accent underline instead of the weight; then the page's actions at the end:
//   the 28 play disc, the 28 shuffle ring and the word actions as 28-tall outlined pills, 14 apart.
//   COLLAPSE (`collapseBand`): row 2 folds away while the page scrolls down and comes back as it scrolls up or reaches the top, the
//   band's own height easing 84 <-> 52 over the pivot slide's 250 ms (the slot hugs it, so the page below follows, never a jump). Huge and
//   Huge 4K never collapse: there is room (Z0's frame record).
//
//   THE MATERIAL: the flat ground (the style's shell ground, #161616 / #f5f5f5), then by the `background` option:
//     artwork wash   the published cover tint as a band from the top of the window that fades out by the band's foot (180: the
//                    title bar and the band; albums, artists, playlists). The page's own part of the wash is the page-tone and
//                    artist-blend leaves, so the two meet instead of doubling under the page;
//     mix            the page's mix covers drifting behind the top 480 at 18 %, faded out at its foot;
//     none           the flat ground alone.
//   Under artwork wash and mix, a page's BLEED (`PageChrome.Backdrop.Bleed`, an artist's header) lies behind the band and the head,
//   420 tall at 35 %, fading out over its lower 240.
//
// Public, so `Wavee.Tests` can pin them.

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>What Zune's band lays out in one window tier (Z-Part-Band).</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Inset">The lead inset: the frame's gutter, so the first pivot stands on the page's x.</param>
/// <param name="Row1H">Row 1's height (the pivots and the pins).</param>
/// <param name="Row2H">Row 2's height (the page's views and actions).</param>
/// <param name="UnderH">The space under the rows (the band's foot, kept when it collapses).</param>
/// <param name="PivotGap">The gap between two pivot words.</param>
/// <param name="RowGap">The gap between the pivot run and the pins.</param>
/// <param name="Pins">The pins show in this tier (P5 decision 4; the `bandPins` option can still hide them).</param>
/// <param name="PinArt">A pin's art edge.</param>
/// <param name="Row2Gap">The gap between row 2's words.</param>
/// <param name="Row2End">The rows' trailing inset (the pins and row 2's last action end on it): the frame's gutter from Small up (24 / 48 /
/// 48 / 64, the page's right content edge; A2), 0 at Compact, where row 1 and row 2 scroll to the window's edge under their fade.</param>
/// <param name="TitleAndActions">Row 2 leads with a detail page's title and ends with its actions.</param>
/// <param name="Collapses">The band folds row 2 away while the page scrolls down.</param>
/// <param name="Rotate">The current pivot leads row 1 (the run rotates; Compact).</param>
/// <param name="Fade">Row 1 and row 2 scroll under an edge fade when they overflow (Compact and Small; at every tier under the
/// <see cref="PivotOverflow.EdgeFadeScroll"/> parameter the run still scrolls rather than clip).</param>
/// <param name="Narrow">The tier is narrow enough that the <see cref="PivotOverflow.MoreMenu"/> parameter folds the other pivots into
/// a "more" word (Compact and Small).</param>
/// <param name="ViewMinW">A row-2 view's hit box is at least this wide (Compact: 40, the touch floor, so a short word such as "all" is a 40 x 40
/// target; the word is centred in it. 0 = the word's own width).</param>
public sealed record ZuneBandLayout(WidthTier Tier, float Inset, float Row1H, float Row2H, float UnderH, float PivotGap, float RowGap,
    bool Pins, float PinArt, float Row2Gap, float Row2End, bool TitleAndActions, bool Collapses, bool Rotate, bool Fade, bool Narrow,
    float ViewMinW = 0f)
    : ITierLayoutRecord
{
    /// <summary>The band with both rows.</summary>
    public float ExpandedH => Row1H + Row2H + UnderH;

    /// <summary>The band with row 1 alone (no row 2 on this page, or collapsed on scroll).</summary>
    public float CollapsedH => Row1H + UnderH;

    /// <summary>The band's height with or without row 2.</summary>
    public float HeightWith(bool row2) => row2 ? ExpandedH : CollapsedH;

    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions => ImmutableArray<TierRegion>.Empty;
}

/// <summary>The constants of Zune's band and material.</summary>
public static class ZuneBandRules
{
    // ── the node keys the views carry (the tests find them) ──────────────────────────────────────────────────────────────

    /// <summary>The band's root, its two rows, the pivot run, a pivot (`zune-band-pivot:<id>`), the "more" word, the pins cluster, a pin
    /// (`zune-band-pin:<id>`), a pin's play affordance, a row-2 view (`zune-band-view:<id>`), a row-2 action (`zune-band-action:<id>`),
    /// the row-2 title.</summary>
    public const string BandKey = "zune-band", Row1Key = "zune-band-row1", Row2Key = "zune-band-row2", PivotRunKey = "zune-band-pivots",
        PivotKeyPrefix = "zune-band-pivot:", MoreKey = "zune-band-more", PinsKey = "zune-band-pins", PinKeyPrefix = "zune-band-pin:",
        PinPlayKeyPrefix = "zune-band-pin-play:", ViewKeyPrefix = "zune-band-view:", ActionKeyPrefix = "zune-band-action:",
        Row2TitleKey = "zune-band-title";

    /// <summary>The material's parts: the flat ground, the artwork wash, the bleed, the mix.</summary>
    public const string GroundKey = "zune.material.ground", WashKey = "zune.material.wash", BleedKey = "zune.material.bleed",
        MixKey = "zune.material.mix";

    // ── the band ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly ZuneBandLayout[] s_bands =
    [
        new(WidthTier.Compact, Inset: 16f, Row1H: 44f, Row2H: 40f, UnderH: 8f, PivotGap: 24f, RowGap: 24f, Pins: false, PinArt: 32f,
            Row2Gap: 22f, Row2End: 0f, TitleAndActions: false, Collapses: true, Rotate: true, Fade: true, Narrow: true, ViewMinW: 40f),
        new(WidthTier.Small, Inset: 24f, Row1H: 44f, Row2H: 32f, UnderH: 8f, PivotGap: 24f, RowGap: 24f, Pins: false, PinArt: 32f,
            Row2Gap: 20f, Row2End: 24f, TitleAndActions: true, Collapses: true, Rotate: false, Fade: true, Narrow: true),
        new(WidthTier.Standard, Inset: 48f, Row1H: 44f, Row2H: 32f, UnderH: 8f, PivotGap: 24f, RowGap: 24f, Pins: true, PinArt: 32f,
            Row2Gap: 20f, Row2End: 48f, TitleAndActions: true, Collapses: true, Rotate: false, Fade: false, Narrow: false),
        new(WidthTier.Huge, Inset: 48f, Row1H: 44f, Row2H: 32f, UnderH: 8f, PivotGap: 24f, RowGap: 24f, Pins: true, PinArt: 32f,
            Row2Gap: 20f, Row2End: 48f, TitleAndActions: true, Collapses: false, Rotate: false, Fade: false, Narrow: false),
        new(WidthTier.Huge4K, Inset: 64f, Row1H: 64f, Row2H: 40f, UnderH: 8f, PivotGap: 32f, RowGap: 24f, Pins: true, PinArt: 40f,
            Row2Gap: 20f, Row2End: 64f, TitleAndActions: true, Collapses: false, Rotate: false, Fade: false, Narrow: false),
    ];

    /// <summary>The band's record for window tier <paramref name="window"/>.</summary>
    public static ZuneBandLayout Band(WidthTier window) => s_bands[Math.Clamp((int)window, 0, s_bands.Length - 1)];

    /// <summary>The band's record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneBandLayout BandOf(in TierFacts facts)
    {
        if (facts.Policy is null) return Band(WidthTier.Standard);
        var window = Band(facts.Window);
        if (facts.Content == facts.Window) return window;
        // A docked panel narrows the content area under the band (window Standard, content Small): the page lays out at the CONTENT tier's gutter, so the
        // band's lead inset (and, where the content is not Compact, its trailing one) is the content tier's too: the band, the page column and the title
        // bar's wordmark share one left edge in every combination. Everything else (rows, pins, collapse, overflow) stays the window's.
        int w = (int)facts.Window, c = (int)facts.Content;
        if ((uint)w >= (uint)s_mixed.GetLength(0) || (uint)c >= (uint)s_mixed.GetLength(1)) return window;
        return s_mixed[w, c] ??= MixedWith(window, Band(facts.Content));
    }

    private static readonly ZuneBandLayout?[,] s_mixed = new ZuneBandLayout?[5, 5];

    private static ZuneBandLayout MixedWith(ZuneBandLayout window, ZuneBandLayout content)
        => window with { Inset = content.Inset, Row2End = content.Tier == WidthTier.Compact ? window.Row2End : content.Row2End };

    /// <summary>The pins show: the tier shows them (decision 4) and the `bandPins` option is on.</summary>
    public static bool ShowPins(ZuneBandLayout band, bool option) => band.Pins && option;

    /// <summary>Row 2 is folded away: the band collapses in this tier, the `collapseBand` parameter is on and the page last scrolled down.</summary>
    public static bool Collapsed(ZuneBandLayout band, bool parameter, in PageScroll scroll)
        => band.Collapses && parameter && scroll.Direction == PageScrollDirection.Down;

    /// <summary>Row 2 is drawn: the page has views (or, on a detail page, a title or actions to carry) and the band is not collapsed.</summary>
    public static bool Row2Shown(bool hasRow2, bool collapsed) => hasRow2 && !collapsed;

    /// <summary>Row 1's pivot order: as given, or (<see cref="ZuneBandLayout.Rotate"/>) starting at the current one, the ones before it
    /// following at the end (Z-Part-Band compact). Indices into the destinations.</summary>
    public static int[] PivotOrder(int count, int current, bool rotate)
    {
        var order = new int[Math.Max(0, count)];
        int start = rotate && current > 0 && current < count ? current : 0;
        for (int i = 0; i < order.Length; i++) order[i] = (start + i) % count;
        return order;
    }

    /// <summary>The roving tab stop of row 1: the lit pivot, else the first.</summary>
    public static int TabStop(int count, int current) => current >= 0 && current < count ? current : 0;

    /// <summary>Where Left / Right / Home / End move from <paramref name="at"/> among <paramref name="count"/> tabs; -1 for any other key or
    /// at an edge (the key is still swallowed by the caller, as a tab list does).</summary>
    public static int Step(int at, int count, TabKey key) => key switch
    {
        TabKey.Previous => at > 0 ? at - 1 : -1,
        TabKey.Next => at < count - 1 ? at + 1 : -1,
        TabKey.First => count > 0 && at != 0 ? 0 : -1,
        TabKey.Last => count > 0 && at != count - 1 ? count - 1 : -1,
        _ => -1,
    };

    /// <summary>The tab-list keys the band answers.</summary>
    public enum TabKey : byte
    {
        /// <summary>Any other key.</summary>
        None,
        /// <summary>Left.</summary>
        Previous,
        /// <summary>Right.</summary>
        Next,
        /// <summary>Home.</summary>
        First,
        /// <summary>End.</summary>
        Last,
    }

    /// <summary>The band's height motion: it eases between its two heights over the pivot slide (a reflow on the height axis, so the page
    /// below follows each frame; the rows are laid out at full height and clipped from the bottom). Null under reduced motion at the call site.</summary>
    public static readonly LayoutTransition HeightMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height,
        // The page scrolling down is what folds row 2: the scroll-motion latch must not snap the fold it causes.
        AnimateDuringScroll: true);

    /// <summary>The pins island's entrance and exit: it fades and opens (or closes) its width as a reflow, so the pivot run in the same row follows frame by frame.</summary>
    public static readonly LayoutTransition PinsMotion = new(TransitionChannels.Size | TransitionChannels.Opacity,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>The height motion while row 2 folds away: it waits for row 2's fade-out (<see cref="Row2FadeMs"/>), so the clip never cuts the row.</summary>
    public static readonly LayoutTransition HeightMotionAfterFade = HeightMotion with { DelayMs = Row2FadeMs };

    /// <summary>Row 2's fade-out, and the slide that rides it (up, under row 1).</summary>
    public const float Row2FadeMs = 120f, Row2Slide = -8f;

    /// <summary>Row 2 comes back after the band's height has eased open (whole, fading in), and leaves at once (whole, fading out).</summary>
    public static readonly EnterExit Row2Enter = new(Dy: Row2Slide, Opacity: 0f, Active: true, DelayMs: ZuneMotion.PivotSlideMs);

    /// <inheritdoc cref="Row2Enter"/>
    public static readonly EnterExit Row2Exit = new(Dy: Row2Slide, Opacity: 0f, Active: true);

    /// <summary>The edge fade of a row that overflows where the tier does not fade (the pivot run under EdgeFadeScroll at Standard and up).</summary>
    public const float EdgeFadeBand = 32f;

    /// <summary>The fade tiers' edge fade (Compact and Small, rows 1 and 2; the T1 audit's A7): the board masks the row from 84 % of its width
    /// to transparent, so the fade is 16 % of the band's width, never under 48 (32 was invisible on row 1 at 400). Rounded to 4 DIP so a
    /// live resize re-renders the band every 4 DIP of fade, not every frame.</summary>
    public const float EdgeFadeMin = 48f, EdgeFadeShare = 0.16f, EdgeFadeStep = 4f;

    /// <summary>The edge fade of row 1 and row 2 in <paramref name="band"/>'s tier for a band <paramref name="bandWidth"/> wide: max(48, 16 %
    /// of the width) where the tier fades, else <see cref="EdgeFadeBand"/>.</summary>
    public static float EdgeFade(ZuneBandLayout band, float bandWidth)
        => band.Fade ? MathF.Max(EdgeFadeMin, MathF.Round(EdgeFadeShare * bandWidth / EdgeFadeStep) * EdgeFadeStep) : EdgeFadeBand;

    /// <summary>The "pinned" word: 4 before the first pin.</summary>
    public static readonly Edges4 PinnedWordMargin = new(0f, 0f, 4f, 0f);

    /// <summary>The pins: 8 apart (they end on the rows' trailing inset, <see cref="ZuneBandLayout.Row2End"/>).</summary>
    public const float PinGap = 8f;

    /// <summary>The playing dot under a pin: 4 across, 2 under the art.</summary>
    public const float PinDot = 4f, PinDotGap = 2f;

    /// <summary>A pin's play affordance: a 20 disc over the art's lower right corner, its glyph 9, shown on hover and focus.</summary>
    public const float PinPlay = 20f, PinPlayGlyph = 9f;

    /// <summary>The pins' art decode size (the 4K art at 2x).</summary>
    public const int PinDecodePx = 80;

    /// <summary>The "more" word's menu width floor.</summary>
    public const float MoreMenuMinW = 200f;

    /// <summary>Row 2: the title's widest (it ellipsizes before it crowds the views), the hairline after it.</summary>
    public const float TitleMaxW = 360f, HairlineW = 1f, HairlineH = 16f;

    /// <summary>The current view's underline on a detail page (the tab style): 2 tall, the accent.</summary>
    public const float UnderlineH = 2f;

    /// <summary>The actions: 14 apart, every one 28 tall; the discs are 28 round, the ring's border 2; a word pill is padded 12 with a
    /// 2 accent border; the glyphs are 11 (play) and 12 (shuffle), the pill's word 13.</summary>
    public const float ActionGap = 14f, ActionH = 28f, RingBorder = 2f, PillBorder = 2f, PlayGlyph = 11f, ShuffleGlyph = 12f, PillText = 13f;

    /// <summary>A disabled action's opacity (drawn, not clickable).</summary>
    public const float DisabledOpacity = 0.4f;

    /// <summary>A word pill's padding.</summary>
    public static readonly Edges4 PillPadding = new(12f, 0f, 12f, 0f);

    // ── the material ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The artwork wash: a band from the top of the window that fades out over this height (the title bar, the band, a little past).</summary>
    public const float WashH = 180f;

    /// <summary>The wash's opacity at its top (the tint carries its own alpha; this scales it up for a band rather than a floor).</summary>
    public const float WashTopScale = 2f;

    /// <summary>The bleed: 420 tall at 35 %, fading out over its lower 240.</summary>
    public const float BleedH = 420f, BleedOpacity = 0.35f, BleedFade = 240f;

    /// <summary>The mix: the covers behind the top 480 at 18 %, fading out over the lower 240, at most three side by side.</summary>
    public const float MixH = 480f, MixOpacity = 0.18f, MixFade = 240f;

    /// <summary>On the LIGHT ground a photo dims the page instead of lighting it: at the dark ground's strengths a dark artist photo took the band
    /// and the head to about #bfb7c4, which put the tertiary pivots under 3 : 1 and the small grey lines under 4 : 1. The light kind's strengths
    /// (14 % and 10 %) keep the band above about #dadada under the darkest photo.</summary>
    public const float BleedOpacityLight = 0.14f, MixOpacityLight = 0.10f;

    /// <summary>The bleed's opacity on the ground of the kind.</summary>
    public static float BleedOpacityOf(bool light) => light ? BleedOpacityLight : BleedOpacity;

    /// <summary>The mix's opacity on the ground of the kind.</summary>
    public static float MixOpacityOf(bool light) => light ? MixOpacityLight : MixOpacity;

    /// <summary>At most this many mix covers are drawn.</summary>
    public const int MixMax = 3;

    /// <summary>The bleed's and the mix's decode size.</summary>
    public const int BackdropDecodePx = 960;

    /// <summary>The wash and the backdrops cross-fade page to page over the standard 250 ms.</summary>
    public const float MaterialFadeMs = 250f;

    /// <summary>A backdrop layer's cross-fade (opacity only).</summary>
    public static readonly EnterExit MaterialFade = new(Opacity: 0f, Active: true);

    /// <summary>The page-tone leaf's top band (artwork wash on a detail page): the page tone at the plane's alpha, fading out by 60 % of
    /// the page's hero band.</summary>
    public const float ToneFadeStop = 0.6f;
}
