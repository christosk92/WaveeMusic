// ── Wavee.Tests/CrashBundleTests.cs — Platform/Crash.Upload.cs (WP-C, packer + uploader) ───────────────────────────────
//
// docs/plans/wavee/crash-diagnostics-implementation.md §I ("Worker ingest contract"), §J (erasure): `Crash.Bundle.Pack`
// is pure and deterministic for a fixed boundary — every byte of the multipart framing is pinned here, the dump-cap
// drop is pinned, and the JSON part is proven to deserialize back into `Crash.Summary`. `Crash.Uploader.EraseUrl` is
// the one piece of the right-to-erasure client that needs no network to test. No disk, no HTTP, no process. The upload
// decisions live in CrashUploadPolicyTests; the byte-exact Worker contract in CrashContractTests.

using System.Text;
using System.Text.Json;
using Wavee;
using Xunit;
using static Wavee.Crash;

namespace Wavee.Tests;

public class CrashBundleTests
{
    static Summary MakeSummary() => new(
        ReportId: "3f9c2b1a", InstallId: "8a1d9e0f", Kind: Kind.Managed, StampUtc: "2026-09-24T14:30:12.118Z",
        Version: "0.3.0", Quad: "0.3.0.41", Commit: "7e209e37", Channel: "stable", Arch: "arm64", OsBuild: "26100",
        Gpu: "NVIDIA GeForce RTX", GpuTier: "Strong", SoftwareAdapter: false, Packaged: true, Locale: "en-US",
        SessionId: "sess-1", UptimeMs: 12_345, BeforeFirstFrame: false, LastRoute: "artist:abc",
        ExceptionType: "System.InvalidOperationException", ExceptionMessage: "--crash-probe",
        Rvas: [0x7b1fc6, 0x10], ModuleBase: 0x140000000, ModuleSize: 0x2000000,
        DebugId: "7E2C1B4A-1", ExitCode: 0, HasDump: false, DumpBytes: 0);

