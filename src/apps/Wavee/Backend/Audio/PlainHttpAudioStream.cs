using System.Net.Http;
using Wavee.Sdk.Streams;

namespace Wavee.Backend.Audio;

/// <summary>Seekable plain-HTTP audio stream (external RSS/podcast episodes) — no Spotify decrypt, no clear head. Built on
/// the shared <see cref="RangedHttpSource"/> so it gets the same read-ahead, retry/backoff, and (for servers that ignore
/// Range) a one-shot full-body fallback that the bespoke single-buffer version lacked.</summary>
public sealed class PlainHttpAudioStream : Stream, IAsyncDisposable, IAudioReadStream, IAudioNetworkRecoverySource
{
    readonly RangedHttpSource _source;
    readonly long _headHintSize;   // from the opening HEAD; the source refines it from the first response
    long _pos;
    bool _disposed;
    event Action<AudioNetworkRecoveryEvent>? NetworkRecovery;

    event Action<AudioNetworkRecoveryEvent>? IAudioNetworkRecoverySource.NetworkRecovery
    {
        add => NetworkRecovery += value;
        remove => NetworkRecovery -= value;
    }

    /// <summary>The <c>Content-Type</c> media type from the opening HEAD (e.g. "audio/mpeg"), or null. Used to pick the
    /// decoder without assuming MP3.</summary>
    public string? ContentType { get; }

    // IAudioReadStream — a plain stream has no clear head and no separate "body attach" step.
    public Stream AsStream() => this;
    public long CurrentOffset => Volatile.Read(ref _pos);
    public bool IsBodyAttached => true;
    public long KnownSize => _source.KnownSize;
    public int ClearHeadLength => 0;
    public IDisposable PauseReadAhead() => _source.PauseReadAhead();
    public void ResumeReadAheadAtCurrentOffset() => _source.ResumeReadAheadAt(Volatile.Read(ref _pos));
    // Unlike the interface's no-op default, this stream DOES have a read-ahead window worth tuning — forward to the
    // shared RangedHttpSource so an external podcast episode gets the same bitrate/metered-aware sizing as Spotify.
    public void ConfigureReadAhead(int bitrateBitsPerSec, bool metered) => _source.ConfigureReadAhead(bitrateBitsPerSec, metered);

    PlainHttpAudioStream(HttpClient http, string url, long knownSize, string? contentType, WaveeLogger log)
    {
        _headHintSize = knownSize;
        ContentType = contentType;
        // requireRange:false → tolerate a 200 (server ignored Range) by buffering the whole body once.
        _source = new RangedHttpSource(http, url, log, headFloor: 0, onRangeAvailable: null, requireRange: false,
            onRecovery: e => NetworkRecovery?.Invoke(e));
        _source.Configure([url], knownSize > 0 ? knownSize : null);
        _source.StartReadAhead();
    }

    /// <summary>Open the stream, priming the length from a HEAD when the server offers one (optional — the size is also
    /// discovered from the first ranged/200 response).</summary>
    public static async Task<PlainHttpAudioStream> OpenAsync(HttpClient http, string url, WaveeLogger log = default, CancellationToken ct = default)
    {
        long size = -1;
        string? contentType = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                size = resp.Content.Headers.ContentLength ?? -1;
                contentType = resp.Content.Headers.ContentType?.MediaType;
            }
            log.Info($"external audio HEAD {url}: len={size} type={contentType ?? "?"}");
        }
        catch { /* HEAD optional */ }
        return new PlainHttpAudioStream(http, url, size, contentType, log);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count <= 0) return 0;

        var pos = Volatile.Read(ref _pos);
        var size = _source.KnownSize;
        if (size > 0 && pos >= size) return 0;

        var wanted = size > 0 ? (int)Math.Min(count, size - pos) : count;
        if (wanted <= 0) return 0;
        _source.EnsureRange(pos, wanted);

        var available = _source.ContainedLengthFrom(pos);
        size = _source.KnownSize;
        if (size > 0) available = Math.Min(available, size - pos);
        if (available <= 0) return 0;

        var m = (int)Math.Min(wanted, available);
        _source.ReadRaw(pos, buffer, offset, m);
        Volatile.Write(ref _pos, pos + m);
        _source.MarkProgress(pos + m);
        return m;
    }

    public override long Length
    {
        get
        {
            var size = _source.KnownSize;
            if (size > 0) return size;
            if (_headHintSize > 0) return _headHintSize;
            throw new NotSupportedException();
        }
    }

    public override long Position
    {
        get => Volatile.Read(ref _pos);
        set => Volatile.Write(ref _pos, Math.Max(0, value));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Volatile.Read(ref _pos) + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Volatile.Write(ref _pos, Math.Max(0, next));
        _source.ResumeReadAheadAt(Volatile.Read(ref _pos));
        return Volatile.Read(ref _pos);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _source.Dispose();
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
