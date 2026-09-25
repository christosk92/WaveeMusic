// ── Wavee.Tests/CrashCoreTests.cs — Platform/Crash.cs (CORE, WP-A) ─────────────────────────────────────────────────
//
// The crash & diagnostics pipeline's engine-free core (docs/plans/wavee/crash-diagnostics-implementation.md §B.2,
// §B.3, §B.7, §B.8, §I): the Summary/SendRecord JSON round trip, Files' bundle-name arithmetic and pruner, PeDebugId's
// hand-rolled PE debug-directory reader, HangRules, ConsentPolicy, RecoveryPolicy and InstallId. Pure: no disk beyond
// an in-memory Stream, no process, no window — every fact here is a table, not an eyeball check.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Wavee.Tests;

// ── 1. Summary + SendRecord JSON round trip ─────────────────────────────────────────────────────────────────────────

public class CrashJsonTests
{
    static Crash.Summary Sample() => new(
        ReportId: "3f9c2b1a", InstallId: "8a1d9e0f", Kind: Crash.Kind.Managed, StampUtc: "2026-09-24T14:30:12.118Z",
        Version: "0.3.0", Quad: "0.3.0.41", Commit: "7e209e37", Channel: "stable", Arch: "arm64", OsBuild: "26100",
        Gpu: "NVIDIA GeForce RTX", GpuTier: "Strong", SoftwareAdapter: false, Packaged: true, Locale: "en-US",
        SessionId: "sess-1", UptimeMs: 12_345, BeforeFirstFrame: false, LastRoute: "artist:abc",
        ExceptionType: "System.InvalidOperationException", ExceptionMessage: "--crash-probe",
        Rvas: [0x7b1fc6, 0x10], ModuleBase: 0x140000000, ModuleSize: 0x2000000,
        DebugId: "7E2C1B4A-0000-0000-0000-0000000000AA-1", ExitCode: 0, HasDump: true, DumpBytes: 4_200_000);

    [Fact]
    public void A_summary_round_trips_through_the_source_generated_context()
    {
        var original = Sample();
        string json = JsonSerializer.Serialize(original, Crash.CrashJson.Default.Summary);
        var back = JsonSerializer.Deserialize(json, Crash.CrashJson.Default.Summary)!;
        // Records compare an array field by reference, not by content — check Rvas structurally, then let the
        // record's own Equals cover every other field (rebinding Rvas to the same array reference).
        Assert.Equal(original.Rvas, back.Rvas);
        Assert.Equal(original, back with { Rvas = original.Rvas });
    }

