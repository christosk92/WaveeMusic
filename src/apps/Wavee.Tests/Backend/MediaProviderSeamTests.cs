using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Backend.MediaSources;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;

namespace Wavee.Tests;

// P1 of the source-agnostic playable seams: the provider registry is the ONE dispatch point between play-intent and a
// media source. These tests pin the routing contract (first Owns wins; spotify: covers tracks AND episodes; an unowned
// uri is a typed Restricted failure, never a silent drop), the capability queries, and the two additive host hooks
// (controller prepared-next gate, publisher uri mask) whose null state must stay byte-identical to today's behavior.
public class MediaProviderSeamTests
{
    static Track T(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    // ── Registry dispatch ─────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("spotify:track:abc")]
    [InlineData("spotify:episode:abc")]
    public async Task Registry_RoutesEverySpotifyNamespace_ToTheSpotifyProvider(string uri)
    {
        var fast = new RecordingFast();
        var registry = new MediaProviderRegistry(new SpotifyMediaProvider(DummyLiveResolver(), fast));

        var plan = await registry.ResolveFastAsync(T(uri));

        Assert.Equal("spotify", registry.OwnerOf(uri)!.Id);
        Assert.Equal(new[] { uri }, fast.Resolved.ToArray());
        Assert.Equal(uri, plan.Start.TrackUri);
    }

    [Fact]
    public async Task Registry_UnownedUri_ThrowsRestricted_SoTheTypedErrorPathHandlesIt()
    {
        var registry = new MediaProviderRegistry(new SpotifyMediaProvider(DummyLiveResolver(), new RecordingFast()));

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => registry.ResolveFastAsync(T("wavee:local:file:zzz")));
        Assert.Equal(AudioKeyFailureReason.Restricted, ex.Reason);
        Assert.Contains("no media source owns wavee:local:file:zzz", ex.Message);
        Assert.Null(registry.OwnerOf("wavee:local:file:zzz"));

        await Assert.ThrowsAsync<AudioPlaybackException>(() => registry.ResolveAsync(T("wavee:local:file:zzz")));
    }

    [Fact]
    public async Task Registry_FirstOwnerWins_InRegistrationOrder()
    {
        var first = new FakeProvider("first", "wavee:", MediaProviderCaps.None);
        var second = new FakeProvider("second", "wavee:", MediaProviderCaps.None);
        var registry = new MediaProviderRegistry(first, second);

        await registry.ResolveFastAsync(T("wavee:media:x"));

        Assert.Equal("first", registry.OwnerOf("wavee:media:x")!.Id);
        Assert.Equal(1, first.FastCalls);
        Assert.Equal(0, second.FastCalls);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public async Task Registry_PlainResolve_DefaultsToTheFastPlansBody()
    {
        var provider = new FakeProvider("fake", "wavee:", MediaProviderCaps.None);
        var registry = new MediaProviderRegistry(provider);

        var handle = await registry.ResolveAsync(T("wavee:media:x"));

        Assert.Equal("wavee:media:x", handle.TrackUri);
    }

    [Fact]
    public void Registry_Warm_DispatchesToTheOwner_AndIgnoresUnownedUris()
    {
        var provider = new FakeProvider("fake", "wavee:", MediaProviderCaps.None);
        var registry = new MediaProviderRegistry(provider);

        registry.Warm(T("wavee:media:x"), "after-start");
        registry.Warm(T("spotify:track:a"), "after-start");   // no owner → no-op, never a throw

        Assert.Equal(new[] { "wavee:media:x" }, provider.Warmed.ToArray());
    }

    // ── Capability queries ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SpotifyProvider_OwnsTheWholeSpotifyNamespace_WithTheFullCapabilitySet()
    {
        var provider = new SpotifyMediaProvider(DummyLiveResolver(), new RecordingFast());

        Assert.Equal("spotify", provider.Id);
        Assert.True(provider.Owns("spotify:track:abc"));
        Assert.True(provider.Owns("spotify:episode:abc"));
        Assert.False(provider.Owns("wavee:local:file:abc"));
        Assert.Equal(
            MediaProviderCaps.PreparedNext | MediaProviderCaps.ConnectPublish | MediaProviderCaps.WireMeta,
            provider.Caps);
    }

    [Fact]
    public async Task SpotifyProvider_RefusesSpotifysOwnLocalFileNamespace()
    {
        // `spotify:local:artist:album:title:sec` parses as Spotify but is a file on somebody's disk: no id, no TRACK_V4
        // extension, no audio file. It is ALSO what ConnectUriMask publishes every non-publishable row as — so the
        // Connect cluster hands one back to us as the echo of our own state. Owning it turned that echo into a real
        // resolve attempt and a "Restricted: no TRACK_V4 extension for spotify:local:…" error toast at launch.
        var registry = new MediaProviderRegistry(new SpotifyMediaProvider(DummyLiveResolver(), new RecordingFast()));
        const string masked = "spotify:local:Claude::Claude+FM:0";

        Assert.Null(registry.OwnerOf(masked));
        Assert.False(registry.SupportsPreparedNext(masked));
        Assert.False(registry.IsConnectPublishable(masked));

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => registry.ResolveFastAsync(T(masked)));
        Assert.Equal(AudioKeyFailureReason.Restricted, ex.Reason);
        Assert.Contains("no media source owns " + masked, ex.Message);

        // …and the rest of the namespace is untouched.
        Assert.NotNull(registry.OwnerOf("spotify:track:abc"));
        Assert.NotNull(registry.OwnerOf("spotify:episode:abc"));
        Assert.NotNull(registry.OwnerOf("spotify:local-files"));   // the CONTEXT shape is not the row shape
    }

