// ── Entities/Store.Files.cs — CORE + SHELL (owner S1, wave D1) ─────────────────────────────────────────────────────
//
// The cache FILES, as opposed to what is in them (plan: docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md
// §3.1). Two small things live here, each a pure decision beside the shell that acts on it:
//
//   1. `StoreFiles` — which files in the cache folder are ours and stale (PURE `Plan`, over file NAMES only), and the
//      boot-time reaper that removes them (SHELL `Reap`). The cache file is named for the schema it holds
//      (`Store.FileName`, `library.<fingerprint>.db`), so every DDL change leaves the previous generation behind, and
//      every install that ever ran a pre-D1 build still has the shared `library.db`. Both go here, at boot, AFTER the
//      single-instance gate — a second instance must never reap the first one's live file — and each through
//      `Store.Delete`'s all-or-nothing rename: a set another process still holds (an older build that is running) does
//      not move, is left exactly as it was, and is tried again next launch.
//
//   2. `StoreHealth` — what a sqlite failure in the middle of a session means for the FILE (PURE `OnFault`). Store.cs's
//      `Fault` routes every failure through it: the first "the file is damaged" of a store session rebuilds the file,
//      a second one retires the store to memory-only, and every other failure is only counted. Before D1 a file that
//      went bad mid-session faulted every batch until the process exited, and the next launch inherited it.
//
// Nothing here opens a database. `Plan` and `OnFault` are pure — names and numbers in, a verdict out — which is what
// lets StoreFilesTests / StoreHealthTests pin them without a file on disk.

using System.Diagnostics;

namespace Wavee;

/// <summary>What the reaper should remove, decided from file names alone (<see cref="StoreFiles.Plan"/>).</summary>
/// <param name="Sets">Base names of whole sets — <c>library.db</c> or <c>library.&lt;16 hex&gt;.db</c> — each removed with
/// its <c>-wal</c> and <c>-shm</c> through <see cref="Store"/>'s all-or-nothing delete. A set whose main file is gone but
/// whose <c>-wal</c> or <c>-shm</c> remains is still named here by its base: an orphan WAL is the most dangerous file this
/// store can leave behind (it is replayed over whatever is created at that path next). Sorted, case-insensitively.</param>
/// <param name="Dead">Full names of <c>.dead-*</c> leftovers — members a delete renamed aside and could not remove. They
/// belong to no set any more and are deleted one by one. Sorted, case-insensitively.</param>
public sealed record ReapPlan(string[] Sets, string[] Dead);

/// <summary>What one <see cref="StoreFiles.Reap"/> did — the fields of its <c>store.reap</c> line.</summary>
/// <param name="Sets">Stale sets found.</param>
/// <param name="Reaped">Sets removed, all members.</param>
/// <param name="Held">Sets left exactly as they were because another handle held a member — tried again next launch.</param>
/// <param name="Dead"><c>.dead-*</c> leftovers deleted.</param>
/// <param name="Ms">Wall time of the whole pass.</param>
public readonly record struct ReapResult(int Sets, int Reaped, int Held, int Dead, long Ms);

/// <summary>The cache folder's janitor: a pure planner over file names, and the shell that runs the plan at boot.</summary>
public static class StoreFiles
{
    /// <summary>The suffix <see cref="Store"/>'s delete renames a member to before removing it:
    /// <c>&lt;member&gt;.dead-&lt;8 hex&gt;</c>. Shared here so the planner and the delete can never disagree on it.</summary>
    public const string DeadTag = ".dead-";

    /// <summary>How many hex digits follow <see cref="DeadTag"/>.</summary>
    public const int DeadTagHexDigits = 8;

    /// <summary>The fingerprint in <see cref="Store.FileName"/>: a 64-bit FNV-1a as 16 hex digits.</summary>
    const int FingerprintHexDigits = 16;

    const string SetHead = "library.", SetTail = ".db";

