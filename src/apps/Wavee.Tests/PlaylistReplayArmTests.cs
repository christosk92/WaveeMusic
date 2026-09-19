// ── Wavee.Tests/PlaylistReplayArmTests.cs — the /diff replay arm (wave D3, plan §3.3), end to end without a transport ──
//
// The arm's network half is two requests (`Spotify.Api.ReadList`); everything it DECIDES is `ListReplay.Decide` (pure:
// the answer's bytes + the revision held + the baseline the batch carried → a verdict and a list), and everything it
// LANDS goes through `Spotify.Decode.PlaylistReplay`/`RootlistReplay` → `Store.StageList`. These facts drive exactly
// those, over the captured playlist-ops fixtures (Fixtures/playlist-ops, README there): the held list is landed the way
// the wire lands it (the full-read decoders, committed), its baseline taken the way the planner takes it
// (`Store.SnapshotList`), the answer decided, the replay staged and committed — and the result compared with what a REAL
// read of the same revision lands, wherever a capture has one (playlist r35 → r36, rootlist 135 → 137).
//
// The `SpotifyApiTests` cases of the retired `DiffVerdict` (the four answer shapes, field 20 outranking every other
// field) are re-pinned here against the arm that replaced it.

using System.Collections.Concurrent;
using System.Text;
using Google.Protobuf;
using Wavee;
using Xunit;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlaylistReplayArmTests
{
    const string WireRevision = @"^\d{1,10},[0-9a-f]{40}$";

    static byte[] Bytes(string name) => PlaylistOpsFixtures.Bytes(name);

    static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The full-read decoders' own unit rule (<c>Spotify.Decode.Instant</c>): milliseconds past 10^11.</summary>
    static int Seconds(long wire) => wire <= 0 ? 0 : (int)(wire > 100_000_000_000L ? wire / 1000 : wire);

    /// <summary>A PlaylistRead answer landed exactly as the provider lands one (<c>Spotify.Api.PlaylistAnswer</c>).</summary>
    static int LandRead(string uri, byte[] body)
    {
        var s = Staging.Rent();
        Spotify.Decode.PlaylistRevision(body, Utf8(uri), s);
        Spotify.Decode.PlaylistFormatAttributes(body, Utf8(uri), s);
        TestScope.CommitAndPublish(s);
        return Entities.Current.Playlists.Slot(uri.AsSpan());
    }

    static void LandReplay(string uri, in ListReplay.Outcome outcome)
    {
        var s = Staging.Rent();
        Assert.True(Spotify.Decode.PlaylistReplay(outcome.Rows!, uri, outcome.Answer.To!, outcome.Attrs, s));
        TestScope.CommitAndPublish(s);
    }

    static string Held(int slot) => Entities.Strings.Resolve(new Playlist(slot).RevisionId);

    /// <summary>Every membership fact the edge holds, in order, as text.</summary>
    static (string Uri, string ItemId, int AddedAt, string AddedBy, byte Chart, ushort Pos, ushort Prev, byte Flags)[] Members(int slot)
    {
        Scope scope = Entities.Current;
        var p = new Playlist(slot);
        ReadOnlySpan<int> targets = p.TrackSlots;
        ReadOnlySpan<PlaylistTrackEdge> edges = p.TrackEdges;
        var rows = new (string, string, int, string, byte, ushort, ushort, byte)[targets.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            PlaylistTrackEdge e = edges[i];
            rows[i] = (scope.Tracks.Id[targets[i]].Text, Entities.Strings.Resolve(e.ItemId), e.AddedAt,
                       e.AddedBy > Table.None ? scope.Users.Id[e.AddedBy].Text : "", e.ChartStatus, e.ChartPos, e.ChartPrev, e.Flags);
        }
        return rows;
    }

    static (string Uri, string? ItemId)[] Keys(IEnumerable<ListRow> rows) => rows.Select(r => (r.Uri, r.ItemId)).ToArray();

    static ListRow Member(string uri, string? itemId) => new(uri, itemId, 0, null, 0, 0, 0, 0, RootlistKind.Item, 0, 0, null, null);

    static byte[] Revision24(int counter)
    {
        var revision = new byte[24];
        revision[3] = (byte)counter;
        for (int i = 4; i < 24; i++) revision[i] = (byte)i;
        return revision;
    }

    static string Spelled(byte[] revision)
    {
        Span<char> chars = stackalloc char[128];
        return new string(chars[..Spotify.Api.FormatRevision(revision, chars)]);
    }

    // ── applied: the replayed whole list lands exactly like a read ───────────────────────────────────────────────────

    /// <summary>THE ARM: the real rev-6 read held, the captured 6→11 diff replayed over its baseline — the whole list
    /// lands in the derived order with every membership fact (the kept rows' own, the added rows' off the diff: item id,
    /// adder as a user uri, instant in seconds), `to` is the revision held now, the count moved to 6 and is known.</summary>
    [Fact]
    public void A_diff_with_ops_lands_the_replayed_whole_list_at_its_to_revision()
    {
        TestScope.Fresh();
        string uri = ListAnswers.PlaylistUri(9601);
        int slot = LandRead(uri, Bytes("p2-read-r6.bin"));
        PlaylistOpsFixtures.Read("p2-read-r6.bin", out string r6);
        Assert.Equal(r6, Held(slot));
        var before = Members(slot);

        ListRow[] baseline = Store.SnapshotList(Entities.Current, EdgeRelation.PlaylistTracks, slot)!;
        Assert.NotNull(baseline);
        Assert.Equal(before.Select(m => (m.Uri, (string?)m.ItemId)).ToArray(), Keys(baseline));
        var outcome = ListReplay.Decide(false, Bytes("p2-diff-r6-r11.bin"), r6, baseline);

        Assert.Equal(ListReplay.Verdict.Applied, outcome.Verdict);
        Assert.Equal("applied", ListReplay.VerdictText(in outcome));
        Assert.Matches(WireRevision, outcome.Answer.To!);
        Assert.Equal(new PlaylistOps.Tally(Added: 3, Removed: 1, Moved: 1, ItemUpdates: 0, ListUpdates: 0), outcome.Tally);
        Assert.Equal(PlaylistOpsFixtures.Keys(PlaylistOpsFixtures.Expected("p2-r11.expected.txt")), Keys(outcome.Rows!));
        Assert.Equal(4, baseline.Length);                                        // the baseline itself never moves

        LandReplay(uri, in outcome);
        var p = new Playlist(slot);
        Assert.Equal(EdgeState.Complete, p.MembershipState);
        Assert.Equal(6, p.MembershipTotal);
        Assert.Equal(6, p.TrackCount);
        Assert.True(p.Knows(PlaylistFields.TrackCount));
        Assert.Equal(outcome.Answer.To, Held(slot));

        var after = Members(slot);
        Assert.Equal(PlaylistOpsFixtures.Keys(PlaylistOpsFixtures.Expected("p2-r11.expected.txt")),
                     after.Select(m => (m.Uri, (string?)m.ItemId)).ToArray());
        foreach (var kept in after.Take(3))                                      // a kept row keeps every fact it had
            Assert.Equal(before.Single(b => b.ItemId == kept.ItemId), kept);
        PlaylistOps.WireItem[] added = outcome.Answer.Batch.Ops.Where(o => o.Kind == PlaylistOps.Kind.Add)
                                              .Select(o => outcome.Answer.Batch.Items[o.ItemsStart]).ToArray();
        for (int i = 0; i < added.Length; i++)
        {
            var row = after[3 + i];
            Assert.Equal(added[i].Uri, row.Uri);
            Assert.Equal(added[i].ItemId, row.ItemId);
            Assert.Equal(Seconds(added[i].Timestamp), row.AddedAt);
            Assert.Equal("spotify:user:" + added[i].AddedBy, row.AddedBy);
        }

        var hydrate = new List<string>();
        outcome.Answer.Batch.AddedUris(hydrate);                                 // the only rows the planner has to fill
        Assert.Equal(after.Skip(3).Select(m => m.Uri).ToArray(), hydrate.ToArray());
    }

    /// <summary>The chain chains: the baseline the 6→11 replay left is what 11→14 replays over, and the diff's header op
    /// (a rename + a first description, §3.10) lands in the SAME commit as the rows.</summary>
    [Fact]
    public void A_chained_diff_replays_again_and_its_header_op_lands_in_the_same_commit()
    {
        TestScope.Fresh();
        string uri = ListAnswers.PlaylistUri(9602);
        int slot = LandRead(uri, Bytes("p2-read-r6.bin"));
        LandReplay(uri, ListReplay.Decide(false, Bytes("p2-diff-r6-r11.bin"), Held(slot),
                                          Store.SnapshotList(Entities.Current, EdgeRelation.PlaylistTracks, slot)));

        var outcome = ListReplay.Decide(false, Bytes("p2-diff-r11-r14.bin"), Held(slot),
                                        Store.SnapshotList(Entities.Current, EdgeRelation.PlaylistTracks, slot));
        Assert.Equal(ListReplay.Verdict.Applied, outcome.Verdict);
        Assert.Equal("Playlist C", outcome.Attrs.Name);
        LandReplay(uri, in outcome);

        var p = new Playlist(slot);
        Assert.Equal(PlaylistOpsFixtures.Keys(PlaylistOpsFixtures.Expected("p2-r14.expected.txt")),
                     Members(slot).Select(m => (m.Uri, (string?)m.ItemId)).ToArray());
        Assert.Equal(4, p.TrackCount);
        Assert.Equal(outcome.Answer.To, Held(slot));
        Assert.Equal("Playlist C", Entities.Strings.Resolve(p.TitleId));
        Assert.Equal("Description A", Entities.Strings.Resolve(p.DescriptionId));
    }

    /// <summary>A header op that UNSETS the description (its <c>no_value</c>) lands it as absent — never an empty
    /// string (§3.10) — beside the new name.</summary>
    [Fact]
    public void An_unset_attribute_lands_as_absent()
    {
        TestScope.Fresh();
        string uri = ListAnswers.PlaylistUri(9603);
        int slot = LandRead(uri, Bytes("p2-read-r6.bin"));
        LandReplay(uri, ListReplay.Decide(false, Bytes("p2-diff-r6-r11.bin"), Held(slot),
                                          Store.SnapshotList(Entities.Current, EdgeRelation.PlaylistTracks, slot)));
        var body = Pl.SelectedListContent.Parser.ParseFrom(Bytes("p2-diff-r11-r14.bin"));
        var change = body.Diff.Ops[2].UpdateListAttributes.NewAttributes;
        change.Values = new Pl.ListAttributes { Name = "Renamed" };
        change.NoValue.Add(Pl.ListAttributeKind.ListDescription);

        var outcome = ListReplay.Decide(false, body.ToByteArray(), Held(slot),
                                        Store.SnapshotList(Entities.Current, EdgeRelation.PlaylistTracks, slot));
        Assert.Equal(ListReplay.Verdict.Applied, outcome.Verdict);
        Assert.Equal(PlaylistOps.ListAttrs.Description, outcome.Attrs.Unset);
        LandReplay(uri, in outcome);

        var p = new Playlist(slot);
        Assert.Equal("Renamed", Entities.Strings.Resolve(p.TitleId));
        Assert.True(p.DescriptionId.IsEmpty);
    }

    /// <summary>"Exactly like a read", against a real read of the result: the r36 push's ADD at 123, decided as a diff
    /// over the real r35 read's baseline and landed, holds the very membership the real r36 read lands — every fact of
    /// all 199 rows — at the same revision.</summary>
    [Fact]
    public void A_replay_over_the_real_r35_read_lands_exactly_what_the_real_r36_read_lands()
    {
        TestScope.Fresh();
        string uri = ListAnswers.PlaylistUri(9604);
        int slot = LandRead(uri, Bytes("p1-read-r35.bin"));
        var push = PlaylistOps.DecodePush(Bytes("p1-push-r36-add-123.bin"));
        var answer = new PlaylistOps.DiffAnswer(PlaylistOps.Answer.Replay, PlaylistOps.Refusal.None, push.ParentRevision,
                                                push.NewRevision, push.Batch);
        var outcome = ListReplay.Decide(false, in answer, Held(slot),
                                        Store.SnapshotList(Entities.Current, EdgeRelation.PlaylistTracks, slot));
        Assert.Equal(ListReplay.Verdict.Applied, outcome.Verdict);
        LandReplay(uri, in outcome);
        var replayed = Members(slot);
        string replayedAt = Held(slot);

        LandRead(uri, Bytes("p1-read-r36.bin"));
        Assert.Equal(199, replayed.Length);
        Assert.Equal(Members(slot), replayed);
        Assert.Equal(Held(slot), replayedAt);
    }

    /// <summary>The ROOTLIST over its whole wire stream: the real 135 read held, the 135→137 diff (ONE ADD of a
    /// start+end marker pair, then a MOV between them) replayed — markers, folder ids and names, recomputed depths, wire
    /// positions, targets and instants are exactly what the real 137 read lands, at the same revision.</summary>
    [Fact]
    public void The_rootlist_135_to_137_replay_lands_exactly_what_the_real_137_read_lands()
    {
        TestScope.Fresh();
        Scope scope = Entities.Current;
        const string me = "spotify:user:user1";
        LandRootlist(me, Bytes("rootlist-read-r135.bin"));
        int parent = scope.Users.Slot(me.AsSpan());
        string held = Entities.Strings.Resolve(scope.Edges.RootlistRevision(parent));
        PlaylistOpsFixtures.Read("rootlist-read-r135.bin", out string r135);
        Assert.Equal(r135, held);

        ListRow[] baseline = Store.SnapshotList(scope, EdgeRelation.Rootlist, parent)!;
        Assert.Equal(38, baseline.Length);
        var outcome = ListReplay.Decide(true, Bytes("rootlist-diff-r135-r137.bin"), held, baseline);
        Assert.Equal(ListReplay.Verdict.Applied, outcome.Verdict);
        Assert.Equal(40, outcome.Rows!.Length);
        Assert.Equal("ADD,MOV", ListReplay.Kinds(outcome.Answer.Batch));

        var s = Staging.Rent();
        Assert.True(Spotify.Decode.RootlistReplay(outcome.Rows, me, outcome.Answer.To!, s));
        TestScope.CommitAndPublish(s);
        var replayed = Stream(scope, parent);
        Assert.Equal(outcome.Answer.To, Entities.Strings.Resolve(scope.Edges.RootlistRevision(parent)));
        Assert.Equal((byte)RootlistKind.FolderStart, replayed[0].Kind);
        Assert.Equal((byte)1, replayed[1].Depth);                                // the moved playlist sits inside the new folder
        Assert.Equal((byte)RootlistKind.FolderEnd, replayed[2].Kind);

        LandRootlist(me, Bytes("rootlist-read-r137.bin"));
        Assert.Equal(Stream(scope, parent), replayed);
        Assert.Equal(outcome.Answer.To, Entities.Strings.Resolve(scope.Edges.RootlistRevision(parent)));
    }

    static void LandRootlist(string me, byte[] body)
    {
        var s = Staging.Rent();
        Spotify.Decode.Rootlist(body, Utf8(me), s);
        TestScope.CommitAndPublish(s);
    }

    static (byte Kind, byte Depth, ushort Position, string Folder, string Name, string Target, int AddedAt)[] Stream(Scope scope, int parent)
    {
        ReadOnlySpan<int> targets = scope.Edges.Rootlist.Targets(parent);
        ReadOnlySpan<RootlistEdge> payload = scope.Edges.Rootlist.Payload(parent);
        var rows = new (byte, byte, ushort, string, string, string, int)[payload.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            RootlistEdge e = payload[i];
            rows[i] = (e.Kind, e.Depth, e.Position, Entities.Strings.Resolve(e.FolderId), Entities.Strings.Resolve(e.FolderName),
                       targets[i] > Table.None ? scope.Playlists.Id[targets[i]].Text : "", e.AddedAt);
        }
        return rows;
    }

    // ── full read: every guard, and nothing staged ───────────────────────────────────────────────────────────────────

    static ListRow[] R6Baseline(out string r6)
        => PlaylistOpsFixtures.Read("p2-read-r6.bin", out r6).Select(w => Member(w.Uri, w.ItemId)).ToArray();

    static ListRow[] R11Baseline(out string r11)
    {
        r11 = PlaylistOps.DecodeDiff(Bytes("p2-diff-r6-r11.bin")).To!;
        return PlaylistOpsFixtures.Expected("p2-r11.expected.txt").Select(w => Member(w.Uri, w.ItemId)).ToArray();
    }

    static void AssertFullRead(in ListReplay.Outcome outcome, string why)
    {
        Assert.Equal(ListReplay.Verdict.FullRead, outcome.Verdict);
        Assert.Equal(why, outcome.Why);
        Assert.Null(outcome.Rows);                                               // nothing to stage
        Assert.Equal("fullread:" + why, ListReplay.VerdictText(in outcome));
    }

    [Fact]
    public void A_from_that_is_not_the_revision_held_is_a_full_read()
    {
        ListRow[] baseline = R6Baseline(out _);
        AssertFullRead(ListReplay.Decide(false, Bytes("p2-diff-r6-r11.bin"), ListAnswers.RevisionA, baseline), ListReplay.FromMismatch);
    }

    [Fact]
    public void Without_a_baseline_ops_are_a_full_read()
    {
        R6Baseline(out string r6);
        AssertFullRead(ListReplay.Decide(false, Bytes("p2-diff-r6-r11.bin"), r6, null), ListReplay.NoBaseline);
    }

    /// <summary>Field 20 is read first and nothing else gets a vote — not the ops beside it, not a baseline that fits.</summary>
    [Fact]
    public void A_resync_flagged_answer_is_a_full_read_whatever_it_carries()
    {
        ListRow[] baseline = R6Baseline(out string r6);
        var body = Pl.SelectedListContent.Parser.ParseFrom(Bytes("p2-diff-r6-r11.bin"));
        body.ChangesRequireResync = true;
        AssertFullRead(ListReplay.Decide(false, body.ToByteArray(), r6, baseline), nameof(PlaylistOps.Refusal.Resync));
        AssertFullRead(ListReplay.Decide(false, Bytes("p1-changes-r35-resync.bin"), r6, baseline), nameof(PlaylistOps.Refusal.Resync));
    }

    /// <summary>A misfit names its refusal, stages nothing, and leaves the baseline exactly as it was: here the REM's
    /// carried row is not the row the (reordered) baseline holds at its index.</summary>
    [Fact]
    public void A_misfit_is_a_full_read_named_by_its_refusal_and_the_baseline_is_untouched()
    {
        ListRow[] baseline = R6Baseline(out string r6);
        (baseline[2], baseline[3]) = (baseline[3], baseline[2]);
        ListRow[] copy = baseline.ToArray();
        AssertFullRead(ListReplay.Decide(false, Bytes("p2-diff-r6-r11.bin"), r6, baseline), nameof(PlaylistOps.Refusal.IdentityMismatch));
        Assert.Equal(copy, baseline);
    }

    /// <summary>Our own ADD replayed over a list that already holds it — the list is not the one the ops were computed
    /// against — refuses rather than landing a duplicate.</summary>
    [Fact]
    public void Ops_replayed_over_a_list_they_already_describe_refuse()
    {
        ListRow[] r11 = R11Baseline(out _);
        R6Baseline(out string r6);
        var outcome = ListReplay.Decide(false, Bytes("p2-diff-r6-r11.bin"), r6, r11);
        Assert.Equal(ListReplay.Verdict.FullRead, outcome.Verdict);
        Assert.Null(outcome.Rows);
    }

    /// <summary>A header op that names ONE attribute and says nothing of the other cannot land as the full read's
    /// Identity group does without guessing it: read in full.</summary>
    [Fact]
    public void A_header_op_that_states_only_one_attribute_is_a_full_read()
    {
        ListRow[] baseline = R11Baseline(out string r11);
        var body = Pl.SelectedListContent.Parser.ParseFrom(Bytes("p2-diff-r11-r14.bin"));
        body.Diff.Ops[2].UpdateListAttributes.NewAttributes.Values = new Pl.ListAttributes { Name = "Renamed" };
        AssertFullRead(ListReplay.Decide(false, body.ToByteArray(), r11, baseline), ListReplay.Header);
    }

    [Fact]
    public void A_to_revision_that_is_malformed_or_did_not_move_is_a_full_read()
    {
        ListRow[] baseline = R6Baseline(out string r6);
        var batch = PlaylistOps.DecodeDiff(Bytes("p2-diff-r6-r11.bin")).Batch;
        var malformed = new PlaylistOps.DiffAnswer(PlaylistOps.Answer.Replay, PlaylistOps.Refusal.None, r6, "7,0123", batch);
        var still = new PlaylistOps.DiffAnswer(PlaylistOps.Answer.Replay, PlaylistOps.Refusal.None, r6, r6, batch);
        AssertFullRead(ListReplay.Decide(false, in malformed, r6, baseline), ListReplay.ToRevision);
        AssertFullRead(ListReplay.Decide(false, in still, r6, baseline), ListReplay.ToRevision);
    }

    /// <summary>A rootlist op addresses the WIRE stream: a baseline with a skipped non-playlist item (positions 0, 2)
    /// cannot be replayed over.</summary>
    [Fact]
    public void A_rootlist_whose_rows_are_not_at_their_wire_positions_is_a_full_read()
    {
        ListRow[] baseline =
        [
            ListAnswers.Item(0, 0, ListAnswers.PlaylistUri(9611)),
            ListAnswers.Item(2, 0, ListAnswers.PlaylistUri(9612)),
        ];
        var answer = new PlaylistOps.DiffAnswer(PlaylistOps.Answer.Replay, PlaylistOps.Refusal.None, ListAnswers.RevisionA,
            ListAnswers.RevisionB, new PlaylistOps.Batch([PlaylistOps.Op.Move(0, 1, 2)], [], []));
        AssertFullRead(ListReplay.Decide(true, in answer, ListAnswers.RevisionA, baseline), ListReplay.RootlistPositions);
    }

    /// <summary>A folder marker is identified the way its uri would be — kind, group id, and (for a start marker) the
    /// decoded name — and depths are re-derived after the replay: removing a folder's start leaves its playlist at the
    /// top level and its end marker clamped at 0, exactly as a read of that stream would.</summary>
    [Fact]
    public void A_rootlist_marker_is_identified_by_kind_group_and_name()
    {
        string a = ListAnswers.PlaylistUri(9621);
        ListRow[] baseline =
        [
            ListAnswers.FolderStart(0, 0, "a1b2c3d4e5f60718", "Work out"),
            ListAnswers.Item(1, 1, a),
            ListAnswers.FolderEnd(2, 0, "a1b2c3d4e5f60718"),
        ];
        PlaylistOps.DiffAnswer Rem(string markerUri) => new(PlaylistOps.Answer.Replay, PlaylistOps.Refusal.None,
            ListAnswers.RevisionA, ListAnswers.RevisionB,
            new PlaylistOps.Batch([PlaylistOps.Op.Remove(0, 1, 0)], [new PlaylistOps.WireItem(markerUri)], []));

        AssertFullRead(ListReplay.Decide(true, Rem("spotify:start-group:a1b2c3d4e5f60718:Other"), ListAnswers.RevisionA, baseline),
                       nameof(PlaylistOps.Refusal.IdentityMismatch));
        AssertFullRead(ListReplay.Decide(true, Rem("spotify:start-group:ffffffffffffffff:Work+out"), ListAnswers.RevisionA, baseline),
                       nameof(PlaylistOps.Refusal.IdentityMismatch));

        var outcome = ListReplay.Decide(true, Rem("spotify:start-group:a1b2c3d4e5f60718:Work+out"), ListAnswers.RevisionA, baseline);
        Assert.Equal(ListReplay.Verdict.Applied, outcome.Verdict);
        ListRow[] rows = outcome.Rows!;
        Assert.Equal(2, rows.Length);
        Assert.Equal((RootlistKind.Item, (byte)0, (ushort)0, a), (rows[0].Kind, rows[0].Depth, rows[0].WirePos, rows[0].Uri));
        Assert.Equal((RootlistKind.FolderEnd, (byte)0, (ushort)1), (rows[1].Kind, rows[1].Depth, rows[1].WirePos));
    }

    /// <summary>A rootlist ADD of something the rootlist decoder would skip (not a playlist, not a marker) would shift
    /// every later wire position off ours: read in full.</summary>
    [Fact]
    public void A_rootlist_add_of_a_row_the_decoder_skips_is_a_full_read()
    {
        ListRow[] baseline = [ListAnswers.Item(0, 0, ListAnswers.PlaylistUri(9631))];
        var answer = new PlaylistOps.DiffAnswer(PlaylistOps.Answer.Replay, PlaylistOps.Refusal.None, ListAnswers.RevisionA,
            ListAnswers.RevisionB,
            new PlaylistOps.Batch([PlaylistOps.Op.AddAt(0, 0, 1)], [new PlaylistOps.WireItem(ListAnswers.TrackUri(9632))], []));
        AssertFullRead(ListReplay.Decide(true, in answer, ListAnswers.RevisionA, baseline), ListReplay.RootlistItem);
    }

    // ── unchanged and contents: the four answer shapes (the retired DiffVerdict's cases, re-pinned) ──────────────────

    [Fact]
    public void The_unchanged_shapes_stand_only_for_the_revision_held()
    {
        ListRow[] baseline = R11Baseline(out string r11);
        Assert.Equal(ListReplay.Verdict.Unchanged, ListReplay.Decide(false, [], r11, baseline).Verdict);                  // the bare 304
        Assert.Equal(ListReplay.Verdict.Unchanged,
                     ListReplay.Decide(false, new Pl.SelectedListContent { UpToDate = true }.ToByteArray(), r11, baseline).Verdict);
        var trivial = ListReplay.Decide(false, Bytes("p2-diff-r11-r11-empty.bin"), r11, baseline);
        Assert.Equal(ListReplay.Verdict.Unchanged, trivial.Verdict);
        Assert.Equal("unchanged", ListReplay.VerdictText(in trivial));
        Assert.Null(trivial.Rows);

        // A trivial diff about ANOTHER revision vouches for nothing we hold.
        R6Baseline(out string r6);
        AssertFullRead(ListReplay.Decide(false, Bytes("p2-diff-r11-r11-empty.bin"), r6, baseline), ListReplay.FromMismatch);
    }

    [Fact]
    public void A_contents_answer_is_contents_only_with_its_revision_and_never_beside_resync_or_a_diff()
    {
        byte[] r1 = Revision24(1);
        string held = Spelled(Revision24(0));
        var clean = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(r1), Length = 50, Contents = new Pl.ListItems { Pos = 0, Truncated = false },
        };
        var contents = ListReplay.Decide(false, clean.ToByteArray(), held, null);
        Assert.Equal(ListReplay.Verdict.Contents, contents.Verdict);
        Assert.Equal("contents", ListReplay.VerdictText(in contents));
        Assert.Equal(ListReplay.Verdict.Contents,
                     ListReplay.Decide(false, Bytes("listen-later-diff-contents.bin"), held, null).Verdict);

        clean.ClearRevision();                                                   // rows with no revision would sit under the old one
        AssertFullRead(ListReplay.Decide(false, clean.ToByteArray(), held, null), ListReplay.NoRevision);

        string resync = nameof(PlaylistOps.Refusal.Resync);                      // bug A1's shape: never a trusted snapshot
        AssertFullRead(ListReplay.Decide(false, new Pl.SelectedListContent { ChangesRequireResync = true }.ToByteArray(), held, null), resync);
        AssertFullRead(ListReplay.Decide(false, new Pl.SelectedListContent
        {
            ChangesRequireResync = true, Length = 0, Revision = ByteString.CopyFrom(r1),
            Contents = new Pl.ListItems { Pos = 0, Truncated = false },
        }.ToByteArray(), held, null), resync);
        AssertFullRead(ListReplay.Decide(false, new Pl.SelectedListContent { ChangesRequireResync = true, UpToDate = true }.ToByteArray(),
                                         held, null), resync);

        var both = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(r1), Contents = new Pl.ListItems { Pos = 0, Truncated = false },
            Diff = new Pl.Diff { FromRevision = ByteString.CopyFrom(r1), ToRevision = ByteString.CopyFrom(r1) },
        };
        AssertFullRead(ListReplay.Decide(false, both.ToByteArray(), held, null), nameof(PlaylistOps.Refusal.Contradictory));
    }

    // ── the list decode: the revision rides the rows ─────────────────────────────────────────────────────────────────

    /// <summary>Wave D2's finding, fixed: a <c>/diff</c> answered with <c>contents</c> and NO attributes lands its rows
    /// under ITS revision, not the one held before — so the next <c>/diff</c> is asked from the list those rows are.</summary>
    [Fact]
    public void Contents_without_attributes_still_stage_their_revision()
    {
        TestScope.Fresh();
        string uri = ListAnswers.PlaylistUri(9641);
        var first = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(Revision24(1)),
            Attributes = new Pl.ListAttributes { Name = "A list" },
            Contents = new Pl.ListItems { Pos = 0, Truncated = false },
        };
        first.Contents.Items.Add(new Pl.Item { Uri = ListAnswers.TrackUri(96411) });
        int slot = LandRead(uri, first.ToByteArray());
        Assert.Equal(Spelled(Revision24(1)), Held(slot));

        var bare = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(Revision24(2)),
            Contents = new Pl.ListItems { Pos = 0, Truncated = false },
        };
        bare.Contents.Items.Add(new Pl.Item { Uri = ListAnswers.TrackUri(96412) });
        bare.Contents.Items.Add(new Pl.Item { Uri = ListAnswers.TrackUri(96413) });
        LandRead(uri, bare.ToByteArray());

        Assert.Equal(Spelled(Revision24(2)), Held(slot));
        Assert.Equal(2, new Playlist(slot).TrackSlots.Length);
    }

    /// <summary>The other half of the same rule: an answer with attributes and NO rows says nothing about the membership,
    /// so it does not move the revision the held rows are true at.</summary>
    [Fact]
    public void A_header_only_answer_does_not_move_the_revision()
    {
        TestScope.Fresh();
        string uri = ListAnswers.PlaylistUri(9642);
        var read = new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(Revision24(3)),
            Attributes = new Pl.ListAttributes { Name = "A list" },
            Contents = new Pl.ListItems { Pos = 0, Truncated = false },
        };
        int slot = LandRead(uri, read.ToByteArray());
        LandRead(uri, new Pl.SelectedListContent
        {
            Revision = ByteString.CopyFrom(Revision24(4)),
            Attributes = new Pl.ListAttributes { Name = "A list", Format = "daylist" },
        }.ToByteArray());
        Assert.Equal(Spelled(Revision24(3)), Held(slot));
    }

    // ── the baseline ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>Store.SnapshotList</c> hands the replay a settled whole or nothing: every row as text (a gid member's
    /// uri formatted — the provider has no table to format from), the item id, the adder's uri and the instant; null for
    /// a list nobody answered, a Partial window, and a list with an optimistic row.</summary>
    [Fact]
    public void The_baseline_is_a_settled_whole_as_text_or_nothing()
    {
        TestScope.Fresh();
        Scope scope = Entities.Current;
        string whole = ListAnswers.PlaylistUri(9651), window = ListAnswers.PlaylistUri(9652);
        ListRow[] members =
        [
            ListAnswers.Member(ListAnswers.TrackUri(96511), "0a0b0c0d", 1_700_000_000, "spotify:user:alice", chartStatus: 2, chartPos: 3),
            ListAnswers.Member(ListAnswers.TrackUri(96512), "1a1b1c1d", 1_700_000_100),
        ];
        TestScope.CommitAndPublish(ListAnswers.FullRead(whole, ListAnswers.RevisionA, members));
        TestScope.CommitAndPublish(ListAnswers.FullRead(window, ListAnswers.RevisionA, members, total: 5));
        int wholeSlot = scope.Playlists.Slot(whole.AsSpan()), windowSlot = scope.Playlists.Slot(window.AsSpan());

        ListRow[]? baseline = Store.SnapshotList(scope, EdgeRelation.PlaylistTracks, wholeSlot);
        Assert.NotNull(baseline);
        Assert.Equal(members.Select(m => (m.Uri, m.ItemId, m.AddedAt, m.AddedBy, m.ChartStatus, m.ChartPos)).ToArray(),
                     baseline.Select(m => (m.Uri, m.ItemId, m.AddedAt, m.AddedBy, m.ChartStatus, m.ChartPos)).ToArray());

        Assert.Null(Store.SnapshotList(scope, EdgeRelation.PlaylistTracks, windowSlot));
        Assert.Null(Store.SnapshotList(scope, EdgeRelation.PlaylistTracks, scope.Playlists.Slot(ListAnswers.PlaylistUri(9653).AsSpan())));
        Assert.Null(Store.SnapshotList(scope, EdgeRelation.Liked, scope.MeSlot));

        int track = scope.Tracks.Slot(ListAnswers.TrackUri(96512).AsSpan());
        Assert.True(scope.Edges.PlaylistTracks.MarkRemove(wholeSlot, track));
        Assert.Null(Store.SnapshotList(scope, EdgeRelation.PlaylistTracks, wholeSlot));
    }

    // ── the list.replay line's tokens ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_log_line_names_kinds_once_each_and_the_verdict_with_its_reason()
    {
        Assert.Equal("MOV,REM,ADD", ListReplay.Kinds(PlaylistOps.DecodeDiff(Bytes("p2-diff-r6-r11.bin")).Batch));
        Assert.Equal("MOV,REM,LIST", ListReplay.Kinds(PlaylistOps.DecodeDiff(Bytes("p2-diff-r11-r14.bin")).Batch));
        Assert.Equal("", ListReplay.Kinds(PlaylistOps.Batch.Empty));
        Assert.Equal("fullread:status-509", ListReplay.VerdictText(ListReplay.Refused(ListReplay.StatusReason(509))));
    }
}

