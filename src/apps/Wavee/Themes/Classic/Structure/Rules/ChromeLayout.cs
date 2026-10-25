// ── Themes/Classic/Structure/Rules/ChromeLayout.cs — the numbers of Classic's title bar and search (themes plan 6.1, T4b) ─────
//
// What `Shell.UI.cs` and `Shell.Masthead.UI.cs` held as literals beside the chrome row's builders and the omnibar's popup,
// moved verbatim with the pictures: the island buttons' footprint, the identity chip's box, the tab strip's text size and its lane
// motion, the search field's floor, and the popup's paddings, margins, skeleton bars and height motion. They are data, not
// decisions: `Views\TitleBar\ClassicTitleBar.cs` and `Views\Search\ClassicSearch.cs` read them (WVTH003 allows literals only here,
// in `Rules\**`). The WIDTHS the row is allocated by (what each affordance costs, where the optional ones may appear, how wide the
// search and the tabs may be) are not here: they are `ClassicPolicies.Chrome`, the budget the core allocator runs on.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;
using FgMotion = FluentGpu.Dsl.Motion;

namespace Wavee.Themes.Classic;

/// <summary>The constants of Classic's title bar row and search.</summary>
internal static class ClassicChromeLayout
{
    // ── the title bar row ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The island-button footprint's width: the bar's own nav metric (a 32-DIP button would leave strips that are neither
    /// draggable nor clickable).</summary>
    public const float ButtonSize = 40f;

    /// <summary>The island-button footprint's height.</summary>
    public const float ButtonHeight = 44f;

    /// <summary>2 DIP either side: what the bar gives its own pane toggle.</summary>
    public static readonly Edges4 ButtonMargin = new(2f, 0f, 2f, 0f);

    /// <summary>The Compact row's spacers (board C-TitleBar, Compact: ≡ and Back side by side, a flexible gap, the search and the count against the captions): no lead
    /// pad after the pane toggle and a 16-DIP drag strip, so the four 44-DIP buttons and the caption cluster fit a narrow window. Every other tier keeps the engine's
    /// WinUI 14 and 48. `ClassicPolicies.Chrome.Compact` budgets the same two numbers, so the allocation and the bar agree.</summary>
    public static readonly TitleBarSpacing CompactSpacing = new(LeadPad: 0f, MinDragStrip: 16f);   // = the core's ChromePolicy.Default.Compact (the shell baseline)

    /// <summary>The row's spacers in window tier <paramref name="window"/>.</summary>
    public static TitleBarSpacing SpacingFor(WidthTier window) => window == WidthTier.Compact ? CompactSpacing : TitleBarSpacing.Default;

    /// <summary>The "Connecting" caption chip's height.</summary>
    public const float ChipHeight = 32f;

    /// <summary>The "Connecting" caption chip's padding.</summary>
    public static readonly Edges4 ConnectingChipPadding = new(8f, 0f, 8f, 0f);

    /// <summary>The Compact title bar's tab count (board C-TitleBar): an 18-DIP outlined box at the 4 radius with a 1.2 stroke, 3 DIP of
    /// padding either side of an 11 / 600 count.</summary>
    public const float TabCountBox = 18f, TabCountRadius = 4f, TabCountStroke = 1.2f, TabCountTextSize = 11f;

    /// <inheritdoc cref="TabCountBox"/>
    public const ushort TabCountWeight = 600;

    /// <inheritdoc cref="TabCountBox"/>
    public static readonly Edges4 TabCountPadding = new(3f, 0f, 3f, 0f);

    /// <summary>The tab titles' text size (text-first tabs: weight and opacity carry selection).</summary>
    public const float TabTextSize = 13f;

    /// <summary>The tabs lane's reservation moving (a tab opened, closed, pinned or moved, a title growing): one transition on
    /// <c>chrome-tabs-lane</c>, and each tab eases its own width on the same spring (<see cref="TabWidthMotion"/>). A REFLOW of the width, so the centred search beside
    /// it rides the lane through real layout, on a critically damped spring: the reservation follows the strip's MEASURED extent, which
    /// moves a frame at a time while a tab eases its width, and a tween restarted on every such retarget held a frame each time and
    /// stepped the lane 10 DIP at a go while the search jumped beside it (audit FU2 item 8). The spring keeps its velocity across a
    /// retarget, so the run is one glide.</summary>
    public static readonly LayoutTransition TabLaneMotion = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Spring(TabLaneResponse, TabLaneDamping), Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>The lane spring: about the old 167 ms tween's settle, and critically damped (a reservation never overshoots its tabs).</summary>
    const float TabLaneResponse = 0.18f, TabLaneDamping = 1f;

    /// <summary>A tab's own width change inside the lane (a title landing or changing length, the tier's cap moving): the lane's spring,
    /// through real layout, so the tabs after it and the underline follow and the tab grows with the lane's clip instead of stepping
    /// (the strip's own decelerate tween put 14 of a title's 24 DIP into one frame on a playlist's landing, audit FU2c). Unclipped: the
    /// selected plate overhangs its cell.</summary>
    public static readonly LayoutTransition TabWidthMotion = new(TransitionChannels.Size,
        TransitionDynamics.Spring(TabLaneResponse, TabLaneDamping), Size: SizeMode.Reflow, Axes: SizeAxes.Width, ReflowUnclipped: true);

