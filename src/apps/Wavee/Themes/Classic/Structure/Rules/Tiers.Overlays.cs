// ── Themes/Classic/Structure/Rules/Tiers.Overlays.cs — Classic's overlays, dialogs, toasts and states per window tier (themes plan 3.6, TC9) ──
//
// ONE record per width band, read by the views through `ClassicOverlayTiers.For(ctx.Tier().Window)` and by the core hosts through the policy
// (`ThemePolicies.Overlays`, built here from the same records). Compact follows the Compact boards (`C-ContextMenu-Compact`, `C-Dialogs-Compact`,
// `C-Flyouts-Compact`, `C-Toasts-Compact`, `C-Empty/Error/Offline/Unavailable-Compact`, `C-MultiSelect-Compact`, `C-RowStates-Compact`,
// `C-Skeletons-Compact`); Small and Standard are the tuned design (P3b, unchanged) with the caps that keep it inside a window; Huge is Standard
// with its own record, so a Huge tuning is a change of data.
//
// KEYED ON COMPACT. The window ladder is TC1's and moves (Small becomes 528-1007, Standard 1008-1919, Huge from 1920); nothing here names a width
// edge. `For` maps Compact, Huge and Huge 4K, and everything between is the standard record, so the rules are right under either ladder.
//
// The arithmetic (a card's width in a window, a panel's cap) lives here too, so a view never compares or computes with a measured width (WVTH013):
// it passes the live viewport and takes the answer.

using FluentGpu.Controls;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>What one width band decides for Classic's overlays. Public, not internal: this assembly has no <c>InternalsVisibleTo</c>, and the tests pin the records.</summary>
public sealed record ClassicOverlayTier
{
    /// <summary>What the core hosts read (the dialog card's fit, the toast dock, the vacancy, the selection bar, context sheets).</summary>
    public required OverlayLayout Core { get; init; }

    // ── the bare modal cards (the log-out and play-a-link cards) ──────────────────────────────────────────────────

    /// <summary>The card fills the window less its margin (Compact), instead of its own width.</summary>
    public required bool CardFills { get; init; }

    /// <summary>The window margin the card keeps on each side.</summary>
    public required float CardMargin { get; init; }

    /// <summary>The card's padding.</summary>
    public required Edges4 CardPadding { get; init; }

    /// <summary>The gap between the card's parts; null keeps the spacing scale's M (read where the element is built, so a theme change reaches it).</summary>
    public required float? CardGap { get; init; }

    /// <summary>The card's buttons are this tall and share the row equally (Compact: a 40 grid). 0 keeps the control's own height and its right-aligned row.</summary>
    public required float CardButtonHeight { get; init; }

    // ── flyout panels (the notification panel, the account menu, the runtime banner) ──────────────────────────────

    /// <summary>The window margin a panel keeps on each side: it is never wider than the window less this.</summary>
    public required float PanelMargin { get; init; }

    // ── the offline strip and vacancy ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The strip's height, its side padding and its Retry button's height (0 keeps the control's own).</summary>
    public required float StripHeight { get; init; }

    /// <summary>The strip's side padding.</summary>
    public required float StripPadX { get; init; }

    /// <summary>The strip's corner (0: edge to edge, Compact); null keeps the control radius.</summary>
    public required float? StripCorner { get; init; }

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

/// <summary>Classic's overlay tier records (public so <c>Wavee.Tests</c> can pin them).</summary>
public static class ClassicOverlayTiers
{
    /// <summary>Compact (the Compact boards): cards fill the window less 16, 40-tall command targets, the sheet, the quiet vacancy.</summary>
    public static ClassicOverlayTier Compact { get; } = new()
    {
        Core = new OverlayLayout(
            // Every dialog's commands are the engine's buttons at the 40-DIP target, a two-column grid (C-Dialogs-Compact): equal shares from a 96 floor, so two sit
            // side by side in a 300-DIP window's 268 card (236 inside the 16 padding) and three stack until the card holds 3 x 96 + 2 x 8.
            Dialog: new DialogFit(Fill: true, Inset: 16f, MaxWidth: 548f, EdgeY: 16f,
                ContentPadding: new Edges4(16f, 20f, 16f, 20f), CommandPadding: new Edges4(16f, 8f, 16f, 16f),
                CommandSize: ControlSize.Large, CommandMinWidth: 96f, UniformCommands: true),
            // C-Toasts-Compact: the card is the window less 8 either side, its action a 40-DIP button.
            Toast: new ToastDock(ToastPlacement.BottomCenter, GapAboveBar: 8f, BarHeight: ClassicPlayerTiers.MiniHeight,
                Lane: new ToastLaneStyle { SideMargin = 8f, FillWidth = true, ActionSize = ControlSize.Large }),
            Vacancy: new VacancyFit(QuietTitle: true, Padding: 16f, ActionHeight: 40f),
            Selection: new SelectionFit(MinHeight: 48f, SideInset: 8f, Padding: new Edges4(4f, 4f, 4f, 4f)),
            ContextSheet: true, MenuSize: ControlSize.Large),   // the anchored menus (sort, filter, the "..." menus) at 40-DIP rows
        CardFills = true, CardMargin = 16f, CardPadding = new Edges4(16f, 20f, 16f, 16f), CardGap = 12f, CardButtonHeight = 40f,
        PanelMargin = 8f,
        StripHeight = 40f, StripPadX = 16f, StripCorner = 0f, StripRetryHeight = 40f,
        VacancyQuietTitle = true, VacancyPadX = 16f, VacancyActionHeight = 40f, VacancyActionsStack = true, VacancyLift = ClassicOverlayRules.OfflineVacancyLift,
    };

