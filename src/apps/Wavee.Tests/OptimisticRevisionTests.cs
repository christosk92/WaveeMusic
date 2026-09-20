// ── Wavee.Tests/OptimisticRevisionTests.cs — an optimistic row edit forgets its list's held revision (wave D3) ────────
//
// docs/plans/wavee/cache-integrity-and-playlist-diff-implementation.md §2: "never persist a revision whose ops/contents
// you did not apply"; "only settled membership is written". Since D3 a held revision is a BASELINE CONTRACT: the planner
// sends it (`Fetch.FillRevisions`) and snapshots the held rows beside it (`Fetch.FillBaselines`), the provider replays
// the `/diff`'s ops over that snapshot, and a dealer push is applied in place when it names it as its parent. An
// optimistic row edit that kept it would have the server's echo of OUR OWN edit replayed over rows that already hold it
// — a positional MOV carries nothing to refuse it by — and that list written to disk at the new head, for good.
//
// These facts drive the doors every optimistic list write lands through — `PlaylistEdits.LandOptimistic` (remove, move,
// revert), `Library.LandRootlistTree` / `AdoptRootlistHead` / `ReadRootlistBack` (every rootlist gesture, a create's
// filing, a delete) and `Library.LandPushReplay` (the dealer's in-place apply) — over a real account scope and the real
// planner with a holding transport. The network halves of the edit host (`RemoveRows`/`MoveRows` need an Online
// session) are not driven: what they land is these doors. The head-adoption rule is pure (`OptimisticHead`).

