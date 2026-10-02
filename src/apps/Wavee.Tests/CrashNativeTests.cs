// ── Wavee.Tests/CrashNativeTests.cs — Platform/Crash.Native.cs, Crash.Handler's layout probe, the bundle finalizers ──
//
// #165 W3a (docs/plans/wavee/crash-production-readiness-implementation.md "W3a" + appendix A3): the pure native-crash
// identity rules — which captured frames become a native bundle's RVAs (NativeFrames.SelectAppRvas), which module a
// fault address falls in and how its name is normalized (FaultModule) — the MINIDUMP_EXCEPTION_INFORMATION layout the
// dump request hands dbghelp (pshpack4), the shared "Frames (RVA)" report section, and the bundle helpers the child
// finalizes a bundle with (UpdateSummary / AppendReportLine) plus WriteSend's never-recreate rule. The last group uses
// a temp directory per test (the only disk these facts touch); everything else is a table over plain values.

using System.Text;
using Xunit;

namespace Wavee.Tests;

// ── 1. MINIDUMP_EXCEPTION_INFORMATION — the SDK's pshpack4 layout ──────────────────────────────────────────────────

public class MinidumpExceptionInfoLayoutTests
{
    [Fact]
    public void The_managed_declaration_matches_the_sdk_pshpack4_layout()
    {
        // minidumpapiset.h: ThreadId@0, ExceptionPointers@4, ClientPointers@4+sizeof(void*) — no padding anywhere.
        var expected = IntPtr.Size == 8
            ? new Crash.Handler.NativeLayout(Size: 16, ExceptionPointersOffset: 4, ClientPointersOffset: 12)
            : new Crash.Handler.NativeLayout(Size: 12, ExceptionPointersOffset: 4, ClientPointersOffset: 8);
        Assert.Equal(expected, Crash.Handler.ExceptionInfoLayout());
    }
}

// ── 2. NativeFrames.SelectAppRvas ──────────────────────────────────────────────────────────────────────────────────

public class NativeFramesTests
{
    static readonly nint Base = 0x4000_0000;
    static readonly nuint Size = 0x0100_0000;

    static nint Img(int rva) => Base + rva;              // inside Wavee.exe
    static nint Foreign(int at) => 0x7000_0000 + at;     // ntdll / any other module

    static readonly nint FaultAt = Foreign(0x3000);

    /// <summary>The shape a VEH walk really has: the handler's own frames, ntdll's dispatcher (with one in-image
    /// frame wedged in it, to tell "start at the fault" from "skip the leading run"), the faulting frame, then the
    /// app frames that called into the faulting module, interleaved with one more foreign frame.</summary>
    static nint[] VehWalk(bool includeFaultFrame)
    {
        var frames = new List<nint>
        {
            Img(0x10),          // CaptureFaultStack
            Img(0x20),          // Veh
            Foreign(0x1000),    // RtlpCallVectoredHandlers
            Img(0x30),          // an in-image frame inside the dispatch (e.g. a runtime thunk)
            Foreign(0x2000),    // KiUserExceptionDispatcher
        };
        if (includeFaultFrame) frames.Add(FaultAt);   // RtlFillMemory+x — the faulting PC
        frames.AddRange([Img(0x500), Foreign(0x4000), Img(0x600), Img(0x700)]);
        return frames.ToArray();
    }

    [Fact]
    public void Selection_starts_at_the_exception_address_frame()
    {
        long[] rvas = Crash.NativeFrames.SelectAppRvas(VehWalk(includeFaultFrame: true), FaultAt, Base, Size);
        Assert.Equal(new long[] { 0x500, 0x600, 0x700 }, rvas);
    }

    [Fact]
    public void Without_the_exception_frame_only_the_handlers_leading_wavee_frames_are_skipped()
    {
        long[] rvas = Crash.NativeFrames.SelectAppRvas(VehWalk(includeFaultFrame: false), FaultAt, Base, Size);
        Assert.Equal(new long[] { 0x30, 0x500, 0x600, 0x700 }, rvas);
    }

    [Fact]
    public void An_unknown_exception_address_takes_the_same_fallback()
    {
        long[] rvas = Crash.NativeFrames.SelectAppRvas(VehWalk(includeFaultFrame: true), exceptionAddress: 0, Base, Size);
        Assert.Equal(new long[] { 0x30, 0x500, 0x600, 0x700 }, rvas);
    }

    [Fact]
    public void A_fault_inside_the_image_is_its_own_first_rva()
    {
        nint[] frames = [Img(0x10), Foreign(0x1000), Img(0x900), Img(0x500)];
        Assert.Equal(new long[] { 0x900, 0x500 }, Crash.NativeFrames.SelectAppRvas(frames, Img(0x900), Base, Size));
    }

