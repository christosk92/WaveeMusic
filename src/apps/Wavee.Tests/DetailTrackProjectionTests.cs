using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class DetailTrackProjectionTests
{
    static Track Song(string id, string title, bool explicitTrack = false) => new(id, "spotify:track:" + id,
        title, [], new("album", "spotify:album:album", "Album"), 123000, explicitTrack, null);

    static readonly TrackFacts AllKnown = TrackFacts.Delegated(static (_, _) => true, static _ => false);

    [Theory]
    [InlineData(12)]
    [InlineData(31)]
    [InlineData(50)]
    [InlineData(327)]
    [InlineData(1494)]
    [InlineData(10000)]
    public void NavigationFixtureSizesPublishOneCoherentOccurrenceMap(int count)
    {
        // Duplicate identities are separate playlist occurrences, including at the scaling boundary.
        var rows = Enumerable.Range(0, count).Select(i => Song((i % 7).ToString(), $"Title {i:D5}")).ToArray();
        int factReads = 0;
        var facts = TrackFacts.Delegated((_, _) => { factReads++; return true; }, static _ => false);
        var projected = DetailTrackProjection.Build(rows, new(SortColumn.Title, true), "", default, null,
            facts, DateTimeOffset.UnixEpoch);
        Assert.Same(rows, projected.Tracks);
        Assert.Equal(Enumerable.Range(0, count).Reverse(), projected.Indices);
        Assert.Equal(count, projected.Indices.Distinct().Count());
        // A resting list (no query, no filters) reads NO knowledge at all: every Known probe exists to relax a filter,
        // and a filter at its default cannot be relaxed. This pass used to take five to seven lookups per track on
        // every one of the ~20 publications a cold open produces.
        Assert.Equal(0, factReads);
        Assert.All(projected.Indices, index => Assert.Same(rows[index], projected.Tracks[index]));
    }

    [Fact]
    public void StableDescendingSortRetainsDuplicateMembershipAndExactSource()
    {
        Track[] rows = [Song("same", "A"), Song("b", "B"), Song("same", "A")];
        var projected = DetailTrackProjection.Build(rows, new(SortColumn.Title, true), "", default, null,
            AllKnown, DateTimeOffset.UtcNow);
        Assert.Same(rows, projected.Tracks);
        Assert.Equal([1, 0, 2], projected.Indices);
        Assert.Same(rows[0], projected.Tracks[projected.Indices[1]]);
        Assert.Same(rows[2], projected.Tracks[projected.Indices[2]]);
    }

    [Fact]
    public void UnknownFilterFactsRetainCandidatesAndKnownFactsApplyFilter()
    {
        Track[] rows = [Song("a", "A", explicitTrack: true), Song("b", "B")];
        var filters = new TrackFilterState(ExplicitMode: TrackTraitMode.Hide);
        var pending = DetailTrackProjection.Build(rows, default, "", filters, null,
            TrackFacts.Delegated(static (_, _) => false, static _ => false), DateTimeOffset.UtcNow);
        var known = DetailTrackProjection.Build(rows, default, "", filters, null, AllKnown, DateTimeOffset.UtcNow);
        Assert.Equal([0, 1], pending.Indices);
        Assert.Equal([1], known.Indices);
        Assert.Same(rows, pending.Tracks);
        Assert.Same(rows, known.Tracks);
    }

    [Fact]
    public void MembershipShrinkDoesNotMutateAPreviouslyPublishedMapping()
    {
        Track[] before = [Song("a", "A"), Song("b", "B"), Song("c", "C")];
        Track[] after = [before[2]];
        var old = DetailTrackProjection.Build(before, new(SortColumn.Title, true), "", default, null,
            AllKnown, DateTimeOffset.UtcNow);
        var next = DetailTrackProjection.Build(after, new(SortColumn.Title, true), "", default, null,
            AllKnown, DateTimeOffset.UtcNow);
        Assert.Equal([2, 1, 0], old.Indices);
        Assert.Equal([0], next.Indices);
        Assert.Same(before[2], old.Tracks[old.Indices[0]]);
        Assert.Same(after[0], next.Tracks[next.Indices[0]]);
    }
}
