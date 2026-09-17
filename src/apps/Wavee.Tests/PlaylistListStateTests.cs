// ── Wavee.Tests/PlaylistListStateTests.cs — WP-5.O stream A ────────────────────────────────────────────────────────
// 0.2.9's PlaylistListStateTests against 0.3's `Playlist.RowsStateOf` / `IsLoading` / `NameOf` (Playlist.cs §4 — the
// existing member IS the port). The four 0.2.9 facts about a `Failed` arm are not ported: 0.3's list state has no Failed
// value — a failed ask with nothing resident is the EDGE's `Readiness` (EdgeState.Failed, G-050), which the table reads
// directly, and a failure with rows resident keeps the rows by construction (the state is computed from the rows).

using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>The detail track list's empty branch: a thin header whose membership has not been adopted must shimmer, never
/// say "Nothing here yet".</summary>
public class PlaylistListStateTests
{
    [Fact]
    public void AThinHeader_IsLoading_NotEmpty()
        => Assert.Equal(PlaylistRowsState.Loading, Playlist.RowsStateOf(membershipKnown: false, total: 0, visible: 0));

    [Fact]
    public void AnAdoptedEmptyMembership_IsEmpty()
        => Assert.Equal(PlaylistRowsState.Empty, Playlist.RowsStateOf(membershipKnown: true, total: 0, visible: 0));

    [Fact]
    public void AFilterThatHidesEveryRow_IsNoMatch()
        => Assert.Equal(PlaylistRowsState.NoMatch, Playlist.RowsStateOf(membershipKnown: true, total: 40, visible: 0));

    [Fact]
    public void RowsAreRows()
        => Assert.Equal(PlaylistRowsState.Rows, Playlist.RowsStateOf(membershipKnown: true, total: 40, visible: 12));

    /// <summary>Rows are proof: a model that carries tracks is never "loading", whatever the flag says.</summary>
    [Fact]
    public void ResidentRows_OutrankAnUnknownMembership()
    {
        Assert.Equal(PlaylistRowsState.Rows, Playlist.RowsStateOf(membershipKnown: false, total: 40, visible: 12));
        Assert.Equal(PlaylistRowsState.NoMatch, Playlist.RowsStateOf(membershipKnown: false, total: 40, visible: 0));
        Assert.False(Playlist.IsLoading(membershipKnown: false, total: 40));
    }

    /// <summary>The whole cold-open sequence: shimmer → rows, or shimmer → "Nothing here yet" — never Empty first.</summary>
    [Fact]
    public void AColdOpen_NeverSaysEmptyBeforeTheSnapshot()
    {
        Assert.Equal(PlaylistRowsState.Loading, Playlist.RowsStateOf(membershipKnown: false, total: 0, visible: 0));
        Assert.Equal(PlaylistRowsState.Rows, Playlist.RowsStateOf(membershipKnown: true, total: 75, visible: 75));
        Assert.Equal(PlaylistRowsState.Empty, Playlist.RowsStateOf(membershipKnown: true, total: 0, visible: 0));
    }

    [Fact]
    public void Names_AreTheDiagnosticsSpelling()
    {
        Assert.Equal("Loading", Playlist.NameOf(PlaylistRowsState.Loading));
        Assert.Equal("Empty", Playlist.NameOf(PlaylistRowsState.Empty));
        Assert.Equal("NoMatch", Playlist.NameOf(PlaylistRowsState.NoMatch));
        Assert.Equal("Rows", Playlist.NameOf(PlaylistRowsState.Rows));
    }
}
