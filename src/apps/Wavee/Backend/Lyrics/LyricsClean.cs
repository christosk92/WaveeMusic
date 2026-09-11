using System;
using System.Collections.Generic;
using System.Text;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>The ONE owner of "is this row actually a lyric?".
///
/// <para>Every provider ships non-lyric rows inside its lyric document, and they do two kinds of damage. On screen they
/// render as blank gaps, bare ♪ glyphs, writer credits and a fake first line carrying the song's own title. In the
/// reranker they inflate the line COUNT that <c>coverage</c> is computed from (min/max), which punishes a provider for
/// what its rival padded with: measured on "Caribbean Queen", Spotify sends 62 lines of which 23 are ♪ or blank, so a
/// Kugou KRC matching 34 of its 35 lines scored coverage 0.565. Cleaned, the same pair scores text 1.000 / coverage
/// 0.872. Issue #16 is the credit flavour: Kugou's "Close to Me" opens with three syllable-timed 词：/曲：/编曲： rows
/// (text 0.94, coverage 0.74 against Spotify's 31 lines) that were shown as sung lines.</para>
///
/// <para>Applied at a single chokepoint (<see cref="AggregatingLyricsProvider"/>'s per-source fetch), so every
/// provider — INCLUDING the Spotify document used as the comparison reference — is cleaned by the same rule and both
/// sides of every comparison stay consistent. The credit judgement itself is <see cref="LyricsCreditRules"/>, whose
/// three tiers are trusted in this order: (1) structural — the parsers already refuse the rows the format marks as
/// metadata, and this pass catches a tag that slipped into a timed row; (2) reference alignment — needs the reference,
/// so it runs at the rank site (<see cref="LyricsCreditRules.TrimUnalignedEdges"/>), not here; (3) grammar — applied
/// here, positionally, because at fetch time no reference has arrived yet and the disk-cache path never has one.</para>
/// </summary>
public static class LyricsClean
{
    /// <summary>Token overlap with the track's own title+artist above which a LEADING line is the provider's title
    /// header rather than a lyric.</summary>
    const double HeaderOverlap = 0.8;
    /// <summary>…and how far the real lyrics must start after it, when the line carries no " - " separator. A header is
    /// a pre-roll; a chorus line that happens to be the song's title is not.</summary>
    const long HeaderGapMs = 3000;

    /// <summary>True for a row that carries no readable text at all — "", "♪", "...", "—", "***", "//".
    /// <see cref="LyricsText.Normalize"/> already strips every punctuation and symbol codepoint, so an empty
    /// normalization IS the test; there is deliberately no second hand-written character list to drift from it.</summary>
    public static bool IsSymbolOnly(string? text) => LyricsText.Normalize(text ?? "").Length == 0;

    /// <summary>Strip the non-lyric rows. <paramref name="title"/>/<paramref name="artists"/> are the track's own
    /// metadata and enable the header rule; omit them (the disk-cache path, which by design never resolves the track)
    /// and the other families still apply.</summary>
    public static LyricsDocument Apply(LyricsDocument doc, string? title = null, string? artists = null)
        => Apply(doc, title, artists, out _);

