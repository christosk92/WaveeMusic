// ── Themes/Zune/Structure/Rules/ZuneFrameRules.cs — the numbers of Zune's frame (themes plan 6.3; P5 Z2a) ───────────────────────────
//
// Every number the frame family draws with, read off the boards (Z-Frame-Home, Z-Frame-Panel, Z-Part-TitleBar, Z-Part-Panel,
// Z-Frame-Narrow). The views under `Views\{Frame,TitleBar,Search,Palette,FrameBits,ContentState}` and `Views\Panel\ZunePanelFrame.cs`
// read them; WVTH003 allows literals only here, in `Rules\**`.
//
//   THE TITLE BAR (Z-Part-TitleBar), per WINDOW tier (`ZuneTitleBarLayout`; the height and the lead gutter are `ZuneFrameLayout`'s):
//     tier       search            crumbs   gap   pill          crumb text   bell
//     Compact    the magnifier     no       4     -             -            no
//     Small      the magnifier     yes      12    -             13           no
//     Standard   the pill          yes      12    220 x 32 r16  13           yes
//     Huge       the pill          yes      12    320 x 32 r16  13           yes
//     Huge 4K    the pill          yes      12    400 x 40 r20  15           yes
//   Every button is 40 x 40 (the Compact target floor holds at every tier); the notification dot is 6, 9 in from the button's top right.
//   The pill grows while it is focused (plan 6.3, the #205-era list): +120 at every pill tier, eased (an assumption: no board says how much).
//
//   THE PANEL (Z-Part-Panel, Z-Frame-Panel, Z-Frame-Narrow): the head is the pivot row (26/32/300 words, 18 apart, in a 44 row, 6 under it),
//   inset 24 left and 20 right, 12 from the top; the close is a 40 x 40 glyph; a floating panel is 340 wide, flush right, under the band.
//
// Public, so `Wavee.Tests` can pin them.

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>What Zune's title bar lays out in one window tier (Z-Part-TitleBar).</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Pill">The search is the pill in the row (Standard and up); the magnifier that opens a flyout otherwise.</param>
/// <param name="Crumbs">The page's trail stands beside Back (every tier but Compact).</param>
/// <param name="BellInRow">The notification bell stands in the row (the pill tiers; the account menu carries it below).</param>
/// <param name="Gap">The gap between the row's parts.</param>
/// <param name="PillW">The pill's rest width.</param>
/// <param name="PillH">The pill's height.</param>
/// <param name="PillRadius">The pill's corner (half its height).</param>
/// <param name="LargeCrumbs">The trail is set in the body voice (15) instead of the meta voice (13): Huge 4K.</param>
/// <param name="DragStrip">The drag strip before the caption buttons.</param>
/// <param name="FlyoutW">The magnifier's flyout width (the window less the gutters when narrower).</param>
public sealed record ZuneTitleBarLayout(WidthTier Tier, bool Pill, bool Crumbs, bool BellInRow, float Gap, float PillW, float PillH,
    float PillRadius, bool LargeCrumbs, float DragStrip, float FlyoutW) : ITierLayoutRecord
{
    /// <inheritdoc />
    public ImmutableArray<TierRegion> Regions => ImmutableArray<TierRegion>.Empty;
}

/// <summary>The constants of Zune's frame, title bar, search, palette, frame bits, content states and panel frame.</summary>
public static class ZuneFrameRules
{
    // ── the node keys the views carry (the tests find them) ──────────────────────────────────────────────────────────────

    /// <summary>The title bar's parts. Back and the identity chip keep the shell's keys (`chrome-back`, `chrome-profile`), so the probes and
    /// the tier harness read Zune's bar as they read Classic's.</summary>
    public const string WordmarkKey = "zune-wordmark", BackKey = "chrome-back", CrumbsKey = "zune-crumbs", PillKey = "zune-search-pill",
        SearchIconKey = "zune-search-icon", ProfileKey = "chrome-profile", ConnectingKey = "chrome-connecting", SignInKey = "chrome-sign-in",
        BellKey = "zune-bell", BellDotKey = "zune-bell-dot", PaneToggleKey = "zune-pane-toggle";

