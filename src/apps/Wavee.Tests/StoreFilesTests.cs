// ── Wavee.Tests/StoreFilesTests.cs — the cache folder's janitor: which files are ours and stale, and the reap (D1) ───
//
// The cache file is named for its schema (`library.<fingerprint>.db`), so every DDL change leaves a generation behind
// and every pre-D1 install still carries the shared `library.db`. `StoreFiles.Plan` decides, from file NAMES alone,
// what goes; `StoreFiles.Reap` does it through Store's all-or-nothing, rename-first delete.
//
// The facts that matter most are the two NEVERs — never the current set, never a file that is not ours — and the
// all-or-nothing: a set another handle holds must come out of a reap EXACTLY as it went in (every member, every byte,
// no `.dead-*` left beside it). The half-deleted set was the 2026-09-18 incident: a `-wal` that outlived its database
// was replayed over the next one. The held-set facts below are the only way to reach `Store.Delete` from a test (it is
// internal), and they cover it in both orders a held member can fail in.
//
// Temp folders only, one per test, removed in Dispose; %LOCALAPPDATA% is never touched.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class StoreFilesTests : IDisposable
{
    const string Current = "library.0123456789abcdef.db";
    const string Other = "library.fedcba9876543210.db";
    const string Another = "library.aaaaaaaaaaaaaaaa.db";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "wavee-store-files-" + Guid.NewGuid().ToString("n"));

    public StoreFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string[] Sorted(params string[] names)
    {
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // ── the plan (pure: names in, names out) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_legacy_set_is_reaped_whole()
    {
        ReapPlan plan = StoreFiles.Plan(["library.db", "library.db-wal", "library.db-shm"], Current);

        Assert.Equal(new[] { "library.db" }, plan.Sets);         // ONE set, not three files
        Assert.Empty(plan.Dead);
    }

    /// <summary>The incident's leftover: a WAL whose database is gone. It is the most dangerous file this store can leave
    /// behind — it is replayed over whatever is created at that path next — so it names its set by itself.</summary>
    [Fact]
    public void An_orphan_wal_or_shm_still_names_its_set()
    {
        Assert.Equal(new[] { "library.db" }, StoreFiles.Plan(["library.db-wal"], Current).Sets);
        Assert.Equal(new[] { "library.db" }, StoreFiles.Plan(["library.db-shm", "library.db-wal"], Current).Sets);
        Assert.Equal(new[] { Other }, StoreFiles.Plan([Other + "-wal"], Current).Sets);
    }

    [Fact]
    public void Every_other_generation_is_reaped_and_the_current_one_never_is()
    {
        ReapPlan plan = StoreFiles.Plan(
            [Current, Current + "-wal", Current + "-shm", Other, Other + "-wal", Another + "-shm"], Current);

        Assert.Equal(Sorted(Other, Another), plan.Sets);         // exactly these: the current set is not among them
        Assert.Empty(plan.Dead);
    }

    [Fact]
    public void Dead_leftovers_are_reaped_file_by_file_even_the_current_generations()
    {
        string[] dead = ["library.db.dead-1a2b3c4d", Current + "-wal.dead-0f0f0f0f", Other + ".dead-deadbeef"];

        ReapPlan plan = StoreFiles.Plan(dead, Current);

        Assert.Equal(Sorted(dead), plan.Dead);                   // a renamed-aside member belongs to NO set any more
        Assert.Empty(plan.Sets);                                 // …and does not drag its old set into the plan
    }

    [Fact]
    public void Nothing_that_is_not_ours_is_ever_named()
    {
        ReapPlan plan = StoreFiles.Plan(
        [
            "audiokeys.db", "store.json", "library-notes.db", "library.db.bak", "library.db-journal",
            "notlibrary.db", "library.12345.db", "library.0123456789abcdeg.db", "library.0123456789abcdef0.db",
            Current + ".bak", "library.db.dead-xyz", "library.db.dead-1a2b3c4d5", "library.dead-notes.txt",
            "library.db.dead-1a2b3c4d.txt", "", "library",
        ], Current);

        Assert.Empty(plan.Sets);
        Assert.Empty(plan.Dead);
    }

    [Fact]
    public void Names_compare_the_way_the_folder_does_case_insensitively()
    {
        ReapPlan plan = StoreFiles.Plan(
            ["LIBRARY.DB", "Library.DB-WAL", Current.ToUpperInvariant(), "library.FEDCBA9876543210.db-SHM",
             "LIBRARY.DB.DEAD-1A2B3C4D"], Current);

        Assert.Equal(Sorted("LIBRARY.DB", "library.FEDCBA9876543210.db"), plan.Sets);   // first spelling, once
        Assert.Equal(new[] { "LIBRARY.DB.DEAD-1A2B3C4D" }, plan.Dead);
    }

    [Fact]
    public void The_current_set_is_never_named_even_when_it_is_the_legacy_name()
        => Assert.Empty(StoreFiles.Plan(["library.db", "library.db-wal", "library.db-shm"], "library.db").Sets);

    // ── the reap (a real folder) ────────────────────────────────────────────────────────────────────────────────────

    string At(string name) => Path.Combine(_dir, name);

    /// <summary>A set's three members, each with its own recognisable bytes.</summary>
    void Set(string name)
    {
        File.WriteAllBytes(At(name), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(At(name + "-wal"), new byte[] { 5, 6, 7 });
        File.WriteAllBytes(At(name + "-shm"), new byte[] { 8, 9 });
    }

    string[] Names()
    {
        string[] files = Directory.GetFiles(_dir);
        for (int i = 0; i < files.Length; i++) files[i] = Path.GetFileName(files[i]);
        return Sorted(files);
    }

    [Fact]
    public void A_reap_removes_every_planned_set_and_leftover_and_nothing_else()
    {
        Set("library.db");
        Set(Other);
        Set(Current);
        File.WriteAllBytes(At(Another + ".dead-0badf00d"), new byte[] { 0 });
        File.WriteAllBytes(At("audiokeys.db"), new byte[] { 0 });
        File.WriteAllBytes(At("library-notes.db"), new byte[] { 0 });

        ReapResult result = StoreFiles.Reap(_dir, Current);

        Assert.Equal(2, result.Sets);
        Assert.Equal(2, result.Reaped);
        Assert.Equal(0, result.Held);
        Assert.Equal(1, result.Dead);
        Assert.Equal(Sorted(Current, Current + "-wal", Current + "-shm", "audiokeys.db", "library-notes.db"), Names());
    }

    /// <summary>THE ALL-OR-NOTHING, main file held. The delete moves the <c>-wal</c> first and the main file LAST, so
    /// holding the main file is the case where two members have ALREADY been renamed aside when the third refuses —
    /// and both must be put back, under their own names, with their own bytes, before the reap moves on.</summary>
    [Fact]
    public void A_held_set_comes_out_of_a_reap_exactly_as_it_went_in()
    {
        Set("library.db");
        Set(Other);

        ReapResult result;
        using (new FileStream(At("library.db"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = StoreFiles.Reap(_dir, Current);

        Assert.Equal(2, result.Sets);
        Assert.Equal(1, result.Reaped);                          // the unheld generation went…
        Assert.Equal(1, result.Held);                            // …the held one is reported, not hidden
        Assert.Equal(Sorted("library.db", "library.db-wal", "library.db-shm"), Names());   // no .dead-* beside it
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(At("library.db")));
        Assert.Equal(new byte[] { 5, 6, 7 }, File.ReadAllBytes(At("library.db-wal")));
        Assert.Equal(new byte[] { 8, 9 }, File.ReadAllBytes(At("library.db-shm")));
    }

    /// <summary>The same, held in the middle: the <c>-wal</c> has moved when the <c>-shm</c> refuses, and comes back.</summary>
    [Fact]
    public void A_set_held_by_its_shm_rolls_its_wal_back()
    {
        Set(Other);

        ReapResult result;
        using (new FileStream(At(Other + "-shm"), FileMode.Open, FileAccess.Read, FileShare.None))
            result = StoreFiles.Reap(_dir, Current);

        Assert.Equal(1, result.Held);
        Assert.Equal(0, result.Reaped);
        Assert.Equal(Sorted(Other, Other + "-wal", Other + "-shm"), Names());
        Assert.Equal(new byte[] { 5, 6, 7 }, File.ReadAllBytes(At(Other + "-wal")));
    }

    [Fact]
    public void A_missing_folder_reaps_nothing_and_does_not_throw()
    {
        ReapResult result = StoreFiles.Reap(Path.Combine(_dir, "no-such-folder"), Current);

        Assert.Equal(0, result.Sets);
        Assert.Equal(0, result.Reaped);
    }
}
