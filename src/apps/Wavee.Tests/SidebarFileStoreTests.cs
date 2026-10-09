// ── Wavee.Tests/SidebarFileStoreTests.cs — the sidebar's byte store: atomic write, one rotated .bak, the debounce, ───────
// the size budget, the set-aside of a corrupt file and the completed-write signal ─────────────────────────────────────
//
// Ported from SidebarStoreTests.cs (the v2 layout store's file facts). The store moves bytes and nothing else, so every
// fact here commits raw bytes and reads them back: parsing, versions and the defaults are the caller's (SidebarStoreV3,
// SidebarAccountStore) and are tested with their own wire.
//
// Every fact opens its own temp directory under Path.GetTempPath() and deletes it in Dispose(); nothing here reads,
// writes or deletes anything under Platform.LocalFolder or %LOCALAPPDATA%\Wavee.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SidebarFileStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-sidebar-file-tests", Guid.NewGuid().ToString("n"));
    readonly string _path;

    public SidebarFileStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "sidebar.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    SidebarFileStore Store() => new(_path);

    static byte[] Doc(string tag) => Encoding.UTF8.GetBytes("{\"v\":3,\"tag\":\"" + tag + "\"}");

    static string TextOf(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    static void CommitAndWait(SidebarFileStore store, byte[] bytes)
    {
        store.Commit(bytes);
        Assert.True(store.WaitForWrites(10_000), "the pool write did not finish inside 10 s");
    }

    // ── first run and reads ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void First_run_is_not_a_fault()
    {
        var store = Store();
        var read = store.Read();
        Assert.Equal(SidebarFileReadOutcome.Missing, read.Outcome);
        Assert.Null(read.Bytes);
        Assert.False(store.SaveFaulted);
        Assert.False(File.Exists(_path));   // a read must not create anything
    }

    [Fact]
    public void Read_returns_the_bytes_that_were_committed()
    {
        var store = Store();
        CommitAndWait(store, Doc("first"));

        var read = store.Read();
        Assert.Equal(SidebarFileReadOutcome.Ok, read.Outcome);
        Assert.Equal(Doc("first"), read.Bytes);
    }

    [Fact]
    public void First_commit_creates_the_file_and_no_backup()
    {
        var store = Store();
        CommitAndWait(store, Doc("first"));

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(store.BakPath));
        Assert.False(File.Exists(store.TmpPath));
    }

    [Fact]
    public void Sidecar_paths_are_derived_from_the_document_path()
    {
        var store = Store();
        Assert.Equal(_path, store.FilePath);
        Assert.Equal(_path + ".bak", store.BakPath);
        Assert.Equal(_path + ".tmp", store.TmpPath);
        Assert.Equal(_path + ".corrupt", store.CorruptPath);
    }

    [Fact]
    public void Commit_creates_missing_directories()
    {
        string nested = Path.Combine(_dir, "a", "b", "sidebar.json");
        var store = new SidebarFileStore(nested);
        CommitAndWait(store, Doc("deep"));
        Assert.True(File.Exists(nested));
    }

    // ── atomic write + exactly one rotated .bak ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Atomic_write_leaves_no_temp_file_and_creates_exactly_one_backup()
    {
        var store = Store();

        CommitAndWait(store, Doc("first"));
        Assert.False(File.Exists(store.TmpPath));
        Assert.False(File.Exists(store.BakPath));

        CommitAndWait(store, Doc("second"));
        Assert.False(File.Exists(store.TmpPath));
        Assert.True(File.Exists(store.BakPath));

        // exactly ONE backup, never .bak.1 / .bak.2, and it holds the PREVIOUS content
        Assert.Single(Directory.GetFiles(_dir, "*.bak"));
        Assert.Equal(Doc("first"), File.ReadAllBytes(store.BakPath));
        Assert.Equal(Doc("second"), store.Read().Bytes);

        CommitAndWait(store, Doc("third"));
        Assert.Single(Directory.GetFiles(_dir, "*.bak"));
        Assert.Equal(Doc("second"), File.ReadAllBytes(store.BakPath));   // rotated forward, still exactly one
    }

    [Fact]
    public void ReadBak_returns_the_previous_generation()
    {
        var store = Store();
        Assert.Equal(SidebarFileReadOutcome.Missing, store.ReadBak().Outcome);

        CommitAndWait(store, Doc("first"));
        CommitAndWait(store, Doc("second"));

        var bak = store.ReadBak();
        Assert.Equal(SidebarFileReadOutcome.Ok, bak.Outcome);
        Assert.Equal(Doc("first"), bak.Bytes);
    }

    // ── the named debounce, and its early flush ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void CommitDebounceMs_is_the_documented_300_ms_window()
        => Assert.Equal(300, SidebarFileStore.CommitDebounceMs);

    [Fact]
    public void Commit_coalesces_a_burst_into_the_last_snapshot()
    {
        var store = Store();
        CommitAndWait(store, Doc("seed"));

        // A burst of editor commands (a drag, a resize, a property edit): last-wins, one write lands
        // CommitDebounceMs after the LAST one, never one write per call.
        for (int i = 0; i < 25; i++) store.Commit(Doc("burst_" + i));
        Assert.True(store.WaitForWrites(10_000));

        Assert.Equal(Doc("burst_24"), store.Read().Bytes);
        Assert.False(File.Exists(store.TmpPath));
    }

    [Fact]
    public void Commit_returns_before_the_write_is_observable()
    {
        // The snapshot-then-background-write cadence: the UI thread is never blocked on the disk (C9). The write
        // becomes observable after WaitForWrites (the drain seam), never synchronously after Commit.
        var store = Store();
        store.Commit(Doc("async"));
        Assert.False(File.Exists(_path));                    // armed, not written
        Assert.True(store.WaitForWrites(10_000));
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void FlushNow_lands_the_debounced_write_before_the_window_elapses_so_a_shutdown_never_loses_the_last_edit()
    {
        var store = Store();
        CommitAndWait(store, Doc("seed"));

        // Still well inside CommitDebounceMs: nothing would have fired on its own yet.
        store.Commit(Doc("closing_edit"));
        store.FlushNow();
        Assert.True(store.WaitForWrites(10_000), "the flushed write did not land");

        Assert.Equal(Doc("closing_edit"), store.Read().Bytes);
    }

    // ── set-aside of a file the caller cannot parse ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MarkCorrupt_moves_the_file_to_dot_corrupt_and_keeps_the_bytes()
    {
        var store = Store();
        File.WriteAllText(_path, "{ truncated");
        File.WriteAllText(store.BakPath, "{ also broken");

        store.MarkCorrupt();

        Assert.False(File.Exists(_path));
        Assert.True(File.Exists(store.CorruptPath));
        Assert.Equal("{ truncated", File.ReadAllText(store.CorruptPath));   // the user's bytes are PRESERVED, not deleted
        Assert.Equal("{ also broken", File.ReadAllText(store.BakPath));     // the .bak is left alone
        Assert.Equal(SidebarFileReadOutcome.Missing, store.Read().Outcome);
    }

    [Fact]
    public void MarkCorrupt_replaces_a_previous_corrupt_file_and_the_next_commit_writes_fresh()
    {
        var store = Store();
        File.WriteAllText(store.CorruptPath, "an older casualty");
        File.WriteAllText(_path, "the newest casualty");

        store.MarkCorrupt();
        Assert.Equal("the newest casualty", File.ReadAllText(store.CorruptPath));
        Assert.Single(Directory.GetFiles(_dir, "*.corrupt"));

        CommitAndWait(store, Doc("fresh_start"));
        Assert.Equal(Doc("fresh_start"), store.Read().Bytes);
    }

    // ── the size budget ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Oversized_document_is_a_save_fault_and_nothing_is_written()
    {
        var store = Store();
        CommitAndWait(store, Doc("good"));
        byte[] before = File.ReadAllBytes(_path);

        // Checked against the SERIALIZED bytes: one byte over the budget is refused whole, never truncated.
        var huge = new byte[SidebarFileStore.MaxDocumentBytes + 1];
        Array.Fill(huge, (byte)'x');
        store.Commit(huge);
        Assert.True(store.WaitForWrites(20_000));

        Assert.Equal(SidebarSaveFault.DocumentTooLarge, store.SaveFault);
        Assert.Contains("budget", store.SaveFaultDetail!);
        Assert.Contains(SidebarFileStore.MaxDocumentBytes.ToString(), store.SaveFaultDetail!);
        Assert.Equal(before, File.ReadAllBytes(_path));
        Assert.False(File.Exists(store.TmpPath));

        // Shrinking the document IS the recovery: the next in-budget commit clears the fault.
        CommitAndWait(store, Doc("small_again"));
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
        Assert.Null(store.SaveFaultDetail);
    }

    [Fact]
    public void Document_exactly_at_the_budget_is_written()
    {
        var store = Store();
        var atCap = new byte[SidebarFileStore.MaxDocumentBytes];
        Array.Fill(atCap, (byte)'x');
        CommitAndWait(store, atCap);

        Assert.Equal(SidebarSaveFault.None, store.SaveFault);
        Assert.Equal(atCap, store.Read().Bytes);
    }

    // ── the completed-write signal ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Completed_write_publishes_a_healthy_measured_result()
    {
        var store = Store();
        SidebarWriteResult observed = default;
        store.WriteCompleted = result => observed = result;

        CommitAndWait(store, Doc("health"));

        Assert.True(observed.Success);
        Assert.Equal(SidebarPersistenceFault.None, observed.Fault);
        Assert.Equal(Doc("health").Length, observed.Bytes);
        Assert.True(observed.ElapsedMs >= 0);
        Assert.Equal(observed, store.LastWriteResult);
    }

    [Fact]
    public void Oversized_refusal_publishes_a_redacted_budget_fault()
    {
        var store = Store();
        SidebarWriteResult observed = default;
        store.WriteCompleted = result => observed = result;

        store.Commit(new byte[SidebarFileStore.MaxDocumentBytes + 1]);
        Assert.True(store.WaitForWrites(20_000));

        Assert.False(observed.Success);
        Assert.Equal(SidebarPersistenceFault.DocumentTooLarge, observed.Fault);
        Assert.Contains("budget", observed.SafeDetail!);
        Assert.DoesNotContain(_path, observed.SafeDetail!);
    }

    [Fact]
    public void IoFailure_is_classified_reactively_and_a_healthy_retry_clears_it()
    {
        // A file where the directory should be makes the directory creation fail: a real I/O fault, not a mock.
        string blocker = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(blocker, "block directory creation");
        string target = Path.Combine(blocker, "sidebar.json");
        var store = new SidebarFileStore(target);
        var observed = new List<SidebarWriteResult>();
        store.WriteCompleted = observed.Add;

        store.Commit(Doc("sec_fail"));
        Assert.True(store.WaitForWrites());

        var failed = Assert.Single(observed);
        Assert.False(failed.Success);
        Assert.Equal(SidebarPersistenceFault.IoFailure, failed.Fault);
        Assert.Equal(SidebarSaveFault.IoFailure, store.SaveFault);
        Assert.DoesNotContain(target, failed.SafeDetail!);

        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        store.Commit(Doc("recovered"));
        Assert.True(store.WaitForWrites());

        Assert.Equal(2, observed.Count);
        Assert.True(observed[^1].Success);
        Assert.Equal(SidebarSaveFault.None, store.SaveFault);   // reactive, not latched: the good retry clears it
    }
}
