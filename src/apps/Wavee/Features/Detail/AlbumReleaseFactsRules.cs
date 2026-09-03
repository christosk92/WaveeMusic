using System;
using System.Collections.Generic;
using System.Globalization;
using Wavee.Core;

namespace Wavee;

/// <summary>The "About this release" block as DATA: computed once per projection in DetailPage (the mapper), read by
/// every surface that shows it (rail, compact header, vertical arm). Nothing here is decided in a Render — the
/// composition below is FIXED, only the strings refine as hydration rungs land (Open → Rich → Full), and the store's
/// monotone merge guarantees a value never goes back to null once shown.</summary>
public sealed record AlbumReleaseFacts(
    string? Songs,          // "10" · "8 of 10" (not-yet-out tracks excluded from the count that is OUT) · null before Open
    string? Length,         // "30 min" / "1 hr 12 min" · null when no OUT track carries a duration yet
    string? Released,       // "2025" → "November 2025" → "November 4, 2025" as precision rises · null before any year
    bool ReleasesInFuture,  // caption "Releases" instead of "Released" (ReleaseInstant > now, `now` passed in)
    string? Label,          // "BLØF" — a NOTE line, never a tile (arrives at Full, must not reshape the grid)
    IReadOnlyList<string> Notes)   // courtesy line, ℗/© lines, in that order
{
    public static readonly AlbumReleaseFacts Empty = new(null, null, null, false, null, Array.Empty<string>());
    public bool HasTiles => Songs is not null || Length is not null || Released is not null;
    public bool IsEmpty => !HasTiles && Label is null && Notes.Count == 0;
}

/// <summary>
/// The PURE arithmetic behind <see cref="AlbumReleaseFacts"/>. Engine-free by construction (System + Wavee.Core only)
/// so it is pinned by <c>AlbumReleaseFactsRulesTests</c> against the production code rather than a copy of it — the
/// same discipline as <c>PlaylistPageNoticeRules</c> / <c>LikedFactsRules</c> / <c>TrackExpandedFacts</c>.
///
/// <para>This moves the arithmetic that used to live in <c>DetailTrailing.AlbumFactTiles</c> / <c>ReleaseNotes</c>
/// (composing the block on every render, from whatever the model happened to carry that frame) into ONE function
/// that returns a fixed-shape record. The two-tile-then-reflow bug (bugg.mp4) was never a layout bug: it was this
/// arithmetic re-running mid-render with a wider "Released" value and the wrap-grow row re-deciding its own shape.
/// A view over a stable record can only ever refine text inside boxes it already drew.</para>
///
/// <para><c>now</c> is INJECTED, taken once at the mapper boundary (`DetailPage`), never read in here — the same
/// "now is read ONCE at the panel boundary" rule <c>LikedFactsPanel</c> documents for its own clock.</para>
/// </summary>
public static class AlbumReleaseFactsRules
{
    /// <summary>The release facts for one album/single projection. Every input is optional because the hydration
    /// rungs land independently (tracks at Open, release date + precision + copyright at Rich, label + courtesy at
    /// Full) — a missing input simply drops its piece, it never blocks the others.</summary>
    public static AlbumReleaseFacts For(IReadOnlyList<Track> tracks, string? releaseDateIso, string? precision, int? year,
                                        DateTimeOffset? releaseInstant, string? label, string? courtesy, string? copyright,
                                        DateTimeOffset now)
    {
        tracks ??= Array.Empty<Track>();

        string? songs = null;
        string? length = null;
        if (tracks.Count > 0)
        {
            // On a PARTLY released album the plain count and the summed length both lie: the count includes tracks
            // that are not out, and the length silently omits their unknown durations, so "12 songs · 31 min" would
            // describe a record that does not exist yet. Report what is actually out, and measure only that.
            int outNow = 0;
            long ms = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].IsNotYetOut()) continue;
                outNow++;
                ms += tracks[i].DurationMs;
            }
            songs = outNow == tracks.Count
                ? tracks.Count.ToString(CultureInfo.InvariantCulture)
                : outNow.ToString(CultureInfo.InvariantCulture) + " of " + tracks.Count.ToString(CultureInfo.InvariantCulture);
            if (ms > 0) length = TotalTimeLiteral(ms);
        }

        string? released = FormatReleaseDate(releaseDateIso, precision)
            ?? (year is > 0 ? year.Value.ToString(CultureInfo.InvariantCulture) : null);
        bool releasesInFuture = releaseInstant is { } ri && ri > now;
        string? lbl = label is { Length: > 0 } ? label : null;

        // [courtesy, copyright] in that fixed order, present-only. Copyright is a newline-joined multi-line string on
        // the wire (several notices glued with '\n') and stays ONE entry here — the view wraps and breaks it, this
        // layer only decides whether the line exists.
        List<string>? notes = null;
        if (courtesy is { Length: > 0 }) (notes ??= new List<string>(2)).Add(courtesy);
        if (copyright is { Length: > 0 }) (notes ??= new List<string>(2)).Add(copyright);

        return new AlbumReleaseFacts(songs, length, released, releasesInFuture, lbl,
            (IReadOnlyList<string>?)notes ?? Array.Empty<string>());
    }

    /// <summary>ISO date + Spotify precision: YEAR → "2014"; MONTH → "November 2014"; DAY (default) → "November 4,
    /// 2014"; null/unparseable → null. Moved here from <c>DetailPage.FormatReleaseDate</c> — it is a RULE, not a
    /// mapper concern, and the mapper's old unparseable-string fallback (echoing the raw ISO text) is dropped: a date
    /// this file cannot parse is not a date this block can honestly show.</summary>
    public static string? FormatReleaseDate(string? iso, string? precision)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            return null;
        return (precision ?? "").ToUpperInvariant() switch
        {
            "YEAR" => d.ToString("yyyy", CultureInfo.InvariantCulture),
            "MONTH" => d.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            _ => d.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture),
        };
    }

    // `DetailFormat.TotalTime` (Features/Detail/DetailConfig.cs) forwards to the loc runtime
    // (Strings.Detail.DurationHrMin/DurationMin, FluentGpu.Localization) and is not source-included into
    // Wavee.Tests — and this file must stay engine-free regardless (it is pinned by tests with no FluentGpu
    // reference), the same reason TrackExpandedFacts keeps its own TrackTime/Bpm formatters rather than calling into
    // DetailFormat. So the total-duration phrase is inlined here: identical arithmetic to DetailFormat.TotalTime
    // (hours + minutes, a sub-minute total floors up to "1 min" rather than reading "0 min"), spelled literally.
    static string TotalTimeLiteral(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        int h = (int)t.TotalHours, m = t.Minutes;
        return h >= 1 ? $"{h} hr {m} min" : $"{Math.Max(1, m)} min";
    }
}
