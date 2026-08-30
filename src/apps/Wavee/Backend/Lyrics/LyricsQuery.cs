using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Wavee.Backend.Lyrics;

/// <summary>Builds the ordered, deduped search-query variants a metadata-matched lyric source tries in turn (most
/// specific first): full "title artist", then a feat-stripped "title artist", then feat-stripped "title". Mirrors
/// WaveeMusic's Lyricify fallback ladder (docs/lyrics-aggregator-reranker-plan.md) — the single biggest lever on match
/// rate, since one keyword per source finds far fewer songs. Pure/stateless. <see cref="Normalize"/> is the SEARCH-text
/// cleanup (full-width→ASCII, curly quotes, " - " separators) — distinct from the reranker's token-level LyricsText.Normalize.</summary>
public static class LyricsQuery
{
    // Featured-artist clause in brackets: "(feat. X)", "[ft X]", "(featuring X)". \b after the keyword so "feature" is
    // NOT matched (no boundary before 'u'); the keyword's "featuring" branch covers that word.
    static readonly Regex FeatBracket = new(@"[([{]\s*(?:feat\.?|featuring|ft\.?)\b[^)\]}]*[)\]}]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // Trailing dash-introduced feat: " - feat. X", " – ft X".
    static readonly Regex FeatDashTail = new(@"\s*[-–—]\s*(?:feat\.?|featuring|ft\.?)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    static readonly Regex MultiSpace = new(@"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Joined "title artist" keyword variants, most specific first, normalized + deduped (a no-feat title
    /// collapses level 0/1 → 2 variants). Never empty when the title is non-empty.</summary>
    public static IReadOnlyList<string> Variants(LyricsRequest req)
    {
        string title = req.Title ?? "";
        string artist = req.PrimaryArtist;
        string stripped = StripFeat(title);
        return Dedup(new[]
        {
            Join(title, artist),       // V0: full "title artist"
            Join(stripped, artist),    // V1: feat-stripped "title artist"
            Normalize(stripped),       // V2: feat-stripped title only
        });
    }

    /// <summary>The same three levels as (Title, Artist) PAIRS (pre-join) for split-field sources (LRCLIB
    /// track_name/artist_name, Musixmatch q_track/q_artist). Each component normalized; deduped on the pair.</summary>
    public static IReadOnlyList<(string Title, string Artist)> TitleArtistVariants(LyricsRequest req)
    {
        string title = Normalize(req.Title ?? "");
        string artist = Normalize(req.PrimaryArtist);
        string stripped = Normalize(StripFeat(req.Title ?? ""));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outv = new List<(string, string)>(3);
        foreach (var (t, a) in new[] { (title, artist), (stripped, artist), (stripped, "") })
            if (t.Length > 0 && seen.Add(t + "" + a)) outv.Add((t, a));
        return outv;
    }

    /// <summary>Remove featured-artist clauses (bracketed "(feat. …)" and trailing " - feat. …"); whitespace-collapsed.</summary>
    public static string StripFeat(string title)
    {
        if (string.IsNullOrEmpty(title)) return title ?? "";
        string s = FeatBracket.Replace(title, "");
        s = FeatDashTail.Replace(s, "");
        return MultiSpace.Replace(s, " ").Trim();
    }

    /// <summary>Search-safe normalization: full-width forms → ASCII, ideographic space → space, curly quotes/dashes →
    /// straight, " - " separator → " ", collapse whitespace, trim. Case is PRESERVED (this is a query, not a comparison).
    /// Traditional→Simplified (ToSC) is deliberately deferred (needs a large mapping table; not the Western/Korean bottleneck).</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            char n = c;
            if (c >= '！' && c <= '～') n = (char)(c - 0xFEE0);   // full-width ASCII block → ASCII
            else if (c == '　') n = ' ';                             // ideographic space
            else switch (c)
            {
                case '‘' or '’': n = '\''; break;              // curly single quotes → '
                case '“' or '”': n = '"'; break;              // curly double quotes → "
                case '–' or '—': n = '-'; break;              // en/em dash → -
            }
            sb.Append(n);
        }
        string r = sb.ToString().Replace(" - ", " ");
        return MultiSpace.Replace(r, " ").Trim();
    }

    static string Join(string title, string artist)
    {
        string t = Normalize(title);
        string a = Normalize(artist);
        return a.Length > 0 ? (t + " " + a).Trim() : t;
    }

    static IReadOnlyList<string> Dedup(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outv = new List<string>();
        foreach (var s in items)
            if (s.Length > 0 && seen.Add(s)) outv.Add(s);
        return outv;
    }
}
