// ── Wavee.Tests/StoreTests.cs — the schema, the round trip, the upsert merge, the journal, the epoch drop ─────────
//
// Wave 1's gate for the SHELL half of Entities/Store.cs (plan §5: "a Store round-trip against a temp db"). Every
// test here runs against a real sqlite file in the temp directory — there is no mock, because the thing under test
// IS the file: the generated v3 schema, the coalescing upsert that keeps a Thin answer from blanking a Full one, and
// the two rules that make the cache safe to throw away (a foreign schema is deleted, and an answer for a replaced
// scope is dropped).
//
// The eviction DECISION is not here: it is pure, and it is pinned in CatalogSweepScheduleTests with no file at all.
//
// SINCE 2026-09-12 the identity in memory is a packed 24-byte `EntityId` and the key on disk is still `uri TEXT`
// (Store.cs's header records that decision and why). That makes two facts load-bearing, and they are the first two
// round-trip tests below: `Format` and `Parse` are exact inverses, in BOTH forms, so a row written and read back is
// the same id and therefore the same slot — and a decoder that only ever held 16 gid bytes can key a row without a
// uri string existing anywhere. The third new fact is the leak (defect 1): a trim now measurably shrinks the
// interner, which is the entire return on ref-counting the graph's text.

using System.Collections.Concurrent;
using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>The minimum a kind teaches the store, standing in for <c>Track.cs</c>'s real one: a title (indexed), a
/// number, and a per-group authority column (merged with <c>max</c>, never overwritten).</summary>
sealed class StoreProbeShape : KindShape
{
    readonly StoreColumn[] _cols;

    public StoreProbeShape(bool extraColumn = false)
        => _cols = extraColumn
            ? [new("title", StoreType.Text, StoreColumnFlags.Title), new("duration_ms", StoreType.Int),
               new("identity_auth", StoreType.Int, StoreColumnFlags.Authority), new("isrc", StoreType.Text)]
            : [new("title", StoreType.Text, StoreColumnFlags.Title), new("duration_ms", StoreType.Int),
               new("identity_auth", StoreType.Int, StoreColumnFlags.Authority)];

    public override EntityKind Kind => EntityKind.Track;
    public override string Table => "track";
    public override ReadOnlySpan<StoreColumn> Columns => _cols;

    /// <summary>What the next write-behind should persist. A null title is a Thin answer that says NOTHING about the
    /// title — which must read as NULL on the wire and therefore leave the stored value alone.</summary>
    public readonly List<(string Uri, string? Title, int DurationMs, uint Known, int Auth, int FetchedAt, int Touched)> Out = new();

    /// <summary>Staged rows whose identity is already PACKED — Wave 2's shape (16 raw gid bytes off the wire, no uri
    /// text anywhere, doc §2). They go out through <c>RowWriter.Emit(in EntityId, …)</c>, which formats the key on the
    /// store thread.</summary>
    public readonly List<(EntityId Id, string? Title, int DurationMs, uint Known, int Auth, int FetchedAt, int Touched)> OutIds = new();

    /// <summary>What came back, materialized to strings on the store thread so the test can read it after the
    /// staging has gone back to the pool.</summary>
    public readonly List<(string Uri, string Title, long DurationMs, uint Known, long Auth, int FetchedAt, int Touched)> In = new();

    public override void Save(Staging s, RowWriter w)
    {
        for (int i = 0; i < Out.Count; i++)
        {
            var row = Out[i];
            Bind(s, w, row.Title, row.DurationMs, row.Auth);
            w.Emit(s.AddText(Encoding.UTF8.GetBytes(row.Uri)), row.Known, row.FetchedAt, row.Touched);
        }
        for (int i = 0; i < OutIds.Count; i++)
        {
            var row = OutIds[i];
            Bind(s, w, row.Title, row.DurationMs, row.Auth);
            w.Emit(row.Id, row.Known, row.FetchedAt, row.Touched);
        }
    }

    void Bind(Staging s, RowWriter w, string? title, int durationMs, int auth)
    {
        w.Text(0, title is null ? default : s.AddText(Encoding.UTF8.GetBytes(title)));
        w.Int(1, durationMs);
        w.Int(2, auth);
        if (_cols.Length > 3) w.Null(3);
    }

    public override void Load(RowReader r, Staging into)
        => In.Add((Str(into, r.Uri), Str(into, r.Text(0)), r.Int(1), r.Known, r.Int(2), r.FetchedAt, r.Touched));

