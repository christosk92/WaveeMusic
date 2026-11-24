// ── Playback/LocalLink.Score.cs ─────────────────────────────────────────────────────────────────────────────────────
// the matching ladder: how one group of files is held against one Spotify album (or one track), and the plain evidence it yields
//
// Role: PURE (any thread; no entity reads: the matcher hands in what it read)
// Spec: 240-spotify-linking-concept.md §4 US1-US2, §6.2-6.3; board frame 37 ("The matching ladder")
//
// THE LADDER, first rung that answers sets the confidence (Not on Spotify groups never reach it):
//   1  ISRC in the file      every song's ISRC names a track of the album (a song: the track)            → Exact  "Same ISRC on 11 of 11 songs"
//   2  album structure       every song maps to a track of the album and every length lines up (±2 s);  → Strong "10 of 10 track lengths line up"
//                            or at least half of the ISRCs agree                                         → Strong "Same ISRC on 6 of 11 songs"
//   3  title, artist, length the album's name and artist agree and at least half the lengths line up      → Likely
//                            (a song: title and artist agree and the length is within 3 s)               → Likely "Title, artist and length"
//   4  anything weaker       a name alone, or a length that is off                                       → Check  (the user picks)
// A song with two or more candidates that are equally good (same rung, both within 3 s) is a Check: the user picks between them.
// Fingerprints (AcoustID) are a later rung, for files whose tags say nothing.
//
// MAPPING a file to a track of the album: by (disc, number) when the file has a track number and the album a track there whose title agrees;
// else by title. A track is used once.

using System.Globalization;
using System.Text;

namespace Wavee;

public static partial class LocalLink
{
    /// <summary>A file as the matcher holds it: its tags (or its file name's split), length and ISRC.</summary>
    public readonly record struct FileFacts(long Id, string Title, string Artist, string Album, int DurationMs, string Isrc, int Disc, int Number, int Year);

    /// <summary>Lengths "line up" within this much (an album's tracks).</summary>
    const int AlbumSlackMs = 2000;
    /// <summary>A song's length agrees within this much.</summary>
    const int SongSlackMs = 3000;

    /// <summary>Hold an album group against one Spotify album. <paramref name="tracks"/> carries every track's ISRC where known.</summary>
    public static LinkCandidate ScoreAlbum(IReadOnlyList<FileFacts> files, string albumTitle, string albumArtist, LinkAlbum album)
    {
        int n = files.Count;
        var mapped = new LinkTrack?[n];
        var used = new bool[album.Tracks.Count];
        // (disc, number) first, where the titles agree.
        for (int i = 0; i < n; i++)
        {
            FileFacts f = files[i];
            if (f.Number <= 0) continue;
            for (int k = 0; k < album.Tracks.Count; k++)
            {
                LinkTrack t = album.Tracks[k];
                if (used[k] || t.Number != f.Number || (f.Disc > 0 && t.Disc > 0 && t.Disc != f.Disc) || !SameTitle(f.Title, t.Title)) continue;
                mapped[i] = t; used[k] = true; break;
            }
        }
        // Then by ISRC, then by title.
        for (int i = 0; i < n; i++)
        {
            if (mapped[i] is not null || files[i].Isrc.Length == 0) continue;
            for (int k = 0; k < album.Tracks.Count; k++)
                if (!used[k] && album.Tracks[k].Isrc == files[i].Isrc) { mapped[i] = album.Tracks[k]; used[k] = true; break; }
        }
        for (int i = 0; i < n; i++)
        {
            if (mapped[i] is not null) continue;
            for (int k = 0; k < album.Tracks.Count; k++)
                if (!used[k] && SameTitle(files[i].Title, album.Tracks[k].Title)) { mapped[i] = album.Tracks[k]; used[k] = true; break; }
        }
        int isrc = 0, lengths = 0, titles = 0;
        for (int i = 0; i < n; i++)
        {
            if (mapped[i] is not { } t) continue;
            titles++;
            if (files[i].Isrc.Length > 0 && t.Isrc.Length > 0 && files[i].Isrc == t.Isrc) isrc++;
            if (files[i].DurationMs > 0 && Math.Abs(t.DurationMs - files[i].DurationMs) <= AlbumSlackMs) lengths++;
        }
        bool nameOk = SameAlbum(albumTitle, album.Title), artistOk = SameArtist(albumArtist, album.Artist);
        (LinkConfidence c, LinkEvidence e, int hits) =
            n > 0 && isrc == n ? (LinkConfidence.Exact, LinkEvidence.SameIsrc, isrc)
            : n > 0 && (nameOk || artistOk) && lengths == n && (n >= 2 || nameOk && artistOk) ? (LinkConfidence.Strong, LinkEvidence.LengthsLineUp, lengths)
            : n > 0 && isrc * 2 >= n && isrc > 0 ? (LinkConfidence.Strong, LinkEvidence.SameIsrc, isrc)
            : nameOk && artistOk && lengths * 2 >= n && lengths > 0 ? (LinkConfidence.Likely, LinkEvidence.LengthsLineUp, lengths)
            : nameOk && artistOk ? (LinkConfidence.Check, LinkEvidence.TitleArtist, titles)
            : (LinkConfidence.Check, LinkEvidence.TitleOnly, titles);
        string detail = Strings.LocalFiles.Link.AlbumDetail(album.Tracks.Count, Count(album.Tracks.Count))
                        + (album.Year > 0 ? " · " + album.Year.ToString(CultureInfo.InvariantCulture) : "");
        return new LinkCandidate(c, e, hits, n, 0, album.Uri, album.Title, album.Artist, detail, album.Cover, 0, album.Year, mapped);
    }

