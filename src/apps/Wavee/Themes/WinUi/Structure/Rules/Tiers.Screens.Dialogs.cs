// ── Themes/WinUi/Structure/Rules/Tiers.Dialogs.cs — the report and crash dialogs' bodies per window tier (themes plan 3.6, TC8) ──────────────
//
// The two FORM dialogs, the report-a-problem form (`Dialog.Feedback`) and the crash consent prompt (`Dialog.Crash`), each in the four bands of the boards
// `C-Feedback` / `C-Crash` (-Compact, -Small, -Huge, and the tuned Standard design). ONE record per window tier, read by both bodies through
// `WinUiScreenDialogTiers.Of(ctx.Tier())`; a body never compares a width (WVTH013), it asks the record how its preview sits and how tall its targets are, and
// asks `BodyWidth` how wide the card leaves it.
//
//   Compact   the card fills the window less 16 (`ClassicOverlayTiers.Compact`: padding 16 / 20, the command row the engine's 40-DIP buttons in equal columns, the
//             band's `DialogFit.CommandSize`); the report preview folds behind a 40-DIP "Report preview" row; fields, check boxes, links and the kind switch are 40 tall.
//   Small     the tuned 548 card (board C-Feedback-Small / C-Crash-Small: 548 wide, the body scrolls, the command row 80 high with two equal buttons).
//   Standard  the same card, today's design (C-Feedback / C-Crash).
//   Huge      a two-pane card, 1040 wide (`OverlayFit.ShowForm` + `DialogFit.FormWidth`): the form on the left, the live report on the right, always shown.
//
// THE BODY WIDTH is the card's text region (`DialogFit.FormBodyWidth`): the card the tier's fit gives the window, less the text region's padding. A body reads it
// ONCE, at its mount, and keeps it: the card is sized when the dialog opens and does not follow a resize (as no TC9 dialog does), so a body that re-measured would
// outgrow its own card. The shape the body mounts with (the record AND the width) is therefore the dialog's, frozen at open.
//
// Public so `Wavee.Tests` pins the records.

using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>How the report preview sits in a form dialog.</summary>
public enum WinUiReportPreview : byte
{
    /// <summary>Under the form, always shown (Small, Standard).</summary>
    Inline,

    /// <summary>Behind a 40-DIP row that expands it (Compact).</summary>
    Collapsed,

    /// <summary>In a second pane beside the form, always shown (Huge).</summary>
    Pane,
}

/// <summary>What the report and crash dialogs' bodies lay out in one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Preview">Where the report preview sits.</param>
/// <param name="Target">The hit target the controls are lifted to (0 keeps the stock controls; Compact 40).</param>
/// <param name="NominalBodyWidth">The body's own width where the card has room (the stock 548 card less its 24 padding).</param>
/// <param name="TextFieldHeight">A single-line field's height (the stock 32; Compact 40).</param>
/// <param name="PreviewHeight">The preview box's height.</param>
/// <param name="FormPaneWidth">The form pane's width in a two-pane body (0 elsewhere).</param>
public sealed record WinUiScreenDialogLayout(WidthTier Tier, WinUiReportPreview Preview, float Target, float NominalBodyWidth,
    float TextFieldHeight, float PreviewHeight, float FormPaneWidth) : ITierLayoutRecord
{
    /// <summary>A second pane carries the preview.</summary>
    public bool TwoPane => Preview == WinUiReportPreview.Pane;

    /// <summary>The preview folds behind its row.</summary>
    public bool PreviewFolds => Preview == WinUiReportPreview.Collapsed;

    /// <summary>The body lifts its controls to <see cref="Target"/>.</summary>
    public bool Touch => Target > 0f;

    /// <summary>The regions a resized frame may clip: none (a dialog's card is sized when it opens and clips its own body).</summary>
    public System.Collections.Immutable.ImmutableArray<TierRegion> Regions => System.Collections.Immutable.ImmutableArray<TierRegion>.Empty;
}

/// <summary>What a form dialog's body mounted with: the tier's record and the width the card left it, both frozen at open.</summary>
/// <param name="Layout">The record of the window tier the dialog opened in.</param>
/// <param name="BodyWidth">The card's text region at open.</param>
public sealed record WinUiScreenDialogShape(WinUiScreenDialogLayout Layout, float BodyWidth)
{
    /// <summary>The form's column width: the form pane in a two-pane body, else the whole body.</summary>
    public float FormWidth => WinUiScreenDialogTiers.FormWidth(Layout, BodyWidth);

    /// <summary>The preview pane's width in a two-pane body.</summary>
    public float PreviewPaneWidth => WinUiScreenDialogTiers.PreviewPaneWidth(Layout, BodyWidth);
}

