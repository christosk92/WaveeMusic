using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The two surface tables (design §2.4). Pinned exhaustively because they are the ONLY thing deciding what a POST
// carries — the four separate services each had their own answer, which is how the album page ended up asking for
// kind 185 twice and the show page asking for nothing.
public class TraitPolicyTests
{
    static TraitPolicy Policy() => new();

    [Theory]
    [InlineData(TraitSurface.AlbumOpen, TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing)]
    [InlineData(TraitSurface.ShowOpen, TraitSet.RowBundle)]
    [InlineData(TraitSurface.ArtistPopular, TraitSet.RowBundle | TraitSet.PlayCount)]
    // List surfaces ask kind 185 unconditionally: it has no retry surface, so gating it on the column
    // setting permanently starved lists hydrated while the column was off. Visibility is UI-only now.
    [InlineData(TraitSurface.PlaylistOpen, TraitSet.RowBundle | TraitSet.PlayCount)]
    [InlineData(TraitSurface.LikedSongs, TraitSet.RowBundle | TraitSet.PlayCount)]
    [InlineData(TraitSurface.Queue, TraitSet.RowBundle)]
    [InlineData(TraitSurface.Search, TraitSet.RowBundle)]
    [InlineData(TraitSurface.Recents, TraitSet.IdentityTraits | TraitSet.VisualIdentity)]
    [InlineData(TraitSurface.NowPlaying, TraitSet.Video)]
    [InlineData(TraitSurface.None, TraitSet.None)]
    [InlineData(TraitSurface.Prefetch, TraitSet.None)]
    [InlineData(TraitSurface.Context, TraitSet.None)]
    [InlineData(TraitSurface.Credits, TraitSet.None)]
    public void For_IsTheTable(TraitSurface surface, TraitSet expected)
        => Assert.Equal(expected, Policy().For(surface));

    [Theory]
    [InlineData(TraitSurface.Recents, "mdata_esperanto")]
    [InlineData(TraitSurface.AlbumOpen, "track_metadata_loader")]
    [InlineData(TraitSurface.PlaylistOpen, "track_metadata_loader")]
    [InlineData(TraitSurface.None, null)]
    [InlineData(TraitSurface.PreRelease, null)]
    [InlineData(TraitSurface.UserProfiles, null)]
    public void ClientFeatureId_IsTheAttributionTable(TraitSurface surface, string? expected)
        => Assert.Equal(expected, surface.ClientFeatureId());
}
