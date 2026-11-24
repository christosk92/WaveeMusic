// ── Shell/LocalLinkWords.cs — every sentence "Your files and Spotify" says, in every theme (#240 after 0.4, phase 1) ─────────────────────
//
// Role: CORE. The review, the offer and the opt-in (`Themes/Common/CommonLocalLink.cs`) word nothing themselves: the confidence words, the
// plain evidence ("Same ISRC on 11 of 11 songs", "10 of 10 track lengths line up with the Spotify album", "No ISRC, title only"), the field
// names of the Details table, a decided group's line, the chips' counts. Concept §6.2: evidence, never a score or a percentage.

using System.Globalization;
using FluentGpu.Localization;
using W = Wavee.Strings.LocalFiles.Link;

namespace Wavee;

/// <summary>The words of "Your files and Spotify" (see the file header).</summary>
public static class LocalLinkWords
{
    /// <summary>"Exact", "Strong", "Likely", "Check".</summary>
    public static string Confidence(LinkConfidence c) => Loc.Get(c switch
    {
        LinkConfidence.Exact => W.Exact,
        LinkConfidence.Strong => W.Strong,
        LinkConfidence.Likely => W.Likely,
        _ => W.Check,
    });

    /// <summary>The plain evidence for a candidate. <paramref name="album"/>: an album group's (counts of songs) or a song's.</summary>
    public static string Evidence(LinkCandidate c, bool album) => c.Evidence switch
    {
        LinkEvidence.SameIsrc => album ? W.SameIsrc(Number(c.Hits), Number(c.Of)) : Loc.Get(W.SameIsrcSong),
        LinkEvidence.LengthsLineUp => W.LengthsLineUp(Number(c.Hits), Number(c.Of)),
        LinkEvidence.TitleArtistLength => Loc.Get(W.TitleArtistLength),
        LinkEvidence.TitleArtist => album ? Loc.Get(W.AlbumArtist) : W.TitleArtist(Delta(c.DeltaMs)),
        _ => Loc.Get(W.TitleOnly),
    };

    /// <summary>A song's length against the file's: "+0.4 s", "−19 s", "" when equal within a tenth.</summary>
    public static string Delta(int ms)
    {
        if (Math.Abs(ms) < 100) return "";
        double s = ms / 1000.0;
        string text = Math.Abs(s) < 10 ? Math.Abs(s).ToString("0.#", CultureInfo.CurrentCulture) : Math.Round(Math.Abs(s)).ToString("0", CultureInfo.CurrentCulture);
        return (ms > 0 ? "+" : "−") + text + " s";
    }

    /// <summary>The Details table's field names.</summary>
    public static string Field(LinkFieldKind k) => Loc.Get(k switch
    {
        LinkFieldKind.Title => W.FieldTitle,
        LinkFieldKind.Artist => W.FieldArtist,
        LinkFieldKind.AlbumArtist => W.FieldAlbumArtist,
        LinkFieldKind.Year => W.FieldYear,
        LinkFieldKind.TrackNumbers => W.FieldTrackNumbers,
        _ => W.FieldCover,
    });

    /// <summary>"Empty" for a field the file leaves blank.</summary>
    public static string Empty => Loc.Get(W.Empty);

    /// <summary>"4 fields improve".</summary>
    public static string Improves(int n) => W.FieldsImprove(n, Number(n));

    /// <summary>A decided group's one line: "Accepted · Spotify's info shows in Wavee", "Not on Spotify · never matched again".</summary>
    public static string Decided(LinkGroup g) => g.Decision switch
    {
        LinkDecision.Accepted => Loc.Get(g.SpotifyWins ? W.AcceptedSpotify : W.AcceptedFiles),
        LinkDecision.NotOnSpotify => Loc.Get(W.MarkedNot),
        _ => "",
    };

    /// <summary>The review's line under its title: what was found, or how far matching is.</summary>
    public static string ReviewSub(LinkView v)
    {
        if (v.Phase == LinkPhase.Matching) return W.ReviewMatching(Number(v.Done), Number(v.Total));
        int found = 0;
        foreach (LinkGroup g in v.Groups) if (g.Best is not null || g.Decision == LinkDecision.Accepted) found++;
        return W.ReviewSub(found, Number(found));
    }

    /// <summary>The offer's sentence: "…to 3 albums and 2 songs", "…to 1 song", "…to 42 albums". A song with no album tag is its own group.</summary>
    public static string Offer(int albums, int songs)
    {
        string a = W.OfferAlbums(albums, Number(albums)), s = W.OfferSongs(songs, Number(songs));
        return W.Offer(albums > 0 && songs > 0 ? W.OfferBoth(a, s) : songs > 0 ? s : a);
    }

    /// <summary>The file drawer's Spotify line: "Linked to Savin' Me · Nickelback" and, when the review still holds the group, the evidence; else
    /// "Not linked to Spotify", or a "Not on Spotify" group's line.</summary>
    public static string DrawerLine(long localId)
    {
        if (LocalLink.LinkOf(localId) is { } t)
        {
            string line = W.LinkedTo(t.Title, t.Artists);
            return LocalLink.EvidenceOf(localId) is { Length: > 0 } why ? line + " · " + why : line;
        }
        return LocalLink.IsNotOnSpotify(localId) ? Loc.Get(W.MarkedNot) : Loc.Get(W.NotLinked);
    }

    /// <summary>The activity chip while matching: "Matching · 12 of 48".</summary>
    public static string Chip(int done, int total) => W.ChipMatching(Number(done), Number(total));

    static string Number(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
