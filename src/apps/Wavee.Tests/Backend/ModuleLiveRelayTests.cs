using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Modules;
using Wavee.Core;
using Wavee.Sdk;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE LIVE CHIP, END TO END: a module resolve says <c>isLive</c>, the relay turns that into a per-playable override on
/// the projection, and <see cref="IPlaybackState.IsLive"/> reports it for as long as that playable is current.
/// <para>Composed the way the app composes it — a real <see cref="PlaybackController"/> over a real
/// <see cref="NowPlayingProjection"/> with the relay attached — because every failure this covers was an ORDERING
/// failure between those three, not a logic error in any one of them: the resolve landing after the track publish, and
/// a later republish silently dropping an override that had already been stated.</para>
/// </summary>
public class ModuleLiveRelayTests
{
    const string StreamUri = "wavee:module:wavee.youtube:dFJzUXNUTXZQTmc";
    const string OtherUri = "wavee:module:wavee.youtube:b3RoZXI";

    static ResolvedPlayable LiveResolve() => new(
        PlayableId: "dFJzUXNUTXZQTmc", Title: "The broadcast", Artists: Array.Empty<string>(), ArtworkUrl: null,
        DurationMs: 0, IsLive: true, Form: Wavee.Sdk.MediaForm.Video,
        Media: MediaLocator.FromUrl("https://x/y.m3u8", MediaLocator.ContainerHls),
        ExpiresAtUnixMs: null, Caps: Array.Empty<string>());

    // The synthetic row the queue carries for a pasted link: DurationMs 0, because the length is only known after
    // playback/resolve — and is 0 forever for a broadcast.
    static Track ModuleTrack(string uri) => new(
        Id: uri, Uri: uri, Title: "The broadcast", Artists: Array.Empty<ArtistRef>(), Album: new AlbumRef("", "", ""),
        DurationMs: 0, IsExplicit: false, Image: null, Origin: TrackOrigin.Streamed);

    sealed class Host : IAudioHost
    {
        readonly SimpleSubject<AudioHostSignal> _signals = new();
        public void Load(in AudioStreamHandle s) { }
        public void LoadFastStart(in AudioFastStart s) { }
        public void SupplyBody(in AudioStreamHandle b) { }
        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Stop() => IsPlaying = false;
        public void Seek(long ms, SeekMode mode) => PositionMs = ms;
        public void SetVolume(double v) { }
        public long PositionMs { get; set; }
        public bool IsPlaying { get; private set; }
        public bool IsBuffering => false;
        public bool ClockValid => true;
        public IObservable<AudioHostSignal> Signals => _signals;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static NowPlayingProjection NewProjection() =>
        new("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);

    [Fact]
    public async Task AResolvedLivePlayable_IsLIVE_AfterTheStartedFold_AndStaysLiveAcrossAQueueRepublish()
    {
        var cache = new ModulePlayableCache();
        cache.Put(StreamUri, LiveResolve());        // ModuleHost.MatchAsync resolves BEFORE it hands the row over

        var host = new Host();
        using var projection = NewProjection();
        using var relay = ModuleProjectionRelay.Attach(projection, host: null, cache);
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver(StreamUri), "us");

        await controller.PlayTrackAsync(ModuleTrack(StreamUri));

        Assert.True(projection.IsLive);
        Assert.True(projection.Live.IsLive);
        Assert.False(projection.CanSeek);          // no DVR window stated → nothing to scrub
        Assert.Equal(0, projection.DurationMs);    // a broadcast has no remaining time

        // A queue mutation republishes the whole local snapshot for the SAME current row. The override must survive it —
        // this is the fold that used to drop it, leaving the chip on for one push and off for the rest of the broadcast.
        await controller.EnqueueAsync(ModuleTrack(OtherUri));

        Assert.True(projection.IsLive);
        Assert.Equal(0, projection.DurationMs);
    }

