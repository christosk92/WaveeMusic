// ── Wavee.Tests/PlaylistDiffDecodeTests.cs — the op decoder, replayed over REAL (scrubbed) baselines ─────────────────
//
// THE FIXTURES are Fixtures/playlist-ops/*.bin: scrubbed re-encodes of the official desktop client's 2026-09 captures
// (README there — which capture, which rule, and that every identifier is synthetic). Structure is the capture's field
// for field; uris, item_ids, usernames, names and revision hashes are synthetic and consistent across files, so the
// from/to chains still chain. The two `.expected.txt` lists are DERIVED (no capture read that revision in full); every
// other expectation here is a real read of the result.
//
// What this suite pins, beside PlaylistOpsTests' in-code matrix: the decoder reads every captured shape; a replay over
// the real baseline lands exactly on the real result (rootlist 134 → 135 → 137, playlist r35 → r36, echo AND keyed
// request); the reorders session's echoes agree with its keyed requests and end on the real r35's rows; a resync-flagged
// answer is never replayed; head-only pushes carry nothing to store; the declared proto fields round-trip byte-exact.

using Google.Protobuf;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// Imported INSIDE the namespace on purpose: the namespace-level `Wavee.Place` (Entities/Concert.cs) would otherwise
// win over the imported `PlaylistOps.Place`.
using static Wavee.PlaylistOps;
using Pl = Wavee.Protocol.Playlist;

/// <summary>The playlist-ops fixture set (README.md beside it).</summary>
static class PlaylistOpsFixtures
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "playlist-ops");

    public static readonly string[] Names =
    [
        "p1-push-r29-mov-0-1-2.bin", "p1-push-r30-mov-3-1-1.bin", "p1-push-r31-rem-1-1.bin", "p1-push-r32-rem-x3.bin",
        "p1-push-r33-mov-5-8-0.bin", "p1-push-r34-mov-x3.bin", "p1-push-r35-head-only.bin", "p1-push-r36-add-123.bin",
        "p1-req-r28-mov-after-item.bin", "p1-req-r29-mov-after-item.bin", "p1-req-r30-rem-keyed.bin",
        "p1-req-r31-rem-keyed-x3.bin", "p1-req-r32-mov-add-first-x8.bin", "p1-req-r33-mov-after-item-x3.bin",
        "p1-req-r35-add-after-item.bin", "p1-changes-r35-resync.bin", "p1-read-r35.bin", "p1-read-r36.bin",
        "p2-read-r6.bin", "p2-diff-r6-r11.bin", "p2-diff-r11-r11-empty.bin", "p2-diff-r11-r14.bin",
        "p2-r11.expected.txt", "p2-r14.expected.txt",
        "rootlist-read-r134.bin", "rootlist-diff-r134-r135.bin", "rootlist-read-r135.bin", "rootlist-diff-r135-r137.bin",
        "rootlist-read-r137.bin", "editorial-push-head-only.bin", "listen-later-diff-contents.bin",
    ];

    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(Dir, name));

    /// <summary>A full read's rows, and the revision it answered at.</summary>
    public static List<WireItem> Read(string name, out string revision)
    {
        Assert.True(TryDecodeContents(Bytes(name), out var items, out int pos, out bool truncated, out string? rev), name);
        Assert.Equal(0, pos);
        Assert.False(truncated);
        Assert.NotNull(rev);
        revision = rev;
        return items.ToList();
    }

    public static List<WireItem> Read(string name) => Read(name, out _);

    /// <summary>A derived expectation: one <c>uri item_id</c> per line.</summary>
    public static List<WireItem> Expected(string name)
        => File.ReadAllLines(Path.Combine(Dir, name))
               .Where(line => line.Length > 0)
               .Select(line => line.Split(' '))
               .Select(parts => new WireItem(parts[0], ItemId: parts[1]))
               .ToList();

    public static Batch Changes(string name, out string? baseRevision)
    {
        Assert.True(TryDecodeChanges(Bytes(name), out var batch, out baseRevision, out var why), name + ": " + why);
        return batch;
    }

    public static Batch Changes(string name) => Changes(name, out _);

    /// <summary>Rows compared the way the replayer's identity reads them: uri + item_id, in order.</summary>
    public static (string Uri, string? ItemId)[] Keys(IEnumerable<WireItem> rows) => rows.Select(r => (r.Uri, r.ItemId)).ToArray();
}

