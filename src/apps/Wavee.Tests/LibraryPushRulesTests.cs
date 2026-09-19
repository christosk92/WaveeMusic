// ── Wavee.Tests/LibraryPushRulesTests.cs — which list a dealer push names, and a push replayed over the held rows ─────
//
// Wave D3 (plan §3.3, owner L3). Two pure halves of the dealer path, both CORE:
//   · `LibraryPushRules` — the topic → the relation, and for `hm://playlist/v2/playlist/{id}` the playlist. The playlist
//     topics here are built from the fixtures' own (scrubbed, synthetic) playlist uris: every playlist topic in the four
//     2026-09 captures is this spelling with a 22-character base62 id.
//   · `ListPushReplay` — a push's ops over a playlist's rows AS THE LIST TABLES HOLD THEM (`ListRow`): the wire row
//     mapped exactly as a full read stages it, so the r35 read + the r36 push lands field for field on the r36 read.
// What a push then DOES (drop / dirty / apply / revalidate) is ListPushTests'; the decoder is PlaylistDiffDecodeTests'.

using System.Text;
using Wavee;
using Xunit;

namespace Wavee.Tests;

using Attrs = Wavee.PlaylistOps.ItemAttrs;
using Refusal = Wavee.PlaylistOps.Refusal;
using WireItem = Wavee.PlaylistOps.WireItem;

public class LibraryPushRulesTests
{
    /// <summary>A scrubbed playlist id from the fixture set (p1's).</summary>
    const string P1 = "2EEbSgmD8JC7LR6pUxlLrj";

    static LibraryPush Classify(string topic) => LibraryPushRules.Classify(Encoding.UTF8.GetBytes(topic));

    static string PlaylistId(string topic) => Encoding.UTF8.GetString(LibraryPushRules.PlaylistId(Encoding.UTF8.GetBytes(topic)));

    /// <summary>A full read's rows as the list tables hold them.</summary>
    static ListRow[] Rows(string fixture, out string revision)
        => PlaylistOpsFixtures.Read(fixture, out revision).Select(w => ListPushReplay.RowOf(w)).ToArray();

    static ListRow[] Rows(string fixture) => Rows(fixture, out _);

    // ── the topic ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hm://collection/collection/bob/json", LibraryPush.Liked | LibraryPush.SavedAlbums)]
    [InlineData("hm://collection/artist/bob", LibraryPush.FollowedArtists)]
    [InlineData("hm://collection/show/bob/json", LibraryPush.SavedShows)]
    [InlineData("hm://collection/ylpin/bob", LibraryPush.Pins)]
    [InlineData("hm://collection/listenlater/bob", LibraryPush.None)]
    [InlineData("hm://playlist/v2/user/bob/rootlist", LibraryPush.Rootlist)]
    [InlineData("hm://playlist/user/bob/rootlist/", LibraryPush.Rootlist)]
    [InlineData("hm://playlist/v2/playlist/" + P1, LibraryPush.Playlist)]
    [InlineData("hm://connect-state/v1/cluster", LibraryPush.None)]
    [InlineData("hm://pusher/v1/connections/abc", LibraryPush.None)]
    public void A_dealer_push_names_its_relation(string topic, LibraryPush expected)
        => Assert.Equal(expected, Classify(topic));

    [Theory]
    [InlineData("hm://playlist/v2/playlist/2EEbSgmD8JC7LR6pUxlLr")]         // 21 characters
    [InlineData("hm://playlist/v2/playlist/2EEbSgmD8JC7LR6pUxlLrjj")]       // 23
    [InlineData("hm://playlist/v2/playlist/2EEbSgmD8JC7LR6pUxlL-j")]        // not base62
    [InlineData("hm://playlist/v2/playlist/" + P1 + "/")]                   // a trailing slash no capture showed
    [InlineData("hm://playlist/v2/playlist/" + P1 + "?x=1")]
    [InlineData("hm://playlist/playlist/" + P1)]                            // the legacy spelling, never observed
    [InlineData("hm://playlist/v2/playlist/")]
    public void A_playlist_topic_that_is_not_exactly_the_captured_shape_names_nothing(string topic)
    {
        Assert.Equal(LibraryPush.None, Classify(topic));
        Assert.Equal("", PlaylistId(topic));
    }

    [Fact]
    public void The_playlist_topic_names_its_id_as_a_slice_of_the_topic()
    {
        Assert.Equal(P1, PlaylistId(LibraryPushRules.PlaylistTopicPrefix + P1));
        Assert.Equal("", PlaylistId("hm://playlist/v2/user/bob/rootlist"));
        Assert.Equal(LibraryPushRules.PlaylistIdChars, P1.Length);
    }

