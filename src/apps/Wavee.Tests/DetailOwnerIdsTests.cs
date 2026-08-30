using System;
using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The open-playlist live-refresh predicate's User arm. It used to match EVERY spotify:user: bump in the process, so a
// resolved stranger — a sidebar profile prefetch, another page's owners — re-mapped and re-projected the open page.
// These pin the two halves: the set is what the page RENDERS, and the match is scoped to it.
public class DetailOwnerIdsTests
{
    static Track Row(string id, string? addedBy) =>
        new(id, "spotify:track:" + id, "T" + id, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
            1000, false, null, AddedBy: addedBy);

    [Fact]
    public void From_CollectsOwnerCollaboratorsAndAddedBy_Distinct()
    {
        var ids = DetailOwnerIds.From(
            ownerName: "bob",
            collaborators: new[] { new Owner("carol", "Carol", null) },
            profilesById: new Dictionary<string, Owner> { ["dave"] = new("dave", "Dave", null) },
            tracks: new[] { Row("t1", "bob"), Row("t2", "erin"), Row("t3", "bob"), Row("t4", null) });

        Assert.Equal(new[] { "bob", "carol", "dave", "erin" }, Sorted(ids));
    }

    // Ids arrive in three shapes across the wire (bare, uri, mixed case) and the store keys them lowercased through
    // UserProfileIds. A set that did not normalize would silently never match — i.e. no refresh at all, which is the
    // failure this whole arm exists to prevent.
    [Fact]
    public void From_NormalizesUrisAndCase_TheWayTheStoreKeysOwners()
    {
        var ids = DetailOwnerIds.From("spotify:user:BOB", null, null, new[] { Row("t1", "Bob") });

        Assert.Single(ids);
        Assert.True(DetailOwnerIds.Matches(ids, "spotify:user:bob"));
        Assert.True(DetailOwnerIds.Matches(ids, "spotify:user:BOB"));
    }

    [Fact]
    public void From_DropsWhatIsNotAUserId()
    {
        // A display name with whitespace, an empty AddedBy, and a colon-bearing string are all rejected by Normalize.
        var ids = DetailOwnerIds.From("Bob The Builder", null, null, new[] { Row("t1", ""), Row("t2", "a:b") });
        Assert.Empty(ids);
    }

    [Fact]
    public void Matches_OnlyUserUrisThePageRenders()
    {
        var ids = DetailOwnerIds.From("bob", null, null, new[] { Row("t1", "carol") });

        Assert.True(DetailOwnerIds.Matches(ids, "spotify:user:carol"));
        // THE regression: a stranger's profile resolving elsewhere must not re-map this page.
        Assert.False(DetailOwnerIds.Matches(ids, "spotify:user:stranger"));
        // …and a non-user bump never reaches the set at all.
        Assert.False(DetailOwnerIds.Matches(ids, "spotify:album:al1"));
        Assert.False(DetailOwnerIds.Matches(ids, "spotify:playlist:p1"));
    }

    [Fact]
    public void Matches_EmptySet_IsAlwaysFalse()
        => Assert.False(DetailOwnerIds.Matches(DetailOwnerIds.From(null, null, null, null), "spotify:user:bob"));

    static string[] Sorted(HashSet<string> ids)
    {
        var a = new string[ids.Count];
        ids.CopyTo(a);
        Array.Sort(a, StringComparer.Ordinal);
        return a;
    }
}
