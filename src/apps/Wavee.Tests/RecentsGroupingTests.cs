using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The pure recents collapse (Wavee.Core, proto-free): 9,446 flat wire items → ~1,708 display rows. Consecutive plays of
// one context fold behind a single header, and the header's `group_metadata` — NOT its uri list length — says how many
// plays the card stands for. Also pins the item-vector equality the /diff revision-lies guard leans on (B-2).
public class RecentsGroupingTests
{
    static RecentsItem Header(string itemId, string uri, long playedAt, int childrenGroupId,
        RecentsGroupInfo? group = null, string? contentType = "music")
        => new(itemId, uri, playedAt, RecentsReason.Played, contentType,
            GroupId: 0, HasChildrenGroupId: true, Group: group);

    static RecentsItem Member(string itemId, string uri, long playedAt, int groupId, string? contentType = "music")
        => new(itemId, uri, playedAt, RecentsReason.Played, contentType,
            GroupId: groupId, HasChildrenGroupId: false, Group: null);

    static RecentsItem Single(string itemId, string uri, long playedAt, string? contentType = "music")
        => new(itemId, uri, playedAt, RecentsReason.Played, contentType,
            GroupId: null, HasChildrenGroupId: false, Group: null);

    // ── a header ABSORBS the members that follow it (header children_group_id = N ↔ member KEY group_id_<N>) ─────────
    [Fact]
    public void Header_AbsorbsFollowingMembers_IntoOneRow()
    {
        var items = new List<RecentsItem>
        {
            Header("h1", "spotify:playlist:p", 100, childrenGroupId: 4,
                group: new RecentsGroupInfo(3, ["spotify:track:a", "spotify:track:b", "spotify:track:c"])),
            Member("m1", "spotify:track:a", 99, groupId: 4),
            Member("m2", "spotify:track:b", 98, groupId: 4),
            Member("m3", "spotify:track:c", 97, groupId: 4),
            Single("s1", "spotify:album:z", 96),
        };

        var rows = RecentsList.Group(items);

        Assert.Equal(2, rows.Count);                                  // 5 wire items → 2 rendered rows
        Assert.Equal(RecentsRowKind.Group, rows[0].Kind);
        Assert.Equal("h1", rows[0].ItemId);                           // keyed on the HEADER's item_id
        Assert.Equal("spotify:playlist:p", rows[0].ContextUri);       // what opening the card navigates to
        Assert.Equal(3, rows[0].ChildCount);
        Assert.Equal(RecentsEntityKind.Playlist, rows[0].EntityKind);
        Assert.Equal(RecentsRowKind.Single, rows[1].Kind);
        Assert.Equal("s1", rows[1].ItemId);
        Assert.Equal(RecentsEntityKind.Album, rows[1].EntityKind);
    }

    // ── the real capture's trap: child_count = 11 with only 3 child_uris. The COUNT wins ────────────────────────────
    [Fact]
    public void ChildCount_Wins_OverTruncatedChildUris()
    {
        var rows = RecentsList.Group(
        [
            Header("h1", "spotify:album:a", 100, childrenGroupId: 1,
                group: new RecentsGroupInfo(11, ["spotify:track:a", "spotify:track:b", "spotify:track:c"])),
        ]);

        var row = Assert.Single(rows);
        Assert.Equal(11, row.ChildCount);                             // the "played 11" the card renders
        Assert.Equal(3, row.ChildUris!.Count);                        // …the uri list stays truncated, as sent
    }

    // ── an EMPTY-uri header resolves its kind (and has no ContextUri) from the first child uri ───────────────────────
    [Fact]
    public void EmptyUriHeader_HasNoContextUri_AndTakesKindFromFirstChild()
    {
        var rows = RecentsList.Group(
        [
            Header("h1", "", 100, childrenGroupId: 2,
                group: new RecentsGroupInfo(2, ["spotify:episode:e1", "spotify:episode:e2"]), contentType: "podcasts"),
            Member("m1", "spotify:episode:e1", 99, groupId: 2, contentType: "podcasts"),
        ]);

        var row = Assert.Single(rows);
        Assert.Null(row.ContextUri);
        Assert.Equal(RecentsEntityKind.Episode, row.EntityKind);
        Assert.Equal("podcasts", row.ContentType);
    }

