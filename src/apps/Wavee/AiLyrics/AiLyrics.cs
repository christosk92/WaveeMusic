// ── AiLyrics/AiLyrics.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// On-device AI lyrics sync: the facade (plan docs/plans/wavee/ai-lyrics-sync-implementation.md)
//
// Role: HOST
//
// Turns line-synced (or plain) lyrics into word-by-word lyrics while a song plays, on the NPU of a Copilot+ PC.
// Nothing leaves the PC; the only network use is the one-time model download.

namespace Wavee;

public static partial class AiLyrics
{
    /// <summary>The provider id of a document whose timing this feature generated.</summary>
    public const string ProviderId = "wavee-ai";

    /// <summary>Bumped when the models or the alignment change in a way that invalidates saved results.</summary>
    public const int PackVersion = 1;

    public static string Root => Path.Combine(Platform.LocalFolder, "ai", "lyrics");
    public static string RuntimeDir => Path.Combine(Root, "runtime");
    public static string ModelsDir => Path.Combine(Root, "models");
    public static string ResultsDir => Path.Combine(Root, "results");
    public static string PartialDir => Path.Combine(Root, ".partial");
}
