// ── Themes/Common/Rules/CommonCustomizeRules.cs — the numbers of the Home customizer (themes plan 6.5, T11c) ─────────────────────

namespace Wavee.Themes.Common;

/// <summary>The constants of the Home customizer (moved from Classic's Home rules with the page).</summary>
public static class CommonCustomizeRules
{
    /// <summary>One zone row's height, also the reorderable's item extent.</summary>
    public const float CustomizeRowH = 48f;
    public const float CustomizeHeaderGap = 16f;
    public const float CustomizeTitleGap = 4f;
    public const float CustomizeListTop = 16f;
    public const float CustomizeRowGap = 8f;

    /// <summary>A Compact row: the hit target's height (the Up and Down buttons are 40 square) and a tighter gap, so the label keeps room beside three 40-DIP controls.</summary>
    public const float CompactRowH = 56f, CompactRowGap = 4f;
    public const float CustomizeGripSize = 12f;
    public const float CustomizeLabelSize = 14f;
    public const float CustomizeLabelLine = 20f;

    /// <summary>A hidden zone's row dims in place.</summary>
    public const float CustomizeHiddenOpacity = 0.55f;
}
