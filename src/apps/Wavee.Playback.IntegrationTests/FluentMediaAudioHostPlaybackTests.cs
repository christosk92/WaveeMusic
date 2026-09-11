using System.Collections.Concurrent;
using FluentGpu.Media;
using Wavee.Backend;
using Wavee.Backend.Audio;
using Wavee.Core;
using Wavee.SpotifyLive.Audio;
using Xunit;

namespace Wavee.Playback.IntegrationTests;

public sealed class FluentMediaAudioHostPlaybackTests
{
    // Generated from 256 stereo s16 PCM frames at 44.1 kHz: left=sin(i*.125)*12000, right=sin(i*.25)*8000.
    // Encoded with ffmpeg's FLAC encoder at compression level 0; optional metadata omitted.
    static byte[] FlacExample() => Convert.FromBase64String(
        "ZkxhQ4AAACIEgASAAAH3AAH3CsRC8AAAAQAPq6IMYqNbUIl1E2w0L94w//iJGABEFgAABdgLmAkMnzJciRZZ72c9WUWccMEhAwIHjx48aMFCTiynMQrVX7rpEuXPmT506dMmSJVdv/cpCXtMNOFDBY8ePHBAQLHiRRxpTmpRz1Nci2bJnTYiZPnTZcqTbbR7udqJINOFCRwwIHjiCRo8aKFnHFuctOv+suvky50uJmT50ydJk32Vf7nKnkmGChQwYEDxwQPGjxYoQaYQ5Sl02im66TLmTpkVKiJs2VKuvoq5vCnEknnihY0eNCxQWNHDBQk04h60p3TRbbKkzZcRPlxE2LAAAD3Yd9hO5WWm+uvjshCLRqPZ4PZtLpDa0pT8aXYbje7vd7JU/fcw2F+KxrOp8PRwLxJbE9O7qPX7hebxeLNV/rfwt56KZpOp3o9nIwktyUUnpo9ZuF2vl1tlWoHJh7DuUDKdjwfDoYye6LCRyUKs2u83e9WytUTlytR1JphOZ8PR0NBQellB4v2r2m73i73ba9RevO0m4lmA4Hw8nc0FZ7YEDd+apZ7teL1ca/SfDQzGwjl03Hw8Hk1lcgsKKse9Rsl1u98uVgp/lqZDORC0bjwezydrLxAaEhT9qVZLle7td7FU/PbxWMglc1ng9ng4lwjNiap9tLr9yvF5u9lqn3t4C++FYznk8Hw4GAlNyknddGr1uvN5u9qgykE=");

    // Generated 8 kHz mono MP3: 440 Hz before 600 ms, 880 Hz afterwards, encoded at 16 kbit/s.
    // The frequency change distinguishes a physical 1-second seek from the former quarter-position seek.
    static byte[] Mp3Example() => Convert.FromBase64String(
        "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");

    // One second of generated 8 kHz mono PCM, with a valid LAME-compatible delay/padding extension.
    static byte[] TaggedMp3Example() => Convert.FromBase64String(
        "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");

    [Fact]
    public void UntaggedMp3ProducesFirstPcmWithoutScanningPastItsAvailablePrefix()
    {
        using var stream = new PrefixReadStream(Mp3Example(), 2_048);
        var source = new SpotifyMediaByteSource(stream, 0, WaveeDecoderKind.Mp3, 1_500, 1f);
        using var decoder = new SpotifyEngineAudioDecoder();
        try
        {
            Assert.True(source.Caps.Seekable);
            Assert.True(decoder.TryOpen(source, new(8_000, 1), out _));
            Assert.Equal(512, decoder.Read(new float[512]));
            Assert.InRange(stream.Position, 1, 2_048);
        }
        finally { decoder.Dispose(); source.Close(); }
    }

    [Fact]
    public void TaggedMp3DecoderOwnsTrimOnceAndPreservesTheExactDecodedLength()
    {
        using var stream = new MemoryReadStream(TaggedMp3Example());
        var source = new SpotifyMediaByteSource(stream, 0, WaveeDecoderKind.Mp3, 1_000, 1f);
        using var decoder = new SpotifyEngineAudioDecoder();
        try
        {
            Assert.True(decoder.TryOpen(source, new(8_000, 1), out _));
            Assert.Equal(0, decoder.Gapless.LeadInFrames);
            Assert.Equal(0, decoder.Gapless.TrailPadFrames);
            Assert.Equal(8_000, decoder.Gapless.ExactFrames);
            var block = new float[512];
            int total = 0;
            for (int count; (count = decoder.Read(block)) > 0;) total += count;
            Assert.Equal(8_000, total);
        }
        finally { decoder.Dispose(); source.Close(); }
    }

