// ── Themes/Classic/Structure/Rules/ClassicFrameBitsRules.cs — the numbers of Classic's frame bits (themes plan 6.1, T4c) ───
//
// What `Shell.Masthead.UI.cs`, `Shell.Palette.cs`, `Shell.UI.cs` (file-drop cue, drawer scrim) held as file-level constants and inline literals, moved verbatim with their pictures. They are data, not decisions: the
// views under `Views\Masthead`, `Views\Drawer`, `Views\Palette` and `Views\FrameBits` read them (WVTH003 allows literals only
// here, in `Rules\**`).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Classic;

/// <summary>The constants of the masthead, the drawer, the palette, the file-drop cue.</summary>
internal static class ClassicFrameBitsRules
{
    // ── the masthead band ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The band's top inset.</summary>
    public const float MastheadFrameTop = 32f;

    /// <summary>The band's padding: the page <paramref name="gutter"/> either side (the content tier's, so the band's title sits on the
    /// page's column at every tier) and the top inset.</summary>
    public static Edges4 MastheadPadding(float gutter) => new(gutter, MastheadFrameTop, gutter, 0f);

    /// <summary>The band's swap (tuning F7), on the page swap's own clock: a leaving head EXITS on the exit leg (90 ms, accelerate,
    /// from the commit) with the leaving body; an arriving head waits that leg out and ENTERS on the enter leg (210 ms, decelerate),
    /// with the new body. Opacity only: the head sits on the page's column and never travels.</summary>
    public static LayoutTransition MastheadSwap => new(
        TransitionChannels.Opacity,
        TransitionDynamics.Tween(Design.Nav.EnterDurationMs, Design.Nav.EnterEasing),
        Enter: new EnterExit(Opacity: 0f, Active: true),
        Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Design.Nav.ExitDurationMs, Design.Nav.ExitEasing),
        DelayMs: Design.Nav.EnterDelayMs, ExitDelayMs: 0f);

    // ── the narrow drawer ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The non-dismissing scrim of a PINNED drawer (editing in a forced band): the page behind stays usable (Q16).</summary>
    public static readonly ColorF DrawerPinnedScrim = new(0f, 0f, 0f, 0.2f);

    /// <summary>The scrim of an ordinary open drawer.</summary>
    public static readonly ColorF DrawerScrim = ColorF.FromRgba(0, 0, 0, 0x33);

    /// <summary>The drawer pane's corners: square on the window edge, the card radius on the content side.</summary>
    public static CornerRadius4 DrawerPaneCorners => new(0f, Radii.Card, Radii.Card, 0f);

    // ── the drawer's account row (TC2, C-Shell-Compact / C-Sidebar account="true": a Compact title bar carries no chip) ──

    /// <summary>The row: 56 tall, 4 left and 6 right of padding, 2 between its parts, 4 under its divider.</summary>
    public const float AccountRowH = 56f, AccountRowGap = 2f, AccountRowBelow = 4f;

    /// <summary>The row's padding.</summary>
    public static readonly Edges4 AccountRowPadding = new(4f, 0f, 6f, 0f);

    /// <summary>The bell and friends buttons: 40 x 40 (the Compact target floor), glyph 16.</summary>
    public const float AccountButton = 40f, AccountGlyph = 16f;

    // ── the command palette ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The card's hard width (W1: no clamp).</summary>
    public const float PaletteCardW = 560f;

    /// <summary>The card's maximum height.</summary>
    public const float PaletteCardMaxH = 460f;

    /// <summary>A row's glyph and label size.</summary>
    public const float PaletteRowTextSize = 14f;

    /// <summary>The gap between result rows.</summary>
    public const float PaletteRowGap = 1f;

    /// <summary>The lane's top pad: the card's top edge sits exactly 64 DIP down.</summary>
    public static float PaletteLaneTop => Spacing.XXXL * 2f;

    // ── the file-drop cue ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The drop pill's padding.</summary>
    public static readonly Edges4 FileDropPillPadding = new(18f, 10f, 18f, 10f);

    /// <summary>The drop pill's text size.</summary>
    public const float FileDropTextSize = 14f;

    // ── the setup wizard's cover scrim ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The dim's fade, in milliseconds (0 under reduced motion).</summary>
    public const float CoverScrimFadeMs = 250f;
}
