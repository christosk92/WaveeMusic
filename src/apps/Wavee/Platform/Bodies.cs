// ── Platform/Bodies.cs ─────────────────────────────────────────────────────────────────────────────────────────────
// a body read WHOLE without the large-object heap paying for every byte of it twice: the read into a rented buffer,
// the scope that LENDS that buffer to a decoder, and the one exact copy for a caller that keeps the bytes
//
// Role: SHELL (the reads, the scope) + CORE (`BodySizing`, pure, tested)
// Budget: 220 lines
//
// WHY THIS EXISTS. 2026-10-04: the live process carried a 100 MB large-object heap with 15–39 MB of it free space
// between survivors. Every Spotify api answer was read as `stream.CopyTo(new MemoryStream(4096))` + `ToArray()`. The
// MemoryStream doubles 4K → 8K → …, so an answer past 64 KB left a dead 128K, 256K, 512K … chain on the LOH and then
// paid the `ToArray` copy on top: the 806 KB home feed cost ~2.7 MB of LOH, a 456 KB `fetch.xm` artist batch (the
// logged mean of the big ones) ~1.35 MB, a 2.2 MB one ~10 MB. A dead LOH array is only reclaimed by a gen2 GC, and the
// survivors allocated between them hold the holes open: that is the fragmentation.
//
// HOW. `ReadPooled` reads to EOF into a buffer rented from `ArrayPool<byte>.Shared` (growing rents the next size and
// returns the outgrown one), so the growth chain is recycled instead of dropped. `ReadOwned` is that plus ONE exact copy:
// a caller that keeps the bytes pays for exactly the bytes. A caller that only DECODES opens a scope on its thread
// (`Lend`): inside it a read is LENT (`ReadLent`), the decoder reads the span, and the scope's end hands every lent
// buffer back to the pool — the answer never becomes a heap array at all.
//
// OWNERSHIP (the rule a pool lives or dies by). A lent buffer is reachable ONLY through its `PooledBody`: `Span` cannot
// be stored on the heap, `ToOwned()` is a COPY (made once, then reused), and once the scope has returned the buffer a
// `Span` read throws instead of reading an array the pool may already have handed to someone else. A scope belongs to
// ONE thread and ends on it (`using`); every read inside it is synchronous, so nothing lent can cross an await.

using System.Buffers;

namespace Wavee;

/// <summary>The sizes a pooled read rents. PURE.</summary>
public static class BodySizing
{
    /// <summary>The first rental when the length is unknown (a chunked or decompressed answer): most api answers fit,
    /// and the pool recycles it either way.</summary>
    public const int UnknownLengthStart = 16 * 1024;

    /// <summary>The first rental never trusts a header past this: a body really that large still reads, it just grows
    /// into it, and a hostile <c>Content-Length</c> cannot make one read rent a gigabyte up front.</summary>
    public const int MaxHintedStart = 32 * 1024 * 1024;

    /// <summary>The first rental: the announced length plus ONE byte — room for the read that sees EOF, so a body that
    /// is exactly as long as it said never grows — or <see cref="UnknownLengthStart"/> without one.</summary>
    public static int Start(long? lengthHint)
        => lengthHint is > 0 and < MaxHintedStart ? (int)lengthHint.Value + 1
            : lengthHint >= MaxHintedStart ? MaxHintedStart : UnknownLengthStart;

    /// <summary>The next rental once <paramref name="current"/> is full: double, capped at the largest array the
    /// runtime allows; a body that has already filled that is an <see cref="IOException"/>, as it is for
    /// <c>MemoryStream</c>.</summary>
    public static int Next(int current)
    {
        if (current >= Array.MaxLength) throw new IOException("The body is larger than the largest array.");
        return (int)Math.Min((long)Math.Max(current, 256) * 2, Array.MaxLength);
    }
}

/// <summary>A body in a buffer rented from <see cref="ArrayPool{T}.Shared"/>. See the file header for the ownership rule.</summary>
public sealed class PooledBody : IDisposable
{
    byte[]? _rented;
    byte[]? _owned;

    internal PooledBody(byte[] rented, int length)
    {
        _rented = rented;
        Length = length;
    }

    /// <summary>The body's length in bytes; still answers after the buffer went back.</summary>
    public int Length { get; }

