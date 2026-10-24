// ── Themes/WinUi/Structure/Rules/WinUiFrameRules.cs — the numbers of WinUI's frame (themes plan 6.2; P6 W2a) ───────────────────────────
//
// Boards Shell-Top, W-Shell-Compact / Small / Huge / Short, W-Narrow, W-Flyouts and W-Tiers. The data the frame family's views draw with
// (`Views\Frame`, `Views\TitleBar`, `Views\Search`, `Views\Palette`, `Views\FrameBits`, `Views\Masthead`); the per-tier decisions are the
// title bar's record (`Tiers.Frame.cs`). WVTH003 allows literals only here, in `Rules\**`. Public, so `Wavee.Tests` pins them.
//
//   THE MOTION. Every change of the frame animates (W-Tiers: "Every change animates or keeps its space reserved. Nothing snaps"):
//     · the nav pane's column eases its width over 167 ms on NavigationView's pane curve (a mode change: the option, the tier, the user's
//       collapse), and the content card FLIPs with it on the same dynamics, so the card's left edge rides the pane's edge;
//     · the title bar's slot eases its height 88 <-> 48 over 167 ms when tabs turn on or off, as a REFLOW anchored at its trailing (bottom)
//       edge: the toolbar row stays glued to the slot's foot and the page under it follows frame by frame, while the strip is uncovered
//       (tabs on) or wiped away (tabs off) above it.

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.WinUi;

/// <summary>The numbers of WinUI's frame family.</summary>
public static class WinUiFrameRules
{
    // ── motion ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The frame's one duration: the pane's width, the content card's FLIP, the strip row (W-Tiers, the option's "Live" apply).</summary>
    public const float FrameMs = 167f;

    /// <summary>The nav pane's column: its width eases THROUGH REAL LAYOUT on the pane curve (a reflow anchored at its leading edge: the pane's
    /// right edge sweeps), so the content card beside it follows frame by frame. A Reveal (laid out at the final width, a clip easing) left the
    /// card at its final x on the first frame, covering the closing pane at once (W2b: "the rail <-> pane change flips instantly").</summary>
    public static readonly LayoutTransition NavColumnAnim = new(TransitionChannels.Size,
        TransitionDynamics.Tween(FrameMs, Easing.FluentPane), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>The content card: position and size FLIP on the pane's dynamics (relative to the content row, which never moves).</summary>
    public static readonly LayoutTransition ContentCardAnim = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(FrameMs, Easing.FluentPane), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(FrameMs, Easing.FluentPane), SuppressDescendantTransitions: true);