    [Theory]
    [InlineData("p1-push-r29-mov-0-1-2.bin")]
    [InlineData("p1-push-r35-head-only.bin")]
    [InlineData("p1-push-r36-add-123.bin")]
    [InlineData("editorial-push-head-only.bin")]
    public void Every_captured_push_is_routed_by_the_topic_its_own_uri_implies(string fixture)
    {
        var push = PlaylistOps.DecodePush(PlaylistOpsFixtures.Bytes(fixture));
        Assert.True(push.Ok, fixture);
        Assert.StartsWith("spotify:playlist:", push.Uri);
        string id = push.Uri!["spotify:playlist:".Length..];
        string topic = LibraryPushRules.PlaylistTopicPrefix + id;

        Assert.Equal(LibraryPush.Playlist, Classify(topic));
        Assert.Equal(id, PlaylistId(topic));
        Assert.True(ListPushReplay.NamesList(push.Uri, id));
    }

    [Fact]
    public void A_push_body_naming_another_list_is_not_the_topics()
    {
        Assert.True(ListPushReplay.NamesList(null, P1));                                        // the topic routed it
        Assert.True(ListPushReplay.NamesList("spotify:playlist:" + P1, P1));
        Assert.True(ListPushReplay.NamesList("spotify:user:user1:playlist:" + P1, P1));         // the user-namespaced spelling
        Assert.False(ListPushReplay.NamesList("spotify:playlist:2EEbSgmD8JC7LR6pUxlLrR", P1));
    }

    // ── the wire row as the list tables hold it ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_wire_row_maps_as_a_full_read_stages_it()
    {
        var row = ListPushReplay.RowOf(new WireItem("spotify:track:x", ItemId: "daf0ac8107724220", AddedBy: "user1",
                                                    Timestamp: 1_789_757_787_000, Present: Attrs.AddedBy | Attrs.Timestamp));
        Assert.Equal(new ListRow("spotify:track:x", "daf0ac8107724220", 1_789_757_787, "spotify:user:user1", 0, 0, 0, 0,
                                 RootlistKind.Item, 0, 0, null, null), row);

        // seconds stay seconds; no adder is none; a chart row keeps its triple
        var chart = ListPushReplay.RowOf(new WireItem("spotify:track:y", Timestamp: 1_700_000_000, ChartStatus: 2, ChartPos: 3, ChartPrev: 7));
        Assert.Equal(1_700_000_000, chart.AddedAt);
        Assert.Null(chart.AddedBy);
        Assert.Null(chart.ItemId);
        Assert.Equal((byte)2, chart.ChartStatus);
        Assert.Equal((ushort)3, chart.ChartPos);
        Assert.Equal((ushort)7, chart.ChartPrev);
    }

    [Fact]
    public void An_adder_becomes_a_user_uri_by_the_decoders_own_rule()
    {
        Assert.Equal("spotify:user:bob", ListPushReplay.AdderUri("bob"));
        Assert.Equal("spotify:user:bob", ListPushReplay.AdderUri("spotify:user:bob"));           // already a uri
        Assert.Null(ListPushReplay.AdderUri(""));
        Assert.Null(ListPushReplay.AdderUri(null));
        Assert.Equal("spotify:user:" + new string('a', 200), ListPushReplay.AdderUri(new string('a', 200)));
        Assert.Null(ListPushReplay.AdderUri(new string('a', 201)));                              // over the decoder's bound
    }

    // ── a push replayed over the held rows ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Push_r36_over_the_held_r35_rows_is_the_r36_read_field_for_field()
    {
        var r35 = Rows("p1-read-r35.bin", out string rev35);
        var r36 = Rows("p1-read-r36.bin", out string rev36);
        var push = PlaylistOps.DecodePush(PlaylistOpsFixtures.Bytes("p1-push-r36-add-123.bin"));
        Assert.Equal(ListPush.Verdict.ApplyInPlace,
            ListPush.Decide(rev35, push.ParentRevision, push.NewRevision!, push.HasReplayableOps, resident: true, pendingLocal: false, open: false));

        Assert.True(ListPushReplay.TryReplay(r35, push.Batch, out var rows, out var attrs, out var tally, out var misfit),
                    "misfit at op " + misfit.OpIndex + ": " + misfit.Why);
        Assert.Equal(r36, rows);                        // uri, item id, the adder as a user uri, the instant — what a full read writes
        Assert.True(attrs.IsEmpty);
        Assert.Equal(1, tally.Added);
        Assert.True(tally.Reconciles(r35.Length, rows.Count));
        Assert.Equal(rev36, push.NewRevision);
        Assert.True(ListWrite.IsWellFormedRevision(push.NewRevision));
    }

    [Fact]
    public void A_positional_diff_replays_over_held_rows_with_its_rem_checked_by_item_id()
    {
        var baseline = Rows("p2-read-r6.bin");
        var answer = PlaylistOps.DecodeDiff(PlaylistOpsFixtures.Bytes("p2-diff-r6-r11.bin"));
        Assert.True(ListPushReplay.TryReplay(baseline, answer.Batch, out var rows, out _, out var tally, out var misfit),
                    "misfit at op " + misfit.OpIndex + ": " + misfit.Why);
        var expected = PlaylistOpsFixtures.Expected("p2-r11.expected.txt").Select(w => (w.Uri, w.ItemId)).ToArray();
        Assert.Equal(expected, rows.Select(r => (r.Uri, r.ItemId)).ToArray());
        Assert.Equal(3, tally.Added);
        Assert.Equal(1, tally.Removed);
    }

