using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Backend.Library;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The Liked Songs OUTER join: every member is a row; an unhydrated one is a titleless placeholder that carries only
// what membership knows. The member count is the collection's count, whatever the hydration state.
public class LikedMembershipJoinTests
{
    static Track Trk(string id) => new(id, "spotify:track:" + id, "T" + id, [], new AlbumRef("", "", ""), 1000, false, null);

    [Fact]
    public void EveryMember_IsARow_HydratedOrNot()
    {
        var tracks = new Dictionary<string, Track> { ["spotify:track:a"] = Trk("a") };
        var members = new[] { new SavedItem("spotify:track:a", 3_000), new SavedItem("spotify:track:b", 2_000), new SavedItem("spotify:track:c", 0) };

        var rows = LikedMembershipJoin.Join(members, uri => tracks.GetValueOrDefault(uri));

        Assert.Equal(3, rows.MemberCount);
        Assert.Equal(1, rows.HydratedCount);
        Assert.Equal(3, rows.Tracks.Count);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b", "spotify:track:c" }, new[] { rows.Tracks[0].Uri, rows.Tracks[1].Uri, rows.Tracks[2].Uri });   // order preserved
        Assert.False(LikedMembershipJoin.IsPlaceholder(rows.Tracks[0]));
        Assert.True(LikedMembershipJoin.IsPlaceholder(rows.Tracks[1]));
        Assert.True(LikedMembershipJoin.IsPlaceholder(rows.Tracks[2]));
    }

    [Fact]
    public void Placeholder_CarriesUriIdAndAddDate_AndNothingElse()
    {
        var p = LikedMembershipJoin.Placeholder("spotify:track:xyz", 5_000);

        Assert.Equal("spotify:track:xyz", p.Uri);
        Assert.Equal("xyz", p.Id);
        Assert.Equal("", p.Title);
        Assert.Empty(p.Artists);
        Assert.Equal(0, p.DurationMs);
        Assert.Null(p.Image);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(5_000), p.AddedAt);
        Assert.True(LikedMembershipJoin.IsPlaceholder(p));

        Assert.Null(LikedMembershipJoin.Placeholder("spotify:track:none", 0).AddedAt);   // 0 = unknown, never epoch
    }

    [Fact]
    public void HydratedRow_GetsTheMembershipAddDate_AndIsNotAPlaceholder()
    {
        var rows = LikedMembershipJoin.Join(new[] { new SavedItem("spotify:track:a", 7_000), new SavedItem("spotify:track:b", 0) },
            uri => Trk(uri[(uri.LastIndexOf(':') + 1)..]));

        Assert.Equal(2, rows.HydratedCount);
        Assert.Equal("Ta", rows.Tracks[0].Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(7_000), rows.Tracks[0].AddedAt);
        Assert.Null(rows.Tracks[1].AddedAt);
        Assert.All(rows.Tracks, t => Assert.False(LikedMembershipJoin.IsPlaceholder(t)));
    }
}
