// ── Playback/VorbisComments.cs — the one walk over a Vorbis comment block ─────────────────────────────────────────────
//
// A Vorbis comment block is the vendor string, a field count, then that many length-prefixed `KEY=value` fields, every
// length little-endian. Ogg Vorbis carries it after a 7-byte packet prefix, Opus after `OpusTags`, FLAC as its
// VORBIS_COMMENT metadata block. Two readers need it: the ReplayGain tags at open (`Playback.Audio.ReplayGainTags`) and
// the local index's tag reader (`LocalTags`, #240). This is the walk both use, so the format is parsed in one place.
//
// The block is reached through a source rather than a span, because an Ogg comment packet can span pages and carry a
// base64 cover of megabytes: the tag reader hands the walk a source that maps packet offsets onto the pages and never
// reads a field it does not want. A field longer than `MaxInlineField` is not read at all: the visitor is told its key
// and where its value sits (`LargeField`) and the walk moves past it by length. PURE over the source; no allocation.

using System.Buffers.Binary;

namespace Wavee;

/// <summary>The Vorbis comment block walk (vendor, count, length-prefixed <c>KEY=value</c> fields, little-endian).</summary>
public static class VorbisComments
{
    /// <summary>A field longer than this is not handed over whole: <see cref="IVisitor.LargeField"/> gets its key and position.</summary>
    public const int MaxInlineField = 16 * 1024;

    /// <summary>The longest key the walk looks for in a large field's first bytes.</summary>
    const int MaxKeyBytes = 64;

    /// <summary>Receives each field. Keys are ASCII and compared case-insensitively by the visitor; values are UTF-8.</summary>
    public interface IVisitor
    {
        /// <summary>One field no longer than <see cref="MaxInlineField"/>, and where its value starts in the source. The spans live
        /// until the next call.</summary>
        void Field(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, long valueAt);

        /// <summary>One field longer than <see cref="MaxInlineField"/>: its key, and where its value starts in the source and
        /// how long it is. The walk skips it.</summary>
        void LargeField(ReadOnlySpan<byte> key, long valueAt, int valueLength);
    }

    /// <summary>Where the block's bytes come from.</summary>
    public interface ISource
    {
        /// <summary>Bytes <c>[at, at + length)</c> of the block, or false when the block ends (or the bytes cannot be read)
        /// before them. The span lives until the next call.</summary>
        bool TryRead(long at, int length, out ReadOnlySpan<byte> bytes);
    }

    /// <summary>A block that is one contiguous span (FLAC, a whole Vorbis packet).</summary>
    public ref struct SpanSource(ReadOnlySpan<byte> block) : ISource
    {
        readonly ReadOnlySpan<byte> _block = block;

        /// <inheritdoc/>
        public readonly bool TryRead(long at, int length, out ReadOnlySpan<byte> bytes)
        {
            bytes = default;
            if (at < 0 || length < 0 || at + length > _block.Length) return false;
            bytes = _block.Slice((int)at, length);
            return true;
        }
    }

    /// <summary>Walk <paramref name="block"/> (one span). False when the block is cut short before its last field (the
    /// fields before the cut were still visited).</summary>
    public static bool ForEach<TVisitor>(ReadOnlySpan<byte> block, ref TVisitor visitor)
        where TVisitor : IVisitor, allows ref struct
    {
        var source = new SpanSource(block);
        return ForEach(ref source, ref visitor);
    }

    /// <summary>Walk the block <paramref name="source"/> serves, from its offset 0. Same answer as the span overload.</summary>
    public static bool ForEach<TSource, TVisitor>(ref TSource source, ref TVisitor visitor)
        where TSource : ISource, allows ref struct
        where TVisitor : IVisitor, allows ref struct
    {
        long pos = 0;
        if (!ReadU32(ref source, ref pos, out uint vendor)) return false;
        pos += vendor;
        if (!ReadU32(ref source, ref pos, out uint count)) return false;
        for (uint i = 0; i < count; i++)
        {
            if (!ReadU32(ref source, ref pos, out uint len)) return false;
            if (len <= MaxInlineField)
            {
                if (!source.TryRead(pos, (int)len, out ReadOnlySpan<byte> field)) return false;
                int eq = field.IndexOf((byte)'=');
                if (eq > 0) visitor.Field(field[..eq], field[(eq + 1)..], pos + eq + 1);
            }
            else
            {
                if (len > int.MaxValue || !source.TryRead(pos, MaxKeyBytes, out ReadOnlySpan<byte> prefix)) return false;
                int eq = prefix.IndexOf((byte)'=');
                if (eq > 0) visitor.LargeField(prefix[..eq], pos + eq + 1, (int)len - eq - 1);
            }
            pos += len;
        }
        return true;
    }

    static bool ReadU32<TSource>(ref TSource source, ref long pos, out uint value)
        where TSource : ISource, allows ref struct
    {
        value = 0;
        if (!source.TryRead(pos, 4, out ReadOnlySpan<byte> b)) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(b);
        pos += 4;
        return true;
    }
}