    // ── uri → entity kind, including BOTH collection shapes the recents surface uses ─────────────────────────────────
    [Fact]
    public void EntityKindOf_CoversBothCollectionShapes()
    {
        Assert.Equal(RecentsEntityKind.Collection, RecentsList.EntityKindOf("spotify:collection:tracks"));
        Assert.Equal(RecentsEntityKind.Collection, RecentsList.EntityKindOf("spotify:user:31abc:collection"));
        Assert.Equal(RecentsEntityKind.Track, RecentsList.EntityKindOf("spotify:track:t"));
        Assert.Equal(RecentsEntityKind.Playlist, RecentsList.EntityKindOf("spotify:playlist:p"));
        Assert.Equal(RecentsEntityKind.Album, RecentsList.EntityKindOf("spotify:album:a"));
        Assert.Equal(RecentsEntityKind.Artist, RecentsList.EntityKindOf("spotify:artist:a"));
        Assert.Equal(RecentsEntityKind.Show, RecentsList.EntityKindOf("spotify:show:s"));
        Assert.Equal(RecentsEntityKind.Episode, RecentsList.EntityKindOf("spotify:episode:e"));
        Assert.Equal(RecentsEntityKind.Unknown, RecentsList.EntityKindOf(""));
        Assert.Equal(RecentsEntityKind.Unknown, RecentsList.EntityKindOf("https://open.spotify.com/x"));
        Assert.Equal(RecentsEntityKind.Unknown, RecentsList.EntityKindOf("spotify:banana:b"));
    }

    // ── B-2: SameItems must see a GROUP GROW under a stable item_id, or "played N" freezes forever ────────────────────
    [Fact]
    public void SameItems_DetectsChildCountChange_UnderAStableItemId()
    {
        var before = RecentsList.Group([Header("h1", "spotify:playlist:p", 100, 1, new RecentsGroupInfo(7, []))]);
        var after = RecentsList.Group([Header("h1", "spotify:playlist:p", 100, 1, new RecentsGroupInfo(8, []))]);

        Assert.Equal("h1", before[0].ItemId);
        Assert.Equal(before[0].ItemId, after[0].ItemId);              // same key — the ItemId-only compare saw "unchanged"
        Assert.False(RecentsList.SameItems(before, after));           // …the count moved, so the row DID change
    }

    // ── …and a re-play under the same key (played-at moves) is a change too ─────────────────────────────────────────
    [Fact]
    public void SameItems_DetectsPlayedAtChange_AndAcceptsAnIdenticalVector()
    {
        var a = RecentsList.Group([Single("s1", "spotify:track:t", 100), Single("s2", "spotify:track:u", 90)]);
        var b = RecentsList.Group([Single("s1", "spotify:track:t", 100), Single("s2", "spotify:track:u", 90)]);
        Assert.True(RecentsList.SameItems(a, b));                     // byte-identical contents → the revision lied

        var moved = RecentsList.Group([Single("s1", "spotify:track:t", 101), Single("s2", "spotify:track:u", 90)]);
        Assert.False(RecentsList.SameItems(a, moved));

        var reordered = RecentsList.Group([Single("s2", "spotify:track:u", 90), Single("s1", "spotify:track:t", 100)]);
        Assert.False(RecentsList.SameItems(a, reordered));

        Assert.False(RecentsList.SameItems(a, RecentsList.Group([Single("s1", "spotify:track:t", 100)])));
        Assert.True(RecentsList.SameItems([], []));
    }
}
