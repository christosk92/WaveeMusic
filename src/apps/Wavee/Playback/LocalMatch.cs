// ── Playback/LocalMatch.cs — the keys a spotify:local: uri and a file on disk meet on (#240, plan §3.4) ─────────────────
//
// Matching is Wavee's own design: nothing about local files is synced, so the official client matches on the device and
// how it does so is not on the wire. What the capture fixes is the INPUT: a uri's parts are decoded text (`+` a space,
// `%XX` a UTF-8 byte; `LocalTrackUri.TryParse`), an untagged file's title is its file name without the extension, an
// empty artist or album is empty, and the seconds are the file's length truncated. `LocalIndex.Match` (P1b) walks the
// tiers of §3.4 with these keys; this file is the keys and the tie-break, PURE.
//
// No `string.Normalize`: under InvariantGlobalization on NativeAOT it returns its input unchanged for every form (probed
// 2026-11-23, see LocalMatch.Fold.cs), so the fold is this file's own: the compatibility decomposition of the scripts a
// music library mostly holds (LocalMatch.Fold.cs), then `char.ToUpperInvariant` and `char.GetUnicodeCategory`, which do
// work in that mode. A precomposed letter and its base + combining mark therefore land on the same key, as NFKC would
// have it; the key is decomposed rather than composed, which is the same equivalence.

using System.Buffers;

namespace Wavee;

/// <summary>The matching keys of plan §3.4 and the tie-break among candidates. PURE; safe on any thread.</summary>
public static partial class LocalMatch
{
    /// <summary>Tier 1 key: the compatibility decomposition (NFKD over the folded scripts), upper-invariant, trimmed, inner
    /// whitespace runs collapsed to one space. Control characters count as whitespace.</summary>
    public static string Exact(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        char[] buffer = ArrayPool<char>.Shared.Rent(text.Length * MaxFold + 1);
        try { return new string(buffer, 0, ExactInto(text, buffer)); }
        finally { ArrayPool<char>.Shared.Return(buffer); }
    }

