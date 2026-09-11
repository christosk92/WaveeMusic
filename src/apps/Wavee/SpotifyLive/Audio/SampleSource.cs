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
    long SeekTo(TimeSpan position);
}

/// <summary>Ogg Vorbis via the vendored (WaveeMusic-optimized) NVorbis fork.</summary>
internal sealed class VorbisSampleSource : ISampleSource
{
    readonly VorbisReader _reader;
    public VorbisSampleSource(Stream stream) => _reader = new VorbisReader(stream, false);   // closeOnDispose:false — engine owns the stream
    public int SampleRate => _reader.SampleRate;
    public int Channels => _reader.Channels;
    public int ReadSamples(float[] buffer, int offset, int count) => _reader.ReadSamples(buffer, offset, count);
    public long SeekTo(TimeSpan position) { _reader.SeekTo(position, SeekOrigin.Begin); return _reader.SamplePosition; }
    public void Dispose() => _reader.Dispose();
}

/// <summary>MP3 via NLayer (external RSS episodes).</summary>
internal sealed class Mp3SampleSource : ISampleSource
{
    MpegFile _decoder;
    readonly Stream _stream;
    readonly float[] _seekScratch = new float[4096];
    public Mp3SampleSource(Stream stream)
    {
        _stream = stream;
        // NLayer must not build a whole-file sample index merely to return the first untagged PCM block.
        _decoder = new MpegFile(new ForwardDecodeView(stream));
    }
    public int SampleRate => _decoder.SampleRate;
    public int Channels => _decoder.Channels;
    public int ReadSamples(float[] buffer, int offset, int count) => _decoder.ReadSamples(buffer, offset, count);
    public long SeekTo(TimeSpan position)
    {
        long target = Math.Max(0, (long)(position.TotalSeconds * SampleRate));
        int bytesPerFrame = Channels * sizeof(float);
        // NLayer.Position is decoded float PCM bytes, despite its XML summary calling these samples.
        if (!_stream.CanSeek) throw new NotSupportedException("This MP3 source has no seekable timeline.");
        if (!_decoder.CanSeek)
        {
            _decoder.Dispose();
            _stream.Position = 0;
            _decoder = new MpegFile(_stream); // indexing is admitted only for an explicit seek
        }
        long achieved;
        try
        {
            _decoder.Position = checked(target * bytesPerFrame);
            achieved = _decoder.Position / bytesPerFrame;
        }
        catch (ArgumentOutOfRangeException)
        {
            // A missing/rejected index or a target beyond the stream cannot acknowledge the requested frame.
            // Restart at byte zero and let a bounded discard pass report the actual position, including EOF.
            _decoder.Dispose();
            _stream.Position = 0;
            _decoder = new MpegFile(new ForwardDecodeView(_stream));
            achieved = 0;
        }
        while (achieved < target)
        {
            int samples = (int)Math.Min((target - achieved) * Channels, _seekScratch.Length / Channels * Channels);
            if (_decoder.ReadSamples(_seekScratch, 0, samples) <= 0) break;
            achieved = _decoder.Position / bytesPerFrame;
        }
        return achieved;
    }
    public void Dispose()
    {
        try { _decoder.Dispose(); }
        finally { _stream.Dispose(); }
    }

    // This view borrows the independently owned source cursor; disposing it never closes the outer source.
    sealed class ForwardDecodeView(Stream inner) : Stream
    {
        long _position;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            _position += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            _position += read;
            return read;
        }
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override long Length => throw new NotSupportedException();
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}

/// <summary>FLAC (16/24-bit) via FlacBox. Record-pull reader: Read() → RecordType.Frame → GetValues() yields the frame's
/// interleaved integer samples, scaled to float. FLAC seeks rebuild from byte zero when needed and decode/discard to an honest achieved position.</summary>
internal sealed class FlacSampleSource : ISampleSource
{
    FlacReader _reader;
    readonly Stream _stream;
    long _samplePosition;
    readonly float _scale;
    float[] _frame = new float[16384];
    int _pos, _len;

    public FlacSampleSource(Stream stream)
    {
        _stream = stream;
        _reader = new FlacReader(stream, leaveOpen: true);
        try
        {
            while (_reader.Streaminfo is null)
                if (!_reader.Read()) throw new InvalidOperationException("FLAC: stream carried no STREAMINFO");
        }
        catch
        {
            try { _reader.Close(); } finally { stream.Dispose(); }
            throw;
        }
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
            _pos += n; produced += n; _samplePosition += n;
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

    public long SeekTo(TimeSpan position)
    {
        if (!_stream.CanSeek) throw new NotSupportedException("This FLAC stream is not seekable.");
        long targetFrames = Math.Max(0, (long)(position.TotalSeconds * SampleRate));
        if (TotalFrames > 0) targetFrames = Math.Min(targetFrames, TotalFrames);
        long targetSamples = checked(targetFrames * Channels);
        if (targetSamples < _samplePosition)
        {
            _reader.Close();
            _stream.Position = 0;
            _reader = new FlacReader(_stream, leaveOpen: true);
            _samplePosition = 0; _pos = _len = 0;
        }
        while (_samplePosition < targetSamples)
        {
            if (_pos >= _len && !DecodeFrame()) break;
            int discard = (int)Math.Min(targetSamples - _samplePosition, _len - _pos);
            _pos += discard;
            _samplePosition += discard;
        }
        return _samplePosition / Channels;
    }


    public void Dispose()
    {
        try { _reader.Close(); }
        finally { _stream.Dispose(); }
    }
}
