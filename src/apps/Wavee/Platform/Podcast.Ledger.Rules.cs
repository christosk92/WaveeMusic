// ── Platform/Podcast.Ledger.Rules.cs ───────────────────────────────────────────────────────────────────────────────────
// the played ledger's PURE geometry: the three part weights, the metrics every ledger draws with, and where the flex puts
// the three parts in a bar of a given width (the slide's "from" and "to")
//
// Role: CORE
// Spec: docs/themes/plan/technical-plan.md §5.1 C16 (X46); podcast-show-rework-implementation.md §5.5 (the ledger), §7 (motion)
//
// Moved out of `Controls.Podcast.cs` unchanged (`LedgerPartsOf`, `LedgerGeometry` and the five constants): the ledger
// view keeps drawing exactly what it drew, but the arithmetic a theme's ledger view needs lives here, engine-free, so a
// view in any theme asks the same questions. Nothing here reads a signal, a node or a clock.

namespace Wavee;

public static class PodcastLedgerRules
{
    /// <summary>The ledger bar: 4 high, 2 between parts, every part at least 2 wide.</summary>
    public const float Height = 4f, Gap = 2f, MinPart = 2f;

    /// <summary>The in-progress part's tone (the prototype's 45 % mix) and the floor a part's weight never drops under
    /// (so an empty part keeps its 2-DIP stub and the flex never divides by zero).</summary>
    public const float ProgressTone = 0.45f, Floor = 0.001f;

    /// <summary>The three part weights for the ledger bar: each fraction clamped into what is left of the whole
    /// (progress never overlaps played), the rest to go, every weight floored at <see cref="Floor"/>.</summary>
    public static (float Played, float Progress, float ToGo) PartsOf(float played, float progress)
    {
        float a = Clamp01(played);
        float b = MathF.Min(Clamp01(progress), 1f - a);
        float c = MathF.Max(0f, 1f - a - b);
        return (MathF.Max(Floor, a), MathF.Max(Floor, b), MathF.Max(Floor, c));

        static float Clamp01(float v) => float.IsNaN(v) ? 0f : Math.Clamp(v, 0f, 1f);
    }

    /// <summary>Where the flex puts the three parts in a bar <paramref name="width"/> wide — the FLIP's "from" and "to"
    /// (the layout itself is the engine's flex; this mirrors it closely enough to start a slide from). A part whose share
    /// falls under <see cref="MinPart"/> is pinned at it and the others share what is left, Yoga's freeze-and-
    /// redistribute.</summary>
    public static void Geometry(float width, float played, float progress, float toGo, Span<float> x, Span<float> w)
    {
        Span<float> g = stackalloc float[3];
        g[0] = played; g[1] = progress; g[2] = toGo;
        Span<bool> pinned = stackalloc bool[3];
        float pool = MathF.Max(0f, width - 2f * Gap), sum = g[0] + g[1] + g[2];
        for (int pass = 0; pass < 3; pass++)
        {
            bool changed = false;
            for (int i = 0; i < 3; i++)
            {
                if (pinned[i]) continue;
                float share = sum > 0f ? pool * g[i] / sum : 0f;
                if (share >= MinPart) continue;
                pinned[i] = true; w[i] = MinPart; pool -= MinPart; sum -= g[i]; changed = true;
            }
            if (!changed) break;
        }
        for (int i = 0; i < 3; i++)
            if (!pinned[i]) w[i] = sum > 0f ? MathF.Max(0f, pool) * g[i] / sum : 0f;
        x[0] = 0f;
        x[1] = w[0] + Gap;
        x[2] = x[1] + w[1] + Gap;
    }
}
