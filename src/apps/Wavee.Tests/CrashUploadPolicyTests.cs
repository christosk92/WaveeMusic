// ── Wavee.Tests/CrashUploadPolicyTests.cs — Platform/Crash.Upload.cs's pure decisions (#165) ────────────────────────
//
// docs/plans/wavee/crash-production-readiness-implementation.md W3b + appendix A4: every HTTP answer the uploader gets
// goes Classify → Next, the outbox backoff is Due/Backoff, each request is timed by Timeout, a deleted report's entry is
// an orphan, and the toast a state earns is UploadToasts.For. Plus the outbox sidecar's round trip, the short id and
// the build-stamp gate (WaveeVersionInfo.CrashReportingAvailable). Pure: no disk, no HTTP, no engine.

using Wavee;
using Xunit;
using static Wavee.Crash;

namespace Wavee.Tests;

public class CrashUploadPolicyTests
{
    // ── Classify ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(200, UploadOutcome.Sent)]
    [InlineData(201, UploadOutcome.Sent)]
    [InlineData(204, UploadOutcome.Sent)]
    [InlineData(299, UploadOutcome.Sent)]
    [InlineData(409, UploadOutcome.AlreadySent)]
    [InlineData(410, UploadOutcome.Erased)]
    [InlineData(429, UploadOutcome.RetryServer)]
    [InlineData(500, UploadOutcome.RetryServer)]
    [InlineData(502, UploadOutcome.RetryServer)]
    [InlineData(503, UploadOutcome.RetryServer)]
    [InlineData(400, UploadOutcome.Rejected)]
    [InlineData(401, UploadOutcome.Rejected)]
    [InlineData(404, UploadOutcome.Rejected)]
    [InlineData(413, UploadOutcome.Rejected)]
    [InlineData(301, UploadOutcome.Rejected)]
    public void Classify_maps_every_status(int status, UploadOutcome expected)
        => Assert.Equal(expected, UploadPolicy.Classify(status));

    [Fact]
    public void Classify_no_answer_is_a_network_retry()
        => Assert.Equal(UploadOutcome.RetryNetwork, UploadPolicy.Classify(null));

    // ── Next ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Next_sent_and_already_sent_both_settle_as_sent_and_drop_the_entry()
    {
        var expected = new UploadPolicy.Step(true, SendState.Sent, null);
        Assert.Equal(expected, UploadPolicy.Next(UploadOutcome.Sent, 0, "HTTP 201"));
        // 409: the service already holds this report id (a second drain, a manual send racing the outbox) — it IS sent.
        Assert.Equal(expected, UploadPolicy.Next(UploadOutcome.AlreadySent, 0, "HTTP 409"));
    }

    [Fact]
    public void Next_erased_and_rejected_fail_for_good()
    {
        Assert.Equal(new UploadPolicy.Step(true, SendState.Failed, "install erased"), UploadPolicy.Next(UploadOutcome.Erased, 0, "HTTP 410"));
        Assert.Equal(new UploadPolicy.Step(true, SendState.Failed, "HTTP 400"), UploadPolicy.Next(UploadOutcome.Rejected, 0, "HTTP 400"));
        Assert.Equal(new UploadPolicy.Step(true, SendState.Failed, "HTTP 413"), UploadPolicy.Next(UploadOutcome.Rejected, 2, "HTTP 413"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Next_server_retry_keeps_the_entry_before_the_third_launch(int launches)
        => Assert.Equal(new UploadPolicy.Step(false, SendState.Queued, "HTTP 503"), UploadPolicy.Next(UploadOutcome.RetryServer, launches, "HTTP 503"));

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Next_server_retry_gives_up_on_the_third_launch_that_saw_one(int launches)
        => Assert.Equal(new UploadPolicy.Step(true, SendState.Failed, "gave up"), UploadPolicy.Next(UploadOutcome.RetryServer, launches, "HTTP 503"));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(100)]
    public void Next_network_failure_never_gives_up(int launches)
        => Assert.Equal(new UploadPolicy.Step(false, SendState.Queued, "offline"), UploadPolicy.Next(UploadOutcome.RetryNetwork, launches, "network error"));

    // ── Due / Backoff ─────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Due_an_entry_never_server_retried_is_always_due()
    {
        Assert.True(UploadPolicy.Due(0, null, Now));
        Assert.True(UploadPolicy.Due(0, Now, Now));
        Assert.True(UploadPolicy.Due(2, null, Now));   // no recorded try: nothing to wait for
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(5, 32)]
    public void Due_waits_two_to_the_n_minutes(int attempts, int minutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(minutes), UploadPolicy.Backoff(attempts));
        Assert.False(UploadPolicy.Due(attempts, Now - TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(1), Now));
        Assert.True(UploadPolicy.Due(attempts, Now - TimeSpan.FromMinutes(minutes), Now));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(1000)]
    public void Due_backoff_is_capped_at_sixty_minutes(int attempts)
    {
        Assert.Equal(TimeSpan.FromMinutes(UploadPolicy.MaxBackoffMinutes), UploadPolicy.Backoff(attempts));
        Assert.False(UploadPolicy.Due(attempts, Now - TimeSpan.FromMinutes(59), Now));
        Assert.True(UploadPolicy.Due(attempts, Now - TimeSpan.FromMinutes(60), Now));
    }

    // ── Timeout ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-5L, 30)]
    [InlineData(0L, 30)]
    [InlineData(49_999L, 30)]
    [InlineData(50_000L, 31)]
    [InlineData(1_000_000L, 50)]
    [InlineData(20L << 20, 449)]            // the 20 MB cap: 30 s + one per 50 KB
    [InlineData(28_500_000L, 600)]
    [InlineData(100_000_000L, 600)]         // clamped at ten minutes
    public void Timeout_scales_with_the_body_and_is_clamped(long bodyBytes, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), UploadPolicy.Timeout(bodyBytes));