    [Fact]
    public void RealMp3SeekUsesPcmByteUnitsAndReturnsAudioFromTheAchievedSecond()
    {
        using var stream = new MemoryReadStream(Mp3Example());
        var source = new SpotifyMediaByteSource(stream, 0, WaveeDecoderKind.Mp3, 1_500, 1f);
        using var decoder = new SpotifyEngineAudioDecoder();
        try
        {
            Assert.True(decoder.TryOpen(source, new(8_000, 1), out _));
            Assert.Equal(8_000, decoder.Seek(8_000));
            var pcm = new float[512];
            Assert.Equal(pcm.Length, decoder.Read(pcm));
            double Energy(double hz)
            {
                double real = 0, imaginary = 0;
                for (int i = 0; i < pcm.Length; i++)
                {
                    double angle = 2 * Math.PI * hz * i / 8_000;
                    real += pcm[i] * Math.Cos(angle);
                    imaginary += pcm[i] * Math.Sin(angle);
                }
                return real * real + imaginary * imaginary;
            }
            Assert.True(Energy(880) > 10 * Energy(440));
        }
        finally { decoder.Dispose(); source.Close(); }
    }

    [Fact]
    public void RealFlacDecoderCanSeekBackwardAndDisposesItsViewWithoutClosingTheOuterSource()
    {
        using var stream = new MemoryReadStream(FlacExample());
        var source = new SpotifyMediaByteSource(stream, 0, WaveeDecoderKind.Flac, 1_000, 1f);
        var decoder = new SpotifyEngineAudioDecoder();
        try
        {
            Assert.True(decoder.TryOpen(source, new(44_100, 2), out _));
            float[] first = new float[2];
            Assert.Equal(1, decoder.Read(first));
            Assert.Equal(256, decoder.Seek(1000));
            Assert.Equal(0, decoder.Seek(0));
            float[] replayed = new float[2];
            Assert.Equal(1, decoder.Read(replayed));
            Assert.Equal(first, replayed);

            decoder.Dispose();
            Assert.True(stream.CanRead);
            Assert.Throws<InvalidOperationException>(() => decoder.Seek(0));
            source.Close();
            Assert.False(stream.CanRead);
        }
        finally { decoder.Dispose(); source.Close(); }
    }

    [Fact]
    public void FailedRealDecoderOpenLeavesTheOuterByteSourceWithItsOwner()
    {
        using var stream = new MemoryReadStream(new byte[128]);
        var source = new SpotifyMediaByteSource(stream, 0, WaveeDecoderKind.Flac, 1_000, 1f);
        using var decoder = new SpotifyEngineAudioDecoder();
        try
        {
            Assert.ThrowsAny<Exception>(() => decoder.TryOpen(source, new(44_100, 2), out _));
            Assert.True(stream.CanRead);
            Assert.Throws<InvalidOperationException>(() => decoder.Seek(0));
        }
        finally { source.Close(); }
        Assert.False(stream.CanRead);
    }

    sealed class PrefixReadStream(byte[] bytes, int availableBytes) : MemoryReadStream(bytes)
    {
        public override int Read(Span<byte> buffer)
        {
            if (Position >= availableBytes)
                throw new IOException("The decoder attempted to scan beyond the available MP3 prefix.");
            return base.Read(buffer[..Math.Min(buffer.Length, availableBytes - (int)Position)]);
        }
    }