/// <summary>The replay arm's write-behind, against a real store over a temp file (the <c>StoreListTests</c> harness): a
/// replayed list is a live answer with a revision beside a whole run, so the gate persists it — rows, the NEW revision
/// and the count in one transaction — and the next launch restores exactly the replayed list, ready to <c>/diff</c> from
/// its <c>to</c>.</summary>
[Collection(EntitiesCollection.Name)]
public class PlaylistReplayPersistenceTests : IDisposable
{
    readonly string _dbPath = Path.Combine(Path.GetTempPath(), "wavee-v3-replay-" + Guid.NewGuid().ToString("n") + ".db");
    readonly ConcurrentQueue<Action> _posted = new();

    public PlaylistReplayPersistenceTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Post = a => _posted.Enqueue(a);
        Store.Register(new PlaylistShape());
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

    Scope Boot(CatalogScope key)
    {
        Entities.Boot(key);
        Store.Flush();
        while (_posted.TryDequeue(out Action? a)) a();
        return Entities.Current;
    }

    static void Land(Staging answer)
    {
        Entities.Commit(answer);
        Assert.True(Store.WriteBehind(answer));
        Store.Flush();
    }

    object? SqlValue(string sql)
    {
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false };
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(cs.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public void A_replayed_list_is_written_behind_at_its_new_revision_and_comes_back_after_a_restart()
    {
        var key = new CatalogScope("spotify", "replay-persist", "en-US", "US", 0, true);
        Scope scope = Boot(key);
        string uri = ListAnswers.PlaylistUri(9701);
        byte[] utf8 = Encoding.UTF8.GetBytes(uri);
        byte[] read = PlaylistOpsFixtures.Bytes("p2-read-r6.bin");
        var s = Staging.Rent();
        Spotify.Decode.PlaylistRevision(read, utf8, s);
        Spotify.Decode.PlaylistFormatAttributes(read, utf8, s);
        Land(s);
        int slot = scope.Playlists.Slot(uri.AsSpan());
        string r6 = Entities.Strings.Resolve(new Playlist(slot).RevisionId);

        var outcome = ListReplay.Decide(false, PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"), r6,
                                        Store.SnapshotList(scope, EdgeRelation.PlaylistTracks, slot));
        Assert.Equal(ListReplay.Verdict.Applied, outcome.Verdict);
        var replay = Staging.Rent();
        Assert.True(Spotify.Decode.PlaylistReplay(outcome.Rows!, uri, outcome.Answer.To!, outcome.Attrs, replay));
        Land(replay);

        Assert.Equal(outcome.Answer.To, SqlValue("SELECT revision FROM list_head;") as string);
        Assert.Equal(6L, SqlValue("SELECT total FROM list_head;") is long total ? total : -1);
        Assert.Equal(6L, SqlValue("SELECT count(*) FROM list_item;") is long rows ? rows : -1);

        Store.Shutdown();
        Store.Use(_dbPath);
        scope = Boot(key);
        int back = scope.Playlists.Slot(uri.AsSpan());
        bool? landed = null;
        Assert.True(Store.ReadList(scope, EdgeRelation.PlaylistTracks, scope.Playlists.Id[back], found => landed = found));
        Store.Flush();
        while (_posted.TryDequeue(out Action? a)) a();
        Assert.True(landed);

        var p = new Playlist(back);
        Assert.Equal(outcome.Answer.To, Entities.Strings.Resolve(p.RevisionId));
        Assert.Equal(PlaylistOpsFixtures.Keys(PlaylistOpsFixtures.Expected("p2-r11.expected.txt")),
                     Enumerable.Range(0, p.TrackSlots.Length)
                               .Select(i => (scope.Tracks.Id[p.TrackSlots[i]].Text, (string?)Entities.Strings.Resolve(p.TrackEdges[i].ItemId)))
                               .ToArray());
    }
}
