// ── Themes/Classic/Structure/Rules/SetupGeometry.cs — the numbers of Classic's setup wizard (themes plan 6.1, T11c) ─────────────
//
// What `Screens\Setup.cs` held as `Setup.Layout` (plan P13): every wizard number — the Rise reference plate 762×490 (min 320×184), ONE 770-DIP icon breakpoint with no hysteresis, an
// 80-tall footer whose 210-wide progress column collapses with the icon column (ch 28 §2, §3.1) — and the few pure questions over them. They are data and arithmetic over data, not
// decisions (WVTH003 allows literals only in `Rules\**`). What the core still asks the wizard's picture (the shell's cover for the dim, the hero's asset name) is `Setup.Cover` and
// `Setup.WizardHeroes.AssetFor`, not here.



namespace Wavee.Themes.Classic;

/// <summary>The geometry of Classic's setup wizard plate.</summary>
public static class SetupGeometry
{
    /// <summary>The plate's size, its content padding, the floor it shrinks to and the margin it keeps from the window.</summary>
    public const float PlateWidth = 762f, PlateHeight = 490f, PlatePadding = 24f;
    public const float MinPlateWidth = 320f, MinPlateHeight = 184f, ViewportMargin = 32f;

    /// <summary>The icon column and the ONE breakpoint (a WINDOW width, no hysteresis).</summary>
    public const float IconColumnWidth = 192f, IconColumnGap = 24f, IconBreakpoint = 770f;

    /// <summary>The page frame's header pull, its bottom gap and the spacer that clears the Back button.</summary>
    public const float HeaderTopPull = 4f, HeaderBottomGap = 4f, BackSpacerWidth = 42f;

    /// <summary>The body's rhythm and the scroll rail's gutter.</summary>
    public const float BodySpacing = 20f, BodyInnerSpacing = 12f, ScrollGutter = 24f;

    /// <summary>The footer band.</summary>
    public const float FooterHeight = 80f, FooterPadding = 24f, FooterColumnGap = 6f;

    /// <summary>The footer's progress column (the label over the bar) and its right pad.</summary>
    public const float ProgressColumnWidth = 210f, ProgressColumnRightPad = 48f, ProgressStackGap = 6f, ProgressWidth = 162f;

    /// <summary>The Back button and its glyph.</summary>
    public const float BackButtonSize = 30f, BackGlyphSize = 12f;

    /// <summary>The hairline between the content and the footer.</summary>
    public const float SeparatorHeight = 1f;

    /// <summary>The wizard's pairing QR size (a hint: the plate snaps it to whole pixels per module).</summary>
    public const float QrSize = 80f;

    /// <summary>The keep-alive's page count (Sign in, Layout and Local playback stay mounted behind Terms).</summary>
    public const int KeepAlivePages = 3;

    /// <summary>The hero's zoom over its authored fit — Wavee's one deliberate deviation from a literal Rise readout.</summary>
    public const float HeroZoom = 1.2f;

    /// <summary>The plate's width at a window width.</summary>
    public static float Width(float viewportW) => Math.Clamp(PlateWidth, MinPlateWidth, MathF.Max(MinPlateWidth, viewportW - 2f * ViewportMargin));

    /// <summary>The plate's height at a window height.</summary>
    public static float Height(float viewportH) => Math.Clamp(PlateHeight, MinPlateHeight, MathF.Max(MinPlateHeight, viewportH - 2f * ViewportMargin));

    /// <summary>The one breakpoint, on the WINDOW width (not the plate's).</summary>
    public static bool ShowsIcon(float viewportW) => viewportW >= IconBreakpoint;

    /// <summary>The footer's progress column: its width with the icon column, none without.</summary>
    public static float ProgressColumnFor(bool large) => large ? ProgressColumnWidth : 0f;

    /// <summary>A body card's width at a viewport (W3b): the plate less its padding, less the icon column and its gap
    /// when shown — what `SettingsCard`'s two thresholds (content wrap 476, icon drop 286) are measured against.</summary>
    public static float CardWidth(float viewportW) =>
        Width(viewportW) - 2f * PlatePadding - (ShowsIcon(viewportW) ? IconColumnWidth + IconColumnGap : 0f);

    /// <summary>The 42-DIP back spacer beside the title: only with no icon column, on a page that shows Back.</summary>
    public static bool BackSpacerApplies(Setup.WizardPage page, bool iconShown) => !iconShown && Setup.Gating.ShowsBack(page);
}