    class MemoryReadStream(byte[] bytes) : MemoryStream(bytes), IAudioReadStream
    {
        public Stream AsStream() => this;
        public int TryRead(Span<byte> destination, out bool wouldBlock) { wouldBlock = false; return Read(destination); }
        public long DataVersion => 0;
        public void WaitForData(long version, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The complete in-memory fixture never waits for data.");
        public long CurrentOffset => Position;
        public long KnownSize => Length;
        public bool IsBodyAttached => true;
        public int ClearHeadLength => 0;
        public IDisposable PauseReadAhead() => new NoopScope();
        public void ResumeReadAheadAtCurrentOffset() { }
        sealed class NoopScope : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task PlayingPauseResumeAndManualNextKeepOneEndpointAndFreezeThePlayedClock()
    {
        await using var fixture = new PlaybackFixture();
        var initial = new PlaybackCommandId(1, 1);
        var loaded = fixture.Applied(initial);
        fixture.Host.Load(fixture.Load(initial, 1, "playing") with { PlayWhenReady = true });
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Until(() => fixture.Host.PositionMs > 0 && fixture.Host.Diagnostics.PlayedFrames > 0);

        var pause = new PlaybackCommandId(1, 2);
        var paused = fixture.Applied(pause);
        fixture.Host.Submit(new(pause, AudioTransportAction.Pause, false));
        await paused.WaitAsync(TimeSpan.FromSeconds(10));
        long frozenPosition = fixture.Host.PositionMs;
        fixture.Endpoint!.TryGetPlayed(out long frozenFrames, out _);
        int stoppedAt = fixture.PumpCount;
        await fixture.Until(() => fixture.PumpCount >= stoppedAt + 20);
        fixture.Endpoint.TryGetPlayed(out long stillFrozenFrames, out _);
        Assert.Equal(frozenFrames, stillFrozenFrames);
        Assert.Equal(frozenPosition, fixture.Host.PositionMs);
        Assert.False(fixture.Endpoint.IsStarted);

        var resume = new PlaybackCommandId(1, 3);
        var resumed = fixture.Applied(resume);
        fixture.Host.Submit(new(resume, AudioTransportAction.Play, true));
        await resumed.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Until(() => fixture.Host.PositionMs > frozenPosition);

        await fixture.Host.PrepareNextAsync(new("next-playing", new(1, new(1)), new(2), fixture.Plan("next-playing"), false))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Host.Diagnostics.NextPcmMs >= 500);
        fixture.Host.Submit(new(new(1, 4), AudioTransportAction.Skip, true));
        Assert.True(await fixture.Host.TryPromotePreparedAsync(new("next-playing", new(2, 5), new(2), true))
            .WaitAsync(TimeSpan.FromSeconds(10)));
        await fixture.Until(() => fixture.Host.PositionMs > 0 && fixture.Host.IsPlaying);

        Assert.Equal(1, fixture.EndpointOpenCount);
        Assert.True(fixture.Endpoint.StartCount >= 2);
        Assert.True(fixture.Endpoint.StopCount >= 1);
        Assert.Contains(fixture.Endpoint.Captured.ToArray(), sample => Math.Abs(sample) > 0.001f);
        Assert.Equal(0, fixture.Host.Diagnostics.UnexpectedUnderruns);
    }

    [Fact]
    public async Task UnknownDurationUsesProducerEofForGaplessPlaybackBeforeTheEndpointStops()
    {
        await using var fixture = new PlaybackFixture
        {
            MetadataDurationMs = 0,
            SourceDurationMs = 2_000,
            ReportsExactLength = false,
            PumpDelayMs = 5
        };
        var initial = new PlaybackCommandId(1, 1);
        var loaded = fixture.Applied(initial);
        fixture.Host.Load(fixture.Load(initial, 1, "unknown-first"));
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Host.PrepareNextAsync(new("unknown-next", new(1, new(1)), new(2), fixture.Plan("unknown-next"), false))
            .WaitAsync(TimeSpan.FromSeconds(10));
        var started = fixture.Started("unknown-next");
        fixture.Host.Submit(new(new(1, 2), AudioTransportAction.Play, true));
        AudioTransitionSignal transition = await started.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, transition.EffectiveFadeMs);
        Assert.Equal(1, fixture.EndpointOpenCount);
        Assert.Equal(1, fixture.Endpoint!.StartCount);
        Assert.Equal(0, fixture.Endpoint.StopCount);
        Assert.Equal(0, fixture.Endpoint.ResetCount);
        Assert.Equal(0, fixture.Host.Diagnostics.UnexpectedUnderruns);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualNextReusesAnAlreadyScheduledUncommittedRing(bool playAfterNext)
    {
        await using var fixture = new PlaybackFixture
        {
            MetadataDurationMs = 4_000,
            SourceDurationMs = 4_000,
            PumpDelayMs = 5
        };
        var initial = new PlaybackCommandId(1, 1);
        var loaded = fixture.Applied(initial);
        fixture.Host.Load(fixture.Load(initial, 1, "scheduled-first"));
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Host.PrepareNextAsync(new("scheduled-next", new(1, new(1)), new(2), fixture.Plan("scheduled-next"), false))
            .WaitAsync(TimeSpan.FromSeconds(10));
        var natural = fixture.Started("scheduled-next");
        fixture.Host.Submit(new(new(1, 2), AudioTransportAction.Play, true));
        await fixture.Until(() => fixture.Host.Diagnostics.NextScheduled);
        Assert.True(fixture.Host.Diagnostics.NextReady);
        Assert.True(fixture.Host.Diagnostics.NextPcmMs >= 500);
        var pause = new PlaybackCommandId(1, 3);
        var paused = fixture.Applied(pause);
        fixture.Host.Submit(new(pause, AudioTransportAction.Pause, false));
        await paused.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(natural.IsCompleted);
        Assert.Equal(2, fixture.DecoderCount);

        bool promoted = await fixture.Host.TryPromotePreparedAsync(new("scheduled-next", new(2, 4), new(2), playAfterNext))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(promoted);
        Assert.Equal(2, fixture.DecoderCount);
        Assert.Equal(1, fixture.EndpointOpenCount);
        Assert.Equal(playAfterNext, fixture.Host.PlayIntent);
        Assert.False(natural.IsCompleted);
        if (playAfterNext) await fixture.Until(() => fixture.Host.IsPlaying && fixture.Host.PositionMs > 0);
        else { Assert.Equal(0, fixture.Host.PositionMs); Assert.False(fixture.Endpoint!.IsStarted); }
        Assert.Equal(0, fixture.Host.Diagnostics.UnexpectedUnderruns);
    }

