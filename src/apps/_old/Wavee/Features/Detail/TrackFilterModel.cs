using System;
using Wavee.Core;

namespace Wavee;

/// <summary>Which textual track field the list query searches.</summary>
public enum TrackSearchScope : byte { Everything = 0, Title = 1, Artist = 2, Album = 3 }

/// <summary>Three-way inclusion rule for a boolean track trait: include everything, hide matches, or show only matches.</summary>
public enum TrackTraitMode : byte { All = 0, Hide = 1, Only = 2 }

/// <summary>Combinable binary filters. Three-way traits, ranges, and single-choice facets live on
/// <see cref="TrackFilterState"/>.</summary>
[Flags]
public enum TrackFilterFlags : byte
{
    None = 0,
    LikedOnly = 1,
    PlayableOnly = 2,
}

public enum TrackDurationRange : byte { Any = 0, UnderThreeMinutes = 1, ThreeToFiveMinutes = 2, OverFiveMinutes = 3 }
public enum TrackAddedRange : byte { Any = 0, LastSevenDays = 1, LastThirtyDays = 2, LastSixMonths = 3, LastYear = 4 }
public enum TrackOriginFilter : byte { Any = 0, Streamed = 1, Local = 2 }

/// <summary>Tempo bands, in the vocabulary a listener actually uses ("slow", "fast") rather than raw BPM entry. The
/// boundaries are the conventional ones: 90 separates ballad from mid, 120 is the four-on-the-floor line, 140 is where
/// drum-and-bass / hard dance begins. A track with no tempo (no kind-222 payload yet) matches only <see cref="Any"/>,
/// so an un-enriched list is never silently emptied by this filter.</summary>
public enum TrackTempoBand : byte { Any = 0, Under90 = 1, From90To119 = 2, From120To139 = 3, From140AndUp = 4 }

