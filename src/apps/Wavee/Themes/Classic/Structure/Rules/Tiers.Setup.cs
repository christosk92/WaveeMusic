// ── Themes/Classic/Structure/Rules/Tiers.Setup.cs — Classic's setup wizard per window tier (themes plan 3.6, TC8) ──────────────────────────
//
// ONE record per WINDOW tier, read by every setup view through `ClassicSetupTiers.Of(ctx.Tier())`; it replaces the hand-rolled ladder `SetupGeometry` held (one 770-DIP
// icon breakpoint with no hysteresis, read off the raw viewport). The ladder is TC1's (Rules\Tiers.cs: Compact < 528, Small 528-1007, Standard 1008-1919, Huge from 1920, the
// 40-DIP directional holds), so the wizard changes form exactly where the rest of the frame does and a tier crossing is one animated step, never a snap.
//
//   Compact  (C-Setup-Compact)  NO plate: a full-bleed sheet filling the window under the 48-DIP title row, padding 16 / 24 / 16 / 16, the two sign-in cards stacked
//                               full width, the footer stacked (step label over a full-width bar, then a two-column grid of 40-DIP buttons), Back 40 x 40.
//   Small    (C-Setup-Small)    the 762 x 490 plate WITHOUT the icon column (the board draws none at 800: the old 770-1007 band that kept it is gone), Back beside
//                               the title, the two sign-in cards side by side (min height 132, padding 20), the 80-DIP footer with its 210-DIP progress column.
//   Standard (C-Setup)          today's tuned plate: 762 x 490, the 192 icon column, the cards stacked in the 498-DIP column, Back over the icon column's corner.
//   Huge     (C-Setup-Huge)     the 960 x 600 plate, padding 32, a 240 icon column, the cards side by side, a 120-DIP QR, Back beside the title.
//
// The arithmetic (the plate's size in a window, the body's width in the plate) lives here too, so a view never compares or computes with a measured width (WVTH013): it passes
// the live viewport and takes the answer. Public, so `Wavee.Tests` pins the records (the assembly has no InternalsVisibleTo).

