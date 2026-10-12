// ── Theming/Contracts/Tiers.cs — the window size tiers every structure is measured in (themes plan 3.2, T3b) ───────────
//
// Tiers are a CORE fact; where a tier begins, how it holds across a resize and what each tier lays out are the STRUCTURE's
// (`Wavee.Theming.TierPolicy`). The enums are contract v1.0: a structure maps its own ladders onto these five bands, and a
// view reads the band it is in (`ctx.Tier()`), never a width.

namespace Wavee.Theming.Contracts;

/// <summary>The window (or content region) width bands. The index is the tier index of a <c>FluentGpu.Foundation.TierLadder</c>
/// (Compact = 0, the widest band = the ladder's edge count). A structure that draws no Huge 4K band leaves that edge absent and
/// its Huge band covers every width from the Huge edge up (Classic, WinUI); one that draws it sets the edge (Zune, from 3200).</summary>
public enum WidthTier : byte
{
    /// <summary>One column, no docked pane, the panel as an overlay or sheet, the mini player bar.</summary>
    Compact = 0,

    /// <summary>The small window / laptop split.</summary>
    Small = 1,

    /// <summary>Today's desktop layout.</summary>
    Standard = 2,

    /// <summary>A 32-inch 1440p screen at 100 %, or a 4K screen at 150 %: a designed layout, not an upscale.</summary>
    Huge = 3,

    /// <summary>A 32-inch 4K screen at 100 %.</summary>
    Huge4K = 4,
}

/// <summary>The window height bands.</summary>
public enum HeightTier : byte
{
    /// <summary>A short window: heroes become header rows, the play bar its mini form.</summary>
    Short = 0,

    /// <summary>Every other height.</summary>
    Regular = 1,
}