    /// <summary>Hold a song (a file with no album tag) against one Spotify track.</summary>
    public static LinkCandidate ScoreSong(FileFacts f, LinkTrack t)
    {
        bool title = SameTitle(f.Title, t.Title);
        bool artist = f.Artist.Length > 0 && SameArtist(f.Artist, t.Artists);
        int delta = f.DurationMs > 0 && t.DurationMs > 0 ? t.DurationMs - f.DurationMs : int.MaxValue;
        bool length = delta != int.MaxValue && Math.Abs(delta) <= SongSlackMs;
        (LinkConfidence c, LinkEvidence e) =
            f.Isrc.Length > 0 && f.Isrc == t.Isrc ? (LinkConfidence.Exact, LinkEvidence.SameIsrc)
            : title && artist && length ? (LinkConfidence.Likely, LinkEvidence.TitleArtistLength)
            : title && artist ? (LinkConfidence.Check, LinkEvidence.TitleArtist)
            : (LinkConfidence.Check, LinkEvidence.TitleOnly);
        string detail = t.Artists + (t.Album.Length > 0 ? " · " + t.Album : "") + " · " + Clock(t.DurationMs);
        return new LinkCandidate(c, e, 1, 1, delta == int.MaxValue ? 0 : delta, t.Uri, t.Title, t.Artists, detail, t.Cover, t.DurationMs, t.Year, [t]);
    }

    /// <summary>Best first: the higher rung, then more hits, then the closer length; two Likely songs within the slack of each other make the
    /// best a Check (the user picks).</summary>
    public static List<LinkCandidate> Rank(List<LinkCandidate> list, bool song)
    {
        list.Sort(static (a, b) =>
        {
            int c = a.Confidence.CompareTo(b.Confidence);
            if (c != 0) return c;
            c = b.Hits.CompareTo(a.Hits);
            return c != 0 ? c : Math.Abs(a.DeltaMs).CompareTo(Math.Abs(b.DeltaMs));
        });
        if (song && list.Count >= 2 && list[0].Confidence == LinkConfidence.Likely && list[1].Confidence == LinkConfidence.Likely)
            list[0] = list[0] with { Confidence = LinkConfidence.Check };
        return list;
    }

