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
//   The wordmark is drawn whole from Small up and never at Compact (the T1 audit's A9); at Compact the search is the field filling the lead
//   beside Back while the Search route is on screen (the core's `OnSearchRoute`) or the field has the focus (Z-Search-Compact); the account's name is capped at 160 on the pill tiers.
//
//   THE PAGE COLUMN (the T1 audit's A1; P5 decision 2): every page's content is capped at 2400 between the gutters and centred
//   (`PageRow` around `PageColumn`), and the band's rows sit in the same column, so the band's first pivot, every title and the page's content start at
//   max(gutter, (W - 2400) / 2) and end on its mirror. The title bar and the play bar are window chrome: they keep the plain gutter.
//
//   THE PANEL (Z-Part-Panel, Z-Frame-Panel, Z-Frame-Narrow): the head is the pivot row (26/32/300 words, 18 apart, in a 44 row, 6 under it),
//   ONE line (owner, 2026-10-24: never wrapped), the selected word on the 24 gutter and the rest round the cycle after it, running off under a
//   24 fade; 12 from the top; the close is a 40 x 40 glyph; a floating panel is 340 wide, flush right, under the band.
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
/// <param name="Wordmark">The wordmark leads the row (every tier but Compact, where Back leads on the gutter: the T1 audit's A9, the
/// wordmark never truncates, so where it has no room it is not drawn).</param>
/// <param name="QueryField">While the Search route is on screen (the core's exact `OnSearchRoute` flag, contract 1.30) the search is the FIELD, filling the lead island beside Back, instead of the magnifier (Compact: Z-Search-Compact,
/// "the field takes the title bar").</param>
/// <param name="AccountNameW">The account chip's name at most this wide (Standard, Huge, Huge 4K: 160); 0 keeps the core's name budget.</param>
public sealed record ZuneTitleBarLayout(WidthTier Tier, bool Pill, bool Crumbs, bool BellInRow, float Gap, float PillW, float PillH,
    float PillRadius, bool LargeCrumbs, float DragStrip, float FlyoutW, bool Wordmark, bool QueryField, float AccountNameW) : ITierLayoutRecord
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
    public const string PanelHeadKey = "zune-panel-head", PanelPivotKeyPrefix = "zune-panel-pivot:", PanelCloseKey = "zune-panel-close",
        PanelPaneKeyPrefix = "zune-panel-pane:";

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

    /// <summary>The pill's key hint (the "ctrl f" chip): its node key and its inset from the pill's end (past the 28 query button).</summary>
    public const string PillHintKey = "zune-search-hint";

    /// <inheritdoc cref="PillHintKey"/>
    public const float PillHintInset = 40f;

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

    // ── the pill beside a docked panel (the owner, 2026-10-21: "the searchbar is in between the colors") ─────────────────────────
    //
    // The docked panel's surface runs up under the title bar's end (ZuneFrame, design B), so the trailing island's account and bell sit
    // on the panel's wash while the pill, left of them, could straddle the page/panel seam. While a panel docks, the pill stands wholly on
    // the PAGE side: its right edge one row gap before the panel's left edge (a spacer after it in its host, easing as the island does); the
    // account and the bell keep their places. It narrows (never below `PillMinW`) when the page side is short of room for the lead's
    // wordmark and Back beside it; only below that floor does it keep today's place at the account (the pill tiers' own answer: the
    // magnifier is a window-tier swap, never a panel's). With no panel docked nothing moves. At every docking size the whole pill fits
    // (Standard's narrowest docked page is 668: 152 for the lead, 492 for a 220 pill), so the narrowing is the guard, not the rule.

    /// <summary>The spacer after the pill in its host (its width is how far left of the account the pill stands).</summary>
    public const string PillShiftKey = "zune-search-pill-shift";

    /// <summary>A laid-out width within this of its target has settled (the omnibar model's half DIP).</summary>
    public const float SettleSlop = 0.5f;

    /// <summary>The pill's floor beside a docked panel: below it the pill keeps its place at the account (see the block above).</summary>
    public const float PillMinW = 160f;

    /// <summary>The engine bar's caption cluster after the drag strip: minimise, maximise, close (`CaptionButton.Width` each).</summary>
    public const float CaptionCluster = 3f * CaptionButton.Width;

    /// <summary>Where the pill stands beside a docked panel <paramref name="dockedW"/> wide (0: none) in a window <paramref name="viewportW"/>
    /// wide: the spacer after it (<see cref="PillPlace.Shift"/>) and its widest (<see cref="PillPlace.MaxW"/>, infinite where nothing caps
    /// it). <paramref name="accountW"/> is the account host's laid-out width, <paramref name="wordmarkW"/> the wordmark's, <paramref
    /// name="gutter"/> the lead's start (the frame's gutter).</summary>
    public static PillPlace PillBesidePanel(ZuneTitleBarLayout rec, float viewportW, float dockedW, float accountW, float wordmarkW, float gutter)
    {
        if (!rec.Pill || !(dockedW > 0f) || !float.IsFinite(accountW)) return PillPlace.AtAccount;
        // The pill host's right edge from the window's right edge: the caption cluster, the drag strip, the bell and its gap, the account,
        // the account's margin and the row's gap (right-anchored: the window's width never moves it).
        float hostRight = CaptionCluster + rec.DragStrip + (rec.BellInRow ? Button + rec.Gap : 0f) + accountW + AccountMargin.Left + rec.Gap;
        float shift = MathF.Max(0f, dockedW + rec.Gap - hostRight);
        // The page side's room for the pill: from the lead's fixed parts (the wordmark, Back) and a row gap to the pill's right edge.
        float leadEnd = gutter + (rec.Wordmark && float.IsFinite(wordmarkW) ? wordmarkW + rec.Gap : 0f) + Button;
        float room = viewportW - hostRight - shift - rec.Gap - leadEnd;
        return room < PillMinW ? PillPlace.AtAccount : new PillPlace(shift, room);
    }

    /// <summary>The account chip's name cap on the pill tiers (the T1 audit's A9: "Wavee List..." truncated under the core's 76).</summary>
    public const float AccountNameMaxW = 160f;

    /// <summary>What the core's name budget charges beyond the name itself (`ProfileRules.NameCap`: the chip's 8 gap and 6 extra pad).</summary>
    public const float AccountNameChrome = 14f;

    // Compact's field is the board's 40 tall, r20 (Z-Search-Compact): the touch tier's field, in the title bar or the magnifier's flyout.
    private static readonly ZuneTitleBarLayout[] s_titleBars =
    [
        new(WidthTier.Compact, Pill: false, Crumbs: false, BellInRow: false, Gap: 4f, PillW: 0f, PillH: 40f, PillRadius: 20f,
            LargeCrumbs: false, DragStrip: 8f, FlyoutW: 360f, Wordmark: false, QueryField: true, AccountNameW: 0f),
        new(WidthTier.Small, Pill: false, Crumbs: true, BellInRow: false, Gap: 12f, PillW: 0f, PillH: 32f, PillRadius: 16f,
            LargeCrumbs: false, DragStrip: 16f, FlyoutW: 400f, Wordmark: true, QueryField: false, AccountNameW: 0f),
        new(WidthTier.Standard, Pill: true, Crumbs: true, BellInRow: true, Gap: 12f, PillW: 220f, PillH: 32f, PillRadius: 16f,
            LargeCrumbs: false, DragStrip: 16f, FlyoutW: 400f, Wordmark: true, QueryField: false, AccountNameW: AccountNameMaxW),
        new(WidthTier.Huge, Pill: true, Crumbs: true, BellInRow: true, Gap: 12f, PillW: 320f, PillH: 32f, PillRadius: 16f,
            LargeCrumbs: false, DragStrip: 16f, FlyoutW: 480f, Wordmark: true, QueryField: false, AccountNameW: AccountNameMaxW),
        new(WidthTier.Huge4K, Pill: true, Crumbs: true, BellInRow: true, Gap: 12f, PillW: 400f, PillH: 40f, PillRadius: 20f,
            LargeCrumbs: true, DragStrip: 24f, FlyoutW: 560f, Wordmark: true, QueryField: false, AccountNameW: AccountNameMaxW),
    ];

    /// <summary>The search is the field filling the lead island (<see cref="ZuneTitleBarLayout.QueryField"/>): the tier draws it there, its
    /// flyout is closed (typing in the magnifier's flyout keeps the magnifier, its anchor, until the flyout closes on the search), and the
    /// Search route is on screen (<paramref name="onSearchRoute"/>, the core's exact flag, contract 1.30: not "the omnibar holds a query",
    /// which a stale query outside the route also satisfied) or the field has the focus (clearing the query while typing in it never takes
    /// the field away under the caret).</summary>
    public static bool FieldInLead(ZuneTitleBarLayout rec, bool onSearchRoute, bool flyoutOpen, bool focused)
        => rec.QueryField && !flyoutOpen && (onSearchRoute || focused);

    /// <summary>The account name's cap in <paramref name="rec"/>'s tier: the record's, else the core's <paramref name="coreCap"/>.</summary>
    public static float AccountNameCap(ZuneTitleBarLayout rec, float coreCap) => rec.AccountNameW > 0f ? rec.AccountNameW : coreCap;

    /// <summary>The title bar's record for window tier <paramref name="window"/>.</summary>
    public static ZuneTitleBarLayout TitleBar(WidthTier window) => s_titleBars[Math.Clamp((int)window, 0, s_titleBars.Length - 1)];

    /// <summary>The title bar's record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ZuneTitleBarLayout TitleBarOf(in TierFacts facts) => TitleBar(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The engine bar's spacers in window tier <paramref name="window"/>: the lead pad puts the wordmark on the page's gutter.</summary>
    public static TitleBarSpacing Spacing(WidthTier window)
        => new(LeadPad: MathF.Max(0f, ZuneTiers.Frame(window).Gutter - EngineRootPad), MinDragStrip: TitleBar(window).DragStrip);

    /// <summary>The engine bar's spacers for <paramref name="facts"/>: the drag strip is the window's, the lead pad puts the wordmark on the frame's gutter (the window tier's,
    /// <see cref="ZuneTiers.GutterOf"/>): the same one the band and every page use, so a docked panel moves none of them.</summary>
    public static TitleBarSpacing SpacingOf(in TierFacts facts)
    {
        var window = facts.Policy is null ? WidthTier.Standard : facts.Window;
        return new(LeadPad: MathF.Max(0f, ZuneTiers.GutterOf(in facts) - EngineRootPad), MinDragStrip: TitleBar(window).DragStrip);
    }

    /// <summary>The magnifier's flyout width in a window <paramref name="windowWidth"/> wide: the tier's width, never wider than the window
    /// less a gutter either side.</summary>
    public static float FlyoutWidth(in TierFacts facts, float windowWidth)
    {
        var frame = ZuneTiers.FrameOf(facts);
        return MathF.Max(Button, MathF.Min(TitleBarOf(facts).FlyoutW, windowWidth - 2f * frame.Gutter));
    }

    // ── the search popup (the suggestions body) ──────────────────────────────────────────────────────────────────────────

    /// <summary>The suggestions' foot row ("see all results for" the query, Z-Overlays).</summary>
    public const string SeeAllKey = "zune-omni-see-all";

    /// <summary>The air the suggestions plate leaves above the play bar.</summary>
    public const float PopupFootAir = 8f;

    /// <summary>The suggestions body's cap in a window <paramref name="windowH"/> tall: the core's <paramref name="coreCap"/>, or the room between the rows' top
    /// (<paramref name="rowsTop"/>) and the dock (less the progress slot, the plate's padding, the see-all row of <paramref name="rowH"/> and the foot air), whichever is less, and
    /// never under one row: the plate ends above the play bar at every height (V16: at Small it ran off the window).</summary>
    public static float PopupBodyCap(float coreCap, float windowH, float rowsTop, float dockReserve, float rowH)
        => MathF.Max(rowH, MathF.Min(coreCap,
            windowH - rowsTop - dockReserve - ProgressSlotHeight - PopupPadding.Top - PopupPadding.Bottom - rowH - PopupFootAir));

    /// <summary>The popup's width before the anchor reports one (the Standard pill).</summary>
    public const float FallbackPopupWidth = 220f;

    /// <summary>The popup root's padding: 4 above and below the rows (square plate, Z-Overlays' menu rhythm).</summary>
    public static readonly Edges4 PopupPadding = new(0f, 4f, 0f, 4f);

    /// <summary>The progress line's slot: the first child, 2 tall (Zune's seek-line weight).</summary>
    public const float ProgressSlotHeight = 2f;

    /// <summary>A query row: 36 tall rows inset 16 (the menu rows of Z-Overlays), no margin, square.</summary>
    public const float QueryRowH = 36f;

    /// <summary>The query row's height at a <paramref name="window"/>: 36, and the 40 touch floor at Compact (owner: Compact targets >= 40).</summary>
    public static float QueryRowHFor(WidthTier window) => MathF.Max(QueryRowH, TouchTargetPolicy.FloorFor(window));

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

    // ── the page column (the T1 audit's A1: ONE column for every page and the band) ──────────────────────────────────────

    /// <summary>The widest a page's content grows (P5 decision 2): 2400, centred in the content column once the window passes it.</summary>
    public const float PageCap = 2400f;

    /// <summary>The page column: <paramref name="page"/> capped at <see cref="PageCap"/>, grown from basis 0 up to its cap and stretched across its row
    /// (its own top and bottom padding kept; its left and right padding are the row's gutter spacers, <see cref="Gutter"/>). ON ITS OWN IT COLLAPSES in
    /// this engine (a centred cross-axis child takes its MEASURED width, so the page shrinks to its content): a page stands in it through a centring row,
    /// <see cref="PageRow"/> (or the detail kit's `CentredPage`).</summary>
    public static BoxEl PageColumn(BoxEl page) => page with
    {
        MaxWidth = PageCap, AlignSelf = FlexAlign.Stretch, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
        Padding = page.Padding with { Left = 0f, Right = 0f }, DropTarget = null,
    };

    /// <summary>The page column's gutter, either side: a spacer, never padding (padding cannot ease, the engine has no padding channel). A crossing
    /// that changes the gutter (16 / 24 / 48 / 48 / 64: 640, 1008, 3200) eases its WIDTH through real layout (<see cref="GutterMotion"/>), so the page
    /// rides the re-solve instead of jumping by the difference on the commit frame.</summary>
    public static BoxEl Gutter(float width) => new()
    {
        Width = width, Shrink = 0f, HitTestVisible = false, Animate = Design.Reduced ? null : GutterMotion,
    };

    /// <summary>The gutter spacers' ease: the pivot slide's 250 ms on the smooth-out curve, a reflow on the width axis (a size channel: the engine's
    /// reflow-shove rule snaps a position slide beside a reflowing page, never this) — the band's inset and the play bar's row insets move with it.</summary>
    public static readonly LayoutTransition GutterMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneMotion.PivotSlideMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>A page in the page column: [gutter · <see cref="PageColumn"/> · gutter] in a centring row, so its content runs from x = max(gutter,
    /// (W - 2400) / 2) to the mirror of it in a content column W wide, the x the band's lead inset and every page title share (the band's rows sit in the
    /// same column). The row (<see cref="FlexJustify.Center"/>) takes the page's place in its parent (its grow, shrink, basis, min height and margin;
    /// stretched across a column parent) and its drop target (so a drop on the gutter still lands); the page keeps its key, its motion and every other
    /// handler, grows from basis 0 up to its cap (never to its content's width: a short page, a skeleton or a vacancy keeps the column's width) and is
    /// stretched to the row's height.</summary>
    public static BoxEl PageRow(BoxEl page, float gutter) => new()
    {
        Direction = 0, Justify = FlexJustify.Center, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch,
        Grow = page.Grow, Shrink = page.Shrink, Basis = page.Basis, MinWidth = 0f, MinHeight = page.MinHeight, Margin = page.Margin,
        DropTarget = page.DropTarget,
        Children = [Gutter(gutter), PageColumn(page) with { Margin = default }, Gutter(gutter)],
    };

    /// <summary>Where content starts in the page column of a content column <paramref name="width"/> wide: max(gutter, (W - 2400) / 2), the
    /// x of the band's first pivot, every page title and the page content's left edge (48 at 1440, 80 at 2560, 720 at 3840).</summary>
    public static float ColumnLead(float width, float gutter) => MathF.Max(gutter, (width - PageCap) / 2f);

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

    /// <summary>The pivot head: 44 tall, 12 from the panel's top, 6 above the body, 8 in on the right (the close glyph's own 40 box carries the
    /// board's 20); the lead word stands on <see cref="PanelGutter"/>.</summary>
    public const float PanelHeadH = 44f, PanelPivotGap = 18f;

    /// <summary>The panel's gutter: the lead pivot word stands on it, and the bodies' content (`ZunePanelBodies.Inset`) starts on it.</summary>
    public const float PanelGutter = 24f;

    /// <summary>The pivot words' strip: it clips the line at the close glyph, reaches the panel's left edge and carries the gutter as its inset. Its
    /// soft edges: the left one lies over the empty gutter (a word sliding out fades there; the lead never stands under it), the right one, drawn
    /// only while the words run past the strip, over the strip's last <see cref="PanelEdgeFade"/>, so a trailing word reads until it leaves.</summary>
    public const string PanelPivotsKey = "zune-panel-pivots";
    public const float PanelEdgeFade = 24f;

    /// <summary>A pivot word that moved less than this did not move (half a DIP); the radix that folds the words' order into one key.</summary>
    public const float PanelPivotStill = 0.5f;
    public const int PanelModeRadix = 7;

    /// <summary>A pivot word that wraps round the cycle where both its legs show: the share of the slide it spends leaving before it arrives.</summary>
    public const float PanelPivotWrapSplit = 0.5f;

    /// <summary>A pivot change's panes: the new one comes in from this far on the side of travel, the old one goes as far the other way and fades
    /// out over <see cref="PanelPaneFadeMs"/> (the slide itself is the pivot slide, `ZuneMotion.PivotSlideMs`).</summary>
    public const float PanelPaneShift = 48f, PanelPaneFadeMs = 150f;

    /// <summary>The panel's accent ink while its cover wash is drawn is lifted until it reads this against the wash's head colour over the plate (the text floor is
    /// 4.5; the margin covers the window wash that shows through a docked panel's head and the text's antialiasing), in <see cref="PanelInkLiftSteps"/> bisection steps,
    /// toward white on the dark ground and black on the light one.</summary>
    public const float PanelInkRatio = 5.2f;
    public const int PanelInkLiftSteps = 10;

    /// <summary>The colour the accent ink lifts toward on the ground of the kind.</summary>
    public static ColorF PanelInkLiftToward(bool light) => light ? ColorF.FromRgba(0, 0, 0) : ColorF.FromRgba(255, 255, 255);

    /// <summary>The pivot head's padding (no left inset: the words' strip carries the gutter).</summary>
    public static readonly Edges4 PanelHeadPadding = new(0f, 12f, 8f, 6f);

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

/// <summary>Where the title bar's pill stands beside a docked panel (<see cref="ZuneFrameRules.PillBesidePanel"/>).</summary>
/// <param name="Shift">The spacer after the pill in its host: how far left of its place at the account it stands (0: there).</param>
/// <param name="MaxW">The pill's widest (infinite: its record's width, rest or focused, uncapped).</param>
public readonly record struct PillPlace(float Shift, float MaxW)
{
    /// <summary>Today's place at the account, uncapped: no panel docks, or the page side has no room for the pill's floor.</summary>
    public static PillPlace AtAccount => new(0f, float.PositiveInfinity);
}