/// <summary>The complete transient track-list filter. The default value means no filtering and global text search.</summary>
public readonly record struct TrackFilterState(
    TrackSearchScope SearchScope = TrackSearchScope.Everything,
    TrackTraitMode ExplicitMode = TrackTraitMode.All,
    TrackTraitMode VideoMode = TrackTraitMode.All,
    TrackFilterFlags Flags = TrackFilterFlags.None,
    TrackDurationRange Duration = TrackDurationRange.Any,
    TrackAddedRange Added = TrackAddedRange.Any,
    TrackOriginFilter Origin = TrackOriginFilter.Any,
    TrackTempoBand Tempo = TrackTempoBand.Any,
    // Camelot code ("8B", "11A"). Null = any key. Matched case-insensitively against Track.CamelotCode, which is the
    // stable DJ notation; the pretty name ("C", "G#") is display only and differs by spelling convention.
    string? CamelotCode = null,
    // Liked Songs content-filter chip: a descriptor tag (kind 6 display name, e.g. "K-Pop"). Exclusive by design —
    // one chip at a time — because the chips are a lens on the list, not a set of accumulating constraints.
    string? Tag = null,
    // ── The rail's facts, as lenses (Liked Songs) ────────────────────────────────────────────────────────────────
    // An ARBITRARY saved-date window, in Unix ms, half-open as (After, Before]. 0/0 = off; either endpoint alone is a
    // valid open-ended half. This is what a sparkline bar means when you click it: the bar counts a rolling 7-day
    // window anchored on the moment the panel read its clock, which no <see cref="TrackAddedRange"/> preset can name.
    // MUTUALLY EXCLUSIVE with Added (they answer the same question, "when did I save this?") — use
    // WithAddedWindow / WithAddedRange rather than a bare `with`, which is what keeps that true.
    long AddedAfterMs = 0L,
    long AddedBeforeMs = 0L,
    // An EXACT artist, keyed by the same identity LikedFactsRules.TopArtists ranks by (uri, else id, else name). Not a
    // TrackSearchScope.Artist text query: a lens must mean "these songs credit THIS artist", not "these songs contain
    // this substring somewhere in a credit", and it must not put text the user did not type into the search box.
    string? ArtistId = null,
    // The display name for the lens header. Carried WITH the id rather than looked up: the header must be able to name
    // an artist whose rows the current filter has excluded, and re-deriving the name from the visible rows is exactly
    // how a header ends up disagreeing with the face that was clicked. Display only — it filters nothing and is NOT
    // counted in ActiveCount (a name without an id is inert).
    string? ArtistName = null,
    // Inclusive release-year window. 0/0 = off. A one-year sparkline bar sets min == max; a wide bin sets the bin's
    // range. Year 0 on a track (unknown) never matches a window that is on — the histogram counted dated tracks only.
    int ReleaseYearMin = 0,
    int ReleaseYearMax = 0)
{
    public static readonly TrackFilterState Default = new();

    public bool LikedOnly => (Flags & TrackFilterFlags.LikedOnly) != 0;
    public bool PlayableOnly => (Flags & TrackFilterFlags.PlayableOnly) != 0;
    public bool IsDefault => Equals(Default);

    /// <summary>Number shown on the Filter affordance. Each binary toggle and each non-default facet counts once.</summary>
    public int ActiveCount
    {
        get
        {
            int n = SearchScope == TrackSearchScope.Everything ? 0 : 1;
            if (ExplicitMode != TrackTraitMode.All) n++;
            if (VideoMode != TrackTraitMode.All) n++;
            if (LikedOnly) n++;
            if (PlayableOnly) n++;
            if (Duration != TrackDurationRange.Any) n++;
            if (Added != TrackAddedRange.Any) n++;
            if (Origin != TrackOriginFilter.Any) n++;
            if (Tempo != TrackTempoBand.Any) n++;
            if (!string.IsNullOrEmpty(CamelotCode)) n++;
            if (!string.IsNullOrEmpty(Tag)) n++;
            // One window is ONE facet however many endpoints it names — "(Aug 11, Aug 18]" is a single answer to a
            // single question, and counting the two halves separately would put a 2 on the funnel for one bar click.
            if (AddedAfterMs != 0L || AddedBeforeMs != 0L) n++;
            if (!string.IsNullOrEmpty(ArtistId)) n++;
            if (ReleaseYearMin != 0 || ReleaseYearMax != 0) n++;
            return n;
        }
    }

    /// <summary>Set the coarse saved-date preset, clearing any explicit window. The two are one facet wearing two
    /// faces; leaving both set would AND a preset against a window and quietly return fewer rows than either lens
    /// promised.</summary>
    public TrackFilterState WithAddedRange(TrackAddedRange range)
        => this with { Added = range, AddedAfterMs = 0L, AddedBeforeMs = 0L };

    /// <summary>Set (or, with 0/0, clear) the explicit saved-date window, clearing the coarse preset. See
    /// <see cref="WithAddedRange"/>.</summary>
    public TrackFilterState WithAddedWindow(long afterMs, long beforeMs)
        => this with { AddedAfterMs = afterMs, AddedBeforeMs = beforeMs, Added = TrackAddedRange.Any };

    /// <summary>Set (or, with a null id, clear) the exact-artist lens. The display name travels with the id and is
    /// dropped with it, so a stale name can never outlive the filter it described.</summary>
    public TrackFilterState WithArtist(string? artistId, string? displayName = null)
        => artistId is { Length: > 0 } id
            ? this with { ArtistId = id, ArtistName = displayName }
            : this with { ArtistId = null, ArtistName = null };

    /// <summary>Set (or, with 0/0, clear) the inclusive release-year window. One facet: a bin is one answer however
    /// many years it spans.</summary>
    public TrackFilterState WithReleaseYear(int min, int max)
        => this with { ReleaseYearMin = min, ReleaseYearMax = max };
}

