using System;
using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>An ordered chip set plus the split point between chips the tracks in view demonstrably carry and chips that
/// have no local evidence yet.
///
/// The split matters because the two are NOT interchangeable: selecting an evidenced chip narrows the list, while
/// selecting an un-evidenced one filters it to nothing (the row filter matches a track's own kind-6 descriptors, so a
/// chip no visible row carries can only ever match zero). Keeping both but marking the difference is what lets the bar
/// show the server's full curated set — which is the thing that made it appear at all on a cold list — without
/// offering taps that lead to an empty page.
///
/// Evidenced entries always come FIRST, so the boundary is a single count rather than a parallel array.</summary>
public readonly record struct ContentFilterChipSet(IReadOnlyList<string> Titles, int EvidencedCount)
{
    public static readonly ContentFilterChipSet Empty = new(Array.Empty<string>(), 0);

    public int Count => Titles.Count;

    /// <summary>True when at least one track in view carries this chip's concept, i.e. selecting it yields rows.</summary>
    public bool IsEvidenced(int index) => index < EvidencedCount;
}

/// <summary>A derived chip together with how many of the tracks in view carry it — <see cref="ContentFilterTags.Derive"/>
/// with the numbers kept. <paramref name="Title"/> is the descriptor's presentation form, exactly as the chip renders
/// it.</summary>
public readonly record struct TagCount(string Title, int Count);

/// <summary>Derives the Liked Songs content-filter chip set from the tracks themselves.
///
/// Chips are DERIVED rather than fetched from <c>content-filter/v1/liked-songs</c>: extension kind 6 already carries
/// each descriptor's presentation name in the row bundle the list fetches anyway, so deriving costs no request and
/// cannot disagree with the rows — a chip exists exactly when a visible track carries it, which is also the rule the
/// reference client applies, and it means a chip can never match zero tracks.
///
/// Pure and engine-free so it is directly testable (Wavee.Tests references Wavee.Core, never the engine).</summary>
public static class ContentFilterTags
{
    /// <summary>Chips shown before the bar starts hiding the tail. Beyond this the bar wraps into a wall of concepts
    /// and stops being scannable; the long tail is low-weight noise anyway.</summary>
    const int MaxChips = 10;

    /// <summary>A tag must appear on at least this many tracks to earn a chip. A concept carried by one track out of
    /// several hundred is not a lens, it is trivia — and tapping it would leave a one-row list.
    ///
    /// <para>Public because it is the app's one EVIDENCE FLOOR for descriptor-derived UI: the Liked facts blend bar
    /// gates on the same number rather than inventing a second threshold, so the two surfaces can never disagree about
    /// when there is enough evidence to say anything.</para></summary>
    public const int MinTrackCount = 3;

    /// <summary>The chip set for a track list, most-common first. Empty when nothing is enriched yet (the bar then
    /// renders nothing at all rather than an empty rail). A projection of <see cref="DeriveCounted"/> — same filter,
    /// same order, same cap, with the numbers dropped.</summary>
    public static IReadOnlyList<string> Derive(IReadOnlyList<Track> tracks)
    {
        var counted = DeriveCounted(tracks);
        if (counted.Count == 0) return Array.Empty<string>();

        var titles = new string[counted.Count];
        for (int i = 0; i < counted.Count; i++) titles[i] = counted[i].Title;
        return titles;
    }

