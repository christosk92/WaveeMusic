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
//
// SINCE WAVE D1 (cache integrity, 2026-09-19) the file is named for its schema and every open goes through ONE
// recreate path with ONE always-on `store.open` line; a damaged file is rebuilt ONCE mid-session (`Store.Rebuild`,
// the door the fault path uses); and "Clear metadata" (`Store.DropCatalog`) empties the cache tier while keeping the
// library, the journal and the ledgers. Those facts read the always-on log lines they promise — the lines ARE the
// contract a "why was nothing cached" report depends on. The reaper and the all-or-nothing delete are pinned in
// StoreFilesTests; the pure recovery verdict in StoreHealthTests.

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

    Scope Boot() => Boot(CatalogScope.Fake());

    Scope Boot(CatalogScope key)
    {
        Entities.Boot(key);
        Store.Flush();                             // let Warm resolve the scope id before anything reads or writes
        return Entities.Current;
    }

    /// <summary>A scope with a REAL account (StoreEdgeTests' shape): <c>CatalogScope.Fake()</c> has an empty account,
    /// so no library relation hangs off anything.</summary>
    static CatalogScope AccountScope(string account) => new("wavee-test", account, "en-US", "US", 0, true);

    string FileNameOnly => Path.GetFileName(_dbPath);

    /// <summary>The newest always-on line with this event id whose <paramref name="field"/> is <paramref name="value"/>.
    /// The ring is process-wide, but this collection runs alone (DisableParallelization) and every file name and step
    /// here carries a fresh guid, so the match is this test's own line. The line IS the contract being pinned: the D1
    /// gate reads `store.open outcome=…` and `store.recovered …` off the log, and nothing else says why a session ran
    /// without its cache.</summary>
    static WaveeLogEntry LastLine(string eventId, string field, string value)
    {
        WaveeLogEntry[] ring = Log.Snapshot();
        for (int i = ring.Length - 1; i >= 0; i--)
            if (ring[i].EventId == eventId && FieldOf(ring[i], field) == value) return ring[i];
        Assert.Fail($"no {eventId} line with {field}={value} in the log ring");
        return default;
    }

    static string? FieldOf(in WaveeLogEntry entry, string name)
    {
        if (entry.Fields is not { } fields) return null;
        foreach (WaveeLogField f in fields)
            if (f.Name == name) return f.Value;
        return null;
    }

    /// <summary>A second, unpooled connection to the store's file — what any other reader of a WAL database is. Used
    /// to look at the file directly (and to plant rows no public door writes), never to change what the store does.
    /// Unpooled, so no handle outlives the call and the store's rename-first delete is never refused by the test.</summary>
    Microsoft.Data.Sqlite.SqliteConnection Direct()
    {
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false };
        var c = new Microsoft.Data.Sqlite.SqliteConnection(cs.ToString());
        c.Open();
        return c;
    }

    /// <summary>Every statement in <paramref name="sql"/>, on <see cref="Direct"/>.</summary>
    void Sql(string sql)
    {
        using var c = Direct();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>One value, read on <see cref="Direct"/>.</summary>
    object? SqlValue(string sql)
    {
        using var c = Direct();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    long Count(string sql) => SqlValue(sql) is long n ? n : -1;

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

    /// <inheritdoc cref="GidUri(int)"/>
    /// <param name="token">The kind's own uri token (plan §4.1: <c>track</c>, <c>album</c>, <c>artist</c>,
    /// <c>playlist</c>, <c>show</c>, <c>episode</c> — the six <see cref="EntityId.IsGidKind"/> kinds. <c>user</c> and
    /// <c>concert</c> are NOT gid kinds — this spelling for either parses back as the TEXT form, on purpose.)</param>
    static string GidUri(string token, int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL + 17, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 5), buf);
        return "spotify:" + token + ":" + new string(buf);
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
        // and a migration would be a second schema to keep correct forever. In the app a schema change names a
        // DIFFERENT file (Store.FileName) and this never happens at open; a test's path carries no schema, so here it
        // is the `stale` verdict, set aside and recreated through the one recreate path.
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
    public void A_file_sqlite_cannot_read_is_deleted_and_the_store_still_opens()
    {
        // 2026-09-18: a malformed library.db threw at the FIRST pragma, before the fingerprint read could call it
        // unreadable, and every launch that day ran memory-only. Garbage on disk is a cache miss, never an outage.
        File.WriteAllBytes(_dbPath, new byte[8192].Select(static (_, i) => (byte)(i * 31 + 7)).ToArray());

        Scope scope = Boot();

        Assert.True(Store.IsOpen);
        // …and the log says so in the one line a "why was nothing cached" report greps for: SQLITE_NOTADB at the
        // pragmas, set aside and recreated — at Warning, because a whole cache was just thrown away.
        WaveeLogEntry open = LastLine("store.open", "file", FileNameOnly);
        Assert.Equal("recreated:unreadable:26", FieldOf(open, "outcome"));
        Assert.Equal(WaveeLogLevel.Warning, open.Level);
        Write(("spotify:track:a", "Alpha", 1, Identity, 3, 1, 1));   // and it persists like any fresh file
        Assert.True(Store.IsOpen);
        _ = scope;
    }

    /// <summary>The fingerprint verdict, through the same single recreate path. Under the schema-named file a stale
    /// fingerprint at the path only happens to a file copied or renamed by hand — so the file here is written by hand,
    /// with an old-shaped <c>track</c> table a kept file would trip over forever (<c>CREATE TABLE IF NOT EXISTS</c> over
    /// it is a no-op, and the first upsert names a column it does not have).</summary>
    [Fact]
    public void A_file_stamped_with_another_fingerprint_is_recreated_and_persists_afterwards()
    {
        Sql("CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT); INSERT INTO meta VALUES('schema','0123456789abcdef');" +
            "CREATE TABLE track(uri TEXT PRIMARY KEY, title TEXT); INSERT INTO track VALUES('spotify:track:a','Old');");

        Scope scope = Boot();
        Table t = scope.Tracks;
        int faults = Store.Stats.Faults;

        Assert.True(Store.IsOpen);
        Assert.Equal("recreated:stale", FieldOf(LastLine("store.open", "file", FileNameOnly), "outcome"));
        Store.Read(scope, t, new[] { t.Slot("spotify:track:a".AsSpan()) }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();
        Assert.Empty(_shape.In);                                 // nothing of the old file survived…

        Write(("spotify:track:b", "Beta", 2, Identity, 3, 1, 1));
        Store.Read(scope, t, new[] { t.Slot("spotify:track:b".AsSpan()) }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();
        Assert.Equal("Beta", Assert.Single(_shape.In).Title);    // …and the fresh one takes rows and answers reads
        Assert.Equal(faults, Store.Stats.Faults);
    }

    /// <summary>One always-on line per open, whatever happened: a brand-new file says <c>created</c> (Info), the same
    /// file on the next boot says <c>opened</c>, and both name the schema they hold.</summary>
    [Fact]
    public void Every_open_writes_one_store_open_line_created_then_opened()
    {
        Boot();
        WaveeLogEntry first = LastLine("store.open", "file", FileNameOnly);
        Assert.Equal("created", FieldOf(first, "outcome"));
        Assert.Equal("0", FieldOf(first, "walBytes"));           // nothing was there before sqlite looked
        Assert.Equal($"{Store.Fingerprint(Store.Ddl()):x16}", FieldOf(first, "fingerprint"));
        Assert.Equal(WaveeLogLevel.Info, first.Level);

        Store.Shutdown();
        Boot();
        WaveeLogEntry second = LastLine("store.open", "file", FileNameOnly);
        Assert.True(second.Sequence > first.Sequence);
        Assert.Equal("opened", FieldOf(second, "outcome"));
        Assert.Equal(WaveeLogLevel.Info, second.Level);
    }

    /// <summary>THE NAME CARRIES THE SCHEMA (wave D1). A build whose DDL differs names a different file, so it never
    /// opens — never mind deletes — the file another build is using.</summary>
    [Fact]
    public void The_file_name_carries_the_schema_and_a_schema_change_names_another_file()
    {
        string name = Store.FileName;
        Assert.Equal($"library.{Store.Fingerprint(Store.Ddl()):x16}.db", name);

        Store.Shutdown();
        Store.Register(new StoreProbeShape(extraColumn: true));  // the same kind, one column more

        Assert.NotEqual(name, Store.FileName);
    }

    [Theory]
    [InlineData(11, true)]    // SQLITE_CORRUPT
    [InlineData(26, true)]    // SQLITE_NOTADB
    [InlineData(1, false)]    // SQLITE_ERROR: the statement, not the file
    [InlineData(5, false)]    // SQLITE_BUSY: another writer, never a reason to delete
    public void Only_a_verdict_about_the_file_deletes_it(int code, bool unreadable)
        => Assert.Equal(unreadable, Store.IsUnreadableFile(code));

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
        Store.DropCatalog();                                     // no store: nothing to clear, nothing to wait for
        Store.Rebuild("no-store");                               // …and nothing to rebuild
        Assert.False(Store.IsOpen);
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

    // ── mid-session recovery (wave D1) ──────────────────────────────────────────────────────────────────────────────
    //
    // `Store.Rebuild` is the recovery door the fault path uses the first time sqlite says the FILE is damaged. It is
    // driven directly here: corrupting bytes under a live handle is not deterministic (what sqlite reports, and when,
    // depends on which page the next statement happens to touch), and the door is the same either way.

    [Fact]
    public void A_rebuild_mid_session_leaves_an_open_store_on_a_fresh_file()
    {
        Scope scope = Boot();
        Table t = scope.Tracks;
        Write(("spotify:track:a", "Alpha", 1, Identity, 3, 1, 1));
        Assert.True(Store.Journal(7, "unsynced"u8) > 0);
        Store.MetaSet("library.sync-token", "ledger-of-the-old-file");
        Store.Flush();
        int faults = Store.Stats.Faults;
        string step = "rebuild-" + Guid.NewGuid().ToString("n");

        Store.Rebuild(step);
        Store.Flush();                                           // the store thread services it before the next job
        DrainPosts();

        Assert.True(Store.IsOpen);
        WaveeLogEntry recovered = LastLine("store.recovered", "step", step);
        Assert.Equal("reopened", FieldOf(recovered, "outcome"));
        Assert.Equal("1", FieldOf(recovered, "lostIntents"));   // counted BEFORE the close, while the file answered
        Assert.Equal(WaveeLogLevel.Warning, recovered.Level);
        Assert.Equal("recreated:recovery:" + step, FieldOf(LastLine("store.open", "file", FileNameOnly), "outcome"));
        Assert.Null(Store.MetaGet("library.sync-token"));        // a ledger with no list behind it is forgotten
        Assert.Equal(faults, Store.Stats.Faults);

        Store.Read(scope, t, new[] { t.Slot("spotify:track:a".AsSpan()) }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();
        Assert.Empty(_shape.In);                                 // the old rows went with the old file…

        Write(("spotify:track:b", "Beta", 2, Identity, 3, 1, 1));
        Store.Read(scope, t, new[] { t.Slot("spotify:track:b".AsSpan()) }, Identity, FetchPriority.Visible);
        Store.Flush();
        DrainPosts();
        Assert.Equal("Beta", Assert.Single(_shape.In).Title);    // …and the fresh one takes writes (re-prepared) and reads
        Assert.Equal(faults, Store.Stats.Faults);
    }

    /// <summary>The journal runs on its CALLER's thread, so it is the one door a rebuild can pull a connection out from
    /// under. After the swap it writes into the fresh file — and an id minted against the OLD file (whose rowid a
    /// fresh file hands out again, from 1) must never settle the new file's intent.</summary>
    [Fact]
    public void The_journal_survives_a_rebuild_and_an_old_id_never_settles_a_new_intent()
    {
        Boot();
        long before = Store.Journal(1, "before"u8);
        Assert.True(before > 0);

        Store.Rebuild("journal-" + Guid.NewGuid().ToString("n"));
        Store.Flush();

        Assert.Equal(0, Store.PendingIntents());                 // the damaged file's intent went with it (and was counted)
        long after = Store.Journal(1, "after"u8);
        Assert.True(after > 0);
        Assert.NotEqual(before, after);                          // the same rowid, a different file
        Assert.Equal(1, Store.PendingIntents());

        Store.JournalSettle(before, ok: true);
        Store.Flush();
        Assert.Equal(1, Store.PendingIntents());                 // the stale id settled nothing

        Store.JournalSettle(after, ok: true);
        Store.Flush();
        Assert.Equal(0, Store.PendingIntents());
    }

    /// <summary>The race itself: journal calls on another thread while the store thread retires, recreates and swaps
    /// the file. Whatever the interleaving, every call must come back with an id and none may touch a disposed
    /// connection (which would surface as a fault). The interleaving varies run to run; the verdict does not.</summary>
    [Fact]
    public void A_journal_racing_a_rebuild_never_uses_a_retired_connection()
    {
        Boot();
        int faults = Store.Stats.Faults;
        var ids = new ConcurrentBag<long>();
        using var stop = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            for (int i = 0; i < 2_000 && !stop.IsSet; i++) ids.Add(Store.Journal(3, "race"u8));
        });
        writer.Start();

        Store.Rebuild("race-" + Guid.NewGuid().ToString("n"));
        Store.Flush();
        stop.Set();
        writer.Join();

        Assert.True(Store.IsOpen);
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.True(id > 0));
        Assert.Equal(faults, Store.Stats.Faults);
    }

    // ── Settings ▸ Storage ▸ "Clear metadata" ───────────────────────────────────────────────────────────────────────

    /// <summary>The cache tier goes — catalog rows, palette rows, every non-library edge list — and everything that is
    /// not a cache stays: the library relations, the intent journal (an unsynced like is the user's, not metadata), and
    /// the sync ledgers that pair with the library. Deliberately not "delete the file", which would take all of it.</summary>
    [Fact]
    public void Clearing_metadata_drops_the_cache_tier_and_keeps_the_library_the_journal_and_the_ledgers()
    {
        Store.RegisterLibraryEdges();
        Scope scope = Boot(AccountScope("clear-" + Guid.NewGuid().ToString("n")));
        int me = scope.MeSlot;
        Assert.NotEqual(Table.None, me);
        EntityId meId = scope.Users.Id[me];
        StagedId parent = meId;

        Write(("spotify:track:a", "Alpha", 1, Identity, 3, 1, 1));               // a catalog row (cache)

        int liked = scope.Tracks.Slot("wavee:test:track:clear-liked".AsSpan()); // the user's Liked list (kept)
        Staging s = Staging.Rent();
        var run = s.Run(Relation.Liked);
        run.Add(scope.Tracks.Id[liked]).At = 1_700_000_000;
        run.End(in parent);
        Entities.Commit(s);
        Assert.True(Store.WriteBehind(s));

        int album = scope.Albums.Slot("wavee:test:album:clear-album".AsSpan()); // an album's track list (cache)
        var albumTracks = new EdgeTable<NoEdge>();
        albumTracks.Replace(album, new[] { liked }, ReadOnlySpan<NoEdge>.Empty, EdgeState.Complete, 1);
        Assert.True(Store.SaveEdges(EdgeRelation.AlbumTracks, albumTracks, album, scope.Albums.Id[album], scope.Tracks));

        Assert.True(Store.Journal(7, "unsynced like"u8) > 0);                  // the journal (kept)
        Store.MetaSet("library.sync-token", "kept");                            // a ledger (kept)
        Store.Flush();
        Sql("INSERT INTO palette(key,known,ts,dark,light) VALUES('cover:clear',1,0,NULL,NULL);");   // a cover's colours (cache)

        Assert.Equal(1L, Count("SELECT count(*) FROM track;"));
        Assert.Equal(1L, Count("SELECT count(*) FROM palette;"));
        Assert.Equal(1L, Count($"SELECT count(*) FROM edge WHERE kind={(int)EdgeRelation.AlbumTracks};"));
        int faults = Store.Stats.Faults;

        Store.DropCatalog();                                     // blocking: the bytes are gone when it returns

        Assert.Equal(0L, Count("SELECT count(*) FROM track;"));
        Assert.Equal(0L, Count("SELECT count(*) FROM palette;"));
        Assert.Equal(0L, Count($"SELECT count(*) FROM edge WHERE kind={(int)EdgeRelation.AlbumTracks};"));
        Assert.Equal(0L, Count($"SELECT count(*) FROM edge_state WHERE kind={(int)EdgeRelation.AlbumTracks};"));
        Assert.Equal(1L, Count($"SELECT count(*) FROM edge WHERE kind={(int)EdgeRelation.Liked};"));
        Assert.Equal(1L, Count($"SELECT count(*) FROM edge_state WHERE kind={(int)EdgeRelation.Liked};"));
        Assert.Equal("kept", SqlValue("SELECT value FROM meta WHERE key='library.sync-token';") as string);
        Assert.Equal(1, Store.PendingIntents());
        Assert.Equal(faults, Store.Stats.Faults);

        // The kept list still reads back through the store's own door, into a table that forgot it.
        scope.Edges.Liked.Clear(me);
        Assert.True(Store.ReadEdges(scope, EdgeRelation.Liked, meId));
        Store.Flush();
        DrainPosts();
        Assert.Equal(EdgeState.Complete, scope.Edges.Liked.State(me));
        Assert.True(scope.Edges.Liked.Targets(me).SequenceEqual(new[] { liked }));
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

    // ── small key/value persistence (meta) ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_meta_value_set_is_immediately_visible_to_MetaGet()
    {
        Boot();
        Store.MetaSet("library.sync-token", "abc123");

        Assert.Equal("abc123", Store.MetaGet("library.sync-token"));
    }

    [Fact]
    public void A_meta_key_that_was_never_set_answers_null_cleanly()
    {
        Boot();

        Assert.Null(Store.MetaGet("no-such-key-20260915"));
    }

    // ── the kind shapes (G-007: the "kind owners write their shapes" half) ─────────────────────────────────────────

    /// <summary>Step 1's prerequisite, exercised end to end through <c>TrackShape</c>'s <c>album_uri</c> column: the
    /// store thread must bind a cross-reference identity whichever form the decoder staged it in — the packed
    /// <see cref="EntityId"/> a gid decoder holds, or the arena <see cref="TextRef"/> a text decoder holds — without
    /// ever touching the interner.</summary>
    [Fact]
    public void RowWriter_Id_binds_a_cross_reference_column_in_either_staged_form()
    {
        Store.Register(new TrackShape());
        Scope scope = Boot();
        TrackTable tracks = scope.Tracks;
        AlbumTable albums = scope.Albums;

        string albumGidUri = GidUri("album", 1);
        int albumGidSlot = albums.Slot(albumGidUri.AsSpan());
        EntityId albumGidId = albums.Id[albumGidSlot];
        Assert.Equal(EntityForm.Gid, albumGidId.Form);

        string trackUriA = GidUri("track", 2);
        int trackSlotA = tracks.Slot(trackUriA.AsSpan());
        EntityId trackIdA = tracks.Id[trackSlotA];

        Staging s1 = Staging.Rent();
        ref var rowA = ref s1.Tracks.Add();
        rowA.Id = trackIdA;
        rowA.Title = s1.AddText("Gid Album Track"u8);
        rowA.AlbumUri = albumGidId;                       // packed — a decoder holding raw gid bytes off the wire
        rowA.DurationMs = 1000;
        rowA.Known = (uint)TrackFields.Identity;
        rowA.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        Assert.True(Store.Read(scope, tracks, new[] { trackSlotA }, (uint)TrackFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal(albumGidSlot, tracks.Album[trackSlotA]);

        const string albumTextUri = "wavee:local:album:cross-ref-text-20260915";
        int albumTextSlot = albums.Slot(albumTextUri.AsSpan());
        EntityId albumTextId = albums.Id[albumTextSlot];
        Assert.Equal(EntityForm.Text, albumTextId.Form);

        string trackUriB = GidUri("track", 3);
        int trackSlotB = tracks.Slot(trackUriB.AsSpan());
        EntityId trackIdB = tracks.Id[trackSlotB];

        Staging s2 = Staging.Rent();
        ref var rowB = ref s2.Tracks.Add();
        rowB.Id = trackIdB;
        rowB.Title = s2.AddText("Text Album Track"u8);
        rowB.AlbumUri = s2.AddText(Encoding.UTF8.GetBytes(albumTextUri));   // arena — a decoder holding a uri string
        rowB.DurationMs = 2000;
        rowB.Known = (uint)TrackFields.Identity;
        rowB.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s2));
        Store.Flush();

        Assert.True(Store.Read(scope, tracks, new[] { trackSlotB }, (uint)TrackFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal(albumTextSlot, tracks.Album[trackSlotB]);
    }

    [Fact]
    public void Show_ddl_gid_round_trip_and_thin_never_blanks_identity()
    {
        Store.Register(new ShowShape());
        Scope scope = Boot();
        ShowTable shows = scope.Shows;

        string ddl = Store.Ddl();
        Assert.Contains("CREATE TABLE IF NOT EXISTS show(", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_show_title ON show(scope_id, title COLLATE NOCASE);", ddl);

        string uri = GidUri("show", 1);
        int slot = shows.Slot(uri.AsSpan());
        EntityId id = shows.Id[slot];
        Assert.Equal(EntityForm.Gid, id.Form);

        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Shows.Add();
        row1.Id = id;
        row1.Title = s1.AddText("Alpha"u8);
        row1.Image = s1.AddText("img"u8);
        row1.Publisher = s1.AddText("Pub Co"u8);
        row1.Description = s1.AddText("First about"u8);
        row1.Known = (uint)(ShowFields.Identity | ShowFields.About);
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        Assert.True(Store.Read(scope, shows, new[] { slot }, (uint)ShowFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha", Entities.Strings.Resolve(shows.Title[slot]));
        Assert.Equal("First about", Entities.Strings.Resolve(shows.Description[slot]));

        // A thin About-only re-answer must not blank the title. Force a genuinely COLD re-read (a fresh, Known=0
        // slot) so the assertion is about what the FILE holds, not about memory's own authority protection.
        Staging s2 = Staging.Rent();
        ref var row2 = ref s2.Shows.Add();
        row2.Id = id;
        row2.Description = s2.AddText("Second about"u8);
        row2.Known = (uint)ShowFields.About;
        row2.Authority = Authority.Thin;
        Assert.True(Store.WriteBehind(s2));
        Store.Flush();

        shows.FreeSlot(slot);
        int slot2 = shows.Slot(uri.AsSpan());
        Assert.True(Store.Read(scope, shows, new[] { slot2 }, (uint)ShowFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha", Entities.Strings.Resolve(shows.Title[slot2]));
        Assert.Equal("Second about", Entities.Strings.Resolve(shows.Description[slot2]));
    }

    [Fact]
    public void User_ddl_and_text_form_round_trip_with_an_independent_social_authority()
    {
        Store.Register(new UserShape());
        Scope scope = Boot();
        UserTable users = scope.Users;

        string ddl = Store.Ddl();
        Assert.Contains("CREATE TABLE IF NOT EXISTS user(", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_user_title ON user(scope_id, name COLLATE NOCASE);", ddl);

        const string uri = "spotify:user:store-user-round-trip-20260915";
        int slot = users.Slot(uri.AsSpan());
        EntityId id = users.Id[slot];
        Assert.Equal(EntityForm.Text, id.Form);      // a username is never a gid (User.cs's own header)

        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Users.Add();
        // TEXT-FORM identity must be staged as ARENA TEXT, not as a packed EntityId. A text-form EntityId's
        // payload is an index into the PROCESS-LOCAL interner, and the store thread may never touch the
        // interner — so `RowWriter.Emit(in StagedId)` can only take the packed branch for a GID, and a
        // text-form id handed to it is counted by `Store.BadKey` and DROPPED. A real decoder always has the
        // uri's UTF-8 bytes in hand and stages `s.AddText(...)`, which is what this now does.
        row1.Id = s1.AddText(System.Text.Encoding.UTF8.GetBytes(uri));
        row1.Name = s1.AddText("Alpha"u8);
        row1.Image = s1.AddText("img"u8);
        row1.Followers = 10;
        row1.Following = 5;
        row1.Known = (uint)(UserFields.Identity | UserFields.Social);
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        Assert.True(Store.Read(scope, users, new[] { slot }, (uint)UserFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha", Entities.Strings.Resolve(users.Name[slot]));
        Assert.Equal(10, users.Followers[slot]);
        Assert.Equal(id, EntityId.Parse(uri.AsSpan()));
        Assert.Equal(slot, users.Slot(uri.AsSpan()));

        // A thin, Social-only answer must not blank the name.
        Staging s2 = Staging.Rent();
        ref var row2 = ref s2.Users.Add();
        row2.Id = s2.AddText(System.Text.Encoding.UTF8.GetBytes(uri));
        row2.Followers = 20;
        row2.Following = 6;
        row2.Known = (uint)UserFields.Social;
        row2.Authority = Authority.Thin;
        Assert.True(Store.WriteBehind(s2));
        Store.Flush();

        users.FreeSlot(slot);
        int slot2 = users.Slot(uri.AsSpan());
        Assert.True(Store.Read(scope, users, new[] { slot2 }, (uint)UserFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha", Entities.Strings.Resolve(users.Name[slot2]));
        Assert.Equal(20, users.Followers[slot2]);
    }

    [Fact]
    public void Episode_ddl_gid_round_trip_and_progress_authority_is_independent()
    {
        Store.Register(new EpisodeShape());
        Scope scope = Boot();
        EpisodeTable episodes = scope.Episodes;
        ShowTable shows = scope.Shows;

        string ddl = Store.Ddl();
        Assert.Contains("CREATE TABLE IF NOT EXISTS episode(", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_episode_title ON episode(scope_id, title COLLATE NOCASE);", ddl);

        string showUri = GidUri("show", 4);
        int showSlot = shows.Slot(showUri.AsSpan());
        EntityId showId = shows.Id[showSlot];

        string uri = GidUri("episode", 5);
        int slot = episodes.Slot(uri.AsSpan());
        EntityId id = episodes.Id[slot];
        Assert.Equal(EntityForm.Gid, id.Form);

        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Episodes.Add();
        row1.Id = id;
        row1.Title = s1.AddText("Episode One"u8);
        row1.Image = s1.AddText("img"u8);
        row1.ShowUri = showId;
        row1.DurationMs = 60_000;
        row1.PublishedAt = 100;
        row1.ProgressMs = 1_000;
        row1.Known = (uint)(EpisodeFields.Identity | EpisodeFields.Progress);
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        Assert.True(Store.Read(scope, episodes, new[] { slot }, (uint)EpisodeFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Episode One", Entities.Strings.Resolve(episodes.Title[slot]));
        Assert.Equal(showSlot, episodes.Show[slot]);
        Assert.Equal(1_000, episodes.ProgressMs[slot]);

        // Progress rides its OWN authority column precisely so a stale server position can never rewind what this
        // device just played to — a thin Progress-only re-answer must not blank the title either way.
        Staging s2 = Staging.Rent();
        ref var row2 = ref s2.Episodes.Add();
        row2.Id = id;
        row2.ProgressMs = 30_000;
        row2.Known = (uint)EpisodeFields.Progress;
        row2.Authority = Authority.Thin;
        Assert.True(Store.WriteBehind(s2));
        Store.Flush();

        episodes.FreeSlot(slot);
        int slot2 = episodes.Slot(uri.AsSpan());
        Assert.True(Store.Read(scope, episodes, new[] { slot2 }, (uint)EpisodeFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Episode One", Entities.Strings.Resolve(episodes.Title[slot2]));
        Assert.Equal(30_000, episodes.ProgressMs[slot2]);
    }

    [Fact]
    public void Track_ddl_and_a_thin_playcount_only_answer_never_blanks_identity()
    {
        Store.Register(new TrackShape());
        Scope scope = Boot();
        TrackTable tracks = scope.Tracks;

        string ddl = Store.Ddl();
        Assert.Contains("CREATE TABLE IF NOT EXISTS track(", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_track_title ON track(scope_id, title COLLATE NOCASE);", ddl);

        string uri = GidUri("track", 6);
        int slot = tracks.Slot(uri.AsSpan());
        EntityId id = tracks.Id[slot];
        Assert.Equal(EntityForm.Gid, id.Form);

        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Tracks.Add();
        row1.Id = id;
        row1.Title = s1.AddText("Alpha"u8);
        row1.Image = s1.AddText("img"u8);
        row1.DurationMs = 191_000;
        row1.Isrc = s1.AddText("ISRC1"u8);
        row1.PlayCount = 3;
        row1.Known = (uint)(TrackFields.Identity | TrackFields.PlayCount | TrackFields.Isrc);
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        Assert.True(Store.Read(scope, tracks, new[] { slot }, (uint)TrackFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha", Entities.Strings.Resolve(tracks.Title[slot]));
        Assert.Equal(191_000, tracks.DurationMs[slot]);
        Assert.Equal(3u, tracks.PlayCount[slot]);

        // A thin PlayCount-only re-answer (a kind-185 batch) must not blank the title or the Isrc group.
        Staging s2 = Staging.Rent();
        ref var row2 = ref s2.Tracks.Add();
        row2.Id = id;
        row2.PlayCount = 99;
        row2.Known = (uint)TrackFields.PlayCount;
        row2.Authority = Authority.Thin;
        Assert.True(Store.WriteBehind(s2));
        Store.Flush();

        tracks.FreeSlot(slot);
        int slot2 = tracks.Slot(uri.AsSpan());
        Assert.True(Store.Read(scope, tracks, new[] { slot2 }, (uint)TrackFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha", Entities.Strings.Resolve(tracks.Title[slot2]));
        Assert.Equal(99u, tracks.PlayCount[slot2]);
        Assert.Equal("ISRC1", Entities.Strings.Resolve(tracks.Isrc[slot2]));
    }

    /// <summary>BUG C (2026-09-15, the persistence regression). A `TrackShape.Load` that restored the `Artists` bit
    /// inside `Identity` told the planner "never ask again" for a credit line and its click-target edge
    /// (`Edges.TrackArtists`) that this shape has no column or edge storage for — the row's "Artist · Album" line
    /// went blank forever on the SECOND launch, a cache hit showing LESS than a cache miss. The fix masks `Artists`
    /// out of what `Save` writes and `Load` restores (`TrackShape.PersistedIdentity`) so a cached track still
    /// re-asks TrackV4 exactly once after a restart. Title (a real column) must survive the very same disk answer
    /// that Artists (no column) must not.</summary>
    [Fact]
    public void Track_disk_load_never_restores_Artists_known_though_Title_survives()
    {
        Store.Register(new TrackShape());
        Scope scope = Boot();
        TrackTable tracks = scope.Tracks;

        string uri = GidUri("track", 11);
        int slot = tracks.Slot(uri.AsSpan());
        EntityId id = tracks.Id[slot];
        Assert.Equal(EntityForm.Gid, id.Form);

        // The live TrackV4 answer: one wire shape, all six Identity bits at once — Artists included, exactly as
        // ch 01 §7 describes it ("a TrackV4, a search hit and a playlist item all fill exactly it").
        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Tracks.Add();
        row1.Id = id;
        row1.Title = s1.AddText("Alpha"u8);
        row1.ArtistLine = s1.AddText("Alpha Artist"u8);
        row1.Image = s1.AddText("img"u8);
        row1.DurationMs = 191_000;
        row1.Known = (uint)TrackFields.Identity;
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        // Forget the in-memory row entirely and ask the disk for it back — the exact round trip the bug lives in.
        tracks.FreeSlot(slot);
        int slot2 = tracks.Slot(uri.AsSpan());
        Assert.True(Store.Read(scope, tracks, new[] { slot2 }, (uint)TrackFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        var loaded = new Track(slot2);
        Assert.True(loaded.Knows(TrackFields.Title));
        Assert.Equal("Alpha", loaded.Title);                     // the shape DOES persist Title …
        Assert.False(loaded.Knows(TrackFields.Artists));         // … but must never claim Artists: no column, no edge
    }

    /// <summary>The companion guard next to the mask above (Track.cs's commit, the handoff's ":512"): even with
    /// `Artists` never claimed known from disk, a disk-loaded batch's `ArtistLine` TEXT is unconditionally empty —
    /// the shape has no <c>artist_line</c> column at all — so the commit must not use that emptiness to actively
    /// BLANK a credit line a live answer already interned this session, the same way it already refused to blank
    /// Title or Image.</summary>
    [Fact]
    public void Track_disk_load_with_no_credit_line_never_blanks_a_live_ArtistLine()
    {
        Store.Register(new TrackShape());
        Scope scope = Boot();
        TrackTable tracks = scope.Tracks;

        string uri = GidUri("track", 12);
        int slot = tracks.Slot(uri.AsSpan());
        EntityId id = tracks.Id[slot];

        // Persist a bare row to disk first — no credit line, because the shape has no column for one.
        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Tracks.Add();
        row1.Id = id;
        row1.Title = s1.AddText("Alpha"u8);
        row1.Image = s1.AddText("img"u8);
        row1.DurationMs = 191_000;
        row1.Known = (uint)TrackFields.Identity;
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        // The LIVE answer lands this session (a fresh TrackV4) and interns a real credit line on the row.
        Staging s2 = Staging.Rent();
        ref var row2 = ref s2.Tracks.Add();
        row2.Id = id;
        row2.Title = s2.AddText("Alpha"u8);
        row2.ArtistLine = s2.AddText("Real Artist"u8);
        row2.Image = s2.AddText("img"u8);
        row2.DurationMs = 191_000;
        row2.Known = (uint)TrackFields.Identity;
        row2.Authority = Authority.Full;
        TestScope.CommitAndPublish(s2);
        Assert.Equal("Real Artist", Entities.Strings.Resolve(tracks.ArtistLine[slot]));

        // The disk answer for the SAME row lands on top of the live one (a race the store may win or lose) and
        // must not blank the credit line the live answer already set.
        Assert.True(Store.Read(scope, tracks, new[] { slot }, (uint)TrackFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        Assert.Equal("Real Artist", Entities.Strings.Resolve(tracks.ArtistLine[slot]));
        Assert.Equal("Alpha", Entities.Strings.Resolve(tracks.Title[slot]));
    }

    /// <summary>BUG C, the ALBUM variant (2026-09-15). Same shape as the track fact above: `AlbumV4` is the sole
    /// route registered for `AlbumFields.Identity` and it also closes `Relation.AlbumArtists` as a side effect —
    /// a relation nothing here persists. Before the fix, `AlbumShape.Load` restored the whole `Identity` group
    /// (Artists included) off a live answer that had genuinely closed the run, and `Fetch.NeedOf` never asked
    /// `AlbumV4` again: a cached album's billed-artist line went blank forever. The fix masks `Artists` out of
    /// `AlbumShape.PersistedFields` (`AlbumShape.PersistedIdentity`) the same way `TrackShape` does, and
    /// `CommitAlbums`'s `known &amp; AlbumFields.Identity` (not the bare constant) is what stops a disk-restored
    /// row from re-granting the whole group once one bit of it is missing — so `Knows(Identity)` must ALSO read
    /// false, not just `Knows(Artists)`: the group is not whole, and nothing may claim it is.</summary>
    [Fact]
    public void Album_disk_load_never_restores_Artists_known_though_Title_survives()
    {
        Store.Register(new AlbumShape());
        Scope scope = Boot();
        AlbumTable albums = scope.Albums;

        string uri = GidUri("album", 21);
        int slot = albums.Slot(uri.AsSpan());
        EntityId id = albums.Id[slot];
        Assert.Equal(EntityForm.Gid, id.Form);

        // The live AlbumV4 answer: full Identity (Artists included — it closed Relation.AlbumArtists as a side
        // effect, exactly as the decoder does; the edge itself is irrelevant to this fact, only the Known bit is).
        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Albums.Add();
        row1.Id = id;
        row1.Title = s1.AddText("Alpha"u8);
        row1.Image = s1.AddText("img"u8);
        row1.TrackCount = 10;
        row1.Kind = (byte)AlbumKind.Album;
        row1.Known = (uint)AlbumFields.Identity;
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        // Forget the in-memory row entirely and ask the disk for it back — the exact round trip the bug lives in.
        albums.FreeSlot(slot);
        int slot2 = albums.Slot(uri.AsSpan());
        Assert.True(Store.Read(scope, albums, new[] { slot2 }, (uint)AlbumFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        var loaded = new Album(slot2);
        Assert.True(loaded.Knows(AlbumFields.Title));
        Assert.Equal("Alpha", loaded.Title);                       // the shape DOES persist Title …
        Assert.False(loaded.Knows(AlbumFields.Artists));           // … but must never claim Artists: no edge on disk
        Assert.False(loaded.Knows(AlbumFields.Identity));          // … and therefore the WHOLE group is not known
    }

    [Fact]
    public void Album_ddl_round_trip_and_year_rides_with_a_release_only_answer()
    {
        Store.Register(new AlbumShape());
        Scope scope = Boot();
        AlbumTable albums = scope.Albums;

        string ddl = Store.Ddl();
        Assert.Contains("CREATE TABLE IF NOT EXISTS album(", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_album_title ON album(scope_id, title COLLATE NOCASE);", ddl);

        string uri = GidUri("album", 7);
        int slot = albums.Slot(uri.AsSpan());
        EntityId id = albums.Id[slot];
        Assert.Equal(EntityForm.Gid, id.Form);

        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Albums.Add();
        row1.Id = id;
        row1.Title = s1.AddText("Alpha"u8);
        row1.Image = s1.AddText("img"u8);
        row1.TrackCount = 10;
        row1.Kind = (byte)AlbumKind.Album;
        row1.Known = (uint)AlbumFields.Identity;
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        Assert.True(Store.Read(scope, albums, new[] { slot }, (uint)AlbumFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha", Entities.Strings.Resolve(albums.Title[slot]));
        Assert.Equal(0, (int)albums.Year[slot]);        // no Release answer has landed yet

        // A date-only answer (extension kind 183, ch 05 §7): Release known, Identity NOT — yet it carries the Year,
        // and it must land without blanking the title Identity already wrote.
        Staging s2 = Staging.Rent();
        ref var row2 = ref s2.Albums.Add();
        row2.Id = id;
        row2.Year = 2019;
        row2.ReleaseDateIso = s2.AddText("2019"u8);
        row2.Known = (uint)AlbumFields.Release;
        row2.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s2));
        Store.Flush();

        albums.FreeSlot(slot);
        int slot2 = albums.Slot(uri.AsSpan());
        Assert.True(Store.Read(scope, albums, new[] { slot2 }, (uint)AlbumFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha", Entities.Strings.Resolve(albums.Title[slot2]));   // survived the release-only answer
        Assert.Equal(2019, (int)albums.Year[slot2]);                          // …and the year rode with it
    }

    /// <summary>The cover accent rides with Identity (2026-09-16): an ARGB above <c>int.MaxValue</c> must survive the
    /// INTEGER column and come back as the same <c>uint</c>, so a cold page can tint before the palette grades.</summary>
    [Fact]
    public void Album_accent_round_trips_with_identity()
    {
        Store.Register(new AlbumShape());
        Scope scope = Boot();
        AlbumTable albums = scope.Albums;

        string uri = GidUri("album", 9);
        int slot = albums.Slot(uri.AsSpan());
        EntityId id = albums.Id[slot];

        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Albums.Add();
        row1.Id = id;
        row1.Title = s1.AddText("Tinted"u8);
        row1.Image = s1.AddText("img"u8);
        row1.TrackCount = 3;
        row1.Kind = (byte)AlbumKind.Album;
        row1.Accent = 0xFF8898A8u;
        row1.Known = (uint)AlbumFields.Identity;
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        albums.FreeSlot(slot);
        int slot2 = albums.Slot(uri.AsSpan());
        Assert.True(Store.Read(scope, albums, new[] { slot2 }, (uint)AlbumFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();

        Assert.Equal("Tinted", Entities.Strings.Resolve(albums.Title[slot2]));
        Assert.Equal(0xFF8898A8u, new Album(slot2).Accent);
    }

    [Fact]
    public void Artist_ddl_and_gid_round_trip()
    {
        Store.Register(new ArtistShape());
        Scope scope = Boot();
        ArtistTable artists = scope.Artists;

        string ddl = Store.Ddl();
        Assert.Contains("CREATE TABLE IF NOT EXISTS artist(", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_artist_title ON artist(scope_id, name COLLATE NOCASE);", ddl);

        string uri = GidUri("artist", 8);
        int slot = artists.Slot(uri.AsSpan());
        EntityId id = artists.Id[slot];
        Assert.Equal(EntityForm.Gid, id.Form);

        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Artists.Add();
        row1.Id = id;
        row1.Name = s1.AddText("Alpha Artist"u8);
        row1.Image = s1.AddText("img"u8);
        row1.Header = s1.AddText("header"u8);
        row1.HeaderAccent = 0xFF00FF;
        row1.Bio = s1.AddText("A biography"u8);
        row1.Monthly = 1_000;
        row1.Followers = 2_000;
        row1.WorldRank = 42;
        row1.Known = (uint)(ArtistFields.Identity | ArtistFields.Header | ArtistFields.Stats | ArtistFields.Bio);
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        Assert.True(Store.Read(scope, artists, new[] { slot }, (uint)ArtistFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha Artist", Entities.Strings.Resolve(artists.Name[slot]));
        Assert.Equal("A biography", Entities.Strings.Resolve(artists.Bio[slot]));
        Assert.Equal(1_000u, artists.Monthly[slot]);
        Assert.Equal((ushort)42, artists.WorldRank[slot]);
    }

    [Fact]
    public void Concert_ddl_and_text_form_round_trip()
    {
        Store.Register(new ConcertShape());
        Scope scope = Boot();
        ConcertTable concerts = scope.Concerts;

        string ddl = Store.Ddl();
        Assert.Contains("CREATE TABLE IF NOT EXISTS concert(", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_concert_title ON concert(scope_id, title COLLATE NOCASE);", ddl);

        const string uri = "spotify:concert:store-concert-round-trip-20260915";
        int slot = concerts.Slot(uri.AsSpan());
        EntityId id = concerts.Id[slot];
        Assert.Equal(EntityForm.Text, id.Form);          // concert is not one of the six gid kinds

        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Concerts.Add();
        // TEXT-FORM identity must be staged as ARENA TEXT, not as a packed EntityId. A text-form EntityId's
        // payload is an index into the PROCESS-LOCAL interner, and the store thread may never touch the
        // interner — so `RowWriter.Emit(in StagedId)` can only take the packed branch for a GID, and a
        // text-form id handed to it is counted by `Store.BadKey` and DROPPED. A real decoder always has the
        // uri's UTF-8 bytes in hand and stages `s.AddText(...)`, which is what this now does.
        row1.Id = s1.AddText(System.Text.Encoding.UTF8.GetBytes(uri));
        row1.Title = s1.AddText("Alpha Live"u8);
        row1.Venue = s1.AddText("The Venue"u8);
        row1.City = s1.AddText("Metropolis"u8);
        row1.Date = 1_700_000_000_000;
        row1.OffsetMinutes = 120;
        row1.Image = s1.AddText("poster"u8);
        row1.Accent = 0xABCDEF;
        row1.Lat = 51.5f;
        row1.Lon = -0.12f;
        row1.Known = (uint)(ConcertFields.Identity | ConcertFields.Art | ConcertFields.Coords);
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        Assert.True(Store.Read(scope, concerts, new[] { slot }, (uint)ConcertFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha Live", Entities.Strings.Resolve(concerts.Title[slot]));
        Assert.Equal("The Venue", Entities.Strings.Resolve(concerts.Venue[slot]));
        Assert.Equal(1_700_000_000_000, concerts.Date[slot]);
        Assert.Equal((short)120, concerts.OffsetMinutes[slot]);
        Assert.Equal(51.5f, concerts.Lat[slot]);
        Assert.Equal(-0.12f, concerts.Lon[slot]);
    }

    [Fact]
    public void Playlist_ddl_round_trip_and_owner_uri_cross_reference()
    {
        Store.Register(new PlaylistShape());
        Scope scope = Boot();
        PlaylistTable playlists = scope.Playlists;
        UserTable users = scope.Users;

        string ddl = Store.Ddl();
        Assert.Contains("CREATE TABLE IF NOT EXISTS playlist(", ddl);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_playlist_title ON playlist(scope_id, title COLLATE NOCASE);", ddl);

        const string ownerUri = "spotify:user:store-playlist-owner-20260915";
        int ownerSlot = users.Slot(ownerUri.AsSpan());

        string uri = GidUri("playlist", 9);
        int slot = playlists.Slot(uri.AsSpan());
        EntityId id = playlists.Id[slot];
        Assert.Equal(EntityForm.Gid, id.Form);

        Staging s1 = Staging.Rent();
        ref var row1 = ref s1.Playlists.Add();
        row1.Id = id;
        row1.Title = s1.AddText("Alpha Mix"u8);
        row1.Description = s1.AddText("desc"u8);
        row1.Image = s1.AddText("img"u8);
        row1.ShareUrl = s1.AddText("https://open.spotify.com/x"u8);
        row1.TrackCount = 42;
        row1.OwnerUri = s1.AddText(Encoding.UTF8.GetBytes(ownerUri));   // arena — a decoder holding a uri string
        row1.Caps = (byte)PlaylistCaps.CanView;
        row1.Accent = 0x112233;
        row1.Saves = 7;
        row1.Known = (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities | PlaylistFields.Accent | PlaylistFields.Saves);
        row1.Authority = Authority.Full;
        Assert.True(Store.WriteBehind(s1));
        Store.Flush();

        Assert.True(Store.Read(scope, playlists, new[] { slot }, (uint)PlaylistFields.All, FetchPriority.Visible));
        Store.Flush();
        DrainPosts();
        Assert.Equal("Alpha Mix", Entities.Strings.Resolve(playlists.Title[slot]));
        Assert.Equal(42, playlists.TrackCount[slot]);
        Assert.Equal(ownerSlot, playlists.Owner[slot]);
        Assert.Equal(7, playlists.Saves[slot]);
    }
}
