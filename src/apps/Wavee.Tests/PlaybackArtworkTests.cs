using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class PlaybackArtworkTests
{

    // The projection no longer holds a TrackResolver Func: it asks THE façade for the playable's Open rung and then
    // RE-READS THE STORE (design §1.5). A test therefore supplies both halves — a hydrator whose "the transport
    // landed" writes the full row, and the store it lands in.
    static NowPlayingProjection Enriching(InMemoryStore store, Func<string, Track> landed)
    {
        var rec = new RecordingHydrator(store)
        {
            OnEnsureMany = uris => { foreach (var u in uris) store.UpsertTrack(landed(u)); },
        };
        return new NowPlayingProjection("us", rec, store, () => 0);
    }
    static readonly Image Cover = new("https://i.scdn.co/image/cover", 300, 300);
    static readonly ArtistRef Artist = new("ar", "spotify:artist:ar", "Arash");
    static readonly AlbumRef Album = new("al", "spotify:album:al", "SUPERMAN");

    [Fact]
    public void StoreMerge_ThinTrack_DoesNotEraseRichArtwork()
    {
        var store = new InMemoryStore();
        var rich = new Track("tr", "spotify:track:tr", "Broken Angel", [Artist], Album, 180000, false, Cover,
            PlayCount: 42, Source: "rich");
        var thin = new Track("tr", "spotify:track:tr", "", [], new AlbumRef("", "", ""), 0, false, null);

        store.UpsertTrack(rich);
        store.UpsertTrack(thin);

        var merged = store.GetTrack("spotify:track:tr");
        Assert.NotNull(merged);
        Assert.Equal("Broken Angel", merged!.Title);
        Assert.Same(Cover, merged.Image);
        Assert.Equal("Arash", merged.Artists[0].Name);
        Assert.Equal("SUPERMAN", merged.Album.Name);
        // (has-video is no longer a Track field the merge must defend — it lives on the VideoAssociation plane)
        Assert.Equal(42, merged.PlayCount);
    }

    [Fact]
    public async Task NowPlayingProjection_EnrichesThinClusterTrack_WithArtwork()
    {
        var store = new InMemoryStore();
        var p = Enriching(store, uri => new Track("tr", uri, "Broken Angel", [Artist], Album, 180000, false, Cover));
        var changed = new TaskCompletionSource<Track?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = p.Changes.Subscribe(Observers.From<IPlaybackState>(s =>
        {
            if (s.CurrentTrack?.Image is not null) changed.TrySetResult(s.CurrentTrack);
        }));

        p.OnCluster(new ClusterDelta("other", true,
            new RemoteTrack("spotify:track:tr", "Broken Angel", "", "", "SUPERMAN", "spotify:album:al", null, 180000),
            "spotify:album:al", true, false, false, 0, 0, 0, 180000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>()));

        var done = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Same(changed.Task, done);
        var t = await changed.Task;
        Assert.Equal(Cover, t!.Image);
        Assert.Equal("Arash", t.Artists[0].Name);
        Assert.Equal("SUPERMAN", t.Album.Name);

        // A later Connect heartbeat carries the same thin player_state again. It must not erase the resolved cover.
        p.OnCluster(new ClusterDelta("other", true,
            new RemoteTrack("spotify:track:tr", "Broken Angel", "", "", "SUPERMAN", "spotify:album:al", null, 180000),
            "spotify:album:al", true, false, false, 1000, 0, 0, 180000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>()));
        Assert.Equal(Cover, p.CurrentTrack!.Image);
        Assert.Equal("Arash", p.CurrentTrack.Artists[0].Name);
    }

    [Fact]
    public void NowPlayingProjection_NormalizesClusterArtwork_BeforeUiReadsTrack()
    {
        var p = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);

        p.OnCluster(new ClusterDelta("other", true,
            new RemoteTrack("spotify:track:tr", "Broken Angel", "Arash", "spotify:artist:ar", "SUPERMAN", "spotify:album:al",
                "spotify:image:ab67616d00001e02870c1c64b1d77eb4456e4283", 180000),
            "spotify:album:al", true, false, false, 0, 0, 0, 180000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>()));

        Assert.Equal("https://i.scdn.co/image/ab67616d00001e02870c1c64b1d77eb4456e4283", p.CurrentTrack!.Image!.Url);
    }

    [Fact]
    public async Task NowPlayingProjection_ReplacesUnsupportedClusterArtwork_WithResolvedHttpArtwork()
    {
        var resolved = new Image("https://i.scdn.co/image/cover", 300, 300);
        var store = new InMemoryStore();
        var p = Enriching(store, uri => new Track("tr", uri, "Broken Angel", [Artist], Album, 180000, false, resolved));
        var changed = new TaskCompletionSource<Track?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = p.Changes.Subscribe(Observers.From<IPlaybackState>(s =>
        {
            if (s.CurrentTrack?.Image?.Url == resolved.Url) changed.TrySetResult(s.CurrentTrack);
        }));

        p.OnCluster(new ClusterDelta("other", true,
            new RemoteTrack("spotify:track:tr", "Broken Angel", "", "", "SUPERMAN", "spotify:album:al",
                "spotify:image:ab67616d00001e02870c1c64b1d77eb4456e4283", 180000),
            "spotify:album:al", true, false, false, 0, 0, 0, 180000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>()));

        var done = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Same(changed.Task, done);
        Assert.Equal(resolved.Url, p.CurrentTrack!.Image!.Url);
    }

    [Fact]
    public async Task NowPlayingProjection_EnrichesMissingAlbumIdentity_EvenWhenArtAndArtistArePresent()
    {
        var store = new InMemoryStore();
        var p = Enriching(store, uri => new Track("tr", uri, "Broken Angel", [Artist], Album, 180000, false, Cover));
        var resolved = new TaskCompletionSource<Track?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = p.Changes.Subscribe(Observers.From<IPlaybackState>(s =>
        {
            if (s.CurrentTrack?.Album.Uri == Album.Uri) resolved.TrySetResult(s.CurrentTrack);
        }));

        // This snapshot used to skip enrichment: artist and HTTP artwork are already usable, but AlbumUri is absent.
        p.OnCluster(new ClusterDelta("other", true,
            new RemoteTrack("spotify:track:tr", "Broken Angel", Artist.Name, Artist.Uri, "SUPERMAN", "", Cover.Url, 180000),
            "spotify:album:al", true, false, false, 0, 0, 0, 180000, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>()));

        var done = await Task.WhenAny(resolved.Task, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Same(resolved.Task, done);
        Assert.Equal(Album.Uri, (await resolved.Task)!.Album.Uri);
    }
}