    /// <summary>The Details table of an album group against its candidate (board 29): what the files say, what Spotify says.</summary>
    public static LinkField[] FieldsOf(IReadOnlyList<FileFacts> files, string title, string artist, string albumArtist, int year, string? fileCover, LinkCandidate c, bool album)
    {
        string empty = "";
        int missingNumbers = 0, maxNumber = 0;
        foreach (FileFacts f in files) { if (f.Number <= 0) missingNumbers++; maxNumber = Math.Max(maxNumber, f.Number); }
        int theirMax = 0;
        foreach (LinkTrack? t in c.Tracks) if (t is not null) theirMax = Math.Max(theirMax, t.Number);
        string mineNumbers = missingNumbers > 0 ? empty : "1–" + maxNumber.ToString(CultureInfo.InvariantCulture);
        string theirNumbers = theirMax > 0 ? "1–" + theirMax.ToString(CultureInfo.InvariantCulture) : empty;
        var list = new List<LinkField>(6)
        {
            new(LinkFieldKind.Title, title, c.Title, !string.Equals(title, c.Title, StringComparison.Ordinal)),
            new(LinkFieldKind.Artist, artist, c.Artist, !string.Equals(artist, c.Artist, StringComparison.Ordinal)),
        };
        if (album)
        {
            list.Add(new(LinkFieldKind.AlbumArtist, albumArtist, c.Artist, albumArtist.Length == 0 || !string.Equals(albumArtist, c.Artist, StringComparison.Ordinal)));
            list.Add(new(LinkFieldKind.TrackNumbers, mineNumbers, theirNumbers, missingNumbers > 0 && theirNumbers.Length > 0));
        }
        list.Add(new(LinkFieldKind.Year, year > 0 ? year.ToString(CultureInfo.InvariantCulture) : empty, c.Year > 0 ? c.Year.ToString(CultureInfo.InvariantCulture) : empty,
            c.Year > 0 && year != c.Year));
        list.Add(new(LinkFieldKind.Cover, fileCover is null ? empty : "", c.Cover is null ? empty : "", fileCover is null && c.Cover is not null, fileCover, c.Cover));
        return [.. list];
    }

    // ── comparing names ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Two song titles name the same song: case, accents, punctuation and a trailing " - Remastered 2011" aside. "(Live)" and
    /// "(Acoustic)" stay part of the title: a live take is another song.</summary>
    public static bool SameTitle(string a, string b) => Fold(StripRemaster(a)) == Fold(StripRemaster(b));

    /// <summary>Two album names name the same album: as <see cref="SameTitle"/>, and an edition in brackets ("(Deluxe Edition)") aside.</summary>
    public static bool SameAlbum(string a, string b) => Fold(StripEdition(StripRemaster(a))) == Fold(StripEdition(StripRemaster(b)));

    /// <summary>The artists agree: one credit names the other's first artist ("Sable & Sun" in "Sable & Sun, Mia Solace").</summary>
    public static bool SameArtist(string mine, string theirs)
    {
        string a = Fold(mine), b = Fold(theirs);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b || b.Contains(a, StringComparison.Ordinal) || a.Contains(b, StringComparison.Ordinal)) return true;
        foreach (string part in theirs.Split([',', '&', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (Fold(part) is { Length: > 0 } p && a.Contains(p, StringComparison.Ordinal)) return true;
        return false;
    }

    static string StripRemaster(string s)
    {
        int at = s.IndexOf(" - ", StringComparison.Ordinal);
        if (at > 0 && s.AsSpan(at + 3).Contains("remaster", StringComparison.OrdinalIgnoreCase)) return s[..at];
        int open = s.LastIndexOf('(');
        if (open > 0 && s.AsSpan(open).Contains("remaster", StringComparison.OrdinalIgnoreCase)) return s[..open];
        return s;
    }

    static string StripEdition(string s)
    {
        int open = s.LastIndexOfAny(['(', '[']);
        if (open <= 0) return s;
        var tail = s.AsSpan(open);
        foreach (string w in s_editions) if (tail.Contains(w, StringComparison.OrdinalIgnoreCase)) return s[..open];
        return s;
    }

    static readonly string[] s_editions = ["deluxe", "edition", "expanded", "anniversary", "bonus", "version"];

    /// <summary>Lower case, accents off, only letters and digits (an "&amp;" reads "and").</summary>
    public static string Fold(string s)
    {
        if (s.Length == 0) return "";
        string d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (ch == '&') { sb.Append("and"); continue; }
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>"4:33".</summary>
    public static string Clock(int ms)
    {
        int s = Math.Max(0, ms) / 1000;
        return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    static string Count(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
