// ── Entities/Store.cs — SHELL with a CORE section (owner C, wave 1, budget 1,200; plan §2, §4.4) ─────────────────────
//
// THE DISK. One sqlite file — `library.db`, schema v4 (v4 adds the process-wide `palette` table, §WS-C) — holding
// the same columns the tables hold in memory, so "every track whose title starts with X" and "this album's rows"
// are real indexed queries instead of a walk over
// deserialized JSON (§5.4, P11). Everything here is a CACHE: a file whose schema does not match what this build
// writes is DELETED, never migrated (plan §4.4 — a v2 file is deleted; the provider can answer again, and a
// migration is a second schema to keep correct forever).
//
// The shape of this file, in order:
//   1. CORE — `CatalogSweepSchedule`: which rows leave the cache, as a pure function over synthetic rows. Ported
//      from _old/Wavee/Backend/Persistence/EntityCacheGc.cs (TTL is the FILTER, LRU is the RANKER, the byte budget
//      is the ceiling) with every database call, timer and lock removed, so the decision is unit-testable with no
//      file on disk (D17, and the reason the 0.2.9 GC had no test of its ordering at all).
//   2. CORE — the schema: the v3 DDL as text, generated from the registered kind shapes, and its fingerprint.
//   3. SHELL — one write connection and one read connection, both owned by ONE store thread (C9/D22). The UI thread
//      never touches sqlite: it enqueues, and it is answered by a `Staging` posted back (C1/C10).
//   4. SHELL — the batched cold read, write-behind, the edge tables, the synchronous intent journal, the GC tick.
//
// The rules this file is written under:
//   C1/C9   the UI thread never opens, reads, writes or waits on sqlite. Everything crosses through `Store.Post`.
//   C7      a cold read carries the scope epoch it was asked for; an answer for a replaced scope is dropped whole.
//   C8      every queue is bounded (4,096 jobs). A full queue DROPS a write (it is a cache) and REFUSES a read
//           (the planner clears in-flight and the row is asked again) — it never blocks the caller.
//   D22     write-behind for everything the provider said, and one SYNCHRONOUS journal for what the USER said: an
//           intent must be on disk before the call that made it returns, because a crash between the click and the
//           PUT is the only case where the two disagree.
//   P4      every API is a batch API: one `IN (…)` query per drain per kind, one transaction per batch.
//   P7      timestamps in memory are `int` seconds since the app epoch; on disk they are unix seconds, because a
//           file outlives the process that wrote it. `ToUnix`/`ToApp` are the only conversion, and they live here.
//
// THE KEY IS STILL `uri TEXT`, AND THAT IS A DECISION (2026-09-12; docs/plans/wavee/wavee-0.3-entity-identity-memory.md
// §4.4 and §6). A row's identity in memory is now a packed 24-byte `EntityId`, so the alternative was a BLOB key —
// 18 bytes instead of 36, no formatting at all, and a v3 file this build would simply delete (the fingerprint covers
// the DDL, and plan §4.4 already says a stale cache is DELETED, never migrated). It stays TEXT, for four reasons:
//   1. HALF THE IDS CANNOT BE PACKED. A text-form id's payload is a `StringId` — an index into a PROCESS-LOCAL
//      interner, meaningless in the next launch. Only the gid form survives a restart as bytes, so a BLOB key would
//      have to be two encodings in one column (a tagged gid, and the uri's UTF-8 for everything else), with a whole
//      class of "the same entity was written under two keys" bugs behind it. The uri text is the one spelling both
//      forms already have, and `EntityId.Parse` folds it back to one id either way.
//   2. THE FILE STAYS READABLE. `sqlite3 library.db "select uri,title from track"` is the diagnostic a cache should
//      keep (CLAUDE.md: always-on diagnostics, no debug switches), and `edge.parent`/`edge.child` and the sweep's
//      `length(uri)+64` byte estimate all read the same column.
//   3. THE COST MOVED OFF THE FRAME, WHICH IS THE HALF THAT MATTERED. The doc prices the new key at one `Format`
//      (81 ns) plus one string per row per read/write, where the interned string used to be free. It is paid on the
//      STORE thread now: the UI thread hands over a snapshot of `Column<EntityId>` (a 24-byte copy per row and no
//      string at all) and the store thread formats what it needs. The frame's per-row cost went DOWN, not up.
//   4. AND THE ESCAPE HATCH IS FREE. If a BLOB(18) key is ever wanted, it is one DDL change: the fingerprint moves,
//      the stale file is deleted, and the provider re-answers. Nothing here is ever migrated.
//
// WHO MAY TOUCH THE INTERNER, AND FROM WHERE (C1 — and the reason for the two-array snapshot below). `Resolve` is
// safe from a second thread only while the id is ALIVE: a released id's slot is cleared 16 ticks after its last
// reference goes (`StringTable.Tick`), and a queued write-behind job can sit far longer than 16 frames. So the store
// thread NEVER resolves. Text-form keys are resolved on the UI thread (free — the string already exists) and carried
// across as `string`; gid-form keys carry no string at all and are formatted on the store thread, where `Format` is
// pure arithmetic over the packed id.
//
// WHAT THIS FILE DOES NOT KNOW: a kind's columns. `Track.cs` owns which columns a track has, so it also owns the
// six lines that bind them to a row — that is `KindShape`, and it is the only thing an owner has to write to make
// their kind persist. The SCHEMA is still this file's (it generates the DDL, the indexes, the upsert and the
// fingerprint from the shapes), exactly as plan §4.4 has it; the per-column binding is the kind's.

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Foundation;
using Microsoft.Data.Sqlite;

namespace Wavee;

// ── 1. CORE: the sweep schedule (pure — no database, no clock, no allocation) ────────────────────────────────────────

/// <summary>Why a row cannot be evicted. One flag today; a flags enum rather than a <c>bool</c> because the pin set
/// grows (now-playing, the queue, the open page, the rootlist — <c>EntityCacheGc</c>'s pin table had five sources).</summary>
[Flags]
public enum SweepFlags : byte
{
    None = 0,
    /// <summary>Something live points at this row: it is playing, queued, on the open page, or in the library.</summary>
    Pinned = 1,
}

/// <summary>One candidate row, as the sweep sees it. Deliberately NOT a table slot: the planner is pure, and the
/// caller (sqlite, a test) decides what <see cref="Key"/> means — a uri index, a slot, a row number.
/// <para><paramref name="Touched"/> is the LRU clock (when a page last read it) and <paramref name="FetchedAt"/> the
/// freshness clock (when a provider last answered). Both are app seconds, both may be negative for a row written by
/// an earlier launch — see <see cref="Store.ToApp"/>.</para></summary>
public readonly record struct SweepRow(int Key, int Touched, int FetchedAt, int Bytes, SweepFlags Flags);

/// <summary>The knobs, all of them, in one value so a test can shorten every window at once. Defaults are 0.2.9's
/// measured ones (<c>EntityCacheGc.EntityTtlSeconds</c>, <c>SqliteColdStore.GcNewRowGraceSeconds</c>,
/// <c>DefaultCacheBudgetBytes</c>, <c>GcDeleteBatchRows</c>).</summary>
/// <param name="TtlSeconds">A row nobody has read for this long is cold. 30 days.</param>
/// <param name="GraceSeconds">A row a provider answered inside this window is too new to judge (critique #11): a
/// page that mounts and evicts its own freshly-fetched rows in the same pass is a fetch loop.</param>
/// <param name="ByteBudget">The cache-tier ceiling. 0 disables the budget leg entirely.</param>
/// <param name="HeadroomPercent">The budget leg deletes down to this percentage of the budget, not to the budget
/// itself — hysteresis, so the next pass is not immediately over again (0.2.9: "down to 0.9 × budget").</param>
/// <param name="MaxRowsPerPass">The atomic DELETE batch. Bounded so a pass can be abandoned between batches and the
/// file is still consistent.</param>
/// <param name="DeferQueueDepth">Above this many queued jobs the sweep does not run at all: a GC that competes with
/// the write-behind lane turns a burst of answers into a stall (R2).</param>
public readonly record struct SweepPolicy(
    int TtlSeconds,
    int GraceSeconds,
    long ByteBudget,
    int HeadroomPercent,
    int MaxRowsPerPass,
    int DeferQueueDepth)
{
    public static SweepPolicy Default { get; } = new(
        TtlSeconds: 30 * 24 * 60 * 60,
        GraceSeconds: 15 * 60,
        ByteBudget: 64L * 1024 * 1024,
        HeadroomPercent: 90,
        MaxRowsPerPass: 1_000,
        DeferQueueDepth: 64);
}

/// <summary>What one pass decided. <see cref="More"/> means the pass hit a bound (the row cap, or it is still over
/// budget) and another pass should follow — the caller decides when, which is what keeps the schedule pure.</summary>
public readonly record struct SweepPlan(
    int Victims,
    int TtlVictims,
    int BudgetVictims,
    long BytesFreed,
    long BytesAfter,
    bool Deferred,
    bool More);

/// <summary>Who is holding a row, so a MEMORY trim cannot retire it under them
/// (<see cref="Store.TrimMemory(Scope,Table,in SweepPolicy,int)"/>). An abstract class and not a delegate because the
/// trim calls it once per candidate row and a closure per call is exactly the allocation P9 forbids; and not a flags
/// column on the table, because the pin sources are the SHELL's (now-playing, the queue, the open page's bound rows,
/// the rootlist) and core must not know them.
///
/// <para><b>Why this has no default implementation and nothing calls the trim yet.</b> A slot is a row's whole
/// identity to a bound page, and <see cref="Table.FreeSlot"/> puts it back on the free list for the next entity to
/// take. Retiring a row somebody is rendering is therefore a VISIBLE bug, not a memory optimisation, and 0.3 has no
/// reverse index from a slot to the edges and pages pointing at it yet. So the trim is opt-in: the shell registers
/// its pin sources with <see cref="Store.Pins"/> and drives <see cref="Store.TrimMemory(Scope)"/> from the memory
/// governor when those sources exist. Until then the leak stays fixed (a scope switch and every explicit
/// <c>FreeSlot</c> hand text back) and nothing evicts a row behind a page's back.</para></summary>
public abstract class MemoryPins
{
    /// <summary>Is anything live pointing at this row? UI thread; called once per candidate, so keep it a lookup.</summary>
    public abstract bool IsPinned(EntityKind kind, int slot);
}

/// <summary>THE eviction decision, ported from <c>EntityCacheGc.RunPassCore</c> as a pure function (plan §4.4).
///
/// <para>Two-stage victim selection, Firefox cache2's: <b>TTL is the filter, LRU is the ranker</b>. There is no
/// W-TinyLFU and no SIEVE — a periodic pass over one index IS the whole engine, and the parts that made 0.2.9's
/// version untestable (the connection, the temp tables, the cancellation token, the six SQL statements) are the
/// caller's now.</para>
///
/// <para><b>The input must be ordered by <c>Touched</c> ascending</b> — coldest first. That is not a convenience: it
/// is what makes the budget leg an LRU rank instead of a sort, and it is free, because <c>ix_&lt;kind&gt;_gc</c>
/// exists for exactly this query (plan §4.4's schema). A caller that hands over unordered rows still gets a correct
/// TTL sweep; only the budget leg's *choice* of victims degrades.</para></summary>
public static class CatalogSweepSchedule
{
    /// <summary>Choose this pass's victims. Returns the plan; the victims' <see cref="SweepRow.Key"/>s are written
    /// into <paramref name="victims"/>, which also bounds the pass.</summary>
    public static SweepPlan Plan(
        ReadOnlySpan<SweepRow> rows, in SweepPolicy policy, int now, long cacheBytes, int queueDepth, Span<int> victims)
    {
        if (victims.IsEmpty || policy.MaxRowsPerPass <= 0) return new SweepPlan(0, 0, 0, 0, cacheBytes, false, false);

        // Step 0 (R2): a busy write lane owns the disk. A sweep is never urgent — the budget it enforces is a
        // ceiling, not a wall — so it yields, and reports that it did rather than reporting "nothing to do".
        if (queueDepth > policy.DeferQueueDepth) return new SweepPlan(0, 0, 0, 0, cacheBytes, true, true);

        int cap = Math.Min(policy.MaxRowsPerPass, victims.Length);
        int coldBefore = now - policy.TtlSeconds;     // touched at or before this ⇒ nobody has looked at it in a TTL
        int graceBefore = now - policy.GraceSeconds;  // answered after this ⇒ too new to judge (critique #11)
        int n = 0, ttl = 0, budget = 0;
        long freed = 0;

        // 1. TTL FILTER — the unpinned rows nobody has read for a TTL. Order-independent by construction.
        for (int i = 0; i < rows.Length && n < cap; i++)
        {
            ref readonly SweepRow r = ref rows[i];
            if ((r.Flags & SweepFlags.Pinned) != 0) continue;
            if (r.FetchedAt > graceBefore) continue;
            if (r.Touched > coldBefore) continue;
            victims[n++] = r.Key;
            ttl++;
            freed += r.Bytes;
        }
        bool more = n == cap;                         // stopped on the bound, not on the data

        // 2. BUDGET LRU — still too big after the cold rows went? Take the least recently used WARM rows, oldest
        //    first, down to the headroom mark. This is the leg 0.2.9 could not meet for two years, because the
        //    extension tier's bytes counted toward the budget and no sweep could delete them; here there is one
        //    tier and one budget, and the rows the ceiling reaches are exactly the rows it counts.
        long after = cacheBytes - freed;
        if (policy.ByteBudget > 0 && after > policy.ByteBudget)
        {
            long target = policy.ByteBudget * policy.HeadroomPercent / 100;
            for (int i = 0; i < rows.Length && n < cap && after > target; i++)
            {
                ref readonly SweepRow r = ref rows[i];
                if ((r.Flags & SweepFlags.Pinned) != 0) continue;
                if (r.FetchedAt > graceBefore) continue;
                if (r.Touched <= coldBefore) continue;   // the TTL leg already took it
                victims[n++] = r.Key;
                budget++;
                freed += r.Bytes;
                after -= r.Bytes;
            }
            more |= after > target;
        }

        return new SweepPlan(n, ttl, budget, freed, after, false, more);
    }
}