/// <summary>The report and crash dialogs' tier records and the arithmetic over them.</summary>
public static class WinUiScreenDialogTiers
{
    /// <summary>The Compact hit target (the brief's floor).</summary>
    public const float CompactTarget = 40f;

    /// <summary>The Huge two-pane card (<c>C-Feedback-Huge</c>, <c>C-Crash-Huge</c>: 1040 wide).</summary>
    public const float HugeCardWidth = 1040f;

    /// <summary>The stock card's body width: 548 less its 24 padding either side.</summary>
    public const float StockBodyWidth = 548f - 48f;

    /// <summary>The gap either side of the divider in a two-pane body, and the divider's own width.</summary>
    public const float PaneGap = 24f, DividerWidth = 1f;

    /// <summary>The kind switch's items at Compact are at least this wide before they wrap to a second row (the board's <c>flex: 1 1 64px</c>).</summary>
    public const float KindMinWidth = 64f;

    /// <summary>The kind switch's gap between its items at Compact, and its padding.</summary>
    public const float KindGap = 2f, KindPad = 2f;

    /// <summary>The Compact preview row's padding either side and its gap.</summary>
    public const float FoldPadX = 12f, FoldGap = 8f;

    private static readonly WinUiScreenDialogLayout[] s_layouts =
    [
        // Compact (C-Feedback-Compact, C-Crash-Compact): the card fills the window less 16; a 40 field, the preview behind its 40 row.
        new(WidthTier.Compact, WinUiReportPreview.Collapsed, Target: CompactTarget, NominalBodyWidth: StockBodyWidth, TextFieldHeight: CompactTarget,
            PreviewHeight: 220f, FormPaneWidth: 0f),
        // Small (C-Feedback-Small, C-Crash-Small) and Standard (C-Feedback, C-Crash): the tuned 548 card.
        new(WidthTier.Small, WinUiReportPreview.Inline, Target: 0f, NominalBodyWidth: StockBodyWidth, TextFieldHeight: 32f, PreviewHeight: 220f, FormPaneWidth: 0f),
        new(WidthTier.Standard, WinUiReportPreview.Inline, Target: 0f, NominalBodyWidth: StockBodyWidth, TextFieldHeight: 32f, PreviewHeight: 220f, FormPaneWidth: 0f),
        // Huge (C-Feedback-Huge, C-Crash-Huge): the form pane (the board's 520 less its 24 padding either side) and the preview pane beside it.
        new(WidthTier.Huge, WinUiReportPreview.Pane, Target: 0f, NominalBodyWidth: HugeCardWidth - 48f, TextFieldHeight: 32f, PreviewHeight: 380f, FormPaneWidth: 472f),
    ];

    /// <summary>The record of <paramref name="window"/> (Huge 4K reads Huge's).</summary>
    public static WinUiScreenDialogLayout For(WidthTier window) => s_layouts[Math.Min((int)window, (int)WidthTier.Huge)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static WinUiScreenDialogLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The body's width in a window <paramref name="viewportWidth"/> wide: the text region of the card the tier's overlay fit gives it.</summary>
    public static float BodyWidth(WinUiScreenDialogLayout layout, float viewportWidth)
        => OverlayFit.Layout.Dialog.FormBodyWidth(layout.NominalBodyWidth, viewportWidth);

    /// <summary>The shape a body mounts with: the record for <paramref name="facts"/> and the body width in a window <paramref name="viewportWidth"/> wide (the live layout
    /// viewport; the facts' own where none is known; the nominal width where neither is).</summary>
    public static WinUiScreenDialogShape Open(in TierFacts facts, float viewportWidth)
    {
        var layout = Of(facts);
        float window = viewportWidth > 0f ? viewportWidth : facts.ViewportWidth;
        return new WinUiScreenDialogShape(layout, window > 0f ? BodyWidth(layout, window) : layout.NominalBodyWidth);
    }

    /// <summary>A two-pane body's preview pane width: what the form pane, the two gaps and the divider leave of <paramref name="bodyWidth"/>.</summary>
    public static float PreviewPaneWidth(WinUiScreenDialogLayout layout, float bodyWidth)
        => MathF.Max(1f, bodyWidth - layout.FormPaneWidth - 2f * PaneGap - DividerWidth);

    /// <summary>The form's column width: the form pane in a two-pane body, else the whole body.</summary>
    public static float FormWidth(WinUiScreenDialogLayout layout, float bodyWidth) => layout.TwoPane ? MathF.Min(layout.FormPaneWidth, bodyWidth) : bodyWidth;
}