    /// <summary>The search popup's progress slot (always the popup's first child) and the history menu's root.</summary>
    public const string PopupProgressKey = "zune-omni-progress", HistoryMenuKey = "zune-history-menu";

    /// <summary>The frame's parts: the band slot (Z2b's `Shell.NavPane`), the page clip, the panel's host and the second pane's host.</summary>
    public const string BandSlotKey = "zune-band-slot", PageClipKey = "zune-page", PanelHostKey = "zune-panel-host",
        SecondPanelHostKey = "zune-panel-host-2";

    /// <summary>The panel's parts: its pivot head, one pivot word (`zune-panel-pivot:<mode>`), the close glyph.</summary>
    public const string PanelHeadKey = "zune-panel-head", PanelPivotKeyPrefix = "zune-panel-pivot:", PanelCloseKey = "zune-panel-close";

    /// <summary>The page head's title (stable across a swap, so Browse home -> a category does not remount it).</summary>
    public const string PageTitleKey = "zune-page-title";

    // ── the title bar ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every title-bar button: 40 x 40 (the Compact target floor, Z-Part-TitleBar).</summary>
    public const float Button = 40f;

    /// <summary>The notification dot: 6 across, 9 in from the bell's top right (Z-Part-TitleBar).</summary>
    public const float DotSize = 6f, DotInset = 9f;

    /// <summary>The engine bar's own left padding: the lead pad is the gutter less it, so the wordmark starts on the page's x.</summary>
    public const float EngineRootPad = 2f;

    /// <summary>The account sits 8 further after the pill (Z-Part-TitleBar's margin-left), past the row's gap.</summary>
    public static readonly Edges4 AccountMargin = new(8f, 0f, 0f, 0f);

    /// <summary>The notification dot's place in the bell's box: 9 down from the top, 9 in from the right.</summary>
    public static readonly Edges4 DotMargin = new(0f, DotInset, DotInset, 0f);

    /// <summary>The gap between the trail's words and its separators.</summary>
    public const float CrumbGap = 6f;

    /// <summary>The trail shows from a depth of two (a root page has nothing above it).</summary>
    public const int CrumbMinDepth = 2;

    /// <summary>The "Connecting" caption's padding.</summary>
    public static readonly Edges4 ConnectingPadding = new(8f, 0f, 8f, 0f);

    /// <summary>What the pill grows by while it has focus or its suggestions are open.</summary>
    public const float PillFocusGrow = 120f;