    [Fact]
    public void The_wire_shape_is_camelCase()
    {
        string json = JsonSerializer.Serialize(Sample(), Crash.CrashJson.Default.Summary);
        Assert.Contains("\"reportId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"installId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"exceptionMessage\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hasDump\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ReportId\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Crash.SendState.NotSent)]
    [InlineData(Crash.SendState.Queued)]
    [InlineData(Crash.SendState.Sent)]
    [InlineData(Crash.SendState.Failed)]
    public void A_send_record_round_trips_for_every_state(Crash.SendState state)
    {
        var original = new Crash.SendRecord(state, state == Crash.SendState.Sent ? "2026-09-24T14:35:00Z" : null,
            state == Crash.SendState.Failed ? "503" : null, Attempts: 3, DumpIncluded: true);
        string json = JsonSerializer.Serialize(original, Crash.CrashJson.Default.SendRecord);
        Assert.Contains("\"dumpIncluded\"", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(json, Crash.CrashJson.Default.SendRecord)!;
        Assert.Equal(original, back);
    }
}

// ── 2. Files: naming, parsing, pruning ──────────────────────────────────────────────────────────────────────────────

public class CrashFilesTests
{
    [Theory]
    [InlineData(Crash.Kind.Managed, "managed")]
    [InlineData(Crash.Kind.Native, "native")]
    [InlineData(Crash.Kind.Hang, "hang")]
    [InlineData(Crash.Kind.ExitCode, "exitcode")]
    [InlineData(Crash.Kind.UncleanExit, "uncleanexit")]
    public void A_bundle_name_round_trips_its_stamp_and_kind(Crash.Kind kind, string token)
    {
        var at = new DateTimeOffset(2026, 9, 24, 14, 30, 12, 118, TimeSpan.Zero);
        string name = Crash.Files.BundleName(at, kind);
        Assert.Equal("20260924-143012-118-" + token, name);

        Assert.True(Crash.Files.TryParse(name, out var stamp, out var parsedKind));
        Assert.Equal(new DateTime(2026, 9, 24, 14, 30, 12, 118), stamp);
        Assert.Equal(kind, parsedKind);
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("20260924-143012-118-")]
    [InlineData("20260924-143012-118-sparkle")]
    [InlineData("20260924-143012-managed")]
    [InlineData("")]
    [InlineData("outbox")]
    public void Garbage_names_never_parse(string name) => Assert.False(Crash.Files.TryParse(name, out _, out _));

    [Fact]
    public void Newest_bundle_name_sorts_first()
    {
        var names = new[]
        {
            @"C:\logs\crash\20260101-000000-000-managed", @"C:\logs\crash\20260924-143012-118-hang",
            @"C:\logs\crash\20260501-090000-004-exitcode",
        };
        Array.Sort(names, Crash.Files.NewestFirst);
        Assert.EndsWith("20260924-143012-118-hang", names[0], StringComparison.Ordinal);
        Assert.EndsWith("20260101-000000-000-managed", names[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Root_and_outbox_nest_under_the_log_folder()
    {
        Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Local\Wavee\logs", "crash"), Crash.Files.Root(@"C:\Users\x\AppData\Local\Wavee\logs"));
        Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Local\Wavee\logs", "crash", "outbox"), Crash.Files.OutboxDir(@"C:\Users\x\AppData\Local\Wavee\logs"));
    }

    static (string Path, long Bytes)[] NewestFirstOf(int count, long bytesEach) =>
        Enumerable.Range(0, count).Select(i => ($@"C:\crash\bundle-{count - i:D3}", bytesEach)).ToArray();

    [Fact]
    public void Prune_keeps_at_most_ten_bundles_regardless_of_size()
    {
        var bundles = NewestFirstOf(12, bytesEach: 1_000);   // well under MaxFolderBytes
        var toDelete = Crash.Files.Prune(bundles, Crash.Files.KeepBundles, Crash.Files.MaxFolderBytes);
        Assert.Equal(2, toDelete.Count);
        Assert.Equal(bundles[10].Path, toDelete[0]);
        Assert.Equal(bundles[11].Path, toDelete[1]);
    }

    [Fact]
    public void Prune_drops_the_oldest_kept_bundles_until_the_total_is_within_budget()
    {
        // 5 bundles, 60 MB each = 300 MB, well within the "keep 10" count cap but over a 200 MB budget.
        var bundles = NewestFirstOf(5, bytesEach: 60L << 20);
        var toDelete = Crash.Files.Prune(bundles, keep: 10, maxBytes: 200L << 20);
        // 300 - 60 = 240 (still over) ; 240 - 60 = 180 (fits) -> drop the two OLDEST (indices 4 then 3).
        Assert.Equal(new[] { bundles[4].Path, bundles[3].Path }, toDelete);
    }

    [Fact]
    public void Prune_combines_the_count_cap_and_the_byte_budget()
    {
        var bundles = NewestFirstOf(12, bytesEach: 30L << 20);   // 12 * 30 MB = 360 MB
        var toDelete = Crash.Files.Prune(bundles, Crash.Files.KeepBundles, maxBytes: 200L << 20);
        // beyond-keep: indices 10, 11 first; the kept ten total 300 MB, so oldest-first (index 9, 8, 7, 6)
        // are then dropped until the running total of the KEPT set is <= 200 MB (300 -30*4=180).
        Assert.Equal(new[] { bundles[10].Path, bundles[11].Path, bundles[9].Path, bundles[8].Path, bundles[7].Path, bundles[6].Path }, toDelete);
    }

    [Fact]
    public void Prune_deletes_nothing_when_everything_already_fits()
    {
        var bundles = NewestFirstOf(3, bytesEach: 10);
        Assert.Empty(Crash.Files.Prune(bundles, Crash.Files.KeepBundles, Crash.Files.MaxFolderBytes));
    }
}

// ── 3. PeDebugId: a minimal in-memory PE ────────────────────────────────────────────────────────────────────────────

public class PeDebugIdTests
{
    /// <summary>A minimal PE32+ image with one section and one CodeView (RSDS) debug directory entry — just enough
    /// bytes for <see cref="Crash.PeDebugId.TryRead"/> to walk: DOS stub (64) · "PE\0\0" · COFF (1 section) ·
    /// optional header (PE32+, 7 data directories, only #6/Debug non-zero) · one section header · the
    /// IMAGE_DEBUG_DIRECTORY entry · the RSDS blob.</summary>
    static byte[] BuildMinimalPe(Guid guid, uint age, string pdbPath)
    {
        const int DosSize = 64, CoffSize = 20, OptSize = 168, SectionSize = 40, DebugDirSize = 28;
        const uint DebugRva = 0x2000;

        byte[] pdbBytes = Encoding.UTF8.GetBytes(pdbPath);
        int cvSize = 4 + 16 + 4 + pdbBytes.Length + 1;   // "RSDS" + guid + age + NUL-terminated path

        int peOff = DosSize;
        int coffOff = peOff + 4;
        int optOff = coffOff + CoffSize;
        int sectionOff = optOff + OptSize;
        int debugDirOff = sectionOff + SectionSize;
        int cvOff = debugDirOff + DebugDirSize;
        int total = cvOff + cvSize;

        byte[] buf = new byte[total];
        buf[0] = (byte)'M'; buf[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0x3C, 4), peOff);

        buf[peOff] = (byte)'P'; buf[peOff + 1] = (byte)'E'; buf[peOff + 2] = 0; buf[peOff + 3] = 0;

        var coff = buf.AsSpan(coffOff, CoffSize);
        BinaryPrimitives.WriteUInt16LittleEndian(coff.Slice(2, 2), 1);              // NumberOfSections
        BinaryPrimitives.WriteUInt16LittleEndian(coff.Slice(16, 2), (ushort)OptSize); // SizeOfOptionalHeader

        var opt = buf.AsSpan(optOff, OptSize);
        BinaryPrimitives.WriteUInt16LittleEndian(opt.Slice(0, 2), 0x20B);   // PE32+
        BinaryPrimitives.WriteUInt64LittleEndian(opt.Slice(24, 8), 0x1_4000_0000);  // ImageBase
        BinaryPrimitives.WriteUInt32LittleEndian(opt.Slice(108, 4), 7);      // NumberOfRvaAndSizes (0..6)
        // DataDirectory[6] = Debug, at 112 + 6*8 = 160
        BinaryPrimitives.WriteUInt32LittleEndian(opt.Slice(160, 4), DebugRva);
        BinaryPrimitives.WriteUInt32LittleEndian(opt.Slice(164, 4), DebugDirSize);

        var section = buf.AsSpan(sectionOff, SectionSize);
        Encoding.ASCII.GetBytes(".rdata\0\0").AsSpan().CopyTo(section.Slice(0, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(section.Slice(8, 4), 0x1000);       // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(section.Slice(12, 4), DebugRva);    // VirtualAddress
        BinaryPrimitives.WriteUInt32LittleEndian(section.Slice(16, 4), 0x1000);      // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(section.Slice(20, 4), (uint)debugDirOff); // PointerToRawData

        var debugDir = buf.AsSpan(debugDirOff, DebugDirSize);
        BinaryPrimitives.WriteUInt32LittleEndian(debugDir.Slice(12, 4), 2);          // Type = CODEVIEW
        BinaryPrimitives.WriteUInt32LittleEndian(debugDir.Slice(16, 4), (uint)cvSize); // SizeOfData
        BinaryPrimitives.WriteUInt32LittleEndian(debugDir.Slice(24, 4), (uint)cvOff); // PointerToRawData

        var cv = buf.AsSpan(cvOff, cvSize);
        cv[0] = (byte)'R'; cv[1] = (byte)'S'; cv[2] = (byte)'D'; cv[3] = (byte)'S';
        guid.ToByteArray().CopyTo(cv.Slice(4, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(cv.Slice(20, 4), age);
        pdbBytes.AsSpan().CopyTo(cv.Slice(24, pdbBytes.Length));
        // trailing byte is the NUL terminator (buf is zero-initialized)

        return buf;
    }

    [Fact]
    public void Reads_the_rsds_guid_age_and_pdb_path_from_a_well_formed_pe()
    {
        var guid = Guid.Parse("7E2C1B4A-1234-4567-89AB-0011223344FF");
        byte[] pe = BuildMinimalPe(guid, age: 1, pdbPath: @"C:\build\Wavee.pdb");

        Assert.True(Crash.PeDebugId.TryRead(new MemoryStream(pe), out string debugId, out string pdbName));
        Assert.Equal(guid.ToString("D").ToUpperInvariant() + "-1", debugId);
        Assert.Equal(@"C:\build\Wavee.pdb", pdbName);
    }

    [Fact]
    public void A_different_age_changes_only_the_suffix()
    {
        var guid = Guid.Parse("00000000-0000-0000-0000-000000000001");
        byte[] pe = BuildMinimalPe(guid, age: 42, pdbPath: "a.pdb");
        Assert.True(Crash.PeDebugId.TryRead(new MemoryStream(pe), out string debugId, out _));
        Assert.EndsWith("-42", debugId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(80)]
    public void A_truncated_stream_never_throws_and_returns_false(int keepBytes)
    {
        byte[] pe = BuildMinimalPe(Guid.NewGuid(), 1, "a.pdb");
        byte[] truncated = pe.AsSpan(0, Math.Min(keepBytes, pe.Length)).ToArray();
        Assert.False(Crash.PeDebugId.TryRead(new MemoryStream(truncated), out string debugId, out string pdbName));
        Assert.Equal("", debugId);
        Assert.Equal("", pdbName);
    }

    [Fact]
    public void Garbage_that_is_not_a_pe_at_all_returns_false()
    {
        byte[] garbage = new byte[128];
        new Random(1).NextBytes(garbage);
        garbage[0] = 0; garbage[1] = 0;   // make sure it does not accidentally start with "MZ"
        Assert.False(Crash.PeDebugId.TryRead(new MemoryStream(garbage), out _, out _));
    }

    [Fact]
    public void A_null_length_stream_returns_false()
    {
        Assert.False(Crash.PeDebugId.TryRead(new MemoryStream(), out _, out _));
    }
}

// ── 4. HangRules ─────────────────────────────────────────────────────────────────────────────────────────────────────

public class HangRulesTests
{
    const long TicksPerMs = 10_000L;

    [Fact]
    public void The_positive_case_is_hung()
    {
        long now = 100_000 * TicksPerMs;
        long lastBeat = now - (Crash.HangRules.NoBeatMs + 1) * TicksPerMs;
        Assert.True(Crash.HangRules.IsHung(now, lastBeat, hasWindow: true, hungWindowMs10s: true,
            debuggerAttached: false, modalPump: false, exiting: false, suspendEpochAtBeat: 3, suspendEpochNow: 3));
    }

    [Fact]
    public void No_window_is_never_hung() =>
        Assert.False(Crash.HangRules.IsHung(1_000_000, 0, hasWindow: false, hungWindowMs10s: true,
            debuggerAttached: false, modalPump: false, exiting: false, suspendEpochAtBeat: 0, suspendEpochNow: 0));

    [Fact]
    public void A_debugger_suppresses_the_verdict() =>
        Assert.False(Crash.HangRules.IsHung(1_000_000_000, 0, hasWindow: true, hungWindowMs10s: true,
            debuggerAttached: true, modalPump: false, exiting: false, suspendEpochAtBeat: 0, suspendEpochNow: 0));

    [Fact]
    public void A_modal_pump_suppresses_the_verdict() =>
        Assert.False(Crash.HangRules.IsHung(1_000_000_000, 0, hasWindow: true, hungWindowMs10s: true,
            debuggerAttached: false, modalPump: true, exiting: false, suspendEpochAtBeat: 0, suspendEpochNow: 0));

    [Fact]
    public void An_exiting_process_suppresses_the_verdict() =>
        Assert.False(Crash.HangRules.IsHung(1_000_000_000, 0, hasWindow: true, hungWindowMs10s: true,
            debuggerAttached: false, modalPump: false, exiting: true, suspendEpochAtBeat: 0, suspendEpochNow: 0));

    [Fact]
    public void A_suspend_epoch_change_suppresses_the_verdict_a_sleep_never_counts_as_a_hang() =>
        Assert.False(Crash.HangRules.IsHung(1_000_000_000, 0, hasWindow: true, hungWindowMs10s: true,
            debuggerAttached: false, modalPump: false, exiting: false, suspendEpochAtBeat: 1, suspendEpochNow: 2));

    [Fact]
    public void A_gap_shorter_than_the_no_beat_threshold_is_not_a_hang()
    {
        long now = 100_000 * TicksPerMs;
        long lastBeat = now - (Crash.HangRules.NoBeatMs - 1) * TicksPerMs;
        Assert.False(Crash.HangRules.IsHung(now, lastBeat, hasWindow: true, hungWindowMs10s: true,
            debuggerAttached: false, modalPump: false, exiting: false, suspendEpochAtBeat: 0, suspendEpochNow: 0));
    }

    [Fact]
    public void The_window_itself_reading_not_hung_yet_suppresses_the_verdict()
    {
        long now = 100_000 * TicksPerMs;
        long lastBeat = now - (Crash.HangRules.NoBeatMs + 1) * TicksPerMs;
        Assert.False(Crash.HangRules.IsHung(now, lastBeat, hasWindow: true, hungWindowMs10s: false,
            debuggerAttached: false, modalPump: false, exiting: false, suspendEpochAtBeat: 0, suspendEpochNow: 0));
    }
}

// ── 5. ConsentPolicy ─────────────────────────────────────────────────────────────────────────────────────────────────

public class ConsentPolicyTests
{
    [Theory]
    [InlineData(Crash.Reporting.Off, true)]
    [InlineData(Crash.Reporting.Off, false)]
    [InlineData(Crash.Reporting.Ask, true)]
    [InlineData(Crash.Reporting.Ask, false)]
    [InlineData(Crash.Reporting.Auto, true)]
    [InlineData(Crash.Reporting.Auto, false)]
    public void An_unclean_exit_is_never_offered_regardless_of_mode_or_connectivity(Crash.Reporting mode, bool online) =>
        Assert.Equal(Crash.ConsentPolicy.Action.Nothing, Crash.ConsentPolicy.Decide(mode, Crash.Kind.UncleanExit, online));

    [Theory]
    [InlineData(Crash.Kind.Managed)]
    [InlineData(Crash.Kind.Native)]
    [InlineData(Crash.Kind.Hang)]
    [InlineData(Crash.Kind.ExitCode)]
    public void Off_mode_still_prompts_locally_for_every_evidence_kind(Crash.Kind kind)
    {
        Assert.Equal(Crash.ConsentPolicy.Action.Prompt, Crash.ConsentPolicy.Decide(Crash.Reporting.Off, kind, online: true));
        Assert.Equal(Crash.ConsentPolicy.Action.Prompt, Crash.ConsentPolicy.Decide(Crash.Reporting.Off, kind, online: false));
    }

    [Theory]
    [InlineData(Crash.Kind.Managed)]
    [InlineData(Crash.Kind.Native)]
    [InlineData(Crash.Kind.Hang)]
    [InlineData(Crash.Kind.ExitCode)]
    public void Ask_mode_always_prompts(Crash.Kind kind)
    {
        Assert.Equal(Crash.ConsentPolicy.Action.Prompt, Crash.ConsentPolicy.Decide(Crash.Reporting.Ask, kind, online: true));
        Assert.Equal(Crash.ConsentPolicy.Action.Prompt, Crash.ConsentPolicy.Decide(Crash.Reporting.Ask, kind, online: false));
    }

    [Theory]
    [InlineData(Crash.Kind.Managed)]
    [InlineData(Crash.Kind.Native)]
    [InlineData(Crash.Kind.Hang)]
    [InlineData(Crash.Kind.ExitCode)]
    public void Auto_mode_uploads_silently_online_and_toasts_offline(Crash.Kind kind)
    {
        Assert.Equal(Crash.ConsentPolicy.Action.UploadSilently, Crash.ConsentPolicy.Decide(Crash.Reporting.Auto, kind, online: true));
        Assert.Equal(Crash.ConsentPolicy.Action.Toast, Crash.ConsentPolicy.Decide(Crash.Reporting.Auto, kind, online: false));
    }

    [Fact]
    public void A_manual_send_always_allows_the_dump_even_with_reporting_off_or_a_hang()
    {
        Assert.True(Crash.ConsentPolicy.DumpAllowed(Crash.Reporting.Off, Crash.Kind.Hang, includeDump: false, manualSend: true));
        Assert.True(Crash.ConsentPolicy.DumpAllowed(Crash.Reporting.Off, Crash.Kind.Managed, includeDump: false, manualSend: true));
    }

    [Fact]
    public void Reporting_off_never_allows_an_automatic_dump()
    {
        Assert.False(Crash.ConsentPolicy.DumpAllowed(Crash.Reporting.Off, Crash.Kind.Managed, includeDump: true, manualSend: false));
    }

    [Theory]
    [InlineData(Crash.Reporting.Ask)]
    [InlineData(Crash.Reporting.Auto)]
    public void Hang_dumps_are_never_automatic_even_when_include_dump_is_on(Crash.Reporting mode) =>
        Assert.False(Crash.ConsentPolicy.DumpAllowed(mode, Crash.Kind.Hang, includeDump: true, manualSend: false));

    [Theory]
    [InlineData(Crash.Reporting.Ask)]
    [InlineData(Crash.Reporting.Auto)]
    public void Non_hang_dumps_are_allowed_when_the_toggle_is_on(Crash.Reporting mode) =>
        Assert.True(Crash.ConsentPolicy.DumpAllowed(mode, Crash.Kind.Managed, includeDump: true, manualSend: false));

    [Theory]
    [InlineData(Crash.Reporting.Ask)]
    [InlineData(Crash.Reporting.Auto)]
    public void The_dump_toggle_being_off_blocks_an_automatic_dump(Crash.Reporting mode) =>
        Assert.False(Crash.ConsentPolicy.DumpAllowed(mode, Crash.Kind.Managed, includeDump: false, manualSend: false));
}

// ── 6. RecoveryPolicy ────────────────────────────────────────────────────────────────────────────────────────────────

public class RecoveryPolicyTests
{
    [Fact]
    public void The_recovery_switch_always_enters() => Assert.True(Crash.RecoveryPolicy.Enter(recoverySwitch: true, consecutiveBootFailures: 0, bootThrew: false));

    [Fact]
    public void A_thrown_boot_always_enters() => Assert.True(Crash.RecoveryPolicy.Enter(recoverySwitch: false, consecutiveBootFailures: 0, bootThrew: true));

    [Fact]
    public void Two_consecutive_boot_failures_enter() => Assert.True(Crash.RecoveryPolicy.Enter(recoverySwitch: false, consecutiveBootFailures: 2, bootThrew: false));

    [Fact]
    public void One_boot_failure_does_not_enter() => Assert.False(Crash.RecoveryPolicy.Enter(recoverySwitch: false, consecutiveBootFailures: 1, bootThrew: false));

    [Fact]
    public void A_clean_boot_never_enters() => Assert.False(Crash.RecoveryPolicy.Enter(recoverySwitch: false, consecutiveBootFailures: 0, bootThrew: false));

    [Fact]
    public void An_unclean_run_that_never_reached_a_frame_increments_the_counter() =>
        Assert.Equal(2, Crash.RecoveryPolicy.NextBootFailures(RunOutcome.Unclean, previousReachedFirstFrame: false, versionChanged: false, current: 1));

    [Fact]
    public void Reaching_the_first_frame_resets_the_counter_even_on_an_unclean_exit() =>
        Assert.Equal(0, Crash.RecoveryPolicy.NextBootFailures(RunOutcome.Unclean, previousReachedFirstFrame: true, versionChanged: false, current: 5));

    [Fact]
    public void A_version_change_resets_the_counter_an_update_kill_is_never_a_boot_loop() =>
        Assert.Equal(0, Crash.RecoveryPolicy.NextBootFailures(RunOutcome.Unclean, previousReachedFirstFrame: false, versionChanged: true, current: 5));

    [Fact]
    public void A_clean_previous_run_resets_the_counter() =>
        Assert.Equal(0, Crash.RecoveryPolicy.NextBootFailures(RunOutcome.Clean, previousReachedFirstFrame: true, versionChanged: false, current: 5));

    [Fact]
    public void An_unknown_previous_run_resets_the_counter() =>
        Assert.Equal(0, Crash.RecoveryPolicy.NextBootFailures(RunOutcome.Unknown, previousReachedFirstFrame: false, versionChanged: false, current: 5));
}

// ── 7. RunMarker's "frame" value ─────────────────────────────────────────────────────────────────────────────────────

public class RunMarkerFrameTests
{
    [Theory]
    [InlineData(RunMarker.Clean, RunOutcome.Clean, true)]
    [InlineData(RunMarker.Running, RunOutcome.Unclean, false)]
    [InlineData(RunMarker.Crashed, RunOutcome.Unclean, false)]
    [InlineData(RunMarker.Frame, RunOutcome.Unclean, true)]
    [InlineData("", RunOutcome.Unknown, false)]
    public void Begin_reports_the_outcome_and_whether_a_frame_was_reached(string previous, RunOutcome expectedOutcome, bool expectedFrame)
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.RunMarker, previous);
        Assert.Equal(expectedOutcome, RunMarker.Begin(s, out bool reachedFirstFrame));
        Assert.Equal(expectedFrame, reachedFirstFrame);
        Assert.Equal(RunMarker.Running, s.Get(Platform.Keys.RunMarker));
    }

    [Fact]
    public void The_single_arg_overload_behaves_the_same_as_before()
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.RunMarker, RunMarker.Frame);
        Assert.Equal(RunOutcome.Unclean, RunMarker.Begin(s));
    }

    [Fact]
    public void MarkFrame_upgrades_a_running_marker_to_frame()
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.RunMarker, RunMarker.Running);
        RunMarker.MarkFrame(s);
        Assert.Equal(RunMarker.Frame, s.Get(Platform.Keys.RunMarker));
    }

    [Fact]
    public void MarkFrame_never_stomps_a_crashed_marker()
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.RunMarker, RunMarker.Crashed);
        RunMarker.MarkFrame(s);
        Assert.Equal(RunMarker.Crashed, s.Get(Platform.Keys.RunMarker));
    }

    [Fact]
    public void MarkFrame_is_a_no_op_once_already_frame_or_clean()
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.RunMarker, RunMarker.Clean);
        RunMarker.MarkFrame(s);
        Assert.Equal(RunMarker.Clean, s.Get(Platform.Keys.RunMarker));
    }

