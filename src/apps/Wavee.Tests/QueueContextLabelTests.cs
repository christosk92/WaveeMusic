// ── Wavee.Tests/QueueContextLabelTests.cs — a server-generated `spotify:list:` context: its name and where its header goes ──
//
// While another device plays "FLEMMING Popular" the wire's context is `spotify:list:popular-release-segments-main-roles:
// artist_<id>` — no table, no page, and (before `EntityKind.List`) not even a known kind, so the panel named nothing. The
// name is the cluster's own `context_description`; the header opens the artist the referrer (else the list id) names.
// Pure rules over ids, no scope, no engine loop.

using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class QueueContextLabelTests
{
    const string ArtistGid = "0YLlTW9rW7ZCy2cA2u3RYk";
    const string ListUri = "spotify:list:popular-release-segments-main-roles:artist_" + ArtistGid;

    static readonly EntityId Artist = EntityId.Parse("spotify:artist:" + ArtistGid);

    [Fact]
    public void A_list_uri_is_a_known_kind_the_spotify_provider_owns()
    {
        EntityId list = EntityId.Parse(ListUri);
        Assert.Equal(EntityKind.List, list.Kind);
        Assert.True(list.IsValid);
        Assert.Equal(ListUri, list.Text);
    }

    [Fact]
    public void The_name_of_a_list_context_is_the_wires_description()
    {
        EntityId list = EntityId.Parse(ListUri);
        var wire = new Queue.ContextWire(list, "FLEMMING Popular", Artist);
        Assert.Equal("FLEMMING Popular", Queue.ContextName(list, in wire));
    }

    [Fact]
    public void A_description_that_labelled_another_context_is_not_read()
    {
        EntityId list = EntityId.Parse(ListUri);
        var stale = new Queue.ContextWire(EntityId.Parse("spotify:list:other:artist_" + ArtistGid), "Someone Else Popular", Artist);
        Assert.Null(Queue.ListName(list, in stale));
    }

    [Fact]
    public void The_header_opens_the_referrer_artist_else_the_artist_in_the_list_id()
    {
        EntityId list = EntityId.Parse(ListUri);
        EntityId other = EntityId.Parse("spotify:artist:4uLU6hMCjMI75M1A2tKUQC");

        Assert.Equal(other, Queue.ContextTarget(list, new Queue.ContextWire(list, "x", other)));   // the wire's referrer wins
        Assert.Equal(Artist, Queue.ContextTarget(list, new Queue.ContextWire(list, "x", default))); // the id's own segment
        Assert.Equal(Artist, Queue.ContextTarget(list, default));                                   // no wire at all
        Assert.Equal(Artist, Queue.ListArtist(list));
    }

    [Fact]
    public void A_list_naming_no_artist_opens_nothing_and_any_other_context_opens_itself()
    {
        EntityId recents = EntityId.Parse("spotify:list:recents:main");
        Assert.True(Queue.ContextTarget(recents, default).IsEmpty);
        Assert.True(Queue.ListArtist(recents).IsEmpty);

        EntityId album = EntityId.Parse("spotify:album:2noRn2Aes5aoNVsU6iWThc");
        Assert.Equal(album, Queue.ContextTarget(album, default));
    }
}
