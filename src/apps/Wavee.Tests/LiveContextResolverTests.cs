using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Hydration;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

public class LiveContextResolverTests
{
    static byte[] ArtistListBody()
    {
        var slc = new Pl.SelectedListContent { Length = 3 };
        var contents = new Pl.ListItems();
        for (int i = 0; i < 3; i++)
            contents.Items.Add(new Pl.Item
            {
                Uri = $"spotify:track:t{i}",
                Attributes = new Pl.ItemAttributes { ItemId = ByteString.CopyFrom((byte)i, (byte)i, (byte)i, (byte)i) },
            });
        slc.Contents = contents;
        return slc.ToByteArray();
    }

    [Fact]
    public async Task ArtistResolve_SecondPlay_OneWireFetch_SkipParamsStillApply()
    {
        int wireCalls = 0;
        var transport = new ArtistCountingTransport(() =>
        {
            Interlocked.Increment(ref wireCalls);
            return new Resp(true, ArtistListBody(), 200);
        });
        var store = new InMemoryStore();
        // The resolver hydrates the resolved order through THE façade now (Identity, surface Context) instead of
        // owning a metadata client. These tests exercise the WIRE + ordering, so the store-only hydrator is exactly
        // right: it never fetches, and every row falls through to the uri-only placeholder as before.
        var resolver = new LiveContextResolver(transport, new OfflineEntityHydrator(store), store, () => SessionContext.LoggedOut);

        var r1 = await resolver.ResolveAsync(ContextSpec.ForUri("spotify:artist:abc", 0));
        var r2 = await resolver.ResolveAsync(ContextSpec.ForUri("spotify:artist:abc", 2));

        Assert.Equal(1, wireCalls);
        Assert.Equal(3, r1.Tracks.Count);
        Assert.Equal(3, r2.Tracks.Count);
        Assert.Equal(0, r1.StartIndex);
        Assert.Equal(2, r2.StartIndex);
    }

    [Fact]
    public async Task PopularReleaseListUri_RoutesThroughArtistListResolver()
    {
        var transport = new ArtistCountingTransport(() => new Resp(true, ArtistListBody(), 200));
        var resolver = MakeResolver(transport);

        var result = await resolver.ResolveAsync(ContextSpec.ForUri(
            "spotify:list:popular-release-segments-main-roles:artist_abc", 1));

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result.StartIndex);
        Assert.Contains("/artist_abc", transport.LastRoute, StringComparison.Ordinal);
    }

    // ── inspiredby-mix/v2/seed_to_playlist (explicit "Start radio" seed → radio playlist uri) ─────────────────────────
    static LiveContextResolver MakeResolver(ITransport transport)
    {
        var store = new InMemoryStore();
        return new LiveContextResolver(transport, new OfflineEntityHydrator(store), store, () => SessionContext.LoggedOut);
    }

    [Fact]
    public async Task ResolveRadioSeed_ReturnsFirstMediaItemUri_AndSendsLiteralColonRoute()
    {
        var transport = new RouteTransport((route, _) =>
            route.Contains("inspiredby-mix/v2/seed_to_playlist", StringComparison.Ordinal)
                ? new Resp(true, System.Text.Encoding.UTF8.GetBytes(
                    "{\"total\":1,\"mediaItems\":[{\"uri\":\"spotify:playlist:37i9dQZF1E8abc\"}]}"), 200)
                : new Resp(false, Array.Empty<byte>(), 404));
        var resolver = MakeResolver(transport);

        var uri = await resolver.ResolveRadioSeedAsync("spotify:track:seed1");

        Assert.Equal("spotify:playlist:37i9dQZF1E8abc", uri);
        // Seed rides the path segment with literal colons (RFC 3986), not percent-escaped.
        Assert.Contains("/inspiredby-mix/v2/seed_to_playlist/spotify:track:seed1", transport.LastRoute);
        Assert.Contains("response-format=json", transport.LastRoute);
    }

    [Fact]
    public async Task ResolveRadioSeed_404_ReturnsNull()
    {
        var resolver = MakeResolver(new RouteTransport((_, _) => new Resp(false, Array.Empty<byte>(), 404)));
        Assert.Null(await resolver.ResolveRadioSeedAsync("spotify:track:seed1"));
    }

    [Fact]
    public async Task ResolveRadioSeed_EmptyMediaItems_ReturnsNull()
    {
        var resolver = MakeResolver(new RouteTransport((_, _) =>
            new Resp(true, System.Text.Encoding.UTF8.GetBytes("{\"total\":0,\"mediaItems\":[]}"), 200)));
        Assert.Null(await resolver.ResolveRadioSeedAsync("spotify:artist:seed2"));
    }

    sealed class RouteTransport(Func<string, string, Resp> respond) : ITransport
    {
        public string LastRoute { get; private set; } = "";
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            LastRoute = route;
            return Task.FromResult(respond(route, method ?? (body.IsEmpty ? "GET" : "POST")));
        }
        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    sealed class ArtistCountingTransport(Func<Resp> respond) : ITransport
    {
        public string LastRoute { get; private set; } = "";
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
        {
            LastRoute = route;
            if (route.Contains("popular-release-segments", StringComparison.Ordinal))
                return Task.FromResult(respond());
            return Task.FromResult(new Resp(false, Array.Empty<byte>(), 404));
        }

        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }
}
