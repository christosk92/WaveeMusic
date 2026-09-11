using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Catalog;
using Wavee.Backend.Playlists;
using Wavee.Backend.Sync;
using Wavee.Backend.Spotify;
using Wavee.Backend;
using Wavee.Core;
using Wavee.Core.Catalog;
using Xunit;

namespace Wavee.Tests;

public sealed class EpisodeInPlaylistJoinTests
{
    const string PlaylistUri = "spotify:playlist:mixed";
    const string EpisodeUri = "spotify:episode:e1";
    const string ShowUri = "spotify:show:s1";
    const string TrackUri = "spotify:track:t1";

    static async Task Seed(CatalogQueryTestHost host, string? showUri)
    {
        await host.SeedAsync(new Track("t1", TrackUri, "Song", [], new("", "", ""), 1000, false, null));
        await host.SeedAsync(new Episode("e1", EpisodeUri, "Ep One", "The Show", new("https://img/ep"),
            1_800_000, DateTimeOffset.UnixEpoch, ShowUri: showUri));
        await host.Data.Replicas.AdoptPlaylistAsync(new(PlaylistUri, PlaylistReadKind.Snapshot, null,
            new byte[24], [new("i1", TrackUri, null, 0), new("i2", EpisodeUri, "alice", 1_700_000_000_000)], [],
            new("mixed", PlaylistUri, "Mixed", null, "Me", null, 2)));
    }

    [Theory]
    [InlineData(ShowUri)]
    [InlineData(null)]
    public async Task PlaylistRetainsEpisodeIdentityAndOccurrence_WithAnOptionalShowLink(string? showUri)
    {
        await using var host = new CatalogQueryTestHost();
        await Seed(host, showUri);
        using var handle = host.Data.Queries.Acquire(new PlaylistDetailQuery(host.Scope, PlaylistUri));
        await QueryPublication.Initial(handle);
        var playlist = handle.Current.Value;
        Assert.Equal(2, playlist.Tracks!.Count);
        var row = playlist.Tracks[1];
        Assert.Equal("e1", row.Id); Assert.Equal(EpisodeUri, row.Uri); Assert.Equal("Ep One", row.Title);
        Assert.Empty(row.Artists); Assert.Equal("The Show", row.Album.Name);
        Assert.Equal(showUri ?? "", row.Album.Uri); Assert.Equal("podcast", row.Source);
        Assert.Equal("i2", row.ContextUid); Assert.Equal("alice", row.AddedBy);
    }

    [Fact]
    public async Task ContextStreamPreservesTheEpisodeAndItsOrder()
    {
        await using var host = new CatalogQueryTestHost();
        await Seed(host, ShowUri);
        var titles = new List<string>();
        await foreach (var page in host.Library.StreamTracksAsync(PlaylistUri))
            foreach (var track in page.Tracks) titles.Add(track.Title);
        Assert.Equal(new[] { "Song", "Ep One" }, titles);
    }

    [Fact]
    public async Task MissingShowReferenceInASeedCannotEraseTheKnownLink()
    {
        await using var host = new CatalogQueryTestHost();
        await Seed(host, ShowUri);
        await host.SeedAsync(new Episode("e1", EpisodeUri, "Thin", "The Show", null, 0, default));
        var value = Assert.IsType<EpisodeIdentityValue>(host.Data.Catalog.Peek(new(host.Scope, EpisodeUri, FacetKind.EpisodeIdentity)).Value);
        Assert.Equal(ShowUri, value.ShowUri);
    }

    [Fact]
    public void EpisodeWireDecoderRetainsTheEmbeddedShowUri()
    {
        var episode = new Wavee.Protocol.Metadata.Episode { Name = "Ep One", Duration = 5000,
            Show = new() { Name = "The Show", Gid = Google.Protobuf.ByteString.CopyFrom(new byte[16]) } };
        var scope = new CatalogScope("spotify", "test", "en", "NL", "premium", 1, false);
        var result = SpotifyCatalogDecoder.Decode(new(scope, EpisodeUri, FacetKind.EpisodeIdentity), episode.ToByteString());
        Assert.Equal("spotify:show:" + Base62.Encode(new byte[16]), Assert.IsType<EpisodeIdentityValue>(result.Patch.Apply(null)).ShowUri);
    }
}