    /// <summary>The same chip set with the carrier counts KEPT. The counts were always computed and then discarded;
    /// the Liked facts legend needs them, and re-deriving them at the call site would be a second copy of this
    /// filter/order/cap that could drift out of step with the chips it is labelling.</summary>
    public static IReadOnlyList<TagCount> DeriveCounted(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return Array.Empty<TagCount>();

        // Case-insensitive so "K-Pop" and "k-pop" (display name absent → the lowercase token) collapse to one chip.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tracks.Count; i++)
        {
            var tags = tracks[i].Tags;
            if (tags is null) continue;
            for (int t = 0; t < tags.Count; t++)
            {
                counts.TryGetValue(tags[t], out int n);
                counts[tags[t]] = n + 1;
            }
        }
        if (counts.Count == 0) return Array.Empty<TagCount>();

        var ordered = new List<KeyValuePair<string, int>>(counts.Count);
        foreach (var kv in counts)
            if (kv.Value >= MinTrackCount) ordered.Add(kv);
        if (ordered.Count == 0) return Array.Empty<TagCount>();

        // Count descending, then name, so the bar is stable across re-derives (a tie that reorders on every enrichment
        // pass would make the chips visibly shuffle while the list loads).
        ordered.Sort(static (a, b) =>
        {
            int c = b.Value.CompareTo(a.Value);
            return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase);
        });

        int take = Math.Min(MaxChips, ordered.Count);
        var result = new TagCount[take];
        for (int i = 0; i < take; i++) result[i] = new TagCount(ordered[i].Key, ordered[i].Value);
        return result;
    }


    /// <summary>The server's curated chips, ordered so the ones the tracks in view demonstrably carry come first, with
    /// the boundary between the two reported so the bar can present them differently.
    ///
    /// Every server chip is KEPT: the set is library-scoped, so a chip with no local evidence usually means descriptor
    /// enrichment has not landed for those rows yet, not that the concept is absent. Dropping them was what hid the
    /// bar on a cold list. Order is otherwise the server's own.
    ///
    /// Reporting <see cref="ContentFilterChipSet.EvidencedCount"/> is what keeps that decision honest. Keeping every
    /// chip while claiming "a chip can never match zero rows" was false: the row filter matches a track's own kind-6
    /// descriptors, so a chip with no evidence filters the list to empty. The caller renders those as unavailable
    /// instead of removing them, so the bar still shows the full curated set and no tap leads to a blank list.</summary>
    public static ContentFilterChipSet OrderByEvidence(IReadOnlyList<ContentFilterChip> serverChips, IReadOnlyList<Track> tracks)
    {
        if (serverChips.Count == 0) return ContentFilterChipSet.Empty;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tracks.Count; i++)
        {
            var tags = tracks[i].Tags;
            if (tags is null) continue;
            for (int t = 0; t < tags.Count; t++) present.Add(tags[t]);
        }

        var evidenced = new List<string>(serverChips.Count);
        var rest = new List<string>(serverChips.Count);
        foreach (var chip in serverChips)
            (present.Contains(chip.Token) || present.Contains(chip.Title) ? evidenced : rest).Add(chip.Title);
        int evidencedCount = evidenced.Count;
        evidenced.AddRange(rest);
        return new ContentFilterChipSet(evidenced, evidencedCount);
    }

    /// <summary>Reconciles the server's curated chip set against the tracks actually present.
    ///
    /// The server list is AUTHORITATIVE for which chips exist, their wording, and their order — that is what makes the
    /// bar match Desktop. This only drops the ones nothing in view carries, which is the reference client's own rule
    /// and the reason a chip can never yield an empty list. A server chip matches a track when either its lowercase
    /// query token or its display title equals one of the track's descriptors (the kind-6 proto documents display_name
    /// as the same string the endpoint returns as `title`).</summary>
    public static IReadOnlyList<string> Reconcile(IReadOnlyList<ContentFilterChip> serverChips, IReadOnlyList<Track> tracks)
    {
        if (serverChips.Count == 0 || tracks.Count == 0) return Array.Empty<string>();

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tracks.Count; i++)
        {
            var tags = tracks[i].Tags;
            if (tags is null) continue;
            for (int t = 0; t < tags.Count; t++) present.Add(tags[t]);
        }
        if (present.Count == 0) return Array.Empty<string>();

        List<string>? kept = null;
        foreach (var chip in serverChips)
            if (present.Contains(chip.Token) || present.Contains(chip.Title))
                (kept ??= new List<string>(serverChips.Count)).Add(chip.Title);
        return (IReadOnlyList<string>?)kept ?? Array.Empty<string>();
    }
}
