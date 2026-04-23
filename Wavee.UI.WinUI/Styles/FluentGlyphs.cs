namespace Wavee.UI.WinUI.Styles;

/// <summary>
/// Named constants for Fluent / Segoe MDL2 icon codepoints used from C#.
///
/// XAML should inline glyph literals directly because XAML-literal PUA codepoints are stable —
/// but in C# they cause two classes of bugs:
///   (1) source-file encoding churn (editors silently save-as CP-1252 on Windows and mangle PUA),
///   (2) Edit-tool byte-matching fails (PUA characters don't survive tool/clipboard round-trips
///       reliably, so text replacements targeting a line with an inline glyph become unreliable).
/// Centralising the codepoints here — expressed as <c>\uXXXX</c> escapes — sidesteps both.
///
/// When adding a new entry, keep the name aligned with the official Fluent / MDL2 entry name
/// (<see href="https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font"/>).
/// </summary>
public static class FluentGlyphs
{
    /// <summary>Folder (closed) — E8B7.</summary>
    public const string Folder = "";

    /// <summary>FolderOpen — E838.</summary>
    public const string FolderOpen = "";
}