    // ── IsOrphan ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\logs\crash\20261002-091530-250-managed", false, true)]   // the report was deleted: never send it
    [InlineData(@"C:\logs\crash\20261002-091530-250-managed", true, false)]
    [InlineData("", false, false)]                                            // no recorded folder: still sent
    [InlineData("", true, false)]
    public void IsOrphan_only_when_a_recorded_folder_is_gone(string bundleDir, bool exists, bool expected)
        => Assert.Equal(expected, UploadPolicy.IsOrphan(bundleDir, exists));

    // ── UploadToasts.For ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SendState.Sent, false, UploadToasts.Toast.Sent)]
    [InlineData(SendState.Sent, true, UploadToasts.Toast.Sent)]
    [InlineData(SendState.Failed, false, UploadToasts.Toast.Failed)]
    [InlineData(SendState.Failed, true, UploadToasts.Toast.Failed)]
    [InlineData(SendState.Queued, false, UploadToasts.Toast.Queued)]
    [InlineData(SendState.Queued, true, UploadToasts.Toast.None)]     // "queued" is said once per report, not per retry
    [InlineData(SendState.NotSent, false, UploadToasts.Toast.None)]
    public void UploadToasts_pick_the_card_for_the_real_state(SendState state, bool queuedShown, UploadToasts.Toast expected)
        => Assert.Equal(expected, UploadToasts.For(state, queuedShown));

    // ── OutboxMeta ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OutboxMeta_round_trips_through_its_sidecar_text()
    {
        var meta = new OutboxMeta(@"C:\Users\x\AppData\Local\Wavee\logs\crash\20261002-091530-250-native", 3, false,
            new DateTime(2026, 10, 2, 9, 15, 30, 250, DateTimeKind.Utc), 2);
        var back = OutboxMeta.Parse(meta.Format());
        Assert.Equal(meta, back);
        Assert.Equal(DateTimeKind.Utc, back.LastTryUtc!.Value.Kind);
    }

    [Fact]
    public void OutboxMeta_parse_tolerates_crlf_unknown_keys_and_missing_fields()
    {
        Assert.Equal(OutboxMeta.Empty, OutboxMeta.Parse(""));
        var meta = OutboxMeta.Parse("bundleDir=C:\\a=b\r\nattempts=2\r\nfuture=1\r\nincludeDump=false\r\n");
        Assert.Equal(@"C:\a=b", meta.BundleDir);
        Assert.Equal(2, meta.Attempts);
        Assert.False(meta.IncludeDump);
        Assert.Null(meta.LastTryUtc);
        Assert.Equal(0, meta.Launches);
    }

    // ── ShortId ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3f9c2b1a8d7e4c60a1b2c3d4e5f60718", "3f9c-2b1a")]
    [InlineData("3f9c2b1a", "3f9c-2b1a")]
    [InlineData("3f9c2b1", "3f9c2b1")]
    [InlineData("", "")]
    public void ShortId_is_the_first_eight_characters_split_by_a_dash(string reportId, string expected)
        => Assert.Equal(expected, ShortId(reportId));

    // ── the build-stamp gate ──────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://crash.cproducts.dev", "k3y", "1.2.1011.0", true)]
    [InlineData("", "k3y", "1.2.1011.0", false)]
    [InlineData("https://crash.cproducts.dev", "", "1.2.1011.0", false)]
    [InlineData("https://crash.cproducts.dev", "k3y", "", false)]       // no quad → the Worker's 400 → dropped for good
    [InlineData("   ", "k3y", "1.2.1011.0", false)]
    [InlineData("", "", "", false)]
    public void CrashReportingAvailable_needs_url_key_and_quad(string url, string key, string quad, bool expected)
    {
        var v = new WaveeVersionInfo("1.2.1011", "1.2.1011", null, quad, "", "store", "86bf1105", "2026-10-02",
            CrashIngestUrl: url, CrashIngestKey: key);
        Assert.Equal(expected, v.CrashReportingAvailable);
    }
}