    [Theory]
    [InlineData(48_000, 2, 2)]
    [InlineData(44_100, 1, 3)]
    public async Task PausedDeviceRebuildRestoresTheAudiblePositionAndNegotiatedFormat(int rate, int channels, int opens)
    {
        await using var fixture = new PlaybackFixture();
        var initial = new PlaybackCommandId(1, 1);
        var loaded = fixture.Applied(initial);
        fixture.Host.Load(fixture.Load(initial, 1, "device-recovery"));
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        var seek = new PlaybackCommandId(1, 2);
        var sought = fixture.Applied(seek);
        fixture.Host.Submit(new(seek, AudioTransportAction.Seek, false, 2_000));
        await sought.WaitAsync(TimeSpan.FromSeconds(10));
        int decodersBefore = fixture.DecoderCount;

        await fixture.RebuildDevice(new(rate, channels)).WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Until(() => fixture.DecoderCount > decodersBefore && fixture.Host.ClockValid
            && fixture.Host.Diagnostics.SampleRate == rate && fixture.Endpoint!.ResetCount > 0);

        Assert.Equal(2_000, fixture.Host.PositionMs);
        Assert.False(fixture.Host.PlayIntent);
        Assert.False(fixture.Endpoint!.IsStarted);
        Assert.Equal(channels, fixture.Host.Diagnostics.Channels);
        Assert.Equal(opens, fixture.EndpointOpenCount);
        Assert.True(fixture.Host.Diagnostics.CurrentPcmMs >= 100);
    }

