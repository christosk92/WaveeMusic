// ── Wavee.Tests/TempoFilterTests.cs — the tempo band, the camelot key and the descriptor-chip facets ────────────────
//
// Wave 4.5's gate for the enrichment facets of `Track.FilterModel` (Entities/Track.Rules.cs), ported VERBATIM from
// 0.2.9's TempoFilterTests (70 lines). The shapes moved: a tempo is a double on the row (0 = no kind-222 answer, which
// is what 0.2.9's null meant), the key is the one-based camelot BYTE the decoder writes, and a tag is an interned
// StringId resolved and compared OrdinalIgnoreCase. The camelot fact still pins "case-insensitive": both spellings go
// through `Spotify.Decode.CamelotCode`, which is where the case now folds.
//
// Touches the process-wide interner (the tag ids), so it joins EntitiesCollection.

using FluentGpu.Foundation;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class TempoFilterTests
{
    static readonly long Now = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    static Track.FilterRow T(double bpm = 0d, byte camelot = 0, params string[] tags) => new()
    {
        Title = "Title",
        DurationMs = 180_000,
        TempoBpm = bpm,
        Camelot = camelot,
        Tags = Intern(tags),
    };

    static StringId[] Intern(string[] tags)
    {
        var ids = new StringId[tags.Length];
        for (int i = 0; i < tags.Length; i++) ids[i] = Entities.Strings.Intern(tags[i]);
        return ids;
    }

    static byte Camelot(string code) => Spotify.Decode.CamelotCode(System.Text.Encoding.UTF8.GetBytes(code));

    static bool Match(in Track.FilterRow t, in Track.FilterState f)
        => Track.FilterModel.Matches(in t, "", in f, Now);

    [Theory]
    [InlineData(75, Track.TempoBand.Under90, true)]
    [InlineData(89.9, Track.TempoBand.Under90, true)]
    [InlineData(90, Track.TempoBand.Under90, false)]
    [InlineData(90, Track.TempoBand.From90To119, true)]
    [InlineData(119.9, Track.TempoBand.From90To119, true)]
    [InlineData(120, Track.TempoBand.From90To119, false)]
    [InlineData(133, Track.TempoBand.From120To139, true)]
    [InlineData(140, Track.TempoBand.From120To139, false)]
    [InlineData(174, Track.TempoBand.From140AndUp, true)]
    public void BandBoundariesAreHalfOpen(double bpm, Track.TempoBand band, bool expected)
        => Assert.Equal(expected, Match(T(bpm), Track.FilterState.Default with { Tempo = band }));

    [Fact]
    public void UnknownTempoMatchesOnlyAny()
    {
        // A track with no kind-222 payload must not be swept into a band — but it must survive the default filter.
        Assert.True(Match(T(), Track.FilterState.Default));
        Assert.False(Match(T(), Track.FilterState.Default with { Tempo = Track.TempoBand.Under90 }));
        Assert.False(Match(T(bpm: 0), Track.FilterState.Default with { Tempo = Track.TempoBand.Under90 }));
    }

    [Fact]
    public void CamelotCodeMatchesCaseInsensitively()
    {
        Assert.True(Match(T(camelot: Camelot("8B")), Track.FilterState.Default with { Camelot = Camelot("8b") }));
        Assert.False(Match(T(camelot: Camelot("8B")), Track.FilterState.Default with { Camelot = Camelot("11A") }));
        Assert.False(Match(T(camelot: 0), Track.FilterState.Default with { Camelot = Camelot("8B") }));
    }

    [Fact]
    public void TagFilterMatchesDisplayNameCaseInsensitively()
    {
        Assert.True(Match(T(tags: "K-Pop"), Track.FilterState.Default with { Tag = "k-pop" }));
        Assert.False(Match(T(tags: "K-Pop"), Track.FilterState.Default with { Tag = "Jazz" }));
        Assert.False(Match(T(), Track.FilterState.Default with { Tag = "K-Pop" }));
    }

    [Fact]
    public void ActiveCountCountsEachNewFacetOnce()
    {
        var f = Track.FilterState.Default with
        {
            Tempo = Track.TempoBand.From120To139, Camelot = Camelot("8B"), Tag = "K-Pop",
        };
        Assert.Equal(3, f.ActiveCount);
        Assert.False(f.IsDefault);
        Assert.Equal(0, Track.FilterState.Default.ActiveCount);
    }
}