    [Fact]
    public void End_downgrades_either_running_or_frame_to_clean()
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.RunMarker, RunMarker.Running);
        RunMarker.End(s);
        Assert.Equal(RunMarker.Clean, s.Get(Platform.Keys.RunMarker));

        s.Set(Platform.Keys.RunMarker, RunMarker.Frame);
        RunMarker.End(s);
        Assert.Equal(RunMarker.Clean, s.Get(Platform.Keys.RunMarker));
    }

    [Fact]
    public void End_never_stomps_a_crashed_marker()
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.RunMarker, RunMarker.Crashed);
        RunMarker.End(s);
        Assert.Equal(RunMarker.Crashed, s.Get(Platform.Keys.RunMarker));
    }
}

// ── 8. InstallId ─────────────────────────────────────────────────────────────────────────────────────────────────────

public class InstallIdTests
{
    [Fact]
    public void The_first_call_generates_a_guid_n_and_every_later_call_returns_the_same_value()
    {
        var s = new MemoryAppSettings();
        Assert.False(s.WasWritten(Platform.Keys.CrashInstallId));

        string id = Crash.InstallId.Ensure(s);
        Assert.Equal(32, id.Length);
        Assert.True(Guid.TryParseExact(id, "N", out _));
        Assert.True(s.WasWritten(Platform.Keys.CrashInstallId));

        Assert.Equal(id, Crash.InstallId.Ensure(s));
        Assert.Equal(id, s.Get(Platform.Keys.CrashInstallId));
    }

    [Fact]
    public void An_already_stored_id_is_returned_untouched()
    {
        var s = new MemoryAppSettings();
        s.Set(Platform.Keys.CrashInstallId, "deadbeefdeadbeefdeadbeefdeadbeef");
        Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeef", Crash.InstallId.Ensure(s));
    }
}
