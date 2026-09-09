namespace Wavee.Core.Catalog;

[Flags]
public enum TrackPresentationFacts : byte
{
    None = 0,
    PlayCount = 1,
    Audio = 2,
    Video = 4,
    Descriptors = 8,
    Availability = 16,
}

/// <summary>One mapping for the optional facts a surface paints and the demand it submits.</summary>
public static class TrackPresentationRequirements
{
    // The small closed set is constructed once, never in a row's paint binding.
    static readonly IReadOnlyList<FacetKind>[] Sets = BuildSets();

    public static IReadOnlyList<FacetKind> RequiredFacets(TrackPresentationFacts facts)
    {
        int index = (int)facts;
        if ((uint)index >= Sets.Length) throw new ArgumentOutOfRangeException(nameof(facts));
        return Sets[index];
    }

    public static TrackPresentationFacts ArtistPopular => TrackPresentationFacts.PlayCount | TrackPresentationFacts.Video;

    static IReadOnlyList<FacetKind>[] BuildSets()
    {
        var sets = new IReadOnlyList<FacetKind>[32];
        for (int i = 0; i < sets.Length; i++)
        {
            var facets = new List<FacetKind>(5);
            if ((i & (int)TrackPresentationFacts.PlayCount) != 0) facets.Add(FacetKind.PlayCount);
            if ((i & (int)TrackPresentationFacts.Audio) != 0) facets.Add(FacetKind.AudioAttributes);
            if ((i & (int)TrackPresentationFacts.Video) != 0) facets.Add(FacetKind.VideoAssociation);
            if ((i & (int)TrackPresentationFacts.Descriptors) != 0) facets.Add(FacetKind.Descriptors);
            if ((i & (int)TrackPresentationFacts.Availability) != 0) facets.Add(FacetKind.Availability);
            sets[i] = facets.AsReadOnly();
        }
        return sets;
    }
}

public enum TrackFactState : byte { Pending, Present, Absent, Unsupported, Failed, Offline }

/// <summary>Knowledge is independent of refresh activity. In particular, zero is a valid count.</summary>
public static class TrackFactPresentation
{
    public static TrackFactState State(ResourceSnapshot? resource, bool queryFailed = false)
    {
        if (resource?.Knowledge == Knowledge.Present && resource.Value is not null) return TrackFactState.Present;
        if (resource?.Knowledge == Knowledge.Absent) return TrackFactState.Absent;
        if (resource?.Knowledge == Knowledge.Unsupported) return TrackFactState.Unsupported;
        if (resource?.Activity == ResourceActivity.Offline) return TrackFactState.Offline;
        if (resource?.Activity is ResourceActivity.Queued or ResourceActivity.Fetching or ResourceActivity.Backoff)
            return TrackFactState.Pending;
        if (resource?.Error is not null || queryFailed) return TrackFactState.Failed;
        return TrackFactState.Pending;
    }
}