    /// <summary>The body. Throws once the buffer is back in the pool, unless <see cref="ToOwned"/> already copied it.</summary>
    public ReadOnlySpan<byte> Span
        => _rented is { } rented ? rented.AsSpan(0, Length)
            : _owned ?? throw new ObjectDisposedException(nameof(PooledBody), "a lent body was read after its scope returned it");

    /// <summary>An exact-size copy the caller may keep forever. Made on the first call; every later call returns the
    /// same array.</summary>
    public byte[] ToOwned()
    {
        if (_owned is not null) return _owned;
        byte[] rented = _rented ?? throw new ObjectDisposedException(nameof(PooledBody), "a lent body was copied after its scope returned it");
        _owned = Length == 0 ? [] : rented.AsSpan(0, Length).ToArray();
        return _owned;
    }

    internal Span<byte> Writable => _rented.AsSpan(0, Length);

    /// <summary>Hand the buffer back to the pool. Idempotent.</summary>
    public void Dispose()
    {
        byte[]? rented = _rented;
        _rented = null;
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }
}

/// <summary>The reads and the lending scope. SHELL: they read streams.</summary>
public static class Bodies
{
    [ThreadStatic] static List<PooledBody>? t_lent;

    /// <summary>Does this thread hold an open <see cref="Lend"/> scope?</summary>
    public static bool Lending => t_lent is not null;

    /// <summary>Open a lending scope on THIS thread; dispose it on the same thread (<c>using</c>). Scopes nest: an inner
    /// one returns only what was lent inside it.</summary>
    public static LendScope Lend()
    {
        List<PooledBody>? outer = t_lent;
        var mine = new List<PooledBody>(4);
        t_lent = mine;
        return new LendScope(outer, mine);
    }

    /// <summary>The scope <see cref="Lend"/> opened. Its end returns every body lent inside it.</summary>
    public readonly struct LendScope : IDisposable
    {
        readonly List<PooledBody>? _outer;
        readonly List<PooledBody>? _mine;

        internal LendScope(List<PooledBody>? outer, List<PooledBody> mine)
        {
            _outer = outer;
            _mine = mine;
        }

        public void Dispose()
        {
            if (_mine is null) return;
            foreach (PooledBody body in _mine) body.Dispose();
            _mine.Clear();
            if (ReferenceEquals(t_lent, _mine)) t_lent = _outer;
        }
    }

    /// <summary>Read <paramref name="stream"/> to EOF into a rented buffer. The caller owns the result and disposes it.
    /// A read that throws returns its buffer first.</summary>
    public static PooledBody ReadPooled(Stream stream, long? lengthHint)
    {
        ArrayPool<byte> pool = ArrayPool<byte>.Shared;
        byte[] buffer = pool.Rent(BodySizing.Start(lengthHint));
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    byte[] bigger = pool.Rent(BodySizing.Next(buffer.Length));
                    buffer.AsSpan(0, length).CopyTo(bigger);
                    pool.Return(buffer);
                    buffer = bigger;
                }
                int read = stream.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
        }
        catch
        {
            pool.Return(buffer);
            throw;
        }
        return new PooledBody(buffer, length);
    }

    /// <summary>Read <paramref name="stream"/> to EOF into ONE exact-size array: the growth is pooled, the copy is the
    /// only allocation.</summary>
    public static byte[] ReadOwned(Stream stream, long? lengthHint)
    {
        using PooledBody body = ReadPooled(stream, lengthHint);
        return body.ToOwned();
    }

    /// <summary>Read <paramref name="stream"/> to EOF into a buffer LENT to the open scope, which returns it. Without a
    /// scope there is nobody to return it, so this is a programming error.</summary>
    public static PooledBody ReadLent(Stream stream, long? lengthHint)
    {
        List<PooledBody> scope = t_lent ?? throw new InvalidOperationException("Bodies.ReadLent needs an open Bodies.Lend() scope");
        PooledBody body = ReadPooled(stream, lengthHint);
        scope.Add(body);
        return body;
    }

    /// <summary>A buffer of exactly <paramref name="length"/> writable bytes, LENT to the open scope — for an answer this
    /// thread builds itself (the metadata cache's rebuilt response) and only decodes.</summary>
    public static PooledBody RentLent(int length, out Span<byte> into)
    {
        List<PooledBody> scope = t_lent ?? throw new InvalidOperationException("Bodies.RentLent needs an open Bodies.Lend() scope");
        var body = new PooledBody(ArrayPool<byte>.Shared.Rent(Math.Max(1, length)), length);
        scope.Add(body);
        into = body.Writable;
        return body;
    }
}