    /// <summary>A docked panel's reservation and gap in the content row: its width eases THROUGH REAL LAYOUT on the panel's own slide (its
    /// duration and curve, `WinUiPanelLayout.SlideMs` / `SlideEasing`), so the content card beside it reflows every frame and its right edge rides
    /// the sliding pane's left edge: no empty strip on an open, no page widening before the pane has left on a close. A seam drag suppresses
    /// layout transitions, so the reservation follows the pointer live.</summary>
    public static readonly LayoutTransition PanelReserveAnim = new(TransitionChannels.Size,
        TransitionDynamics.Tween(WinUiPanelLayout.SlideMs, WinUiPanelLayout.SlideEasing), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>A docked panel's host (dock, float, sheet; the second pane opening beside it): the content card's dynamics.</summary>
    public static readonly LayoutTransition PanelHostAnim = ContentCardAnim;

    /// <summary>The title bar's slot across tabs on / off: its height runs through real layout (the page follows), the bar inside anchored
    /// to the slot's foot so the toolbar row never moves relative to the page.</summary>
    public static readonly LayoutTransition TitleSlotAnim = new(TransitionChannels.Size,
        TransitionDynamics.Tween(FrameMs, Easing.SmoothOut),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing, Axes: SizeAxes.Height);

    /// <summary>The content row's shared-layout group: the content card FLIPs relative to the row.</summary>
    public const string ContentRowMorphId = "winui.content-row";

    // ── the content card ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card's hairline (1 px, left and top only).</summary>
    public const float StrokeWidth = 1f;

    /// <summary>The left+top-only stroke: one uniform ring one DIP larger right and bottom, parked in the card's clip.</summary>
    public static readonly Edges4 StrokeOverhang = new(0f, 0f, -1f, -1f);

    /// <summary>The content layer's corners: r8 on the top left only, from Small up (NavigationView's content); square and flush at Compact.</summary>
    public static CornerRadius4 CardCorners(bool square) => square ? CornerRadius4.All(0f) : new CornerRadius4(Radii.Card, 0f, 0f, 0f);

    /// <summary>A docked pane's band corners: the card radius on the top left.</summary>
    public static CornerRadius4 PaneBandCorners => new(Radii.Card, 0f, 0f, 0f);

    /// <summary>The floating panel's inset from the card's right edge, the toolbar and the player bar (W-Shell-Small: flush).</summary>
    public const float PanelFloatInset = 0f;

    /// <summary>The floating panel's width (W-Shell-Small: 340, acrylic).</summary>
    public const float PanelFloatW = 340f;

    /// <summary>The bottom sheet's scrim (W-Shell-Compact 3: smoke) and its fade.</summary>
    public static readonly ColorF SheetScrim = ColorF.FromRgba(0, 0, 0, 0x4D);

    /// <summary>The sheet scrim's fade, in milliseconds.</summary>
    public const float SheetScrimFadeMs = 167f;

    /// <summary>The offline lane's inset in the card: the tier's inset either side and on top, nothing below.</summary>
    public static Edges4 OfflineLanePadding(float insetX, float insetTop) => new(insetX, insetTop, insetX, 0f);

    // ── the title bar (Shell-Top) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A toolbar button: 40 wide, 36 tall, the control radius (Shell-Top `tool`).</summary>
    public const float ToolW = 40f, ToolH = 36f;

    /// <summary>A toolbar button at Compact: 40 x 40 (the touch floor).</summary>
    public const float CompactToolH = 40f;

    /// <summary>The Compact strip's tab and "+" height: the whole 40 row (the touch floor). The selected tab's close button is the same 40 square.</summary>
    public const float CompactTabH = 40f;

    /// <summary>The ladder strip's "+" above Compact: a 32 square (the hit floor; the engine's Chrome plate is 32 x 24).</summary>
    public const float AddTarget = 32f;

    /// <summary>The search field's width across a tier crossing (520 -> 640), a maximize or a restore: a reflow of the field's sizer inside
    /// hugging ancestors, so the window-centred field re-centres every tick (its x is layout's, never a FLIP of its own). A live resize snaps
    /// it with every projection.</summary>
    public static readonly LayoutTransition FieldWidthMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(FrameMs, Easing.SmoothOut), Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>The identity's width when the name joins the avatar at Huge: a reflow anchored at its end, so the bell, Friends and pin glide.</summary>
    public static readonly LayoutTransition IdentityWidthMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(FrameMs, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing, Axes: SizeAxes.Width);

    /// <summary>Small's ⋯ slot after the bell: it opens and closes its width (the 4 gap and the 40 button) through real layout and fades, so the
    /// bell and everything left of it glide 44 across the 1008 edge (W-Tiers: "Nothing snaps").</summary>
    public static readonly LayoutTransition MoreSlotMotion = new(TransitionChannels.Size | TransitionChannels.Opacity,
        TransitionDynamics.Tween(FrameMs, Easing.SmoothOut), Size: SizeMode.Reflow, Axes: SizeAxes.Width,
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true));

    /// <summary>The toolbar clusters: 4 between buttons, 4 before the first, 8 after the last.</summary>
    public const float ToolGap = 4f, LeadInset = 4f, TrailInset = 8f;

    /// <summary>The engine bar's lead pad after the pane toggle (none: the cluster gap spaces the buttons) and its drag strip before the
    /// caption buttons (the tab row's own; WinUI's minimum drag column).</summary>
    public const float LeadPad = 0f, DragStrip = 48f, CompactDragStrip = 16f;

    /// <summary>The selected tab's height in the 40 row (Shell-Top: 36, its top corners r8).</summary>
    public const float TabH = 36f;