    static string Str(Staging s, TextRef t) => t.IsEmpty ? "" : Encoding.UTF8.GetString(s.Utf8(t));
}

/// <summary>The shell's pin source, standing in for the real ones (now-playing, the queue, the open page's bound
/// rows, the rootlist). An abstract class and not a lambda for the reason <see cref="MemoryPins"/> gives: the trim
/// asks once per candidate row.</summary>
sealed class PinOneSlot(int slot) : MemoryPins
{
    public override bool IsPinned(EntityKind kind, int candidate) => candidate == slot;
}

[Collection(EntitiesCollection.Name)]
public class StoreTests : IDisposable
{
    const uint Identity = 1u << 0;
    const uint Extras = 1u << 1;

    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-v3-store-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();
    readonly StoreProbeShape _shape = new();

    public StoreTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Post = a => _posted.Enqueue(a);     // the UI drain, run by the test thread where it belongs
        Store.Register(_shape);
        Store.Use(_dbPath);
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Pins = null;                         // a pin source is process-wide; never leak one into the next test
        Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    Scope Boot()
    {
        Entities.Boot(CatalogScope.Fake());
        Store.Flush();                             // let Warm resolve the scope id before anything reads or writes
        return Entities.Current;
    }

    void Write(params (string Uri, string? Title, int DurationMs, uint Known, int Auth, int FetchedAt, int Touched)[] rows)
    {
        _shape.Out.Clear();
        _shape.Out.AddRange(rows);
        Staging staging = Staging.Rent();
        Assert.True(Store.WriteBehind(staging));   // the store owns the staging from here
        Store.Flush();
        _shape.Out.Clear();
    }

