namespace Wavee;

// The custom WaveeIcons font (app/Wavee/assets/fonts/wavee-icons.otf, built by build-wavee-icons.py) — Spotify's real
// "Play next" / "Add to queue" marks the Segoe Fluent set doesn't carry (the engine's generated Icons.* superset carries
// every other Wavee glyph; only these three custom-font marks stay app-local). ASCII-safe Of(0x____) convention: built
// from hex codepoints at runtime so the SOURCE stays pure-ASCII — raw PUA chars / \u escapes get mangled by the
// edit/encoding chain (per the engine's own rule).
internal static class WaveeIcons
{
    static string Of(int cp) => ((char)cp).ToString();

    public static readonly string PlayNext = Of(0xE900);       // play-on-top mark (front of queue)
    public static readonly string PlayAfter = Of(0xE901);      // play-on-bottom / add-to-queue mark (end of queue)
    public static readonly string Lyrics = Of(0xE902);         // lyrics/chat bubble

    // Absolute path + #family (the engine loads by PATH; the #suffix is a stable cache key only).
    public static readonly string Font =
        System.IO.Path.Combine(System.AppContext.BaseDirectory, "assets", "fonts", "wavee-icons.otf") + "#WaveeIcons";
}

// The app's font FILES, resolved next to the exe (assets/fonts/*, shipped by the csproj's assets\** Content glob and the
// MSIX layout's recursive copy of the publish dir). Every entry is the engine's "path#Family" form: GlyphRenderer /
// TextLayoutEngine split at '#', load the PATH through IDWriteFactory::CreateFontFileReference and never hand the
// family name to DirectWrite, so the "#Family" half is only the (family, weight) cache key.
internal static class WaveeFonts
{
    // Segoe Fluent Icons, BUNDLED (assets/fonts/SegoeFluentIcons.ttf, see FONTS.md beside it). Assigned to
    // Theme.IconFont at startup (Program.cs) so every Icons.* / IconRef glyph resolves against this file on Windows 10
    // and 11 alike. The system family of the same name ships with Windows 11 only: on Windows 10 DirectWrite substitutes
    // Segoe MDL2 Assets for the shared PUA range, and every glyph ADDED in Fluent Icons (RefineSparkle U+F1D5, the
    // "Tune" toolbar mark, RowSize, ...) rendered as tofu. Loading by path removes the OS dependency outright.
    public static readonly string Icons =
        System.IO.Path.Combine(System.AppContext.BaseDirectory, "assets", "fonts", "SegoeFluentIcons.ttf") + "#Segoe Fluent Icons";

    /// <summary>The bundled icon file's absolute path (the half before '#'), for the startup log / diagnostics.</summary>
    public static string IconsPath => Icons.Substring(0, Icons.IndexOf('#'));
}