// ── 2. CORE: the schema and the per-kind seam ────────────────────────────────────────────────────────────────────────

/// <summary>A persisted column's storage class. Three, because sqlite has three that matter here.</summary>
public enum StoreType : byte { Int, Text, Blob }

/// <summary>What is special about a persisted column, beyond its type.</summary>
[Flags]
public enum StoreColumnFlags : byte
{
    None = 0,
    /// <summary>The kind's display title: gets <c>ix_&lt;table&gt;_title(scope_id, &lt;col&gt; COLLATE NOCASE)</c>, which
    /// is what makes library search a real indexed query and not a scan (P11). At most one per kind.</summary>
    Title = 1,
    /// <summary>A per-group authority column (<c>identity_auth</c>, <c>extras_auth</c>, …). Merged with <c>max()</c>
    /// on conflict rather than overwritten, so a Thin answer landing after a Full one cannot demote the row on disk
    /// the way it cannot demote it in memory (D16).</summary>
    Authority = 2,
}

/// <summary>One persisted column of one kind.</summary>
public readonly record struct StoreColumn(string Name, StoreType Type, StoreColumnFlags Flags = StoreColumnFlags.None);

/// <summary>How ONE kind's columns become a row, and back. The whole per-kind surface of the store, and the only
/// thing a kind owner writes to make their kind survive a restart.
///
/// <para>Both halves run on the STORE THREAD, so neither may touch a live column or the interner (C1): the currency
/// is <see cref="Staging"/> — its rows are values and its text is a UTF-8 arena the commit interns later (P14). That
/// is also why <see cref="Save"/> takes the staging that was just committed rather than the table: the batch the
/// provider answered with is exactly the batch that should reach the disk, and it is already a snapshot.</para>
///
/// <para>The shape declares its columns; <see cref="Store"/> generates the <c>CREATE TABLE</c>, the two indexes, the
/// upsert and the select from them, so adding a column to a kind is one entry here plus one line in each half.</para></summary>
public abstract class KindShape
{
    /// <summary>Which kind's rows this shape persists. One shape per kind.</summary>
    public abstract EntityKind Kind { get; }

    /// <summary>The sql table name (plan §4.4: <c>track</c>, <c>album</c>, <c>artist</c>, …). Lowercase, no quoting.</summary>
    public abstract string Table { get; }

    /// <summary>The kind's own columns, in a FIXED order — the order <see cref="Save"/> and <see cref="Load"/> index
    /// them by. Appending is free (the fingerprint changes, so the stale cache file is dropped); reordering is too,
    /// for the same reason. Return a span over a <c>static readonly</c> array, never a fresh one.</summary>
    public abstract ReadOnlySpan<StoreColumn> Columns { get; }

    /// <summary>Write every staged row of this kind into the prepared upsert: bind the kind's columns by index, then
    /// call <see cref="RowWriter.Emit"/> with the row's uri and bookkeeping. Store thread.</summary>
    public abstract void Save(Staging s, RowWriter w);

    /// <summary>Turn ONE sqlite row into one staged row of this kind (append it to the staging's list). Store thread:
    /// copy text with <see cref="RowReader.Text"/>, which lands it in the arena, and never intern.</summary>
    public abstract void Load(RowReader r, Staging into);
}

/// <summary>The bind side of a row. Owned by the store thread; a shape only ever calls it from
/// <see cref="KindShape.Save"/>. Every column resets to NULL after each <see cref="Emit"/>, so a row that does not
/// set a column writes NULL rather than the previous row's value — and NULL is what the upsert's
/// <c>coalesce(excluded.x, x)</c> reads as "this answer said nothing about that column".</summary>
public sealed class RowWriter
{
    internal SqliteCommand Cmd = null!;
    internal SqliteParameter[] Cols = [];
    internal SqliteParameter PUri = null!, PKnown = null!, PFetched = null!, PTouched = null!;
    internal Staging S = null!;
    internal int Rows;

    /// <summary>How many of the kind's own columns this shape declared — a shape can assert against it.</summary>
    public int ColumnCount => Cols.Length;

    public void Int(int i, long value) => Cols[i].Value = value;

    /// <summary>Bind text from the staging arena. UTF-8 → UTF-16 here because <c>Microsoft.Data.Sqlite</c> binds
    /// <see cref="string"/>; it is one string per non-empty cell on a background thread and it is the store's only
    /// per-row allocation. (SQLitePCLRaw's <c>sqlite3_bind_text</c> takes the bytes directly if a profile ever asks.)</summary>
    public void Text(int i, TextRef t) => Cols[i].Value = t.IsEmpty ? DBNull.Value : (object)Encoding.UTF8.GetString(S.Utf8(t));

    public void Bytes(int i, ReadOnlySpan<byte> blob) => Cols[i].Value = blob.IsEmpty ? DBNull.Value : (object)blob.ToArray();

    public void Null(int i) => Cols[i].Value = DBNull.Value;

    /// <summary>Bind a CROSS-REFERENCE column from a <see cref="StagedId"/> — a track's <c>album_uri</c>, a show's
    /// owner, an episode's parent show: another row's identity, staged in whichever form the wire gave it. Text-form
    /// binds the arena <see cref="TextRef"/> directly, exactly as <see cref="Text"/> does (free — the bytes are
    /// already in <paramref name="s"/>'s arena). Gid-form is the one this method exists for: unlike the row's OWN key
    /// (<see cref="Emit(in EntityId,uint,int,int)"/>, which binds straight to its own dedicated parameter), a generic
    /// column can only be bound through <see cref="Text"/>, so the packed id is formatted to a stack buffer, its UTF-8
    /// bytes are copied into the staging arena (one more row's worth, same arena the shape's own rows already use),
    /// and the resulting <see cref="TextRef"/> is bound — one string per non-null cross-reference, still never through
    /// the interner (file header). Empty or a stray text-form value inside <see cref="StagedId.Packed"/> (never
    /// produced by a decoder, but not this thread's to assume) both bind NULL — the latter counted via
    /// <see cref="Store.BadKey"/>, same as the row-key door.</summary>
    public void Id(int i, Staging s, in StagedId id)
    {
        if (!id.Text.IsEmpty) { Text(i, id.Text); return; }
        if (id.Packed.IsEmpty) { Null(i); return; }
        if (id.Packed.Form != EntityForm.Gid) { Store.BadKey(id.Packed.Form); Null(i); return; }
        Span<char> chars = stackalloc char[EntityId.MaxGidTextChars];
        int n = id.Packed.Format(chars);
        Span<byte> bytes = stackalloc byte[EntityId.MaxGidTextChars * 3];
        int written = Encoding.UTF8.GetBytes(chars[..n], bytes);
        Text(i, s.AddText(bytes[..written]));
    }

    /// <summary>The <see cref="StagedId"/> spelling of the row's own key: dispatches to whichever form the wire
    /// actually gave, so a shape's <c>Save</c> loop never has to ask which overload its own row's identity needs.
    /// <para><b>A TEXT-form identity must arrive as <see cref="StagedId.Text"/> — arena bytes — never as a text-form
    /// <see cref="EntityId"/> in <see cref="StagedId.Packed"/>.</b> A text-form <c>EntityId</c>'s payload is an index
    /// into the PROCESS-LOCAL interner, and this runs on the STORE THREAD, which may never touch the interner (file
    /// header). So the packed branch below can only serve a GID; handed a text-form id it counts
    /// <see cref="Store.BadKey"/> and DROPS the row. That is easy to trip over because
    /// <c>StagedId</c> has an IMPLICIT conversion from <c>EntityId</c>, so assigning a live table's
    /// <c>Id[slot]</c> compiles and then silently persists nothing. A decoder always has the uri's UTF-8 in hand and
    /// stages <c>s.AddText(...)</c>; anything reading an id back out of a table must format it on the UI thread
    /// first.</para></summary>
    public void Emit(in StagedId id, uint known, int fetchedAt, int touched)
    {
        if (!id.Text.IsEmpty) { Emit(id.Text, known, fetchedAt, touched); return; }
        System.Diagnostics.Debug.Assert(id.Packed.IsEmpty || id.Packed.Form == EntityForm.Gid,
            "StagedId.Packed carries a TEXT-form EntityId: the store thread cannot resolve it, so the row would be " +
            "dropped. Stage the uri as arena text (s.AddText) instead.");
        Emit(id.Packed, known, fetchedAt, touched);
    }

    /// <summary>Bind the shared bookkeeping and write the row, keyed by the uri the decoder staged.
    /// <paramref name="fetchedAt"/> and <paramref name="touched"/> are APP seconds (P7); they are converted to unix
    /// seconds here, because the file outlives the epoch they are measured from.
    /// <para>The bytes come out of the staging arena, so this overload is safe for EVERY id form — it never touches
    /// the interner (file header). A decoder holding a packed gid instead of text uses the overload below.</para></summary>
    public void Emit(TextRef uri, uint known, int fetchedAt, int touched)
    {
        if (uri.IsEmpty) { Reset(); return; }                     // a row with no uri is not addressable — drop it
        PUri.Value = Encoding.UTF8.GetString(S.Utf8(uri));
        Write(known, fetchedAt, touched);
    }

    /// <summary>The same row, keyed by an identity that is ALREADY PACKED — what a Wave-2 decoder holds the moment a
    /// protobuf answer lands: 16 raw gid bytes, and no uri text anywhere in the process (doc §2). The key is
    /// formatted here, on the store thread, into a stack buffer: one string per row, off the frame.
    ///
    /// <para><b>GID FORM ONLY.</b> A text-form id's payload is an index into the process-wide interner and this
    /// thread may not resolve it (file header), so such a row is DROPPED and counted (<c>Stats.BadKeys</c>) rather
    /// than keyed by a string that may already have been reclaimed. A staged row whose uri is text already carries its bytes in the
    /// arena — that is <see cref="Emit(TextRef,uint,int,int)"/>, and it is what a decoder with text does.</para></summary>
    public void Emit(in EntityId id, uint known, int fetchedAt, int touched)
    {
        if (id.Form != EntityForm.Gid) { Store.BadKey(id.Form); Reset(); return; }
        Span<char> buf = stackalloc char[EntityId.MaxGidTextChars];
        PUri.Value = new string(buf[..id.Format(buf)]);
        Write(known, fetchedAt, touched);
    }

    void Write(uint known, int fetchedAt, int touched)
    {
        PKnown.Value = (long)known;
        PFetched.Value = Store.ToUnix(fetchedAt);
        PTouched.Value = Store.ToUnix(touched);
        Cmd.ExecuteNonQuery();
        Rows++;
        Reset();
    }

    /// <summary>Every column back to NULL. A DROPPED row resets too: otherwise its half-bound columns ride out with
    /// the NEXT row, which is the one way a coalescing upsert can persist a value nobody answered.</summary>
    void Reset()
    {
        for (int i = 0; i < Cols.Length; i++) Cols[i].Value = DBNull.Value;
    }
}

/// <summary>The read side of a row. Ordinals 0-3 are the shared bookkeeping; a shape's own columns start at 4 and
/// are addressed by the index they were declared at, so a shape never counts ordinals.</summary>
public sealed class RowReader
{
    internal SqliteDataReader R = null!;
    internal Staging S = null!;
    const int First = 4;

    /// <summary>The row's uri, copied into the staging arena as UTF-8 (P14: the commit resolves it, not this thread).
    ///
    /// <para><b>THE ROUND TRIP.</b> <c>uri TEXT</c> is the persisted spelling of the row's <see cref="EntityId"/>
    /// (file header), and the commit turns it back into one through <c>table.Slot(s.Utf8(row.Uri))</c>: a
    /// <c>spotify:&lt;kind&gt;:&lt;22 base62&gt;</c> key parses straight back to the same packed gid with no intern at
    /// all, and every other spelling interns back to the same text form. A row written and read back is the SAME id
    /// and therefore the same slot — <c>StoreTests</c> pins that in both forms, because it is the whole contract a
    /// text key has to meet.</para></summary>
    public TextRef Uri => Utf8(0);

    /// <summary>The field-group bits this row carried when it was written — the whole point of persisting: a cold
    /// start knows what it knows without asking a provider.</summary>
    public uint Known => R.IsDBNull(1) ? 0u : (uint)R.GetInt64(1);

    /// <summary>App seconds (P7), converted back from the unix seconds on disk. NEGATIVE for a row written by an
    /// earlier launch, which is correct: it is older than this process's epoch.</summary>
    public int FetchedAt => R.IsDBNull(2) ? 0 : Store.ToApp(R.GetInt64(2));

    /// <inheritdoc cref="FetchedAt"/>
    public int Touched => R.IsDBNull(3) ? 0 : Store.ToApp(R.GetInt64(3));

    public long Int(int i) => R.IsDBNull(First + i) ? 0L : R.GetInt64(First + i);

    /// <summary>A text column, copied into the staging arena. Empty when the column is NULL.</summary>
    public TextRef Text(int i) => Utf8(First + i);

    public byte[]? Blob(int i) => R.IsDBNull(First + i) ? null : R.GetFieldValue<byte[]>(First + i);

    TextRef Utf8(int ordinal)
    {
        if (R.IsDBNull(ordinal)) return default;
        string s = R.GetString(ordinal);
        if (s.Length == 0) return default;
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        byte[] scratch = ArrayPool<byte>.Shared.Rent(max);
        try { return S.AddText(scratch.AsSpan(0, Encoding.UTF8.GetBytes(s, scratch))); }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }
}

