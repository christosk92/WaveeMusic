// ── Wavee.Tests/EpisodeNeighboursTests.cs — the episode page's next/previous doors (podcast plan §5.2, §10) ─────────
//
// Next is the NEWER neighbour and previous the older, in every consumption order (the prototype's `chrono[k+1]` /
// `chrono[k-1]`); the order only changes the doors' copy. Answers are SLOTS from the list, -1 where there is none.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class EpisodeNeighboursTests
{
    // newest first: slot 50 is the latest episode, slot 10 the first
    static readonly int[] Slots = [50, 40, 30, 20, 10];

    [Fact]
    public void Middle_NextIsNewer_PreviousIsOlder()
        => Assert.Equal((40, 20), EpisodeNeighbours.Of(Slots, 30));

    [Fact]
    public void TheLatest_HasNoNext()
        => Assert.Equal((-1, 40), EpisodeNeighbours.Of(Slots, 50));

    [Fact]
    public void TheFirst_HasNoPrevious()
        => Assert.Equal((20, -1), EpisodeNeighbours.Of(Slots, 10));

    [Fact]
    public void AnAbsentEpisode_HasNeither()
    {
        Assert.Equal((-1, -1), EpisodeNeighbours.Of(Slots, 35));
        Assert.Equal((-1, -1), EpisodeNeighbours.Of(Slots, -1));
    }

    [Fact]
    public void ASingleEpisode_OrNone_HasNeither()
    {
        Assert.Equal((-1, -1), EpisodeNeighbours.Of([7], 7));
        Assert.Equal((-1, -1), EpisodeNeighbours.Of([], 7));
    }
}
