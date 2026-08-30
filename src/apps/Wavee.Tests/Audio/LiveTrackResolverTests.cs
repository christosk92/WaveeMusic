using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Wavee.SpotifyLive;
using Xunit;
using M = Wavee.Protocol.Metadata;
using Af = Wavee.Protocol.Audiofiles;

namespace Wavee.Tests.Audio;

public class LiveTrackResolverTests
{
    static byte[] Id(byte s) => A.Bytes(s, 20);
    static byte[] Gid(byte s) => A.Bytes(s, 16);
    static Track DomainTrack(string uri = "spotify:track:abc") =>
        new("abc", uri, "name", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 180000, false, null);

    static M.AudioFile File(byte[] id, M.AudioFile.Types.Format fmt) =>
        new() { FileId = ByteString.CopyFrom(id), Format = fmt };

    static ByteString TrackV4(byte[]? oggId = null, M.AudioFile.Types.Format oggFmt = M.AudioFile.Types.Format.OggVorbis320, bool withAudio = true)
    {
        var t = new M.Track { Gid = ByteString.CopyFrom(Gid(1)), Duration = 180000 };
        if (oggId is not null) t.File.Add(File(oggId, oggFmt));
        if (withAudio) t.OriginalAudio = new M.Audio { Uuid = ByteString.CopyFrom(Gid(9)) };
        return t.ToByteString();
    }

    static ByteString AudioFiles(byte[] flacId, M.AudioFile.Types.Format fmt = M.AudioFile.Types.Format.FlacFlac, float loud = -8.87f, float peak = 1.35f)
    {
        var r = new Af.AudioFilesExtensionResponse
        {
            DefaultFileNormalizationParams = new Af.NormalizationParams { LoudnessDb = loud, TruePeakDb = peak },
        };
        r.Files.Add(new Af.ExtendedAudioFile { File = File(flacId, fmt), AverageBitrate = 925665 });
        return r.ToByteString();
    }

    static Func<string, CancellationToken, Task<ByteString?>> Fetch(ByteString? v) => (_, _) => Task.FromResult(v);

    // ── SelectOgg (pure) ──────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SelectOgg_PicksHighestQuality()
    {
        var t = new M.Track { Gid = ByteString.CopyFrom(Gid(1)) };
        t.File.Add(File(Id(9), M.AudioFile.Types.Format.OggVorbis96));
        t.File.Add(File(Id(3), M.AudioFile.Types.Format.OggVorbis320));
        var sel = LiveTrackResolver.SelectOgg(t);
        Assert.NotNull(sel);
        Assert.Equal(AudioFormat.OggVorbis320, sel!.Value.fmt);
        Assert.Equal(Id(3), sel.Value.fileId);
        Assert.Equal(Gid(1), sel.Value.gid);
    }

    [Fact]
    public void SelectOgg_FallsThroughToAlternative_WithItsGid()
    {
        var t = new M.Track { Gid = ByteString.CopyFrom(Gid(1)) };   // main track has NO files (market-restricted)
        var alt = new M.Track { Gid = ByteString.CopyFrom(Gid(2)) };
        alt.File.Add(File(Id(5), M.AudioFile.Types.Format.OggVorbis160));
        t.Alternative.Add(alt);
        var sel = LiveTrackResolver.SelectOgg(t);
        Assert.NotNull(sel);
        Assert.Equal(AudioFormat.OggVorbis160, sel!.Value.fmt);
        Assert.Equal(Gid(2), sel.Value.gid);   // the ALTERNATIVE's gid (the file belongs to it)
    }

    [Fact]
    public void SelectOgg_NullWhenNoOgg()
    {
        var t = new M.Track { Gid = ByteString.CopyFrom(Gid(1)) };
        t.File.Add(File(Id(8), M.AudioFile.Types.Format.Aac24));   // non-Ogg only
        Assert.Null(LiveTrackResolver.SelectOgg(t));
    }

    // ── SelectFlac (pure) ─────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void SelectFlac_Prefers24Bit()
    {
        var r = new Af.AudioFilesExtensionResponse();
        r.Files.Add(new Af.ExtendedAudioFile { File = File(Id(1), M.AudioFile.Types.Format.FlacFlac) });
        r.Files.Add(new Af.ExtendedAudioFile { File = File(Id(2), M.AudioFile.Types.Format.FlacFlac24Bit) });
        var sel = LiveTrackResolver.SelectFlac(r);
        Assert.NotNull(sel);
        Assert.Equal(AudioFormat.Flac24, sel!.Value.fmt);
        Assert.Equal(Id(2), sel.Value.fileId);
    }