// ── the edge seam ────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Which relation an <c>edge</c> row belongs to. <b>These numbers are PERSISTED</b> (the <c>edge.kind</c>
/// column) — append only, never renumber, or every cached album's tracks read back as somebody's liked songs. The
/// same discipline 0.2.9's <c>Backend.Metadata.EntityKind</c> was pinned by, for the same reason.
/// <para>Only relations whose targets are ENTITY rows are here: <c>AlbumMerch</c> points at <see cref="MerchTable"/>
/// slots, not at uris, so it has no <c>child TEXT</c> to write and is memory-only until merch gets a uri. The
/// session relations (<c>Queue</c>, <c>HomeSection</c>, <c>SearchResult</c>, <c>Friends</c>) are deliberately absent:
/// they are answers to "right now", and a restart is a new now (ch 13 §7 says so for search in terms).</para></summary>
public enum EdgeRelation : byte
{
    None = 0,
    TrackArtists = 1,
    AlbumArtists = 2,
    AlbumTracks = 3,
    ArtistReleases = 4,
    ArtistAppearsOn = 5,
    ArtistRelated = 6,
    ArtistPopular = 7,
    ShowEpisodes = 8,
    PlaylistTracks = 9,
    Liked = 10,
    SavedAlbums = 11,
    FollowedArtists = 12,
    SavedShows = 13,
    Pins = 14,
    Rootlist = 15,
    TrackTags = 16,
    AlbumFeaturedOn = 17,
    AlbumSimilar = 18,
}

/// <summary>One parent's edge list, read back off the disk and posted to the UI thread. The children arrive as uri
/// TEXT because that is what survives a restart (a slot does not, and neither does a packed id's text form, whose
/// payload is an interner index — file header); the applier maps them to slots with <c>table.Slot(uri)</c>, which
/// parses a catalog key back to its gid with no intern at all and allocates only for a uri this session has never
/// seen (P6).</summary>
public sealed class EdgePage
{
    public EdgeRelation Relation;
    /// <summary>The parent's IDENTITY, parsed back from the persisted key on the UI thread before the applier runs
    /// (<see cref="EntityId.Parse(ReadOnlySpan{char})"/> interns for the text form, which is exactly why it happens
    /// there and not on the store thread).</summary>
    public EntityId Parent;
    public EdgeState State;
    public int Total;
    public int Count;
    /// <summary>The children, in ordinal order. Only the first <see cref="Count"/> entries are live.</summary>
    public string[] Children = [];
    /// <summary>The payloads, packed end to end, <see cref="Stride"/> bytes each. Empty when the relation's payload
    /// is <see cref="NoEdge"/> or the rows were written without one.</summary>
    public byte[] Payload = [];
    public int Stride;

    /// <summary>The payload span, typed. The applier knows the type; the store never does.</summary>
    public ReadOnlySpan<TEdge> PayloadAs<TEdge>() where TEdge : unmanaged
        => Stride == 0 || Payload.Length == 0
            ? default
            : MemoryMarshal.Cast<byte, TEdge>(Payload.AsSpan(0, Count * Stride));
}

// ── 3. SHELL: the store ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>sqlite as columns (plan §4.4). One file, one thread, two connections, four ways in: <see cref="Read"/>
/// (cold read), <see cref="WriteBehind"/> (a committed batch), <see cref="Journal"/> (a user intent, synchronous)
/// and <see cref="Tick"/> (the GC). Nothing else may reach the database, and nothing here may reach a live column.</summary>
public static partial class Store
{
    /// <summary>Bumped whenever the generated DDL changes shape in a way the fingerprint cannot see (it can see
    /// almost everything). Part of the fingerprint, so bumping it drops every cached file.</summary>
    public const int SchemaVersion = 4;

    /// <summary>Bounded, per C8. A full queue is a signal, not a wait: writes are dropped (the provider can answer
    /// again) and reads are refused (the planner un-marks in-flight and the row is asked again next drain).</summary>
    public const int QueueCapacity = 4096;

    /// <summary>Uris per cold-read <c>IN (…)</c> query. The same 300 the wire batches by (see <c>Fetch</c>): one
    /// number for "how many entities is a batch", so a page's demand is one shape all the way down (P4).</summary>
    public const int ReadChunk = 300;

    /// <summary>Rows per DELETE batch in a sweep — each batch its own transaction, so a pass can be abandoned
    /// between any two and the file is still consistent (0.2.9 <c>GcDeleteBatchRows</c>).</summary>
    public const int DeleteBatch = 1_000;

    /// <summary>Freelist pages reclaimed per pass. Slices, never a routine full VACUUM (§C.7, the SSD-wear incident).</summary>
    public const int VacuumPagesPerPass = 200;

    /// <summary>Warm + 30 s before the first sweep, then every 6 h — 0.2.9's cadence, and for its reason: a sweep
    /// during the first paint competes with exactly the reads that make the first paint.</summary>
    public const int FirstSweepDelaySeconds = 30;
    public const int SweepPeriodSeconds = 6 * 60 * 60;

    // ── the UI-thread seam (C1) ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE marshaller back to the UI thread. <c>App.cs</c> sets it to <c>AppHost.Post</c>; a test sets it to
    /// a list it drains itself; the default runs the action inline, which is what makes every unit test in this
    /// file's suite single-threaded and deterministic.
    /// <para>It is settable rather than injected because there is exactly one UI thread and both SHELL files in
    /// <c>Entities/</c> post through it — a constructor parameter would be threaded through every call site to
    /// deliver the same value.</para></summary>
    public static Action<Action> Post { get; set; } = static a => a();

