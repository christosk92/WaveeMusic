// ── Wavee.Tests/ContextPlayRulesTests.cs — a container's ▶ pauses what it is already playing ───────────────────────────
//
// ContextPlayRules.For is the pure decision behind Playback.PlayOrToggleContext: the card seam's relation plus the list
// row's fault gate. Identities in, a verb out — no deck.

using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class ContextPlayRulesTests
{
    public ContextPlayRulesTests() => TestScope.Fresh();

    static EntityId Id(string uri) => EntityId.Parse(uri);

    static readonly string Album = "spotify:album:4aawyAB9vmqN3uQ7FjRGTy";
    static readonly string Playlist = "spotify:playlist:37i9dQZF1DXcBWIGoYBM5M";
    static readonly string Track = "spotify:track:6rqhFgbbKwnb9MLmUQDhG6";

    [Fact]
    public void The_playing_context_toggles()
        => Assert.Equal(ContextPlayAction.Toggle,
            ContextPlayRules.For(Id(Album), Id(Album), Id(Track), Playback.Fault.None));

    [Fact]
    public void Another_context_starts()
        => Assert.Equal(ContextPlayAction.Start,
            ContextPlayRules.For(Id(Album), Id(Playlist), Id(Track), Playback.Fault.None));

    [Fact]
    public void Nothing_on_deck_starts()
        => Assert.Equal(ContextPlayAction.Start,
            ContextPlayRules.For(Id(Album), default, default, Playback.Fault.None));

    [Fact]
    public void A_standing_fault_starts_afresh_because_resume_would_do_nothing()
    {
        foreach (var fault in new[] { Playback.Fault.Network, Playback.Fault.Unavailable, Playback.Fault.DrmRequired,
                                      Playback.Fault.DecodeFailed, Playback.Fault.RuntimeMissing, Playback.Fault.Unknown })
            Assert.Equal(ContextPlayAction.Start, ContextPlayRules.For(Id(Album), Id(Album), Id(Track), fault));
    }

    [Fact]
    public void A_playable_target_is_compared_with_the_playing_item_not_the_context()
    {
        Assert.Equal(ContextPlayAction.Toggle, ContextPlayRules.For(Id(Track), Id(Album), Id(Track), Playback.Fault.None));
        Assert.Equal(ContextPlayAction.Start, ContextPlayRules.For(Id(Track), Id(Track), default, Playback.Fault.None));
    }

    [Fact]
    public void An_empty_target_starts_nothing_special()
        => Assert.Equal(ContextPlayAction.Start,
            ContextPlayRules.For(default, Id(Album), Id(Track), Playback.Fault.None));
}
