// ── Themes/WinUi/Structure/Rules/Tiers.Overlays.cs — WinUI's overlays, dialogs, toasts and states per window tier (themes plan 3.6, P6 W8a) ──
//
// ONE record per window band, read by the views through `WinUiOverlayTiers.For(ctx.Tier().Window)` and by the core hosts through the policy
// (`ThemePolicies.Overlays`, built here from the same records). The tier rule is W-Tiers' text: "ContentDialogs go full-screen under 641 and
// cap at 588 / 760 elsewhere" (588 the card, 760 the report form; Huge's report form is the two-pane 1040 of W8b's body). Compact is the touch
// tier (every target 40); the menus, flyouts, toasts and the InfoBar are the engine's WinUI-look controls, sized here only where a band moves them.
//
// KEYED ON COMPACT. `For` maps Compact, Small, Huge (and Huge 4K) and everything between is the standard record, so the rules are right under
// the ladder's edges (641 / 1008 / 1920) without naming one.
//
// The arithmetic (a card's width in a window, a panel's cap) lives here too, so a view never compares or computes with a measured width (WVTH013):
// it passes the live viewport and takes the answer.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>What one width band decides for WinUI's overlays. Public, not internal: this assembly has no <c>InternalsVisibleTo</c>, and the tests pin the records.</summary>
public sealed record WinUiOverlayTier
{
    /// <summary>What the core hosts read (the dialog card's fit, the toast dock, the vacancy, the selection bar, context sheets).</summary>
    public required OverlayLayout Core { get; init; }

    // ── the bare modal cards (the log-out and play-a-link cards) ──────────────────────────────────────────────────

    /// <summary>The card fills the window less its margin (Compact: full screen, margin 0), instead of its own width.</summary>
    public required bool CardFills { get; init; }

    /// <summary>The window margin the card keeps on each side.</summary>
    public required float CardMargin { get; init; }

    /// <summary>The card's corner radius (0 at Compact, where the card is the whole window; r8 elsewhere).</summary>
    public required float CardCorner { get; init; }

    /// <summary>The card's padding.</summary>
    public required Edges4 CardPadding { get; init; }

    /// <summary>The gap between the card's parts; null keeps the spacing scale's M (read where the element is built, so a theme change reaches it).</summary>
    public required float? CardGap { get; init; }

    /// <summary>The card's buttons are this tall and share the row equally (Compact: a 40 grid). 0 keeps the control's own height and its right-aligned row.</summary>
    public required float CardButtonHeight { get; init; }

    // ── the account chip ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The chip is at least this tall and wide (Compact: a 40 touch target around the 24 avatar); 0 keeps the chip's own 32 height and hugging width.</summary>
    public float ChipTarget { get; init; }

    // ── flyout panels (the notification panel, the account menu, the runtime banner) ──────────────────────────────

    /// <summary>The window margin a panel keeps on each side: it is never wider than the window less this.</summary>
    public required float PanelMargin { get; init; }

    // ── the offline strip and vacancy ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The strip stacks its title over the notice (Compact, where the row has no room for both on one line).</summary>
    public required bool StripStacked { get; init; }

    /// <summary>The strip's Retry height (0 keeps the control's own).</summary>
    public required float StripRetryHeight { get; init; }

    /// <summary>The vacancy's headline is the 20/28 rung and its caption the 12/16 line (Compact), instead of the page hero.</summary>
    public required bool VacancyQuietTitle { get; init; }

    /// <summary>The vacancy's side padding.</summary>
    public required float VacancyPadX { get; init; }

    /// <summary>Retry and the library link are at least this tall (Compact: 40); 0 keeps the controls' own.</summary>
    public required float VacancyActionHeight { get; init; }

    /// <summary>The column sits above the page's centre by this much (the board's 120 bottom padding), at a Regular window height.</summary>
    public required float VacancyLift { get; init; }

    /// <summary>The vacancy's actions stack (a column) instead of sitting in one row.</summary>
    public required bool VacancyActionsStack { get; init; }
}

