using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Lyrics;

/// <summary>The ONE owner of "is this row actually a lyric?".
///
/// <para>Every provider ships non-lyric rows inside its lyric document, and they do two kinds of damage. On screen they
/// render as blank gaps, bare ♪ glyphs and a fake first line carrying the song's own title. In the reranker they inflate
/// the line COUNT that <c>coverage</c> is computed from (min/max), which punishes a provider for what its rival padded
/// with: measured on "Caribbean Queen", Spotify sends 62 lines of which 23 are ♪ or blank, so a Kugou KRC matching 34 of
/// its 35 lines scored coverage 0.565. Cleaned, the same pair scores text 1.000 / coverage 0.872.</para>
///
/// <para>Three families, one pass. Applied at a single chokepoint (<see cref="AggregatingLyricsProvider"/>'s per-source
/// fetch), so every provider — INCLUDING the Spotify document used as the comparison reference — is cleaned by the same
/// rule and both sides of every comparison stay consistent.</para>
/// </summary>
public static class LyricsClean
{
    /// <summary>Token overlap with the track's own title+artist above which a LEADING line is the provider's title
    /// header rather than a lyric.</summary>
    const double HeaderOverlap = 0.8;
    /// <summary>…and how far the real lyrics must start after it, when the line carries no " - " separator. A header is
    /// a pre-roll; a chorus line that happens to be the song's title is not.</summary>
    const long HeaderGapMs = 3000;

    /// <summary>True for a row that carries no readable text at all — "", "♪", "...", "—". <see cref="LyricsText.Normalize"/>
    /// already strips every punctuation and symbol codepoint, so an empty normalization IS the test; there is deliberately
    /// no second hand-written character list to drift from it.</summary>
    public static bool IsSymbolOnly(string? text) => LyricsText.Normalize(text ?? "").Length == 0;

    /// <summary>Strip the non-lyric rows. <paramref name="title"/>/<paramref name="artists"/> are the track's own
    /// metadata and enable the header rule; omit them (the disk-cache path, which by design never resolves the track)
    /// and the other two families still apply.</summary>
    public static LyricsDocument Apply(LyricsDocument doc, string? title = null, string? artists = null)
    {
        int n = doc.Lines.Count;
        if (n == 0) return doc;

        var drop = new bool[n];

        // ── 1. symbol-only / empty, anywhere in the document ─────────────────────────────────────────────────────────
        for (int i = 0; i < n; i++)
            if (IsSymbolOnly(doc.Lines[i].Text)) drop[i] = true;

        // ── 2. credits, in the LEADING and TRAILING runs only ────────────────────────────────────────────────────────
        // Credits sit at the top and bottom of a document; restricting the sweep to those runs makes a mid-song false
        // positive structurally impossible. That is stricter than Lyricify/BetterLyrics, which filter the whole document
        // by text match and therefore have to ship the feature switched off by default.
        for (int i = 0; i < n; i++)
        {
            if (drop[i]) continue;                                  // already-dropped filler does not end the run
            if (!LyricsText.IsCreditLine(doc.Lines[i].Text)) break; // first real lyric ⇒ the leading run is over
            drop[i] = true;
        }
        for (int i = n - 1; i >= 0; i--)
        {
            if (drop[i]) continue;
            if (!LyricsText.IsCreditLine(doc.Lines[i].Text)) break;
            drop[i] = true;
        }

        // ── 3. the provider's title header (Kugou / QQ / NetEase convention) ─────────────────────────────────────────
        int first = FirstKept(drop);
        if (first >= 0 && IsTitleHeader(doc, first, drop, title, artists)) drop[first] = true;

        int kept = 0;
        foreach (bool d in drop) if (!d) kept++;
        if (kept == n) return doc;

        // ── rebuild, FOLDING each dropped row's timestamp into the line above it ─────────────────────────────────────
        // A ♪ or blank row is where the previous line stops being sung. Dropping it without carrying that over would
        // stretch the preceding line across the whole instrumental — the "previous line is still fully active" failure
        // LyricsView guards against — and would erase the very gap the interlude dots are detected from.
        var lines = new List<LyricLine>(kept);
        for (int i = 0; i < n; i++)
        {
            if (!drop[i]) { lines.Add(doc.Lines[i]); continue; }
            if (lines.Count == 0) continue;                          // nothing above it to carry the end to
            int last = lines.Count - 1;
            if (lines[last].EndMs is null) lines[last] = lines[last] with { EndMs = doc.Lines[i].StartMs };
        }
        return doc with { Lines = lines };
    }

    static int FirstKept(bool[] drop)
    {
        for (int i = 0; i < drop.Length; i++) if (!drop[i]) return i;
        return -1;
    }

    static bool IsTitleHeader(LyricsDocument doc, int index, bool[] drop, string? title, string? artists)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;

        string[] lineTokens = Tokens(doc.Lines[index].Text);
        if (lineTokens.Length == 0) return false;
        var meta = new HashSet<string>(Tokens(title + " " + (artists ?? "")), StringComparer.Ordinal);
        if (meta.Count == 0) return false;

        int hits = 0;
        foreach (string t in lineTokens) if (meta.Contains(t)) hits++;
        if (hits / (double)lineTokens.Length < HeaderOverlap) return false;

        // Corroboration, so a chorus line that IS the song's title survives: a header either carries the
        // "Title - Artist" separator, or sits well before the singing starts.
        if (doc.Lines[index].Text.Contains(" - ", StringComparison.Ordinal)) return true;
        for (int j = index + 1; j < doc.Lines.Count; j++)
        {
            if (drop[j]) continue;
            return doc.Lines[j].StartMs - doc.Lines[index].StartMs >= HeaderGapMs;
        }
        return false;   // it is the only line — keep it rather than empty the document
    }

    static string[] Tokens(string text)
    {
        string n = LyricsText.Normalize(text);
        return n.Length == 0 ? Array.Empty<string>() : n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
