// ── Entities/PalettePersistence.cs — CORE (owner C; plan §WS-C) ────────────────────────────────────────────────────
//
// Pure decisions for the palette's disk half: what is worth writing, how a Scheme becomes 20 bytes and back, and
// whether a loaded row is still fresh. No sqlite, no table, no clock source of its own — Store.Palette.cs calls this;
// PalettePersistenceTests pins it with no file on disk at all.

using System.Buffers.Binary;

namespace Wavee;

public static class PalettePersistence
{
    /// <summary>One <see cref="Scheme"/> is five little-endian <c>uint</c>s: 20 bytes, one column read wide (Palette.cs
    /// P2), and the same width whether the row is dark or light.</summary>
    public const int SchemeBytes = 20;

    /// <summary>What a persisted row's <c>known</c> column may carry. <see cref="PaletteBits.Queued"/> never reaches
    /// disk: it names an in-flight request, and a request cannot survive the process that made it.</summary>
    public const PaletteBits PersistedBits =
        PaletteBits.Dark | PaletteBits.Light | PaletteBits.Negative | PaletteBits.BestFitIsLight;

    /// <summary>Is this row worth a disk write? Only an ANSWERED row — a queued-only row has nothing to persist.</summary>
    public static bool IsWorthPersisting(uint known) => (known & (uint)PaletteBits.Answered) != 0;

    /// <summary>Strip everything a stored row must not carry back into memory (chiefly <see cref="PaletteBits.Queued"/>,
    /// which the writer never set on disk in the first place, but a future column added to <see cref="PaletteBits"/>
    /// without a matching bit here would otherwise leak through unmasked).</summary>
    public static uint RestoreMask(uint stored) => stored & (uint)PersistedBits;

    /// <summary>Write one scheme's five roles, little-endian, into a 20-byte destination.</summary>
    public static void Encode(in Scheme scheme, Span<byte> dst)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst[0..4], scheme.BackgroundBase);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[4..8], scheme.BackgroundTintedBase);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[8..12], scheme.TextBase);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[12..16], scheme.TextSubdued);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[16..20], scheme.TextBrightAccent);
    }

    /// <summary>The inverse of <see cref="Encode"/>. Anything shorter than <see cref="SchemeBytes"/> (a NULL blob, a
    /// foreign row) decodes to <c>default</c> — empty, never a partial read.</summary>
    public static Scheme Decode(ReadOnlySpan<byte> src)
    {
        if (src.Length < SchemeBytes) return default;
        return new Scheme(
            BinaryPrimitives.ReadUInt32LittleEndian(src[0..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[16..20]));
    }

    /// <summary>Is a loaded row still within its TTL? Same rule as the live table's <c>Fresh</c> (Palette.cs): a hit
    /// lasts <see cref="Palette.HitTtlSeconds"/>, a negative only <see cref="Palette.MissTtlSeconds"/>.</summary>
    public static bool FreshOnLoad(uint known, long tsUnix, long nowUnix)
    {
        if ((known & (uint)PaletteBits.Answered) == 0) return false;
        long ttl = (known & (uint)PaletteBits.Negative) != 0 ? Palette.MissTtlSeconds : Palette.HitTtlSeconds;
        return nowUnix - tsUnix <= ttl;
    }

    /// <summary>The warm read's SELECT bound: the longest TTL any persisted row could still be fresh under. Coarse on
    /// purpose — <see cref="FreshOnLoad"/> narrows a negative row's shorter window after the row is in hand, so this
    /// only has to stop the query from scanning rows that could not possibly still be fresh.</summary>
    public static long LoadCutoffUnix(long nowUnix) => nowUnix - Palette.HitTtlSeconds;
}
