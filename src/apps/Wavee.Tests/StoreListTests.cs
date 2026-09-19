// ── Wavee.Tests/StoreListTests.cs — lists on disk (wave D2): the round trip, isolation, the gate, one transaction ────
//
// docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §3.2 / §6. A playlist's settled membership and
// the account's rootlist are persisted as TEXT rows beside the revision they are true at (Store.Lists.cs), written in
// one transaction and read back through the very staging + commit a wire answer lands through. These facts drive the
// real store over a temp file — the thing under test IS the file — and every restart is a real one: the store is shut
// down and a brand-new table set reads the file, so a row, a user or a folder name that comes back came from the disk.

using System.Collections.Concurrent;
using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Wire-shaped answers for the list facts, staged exactly as the decoders stage them: a PlaylistRead-shaped
/// full read (<c>Spotify.Decode.PlaylistRevision</c> + <c>PlaylistFormatAttributes</c>: a header row carrying the
/// revision, a <see cref="Relation.PlaylistTracks"/> run of text identities with the item id as hex and the adder as a
/// user uri) and a rootlist marker stream (<c>Spotify.Decode.Rootlist</c>). Shared with <c>EdgeDoorTests</c>.</summary>
static class ListAnswers
{
    /// <summary>Well-formed heads: a counter, a comma, a 20-byte hash as 40 lowercase hex characters.</summary>
    public const string RevisionA = "7,0123456789abcdef0123456789abcdef01234567";
    public const string RevisionB = "8,89abcdef0123456789abcdef0123456789abcdef";
    public const string RevisionC = "135,fedcba9876543210fedcba9876543210fedcba98";

    /// <summary>A real-shaped catalog uri (<c>spotify:&lt;kind&gt;:</c> + 22 base62 characters) — the GID form.</summary>
    public static string GidUri(string kind, int seed)
    {
        Span<char> buf = stackalloc char[Base62.GidChars];
        Base62.Encode(new UInt128((ulong)seed * 0x9E37_79B9_7F4A_7C15UL + 23, (ulong)seed * 0xC2B2_AE3D_27D4_EB4FUL + 9), buf);
        return "spotify:" + kind + ":" + new string(buf);
    }

    public static string PlaylistUri(int seed) => GidUri("playlist", seed);
    public static string TrackUri(int seed) => GidUri("track", seed);

    /// <summary>One playlist member.</summary>
    public static ListRow Member(string uri, string itemId, int addedAt = 0, string? addedBy = null,
                                 byte chartStatus = 0, ushort chartPos = 0, ushort chartPrev = 0, byte flags = 0)
        => new(uri, itemId, addedAt, addedBy, chartStatus, chartPos, chartPrev, flags, RootlistKind.Item, 0, 0, null, null);

    /// <summary>A rootlist folder's start marker.</summary>
    public static ListRow FolderStart(ushort position, byte depth, string folderId, string name, int addedAt = 0)
        => new("", null, addedAt, null, 0, 0, 0, 0, RootlistKind.FolderStart, depth, position, folderId, name);

    /// <summary>A rootlist playlist row.</summary>
    public static ListRow Item(ushort position, byte depth, string playlistUri, int addedAt = 0)
        => new(playlistUri, null, addedAt, null, 0, 0, 0, 0, RootlistKind.Item, depth, position, null, null);

    /// <summary>A rootlist folder's end marker.</summary>
    public static ListRow FolderEnd(ushort position, byte depth, string folderId)
        => new("", null, 0, null, 0, 0, 0, 0, RootlistKind.FolderEnd, depth, position, folderId, null);

