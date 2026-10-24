// ── Themes/Common/Rules/CommonFpsRules.cs — the numbers of the frame-rate overlay (themes plan 6.5, T11c) ─────────────────────
//
// What `Diagnostics.UI.cs` held as inline literals, moved verbatim with the picture (T4c, then T11c: the HUD is one copy in
// `Common`, every theme registers it). Data, not decisions: `CommonFps` reads them (WVTH003 allows literals only here, in `Rules\**`).

using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee.Themes.Common;

/// <summary>The constants of the frame-rate overlay.</summary>
internal static class CommonFpsRules
{
    /// <summary>The HUD's inset from the window's top right: below the title bar and the first rows, off the scroll bar.</summary>
    public static readonly Edges4 FpsInset = new(0f, 104f, 14f, 0f);

    /// <summary>The pill's padding.</summary>
    public static readonly Edges4 FpsPillPadding = new(8f, 4f, 8f, 4f);

    /// <summary>The pill's corner radius.</summary>
    public const float FpsPillRadius = 6f;

    /// <summary>The pill's gap between its numbers and units.</summary>
    public const float FpsPillGap = 4f;

    /// <summary>The pill's fill opacity over the shell.</summary>
    public const float FpsPillFillAlpha = 0.90f;

    /// <summary>The numbers' and units' size.</summary>
    public const float FpsTextSize = 12f;

    /// <summary>The frame-rate figure's weight.</summary>
    public const int FpsFigureWeight = 700;

    /// <summary>The unit and frame-time weight.</summary>
    public const int FpsUnitWeight = 600;
}