    [Theory]
    [InlineData(48_000, 2, false, 2)]
    [InlineData(48_000, 2, true, 2)]
    [InlineData(44_100, 1, false, 3)]
    [InlineData(44_100, 1, true, 3)]
    public async Task LiveDeviceRecoveryReopensAtLiveEdgeAndHonorsIntentDuringReconnect(
        int rate, int channels, bool playAfterRecovery, int expectedEndpointOpens)
    {
        await using var fixture = new PlaybackFixture
        {
            UseLiveSource = true, MetadataDurationMs = 0, ReportsExactLength = false,
            HoldLiveReconnect = true
        };
        var initial = new PlaybackCommandId(1, 1);
        var loaded = fixture.Applied(initial);
        fixture.Host.Load(fixture.Load(initial, 1, "live-device-recovery") with { PlayWhenReady = true });
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Until(() => fixture.Host.PositionMs > 0);

        await fixture.RebuildDevice(new(rate, channels)).WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.LiveReconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pause = new PlaybackCommandId(1, 2);
        var paused = fixture.Applied(pause);
        fixture.Host.Submit(new(pause, AudioTransportAction.Pause, false));
        await paused.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(fixture.Host.PlayIntent);
        if (playAfterRecovery)
        {
            var resumeDuringRecovery = new PlaybackCommandId(1, 3);
            var resumedDuringRecovery = fixture.Applied(resumeDuringRecovery);
            fixture.Host.Submit(new(resumeDuringRecovery, AudioTransportAction.Play, true));
            await resumedDuringRecovery.WaitAsync(TimeSpan.FromSeconds(10));
        }
        fixture.LiveReconnectRelease.TrySetResult();
        await fixture.Until(() => fixture.DecoderCount == 2 && fixture.Host.ClockValid
            && fixture.Host.Diagnostics.SampleRate == rate);

        Assert.Equal(2, fixture.LiveOpenCount);
        Assert.Equal(expectedEndpointOpens, fixture.EndpointOpenCount);
        Assert.Equal(channels, fixture.Host.Diagnostics.Channels);
        Assert.Equal(playAfterRecovery, fixture.Host.PlayIntent);
        Assert.False(fixture.SeekEntered.Task.IsCompleted); // Recovery must never index a forward-only station.
        if (!playAfterRecovery)
        {
            Assert.Equal(0, fixture.Host.PositionMs);
            Assert.False(fixture.Endpoint!.IsStarted);
            int pumps = fixture.PumpCount;
            await fixture.Until(() => fixture.PumpCount >= pumps + 20);
            Assert.Equal(0, fixture.Host.PositionMs);
            var resume = new PlaybackCommandId(1, 4);
            var resumed = fixture.Applied(resume);
            fixture.Host.Submit(new(resume, AudioTransportAction.Play, true));
            await resumed.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await fixture.Until(() => fixture.Host.PositionMs > 0 && fixture.Endpoint!.IsStarted);
        // Findings 2026-09-06 #4: a seek on a live (non-seekable) source is REJECTED without a fault — the operation
        // settles as Superseded, the position is left untouched and playback continues. A Failed signal here would
        // reach the controller's ReportPlaybackError and stop the stream with an error toast.
        var seek = new PlaybackCommandId(1, 5);
        var rejected = fixture.Superseded(seek);
        fixture.Host.Submit(new(seek, AudioTransportAction.Seek, true, 2_000));
        AudioHostSignal settled = await rejected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PlaybackOperationStatus.Superseded, settled.OperationStatus);
        long position = fixture.Host.PositionMs;
        await fixture.Until(() => fixture.Host.PositionMs > position);
        Assert.True(fixture.Host.ClockValid);
        Assert.True(fixture.Host.PlayIntent);
        Assert.Equal(2, fixture.LiveOpenCount);
        Assert.Equal(0, fixture.Host.Diagnostics.UnexpectedUnderruns);
    }

    [Fact]
    public async Task PausedPreparedPromotionRetainsEndpointAndFilledPcm()
    {
        await using var fixture = new PlaybackFixture();
        var initial = new PlaybackCommandId(1, 1);
        Task<AudioHostSignal> loaded = fixture.Applied(initial);
        fixture.Host.Load(fixture.Load(initial, 1, "first"));
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        var next = fixture.Plan("second");
        await fixture.Host.PrepareNextAsync(new AudioPrepareRequest("next", new(1, new(1)), new(2), next, true))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Host.Diagnostics.NextReady);
        Assert.True(fixture.Host.Diagnostics.NextPcmMs >= 500);

        bool promoted = await fixture.Host.TryPromotePreparedAsync(new AudioPromoteRequest(
            "next", new(2, 2), new(2), false)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(promoted);
        Assert.Equal(1, fixture.EndpointOpenCount);
        Assert.False(fixture.Host.PlayIntent);
        Assert.False(fixture.Host.IsPlaying);
        Assert.Equal(0, fixture.Host.PositionMs);
        Assert.False(fixture.Host.Diagnostics.NextReady);
        Assert.True(fixture.Host.Diagnostics.CurrentPcmMs >= 100);
        Assert.Equal(0, fixture.Endpoint!.StartCount);
        Assert.Empty(fixture.Endpoint.Captured.ToArray());
    }

    [Fact]
    public async Task SeekAcknowledgementWaitsForReplacementAndReportsAchievedPosition()
    {
        await using var fixture = new PlaybackFixture();
        var initial = new PlaybackCommandId(1, 1);
        var loaded = fixture.Applied(initial);
        fixture.Host.Load(fixture.Load(initial, 1, "seekable"));
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.SeekAdjustmentFrames = -480;
        fixture.SeekRelease.Reset();
        var seek = new PlaybackCommandId(1, 2);
        var applied = fixture.Applied(seek);
        try
        {
            fixture.Host.Submit(new(seek, AudioTransportAction.Seek, false, 2_000));
            await fixture.SeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(applied.IsCompleted);
            Assert.False(fixture.Host.PlayIntent);
        }
        finally { fixture.SeekRelease.Set(); }

        AudioHostSignal signal = await applied.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1_990, signal.PositionMs);
        Assert.Equal(1, fixture.EndpointOpenCount);
        Assert.True(fixture.Endpoint!.ResetCount > 0);
        Assert.False(fixture.Host.IsPlaying);
    }