/// <summary>WinUI's overlay tier records (public so <c>Wavee.Tests</c> can pin them).</summary>
public static class WinUiOverlayTiers
{
    private static readonly OverlayLayout s_compactCore = new(
        // W-Tiers: ContentDialogs go full-screen under 641 (W-Feedback-Compact: the plate is the window below the 48 chrome row, square, padded 20, the
        // title Display 28, 40-tall commands in equal columns). The card keeps no margin; EdgeY 0 lets it take the whole height.
        Dialog: new DialogFit(Fill: true, Inset: 0f, MaxWidth: WinUiDialogRules.FullScreenCap, EdgeY: 0f,
            ContentPadding: new Edges4(20f, 20f, 20f, 12f), CommandPadding: new Edges4(20f, 12f, 20f, 20f),
            CommandSize: ControlSize.Large, CommandMinWidth: 96f, UniformCommands: true, TitleSize: 28f, PlateRadius: 0f),
        // W-Notifications on a Compact window: the card is the window less 8 either side, its action a 40 target, above the Mini bar.
        Toast: new ToastDock(ToastPlacement.BottomCenter, GapAboveBar: 8f, BarHeight: WinUiTiers.MiniBarH,
            Lane: new ToastLaneStyle { SideMargin = 8f, FillWidth = true, ActionSize = ControlSize.Large }),
        Vacancy: new VacancyFit(QuietTitle: true, Padding: 16f, ActionHeight: 40f),
        Selection: new SelectionFit(MinHeight: 48f, SideInset: 8f, Padding: new Edges4(4f, 4f, 4f, 4f)),
        ContextSheet: true, MenuSize: ControlSize.Large);   // the anchored menus (sort, filter, the "..." menus) at 40-DIP rows

    /// <summary>Compact (below 641): full-screen dialogs, 40-tall command targets, the context sheet, the quiet vacancy.</summary>
    public static WinUiOverlayTier Compact { get; } = new()
    {
        Core = s_compactCore,
        CardFills = true, CardMargin = 0f, CardCorner = 0f, CardPadding = new Edges4(20f, 20f, 20f, 16f), CardGap = 12f, CardButtonHeight = 40f,
        PanelMargin = 8f, ChipTarget = 40f,
        StripStacked = true, StripRetryHeight = 40f,
        VacancyQuietTitle = true, VacancyPadX = 16f, VacancyActionHeight = 40f, VacancyActionsStack = true, VacancyLift = WinUiOverlayRules.OfflineVacancyLift,
    };

    /// <summary>The dialog card of the bands that keep a width: capped at 588, the window less 16 at the narrowest.</summary>
    private static DialogFit Dialogs(float? report = null, float? form = null)
        => new(Fill: false, Inset: WinUiDialogRules.WindowInset, MaxWidth: WinUiDialogRules.CardCap, EdgeY: WinUiDialogRules.EdgeY,
            FormWidth: form, ReportWidth: report);

    private static WinUiOverlayTier Banded(DialogFit dialog) => new()
    {
        Core = new OverlayLayout(dialog, ToastDock.Stock, VacancyFit.Stock, SelectionFit.Stock, ContextSheet: false),
        CardFills = false, CardMargin = WinUiDialogRules.WindowInset, CardCorner = WinUiDialogRules.CardRadius, CardPadding = Edges4.All(WinUiDialogRules.CardPadding),
        CardGap = null, CardButtonHeight = 0f,
        PanelMargin = 16f,
        StripStacked = false, StripRetryHeight = 0f,
        VacancyQuietTitle = false, VacancyPadX = 0f, VacancyActionHeight = 0f, VacancyActionsStack = false, VacancyLift = WinUiOverlayRules.OfflineVacancyLift,
    };

    /// <summary>Small: the 588 card; the report form is 588 as well (W-Feedback-Small).</summary>
    public static WinUiOverlayTier Small { get; } = Banded(Dialogs(report: WinUiDialogRules.CardCap));