    /// <summary>The search box: 32 tall (Shell-Top `search`).</summary>
    public const float SearchH = 32f;

    /// <summary>The centred search's width at Standard (520) and Huge (640); Small fills the middle up to 520.</summary>
    public const float SearchW = 520f, SearchHugeW = 640f;

    /// <summary>The flyout the Compact search button opens: the window less 12 either side, at most 520.</summary>
    public const float SearchFlyoutMaxW = 520f, SearchFlyoutMargin = 12f;

    /// <summary>The field's magnifier (the query button): its plate stays the stock 30 x 22 inside its 32 x 28 slot, and a press reaches the
    /// field's whole 32 x 32 at that end (the hit floor above Compact). Hit only: no layout, no paint.</summary>
    public static readonly Edges4 QueryHitSlop = new(1f, 5f, 1f, 5f);

    /// <summary>The avatar (32, a circle) and the name beside it at Huge (14, 4 before, 8 after).</summary>
    public const float AvatarSize = 32f;

    /// <summary>The bell's badge: 16 tall, its top 4 below the button's top, its right 6 in.</summary>
    public static readonly Edges4 BadgeMargin = new(0f, 4f, 6f, 0f);

    /// <summary>The Wavee mark (tabs off): a 16 accent square with r4, 8 before the word (12), 8 from the Forward button.</summary>
    public const float MarkSize = 16f, MarkRadius = 4f, MarkGap = 8f, MarkLeadPad = 8f, MarkTextSize = 12f;

    // ── the search popup (W-Flyouts: AutoSuggestBox suggestions) ────────────────────────────────────────────────────────

    /// <summary>The popup's row text: 14; the matched run semibold.</summary>
    public const float SuggestTextSize = 14f;

    /// <summary>The matched run's and the rest's weights.</summary>
    public const ushort MatchWeight = 600, RestWeight = 400;

    /// <summary>A query row's glyph: 16, 12 from the edge, 12 before the text.</summary>
    public const float QueryGlyphSize = 16f;

    /// <summary>A query row's glyph margin.</summary>
    public static readonly Edges4 QueryGlyphMargin = new(0f, 0f, 12f, 0f);

    /// <summary>The popup's padding (4: W-Flyouts' "4 px inner padding").</summary>
    public static readonly Edges4 PopupPadding = Edges4.All(4f);

    /// <summary>A row's padding inside the popup (12 from the edge) and its margin.</summary>
    public static readonly Edges4 QueryRowPadding = new(12f, 0f, 12f, 0f), RowMargin = new(0f, 1f, 0f, 1f);

    /// <summary>The divider between the query rows and the entity rows: edge to edge (W-Flyouts: "Separators run edge to edge").</summary>
    public static readonly Edges4 DividerMargin = new(-4f, 4f, -4f, 4f);

    /// <summary>The progress slot over the rows (3) and a notice's padding.</summary>
    public const float ProgressSlotH = 3f;

    /// <summary>A notice row's padding (the failure's Retry sits at its end).</summary>
    public static Edges4 NoticePadding(bool trailing) => new(12f, 0f, trailing ? 4f : 12f, 0f);

    /// <summary>The popup's width when the field has not reported one.</summary>
    public const float FallbackPopupW = 520f;

    /// <summary>The skeleton rows a pending answer shows: the bars' heights and widths, the art square.</summary>
    public const float SkeletonBarH = 12f, SkeletonSubBarH = 10f, SkeletonQueryBarW = 120f, SkeletonQueryBarStep = 24f, SkeletonRichBarW = 140f,
        SkeletonRichBarStep = 20f, SkeletonSubBarW = 90f, SkeletonArt = 32f, SkeletonTextGap = 6f, SkeletonRichGap = 12f, SkeletonNoticeBarW = 160f;

    /// <summary>The skeleton's width cycle.</summary>
    public const int SkeletonPeriod = 3;

    /// <summary>A skeleton row's padding.</summary>
    public static readonly Edges4 SkeletonRowPadding = new(12f, 0f, 12f, 0f);