    /// <summary>The unix second that <c>Entities.Now == 0</c> means. Defaults to this process's start and is
    /// self-consistent by construction: <c>Entities.Now</c> is defined as <c>Store.ToApp(unixNow)</c>, so the two
    /// always agree without either seeding the other. In-memory time is app seconds (P7: an <c>int</c> column, not a
    /// <c>DateTime</c>), on-disk time is unix seconds, and these two are the conversion.
    /// <para><b>NOT <c>Clock.SeedEpoch</c>.</b> That constant is a FIXED PAST DATE used only to seed <c>--fake</c>
    /// data with plausible-looking ages; seeding this <see cref="Epoch"/> from it would stamp every REAL row weeks
    /// into the past and corrupt the sweep's TTL arithmetic (2026-09-15).</para></summary>
    public static long Epoch { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static long ToUnix(int appSeconds) => Epoch + appSeconds;

    /// <summary>Unix seconds → app seconds. Negative for anything written before this launch — which is correct and
    /// load-bearing: the sweep's "cold" test is a subtraction, and a row from last week must read as older than 0.</summary>
    public static int ToApp(long unixSeconds)
    {
        long v = unixSeconds - Epoch;
        return v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;
    }

    // ── the uri key: a packed identity on one side, `uri TEXT` on the other (file header) ───────────────────────────

    /// <summary>UI THREAD. Snapshot the identities of <paramref name="slots"/> for a job that is about to cross to
    /// the store thread: the packed ids (a 24-byte column copy — no string, no interner, no allocation), plus the
    /// resolved text for the TEXT-form rows only, which is free because that string already exists.
    /// <para>This is the whole of "the format moved off the frame". A gid row's <c>text</c> entry stays null and its
    /// key is built by <see cref="KeyOf"/> on the store thread; a text row's cannot be, because resolving an id off
    /// the UI thread is only safe while the id is alive and a queued job outlives that guarantee (file header).</para></summary>
    static void SnapshotKeys(Table table, ReadOnlySpan<int> slots, int[] slotCopy, EntityId[] ids, string?[] text)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            int slot = slots[i];
            EntityId id = table.Id[slot];
            slotCopy[i] = slot;
            ids[i] = id;
            text[i] = id.Form == EntityForm.Text ? Entities.Strings.Resolve(id.TextId) : null;
        }
    }

    /// <summary>STORE THREAD. The <c>uri TEXT</c> key for one row: the string the UI thread resolved for a text-form
    /// id, or 81 ns of base62 for a gid one. Empty for a slot with no identity at all — such a row cannot be on disk,
    /// and the upsert drops it.</summary>
    static string KeyOf(in EntityId id, string? text)
    {
        if (text is not null) return text;
        if (id.Form != EntityForm.Gid) return "";
        Span<char> buf = stackalloc char[EntityId.MaxGidTextChars];
        return new string(buf[..id.Format(buf)]);
    }

    /// <summary>UI THREAD. One key, for the cold paths that carry a single identity (the account's own row, an edge
    /// parent). Resolving is legal here; formatting is legal anywhere.</summary>
    static string KeyText(in EntityId id)
    {
        if (id.Form == EntityForm.Text) return Entities.Strings.Resolve(id.TextId);
        if (id.Form != EntityForm.Gid) return "";
        Span<char> buf = stackalloc char[EntityId.MaxGidTextChars];
        return new string(buf[..id.Format(buf)]);
    }

    /// <summary>A row reached <see cref="RowWriter.Emit(in EntityId,uint,int,int)"/> with an identity the store
    /// thread cannot key (see there). Its own counter rather than a fault: a fault is the database misbehaving and
    /// the suite asserts there are none, while this is a shape staging the wrong thing — visible, and never silent.</summary>
    internal static void BadKey(EntityForm form)
    {
        s_badKeys++;
        Debug.WriteLine($"[store] a staged row was emitted with a {form}-form id: only a gid can be keyed off the store thread");
    }

    /// <summary>WAL so a reader never blocks the writer; NORMAL because this is a cache and an fsync per commit
    /// buys durability nobody needs; a busy timeout so a slow checkpoint is a wait and not an exception; incremental
    /// auto-vacuum so the sweep can reclaim in slices instead of a full VACUUM (§C.7).</summary>
    const string Pragmas = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA auto_vacuum=INCREMENTAL;";

    // ── state ───────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class Db(string path, SqliteConnection write, SqliteConnection read)
    {
        public readonly string Path = path;
        public readonly SqliteConnection Write = write;
        public readonly SqliteConnection Read = read;
        /// <summary>Guards <see cref="Write"/>: the store thread and the synchronous journal both use it (D22).</summary>
        public readonly object WriteLock = new();
    }

    sealed class ShapeSql(KindShape shape)
    {
        public readonly KindShape Shape = shape;
        public readonly RowWriter Writer = new();
        public SqliteCommand? Upsert;
        public string? SelectHead;
    }

    static string? s_path;
    static Db? s_db;
    static Thread? s_thread;
    // NOT readonly: `CompleteAdding` is permanent, so a shutdown retires the queue and the next Boot mints a
    // fresh one. (A sign-out/sign-in cycle re-Boots the store; a single instance would silently refuse every job.)
    static BlockingCollection<Action> s_queue = new(QueueCapacity);
    static readonly ShapeSql?[] s_shapes = new ShapeSql?[16];        // indexed by (byte)EntityKind
    static readonly Action<EdgePage>?[] s_edgeAppliers = new Action<EdgePage>?[64];
    /// <summary>The whole `meta` table, warmed into memory (see <see cref="MetaGet"/>/<see cref="MetaSet"/>). UI
    /// thread only: written by <see cref="MetaSet"/> and by <see cref="WarmMetaCore"/>'s <see cref="Post"/>-back.</summary>
    static readonly Dictionary<string, string> s_meta = new();
    static volatile bool s_open;
    static volatile bool s_stopping;
    static long s_scopeId;                                            // resolved on the store thread, read there only
    static SweepPolicy s_policy = SweepPolicy.Default;
    static int s_nextSweepAt;                                         // app seconds
    static bool s_paletteWarmedOnce;                                  // Store.Palette.cs's Boot guard

    // Always-on counters (CLAUDE.md: no env-var switches; the diagnostics page reads these).
    static int s_reads, s_readRows, s_writes, s_writeRows, s_dropped, s_sweeps, s_evicted, s_faults, s_trimmed, s_badKeys;

    /// <summary>What the store has done this session — the diagnostics page's row, and the only observability this
    /// file has (there is no debug flag to turn anything else on).</summary>
    /// <param name="Trimmed">Rows <see cref="TrimMemory(Scope,Table,in SweepPolicy,int)"/> has retired from MEMORY
    /// this session — the leg that actually hands interned text back (defect 1), as distinct from
    /// <paramref name="Evicted"/>, which is rows deleted from the FILE.</param>
    /// <param name="BadKeys">Staged rows a shape emitted with an identity this thread cannot key
    /// (<see cref="RowWriter.Emit(in EntityId,uint,int,int)"/>). Its own number and not a
    /// <paramref name="Faults"/>, because it is a programming error in a shape and not the database misbehaving —
    /// two conditions with two fixes.</param>
    public readonly record struct StoreStats(
        bool Open, string? Path, int Queued, int Reads, int ReadRows, int Writes, int WriteRows,
        int Dropped, int Sweeps, int Evicted, int Faults, int Trimmed, int BadKeys);

    public static StoreStats Stats => new(
        s_open, s_path, s_queue.Count, s_reads, s_readRows, s_writes, s_writeRows, s_dropped, s_sweeps, s_evicted,
        s_faults, s_trimmed, s_badKeys);

    /// <summary>Is there a database at all? <c>--fake</c> and every unit test answer no, and every call below is then
    /// a no-op that returns false — which is why nothing in this layer needs a null store to reason about.</summary>
    public static bool IsOpen => s_open;

    /// <summary>The eviction knobs. Settings writes the byte budget here; the sweep reads it on its next pass.</summary>
    public static SweepPolicy Policy
    {
        get => s_policy;
        set => s_policy = value;
    }

    // ── boot ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Point the store at a file. <c>App.cs</c> calls this before <c>Entities.Boot</c> with
    /// <c>%LOCALAPPDATA%\Wavee\library.db</c> (packaged: the package's <c>LocalCache</c>) — and deliberately does NOT
    /// call it under <c>--fake</c>, which is the whole of "the demo catalog is not persisted". No path, no database,
    /// no thread, no file touched.
    /// <para>Pass null (or an empty path) to detach: the next <see cref="Boot"/> opens nothing. That is the whole
    /// of "the demo catalog is not persisted", and the whole of a test asking for a memory-only graph.</para></summary>
    public static void Use(string? path) => s_path = string.IsNullOrEmpty(path) ? null : path;

    /// <summary>Register a kind's persistence shape. Before <see cref="Boot"/>: the registered set generates the
    /// schema, and therefore the fingerprint that decides whether the existing file can be kept.</summary>
    public static void Register(KindShape shape)
    {
        int i = (byte)shape.Kind;
        if ((uint)i >= (uint)s_shapes.Length) throw new ArgumentOutOfRangeException(nameof(shape), "unknown kind");
        if (s_open) throw new InvalidOperationException("Store.Register must run before Store.Boot: the registered shapes ARE the schema.");
        s_shapes[i] = new ShapeSql(shape);
    }

    /// <summary>Register who applies a relation's rows when they come back off the disk. The kind that owns the
    /// relation owns the applier, because only it knows the payload type and which table the children live in.</summary>
    public static void RegisterEdges(EdgeRelation relation, Action<EdgePage> apply)
        => s_edgeAppliers[(byte)relation] = apply;

    /// <summary>Register the appliers for the five library relations this build persists — every relation whose
    /// payload is <see cref="LibraryEdge"/> (Liked, SavedAlbums, FollowedArtists, SavedShows, Pins). MUST run before
    /// <see cref="Boot"/> (<c>App.cs</c>, alongside <c>RegisterShapes</c>): <see cref="Warm"/> fires inside
    /// <c>Entities.Boot</c>, and a page whose applier slot is still null is simply dropped.
    ///
    /// <para><b>Everything else stays off disk, on purpose.</b> Rootlist (<see cref="RootlistEdge"/>), PlaylistTracks
    /// (<see cref="PlaylistTrackEdge"/>), TrackTags (<see cref="StringId"/>), TrackCredits (<see cref="CreditEdge"/>)
    /// and Friends (<see cref="FriendEdge"/>) all carry a <see cref="StringId"/> in their payload — an index into the
    /// PROCESS-LOCAL interner (Edges.cs's file header) — and <see cref="SaveEdges{TEdge}"/> writes a payload as its
    /// raw bytes (that method's own doc). Replaying those bytes on a later launch would resolve to whatever string
    /// that same numeric id happens to mean THIS time, unrelated to what was saved; those relations stay answered by
    /// the network only.</para></summary>
    public static void RegisterLibraryEdges()
    {
        RegisterEdges(EdgeRelation.Liked,
            page => ApplyLibraryEdge(page, Entities.Current.Edges.Liked, Entities.Current.Tracks));
        RegisterEdges(EdgeRelation.SavedAlbums,
            page => ApplyLibraryEdge(page, Entities.Current.Edges.SavedAlbums, Entities.Current.Albums));
        RegisterEdges(EdgeRelation.FollowedArtists,
            page => ApplyLibraryEdge(page, Entities.Current.Edges.FollowedArtists, Entities.Current.Artists));
        RegisterEdges(EdgeRelation.SavedShows,
            page => ApplyLibraryEdge(page, Entities.Current.Edges.SavedShows, Entities.Current.Shows));
        RegisterEdges(EdgeRelation.Pins, ApplyPinsEdge);
    }

    /// <summary>Liked/SavedAlbums/FollowedArtists/SavedShows share this shape: every child is the SAME entity kind,
    /// so one <paramref name="childTable"/> maps every uri (<see cref="Table.Slot(ReadOnlySpan{char})"/>). UI thread
    /// (<see cref="RegisterEdges"/>'s contract).
    /// <para>THE PAYLOAD'S SHAPE IS NOT COVERED BY THE SCHEMA FINGERPRINT (<see cref="SaveEdges{TEdge}"/>'s doc): a
    /// page whose <see cref="EdgePage.Stride"/> disagrees with <c>sizeof(LibraryEdge)</c> is DROPPED rather than
    /// reinterpreted as garbage. A page with no rows at all (<see cref="EdgePage.Count"/> 0 — a real, answered "you
    /// have none of these") has nothing to misread and always lands.</para></summary>
    static void ApplyLibraryEdge(EdgePage page, EdgeTable<LibraryEdge> table, Table childTable)
    {
        int parent = Entities.Current.Users.Slot(page.Parent);
        if (parent == Table.None) return;
        if (page.Count > 0 && page.Stride != Unsafe.SizeOf<LibraryEdge>()) return;

        int n = page.Count;
        int[] targets = n == 0 ? Array.Empty<int>() : new int[n];
        for (int i = 0; i < n; i++)
        {
            string uri = page.Children[i];
            targets[i] = uri.Length == 0 ? Table.None : childTable.Slot(uri.AsSpan());
        }
        table.Replace(parent, targets, page.PayloadAs<LibraryEdge>(), page.State, page.Total);
    }

    /// <summary>Pins are CROSS-KIND (<see cref="PinKind"/>'s doc): a playlist, an album, an artist, a show, the Liked
    /// collection or a rootlist folder, and which TABLE a target indexes rides the edge's OWN payload byte, not one
    /// fixed for the whole relation — so this applier resolves each child by its own kind rather than sharing
    /// <see cref="ApplyLibraryEdge"/>'s one-table shape. A folder pin's disk text is its bare group id (never a table
    /// row — <see cref="PinKind.Folder"/>'s doc) and is re-interned to the very <see cref="StringId"/> value
    /// <see cref="LibraryEdge.Flags"/> already names Folder targets by; Liked and an unrecognised kind both target
    /// <see cref="Table.None"/>. Same stride discipline as <see cref="ApplyLibraryEdge"/>.</summary>
    static void ApplyPinsEdge(EdgePage page)
    {
        int parent = Entities.Current.Users.Slot(page.Parent);
        if (parent == Table.None) return;
        if (page.Count > 0 && page.Stride != Unsafe.SizeOf<LibraryEdge>()) return;

        ReadOnlySpan<LibraryEdge> payload = page.PayloadAs<LibraryEdge>();
        int n = page.Count;
        int[] targets = n == 0 ? Array.Empty<int>() : new int[n];
        for (int i = 0; i < n; i++)
        {
            string uri = page.Children[i];
            var kind = i < payload.Length ? (PinKind)payload[i].Flags : PinKind.Unknown;
            if (uri.Length == 0) { targets[i] = Table.None; continue; }
            if (kind == PinKind.Folder) { targets[i] = Entities.Strings.Intern(uri.AsSpan()).Value; continue; }
            Table? t = kind is PinKind.Unknown or PinKind.Liked ? null : Entities.TableFor(User.EntityKindOf(kind));
            targets[i] = t?.Slot(uri.AsSpan()) ?? Table.None;
        }
        Entities.Current.Edges.Pins.Replace(parent, targets, payload, page.State, page.Total);
    }

    /// <summary>Open the file (creating or REPLACING it) and start the store thread. Called by
    /// <c>Entities.Boot</c> through the <c>StoreBoot</c> hook; a no-op when <see cref="Use"/> was never called.</summary>
    public static void Boot()
    {
        if (s_open || s_path is null) return;
        try
        {
            string? dir = Path.GetDirectoryName(s_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            s_db = Open(s_path);
            // The prepared upserts belong to the connection that is being replaced: drop them, or the first write
            // after a re-Boot executes against a closed handle.
            for (int i = 0; i < s_shapes.Length; i++)
                if (s_shapes[i] is { } sql) { sql.Upsert?.Dispose(); sql.Upsert = null; }
            s_meta.Clear();   // this may be a DIFFERENT file (a re-Boot after Shutdown): nothing carries over
            s_open = true;
            s_stopping = false;
            s_nextSweepAt = Entities.Now + FirstSweepDelaySeconds;
            s_queue = new BlockingCollection<Action>(QueueCapacity);
            BlockingCollection<Action> queue = s_queue;
            s_thread = new Thread(() => Loop(queue)) { IsBackground = true, Name = "wavee-store" };
            s_thread.Start();
            // FIRST, before `Warm` (Entities.Boot calls `StoreWarm` right after this returns): the palette is
            // process-wide, not scoped, so it has no scope id to wait for. Once per boot — reset at `Shutdown`.
            if (!s_paletteWarmedOnce)
            {
                s_paletteWarmedOnce = true;
                Enqueue(WarmPaletteCore);
            }
        }
        catch (Exception ex)
        {
            // A cache that will not open must not stop the app: the provider is still there. Count it and run
            // memory-only — the one behaviour 0.2.9 also had, and the reason it survived a corrupt file in the field.
            s_faults++;
            Fault("open", ex);
            s_db = null;
            s_open = false;
        }
    }

    static Db Open(string path)
    {
        var write = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        try
        {
            write.Open();
            Exec(write, Pragmas);
        }
        catch (SqliteException ex) when (IsUnreadableFile(ex.SqliteErrorCode))
        {
            // THE PRAGMAS ARE THE FIRST STATEMENTS THAT TOUCH THE FILE, so a malformed one throws HERE — before
            // `ReadFingerprint`'s unreadable-file verdict can ever run. 2026-09-18: `journal_mode=WAL` over a corrupt
            // library.db raised SQLITE_CORRUPT on every launch, `Boot` caught it and the whole day ran memory-only
            // (every page a cold network load, nothing persisted) with `store.fault step=open` as the only trace.
            // Same verdict as a foreign file: it is a cache, delete it and start clean.
            write.Dispose();
            SqliteConnection.ClearAllPools();
            bool gone = Delete(path);
            Log.Event(WaveeLogLevel.Warning, "store", "store.dropped",
                "the cache file could not be opened and was recreated", null, -1, null,
                WaveeLogField.Of("why", $"unreadable:{ex.SqliteErrorCode}"), WaveeLogField.Of("deleted", gone));
            if (!gone) throw;                                // undeletable AND unreadable: memory-only is the honest outcome
            write = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            write.Open();
            Exec(write, Pragmas);
        }

        string ddl = Ddl();
        ulong want = Fingerprint(ddl);
        if (ReadFingerprint(write, out string why) is { } have && have != want)
        {
            // A CACHE, NOT A DOCUMENT (plan §4.4). Every row here can be asked for again; a migration would be a
            // second schema to keep correct forever, for data whose whole value is that it saves one round trip.
            write.Close();
            write.Dispose();
            SqliteConnection.ClearAllPools();               // release the file handles the driver's pool is holding
            bool gone = Delete(path);
            write = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            write.Open();
            Exec(write, Pragmas);
            // The file survived the delete (another process, an antivirus, a handle the pool did not release). Empty
            // it instead: `CREATE TABLE IF NOT EXISTS` over a table with the OLD columns is a silent no-op, and the
            // first read of a column this build expects would fail forever. Same outcome, one more statement.
            // ALWAYS-ON (CLAUDE.md: no debug switches), and ONE line: losing the whole cache is the kind of thing a
            // "why is nothing cached today" report has to be able to read off the log. `why` says which of the three
            // verdicts fired — a stale schema, a foreign file, or a file sqlite itself could not read — and `deleted`
            // says whether the file really went. BEFORE the drop, not after: `DropEverything` reads `sqlite_master`, so
            // it is the one statement that can still throw on an undeletable MALFORMED file, and this line has to be in
            // the log when it does (a `store.fault step=open` follows it and memory-only is then the honest outcome).
            Log.Event(WaveeLogLevel.Warning, "store", "store.dropped",
                "the cache file did not match this build and was recreated", null, -1, null,
                WaveeLogField.Of("why", why), WaveeLogField.Of("found", $"{have:x16}"),
                WaveeLogField.Of("want", $"{want:x16}"), WaveeLogField.Of("deleted", gone));
            if (!gone) DropEverything(write);
        }
        Exec(write, ddl);
        Exec(write, $"INSERT OR REPLACE INTO meta(key,value) VALUES('schema','{want:x16}');");

        // The reader is a SECOND connection so a cold read never queues behind the writer's transaction. Read-only
        // attach can fail on filesystems that will not let it create the -shm; a second read/write connection is
        // still the point, it just is not enforced by the driver then (0.2.9 SqliteColdStore.OpenReader).
        SqliteConnection read;
        try
        {
            read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
            read.Open();
        }
        catch (SqliteException)
        {
            read = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            read.Open();
        }
        return new Db(path, write, read);
    }

    /// <summary>The file's schema fingerprint, and <paramref name="why"/> for the log line if it loses. THREE outcomes,
    /// and the difference between the last two is a whole session's persistence:
    /// <list type="bullet">
    /// <item>a real fingerprint — compare it with this build's and delete the file if it differs (<c>why = "stale"</c>).</item>
    /// <item><c>0</c> — a FOREIGN file. It has a <c>meta</c> table with no schema row, or sqlite could not read it as a
    /// database at all. 0 never equals a real fingerprint, so <see cref="Open"/> deletes and recreates it.</item>
    /// <item><c>null</c> — a BRAND-NEW file: <c>meta</c> does not exist yet, so there is nothing to drop and the DDL
    /// right after this simply creates it.</item>
    /// </list>
    /// <para>A MALFORMED FILE USED TO COME BACK <c>null</c> WITH THE BRAND-NEW ONES, and that was a whole-process
    /// outage: every <see cref="SqliteException"/> read as "no meta table at all ⇒ nothing to drop", so a corrupt
    /// <c>library.db</c> (SQLITE_CORRUPT, 11) skipped the delete-and-recreate path, <c>Exec(write, ddl)</c> threw on the
    /// very next line, <see cref="Boot"/> caught it and ran MEMORY-ONLY for the rest of the process — no rows, no
    /// palette, nothing persisted, every launch the same — with a <c>Debug.WriteLine</c> nobody sees in Release as the
    /// only trace. One bad byte on disk cost the cache permanently; deleting the file costs one refetch.</para></summary>
    static ulong? ReadFingerprint(SqliteConnection c, out string why)
    {
        why = "stale";
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='schema';";
            if (cmd.ExecuteScalar() is string s && ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out ulong v))
                return v;
            why = "foreign";                                 // a meta table with no fingerprint is a foreign file
            return 0UL;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteGenericError)
        {
            return null;                                     // no meta table at all ⇒ a brand-new file, nothing to drop
        }
        catch (SqliteException ex)
        {
            // THE FILE is the problem, not the missing table: SQLITE_CORRUPT (11), SQLITE_NOTADB (26), a truncated
            // header, an encrypted file. It holds nothing this build can read, which is the same verdict as a foreign
            // file — and unlike `null` it takes the delete path instead of leaving the store memory-only forever.
            why = $"unreadable:{ex.SqliteErrorCode}";
            return 0UL;
        }
    }

    /// <summary>SQLITE_ERROR — what <c>SELECT … FROM meta</c> raises when the TABLE does not exist (a file this build
    /// has never written). Every other sqlite error on that one read is a statement about the FILE.</summary>
    const int SqliteGenericError = 1;

    /// <summary>The sqlite verdicts that are a statement about the FILE rather than the statement: SQLITE_CORRUPT (11)
    /// and SQLITE_NOTADB (26). PURE.</summary>
    internal static bool IsUnreadableFile(int sqliteErrorCode) => sqliteErrorCode is 11 or 26;

    /// <summary>WAL mode means three files, and a half-deleted set is worse than none. Returns whether the main file
    /// is actually gone — the caller has a fallback when it is not.</summary>
    static bool Delete(string path)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return !File.Exists(path);
    }

    /// <summary>Drop every table in the file. The undeletable-file path of "a v2 file is deleted, not migrated".</summary>
    static void DropEverything(SqliteConnection c)
    {
        var tables = new List<string>(16);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        for (int i = 0; i < tables.Count; i++)
            try { Exec(c, $"DROP TABLE IF EXISTS \"{tables[i].Replace("\"", "\"\"")}\";"); } catch (SqliteException) { }
    }

    /// <summary>Close the store: stop taking work, drain what is queued, checkpoint the WAL, close both connections.
    /// Blocking, and never called from the UI thread (App.cs calls it from its shutdown shell).</summary>
    public static void Shutdown()
    {
        if (!s_open) return;
        s_stopping = true;
        s_queue.CompleteAdding();
        s_thread?.Join(TimeSpan.FromSeconds(5));
        var db = s_db;
        if (db is not null)
        {
            lock (db.WriteLock)
            {
                // The reader goes first: a live read connection can hold the WAL open and turn the truncating
                // checkpoint into a no-op, which is how a 51 MB WAL survives a clean shutdown.
                try { db.Read.Close(); db.Read.Dispose(); } catch (SqliteException) { }
                try { Exec(db.Write, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
                try { db.Write.Close(); db.Write.Dispose(); } catch (SqliteException) { }
            }
        }
        SqliteConnection.ClearAllPools();   // so a later Boot that must DELETE this file can actually do it
        s_db = null;
        s_open = false;
        s_meta.Clear();                     // the next Boot (if any) warms fresh from whatever file it opens
        s_paletteWarmedOnce = false;         // the next Boot opens (possibly) a different file and must warm it too
    }

    /// <summary>Block until everything queued has run. Tests and shutdown only — never the UI thread (C9).</summary>
    public static void Flush()
    {
        if (!s_open) return;
        var done = new ManualResetEventSlim(false);   // NOT disposed: a late job may still Set it
        if (!Enqueue(done.Set)) return;
        done.Wait(TimeSpan.FromSeconds(30));
    }

    // ── the store thread ────────────────────────────────────────────────────────────────────────────────────────────

    static void Loop(BlockingCollection<Action> queue)
    {
        while (!s_stopping || queue.Count > 0)
        {
            Action? job = null;
            try { if (!queue.TryTake(out job, 250)) job = null; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }         // CompleteAdding + empty
            if (job is not null)
            {
                try { job(); }
                catch (Exception ex) { s_faults++; Fault("job", ex); }
                continue;
            }
            if (!s_stopping) MaybeSweep();
        }
    }

    static bool Enqueue(Action job)
    {
        if (!s_open || s_stopping) return false;
        // TryAdd, never Add: a bounded queue whose producer is the UI thread must refuse, not block (C8/C9).
        if (s_queue.TryAdd(job)) return true;
        s_dropped++;
        return false;
    }

    static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every store failure, in ONE always-on line (CLAUDE.md: always-on logs, no debug switches). It was a
    /// <c>Debug.WriteLine</c>, which is compiled OUT of Release — so the one configuration the user runs reported a
    /// dead cache as silence: <c>open</c> failing means memory-only for the whole process (<see cref="Boot"/>), and
    /// <c>read</c>/<c>write</c>/<c>palette.*</c> failing means a cold start that never warms. `s_faults` already
    /// counts them for the diagnostics page; this is what names them. Warning, not Error: the app is still correct
    /// without a cache, it is only slower.
    /// <para>Called from the STORE THREAD for every case but <c>open</c>; <c>Log.Event</c> is ring-locked and safe
    /// from any thread.</para>
    /// <para>ONE line and no stack, deliberately: a failing <c>write</c> faults once per batch, and a stack per batch
    /// would bury the log it exists to inform. The type and the message are what the <c>Debug.WriteLine</c> printed and
    /// what actually names the cause ("SqliteException: database disk image is malformed").</para></summary>
    static void Fault(string what, Exception ex)
        => Log.Event(WaveeLogLevel.Warning, "store", "store.fault",
            $"the store's {what} step failed: {ex.GetType().Name}: {ex.Message}", null, -1, null,
            WaveeLogField.Of("step", what), WaveeLogField.Of("error", ex.GetType().Name),
            WaveeLogField.Of("faults", s_faults));

    // ── the schema (CORE: pure text) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The whole v4 schema as one script — the fixed tables verbatim from plan §4.4, plus one table and two
    /// indexes per registered kind, generated from its columns. Pure: same shapes in, same text out, which is what
    /// makes <see cref="Fingerprint"/> a decision and not a guess.</summary>
    public static string Ddl()
    {
        var sb = new StringBuilder(4096);
        sb.Append("CREATE TABLE IF NOT EXISTS scope(scope_id INTEGER PRIMARY KEY, provider TEXT, account TEXT, locale TEXT, market TEXT, tier INT, explicit INT, UNIQUE(provider,account,locale,market,tier,explicit));\n");

        for (int k = 0; k < s_shapes.Length; k++)
        {
            if (s_shapes[k] is not { } sql) continue;
            KindShape shape = sql.Shape;
            var cols = shape.Columns;
            sb.Append("CREATE TABLE IF NOT EXISTS ").Append(shape.Table).Append("(scope_id INT NOT NULL, uri TEXT NOT NULL");
            for (int i = 0; i < cols.Length; i++)
                sb.Append(", ").Append(cols[i].Name).Append(' ').Append(SqlType(cols[i].Type));
            sb.Append(", known INT NOT NULL DEFAULT 0, fetched_at INT NOT NULL DEFAULT 0, touched INT NOT NULL DEFAULT 0")
              .Append(", PRIMARY KEY(scope_id,uri)) WITHOUT ROWID;\n");
            for (int i = 0; i < cols.Length; i++)
                if ((cols[i].Flags & StoreColumnFlags.Title) != 0)
                    sb.Append("CREATE INDEX IF NOT EXISTS ix_").Append(shape.Table).Append("_title ON ")
                      .Append(shape.Table).Append("(scope_id, ").Append(cols[i].Name).Append(" COLLATE NOCASE);\n");
            sb.Append("CREATE INDEX IF NOT EXISTS ix_").Append(shape.Table).Append("_gc ON ")
              .Append(shape.Table).Append("(touched);\n");
        }

        sb.Append("CREATE TABLE IF NOT EXISTS edge(scope_id INT, kind INT, parent TEXT, ordinal INT, child TEXT, payload BLOB, PRIMARY KEY(scope_id,kind,parent,ordinal)) WITHOUT ROWID;\n");
        sb.Append("CREATE INDEX IF NOT EXISTS ix_edge_child ON edge(scope_id, kind, child);\n");
        sb.Append("CREATE TABLE IF NOT EXISTS edge_state(scope_id INT, kind INT, parent TEXT, state INT, total INT, version INT, fetched_at INT, PRIMARY KEY(scope_id,kind,parent)) WITHOUT ROWID;\n");
        sb.Append("CREATE TABLE IF NOT EXISTS intent(id INTEGER PRIMARY KEY, kind INT, payload BLOB, created_at INT, state INT);\n");
        sb.Append("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);\n");
        // The palette: process-wide (Palette.cs's file header), so no scope_id — one row per artwork identity, ever.
        sb.Append("CREATE TABLE IF NOT EXISTS palette(key TEXT PRIMARY KEY, known INT NOT NULL, ts INT NOT NULL, dark BLOB, light BLOB) WITHOUT ROWID;\n");
        sb.Append("CREATE INDEX IF NOT EXISTS ix_palette_ts ON palette(ts);\n");
        return sb.ToString();
    }

    static string SqlType(StoreType t) => t switch
    {
        StoreType.Text => "TEXT",
        StoreType.Blob => "BLOB",
        _ => "INT",
    };

    /// <summary>FNV-1a over the DDL plus the schema version. A file whose fingerprint differs is a file written by a
    /// build with different columns, and it is deleted — so an owner adding a column to their kind's shape never has
    /// to think about migration, and never gets a column read back at the wrong ordinal.</summary>
    public static ulong Fingerprint(string ddl)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < ddl.Length; i++)
        {
            h ^= ddl[i];
            h *= 1099511628211UL;
        }
        h ^= (ulong)SchemaVersion;
        return h * 1099511628211UL;
    }

    // ── warm (cold start) ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A scope became current: resolve its <c>scope_id</c> and read what an offline launch needs first —
    /// the account's own edge lists, because the library IS edges (G6, plan §4.14). Everything else is read on
    /// demand by the planner, which is what "disk before network" means; there is no speculative warm of the
    /// catalog, because the catalog is not what the first frame paints.</summary>
    public static void Warm(Scope scope)
    {
        if (!s_open) return;
        uint epoch = scope.Epoch;
        CatalogScope key = scope.Key;
        // Build the account's KEY here, on the UI thread: the store thread may not touch a live column, and may not
        // resolve a text-form id at all (file header). An account uri is always the text form, so this is a resolve.
        string? me = scope.MeSlot == Table.None ? null : KeyText(scope.Users.Id[scope.MeSlot]);
        if (me is { Length: 0 }) me = null;
        Enqueue(() =>
        {
            s_scopeId = ResolveScopeId(key);
            WarmMetaCore();
            if (me is null) return;
            ReadEdgesCore(scope, EdgeRelation.Liked, me, epoch);
            ReadEdgesCore(scope, EdgeRelation.SavedAlbums, me, epoch);
            ReadEdgesCore(scope, EdgeRelation.FollowedArtists, me, epoch);
            ReadEdgesCore(scope, EdgeRelation.SavedShows, me, epoch);
            ReadEdgesCore(scope, EdgeRelation.Pins, me, epoch);
            // Rootlist is NOT read here, on purpose (2026-09-15): `RootlistEdge` carries two `StringId`s
            // (FolderName, FolderId) — indices into the PROCESS-LOCAL interner — and `SaveEdges` writes a payload as
            // raw bytes (its own doc). A persisted rootlist page would resolve those bytes to whatever string that
            // same numeric id happens to mean on the NEXT launch, unrelated to what was saved. The rootlist stays
            // answered by the network only; `RegisterLibraryEdges` registers no applier for it either.
        });
    }

    static long ResolveScopeId(CatalogScope key)
    {
        Db? db = s_db;
        if (db is null) return 0;
        lock (db.WriteLock)
        {
            using var insert = db.Write.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO scope(provider,account,locale,market,tier,explicit) VALUES($p,$a,$l,$m,$t,$e);";
            Bind(insert);
            insert.ExecuteNonQuery();

            using var select = db.Write.CreateCommand();
            select.CommandText = "SELECT scope_id FROM scope WHERE provider=$p AND account=$a AND locale=$l AND market=$m AND tier=$t AND explicit=$e;";
            Bind(select);
            return select.ExecuteScalar() is long id ? id : 0;

            void Bind(SqliteCommand cmd)
            {
                cmd.Parameters.Add(new SqliteParameter("$p", key.Provider));
                cmd.Parameters.Add(new SqliteParameter("$a", key.Account));
                cmd.Parameters.Add(new SqliteParameter("$l", key.Locale));
                cmd.Parameters.Add(new SqliteParameter("$m", key.Market));
                cmd.Parameters.Add(new SqliteParameter("$t", (long)key.Tier));
                cmd.Parameters.Add(new SqliteParameter("$e", key.AllowExplicit ? 1L : 0L));
            }
        }
    }

    // ── small key/value persistence (meta) ─────────────────────────────────────────────────────────────────────────
    //
    // `meta(key TEXT PRIMARY KEY, value TEXT)` already exists in the generated DDL (see `Ddl()` above) and, until now,
    // held only the schema fingerprint (the `INSERT OR REPLACE ... 'schema'` in `Open`). A ROW here costs nothing: the
    // fingerprint is computed over the DDL TEXT, so a value living in `meta` never touches it — where a new COLUMN
    // (on `edge_state` or anywhere else) would move the fingerprint and delete every user's `library.db` once, for
    // the whole app, on the next launch (plan §4.4). So this is rows, never a column.
    //
    // MetaGet must never block the UI thread (C1/C9), and the store owns its own thread, so a synchronous read from
    // sqlite is not an option here (no sync-over-async, CLAUDE.md). The chosen shape is `Warm`'s: the WHOLE table is
    // read once on the store thread and handed back through `Post` into `s_meta`, so every `MetaGet` after that is a
    // dictionary lookup, not a read. `MetaSet` writes `s_meta` immediately (so a `MetaGet` right after a `MetaSet`,
    // even before the next drain, already sees it) and enqueues the durable write behind it, exactly like every
    // other write here (D22) — a crash between the two loses at most this one value, which is what a cache promises.

    /// <summary>The value last set for <paramref name="key"/>, or null when nothing has ever set it (this session or a
    /// prior one, once <see cref="Warm"/> has run). Never touches sqlite — see the section header — so it is safe to
    /// call from the UI thread at any time, store open or not.</summary>
    public static string? MetaGet(string key)
        => !string.IsNullOrEmpty(key) && s_meta.TryGetValue(key, out string? value) ? value : null;

    /// <summary>Set one key. <see cref="MetaGet"/> sees it immediately (the cache is written right here, on the UI
    /// thread); the durable write is enqueued behind it like every other write-behind. A no-op with no store open —
    /// <see cref="s_meta"/> still gets the memory-only write, the same shape <c>--fake</c> and every unit test run
    /// the rest of this file in.</summary>
    public static void MetaSet(string key, string? value)
    {
        if (string.IsNullOrEmpty(key)) return;
        value ??= "";
        s_meta[key] = value;
        if (!s_open) return;
        Enqueue(() =>
        {
            Db? db = s_db;
            if (db is null) return;
            try
            {
                lock (db.WriteLock)
                {
                    using var cmd = db.Write.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES($k,$v);";
                    cmd.Parameters.Add(new SqliteParameter("$k", key));
                    cmd.Parameters.Add(new SqliteParameter("$v", value));
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { s_faults++; Fault("meta.set", ex); }
        });
    }

    /// <summary>STORE THREAD, called from <see cref="Warm"/>'s enqueued job. Read the whole `meta` table (the
    /// `'schema'` row excepted — that one is this file's, never a caller's key) and hand it back through
    /// <see cref="Post"/> so <see cref="MetaGet"/> can answer from memory from here on.</summary>
    static void WarmMetaCore()
    {
        Db? db = s_db;
        if (db is null) return;
        var loaded = new List<KeyValuePair<string, string>>();
        try
        {
            using var cmd = db.Read.CreateCommand();
            cmd.CommandText = "SELECT key,value FROM meta;";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                if (key == "schema") continue;
                loaded.Add(new(key, r.IsDBNull(1) ? "" : r.GetString(1)));
            }
        }
        catch (Exception ex) { s_faults++; Fault("meta.warm", ex); return; }

        Post(() =>
        {
            for (int i = 0; i < loaded.Count; i++) s_meta[loaded[i].Key] = loaded[i].Value;
        });
    }

    // ── the cold read (P4: one query per kind per drain) ────────────────────────────────────────────────────────────

    /// <summary>"These slots, this batch of groups, off the disk." The planner's disk leg (plan §4.5): it runs BEFORE
    /// the network, and whatever the disk could not answer continues to the provider through
    /// <see cref="Fetch.Continue"/> when the answer lands.
    ///
    /// <para>Returns false when there is no store or the queue refused the job — the caller then goes straight to the
    /// network rather than leaving the rows in-flight forever.</para>
    ///
    /// <para><b>THE GATE IS ITS OWN METHOD, AND THAT IS LOAD-BEARING.</b> <see cref="Enqueue(Action)"/> takes a
    /// closure over ten locals, and Roslyn creates that closure's display class at the TOP of whichever method
    /// declares it — before any guard in the body can run. With the job written inline here, every refusal
    /// (<c>--fake</c>, every unit test, and the app's whole first second before the store opens) allocated an
    /// 80-byte display class to be told there is no database — a "no-op that returns false" that was not one.
    /// <c>FetchTests</c> measures both halves: this door costs 0 B closed, and 144 B — that display class plus the
    /// one delegate — per CALL when it is open, never a byte per row.</para></summary>
    public static bool Read(Scope scope, Table table, ReadOnlySpan<int> slots, uint wanted, FetchPriority priority)
    {
        if (!s_open || slots.IsEmpty) return false;
        if (s_shapes[(byte)table.Kind] is not { } sql) return false;   // nobody taught the store this kind's columns
        return ReadJob(scope, table, sql, slots, wanted, priority);
    }

    static bool ReadJob(Scope scope, Table table, ShapeSql sql, ReadOnlySpan<int> slots, uint wanted,
                        FetchPriority priority)
    {
        // Snapshot on the UI thread: a span cannot cross a thread and a live column must not be read off one (C1).
        // Three pooled arrays and NO string per row — the gid keys are formatted on the store thread (file header).
        int n = slots.Length;
        int[] slotCopy = ArrayPool<int>.Shared.Rent(n);
        EntityId[] ids = ArrayPool<EntityId>.Shared.Rent(n);
        string?[] text = ArrayPool<string?>.Shared.Rent(n);
        SnapshotKeys(table, slots, slotCopy, ids, text);
        uint epoch = scope.Epoch;
        if (!Enqueue(() => ReadCore(scope, table, sql, slotCopy, ids, text, n, epoch, wanted, priority)))
        {
            ArrayPool<int>.Shared.Return(slotCopy);
            ArrayPool<EntityId>.Shared.Return(ids);
            ArrayPool<string?>.Shared.Return(text, clearArray: true);
            return false;
        }
        return true;
    }

    static void ReadCore(Scope scope, Table table, ShapeSql sql, int[] slots, EntityId[] ids, string?[] text, int n,
                         uint epoch, uint wanted, FetchPriority priority)
    {
        Db? db = s_db;
        Staging staging = Staging.Rent();
        staging.Epoch = epoch;
        staging.Authority = Authority.Thin;     // the shape restores the persisted per-group authority per row (D16)
        int rows = 0;
        try
        {
            if (db is not null)
            {
                var reader = new RowReader { S = staging };
                for (int from = 0; from < n; from += ReadChunk)
                {
                    int take = Math.Min(ReadChunk, n - from);
                    using var cmd = db.Read.CreateCommand();
                    cmd.CommandText = SelectSql(sql, take);
                    cmd.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                    for (int i = 0; i < take; i++)
                        cmd.Parameters.Add(new SqliteParameter("$u" + i, KeyOf(ids[from + i], text[from + i])));
                    using SqliteDataReader r = cmd.ExecuteReader();
                    reader.R = r;
                    while (r.Read())
                    {
                        sql.Shape.Load(reader, staging);
                        rows++;
                    }
                }
            }
        }
        catch (Exception ex) { s_faults++; Fault("read", ex); }

        s_reads++;
        s_readRows += rows;

        Post(() =>
        {
            bool live = false;
            try
            {
                // C7: the scope moved on while we were on the disk — the answer belongs to a table set nobody holds.
                if (scope.Epoch != epoch || !ReferenceEquals(scope, Entities.Current)) return;
                live = true;

                // THE DISK PROBE MARK. Every slot in the batch is stamped answered, found or not — a row the disk
                // does not have is a row the disk has ANSWERED about (0.2.9 sealed the same fact as an "exhausted"
                // ledger rung / a negative memo). Without it the planner asks the disk again on every page mount for
                // every row the disk will never have.
                // `Math.Max(now, 1)` because 0 in this column MEANS "nobody has ever answered": during the app's
                // first second the honest timestamp and the sentinel are the same number, and the sentinel wins.
                int now = Math.Max(Entities.Now, 1);
                for (int i = 0; i < n; i++)
                {
                    int slot = slots[i];
                    if (table.FetchedAt[slot] == 0) table.FetchedAt[slot] = now;
                }
                Entities.Commit(staging);
            }
            finally
            {
                Staging.Return(staging);
                // Whatever the disk could not fill goes to the provider now — the second half of "disk before
                // network". Re-planning re-derives `wanted & ~known` from the columns the commit just wrote, so a
                // fully-answered batch asks for nothing. A dropped batch continues nothing: those slots belong to a
                // table set nobody is looking at.
                if (live) Fetch.Continue(scope, table, slots.AsSpan(0, n), wanted, priority);
                ArrayPool<int>.Shared.Return(slots);
                ArrayPool<EntityId>.Shared.Return(ids);
                ArrayPool<string?>.Shared.Return(text, clearArray: true);
            }
        });
    }

    static string SelectSql(ShapeSql sql, int count)
    {
        var sb = new StringBuilder(256);
        sb.Append(sql.SelectHead ??= BuildSelectHead(sql.Shape));
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("$u").Append(i);
        }
        sb.Append(");");
        return sb.ToString();
    }

    static string BuildSelectHead(KindShape shape)
    {
        var sb = new StringBuilder(256);
        sb.Append("SELECT uri,known,fetched_at,touched");
        var cols = shape.Columns;
        for (int i = 0; i < cols.Length; i++) sb.Append(',').Append(cols[i].Name);
        sb.Append(" FROM ").Append(shape.Table).Append(" WHERE scope_id=$s AND uri IN (");
        return sb.ToString();
    }

    // ── write-behind (D22) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Persist a batch the UI thread has just committed. <b>The staging is HANDED OVER</b>: the caller must
    /// not touch it or return it to the pool afterwards — the store writes from its arena on its own thread and
    /// returns it when the transaction lands. Returns false when there is no store or the queue refused, and the
    /// caller then returns the staging itself.
    /// <para>Write-behind and not write-through because the provider's answer is already in the columns the UI is
    /// reading: the disk is a shortcut for the NEXT launch, and making the current one wait for it would be paying
    /// tomorrow's cost today (D22).</para></summary>
    public static bool WriteBehind(Staging staging)
    {
        if (!s_open) return false;
        // The five persisted library relations, straight off the SAME staging (Fetch.cs, Home.Host.cs: every caller
        // runs `Entities.Commit(staging)` immediately before this, on the UI thread, so the live tables already hold
        // this batch's answer by the time we get here) — see `SaveLibraryEdgesTouchedBy`'s own doc for why this is
        // the one call site that reaches every library write, wherever it commits.
        SaveLibraryEdgesTouchedBy(staging);
        return Enqueue(() => WriteCore(staging));
    }

    static void WriteCore(Staging staging)
    {
        Db? db = s_db;
        if (db is null) { Staging.Return(staging); return; }
        int rows = 0;
        try
        {
            lock (db.WriteLock)
            {
                using SqliteTransaction tx = db.Write.BeginTransaction();
                for (int k = 0; k < s_shapes.Length; k++)
                {
                    if (s_shapes[k] is not { } sql) continue;
                    RowWriter w = PrepareUpsert(db, sql, tx);
                    w.S = staging;
                    w.Rows = 0;
                    sql.Shape.Save(staging, w);
                    rows += w.Rows;
                }
                tx.Commit();
            }
        }
        catch (Exception ex) { s_faults++; Fault("write", ex); }
        finally { Staging.Return(staging); }
        s_writes++;
        s_writeRows += rows;
    }

    static RowWriter PrepareUpsert(Db db, ShapeSql sql, SqliteTransaction tx)
    {
        RowWriter w = sql.Writer;
        SqliteCommand? upsert = sql.Upsert;
        if (upsert is null)
        {
            KindShape shape = sql.Shape;
            var cols = shape.Columns;
            var sb = new StringBuilder(512);
            sb.Append("INSERT INTO ").Append(shape.Table).Append("(scope_id,uri");
            for (int i = 0; i < cols.Length; i++) sb.Append(',').Append(cols[i].Name);
            sb.Append(",known,fetched_at,touched) VALUES($s,$uri");
            for (int i = 0; i < cols.Length; i++) sb.Append(",$c").Append(i);
            sb.Append(",$known,$fetched,$touched) ON CONFLICT(scope_id,uri) DO UPDATE SET ");
            for (int i = 0; i < cols.Length; i++)
            {
                // coalesce, not assign: a Thin answer that says nothing about a column must not blank what a Full
                // answer wrote last week — the same rule `Table.Accepts` enforces in memory, expressed in sql (D16).
                // Authority columns take the max for the same reason: authority climbs, and never falls.
                string name = cols[i].Name;
                sb.Append(name).Append('=');
                sb.Append((cols[i].Flags & StoreColumnFlags.Authority) != 0
                    ? $"max({name},coalesce(excluded.{name},0))"
                    : $"coalesce(excluded.{name},{name})");
                sb.Append(',');
            }
            // `known` is a UNION and never a replacement — the disk's copy of `Known` has to answer the same
            // question the column does: which groups does this row carry, from every answer that ever landed.
            sb.Append("known=known|excluded.known,fetched_at=max(fetched_at,excluded.fetched_at),touched=max(touched,excluded.touched);");

            upsert = db.Write.CreateCommand();
            upsert.CommandText = sb.ToString();
            upsert.Parameters.Add(new SqliteParameter("$s", s_scopeId));
            w.PUri = new SqliteParameter("$uri", DBNull.Value);
            upsert.Parameters.Add(w.PUri);
            var ps = new SqliteParameter[cols.Length];
            for (int i = 0; i < cols.Length; i++)
            {
                ps[i] = new SqliteParameter("$c" + i, DBNull.Value);
                upsert.Parameters.Add(ps[i]);
            }
            w.PKnown = new SqliteParameter("$known", 0L);
            w.PFetched = new SqliteParameter("$fetched", 0L);
            w.PTouched = new SqliteParameter("$touched", 0L);
            upsert.Parameters.Add(w.PKnown);
            upsert.Parameters.Add(w.PFetched);
            upsert.Parameters.Add(w.PTouched);
            w.Cols = ps;
            w.Cmd = upsert;
            sql.Upsert = upsert;
        }
        // The scope id resolves on the store thread after the first commands may already exist, and a scope switch
        // changes it: rebind it every batch rather than trusting the value the command was built with.
        upsert.Parameters[0].Value = s_scopeId;
        upsert.Transaction = tx;
        return w;
    }

    // ── edges ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Persist one parent's edge list. Called from the UI thread by whoever changed it (a commit, a user
    /// edit); the children's uris are resolved HERE, because the store thread may not read a live column.
    ///
    /// <para>The payload is written as its raw unmanaged bytes. <b>THIS IS NOT COVERED BY THE SCHEMA FINGERPRINT</b> —
    /// <see cref="Ddl"/> emits the fixed <c>edge(…, payload BLOB, …)</c> table verbatim; a payload struct's SHAPE
    /// (its field order, widths, padding) never appears in the generated text, so changing <c>TEdge</c> does not
    /// change <see cref="Fingerprint"/> and does not drop the file. An applier reading last build's bytes as this
    /// build's struct is a real hazard, not a hypothetical one — which is exactly why every registered applier MUST
    /// assert <c>page.Stride == Unsafe.SizeOf&lt;TEdge&gt;()</c> before trusting <see cref="EdgePage.PayloadAs{TEdge}"/>,
    /// and drop the page (never read it) when the width does not match (2026-09-15).</para></summary>
    public static bool SaveEdges<TEdge>(EdgeRelation relation, EdgeTable<TEdge> edges, int parent, EntityId parentId, Table children)
        where TEdge : unmanaged
    {
        if (!s_open || relation == EdgeRelation.None || parent == Table.None || parentId.IsEmpty) return false;

        ReadOnlySpan<int> targets = edges.Targets(parent);
        ReadOnlySpan<TEdge> payload = edges.Payload(parent);
        int n = targets.Length;
        // A playlist's edge list is thousands of rows, so this loop is the one that would have hurt: it copies 24-byte
        // identities and resolves ONLY the text-form children (free — the string exists). The gid children, which is
        // to say nearly all of them, are formatted on the store thread (file header).
        EntityId[] kidIds = n == 0 ? Array.Empty<EntityId>() : new EntityId[n];
        string?[] kidText = n == 0 ? Array.Empty<string?>() : new string?[n];
        for (int i = 0; i < n; i++)
        {
            EntityId id = children.Id[targets[i]];
            kidIds[i] = id;
            kidText[i] = id.Form == EntityForm.Text ? Entities.Strings.Resolve(id.TextId) : null;
        }

        int stride = payload.IsEmpty ? 0 : Unsafe.SizeOf<TEdge>();
        byte[] blob = payload.IsEmpty ? Array.Empty<byte>() : MemoryMarshal.AsBytes(payload).ToArray();
        string parentText = KeyText(parentId);
        byte state = (byte)edges.State(parent);
        int total = edges.Total(parent);
        uint version = edges.Version(parent);

        return Enqueue(() => SaveEdgesCore(relation, parentText, kidIds, kidText, n, blob, stride, state, total, version));
    }

    static void SaveEdgesCore(EdgeRelation relation, string parent, EntityId[] kidIds, string?[] kidText, int n,
                              byte[] blob, int stride, byte state, int total, uint version)
    {
        Db? db = s_db;
        if (db is null) return;
        try
        {
            lock (db.WriteLock)
            {
                using SqliteTransaction tx = db.Write.BeginTransaction();
                using (var del = db.Write.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM edge WHERE scope_id=$s AND kind=$k AND parent=$p;";
                    del.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                    del.Parameters.Add(new SqliteParameter("$k", (long)(byte)relation));
                    del.Parameters.Add(new SqliteParameter("$p", parent));
                    del.ExecuteNonQuery();
                }
                if (n > 0)
                {
                    using var ins = db.Write.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO edge(scope_id,kind,parent,ordinal,child,payload) VALUES($s,$k,$p,$o,$c,$y);";
                    ins.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                    ins.Parameters.Add(new SqliteParameter("$k", (long)(byte)relation));
                    ins.Parameters.Add(new SqliteParameter("$p", parent));
                    var po = new SqliteParameter("$o", 0L);
                    var pc = new SqliteParameter("$c", DBNull.Value);
                    var py = new SqliteParameter("$y", DBNull.Value);
                    ins.Parameters.Add(po);
                    ins.Parameters.Add(pc);
                    ins.Parameters.Add(py);
                    for (int i = 0; i < n; i++)
                    {
                        po.Value = (long)i;
                        pc.Value = KeyOf(kidIds[i], kidText[i]);
                        py.Value = stride == 0 ? DBNull.Value : (object)blob.AsSpan(i * stride, stride).ToArray();
                        ins.ExecuteNonQuery();
                    }
                }
                using (var st = db.Write.CreateCommand())
                {
                    st.Transaction = tx;
                    st.CommandText = "INSERT OR REPLACE INTO edge_state(scope_id,kind,parent,state,total,version,fetched_at) VALUES($s,$k,$p,$t,$n,$v,$f);";
                    st.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                    st.Parameters.Add(new SqliteParameter("$k", (long)(byte)relation));
                    st.Parameters.Add(new SqliteParameter("$p", parent));
                    st.Parameters.Add(new SqliteParameter("$t", (long)state));
                    st.Parameters.Add(new SqliteParameter("$n", (long)total));
                    st.Parameters.Add(new SqliteParameter("$v", (long)version));
                    st.Parameters.Add(new SqliteParameter("$f", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                    st.ExecuteNonQuery();
                }
                tx.Commit();
            }
            s_writes++;
        }
        catch (Exception ex) { s_faults++; Fault("edge.write", ex); }
    }

    /// <summary>THE call site: whatever a batch's <see cref="Staging.EdgesOrNull"/> says it touched, of the five
    /// relations this build persists, is re-saved to disk — called from <see cref="WriteBehind"/>, which every write
    /// path (a provider's answer in <c>Fetch.Answer</c>, a top-content load in <c>Home.Host.cs</c>, …) already runs
    /// right after <c>Entities.Commit(staging)</c> lands the SAME staging's runs into the live tables. That commit is
    /// "wherever a library relation commits" — this file has no other seam that sees every one of them, and does not
    /// need one: it reads which relations this batch named, then re-persists each one's WHOLE current list off the
    /// live <see cref="Edges"/> tables (not off the staged runs themselves), so a batch that only ADDED one liked
    /// track still writes the complete, correct list. UI thread, like <see cref="SaveEdges{TEdge}"/> itself.</summary>
    static void SaveLibraryEdgesTouchedBy(Staging staging)
    {
        StagedEdgeList? list = staging.EdgesOrNull;
        if (list is null || list.RunCount == 0) return;
        if (staging.Epoch != 0 && staging.Epoch != Entities.Current.Epoch) return;   // C7: the scope moved on

        Scope scope = Entities.Current;
        int me = scope.MeSlot;
        if (me == Table.None) return;

        bool liked = false, savedAlbums = false, followedArtists = false, savedShows = false, pins = false;
        ReadOnlySpan<StagedRun> runs = list.Runs;
        for (int i = 0; i < runs.Length; i++)
        {
            switch (runs[i].Relation)
            {
                case Relation.Liked: liked = true; break;
                case Relation.SavedAlbums: savedAlbums = true; break;
                case Relation.FollowedArtists: followedArtists = true; break;
                case Relation.SavedShows: savedShows = true; break;
                case Relation.Pins: pins = true; break;
            }
        }
        if (!(liked || savedAlbums || followedArtists || savedShows || pins)) return;

        EntityId meId = scope.Users.Id[me];
        if (liked) SaveEdges(EdgeRelation.Liked, scope.Edges.Liked, me, meId, scope.Tracks);
        if (savedAlbums) SaveEdges(EdgeRelation.SavedAlbums, scope.Edges.SavedAlbums, me, meId, scope.Albums);
        if (followedArtists) SaveEdges(EdgeRelation.FollowedArtists, scope.Edges.FollowedArtists, me, meId, scope.Artists);
        if (savedShows) SaveEdges(EdgeRelation.SavedShows, scope.Edges.SavedShows, me, meId, scope.Shows);
        if (pins) SavePinsEdges(scope, me, meId);
    }

    /// <summary>Pins' write side: cross-kind (see <see cref="ApplyPinsEdge"/>), so it cannot share
    /// <see cref="SaveEdges{TEdge}"/>'s one-fixed-table shape — each edge's child KEY comes from its own kind rather
    /// than one <c>Table</c>, but it lands through the very same <see cref="SaveEdgesCore"/> the generic path uses,
    /// so the file format (and <see cref="ApplyPinsEdge"/>'s read side) is identical either way. UI thread: every
    /// identity read here is a live column (C1).</summary>
    static void SavePinsEdges(Scope scope, int parent, EntityId parentId)
    {
        if (parent == Table.None || parentId.IsEmpty) return;
        EdgeTable<LibraryEdge> pins = scope.Edges.Pins;
        ReadOnlySpan<int> targets = pins.Targets(parent);
        ReadOnlySpan<LibraryEdge> payload = pins.Payload(parent);
        int n = targets.Length;

        EntityId[] kidIds = n == 0 ? Array.Empty<EntityId>() : new EntityId[n];
        string?[] kidText = n == 0 ? Array.Empty<string?>() : new string?[n];
        for (int i = 0; i < n; i++)
        {
            var kind = (PinKind)payload[i].Flags;
            kidText[i] = kind switch
            {
                PinKind.Folder => targets[i] > 0 ? Entities.Strings.Resolve(new StringId(targets[i])) : "",
                PinKind.Liked => "",
                _ => TextOf(Entities.TableFor(User.EntityKindOf(kind)), targets[i]),
            };
            kidIds[i] = default;   // every key here travels as TEXT (kidText) — see TextOf/KeyOf
        }

        int stride = n == 0 ? 0 : Unsafe.SizeOf<LibraryEdge>();
        byte[] blob = n == 0 ? Array.Empty<byte>() : MemoryMarshal.AsBytes(payload).ToArray();
        string parentText = KeyText(parentId);
        byte state = (byte)pins.State(parent);
        int total = pins.Total(parent);
        uint version = pins.Version(parent);

        Enqueue(() => SaveEdgesCore(EdgeRelation.Pins, parentText, kidIds, kidText, n, blob, stride, state, total, version));
    }

    /// <summary>A slot's uri text, or "" for none — <see cref="SavePinsEdges"/>'s per-kind lookup (mirrors
    /// <see cref="KeyText"/>, guarded for a slot that may not belong to <paramref name="table"/> at all).</summary>
    static string TextOf(Table? table, int slot)
        => table is null || slot <= Table.None || slot >= table.Count ? "" : KeyText(table.Id[slot]);

    /// <summary>Read one parent's edge list back. The page is posted to the relation's registered applier on the UI
    /// thread (<see cref="RegisterEdges"/>); with no applier the page is simply dropped, which is what lets the
    /// store warm a relation whose owner has not landed yet.</summary>
    public static bool ReadEdges(Scope scope, EdgeRelation relation, EntityId parent)
    {
        if (!s_open || relation == EdgeRelation.None || parent.IsEmpty) return false;
        string parentText = KeyText(parent);
        uint epoch = scope.Epoch;
        return Enqueue(() => ReadEdgesCore(scope, relation, parentText, epoch));
    }

    static void ReadEdgesCore(Scope scope, EdgeRelation relation, string parent, uint epoch)
    {
        Db? db = s_db;
        if (db is null) return;
        var page = new EdgePage { Relation = relation };
        try
        {
            using (var st = db.Read.CreateCommand())
            {
                st.CommandText = "SELECT state,total FROM edge_state WHERE scope_id=$s AND kind=$k AND parent=$p;";
                st.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                st.Parameters.Add(new SqliteParameter("$k", (long)(byte)relation));
                st.Parameters.Add(new SqliteParameter("$p", parent));
                using SqliteDataReader r = st.ExecuteReader();
                if (!r.Read()) return;                             // nothing persisted for this parent
                page.State = (EdgeState)(byte)r.GetInt64(0);
                page.Total = (int)r.GetInt64(1);
            }

            var kids = new List<string>(64);
            var blobs = new List<byte[]?>(64);
            using (var cmd = db.Read.CreateCommand())
            {
                cmd.CommandText = "SELECT child,payload FROM edge WHERE scope_id=$s AND kind=$k AND parent=$p ORDER BY ordinal;";
                cmd.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                cmd.Parameters.Add(new SqliteParameter("$k", (long)(byte)relation));
                cmd.Parameters.Add(new SqliteParameter("$p", parent));
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    kids.Add(r.GetString(0));
                    blobs.Add(r.IsDBNull(1) ? null : r.GetFieldValue<byte[]>(1));
                }
            }

            page.Count = kids.Count;
            page.Children = kids.ToArray();
            int stride = 0;
            for (int i = 0; i < blobs.Count; i++) if (blobs[i] is { Length: > 0 } b) { stride = b.Length; break; }
            page.Stride = stride;
            if (stride > 0)
            {
                var packed = new byte[stride * page.Count];
                for (int i = 0; i < page.Count; i++)
                    if (blobs[i] is { } b && b.Length == stride) b.CopyTo(packed.AsSpan(i * stride));
                page.Payload = packed;
            }
        }
        catch (Exception ex) { s_faults++; Fault("edge.read", ex); return; }

        Post(() =>
        {
            if (scope.Epoch != epoch || !ReferenceEquals(scope, Entities.Current)) return;   // C7
            page.Parent = EntityId.Parse(parent.AsSpan());        // UI thread: the text form interns here, and only here
            s_edgeAppliers[(byte)relation]?.Invoke(page);
        });
    }

    // ── the intent journal (C6/D22) — the ONE synchronous call ──────────────────────────────────────────────────────

    /// <summary>Record a user intent BEFORE its network call goes out, and block until it is on disk. This is the
    /// only blocking database call in the app, and it is blocking on purpose: the window between "the user pressed
    /// like" and "the PUT was accepted" is the only place where the disk and the truth can disagree, and a crash in
    /// that window must leave a record. Everything else is write-behind.
    ///
    /// <para><b>Never from the UI thread</b> (C9) — the intent's own shell calls it, off the frame. There is no
    /// runtime guard: this layer has no oracle for "am I the UI thread", and a wrong caller shows up as a stalled
    /// frame in the always-on frame log, which is where a stall belongs.</para>
    /// <para>Returns the intent's id, or 0 when there is no store. Nothing here is REPLAYED automatically (C6): a
    /// pending row is a record of what was in flight, for the shell to reconcile or drop, never a queue that fires
    /// itself on a later launch.</para></summary>
    public static long Journal(byte kind, ReadOnlySpan<byte> payload)
    {
        Db? db = s_db;
        if (!s_open || db is null) return 0;
        byte[] bytes = payload.IsEmpty ? Array.Empty<byte>() : payload.ToArray();
        try
        {
            lock (db.WriteLock)
            {
                using var cmd = db.Write.CreateCommand();
                cmd.CommandText = "INSERT INTO intent(kind,payload,created_at,state) VALUES($k,$p,$c,0);";
                cmd.Parameters.Add(new SqliteParameter("$k", (long)kind));
                cmd.Parameters.Add(new SqliteParameter("$p", bytes.Length == 0 ? DBNull.Value : (object)bytes));
                cmd.Parameters.Add(new SqliteParameter("$c", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                cmd.ExecuteNonQuery();
                // Its own statement: which statement of a multi-statement command a scalar comes from is the
                // driver's business, and this id is the caller's only handle on the intent it just recorded.
                using var last = db.Write.CreateCommand();
                last.CommandText = "SELECT last_insert_rowid();";
                return last.ExecuteScalar() is long id ? id : 0;
            }
        }
        catch (Exception ex) { s_faults++; Fault("journal", ex); return 0; }
    }

    /// <summary>The intent settled (C6's four cases collapse to two on disk): the row is deleted when the server
    /// accepted it, and marked failed when it did not — a failed row is what the diagnostics page lists and what a
    /// human decides about, never something this layer retries on its own.</summary>
    public static void JournalSettle(long id, bool ok)
    {
        if (id <= 0 || !s_open) return;
        Enqueue(() =>
        {
            Db? db = s_db;
            if (db is null) return;
            try
            {
                lock (db.WriteLock)
                {
                    using var cmd = db.Write.CreateCommand();
                    cmd.CommandText = ok ? "DELETE FROM intent WHERE id=$i;" : "UPDATE intent SET state=2 WHERE id=$i;";
                    cmd.Parameters.Add(new SqliteParameter("$i", id));
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { s_faults++; Fault("journal.settle", ex); }
        });
    }

    /// <summary>How many intents did not settle — read once at boot by the shell that owns them, and by the
    /// diagnostics page. Blocking, like <see cref="Journal"/>, and never called from the UI thread.</summary>
    public static int PendingIntents()
    {
        Db? db = s_db;
        if (!s_open || db is null) return 0;
        try
        {
            lock (db.WriteLock)
            {
                using var cmd = db.Write.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM intent;";
                return cmd.ExecuteScalar() is long n ? (int)n : 0;
            }
        }
        catch (Exception ex) { s_faults++; Fault("journal.count", ex); return 0; }
    }

    // ── the GC tick (R2) ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ask for a sweep on the next idle moment of the store thread. The thread also does this on its own
    /// cadence (warm + 30 s, then every 6 h); this is the manual door for the diagnostics page and for shutdown.</summary>
    public static void Tick() => s_nextSweepAt = 0;

    static void MaybeSweep()
    {
        if (!s_open || Entities.Now < s_nextSweepAt) return;
        s_nextSweepAt = Entities.Now + SweepPeriodSeconds;
        Sweep();
    }

    /// <summary>One pass, on the store thread: measure the file, ask <see cref="CatalogSweepSchedule"/> what should
    /// go, delete it in bounded atomic batches, reclaim a slice. Every step is its own transaction, so abandoning
    /// the pass between any two leaves a consistent file — which is what lets shutdown simply walk away.</summary>
    static void Sweep()
    {
        Db? db = s_db;
        if (db is null) return;
        s_sweeps++;
        try
        {
            long bytes = FileBytes(db);
            int now = Entities.Now;
            int evicted = 0;
            SweepPolicy policy = s_policy;
            for (int k = 0; k < s_shapes.Length; k++)
            {
                if (s_shapes[k] is not { } sql) continue;
                if (s_queue.Count > policy.DeferQueueDepth) return;      // the write lane got busy mid-pass: yield

                // The candidate window: the coldest rows first, which is what ix_<table>_gc is for. One window per
                // pass per kind — a sweep is a ceiling, not a completeness guarantee (More says another is due).
                int window = Math.Max(policy.MaxRowsPerPass * 4, 1024);
                var rows = new List<SweepRow>(Math.Min(window, 4096));
                var uris = new List<string>(Math.Min(window, 4096));
                using (var cmd = db.Read.CreateCommand())
                {
                    cmd.CommandText =
                        $"SELECT uri,touched,fetched_at,length(uri)+64 FROM {sql.Shape.Table} WHERE scope_id=$s ORDER BY touched ASC LIMIT $n;";
                    cmd.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                    cmd.Parameters.Add(new SqliteParameter("$n", (long)window));
                    using SqliteDataReader r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        uris.Add(r.GetString(0));
                        rows.Add(new SweepRow(uris.Count - 1, ToApp(r.GetInt64(1)), ToApp(r.GetInt64(2)), (int)r.GetInt64(3), SweepFlags.None));
                    }
                }
                if (rows.Count == 0) continue;

                int[] victims = new int[Math.Min(policy.MaxRowsPerPass, rows.Count)];
                SweepPlan plan = CatalogSweepSchedule.Plan(
                    CollectionsMarshal.AsSpan(rows), policy, now, bytes, s_queue.Count, victims);
                if (plan.Deferred) return;
                if (plan.Victims == 0) continue;

                DeleteRows(db, sql.Shape.Table, uris, victims.AsSpan(0, plan.Victims));
                bytes = plan.BytesAfter;
                evicted += plan.Victims;
                s_evicted += plan.Victims;
            }

            // The palette has no scope_id (it is process-wide) and no per-kind loop above, so its stale rows are
            // swept here, once a pass, on the same coarse cutoff `WarmPaletteCore` reads by.
            lock (db.WriteLock)
            {
                try
                {
                    using var del = db.Write.CreateCommand();
                    del.CommandText = "DELETE FROM palette WHERE ts < $cut;";
                    del.Parameters.Add(new SqliteParameter("$cut", PalettePersistence.LoadCutoffUnix(DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
                    del.ExecuteNonQuery();
                }
                catch (SqliteException) { }
            }

            if (evicted > 0)
            {
                lock (db.WriteLock)
                {
                    try { Exec(db.Write, $"PRAGMA incremental_vacuum({VacuumPagesPerPass});"); } catch (SqliteException) { }
                }
            }
            // UNCONDITIONAL, unlike the vacuum: the WAL grows from ordinary writes far more than from deletes, and an
            // unbounded WAL taxes every later launch (0.2.9 measured 51 MB against a 125 MB file).
            lock (db.WriteLock)
            {
                try { Exec(db.Write, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
            }
        }
        catch (Exception ex) { s_faults++; Fault("sweep", ex); }
    }

    static void DeleteRows(Db db, string table, List<string> uris, ReadOnlySpan<int> victims)
    {
        lock (db.WriteLock)
        {
            for (int from = 0; from < victims.Length; from += DeleteBatch)
            {
                int take = Math.Min(DeleteBatch, victims.Length - from);
                using SqliteTransaction tx = db.Write.BeginTransaction();
                using var cmd = db.Write.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {table} WHERE scope_id=$s AND uri=$u;";
                cmd.Parameters.Add(new SqliteParameter("$s", s_scopeId));
                var pu = new SqliteParameter("$u", DBNull.Value);
                cmd.Parameters.Add(pu);
                for (int i = 0; i < take; i++)
                {
                    pu.Value = uris[victims[from + i]];
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }

    static long FileBytes(Db db)
    {
        try
        {
            long pages = Scalar(db.Read, "PRAGMA page_count;");
            long size = Scalar(db.Read, "PRAGMA page_size;");
            long free = Scalar(db.Read, "PRAGMA freelist_count;");
            return (pages - free) * size;
        }
        catch (SqliteException) { return 0; }
    }

    static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() is long v ? v : 0;
    }

    // ── the MEMORY trim (R2, and the point of defect 1) ─────────────────────────────────────────────────────────────
    //
    // The sweep above deletes rows from the FILE. This deletes them from the TABLES, and it is the leg that gives the
    // interner its text back: before 2026-09-12 nothing in Entities/ ref-counted a StringId, so a retired row freed its
    // 89 B of columns and leaked the uri, the title, the image and the artist line for the life of the process — a
    // scope's floor could only ever rise (doc §4.4). `Table.FreeSlot` now releases every string the row owned, so a
    // trim measurably SHRINKS `Entities.Strings.MapCount`; `StoreTests` asserts exactly that, because it is the whole
    // return on the change.
    //
    // It needs no database: a graph with `Store.Use(null)` trims the same way, which is also how it is tested.

    /// <summary>What one trimmed row gives back, for the pass's own arithmetic: ~120 B for a 0.3 catalog row (89 B of
    /// value columns + ~31 B of packed identity and its index — the measured option-2 total, doc §3). The text the row
    /// owned comes back on top of it and is deliberately NOT guessed at here: it is shared between rows (P6), so the
    /// only honest measure of it is the interner's own count before and after.</summary>
    public const int MemoryBytesPerRow = 120;

    /// <summary>The shell's pin sources. Null means "nothing outside the table itself is known to be holding a row",
    /// which is why <see cref="TrimMemory(Scope,Table,in SweepPolicy,int)"/> is not called from anywhere in this
    /// file — see <see cref="MemoryPins"/> for why that is deliberate rather than unfinished.</summary>
    public static MemoryPins? Pins { get; set; }

    // UI thread only (C1), reused across passes so a trim allocates nothing after the first (P8).
    static SweepRow[] s_trimRows = new SweepRow[1_024];
    static int[] s_trimVictims = new int[1_024];

    /// <summary>Retire this table's cold rows: the same <see cref="CatalogSweepSchedule"/> decision the disk sweep
    /// uses, over the table's OWN <c>Touched</c>/<c>FetchedAt</c> columns, then one <see cref="Table.FreeSlot"/> per
    /// victim. UI thread (C1) — <c>FreeSlot</c> writes columns, the indexes and the interner.
    ///
    /// <para><b>The budget leg is off, on purpose.</b> <see cref="CatalogSweepSchedule"/>'s LRU rank needs its input
    /// ordered coldest-first, which on disk is free (<c>ix_&lt;table&gt;_gc</c>) and in memory would be a sort of the
    /// whole table per pass. So memory takes the TTL leg only — the exact, order-independent one — and the ceiling
    /// stays the disk's. What memory needs from a trim is the FLOOR, and the floor is the text.</para>
    ///
    /// <para><b>Two rows are always pinned, whatever <see cref="Pins"/> says:</b> a row with a request out
    /// (<c>Inflight != 0</c> — the planner marks it BEFORE it queues, so its bucket is holding that slot and a recycled
    /// slot would take the answer), and the account's own row (every library edge hangs off it).</para></summary>
    public static SweepPlan TrimMemory(Scope scope, Table table, in SweepPolicy policy, int maxRows = 1_024)
    {
        if (!ReferenceEquals(scope, Entities.Current)) return default;
        int cap = Math.Min(maxRows, table.Count - 1);
        if (cap <= 0) return default;
        if (s_trimRows.Length < cap) { s_trimRows = new SweepRow[cap]; s_trimVictims = new int[cap]; }

        MemoryPins? pins = Pins;
        int n = 0;
        for (int slot = 1; slot < table.Count && n < cap; slot++)
        {
            // A slot with no identity is slot 0 or one already on the free list: `FreeSlot` blanks the cell.
            if (table.Id[slot].Form == EntityForm.None) continue;
            bool pinned = table.Inflight[slot] != 0
                       || (table.Kind == EntityKind.User && slot == scope.MeSlot)
                       || (pins is not null && pins.IsPinned(table.Kind, slot));
            s_trimRows[n++] = new SweepRow(slot, table.Touched[slot], table.FetchedAt[slot], MemoryBytesPerRow,
                                           pinned ? SweepFlags.Pinned : SweepFlags.None);
        }
        if (n == 0) return default;

        // ByteBudget 0 disables the budget leg (see above); everything else is the policy the disk sweep runs under,
        // so "cold" means the same thing in both tiers and a row cannot be alive in memory and dead on disk.
        var memoryPolicy = new SweepPolicy(policy.TtlSeconds, policy.GraceSeconds, 0, policy.HeadroomPercent,
                                           Math.Min(policy.MaxRowsPerPass, cap), policy.DeferQueueDepth);
        SweepPlan plan = CatalogSweepSchedule.Plan(
            s_trimRows.AsSpan(0, n), memoryPolicy, Entities.Now, (long)table.LiveCount * MemoryBytesPerRow,
            queueDepth: 0, s_trimVictims.AsSpan(0, Math.Min(memoryPolicy.MaxRowsPerPass, cap)));

        for (int i = 0; i < plan.Victims; i++) table.FreeSlot(s_trimVictims[i]);
        s_trimmed += plan.Victims;
        return plan;
    }

    /// <summary>The same pass over EVERY entity table in the scope, under the store's current
    /// <see cref="Policy"/>. Returns how many rows were retired.</summary>
    public static int TrimMemory(Scope scope)
    {
        SweepPolicy policy = s_policy;
        int freed = 0;
        for (int i = 0; i < scope.Tables.Length; i++) freed += TrimMemory(scope, scope.Tables[i], policy).Victims;
        return freed;
    }
}

// ── 4. the Entities hooks ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The two SHELL seams <c>Entities.cs</c> declares for the store. They are partial methods, so a build
/// without this file simply has no store — which is exactly what <c>--fake</c> and every unit test want.</summary>
public static partial class Entities
{
    static partial void StoreBoot() => Store.Boot();

    static partial void StoreWarm(Scope scope) => Store.Warm(scope);
}
