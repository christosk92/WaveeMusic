using System;
using System.IO;
using FluentGpu.Media.Windows;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>AAC (ADTS) behind the same <see cref="ISampleSource"/> pull contract the Vorbis/FLAC/MP3 leaves satisfy, so
/// the engine's decode edge stays codec-agnostic. Frames come from <see cref="AdtsFrameReader"/> (which resyncs over the
/// splice a live reconnect leaves) and the actual decode is Media Foundation's in-box AAC MFT
/// (<see cref="MfAacDecoder"/>).
///
/// <para><b>Priming is not optional.</b> HE-AAC signals SBR/PS in-band, so the MFT accepts the core-rate input type and
/// only reveals its real (doubled) output rate on the first <c>ProcessOutput</c>, as a stream change. Publishing
/// <see cref="SampleRate"/> before that happens would bind the mixer's resampler to half the true rate — audibly
/// pitched-down playback. So the constructor decodes up to <see cref="MaxPrimeFrames"/> frames, discards anything
/// produced before a format change, and only then reports the format.</para>
///
/// <para>After priming a format change is a genuine mid-stream encoding switch: this throws, the voice faults, the host
/// maps it to a typed live drop, and the retry re-opens the stream — which re-primes at the new format. That is strictly
/// better than silently resampling from a stale rate.</para></summary>
internal sealed class AacSampleSource : ISampleSource
{
    /// <summary>Enough frames to get past the implicit-SBR stream change (which lands on the first or second) without
    /// burning real audio if the station is plain AAC-LC.</summary>
    const int MaxPrimeFrames = 8;

    readonly AdtsFrameReader _reader;
    readonly MfAacDecoder _decoder;
    readonly float[] _scratch = new float[16384];

    float[] _primed = [];
    int _primedStart;
    int _primedCount;
    bool _eof;

    public AacSampleSource(Stream stream)
    {
        _reader = new AdtsFrameReader(stream);
        if (!_reader.TryReadFrame(out var first, out var header))
            throw new InvalidOperationException("AAC: the stream carried no ADTS frame");

        Span<byte> asc = stackalloc byte[2];
        AdtsFrameParser.WriteAudioSpecificConfig(header, asc);
        _decoder = new MfAacDecoder(header.SampleRate, header.ChannelConfiguration, asc);
        try
        {
            Prime(first);
        }
        catch
        {
            _decoder.Dispose();
            throw;
        }

        SampleRate = _decoder.OutputSampleRate > 0 ? _decoder.OutputSampleRate : header.SampleRate;
        Channels = _decoder.OutputChannels > 0 ? _decoder.OutputChannels : Math.Max(1, header.ChannelConfiguration);
    }

    public int SampleRate { get; }
    public int Channels { get; }

    public int ReadSamples(float[] buffer, int offset, int count)
    {
        int produced = 0;
        while (produced < count)
        {
            if (_primedCount > 0)
            {
                int n = Math.Min(count - produced, _primedCount);
                Array.Copy(_primed, _primedStart, buffer, offset + produced, n);
                _primedStart += n;
                _primedCount -= n;
                produced += n;
                if (_primedCount == 0) _primedStart = 0;
                continue;
            }
            if (_eof) break;

            int got = _decoder.ReadSamples(buffer.AsSpan(offset + produced, count - produced));
            if (_decoder.FormatChanged)
                throw new InvalidOperationException("AAC: the stream changed encoding mid-play");
            if (got > 0) { produced += got; continue; }

            if (!_reader.TryReadFrame(out var frame, out _)) { _eof = true; break; }
            if (!_decoder.TryPushFrame(frame))
            {
                // The transform is holding output it will not accept input over — drain, then take the frame.
                DrainIntoPrimed();
                _decoder.TryPushFrame(frame);
            }
        }
        return produced;
    }

    /// <summary>A live stream has no timeline to seek on; the transport refuses repositioning and this mirrors it.</summary>
    public void SeekTo(TimeSpan position) { }

    public void Dispose() => _decoder.Dispose();

    void Prime(ReadOnlySpan<byte> firstFrame)
    {
        PushAndDrain(firstFrame);
        for (int i = 1; i < MaxPrimeFrames && _primedCount == 0 && !_eof; i++)
        {
            if (!_reader.TryReadFrame(out var frame, out _)) { _eof = true; break; }
            PushAndDrain(frame);
        }
        if (_primedCount == 0 && !_eof)
            throw new InvalidOperationException("AAC: the decoder produced no samples while priming");
    }

    void PushAndDrain(ReadOnlySpan<byte> frame)
    {
        _decoder.TryPushFrame(frame);
        DrainIntoPrimed();
        if (!_decoder.FormatChanged) return;
        // Everything decoded before the change was at the pre-SBR format — it must not reach the mixer.
        _decoder.ClearFormatChanged();
        _primedStart = 0;
        _primedCount = 0;
    }

    void DrainIntoPrimed()
    {
        while (true)
        {
            int n = _decoder.ReadSamples(_scratch);
            if (n <= 0) return;
            Append(_scratch.AsSpan(0, n));
        }
    }

    void Append(ReadOnlySpan<float> samples)
    {
        if (_primedCount == 0) _primedStart = 0;
        int needed = _primedStart + _primedCount + samples.Length;
        if (_primed.Length < needed)
        {
            if (_primedStart > 0 && _primedCount > 0)
            {
                Array.Copy(_primed, _primedStart, _primed, 0, _primedCount);
                _primedStart = 0;
                needed = _primedCount + samples.Length;
            }
            if (_primed.Length < needed) Array.Resize(ref _primed, Math.Max(needed, 16384));
        }
        samples.CopyTo(_primed.AsSpan(_primedStart + _primedCount));
        _primedCount += samples.Length;
    }
}
