// ── Theming/Policies/OverlayPolicy.cs — what a structure decides about the overlays the core composes (themes plan 3.2, 5.5; TC9) ──────
//
// The core builds some overlays out of the engine's stock controls (the ContentDialog's card, the toast lane, the vacancy, the standalone selection
// bar) and opens the context menu. How those SIZE and WHERE they sit per window tier is the structure's: it publishes one `OverlayLayout` per tier
// band here, and the core hosts read the band they are in (`ThemeHost.Facts.Tiers.Window`), never a width.
//
// A structure keys its layouts on Compact, the bands between and Huge. The three records are what the structure's own `Rules\Tiers.Overlays.cs`
// builds; a structure that declares nothing is the shell as it shipped (`Default`: every band `Stock`).
//
// The numbers are DATA of the structure (WVTH003 allows literals only in `Rules\**`); this file holds shapes and the pure arithmetic over them.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>How the engine's ContentDialog card fits the window.</summary>
/// <param name="Fill">The card fills the window less <paramref name="Inset"/> on each side (the Compact boards), instead of its opener's rung.</param>
/// <param name="Inset">The window margin the card always keeps on each side.</param>
/// <param name="MaxWidth">The widest the card ever is (the engine's own rung 4).</param>
/// <param name="EdgeY">The window margin the card keeps above and below: a card taller than the window less this scrolls inside itself.</param>
/// <param name="ContentPadding">The card's text region padding (title, message, body); null keeps the engine's 24.</param>
/// <param name="CommandPadding">The command row's padding; null keeps the engine's 24.</param>
/// <param name="FormWidth">The card width a FORM dialog (the report and the crash prompt: <c>OverlayFit.ShowForm</c>) takes where the band has the room for a second pane,
/// independent of <paramref name="MaxWidth"/> (which still caps every other dialog); null keeps the opener's rung.</param>
/// <param name="CommandSize">The command buttons' size on the engine's size axis (<c>ContentDialog.CommandSize</c>): Medium keeps the engine's 32-DIP buttons,
/// Large is the 40-DIP target (Compact).</param>
/// <param name="CommandMinWidth">Each command button's least width (<c>ContentDialog.CommandMinWidth</c>; the engine's 130 by default): a narrow band lowers it so two
/// 40-tall buttons still sit side by side in its card. <see cref="StacksCommands"/> reads it.</param>
/// <param name="UniformCommands">The command buttons share the row equally (<c>ContentDialog.UniformCommandWidths</c>: the Compact boards' two-column grid) instead of
/// each growing from its own label.</param>
public sealed record DialogFit(bool Fill, float Inset, float MaxWidth, float EdgeY, Edges4? ContentPadding = null, Edges4? CommandPadding = null,
    float? FormWidth = null, ControlSize CommandSize = ControlSize.Medium, float CommandMinWidth = DialogFit.EngineButtonMinWidth, bool UniformCommands = false)
{
    /// <summary>The engine's card with the window caps only: the opener's rung, never wider than the window less the inset.</summary>
    public static DialogFit Stock { get; } = new(Fill: false, Inset: 16f, MaxWidth: 548f, EdgeY: 24f);

    /// <summary>The card's width for <paramref name="requested"/> (the opener's rung) in a window <paramref name="viewportWidth"/> wide.</summary>
    public float WidthFor(float requested, float viewportWidth)
    {
        float room = MathF.Max(1f, viewportWidth - 2f * Inset);
        float want = Fill ? room : requested;
        return MathF.Min(MathF.Min(want, MaxWidth), room);
    }

    /// <summary>The card's width for a FORM dialog: <see cref="FormWidth"/> (never wider than the window less the inset) where the band names one, else
    /// <see cref="WidthFor"/> of <paramref name="requested"/>.</summary>
    public float FormCardWidthFor(float requested, float viewportWidth)
        => FormWidth is { } form ? MathF.Min(form, MathF.Max(1f, viewportWidth - 2f * Inset)) : WidthFor(requested, viewportWidth);

    /// <summary>The widest a FORM dialog's body may be: the whole text region of its card where the band names a <see cref="FormWidth"/>, else <see cref="BodyWidth"/>.</summary>
    public float FormBodyWidth(float nominal, float viewportWidth)
        => FormWidth is { } form
            ? MathF.Max(1f, MathF.Min(form, MathF.Max(1f, viewportWidth - 2f * Inset)) - (ContentPadding?.Horizontal ?? 2f * EnginePadding))
            : BodyWidth(nominal, viewportWidth);

    /// <summary>The card's tallest height in a window <paramref name="viewportHeight"/> tall.</summary>
    public float MaxHeightFor(float viewportHeight) => MathF.Max(1f, viewportHeight - 2f * EdgeY);

    /// <summary>The engine's own padding either side of a dialog's body (<c>ContentDialog.Pad</c>), when the structure sets none.</summary>
    public const float EnginePadding = 24f;

    /// <summary>The engine's own chrome around a dialog's body (the title over two lines, its gap, the paddings, the separator and the command row); the engine
    /// bounds its own body scroller by its tallest height less 200.</summary>
    public const float ChromeHeight = 200f;

    /// <summary>The engine's command buttons are at least this wide by default (<c>ContentDialog.CommandMinWidth</c>) and sit this far apart (<c>BtnGap</c>).</summary>
    public const float EngineButtonMinWidth = 130f, EngineButtonGap = 8f;

    /// <summary>The command row stacks its buttons in a column when <paramref name="buttons"/> of them, each at least <see cref="CommandMinWidth"/> wide, cannot sit side
    /// by side inside a card <paramref name="cardWidth"/> wide (a 300-DIP window's 268 holds two 130-wide buttons only with no padding at all).</summary>
    public bool StacksCommands(float cardWidth, int buttons)
    {
        if (buttons < 2) return false;
        float inner = cardWidth - (CommandPadding?.Horizontal ?? 2f * EnginePadding);
        return inner < buttons * CommandMinWidth + (buttons - 1) * EngineButtonGap;
    }

    /// <summary>The widest a dialog's body may be in a window <paramref name="viewportWidth"/> wide: the card's width less the text region's padding. A body
    /// built for <paramref name="nominal"/> keeps it where the card keeps its own width, and fills the card where it fills.</summary>
    public float BodyWidth(float nominal, float viewportWidth)
    {
        float card = MathF.Min(MaxWidth, MathF.Max(1f, viewportWidth - 2f * Inset));
        float room = MathF.Max(1f, card - (ContentPadding?.Horizontal ?? 2f * EnginePadding));
        return Fill ? room : MathF.Min(nominal, room);
    }

    /// <summary>The tallest a dialog's scrolling body may be in a window <paramref name="viewportHeight"/> tall: the card's tallest height less its chrome (never
    /// under a few rows), so a body taller than the window scrolls inside the card instead of pushing the command row out of it.</summary>
    public float BodyMaxHeight(float nominal, float viewportHeight)
        => MathF.Min(nominal, MathF.Max(96f, MaxHeightFor(viewportHeight) - ChromeHeight));
}

