namespace Wavee.Local.Enrichment;

/// <summary>
/// Small Levenshtein-based similarity helper. Used by the Spotify match
/// heuristic in <see cref="LocalEnrichmentService.EnrichMusicAsync"/> to
/// decide whether a search hit is close enough to link.
///
/// <para>Operates on already-normalised strings (lowercase, ASCII-folded,
/// parens stripped) — see <c>NormaliseForMatch</c> in the same file.</para>
/// </summary>
internal static class StringSimilarity
{
    /// <summary>
    /// Returns a value in [0, 1] where 1.0 = identical and 0.0 = entirely
    /// different. <c>1 - (distance / max-length)</c>. Both empty strings
    /// return 1.0; one empty + one non-empty returns 0.0.
    /// </summary>
    public static double Ratio(string a, string b)
    {
        if (a is null || b is null) return 0;
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;

        var d = Levenshtein(a, b);
        var max = System.Math.Max(a.Length, b.Length);
        return 1.0 - ((double)d / max);
    }

    /// <summary>Standard two-row Levenshtein. O(a.Length × b.Length) time,
    /// O(min) space. Cheap for track-title-sized inputs (typically &lt; 80 chars).</summary>
    private static int Levenshtein(string a, string b)
    {
        // Ensure b is the shorter — minimises the working buffer.
        if (a.Length < b.Length) (a, b) = (b, a);

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = System.Math.Min(
                    System.Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