    /// <summary>PURE. From the file NAMES in the cache folder, what to reap:
    /// <list type="bullet">
    /// <item>every <c>library.&lt;16 hex&gt;.db</c> set other than <paramref name="currentFileName"/> (a generation some
    /// other schema wrote);</item>
    /// <item>the legacy <c>library.db</c> set (<see cref="Store.LegacyFileName"/>, every build's shared name before D1);</item>
    /// <item>every <c>.dead-*</c> leftover of either (<see cref="ReapPlan.Dead"/>).</item>
    /// </list>
    /// A set is its base name plus <c>-wal</c> and <c>-shm</c>; an orphan member with no base file still belongs to its
    /// set, and names it. NEVER the current set, whatever its spelling — and NEVER a file that is not ours:
    /// <c>audiokeys.db</c>, <c>store.json</c>, <c>library-notes.db</c>, <c>library.db.bak</c>, a <c>.dead-</c> with the
    /// wrong tag, anything that is not exactly one of the two shapes above. Case-insensitive, because the folder is
    /// (Windows). Runs once per launch over a handful of names: clarity first, a few small allocations allowed.</summary>
    public static ReapPlan Plan(ReadOnlySpan<string> fileNames, string currentFileName)
    {
        List<string>? sets = null, dead = null;
        foreach (string name in fileNames)
        {
            if (string.IsNullOrEmpty(name)) continue;
            ReadOnlySpan<char> member = name;
            bool isDead = TryStripDeadTag(ref member);
            ReadOnlySpan<char> set = SetOf(member);
            if (!IsOurs(set)) continue;                                               // not a store file: never touched
            if (isDead) { (dead ??= new List<string>()).Add(name); continue; }        // a leftover of ANY set, live or not
            if (set.Equals(currentFileName, StringComparison.OrdinalIgnoreCase)) continue;   // THE live set: never
            sets ??= new List<string>();
            if (!ContainsIgnoreCase(sets, set)) sets.Add(set.ToString());
        }
        return new ReapPlan(Sorted(sets), Sorted(dead));
    }

