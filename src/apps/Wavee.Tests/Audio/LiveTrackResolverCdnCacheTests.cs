using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Wavee.Protocol.Storage;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests.Audio;

public class LiveTrackResolverCdnCacheTests
{
    static byte[] FileId(byte s) => A.Bytes(s, 20);
    static byte[] Gid(byte s) => A.Bytes(s, 16);

    static LiveTrackResolver.TrackMeta Meta(byte fileSeed = 5) =>
        new(FileId(fileSeed), Convert.ToHexStringLower(FileId(fileSeed)), Gid(9), AudioFormat.OggVorbis320, 180000,
            "spotify:track:abc", 0f);

    static StorageResolveResponse CdnResponse(int ttlSeconds = 86400)
    {
        var r = new StorageResolveResponse();
        r.Cdnurl.Add("https://cdn1.test/a");
        r.Cdnurl.Add("https://cdn2.test/b");
        r.Cdnurl.Add("https://cdn3.test/c");
        if (ttlSeconds > 0) r.TtlSeconds = ttlSeconds;
        return r;
    }

    [Fact]
    public async Task ResolveBody_SecondCall_SkipsStorageResolve()
    {
        int calls = 0;
        var transport = new CountingTransport(route =>
        {
            Interlocked.Increment(ref calls);
            return new Resp(true, CdnResponse().ToByteArray(), 200);
        });
        var resolver = new LiveTrackResolver(transport, new StubAudioKeySource(), (_, _) => Task.FromResult<ByteString?>(null));
        var meta = Meta();

        var h1 = await resolver.ResolveBodyAsync(meta);
        var h2 = await resolver.ResolveBodyAsync(meta);

        Assert.Equal(1, calls);
        Assert.Equal(3, h1.CdnUrls!.Length);
        Assert.Equal(3, h2.CdnUrls!.Length);
    }

    [Fact]
    public async Task ResolveBody_TtlExpired_Refetches()
    {
        int calls = 0;
        var transport = new CountingTransport(route =>
        {
            Interlocked.Increment(ref calls);
            return new Resp(true, CdnResponse(ttlSeconds: 1).ToByteArray(), 200);
        });
        var resolver = new LiveTrackResolver(transport, new StubAudioKeySource(), (_, _) => Task.FromResult<ByteString?>(null));
        var meta = Meta();

        await resolver.ResolveBodyAsync(meta);
        await Task.Delay(900);
        await resolver.ResolveBodyAsync(meta);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ResolveBody_TtlFromResponse_IsHonored()
    {
        var transport = new CountingTransport(_ => new Resp(true, CdnResponse(86400).ToByteArray(), 200));
        var resolver = new LiveTrackResolver(transport, new StubAudioKeySource(), (_, _) => Task.FromResult<ByteString?>(null));
        var meta = Meta();

        await resolver.ResolveBodyAsync(meta);
        var expires = resolver.PeekCdnExpiresAt(meta.FileIdHex);

        Assert.NotNull(expires);
        var expected = DateTime.UtcNow + TimeSpan.FromSeconds(86400 * 0.8);
        Assert.True(expires!.Value > DateTime.UtcNow + TimeSpan.FromHours(19));
        Assert.True(expires.Value < expected + TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task InvalidateCdn_ForcesRefetch()
    {
        int calls = 0;
        var transport = new CountingTransport(route =>
        {
            Interlocked.Increment(ref calls);
            return new Resp(true, CdnResponse().ToByteArray(), 200);
        });
        var resolver = new LiveTrackResolver(transport, new StubAudioKeySource(), (_, _) => Task.FromResult<ByteString?>(null));
        var meta = Meta();

        await resolver.ResolveBodyAsync(meta);
        resolver.InvalidateCdn(meta.FileIdHex);
        await resolver.ResolveBodyAsync(meta);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Restricted_Throws_NotCachedAsValue()
    {
        int calls = 0;
        var transport = new CountingTransport(route =>
        {
            Interlocked.Increment(ref calls);
            var r = new StorageResolveResponse { Result = StorageResolveResponse.Types.Result.Restricted };
            return new Resp(true, r.ToByteArray(), 200);
        });
        var resolver = new LiveTrackResolver(transport, new StubAudioKeySource(), (_, _) => Task.FromResult<ByteString?>(null));
        var meta = Meta();

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => resolver.ResolveBodyAsync(meta));
        Assert.Equal(AudioKeyFailureReason.Restricted, ex.Reason);
        await Assert.ThrowsAsync<AudioPlaybackException>(() => resolver.ResolveBodyAsync(meta));
        Assert.Equal(2, calls);
    }

    sealed class CountingTransport(Func<string, Resp> respond) : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
            => Task.FromResult(respond(route));

        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }
}
