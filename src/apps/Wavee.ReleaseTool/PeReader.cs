using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Wavee.ReleaseTool;

/// <summary>
/// A minimal 64-bit PE reader: just enough to pull the RSDS CodeView debug id (PDB GUID + age + the PDB path the
/// linker recorded), SizeOfImage, out of a <c>Wavee.exe</c>.
/// </summary>
/// <remarks>
/// Deliberately a PRIVATE copy, not a reference to <c>Wavee.Crash.PeDebugId</c> — that pure class belongs to WP-A/B
/// of the crash &amp; diagnostics plan and is written by a different agent concurrently; sharing a file here would
/// be a race on the same source. Wavee.ReleaseTool already has no ProjectReference to Wavee.csproj (see its own
/// header comment) so a private reader is also the path of least resistance, not just a scheduling convenience.
/// Wavee ships win-x64 / win-arm64 only, so only the PE32+ (64-bit) optional header shape is supported.
/// </remarks>
static class PeReader
{
    public sealed record DebugInfo(Guid PdbGuid, uint Age, string PdbPath, ulong SizeOfImage);

    const int DebugDataDirectoryIndex = 6;      // IMAGE_DIRECTORY_ENTRY_DEBUG
    const uint ImageDebugTypeCodeView = 2;       // IMAGE_DEBUG_TYPE_CODEVIEW
    const ushort Pe32PlusMagic = 0x20B;

    public static DebugInfo ReadDebugInfo(string exePath)
    {
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // ---- DOS header: just e_lfanew (offset of the PE header) at 0x3C ----------------------------------------
        Span<byte> dos = stackalloc byte[64];
        ReadExact(fs, dos);
        if (dos[0] != (byte)'M' || dos[1] != (byte)'Z')
            throw new InvalidDataException(exePath + " is not a PE file (missing MZ signature)");
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.Slice(0x3C, 4));

        // ---- PE signature + IMAGE_FILE_HEADER --------------------------------------------------------------------
        fs.Position = peOffset;
        Span<byte> peSig = stackalloc byte[4];
        ReadExact(fs, peSig);
        if (peSig[0] != (byte)'P' || peSig[1] != (byte)'E' || peSig[2] != 0 || peSig[3] != 0)
            throw new InvalidDataException(exePath + ": no PE signature at offset " + peOffset);

        Span<byte> fileHeader = stackalloc byte[20];
        ReadExact(fs, fileHeader);
        ushort numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader.Slice(2, 2));
        ushort sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader.Slice(16, 2));
        if (sizeOfOptionalHeader < 112)
            throw new InvalidDataException(exePath + ": optional header too small (" + sizeOfOptionalHeader + " bytes) to carry a data directory table");

        // ---- IMAGE_OPTIONAL_HEADER64 ------------------------------------------------------------------------------
        long optionalHeaderStart = fs.Position;
        byte[] optionalHeader = new byte[sizeOfOptionalHeader];
        ReadExact(fs, optionalHeader);
        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(optionalHeader.AsSpan(0, 2));
        if (magic != Pe32PlusMagic)
            throw new InvalidDataException(exePath + ": not a PE32+ (64-bit) image (magic 0x" + magic.ToString("X4") +
                "); Wavee ships win-x64/win-arm64 only");

        uint sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(56, 4));
        uint numberOfRvaAndSizes = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(108, 4));
        if (numberOfRvaAndSizes <= DebugDataDirectoryIndex)
            throw new InvalidDataException(exePath + ": no debug data directory (NumberOfRvaAndSizes=" + numberOfRvaAndSizes +
                ") - was it published with NativeDebugSymbols=true?");

        int dirOffset = 112 + DebugDataDirectoryIndex * 8;
        uint debugDirRva = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(dirOffset, 4));
        uint debugDirSize = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(dirOffset + 4, 4));
        if (debugDirRva == 0 || debugDirSize == 0)
            throw new InvalidDataException(exePath + ": empty debug data directory - was it published with NativeDebugSymbols=true?");

        // ---- section headers, to translate the debug directory's RVA to a file offset -----------------------------
        fs.Position = optionalHeaderStart + sizeOfOptionalHeader;
        var sections = new (uint VirtualAddress, uint VirtualSize, uint PointerToRawData)[numberOfSections];
        Span<byte> section = stackalloc byte[40];
        for (int i = 0; i < numberOfSections; i++)
        {
            ReadExact(fs, section);
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(8, 4));
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(12, 4));
            uint pointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(20, 4));
            sections[i] = (virtualAddress, virtualSize, pointerToRawData);
        }

        long debugDirFileOffset = RvaToFileOffset(sections, debugDirRva, exePath);
        int entryCount = (int)(debugDirSize / 28);   // sizeof(IMAGE_DEBUG_DIRECTORY)
        fs.Position = debugDirFileOffset;

        Guid? pdbGuid = null;
        uint age = 0;
        string pdbPath = string.Empty;
        for (int i = 0; i < entryCount; i++)
        {
            Span<byte> entry = stackalloc byte[28];
            ReadExact(fs, entry);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4));
            uint sizeOfData = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(16, 4));
            uint pointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(24, 4));
            if (type != ImageDebugTypeCodeView || sizeOfData < 24 || pointerToRawData == 0) continue;

            long resumeAt = fs.Position;
            fs.Position = pointerToRawData;
            byte[] cv = new byte[sizeOfData];
            ReadExact(fs, cv);
            fs.Position = resumeAt;

            // "RSDS" + GUID(16) + Age(4) + a NUL-terminated PDB path — the modern (PDB 7.0) CodeView record every
            // MSVC/link.exe toolchain since VS2005 writes; there is no older format to fall back to here.
            if (cv.Length < 24 || cv[0] != (byte)'R' || cv[1] != (byte)'S' || cv[2] != (byte)'D' || cv[3] != (byte)'S') continue;

            pdbGuid = new Guid(cv.AsSpan(4, 16));
            age = BinaryPrimitives.ReadUInt32LittleEndian(cv.AsSpan(20, 4));
            int nameEnd = Array.IndexOf(cv, (byte)0, 24);
            if (nameEnd < 0) nameEnd = cv.Length;
            pdbPath = Encoding.UTF8.GetString(cv, 24, nameEnd - 24);
            break;
        }

        if (pdbGuid is null)
            throw new InvalidDataException(exePath + ": no RSDS CodeView debug record found - was it published with NativeDebugSymbols=true?");

        return new DebugInfo(pdbGuid.Value, age, pdbPath, sizeOfImage);
    }

    static long RvaToFileOffset((uint VirtualAddress, uint VirtualSize, uint PointerToRawData)[] sections, uint rva, string exePath)
    {
        foreach (var s in sections)
        {
            uint span = Math.Max(s.VirtualSize, 1);
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + span)
                return s.PointerToRawData + (rva - s.VirtualAddress);
        }
        throw new InvalidDataException(exePath + ": RVA 0x" + rva.ToString("X") + " is not inside any section");
    }

    static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n <= 0) throw new EndOfStreamException("truncated PE file (wanted " + buffer.Length + " bytes, got " + total + ")");
            total += n;
        }
    }
}
