using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Wavee;
using Xunit;
using static Wavee.Crash;

namespace Wavee.Tests;

public class CrashCollectionTests
{
    sealed class BlockingWriter() : StreamWriter(Stream.Null)
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public readonly ConcurrentQueue<string> Lines = new();
        int _first;

        public override void WriteLine(string? value)
        {
            if (Interlocked.Exchange(ref _first, 1) == 0)
            {
                Entered.Set();
                if (!Release.Wait(10_000)) throw new TimeoutException("test writer barrier");
            }
            Lines.Enqueue(value ?? "");
        }
    }

    static ParentContext Parent(long sequence = 1) => new(1234, sequence, 1000,
        CrashJsonTests.Sample() with { ParentPid = 1234, ReporterPid = 1234, LastRoute = "settings", Gpu = "Parent GPU" },
        "Wavee.exe", "Parent runtime", "Parent OS", "2026-10-13");

    [Fact]
    public void Secondary_cleanup_failure_is_recorded_without_replacing_primary_and_healthy_failures_propagate()
    {
        var primary = new InvalidOperationException("primary");
        var secondary = new IOException("cleanup");
        Exception? recorded = null;
        CleanupDiagnostics.Run(primary, () => throw secondary, ex => recorded = ex);
        Assert.Same(secondary, recorded);
        Assert.Same(secondary, Assert.Throws<IOException>(() => CleanupDiagnostics.Run(null, () => throw secondary, _ => { })));
        // A broken diagnostic sink also cannot replace the original failure.
        CleanupDiagnostics.Run(primary, () => throw secondary, _ => throw new ArgumentException("diagnostic sink"));
    }

    [Fact]
    public void Parent_handshake_is_a_single_bounded_line_and_round_trips_through_AOT_JSON()
    {
        var parent = Parent();
        string line = ContextProtocol.Format(parent);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.True(line.Length < ContextProtocol.MaxLineChars);
        Assert.True(ContextProtocol.TryRead(line, 1234, null, out var context));
        Assert.NotNull(context);
        Assert.Equal(parent.Metadata.Rvas, context.Metadata.Rvas);
        Assert.Equal(parent, context with { Metadata = context.Metadata with { Rvas = parent.Metadata.Rvas } });
    }

    [Theory]
    [InlineData(Kind.ExitCode)]
    [InlineData(Kind.Hang)]
    public void Synthetic_reports_use_parent_identity_state_and_monotonic_uptime(Kind kind)
    {
        var parent = Parent();
        var summary = ContextProtocol.BuildSummary(parent, 1234, 5678, kind, unchecked((int)0xc0000602),
            "parent failed", false, 15_000, DateTimeOffset.UnixEpoch);
        Assert.Equal(1234, summary.ParentPid);
        Assert.Equal(5678, summary.ReporterPid);
        Assert.Equal("sess-1", summary.SessionId);
        Assert.Equal("Parent GPU", summary.Gpu);
        Assert.Equal("settings", summary.LastRoute);
        Assert.Equal(14_000, summary.UptimeMs);
        Assert.Equal(parent.Metadata.DebugId, summary.DebugId);
        Assert.Equal(parent.Metadata.Quad, summary.Quad);
        Assert.Equal(parent.Metadata.ModuleBase, summary.ModuleBase);
        Assert.Equal(parent.Metadata.ModuleSize, summary.ModuleSize);
        Assert.Empty(summary.Rvas);
        Assert.False(summary.HasDump);
        var text = new StringBuilder();
        Report.DescribeParent("parent failed", text, summary, parent);
        Assert.Contains("pid=1234\nreporterPid=5678", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("framework=Parent runtime", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("module=Wavee.exe base=0x140000000", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_parent_handshake_keeps_parent_metadata_unknown()
    {
        var summary = ContextProtocol.BuildSummary(null, 1234, 5678, Kind.ExitCode, 3, "exit", true,
            100_000, DateTimeOffset.UnixEpoch);
        Assert.Equal(1234, summary.ParentPid);
        Assert.Equal(5678, summary.ReporterPid);
        Assert.Empty(summary.SessionId);
        Assert.Empty(summary.Gpu);
        Assert.Empty(summary.LastRoute);
        Assert.Empty(summary.Arch);
        Assert.Empty(summary.DebugId);
        Assert.Equal(0, summary.ModuleBase);
        Assert.Equal(0, summary.UptimeMs);
        var text = new StringBuilder();
        Report.DescribeParent("exit", text, summary, null);
        Assert.Contains("framework=unknown", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("module=unknown", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Stale_foreign_and_incomplete_context_do_not_overwrite_latest_parent_state()
    {
        var previous = Parent(10);
        ParentContext[] rejected = [Parent(9), Parent(10), Parent(11) with { Pid = 5678 },
            Parent(11) with { StartedTickMs = 1001 },
            Parent(11) with { Metadata = previous.Metadata with { SessionId = "different-session" } }];
        foreach (var context in rejected)
        {
            Assert.False(ContextProtocol.TryRead(ContextProtocol.Format(context), 1234, previous, out var kept));
            Assert.Same(previous, kept);
        }
        foreach (string line in new[] { "C {", "C null", "C {}", "C {\"pid\":1234,\"sequence\":11,\"metadata\":{\"sessionId\":\"sess-1\",\"rvas\":[]}}" })
        {
            Assert.False(ContextProtocol.TryRead(line, 1234, previous, out var kept));
            Assert.Same(previous, kept);
        }
        var newer = Parent(11) with { Metadata = previous.Metadata with { LastRoute = "album", Gpu = "New GPU" } };
        Assert.True(ContextProtocol.TryRead(ContextProtocol.Format(newer), 1234, previous, out var accepted));
        Assert.Equal("album", accepted!.Metadata.LastRoute);
        Assert.Equal("New GPU", accepted.Metadata.Gpu);
    }

    [Fact]
    public void Oversized_protocol_line_is_drained_without_losing_the_next_control_line()
    {
        using var reader = new StringReader("C " + new string('x', ContextProtocol.MaxLineChars * 2) + "\r\nB\r\n");
        string? first = ContextProtocol.ReadLine(reader, out bool oversized);
        Assert.True(oversized);
        Assert.NotNull(first);
        Assert.True(first.Length <= ContextProtocol.MaxLineChars);
        Assert.False(ContextProtocol.TryRead(first, 1234, null, out _));
        Assert.Equal("B", ContextProtocol.ReadLine(reader, out oversized));
        Assert.False(oversized);
        Assert.Null(ContextProtocol.ReadLine(reader, out _));
    }

    [Fact]
    public void Latest_context_has_an_independent_coalesced_slot_when_blocked_writer_fills_ordinary_queue()
    {
        using var writer = new BlockingWriter();
        var sink = new Host.StdinSink(writer);
        try
        {
            sink.Post("B0");
            Assert.True(writer.Entered.Wait(5000), "writer never reached blocking barrier");
            sink.Post(ContextProtocol.Format(Parent(1)));
            for (int i = 1; i <= 256; i++) sink.Post("B" + i);
            sink.Post("ordinary-overflow");
            sink.Post(ContextProtocol.Format(Parent(2)));
            sink.Post("D forced-control", force: true);
            string latest = ContextProtocol.Format(Parent(3));
            sink.Post(latest);
            writer.Release.Set();
            sink.WaitIdle(5000);
            string[] lines = writer.Lines.ToArray();
            Assert.Equal(259, lines.Length);
            Assert.Equal("B0", lines[0]);
            for (int i = 1; i <= 256; i++) Assert.Equal("B" + i, lines[i]);
            Assert.Equal("D forced-control", lines[^2]);
            Assert.Equal(latest, lines[^1]);
            Assert.Single(lines, line => line.StartsWith("C ", StringComparison.Ordinal));
            Assert.DoesNotContain("ordinary-overflow", lines);
        }
        finally
        {
            writer.Release.Set();
            sink.WaitIdle(5000);
            sink.Stop();
            writer.Entered.Dispose(); writer.Release.Dispose();
        }
    }

    [Fact]
    public void Validated_post_frame_context_arms_child_frame_state_without_F_and_cannot_regress()
    {
        var initial = Parent(1) with { Metadata = Parent().Metadata with { BeforeFirstFrame = true } };
        Assert.True(ContextProtocol.TryRead(ContextProtocol.Format(initial), 1234, null, out var accepted));
        bool seen = ContextProtocol.FirstFrameSeen(false, accepted);
        Assert.False(seen);
        var rendered = Parent(2) with { Metadata = initial.Metadata with { BeforeFirstFrame = false } };
        Assert.True(ContextProtocol.TryRead(ContextProtocol.Format(rendered), 1234, accepted, out accepted));
        seen = ContextProtocol.FirstFrameSeen(seen, accepted);
        Assert.True(seen);
        var later = Parent(3) with { Metadata = initial.Metadata };
        Assert.True(ContextProtocol.TryRead(ContextProtocol.Format(later), 1234, accepted, out accepted));
        Assert.True(ContextProtocol.FirstFrameSeen(seen, accepted));
        Assert.True(ContextProtocol.FirstFrameSeen(seen, null));
        Assert.False(ContextProtocol.FirstFrameSeen(false, null));
    }

    [Fact]
    public void Parent_tail_filters_interleaved_processes_before_taking_last_lines_and_keeps_owned_continuations()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-crash-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "daily.log");
            var lines = new List<string> { Line(1, 1234, "old parent") };
            for (int i = 2; i < 350; i++) lines.Add(Line(i, 5678, "handoff pid=1234"));
            lines.Add(Line(350, 1234, "parent failure"));
            lines.Add("   at Parent.StackFrame()");
            lines.Add("[line dropped: credential]");
            lines.Add(Line(351, 5678, "other process"));
            lines.Add("   at Other.StackFrame()");
            File.WriteAllLines(path, lines);
            Bundles.WriteTail(dir, path, 4, Feedback.RedactionRules.None, parentPid: 1234);
            string tail = File.ReadAllText(Path.Combine(dir, Files.TailName));
            Assert.Contains("old parent", tail, StringComparison.Ordinal);
            Assert.Contains("parent failure", tail, StringComparison.Ordinal);
            Assert.Contains("Parent.StackFrame", tail, StringComparison.Ordinal);
            Assert.Contains("[line dropped: credential]", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("handoff", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("Other.StackFrame", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("pid=5678", tail, StringComparison.Ordinal);
            Bundles.WriteTail(dir, path, 2, Feedback.RedactionRules.None,
                [Line(352, 1234, "pending parent"), Line(353, 5678, "pending other")], parentPid: 1234);
            tail = File.ReadAllText(Path.Combine(dir, Files.TailName));
            Assert.Contains("pending parent", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("pending other", tail, StringComparison.Ordinal);
            Assert.Equal(2, tail.Count(c => c == '\n'));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static string Line(int seq, int pid, string message) =>
        $"seq={seq} tid=4 t=1791854130000 sid=sess-1 pid={pid} I [app] shutdown.phase - {message}";

    [Fact]
    public void Known_parent_session_excludes_previous_launch_with_reused_pid_and_its_pending_continuations()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-crash-session-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "daily.log");
            File.WriteAllLines(path,
            [
                Line(1, 1234, "previous launch").Replace("sid=sess-1", "sid=old-session", StringComparison.Ordinal),
                "   at Previous.StackFrame()",
                Line(2, 1234, "current launch"),
                "   at Current.StackFrame()",
                Line(3, 1234, "other previous launch").Replace("sid=sess-1", "sid=old-session", StringComparison.Ordinal),
            ]);
            Bundles.WriteTail(dir, path, 300, Feedback.RedactionRules.None,
                [Line(4, 1234, "old pending").Replace("sid=sess-1", "sid=old-session", StringComparison.Ordinal),
                    "   at OldPending.StackFrame()", Line(5, 1234, "current pending")],
                parentPid: 1234, sessionId: "sess-1");
            string tail = File.ReadAllText(Path.Combine(dir, Files.TailName));
            Assert.Contains("current launch", tail, StringComparison.Ordinal);
            Assert.Contains("Current.StackFrame", tail, StringComparison.Ordinal);
            Assert.Contains("current pending", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("previous launch", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("Previous.StackFrame", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("old pending", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("OldPending.StackFrame", tail, StringComparison.Ordinal);
            // Without a handshake the known PID is still usable; no synthetic session is invented.
            var unknownSession = new ParentTailFilter(1234);
            Assert.True(unknownSession.Keep(Line(1, 1234, "previous launch").Replace("sid=sess-1", "sid=old-session", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("[d3d12]", "recorded", "0x887A0005")]
    [InlineData("[d3d12]", "currentReason", "0x887a0006")]
    [InlineData("[d3d12]", "vendorId", "0x10DE")]
    [InlineData("[d3d12]", "deviceId", "0x00002484")]
    [InlineData("[d3d12]", "hr", "0x80004005")]
    [InlineData("[d3d12.recovery]", "phase", "submit-start")]
    [InlineData("[d3d12.teardown]", "phase", "device-released")]
    public void Narrow_gpu_diagnostics_survive_structural_scrubbing(string marker, string key, string value)
    {
        string result = Scrubber.ScrubTailLine($"I [engine] - {marker} {key}={value}")!;
        Assert.Contains($"{key}={value}", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[d3d12]", "recorded", "0x887A0005secret")]
    [InlineData("[d3d12]", "currentReason", "0x887A005")]
    [InlineData("[d3d12]", "hr", "0x80004005;account")]
    [InlineData("[d3d12]", "vendorId", "alice@example.com")]
    [InlineData("[d3d12]", "deviceId", "0x123456789")]
    [InlineData("[d3d12]", "backend", "personal-backend")]
    [InlineData("[d3d12.recovery]", "phase", "personal-phase")]
    [InlineData("[d3d12.fake]", "recorded", "0x887A0005")]
    [InlineData("[d3d12.recoveryevil]", "hr", "0x80004005")]
    [InlineData("[other]", "vendorId", "0x10DE")]
    public void Invalid_or_unrelated_gpu_values_remain_redacted(string marker, string key, string value)
    {
        string result = Scrubber.ScrubTailLine($"I [engine] - {marker} {key}=\"{value}\"")!;
        Assert.Contains($"{key}=<dropped>", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_gpu_details_do_not_bypass_personal_data_or_credential_redaction()
    {
        string line = "I [engine] - [d3d12] recorded=0x887A0005 email=alice@example.com path=C:\\Users\\alice\\secret arbitrary=confidential";
        var scrubbed = Scrubber.Scrub(CrashJsonTests.Sample(), "", [line], Feedback.RedactionRules.None);
        Assert.Contains("recorded=0x887A0005", scrubbed.TailTxt, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", scrubbed.TailTxt, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential", scrubbed.TailTxt, StringComparison.Ordinal);
        Assert.Equal("[line dropped: credential]", Scrubber.ScrubTailLine("I [engine] - [d3d12] hr=0x887A0005 Authorization=secret"));
        Assert.Equal(scrubbed, Scrubber.Scrub(scrubbed.Summary, scrubbed.ReportTxt, scrubbed.TailTxt.Split('\n'), Feedback.RedactionRules.None));
    }

    [Theory]
    [InlineData(false, null, "consent")]
    [InlineData(true, null, "missing")]
    public void Dump_selection_preserves_consent_and_missing_file_reason(bool requested, string? path, string expected)
    {
        var selected = DumpSelection.Read(path, requested);
        Assert.Null(selected.Bytes);
        Assert.Equal(expected, selected.OmissionReason);
    }

    [Fact]
    public void Dump_selection_distinguishes_missing_read_failure_empty_and_success_without_changing_consent()
    {
        Assert.Equal("missing", DumpSelection.Read("test", true, _ => throw new FileNotFoundException()).OmissionReason);
        Assert.Equal("read-failed", DumpSelection.Read("test", true, _ => throw new IOException()).OmissionReason);
        Assert.Equal("read-failed", DumpSelection.Read("test", true, _ => throw new UnauthorizedAccessException()).OmissionReason);
        Assert.Equal("missing", DumpSelection.Read("test", true, _ => []).OmissionReason);
        var success = DumpSelection.Read("test", true, _ => [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, success.Bytes);
        Assert.Null(success.OmissionReason);
        Assert.Equal("consent", DumpSelection.Read("test", false, _ => throw new InvalidOperationException("Must not read without consent")).OmissionReason);
    }

    [Fact]
    public void Local_dump_cap_preserves_created_size_and_omission_while_consent_stays_first()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wavee-crash-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Bundles.WriteSummary(dir, CrashJsonTests.Sample() with { HasDump = false, DumpBytes = 0 });
            string path = Path.Combine(dir, Files.DumpName);
            long bytes = Bundles.MaxDumpBytes + 1;
            using (var file = File.Create(path)) file.SetLength(bytes);
            Assert.False(Bundles.FinalizeDumpCapture(dir));
            Assert.False(File.Exists(path));
            var summary = Bundles.ReadSummary(dir)!;
            Assert.True(summary.DumpLocalCreated);
            Assert.False(summary.HasDump);
            Assert.Equal(bytes, summary.DumpBytes);
            Assert.Equal("size-cap", summary.DumpOmissionReason);
            Assert.Equal("size-cap", DumpSelection.Read(path, true, knownMissingReason: "size-cap").OmissionReason);
            Assert.Equal("consent", DumpSelection.Read(path, false, knownMissingReason: "size-cap").OmissionReason);
            Assert.Equal("missing", DumpSelection.Read(path, true).OmissionReason);
            // Creation size survives an earlier nonconsenting upload; the next inclusion attempt still knows the local cap.
            Bundles.UpdateSummary(dir, s => s with { DumpOmissionReason = "consent" });
            summary = Bundles.ReadSummary(dir)!;
            Assert.True(summary.DumpLocalCreated == true && summary.DumpBytes > Bundles.MaxDumpBytes);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("missing")]
    [InlineData("read-failed")]
    [InlineData("size-cap")]
    public void Packed_summary_records_actual_dump_omission_preserving_local_creation_facts(string reason)
    {
        var original = CrashJsonTests.Sample() with { DumpLocalCreated = true, DumpOmissionReason = reason };
        byte[] body = Bundle.Pack(new ScrubbedBundle(original, "report", "tail"), null, Bundle.CapBytes, "B", out _);
        var summary = PackedSummary(body);
        Assert.False(summary.DumpIncluded);
        Assert.Equal(reason, summary.DumpOmissionReason);
        Assert.True(summary.DumpLocalCreated);
        Assert.True(summary.HasDump);
        Assert.Equal(original.DumpBytes, summary.DumpBytes);
        Assert.DoesNotContain("name=\"dump\"", Encoding.UTF8.GetString(body), StringComparison.Ordinal);
    }

    [Fact]
    public void Multipart_size_decision_and_serialized_dump_metadata_agree()
    {
        var b = new ScrubbedBundle(CrashJsonTests.Sample(), "report", "tail");
        byte[] dump = new byte[4096];
        byte[] included = Bundle.Pack(b, dump, Bundle.CapBytes, "B", out bool dropped);
        Assert.False(dropped);
        Assert.True(PackedSummary(included).DumpIncluded);
        Assert.Null(PackedSummary(included).DumpOmissionReason);
        byte[] absent = Bundle.Pack(b, null, Bundle.CapBytes, "B", out _);
        byte[] capped = Bundle.Pack(b, dump, absent.Length + 128, "B", out dropped);
        Assert.True(dropped);
        Assert.False(PackedSummary(capped).DumpIncluded);
        Assert.Equal("size-cap", PackedSummary(capped).DumpOmissionReason);
        Assert.DoesNotContain("name=\"dump\"", Encoding.UTF8.GetString(capped), StringComparison.Ordinal);
        byte[] empty = Bundle.Pack(b, ReadOnlyMemory<byte>.Empty, Bundle.CapBytes, "B", out dropped);
        Assert.False(dropped);
        Assert.False(PackedSummary(empty).DumpIncluded);
        Assert.Equal("missing", PackedSummary(empty).DumpOmissionReason);
    }

    static Summary PackedSummary(byte[] body)
    {
        string text = Encoding.UTF8.GetString(body);
        int start = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        int end = text.IndexOf("\r\n--", start, StringComparison.Ordinal);
        return JsonSerializer.Deserialize(text[start..end], CrashJson.Default.Summary)!;
    }
}
