// ── Wavee.Tests/CrashContractTests.cs — the app ↔ Worker multipart contract, byte for byte (#165) ─────────────────────
//
// docs/plans/wavee/crash-production-readiness-implementation.md W3b "Contract test": two fixed bundles (a managed crash,
// and a native access violation with a 64-byte "MDMP" dump) are packed exactly as Crash.Uploader packs a report —
// Bundle.Pack under Bundle.CapBytes with Bundle.BoundaryFor(reportId) — and compared with ops/crash/contract/
// <name>.multipart, the SAME files the Worker's vitest suite parses (linked into Fixtures/crash-contract/ by
// Wavee.Tests.csproj). A wire change on either side therefore fails on both.
//
// On a missing or different fixture the packer's bytes are written to crash-contract-actual/<name>.multipart under the
// test output folder and the test fails with what to copy where — an intended wire change is reviewed and copied by
// hand, never regenerated silently. The summary part is indented JSON, so the fixtures carry this platform's newlines
// (CRLF: the suite runs on Windows only).

using Wavee;
using Xunit;
using static Wavee.Crash;

namespace Wavee.Tests;

public class CrashContractTests
{
    const string ReportId = "3f9c2b1a8d7e4c60a1b2c3d4e5f60718";
    const string InstallId = "8a1d9e0f7b6c4d3e2f1a0b9c8d7e6f50";

    static Summary Base(Kind kind) => new(
        ReportId: ReportId, InstallId: InstallId, Kind: kind, StampUtc: "2026-10-02T09:15:30.250Z",
        Version: "1.2.1011", Quad: "1.2.1011.0", Commit: "86bf1105", Channel: "store", Arch: "arm64", OsBuild: "26100",
        Gpu: "Qualcomm(R) Adreno(TM) X1-85 GPU", GpuTier: "Strong", SoftwareAdapter: false, Packaged: true, Locale: "en-US",
        SessionId: "20261002-091458", UptimeMs: 31_250, BeforeFirstFrame: false, LastRoute: "album",
        ExceptionType: "System.InvalidOperationException", ExceptionMessage: "--crash-probe throw",
        Rvas: [0x7b1fc6, 0x7b2004, 0x10a3c0], ModuleBase: 0x7ff6_1000_0000, ModuleSize: 0x0200_0000,
        DebugId: "7E2C1B4A-5D3F-4E2A-9B1C-0D8E7F6A5B4C-1", ExitCode: 0, HasDump: false, DumpBytes: 0);

    static Summary ManagedSummary() => Base(Kind.Managed);

    static Summary NativeSummary() => Base(Kind.Native) with
    {
        ExceptionType = "Native",
        ExceptionMessage = "native fault 0xc0000005 at 0x7ffb12341234",
        Rvas = [0x7b1fc6, 0x7b2004],
        HasDump = true,
        DumpBytes = 64,
        ExceptionCode = 0xC0000005,
        FaultModule = "ntdll.dll",
        FaultOffset = 0x1234,
    };

    /// <summary>64 bytes that start like a real minidump (signature "MDMP", MINIDUMP_VERSION 0xA793), then a fixed ramp.</summary>
    static byte[] Dump()
    {
        var d = new byte[64];
        "MDMP"u8.CopyTo(d);
        d[4] = 0x93; d[5] = 0xA7;
        for (int i = 8; i < d.Length; i++) d[i] = (byte)i;
        return d;
    }

    const string ReportTxt =
        "Wavee 1.2.1011 (1.2.1011.0) · 86bf1105 · arm64 · store\n" +
        "kind=managed stamp=2026-10-02T09:15:30.250Z\n" +
        "System.InvalidOperationException: --crash-probe throw\n" +
        "\n" +
        "Frames (RVA)\n" +
        "  0x7b1fc6\n" +
        "  0x7b2004\n" +
        "  0x10a3c0\n";

    const string TailTxt =
        "2026-10-02 09:15:29.880 INF app boot.pages\n" +
        "2026-10-02 09:15:30.120 INF nav route=album\n" +
        "2026-10-02 09:15:30.240 WRN crash probe=throw\n";

    [Theory]
    [InlineData("managed")]
    [InlineData("native")]
    public void Packed_bundle_matches_the_contract_fixture(string name)
    {
        bool native = name == "native";
        Summary summary = native ? NativeSummary() : ManagedSummary();
        var scrubbed = new ScrubbedBundle(summary, ReportTxt, TailTxt);
        ReadOnlyMemory<byte>? dump = native ? new ReadOnlyMemory<byte>(Dump()) : null;

        byte[] actual = Bundle.Pack(scrubbed, dump, Bundle.CapBytes, Bundle.BoundaryFor(summary.ReportId), out bool dumpDropped);
        Assert.False(dumpDropped);

        AssertMatchesFixture(name, actual);
    }

    static void AssertMatchesFixture(string name, byte[] actual)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "crash-contract", name + ".multipart");
        byte[]? expected = File.Exists(fixture) ? File.ReadAllBytes(fixture) : null;
        if (expected is not null && expected.AsSpan().SequenceEqual(actual)) return;

        string outDir = Path.Combine(AppContext.BaseDirectory, "crash-contract-actual");
        Directory.CreateDirectory(outDir);
        string actualPath = Path.Combine(outDir, name + ".multipart");
        File.WriteAllBytes(actualPath, actual);

        string target = "ops/crash/contract/" + name + ".multipart";
        Assert.Fail(expected is null
            ? target + " is missing. The packer's bytes were written to " + actualPath + " — review them, copy that file to "
              + target + ", and run the Worker's contract tests (npm test in ops/crash/worker) against it."
            : target + " no longer matches what the app sends (first difference at byte " + FirstDifference(expected, actual)
              + ", fixture " + expected.Length + " bytes, actual " + actual.Length + "). The packer's bytes were written to "
              + actualPath + ". If the wire change is intended, copy that file to " + target
              + " and make the Worker accept it (npm test in ops/crash/worker); otherwise the app broke the ingest contract.");
    }

    static int FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return n;
    }
}
