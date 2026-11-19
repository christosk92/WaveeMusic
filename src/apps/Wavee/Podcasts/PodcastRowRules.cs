// ── Podcasts/PodcastRowRules.cs — what an episode row leaves out (podcast pages: "only what differs") ───────────────────────────────────────────
//
// A show's rows repeat themselves: every title opens on the show's own name and "EP 97 -", every description on the same greeting. A row says each
// once. The number is its NUMERAL, the title is what follows it, and the one line of description starts after the opening the episodes share.
//
//   "THE POG STATE | EP 97 - LCK Goes to Worlds 2026"   →  numeral "97", title "LCK Goes to Worlds 2026"
//   "#9 · Edge Cases" (the wire says number 9)           →  numeral "9",  title "Edge Cases"
//   "Essentials: Sleep Toolkit"                          →  no numeral, the title as written (nothing here is the show's own)
//   "Welcome back, Poggers! With Worlds 2026 …" ×N       →  the blurb starts at "With Worlds 2026 …"
//
// Every rule reads ONE episode (its title, the show's name, the wire's number), so a row never changes when more of the show loads; only the shared
// opening is read across the loaded descriptions. Pure.

using System.Globalization;

namespace Wavee;

/// <summary>An episode row's numeral, its short title and its blurb.</summary>
internal static class PodcastRowRules
{
    const char NoBreakSpace = (char)0x00A0, MiddleDot = (char)0x00B7, EnDash = (char)0x2013, EmDash = (char)0x2014;
    /// <summary>A shared opening is a short sentence: at least this long (a lone "Hi." is noise), at most this long.</summary>
    const int LeadMin = 8, LeadMax = 80;
    /// <summary>An opening is shared when this many episodes start on it, and at least half of those that have a description.</summary>
    const int LeadEpisodes = 3;
    const int NumeralDigits = 5;

    /// <summary>The episode's numeral and the title beside it. <paramref name="number"/> is the wire's own episode number (0 = none said): it wins
    /// over a number read from the title. Returns ("", title) when the title says nothing the show repeats.</summary>
    public static (string Numeral, string Title) Split(string title, string showTitle, int number)
    {
        if (string.IsNullOrWhiteSpace(title)) return ("", title ?? "");
        var s = title.AsSpan();
        int i = 0;
        while (i < s.Length && IsSpace(s[i])) i++;

        // The show's own name at the head of the title, when something follows it.
        int afterName = -1;
        bool namePunctuated = false;
        if (showTitle.Length > 0 && s.Length - i > showTitle.Length && s[i..].StartsWith(showTitle, StringComparison.OrdinalIgnoreCase)
            && !char.IsLetterOrDigit(s[i + showTitle.Length]))
        {
            int j = i + showTitle.Length;
            namePunctuated = SkipSeparators(s, ref j, bars: true);
            if (j < s.Length) afterName = j;
        }

        int k = afterName >= 0 ? afterName : i;
        Marker marker = SkipMarker(s, ref k);
        long value = 0;
        int digits = 0;
        while (k < s.Length && char.IsAsciiDigit(s[k]) && digits < NumeralDigits) { value = value * 10 + (s[k] - '0'); k++; digits++; }
        bool numbered = digits > 0 && value > 0 && (k == s.Length || !char.IsLetterOrDigit(s[k]));
        if (numbered)
        {
            int r = k;
            bool punctuated = SkipSeparators(s, ref r, bars: false);
            // A number the title only happens to open on ("1983 Days", "Ep 5 Things We Learned") is its words. It is the episode's own after
            // "#"; after "EP" / "Episode" where the show's name stands before it or punctuation after it ("EP 97 - Title"); and bare only
            // between the show's name and punctuation ("The Show 97 - Title").
            bool ours = marker switch
            {
                Marker.Hash => r > k,
                Marker.Word => r > k && (punctuated || afterName >= 0),
                _ => afterName >= 0 && punctuated,
            };
            if (ours && r < s.Length) return (FormatInt(value), title[r..]);
        }

        // No number of its own in the title: the wire's number (the title's "#9 ·" taken off), and the show's name where punctuation ends it.
        string rest = afterName >= 0 && namePunctuated ? title[afterName..] : title;
        if (number > 0) return (FormatInt(number), Episode.TitleSansNumber(rest, number));
        return ("", rest);
    }