    [Fact]
    public void Only_frames_inside_the_image_range_are_kept()
    {
        nint[] frames = [FaultAt, Base - 1, Base, Base + (nint)Size - 1, Base + (nint)Size, Foreign(0x10)];
        Assert.Equal(new long[] { 0, (long)Size - 1 }, Crash.NativeFrames.SelectAppRvas(frames, FaultAt, Base, Size));
    }

    [Fact]
    public void At_most_max_rvas_are_kept_in_order()
    {
        var frames = new List<nint> { Img(0x10), FaultAt };
        for (int i = 0; i < 100; i++) frames.Add(Img(0x1000 + i * 0x10));
        nint[] walk = frames.ToArray();

        long[] capped = Crash.NativeFrames.SelectAppRvas(walk, FaultAt, Base, Size);
        Assert.Equal(Crash.NativeFrames.MaxRvas, capped.Length);
        Assert.Equal(0x1000, capped[0]);
        Assert.Equal(0x1000 + (Crash.NativeFrames.MaxRvas - 1) * 0x10, capped[^1]);

        Assert.Equal(new long[] { 0x1000, 0x1010, 0x1020 }, Crash.NativeFrames.SelectAppRvas(walk, FaultAt, Base, Size, max: 3));
    }

    [Fact]
    public void An_all_foreign_walk_yields_no_rvas()
    {
        nint[] frames = [Foreign(0x1000), Foreign(0x2000), FaultAt, Foreign(0x4000)];
        Assert.Empty(Crash.NativeFrames.SelectAppRvas(frames, FaultAt, Base, Size));
        Assert.Empty(Crash.NativeFrames.SelectAppRvas(frames, exceptionAddress: 0, Base, Size));
    }

    [Fact]
    public void No_frames_no_image_range_or_a_zero_cap_yields_no_rvas()
    {
        Assert.Empty(Crash.NativeFrames.SelectAppRvas([], FaultAt, Base, Size));
        Assert.Empty(Crash.NativeFrames.SelectAppRvas(VehWalk(true), FaultAt, imageBase: 0, Size));
        Assert.Empty(Crash.NativeFrames.SelectAppRvas(VehWalk(true), FaultAt, Base, imageSize: 0));
        Assert.Empty(Crash.NativeFrames.SelectAppRvas(VehWalk(true), FaultAt, Base, Size, max: 0));
    }
}

// ── 3. FaultModule.Find / Normalize ────────────────────────────────────────────────────────────────────────────────

public class FaultModuleTests
{
    static readonly (nint Base, uint Size)[] Modules = [(0x1000, 0x100), (0x5000, 0x2000), (0, 0)];

    [Theory]
    [InlineData(0x1000L, 0)]
    [InlineData(0x10FFL, 0)]
    [InlineData(0x1100L, -1)]    // one past the first module's end
    [InlineData(0x0FFFL, -1)]    // one before its start
    [InlineData(0x5000L, 1)]
    [InlineData(0x6FFFL, 1)]
    [InlineData(0x7000L, -1)]
    [InlineData(0L, -1)]         // an unread (0, 0) entry never matches
    public void Find_answers_the_module_whose_range_holds_the_address(long address, int expected) =>
        Assert.Equal(expected, Crash.FaultModule.Find(Modules, (nint)address));

    [Fact]
    public void Find_over_no_modules_is_minus_one() => Assert.Equal(-1, Crash.FaultModule.Find([], 0x1000));

