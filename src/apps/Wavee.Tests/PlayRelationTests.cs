// ── Wavee.Tests/PlayRelationTests.cs — "does this surface's ▶ pause right now" as a core fact (C16, X45) ─────────────
//
// `PlayRelation.ShowsPause` is the fact a play button names its glyph and label from (Play or Pause). The pure overload
// takes the deck's facts as arguments, so it is tested without a deck: the relation is `CardRelation`'s (pinned in
// CardRelationTests), the gate is "something is on deck, playback runs, no fault stands" (the same gate
// `ContextPlayRules` uses to choose Toggle over Start). The subscribing overloads only read the deck's signals; the
// facts here are the ones that touch no signal state.

using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class PlayRelationTests
{
    public PlayRelationTests() => TestScope.Fresh();

    static EntityId Id(string uri) => EntityId.Parse(uri);

    static readonly string s_album = "spotify:album:4aawyAB9vmqN3uQ7FjRGTy";
    static readonly string s_other = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";
    static readonly string s_track = "spotify:track:6rqhFgbbKwnb9MLmUQDhG6";

    [Fact]
    public void The_playing_context_pauses()
        => Assert.True(PlayRelation.ShowsPause(Id(s_album), Id(s_album), Id(s_track), hasContext: true, Playback.Fault.None, isPlaying: true));

    [Fact]
    public void A_paused_deck_does_not_pause_again()
        => Assert.False(PlayRelation.ShowsPause(Id(s_album), Id(s_album), Id(s_track), hasContext: true, Playback.Fault.None, isPlaying: false));

    [Theory]
    [InlineData(Playback.Fault.Network)]
    [InlineData(Playback.Fault.Unavailable)]
    [InlineData(Playback.Fault.DecodeFailed)]
    public void A_standing_fault_never_shows_pause(Playback.Fault fault)
        => Assert.False(PlayRelation.ShowsPause(Id(s_album), Id(s_album), Id(s_track), hasContext: true, fault, isPlaying: true));

    [Fact]
    public void An_idle_deck_shows_play_whatever_the_identities_say()
        => Assert.False(PlayRelation.ShowsPause(Id(s_album), Id(s_album), Id(s_track), hasContext: false, Playback.Fault.None, isPlaying: true));

    [Fact]
    public void A_context_that_is_not_the_playing_one_shows_play()
        => Assert.False(PlayRelation.ShowsPause(Id(s_other), Id(s_album), Id(s_track), hasContext: true, Playback.Fault.None, isPlaying: true));

    [Fact]
    public void A_playable_pauses_when_it_is_the_item_on_deck_and_not_when_it_only_is_the_context()
    {
        var track = Id(s_track);
        Assert.True(PlayRelation.ShowsPause(track, Id(s_album), track, hasContext: true, Playback.Fault.None, isPlaying: true));
        Assert.False(PlayRelation.ShowsPause(track, track, default, hasContext: true, Playback.Fault.None, isPlaying: true));
    }

    [Fact]
    public void An_empty_target_never_pauses()
        => Assert.False(PlayRelation.ShowsPause(default, default, default, hasContext: true, Playback.Fault.None, isPlaying: true));

    [Fact]
    public void The_live_overloads_answer_false_for_an_empty_or_missing_target_without_reading_the_deck()
    {
        Assert.False(PlayRelation.ShowsPause(default(EntityId)));
        Assert.False(PlayRelation.ShowsPause(""));
        Assert.False(PlayRelation.ShowsPause((string)null!));
        Assert.False(PlayRelation.CardRelates(""));
        Assert.False(PlayRelation.CardRelates(null!));
    }
}