    /// <summary>Small and Standard: the tuned design, capped to the window.</summary>
    public static ClassicOverlayTier Standard { get; } = new()
    {
        Core = new OverlayLayout(DialogFit.Stock, ToastDock.Stock, VacancyFit.Stock, SelectionFit.Stock, ContextSheet: false),
        CardFills = false, CardMargin = 16f, CardPadding = Edges4.All(ClassicDialogRules.CardPadding), CardGap = null, CardButtonHeight = 0f,
        PanelMargin = 16f,
        StripHeight = ClassicOverlayRules.OfflineStripH, StripPadX = ClassicOverlayRules.OfflineStripPadX, StripCorner = null, StripRetryHeight = 0f,
        VacancyQuietTitle = false, VacancyPadX = 0f, VacancyActionHeight = 0f, VacancyActionsStack = false, VacancyLift = ClassicOverlayRules.OfflineVacancyLift,
    };

    /// <summary>Huge and Huge 4K: the standard design (a dialog never stretches: its width caps at the engine's 548; a panel keeps its width), except the two FORM dialogs
    /// (the report and the crash prompt), whose card is the boards' two-pane 1040 (<c>C-Feedback-Huge</c>, <c>C-Crash-Huge</c>).</summary>
    public static ClassicOverlayTier Huge { get; } = Standard with
    {
        Core = new OverlayLayout(DialogFit.Stock with { FormWidth = ClassicScreenDialogTiers.HugeCardWidth }, ToastDock.Stock, VacancyFit.Stock, SelectionFit.Stock, ContextSheet: false),
    };

    /// <summary>The record of <paramref name="tier"/>: Compact, Huge (and Huge 4K), and the standard record for the bands between.</summary>
    public static ClassicOverlayTier For(WidthTier tier) => tier == WidthTier.Compact ? Compact : tier >= WidthTier.Huge ? Huge : Standard;

    /// <summary>The policy the structure publishes for the core hosts.</summary>
    public static OverlayPolicy Policy { get; } = new(Compact.Core, Standard.Core, Huge.Core);

    // ── what a Short window gives up (a height rule: plan 3.2, 330 tall leaves the page 210) ───────────────────

    /// <summary>The vacancy's lift: the board's 120 at a Regular height, none in a Short window (the page is 210 tall and the column needs all of it).</summary>
    public static float VacancyLiftAt(this ClassicOverlayTier tier, HeightTier height) => height == HeightTier.Short ? 0f : tier.VacancyLift;

    /// <summary>The vacancy draws its 40-DIP glyph: not in a Short window, where the decoration gives way so the words and the actions fit.</summary>
    public static bool VacancyShowsGlyph(HeightTier height) => height != HeightTier.Short;

    // ── the arithmetic (a view passes the live viewport and takes the answer) ────────────────────────────────────

    /// <summary>The width of a bare card whose own width is <paramref name="nominal"/> (bounded by <paramref name="min"/> and <paramref name="max"/>)
    /// in a window <paramref name="viewportWidth"/> wide: the window less the margins when the tier fills, else the nominal width; never wider than
    /// the window less the margins, never wider than <paramref name="max"/>.</summary>
    public static float CardWidth(this ClassicOverlayTier tier, float nominal, float max, float viewportWidth)
    {
        float room = MathF.Max(1f, viewportWidth - 2f * tier.CardMargin);
        float want = tier.CardFills ? room : nominal;
        return MathF.Min(MathF.Min(want, max), room);
    }

    /// <summary>The widest a flyout panel built for <paramref name="nominal"/> may be in a window <paramref name="viewportWidth"/> wide.</summary>
    public static float PanelWidth(this ClassicOverlayTier tier, float nominal, float viewportWidth)
        => MathF.Min(nominal, MathF.Max(1f, viewportWidth - 2f * tier.PanelMargin));

    /// <summary>The tallest a flyout panel may be in a window <paramref name="viewportHeight"/> tall: <paramref name="nominal"/>, bounded by the window
    /// less the panel margin above and below and the 48-DIP chrome row, so a panel taller than a short window scrolls inside itself.</summary>
    public static float PanelHeight(this ClassicOverlayTier tier, float nominal, float viewportHeight)
        => MathF.Min(nominal, MathF.Max(1f, viewportHeight - ClassicSheetRules.ChromeRow - 2f * tier.PanelMargin));

    /// <summary>The tallest the scrolling body of a panel may be: <paramref name="nominalBody"/>, bounded by the panel's height (<see cref="PanelHeight"/> of
    /// <paramref name="nominalPanel"/>) less <paramref name="fixedParts"/> (the header and the rails above the body), never under a row.</summary>
    public static float PanelBodyHeight(this ClassicOverlayTier tier, float nominalBody, float nominalPanel, float fixedParts, float viewportHeight)
        => MathF.Max(56f, MathF.Min(nominalBody, tier.PanelHeight(nominalPanel, viewportHeight) - fixedParts));
}