    [Theory]
    [InlineData("ntdll.dll", "ntdll.dll")]
    [InlineData("KERNELBASE.dll", "kernelbase.dll")]
    [InlineData(@"C:\Windows\System32\ntdll.dll", "ntdll.dll")]
    [InlineData("C:/Program Files/Vendor/Foo_Bar-1.2.dll", "foo_bar-1.2.dll")]
    [InlineData("  ucrtbase.dll  ", "ucrtbase.dll")]
    [InlineData("d3d12core.dll", "d3d12core.dll")]
    [InlineData("my module.dll", "")]        // a space is outside [a-z0-9._-]
    [InlineData(".hidden.dll", "")]          // the first character must be a letter or digit
    [InlineData("_x.dll", "")]
    [InlineData("Ünïcode.dll", "")]          // non-ASCII is rejected, never transliterated
    [InlineData(@"C:\dir\", "")]             // a path with no base name
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize_keeps_a_lower_case_base_name_the_worker_accepts_or_answers_empty(string? raw, string expected) =>
        Assert.Equal(expected, Crash.FaultModule.Normalize(raw));

    [Fact]
    public void Normalize_keeps_sixty_four_characters_and_rejects_sixty_five()
    {
        string sixtyFour = new string('a', 60) + ".dll";
        Assert.Equal(sixtyFour, Crash.FaultModule.Normalize(sixtyFour));
        Assert.Equal("", Crash.FaultModule.Normalize("b" + sixtyFour));
    }
}

// ── 4. Report.AppendRvaSection — one format for managed and native reports ─────────────────────────────────────────

public class CrashRvaSectionTests
{
    [Fact]
    public void The_section_lists_one_hex_offset_per_line_innermost_first()
    {
        var sb = new StringBuilder();
        Crash.Report.AppendRvaSection(sb, new long[] { 0x7b1fc6, 0x10 });
        string text = sb.ToString();
        Assert.StartsWith("\nFrames (RVA)\n------------\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n0x7b1fc6\n0x10\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_rvas_write_no_section()
    {
        var sb = new StringBuilder();
        Crash.Report.AppendRvaSection(sb, Array.Empty<long>());
        Assert.Equal(0, sb.Length);
    }
}

// ── 5. The bundle finalizers (temp directories) ────────────────────────────────────────────────────────────────────

public sealed class CrashBundleFinalizeTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("wavee-crash-").FullName;
    string Dir => Path.Combine(_root, "20260924-143012-118-native");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static Crash.Summary NativeSample() => CrashJsonTests.Sample() with
    {
        Kind = Crash.Kind.Native, HasDump = false, DumpBytes = 0, ExceptionCode = 0xC0000005,
    };

    [Fact]
    public void WriteSend_never_recreates_a_deleted_bundle()
    {
        Crash.Bundles.WriteSummary(Dir, NativeSample());
        Directory.Delete(Dir, recursive: true);

        Crash.Bundles.WriteSend(Dir, new Crash.SendRecord(Crash.SendState.Sent, "2026-09-24T14:35:00Z", null, 1, false));

        Assert.False(Directory.Exists(Dir));
    }

    [Fact]
    public void WriteSend_still_records_into_a_bundle_that_exists()
    {
        Crash.Bundles.WriteSummary(Dir, NativeSample());
        var queued = new Crash.SendRecord(Crash.SendState.Queued, null, "offline", 2, true);

        Crash.Bundles.WriteSend(Dir, queued);

        Assert.Equal(queued, Crash.Bundles.ReadSend(Dir));
    }

    [Fact]
    public void UpdateSummary_round_trips_the_childs_fields_and_keeps_everything_else()
    {
        var original = NativeSample();
        Crash.Bundles.WriteSummary(Dir, original);

        Assert.True(Crash.Bundles.UpdateSummary(Dir, s => s with { HasDump = true, DumpBytes = 4096, FaultModule = "ntdll.dll", FaultOffset = 0x1a2b }));

        var back = Crash.Bundles.ReadSummary(Dir)!;
        Assert.True(back.HasDump);
        Assert.Equal(4096, back.DumpBytes);
        Assert.Equal("ntdll.dll", back.FaultModule);
        Assert.Equal(0x1a2b, back.FaultOffset);
        Assert.Equal(original with { HasDump = true, DumpBytes = 4096, FaultModule = "ntdll.dll", FaultOffset = 0x1a2b, Rvas = back.Rvas }, back);
        Assert.False(File.Exists(Path.Combine(Dir, Crash.Files.SummaryName + ".tmp")));
        Assert.NotNull(Crash.Bundles.Read(Dir));   // still a bundle the Reports list reads
    }

    [Fact]
    public void UpdateSummary_on_a_deleted_bundle_answers_false_and_creates_nothing()
    {
        Crash.Bundles.WriteSummary(Dir, NativeSample());
        Directory.Delete(Dir, recursive: true);

        Assert.False(Crash.Bundles.UpdateSummary(Dir, s => s with { HasDump = true }));
        Assert.False(Directory.Exists(Dir));
    }

    [Fact]
    public void UpdateSummary_without_a_summary_answers_false_and_writes_none()
    {
        Directory.CreateDirectory(Dir);

        Assert.False(Crash.Bundles.UpdateSummary(Dir, s => s with { HasDump = true }));
        Assert.False(File.Exists(Path.Combine(Dir, Crash.Files.SummaryName)));
        Assert.Null(Crash.Bundles.ReadSummary(Dir));
    }

    [Fact]
    public void AppendReportLine_adds_its_own_paragraph_after_the_frames()
    {
        Directory.CreateDirectory(Dir);
        Crash.Bundles.WriteReport(Dir, "native fault 0xc0000005 at 0x7000a000\n\nFrames (RVA)\n------------\n0x500\n");

        Crash.Bundles.AppendReportLine(Dir, "fault=ntdll.dll+0x1a2b");

        string text = File.ReadAllText(Path.Combine(Dir, Crash.Files.ReportName));
        Assert.EndsWith("0x500\n\nfault=ntdll.dll+0x1a2b\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendReportLine_on_a_deleted_bundle_is_a_no_op()
    {
        Crash.Bundles.AppendReportLine(Dir, "fault=ntdll.dll+0x1a2b");
        Assert.False(Directory.Exists(Dir));
    }
}
