// ── Wavee.Tests/LogStoreTests.cs — the Logs page's disk model (C15, X38) ──────────────────────────────────────────────
//
// `LogStore` is the walk, the load and the export the Logs page used to do inline. The line parser and the session
// splitter are pinned in LogFilesTests (over in-memory lines); these facts run the whole path over a real temp folder:
// a walk finds the sessions, a load reads one, an export writes it, and a folder that is not there is an empty answer,
// never a fault.

using Xunit;

namespace Wavee.Tests;

public sealed class LogStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-logstore-" + Guid.NewGuid().ToString("N"));

    public LogStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    static string Line(long seq, string sid, string rest = "x")
        => "seq=" + seq + " tid=1 t=" + (1_790_000_000_000 + seq) + " sid=" + sid + " pid=7 I [app] " + rest;

    string BasePath => Path.Combine(_dir, "wavee.log");

    /// <summary>Two completed runs in one daily file: three lines of "aaaaaaaa" then two of "bbbbbbbb".</summary>
    string WriteTwoSessions()
    {
        string file = Path.Combine(_dir, "wavee-20260101.log");
        File.WriteAllLines(file,
        [
            Line(1, "aaaaaaaa", "first"), Line(2, "aaaaaaaa", "second"), Line(3, "aaaaaaaa", "third"),
            Line(1, "bbbbbbbb", "fourth"), Line(2, "bbbbbbbb", "fifth"),
        ]);
        return file;
    }

    // ── the walk ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_folder_with_no_logs_walks_to_an_empty_answer_that_is_not_a_failure()
    {
        var outcome = LogStore.Walk(BasePath, pid: 1);

        Assert.NotNull(outcome.Walk);
        Assert.Empty(outcome.Walk!.Sessions);
        Assert.Null(outcome.Fault);
        Assert.False(outcome.Failed);
        Assert.True(outcome.ElapsedMs >= 0);
    }

    [Fact]
    public void A_folder_that_is_not_there_and_a_null_base_walk_to_an_empty_answer()
    {
        Assert.Empty(LogStore.Walk(Path.Combine(_dir, "gone", "wavee.log"), pid: 1).Walk!.Sessions);
        Assert.Empty(LogStore.Walk(null, pid: 1).Walk!.Sessions);
    }

    [Fact]
    public void The_walk_lists_every_completed_session_newest_first()
    {
        WriteTwoSessions();

        var outcome = LogStore.Walk(BasePath, pid: 1);

        Assert.False(outcome.Failed);
        var sessions = outcome.Walk!.Sessions;
        Assert.Equal(2, sessions.Count);
        Assert.Equal("bbbbbbbb", sessions[0].SessionId);
        Assert.Equal("aaaaaaaa", sessions[1].SessionId);
        Assert.Equal(3, sessions[1].EntryCount);
        Assert.Equal(1, outcome.Walk!.FilesSeen);
    }

    // ── load and export ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_past_session_loads_its_entries()
    {
        WriteTwoSessions();
        var older = LogStore.Walk(BasePath, pid: 1).Walk!.Sessions[1];

        var entries = LogStore.Load(older);

        Assert.NotNull(entries);
        Assert.Equal(3, entries!.Length);
        Assert.Equal(1, entries[0].Sequence);
    }

    [Fact]
    public void A_session_whose_file_vanished_loads_as_nothing_not_a_throw()
    {
        string file = WriteTwoSessions();
        var older = LogStore.Walk(BasePath, pid: 1).Walk!.Sessions[1];
        File.Delete(file);

        Assert.Empty(LogStore.Load(older) ?? []);      // the splitter's reader absorbs the I/O fault: an empty list
    }

    [Fact]
    public void Exporting_a_past_session_writes_its_raw_lines()
    {
        WriteTwoSessions();
        var newer = LogStore.Walk(BasePath, pid: 1).Walk!.Sessions[0];
        string target = Path.Combine(_dir, "export.txt");

        Assert.True(LogStore.Export(target, newer));

        Assert.Equal(new[] { Line(1, "bbbbbbbb", "fourth"), Line(2, "bbbbbbbb", "fifth") }, File.ReadAllLines(target));
    }

    [Fact]
    public void Exporting_the_live_ring_writes_a_file()
    {
        Log.Info("test", "log store export fixture line");
        string target = Path.Combine(_dir, "live.txt");

        Assert.True(LogStore.Export(target, past: null));

        Assert.True(new FileInfo(target).Length > 0);
    }

    [Fact]
    public void An_export_that_cannot_be_written_is_logged_and_answers_false()
        => Assert.False(LogStore.Export(Path.Combine(_dir, "no-such-folder", "export.txt"), past: null));

    [Fact]
    public void The_export_names_say_live_or_the_sessions_key()
    {
        WriteTwoSessions();
        var newer = LogStore.Walk(BasePath, pid: 1).Walk!.Sessions[0];

        Assert.Equal("wavee-session-live.txt", LogStore.ExportFileName(null));
        Assert.Equal("wavee-session-" + WaveeLogSessions.KeyOf(newer) + ".txt", LogStore.ExportFileName(newer));
    }
}