    [Fact]
    public async Task Registry_CapabilityQueries_FollowOwnership()
    {
        var registry = new MediaProviderRegistry(
            new SpotifyMediaProvider(DummyLiveResolver(), new RecordingFast()),
            new FakeProvider("plain", "wavee:", MediaProviderCaps.None));

        Assert.True(registry.SupportsPreparedNext("spotify:track:a"));
        Assert.True(registry.SupportsPreparedNext("spotify:episode:a"));
        Assert.True(registry.IsConnectPublishable("spotify:track:a"));

        Assert.False(registry.SupportsPreparedNext("wavee:media:x"));
        Assert.False(registry.IsConnectPublishable("wavee:media:x"));
        Assert.Null(await registry.ResolveWireMetaAsync(T("wavee:media:x")));

        // An unowned uri answers false everywhere (the hard cut / masked publish is always the safe boundary).
        Assert.False(registry.SupportsPreparedNext("http://example.test/a.mp4"));
        Assert.False(registry.IsConnectPublishable("http://example.test/a.mp4"));
        Assert.Null(await registry.ResolveWireMetaAsync(T("http://example.test/a.mp4")));
    }

    // ── Controller: the prepared-next gate ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CanPrepareNext_False_SkipsThePreparedHandoff()
    {
        var host = new PreparedHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "dev");
        var asked = new ConcurrentQueue<string>();
        controller.CanPrepareNext = t => { asked.Enqueue(t.Uri); return false; };

        await controller.PlayAsync("spotify:playlist:test");
        await Task.Delay(80);

