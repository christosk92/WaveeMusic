// ── Themes/WinUi/Structure/Rules/WinUiSetupGeometry.cs — the tier-invariant numbers of Classic's setup wizard (themes plan 6.1, T11c; TC8) ───────────
//
// What `Screens\Setup.cs` held as `Setup.Layout` (plan P13): the wizard's numbers. Everything that differs by window size (the plate or the sheet and its size, the icon column,
// the footer's form, the button height, Back, the sign-in card arrangement) is the per-tier record `WinUiSetupLayout` in `Tiers.Setup.cs`; the one icon breakpoint this file held
// is gone with it. What stays here is the rhythm every tier shares: the header's pull, the body's spacing, the footer's gaps, the keep-alive count and the hero's zoom.
// They are data, not decisions (WVTH003 allows literals only in `Rules\**`). What the core still asks the wizard's picture (the shell's cover for the dim, the hero's asset name) is
// `Setup.Cover` and `Setup.WizardHeroes.AssetFor`, not here.

namespace Wavee.Themes.WinUi;

/// <summary>The tier-invariant geometry of Classic's setup wizard.</summary>
public static class WinUiSetupGeometry
{
    /// <summary>The page frame's header pull and its bottom gap.</summary>
    public const float HeaderTopPull = 4f, HeaderBottomGap = 4f;

    /// <summary>The body's rhythm.</summary>
    public const float BodySpacing = 20f, BodyInnerSpacing = 12f;

    /// <summary>The footer's gap between its row items, and the step label's gap over its bar.</summary>
    public const float FooterColumnGap = 6f, ProgressStackGap = 6f;

    /// <summary>The stacked footer's gap between the step block and the buttons, and between the two buttons.</summary>
    public const float StackedFooterGap = 12f, StackedButtonGap = 8f;

    /// <summary>The row footer's step column: the right pad after its label and bar, and the bar's width.</summary>
    public const float ProgressColumnRightPad = 48f, ProgressWidth = 162f;

    /// <summary>The Back button's glyph.</summary>
    public const float BackGlyphSize = 12f;

    /// <summary>The hairline between the content and the footer.</summary>
    public const float SeparatorHeight = 1f;

    /// <summary>The keep-alive's page count (Sign in, Layout and Local playback stay mounted behind Terms).</summary>
    public const int KeepAlivePages = 3;

    /// <summary>The hero's zoom over its authored fit — Wavee's one deliberate deviation from a literal Rise readout.</summary>
    public const float HeroZoom = 1.2f;
}
