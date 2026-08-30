using System.Threading;

namespace Wavee.Backend.Audio;

/// <summary>The sink half of the live transport: whatever consumes demuxed audio bytes. Implemented by
/// <see cref="LiveRingBuffer"/>; the tests substitute a recording sink.</summary>
internal interface IByteSink
{
    void Write(ReadOnlySpan<byte> data);
}

/// <summary>A bounded single-producer / single-consumer byte ring for an ENDLESS stream (internet radio).
///
/// <para>The two halves have deliberately opposite policies. <see cref="Write"/> NEVER blocks — a live producer that
/// waited on a stalled decoder would build unbounded latency behind it, so an overrun drops the OLDEST bytes and
/// counts them in <see cref="TotalDropped"/> (the decoder resyncs over the splice; the alternative — killing the
/// stream — is strictly worse for radio). <see cref="Read"/> DOES block: the engine's decode edge is a pull loop that
/// treats a 0-length read as end-of-stream, so returning "nothing yet" would end the track on every underrun.</para>
///
/// <para>Termination is explicit and three-way: <see cref="Complete"/> with no error drains then returns 0 (a real EOF),
/// <see cref="Complete"/> with an error drains then THROWS it (a typed fault the host maps to a drop), and
/// <see cref="Dispose"/> throws <see cref="ObjectDisposedException"/> immediately — which the decoder treats as EOF, and
/// which is the wake a blocked reader needs when the session is torn down.</para></summary>
internal sealed class LiveRingBuffer : IByteSink, IDisposable
{
    /// <summary>How long a blocked reader parks before re-checking state. Bounded (rather than an indefinite wait) so a
    /// missed pulse can never hang the decode thread.</summary>
    const int WaitSliceMs = 100;

    readonly byte[] _buf;
    readonly object _gate = new();
    int _head;          // read cursor into _buf
    int _count;         // bytes currently held
    long _totalWritten;
    long _totalDropped;
    bool _completed;
    bool _disposed;
    Exception? _error;

    public LiveRingBuffer(int capacityBytes = 3 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 1024);
        _buf = new byte[capacityBytes];
    }

    public int Capacity => _buf.Length;

    /// <summary>Bytes currently buffered (a snapshot — the producer may have moved on by the time you read it).</summary>
    public int Available { get { lock (_gate) return _count; } }

    /// <summary>Bytes discarded because the producer outran the consumer. Non-zero means an audible splice.</summary>
    public long TotalDropped { get { lock (_gate) return _totalDropped; } }

    /// <summary>Bytes accepted since construction (dropped bytes included — this is what the producer pushed).</summary>
    public long TotalWritten { get { lock (_gate) return _totalWritten; } }

    public bool IsCompleted { get { lock (_gate) return _completed; } }

    /// <summary>Push producer bytes. Never blocks; drops the oldest buffered bytes on overrun.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_gate)
        {
            if (_disposed || _completed) return;
            _totalWritten += data.Length;

            // A single push larger than the ring can only leave its TAIL: everything before it is already superseded.
            if (data.Length >= _buf.Length)
            {
                _totalDropped += _count + (data.Length - _buf.Length);
                data[^_buf.Length..].CopyTo(_buf);
                _head = 0;
                _count = _buf.Length;
                Monitor.PulseAll(_gate);
                return;
            }

            int overflow = _count + data.Length - _buf.Length;
            if (overflow > 0)
            {
                _head = (_head + overflow) % _buf.Length;
                _count -= overflow;
                _totalDropped += overflow;
            }

            int tail = (_head + _count) % _buf.Length;
            int first = Math.Min(data.Length, _buf.Length - tail);
            data[..first].CopyTo(_buf.AsSpan(tail));
            if (first < data.Length) data[first..].CopyTo(_buf.AsSpan(0));
            _count += data.Length;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Blocking read. Waits until at least <paramref name="minAvailable"/> bytes are buffered (clamped to the
    /// capacity), then copies as much as fits into <paramref name="dst"/>.
    /// Returns 0 ONLY when the buffer was completed without an error and is fully drained.
    /// Throws the completion error after the drain, or <see cref="ObjectDisposedException"/> once disposed.</summary>
    public int Read(Span<byte> dst, int minAvailable = 1)
    {
        if (dst.IsEmpty) return 0;
        int want = Math.Clamp(minAvailable, 1, Math.Min(_buf.Length, dst.Length));
        lock (_gate)
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_count >= want || (_completed && _count > 0)) break;
                if (_completed)
                {
                    if (_error is not null) throw _error;
                    return 0;
                }
                Monitor.Wait(_gate, WaitSliceMs);
            }

            int n = Math.Min(dst.Length, _count);
            int first = Math.Min(n, _buf.Length - _head);
            _buf.AsSpan(_head, first).CopyTo(dst);
            if (first < n) _buf.AsSpan(0, n - first).CopyTo(dst[first..]);
            _head = (_head + n) % _buf.Length;
            _count -= n;
            return n;
        }
    }

    /// <summary>Copy up to <paramref name="dst"/>.Length buffered bytes WITHOUT consuming them. Never blocks.</summary>
    public int Peek(Span<byte> dst)
    {
        if (dst.IsEmpty) return 0;
        lock (_gate)
        {
            if (_disposed) return 0;
            int n = Math.Min(dst.Length, _count);
            int first = Math.Min(n, _buf.Length - _head);
            _buf.AsSpan(_head, first).CopyTo(dst);
            if (first < n) _buf.AsSpan(0, n - first).CopyTo(dst[first..]);
            return n;
        }
    }

    /// <summary>Park until at least <paramref name="minAvailable"/> bytes are buffered (or the buffer completes /
    /// is disposed / the timeout elapses). Returns true when the bytes are there. Used by the head sniff, which must
    /// see real bytes before it can pick a decoder but must never hang the load pump.</summary>
    public bool WaitForAvailable(int minAvailable, int timeoutMs)
    {
        int want = Math.Clamp(minAvailable, 1, _buf.Length);
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        lock (_gate)
        {
            while (true)
            {
                if (_disposed) return false;
                if (_count >= want) return true;
                if (_completed) return _count >= want;
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return _count >= want;
                Monitor.Wait(_gate, (int)Math.Min(WaitSliceMs, remaining));
            }
        }
    }

    /// <summary>Close the producer end. A null <paramref name="error"/> is a clean EOF; a non-null one is re-thrown to
    /// the reader AFTER the buffered bytes drain, so the last audible second is never sacrificed to report the fault.</summary>
    public void Complete(Exception? error = null)
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _error = error;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Idempotent. Wakes a blocked reader with <see cref="ObjectDisposedException"/> — the decode edge maps
    /// that to EOF, which is what lets a session teardown finish inside the feed thread's 500 ms join.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _count = 0;
            Monitor.PulseAll(_gate);
        }
    }
}