/// <summary>Where the engine's toast lane docks, how far above the docked player bar it floats, and its geometry.</summary>
/// <param name="Placement">The lane's corner or edge.</param>
/// <param name="GapAboveBar">The gap between the lane and the player bar's top.</param>
/// <param name="BarHeight">The height of the bar the lane clears at this band when it is not the policy's own (Compact's mini bar), else null.</param>
/// <param name="Lane">The lane's side margin, card widths and action size (the engine's <c>ToastLaneStyle</c>, provided as <c>Toast.Lane</c>); null keeps the engine's
/// (24 in from each side, 300-380 cards, a 32-DIP action).</param>
public sealed record ToastDock(ToastPlacement Placement, float GapAboveBar, float? BarHeight = null, ToastLaneStyle? Lane = null)
{
    /// <summary>The engine's own dock margin from the screen edge (`ToastController.BuildLane`): the lane always sits this far in, then
    /// <c>Toast.EdgeInset</c> more.</summary>
    public const float EngineEdge = 24f;

    /// <summary>The shell as it shipped: bottom right, 24 above the bar.</summary>
    public static ToastDock Stock { get; } = new(ToastPlacement.BottomRight, EngineEdge);

    /// <summary>The <c>Toast.EdgeInset</c> that puts the lane <see cref="GapAboveBar"/> above the bar: <see cref="BarHeight"/> when this band names one,
    /// else <paramref name="policyBarHeight"/> (the player bar policy's).</summary>
    public float EdgeInsetFor(float policyBarHeight) => MathF.Max(0f, (BarHeight ?? policyBarHeight) + GapAboveBar - EngineEdge);
}

