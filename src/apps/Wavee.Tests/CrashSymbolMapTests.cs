// ── Wavee.Tests/CrashSymbolMapTests.cs — the `.symmap` binary format round-trips (crash & diagnostics plan §I) ────
//
// WP-G's half of the plan's shared contract: `Crash.SymbolMap` (src/apps/Wavee/Platform/Crash.SymbolMap.cs) is
// linked into Wavee.ReleaseTool's `symbol-map` verb AND is what a future in-app/Worker-side reader would parse, so
// its Write/Read/TryResolve round-trip is covered here directly (a pure, engine-free format class — no source-text
// test needed, this tests the real thing).

using System;
using System.Collections.Generic;
using System.IO;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class CrashSymbolMapTests
{
    static readonly Guid SampleGuid = new("7e2c1234-5678-4abc-9def-0123456789ab");

    static Crash.SymbolMap.Entry[] SampleEntries() =>
    [
        new(0x2000, 0x40, "Wavee_Entities_Detail_UI_Hero__Render"),
        new(0x1000, 0x80, "Wavee_App_Main"),
        new(0x3000, 0x10, "Wavee_Queue_DecideSeed"),
    ];

    static MemoryStream WriteSample(out Guid guid, out uint age, out ulong imageSize)
    {
        guid = SampleGuid;
        age = 3;
        imageSize = 0x2A00000;
        var ms = new MemoryStream();
        Crash.SymbolMap.Write(ms, SampleEntries(), guid, age, imageSize);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Round_trips_header_fields()
    {
        using var ms = WriteSample(out Guid guid, out uint age, out ulong imageSize);
        var map = Crash.SymbolMap.Read(ms);

        Assert.Equal(guid, map.PdbGuid);
        Assert.Equal(age, map.Age);
        Assert.Equal(imageSize, map.ImageSize);
        Assert.Equal(3, map.Entries.Count);
    }

    [Fact]
    public void Write_sorts_entries_by_rva_regardless_of_input_order()
    {
        using var ms = WriteSample(out _, out _, out _);
        var map = Crash.SymbolMap.Read(ms);

        Assert.Equal(0x1000u, map.Entries[0].Rva);
        Assert.Equal(0x2000u, map.Entries[1].Rva);
        Assert.Equal(0x3000u, map.Entries[2].Rva);
        Assert.Equal("Wavee_App_Main", map.Entries[0].Name);
        Assert.Equal("Wavee_Entities_Detail_UI_Hero__Render", map.Entries[1].Name);
        Assert.Equal("Wavee_Queue_DecideSeed", map.Entries[2].Name);
    }

    [Fact]
    public void TryResolve_finds_the_exact_entry_start()
    {
        using var ms = WriteSample(out _, out _, out _);
        var map = Crash.SymbolMap.Read(ms);

        bool ok = map.TryResolve(0x2000, out string name, out uint offset);
        Assert.True(ok);
        Assert.Equal("Wavee_Entities_Detail_UI_Hero__Render", name);
        Assert.Equal(0u, offset);
    }

    [Fact]
    public void TryResolve_finds_the_nearest_symbol_at_or_below_an_rva_inside_a_function_body()
    {
        // A report's frame RVA is almost never a function's exact entry point (crash & diagnostics plan §I): the
        // resolver must answer with the containing function, the same way WinDbg's `ln` does.
        using var ms = WriteSample(out _, out _, out _);
        var map = Crash.SymbolMap.Read(ms);

        bool ok = map.TryResolve(0x2010, out string name, out uint offset);
        Assert.True(ok);
        Assert.Equal("Wavee_Entities_Detail_UI_Hero__Render", name);
        Assert.Equal(0x10u, offset);
    }

    [Fact]
    public void TryResolve_fails_below_the_first_entry()
    {
        using var ms = WriteSample(out _, out _, out _);
        var map = Crash.SymbolMap.Read(ms);

        bool ok = map.TryResolve(0x500, out string name, out uint offset);
        Assert.False(ok);
        Assert.Equal(string.Empty, name);
        Assert.Equal(0u, offset);
    }

    [Fact]
    public void TryResolve_answers_past_the_last_entry_too_offsetInto_just_keeps_growing()
    {
        // No Size-based bound is enforced deliberately: a stripped/incomplete symbol table (or a tail-called/folded
        // body, IlcFoldIdenticalMethodBodies) still gets the nearest-below name, matching `ln`'s own behavior.
        using var ms = WriteSample(out _, out _, out _);
        var map = Crash.SymbolMap.Read(ms);

        bool ok = map.TryResolve(0x3100, out string name, out uint offset);
        Assert.True(ok);
        Assert.Equal("Wavee_Queue_DecideSeed", name);
        Assert.Equal(0x100u, offset);
    }

    [Fact]
    public void TryResolve_on_an_empty_map_always_fails()
    {
        var ms = new MemoryStream();
        Crash.SymbolMap.Write(ms, Array.Empty<Crash.SymbolMap.Entry>(), SampleGuid, 1, 0x1000);
        ms.Position = 0;
        var map = Crash.SymbolMap.Read(ms);

        Assert.False(map.TryResolve(0, out _, out _));
    }

    [Fact]
    public void Read_rejects_a_bad_magic()
    {
        using var ms = WriteSample(out _, out _, out _);
        var bytes = ms.ToArray();
        bytes[0] = (byte)'X';
        using var bad = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() => Crash.SymbolMap.Read(bad));
    }

    [Fact]
    public void Read_rejects_an_unsupported_version()
    {
        using var ms = WriteSample(out _, out _, out _);
        var bytes = ms.ToArray();
        bytes[4] = 2; // version field, offset 4, little-endian u32 -> low byte first
        using var bad = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() => Crash.SymbolMap.Read(bad));
    }

    [Fact]
    public void Read_throws_on_a_truncated_file()
    {
        using var ms = WriteSample(out _, out _, out _);
        var bytes = ms.ToArray();
        using var truncated = new MemoryStream(bytes[..(bytes.Length - 5)]);

        Assert.ThrowsAny<EndOfStreamException>(() => Crash.SymbolMap.Read(truncated));
    }

    [Fact]
    public void Round_trips_an_ilc_mangled_name_and_an_empty_one()
    {
        var entries = new List<Crash.SymbolMap.Entry>
        {
            new(0, 4, "Wavee_Wavee_Entities_Album_Page__Load"),
            new(0x10, 4, string.Empty), // DbgHelp can hand back an empty name for a compiler-generated thunk
        };
        using var ms = new MemoryStream();
        Crash.SymbolMap.Write(ms, entries, SampleGuid, 7, 0x9000);
        ms.Position = 0;
        var map = Crash.SymbolMap.Read(ms);

        Assert.Equal("Wavee_Wavee_Entities_Album_Page__Load", map.Entries[0].Name);
        Assert.Equal(string.Empty, map.Entries[1].Name);
    }
}