    void WriteIds(params (EntityId Id, string? Title, int DurationMs, uint Known, int Auth, int FetchedAt, int Touched)[] rows)
    {
        _shape.OutIds.Clear();
        _shape.OutIds.AddRange(rows);
        Staging staging = Staging.Rent();
        Assert.True(Store.WriteBehind(staging));
        Store.Flush();
        _shape.OutIds.Clear();
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    /// <summary>A real-shaped catalog uri: <c>spotify:track:</c> + 22 base62 characters, which is the GID form — the
    /// half of the population that carries no uri string in memory at all.</summary>
    static string GidUri(int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL + 11, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 3), buf);
        return "spotify:track:" + new string(buf);
    }

    /// <summary>Sixteen bytes that look like a gid off the wire.</summary>
    static byte[] Gid(int seed)
    {
        var gid = new byte[Base62.GidBytes];
        for (int i = 0; i < gid.Length; i++) gid[i] = (byte)(seed * 31 + i * 7 + 3);
        return gid;
    }

    // ── the schema (pure) ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_ddl_is_the_v3_schema_plus_one_table_per_registered_kind()
    {
        string ddl = Store.Ddl();

        // The fixed half, verbatim from plan §4.4 — these five are what every kind's rows hang off.
        Assert.Contains("CREATE TABLE IF NOT EXISTS scope(", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS edge(", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS edge_state(", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS intent(", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS meta(", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_edge_child ON edge(scope_id, kind, child);", ddl);

        // The generated half: the kind's own columns, the shared bookkeeping, and WITHOUT ROWID because the primary
        // key IS the row (a rowid would be a second index over a table that is only ever read by (scope, uri)).
        Assert.Contains("CREATE TABLE IF NOT EXISTS track(scope_id INT NOT NULL, uri TEXT NOT NULL, title TEXT, duration_ms INT, identity_auth INT, known INT NOT NULL DEFAULT 0, fetched_at INT NOT NULL DEFAULT 0, touched INT NOT NULL DEFAULT 0, PRIMARY KEY(scope_id,uri)) WITHOUT ROWID;", ddl);
        // P11: a real indexed search, and the LRU order the sweep reads rows in.
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_track_title ON track(scope_id, title COLLATE NOCASE);", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_track_gc ON track(touched);", ddl);
    }

    [Fact]
    public void A_kind_that_gains_a_column_changes_the_fingerprint()
    {
        string before = Store.Ddl();
        Store.Shutdown();
        Store.Register(new StoreProbeShape(extraColumn: true));
        string after = Store.Ddl();

        Assert.NotEqual(before, after);
        Assert.NotEqual(Store.Fingerprint(before), Store.Fingerprint(after));
        Assert.Equal(Store.Fingerprint(before), Store.Fingerprint(before));   // and it is a function, not a clock
    }

    // ── the round trip ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_row_survives_the_file()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        Write(("spotify:track:a", "Alpha", 191_000, Identity, (int)Authority.Full, 120, 130));

        int slot = t.Slot("spotify:track:a".AsSpan());
        Assert.True(Store.Read(scope, t, new[] { slot }, Identity, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        var row = Assert.Single(_shape.In);
        Assert.Equal("spotify:track:a", row.Uri);
        Assert.Equal("Alpha", row.Title);
        Assert.Equal(191_000L, row.DurationMs);
        Assert.Equal(Identity, row.Known);
        Assert.Equal((long)Authority.Full, row.Auth);
        // App seconds out, unix seconds on disk, app seconds back (P7) — and the conversion is an identity while the
        // epoch holds still, which is the only property anything depends on.
        Assert.Equal(120, row.FetchedAt);
        Assert.Equal(130, row.Touched);
    }

    /// <summary>THE CONTRACT A TEXT KEY HAS TO MEET (Store.cs's header decision). A row's identity in memory is 24
    /// packed bytes; on disk it is <c>uri TEXT</c>. If <c>Format</c> and <c>Parse</c> were not exact inverses the
    /// cache would answer under a different id — two rows for one entity, forever, and nothing would notice: the
    /// SELECT would simply return nothing and the row would look uncached.</summary>
    [Fact]
    public void A_gid_row_round_trips_through_the_file_as_the_same_identity()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        string uri = GidUri(1);
        int slot = t.Slot(uri.AsSpan());
        EntityId id = t.Id[slot];
        Assert.Equal(EntityForm.Gid, id.Form);                  // no uri string in the interner for this row at all

        Write((uri, "Alpha", 191_000, Identity, (int)Authority.Full, 120, 130));
        Store.Read(scope, t, new[] { slot }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        var row = Assert.Single(_shape.In);                     // the read FOUND it, so the formatted key matched…
        Assert.Equal(uri, row.Uri);                             // …character for character…
        Assert.Equal(id, EntityId.Parse(row.Uri.AsSpan()));     // …and parses back to the identical packed id…
        Assert.Equal(slot, t.Slot(row.Uri.AsSpan()));           // …which is the identical row.
        Assert.Equal("Alpha", row.Title);
    }

    /// <summary>The other form, and the reason the key stayed TEXT: a text-form id's payload is an index into a
    /// PROCESS-LOCAL interner, so it is the only form that could not have been persisted as packed bytes at all.</summary>
    [Fact]
    public void A_text_form_row_round_trips_through_the_file_as_the_same_identity()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        const string uri = "wavee:local:file:c3RvcmUtcm91bmQtdHJpcA";
        int slot = t.Slot(uri.AsSpan());
        EntityId id = t.Id[slot];
        Assert.Equal(EntityForm.Text, id.Form);

        Write((uri, "Local", 4_000, Identity, (int)Authority.Local, 1, 1));
        Store.Read(scope, t, new[] { slot }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        var row = Assert.Single(_shape.In);
        Assert.Equal(uri, row.Uri);
        Assert.Equal(id, EntityId.Parse(row.Uri.AsSpan()));
        Assert.Equal(slot, t.Slot(row.Uri.AsSpan()));
    }

    /// <summary>Wave 2's shape, end to end (doc §2). The wire hands over 16 raw bytes; 0.2.9 base62-ENCODED them into
    /// a uri (569 ns) that the next line hashed back into a slot. The staged row now carries the packed id, the STORE
    /// thread formats the one string sqlite needs, and the row comes back to the same slot the 3 ns gid door finds.
    ///
    /// <para><c>Store.Stats</c> IS PROCESS-GLOBAL AND NOTHING RESETS IT — not <c>Shutdown</c>, not <c>Boot</c>, not a
    /// fresh scope; it is a session odometer for the diagnostics page. So every fact about it here is a DELTA. The
    /// absolute <c>BadKeys == 0</c> this line used to assert passed alone and failed in the suite, because the very
    /// next test deliberately drives that counter to 1 — a test whose verdict was decided by the order xunit happened
    /// to pick.</para></summary>
    [Fact]
    public void A_decoder_that_only_ever_had_a_gid_keys_a_row_with_no_uri_text_in_the_process()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        byte[] gid = Gid(9);
        EntityId id = EntityId.ForGid(EntityKind.Track, gid);
        int badBefore = Store.Stats.BadKeys;

        WriteIds((id, "Packed", 4_000, Identity, (int)Authority.Full, 10, 10));

        int slot = t.Slot(EntityKind.Track, gid);
        Assert.Equal(id, t.Id[slot]);
        Store.Read(scope, t, new[] { slot }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        var row = Assert.Single(_shape.In);
        Assert.Equal("Packed", row.Title);
        Assert.Equal(id, EntityId.Parse(row.Uri.AsSpan()));
        Assert.Equal(badBefore, Store.Stats.BadKeys);           // the packed door took it without complaint
    }

    /// <summary>And the door that must stay shut. The store thread may not resolve a <c>StringId</c>: a released id's
    /// slot is cleared 16 ticks later and a queued write-behind job outlives that window by a wide margin. So a
    /// text-form id reaching the packed overload is DROPPED and counted as a fault, rather than keyed by a string
    /// that may already have been reclaimed — the arena overload is the one a decoder with text uses, and it is safe
    /// for every form.</summary>
    [Fact]
    public void A_text_form_id_is_refused_by_the_packed_door_instead_of_being_resolved_off_the_ui_thread()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        int slot = t.Slot("wavee:local:file:refused-20260912".AsSpan());
        int badBefore = Store.Stats.BadKeys;
        int faultsBefore = Store.Stats.Faults;                  // session odometers: read a baseline, assert a delta

        WriteIds((t.Id[slot], "Nope", 1, Identity, 3, 1, 1));
        Store.Read(scope, t, new[] { slot }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        Assert.Empty(_shape.In);                                // nothing was written under a wrong key…
        Assert.Equal(badBefore + 1, Store.Stats.BadKeys);       // …and it is counted, not silent
        Assert.Equal(faultsBefore, Store.Stats.Faults);         // …and it is not confused with a database fault
    }

    [Fact]
    public void Only_the_rows_that_were_asked_for_come_back()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        Write(("spotify:track:a", "Alpha", 1, Identity, 3, 1, 1),
              ("spotify:track:b", "Beta", 2, Identity, 3, 1, 1),
              ("spotify:track:c", "Gamma", 3, Identity, 3, 1, 1));

        int slot = t.Slot("spotify:track:b".AsSpan());
        Store.Read(scope, t, new[] { slot }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        Assert.Equal("Beta", Assert.Single(_shape.In).Title);
    }

    [Fact]
    public void A_thin_answer_never_blanks_what_a_full_one_wrote()
    {
        // D16 in sql. The upsert coalesces every value column and ORs `known`, so an answer that says nothing about
        // the title leaves the stored title alone — the same rule `Table.Accepts` enforces in memory, and the reason
        // a search hit landing after a full fetch cannot degrade a row on disk either.
        Scope scope = Boot();
        Table t = scope.Tracks;
        Write(("spotify:track:a", "Alpha", 191_000, Identity, (int)Authority.Full, 100, 100));
        Write(("spotify:track:a", null, 191_500, Extras, (int)Authority.Thin, 200, 200));

        Store.Read(scope, t, new[] { t.Slot("spotify:track:a".AsSpan()) }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        var row = Assert.Single(_shape.In);
        Assert.Equal("Alpha", row.Title);                       // survived the thin write
        Assert.Equal(191_500L, row.DurationMs);                  // the thin write DID have a duration
        Assert.Equal(Identity | Extras, row.Known);             // known is a union, never a replacement
        Assert.Equal((long)Authority.Full, row.Auth);           // authority climbs and never falls
        Assert.Equal(200, row.FetchedAt);
    }

    [Fact]
    public void A_cold_read_stamps_every_slot_it_asked_about_even_the_misses()
    {
        // THE NEGATIVE MEMO. Without this the planner sends the same never-stored rows to sqlite on every page
        // mount, forever: the disk cannot answer, and nothing records that it was asked.
        Scope scope = Boot();
        Table t = scope.Tracks;
        int a = t.Slot("spotify:track:missing-1".AsSpan());
        int b = t.Slot("spotify:track:missing-2".AsSpan());
        Entities.Now = 42;

        Store.Read(scope, t, new[] { a, b }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        Assert.Empty(_shape.In);
        Assert.Equal(42, t.FetchedAt[a]);
        Assert.Equal(42, t.FetchedAt[b]);
    }

    [Fact]
    public void An_answer_for_a_replaced_scope_is_dropped_whole()
    {
        // C7. The read left for the disk against one table set and came back to another; committing it would write a
        // German-market row into the American set's columns.
        Scope scope = Boot();
        Table t = scope.Tracks;
        Write(("spotify:track:a", "Alpha", 1, Identity, 3, 5, 5));
        int slot = t.Slot("spotify:track:a".AsSpan());
        Entities.Now = 42;

        Store.Read(scope, t, new[] { slot }, Identity, FetchPriority.Visible);
        Store.Flush();
        Entities.Switch(CatalogScope.Fake(locale: "de-DE", market: "DE"));
        DrainPosts();

        Assert.Equal(0, t.FetchedAt[slot]);                     // the old set was not touched at all
        Assert.Equal(0u, t.Known[slot]);
    }

    // ── the cache is a cache ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_file_written_by_another_schema_is_deleted_not_migrated()
    {
        // plan §4.4: "a v2 file is deleted, not migrated — it is a cache". Every row in it can be asked for again,
        // and a migration would be a second schema to keep correct forever.
        Scope scope = Boot();
        Write(("spotify:track:a", "Alpha", 1, Identity, 3, 1, 1));
        Store.Shutdown();

        var reopened = new StoreProbeShape(extraColumn: true);   // the same kind, one column more
        Store.Register(reopened);
        scope = Boot();
        Table t = scope.Tracks;

        Store.Read(scope, t, new[] { t.Slot("spotify:track:a".AsSpan()) }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        Assert.Empty(reopened.In);
        Assert.True(Store.IsOpen);                              // and the new file is perfectly usable
    }

    [Fact]
    public void The_same_schema_keeps_the_file()
    {
        Scope scope = Boot();
        Write(("spotify:track:a", "Alpha", 1, Identity, 3, 1, 1));
        Store.Shutdown();

        Store.Register(_shape);                                  // the identical shape ⇒ the identical fingerprint
        scope = Boot();
        Table t = scope.Tracks;
        Store.Read(scope, t, new[] { t.Slot("spotify:track:a".AsSpan()) }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        Assert.Equal("Alpha", Assert.Single(_shape.In).Title);
    }

    [Fact]
    public void With_no_path_there_is_no_database_and_every_door_says_so()
    {
        Store.Shutdown();
        Store.Use(null);
        Scope scope = Boot();

        Assert.False(Store.IsOpen);
        Assert.False(Store.Read(scope, scope.Tracks, new[] { scope.Tracks.Slot("spotify:track:a".AsSpan()) }, Identity, FetchPriority.Visible));
        Assert.Equal(0L, Store.Journal(1, "x"u8));
        Assert.Equal(0, Store.PendingIntents());
        Staging staging = Staging.Rent();
        Assert.False(Store.WriteBehind(staging));
        Staging.Return(staging);                                 // refused ⇒ still the caller's
    }

    // ── the memory trim (R2, defect 1) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>THE RETURN ON REF-COUNTING THE GRAPH (defect 1, doc §4.4). Before 2026-09-12 nothing in
    /// <c>Entities/</c> called <c>AddRef</c>, and an id nobody AddRefs is PERMANENT
    /// (<c>StringTable.cs:26</c>) — so a trim freed a row's columns and left its uri, title, image and artist line in
    /// the interner for the life of the process. A scope's memory floor could only ever rise, which made the whole
    /// trim pointless. It is one assertion: after the pass, the interner is back where it started.
    ///
    /// <para>The rows carry a TITLE as well as a uri, because the release has two legs and they live in two places:
    /// <c>Table.UnbindId</c> hands back the identity's own <c>StringId</c>, and <c>Table.ReleaseText</c> — the
    /// kind's override — hands back its text COLUMNS. A row with only a uri would let the second leg be deleted
    /// without this test noticing. Verified by deleting each in turn: both make this fact fail.</para></summary>
    [Fact]
    public void A_trimmed_scope_hands_its_text_back_to_the_interner()
    {
        Scope scope = Boot();
        TrackTable t = scope.Tracks;
        int mapBefore = Entities.Strings.MapCount;
        int trimmedBefore = Store.Stats.Trimmed;
        var slots = new int[8];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = t.Slot($"wavee:local:file:trim-{i}-20260912".AsSpan());
            t.SetText(ref t.Title, slots[i], Entities.Strings.Intern($"trim title {i} 20260912"));
        }
        Assert.Equal(mapBefore + slots.Length * 2, Entities.Strings.MapCount);   // a uri and a title per row

        Entities.Now = 3_000_000;                               // every row is now well past the 30-day TTL
        SweepPlan plan = Store.TrimMemory(scope, t, SweepPolicy.Default);

        Assert.Equal(slots.Length, plan.Victims);
        Assert.Equal(slots.Length, plan.TtlVictims);
        Assert.Equal(0, plan.BudgetVictims);                    // memory runs the TTL leg only — see TrimMemory
        Assert.Equal(mapBefore, Entities.Strings.MapCount);     // THE POINT: the floor actually came back down
        Assert.Equal(0, t.LiveCount);
        Assert.Equal(trimmedBefore + slots.Length, Store.Stats.Trimmed);
    }

    /// <summary>A slot IS a bound page's whole handle on a row, and <c>FreeSlot</c> hands it to the next entity that
    /// asks. Two pins are therefore unconditional: a row with a request out (the planner marks <c>Inflight</c> BEFORE
    /// it queues, so a bucket is holding that slot and the answer would land on a stranger) and anything the shell
    /// says is live.</summary>
    [Fact]
    public void A_trim_never_takes_a_row_with_a_request_out_or_one_the_shell_pinned()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        int inflight = t.Slot("wavee:local:file:pin-inflight-20260912".AsSpan());
        int pinned = t.Slot("wavee:local:file:pin-shell-20260912".AsSpan());
        int cold = t.Slot("wavee:local:file:pin-cold-20260912".AsSpan());
        t.Inflight[inflight] = 7;                               // what Fetch.Plan stamps before it queues (C7)
        Store.Pins = new PinOneSlot(pinned);
        Entities.Now = 3_000_000;

        SweepPlan plan = Store.TrimMemory(scope, t, SweepPolicy.Default);

        Assert.Equal(1, plan.Victims);
        Assert.Equal(EntityForm.None, t.Id[cold].Form);         // the unheld one went…
        Assert.Equal(EntityForm.Text, t.Id[inflight].Form);     // …and neither pin did
        Assert.Equal(EntityForm.Text, t.Id[pinned].Form);
    }

    /// <summary>The trim is memory's, not the file's: it works with no database at all, which is also the shape a
    /// <c>--fake</c> session and every unit test runs in.</summary>
    [Fact]
    public void A_trim_needs_no_database()
    {
        Store.Shutdown();
        Store.Use(null);
        Scope scope = Boot();
        Assert.False(Store.IsOpen);
        int mapBefore = Entities.Strings.MapCount;
        scope.Tracks.Slot("wavee:local:file:trim-no-db-20260912".AsSpan());

        Entities.Now = 3_000_000;
        int freed = Store.TrimMemory(scope);

        Assert.Equal(1, freed);
        Assert.Equal(mapBefore, Entities.Strings.MapCount);
    }

    // ── the intent journal (C6/D22) ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_intent_is_on_disk_before_the_call_returns()
    {
        Boot();

        long id = Store.Journal(kind: 7, "spotify:track:a"u8);

        Assert.True(id > 0);
        Assert.Equal(1, Store.PendingIntents());                 // synchronous: no flush, no wait, it is already there
    }

    [Fact]
    public void An_accepted_intent_leaves_no_row_and_a_rejected_one_stays_for_a_human()
    {
        Boot();
        long ok = Store.Journal(1, "a"u8);
        long bad = Store.Journal(1, "b"u8);

        Store.JournalSettle(ok, true);
        Store.JournalSettle(bad, false);
        Store.Flush();

        // C6: never replayed automatically. The failed row is a record of what was in flight when the process died,
        // for the shell to reconcile — not a queue that fires itself on the next launch.
        Assert.Equal(1, Store.PendingIntents());
    }

    // ── the counters ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every counter here is a SESSION odometer that no <c>Shutdown</c>, <c>Boot</c> or scope switch resets,
    /// so this fact is written as a delta over a baseline taken inside the test. An absolute reading would be a
    /// statement about whichever tests xunit ran first, which is not a statement about the store.</summary>
    [Fact]
    public void The_diagnostics_row_counts_what_actually_happened()
    {
        Scope scope = Boot();
        var before = Store.Stats;
        Write(("spotify:track:a", "Alpha", 1, Identity, 3, 1, 1));
        Store.Read(scope, scope.Tracks, new[] { scope.Tracks.Slot("spotify:track:a".AsSpan()) }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();

        var stats = Store.Stats;
        Assert.True(stats.Open);
        Assert.Equal(_dbPath, stats.Path);
        Assert.True(stats.Writes > before.Writes);
        Assert.True(stats.WriteRows > before.WriteRows);
        Assert.True(stats.Reads > before.Reads);
        Assert.True(stats.ReadRows > before.ReadRows);
        Assert.Equal(before.Faults, stats.Faults);
    }
}
