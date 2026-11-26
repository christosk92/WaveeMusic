// ── Themes/Common/Rules/CommonDropPanelRules.cs — the drop panel's numbers (#240 §3.15, board 22) ──────────────────────────────

using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace Wavee.Themes.Common;

/// <summary>The drop panel's numbers (WinUI and Classic).</summary>
internal static class CommonDropPanelRules
{
    /// <summary>The drop panel (#240 §3.15, board 22): its key, and each zone's (a lint and trace family).</summary>
    public const string DropPanelKey = "shell.drop-panel";

    static readonly string[] s_dropZoneKeys = ["shell.drop-zone.0", "shell.drop-zone.1", "shell.drop-zone.2", "shell.drop-zone.3"];

    public static string DropZoneKey(int index) => s_dropZoneKeys[Math.Clamp(index, 0, s_dropZoneKeys.Length - 1)];

    /// <summary>The panel card: 16 around the zones, 8 between them (board 22: a 720 card for three 222 zones), and 16 kept from the
    /// window's edges where the content is narrower than the card (the zones shrink, their text wraps).</summary>
    public static readonly Edges4 DropPanelPadding = new(16f, 16f, 16f, 16f), DropLayerPadding = new(16f, 16f, 16f, 16f);
    public const float DropZoneGap = 8f, DropZoneW = 222f, DropZoneH = 226f, DropPanelPad = 16f;

    /// <summary>The card's widest: its zones at full width, the gaps and the padding.</summary>
    public static float DropPanelMaxW(int zones) => zones * DropZoneW + Math.Max(0, zones - 1) * DropZoneGap + 2f * DropPanelPad;

    /// <summary>The strokes: 1 for the card and a resting zone (dashed 4 on, 4 off), 2 for the lit zone; the lit zone's accent tint.</summary>
    public const float DropStroke = 1f, DropHotStroke = 2f, DropDash = 4f, DropHotTintA = 0.16f;

    /// <summary>A zone's inside: 12 around; the 52 glyph plate (a 20 glyph) and 18 under it; the title (Subtitle, 2 lines at most) and
    /// 4 above the consequence (Body, 2 lines).</summary>
    public static readonly Edges4 DropZonePadding = new(12f, 12f, 12f, 12f), DropGlyphMargin = new(0f, 0f, 0f, 18f), DropDetailMargin = new(0f, 4f, 0f, 0f);
    public const float DropGlyphPlate = 52f, DropGlyph = 20f;
    public const int DropTitleLines = 2, DropDetailLines = 2;

    /// <summary>The panel's entrance and exit: WinUI's flyout fade (167 ms decelerate in, 83 ms accelerate out).</summary>
    public static readonly LayoutTransition DropPanelMotion = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(Design.Motion.Fast, Easing.FluentDecelerate),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(Design.Motion.Faster, Easing.FluentAccelerate));
}