    /// <summary>As <see cref="Apply(LyricsDocument, string?, string?)"/>, also reporting how many of the dropped rows
    /// were credit headers (for the probe note).</summary>
    public static LyricsDocument Apply(LyricsDocument doc, string? title, string? artists, out int credits)
    {
        credits = 0;
        int n = doc.Lines.Count;
        if (n == 0) return doc;

        var drop = new bool[n];

        // ── 1. rows with no lyric in them, anywhere in the document ─────────────────────────────────────────────────
        // Symbol-only/empty filler, a format tag that leaked into a timed row ([offset:…]), and the provider's own
        // boilerplate sentences. An INSTRUMENTAL notice is the provider saying the song has no lyrics at all: the whole
        // document goes (the caller turns an empty result into a miss) — NetEase pairs that sentence with writer
        // credits, which would otherwise be displayed as the lyrics of an instrumental.
        for (int i = 0; i < n; i++)
        {
            string text = doc.Lines[i].Text;
            if (LyricsCreditRules.IsInstrumentalNotice(text)) return doc with { Lines = Array.Empty<LyricLine>() };
            if (IsSymbolOnly(text) || LyricsCreditRules.IsStructuralMetadata(text) || LyricsCreditRules.IsProviderBoilerplate(text))
                drop[i] = true;
        }

        // ── 2. credits by grammar, positionally (LyricsCreditRules tier 3) ──────────────────────────────────────────
        // Credits sit at the top and bottom of a document. In the LEADING and TRAILING runs any credit-shaped line goes;
        // in the MIDDLE only a KNOWN key with a full-width colon does ("作曲：X" is not a lyric in any language, whereas
        // "Girl: I told you" is). That is stricter than Lyricify/BetterLyrics, which filter the whole document by text
        // match and therefore have to ship the feature switched off by default.
        var credit = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (drop[i]) continue;                                                       // filler does not end the run
            if (!LyricsCreditRules.LooksLikeCreditLine(doc.Lines[i].Text, out _, out _)) break;   // first real lyric
            credit[i] = true;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            if (drop[i] || credit[i]) continue;
            if (!LyricsCreditRules.LooksLikeCreditLine(doc.Lines[i].Text, out _, out _)) break;
            credit[i] = true;
        }
        for (int i = 0; i < n; i++)
        {
            if (drop[i] || credit[i]) continue;
            if (LyricsCreditRules.LooksLikeCreditLine(doc.Lines[i].Text, out bool known, out bool fullWidth) && known && fullWidth)
                credit[i] = true;
        }
        // Credits alone never empty a document: if every surviving line is credit-shaped, the shape is the document's
        // idiom rather than a header, and the grammar is the thing that is wrong. Keep them all.
        int survivors = 0;
        for (int i = 0; i < n; i++) if (!drop[i] && !credit[i]) survivors++;
        if (survivors > 0)
            for (int i = 0; i < n; i++) if (credit[i]) { drop[i] = true; credits++; }

        // ── 3. the provider's title header (Kugou / QQ / NetEase convention) ─────────────────────────────────────────
        // Both edges: a search-matched document can carry the same "Title - Artist" row at the top (the common case)
        // or, less often, repeated at the bottom after the last sung line.
        int first = FirstKept(drop);
        if (first >= 0 && IsTitleHeader(doc, first, drop, title, artists, leading: true)) drop[first] = true;
        int last = LastKept(drop);
        if (last >= 0 && IsTitleHeader(doc, last, drop, title, artists, leading: false)) drop[last] = true;

        int kept = 0;
        foreach (bool d in drop) if (!d) kept++;
        if (kept == n) return doc;