using System.Collections.Concurrent;
using System.Globalization;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class OptimisticRevisionTests : IDisposable
{
    /// <summary>One write on from <see cref="ListAnswers.RevisionC"/> (counter 135), and two.</summary>
    const string Next = "136,0123456789abcdef0123456789abcdef01234567";
    const string After = "137,89abcdef0123456789abcdef0123456789abcdef";

    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-v3-optimistic-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public OptimisticRevisionTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        Entities.Now = 0;
        ListStamps.ForgetAll();
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        ListStamps.ForgetAll();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }

    // ── the scaffolding ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A live-shaped account scope (EdgeDoorTests' own): the account row is <c>spotify:user:christos</c>.</summary>
    static Scope SignedIn()
    {
        Entities.Boot(new CatalogScope("spotify", "christos", "en-US", "US", 0, true));
        return Entities.Current;
    }

    /// <summary>The same scope over a REAL store file, its posts drained by this thread.</summary>
    Scope SignedInWithStore()
    {
        Store.Post = a => _posted.Enqueue(a);
        Store.Use(_dbPath);
        return Opened();
    }

    /// <summary>A real restart over the same file: a brand-new table set that has never seen the list.</summary>
    Scope Relaunch()
    {
        Store.Shutdown();
        Store.Use(_dbPath);
        return Opened();
    }

    Scope Opened()
    {
        Scope scope = SignedIn();
        Store.Flush();
        DrainPosts();
        return scope;
    }

    void DrainPosts()
    {
        while (_posted.TryDequeue(out Action? a)) a();
    }

    /// <summary><paramref name="count"/> members with gid track uris and hex item ids, <c>seed * 10 + i</c>.</summary>
    static ListRow[] Members(int seed, int count)
    {
        var rows = new ListRow[count];
        for (int i = 0; i < count; i++)
        {
            int n = seed * 10 + i;
            rows[i] = ListAnswers.Member(ListAnswers.TrackUri(n), n.ToString("x8", CultureInfo.InvariantCulture));
        }
        return rows;
    }

    /// <summary>A playlist SETTLED the way a full read settles it — Complete, every row, at <paramref name="revision"/>.</summary>
    static Playlist Settled(string uri, string revision, int seed, int count)
    {
        Staging answer = ListAnswers.FullRead(uri, revision, Members(seed, count));
        Entities.Commit(answer);
        Staging.Return(answer);
        var p = new Playlist(Entities.Current.Playlists.Slot(uri.AsSpan()));
        Assert.Equal(revision, Held(p));
        return p;
    }

    /// <summary>The revision the dealer host and the planner read off the column, or null.</summary>
    static string? Held(Playlist p) => p.RevisionId.IsEmpty ? null : Entities.Strings.Resolve(p.RevisionId);

    /// <summary>The rootlist head the planner and the dealer host read, or null.</summary>
    static string? HeldHead(Scope scope)
    {
        var head = scope.Edges.RootlistRevision(scope.MeSlot);
        return head.IsEmpty ? null : Entities.Strings.Resolve(head);
    }

    /// <summary>The edit host's optimistic REMOVE of one row, as <c>PlaylistEdits.RemoveRows</c> computes it.</summary>
    static void RemoveAt(Playlist p, int at)
    {
        int[] targets = p.TrackSlots.ToArray();
        PlaylistTrackEdge[] payload = p.TrackEdges.ToArray();
        var keptTargets = new int[targets.Length - 1];
        var keptPayload = new PlaylistTrackEdge[targets.Length - 1];
        for (int i = 0, k = 0; i < targets.Length; i++)
            if (i != at) { keptTargets[k] = targets[i]; keptPayload[k++] = payload[i]; }
        Spotify.PlaylistEdits.LandOptimistic(p, keptTargets, keptPayload, EdgeState.Complete, keptTargets.Length);
    }

    /// <summary>The edit host's optimistic MOVE of the first row to the end, as <c>PlaylistEdits.MoveRows</c> computes it.</summary>
    static void MoveFirstToEnd(Playlist p)
    {
        int[] targets = p.TrackSlots.ToArray();
        PlaylistTrackEdge[] payload = p.TrackEdges.ToArray();
        int n = targets.Length;
        var moved = new int[n];
        var movedPayload = new PlaylistTrackEdge[n];
        for (int i = 1; i < n; i++) { moved[i - 1] = targets[i]; movedPayload[i - 1] = payload[i]; }
        moved[n - 1] = targets[0];
        movedPayload[n - 1] = payload[0];
        Spotify.PlaylistEdits.LandOptimistic(p, moved, movedPayload, EdgeState.Complete, n);
    }

    /// <summary>What the dealer host decides for the server's echo of an edit made at <see cref="ListAnswers.RevisionA"/>
    /// — the facts <c>Spotify.Library.PlaylistPushed</c> reads off the live tables, into the pure rule.</summary>
    static ListPush.Verdict EchoVerdict(Playlist p)
    {
        var edges = Entities.Current.Edges.PlaylistTracks;
        return ListPush.Decide(Held(p), ListAnswers.RevisionA, ListAnswers.RevisionB, hasOps: true,
                               resident: edges.State(p.Slot) == EdgeState.Complete,
                               pendingLocal: ListWrite.AnyPending(edges.Pending(p.Slot)), open: false);
    }

    /// <summary>The account's rootlist, landed as a wire read at <paramref name="revision"/>: two playlists, a then b.</summary>
    static (string A, string B) HeldRootlist(Scope scope, string revision)
    {
        string me = scope.Users.Id[scope.MeSlot].Text;
        string a = ListAnswers.PlaylistUri(2_001), b = ListAnswers.PlaylistUri(2_002);
        Staging answer = ListAnswers.Rootlist(me, revision, ListAnswers.Item(0, 0, a), ListAnswers.Item(1, 0, b));
        Entities.Commit(answer);
        Staging.Return(answer);
        Assert.Equal(revision, HeldHead(scope));
        return (a, b);
    }

    static string RootlistUri(Scope scope, int index)
        => scope.Playlists.Id[scope.Edges.Rootlist.Targets(scope.MeSlot)[index]].Text;

    // ── a playlist's rows ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The control: a settled list's refresh carries its revision and the rows it describes — the
    /// <c>/diff</c> and its replay baseline. Everything below is the absence of exactly these two.</summary>
    [Fact]
    public void A_settled_list_sends_its_revision_and_the_rows_it_describes()
    {
        SignedIn();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Playlist p = Settled(ListAnswers.PlaylistUri(1_101), ListAnswers.RevisionA, 11, 3);

        Entities.RefreshEdge(FetchEdge.PlaylistTracks, p.Slot);
        Fetch.Drain();

        FetchBatch ask = provider.Last!;
        Assert.Equal(FetchEdge.PlaylistTracks, ask.Edge);
        Assert.Equal(ListAnswers.RevisionA, ask.Revisions[0]);
        Assert.NotNull(ask.Baselines[0]);
        Assert.Equal(3, ask.Baselines[0]!.Length);
    }

    /// <summary>THE DEFECT, closed at the door: after an optimistic remove the held revision is gone, so the refresh the
    /// edit host sends on the 200 carries no revision and no baseline — the full read, never a <c>/diff</c> from the
    /// pre-edit head replayed over rows that already lack the removed one.</summary>
    [Fact]
    public void An_optimistic_remove_forgets_the_revision_and_the_refresh_is_a_full_read()
    {
        SignedIn();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        Playlist p = Settled(ListAnswers.PlaylistUri(1_201), ListAnswers.RevisionA, 12, 3);

        RemoveAt(p, 1);

        Assert.Equal(2, p.TrackSlots.Length);
        Assert.Null(Held(p));
        Entities.RefreshEdge(FetchEdge.PlaylistTracks, p.Slot);
        Fetch.Drain();
        FetchBatch ask = provider.Last!;
        Assert.Equal(FetchEdge.PlaylistTracks, ask.Edge);
        Assert.Null(ask.Revisions[0]);                                        // no revision: the full read
        Assert.Null(ask.Baselines[0]);                                        // nothing to replay anything over
    }

    /// <summary>The dealer half: before a move the server's echo of an edit made at the held head WOULD be replayed in
    /// place; after it, the held head is not the echo's parent, so the echo can only mark the list dirty.</summary>
    [Fact]
    public void An_optimistic_move_forgets_the_revision_so_its_echo_can_only_dirty_the_list()
    {
        SignedIn();
        Playlist p = Settled(ListAnswers.PlaylistUri(1_301), ListAnswers.RevisionA, 13, 3);
        Assert.Equal(ListPush.Verdict.ApplyInPlace, EchoVerdict(p));
        int first = p.TrackSlots[0];

        MoveFirstToEnd(p);

        Assert.Equal(first, p.TrackSlots[2]);
        Assert.Null(Held(p));
        Assert.Equal(ListPush.Verdict.MarkDirty, EchoVerdict(p));
    }

    /// <summary>The forget is MEMORY ONLY: the file keeps the consistent pre-edit pair, so a restart restores the rows
    /// the edit was made against with the head they are true at, and its first ask is the <c>/diff</c> from that head —
    /// which replays the edit exactly once.</summary>
    [Fact]
    public void The_forget_is_memory_only_so_the_next_launch_diffs_from_the_pre_edit_pair()
    {
        Scope scope = SignedInWithStore();
        string uri = ListAnswers.PlaylistUri(1_401);
        Staging answer = ListAnswers.FullRead(uri, ListAnswers.RevisionA, Members(14, 3));
        Entities.Commit(answer);
        Assert.True(Store.WriteBehind(answer));
        Store.Flush();
        var p = new Playlist(scope.Playlists.Slot(uri.AsSpan()));

        RemoveAt(p, 0);
        Assert.Null(Held(p));
        Store.Flush();

        Scope next = Relaunch();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        int slot = next.Playlists.Slot(uri.AsSpan());
        Entities.EnsureEdge(FetchEdge.PlaylistTracks, slot);
        Fetch.Drain();                                                        // the disk first…
        Store.Flush();
        DrainPosts();                                                         // …the pre-edit list lands…
        Fetch.Drain();                                                        // …then the network ask

        Assert.Equal(3, next.Edges.PlaylistTracks.Count(slot));
        Assert.Equal(ListAnswers.RevisionA, Held(new Playlist(slot)));
        FetchBatch ask = provider.Last!;
        Assert.Equal(ListAnswers.RevisionA, ask.Revisions[0]);
        Assert.Equal(3, ask.Baselines[0]!.Length);
    }

    // ── the dealer's in-place apply ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A replayed push the staging refuses (here: a member with no identity) changes NOTHING — no rows, no head
    /// — and is never stamped revalidated: a stamp would call current a list that never landed.</summary>
    [Fact]
    public void A_push_replay_the_staging_refuses_changes_nothing_and_stamps_nothing()
    {
        Scope scope = SignedIn();
        string uri = ListAnswers.PlaylistUri(1_501);
        Playlist p = Settled(uri, ListAnswers.RevisionA, 15, 3);
        uint version = scope.Edges.PlaylistTracks.Version(p.Slot);
        ListRow[] replayed = [Members(15, 1)[0], ListAnswers.Member("", "000000ff")];

        Assert.False(Spotify.Library.LandPushReplay(scope, uri, replayed, ListAnswers.RevisionB));

        Assert.Equal(0, ListStamps.RevalidatedAtMs(uri));
        Assert.Equal(ListAnswers.RevisionA, Held(p));
        Assert.Equal(version, scope.Edges.PlaylistTracks.Version(p.Slot));
        Assert.Equal(3, p.TrackSlots.Length);
    }

    /// <summary>The control for the fact above: a replay that stages lands its rows and its head together, and only
    /// then is the list stamped revalidated.</summary>
    [Fact]
    public void A_push_replay_that_stages_lands_the_pair_and_stamps_it()
    {
        Scope scope = SignedIn();
        string uri = ListAnswers.PlaylistUri(1_601);
        Playlist p = Settled(uri, ListAnswers.RevisionA, 16, 3);
        ListRow[] members = Members(16, 3);
        ListRow[] replayed = [members[2], members[0]];

        Assert.True(Spotify.Library.LandPushReplay(scope, uri, replayed, ListAnswers.RevisionB));

        Assert.NotEqual(0, ListStamps.RevalidatedAtMs(uri));
        Assert.Equal(ListAnswers.RevisionB, Held(p));
        Assert.Equal(2, p.TrackSlots.Length);
        Assert.Equal(members[2].Uri, scope.Tracks.Id[p.TrackSlots[0]].Text);
    }

    // ── the rootlist ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The control: a held rootlist's refresh carries its head and its tree.</summary>
    [Fact]
    public void A_held_rootlist_sends_its_head_and_the_tree_it_describes()
    {
        Scope scope = SignedIn();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        HeldRootlist(scope, ListAnswers.RevisionC);

        Entities.RefreshEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();

        FetchBatch ask = provider.Last!;
        Assert.Equal(ListAnswers.RevisionC, ask.Revisions[0]);
        Assert.Equal(2, ask.Baselines[0]!.Length);
    }

    /// <summary>A rootlist gesture lands its tree and forgets the head it was computed over — returned as the write's
    /// base — so the next rootlist ask is the full read and a push (the echo) is never dropped as "already held".</summary>
    [Fact]
    public void A_rootlist_write_forgets_the_held_head_and_the_next_ask_is_a_full_read()
    {
        Scope scope = SignedIn();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        (string a, string b) = HeldRootlist(scope, ListAnswers.RevisionC);

        var landed = Spotify.Library.LandRootlistTree(scope, Spotify.Encode.EntriesFromUris([b, a]));

        Assert.Equal(ListAnswers.RevisionC, landed.Base);
        Assert.Equal(b, RootlistUri(scope, 0));                               // the tree is live
        Assert.Null(HeldHead(scope));
        Assert.NotEqual(ListPush.Verdict.Drop, ListPush.DecideRootlist(HeldHead(scope), Next, open: true));
        Entities.RefreshEdge(FetchEdge.Rootlist, scope.MeSlot);
        Fetch.Drain();
        FetchBatch ask = provider.Last!;
        Assert.Equal(FetchEdge.Rootlist, ask.Edge);
        Assert.Null(ask.Revisions[0]);
        Assert.Null(ask.Baselines[0]);
    }

    /// <summary>The 200's head one write on from the base, over rows nothing touched since: provably the landed tree, so
    /// it is held again — and the write's own echo drops as the head already held.</summary>
    [Fact]
    public void A_confirmed_rootlist_write_holds_the_head_one_write_on_from_its_base()
    {
        Scope scope = SignedIn();
        (string a, string b) = HeldRootlist(scope, ListAnswers.RevisionC);
        var landed = Spotify.Library.LandRootlistTree(scope, Spotify.Encode.EntriesFromUris([b, a]));

        Assert.True(Spotify.Library.AdoptRootlistHead(scope, landed, Next));

        Assert.Equal(Next, HeldHead(scope));
        Assert.Equal(ListPush.Verdict.Drop, ListPush.DecideRootlist(HeldHead(scope), Next, open: true));
    }

    /// <summary>A reply head that skipped a counter step means the server rebased our ops over a change this client never
    /// saw: the landed tree is not that head's list, so nothing is held.</summary>
    [Fact]
    public void A_reply_head_that_skipped_a_write_is_not_held()
    {
        Scope scope = SignedIn();
        (string a, string b) = HeldRootlist(scope, ListAnswers.RevisionC);
        var landed = Spotify.Library.LandRootlistTree(scope, Spotify.Encode.EntriesFromUris([b, a]));

        Assert.False(Spotify.Library.AdoptRootlistHead(scope, landed, After));

        Assert.Null(HeldHead(scope));
    }

    /// <summary>Two writes in flight: the second tree was computed over the first's unconfirmed tree (no base), and the
    /// first's rows moved on under the second — neither 200 can vouch for what is live.</summary>
    [Fact]
    public void A_second_landing_before_the_confirm_leaves_the_head_forgotten()
    {
        Scope scope = SignedIn();
        (string a, string b) = HeldRootlist(scope, ListAnswers.RevisionC);
        var first = Spotify.Library.LandRootlistTree(scope, Spotify.Encode.EntriesFromUris([b, a]));
        var second = Spotify.Library.LandRootlistTree(scope, Spotify.Encode.EntriesFromUris([a, b]));

        Assert.Equal("", second.Base);
        Assert.False(Spotify.Library.AdoptRootlistHead(scope, first, Next));
        Assert.False(Spotify.Library.AdoptRootlistHead(scope, second, After));
        Assert.Null(HeldHead(scope));
    }

    /// <summary>A read that landed between the write and its 200 brought its own pair; the 200 never overwrites it.</summary>
    [Fact]
    public void A_landing_between_the_write_and_its_confirm_keeps_its_own_head()
    {
        Scope scope = SignedIn();
        (string a, string b) = HeldRootlist(scope, ListAnswers.RevisionC);
        var landed = Spotify.Library.LandRootlistTree(scope, Spotify.Encode.EntriesFromUris([b, a]));
        string me = scope.Users.Id[scope.MeSlot].Text;
        Staging read = ListAnswers.Rootlist(me, Next, ListAnswers.Item(0, 0, b), ListAnswers.Item(1, 0, a));
        Entities.Commit(read);
        Staging.Return(read);

        Assert.False(Spotify.Library.AdoptRootlistHead(scope, landed, Next));

        Assert.Equal(Next, HeldHead(scope));
    }

    /// <summary>A refused write (and a create that did not file, and a delete): whatever head is held — here one a
    /// landing re-armed while the write was out — is forgotten, and the rootlist is read back in FULL, never as a
    /// <c>/diff</c> answered about a tree the server never had.</summary>
    [Fact]
    public void A_refused_rootlist_write_reads_the_rootlist_back_in_full()
    {
        Scope scope = SignedIn();
        var provider = new HoldingProvider(EntityProvider.Spotify);
        Fetch.Register(provider);
        HeldRootlist(scope, ListAnswers.RevisionC);

        Spotify.Library.ReadRootlistBack(scope);
        Fetch.Drain();

        Assert.Null(HeldHead(scope));
        FetchBatch ask = provider.Last!;
        Assert.Equal(FetchEdge.Rootlist, ask.Edge);
        Assert.Null(ask.Revisions[0]);
        Assert.Null(ask.Baselines[0]);
    }

    // ── the head-adoption rule (pure) ───────────────────────────────────────────────────────────────────────────────

    /// <summary>One write on, exactly: every <c>/changes</c> in the captures moved the counter by one (the a164
    /// folder-create golden 71→72; reorders.saz 28→29 … 35→36). Anything else — a skipped step, no step, a malformed or
    /// missing head — holds nothing, and a touched list holds nothing whatever the heads say.</summary>
    [Theory]
    [InlineData("71,0123456789abcdef0123456789abcdef01234567", "72,89abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("28,0123456789abcdef0123456789abcdef01234567", "29,89abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0,0123456789abcdef0123456789abcdef01234567", "1,89abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("28,0123456789abcdef0123456789abcdef01234567", "30,89abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("28,0123456789abcdef0123456789abcdef01234567", "28,89abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("29,0123456789abcdef0123456789abcdef01234567", "28,89abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("", "1,89abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("28,0123456789abcdef0123456789abcdef01234567", "", false)]
    [InlineData("4294967295,0123456789abcdef0123456789abcdef01234567", "4294967296,89abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("7,0123", "8,89abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("7,0123456789ABCDEF0123456789ABCDEF01234567", "8,89abcdef0123456789abcdef0123456789abcdef", false)]
    public void A_reply_head_is_held_only_one_write_on_from_its_base(string from, string to, bool held)
    {
        Assert.Equal(held, OptimisticHead.IsDirectSuccessor(from, to));
        Assert.Equal(held, OptimisticHead.MayAdopt(from, to, untouched: true));
        Assert.False(OptimisticHead.MayAdopt(from, to, untouched: false));
    }

    [Fact]
    public void A_missing_base_or_head_holds_nothing()
    {
        Assert.False(OptimisticHead.IsDirectSuccessor(null, Next));
        Assert.False(OptimisticHead.IsDirectSuccessor(ListAnswers.RevisionC, null));
        Assert.True(OptimisticHead.IsDirectSuccessor(ListAnswers.RevisionC, Next));
    }
}
