using System;
using System.IO;
using ZstdSharp;

namespace Wavee.Backend.Persistence;

// ── library.db catalog payload framing ────────────────────────────────────────────────────────────────────────────────
// Every catalog blob (`catalog_resource.payload`, `extension_cache.payload`) carries a 1-BYTE FORMAT PREFIX so the
// stored bytes are self-describing: a reader never has to trust the `fmt` column (which is kept in sync purely for
// SQL-side stats and for a future re-encode sweep). 0 = raw STJ JSON, 1 = zstd(JSON) — 2 is reserved for zstd + a
// trained dictionary.
//
// This is the ONLY place that knows the framing: SqliteColdStore routes every payload read and write through it.
public static class PayloadCodec
{
    public const int FmtRawJson = 0;
    public const int FmtZstd = 1;

    /// <summary>zstd level 3 — the same level the transport guard uses; single-digit-µs decode for ~1 KB rows.</summary>
    public const int ZstdLevel = 3;

    // ZstdSharp's Compressor/Decompressor are stateful and NOT thread-safe. One instance per thread (the writer
    // thread, whichever thread reads) beats allocating a fresh context per row.
    [ThreadStatic] static Compressor? _comp;
    [ThreadStatic] static Decompressor? _decomp;

    /// <summary>Frame <paramref name="json"/> for storage under <paramref name="fmt"/> (prefix byte + body).</summary>
    public static byte[] Encode(ReadOnlySpan<byte> json, int fmt)
    {
        if (fmt == FmtZstd && json.Length > 0)
        {
            var comp = _comp ??= new Compressor(ZstdLevel);
            var body = comp.Wrap(json);
            var packed = new byte[body.Length + 1];
            packed[0] = FmtZstd;
            body.CopyTo(packed.AsSpan(1));
            return packed;
        }
        var raw = new byte[json.Length + 1];
        raw[0] = FmtRawJson;
        json.CopyTo(raw.AsSpan(1));
        return raw;
    }

    /// <summary>The format byte of a stored blob (raw for an empty/absent payload).</summary>
    public static int FormatOf(byte[]? stored) => stored is { Length: > 0 } ? stored[0] : FmtRawJson;

    /// <summary>Unframe a stored blob back to the raw UTF-8 JSON bytes the callers deserialize.</summary>
    public static byte[] Decode(byte[]? stored)
    {
        if (stored is null || stored.Length == 0) return Array.Empty<byte>();
        int fmt = stored[0];
        if (stored.Length == 1) return Array.Empty<byte>();
        if (fmt == FmtRawJson) return stored.AsSpan(1).ToArray();
        if (fmt != FmtZstd) throw new InvalidDataException("Unsupported catalog payload format.");

        try
        {
            var d = _decomp ??= new Decompressor();
            var un = d.Unwrap(stored.AsSpan(1));
            if (un.Length > 0) return un.ToArray();
        }
        catch (Exception)
        {
            _decomp = null;   // a faulted context is not reusable
        }
        // Frames written without a content-size header (or a faulted one-shot) decode frame-by-frame instead.
        using var src = new MemoryStream(stored, 1, stored.Length - 1, writable: false);
        using var zs = new DecompressionStream(src);
        using var dst = new MemoryStream();
        zs.CopyTo(dst);
        return dst.ToArray();
    }
}