    [Fact]
    public async Task FailedPlayingSeekRestoresTheValidFrozenClockAndCanResume()
    {
        await using var fixture = new PlaybackFixture();
        var initial = new PlaybackCommandId(1, 1);
        var loaded = fixture.Applied(initial);
        fixture.Host.Load(fixture.Load(initial, 1, "seek-failure") with { PlayWhenReady = true });
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Until(() => fixture.Host.PositionMs > 0);
        fixture.ThrowOnSeek = true;
        var seek = new PlaybackCommandId(1, 2);
        Task<AudioHostSignal> failed = fixture.Failed(seek);
        fixture.Host.Submit(new(seek, AudioTransportAction.Seek, true, 2_000));
        AudioHostSignal failure = await failed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("fixture seek failed", failure.Detail);
        Assert.True(fixture.Host.ClockValid);
        Assert.True(fixture.Host.PlayIntent);
        long frozen = fixture.Host.PositionMs;
        int steps = fixture.PumpCount;
        await fixture.Until(() => fixture.PumpCount >= steps + 20);
        Assert.Equal(frozen, fixture.Host.PositionMs);
        Assert.False(fixture.Endpoint!.IsStarted);

        fixture.ThrowOnSeek = false;
        var resume = new PlaybackCommandId(1, 3);
        var resumed = fixture.Applied(resume);
        fixture.Host.Submit(new(resume, AudioTransportAction.Play, true));
        await resumed.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Until(() => fixture.Host.PositionMs > frozen);
        Assert.Equal(1, fixture.EndpointOpenCount);
    }

    [Fact]
    public async Task CancelPreparationWaitingForBodyDoesNotInstallItsRing()
    {
        await using var fixture = new PlaybackFixture();
        var initial = new PlaybackCommandId(1, 1);
        var loaded = fixture.Applied(initial);
        fixture.Host.Load(fixture.Load(initial, 1, "current"));
        await loaded.WaitAsync(TimeSpan.FromSeconds(10));
        var body = new TaskCompletionSource<AudioStreamHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new AudioFastStart("next", "next", AudioFormat.Mp3, 600_000, 0, new byte[32]);
        Task preparing = fixture.Host.PrepareNextAsync(new AudioPrepareRequest(
            "cancel-me", new(1, new(1)), new(2), new(start, body.Task), true));
        await fixture.SecondDecoderOpened.Task.WaitAsync(TimeSpan.FromSeconds(10));

        AudioPrepareCancelResult result = await fixture.Host.CancelPreparedAsync("cancel-me")
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AudioPrepareCancelResult.Cancelled, result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(fixture.Host.Diagnostics.NextReady);
        Assert.False(await fixture.Host.TryPromotePreparedAsync(new("cancel-me", new(2, 2), new(2), false)));
        Assert.Equal(1, fixture.EndpointOpenCount);
    }

    sealed class PlaybackFixture : IAsyncDisposable
    {
        readonly HttpClient _http = new();
        readonly CancellationTokenSource _pumpCancellation = new();
        readonly ConcurrentBag<IDisposable> _subscriptions = new();
        readonly string _file = Path.GetTempFileName();
        readonly Task _pump;
        AudioFeedThread? _feed;
        PcmAudioSession? _session;
        MixFormat _format = new(48_000, 2);
        readonly ConcurrentQueue<Action> _deviceChanges = new();
        int _opens;
        int _decoders;
        int _liveOpens;
        int _pumpCount;
        public int PumpCount => Volatile.Read(ref _pumpCount);
        public readonly ManualResetEventSlim SeekRelease = new(true);
        public readonly TaskCompletionSource SeekEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SecondDecoderOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource LiveReconnectEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource LiveReconnectRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool UseLiveSource;
        public bool HoldLiveReconnect;
        public int SeekAdjustmentFrames;
        public bool ThrowOnSeek;
        public long MetadataDurationMs = 600_000;
        public int SourceDurationMs = 600_000;
        public bool ReportsExactLength = true;
        public int PumpDelayMs = 1;
        public BufferedAudioEndpoint? Endpoint;
        public int EndpointOpenCount => Volatile.Read(ref _opens);
        public int DecoderCount => Volatile.Read(ref _decoders);
        public int LiveOpenCount => Volatile.Read(ref _liveOpens);
        public FluentMediaAudioHost Host { get; }