/// <summary>How the shared vacancy (empty, error, offline, no match) is set at a tier.</summary>
/// <param name="QuietTitle">The headline uses the 20/28 rung at page scale too (the Compact boards), instead of the 28/36 page hero.</param>
/// <param name="Padding">The vacancy's padding on every side; null keeps the spacing scale's own (XXL).</param>
/// <param name="ActionHeight">The action button's least height (Compact: 40); 0 keeps the control's own.</param>
public sealed record VacancyFit(bool QuietTitle, float? Padding = null, float ActionHeight = 0f)
{
    /// <summary>The shell as it shipped: the page hero, the scale's XXL around.</summary>
    public static VacancyFit Stock { get; } = new(QuietTitle: false);
}

/// <summary>How the standalone selection bar (the floating card over a list) sits at a tier.</summary>
/// <param name="MinHeight">The card's least height (its commands are at least 40 tall at Compact).</param>
/// <param name="SideInset">The window margin on each side; null keeps the spacing scale's own (L).</param>
/// <param name="Padding">The card's padding.</param>
public sealed record SelectionFit(float MinHeight, float? SideInset, Edges4 Padding)
{
    /// <summary>The shell as it shipped: no least height, the scale's L either side, 8 over 6 around.</summary>
    public static SelectionFit Stock { get; } = new(MinHeight: 0f, SideInset: null, Padding: new Edges4(8f, 6f, 8f, 6f));
}

/// <summary>Everything the structure decides about the core's overlays at ONE width band.</summary>
/// <param name="Dialog">The ContentDialog card.</param>
/// <param name="Toast">The toast lane's dock.</param>
/// <param name="Vacancy">The vacancy's type and padding.</param>
/// <param name="Selection">The standalone selection bar.</param>
/// <param name="ContextSheet">Context menus open as a bottom sheet (<c>Overlay.ContextSheet</c>) instead of an anchored menu.</param>
/// <param name="MenuSize">The anchored menus' rows on the engine's size axis (<c>MenuFlyout.Create(…, size:)</c>): Medium keeps the engine's 36-DIP rows, Large is the
/// 40-DIP target (Compact).</param>
public sealed record OverlayLayout(DialogFit Dialog, ToastDock Toast, VacancyFit Vacancy, SelectionFit Selection, bool ContextSheet,
    ControlSize MenuSize = ControlSize.Medium)
{
    /// <summary>The shell as it shipped: the engine's card, the toast lane bottom right, the page-hero vacancy, anchored menus.</summary>
    public static OverlayLayout Stock { get; } = new(DialogFit.Stock, ToastDock.Stock, VacancyFit.Stock, SelectionFit.Stock, ContextSheet: false);

    /// <summary>Everything wrong with the layout, empty when every core host can run on it.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Dialog is null) problems.Add("Dialog is missing");
        else if (Dialog.Inset < 0f || Dialog.EdgeY < 0f || Dialog.MaxWidth <= 0f) problems.Add("the dialog's inset, edge and width cap must be positive");
        else if (!float.IsFinite(Dialog.CommandMinWidth) || Dialog.CommandMinWidth < 0f) problems.Add("the dialog's command min width must be zero or more");
        if (Toast is null) problems.Add("Toast is missing");
        if (Vacancy is null) problems.Add("Vacancy is missing");
        if (Selection is null) problems.Add("Selection is missing");
        return problems;
    }
}

/// <summary>What the active structure decides about the overlays the core composes, per window band. Keyed on Compact, the bands between
/// (Small and Standard) and Huge (Huge 4K included), so a ladder that moves an edge never moves a layout.</summary>
/// <param name="Compact">Compact.</param>
/// <param name="Standard">Small and Standard.</param>
/// <param name="Huge">Huge and Huge 4K.</param>
public sealed record OverlayPolicy(OverlayLayout Compact, OverlayLayout Standard, OverlayLayout Huge)
{
    /// <summary>The shell as it shipped: every band `Stock`.</summary>
    public static OverlayPolicy Default { get; } = new(OverlayLayout.Stock, OverlayLayout.Stock, OverlayLayout.Stock);

    /// <summary>The layout of <paramref name="tier"/>.</summary>
    public OverlayLayout For(WidthTier tier) => tier == WidthTier.Compact ? Compact : tier >= WidthTier.Huge ? Huge : Standard;

    /// <summary>Everything wrong with the policy, empty when every core host can run on it.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Compact is null) problems.Add("Compact is missing"); else problems.AddRange(Compact.Validate());
        if (Standard is null) problems.Add("Standard is missing"); else problems.AddRange(Standard.Validate());
        if (Huge is null) problems.Add("Huge is missing"); else problems.AddRange(Huge.Validate());
        return problems;
    }
}
