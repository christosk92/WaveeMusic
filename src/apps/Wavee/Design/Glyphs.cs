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
