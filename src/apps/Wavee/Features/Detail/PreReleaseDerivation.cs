using System;
using System.Globalization;
using Wavee.Core;

namespace Wavee;

// Deliberately its own engine-free file (the DetailLayoutBreakpoints.cs precedent): Wavee.Tests consumes app logic by
// <Compile Include> source-inclusion, and DetailConfig.cs drags in FluentGpu.Controls + the generated Strings table,
// which the test project does not reference.

/// <summary>How an <see cref="Album"/>'s scattered "not out yet" signals collapse into the single instant the detail
/// surface counts down to. Pure + static so the mapper, the rail card and the tests all read the same ladder.</summary>
public static class PreReleaseDerivation
{
    /// <summary><c>PreReleaseEnd</c> ▸ earliest FUTURE track <c>AvailableAt</c> ▸ a FUTURE parsed <c>ReleaseDate</c> ▸
    /// null. Each step is a strictly weaker signal, and each is only consulted when it is genuinely in the future — so
    /// a released album can never acquire a countdown from a stale flag left on a record nobody has re-read.</summary>
    public static DateTimeOffset? UpcomingAt(Album a, DateTimeOffset now)
    {
        if (a.PreReleaseEnd is { } end && end > now) return end;

        // A partly-released ("waterfall") album carries no album-level flag at all: the only evidence is that some rows
        // are still pending, so the next one to land is the moment worth announcing.
        DateTimeOffset? soonest = null;
        var tracks = a.Tracks;
        if (tracks is not null)
            for (int i = 0; i < tracks.Count; i++)
                if (tracks[i].AvailableAt is { } at && at > now && (soonest is null || at < soonest)) soonest = at;
        if (soonest is { } s) return s;

        return ReleaseInstant(a.ReleaseDate) is { } rd && rd > now ? rd : null;
    }

    /// <summary>The album's ISO release date as an instant, or null when absent/unparseable. A precision-reduced
    /// "2026-09" still parses (→ the 1st); a bare "2026" does NOT — which is fine, because the mapper normalises
    /// YEAR precision to "yyyy-01-01" before it ever reaches <c>Album.ReleaseDate</c> (SpotifyExportMapper.IsoDate),
    /// and a countdown to January 1 of a year-only date would be a lie anyway. Invariant culture + assumed-UTC:
    /// this is a wire value, not user input.</summary>
    public static DateTimeOffset? ReleaseInstant(string? iso)
        => !string.IsNullOrWhiteSpace(iso)
           && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
           ? d : null;
}