        // ── rebuild, FOLDING each dropped row's timestamp into the line above it ─────────────────────────────────────
        // A ♪ or blank row is where the previous line stops being sung. Dropping it without carrying that over would
        // stretch the preceding line across the whole instrumental — the "previous line is still fully active" failure
        // LyricsView guards against — and would erase the very gap the interlude dots are detected from. A kept line is
        // carried over untouched (its start, end and syllables); a dropped syllable-timed row takes its syllables with
        // it, so a leading credit run simply moves the document's first line to the first real lyric.
        var lines = new List<LyricLine>(kept);
        for (int i = 0; i < n; i++)
        {
            if (!drop[i]) { lines.Add(doc.Lines[i]); continue; }
            if (lines.Count == 0) continue;                          // nothing above it to carry the end to
            int lastIdx = lines.Count - 1;
            if (lines[lastIdx].EndMs is null) lines[lastIdx] = lines[lastIdx] with { EndMs = doc.Lines[i].StartMs };
        }
        return doc with { Lines = lines };
    }

    static int FirstKept(bool[] drop)
    {
        for (int i = 0; i < drop.Length; i++) if (!drop[i]) return i;
        return -1;
    }

    static int LastKept(bool[] drop)
    {
        for (int i = drop.Length - 1; i >= 0; i--) if (!drop[i]) return i;
        return -1;
    }

    /// <summary>True when the KEPT row at <paramref name="index"/> (the document's first or last surviving line, per
    /// <paramref name="leading"/>) is a "Title (annotation) - Artist" header rather than a lyric: a CJK provider's own
    /// bracketed remark — a franchise/TV-tie-in note in <c>《…》</c>, a parenthetical, <c>【…】</c> — sits alongside the
    /// title on the very row this trims (this exact shape shipped a Chinese TV-insert credit,
    /// <c>The Night We Met (《十三个原因 第一季》电视剧插曲) - Lord Huron</c>, as track "Ohoo…"'s opening line). The
    /// annotation's own words are never part of the track's title/artist metadata, so counting them toward the row's
    /// token total used to drag the overlap ratio below <see cref="HeaderOverlap"/> and let the header through —
    /// <see cref="StripBracketedAnnotations"/> removes that bracketed content before the ratio is judged, exactly as a
    /// reader would ignore a parenthetical aside when checking whether a line "is" the title.</summary>
    static bool IsTitleHeader(LyricsDocument doc, int index, bool[] drop, string? title, string? artists, bool leading)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        string rawText = doc.Lines[index].Text;
        string[] lineTokens = Tokens(StripBracketedAnnotations(rawText));
        if (lineTokens.Length == 0) return false;
        var meta = new HashSet<string>(Tokens(title + " " + (artists ?? "")), StringComparer.Ordinal);
        if (meta.Count == 0) return false;

        int hits = 0;
        foreach (string t in lineTokens) if (meta.Contains(t)) hits++;
        if (hits / (double)lineTokens.Length < HeaderOverlap) return false;

        // Corroboration, so a chorus/outro line that IS the song's title survives. A header always carries the
        // "Title - Artist" separator when it repeats at the BOTTOM (providers duplicate the literal header string, never
        // a bare title) — that is the only signal trusted there. At the TOP a header can also be a bare pre-roll with no
        // separator, corroborated instead by sitting well before the singing starts; that heuristic does NOT carry over
        // to the trailing edge, where an isolated last line after an instrumental outro is a completely ordinary way for
        // a song to end on its own hook.
        if (rawText.Contains(" - ", StringComparison.Ordinal)) return true;
        if (!leading) return false;
        for (int j = index + 1; j < doc.Lines.Count; j++)
        {
            if (drop[j]) continue;
            return doc.Lines[j].StartMs - doc.Lines[index].StartMs >= HeaderGapMs;
        }
        return false;   // it is the only kept line — keep it rather than empty the document
    }

    static string[] Tokens(string text)
    {
        string n = LyricsText.Normalize(text);
        return n.Length == 0 ? Array.Empty<string>() : n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    // A CJK provider's header row commonly reads "Title (annotation) - Artist" where the annotation is a
    // franchise/soundtrack credit that shares NO words with the track's own title/artist — brackets of every family the
    // sources actually ship: half-width/full-width parens, 《book title marks》 (TV/film/album tie-ins), 【lenticular
    // brackets】, square brackets, and a bare "(feat. X)" aside. Removed WHOLE (open through its matching close) so the
    // annotation's words never dilute the header-overlap ratio in <see cref="IsTitleHeader"/>; unbalanced brackets are
    // left untouched rather than risk eating real lyric text past an unmatched opener.
    static readonly (char Open, char Close)[] BracketPairs =
    {
        ('(', ')'), ('（', '）'), ('《', '》'), ('【', '】'), ('[', ']'),
    };

    static bool IsBracketOpen(char c, out char close)
    {
        foreach (var (open, cl) in BracketPairs) if (c == open) { close = cl; return true; }
        close = '\0';
        return false;
    }

    internal static string StripBracketedAnnotations(string text)
    {
        bool anyBracket = false;
        foreach (char c in text) if (IsBracketOpen(c, out _)) { anyBracket = true; break; }
        if (!anyBracket) return text;

        var sb = new StringBuilder(text.Length);
        var closers = new Stack<char>();
        foreach (char c in text)
        {
            if (IsBracketOpen(c, out char close)) { closers.Push(close); continue; }
            if (closers.Count > 0)
            {
                if (c == closers.Peek()) closers.Pop();
                continue;   // still inside (or just closed) a bracketed run — its text never reaches the output
            }
            sb.Append(c);
        }
        // Unbalanced: a bracket never closed, so we cannot tell where the annotation was meant to end. Keep the
        // ORIGINAL text rather than silently swallow whatever followed the opener as if it were annotation.
        return closers.Count == 0 ? sb.ToString() : text;
    }
}
