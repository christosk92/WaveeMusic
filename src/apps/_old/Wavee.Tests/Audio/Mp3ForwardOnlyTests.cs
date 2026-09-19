using System;
using System.IO;
using NLayer;
using Wavee.Backend.Audio;
using Xunit;

namespace Wavee.Tests.Audio;

/// <summary>Internet radio's other codec is MP3, and the live transport is FORWARD-ONLY — it has no length and refuses
/// every non-zero seek. This pins the two facts the live MP3 path depends on: NLayer's <see cref="MpegFile"/> opens and
/// decodes over a non-seekable stream, and repositioning it throws (which the decode edge catches and treats as "this
/// source cannot seek") rather than silently misbehaving.</summary>
public class Mp3ForwardOnlyTests
{
    const int Mpeg1Layer3FrameBytes = 417;   // 144 × 128000 / 44100, no padding

    /// <summary>One syntactically valid MPEG-1 Layer III frame at 128 kbit/s, 44.1 kHz, stereo, with a zeroed payload
    /// (side info + main data all zero = a silent frame).</summary>
    static byte[] SilentFrame()
    {
        var f = new byte[Mpeg1Layer3FrameBytes];
        f[0] = 0xFF;
        f[1] = 0xFB;   // sync | MPEG-1 | Layer III | no CRC
        f[2] = 0x90;   // bitrate index 9 (128k) | sampling 0 (44100) | no padding
        f[3] = 0x00;   // stereo, no emphasis
        return f;
    }

    static byte[] Frames(int count)
    {
        var ms = new MemoryStream();
        var frame = SilentFrame();
        for (int i = 0; i < count; i++) ms.Write(frame, 0, frame.Length);
        return ms.ToArray();
    }

    /// <summary>A stream with the live transport's shape: readable, not seekable, no length, and every reposition
    /// refused. Deliberately NOT a MemoryStream — a seekable stand-in would prove nothing.</summary>
    sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count <= 0) return 0;
            int n = Math.Min(count, data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        // Mirrors LiveHttpAudioStream: the two zero-offset forms SkipStream's constructor and the soft reload issue
        // are no-ops; every real reposition is refused.
        public override long Seek(long offset, SeekOrigin origin) => offset == 0 && origin != SeekOrigin.End
            ? _pos
            : throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void MpegFile_OpensOverANonSeekableStream()
    {
        using var mpeg = new MpegFile(new ForwardOnlyStream(Frames(24)));

        Assert.Equal(44100, mpeg.SampleRate);
        Assert.Equal(2, mpeg.Channels);
    }

    [Fact]
    public void MpegFile_DecodesForward()
    {
        using var mpeg = new MpegFile(new ForwardOnlyStream(Frames(24)));
        var buffer = new float[4096];

        int total = 0;
        for (int i = 0; i < 16; i++)
        {
            int n = mpeg.ReadSamples(buffer, 0, buffer.Length);
            if (n <= 0) break;
            total += n;
        }

        Assert.True(total > 0, "a forward-only MP3 stream must decode at least one buffer");
    }

    [Fact]
    public void SettingPosition_Throws_SoTheDecodeEdgeCanSwallowIt()
    {
        using var mpeg = new MpegFile(new ForwardOnlyStream(Frames(8)));
        // SpotifyEngineAudioDecoder.Seek wraps exactly this in a catch: a live source has nothing to seek to.
        Assert.ThrowsAny<Exception>(() => mpeg.Position = 44100);
    }

    [Fact]
    public void SkipStreamOverAForwardOnlySource_ReportsNoSeek()
    {
        // A zero skip is what the live path uses; the wrapper must pass CanSeek=false through, which is what keeps
        // Mp3GaplessProbe (seekable-only) off a live stream.
        var skip = new SkipStream(new ForwardOnlyStream(Frames(2)), 0);

        Assert.False(skip.CanSeek);
        Assert.True(skip.Read(new byte[64], 0, 64) > 0);
    }
}
