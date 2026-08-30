using System;
using System.IO;
using FlacBox;
using NLayer;
using NVorbis;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Uniform pull decoder: interleaved float32 + seek. Vorbis (WaveeMusic's vendored NVorbis) and FLAC (FlacBox)
/// both satisfy it, so the engine's decode loop is codec-agnostic.</summary>
internal interface ISampleSource : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    /// <summary>Fill up to <paramref name="count"/> interleaved float samples; returns the number read (0 = end of stream).</summary>
    int ReadSamples(float[] buffer, int offset, int count);
    void SeekTo(TimeSpan position);
}

/// <summary>Ogg Vorbis via the vendored (WaveeMusic-optimized) NVorbis fork.</summary>
internal sealed class VorbisSampleSource : ISampleSource
{
    readonly VorbisReader _reader;
    public VorbisSampleSource(Stream stream) => _reader = new VorbisReader(stream, false);   // closeOnDispose:false — engine owns the stream
    public int SampleRate => _reader.SampleRate;
    public int Channels => _reader.Channels;
    public int ReadSamples(float[] buffer, int offset, int count) => _reader.ReadSamples(buffer, offset, count);
    public void SeekTo(TimeSpan position) => _reader.SeekTo(position, SeekOrigin.Begin);
    public void Dispose() => _reader.Dispose();
}

/// <summary>MP3 via NLayer (external RSS episodes).</summary>
internal sealed class Mp3SampleSource : ISampleSource
{
    readonly MpegFile _decoder;
    public Mp3SampleSource(Stream stream) => _decoder = new MpegFile(stream);
    public int SampleRate => _decoder.SampleRate;
    public int Channels => _decoder.Channels;
    public int ReadSamples(float[] buffer, int offset, int count) => _decoder.ReadSamples(buffer, offset, count);
    public void SeekTo(TimeSpan position)
    {
        long samples = (long)(position.TotalSeconds * SampleRate * Channels);
        if (samples < 0) samples = 0;
        _decoder.Position = samples;
    }
    public void Dispose() => _decoder.Dispose();
}

/// <summary>FLAC (16/24-bit) via FlacBox. Record-pull reader: Read() → RecordType.Frame → GetValues() yields the frame's
/// interleaved integer samples, scaled to float. FlacBox has no random seek, so SeekTo is best-effort (no-op) for now.</summary>
internal sealed class FlacSampleSource : ISampleSource
{
    readonly FlacReader _reader;
    readonly float _scale;
    float[] _frame = new float[16384];
    int _pos, _len;

    public FlacSampleSource(Stream stream)
    {
        _reader = new FlacReader(stream, false);
        while (_reader.Streaminfo is null)
            if (!_reader.Read()) throw new InvalidOperationException("FLAC: stream carried no STREAMINFO");
        var si = _reader.Streaminfo;
        SampleRate = si.SampleRate;
        Channels = si.ChannelsCount;
        TotalFrames = si.TotalSampleCount > 0 ? si.TotalSampleCount : -1;
        _scale = 1f / (1 << (si.BitsPerSample > 1 ? si.BitsPerSample - 1 : 15));   // 16-bit→1/32768, 24-bit→1/8388608
    }

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>STREAMINFO's total inter-channel sample (frame) count, or −1 when the header carried none. Read from the
    /// mandatory first metadata block — no seek, so it is safe on the head-only fast-start path. Feeds the decode edge's
    /// <c>GaplessInfo.ExactFrames</c> so a butt-join stops exactly at the encoded length (W2 gapless fix §3).</summary>
    public long TotalFrames { get; }

    public int ReadSamples(float[] buffer, int offset, int count)
    {
        int produced = 0;
        while (produced < count)
        {
            if (_pos >= _len && !DecodeFrame()) break;   // EOF
            int n = Math.Min(count - produced, _len - _pos);
            Array.Copy(_frame, _pos, buffer, offset + produced, n);
            _pos += n; produced += n;
        }
        return produced;
    }

    bool DecodeFrame()
    {
        while (_reader.Read())
        {
            if (_reader.RecordType != FlacRecordType.Frame) continue;
            int i = 0;
            foreach (int v in _reader.GetValues())   // interleaved samples of this frame
            {
                if (i >= _frame.Length) Array.Resize(ref _frame, _frame.Length * 2);
                _frame[i++] = v * _scale;
            }
            _pos = 0; _len = i;
            if (i > 0) return true;
        }
        return false;
    }

    public void SeekTo(TimeSpan position) { /* FlacBox has no random seek — TODO: proper FLAC seek table support */ }

    public void Dispose() { try { _reader.Close(); } catch { } }
}
