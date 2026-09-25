// ── Platform/Crash.SymbolMap.cs — the `.symmap` binary format (crash & diagnostics plan §I) ──────────────────────
//
// Role: CORE, engine-free, no unsafe. Written by Wavee.ReleaseTool's `symbol-map` verb (Wavee.exe + Wavee.pdb via
// DbgHelp -> a .symmap next to each architecture's package), read by the Cloudflare Worker at crash-ingest time
// (a TypeScript port of this same byte layout) and by anyone resolving a bundle by hand.
//
// Wavee.ReleaseTool has NO ProjectReference to Wavee.csproj (it is a from-scratch AOT tool that compiles a handful
// of named partials by file path, see its own header comment) — this file is linked into it by path
// (`<Compile Include="..\Wavee\Platform\Crash.SymbolMap.cs" Link="Crash.SymbolMap.cs" />`) so the format the tool
// WRITES and the format the app (or a future in-app resolver) READS are the literal same code, never two hand-kept
// copies that can drift.
//
// BYTE LAYOUT (little-endian throughout; this is a contract shared with the Worker — do not change without also
// changing its TypeScript reader):
//   offset  size  field
//   0       4     magic "WSYM" (ASCII bytes 'W','S','Y','M', not a numeric constant — avoids an endianness footgun)
//   4       4     u32 version = 1
//   8       4     u32 count            — number of symbol entries
//   12      4     u32 stringTableBytes — length of the trailing string table
//   16      16    PDB GUID, Guid.ToByteArray() byte order
//   32      4     u32 age
//   36      8     u64 imageSize
//   44      ..    count * { u32 rva, u32 size, u32 nameOffset }, entries SORTED by rva ascending
//   ..      ..    the string table: each name is UTF-8, NUL-terminated; nameOffset indexes into this table
//
// Header is exactly 44 bytes. TryResolve does a binary search for the entry with the greatest Rva <= the queried
// RVA (the same "nearest symbol at or below" answer `cdb`'s `ln` gives), matching how a report's frame RVA — which
// is very rarely a function's exact entry point — is normally read.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wavee;

public static partial class Crash
{
    public sealed class SymbolMap
    {
        /// <summary>One resolved symbol: its start RVA, byte size (0 if DbgHelp did not report one) and mangled name.</summary>
        public readonly record struct Entry(uint Rva, uint Size, string Name);

        const int HeaderBytes = 44;
        const uint FormatVersion = 1;

        static ReadOnlySpan<byte> Magic => "WSYM"u8;

        /// <summary>The PDB's debug-directory GUID this map was built from.</summary>
        public Guid PdbGuid { get; }

        /// <summary>The PDB's debug-directory age.</summary>
        public uint Age { get; }

        /// <summary>The exe's SizeOfImage at the time the map was built.</summary>
        public ulong ImageSize { get; }

        /// <summary>Every entry, sorted by <see cref="Entry.Rva"/> ascending — what <see cref="TryResolve"/> searches.</summary>
        public IReadOnlyList<Entry> Entries { get; }

        SymbolMap(Guid pdbGuid, uint age, ulong imageSize, Entry[] entriesSortedByRva)
        {
            PdbGuid = pdbGuid;
            Age = age;
            ImageSize = imageSize;
            Entries = entriesSortedByRva;
        }

