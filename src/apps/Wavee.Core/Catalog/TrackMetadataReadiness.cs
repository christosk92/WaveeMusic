namespace Wavee.Core.Catalog;

public enum TrackTitleState : byte { Ready, Loading, Offline, Unavailable }

/// <summary>Presentation facts for partially known track rows. Membership is independent of metadata readiness.</summary>
public static class TrackMetadataReadiness
{
    public static TrackTitleState Title(Track track, ResourceSnapshot? identity, bool queryFailed = false)
    {
        // Keep a usable cached label even while its resource is refreshing or the refresh failed.
        if (!string.IsNullOrWhiteSpace(track.Title) && track.Title != track.Uri)
            return TrackTitleState.Ready;
        if (identity?.Activity == ResourceActivity.Offline) return TrackTitleState.Offline;
        if (identity?.Activity is ResourceActivity.Queued or ResourceActivity.Fetching) return TrackTitleState.Loading;
        // A failed cold read of the local cache is not an answer about the track: the key is still Unknown and the
        // provider fetch the query queues for it decides. Presenting it as "unavailable" flashed a terminal state on
        // rows that filled in a few frames later.
        if (identity is { Knowledge: Knowledge.Unknown, Error.Kind: ResourceErrorKind.Persistence }) return TrackTitleState.Loading;
        // An answer that CARRIES a title is never "unavailable" — the row's Track is simply a publication behind it.
        // The identity resource and the Track record come out of ONE query as TWO publications: the resources
        // dictionary flips to Present the frame the extended-metadata batch answers, and the model's Track records are
        // rebuilt from it a background projection later. For the one to three frames in between, EVERY row on a freshly
        // opened playlist was a title-less Track paired with an answered identity, and the ladder below read that as
        // the terminal "Track details unavailable" complete with a Retry button. Pairing the two dictionaries into one
        // snapshot (TrackRowsSnapshot.Resources) made the pair CONSISTENT but not correct: the consistent pair is still
        // (new resources, old model). The label is known, so the row waits for it — shimmer, not a retry affordance.
        // Hoisted out of the ladder below only so it keeps winning over the rule that follows: a failed QUERY is a
        // page-level verdict about the whole batch, and a stale per-row label must not paper over it.
        if (queryFailed) return TrackTitleState.Unavailable;
        if (identity is { Knowledge: Knowledge.Present } && HasTitle(identity.Value)) return TrackTitleState.Loading;
        if (identity?.Error is not null || identity?.Knowledge is Knowledge.Absent or Knowledge.Unsupported
            || identity is { Knowledge: Knowledge.Present, Provenance: CatalogProvenance.Provider }
            // Present via an inline seed (a gid-only disc track, an artist ref) with no title and no in-flight
            // work (Activity is Idle here — Offline/Queued/Fetching already returned above) has no label coming:
            // Unavailable (with Retry), never an eternal Loading.
            || identity is { Knowledge: Knowledge.Present, Activity: ResourceActivity.Idle, Provenance: not CatalogProvenance.Provider })
            return TrackTitleState.Unavailable;
        return TrackTitleState.Loading;
    }

    /// <summary>Does this identity answer actually carry the label the row is waiting for? Only the two identity
    /// facets a track row is ever projected from can — anything else is not an answer about this row's title.</summary>
    static bool HasTitle(CatalogValue? value) => value switch
    {
        TrackIdentityValue t => !string.IsNullOrWhiteSpace(t.Title),
        EpisodeIdentityValue e => !string.IsNullOrWhiteSpace(e.Title),
        _ => false,
    };

    /// <summary>A total exists only for complete membership with every included duration known.
    /// A partial sum must never be labelled as the collection's duration.</summary>
    public static long? CompleteDuration(IReadOnlyList<Track> tracks, int expectedCount,
        bool membershipLoaded = true, bool releasedOnly = false)
    {
        if (!membershipLoaded || tracks.Count != expectedCount || expectedCount == 0) return null;
        long total = 0;
        foreach (var track in tracks)
        {
            if (releasedOnly && track.IsNotYetOut()) continue;
            if (track.DurationMs <= 0) return null;
            total += track.DurationMs;
        }
        return total > 0 ? total : null;
    }
}