        public PlaybackFixture()
        {
            File.WriteAllBytes(_file, new byte[128]);
            Host = new FluentMediaAudioHost(static () => null, _http, effects => new PcmAudioPlayer(
                new MixFormat(48_000, 2), format =>
                {
                    Interlocked.Increment(ref _opens);
                    return Endpoint = new BufferedAudioEndpoint(_format, _format.SampleRate / 10);
                }, effects, maxBlock: 480, driveWithOwnThread: false,
                onSessionCreated: session =>
                {
                    Volatile.Write(ref _session, session);
                    Volatile.Write(ref _feed, new AudioFeedThread(session, sampleRate: session.Format.SampleRate));
                },
                decoderFactory: _ => new FixtureDecoder(this)), OpenLiveSourceAsync);
            _pump = Task.Run(async () =>
            {
                try
                {
                    while (!_pumpCancellation.IsCancellationRequested)
                    {
                        while (_deviceChanges.TryDequeue(out var change)) change();
                        if (Volatile.Read(ref _feed) is { } feed)
                        {
                            feed.WorkerPumpOnce();
                            feed.ControlTickOnce();
                            feed.FeedOnce();
                        }
                        Endpoint?.AdvanceHardware(Math.Max(1, _format.SampleRate / 100));
                        Interlocked.Increment(ref _pumpCount);
                        await Task.Delay(PumpDelayMs, _pumpCancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (_pumpCancellation.IsCancellationRequested) { }
            });
        }

        Task<LiveHttpAudioStream> OpenLiveSourceAsync(string url, CancellationToken ct)
            => LiveHttpAudioStream.OpenAsync(url, async (address, cancellation) =>
            {
                if (Interlocked.Increment(ref _liveOpens) > 1 && HoldLiveReconnect)
                {
                    LiveReconnectEntered.TrySetResult();
                    await LiveReconnectRelease.Task.WaitAsync(cancellation);
                }
                return new LiveHttpResponse(200, new Dictionary<string, string>
                {
                    ["content-type"] = "audio/mpeg"
                }, new LivePrefixBody(), address);
            }, new LiveHttpOptions(CapacityBytes: 64 * 1024, PrefillBytes: 1, ReadIdleTimeoutMs: 60_000), ct: ct);

        public Task RebuildDevice(MixFormat format)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _deviceChanges.Enqueue(() =>
            {
                try
                {
                    _format = format;
                    Interlocked.Increment(ref _opens);
                    var replacement = new BufferedAudioEndpoint(format, format.SampleRate / 10);
                    if (Volatile.Read(ref _session) is not { } session || !session.RebuildSink(replacement))
                        throw new InvalidOperationException("The fixture session could not replace its device.");
                    Endpoint = replacement;
                    completed.TrySetResult();
                }
                catch (Exception error) { completed.TrySetException(error); }
            });
            return completed.Task;
        }

        public async Task Until(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!predicate()) await Task.Delay(2, timeout.Token);
        }

        public FastStartPlan Plan(string uri)
        {
            var start = new AudioFastStart(uri, uri, AudioFormat.Mp3, MetadataDurationMs, 0, default);
            var body = new AudioStreamHandle(uri, uri, UseLiveSource ? "http://station.test/live" : _file,
                default, AudioFormat.Mp3, MetadataDurationMs, 0,
                SourceKind: UseLiveSource ? AudioSourceKind.LiveStream : AudioSourceKind.LocalFile);
            return new(start, Task.FromResult(body));
        }

        public AudioLoadRequest Load(PlaybackCommandId command, ulong item, string uri)
            => new(command, new(command.ItemGeneration, new(item)), Plan(uri), 0, false);

        public Task<AudioTransitionSignal> Started(string token)
        {
            var completion = new TaskCompletionSource<AudioTransitionSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            _subscriptions.Add(Host.Transitions.Subscribe(new TransitionObserver(signal =>
            {
                if (signal.Token == token && signal.Kind == AudioTransitionKind.Started) completion.TrySetResult(signal);
            })));
            return completion.Task;
        }

        public Task<AudioHostSignal> Failed(PlaybackCommandId command)
        {
            var completion = new TaskCompletionSource<AudioHostSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            _subscriptions.Add(Host.Signals.Subscribe(new Observer(signal =>
            {
                if (signal.Command == command && signal.OperationStatus == PlaybackOperationStatus.Failed)
                    completion.TrySetResult(signal);
            })));
            return completion.Task;
        }

        public Task<AudioHostSignal> Applied(PlaybackCommandId command)
        {
            var completion = new TaskCompletionSource<AudioHostSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            _subscriptions.Add(Host.Signals.Subscribe(new Observer(signal =>
            {
                if (signal.Command == command && signal.OperationStatus == PlaybackOperationStatus.Applied)
                    completion.TrySetResult(signal);
                if (signal.Command == command && signal.OperationStatus == PlaybackOperationStatus.Failed)
                    completion.TrySetException(new InvalidOperationException(signal.Detail));
            })));
            return completion.Task;
        }

        /// <summary>A benign rejection: the command settles as Superseded. A Failed signal for the same command
        /// faults the task, so a regression back to fault-on-reject fails the test instead of timing out.</summary>
        public Task<AudioHostSignal> Superseded(PlaybackCommandId command)
        {
            var completion = new TaskCompletionSource<AudioHostSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            _subscriptions.Add(Host.Signals.Subscribe(new Observer(signal =>
            {
                if (signal.Command == command && signal.OperationStatus == PlaybackOperationStatus.Superseded)
                    completion.TrySetResult(signal);
                if (signal.Command == command && signal.OperationStatus == PlaybackOperationStatus.Failed)
                    completion.TrySetException(new InvalidOperationException("seek faulted: " + signal.Detail));
            })));
            return completion.Task;
        }

        public async ValueTask DisposeAsync()
        {
            SeekRelease.Set();
            LiveReconnectRelease.TrySetResult();
            _pumpCancellation.Cancel();
            await _pump;
            try { await Host.DisposeAsync(); }
            finally
            {
                foreach (var subscription in _subscriptions) subscription.Dispose();
                _http.Dispose();
                SeekRelease.Dispose();
                _pumpCancellation.Dispose();
                File.Delete(_file);
            }
        }

        sealed class FixtureDecoder(PlaybackFixture owner) : IAudioDecoder, IDisposable
        {
            long Frames = 48_000L * 600;
            int _channels;
            long _position;
            Stream? _liveView;
            public GaplessInfo Gapless => owner.ReportsExactLength ? new(0, 0, Frames, true) : GaplessInfo.None;
            public bool TryOpen(IMediaByteSource source, MixFormat target, out DecodedInfo info)
            {
                if (owner.UseLiveSource)
                {
                    if (source.Caps.Seekable) throw new InvalidOperationException("The live source unexpectedly supports seeking.");
                    _liveView = ((SpotifyMediaByteSource)source).OpenDecodeStream();
                    if (_liveView.ReadByte() < 0) throw new IOException("The station did not supply its initial bytes.");
                }
                _channels = target.Channels;
                Frames = target.SampleRate * (long)owner.SourceDurationMs / 1000;
                info = new(default, target, TimeSpan.FromMilliseconds(owner.MetadataDurationMs), default);
                if (Interlocked.Increment(ref owner._decoders) == 2) owner.SecondDecoderOpened.TrySetResult();
                return true;
            }
            public int Read(Span<float> destination)
            {
                int count = (int)Math.Min(destination.Length / _channels, Frames - _position);
                destination[..(count * _channels)].Fill(0.125f);
                _position += count;
                return count;
            }
            public long Seek(long frame)
            {
                owner.SeekEntered.TrySetResult();
                if (_liveView is not null) throw new NotSupportedException("A live decoder cannot seek.");
                if (owner.ThrowOnSeek) throw new IOException("fixture seek failed");
                owner.SeekRelease.Wait();
                return _position = Math.Clamp(frame + owner.SeekAdjustmentFrames, 0, Frames);
            }
            public void Dispose() => _liveView?.Dispose();
        }

        sealed class LivePrefixBody : Stream
        {
            int _remaining = 4_096;
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (_remaining <= 0) return WaitForCancellationAsync(ct);
                int count = Math.Min(buffer.Length, _remaining);
                buffer.Span[..count].Fill(0x55);
                _remaining -= count;
                return ValueTask.FromResult(count);
            }
            static async ValueTask<int> WaitForCancellationAsync(CancellationToken ct)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 0;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 4_096 - _remaining; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
        }
    }

    sealed class TransitionObserver(Action<AudioTransitionSignal> next) : IObserver<AudioTransitionSignal>
    {
        public void OnNext(AudioTransitionSignal value) => next(value);
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
    }

    sealed class Observer(Action<AudioHostSignal> next) : IObserver<AudioHostSignal>
    {
        public void OnNext(AudioHostSignal value) => next(value);
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
    }
}