        /// <summary>
        /// Writes the binary map. <paramref name="entries"/> need not be pre-sorted; this sorts a private copy by
        /// RVA before writing (the string table is emitted in that same, final order — nameOffset always lands where
        /// the on-disk record table says it does).
        /// </summary>
        public static void Write(Stream stream, IEnumerable<Entry> entries, Guid pdbGuid, uint age, ulong imageSize)
        {
            ArgumentNullException.ThrowIfNull(stream);
            ArgumentNullException.ThrowIfNull(entries);

            var sorted = new List<Entry>(entries);
            sorted.Sort(static (a, b) => a.Rva.CompareTo(b.Rva));

            var nameOffsets = new uint[sorted.Count];
            using var stringTable = new MemoryStream();
            Span<byte> scratch = stackalloc byte[512];   // hoisted out of the loop (CA2014); longer names heap-allocate
            for (int i = 0; i < sorted.Count; i++)
            {
                nameOffsets[i] = checked((uint)stringTable.Position);
                string name = sorted[i].Name ?? string.Empty;
                int byteCount = Encoding.UTF8.GetByteCount(name);
                Span<byte> nameBytes = byteCount <= scratch.Length ? scratch.Slice(0, byteCount) : new byte[byteCount];
                Encoding.UTF8.GetBytes(name, nameBytes);
                stringTable.Write(nameBytes);
                stringTable.WriteByte(0);
            }
            byte[] stringBytes = stringTable.ToArray();

            Span<byte> header = stackalloc byte[HeaderBytes];
            Magic.CopyTo(header);
            int o = 4;
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(o, 4), FormatVersion); o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(o, 4), checked((uint)sorted.Count)); o += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(o, 4), checked((uint)stringBytes.Length)); o += 4;
            pdbGuid.ToByteArray().CopyTo(header.Slice(o, 16)); o += 16;
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(o, 4), age); o += 4;
            BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(o, 8), imageSize); o += 8;
            stream.Write(header);

            Span<byte> record = stackalloc byte[12];
            for (int i = 0; i < sorted.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(0, 4), sorted[i].Rva);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(4, 4), sorted[i].Size);
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(8, 4), nameOffsets[i]);
                stream.Write(record);
            }
            stream.Write(stringBytes);
        }

        /// <summary>Reads and validates a map written by <see cref="Write"/>. Throws <see cref="InvalidDataException"/>
        /// on a bad magic/version and <see cref="EndOfStreamException"/> on a truncated file.</summary>
        public static SymbolMap Read(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            Span<byte> header = stackalloc byte[HeaderBytes];
            ReadExact(stream, header);
            if (!header[..4].SequenceEqual(Magic))
                throw new InvalidDataException("not a .symmap file (bad magic)");

            int o = 4;
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(o, 4)); o += 4;
            if (version != FormatVersion)
                throw new InvalidDataException($".symmap version {version} is not supported (expected {FormatVersion})");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(o, 4)); o += 4;
            uint stringTableBytes = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(o, 4)); o += 4;
            var pdbGuid = new Guid(header.Slice(o, 16)); o += 16;
            uint age = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(o, 4)); o += 4;
            ulong imageSize = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(o, 8));

            var raw = new (uint Rva, uint Size, uint NameOffset)[count];
            Span<byte> record = stackalloc byte[12];
            for (int i = 0; i < count; i++)
            {
                ReadExact(stream, record);
                raw[i] = (BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(0, 4)),
                          BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(4, 4)),
                          BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(8, 4)));
            }

            var stringBytes = new byte[stringTableBytes];
            ReadExact(stream, stringBytes);

            var entries = new Entry[count];
            for (int i = 0; i < count; i++)
                entries[i] = new Entry(raw[i].Rva, raw[i].Size, ReadNulTerminated(stringBytes, raw[i].NameOffset));

            return new SymbolMap(pdbGuid, age, imageSize, entries);
        }

        /// <summary>Binary search for the symbol whose RVA range contains <paramref name="rva"/> — in practice, the
        /// nearest entry at or below it, the same answer WinDbg's <c>ln</c> gives for an arbitrary offset inside a
        /// function body. False when the map is empty or every entry starts after <paramref name="rva"/>.</summary>
        public bool TryResolve(uint rva, out string name, out uint offsetInto)
        {
            name = string.Empty;
            offsetInto = 0;
            int lo = 0, hi = Entries.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (Entries[mid].Rva <= rva) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (found < 0) return false;
            name = Entries[found].Name;
            offsetInto = rva - Entries[found].Rva;
            return true;
        }

        static string ReadNulTerminated(byte[] table, uint offset)
        {
            if (offset >= table.Length) return string.Empty;
            int end = Array.IndexOf(table, (byte)0, (int)offset);
            if (end < 0) end = table.Length;
            return Encoding.UTF8.GetString(table, (int)offset, end - (int)offset);
        }

        static void ReadExact(Stream stream, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = stream.Read(buffer[total..]);
                if (n <= 0) throw new EndOfStreamException("truncated .symmap file");
                total += n;
            }
        }
    }
}
