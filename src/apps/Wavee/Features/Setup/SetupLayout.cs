using System;

namespace Wavee;

/// <summary>What the shell BEHIND the setup plate should look like while a page is open (the <c>SetupSession.Covering</c>
/// reader). <c>Dim</c> = the ordinary modal scrim, the only value <see cref="SetupLayout.CoverFor"/> returns for a
/// shell that IS behind the plate; <c>None</c> = no scrim at all (the pre-auth bare mount, where the engine's own
/// Modal scrim already paints over bare Mica). <c>Live</c> is unreachable — no page's stage is a live preview of the
/// shell — kept as a value rather than deleted since <c>WaveeShell</c>'s scrim reader outside this file's scope still
/// names it in its own doc comment.</summary>
enum SetupCover { None, Dim, Live }

/// <summary>All setup sizing decisions in one place — the Rise Media Player reference metrics verbatim (captured
/// 2026-09-02 from its live XAML): a 762×490 <c>ContentDialog</c>, a 192-wide icon column beside the page content at
/// one breakpoint (no tier ladder — Rise's own <c>AdaptiveTrigger MinWindowWidth 770</c> is a single on/off switch,
/// not a hysteresis band), and an 80-tall footer whose 210-wide progress column collapses with the icon column.
///
/// <para>This REPLACES the old "stage + decision" 896×576 four-tier composition wholesale — the tier ladder
/// (<c>SetupLayoutTier</c>, hysteresis, the 344-DIP stage rail, every row/chip/pairing-lane const) is gone. Pinned by
/// <c>SetupLayoutTests</c>.</para></summary>
static class SetupLayout
{
    // ── Rise ContentDialog overrides ────────────────────────────────────────────────────────────────────────────────
    public const float PlateWidth = 762f, PlateHeight = 490f, PlatePadding = 24f, PlateCorner = 8f;   // Radii.OverlayAll
    public const float MinPlateWidth = 320f, MinPlateHeight = 184f, ViewportMargin = 32f;             // ContentDialog Min; keep the viewport clamp

    // ── SetupPageContent (the icon column + header + scroller) ──────────────────────────────────────────────────────
    public const float IconColumnWidth = 192f, IconColumnGap = 24f, IconBreakpoint = 770f;            // AdaptiveTrigger MinWindowWidth
    public const float HeaderTopPull = 4f, HeaderBottomGap = 4f, BackSpacerWidth = 42f;                // TextBlock Margin 0,-4,0,4 · PaddingRectangle
    public const float BodySpacing = 20f, BodyInnerSpacing = 12f;                                      // StackPanel.Spacing
    public const float ScrollGutter = 24f;                                                             // ScrollViewer Margin/Padding trick

    // ── ControlGrid (the footer) ─────────────────────────────────────────────────────────────────────────────────────
    public const float FooterHeight = 80f, FooterPadding = 24f, FooterColumnGap = 6f;
    public const float ProgressColumnWidth = 210f, ProgressColumnRightPad = 48f, ProgressStackGap = 6f, ProgressWidth = 162f;
    public const float BackButtonSize = 30f, BackGlyphSize = 12f;
    // The body's own fixed metrics (Ui.Title = 28/36 → one 36-DIP header line; the 1-px separator above the footer)
    public const float HeaderLineHeight = 36f, SeparatorHeight = 1f;
    // Sign-in Idle body: the QR in the scan card's Content slot, and the WinUI text/control heights the budget sums
    // (BodyStrong/Body 14-px line = 20; SettingsCard MinHeight 68 / Padding 16 — FluentGpu.Controls SettingsCard;
    // HyperlinkButton = ButtonPadding 5/6 + a 20-px line = 32 — the same lane as Button.MinHeight).
    public const float QrSize = 80f, BodyLineHeight = 20f, CardMinHeight = 68f, CardPadding = 16f, LinkRowHeight = 32f;

    public static float Width(float viewportW) => Math.Clamp(PlateWidth, MinPlateWidth, MathF.Max(MinPlateWidth, viewportW - 2f * ViewportMargin));
    public static float Height(float viewportH) => Math.Clamp(PlateHeight, MinPlateHeight, MathF.Max(MinPlateHeight, viewportH - 2f * ViewportMargin));

    /// <summary>Rise's one breakpoint: the icon column exists only when the WINDOW is ≥ 770 wide (viewport, not
    /// plate) — a single on/off switch, unlike the old four-tier ladder's hysteresis band.</summary>
    public static bool ShowsIcon(float viewportW) => viewportW >= IconBreakpoint;

    /// <summary>The footer's progress column collapses with the icon column (Rise: <c>ProgressColumn.Width</c>
    /// 0 → 210 in the same <c>LargeSizeState</c>).</summary>
    public static float ProgressColumnFor(bool large) => large ? ProgressColumnWidth : 0f;

    /// <summary>What's left of the footer for its two stretch buttons once the padding, the (possibly collapsed)
    /// progress column and the gaps between all three come out.</summary>
    public static float FooterButtonWidth(float plateW, bool large) =>
        (plateW - 2f * FooterPadding - ProgressColumnFor(large) - 2f * FooterColumnGap) / 2f;

    public static SetupCover CoverFor(bool shellBehind) => shellBehind ? SetupCover.Dim : SetupCover.None;

    /// <summary>The height the page BODY (the scrolling column under the title) gets inside a plate of
    /// <paramref name="plateH"/>: the plate minus the footer, the separator, the content region's 24-DIP padding
    /// top+bottom, and the one-line title with its <c>0,-4,0,4</c> margin (net +0 over the 36-DIP line).</summary>
    public static float BodyLaneHeight(float plateH) =>
        plateH - FooterHeight - SeparatorHeight - 2f * PlatePadding - (HeaderLineHeight - HeaderTopPull + HeaderBottomGap);

    /// <summary>The sign-in Idle body's natural height for a lead paragraph of <paramref name="leadLines"/> lines:
    /// lead · browser card (one-line header + one-line description = the 68 minimum) · scan card (the 80-DIP QR in its
    /// Content slot + 16 padding top/bottom) · the one-row "needs Premium · Sign up" line, with Rise's 20-DIP stack
    /// spacing between the four. <see cref="BodyLaneHeight"/> at the reference plate is the budget it must fit.</summary>
    public static float SignInIdleBodyHeight(int leadLines) =>
        leadLines * BodyLineHeight + BodySpacing
        + CardMinHeight + BodySpacing
        + (QrSize + 2f * CardPadding) + BodySpacing
        + LinkRowHeight;
}