using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>What Classic's setup wizard lays out in one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Sheet">A full-bleed sheet under <paramref name="TopInset"/> instead of a plate (square, no shadow).</param>
/// <param name="TopInset">The sheet's top inset: the title row the window keeps (the title bar stays reachable).</param>
/// <param name="PlateWidth">The plate's nominal size (a sheet fills the window instead).</param>
/// <param name="PlateHeight">The plate's nominal height.</param>
/// <param name="MinPlateWidth">The floor the plate shrinks to.</param>
/// <param name="MinPlateHeight">The floor the plate's height shrinks to.</param>
/// <param name="ViewportMargin">The margin the plate keeps from the window.</param>
/// <param name="ContentPadding">The page region's padding (left, top, right, bottom).</param>
/// <param name="IconColumn">The icon column's width; 0 = no icon column.</param>
/// <param name="IconGap">The gap between the icon column and the page.</param>
/// <param name="BackInContent">Back sits over the page's own corner (beside the title), not over the icon column's.</param>
/// <param name="BackSize">Back's edge (40 at Compact).</param>
/// <param name="BackSpacer">The spacer that clears Back beside the title.</param>
/// <param name="BackPadding">The padding of the layer Back rides in (where its top-left lands).</param>
/// <param name="FooterStacked">The footer is a column (step label over a full-width bar, then the buttons), not one row.</param>
/// <param name="FooterHeight">The row footer's height (a stacked footer takes its content's).</param>
/// <param name="FooterPadding">The footer's padding.</param>
/// <param name="ProgressColumn">The row footer's step column (the label over the bar); 0 when the footer is stacked.</param>
/// <param name="ButtonHeight">The command buttons' height (the footer's, the sign-in door's, the runtime card's).</param>
/// <param name="SignatureHeight">The runtime card's [Signature] button.</param>
/// <param name="Target">The hit-target floor for links, radios and rows: 40 at Compact, 0 (the control's own size) elsewhere.</param>
/// <param name="SignInSideBySide">The two sign-in cards share one row (a plate with no icon column to give up); otherwise they stack.</param>
/// <param name="SignInCardMinHeight">A side-by-side card's least height.</param>
/// <param name="SignInCardPadding">A side-by-side card's padding.</param>
/// <param name="QrSize">The wizard's pairing QR edge.</param>
public sealed record ClassicSetupLayout(
    WidthTier Tier, bool Sheet, float TopInset,
    float PlateWidth, float PlateHeight, float MinPlateWidth, float MinPlateHeight, float ViewportMargin,
    Edges4 ContentPadding, float IconColumn, float IconGap,
    bool BackInContent, float BackSize, float BackSpacer, Edges4 BackPadding,
    bool FooterStacked, float FooterHeight, Edges4 FooterPadding, float ProgressColumn,
    float ButtonHeight, float SignatureHeight, float Target,
    bool SignInSideBySide, float SignInCardMinHeight, float SignInCardPadding, float QrSize)
{
    /// <summary>The icon column shows (a Lottie hero beside the page).</summary>
    public bool ShowsIcon => IconColumn > 0f;

    /// <summary>The icon column's width plus its gap: what the page region gives up to it (0 with no icon column).</summary>
    public float IconSpan => IconColumn > 0f ? IconColumn + IconGap : 0f;

    /// <summary>The wizard's root width in a window <paramref name="viewportWidth"/> wide: the window for a sheet, else the plate's nominal width clamped to the window less its margins.</summary>
    public float RootWidth(float viewportWidth)
        => Sheet ? MathF.Max(1f, viewportWidth)
                 : Math.Clamp(PlateWidth, MinPlateWidth, MathF.Max(MinPlateWidth, viewportWidth - 2f * ViewportMargin));

    /// <summary>The wizard's root height in a window <paramref name="viewportHeight"/> tall: the window for a sheet (the top inset is its padding), else the plate's clamped height.</summary>
    public float RootHeight(float viewportHeight)
        => Sheet ? MathF.Max(1f, viewportHeight)
                 : Math.Clamp(PlateHeight, MinPlateHeight, MathF.Max(MinPlateHeight, viewportHeight - 2f * ViewportMargin));

    /// <summary>The root's top padding: a sheet keeps the title row clear; a plate has none.</summary>
    public float RootTopPad => Sheet ? TopInset : 0f;

    /// <summary>The page body's width in a window <paramref name="viewportWidth"/> wide: the root less the page region's padding and the icon column.</summary>
    public float BodyWidth(float viewportWidth) => RootWidth(viewportWidth) - ContentPadding.Horizontal - IconSpan;

    /// <summary>The footer's progress bar width in a window <paramref name="viewportWidth"/> wide: the bar of the 210 column, or the whole footer row when stacked.</summary>
    public float ProgressBarWidth(float viewportWidth, float columnBar)
        => FooterStacked ? MathF.Max(1f, RootWidth(viewportWidth) - FooterPadding.Horizontal) : columnBar;

    /// <summary>The spacer beside the title that clears Back: only where Back sits in the page's column, on a page that shows Back.</summary>
    public bool BackSpacerApplies(Setup.WizardPage page) => BackInContent && Setup.Gating.ShowsBack(page);
}

