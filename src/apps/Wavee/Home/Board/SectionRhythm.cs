// ── Home/Board/SectionRhythm.cs: never two neighbours alike (Home rhythm A; spec 3.2 rule 2, tightened from "no more than two") ────
//
// The chooser gives every feed section a primary template and the alternates its structure allows. One pass over a run of the
// projected board (in Spotify's order, after the preview folds; nothing is reordered) keeps each section on its primary unless
// the entry before it already draws that template; then it takes the first alternate that differs from the one before it,
// preferring one that does not take the next section's primary either (so a forced move does not force the next) and one of
// another height class than the one before it (tall and short alternate where the structure allows).
//
// STABLE BY IDENTITY: a section's template is a pure function of its own structure and of the entry before it. It moves off its
// primary only when that neighbour draws the same template, so a refresh that changes a section elsewhere on the page never
// flips it, and the same answer always draws the same page. Nothing is stored.
//
// Only a feed section at its auto size takes part. A user's placed item (a pin, a widget, a placed section, a sized section) is
// a fixed neighbour: a feed section next to a placed section still avoids its template, the placed one never changes.

using System.Collections.Immutable;
using Wavee.HomeUi;

namespace Wavee;

public static class SectionRhythm
{
    /// <summary>How tall a template draws, roughly: the pass alternates them where it can.</summary>
    public enum Height : byte { Short, Medium, Tall }

    public static Height HeightOf(SectionTemplate t) => t switch
    {
        SectionTemplate.CompactGrid or SectionTemplate.ArtistCircles or SectionTemplate.MoodPlates => Height.Short,
        SectionTemplate.LeadGrid or SectionTemplate.PreviewCard or SectionTemplate.PreviewGrid or SectionTemplate.BookShelf
            or SectionTemplate.ReleaseList or SectionTemplate.VideoTiles or SectionTemplate.EpisodeRows => Height.Tall,
        _ => Height.Medium,
    };

    /// <summary>The template a full-width body draws as, for the comparison: the three cover-shelf flavours are one look.</summary>
    public static SectionTemplate LookOf(SectionTemplate t) => t is SectionTemplate.EntityShelf or SectionTemplate.PlainShelf ? SectionTemplate.CoverShelf : t;

    /// <summary>The run <paramref name="run"/> (one group's feed-like entries between two placed non-section items) with every
    /// feed section that would repeat the template before it moved to an alternate. Entries that take no room are skipped.</summary>
    public static void Apply(List<BoardEntry> run)
    {
        SectionTemplate prev = SectionTemplate.None;
        Height prevHeight = Height.Medium;
        for (int i = 0; i < run.Count; i++)
        {
            var e = run[i];
            if (e.Hidden) continue;
            if (e.Zone is null || e.Size != HomeSize.Auto)
            {
                // A widget, a pin, a sized section or a block with no zone of its own (the Charts block): it breaks the run.
                prev = SectionTemplate.None;
                continue;
            }
            var look = LookOf(e.Template);
            if (look == prev && e.Kind == BoardEntryKind.FeedSection && Choose(e, prev, prevHeight, NextPrimary(run, i)) is { } alt)
            {
                run[i] = e = e with { Template = alt, Zone = e.Zone with { Kind = SectionChooser.KindOf(alt) } };
                look = alt;
            }
            prev = look;
            prevHeight = HeightOf(look);
        }
    }

    /// <summary>The longest run of one look in <paramref name="templates"/> (the inspector's check; 1 is the target).</summary>
    public static int LongestRun(IReadOnlyList<SectionTemplate> templates)
    {
        int best = 0, n = 0;
        SectionTemplate prev = SectionTemplate.None;
        foreach (var t in templates)
        {
            var look = LookOf(t);
            n = look == prev ? n + 1 : 1;
            prev = look;
            best = Math.Max(best, n);
        }
        return best;
    }

    static SectionTemplate NextPrimary(List<BoardEntry> run, int i)
    {
        for (int k = i + 1; k < run.Count; k++)
        {
            var e = run[k];
            if (e.Hidden) continue;
            return e.Zone is null || e.Size != HomeSize.Auto ? SectionTemplate.None : LookOf(e.Template);
        }
        return SectionTemplate.None;
    }

    static SectionTemplate? Choose(BoardEntry e, SectionTemplate prev, Height prevHeight, SectionTemplate next)
    {
        var alts = e.Alternates;
        if (alts.IsDefaultOrEmpty) return null;
        SectionTemplate? best = null;
        int bestScore = -1;
        foreach (var a in alts)
        {
            if (LookOf(a) == prev) continue;
            // First the next section's freedom, then the height alternation, then the chooser's order.
            int score = (LookOf(a) != next ? 2 : 0) + (HeightOf(a) != prevHeight ? 1 : 0);
            if (score > bestScore) { best = a; bestScore = score; }
        }
        return best;
    }
}

/// <summary>Who a Spotify station is seeded by, read off the station's own art: the server draws a station's cover from its seed
/// (<c>.../img/radio/artist/&lt;id&gt;/...</c> or <c>.../img/radio/track/&lt;id&gt;/...</c>, the mobile and desktop variants alike).
/// The only structural trace of the seed the Home answer carries; an art path that names neither is "not an artist".</summary>
public static class StationSeed
{
    public static bool IsArtist(string? imageUrl)
    {
        if (imageUrl is null) return false;
        int at = imageUrl.IndexOf("/img/radio", StringComparison.Ordinal);
        if (at < 0) return false;
        int slash = imageUrl.IndexOf('/', at + "/img/radio".Length);
        return slash >= 0 && string.CompareOrdinal(imageUrl, slash, "/artist/", 0, "/artist/".Length) == 0;
    }
}
