// ── Wavee.Tests/QueueSeedTests.cs — bug I's pure gate: where a queue seeds from ────────────────────────────────────
//
// Bug I: open the queue rail while a track is already playing but before Play was pressed in this session — a
// restored deck, or a mirrored remote cluster — and the rail shows one grey shimmer row forever, because
// `Queue.State` (Entities/Queue.cs) never leaves `EdgeState.Unknown` until something calls `Queue.Replace`, and
// none of the host's writes ever ran for a row that arrived this way. `Queue.DecideSeed` is the pure decision the
// fix hangs the host's wiring (`Playback.Host.Context.cs`'s `Restore`/`ResolveSeedContext`/`LandSeedContext`,
// `Playback.Host.Remote.cs`'s `SeedQueueFromCluster`) off — no session, no scope, no engine loop, exactly the
// house rule ("no source-text tests": the decision lives in a small pure class, and this tests THAT, the same way
// `SetupGating`, `AppUpdateToasts`, `ShutdownUpdatePolicy` and `ReleaseNotesRange` do for their own screens).
//
// THE ONE THING THAT MUST NEVER HAPPEN: a queue that already answered "what plays next" — by a real Play, or by an
// earlier seed — gets re-decided out from under it. That is not a sequence number race here; it is simply that
// `DecideSeed` refuses everything the moment `state` is not `Unknown` (the last group below).

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class QueueSeedTests
{
    // ── the skeleton is still correct when nothing is playing ─────────────────────────────────────────────────────

    [Fact]
    public void No_current_track_and_no_queue_stays_Unknown()
    {
        // The one case EdgeState.Unknown exists for (Queue.cs:274-276): nobody has answered yet, so the rail must
        // render its skeleton — not an empty "up next", which would be a confident lie about a session that has
        // simply not loaded anything.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: false, hasClusterTracks: false, hasContext: false);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    [Fact]
    public void No_current_track_stays_None_even_with_rows_to_seed_from()
    {
        // A cluster or a context resolve carrying rows means nothing without a real current row to hang them off —
        // there is no "now playing" to seed a deck for, so the answer is still None, not a guess.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: false, hasClusterTracks: true, hasContext: true);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    // ── a real current row must not sit next to an Unknown queue ──────────────────────────────────────────────────

    [Fact]
    public void Real_current_with_a_resolvable_context_seeds_from_the_context()
    {
        // A restored session (or a claim with a known context and no cluster rows in hand): resolve it locally, the
        // same path a Play takes — so the rail is not a skeleton once the resolve lands.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: false, hasContext: true);
        Assert.Equal(Queue.SeedSource.Context, source);
    }

    [Fact]
    public void Real_current_with_cluster_next_tracks_seeds_from_the_cluster()
    {
        // A mirrored remote cluster carries its own prev/next tracks already in hand (no further I/O needed) — build
        // straight from those instead of going back out to resolve a context we may not even own.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: true, hasContext: false);
        Assert.Equal(Queue.SeedSource.Cluster, source);
    }

    [Fact]
    public void Cluster_rows_are_preferred_over_a_resolvable_context()
    {
        // Both available at once: the cluster's rows are already in hand and free (no I/O), the context resolve is
        // not — so a cluster that carries rows wins outright rather than kicking off a redundant resolve.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: true, hasContext: true);
        Assert.Equal(Queue.SeedSource.Cluster, source);
    }

    [Fact]
    public void Real_current_with_nothing_to_seed_from_falls_back_to_the_bare_row()
    {
        // PINNED BEHAVIOUR: a real current row, no cluster rows, no context worth resolving (a bare track played with
        // no context, or a context that turned out empty) — CurrentOnly, never None. Chosen over leaving the queue
        // Unknown because Unknown is reserved for "we have not answered yet"; here the answer already IS "just this
        // row" and saying so (a one-row "Playing from" card, no history, no next up) is honest, whereas an Unknown
        // queue next to a real, populated player bar is exactly bug I. It also matches the launch restore's existing
        // one-row fallback (Playback.Host.Context.cs's SeedCurrentRow), so Next/Previous have a cursor to walk at once.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: false, hasContext: false);
        Assert.Equal(Queue.SeedSource.CurrentOnly, source);
    }

    // ── a seed must never clobber a queue that already answered "what plays next" ──────────────────────────────────

    [Theory]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    public void A_queue_that_left_Unknown_is_never_reseeded(EdgeState state)
    {
        // A real Play (or an earlier seed) already wrote real rows and moved the queue off Unknown — a late cluster
        // heartbeat or a straggling resolve answer must not re-decide the question and clobber what is there, even
        // when every other input looks like a perfect seed candidate.
        Queue.SeedSource source = Queue.DecideSeed(state,
            hasCurrent: true, hasClusterTracks: true, hasContext: true);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    [Fact]
    public void A_non_Unknown_queue_with_no_current_is_also_left_alone()
    {
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Complete,
            hasCurrent: false, hasClusterTracks: false, hasContext: false);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    // ── the TAKEOVER override (A4, playback plan Wave A): the user claiming a mirrored row ───────────────────────────
    //
    // A takeover is not "a late cluster" — it is the listener adopting the session that row already belongs to. It
    // overrides the Unknown gate ONLY (a real current row is still required, and a takeover with nothing to seed
    // from still lands CurrentOnly rather than staying silent — that is what un-sticks a permanently cursor-less
    // mirrored row even once the cluster's own prev/next have gone stale).

    [Theory]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    public void A_takeover_reseeds_from_the_cluster_even_though_the_queue_already_left_Unknown(EdgeState state)
    {
        Queue.SeedSource source = Queue.DecideSeed(state,
            hasCurrent: true, hasClusterTracks: true, hasContext: false, takeover: true);
        Assert.Equal(Queue.SeedSource.Cluster, source);
    }

    [Theory]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    public void A_takeover_with_no_cluster_rows_still_lands_the_bare_current_row_rather_than_staying_silent(EdgeState state)
    {
        Queue.SeedSource source = Queue.DecideSeed(state,
            hasCurrent: true, hasClusterTracks: false, hasContext: false, takeover: true);
        Assert.Equal(Queue.SeedSource.CurrentOnly, source);
    }

    [Fact]
    public void A_takeover_still_answers_None_with_no_current_row_to_hang_it_off()
    {
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Complete,
            hasCurrent: false, hasClusterTracks: true, hasContext: true, takeover: true);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    [Fact]
    public void A_takeover_on_an_already_Unknown_queue_behaves_exactly_like_a_normal_seed()
    {
        // takeover changes nothing when the gate it overrides was never going to refuse in the first place.
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: true, hasContext: false, takeover: true);
        Assert.Equal(Queue.SeedSource.Cluster, source);
    }

    // ── the seed RETRY decision (bug: a boot-time context resolve asked before the session authorises) ──────────────
    //
    // `Queue.SeedRetryOn` is the pure half of the fix for a second, milder form of bug I: the launch restore's context
    // resolve (`Playback.Host.Context.cs`'s `ResolveSeedContext`/`LandSeedContext`) can go out before the session has
    // adopted its catalog scope and comes back 401. The old code treated that identically to a genuinely empty
    // context and landed the bare row — which then made `DecideSeed` refuse to ever re-seed once the session came
    // online, because the queue had already left `EdgeState.Unknown`.

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void An_authentication_refusal_retries_on_the_next_online_transition(int status)
    {
        Queue.SeedRetryDecision decision = Queue.SeedRetryOn(status);
        Assert.Equal(Queue.SeedRetryDecision.RetryOnline, decision);
    }

    [Fact]
    public void A_genuinely_empty_context_lands_the_bare_row_now()
    {
        // 2xx, no tracks: the resolve answered honestly, so there is nothing further worth waiting on.
        Queue.SeedRetryDecision decision = Queue.SeedRetryOn(200);
        Assert.Equal(Queue.SeedRetryDecision.CurrentOnly, decision);
    }

    [Fact]
    public void A_saturated_local_api_queue_lands_the_bare_row_now_rather_than_wait()
    {
        // No server was ever asked (Queue.SeedResolveQueueFull), so there is no auth question to retry either —
        // exactly like an empty context, land the bare row.
        Queue.SeedRetryDecision decision = Queue.SeedRetryOn(Queue.SeedResolveQueueFull);
        Assert.Equal(Queue.SeedRetryDecision.CurrentOnly, decision);
    }

    [Theory]
    [InlineData(EdgeState.Partial)]
    [InlineData(EdgeState.Complete)]
    public void A_queue_that_left_Unknown_is_never_retried_even_once_online(EdgeState state)
    {
        // The armed retry re-runs DecideSeed from scratch before ever asking SeedRetryOn anything — a real Play, or
        // the cluster seed (Playback.Host.Remote.cs's SeedQueueFromCluster), may have already answered "what plays
        // next" while the retry waited for Online, and that answer must never be clobbered.
        Queue.SeedSource source = Queue.DecideSeed(state, hasCurrent: true, hasClusterTracks: false, hasContext: true);
        Assert.Equal(Queue.SeedSource.None, source);
    }

    // ── the queue-fix bugs: session wavee-20260923.log sid=2137ef66, seq 109/475/604 ─────────────────────────────────
    //
    // Bug (b): SeedQueueFromCluster (Playback.Host.Remote.cs) used to hardcode `hasContext: false`, discarding
    // `State.MirrorContext` — so a foreign session whose first fold carried no prev/next tracks landed
    // SeedSource.CurrentOnly (an empty queue, only the card) instead of resolving the richer context. DecideSeed
    // itself already answers Context correctly for this exact shape (Unknown, a real current row, no cluster rows,
    // a context available) — this is the case the fix's `hasContext` now actually reaches.

    [Fact]
    public void A_foreign_fold_with_no_cluster_rows_but_a_mirrored_context_seeds_from_the_context()
    {
        Queue.SeedSource source = Queue.DecideSeed(EdgeState.Unknown,
            hasCurrent: true, hasClusterTracks: false, hasContext: true);
        Assert.Equal(Queue.SeedSource.Context, source);
    }

    // Bug (a): ShouldFollowMirror — a foreign mirror that already left Unknown never gets re-bucketed when the
    // owner advances (MirrorRemote only ever touches Current/CurrentId), so "Next up" keeps starting from the row
    // that used to be playing. This is the pure gate the fix (SeedQueueFromCluster's post-seed re-bucket) hangs off.

    [Fact]
    public void A_foreign_mirror_past_Unknown_with_the_row_found_elsewhere_should_follow()
    {
        bool should = Queue.ShouldFollowMirror(EdgeState.Complete, isForeign: true, foundIndex: 3, foundBucket: (byte)QueueBucket.NextUp);
        Assert.True(should);
    }

    [Fact]
    public void A_row_already_in_the_NowPlaying_bucket_is_a_no_op()
    {
        // The common case: an earlier follow (or a fresh Replace) already put the row where it belongs — re-running
        // Follow would only cost a version bump for nothing.
        bool should = Queue.ShouldFollowMirror(EdgeState.Complete, isForeign: true, foundIndex: 1, foundBucket: (byte)QueueBucket.NowPlaying);
        Assert.False(should);
    }

    [Fact]
    public void A_row_not_found_in_the_mirrored_list_has_nothing_to_follow()
    {
        bool should = Queue.ShouldFollowMirror(EdgeState.Complete, isForeign: true, foundIndex: -1, foundBucket: 0);
        Assert.False(should);
    }

    [Fact]
    public void A_local_session_is_never_re_followed_through_this_seam()
    {
        // A local cursor move already follows through the ordinary transport path (Advance/PutOnDeck); this seam is
        // for a FOREIGN mirror only.
        bool should = Queue.ShouldFollowMirror(EdgeState.Complete, isForeign: false, foundIndex: 3, foundBucket: (byte)QueueBucket.NextUp);
        Assert.False(should);
    }

    [Fact]
    public void An_Unknown_queue_is_DecideSeeds_job_not_this_ones()
    {
        bool should = Queue.ShouldFollowMirror(EdgeState.Unknown, isForeign: true, foundIndex: 3, foundBucket: (byte)QueueBucket.NextUp);
        Assert.False(should);
    }

    // ── the mirror decision: while another device owns playback the panel IS the owner's ──────────────────────────────

    static Queue.MirrorDecision Mirror(Playback.Owner owner = Playback.Owner.Foreign, EdgeState state = EdgeState.Complete,
        bool hasCurrent = true, bool tracks = true, bool stampMoved = false, int found = 2, QueueBucket bucket = QueueBucket.NowPlaying)
        => Queue.DecideMirror(owner, state, hasCurrent, tracks, stampMoved, found, (byte)bucket);

    [Fact]
    public void A_moved_session_or_revision_replaces_the_rows_from_the_cluster()
        => Assert.Equal(Queue.MirrorDecision.ReplaceFromCluster, Mirror(stampMoved: true));

    [Fact]
    public void A_current_row_missing_from_the_local_rows_replaces_them()
        => Assert.Equal(Queue.MirrorDecision.ReplaceFromCluster, Mirror(found: -1, bucket: 0));

    [Fact]
    public void The_last_row_of_an_old_local_queue_with_the_stamp_moved_replaces_them()
    {
        // The Fly Away capture: 12 local album rows, the owner's row is the last one (found = 11, nothing after it), and
        // the owner's 52 autoplay rows were never turned into rows — a moved stamp re-derives them.
        Assert.Equal(Queue.MirrorDecision.ReplaceFromCluster,
            Mirror(stampMoved: true, found: 11, bucket: QueueBucket.NextUp));
    }

    [Fact]
    public void An_empty_or_never_loaded_queue_takes_the_owners_rows_when_the_cluster_has_any()
    {
        Assert.Equal(Queue.MirrorDecision.ReplaceFromCluster, Mirror(state: EdgeState.Unknown, tracks: true, found: -1, bucket: 0));
        // With none to give, DecideSeed resolves the context instead.
        Assert.Equal(Queue.MirrorDecision.None, Mirror(state: EdgeState.Unknown, tracks: false, found: -1, bucket: 0));
    }

    [Fact]
    public void An_agreeing_mirror_only_follows_and_a_settled_one_does_nothing()
    {
        Assert.Equal(Queue.MirrorDecision.FollowMirror, Mirror(found: 3, bucket: QueueBucket.NextUp));
        Assert.Equal(Queue.MirrorDecision.None, Mirror(found: 3, bucket: QueueBucket.NowPlaying));
    }

    [Theory]
    [InlineData(Playback.Owner.Us)]
    [InlineData(Playback.Owner.Nobody)]
    public void Only_a_foreign_owner_is_mirrored(Playback.Owner owner)
    {
        Assert.Equal(Queue.MirrorDecision.None, Mirror(owner, stampMoved: true, found: -1, bucket: 0));
        Assert.Equal(Queue.MirrorDecision.None, Mirror(hasCurrent: false, stampMoved: true));
    }

    // ── the mirror layout: the owner's prev / deck / next as rows ──────────────────────────────────────────────────────

    static Queue.MirrorRow Row(bool queued = false, bool autoplay = false, ulong item = 0, bool playable = true)
        => new(playable, queued, autoplay, item);

    static int Lay(Queue.MirrorRow[] prev, Queue.MirrorRow[] next, out QueueEdge[] rows, out int[] origin, out int deck)
    {
        rows = new QueueEdge[prev.Length + next.Length + 1];
        origin = new int[rows.Length];
        int n = Queue.LayoutMirror(prev, next, rows, origin, out deck);
        rows = rows[..n];
        return n;
    }

    [Fact]
    public void The_layout_buckets_history_the_deck_the_queue_and_the_continuation_by_provenance()
    {
        var prev = new[] { Row(item: 1), Row(item: 2) };
        var next = new[] { Row(item: 3), Row(queued: true, item: 4), Row(autoplay: true, item: 5), Row(item: 6) };
        int n = Lay(prev, next, out var rows, out var origin, out int deck);

        Assert.Equal(7, n);
        Assert.Equal(prev.Length, deck);
        Assert.Equal(((byte)QueueBucket.History, (byte)QueueProvider.Context), (rows[0].Bucket, rows[0].Provider));
        Assert.Equal(new QueueEdge(0, (byte)QueueProvider.Context, (byte)QueueBucket.NowPlaying), rows[deck]);   // the deck: item 0
        Assert.Equal(new QueueEdge(4, (byte)QueueProvider.Queue, (byte)QueueBucket.UserQueue), rows[3]);          // queued first
        Assert.Equal(new QueueEdge(3, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp), rows[4]);
        Assert.Equal(new QueueEdge(5, (byte)QueueProvider.Autoplay, (byte)QueueBucket.NextUp), rows[5]);
        Assert.Equal(new QueueEdge(6, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp), rows[6]);
        Assert.True(Queue.IsOrdered(rows));
        Assert.Equal(new[] { 0, 1, -1, 3, 2, 4, 5 }, origin[..n]);   // prev 0,1 · deck · next k = prev.Length + k
    }

    [Fact]
    public void A_mirrors_divider_is_the_deck_and_upcoming_is_only_the_owners_next_rows()
    {
        // The screenshot regression as a pure assertion: nothing of prev, and not the current row, is upcoming.
        Lay([Row(item: 1), Row(item: 2)], [Row(item: 3), Row(item: 4), Row(autoplay: true, item: 5)], out var rows, out _, out int deck);

        Assert.Equal(deck, Queue.Divider(rows, -1));
        var dst = new int[rows.Length];
        int n = Queue.Split(rows, Queue.Divider(rows, -1), dst, out int user, out int next, out int auto);
        Assert.Equal(3, n);
        Assert.Equal((0, 2, 1), (user, next, auto));
        Assert.Equal(new[] { deck + 1, deck + 2, deck + 3 }, dst[..n]);
    }

    [Fact]
    public void A_skip_moves_the_first_upcoming_row()
    {
        // prev=2,next=50 vs prev=1,next=51 after the owner skipped: the same tail, one row nearer the front.
        var tail = new Queue.MirrorRow[51];
        for (int i = 0; i < tail.Length; i++) tail[i] = Row(item: (ulong)(100 + i));
        Lay([Row(item: 1), Row(item: 2)], tail[1..], out var before, out _, out int d1);
        Lay([Row(item: 1)], tail, out var after, out _, out int d2);

        Assert.Equal(d1, Queue.Divider(before, -1));
        Assert.Equal(2, d1);
        Assert.Equal(1, d2);
        Assert.NotEqual(before[d1 + 1].ItemId, after[d2 + 1].ItemId);
    }

    [Fact]
    public void Unplayable_rows_are_dropped_from_the_layout_and_the_count()
    {
        // `spotify:delimiter` / `spotify:meta:page:1` markers resolve to no playable target.
        int n = Lay([Row(playable: false), Row(item: 1)], [Row(playable: false), Row(item: 2), Row(playable: false)],
            out var rows, out var origin, out int deck);

        Assert.Equal(3, n);
        Assert.Equal(1, deck);
        Assert.Equal(new[] { 1, -1, 3 }, origin[..n]);
        Assert.True(Queue.IsOrdered(rows));
    }

    // ── a click on a row while another device owns playback ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(QueueBucket.NextUp, Queue.RowVerb.NextTrack)]
    [InlineData(QueueBucket.UserQueue, Queue.RowVerb.NextTrack)]
    [InlineData(QueueBucket.History, Queue.RowVerb.PlayContext)]
    [InlineData(QueueBucket.NowPlaying, Queue.RowVerb.PlayContext)]
    public void Only_a_row_of_the_owners_next_tracks_is_a_next_track(QueueBucket bucket, Queue.RowVerb expected)
    {
        var rows = new[] { new QueueEdge(5, (byte)QueueProvider.Context, (byte)bucket) };
        Assert.Equal(expected, Queue.RemoteRowVerb(rows, 0, rowMatches: true, out ulong item));
        Assert.Equal(expected == Queue.RowVerb.NextTrack ? 5UL : 0UL, item);
    }

    [Fact]
    public void A_click_without_a_cursor_or_on_another_row_is_a_context_play()
    {
        var rows = new[] { new QueueEdge(5, (byte)QueueProvider.Context, (byte)QueueBucket.NextUp) };
        Assert.Equal(Queue.RowVerb.PlayContext, Queue.RemoteRowVerb(rows, -1, rowMatches: false, out _));
        Assert.Equal(Queue.RowVerb.PlayContext, Queue.RemoteRowVerb(rows, 0, rowMatches: false, out _));   // a stale cursor
        Assert.Equal(Queue.RowVerb.PlayContext, Queue.RemoteRowVerb(rows, 3, rowMatches: true, out _));    // past the rows
    }

    [Fact]
    public void A_single_track_context_never_frames_a_different_track()
    {
        EntityId a = EntityId.ForGid(EntityKind.Track, (UInt128)1UL), b = EntityId.ForGid(EntityKind.Track, (UInt128)2UL);
        EntityId album = EntityId.ForGid(EntityKind.Album, (UInt128)3UL);
        Assert.Equal(b, ForwardPlayEnvelope.For(a, b));          // the owner's painted track context, another track clicked
        Assert.Equal(a, ForwardPlayEnvelope.For(a, a));
        Assert.Equal(album, ForwardPlayEnvelope.For(album, b));  // a real context passes through
        Assert.Equal(b, ForwardPlayEnvelope.For(default, b));    // no context: the track is its own
        Assert.Equal(album, ForwardPlayEnvelope.For(album, default));
    }

    [Fact]
    public void No_resolved_deck_mirrors_nothing()
        => Assert.Equal(Queue.MirrorDecision.None, Mirror(hasCurrent: false, stampMoved: true, found: -1, bucket: 0));

    [Fact]
    public void Only_a_foreign_owner_takes_the_local_queue_away()
    {
        Assert.False(Queue.LocalQueueWritable(Playback.Owner.Foreign));
        Assert.True(Queue.LocalQueueWritable(Playback.Owner.Us));
        Assert.True(Queue.LocalQueueWritable(Playback.Owner.Nobody));
    }

    [Fact]
    public void The_header_context_is_the_foreign_owners_and_only_while_it_owns()
    {
        EntityId remote = EntityId.ForGid(EntityKind.Album, (UInt128)1UL), local = EntityId.ForGid(EntityKind.Playlist, (UInt128)2UL);
        Assert.Equal(remote, Queue.MirrorContextFor(Playback.Owner.Foreign, true, remote, local));
        Assert.True(Queue.MirrorContextFor(Playback.Owner.Foreign, true, default, local).IsEmpty);   // no context IS the answer
        Assert.Equal(local, Queue.MirrorContextFor(Playback.Owner.Foreign, false, default, local));  // an idle cluster says nothing
        Assert.Equal(local, Queue.MirrorContextFor(Playback.Owner.Us, true, remote, local));
        Assert.Equal(local, Queue.MirrorContextFor(Playback.Owner.Nobody, true, remote, local));
    }
}