/// <summary>Pure filter predicate shared by production and headless tests.</summary>
public static class TrackFilterModel
{
    public static bool Matches(
        Track track,
        string query,
        in TrackFilterState filter,
        bool hasVideo,
        bool isSaved,
        DateTimeOffset now)
    {
        if (!MatchesTrait(track.IsExplicit, filter.ExplicitMode)) return false;
        if (!MatchesTrait(hasVideo, filter.VideoMode)) return false;
        if (filter.LikedOnly && !isSaved) return false;
        // Only a CONFIRMED unavailable is filtered out. Availability is nullable — null means no response ever stated a
        // verdict — and treating unknown as unplayable would empty the list on every surface that never carries
        // playability at all (cluster, library and extended-metadata writes).
        // The shared IsNotYetOut() predicate coincides with "cannot play" here, and that coincidence is intentional: it
        // adds only the AvailableAt clause, so a region-blocked row (Unavailable, no timestamp) is still hidden, while a
        // row whose release moment has passed under a stale server verdict is KEPT — which is the same release-drop heal
        // the greyed row and the play gate get, reached without a refetch.
        if (filter.PlayableOnly && track.IsNotYetOut()) return false;

        if (filter.Origin == TrackOriginFilter.Streamed && track.Origin != TrackOrigin.Streamed) return false;
        if (filter.Origin == TrackOriginFilter.Local && track.Origin != TrackOrigin.Local) return false;

        if (filter.Tag is { Length: > 0 } tag && !HasTag(track.Tags, tag)) return false;
        if (filter.ArtistId is { Length: > 0 } artistId && !HasArtist(track.Artists, artistId)) return false;
        if (!MatchesAddedWindow(track.AddedAt, filter.AddedAfterMs, filter.AddedBeforeMs)) return false;
        if (!MatchesReleaseYear(track.Year, filter.ReleaseYearMin, filter.ReleaseYearMax)) return false;
        if (!MatchesTempo(track.TempoBpm, filter.Tempo)) return false;
        if (filter.CamelotCode is { Length: > 0 } key
            && !string.Equals(track.CamelotCode, key, StringComparison.OrdinalIgnoreCase)) return false;

        if (!MatchesDuration(track.DurationMs, filter.Duration)) return false;
        if (!MatchesAdded(track.AddedAt, filter.Added, now)) return false;
        return query.Length == 0 || MatchesQuery(track, query, filter.SearchScope);
    }

    public static bool MatchesQuery(Track track, string query, TrackSearchScope scope) => scope switch
    {
        TrackSearchScope.Title => track.Title.Contains(query, StringComparison.OrdinalIgnoreCase),
        TrackSearchScope.Artist => ArtistMatches(track, query),
        TrackSearchScope.Album => track.Album.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
        _ => track.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
             || ArtistMatches(track, query)
             || track.Album.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
    };

    static bool MatchesTrait(bool hasTrait, TrackTraitMode mode) => mode switch
    {
        TrackTraitMode.Hide => !hasTrait,
        TrackTraitMode.Only => hasTrait,
        _ => true,
    };