    /// <summary>SHELL. Enumerate <paramref name="folder"/>, <see cref="Plan"/> it, and remove the plan: each set through
    /// the store's all-or-nothing delete (all three files, or — when another handle holds any of them — none, counted
    /// as <c>held</c>), each <c>.dead-*</c> leftover best effort. Called by <c>App.Main</c> once, AFTER the
    /// single-instance gate and BEFORE <see cref="Store.Use"/>, with <see cref="Store.FileName"/>.
    /// <para><b>Never throws</b>: a janitor that failed must not cost the launch, and whatever it did not reach is still
    /// there next time. ONE always-on line either way: <c>store.reap sets= reaped= held= dead= ms=</c> (Info), or the
    /// same at Warning with <c>error=</c> when the pass stopped early.</para></summary>
    public static ReapResult Reap(string folder, string currentFileName)
    {
        long started = Stopwatch.GetTimestamp();
        int sets = 0, reaped = 0, held = 0, dead = 0;
        string? error = null;
        try
        {
            if (Directory.Exists(folder))
            {
                string[] paths = Directory.GetFiles(folder, "library*");
                var names = new string[paths.Length];
                for (int i = 0; i < paths.Length; i++) names[i] = Path.GetFileName(paths[i]);
                ReapPlan plan = Plan(names, currentFileName);
                sets = plan.Sets.Length;
                foreach (string set in plan.Sets)
                {
                    if (Store.Delete(Path.Combine(folder, set))) reaped++;
                    else held++;          // an older build still running, an antivirus scan: exactly as it was, next launch
                }
                foreach (string leftover in plan.Dead)
                    if (Store.TryDelete(Path.Combine(folder, leftover))) dead++;
            }
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        long ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (error is null)
            Log.Event(WaveeLogLevel.Info, "store", "store.reap", "", null, -1, null,
                WaveeLogField.Of("sets", sets), WaveeLogField.Of("reaped", reaped), WaveeLogField.Of("held", held),
                WaveeLogField.Of("dead", dead), WaveeLogField.Of("ms", ms));
        else
            Log.Event(WaveeLogLevel.Warning, "store", "store.reap", "the cache-file reaper stopped early", null, -1, null,
                WaveeLogField.Of("sets", sets), WaveeLogField.Of("reaped", reaped), WaveeLogField.Of("held", held),
                WaveeLogField.Of("dead", dead), WaveeLogField.Of("ms", ms), WaveeLogField.Of("error", error));
        return new ReapResult(sets, reaped, held, dead, ms);
    }

    /// <summary><c>….dead-&lt;8 hex&gt;</c> at the very END of the name ⇒ strip it and say so. Anything else — the tag
    /// mid-name, a tag of the wrong length or alphabet — leaves the name as it is, and such a name then fails
    /// <see cref="IsOurs"/>, which is the point: only the exact shape the delete produces is ever reaped.</summary>
    static bool TryStripDeadTag(ref ReadOnlySpan<char> name)
    {
        int at = name.Length - DeadTag.Length - DeadTagHexDigits;
        if (at <= 0) return false;
        if (!name.Slice(at, DeadTag.Length).Equals(DeadTag, StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsHex(name[(at + DeadTag.Length)..])) return false;
        name = name[..at];
        return true;
    }

    /// <summary>A member's set: the name without its <c>-wal</c> / <c>-shm</c>.</summary>
    static ReadOnlySpan<char> SetOf(ReadOnlySpan<char> member)
        => member.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || member.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
            ? member[..^4]
            : member;

    /// <summary>A set name this store has ever written: the legacy <c>library.db</c>, or
    /// <c>library.&lt;16 hex&gt;.db</c> — exactly, nothing more and nothing less.</summary>
    static bool IsOurs(ReadOnlySpan<char> set)
        => set.Equals(Store.LegacyFileName, StringComparison.OrdinalIgnoreCase)
           || (set.Length == SetHead.Length + FingerprintHexDigits + SetTail.Length
               && set.StartsWith(SetHead, StringComparison.OrdinalIgnoreCase)
               && set.EndsWith(SetTail, StringComparison.OrdinalIgnoreCase)
               && IsHex(set.Slice(SetHead.Length, FingerprintHexDigits)));

    static bool IsHex(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return false;
        foreach (char c in s)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    static bool ContainsIgnoreCase(List<string> names, ReadOnlySpan<char> name)
    {
        foreach (string n in names)
            if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string[] Sorted(List<string>? names)
    {
        if (names is null) return [];
        string[] sorted = names.ToArray();
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        return sorted;
    }
}

/// <summary>What a sqlite failure in the middle of a session means for the file (<see cref="StoreHealth.OnFault"/>).</summary>
public enum StoreFaultVerdict : byte
{
    /// <summary>Count it and carry on: the failure is about the statement, the moment or the machine — busy, locked,
    /// full, an I/O error — or the file's one rebuild is already spent.</summary>
    Count,
    /// <summary>The FILE is damaged and this session has not rebuilt it yet: rebuild it (Store.cs's <c>Rebuild</c>).</summary>
    Recover,
}

/// <summary>The store's health rules — PURE, no file, no clock. Store.cs's <c>Fault</c> is the one caller.</summary>
public static class StoreHealth
{
    /// <summary>PURE. <see cref="StoreFaultVerdict.Recover"/> only for a statement about the FILE — SQLITE_CORRUPT (11)
    /// or SQLITE_NOTADB (26), <see cref="Store.IsUnreadableFile"/>'s rule, the same one that recreates a file at open —
    /// and only while this session's one rebuild is unspent. Every other code (BUSY 5, LOCKED 6, IOERR 10, FULL 13,
    /// ERROR 1, …) and any second corruption ⇒ <see cref="StoreFaultVerdict.Count"/>. Once, because a file that is
    /// damaged again right after being rebuilt is being damaged by something outside it (a disk, an antivirus, a second
    /// writer), and rebuilding again would be a loop.</summary>
    /// <param name="sqliteErrorCode">The PRIMARY sqlite result code (the low byte of an extended one).</param>
    /// <param name="alreadyRecovered">This store session has already rebuilt its file.</param>
    public static StoreFaultVerdict OnFault(int sqliteErrorCode, bool alreadyRecovered)
        => !alreadyRecovered && Store.IsUnreadableFile(sqliteErrorCode) ? StoreFaultVerdict.Recover : StoreFaultVerdict.Count;

    /// <summary>PURE. The other half of "once": a statement about the FILE after the rebuild is spent. The store then
    /// stops using the file for the rest of the session — memory-only, said in the log — rather than faulting every
    /// batch against a file it knows is bad. False for everything <see cref="OnFault"/> would recover, and for every
    /// failure that is not about the file at all.</summary>
    public static bool RecoverySpent(int sqliteErrorCode, bool alreadyRecovered)
        => alreadyRecovered && Store.IsUnreadableFile(sqliteErrorCode);
}