public class PlaylistDiffDecodeTests
{
    const string WireRevision = @"^\d{1,10},[0-9a-f]{40}$";     // the list head's spelling

    static List<WireItem> Replay(List<WireItem> baseline, Batch batch, out ListAttributeChange attrs, out Tally tally)
    {
        var list = baseline.ToList();
        Assert.True(TryApply(list, batch, out attrs, out tally, out var misfit), "misfit at op " + misfit.OpIndex + ": " + misfit.Why);
        Assert.True(tally.Reconciles(baseline.Count, list.Count));
        return list;
    }

    static List<WireItem> Replay(List<WireItem> baseline, Batch batch) => Replay(baseline, batch, out _, out _);

    [Fact]
    public void Every_fixture_is_present()
    {
        foreach (var name in PlaylistOpsFixtures.Names)
            Assert.True(File.Exists(Path.Combine(PlaylistOpsFixtures.Dir, name)), "missing fixture: " + name);
    }

    // ── /diff answers with ops, over real baselines ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Diff_6_to_11_is_one_flat_positional_list_that_replays_over_the_real_rev6_read()
    {
        var baseline = PlaylistOpsFixtures.Read("p2-read-r6.bin", out string r6);
        Assert.Equal(4, baseline.Count);
        var answer = DecodeDiff(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"));

        Assert.Equal(Answer.Replay, answer.Answer);
        Assert.Equal(r6, answer.From);                                            // from_revision == the revision held
        Assert.Matches(WireRevision, answer.To!);
        Assert.Equal(new[] { Kind.Mov, Kind.Rem, Kind.Add, Kind.Add, Kind.Add }, answer.Batch.Ops.Select(o => o.Kind).ToArray());
        Assert.Equal(new Op(Kind.Mov, From: 0, Length: 1, To: 2), answer.Batch.Ops[0]);
        Assert.Equal(new[] { 3, 4, 5 }, answer.Batch.Ops.Skip(2).Select(o => o.From).ToArray());   // sequential ADD indices

        var replayed = Replay(baseline, answer.Batch, out var attrs, out var tally);
        Assert.Equal(PlaylistOpsFixtures.Keys(PlaylistOpsFixtures.Expected("p2-r11.expected.txt")), PlaylistOpsFixtures.Keys(replayed));
        Assert.Equal(new Tally(Added: 3, Removed: 1, Moved: 1, ItemUpdates: 0, ListUpdates: 0), tally);
        Assert.True(attrs.IsEmpty);

        var added = new List<string>();
        answer.Batch.AddedUris(added);                                           // hydrate only these
        Assert.Equal(replayed.Skip(3).Select(r => r.Uri).ToArray(), added.ToArray());
        Assert.All(answer.Batch.Items.Where(i => i.ItemId is not null), i => Assert.Equal(ItemAttrs.AddedBy | ItemAttrs.Timestamp, i.Present));
    }

    [Fact]
    public void Diff_11_to_14_mixes_a_forward_block_mov_a_rem_of_two_and_a_rename()
    {
        var baseline = PlaylistOpsFixtures.Expected("p2-r11.expected.txt");
        var previous = DecodeDiff(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"));
        var answer = DecodeDiff(PlaylistOpsFixtures.Bytes("p2-diff-r11-r14.bin"));

        Assert.Equal(Answer.Replay, answer.Answer);
        Assert.Equal(previous.To, answer.From);                                   // the chain chains
        Op mov = answer.Batch.Ops[0];
        Assert.Equal(new Op(Kind.Mov, From: 0, Length: 3, To: 5), mov);
        Assert.True(mov.To > baseline.Count - mov.Length);                        // only the pre-removal reading is in range
        Assert.Equal(Kind.UpdateListAttributes, answer.Batch.Ops[2].Kind);

        var replayed = Replay(baseline, answer.Batch, out var attrs, out var tally);
        Assert.Equal(PlaylistOpsFixtures.Keys(PlaylistOpsFixtures.Expected("p2-r14.expected.txt")), PlaylistOpsFixtures.Keys(replayed));
        Assert.Equal("Playlist C", attrs.Name);                                   // the synthetic new name
        Assert.Equal("Description A", attrs.Description);                         // the synthetic first description
        Assert.Equal(ListAttrs.None, attrs.Unset);                                // old no_value[DESCRIPTION] is history, not the new state
        Assert.Equal(new Tally(Added: 0, Removed: 2, Moved: 3, ItemUpdates: 0, ListUpdates: 1), tally);
    }

    [Fact]
    public void A_trivial_diff_is_unchanged_and_names_the_revision_held()
    {
        var answer = DecodeDiff(PlaylistOpsFixtures.Bytes("p2-diff-r11-r11-empty.bin"));
        Assert.Equal(Answer.Unchanged, answer.Answer);
        Assert.Equal(answer.From, answer.To);
        Assert.Equal(DecodeDiff(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin")).To, answer.From);
        Assert.Empty(answer.Batch.Ops);
    }

    [Fact]
    public void Rootlist_134_to_135_to_137_replays_onto_the_real_reads_of_both_results()
    {
        var r134 = PlaylistOpsFixtures.Read("rootlist-read-r134.bin", out string rev134);
        var r135 = PlaylistOpsFixtures.Read("rootlist-read-r135.bin", out string rev135);
        var r137 = PlaylistOpsFixtures.Read("rootlist-read-r137.bin", out string rev137);
        Assert.All(r134, row => Assert.Null(row.ItemId));                          // rootlist rows: identity is the uri

        var first = DecodeDiff(PlaylistOpsFixtures.Bytes("rootlist-diff-r134-r135.bin"));
        Assert.Equal(rev134, first.From);
        Assert.Equal(rev135, first.To);
        Assert.Equal(new Op(Kind.Add, From: 0, ItemsStart: 0, ItemsCount: 1), first.Batch.Ops.Single());   // rootlist ADD at 0
        Assert.Equal(PlaylistOpsFixtures.Keys(r135), PlaylistOpsFixtures.Keys(Replay(r134, first.Batch)));

        var second = DecodeDiff(PlaylistOpsFixtures.Bytes("rootlist-diff-r135-r137.bin"));
        Assert.Equal(rev135, second.From);
        Assert.Equal(rev137, second.To);
        Assert.Equal(2, second.Batch.Ops[0].ItemsCount);                           // create folder = ONE ADD of TWO markers
        Assert.StartsWith("spotify:start-group:", second.Batch.Items[0].Uri);
        Assert.StartsWith("spotify:end-group:", second.Batch.Items[1].Uri);
        Assert.Equal(new Op(Kind.Mov, From: 2, Length: 1, To: 1), second.Batch.Ops[1]);
        var replayed = Replay(r135, second.Batch, out _, out var tally);
        Assert.Equal(PlaylistOpsFixtures.Keys(r137), PlaylistOpsFixtures.Keys(replayed));
        Assert.Equal(40, replayed.Count);
        Assert.Equal(2, tally.Added);
    }

    // ── dealer pushes and keyed requests, over real baselines ────────────────────────────────────────────────────────

    [Fact]
    public void Push_r36_add_at_123_over_the_real_r35_read_is_the_real_r36_read()
    {
        var r35 = PlaylistOpsFixtures.Read("p1-read-r35.bin", out string rev35);
        var r36 = PlaylistOpsFixtures.Read("p1-read-r36.bin", out string rev36);
        var push = DecodePush(PlaylistOpsFixtures.Bytes("p1-push-r36-add-123.bin"));

        Assert.True(push.Ok);
        Assert.StartsWith("spotify:playlist:", push.Uri);
        Assert.Equal(rev35, push.ParentRevision);
        Assert.Equal(rev36, push.NewRevision);
        Assert.True(push.HasReplayableOps);
        Assert.Equal(123, push.Batch.Ops.Single().From);
        Assert.Equal(ListPush.Verdict.ApplyInPlace,
            ListPush.Decide(rev35, push.ParentRevision, push.NewRevision!, push.HasReplayableOps, resident: true, pendingLocal: false, open: true));
        Assert.Equal(PlaylistOpsFixtures.Keys(r36), PlaylistOpsFixtures.Keys(Replay(r35, push.Batch)));
    }

    [Fact]
    public void The_keyed_request_behind_that_push_lands_on_the_same_real_r36_read()
    {
        // reorders.saz request 511: ADD{items, add_after_item (field 7)} — the anchor sat at index 122, so 123.
        var r35 = PlaylistOpsFixtures.Read("p1-read-r35.bin", out string rev35);
        var r36 = PlaylistOpsFixtures.Read("p1-read-r36.bin");
        var batch = PlaylistOpsFixtures.Changes("p1-req-r35-add-after-item.bin", out string? baseRevision);

        Assert.Equal(rev35, baseRevision);
        Op add = batch.Ops.Single();
        Assert.Equal(Place.AfterItem, add.Place);
        WireItem anchor = batch.Items[add.Anchor];
        Assert.Equal(122, r35.FindIndex(r => r.ItemId == anchor.ItemId));
        Assert.Equal(PlaylistOpsFixtures.Keys(r36), PlaylistOpsFixtures.Keys(Replay(r35, batch)));
    }

    [Fact]
    public void The_reorders_echoes_agree_with_their_keyed_requests_and_end_on_the_real_r35_rows()
    {
        // The service echoes every keyed edit positionally. Over ONE state, each echo and the request it echoes must land
        // the same rows — and the pre-removal MOV rule plus sequential indices must carry the state through all six edits
        // (four of them index REMs whose carried rows are checked one for one). The r28 state is the rows the edits
        // touch, named by the requests themselves, plus the two rows between them (the real r35's rows 11 and 12); the
        // edits never index past it, and the result must be the real r35 read's first 13 rows.
        var r35 = PlaylistOpsFixtures.Read("p1-read-r35.bin");
        var m = PlaylistOpsFixtures.Changes("p1-req-r28-mov-after-item.bin");
        var q = PlaylistOpsFixtures.Changes("p1-req-r29-mov-after-item.bin");
        var rems = PlaylistOpsFixtures.Changes("p1-req-r31-rem-keyed-x3.bin");
        var first8 = PlaylistOpsFixtures.Changes("p1-req-r32-mov-add-first-x8.bin");
        var after3 = PlaylistOpsFixtures.Changes("p1-req-r33-mov-after-item-x3.bin");

        WireItem moved = m.Items[0], anchor = m.Items[m.Ops[0].Anchor], second = q.Items[0];
        WireItem[] r = first8.Items[..8];
        WireItem z = after3.Items[2];
        var state = new List<WireItem>
        {
            moved, anchor, rems.Items[0], second, z, r35[11], rems.Items[1], rems.Items[2], r35[12],
            r[0], r[1], r[2], r[3], r[4], rems.Items[3], r[5], r[6], r[7],
        };

        (string Push, string Request)[] edits =
        [
            ("p1-push-r29-mov-0-1-2.bin", "p1-req-r28-mov-after-item.bin"),
            ("p1-push-r30-mov-3-1-1.bin", "p1-req-r29-mov-after-item.bin"),
            ("p1-push-r31-rem-1-1.bin", "p1-req-r30-rem-keyed.bin"),
            ("p1-push-r32-rem-x3.bin", "p1-req-r31-rem-keyed-x3.bin"),
            ("p1-push-r33-mov-5-8-0.bin", "p1-req-r32-mov-add-first-x8.bin"),
            ("p1-push-r34-mov-x3.bin", "p1-req-r33-mov-after-item-x3.bin"),
        ];
        string? held = null;
        foreach (var (pushName, requestName) in edits)
        {
            var push = DecodePush(PlaylistOpsFixtures.Bytes(pushName));
            Assert.True(push.HasReplayableOps, pushName);
            if (held is not null) Assert.Equal(held, push.ParentRevision);       // the pushes chain r28 → r34
            held = push.NewRevision;

            var echoed = Replay(state, push.Batch);
            var keyed = Replay(state, PlaylistOpsFixtures.Changes(requestName));
            Assert.Equal(PlaylistOpsFixtures.Keys(keyed), PlaylistOpsFixtures.Keys(echoed));
            state = echoed;
        }
        Assert.Equal(PlaylistOpsFixtures.Keys(r35.Take(state.Count)), PlaylistOpsFixtures.Keys(state));
    }

    [Theory]
    [InlineData("p1-push-r35-head-only.bin")]            // the 50-row bulk ADD's push
    [InlineData("editorial-push-head-only.bin")]         // an editorial daily refresh (counter 0)
    public void A_head_only_push_carries_no_ops_and_no_parent_so_its_head_is_never_stored(string name)
    {
        var push = DecodePush(PlaylistOpsFixtures.Bytes(name));
        Assert.True(push.Ok);
        Assert.Null(push.ParentRevision);
        Assert.Empty(push.Batch.Ops);
        Assert.False(push.HasReplayableOps);
        Assert.Matches(WireRevision, push.NewRevision!);
        Assert.Equal(ListPush.Verdict.MarkDirty,
            ListPush.Decide("1,0000000000000000000000000000000000000000", push.ParentRevision, push.NewRevision!, push.HasReplayableOps,
                resident: true, pendingLocal: false, open: false));
    }

    // ── answers that are never replayed ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_resync_flagged_changes_answer_is_never_replayed()
    {
        var answer = DecodeDiff(PlaylistOpsFixtures.Bytes("p1-changes-r35-resync.bin"));
        Assert.Equal(Answer.FullRead, answer.Answer);
        Assert.Equal(Refusal.Resync, answer.Why);
        Assert.Empty(answer.Batch.Ops);
    }

    [Fact]
    public void The_resync_flag_wins_even_beside_a_diff_with_ops()
    {
        // Bug A1's shape, with ops: field 20 is read first and nothing else gets a vote.
        var body = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"));
        body.ChangesRequireResync = true;
        var answer = DecodeDiff(body.ToByteArray());
        Assert.Equal(Answer.FullRead, answer.Answer);
        Assert.Equal(Refusal.Resync, answer.Why);
    }

    [Fact]
    public void A_diff_answering_full_contents_is_contents()
    {
        byte[] body = PlaylistOpsFixtures.Bytes("listen-later-diff-contents.bin");
        Assert.Equal(Answer.Contents, DecodeDiff(body).Answer);
        Assert.True(TryDecodeContents(body, out var items, out int pos, out bool truncated, out _));
        Assert.Equal(0, pos);
        Assert.False(truncated);
        Assert.Equal(2, items.Length);
        Assert.All(items, i => Assert.StartsWith("spotify:episode:", i.Uri));
    }

    [Fact]
    public void An_empty_body_is_the_bare_304_and_says_unchanged()
        => Assert.Equal(Answer.Unchanged, DecodeDiff(ReadOnlySpan<byte>.Empty).Answer);

    [Fact]
    public void A_truncated_answer_is_refused_where_a_full_read_decode_would_keep_what_it_got()
    {
        byte[] body = PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin");
        var answer = DecodeDiff(body.AsSpan(0, body.Length / 2));                // cuts the diff (field 6) mid-op
        Assert.Equal(Answer.FullRead, answer.Answer);
        Assert.Equal(Refusal.Malformed, answer.Why);
        Assert.False(DecodePush(PlaylistOpsFixtures.Bytes("p1-push-r32-rem-x3.bin").AsSpan(0, 100)).Ok);
    }

    [Fact]
    public void A_zero_op_diff_that_advances_the_revision_is_not_adopted()
    {
        var body = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"));
        body.Diff.Ops.Clear();
        var answer = DecodeDiff(body.ToByteArray());
        Assert.Equal(Answer.FullRead, answer.Answer);
        Assert.Equal(Refusal.Unobserved, answer.Why);
    }

    [Fact]
    public void An_op_field_this_build_does_not_name_is_refused()
    {
        // Field 1000 is Wavee's own outbox field — never on the wire, so a decoder that met it would be guessing.
        var body = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"));
        body.Diff.Ops[2].Add.WaveeAnchorItemId = "0011223344556677";
        var answer = DecodeDiff(body.ToByteArray());
        Assert.Equal(Answer.FullRead, answer.Answer);
        Assert.Equal(Refusal.UnknownField, answer.Why);
    }

    [Fact]
    public void Add_before_item_is_refused_until_a_capture_shows_it()
    {
        var add = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"));
        var addOp = add.Diff.Ops[2].Add;
        addOp.ClearFromIndex();
        addOp.AddBeforeItem = new Pl.Item { Uri = add.Diff.Ops[1].Rem.Items[0].Uri };
        Assert.Equal(Refusal.Unobserved, DecodeDiff(add.ToByteArray()).Why);

        var mov = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"));
        var movOp = mov.Diff.Ops[0].Mov;
        movOp.ClearFromIndex();
        movOp.ClearLength();
        movOp.ClearToIndex();
        movOp.Items.Add(new Pl.Item { Uri = "spotify:track:x" });
        movOp.AddBeforeItem = new Pl.Item { Uri = "spotify:track:y" };
        Assert.Equal(Refusal.Unobserved, DecodeDiff(mov.ToByteArray()).Why);
    }

    [Fact]
    public void An_index_rem_that_carries_fewer_rows_than_it_removes_is_refused_at_decode()
    {
        var body = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p2-diff-r11-r14.bin"));
        body.Diff.Ops[1].Rem.Items.RemoveAt(1);                                  // REM{0,2} now carries one row
        Assert.Equal(Refusal.OpShape, DecodeDiff(body.ToByteArray()).Why);
    }

    [Fact]
    public void An_item_attribute_op_decodes_by_the_rule_and_refuses_what_a_row_cannot_hold()
    {
        // RULE-DERIVED: no capture has an UPDATE_ITEM_ATTRIBUTES. Built from the generated messages.
        var body = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"));
        body.Diff.Ops.Clear();
        body.Diff.Ops.Add(new Pl.Op
        {
            Kind = Pl.Op.Types.Kind.UpdateItemAttributes,
            UpdateItemAttributes = new Pl.UpdateItemAttributes
            {
                Index = 1,
                NewAttributes = new Pl.ItemAttributesPartialState
                {
                    Values = new Pl.ItemAttributes { AddedBy = "user2" },
                    NoValue = { Pl.ItemAttributeKind.ItemTimestamp },
                },
            },
        });
        var answer = DecodeDiff(body.ToByteArray());
        Assert.Equal(Answer.Replay, answer.Answer);
        Op op = answer.Batch.Ops.Single();
        Assert.Equal(new Op(Kind.UpdateItemAttributes, From: 1, ItemsStart: 0, ItemsCount: 1, Set: ItemAttrs.AddedBy, Unset: ItemAttrs.Timestamp), op);

        var baseline = PlaylistOpsFixtures.Read("p2-read-r6.bin");
        var replayed = Replay(baseline, answer.Batch, out _, out var tally);
        Assert.Equal("user2", replayed[1].AddedBy);
        Assert.Equal(ItemAttrs.AddedBy, replayed[1].Present);
        Assert.Equal(1, tally.ItemUpdates);

        body.Diff.Ops[0].UpdateItemAttributes.NewAttributes.Values.SeenAt = 5;   // an attribute no row holds
        Assert.Equal(Refusal.ItemAttribute, DecodeDiff(body.ToByteArray()).Why);
    }

    [Fact]
    public void A_list_attribute_op_on_anything_but_name_or_description_is_refused()
    {
        var body = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p2-diff-r11-r14.bin"));
        body.Diff.Ops[2].UpdateListAttributes.NewAttributes.Values.Collaborative = true;
        Assert.Equal(Refusal.ListAttribute, DecodeDiff(body.ToByteArray()).Why);
    }

    // ── the decoder against the generated parser ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_item_reading_agrees_with_the_generated_parser()
    {
        byte[] bytes = PlaylistOpsFixtures.Bytes("p1-read-r35.bin");
        var generated = Pl.SelectedListContent.Parser.ParseFrom(bytes).Contents.Items
            .Select(i => (i.Uri, i.Attributes is { HasItemId: true } a ? Convert.ToHexStringLower(a.ItemId.Span) : null))
            .ToArray();
        Assert.Equal(generated, PlaylistOpsFixtures.Keys(PlaylistOpsFixtures.Read("p1-read-r35.bin")));
        Assert.Equal(198, generated.Length);
    }

    public static TheoryData<string> Bodies
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in PlaylistOpsFixtures.Names)
                if (name.EndsWith(".bin", StringComparison.Ordinal)) data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Every_fixture_round_trips_byte_exact_through_the_generated_messages(string name)
    {
        // Unknown fields keep round-tripping, and the fields declared from these captures (MetaItem 7/9, ListAttributes
        // 16, ItemAttributes 17, Add 6/7, SelectedListContent 23) parse and re-serialise in place.
        byte[] bytes = PlaylistOpsFixtures.Bytes(name);
        IMessage message = name.Contains("-push", StringComparison.Ordinal) ? Pl.PlaylistModificationInfo.Parser.ParseFrom(bytes)
                         : name.Contains("-req-", StringComparison.Ordinal) ? Pl.ListChanges.Parser.ParseFrom(bytes)
                         : Pl.SelectedListContent.Parser.ParseFrom(bytes);
        Assert.Equal(bytes, message.ToByteArray());
    }

    [Fact]
    public void The_declared_capture_fields_read_what_the_captures_hold()
    {
        var rootlist = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("rootlist-read-r137.bin"));
        Assert.Equal(rootlist.Contents.Items.Count, rootlist.Contents.MetaItems.Count);
        for (int i = 0; i < rootlist.Contents.Items.Count; i++)
        {
            var meta = rootlist.Contents.MetaItems[i];
            bool playlist = rootlist.Contents.Items[i].Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal);
            Assert.Equal(playlist, meta.HasUnknown9);                             // every playlist entry; never a folder marker
            Assert.Equal(playlist, meta.HasUnknown7CapabilitiesShape);
            if (playlist) Assert.Equal(400UL, meta.Unknown9);
        }
        Assert.False(rootlist.HasUnknown23UserOwnedHint);                         // absent on the rootlist

        Assert.Equal(1UL, Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p2-read-r6.bin")).Unknown23UserOwnedHint);
        var listenLater = Pl.SelectedListContent.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("listen-later-diff-contents.bin"));
        Assert.Equal(0UL, listenLater.Unknown23UserOwnedHint);
        Assert.True(listenLater.HasUnknown23UserOwnedHint);
        Assert.Equal(1UL, listenLater.Attributes.Unknown16);

        var keyedAdd = Pl.ListChanges.Parser.ParseFrom(PlaylistOpsFixtures.Bytes("p1-req-r35-add-after-item.bin"));
        Assert.NotNull(keyedAdd.Deltas[0].Ops[0].Add.AddAfterItem);
    }
}