        Assert.Empty(host.Prepared);
        Assert.Contains("spotify:track:b", asked);
        Assert.Equal(new[] { "spotify:track:a" }, host.Loaded.ToArray());   // the current track still plays
    }

    [Fact]
    public async Task CanPrepareNext_Null_LeavesTheHandoffExactlyAsItIsToday()
    {
        var host = new PreparedHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "dev");

        await controller.PlayAsync("spotify:playlist:test");
        await WaitUntilAsync(() => host.Prepared.Count >= 1);

        Assert.Equal("spotify:track:b", host.Prepared.First().Start.TrackUri);
    }

    [Fact]
    public async Task CanPrepareNext_True_PreparesJustLikeTheUnwiredPath()
    {
        var host = new PreparedHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver("spotify:track:a", "spotify:track:b"), "dev");
        controller.CanPrepareNext = _ => true;

        await controller.PlayAsync("spotify:playlist:test");
        await WaitUntilAsync(() => host.Prepared.Count >= 1);

        Assert.Equal("spotify:track:b", host.Prepared.First().Start.TrackUri);
    }

    // ── Publisher: the Connect uri mask ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishUriMask_Null_PublishesEveryUriVerbatim()
    {
        var h = new PublisherHarness();
        h.Connect("c1");
        h.SetQueue(
            new QueueEntry(QueueItemId.None, "now", T("wavee:local:file:a"), QueueBucket.NowPlaying, QueueProvider.Context, false, "u-now"),
            new QueueEntry(QueueItemId.None, "n0", T("spotify:track:b"), QueueBucket.NextUp, QueueProvider.Context, false, "u-n0"));
        h.Play("wavee:local:file:a");
        await Task.Delay(20);

        var snap = Assert.IsType<LocalPlaybackSnapshot>(h.LastSnapshot);
        Assert.Equal("wavee:local:file:a", snap.Track.Uri);
        Assert.Equal("u-now", snap.Track.Uid);
        Assert.Equal("spotify:track:b", snap.NextTracks[0].Uri);
    }

    [Fact]
    public async Task PublishUriMask_Set_RewritesTheUriOnly_AndPreservesTheUid()
    {
        var h = new PublisherHarness();
        h.Publisher.PublishUriMask = t =>
            t.Uri.StartsWith("spotify:", StringComparison.Ordinal) ? t.Uri : "spotify:local:::" + t.Title + ":1";
        h.Connect("c1");
        h.SetQueue(
            new QueueEntry(QueueItemId.None, "now", T("wavee:local:file:a"), QueueBucket.NowPlaying, QueueProvider.Context, false, "u-now"),
            new QueueEntry(QueueItemId.None, "n0", T("spotify:track:b"), QueueBucket.NextUp, QueueProvider.Context, false, "u-n0"));
        h.Play("wavee:local:file:a");
        await Task.Delay(20);

        var snap = Assert.IsType<LocalPlaybackSnapshot>(h.LastSnapshot);
        Assert.Equal("spotify:local:::wavee:local:file:a:1", snap.Track.Uri);
        Assert.Equal("u-now", snap.Track.Uid);              // the wire rows stay addressable
        Assert.Equal("spotify:track:b", snap.NextTracks[0].Uri);   // publishable uris are untouched
        Assert.Equal("u-n0", snap.NextTracks[0].Uid);
    }

    [Fact]
    public async Task PublishUriMask_ThatThrowsOrReturnsEmpty_FallsBackToTheRealUri()
    {
        var h = new PublisherHarness();
        h.Publisher.PublishUriMask = _ => throw new InvalidOperationException("boom");
        h.Connect("c1");
        h.Play("spotify:track:a");
        await Task.Delay(20);

        var snap = Assert.IsType<LocalPlaybackSnapshot>(h.LastSnapshot);
        Assert.Equal("spotify:track:a", snap.Track.Uri);

        h.Publisher.PublishUriMask = _ => "";
        h.Play("spotify:track:c");
        await Task.Delay(20);
        Assert.Equal("spotify:track:c", h.LastSnapshot!.Value.Track.Uri);
    }

    // ── Support ───────────────────────────────────────────────────────────────────────────────────────────────────────

    // A constructible LiveTrackResolver whose network paths are never reached: the provider tests exercise delegation
    // (fast resolve / warm / ownership), not Spotify's own resolution, which LiveTrackResolver*Tests already cover.
    static LiveTrackResolver DummyLiveResolver() =>
        new(new NullTransport(), new StubAudioKeySource(), (_, _) => Task.FromResult<ByteString?>(null));

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    static FastStartPlan PlanFor(string uri) =>
        new(new AudioFastStart(uri, "", AudioFormat.OggVorbis320, 1000, 0f, default),
            Task.FromResult(new AudioStreamHandle(uri, "", "", default, AudioFormat.OggVorbis320, 1000, 0f)));

    sealed class RecordingFast : IFastTrackResolver, IFastTrackWarmer
    {
        public ConcurrentQueue<string> Resolved { get; } = new();
        public ConcurrentQueue<string> Warmed { get; } = new();

        public Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
        {
            Resolved.Enqueue(track.Uri);
            return Task.FromResult(PlanFor(track.Uri));
        }

        public void Warm(Track track, string reason = "") => Warmed.Enqueue(track.Uri);
    }

    // A minimal source that implements ONLY the mandatory member, so the interface defaults (plain resolve, wire meta,
    // warm) are what these tests exercise.
    sealed class FakeProvider(string id, string prefix, MediaProviderCaps caps) : IPlayableMediaProvider
    {
        public ConcurrentQueue<string> Warmed { get; } = new();
        public int FastCalls;

        public string Id => id;
        public MediaProviderCaps Caps => caps;
        public bool Owns(string playableUri) => playableUri.StartsWith(prefix, StringComparison.Ordinal);

        public Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FastCalls);
            return Task.FromResult(PlanFor(track.Uri));
        }

        public void Warm(Track track, string reason = "") => Warmed.Enqueue(track.Uri);
    }

    sealed class NullTransport : ITransport
    {
        public Task<Resp> Request(Channel ch, string route, ReadOnlyMemory<byte> body, CancellationToken ct = default,
            string? method = null, IReadOnlyDictionary<string, string>? headers = null)
            => Task.FromResult(new Resp(false, Array.Empty<byte>(), 500));

        public IObservable<WireEvent> Events(string topicPrefix) => new SimpleSubject<WireEvent>();
        public IObservable<WireRequest> Requests(string identPrefix) => new SimpleSubject<WireRequest>();
        public Task Reply(string requestId, RequestResult result) => Task.CompletedTask;
        public Task<Resp> Publish(string deviceId, string connectionId, ReadOnlyMemory<byte> putState, CancellationToken ct = default)
            => Task.FromResult(new Resp(true, Array.Empty<byte>(), 200));
    }

    sealed class PreparedHost : IAudioHost, IPreparedAudioHost
    {
        readonly SimpleSubject<AudioHostSignal> _signals = new();
        readonly SimpleSubject<AudioTransitionSignal> _transitions = new();
        public ConcurrentQueue<string> Loaded { get; } = new();
        public ConcurrentQueue<AudioPrepareRequest> Prepared { get; } = new();
        public ConcurrentQueue<string> Cancelled { get; } = new();

        public void Load(in AudioStreamHandle stream) => Loaded.Enqueue(stream.TrackUri);
        public void LoadFastStart(in AudioFastStart start) => Loaded.Enqueue(start.TrackUri);
        public void SupplyBody(in AudioStreamHandle body) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Seek(long positionMs, SeekMode mode) { }
        public void SetVolume(double volume01) { }
        public long PositionMs => 0;
        public bool IsPlaying => true;
        public bool IsBuffering => false;
        public bool ClockValid => true;
        public IObservable<AudioHostSignal> Signals => _signals;
        public IObservable<AudioTransitionSignal> Transitions => _transitions;

        public Task PrepareNextAsync(AudioPrepareRequest request, CancellationToken ct = default)
        {
            Prepared.Enqueue(request);
            return Task.CompletedTask;
        }

        public Task SupplyNextBodyAsync(string token, AudioStreamHandle body, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default)
        {
            Cancelled.Enqueue(token);
            return Task.FromResult(AudioPrepareCancelResult.Cancelled);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class PublisherHarness
    {
        public readonly StubTransport Transport = new();
        public readonly NowPlayingProjection Proj = new("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        public readonly SimpleSubject<string?> ConnId = new(null);
        public string? CurrentConnId;
        public LocalPlaybackSnapshot? LastSnapshot;
        public readonly DeviceStatePublisher Publisher;

        public PublisherHarness()
        {
            Publisher = new DeviceStatePublisher(Transport, "us", Proj, ConnId, () => CurrentConnId,
                (reason, snap, mid, active) =>
                {
                    LastSnapshot = snap;
                    return Encoding.UTF8.GetBytes(reason + "|" + active + "|" + (snap?.Track.Uri ?? "-"));
                },
                onCluster: null, clock: () => 1000);
        }

        public void Connect(string id) { CurrentConnId = id; ConnId.OnNext(id); }
        public void SetQueue(params QueueEntry[] q) => Proj.SetLocalQueue(q);

        public void Play(string trackUri)
        {
            var e = new PlaybackEvent(EvKind.Started, T(trackUri), 0);
            Proj.OnEvent(e);
            Publisher.OnEvent(e);
        }
    }
}