    /// <summary>A PlaylistRead-shaped answer: the header (the groups <paramref name="headerKnown"/> names, the member
    /// count, and <paramref name="revision"/> when given) and the membership — a WHOLE Complete rewrite, or, when
    /// <paramref name="total"/> is larger than the members, the first page of that many.</summary>
    public static Staging FullRead(string playlistUri, string? revision, ListRow[] members,
                                   uint headerKnown = (uint)(PlaylistFields.Identity | PlaylistFields.TrackCount),
                                   int total = -1)
    {
        Staging s = Staging.Rent();
        StagedId parent = Text(s, playlistUri);
        ref StagedPlaylist header = ref s.Playlists.RowFor(parent, Authority.Full, headerKnown);
        header.Title = s.AddText("A list"u8);
        header.TrackCount = members.Length;
        if (revision is not null) header.Revision = s.AddText(Encoding.UTF8.GetBytes(revision));

        var run = s.Run(Relation.PlaylistTracks);
        for (int i = 0; i < members.Length; i++)
        {
            ListRow m = members[i];
            ref StagedEdge edge = ref run.Add(Text(s, m.Uri));
            edge.Text = s.AddText(Encoding.UTF8.GetBytes(m.ItemId ?? ""));
            edge.At = m.AddedAt;
            if (m.AddedBy is not null) edge.Aux = Text(s, m.AddedBy);
            edge.B1 = m.ChartStatus;
            edge.U0 = m.ChartPos;
            edge.U1 = m.ChartPrev;
            edge.B0 = m.Flags;
        }
        if (total > members.Length) run.Page(in parent, 0, total);
        else run.EndEvenIfEmpty(in parent, members.Length);
        return s;
    }

    /// <summary>A rootlist-shaped answer for the account <paramref name="meUri"/>: the marker stream, in wire order, and
    /// its revision when given.</summary>
    public static Staging Rootlist(string meUri, string? revision, params ListRow[] rows)
    {
        Staging s = Staging.Rent();
        int start = s.RootlistRows.Count;
        for (int i = 0; i < rows.Length; i++)
        {
            ListRow r = rows[i];
            ref StagedRootlistRow row = ref s.RootlistRows.Add();
            row.Kind = r.Kind;
            row.Position = r.WirePos;
            row.Depth = r.Depth;
            row.AddedAt = r.AddedAt;
            if (r.Uri.Length > 0) row.Target = Text(s, r.Uri);
            if (r.FolderId is not null) row.FolderId = s.AddText(Encoding.UTF8.GetBytes(r.FolderId));
            if (r.FolderName is not null) row.Name = s.AddText(Encoding.UTF8.GetBytes(r.FolderName));
        }
        ref StagedRootlist list = ref s.Rootlists.Add();
        list.Parent = Text(s, meUri);
        list.Start = start;
        list.Length = rows.Length;
        if (revision is not null) list.Revision = s.AddText(Encoding.UTF8.GetBytes(revision));
        return s;
    }

    static StagedId Text(Staging s, string uri) => s.AddText(Encoding.UTF8.GetBytes(uri));
}