    /// <summary>Tag match. Case-insensitive on the DISPLAY name, which is what the chip bar shows and what the store
    /// holds; the lowercase wire token never reaches the UI, so there is one string to compare, not two.</summary>
    static bool HasTag(IReadOnlyList<string>? tags, string tag)
    {
        if (tags is null) return false;
        for (int i = 0; i < tags.Count; i++)
            if (string.Equals(tags[i], tag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Exact credited-artist match, against the same identity ladder <c>LikedFactsRules.TopArtists</c> keys by
    /// (uri, else id, else name) — the two must agree or the face you clicked and the rows you get are two different
    /// artists. EVERY credit counts, not just the primary: a feature credit is a real reason a track is in the library,
    /// and the fact that produced this lens counted it that way too.</summary>
    static bool HasArtist(IReadOnlyList<ArtistRef>? artists, string key)
    {
        if (artists is null) return false;
        for (int i = 0; i < artists.Count; i++)
        {
            var a = artists[i];
            if (a is null) continue;
            if (string.Equals(a.Uri, key, StringComparison.Ordinal)) return true;
            if (string.Equals(a.Id, key, StringComparison.Ordinal)) return true;
            // Name only as the LAST rung, and only when the credit carries no identifier at all — otherwise two
            // different artists who share a display name would collapse into one lens.
            if (string.IsNullOrEmpty(a.Uri) && string.IsNullOrEmpty(a.Id)
                && string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The explicit saved-date window, half-open as <c>(after, before]</c>. 0 on an endpoint means "unbounded
    /// on that side", so the window can be a single half; 0/0 is the whole filter off.
    ///
    /// <para>A track with NO saved date never matches a window that is on. That is the same rule
    /// <see cref="MatchesAdded"/> applies to the coarse presets, and it is the honest one: the sparkline bar this lens
    /// came from counted stamped likes only, so an unstamped row was never part of the number the user clicked.</para>
    ///
    /// <para>Half-open, and in that direction, because the buckets are ROLLING windows laid end to end — bucket k ends
    /// exactly where bucket k+1 begins. A closed-both-ends test would count a like that landed on the seam in two bars,
    /// and the twelve lenses would sum to more than the twelve bars do.</para></summary>
    static bool MatchesAddedWindow(DateTimeOffset? addedAt, long afterMs, long beforeMs)
    {
        if (afterMs == 0L && beforeMs == 0L) return true;
        if (addedAt is not { } at) return false;
        long ms = at.ToUnixTimeMilliseconds();
        if (afterMs != 0L && ms <= afterMs) return false;
        if (beforeMs != 0L && ms > beforeMs) return false;
        return true;
    }

    /// <summary>Inclusive release-year window. 0/0 is off. A track with no year (0) never matches a window that is
    /// on — the year sparkline this lens came from counted dated tracks only.</summary>
    static bool MatchesReleaseYear(int year, int min, int max)
    {
        if (min == 0 && max == 0) return true;
        if (year <= 0) return false;
        if (min != 0 && year < min) return false;
        if (max != 0 && year > max) return false;
        return true;
    }

    /// <summary>The half-open tempo band a BPM falls in — THE one boundary table: the filter predicate below, the
    /// rail's tempo facts (<c>LikedFactsRules.TempoBandCounts</c>) and the flyout's labels all read it, so a pill and
    /// the rows it lenses cannot disagree by a boundary. A non-positive or unknown tempo has no band
    /// (<see cref="TrackTempoBand.Any"/>).</summary>
    public static TrackTempoBand BandOf(double bpm)
        => double.IsNaN(bpm) || bpm <= 0d ? TrackTempoBand.Any
         : bpm < 90d ? TrackTempoBand.Under90
         : bpm < 120d ? TrackTempoBand.From90To119
         : bpm < 140d ? TrackTempoBand.From120To139
         : TrackTempoBand.From140AndUp;

    static bool MatchesTempo(double? bpm, TrackTempoBand band)
    {
        if (band == TrackTempoBand.Any) return true;
        if (bpm is not { } t || t <= 0d) return false;   // unknown tempo cannot satisfy an explicit band
        return BandOf(t) == band;
    }

    static bool MatchesDuration(long durationMs, TrackDurationRange range) => range switch
    {
        TrackDurationRange.UnderThreeMinutes => durationMs < 180_000L,
        TrackDurationRange.ThreeToFiveMinutes => durationMs is >= 180_000L and <= 300_000L,
        TrackDurationRange.OverFiveMinutes => durationMs > 300_000L,
        _ => true,
    };

    static bool MatchesAdded(DateTimeOffset? addedAt, TrackAddedRange range, DateTimeOffset now)
    {
        if (range == TrackAddedRange.Any) return true;
        if (addedAt is null) return false;
        int days = range switch
        {
            TrackAddedRange.LastSevenDays => 7,
            TrackAddedRange.LastThirtyDays => 30,
            TrackAddedRange.LastSixMonths => 180,
            _ => 365,
        };
        return addedAt.Value >= now - TimeSpan.FromDays(days);
    }

    static bool ArtistMatches(Track track, string query)
    {
        for (int i = 0; i < track.Artists.Count; i++)
            if (track.Artists[i].Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
