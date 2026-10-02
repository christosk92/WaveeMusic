// ── Wavee.Tests/CardRelationTests.cs — which media surface relates to the deck (#160) ───────────────────────────────────
//
// The card playback seam (Controls.NowPlaying) is wired from Playback's signals by Playback.InstallCardSeam; the decision
// it applies is CardRelation, tested here without a deck.

using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class CardRelationTests
{
    public CardRelationTests() => TestScope.Fresh();

    static EntityId Id(string uri) => EntityId.Parse(uri);

    [Fact]
    public void Nothing_on_deck_is_inactive()
    {
        Assert.False(CardRelation.Active(default, default));
        Assert.True(CardRelation.Active(Id("spotify:album:4aawyAB9vmqN3uQ7FjRGTy"), default));
        Assert.True(CardRelation.Active(default, Id("spotify:track:6rqhFgbbKwnb9MLmUQDhG6")));
    }

    [Fact]
    public void A_context_card_relates_to_the_playing_context_only()
    {
        var album = Id("spotify:album:4aawyAB9vmqN3uQ7FjRGTy");
        var other = Id("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M");
        var track = Id("spotify:track:6rqhFgbbKwnb9MLmUQDhG6");
        Assert.True(CardRelation.Relates(album, album, track));
        Assert.False(CardRelation.Relates(album, other, track));
        Assert.False(CardRelation.Relates(album, default, album));   // a context card never matches the playable slot
    }

    [Fact]
    public void A_playable_card_relates_to_the_item_on_deck_only()
    {
        var track = Id("spotify:track:6rqhFgbbKwnb9MLmUQDhG6");
        var episode = Id("spotify:episode:512ojhOuo1ktJprKbVcKyQ");
        var album = Id("spotify:album:4aawyAB9vmqN3uQ7FjRGTy");
        Assert.True(CardRelation.Relates(track, album, track));
        Assert.True(CardRelation.Relates(episode, default, episode));
        Assert.False(CardRelation.Relates(track, track, default));   // a track that is the context, not the item, stays dark
    }

    [Fact]
    public void An_empty_card_never_relates()
        => Assert.False(CardRelation.Relates(default, default, default));

    [Theory]
    [InlineData(EntityKind.Track, true)]
    [InlineData(EntityKind.Episode, true)]
    [InlineData(EntityKind.Album, false)]
    [InlineData(EntityKind.Playlist, false)]
    [InlineData(EntityKind.Artist, false)]
    [InlineData(EntityKind.Show, false)]
    [InlineData(EntityKind.Collection, false)]
    public void Only_tracks_and_episodes_are_playable_cards(EntityKind kind, bool playable)
        => Assert.Equal(playable, CardRelation.IsPlayable(kind));
}