[Collection(EntitiesCollection.Name)]
public class StoreListTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-v3-store-list-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public StoreListTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Post = a => _posted.Enqueue(a);      // the UI drain, run by the test thread where it belongs
        Store.Register(new PlaylistShape());        // the list's transaction rewrites this kind's count column
        Store.Use(_dbPath);
        Entities.Now = 0;
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    /// <summary>A live-shaped account scope: the account row is <c>spotify:user:&lt;name&gt;</c>.</summary>
    static CatalogScope Account(string name) => new("spotify", name, "en-US", "US", 0, true);

    /// <summary>Boot (or switch to) a scope, let the store resolve its scope id, and land whatever its warm posted.</summary>
    Scope Boot(CatalogScope key)
    {
        Entities.Boot(key);
        Store.Flush();
        DrainPosts();
        return Entities.Current;
    }

    /// <summary>A REAL restart: the store closes, the same file opens behind a brand-new, empty table set.</summary>
    Scope Restart(CatalogScope key)
    {
        Store.Shutdown();
        Store.Use(_dbPath);
        return Boot(key);
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    /// <summary>What every wire answer does: commit on the UI thread, hand the batch to the write-behind, and (here)
    /// wait for the file.</summary>
    static void Land(Staging answer)
    {
        Entities.Commit(answer);
        Assert.True(Store.WriteBehind(answer));
        Store.Flush();
    }

    /// <summary>The disk door, drained: what the continuation was told (null = it never ran).</summary>
    bool? ReadBack(Scope scope, EdgeRelation relation, EntityId parent)
    {
        bool? found = null;
        Assert.True(Store.ReadList(scope, relation, parent, landed => found = landed));
        Store.Flush();
        DrainPosts();
        return found;
    }

    static EntityId PlaylistId(Scope scope, string uri) => scope.Playlists.Id[scope.Playlists.Slot(uri.AsSpan())];

    /// <summary>A second, unpooled connection to the file — to look at it, and to plant what no public door writes.</summary>
    Microsoft.Data.Sqlite.SqliteConnection Direct()
    {
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false };
        var c = new Microsoft.Data.Sqlite.SqliteConnection(cs.ToString());
        c.Open();
        return c;
    }

    void Sql(string sql)
    {
        using var c = Direct();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    object? SqlValue(string sql)
    {
        using var c = Direct();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    long Count(string sql) => SqlValue(sql) is long n ? n : -1;

    // ── the round trip ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE PRIZE: a settled membership and its revision come back into a scope that has never seen any of it —
    /// Complete, in order, with every membership fact; the adder resolves to the SAME user uri through a fresh user row;
    /// the item id to the same hex; the revision is held (so the next ask is a <c>/diff</c>) and the count is known.
    /// A gid track, a gid episode and a text-form local file cover both key spellings.</summary>
    [Fact]
    public void A_settled_playlist_comes_back_with_its_rows_revision_and_count_after_a_restart()
    {
        CatalogScope key = Account("round-trip");
        Boot(key);
        string playlist = ListAnswers.PlaylistUri(1);
        ListRow[] members =
        [
            ListAnswers.Member(ListAnswers.TrackUri(11), "0a0b0c0d", 1_700_000_000, "spotify:user:alice", chartStatus: 2, chartPos: 3, chartPrev: 5),
            ListAnswers.Member(ListAnswers.GidUri("episode", 12), "1a1b1c1d", 1_700_000_100, "spotify:user:bob"),
            ListAnswers.Member(Playlist.LocalFileUri(@"C:\music\a.mp3"), "2a2b2c2d"),
        ];
        Land(ListAnswers.FullRead(playlist, ListAnswers.RevisionA, members));

        Scope scope = Restart(key);
        int slot = scope.Playlists.Slot(playlist.AsSpan());
        Assert.Equal(EdgeState.Unknown, scope.Edges.PlaylistTracks.State(slot));

        Assert.True(ReadBack(scope, EdgeRelation.PlaylistTracks, scope.Playlists.Id[slot]));

        var p = new Playlist(slot);
        Assert.Equal(EdgeState.Complete, p.MembershipState);
        Assert.Equal(3, p.MembershipTotal);
        Assert.Equal(3, p.TrackSlots.Length);
        for (int i = 0; i < members.Length; i++)
        {
            int target = p.TrackSlots[i];
            PlaylistTrackEdge edge = p.TrackEdges[i];
            // The episode member (index 1) resolves into scope.Episodes, never scope.Tracks (plan §3.1, ledger row
            // 12) — its slot in Kind's own table is what the round trip has to prove, not just that a slot exists.
            EntityId id = edge.Kind == PlaylistItemKind.Episode ? scope.Episodes.Id[target] : scope.Tracks.Id[target];
            Assert.Equal(members[i].Uri, id.Text);
            Assert.Equal(members[i].ItemId, Entities.Strings.Resolve(edge.ItemId));
            Assert.Equal(members[i].AddedAt, edge.AddedAt);
            if (members[i].AddedBy is null) Assert.Equal(Table.None, edge.AddedBy);
            else Assert.Equal(members[i].AddedBy, scope.Users.Id[edge.AddedBy].Text);
        }
        Assert.Equal(PlaylistItemKind.Track, p.TrackEdges[0].Kind);
        Assert.Equal(PlaylistItemKind.Episode, p.TrackEdges[1].Kind);
        Assert.Equal(PlaylistItemKind.Track, p.TrackEdges[2].Kind);
        Assert.True(p.IsMixed);                                                  // one episode among three rows
        Assert.Equal(2, p.TrackEdges[0].ChartStatus);
        Assert.Equal(3, p.TrackEdges[0].ChartPos);
        Assert.Equal(5, p.TrackEdges[0].ChartPrev);
        Assert.Equal(ListAnswers.RevisionA, Entities.Strings.Resolve(p.RevisionId));
        Assert.Equal(3, p.TrackCount);
        Assert.True(p.Knows(PlaylistFields.TrackCount));
        Assert.True(p.HasAddedByColumn);                                        // the fold ran: two distinct adders
    }

    /// <summary>The rootlist is warmed with the library at boot — nobody asks for it — markers, depths, WIRE positions
    /// (a skipped non-playlist item makes position 4 follow 2), folder ids and names re-interned, and the revision the
    /// login sync's first ask will diff from.</summary>
    [Fact]
    public void The_rootlist_warms_at_the_next_boot_with_its_markers_and_revision()
    {
        CatalogScope key = Account("rootlist-warm");
        Scope first = Boot(key);
        string me = first.Users.Id[first.MeSlot].Text;
        string a = ListAnswers.PlaylistUri(21), b = ListAnswers.PlaylistUri(22);
        Land(ListAnswers.Rootlist(me, ListAnswers.RevisionC,
            ListAnswers.FolderStart(0, 0, "edb339e10aebcf38", "Work out", addedAt: 100),
            ListAnswers.Item(1, 1, a, addedAt: 200),
            ListAnswers.FolderEnd(2, 0, "edb339e10aebcf38"),
            ListAnswers.Item(4, 0, b)));

        Scope scope = Restart(key);
        int parent = scope.MeSlot;
        EdgeTable<RootlistEdge> rootlist = scope.Edges.Rootlist;
        Assert.Equal(EdgeState.Complete, rootlist.State(parent));
        Assert.Equal(4, rootlist.Count(parent));

        RootlistEdge start = rootlist.Payload(parent)[0];
        Assert.Equal((byte)RootlistKind.FolderStart, start.Kind);
        Assert.Equal("Work out", Entities.Strings.Resolve(start.FolderName));
        Assert.Equal("edb339e10aebcf38", Entities.Strings.Resolve(start.FolderId));
        Assert.Equal(100, start.AddedAt);
        Assert.Equal(Table.None, rootlist.Targets(parent)[0]);

        RootlistEdge inside = rootlist.Payload(parent)[1];
        Assert.Equal((byte)RootlistKind.Item, inside.Kind);
        Assert.Equal(1, inside.Depth);
        Assert.Equal(200, inside.AddedAt);
        Assert.Equal(a, scope.Playlists.Id[rootlist.Targets(parent)[1]].Text);

        RootlistEdge end = rootlist.Payload(parent)[2];
        Assert.Equal((byte)RootlistKind.FolderEnd, end.Kind);
        Assert.Equal("edb339e10aebcf38", Entities.Strings.Resolve(end.FolderId));

        Assert.Equal((ushort)4, rootlist.Payload(parent)[3].Position);
        Assert.Equal(b, scope.Playlists.Id[rootlist.Targets(parent)[3]].Text);
        Assert.Equal(ListAnswers.RevisionC, Entities.Strings.Resolve(scope.Edges.RootlistRevision(parent)));
    }

    /// <summary>C7 on disk: a list belongs to the account scope that wrote it. Another account on the same file reads a
    /// miss — and its continuation is told so, which is what sends its ask to the network — and the owner reads it.</summary>
    [Fact]
    public void A_list_belongs_to_the_scope_that_wrote_it()
    {
        CatalogScope owner = Account("iso-owner"), stranger = Account("iso-stranger");
        Boot(owner);
        string playlist = ListAnswers.PlaylistUri(31);
        Land(ListAnswers.FullRead(playlist, ListAnswers.RevisionA, [ListAnswers.Member(ListAnswers.TrackUri(311), "0311")]));

        Scope other = Boot(stranger);
        Assert.False(ReadBack(other, EdgeRelation.PlaylistTracks, PlaylistId(other, playlist)));
        Assert.Equal(EdgeState.Unknown, other.Edges.PlaylistTracks.State(other.Playlists.Slot(playlist.AsSpan())));

        Scope back = Boot(owner);
        Assert.True(ReadBack(back, EdgeRelation.PlaylistTracks, PlaylistId(back, playlist)));
        Assert.Equal(EdgeState.Complete, back.Edges.PlaylistTracks.State(back.Playlists.Slot(playlist.AsSpan())));
    }

    // ── the gate ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the gate refuses never reaches the file: an answer that carried no revision, one whose "revision"
    /// is a uri (0.2.x's poisoned shape), a truncated hash, and a first page of a longer list. Each lands in memory as
    /// usual; none writes a head or a row.</summary>
    [Fact]
    public void An_answer_the_gate_refuses_writes_nothing()
    {
        Boot(Account("gate"));
        Land(ListAnswers.FullRead(ListAnswers.PlaylistUri(41), revision: null, [ListAnswers.Member(ListAnswers.TrackUri(411), "0411")]));
        Land(ListAnswers.FullRead(ListAnswers.PlaylistUri(42), "spotify:user:gate:rootlist", [ListAnswers.Member(ListAnswers.TrackUri(421), "0421")]));
        Land(ListAnswers.FullRead(ListAnswers.PlaylistUri(43), "7,0123", [ListAnswers.Member(ListAnswers.TrackUri(431), "0431")]));
        Land(ListAnswers.FullRead(ListAnswers.PlaylistUri(44), ListAnswers.RevisionA, [ListAnswers.Member(ListAnswers.TrackUri(441), "0441")], total: 5));

        Assert.Equal(0L, Count("SELECT count(*) FROM list_head;"));
        Assert.Equal(0L, Count("SELECT count(*) FROM list_item;"));
    }

    /// <summary>Only SETTLED membership is written: while a removal is still unconfirmed the public door refuses, and the
    /// moment it settles (here: rejected, so the row stays) the same list is written.</summary>
    [Fact]
    public void An_optimistic_row_keeps_the_list_off_disk_until_it_settles()
    {
        Scope scope = Boot(Account("pending"));
        string playlist = ListAnswers.PlaylistUri(51);
        Staging answer = ListAnswers.FullRead(playlist, ListAnswers.RevisionA,
            [ListAnswers.Member(ListAnswers.TrackUri(511), "0511"), ListAnswers.Member(ListAnswers.TrackUri(512), "0512")]);
        Entities.Commit(answer);
        Staging.Return(answer);                                                 // memory only: this fact drives the door
        int slot = scope.Playlists.Slot(playlist.AsSpan());
        int track = scope.Tracks.Slot(ListAnswers.TrackUri(512).AsSpan());
        Assert.True(scope.Edges.PlaylistTracks.MarkRemove(slot, track));

        Assert.False(Store.SaveList(scope, EdgeRelation.PlaylistTracks, slot, ListAnswers.RevisionA));
        Store.Flush();
        Assert.Equal(0L, Count("SELECT count(*) FROM list_head;"));

        Assert.True(scope.Edges.PlaylistTracks.Settle(slot, track, ok: false));
        Assert.True(Store.SaveList(scope, EdgeRelation.PlaylistTracks, slot, ListAnswers.RevisionA));
        Store.Flush();
        Assert.Equal(1L, Count("SELECT count(*) FROM list_head;"));
        Assert.Equal(2L, Count("SELECT count(*) FROM list_item;"));
    }

    // ── the transaction ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A second answer for the same list replaces it whole — rows, order, revision, total — never merges.</summary>
    [Fact]
    public void A_second_answer_replaces_the_list_whole()
    {
        Boot(Account("replace"));
        string playlist = ListAnswers.PlaylistUri(61);
        Land(ListAnswers.FullRead(playlist, ListAnswers.RevisionA,
            [ListAnswers.Member(ListAnswers.TrackUri(611), "0611"), ListAnswers.Member(ListAnswers.TrackUri(612), "0612"),
             ListAnswers.Member(ListAnswers.TrackUri(613), "0613")]));
        Land(ListAnswers.FullRead(playlist, ListAnswers.RevisionB,
            [ListAnswers.Member(ListAnswers.TrackUri(614), "0614"), ListAnswers.Member(ListAnswers.TrackUri(611), "0611")]));

        Assert.Equal(1L, Count("SELECT count(*) FROM list_head;"));
        Assert.Equal(2L, Count("SELECT count(*) FROM list_item;"));
        Assert.Equal(2L, Count("SELECT total FROM list_head;"));
        Assert.Equal(ListAnswers.RevisionB, SqlValue("SELECT revision FROM list_head;") as string);
        Assert.Equal(ListAnswers.TrackUri(614), SqlValue("SELECT uri FROM list_item WHERE position=0;") as string);
        Assert.Equal(ListAnswers.TrackUri(611), SqlValue("SELECT uri FROM list_item WHERE position=1;") as string);
    }

    /// <summary>Rows + revision + total in ONE transaction: a save that fails at its LAST statement (a trigger planted
    /// to refuse the second head, after the save's own delete and inserts already ran) leaves the previous head AND the
    /// previous rows standing, and says so as a fault — never a new revision over old rows, never new rows under an old
    /// revision.</summary>
    [Fact]
    public void An_aborted_save_leaves_the_previous_head_and_rows_standing()
    {
        Boot(Account("abort"));
        string playlist = ListAnswers.PlaylistUri(81);
        Land(ListAnswers.FullRead(playlist, ListAnswers.RevisionA,
            [ListAnswers.Member(ListAnswers.TrackUri(811), "0811"), ListAnswers.Member(ListAnswers.TrackUri(812), "0812"),
             ListAnswers.Member(ListAnswers.TrackUri(813), "0813")]));
        Sql("CREATE TRIGGER refuse_two BEFORE INSERT ON list_head WHEN NEW.total = 2 BEGIN SELECT RAISE(ABORT, 'refused'); END;");
        int faults = Store.Stats.Faults;

        Land(ListAnswers.FullRead(playlist, ListAnswers.RevisionB,
            [ListAnswers.Member(ListAnswers.TrackUri(814), "0814"), ListAnswers.Member(ListAnswers.TrackUri(815), "0815")]));

        Assert.Equal(faults + 1, Store.Stats.Faults);
        Assert.Equal(ListAnswers.RevisionA, SqlValue("SELECT revision FROM list_head;") as string);
        Assert.Equal(3L, Count("SELECT total FROM list_head;"));
        Assert.Equal(3L, Count("SELECT count(*) FROM list_item;"));
        Assert.Equal(ListAnswers.TrackUri(811), SqlValue("SELECT uri FROM list_item WHERE position=0;") as string);
    }

    /// <summary>Settings ▸ Storage ▸ "Clear metadata": lists are cache, the rootlist's included — its revision rides its
    /// own head, so both go together and nothing is left inconsistent.</summary>
    [Fact]
    public void Clearing_metadata_drops_every_list_the_rootlist_with_them()
    {
        Scope scope = Boot(Account("clear"));
        string me = scope.Users.Id[scope.MeSlot].Text;
        string playlist = ListAnswers.PlaylistUri(71);
        Land(ListAnswers.FullRead(playlist, ListAnswers.RevisionA, [ListAnswers.Member(ListAnswers.TrackUri(711), "0711")]));
        Land(ListAnswers.Rootlist(me, ListAnswers.RevisionC, ListAnswers.Item(0, 0, playlist)));
        Assert.Equal(2L, Count("SELECT count(*) FROM list_head;"));
        Assert.Equal(2L, Count("SELECT count(*) FROM list_item;"));

        Store.DropCatalog();

        Assert.Equal(0L, Count("SELECT count(*) FROM list_head;"));
        Assert.Equal(0L, Count("SELECT count(*) FROM list_item;"));
    }
    [Fact]
    public void Show_membership_and_canonical_item_ids_survive_a_real_store_restart()
    {
        CatalogScope key = Account("show-round-trip");
        Boot(key);
        string uri = ListAnswers.GidUri("show", 900);
        ListRow[] rows = [ListAnswers.Member(ListAnswers.GidUri("episode", 901), "00112233445566778899", 123)];
        var answer = Staging.Rent();
        Assert.True(Store.StageList(answer, EdgeRelation.ShowEpisodes, uri, rows, ListAnswers.RevisionA));
        Land(answer);
        Scope scope = Restart(key);
        int slot = scope.Shows.Slot(uri.AsSpan());
        Assert.True(ReadBack(scope, EdgeRelation.ShowEpisodes, scope.Shows.Id[slot]));
        Assert.Equal(EdgeState.Complete, scope.Edges.ShowEpisodes.State(slot));
        Assert.Equal(ListAnswers.RevisionA, Entities.Strings.Resolve(scope.Shows.ListRevision[slot]));
        var restored = Assert.Single(Store.SnapshotList(scope, EdgeRelation.ShowEpisodes, slot)!);
        // Every row of a SHOW's membership list is an episode — the store stamps ItemKind = Episode on write
        // regardless of what the caller passed (ListAnswers.Member defaults to the meaningless Track value).
        Assert.Equal(rows[0] with { ItemKind = PlaylistItemKind.Episode }, restored);
    }

}
