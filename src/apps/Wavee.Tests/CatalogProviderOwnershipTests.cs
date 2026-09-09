using System;
using System.Linq;
using System.Threading.Tasks;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class FakeSourceOwnsTests
{
    static readonly FakeSource Fake = new();

    [Theory]
    [InlineData("fake:album:1")]
    [InlineData("fake:track:9")]
    [InlineData("al7")]     // the bare legacy ids FakeData mints
    [InlineData("tr3")]
    [InlineData("pl2")]
    [InlineData("ar11")]
    public void OwnsItsOwnNamespace(string uri) => Assert.True(Fake.Owns(uri));

    [Theory]
    [InlineData("spotify:track:1")]
    [InlineData("local:file:x")]
    [InlineData("wavee:local:file:abc")]
    [InlineData("wavee:playlist:1")]
    [InlineData("wavee:show:1")]
    [InlineData("wavee:episode:1")]
    [InlineData("wavee:mystery:9")]
    public void DoesNotOwnAPeersNamespace_NorTheUnclaimed(string uri) => Assert.False(Fake.Owns(uri));

    [Fact]
    public void DeclaresCatalogAndFallback()
    {
        Assert.True(Fake.Capabilities.HasFlag(SourceCapabilities.Catalog));
        Assert.True(Fake.Capabilities.HasFlag(SourceCapabilities.Fallback));
    }

    [Fact]
    public void ThePeerSourcesKeepTheirOwnUris_InTheDemoRegistry()
    {
        var reg = DemoRegistry();
        Assert.Equal("local", reg.OwnerOf("local:track:1")!.Id);
        Assert.Equal("local", reg.OwnerOf("wavee:local:file:abc")!.Id);
        Assert.Equal("user-playlists", reg.OwnerOf("wavee:playlist:1")!.Id);
        Assert.Equal("fake", reg.OwnerOf("fake:album:1")!.Id);
        Assert.Null(reg.OwnerOf("wavee:mystery:9"));   // unowned — a READ falls back, routing does not
    }

    [Fact]
    public async Task AnUnownedUriStillOpens_ThroughTheFallbackCapability()
    {
        await using var host = new CatalogQueryTestHost(DemoRegistry().All.ToArray());
        var cat = host.Library;

        var album = await cat.GetAlbumAsync("wavee:mystery:9");
        Assert.False(string.IsNullOrEmpty(album.Name));       // NOT the minimal (id, id, "") empty shape
        Assert.NotEmpty(album.Tracks ?? Array.Empty<Track>());

        // …and an owned uri whose owner has NO data (a wavee:playlist: the session never created) falls through too.
        await Assert.ThrowsAsync<InvalidOperationException>(() => cat.GetPlaylistAsync("wavee:playlist:999"));
    }

    /// <summary>The demo backend's catalog sources in Services.CreateFake order, minus the export (which needs a
    /// loaded fixture) — the fallback is last, exactly as registered.</summary>
    static SourceRegistry DemoRegistry() => new(new ISource[]
    {
        new LocalSource(),
        new UserPlaylistSource(),
        new FakeSource(),
        new FakePodcastSource(),
    });
}
