using Wavee.Features.Detail;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The detail track list's empty branch. It used to have one input — the track count — so a Ready playlist whose
/// membership had not been adopted yet (a rootlist-seeded thin header) read as an empty playlist and the page said
/// "Nothing here yet" for the beat before the snapshot landed.
/// </summary>
public class PlaylistListStateTests
{
    [Fact]
    public void AThinHeader_IsLoading_NotEmpty()
        => Assert.Equal(PlaylistRowsState.Loading, PlaylistListState.For(membershipLoaded: false, membershipFailed: false, total: 0, visible: 0));

    [Fact]
    public void AnAdoptedEmptyMembership_IsEmpty()
        => Assert.Equal(PlaylistRowsState.Empty, PlaylistListState.For(membershipLoaded: true, membershipFailed: false, total: 0, visible: 0));

    [Fact]
    public void AFilterThatHidesEveryRow_IsNoMatch()
        => Assert.Equal(PlaylistRowsState.NoMatch, PlaylistListState.For(membershipLoaded: true, membershipFailed: false, total: 40, visible: 0));

    [Fact]
    public void RowsAreRows()
        => Assert.Equal(PlaylistRowsState.Rows, PlaylistListState.For(membershipLoaded: true, membershipFailed: false, total: 40, visible: 12));

    /// <summary>Rows are proof: a model that carries tracks is never "loading", whatever the flag says (the flag is read
    /// from the store a moment after the rows were composed from it, so the two can disagree for one refresh).</summary>
    [Fact]
    public void ResidentRows_OutrankAnUnknownMembership()
    {
        Assert.Equal(PlaylistRowsState.Rows, PlaylistListState.For(membershipLoaded: false, membershipFailed: false, total: 40, visible: 12));
        Assert.Equal(PlaylistRowsState.NoMatch, PlaylistListState.For(membershipLoaded: false, membershipFailed: false, total: 40, visible: 0));
        Assert.False(PlaylistListState.IsLoading(membershipLoaded: false, membershipFailed: false, total: 40));
    }

    /// <summary>The whole cold-open sequence a thin header runs: shimmer → (the snapshot lands) rows, or shimmer →
    /// (the snapshot lands empty) "Nothing here yet". Neither path ever passes through Empty before the snapshot.</summary>
    [Fact]
    public void AColdOpen_NeverSaysEmptyBeforeTheSnapshot()
    {
        var thin = PlaylistListState.For(membershipLoaded: false, membershipFailed: false, total: 0, visible: 0);
        Assert.Equal(PlaylistRowsState.Loading, thin);
        Assert.Equal(PlaylistRowsState.Rows, PlaylistListState.For(membershipLoaded: true, membershipFailed: false, total: 75, visible: 75));
        Assert.Equal(PlaylistRowsState.Empty, PlaylistListState.For(membershipLoaded: true, membershipFailed: false, total: 0, visible: 0));
    }

    [Fact]
    public void Names_AreTheDiagnosticsSpelling()
    {
        Assert.Equal("Loading", PlaylistListState.Name(PlaylistRowsState.Loading));
        Assert.Equal("Empty", PlaylistListState.Name(PlaylistRowsState.Empty));
        Assert.Equal("NoMatch", PlaylistListState.Name(PlaylistRowsState.NoMatch));
        Assert.Equal("Rows", PlaylistListState.Name(PlaylistRowsState.Rows));
        Assert.Equal("Failed", PlaylistListState.Name(PlaylistRowsState.Failed));
    }

    /// <summary>A dead fetch is its own state. This is the regression behind a playlist that sat in shimmer rows for
    /// nine minutes: the membership fetch 400'd, every layer swallowed it, and with only "are rows resident" to go on
    /// the list could not tell a failed fetch from one still in flight.</summary>
    [Fact]
    public void FailedFetch_WithNothingResident_IsFailedNotLoading()
    {
        Assert.Equal(PlaylistRowsState.Failed, PlaylistListState.For(membershipLoaded: false, membershipFailed: true, total: 0, visible: 0));
        Assert.False(PlaylistListState.IsLoading(membershipLoaded: false, membershipFailed: true, total: 0));
        Assert.True(PlaylistListState.IsFailed(membershipLoaded: false, membershipFailed: true, total: 0));
    }

    /// <summary>Rows beat a failure. A revalidate that dies against a baseline we already hold is a staleness problem,
    /// never a reason to blank good rows into an error panel.</summary>
    [Fact]
    public void FailedFetch_WithResidentRows_StillShowsRows()
    {
        Assert.Equal(PlaylistRowsState.Rows, PlaylistListState.For(membershipLoaded: true, membershipFailed: true, total: 40, visible: 12));
        Assert.Equal(PlaylistRowsState.Rows, PlaylistListState.For(membershipLoaded: false, membershipFailed: true, total: 40, visible: 12));
        Assert.False(PlaylistListState.IsFailed(membershipLoaded: false, membershipFailed: true, total: 40));
    }

    /// <summary>An empty playlist that LANDED keeps saying "Nothing here yet" even if a later fetch failed — the
    /// verdict already arrived, so the failure has nothing to add.</summary>
    [Fact]
    public void LandedEmpty_OutranksALaterFailure()
        => Assert.Equal(PlaylistRowsState.Empty, PlaylistListState.For(membershipLoaded: true, membershipFailed: true, total: 0, visible: 0));
}