    [Fact]
    public void A_push_that_does_not_fit_the_held_rows_changes_nothing()
    {
        var r35 = Rows("p1-read-r35.bin");
        var shorter = r35[..100];                                                // ADD at 123 is out of range here
        var push = PlaylistOps.DecodePush(PlaylistOpsFixtures.Bytes("p1-push-r36-add-123.bin"));

        Assert.False(ListPushReplay.TryReplay(shorter, push.Batch, out var rows, out _, out _, out var misfit));
        Assert.Equal(Refusal.IndexOutOfRange, misfit.Why);
        Assert.Equal(shorter, rows);

        // The row it adds is already held (a replay over the wrong list): refused, untouched.
        var r36 = Rows("p1-read-r36.bin");
        Assert.False(ListPushReplay.TryReplay(r36, push.Batch, out rows, out _, out _, out misfit));
        Assert.Equal(Refusal.DuplicateItemId, misfit.Why);
        Assert.Equal(r36, rows);
    }

    [Fact]
    public void An_item_attribute_op_patches_the_adder_and_refuses_what_a_member_cannot_hold()
    {
        // RULE-DERIVED (no capture has an UPDATE_ITEM_ATTRIBUTES): the values ride as a uri-less wire row.
        var baseline = Rows("p2-read-r6.bin");
        var adder = new PlaylistOps.Batch([PlaylistOps.Op.UpdateItem(1, 0, Attrs.AddedBy)],
                                          [new WireItem("", AddedBy: "user2", Present: Attrs.AddedBy)], []);
        Assert.True(ListPushReplay.TryReplay(baseline, adder, out var rows, out _, out var tally, out _));
        Assert.Equal("spotify:user:user2", rows[1].AddedBy);
        Assert.Equal(baseline[1] with { AddedBy = "spotify:user:user2" }, rows[1]);
        Assert.Equal(1, tally.ItemUpdates);

        var unset = new PlaylistOps.Batch([PlaylistOps.Op.UpdateItem(1, 0, Attrs.None, Attrs.Timestamp)], [new WireItem("")], []);
        Assert.True(ListPushReplay.TryReplay(baseline, unset, out rows, out _, out _, out _));
        Assert.Equal(0, rows[1].AddedAt);

        var isPublic = new PlaylistOps.Batch([PlaylistOps.Op.UpdateItem(1, 0, Attrs.Public)],
                                             [new WireItem("", Public: true, Present: Attrs.Public)], []);
        Assert.False(ListPushReplay.TryReplay(baseline, isPublic, out rows, out _, out _, out var misfit));
        Assert.Equal(Refusal.PatchRefused, misfit.Why);
        Assert.Equal(baseline, rows);
    }

    // ── the log line's words ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_push_that_does_not_apply_names_the_first_rule_it_failed()
    {
        var headOnly = PlaylistOps.DecodePush(PlaylistOpsFixtures.Bytes("p1-push-r35-head-only.bin"));
        var withOps = PlaylistOps.DecodePush(PlaylistOpsFixtures.Bytes("p1-push-r36-add-123.bin"));
        var broken = PlaylistOps.DecodePush(PlaylistOpsFixtures.Bytes("p1-push-r32-rem-x3.bin").AsSpan(0, 100));
        var refused = new PlaylistOps.PushAnswer(true, "spotify:playlist:" + P1, "36,3636363636363636363636363636363636363636",
                                                 "35,3535353535353535353535353535353535353535", PlaylistOps.Batch.Empty, Refusal.Unobserved);

        Assert.Equal("undecodable", ListPushReplay.WhyStale(broken, resident: true, pendingLocal: false));
        Assert.Equal("refused:Unobserved", ListPushReplay.WhyStale(refused, resident: true, pendingLocal: false));
        Assert.Equal("head-only", ListPushReplay.WhyStale(headOnly, resident: true, pendingLocal: false));
        Assert.Equal("not-resident", ListPushReplay.WhyStale(withOps, resident: false, pendingLocal: false));
        Assert.Equal("pending", ListPushReplay.WhyStale(withOps, resident: true, pendingLocal: true));
        Assert.Equal("gap", ListPushReplay.WhyStale(withOps, resident: true, pendingLocal: false));
    }

    [Theory]
    [InlineData(ListPush.Verdict.Drop, "drop")]
    [InlineData(ListPush.Verdict.MarkDirty, "dirty")]
    [InlineData(ListPush.Verdict.ApplyInPlace, "applied")]
    [InlineData(ListPush.Verdict.RevalidateNow, "revalidate")]
    public void The_verdict_word_is_the_one_the_log_line_writes(ListPush.Verdict verdict, string word)
        => Assert.Equal(word, ListPushReplay.VerdictName(verdict));
}