    [Fact]
    public async Task ALiveAnswerThatLandsAFTERThePlay_StillLightsTheChip()
    {
        // The ordering that defeats a one-shot probe: the projection publishes the track change before the module's
        // resolve is in the cache. "Not answered yet" is not "not live" — the next projection change re-asks.
        var cache = new ModulePlayableCache();
        var host = new Host();
        using var projection = NewProjection();
        using var relay = ModuleProjectionRelay.Attach(projection, host: null, cache);
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver(StreamUri), "us");

        await controller.PlayTrackAsync(ModuleTrack(StreamUri));
        Assert.False(projection.IsLive);           // honest placeholder — nothing has stated live-ness yet

        cache.Put(StreamUri, LiveResolve());
        await controller.PauseAsync();             // any later publish re-probes

        Assert.True(projection.IsLive);
    }

    [Fact]
    public async Task ANonLivePlayable_NeverClaimsToBeLive()
    {
        var cache = new ModulePlayableCache();
        cache.Put(StreamUri, LiveResolve() with { IsLive = false, DurationMs = 240_000 });

        var host = new Host();
        using var projection = NewProjection();
        using var relay = ModuleProjectionRelay.Attach(projection, host: null, cache);
        using var controller = new PlaybackController(host, new StubTrackResolver(), projection,
            new FakeContextResolver(StreamUri), "us");

        await controller.PlayTrackAsync(ModuleTrack(StreamUri));

        Assert.False(projection.IsLive);
        Assert.True(projection.CanSeek);
    }
}

/// <summary>
/// The switchable playback facade forwards EVERY <see cref="IPlaybackState"/> member. The ones the interface gives a
/// DEFAULT body are the dangerous half: a default is what a PROVIDER with no answer reports, and a facade has no
/// answers of its own — inheriting one silently replaces the live projection's truth with "no". That is exactly what
/// happened to live-ness (the UI bridge binds to this facade, so the LIVE pill never lit for any broadcast) and,
/// alongside it, to the recovery kind and the playing stream's identity.
/// </summary>
public class SwitchableStateForwardingTests
{
    const string StreamUri = "wavee:module:wavee.youtube:dFJzUXNUTXZQTmc";

    static Track TrackFor(string uri, long durationMs) => new(
        Id: uri, Uri: uri, Title: "t", Artists: Array.Empty<ArtistRef>(), Album: new AlbumRef("", "", ""),
        DurationMs: durationMs, IsExplicit: false, Image: null);

    [Fact]
    public void TheFacadeForwardsLiveness()
    {
        using var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var facade = new SwitchableState(projection);

        projection.OnEvent(new PlaybackEvent(EvKind.Started, TrackFor(StreamUri, 0), 0));
        projection.SetLiveOverride(StreamUri, true);

        IPlaybackState published = facade;
        Assert.True(published.IsLive);
        Assert.True(published.Live.IsLive);
        Assert.Equal(0, published.DurationMs);
        Assert.False(published.CanSeek);
    }

    [Fact]
    public void TheFacadeForwardsTheDVRWindow()
    {
        using var projection = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var facade = new SwitchableState(projection);

        projection.OnEvent(new PlaybackEvent(EvKind.Started, TrackFor(StreamUri, 0), 0));
        var window = new LiveWindow(true, 0, 3_600_000, 3_600_000, 3_540_000, IsAtLiveEdge: false);
        projection.SetLiveWindow(StreamUri, window);

        IPlaybackState published = facade;
        Assert.Equal(window, published.Live);
        Assert.True(published.IsLive);
    }

    [Fact]
    public void TheFacadeStaysCorrectAfterAGoLiveSwap()
    {
        using var pre = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var live = new NowPlayingProjection("us", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var facade = new SwitchableState(pre);

        var pushes = new List<bool>();
        using var sub = facade.Changes.Subscribe(ConnectHarness.Obs<IPlaybackState>(s => pushes.Add(s.IsLive)));

        facade.SetInner(live);
        live.OnEvent(new PlaybackEvent(EvKind.Started, TrackFor(StreamUri, 0), 0));
        live.SetLiveOverride(StreamUri, true);

        Assert.True(((IPlaybackState)facade).IsLive);
        Assert.Contains(true, pushes);   // the bridge is TOLD, not merely able to ask
    }
}
