using System;
using System.IO;
using System.Threading;
using Wavee.Backend;

namespace Wavee.Backend.Audio;

/// <summary>
/// Forward-offset decode-stream view, in the same shape as <see cref="SkipStream"/> (presents byte <c>skip</c> of the
/// inner stream as logical position 0), but pulls bytes through <see cref="IAudioReadStream.TryRead"/> instead of a
/// blocking <c>Stream.Read</c> — so the engine's dedicated decode thread never synchronously
/// drives a CDN range fetch. A read-ahead ring miss used to stall the decode thread for however long a ~64 KiB fetch
/// took (<c>RangedHttpSource.EnsureRange</c>, the blocking chain); <c>TryRead</c> instead kicks an ASYNC prefetch
/// (<c>RangedHttpSource.RequestAsyncPrefetch</c>) and reports "no data yet" via <c>wouldBlock</c>.
///
/// <para>A <c>wouldBlock</c> miss can NEVER be handed up the stack as a short/zero read: every codec above this seam
/// treats a zero-byte read as PERMANENT end-of-stream — <c>AudioDecode.cs</c>'s decoder and
/// <c>SpotifyEngineAudioDecoder.Read</c> latch <c>_eof</c> on the very first zero, <c>AdtsFrameParser</c> latches
/// <c>_eof</c> the same way, and NVorbis's page reader gives up after ten. So this wrapper turns <c>TryRead</c> into
/// a BOUNDED-WAIT primitive instead: on a miss it sleeps briefly and retries, up to <see cref="MaxWaitMs"/>, and
/// returns 0 ONLY once <c>TryRead</c> itself reports a genuine EOF (<c>wouldBlock == false</c>) — never on a
/// transient miss, and never on the wait ceiling, which degrades to the blocking read instead. The underlying
/// stream's own state-gate wait (<c>SpotifyAudioStream</c>'s <c>Monitor</c>-based wake, pulsed on every
/// fetch/attach/seek) is private to that class and not reachable from here, hence the straightforward sleep-retry
/// loop rather than a wait-handle hookup.</para>
///
/// <para>So the worst case is exactly the behaviour this wrapper replaced, and the common case skips it: a miss kicks
/// the prefetch immediately rather than making the decode thread drive the fetch itself.</para>
/// </summary>
internal sealed class PrefetchingReadStream : Stream
{
    // Poll granularity while a range is filling. Short enough that the ~500ms PCM decode-ahead ring never runs dry
    // waiting on us; long enough that a sustained miss never turns this into a hot spin on the decode thread.
    const int PollDelayMs = 4;

    // How long the non-blocking fast path is given before falling back to the blocking read. This is NOT a deadline
    // after which the track ends — see Read: exhausting it degrades to the old blocking path (which owns the real
    // retry/backoff budget and mirror failover), never to a zero the codecs would latch as EOF. It only bounds how
    // long we poll while an async prefetch we just kicked is in flight.
    const int MaxWaitMs = 8_000;

    readonly IAudioReadStream _reader;
    readonly Stream _inner;
    readonly long _skip;

    public PrefetchingReadStream(IAudioReadStream reader, long skip)
    {
        _reader = reader;
        _inner = reader.AsStream();
        _skip = skip;
        _inner.Seek(skip, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        long deadline = Environment.TickCount64 + MaxWaitMs;
        while (true)
        {
            int n = _reader.TryRead(buffer, out bool wouldBlock);
            if (n > 0 || !wouldBlock) return n;   // real bytes, or TryRead's own genuine EOF (n == 0 && !wouldBlock)
            if (Environment.TickCount64 >= deadline)
                // Ceiling exhausted. Returning 0 here would be WORSE than the bug this class exists to fix: every codec
                // above latches the first zero as permanent EOF, so a slow-but-alive connection would silently TRUNCATE
                // the track. Degrade instead to the blocking read this wrapper replaced — it routes into
                // SpotifyAudioStream.Read -> EnsureRangeAvailable, which owns the real retry/backoff budget (~90 s) and
                // the mirror failover. The fast path above is a pure improvement; this is its floor, never a new cliff.
                return _inner.Read(buffer);
            Thread.Sleep(PollDelayMs);
        }
    }

    public override long Length => Math.Max(0, _inner.Length - _skip);
    public override long Position
    {
        get => _inner.Position - _skip;
        set => _inner.Position = value + _skip;
    }
    public override long Seek(long offset, SeekOrigin origin) => origin switch
    {
        SeekOrigin.Begin => _inner.Seek(offset + _skip, SeekOrigin.Begin) - _skip,
        SeekOrigin.Current => _inner.Seek(offset, SeekOrigin.Current) - _skip,
        SeekOrigin.End => _inner.Seek(offset, SeekOrigin.End) - _skip,
        _ => _inner.Position - _skip,
    };
    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Pure mapping from the negotiated <see cref="AudioFormat"/> rung to an approximate bits/sec figure for
/// <see cref="IAudioReadStream.ConfigureReadAhead"/>. The seam has no bitrate of its own to read, and
/// <c>knownSize</c> is null at every production attach site, so size÷duration is not a usable substitute. The three
/// Ogg Vorbis rungs are Spotify's own fixed nominal rates; the FLAC/FLAC24 figures are rough lossless ceilings —
/// real content varies, but <c>ReadAheadPolicy.Compute</c> only needs "roughly how many bytes per second" to size
/// its window, never an exact figure. MP3 and AAC report 0 ("unknown") because their bitrate is per-file (CBR/VBR)
/// and not knowable from the format alone; <c>ReadAheadPolicy.Compute</c> already treats &lt;= 0 as "fall back to
/// measured throughput" (<c>RangedHttpSource.cs</c>).
/// </summary>
internal static class AudioBitratePolicy
{
    public static int BitsPerSecond(AudioFormat format) => format switch
    {
        AudioFormat.OggVorbis96 => 96_000,
        AudioFormat.OggVorbis160 => 160_000,
        AudioFormat.OggVorbis320 => 320_000,
        AudioFormat.Flac => 1_000_000,
        AudioFormat.Flac24 => 1_800_000,
        _ => 0,   // Mp3, Aac: per-file CBR/VBR — unknown from the format alone
    };
}