    /// <summary>The popup body's height eases when the answer changes size.</summary>
    public static readonly LayoutTransition PopupHeightMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(FrameMs, Easing.FluentDecelerate), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);

    // ── the command palette ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The palette's card: 560 wide, at most 460 tall, its top 64 under the window's top.</summary>
    public const float PaletteW = 560f, PaletteMaxH = 460f, PaletteTop = 64f;

    /// <summary>A palette row: 36 tall (W-Flyouts: "Items 36 high, 4 px radius"), 14 text, 16 glyph 12 from the edge.</summary>
    public const float PaletteRowH = 36f, PaletteTextSize = 14f, PaletteGlyph = 16f;

    /// <summary>A palette row's padding.</summary>
    public static readonly Edges4 PaletteRowPadding = new(12f, 0f, 12f, 0f);

    /// <summary>The card's inner padding (4) and the gap under the box (4).</summary>
    public const float PalettePad = 4f, PaletteGap = 4f;

    // ── the file drop, the cover scrim ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The drop pill's padding and text size.</summary>
    public static readonly Edges4 FileDropPadding = new(16f, 10f, 16f, 10f);

    /// <summary>The drop pill's text.</summary>
    public const float FileDropTextSize = 14f;

    /// <summary>The setup wizard's cover scrim fade (0 under reduced motion).</summary>
    public const float CoverScrimFadeMs = 167f;

    // ── the masthead and the page head (BreadcrumbBar) ───────────────────────────────────────────────────────────────────

    /// <summary>The breadcrumb's slot: 28 tall, the crumbs on its bottom edge (W-BrowseCategory: Browse › Pop, 14 Body).</summary>
    public const float TrailSlotH = 28f;

    /// <summary>The gap between the trail and the title (4).</summary>
    public const float TrailTitleGap = 4f;

    /// <summary>The page title's line: Title Large 40/52 (W-Tiers: "page titles stay Title Large 40").</summary>
    public const float TitleLine = 52f;

    /// <summary>The fewest crumbs (the current page included) the breadcrumb draws: a root page has none.</summary>
    public const int TrailMinDepth = 2;

    /// <summary>The head's height for each form: the title line (Title), the trail over the title (Drill), the trail alone (Hero).</summary>
    public static float HeadHeight(Wavee.Theming.Contracts.PageHeadForm form) => form switch
    {
        Wavee.Theming.Contracts.PageHeadForm.Drill => TrailSlotH + TrailTitleGap + TitleLine,
        Wavee.Theming.Contracts.PageHeadForm.Hero => TrailSlotH,
        _ => TitleLine,
    };

    /// <summary>The masthead's swap: opacity on the page swap's legs (the old head leaves with the old body, the new arrives with the new).</summary>
    public static LayoutTransition MastheadSwap => new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(Design.Nav.EnterDurationMs, Easing.FluentDecelerate),
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Design.Nav.ExitDurationMs, Easing.FluentAccelerate),
        DelayMs: Design.Nav.EnterDelayMs, ExitDelayMs: 0f);

    // ── node keys ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The nav pane's column, the panel's host, the second pane's host, the docked panes' hosts.</summary>
    public const string NavColumnKey = "shell.nav.column", PanelHostKey = "winui.panel.host", SecondHostKey = "winui.panel.host.second";

    /// <summary>A docked pane's host, by its index (never by tier).</summary>
    public static string PaneHostKey(int index) => "winui.panel.pane:" + index;

    /// <summary>The title bar's parts: the tab strip, the toolbar's buttons, the search, the mark.</summary>
    public const string StripKey = "winui.titlebar.strip", BackKey = "winui.titlebar.back", ForwardKey = "winui.titlebar.forward",
        SearchFieldKey = "winui.titlebar.field", SearchButtonKey = "winui.titlebar.search-button", PinKey = "winui.titlebar.pin",
        FriendsKey = "winui.titlebar.friends", BellKey = "winui.titlebar.bell", MoreKey = "winui.titlebar.more", MoreSlotKey = "winui.titlebar.more-slot",
        AvatarKey = "winui.titlebar.avatar", MarkKey = "winui.titlebar.mark", ToolbarKey = "winui.titlebar.toolbar", TabRowKey = "winui.titlebar.tabs";
}