    /// <summary>The opening sentence the show's episodes share ("Welcome back, Poggers!"), or "" when they share none.</summary>
    public static string SharedLead(string[] descriptions)
    {
        Dictionary<string, int>? counts = null;
        int described = 0;
        foreach (string d in descriptions)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            described++;
            string first = FirstSentence(d);
            if (first.Length == 0) continue;
            counts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            counts[first] = counts.TryGetValue(first, out int c) ? c + 1 : 1;
        }
        if (counts is null) return "";
        string best = "";
        int most = 0;
        foreach (var (lead, count) in counts)
            if (count > most) { most = count; best = lead; }
        return most >= LeadEpisodes && most * 2 >= described ? best : "";
    }

    /// <summary><paramref name="description"/> without the shared opening <paramref name="lead"/>; "" when it does not open on it or nothing
    /// follows it (the caller then shows the description whole).</summary>
    public static string BlurbOf(string description, string lead)
    {
        if (lead.Length == 0 || string.IsNullOrEmpty(description)) return "";
        var d = description.AsSpan().TrimStart();
        if (!d.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) return "";
        var rest = d[lead.Length..].TrimStart();
        return rest.IsEmpty ? "" : rest.ToString();
    }

    /// <summary>The description's first sentence when it is short and more follows it; "" otherwise.</summary>
    static string FirstSentence(string description)
    {
        var d = description.AsSpan().TrimStart();
        int limit = Math.Min(d.Length, LeadMax);
        for (int i = 0; i < limit; i++)
        {
            if (d[i] is not ('.' or '!' or '?')) continue;
            int end = i + 1;
            if (end >= d.Length) return "";
            // The sentence ends where a space or a capital follows ("Poggers!With the Worlds…" is a real opening without its space).
            if (!IsSpace(d[end]) && !char.IsUpper(d[end])) continue;
            return end >= LeadMin ? d[..end].ToString() : "";
        }
        return "";
    }

    enum Marker : byte { None, Word, Hash }

    /// <summary>Steps over "#", or over "EP", "Ep.", "Episode" and the space after it, when a number follows; says which stood there.</summary>
    static Marker SkipMarker(ReadOnlySpan<char> s, ref int i)
    {
        int j = i;
        if (j + 1 < s.Length && s[j] == '#' && char.IsAsciiDigit(s[j + 1])) { i = j + 1; return Marker.Hash; }
        ReadOnlySpan<string> words = ["episode", "ep"];
        foreach (string w in words)
        {
            if (s.Length - j <= w.Length || !s[j..].StartsWith(w, StringComparison.OrdinalIgnoreCase)) continue;
            int e = j + w.Length;
            // The word must END there ("Epic 5" is a title): a dot, a space, "#" or the number itself follows it.
            if (e < s.Length && s[e] == '.') e++;
            while (e < s.Length && IsSpace(s[e])) e++;
            if (e < s.Length && s[e] == '#') e++;
            if (e < s.Length && char.IsAsciiDigit(s[e])) { i = e; return Marker.Word; }
        }
        return Marker.None;
    }

    /// <summary>Steps over spaces and separators; true when a real separator stood among them (not only spaces). With <paramref name="bars"/> a
    /// lone capital I or small l between spaces counts as one: publishers type it for the bar ("THE SHOW I EP 97").</summary>
    static bool SkipSeparators(ReadOnlySpan<char> s, ref int i, bool bars)
    {
        bool punctuated = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (IsSpace(c)) { i++; continue; }
            if (c is '-' or ':' or '.' or '|' or MiddleDot or EnDash or EmDash) { punctuated = true; i++; continue; }
            if (bars && c is 'I' or 'l' && i > 0 && IsSpace(s[i - 1]) && i + 1 < s.Length && IsSpace(s[i + 1])) { punctuated = true; i++; continue; }
            break;
        }
        return punctuated;
    }

    static bool IsSpace(char c) => c is ' ' or NoBreakSpace or '\t';

    static string FormatInt(long value) => value.ToString(CultureInfo.InvariantCulture);
}
