using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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

// P4 of the source-agnostic playable seams: the two non-Spotify providers that PROVE the seam. These tests pin
//   • the three-provider routing table (order IS the table: first Owns wins),
//   • the local-file resolve contract (uri round-trip, the typed missing-file failure, the extension→format map and
//     its refusals) — the audio side of the plan's validation case (b),
//   • the FileStream-backed read/seek stream the host opens for it,
//   • the synthetic Track a picked/dropped file becomes (Origin=Local ⇒ PlayableKind.LocalFile ⇒ audio host),
//   • Connect masking now that non-publishable uris finally exist,
//   • the prepared-next gate closing for a local next (a capability answer, not a special case),
//   • and one end-to-end play: local file → Started → Ended → auto-advance.
public class LocalMediaProviderTests
{
    static Track T(string uri, long durationMs = 0) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), durationMs, false, null);

    // ── The playable uri namespaces ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Music\Sigur Rós\01 Svefn-g-englar.flac")]
    [InlineData(@"\\nas\share\a b\c+d=e.mp3")]
    [InlineData("/home/user/музыка/трек.ogg")]
    public void PlayableUri_RoundTripsAnyPath_ThroughOneColonFreeToken(string path)
    {
        var uri = PlayableUri.ForLocalFile(path);

        Assert.StartsWith(PlayableUri.LocalFilePrefix, uri, StringComparison.Ordinal);
        // The payload must be ONE flat token: a stray ':' would fork every uri parser downstream.
        var payload = uri[PlayableUri.LocalFilePrefix.Length..];
        Assert.DoesNotContain(':', payload);
        Assert.DoesNotContain('/', payload);
        Assert.DoesNotContain('+', payload);
        Assert.DoesNotContain('=', payload);

        Assert.True(PlayableUri.TryDecodeLocalFile(uri, out var decoded));
        Assert.Equal(path, decoded);
    }

    [Fact]
    public void PlayableUri_RejectsForeignAndMalformedUris_WithoutThrowing()
    {
        Assert.False(PlayableUri.TryDecodeLocalFile("spotify:track:abc", out _));
        Assert.False(PlayableUri.TryDecodeLocalFile(PlayableUri.LocalFilePrefix, out _));            // empty payload
        Assert.False(PlayableUri.TryDecodeLocalFile(PlayableUri.LocalFilePrefix + "!!!!", out _));   // not base64url
        Assert.False(PlayableUri.TryDecodeMedia(null, out _));

        Assert.True(PlayableUri.IsHttpUrl("https://cdn.test/a.mp3"));
        Assert.True(PlayableUri.IsHttpUrl("HTTP://cdn.test/a.mp3"));
        Assert.False(PlayableUri.IsHttpUrl(@"C:\a.mp3"));
    }

    // ── Registry: the three-provider routing table ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_RoutesEachNamespaceToItsOwner_InRegistrationOrder()
    {
        var registry = ThreeProviderRegistry(out _);

        Assert.Equal(3, registry.Count);
        Assert.Equal("spotify", registry.OwnerOf("spotify:track:a")!.Id);
        Assert.Equal("spotify", registry.OwnerOf("spotify:episode:a")!.Id);
        Assert.Equal("local-file", registry.OwnerOf(PlayableUri.ForLocalFile(@"C:\a.mp3"))!.Id);
        Assert.Equal("generic", registry.OwnerOf(PlayableUri.ForMedia("https://cdn.test/a.mp3"))!.Id);
        Assert.Null(registry.OwnerOf("wavee:local:track:whatever"));   // a sibling namespace nobody claims stays unowned
    }

    [Fact]
    public void Registry_CapabilitiesSplitSpotifyFromTheLocalSources()
    {
        var registry = ThreeProviderRegistry(out _);
        string local = PlayableUri.ForLocalFile(@"C:\a.mp3");
        string media = PlayableUri.ForMedia("https://cdn.test/a.mp3");

        Assert.True(registry.SupportsPreparedNext("spotify:track:a"));
        Assert.True(registry.IsConnectPublishable("spotify:track:a"));

        // Absent capabilities are not failures — they select the proven simpler path (hard cut, masked uri, no meta).
        Assert.False(registry.SupportsPreparedNext(local));
        Assert.False(registry.IsConnectPublishable(local));
        Assert.False(registry.SupportsPreparedNext(media));
        Assert.False(registry.IsConnectPublishable(media));
    }

    // ── Local-file resolve ────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\a.mp3", AudioFormat.Mp3)]
    [InlineData(@"C:\a.MP3", AudioFormat.Mp3)]
    [InlineData(@"C:\a.ogg", AudioFormat.OggVorbis320)]
    [InlineData(@"C:\a.flac", AudioFormat.Flac)]
    [InlineData(@"C:\a.FLAC", AudioFormat.Flac)]
    public async Task LocalFile_Resolve_MapsTheExtensionAndCarriesThePathInTheHandle(string path, AudioFormat expected)
    {
        var provider = new LocalFileMediaProvider(fileExists: _ => true);
        string uri = PlayableUri.ForLocalFile(path);

        var plan = await provider.ResolveFastAsync(T(uri, durationMs: 4321));

        // The external-episode shape verbatim: an EMPTY head plus an already-completed body.
        Assert.Equal(0, plan.Start.HeadBytes.Length);
        Assert.Equal(uri, plan.Start.TrackUri);
        Assert.Equal(expected, plan.Start.Format);
        Assert.True(plan.Body.IsCompletedSuccessfully);

        var body = await plan.Body;
        Assert.Equal(AudioSourceKind.LocalFile, body.SourceKind);
        Assert.Equal(path, body.CdnUrl);       // the path rides where ExternalPlain carries its URL
        Assert.Equal(expected, body.Format);
        Assert.Equal(4321, body.DurationMs);   // the Track's own duration wins — no probe on the resolve path
        Assert.True(body.Key.IsEmpty);
    }

    [Fact]
    public async Task LocalFile_MissingFile_IsATypedFailure_NotASilentDrop()
    {
        var provider = new LocalFileMediaProvider(fileExists: _ => false);
        string uri = PlayableUri.ForLocalFile(@"C:\gone\a.mp3");

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => provider.ResolveFastAsync(T(uri)));

        Assert.Equal(AudioKeyFailureReason.Restricted, ex.Reason);
        Assert.Contains(@"C:\gone\a.mp3", ex.Message);
    }

    [Theory]
    [InlineData(@"C:\a.m4a")]
    [InlineData(@"C:\a.aac")]
    [InlineData(@"C:\a.wav")]
    [InlineData(@"C:\a.opus")]
    [InlineData(@"C:\a.mp4")]
    [InlineData(@"C:\a")]
    public async Task LocalFile_UnsupportedContainer_IsRefusedBeforeADecoderEverGuesses(string path)
    {
        var provider = new LocalFileMediaProvider(fileExists: _ => true);

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => provider.ResolveFastAsync(T(PlayableUri.ForLocalFile(path))));

        Assert.Equal(AudioKeyFailureReason.ArchUnsupported, ex.Reason);
        Assert.Contains(".mp3", ex.Message);
        Assert.False(LocalFileMediaProvider.IsSupportedAudioFile(path));
    }

    [Fact]
    public async Task LocalFile_MalformedUri_FailsTyped()
    {
        var provider = new LocalFileMediaProvider(fileExists: _ => true);

        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => provider.ResolveFastAsync(T(PlayableUri.LocalFilePrefix + "!!!")));

        Assert.Equal(AudioKeyFailureReason.Restricted, ex.Reason);
    }

    [Fact]
    public async Task LocalFile_DurationProbe_IsConsultedOnlyWhenTheTrackCarriesNone()
    {
        int probes = 0;
        var provider = new LocalFileMediaProvider(fileExists: _ => true, probeDurationMs: _ => { probes++; return 90_000; });

        var withDuration = await (await provider.ResolveFastAsync(T(PlayableUri.ForLocalFile(@"C:\a.mp3"), 1234))).Body;
        Assert.Equal(1234, withDuration.DurationMs);
        Assert.Equal(0, probes);

        var without = await (await provider.ResolveFastAsync(T(PlayableUri.ForLocalFile(@"C:\b.mp3")))).Body;
        Assert.Equal(90_000, without.DurationMs);
        Assert.Equal(1, probes);

        // A throwing probe is "unknown length", never a play failure.
        var throwing = new LocalFileMediaProvider(fileExists: _ => true, probeDurationMs: _ => throw new IOException("nope"));
        var noDuration = await (await throwing.ResolveFastAsync(T(PlayableUri.ForLocalFile(@"C:\c.mp3")))).Body;
        Assert.Equal(0, noDuration.DurationMs);
    }

    // ── Generic provider ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generic_HttpPayload_ResolvesToTheExternalPodcastShape_WithNoNewHostCode()
    {
        var provider = new GenericMediaProvider(fileExists: _ => true);
        string uri = PlayableUri.ForMedia("https://cdn.test/ep.mp3");

        var plan = await provider.ResolveFastAsync(T(uri, 1000));
        var body = await plan.Body;

        Assert.Equal(0, plan.Start.HeadBytes.Length);
        Assert.Equal(AudioSourceKind.ExternalPlain, body.SourceKind);
        Assert.Equal("https://cdn.test/ep.mp3", body.CdnUrl);
    }

    [Fact]
    public async Task Generic_FilePayload_ResolvesThroughTheSameLocalFileBuilder()
    {
        var provider = new GenericMediaProvider(fileExists: _ => true);
        string uri = PlayableUri.ForMedia(@"C:\Music\a.flac");

        var body = await (await provider.ResolveFastAsync(T(uri))).Body;

        Assert.Equal(AudioSourceKind.LocalFile, body.SourceKind);
        Assert.Equal(@"C:\Music\a.flac", body.CdnUrl);
        Assert.Equal(AudioFormat.Flac, body.Format);

        // …including its refusals: one format map, two providers.
        var bad = new GenericMediaProvider(fileExists: _ => true);
        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => bad.ResolveFastAsync(T(PlayableUri.ForMedia(@"C:\Music\a.mp4"))));
        Assert.Equal(AudioKeyFailureReason.ArchUnsupported, ex.Reason);
    }

    // ── LocalFileAudioStream ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LocalFileAudioStream_ReadsAndSeeksAgainstARealFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "wavee-localstream-" + Guid.NewGuid().ToString("N") + ".mp3");
        var payload = new byte[4096];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        File.WriteAllBytes(path, payload);
        try
        {
            using var stream = LocalFileAudioStream.Open(path);

            Assert.Equal(payload.Length, stream.KnownSize);
            Assert.Equal(payload.Length, stream.Length);
            Assert.True(stream.IsBodyAttached);
            Assert.Equal(0, stream.ClearHeadLength);
            Assert.Same(stream, stream.AsStream());

            var head = new byte[16];
            Assert.Equal(16, stream.Read(head, 0, 16));
            Assert.Equal(payload.Take(16), head);
            Assert.Equal(16, stream.CurrentOffset);

            Assert.Equal(1000, stream.Seek(1000, SeekOrigin.Begin));
            var mid = new byte[8];
            Assert.Equal(8, stream.Read(mid, 0, 8));
            Assert.Equal(payload.Skip(1000).Take(8), mid);

            Assert.Equal(payload.Length - 4, stream.Seek(-4, SeekOrigin.End));
            var tail = new byte[8];
            Assert.Equal(4, stream.Read(tail, 0, 8));   // clamped at EOF

            Assert.Equal(payload.Length, stream.Seek(0, SeekOrigin.End));
            Assert.Equal(0, stream.Read(tail, 0, 8));   // EOF reads 0, never throws

            // The read-ahead verbs are no-ops for a local file, but must still honour the contract.
            using (stream.PauseReadAhead()) { }
            stream.ResumeReadAheadAtCurrentOffset();
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void LocalFileAudioStream_DoesNotLockTheUsersFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "wavee-localstream-" + Guid.NewGuid().ToString("N") + ".mp3");
        File.WriteAllBytes(path, new byte[64]);
        try
        {
            using var stream = LocalFileAudioStream.Open(path);
            // A second reader (and Explorer, and the user's tag editor) must still get in while it plays.
            using var second = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Assert.Equal(64, second.Length);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ── The synthetic playables ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SyntheticTrack_ForALocalFile_IsALocalOriginPlayableTitledFromItsFileName()
    {
        var track = LocalPlayables.ForLocalFile(@"C:\Music\Boards of Canada - Roygbiv.flac");

        Assert.Equal(PlayableUri.ForLocalFile(@"C:\Music\Boards of Canada - Roygbiv.flac"), track.Uri);
        Assert.Equal("Boards of Canada - Roygbiv", track.Title);
        Assert.Equal(TrackOrigin.Local, track.Origin);          // ⇒ PlayableKind.LocalFile ⇒ audio host, track_player "audio"
        Assert.Equal("local", track.Source);
        Assert.Equal(0, track.DurationMs);                      // no probe supplied ⇒ unknown, never a guess
        Assert.Equal(Availability.Playable, track.Availability);
        Assert.NotEqual("", track.Id);

        // The kind rules are the EXISTING tested ones — nothing new decides this.
        Assert.Equal(PlayableKind.LocalFile, MediaSwitchLogic.KindOf(false, track.Origin == TrackOrigin.Local));
        Assert.Equal("audio", MediaSwitchLogic.TrackPlayer(PlayableKind.LocalFile));
        Assert.False(MediaSwitchLogic.AllowCrossfade(PlayableKind.Audio, PlayableKind.LocalFile));
    }

    [Fact]
    public void SyntheticTrack_ForAGenericPlayable_IsNotLocalOrigin_SoItPlaysAsPlainAudio()
    {
        var file = LocalPlayables.ForMedia(@"D:\clips\live set.mp4");
        Assert.Equal(TrackOrigin.Streamed, file.Origin);
        Assert.Equal(PlayableKind.Audio, MediaSwitchLogic.KindOf(false, file.Origin == TrackOrigin.Local));
        Assert.Equal("live set", file.Title);

        var url = LocalPlayables.ForMedia("https://cdn.test/shows/ep12.mp3?token=abc");
        Assert.Equal("ep12.mp3", url.Title);
        Assert.Equal(0, url.DurationMs);
    }

    [Fact]
    public void SyntheticTrack_UsesTheProbedDurationForAFile_AndNeverProbesAUrl()
    {
        var probed = new List<string>();
        var track = LocalPlayables.ForLocalFile(@"C:\a.mp3", p => { probed.Add(p); return 123_456; });
        Assert.Equal(123_456, track.DurationMs);
        Assert.Equal(new[] { @"C:\a.mp3" }, probed);

        probed.Clear();
        var url = LocalPlayables.ForMedia("https://cdn.test/a.mp3", p => { probed.Add(p); return 5; });
        Assert.Empty(probed);
        Assert.Equal(0, url.DurationMs);
    }

    [Fact]
    public void DropClassification_PrefersAudio_ThenMp4_ThenRefuses()
    {
        Assert.Equal(LocalPlayables.DropAction.PlayAudio, LocalPlayables.ClassifyDrop([@"C:\a.flac"], out var a));
        Assert.Equal(@"C:\a.flac", a);

        Assert.Equal(LocalPlayables.DropAction.PlayVideo, LocalPlayables.ClassifyDrop([@"C:\readme.txt", @"C:\v.mp4"], out var v));
        Assert.Equal(@"C:\v.mp4", v);

        // A mixed drop is not an error — the unambiguous "play this song" gesture wins.
        Assert.Equal(LocalPlayables.DropAction.PlayAudio, LocalPlayables.ClassifyDrop([@"C:\v.mp4", @"C:\a.mp3"], out var m));
        Assert.Equal(@"C:\a.mp3", m);

        Assert.Equal(LocalPlayables.DropAction.None, LocalPlayables.ClassifyDrop([@"C:\a.m4a", @"C:\b.txt"], out _));
        Assert.Equal(LocalPlayables.DropAction.None, LocalPlayables.ClassifyDrop(null, out _));
        Assert.Equal(LocalPlayables.DropAction.None, LocalPlayables.ClassifyDrop(Array.Empty<string>(), out _));
    }

    // ── Connect masking (activated in P4) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ConnectMask_RewritesANonPublishablePlayable_IntoSpotifysOwnLocalShape()
    {
        var track = new Track("id", PlayableUri.ForLocalFile(@"C:\a.mp3"), "Svefn-g-englar",
            [new ArtistRef("", "", "Sigur Rós")], new AlbumRef("", "", "Ágætis byrjun"), 596_000, false, null);

        Assert.Equal("spotify:local:Sigur+R%C3%B3s:%C3%81g%C3%A6tis+byrjun:Svefn-g-englar:596", ConnectUriMask.Mask(track));
    }

    [Fact]
    public void ConnectMask_LeavesUnknownFieldsEmptyBetweenTheColons_AndEscapesFieldColons()
    {
        var bare = new Track("id", PlayableUri.ForLocalFile(@"C:\x.mp3"), "", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 0, false, null);
        Assert.Equal("spotify:local::::0", ConnectUriMask.Mask(bare));

        // A ':' inside a title must never fork the five segments.
        var colon = new Track("id", "wavee:media:x", "Track: The Sequel", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 1_500, false, null);
        var masked = ConnectUriMask.Mask(colon);
        Assert.Equal("spotify:local:::Track%3A+The+Sequel:1", masked);
        Assert.Equal(6, masked.Split(':').Length);   // spotify | local | artist | album | title | durSec — always exactly six
    }

    [Fact]
    public async Task ConnectMask_PublishesSpotifyRowsVerbatim_MasksTheRest_AndKeepsEveryUid()
    {
        var registry = ThreeProviderRegistry(out _);
        var h = new MaskHarness { Publisher = { } };
        h.Publisher.PublishUriMask = ConnectUriMask.For(registry);

        string localUri = PlayableUri.ForLocalFile(@"C:\Music\Nightcall.mp3");
        var localTrack = new Track("id", localUri, "Nightcall", [new ArtistRef("", "", "Kavinsky")],
            new AlbumRef("", "", "OutRun"), 258_000, false, null);

        h.Connect("c1");
        h.SetQueue(
            new QueueEntry(QueueItemId.None, "now", localTrack, QueueBucket.NowPlaying, QueueProvider.Context, false, "u-now"),
            new QueueEntry(QueueItemId.None, "n0", localTrack, QueueBucket.NextUp, QueueProvider.Context, false, "u-n0"),
            new QueueEntry(QueueItemId.None, "n1", T("spotify:track:b"), QueueBucket.NextUp, QueueProvider.Context, false, "u-n1"));
        h.Play(localTrack);
        await Task.Delay(20);

        var snap = Assert.IsType<LocalPlaybackSnapshot>(h.LastSnapshot);
        Assert.Equal("spotify:local:Kavinsky:OutRun:Nightcall:258", snap.Track.Uri);
        Assert.Equal("u-now", snap.Track.Uid);                                    // uid untouched → skip_to still addresses the row
        Assert.Equal("spotify:local:Kavinsky:OutRun:Nightcall:258", snap.NextTracks[0].Uri);   // the NEXT rows are masked too
        Assert.Equal("u-n0", snap.NextTracks[0].Uid);
        Assert.Equal("spotify:track:b", snap.NextTracks[1].Uri);                  // publishable rows stay verbatim
        Assert.Equal("u-n1", snap.NextTracks[1].Uid);
    }

    [Fact]
    public void ConnectContextMask_RewritesEveryContextSpotifyCannotResolve_IntoItsLocalFilesContext()
    {
        // The row mask alone was not enough: a masked row under `context_uri: wavee:module:…` is still a state the
        // connect-state service cannot resolve, so it never adopts us as the cluster's active device — and the state it
        // echoes back then reads to us as somebody ELSE's, masked uris and all.
        Assert.Equal("spotify:playlist:p", ConnectUriMask.MaskContext("spotify:playlist:p"));
        Assert.Equal("spotify:album:a", ConnectUriMask.MaskContext("spotify:album:a"));
        Assert.Equal(ConnectUriMask.LocalFilesContext, ConnectUriMask.MaskContext("wavee:module:wavee.youtube:dFJz"));
        Assert.Equal(ConnectUriMask.LocalFilesContext, ConnectUriMask.MaskContext("wavee:local:file:zzz"));
        // Spotify's own metadata-in-the-uri local shape is not resolvable either — as a row OR as a context.
        Assert.Equal(ConnectUriMask.LocalFilesContext, ConnectUriMask.MaskContext("spotify:local:Claude::Claude+FM:0"));
        // Absent stays absent (nothing is playing) — the mask never invents a context.
        Assert.Null(ConnectUriMask.MaskContext(null));
        Assert.Equal("", ConnectUriMask.MaskContext(""));
    }

    [Fact]
    public async Task ConnectContextMask_ReachesTheWire_AlongsideTheMaskedRow()
    {
        var h = new MaskHarness();
        h.Publisher.PublishUriMask = ConnectUriMask.For(ThreeProviderRegistry(out _));
        h.Publisher.PublishContextMask = ConnectUriMask.ContextMask;

        const string moduleUri = "wavee:module:wavee.youtube:dFJzUXNUTXZQTmc";
        var moduleTrack = new Track("dFJzUXNUTXZQTmc", moduleUri, "Claude FM", [new ArtistRef("", "", "Claude")],
            new AlbumRef("", "", ""), 0, false, null);

        // The context arrives the way a real one does — through the projection.
        h.Proj.OnCluster(new ClusterDelta("", true, new RemoteTrack(moduleUri, "Claude FM", "Claude", "", "", "", null, 0),
            moduleUri, false, true, false, 0, 0, 0, 0, false, RepeatMode.Off,
            Array.Empty<ConnectDeviceRow>(), Array.Empty<RemoteTrack>()));
        h.Connect("c1");
        h.SetQueue(new QueueEntry(QueueItemId.None, "now", moduleTrack, QueueBucket.NowPlaying, QueueProvider.Context, false, "u-now"));
        h.Play(moduleTrack);
        await Task.Delay(20);

        var snap = Assert.IsType<LocalPlaybackSnapshot>(h.LastSnapshot);
        Assert.Equal(ConnectUriMask.LocalFilesContext, snap.ContextUri);
        Assert.Equal("spotify:local:Claude::Claude+FM:0", snap.Track.Uri);
        Assert.Equal("u-now", snap.Track.Uid);
    }

    // ── Prepared-next closes for a local next ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PreparedNext_IsSkipped_WhenTheNextPlayableIsLocal()
    {
        var registry = ThreeProviderRegistry(out _);
        string localUri = PlayableUri.ForLocalFile(@"C:\Music\next.mp3");
        var host = new RecordingAudioHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, registry, projection,
            new FakeContextResolver("spotify:track:a", localUri), "dev", fast: registry);
        controller.CanPrepareNext = t => registry.SupportsPreparedNext(t.Uri);

        await controller.PlayAsync("spotify:playlist:test");
        await Task.Delay(120);

        Assert.Empty(host.Prepared);                                        // the boundary falls back to the proven hard cut
        Assert.Equal(new[] { "spotify:track:a" }, host.Loaded.ToArray());
    }

    // ── End to end: a local file plays, ends, and hands off ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LocalFile_PlaysThroughTheController_ThenEnded_AutoAdvancesToTheQueuedFile()
    {
        var registry = ThreeProviderRegistry(out var files);
        string first = @"C:\Music\one.mp3";
        string second = @"C:\Music\two.flac";
        files.Add(first);
        files.Add(second);

        var host = new RecordingAudioHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, registry, projection,
            new FakeContextResolver(), "dev", fast: registry);

        await controller.PlayTrackAsync(LocalPlayables.ForLocalFile(first));
        await WaitUntilAsync(() => host.Bodies.Count >= 1);

        // The whole path took the EXISTING instant-start shape: an empty head parks the load, the completed body opens it.
        Assert.Equal(new[] { PlayableUri.ForLocalFile(first) }, host.Loaded.ToArray());
        var body = host.Bodies.First();
        Assert.Equal(AudioSourceKind.LocalFile, body.SourceKind);
        Assert.Equal(first, body.CdnUrl);
        Assert.Equal(AudioFormat.Mp3, body.Format);
        Assert.True(host.Playing);
        Assert.Equal(PlayableKind.LocalFile, controller.CurrentMediaKind);
        Assert.Equal(PlayableUri.ForLocalFile(first), projection.CurrentTrack?.Uri);

        await controller.EnqueueAsync(LocalPlayables.ForLocalFile(second));

        host.RaiseEnded();
        await WaitUntilAsync(() => host.Loaded.Count >= 2);

        Assert.Equal(PlayableUri.ForLocalFile(second), host.Loaded.Last());
        await WaitUntilAsync(() => host.Bodies.Count >= 2);
        Assert.Equal(second, host.Bodies.Last().CdnUrl);
        Assert.Equal(AudioFormat.Flac, host.Bodies.Last().Format);
    }

    [Fact]
    public async Task MissingLocalFile_SurfacesTheTypedError_AndNeverLoadsTheHost()
    {
        var registry = ThreeProviderRegistry(out _);   // the fake disk knows no files
        var host = new RecordingAudioHost();
        var projection = new NowPlayingProjection("dev", NotOwnedEntityHydrator.Instance, new InMemoryStore());
        using var controller = new PlaybackController(host, registry, projection, new FakeContextResolver(), "dev", fast: registry);
        AudioKeyFailureReason? reported = null;
        controller.OnPlaybackError = info => reported = info.Reason;

        await controller.PlayTrackAsync(LocalPlayables.ForLocalFile(@"C:\gone\missing.mp3"));
        await WaitUntilAsync(() => reported is not null);

        Assert.Equal(AudioKeyFailureReason.Restricted, reported);
        Assert.Empty(host.Loaded);
    }

    // ── Localization ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LocKeys_ForThePlayFileSurfaces_AreTranslatedInEveryShippedCulture()
    {
        string? locDir = FindLocDir();
        if (locDir is null) return;   // running outside the repo layout — nothing to assert against

        var baseKeys = ReadBlock(Path.Combine(locDir, "en-US.json"), "localFile");
        Assert.NotEmpty(baseKeys);
        foreach (string culture in new[] { "nl.json", "ko-KR.json" })
            Assert.True(baseKeys.SetEquals(ReadBlock(Path.Combine(locDir, culture), "localFile")),
                culture + " is missing/extra localFile keys");

        // …and every key the code actually resolves exists in the base culture (the generator would not catch a key
        // that was renamed in json only — it regenerates the const, and the call site keeps compiling).
        foreach (string key in new[]
                 {
                     Strings.LocalFile.PlayFile, Strings.LocalFile.PickTitle, Strings.LocalFile.Filter,
                     Strings.LocalFile.Rejected, Strings.LocalFile.NotReady, Strings.LocalFile.DropHint,
                 })
        {
            Assert.StartsWith("localFile.", key, StringComparison.Ordinal);
            Assert.Contains(key["localFile.".Length..], baseKeys);
        }
    }

    static HashSet<string> ReadBlock(string file, string group)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty(group, out var block))
            foreach (var p in block.EnumerateObject()) set.Add(p.Name);
        return set;
    }

    static string? FindLocDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
            candidate = Path.Combine(dir.FullName, "src", "apps", "Wavee", "assets", "loc", "en-US.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }
        return null;
    }

    // ── Support ───────────────────────────────────────────────────────────────────────────────────────────────────────

    // The production registration order, over a fake disk: Spotify, then local-file, then generic.
    static MediaProviderRegistry ThreeProviderRegistry(out HashSet<string> files)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        files = known;
        var resolver = new LiveTrackResolver(new NullTransport(), new StubAudioKeySource(),
            (_, _) => Task.FromResult<ByteString?>(null));
        return new MediaProviderRegistry(
            new SpotifyMediaProvider(resolver, new StubFast()),
            new LocalFileMediaProvider(known.Contains),
            new GenericMediaProvider(known.Contains));
    }

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    sealed class StubFast : IFastTrackResolver, IFastTrackWarmer
    {
        public Task<FastStartPlan> ResolveFastAsync(Track track, CancellationToken ct = default)
            => Task.FromResult(new FastStartPlan(
                new AudioFastStart(track.Uri, "", AudioFormat.OggVorbis320, 1000, 0f, new byte[] { 1, 2, 3 }),
                Task.FromResult(new AudioStreamHandle(track.Uri, "", "", default, AudioFormat.OggVorbis320, 1000, 0f))));

        public void Warm(Track track, string reason = "") { }
    }

    sealed class RecordingAudioHost : IAudioHost, IPreparedAudioHost
    {
        readonly SimpleSubject<AudioHostSignal> _signals = new();
        readonly SimpleSubject<AudioTransitionSignal> _transitions = new();
        public ConcurrentQueue<string> Loaded { get; } = new();
        public ConcurrentQueue<AudioStreamHandle> Bodies { get; } = new();
        public ConcurrentQueue<AudioPrepareRequest> Prepared { get; } = new();
        public bool Playing { get; private set; }

        public void Load(in AudioStreamHandle stream) { Loaded.Enqueue(stream.TrackUri); Bodies.Enqueue(stream); }
        public void LoadFastStart(in AudioFastStart start) => Loaded.Enqueue(start.TrackUri);
        public void SupplyBody(in AudioStreamHandle body) => Bodies.Enqueue(body);
        public void Play() => Playing = true;
        public void Pause() => Playing = false;
        public void Stop() => Playing = false;
        public void Seek(long positionMs, SeekMode mode) { }
        public void SetVolume(double volume01) { }
        public long PositionMs => 0;
        public bool IsPlaying => Playing;
        public bool IsBuffering => false;
        public bool ClockValid => true;
        public IObservable<AudioHostSignal> Signals => _signals;
        public IObservable<AudioTransitionSignal> Transitions => _transitions;

        public void RaiseEnded() => _signals.OnNext(new AudioHostSignal(AudioHostSignalKind.Ended, 0));

        public Task PrepareNextAsync(AudioPrepareRequest request, CancellationToken ct = default)
        {
            Prepared.Enqueue(request);
            return Task.CompletedTask;
        }

        public Task SupplyNextBodyAsync(string token, AudioStreamHandle body, CancellationToken ct = default) => Task.CompletedTask;

        public Task<AudioPrepareCancelResult> CancelPreparedAsync(string token, CancellationToken ct = default)
            => Task.FromResult(AudioPrepareCancelResult.Cancelled);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    sealed class MaskHarness
    {
        public readonly StubTransport Transport = new();
        public readonly NowPlayingProjection Proj = new("us", NotOwnedEntityHydrator.Instance, new InMemoryStore(), () => 0);
        public readonly SimpleSubject<string?> ConnId = new(null);
        public string? CurrentConnId;
        public LocalPlaybackSnapshot? LastSnapshot;
        public readonly DeviceStatePublisher Publisher;

        public MaskHarness()
        {
            Publisher = new DeviceStatePublisher(Transport, "us", Proj, ConnId, () => CurrentConnId,
                (reason, snap, mid, active) =>
                {
                    LastSnapshot = snap;
                    return Encoding.UTF8.GetBytes(reason + "|" + active);
                },
                onCluster: null, clock: () => 1000);
        }

        public void Connect(string id) { CurrentConnId = id; ConnId.OnNext(id); }
        public void SetQueue(params QueueEntry[] q) => Proj.SetLocalQueue(q);

        public void Play(Track track)
        {
            var e = new PlaybackEvent(EvKind.Started, track, 0);
            Proj.OnEvent(e);
            Publisher.OnEvent(e);
        }
    }
}