    [Fact]
    public void SelectFlac_NullWhenNoFlac()
    {
        var r = new Af.AudioFilesExtensionResponse();
        r.Files.Add(new Af.ExtendedAudioFile { File = File(Id(1), M.AudioFile.Types.Format.OggVorbis320) });  // AUDIO_FILES can list Ogg too
        Assert.Null(LiveTrackResolver.SelectFlac(r));
    }

    // ── ResolveMetaAsync (extended-metadata sourcing) ─────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ResolveMeta_OggOnly_WhenNoAudioFiles()
    {
        var r = new LiveTrackResolver(null!, new StubAudioKeySource(), Fetch(TrackV4(Id(3))), fetchAudioFilesV5: null, preferLossless: false);
        var meta = await r.ResolveMetaAsync(DomainTrack());
        Assert.Equal(AudioFormat.OggVorbis320, meta.Fmt);
        Assert.Equal(Id(3), meta.FileId);
    }

    [Fact]
    public async Task ResolveMeta_PrefersFlac_WhenAvailable()
    {
        var r = new LiveTrackResolver(null!, new StubAudioKeySource(),
            Fetch(TrackV4(Id(3))), Fetch(AudioFiles(Id(7))), preferLossless: true);
        var meta = await r.ResolveMetaAsync(DomainTrack());
        Assert.Equal(AudioFormat.Flac, meta.Fmt);
        Assert.Equal(Id(7), meta.FileId);
        Assert.Equal(Gid(1), meta.FileGid);   // FLAC uses the track's gid
        // gain = target(-14) - loudness(-8.87) = -5.13; peak headroom (-1 - 1.35 = -2.35) doesn't bind.
        Assert.Equal(-5.13f, meta.NormalizationGainDb, 2);
    }

    [Fact]
    public async Task ResolveMeta_FallsBackToOgg_WhenAudioFilesFetchThrows()
    {
        Func<string, CancellationToken, Task<ByteString?>> flacThrows = (_, _) => throw new InvalidOperationException("AUDIO_FILES 500");
        var r = new LiveTrackResolver(null!, new StubAudioKeySource(), Fetch(TrackV4(Id(3))), flacThrows, preferLossless: true);
        var meta = await r.ResolveMetaAsync(DomainTrack());
        Assert.Equal(AudioFormat.OggVorbis320, meta.Fmt);   // graceful: lossless probe failed → Ogg
    }

    [Fact]
    public async Task ResolveMeta_PreferLosslessFalse_KeepsOgg_EvenIfFlacExists()
    {
        var r = new LiveTrackResolver(null!, new StubAudioKeySource(),
            Fetch(TrackV4(Id(3))), Fetch(AudioFiles(Id(7))), preferLossless: false);
        var meta = await r.ResolveMetaAsync(DomainTrack());
        Assert.Equal(AudioFormat.OggVorbis320, meta.Fmt);
    }

    [Fact]
    public async Task ResolveMeta_NoPlayableFile_ThrowsRestricted()
    {
        var r = new LiveTrackResolver(null!, new StubAudioKeySource(), Fetch(TrackV4(oggId: null)), fetchAudioFilesV5: null, preferLossless: false);
        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.ResolveMetaAsync(DomainTrack()));
        Assert.Equal(AudioKeyFailureReason.Restricted, ex.Reason);
    }

    [Fact]
    public async Task ResolveMeta_NoTrackV4_ThrowsRestricted()
    {
        var r = new LiveTrackResolver(null!, new StubAudioKeySource(), Fetch(null), fetchAudioFilesV5: null, preferLossless: false);
        var ex = await Assert.ThrowsAsync<AudioPlaybackException>(() => r.ResolveMetaAsync(DomainTrack()));
        Assert.Equal(AudioKeyFailureReason.Restricted, ex.Reason);
    }

    [Fact]
    public async Task ResolveMeta_ConcurrentSameTrack_Coalesces_ToOneFetch()
    {
        int calls = 0;
        var bytes = TrackV4(Id(3));
        Func<string, CancellationToken, Task<ByteString?>> ft = async (_, _) => { Interlocked.Increment(ref calls); await Task.Delay(60); return bytes; };
        var r = new LiveTrackResolver(null!, new StubAudioKeySource(), ft, fetchAudioFilesV5: null, preferLossless: false);

        var track = DomainTrack();
        var metas = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => r.ResolveMetaAsync(track)));

        Assert.Equal(1, calls);   // in-flight coalesced + cached
        Assert.All(metas, m => Assert.Equal(Id(3), m.FileId));
    }
}
