// ── Themes/WinUi/Structure/Rules/WinUiCrashRules.cs — the numbers of Classic's crash consent card (themes plan 6.1, T11c) ──────
//
// What `Crash.UI.cs` held as file-level constants and inline literals, moved verbatim with the picture. The body's WIDTH and everything that differs per window
// tier (the report's place, the 40-DIP targets, the Huge second pane) is the tier record in `Tiers.Dialogs.cs`; what is here are the insets inside it. Data, not
// decisions (WVTH003 allows literals only here, in `Rules\**`).

namespace Wavee.Themes.WinUi;

/// <summary>The constants of the crash / hang consent card.</summary>
internal static class WinUiCrashRules
{
    /// <summary>The "what is sent" box's inner width inset: its padding either side of the body.</summary>
    public const float BoxInset = 32f;

    /// <summary>The preview box's inner padding (all sides).</summary>
    public const float PreviewPadding = 10f;

    /// <summary>A bullet's text width inset (the box padding plus the glyph column).</summary>
    public const float BulletTextInset = 56f;

    /// <summary>A bullet's glyph size.</summary>
    public const float BulletGlyphSize = 14f;

    /// <summary>A bullet's glyph drop from the first line.</summary>
    public const float BulletGlyphTop = 2f;

    /// <summary>The lead line's text size.</summary>
    public const float LeadSize = 14f;

    /// <summary>A bullet's text size.</summary>
    public const float BulletSize = 13f;

    /// <summary>The box heading's, the dump caption's and the "not configured" line's text size.</summary>
    public const float SmallTextSize = 12f;

    /// <summary>The box heading's weight.</summary>
    public const int HeadingWeight = 600;

    /// <summary>The gap between the include-dump box and its caption.</summary>
    public const float DumpGap = 2f;

    /// <summary>The caption's indent (the check box's glyph column) and its width inset.</summary>
    public const float DumpCaptionIndent = 28f;
}