    /// <summary>Tier 2 key: <see cref="Exact"/>, then trailing <c>(…)</c> / <c>[…]</c> groups dropped, <c>FEAT.</c> / <c>FT.</c> /
    /// <c>FEATURING</c> and everything after it dropped, a leading <c>THE </c> dropped, the diacritics removed (every
    /// non-spacing mark of the decomposition), and every character that is not a letter or digit dropped. A text that
    /// would fold to nothing (all punctuation) keeps its <see cref="Exact"/> key, so two such titles stay apart.</summary>
    public static string Loose(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        char[] buffer = ArrayPool<char>.Shared.Rent(text.Length * MaxFold * 2 + 1);
        try
        {
            int n = ExactInto(text, buffer);
            ReadOnlySpan<char> e = buffer.AsSpan(0, n);
            e = DropTrailingGroups(e);
            e = DropFeaturing(e);
            if (e.Length > 4 && e.StartsWith("THE ")) e = e[4..];
            Span<char> into = buffer.AsSpan(n);
            int m = 0;
            foreach (char c in e)
            {
                if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) into[m++] = c;
            }
            return m > 0 ? new string(into[..m]) : new string(buffer, 0, n);
        }
        finally { ArrayPool<char>.Shared.Return(buffer); }
    }

    /// <summary>One map key for a (title, artist) pair: the two keys joined by U+001F, which neither key can contain.</summary>
    public static string Key(string title, string artist) => title + "\u001f" + artist;

    /// <summary>Does a candidate's length agree with the uri's seconds? The uri carries the length TRUNCATED to whole seconds, so the
    /// entry is truncated the same way before the compare. <paramref name="uriSeconds"/> 0 (unknown) or an entry of unknown length
    /// agrees with anything.</summary>
    public static bool DurationAgrees(int uriSeconds, int entryMs, int toleranceSeconds)
        => uriSeconds <= 0 || entryMs <= 0 || Math.Abs(entryMs / 1000 - uriSeconds) <= toleranceSeconds;

    /// <summary>The winner among candidates that share a key, or -1 for none. Order: album equal (<see cref="Exact"/>) first, then the
    /// smallest duration difference (a candidate of unknown length after every known one), then lossless over lossy, then the higher
    /// bitrate, then the ordinal-smallest path (root, then relative path), so the answer never depends on scan order. It does not
    /// filter by tolerance: the walk does (§3.4), before it asks.</summary>
    public static int Pick(ReadOnlySpan<LocalEntry> candidates, string album, int uriSeconds)
    {
        if (candidates.IsEmpty) return -1;
        if (candidates.Length == 1) return 0;
        string wanted = Exact(album);
        int best = 0;
        bool bestAlbum = Exact(candidates[0].Tags.Album) == wanted;
        for (int i = 1; i < candidates.Length; i++)
        {
            bool album_i = Exact(candidates[i].Tags.Album) == wanted;
            if (Better(candidates[i], album_i, candidates[best], bestAlbum, uriSeconds)) { best = i; bestAlbum = album_i; }
        }
        return best;
    }

    /// <summary>Is <paramref name="a"/> a better pick than <paramref name="b"/>?</summary>
    static bool Better(LocalEntry a, bool aAlbum, LocalEntry b, bool bAlbum, int uriSeconds)
    {
        if (aAlbum != bAlbum) return aAlbum;
        long da = DurationDistance(a.Tags.DurationMs, uriSeconds), db = DurationDistance(b.Tags.DurationMs, uriSeconds);
        if (da != db) return da < db;
        bool la = IsLossless(a.Format), lb = IsLossless(b.Format);
        if (la != lb) return la;
        if (a.Tags.BitrateKbps != b.Tags.BitrateKbps) return a.Tags.BitrateKbps > b.Tags.BitrateKbps;
        if (a.RootId != b.RootId) return a.RootId < b.RootId;
        return string.CompareOrdinal(a.RelativePath, b.RelativePath) < 0;
    }

    static long DurationDistance(int entryMs, int uriSeconds)
        => uriSeconds <= 0 ? 0 : entryMs <= 0 ? int.MaxValue : Math.Abs(entryMs / 1000 - uriSeconds);

    static bool IsLossless(Spotify.Audio.Format format) => format is Spotify.Audio.Format.Flac or Spotify.Audio.Format.Flac24 or Spotify.Audio.Format.Pcm;

    // ── the fold ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where each key's decomposition starts in <see cref="FoldValues"/>. Built once from <see cref="FoldLengths"/>.</summary>
    static readonly ushort[] s_foldStarts = BuildStarts();

    static ushort[] BuildStarts()
    {
        ReadOnlySpan<byte> lengths = FoldLengths;
        var starts = new ushort[lengths.Length];
        int at = 0;
        for (int i = 0; i < lengths.Length; i++) { starts[i] = (ushort)at; at += lengths[i]; }
        return starts;
    }

    /// <summary><see cref="Exact"/> into <paramref name="into"/> (at least <c>text.Length × MaxFold</c> long); the length written.</summary>
    static int ExactInto(string text, Span<char> into)
    {
        int n = 0;
        bool pendingSpace = false;
        ReadOnlySpan<char> keys = FoldKeys, values = FoldValues;
        ReadOnlySpan<byte> lengths = FoldLengths;
        foreach (char raw in text)
        {
            int k = raw < ' ' ? -1 : keys.BinarySearch(raw);
            ReadOnlySpan<char> folded = k >= 0 ? values.Slice(s_foldStarts[k], lengths[k]) : new ReadOnlySpan<char>(in raw);
            foreach (char c in folded)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    pendingSpace = n > 0;
                    continue;
                }
                if (pendingSpace) { into[n++] = ' '; pendingSpace = false; }
                into[n++] = char.ToUpperInvariant(c);
            }
        }
        return n;
    }

    /// <summary>Drop trailing <c>(…)</c> and <c>[…]</c> groups, one after another, never down to nothing.</summary>
    static ReadOnlySpan<char> DropTrailingGroups(ReadOnlySpan<char> e)
    {
        while (e.Length > 0 && e[^1] is ')' or ']')
        {
            char open = e[^1] == ')' ? '(' : '[';
            int at = e.LastIndexOf(open);
            if (at <= 0) break;
            ReadOnlySpan<char> rest = e[..at].TrimEnd(' ');
            if (rest.IsEmpty) break;
            e = rest;
        }
        return e;
    }

    /// <summary>Cut at the first <c>FEAT.</c>, <c>FT.</c> or <c>FEATURING</c> that starts a word (after a space or an opening bracket),
    /// never at the very start.</summary>
    static ReadOnlySpan<char> DropFeaturing(ReadOnlySpan<char> e)
    {
        for (int i = 1; i < e.Length; i++)
        {
            char before = e[i - 1];
            if (before is not (' ' or '(' or '[')) continue;
            ReadOnlySpan<char> rest = e[i..];
            bool hit = rest.StartsWith("FEAT.") || rest.StartsWith("FT.")
                || (rest.StartsWith("FEATURING") && (rest.Length == 9 || !char.IsLetterOrDigit(rest[9])));
            if (!hit) continue;
            ReadOnlySpan<char> kept = e[..(i - 1)].TrimEnd(' ');
            return kept.IsEmpty ? e : kept;
        }
        return e;
    }
}
