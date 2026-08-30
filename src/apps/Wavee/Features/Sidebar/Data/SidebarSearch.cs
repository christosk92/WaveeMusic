using System.Globalization;

namespace Wavee;

// The library-ONLY sidebar search matcher (F.7.8). Engine-free (System only), source-included by src/apps/Wavee.Tests.
//
// WHY THERE ARE TWO PATHS: F.7.8 specifies a culture-aware, diacritics-insensitive, allocation-free substring match via
// CompareInfo.IndexOf(IgnoreCase | IgnoreNonSpace). Wavee builds with <InvariantGlobalization>true</InvariantGlobalization>
// (src/apps/Directory.Build.props), and in invariant mode ICU is absent: IgnoreNonSpace either throws
// PlatformNotSupportedException or silently degrades to an ordinal comparison, so "cafe" would stop matching "Café" in the
// SHIPPING configuration. Rather than promise a behaviour the config cannot deliver, the matcher probes the capability once
// and falls back to a hand-folded scan that is equally allocation-free and still diacritics-insensitive across the Latin-1
// range. Both paths are case-insensitive and neither ever allocates.

public static class SidebarSearch
{
    /// <summary>True when the runtime's collator can do the diacritics-insensitive compare itself (ICU present). False
    /// under globalization-invariant mode, where the folded fallback below handles it.</summary>
    public static readonly bool CollatorFoldsDiacritics = ProbeCollator();

    const CompareOptions CollatorOpts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    static bool ProbeCollator()
    {
        try { return CultureInfo.InvariantCulture.CompareInfo.IndexOf("é", "e", CollatorOpts) >= 0; }
        catch (System.PlatformNotSupportedException) { return false; }   // invariant mode rejects IgnoreNonSpace outright
        catch (System.ArgumentException) { return false; }
    }

    /// <summary>Library-ONLY match (locked decision 10 — the sidebar search never hits the network and never reaches
    /// beyond the projected entries). Case- and diacritics-insensitive substring, ALLOCATION-FREE: it scans the live
    /// strings, with no <c>ToLower</c>/<c>Normalize</c> copies.
    ///
    /// <see cref="SidebarLibraryEntry.Name"/> always participates. <see cref="SidebarLibraryEntry.Creator"/> participates
    /// only for queries of 2+ characters, so a single letter does not match every album by an artist whose name contains
    /// it. The query must already be trimmed by the caller (once per keystroke, not once per row).</summary>
    public static bool Matches(in SidebarLibraryEntry e, string query)
        => Matches(e.Name, e.Creator, query);

    /// <summary>The same match against a bare (name, creator) pair — used by rows that are not projected entries (a pin
    /// whose entity has not resolved yet still has to survive the filter rather than vanish).</summary>
    public static bool Matches(string? name, string? creator, string query)
    {
        if (query.Length == 0) return true;
        if (name is { Length: > 0 } && Contains(name, query)) return true;
        return query.Length >= 2 && creator is { Length: > 0 } && Contains(creator, query);
    }

    /// <summary>Normalize a raw search box value into the query the matcher expects (trimmed, never null). Called ONCE
    /// per keystroke by the surface, never per row.</summary>
    public static string Normalize(string? raw) => raw is null ? "" : raw.Trim();

    /// <summary>One substring test, on whichever path this runtime supports.</summary>
    public static bool Contains(string haystack, string needle)
    {
        if (needle.Length == 0) return true;
        if (needle.Length > haystack.Length) return false;
        if (CollatorFoldsDiacritics)
            return CultureInfo.CurrentUICulture.CompareInfo.IndexOf(haystack, needle, CollatorOpts) >= 0;
        return FoldedContains(haystack, needle);
    }

    // ── the invariant-mode fallback: a folded two-pointer scan (no allocations, no culture data) ───────────────────────
    static bool FoldedContains(string hay, string needle)
    {
        int n = needle.Length, last = hay.Length - n;
        for (int i = 0; i <= last; i++)
        {
            int k = 0;
            while (k < n && Fold(hay[i + k]) == Fold(needle[k])) k++;
            if (k == n) return true;
        }
        return false;
    }

    // Latin-1 Supplement (U+00C0..U+00FF) folded to its base letter, lowercased. Beyond that range the fold is
    // case-only: Latin Extended-A and non-Latin scripts keep their marks (documented limit — the alternative is a full
    // Unicode decomposition table, which is exactly the ICU data invariant mode excludes).
    const string Latin1Fold =
        "aaaaaaaceeeeiiiidnooooo×ouuuuyþs" +
        "aaaaaaaceeeeiiiidnooooo÷ouuuuyþy";

    static char Fold(char c)
    {
        if (c < 128) return (uint)(c - 'A') <= 'Z' - 'A' ? (char)(c + 32) : c;
        if (c >= 'À' && c <= 'ÿ') return Latin1Fold[c - 'À'];
        return char.ToLowerInvariant(c);
    }
}