    // ── byte-exact multipart framing ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pack_is_byte_exact_multipart_framing_for_a_fixed_boundary()
    {
        var summary = MakeSummary();
        var scrubbed = new ScrubbedBundle(summary, "hello report\nsecond line", "tail one\ntail two");

        byte[] actual = Bundle.Pack(scrubbed, dump: null, capBytes: Bundle.CapBytes, boundary: "TESTBOUNDARY", out bool dumpDropped);
        Assert.False(dumpDropped);

        string text = Encoding.UTF8.GetString(actual);

        const string summaryHeader = "--TESTBOUNDARY\r\n" +
            "Content-Disposition: form-data; name=\"summary\"\r\n" +
            "Content-Type: application/json\r\n\r\n";
        Assert.StartsWith(summaryHeader, text, StringComparison.Ordinal);

        const string afterSummaryMarker = "\r\n--TESTBOUNDARY\r\n" +
            "Content-Disposition: form-data; name=\"report\"\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n";
        int jsonStart = summaryHeader.Length;
        int jsonEnd = text.IndexOf(afterSummaryMarker, jsonStart, StringComparison.Ordinal);
        Assert.True(jsonEnd > jsonStart, "the report part header must follow the summary part");
        string json = text[jsonStart..jsonEnd];

        string expectedFromReportOn = afterSummaryMarker + scrubbed.ReportTxt + "\r\n" +
            "--TESTBOUNDARY\r\n" +
            "Content-Disposition: form-data; name=\"tail\"\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n" +
            scrubbed.TailTxt + "\r\n" +
            "--TESTBOUNDARY--\r\n";
        Assert.Equal(expectedFromReportOn, text[jsonEnd..]);

        // The JSON part deserializes back through the SAME source-generated context Pack writes with (camelCase,
        // string enums) — the one the Worker validates against.
        var roundTripped = JsonSerializer.Deserialize(json, Crash.CrashJson.Default.Summary);
        Assert.NotNull(roundTripped);
        Assert.Equal(summary.ReportId, roundTripped!.ReportId);
        Assert.Equal(summary.InstallId, roundTripped.InstallId);
        Assert.Equal(summary.Kind, roundTripped.Kind);
        Assert.Equal(summary.ExceptionType, roundTripped.ExceptionType);
        Assert.Equal(summary.ExceptionMessage, roundTripped.ExceptionMessage);
        Assert.Equal(summary.Rvas, roundTripped.Rvas);
        Assert.Equal(summary.DebugId, roundTripped.DebugId);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("reportId", out _), "the wire shape is camelCase");
        Assert.False(doc.RootElement.TryGetProperty("ReportId", out _));
    }

    [Fact]
    public void BoundaryFor_is_one_fixed_boundary_per_report_id()
    {
        // The outbox body is packed at enqueue time and its Content-Type header is written at drain time — both derive
        // the boundary from the report id alone, so they can never disagree (and the contract fixtures reproduce).
        Assert.Equal("wavee-3f9c2b1a", Bundle.BoundaryFor("3f9c2b1a"));
        Assert.Equal(Bundle.BoundaryFor("abc"), Bundle.BoundaryFor("abc"));
        Assert.NotEqual(Bundle.BoundaryFor("abc"), Bundle.BoundaryFor("abd"));
    }

    [Fact]
    public void Pack_is_deterministic_for_the_same_boundary_and_input()
    {
        var scrubbed = new ScrubbedBundle(MakeSummary(), "same report", "same tail");
        byte[] a = Bundle.Pack(scrubbed, null, Bundle.CapBytes, "FIXED", out _);
        byte[] b = Bundle.Pack(scrubbed, null, Bundle.CapBytes, "FIXED", out _);
        Assert.Equal(a, b);
    }

    // ── the dump: included, or dropped over cap ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dump_is_included_with_its_filename_when_it_fits_under_the_cap()
    {
        var scrubbed = new ScrubbedBundle(MakeSummary(), "report", "tail");
        byte[] dump = [1, 2, 3, 4, 5, 250, 251, 252];

        byte[] actual = Bundle.Pack(scrubbed, dump, capBytes: 1_000_000, boundary: "B1", out bool dumpDropped);

        Assert.False(dumpDropped);
        string text = Encoding.Latin1.GetString(actual);   // byte-preserving for the framing text around the raw dump
        Assert.Contains("Content-Disposition: form-data; name=\"dump\"; filename=\"minidump.dmp\"", text, StringComparison.Ordinal);
        Assert.Contains("Content-Type: application/octet-stream", text, StringComparison.Ordinal);
        Assert.True(ContainsBytes(actual, dump), "the raw dump bytes must appear verbatim");
    }

    [Fact]
    public void Dump_is_dropped_when_it_would_push_the_body_over_the_cap()
    {
        var scrubbed = new ScrubbedBundle(MakeSummary(), "report", "tail");
        byte[] withoutDump = Bundle.Pack(scrubbed, null, Bundle.CapBytes, "B2", out bool droppedWhenAbsent);
        Assert.False(droppedWhenAbsent);

        byte[] bigDump = new byte[4096];
        new Random(42).NextBytes(bigDump);
        long tooSmallCap = withoutDump.LongLength + 10;   // room for the no-dump body, not for 4 KB more

        byte[] actual = Bundle.Pack(scrubbed, bigDump, capBytes: tooSmallCap, boundary: "B2", out bool dumpDropped);

        Assert.True(dumpDropped);
        Assert.Equal(withoutDump, actual);
        string text = Encoding.UTF8.GetString(actual);
        Assert.DoesNotContain("name=\"dump\"", text, StringComparison.Ordinal);
        Assert.False(ContainsBytes(actual, bigDump));
    }

    [Fact]
    public void No_dump_offered_means_dumpDropped_is_false_not_true()
    {
        var scrubbed = new ScrubbedBundle(MakeSummary(), "report", "tail");
        _ = Bundle.Pack(scrubbed, dump: null, capBytes: 1, boundary: "B3", out bool dumpDropped);
        // capBytes: 1 is absurdly small, but there was never a dump to drop in the first place.
        Assert.False(dumpDropped);
    }

    static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return true;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    // ── §J erasure: the request builder, without touching the network ──────────────────────────────────────────────────

    [Theory]
    [InlineData("https://crash.wavee.app", "abc123", "https://crash.wavee.app/v1/installs/abc123")]
    [InlineData("https://crash.wavee.app/", "abc123", "https://crash.wavee.app/v1/installs/abc123")]
    [InlineData("https://crash.wavee.app///", "abc123", "https://crash.wavee.app/v1/installs/abc123")]
    [InlineData("https://crash.wavee.app", "8a1d9e0f8a1d9e0f8a1d9e0f8a1d9e0f", "https://crash.wavee.app/v1/installs/8a1d9e0f8a1d9e0f8a1d9e0f8a1d9e0f")]
    public void EraseUrl_builds_the_installs_delete_endpoint(string ingestBaseUrl, string installId, string expected)
        => Assert.Equal(expected, Uploader.EraseUrl(ingestBaseUrl, installId));

    [Fact]
    public void EraseUrl_percent_encodes_an_install_id_that_is_not_a_plain_token()
    {
        string url = Uploader.EraseUrl("https://crash.wavee.app", "an id/with slash");
        Assert.Equal("https://crash.wavee.app/v1/installs/an%20id%2Fwith%20slash", url);
    }
}