    // ── the search field ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The field's height: a 32-DIP field at the control corner radius with the control-default chrome.</summary>
    public const float FieldMinHeight = 32f;

    /// <summary>The field's width motion between its rest and its focused width (tuned F6): a REFLOW on the width axis, so the box
    /// and its text move together and the drag bands either side are pushed through real layout. Standard / FluentStandard; the
    /// call site passes null under reduced motion and when the rest width moves without a focus change.</summary>
    public static readonly LayoutTransition FieldWidthMotion = new(TransitionChannels.Size | TransitionChannels.Position,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.FluentStandard), Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>The field's width when it SNAPS (a rest-width change without a focus flip or a tier crossing: a resize step, a tab added): the
    /// same reflow at no duration, so the box keeps a transition the engine measures it for and the next eased change has its "before".</summary>
    public static readonly LayoutTransition FieldWidthSnap = FieldWidthMotion with { Dynamics = TransitionDynamics.Tween(0f, Easing.Linear) };

    // ── the suggestions popup ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The popup's width before the anchor reports one.</summary>
    public const float FallbackPopupWidth = 720f;

    /// <summary>The popup root's padding.</summary>
    public static readonly Edges4 PopupPadding = new(0f, 2f, 0f, 2f);

    /// <summary>The progress bar's slot: always the first child, it fades and never mounts or unmounts.</summary>
    public const float ProgressSlotHeight = 3f;

    /// <summary>The rows' scroll content is 1 DIP wider either side than the viewport (the popup chrome's stroke).</summary>
    public static readonly Edges4 RowListInset = new(-1f, 0f, -1f, 0f);

    /// <summary>A query row's and a rich row's margin.</summary>
    public static readonly Edges4 RowMargin = new(4f, 2f, 4f, 2f);

    /// <summary>The divider between the query rows and the rich rows.</summary>
    public static readonly Edges4 DividerMargin = new(16f, 4f, 16f, 4f);

    /// <summary>A query row's padding.</summary>
    public static readonly Edges4 QueryRowPadding = new(12f, 0f, 8f, 0f);

    /// <summary>A query row's text size (and the notice's).</summary>
    public const float TextSize = 14f;

    /// <summary>A query row's search glyph.</summary>
    public const float QueryGlyphSize = 16f;

    /// <summary>The gap after the search glyph.</summary>
    public static readonly Edges4 QueryGlyphMargin = new(0f, 0f, 12f, 0f);

    /// <summary>The weight of the typed run in a query row.</summary>
    public const ushort MatchWeight = 700;

    /// <summary>The weight of the rest of a query row.</summary>
    public const ushort RestWeight = 400;

    /// <summary>A notice's padding: 24 either side, 12 on the right when it carries a trailing action (Retry).</summary>
    public static Edges4 NoticePadding(bool hasTrailing) => new(24f, 0f, hasTrailing ? 12f : 24f, 0f);

    // ── the pending skeleton ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The skeleton notice's padding.</summary>
    public static readonly Edges4 SkeletonNoticePadding = new(24f, 0f, 24f, 0f);

    /// <summary>The skeleton notice's bar width.</summary>
    public const float SkeletonNoticeBarW = 160f;

    /// <summary>The skeleton bars' height.</summary>
    public const float SkeletonBarH = 11f;

    /// <summary>A skeleton row's padding.</summary>
    public static readonly Edges4 SkeletonRowPadding = new(16f, 0f, 16f, 0f);

    /// <summary>A skeleton query row's bar: this wide, then 40 wider for every step of the three-step period.</summary>
    public const float SkeletonQueryBarW = 120f;

    /// <summary>The step of the query bars' widths.</summary>
    public const float SkeletonQueryBarStep = 40f;

    /// <summary>A skeleton rich row's art square.</summary>
    public const float SkeletonArt = 44f;

    /// <summary>The gap between a skeleton rich row's art and its text.</summary>
    public const float SkeletonRichGap = 12f;

    /// <summary>The gap between a skeleton rich row's two bars.</summary>
    public const float SkeletonTextGap = 6f;

    /// <summary>A skeleton rich row's title bar: this wide, then 30 wider for every step of the period.</summary>
    public const float SkeletonRichBarW = 140f;

    /// <summary>The step of the rich bars' widths.</summary>
    public const float SkeletonRichBarStep = 30f;

    /// <summary>A skeleton rich row's detail bar.</summary>
    public const float SkeletonSubBarW = 90f;

    /// <summary>A skeleton rich row's detail bar's height.</summary>
    public const float SkeletonSubBarH = 9f;

    /// <summary>The bars' widths repeat every three rows.</summary>
    public const int SkeletonPeriod = 3;

    // ── the popup root's height motion ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The popup root's height motion (Reflow on the height axis): rows arriving or leaving ease the card, and the top edge
    /// never moves. Standard / FluentDecelerate; the call site passes null under reduced motion.</summary>
    public static readonly LayoutTransition BodyHeightMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(Design.Motion.Standard, Easing.FluentDecelerate),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);
}