    /// <summary>Standard: the 588 card; the report form is 760 (W-Feedback).</summary>
    public static WinUiOverlayTier Standard { get; } = Banded(Dialogs(report: WinUiDialogRules.ReportWidth));

    /// <summary>Huge and Huge 4K: the standard design; the two FORM dialogs are the two-pane 1040 card (W8b's body: the form, the live report beside it).</summary>
    public static WinUiOverlayTier Huge { get; } = Banded(Dialogs(form: WinUiDialogRules.HugeFormWidth));

    /// <summary>The record of <paramref name="tier"/>: Compact, Small, Huge (and Huge 4K), and the standard record between.</summary>
    public static WinUiOverlayTier For(WidthTier tier)
        => tier == WidthTier.Compact ? Compact : tier == WidthTier.Small ? Small : tier >= WidthTier.Huge ? Huge : Standard;

    /// <summary>The policy the structure publishes for the core hosts.</summary>
    public static OverlayPolicy Policy { get; } = new(Compact.Core, Standard.Core, Huge.Core) { Small = Small.Core };

    // ── what a Short window gives up (a height rule: plan 3.2, 330 tall leaves the page 210) ───────────────────

    /// <summary>The vacancy's lift: the board's 120 at a Regular height, none in a Short window (the page is 210 tall and the column needs all of it).</summary>
    public static float VacancyLiftAt(this WinUiOverlayTier tier, HeightTier height) => height == HeightTier.Short ? 0f : tier.VacancyLift;

    /// <summary>The vacancy draws its 40-DIP glyph: not in a Short window, where the decoration gives way so the words and the actions fit.</summary>
    public static bool VacancyShowsGlyph(HeightTier height) => height != HeightTier.Short;

    // ── the arithmetic (a view passes the live viewport and takes the answer) ────────────────────────────────────

    /// <summary>The width of a bare card whose own width is <paramref name="nominal"/> (bounded by <paramref name="min"/> and <paramref name="max"/>)
    /// in a window <paramref name="viewportWidth"/> wide: the window less the margins when the tier fills, else the nominal width; never wider than
    /// the window less the margins, never wider than <paramref name="max"/>.</summary>
    public static float CardWidth(this WinUiOverlayTier tier, float nominal, float max, float viewportWidth)
    {
        float room = MathF.Max(1f, viewportWidth - 2f * tier.CardMargin);
        float want = tier.CardFills ? room : nominal;
        return MathF.Min(MathF.Min(want, max), room);
    }

    /// <summary>The widest a flyout panel built for <paramref name="nominal"/> may be in a window <paramref name="viewportWidth"/> wide.</summary>
    public static float PanelWidth(this WinUiOverlayTier tier, float nominal, float viewportWidth)
        => MathF.Min(nominal, MathF.Max(1f, viewportWidth - 2f * tier.PanelMargin));

    /// <summary>The tallest a flyout panel may be in a window <paramref name="viewportHeight"/> tall: <paramref name="nominal"/>, bounded by the window
    /// less the panel margin above and below and the 48-DIP chrome row, so a panel taller than a short window scrolls inside itself.</summary>
    public static float PanelHeight(this WinUiOverlayTier tier, float nominal, float viewportHeight)
        => MathF.Min(nominal, MathF.Max(1f, viewportHeight - WinUiSheetRules.ChromeRow - 2f * tier.PanelMargin));

    /// <summary>The tallest the scrolling body of a panel may be: <paramref name="nominalBody"/>, bounded by the panel's height (<see cref="PanelHeight"/> of
    /// <paramref name="nominalPanel"/>) less <paramref name="fixedParts"/> (the header and the rails above the body), never under a row.</summary>
    public static float PanelBodyHeight(this WinUiOverlayTier tier, float nominalBody, float nominalPanel, float fixedParts, float viewportHeight)
        => MathF.Max(56f, MathF.Min(nominalBody, tier.PanelHeight(nominalPanel, viewportHeight) - fixedParts));
}