/// <summary>Classic's setup wizard records, one per window tier.</summary>
public static class ClassicSetupTiers
{
    private static readonly ClassicSetupLayout[] s_layouts =
    [
        // Compact (C-Setup-Compact): the sheet under the 48 title row; padding 24 / 16 / 16; stacked footer, 40-DIP buttons and Back.
        new(WidthTier.Compact, Sheet: true, TopInset: ClassicSheetRules.ChromeRow,
            PlateWidth: 0f, PlateHeight: 0f, MinPlateWidth: 0f, MinPlateHeight: 0f, ViewportMargin: 0f,
            ContentPadding: new Edges4(16f, 24f, 16f, 16f), IconColumn: 0f, IconGap: 0f,
            BackInContent: true, BackSize: 40f, BackSpacer: 52f, BackPadding: new Edges4(16f, 18f, 0f, 0f),
            FooterStacked: true, FooterHeight: 0f, FooterPadding: Edges4.All(16f), ProgressColumn: 0f,
            ButtonHeight: 40f, SignatureHeight: 40f, Target: 40f,
            SignInSideBySide: false, SignInCardMinHeight: 0f, SignInCardPadding: 16f, QrSize: 80f),
        // Small (C-Setup-Small): the plate, no icon column, Back beside the title, the cards side by side.
        new(WidthTier.Small, Sheet: false, TopInset: 0f,
            PlateWidth: 762f, PlateHeight: 490f, MinPlateWidth: 320f, MinPlateHeight: 184f, ViewportMargin: 16f,
            ContentPadding: Edges4.All(24f), IconColumn: 0f, IconGap: 0f,
            BackInContent: true, BackSize: 30f, BackSpacer: 42f, BackPadding: Edges4.All(24f),
            FooterStacked: false, FooterHeight: 80f, FooterPadding: Edges4.All(24f), ProgressColumn: 210f,
            ButtonHeight: 32f, SignatureHeight: 28f, Target: 0f,
            SignInSideBySide: true, SignInCardMinHeight: 132f, SignInCardPadding: 20f, QrSize: 80f),
        // Standard (C-Setup, the tuned plate): the 192 icon column, Back over the icon column's corner, the cards stacked.
        new(WidthTier.Standard, Sheet: false, TopInset: 0f,
            PlateWidth: 762f, PlateHeight: 490f, MinPlateWidth: 320f, MinPlateHeight: 184f, ViewportMargin: 32f,
            ContentPadding: Edges4.All(24f), IconColumn: 192f, IconGap: 24f,
            BackInContent: false, BackSize: 30f, BackSpacer: 42f, BackPadding: Edges4.All(24f),
            FooterStacked: false, FooterHeight: 80f, FooterPadding: Edges4.All(24f), ProgressColumn: 210f,
            ButtonHeight: 32f, SignatureHeight: 28f, Target: 0f,
            SignInSideBySide: false, SignInCardMinHeight: 0f, SignInCardPadding: 16f, QrSize: 80f),
        // Huge (C-Setup-Huge): the 960 x 600 plate, padding 32, a 240 icon column, Back beside the title (in the page's column), the cards side by side, a 120 QR.
        new(WidthTier.Huge, Sheet: false, TopInset: 0f,
            PlateWidth: 960f, PlateHeight: 600f, MinPlateWidth: 320f, MinPlateHeight: 184f, ViewportMargin: 32f,
            ContentPadding: Edges4.All(32f), IconColumn: 240f, IconGap: 32f,
            BackInContent: true, BackSize: 30f, BackSpacer: 42f, BackPadding: new Edges4(304f, 31f, 0f, 0f),
            FooterStacked: false, FooterHeight: 80f, FooterPadding: Edges4.All(32f), ProgressColumn: 210f,
            ButtonHeight: 32f, SignatureHeight: 28f, Target: 0f,
            SignInSideBySide: true, SignInCardMinHeight: 132f, SignInCardPadding: 20f, QrSize: 120f),
    ];

    /// <summary>The record of window tier <paramref name="window"/> (Huge 4K reads Huge's: Classic draws no 4K band).</summary>
    public static ClassicSetupLayout For(WidthTier window) => s_layouts[Math.Min((int)window, (int)WidthTier.Huge)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers (a view mounted alone).</summary>
    public static ClassicSetupLayout Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Window);
}
