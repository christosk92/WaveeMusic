using FluentAssertions;
using Google.Protobuf;
using Moq;
using Wavee.Core.Http;
using Wavee.Protocol.Metadata;
using Wavee.UI.Services.Tracks;

namespace Wavee.UI.Tests.Services;

public sealed class PreviewUrlResolverTests
{
    private static Track TrackWithPreview(byte[] fileId)
    {
        var t = new Track();
        t.Preview.Add(new AudioFile { FileId = ByteString.CopyFrom(fileId) });
        return t;
    }

    private static (PreviewUrlResolver r, Mock<IExtendedMetadataClient> m) Make(Track? track)
    {
        var m = new Mock<IExtendedMetadataClient>();
        m.Setup(c => c.GetTrackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(track);
        return (new PreviewUrlResolver(m.Object), m);
    }

    [Fact]
    public async Task Builds_preview_url_from_file_id()
    {
        var (r, _) = Make(TrackWithPreview(new byte[] { 0x28, 0xe6, 0x27, 0x56 }));
        var url = await r.ResolveAsync("spotify:track:4uLU6hMCjMI75M1A2tKUQC");
        url.Should().Be("https://p.scdn.co/mp3-preview/28e62756");
    }

    [Fact]
    public async Task Returns_null_when_track_has_no_preview()
    {
        var (r, _) = Make(new Track());
        (await r.ResolveAsync("spotify:track:4uLU6hMCjMI75M1A2tKUQC")).Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_metadata_missing()
    {
        var (r, _) = Make(null);
        (await r.ResolveAsync("spotify:track:4uLU6hMCjMI75M1A2tKUQC")).Should().BeNull();
    }

    [Fact]
    public async Task Caches_so_metadata_is_fetched_once_per_uri()
    {
        var (r, m) = Make(TrackWithPreview(new byte[] { 0x01, 0x02 }));
        await r.ResolveAsync("spotify:track:4uLU6hMCjMI75M1A2tKUQC");
        await r.ResolveAsync("spotify:track:4uLU6hMCjMI75M1A2tKUQC");
        m.Verify(c => c.GetTrackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prefetch_swallows_per_item_failures()
    {
        var m = new Mock<IExtendedMetadataClient>();
        m.Setup(c => c.GetTrackAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("offline"));
        var r = new PreviewUrlResolver(m.Object);
        var act = async () => await r.PrefetchAsync(new[] { "spotify:track:4uLU6hMCjMI75M1A2tKUQC" });
        await act.Should().NotThrowAsync();
    }
}
