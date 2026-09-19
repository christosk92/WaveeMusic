using System;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class TempoFilterTests
{
    static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    static Track T(double? bpm = null, string? camelot = null, params string[] tags) => new(
        "id", "spotify:track:id", "Title",
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
        180_000, false, null,
        TempoBpm: bpm, CamelotCode: camelot,
        Tags: tags.Length == 0 ? null : tags);

    static bool Match(Track t, TrackFilterState f)
        => TrackFilterModel.Matches(t, "", f, hasVideo: false, isSaved: false, Now);

    [Theory]
    [InlineData(75, TrackTempoBand.Under90, true)]
    [InlineData(89.9, TrackTempoBand.Under90, true)]
    [InlineData(90, TrackTempoBand.Under90, false)]
    [InlineData(90, TrackTempoBand.From90To119, true)]
    [InlineData(119.9, TrackTempoBand.From90To119, true)]
    [InlineData(120, TrackTempoBand.From90To119, false)]
    [InlineData(133, TrackTempoBand.From120To139, true)]
    [InlineData(140, TrackTempoBand.From120To139, false)]
    [InlineData(174, TrackTempoBand.From140AndUp, true)]
    public void BandBoundariesAreHalfOpen(double bpm, TrackTempoBand band, bool expected)
        => Assert.Equal(expected, Match(T(bpm), TrackFilterState.Default with { Tempo = band }));

    [Fact]
    public void UnknownTempoMatchesOnlyAny()
    {
        // A track with no kind-222 payload must not be swept into a band — but it must survive the default filter.
        Assert.True(Match(T(bpm: null), TrackFilterState.Default));
        Assert.False(Match(T(bpm: null), TrackFilterState.Default with { Tempo = TrackTempoBand.Under90 }));
        Assert.False(Match(T(bpm: 0), TrackFilterState.Default with { Tempo = TrackTempoBand.Under90 }));
    }

    [Fact]
    public void CamelotCodeMatchesCaseInsensitively()
    {
        Assert.True(Match(T(camelot: "8B"), TrackFilterState.Default with { CamelotCode = "8b" }));
        Assert.False(Match(T(camelot: "8B"), TrackFilterState.Default with { CamelotCode = "11A" }));
        Assert.False(Match(T(camelot: null), TrackFilterState.Default with { CamelotCode = "8B" }));
    }

    [Fact]
    public void TagFilterMatchesDisplayNameCaseInsensitively()
    {
        Assert.True(Match(T(tags: "K-Pop"), TrackFilterState.Default with { Tag = "k-pop" }));
        Assert.False(Match(T(tags: "K-Pop"), TrackFilterState.Default with { Tag = "Jazz" }));
        Assert.False(Match(T(), TrackFilterState.Default with { Tag = "K-Pop" }));
    }

    [Fact]
    public void ActiveCountCountsEachNewFacetOnce()
    {
        var f = TrackFilterState.Default with
        {
            Tempo = TrackTempoBand.From120To139, CamelotCode = "8B", Tag = "K-Pop",
        };
        Assert.Equal(3, f.ActiveCount);
        Assert.False(f.IsDefault);
        Assert.Equal(0, TrackFilterState.Default.ActiveCount);
    }
}