    /// <summary>The pill's focus growth: a reflow on the width axis (the box and its text move together), the pivot's 250 ms on the
    /// smooth-out curve; null under reduced motion at the call site.</summary>
    public static readonly LayoutTransition PillWidthMotion = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut), Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    private static readonly ZuneTitleBarLayout[] s_titleBars =
    [
        new(WidthTier.Compact, Pill: false, Crumbs: false, BellInRow: false, Gap: 4f, PillW: 0f, PillH: 32f, PillRadius: 16f,
            LargeCrumbs: false, DragStrip: 8f, FlyoutW: 360f),
        new(WidthTier.Small, Pill: false, Crumbs: true, BellInRow: false, Gap: 12f, PillW: 0f, PillH: 32f, PillRadius: 16f,
            LargeCrumbs: false, DragStrip: 16f, FlyoutW: 400f),
        new(WidthTier.Standard, Pill: true, Crumbs: true, BellInRow: true, Gap: 12f, PillW: 220f, PillH: 32f, PillRadius: 16f,
            LargeCrumbs: false, DragStrip: 16f, FlyoutW: 400f),
        new(WidthTier.Huge, Pill: true, Crumbs: true, BellInRow: true, Gap: 12f, PillW: 320f, PillH: 32f, PillRadius: 16f,
            LargeCrumbs: false, DragStrip: 16f, FlyoutW: 480f),
        new(WidthTier.Huge4K, Pill: true, Crumbs: true, BellInRow: true, Gap: 12f, PillW: 400f, PillH: 40f, PillRadius: 20f,
            LargeCrumbs: true, DragStrip: 24f, FlyoutW: 560f),
    ];

    /// <summary>The title bar's record for window tier <paramref name="window"/>.</summary>
    public static ZuneTitleBarLayout TitleBar(WidthTier window) => s_titleBars[Math.Clamp((int)window, 0, s_titleBars.Length - 1)];

    /// <summary>The title bar's record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneTitleBarLayout TitleBarOf(in TierFacts facts) => TitleBar(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The engine bar's spacers in window tier <paramref name="window"/>: the lead pad puts the wordmark on the page's gutter.</summary>
    public static TitleBarSpacing Spacing(WidthTier window)
        => new(LeadPad: MathF.Max(0f, ZuneTiers.Frame(window).Gutter - EngineRootPad), MinDragStrip: TitleBar(window).DragStrip);

    /// <summary>The magnifier's flyout width in a window <paramref name="windowWidth"/> wide: the tier's width, never wider than the window
    /// less a gutter either side.</summary>
    public static float FlyoutWidth(in TierFacts facts, float windowWidth)
    {
        var frame = ZuneTiers.FrameOf(facts);
        return MathF.Max(Button, MathF.Min(TitleBarOf(facts).FlyoutW, windowWidth - 2f * frame.Gutter));
    }

    // ── the search popup (the suggestions body) ──────────────────────────────────────────────────────────────────────────

    /// <summary>The popup's width before the anchor reports one (the Standard pill).</summary>
    public const float FallbackPopupWidth = 220f;

    /// <summary>The popup root's padding: 4 above and below the rows (square plate, Z-Overlays' menu rhythm).</summary>
    public static readonly Edges4 PopupPadding = new(0f, 4f, 0f, 4f);

    /// <summary>The progress line's slot: the first child, 2 tall (Zune's seek-line weight).</summary>
    public const float ProgressSlotHeight = 2f;

    /// <summary>A query row: 36 tall rows inset 16 (the menu rows of Z-Overlays), no margin, square.</summary>
    public const float QueryRowH = 36f;

    /// <summary>A query row's padding.</summary>
    public static readonly Edges4 QueryRowPadding = new(16f, 0f, 16f, 0f);

    /// <summary>A query row's glyph and the gap after it.</summary>
    public const float QueryGlyphSize = 14f;

    /// <summary>The gap after the query glyph.</summary>
    public static readonly Edges4 QueryGlyphMargin = new(0f, 0f, 12f, 0f);

    /// <summary>The weight of the typed run (Zune's selected weight) and of the rest of a query row.</summary>
    public const ushort MatchWeight = 600, RestWeight = 400;

    /// <summary>The hairline between the query rows and the rich rows: inset 16, 6 above and below.</summary>
    public static readonly Edges4 DividerMargin = new(16f, 6f, 16f, 6f);

    /// <summary>A rich row's margin (none: the rows meet the plate's edges, square).</summary>
    public static readonly Edges4 RowMargin = new(0f, 0f, 0f, 0f);

    /// <summary>A notice's padding: 16 either side.</summary>
    public static readonly Edges4 NoticePadding = new(16f, 0f, 16f, 0f);

    /// <summary>The skeleton bars (a pending body with nothing to show): heights, widths, the period and the art square.</summary>
    public const float SkeletonBarH = 10f, SkeletonSubBarH = 8f, SkeletonQueryBarW = 110f, SkeletonQueryBarStep = 30f,
        SkeletonRichBarW = 120f, SkeletonRichBarStep = 24f, SkeletonSubBarW = 80f, SkeletonArt = 40f, SkeletonRichGap = 12f,
        SkeletonTextGap = 6f, SkeletonNoticeBarW = 140f;

    /// <summary>The skeleton bars' widths repeat every three rows.</summary>
    public const int SkeletonPeriod = 3;

    /// <summary>The popup root's height motion: rows arriving or leaving ease the plate from its top edge (reflow on the height axis).</summary>
    public static readonly LayoutTransition PopupHeightMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    // ── the command palette ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The palette card: 560 wide (no clamp), at most 460 tall, its top 64 down (the shell's lane, kept).</summary>
    public const float PaletteW = 560f, PaletteMaxH = 460f, PaletteTop = 64f;

    /// <summary>The card's padding: 16 in, 12 above the field.</summary>
    public static readonly Edges4 PalettePadding = new(16f, 12f, 16f, 12f);

    /// <summary>The lane's padding: the card's top edge sits <see cref="PaletteTop"/> down.</summary>
    public static readonly Edges4 PaletteLanePadding = new(0f, PaletteTop, 0f, 0f);

    /// <summary>The field's width: the card less its 16 either side.</summary>
    public const float PaletteFieldW = PaletteW - 32f;

    /// <summary>The gap under the field, before the rows.</summary>
    public const float PaletteFieldGap = 8f;

    /// <summary>A result row: 36 tall, inset 16 (the menu rows), the glyph 16 and 12 before the label.</summary>
    public const float PaletteRowH = 36f, PaletteGlyph = 16f, PaletteRowGap = 12f;

    /// <summary>A result row's padding.</summary>
    public static readonly Edges4 PaletteRowPadding = new(16f, 0f, 16f, 0f);

    // ── the frame bits ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The file-drop cue's plate padding (square, the raised plate, Z-Overlays' toast rhythm).</summary>
    public static readonly Edges4 FileDropPadding = new(24f, 14f, 24f, 14f);

    /// <summary>The cover scrim's fade (0 under reduced motion).</summary>
    public const float CoverScrimFadeMs = 250f;

    // ── the content states and the page head ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The offline lane's inset: the page <paramref name="gutter"/> either side, 16 from the top, nothing below.</summary>
    public static Edges4 OfflineLanePadding(float gutter) => new(gutter, 16f, gutter, 0f);

    // ── the frame ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page row's shared-layout group: the page column FLIPs relative to the row when the panel docks or leaves.</summary>
    public const string ContentRowMorphId = "zune.content-row";

    /// <summary>The page column and the panel host move on one transition: the panel's dock and its leaving ease the page's right edge over
    /// 300 ms on the smooth-out curve, laid out at the final size (a clip and a translate ease).</summary>
    public const float DockMs = 300f;

    /// <inheritdoc cref="DockMs"/>
    public static readonly LayoutTransition PageColumnAnim = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(DockMs, Easing.SmoothOut), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(DockMs, Easing.SmoothOut), SuppressDescendantTransitions: true);


    // ── the panel ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A floating panel's width (Z-Frame-Narrow, Z-Part-Panel "overlay").</summary>
    public const float PanelFloatW = 340f;

    /// <summary>The pivot head: 44 tall, 12 from the panel's top, 6 above the body, inset 24 left and 8 right (the close glyph's own 40 box
    /// carries the board's 20).</summary>
    public const float PanelHeadH = 44f, PanelPivotGap = 18f;

    /// <summary>The pivot head's padding.</summary>
    public static readonly Edges4 PanelHeadPadding = new(24f, 12f, 8f, 6f);

    /// <summary>A body arm's padding under the head (the bodies are the player lane's views: they carry their own insets).</summary>
    public static readonly Edges4 PanelBodyPadding = new(0f, 0f, 0f, 16f);

    /// <summary>The close glyph's size.</summary>
    public const float PanelCloseGlyph = 14f;

    /// <summary>The panel's open/close slide.</summary>
    public const float PanelSlideMs = 300f;

    /// <summary>The slide's curve.</summary>
    public static readonly EasingSpec PanelSlideEasing = Easing.SmoothOut;

    /// <summary>The sheet's dim (black at 40 %, the Compact board's).</summary>
    public static ColorF SheetScrim => ColorF.FromRgba(0, 0, 0, 102);

    /// <summary>The scrim's fade.</summary>
    public const float SheetScrimFadeMs = 167f;

    /// <summary>The now-playing and video arms: 24 in either side, 12 between their parts, 16 above the "up next" head.</summary>
    public static readonly Edges4 PanelArmPadding = new(24f, 4f, 20f, 16f);

    /// <summary>The gap between an arm's parts.</summary>
    public const float PanelArmGap = 12f;
}
