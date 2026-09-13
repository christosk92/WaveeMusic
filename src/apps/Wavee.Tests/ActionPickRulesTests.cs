// ── Wavee.Tests/ActionPickRulesTests.cs — the two pickers' pure half ─────────────────────────────────────────────────
//
// `Actions.PickRules` (Platform/Actions.Rules.cs): which modes the action picker offers and in what order, what a pick
// commits, when the accent button may enable, and the destination picker's live filter.

using Xunit;

namespace Wavee.Tests;

public class ActionPickRulesTests
{
    [Fact]
    public void The_picker_offers_exactly_the_declared_modes_in_offer_order()
    {
        var modes = Actions.PickRules.AcceptedModes(ActionTargetModes.NowPlaying | ActionTargetModes.FixedEntity);
        ActionTargetMode[] expected = [ActionTargetMode.FixedEntity, ActionTargetMode.NowPlaying];
        Assert.Equal(expected, modes);
        Assert.Empty(Actions.PickRules.AcceptedModes(ActionTargetModes.Nothing));
        Assert.Equal(5, Actions.PickRules.AcceptedModes(ActionTargetModes.All).Length);
    }

    [Fact]
    public void Choosing_an_action_resets_the_mode_to_its_first_accepted_one()
    {
        Assert.Equal(ActionTargetMode.FixedTrack,
            Actions.PickRules.FirstMode(ActionTargetModes.FixedTrack | ActionTargetModes.NowPlaying));
        Assert.Equal(ActionTargetMode.None, Actions.PickRules.FirstMode(ActionTargetModes.Nothing));
    }

    [Fact]
    public void Only_the_fixed_modes_need_a_target_before_the_pick_is_committable()
    {
        Assert.True(Actions.PickRules.NeedsTarget(ActionTargetMode.FixedEntity));
        Assert.True(Actions.PickRules.NeedsTarget(ActionTargetMode.FixedTrack));
        Assert.False(Actions.PickRules.NeedsTarget(ActionTargetMode.NowPlaying));

        Assert.False(Actions.PickRules.Ready(hasDescriptor: false, ActionTargetMode.None, null));
        Assert.True(Actions.PickRules.Ready(hasDescriptor: true, ActionTargetMode.ActiveRoute, null));
        Assert.False(Actions.PickRules.Ready(hasDescriptor: true, ActionTargetMode.FixedEntity, ""));
        Assert.True(Actions.PickRules.Ready(hasDescriptor: true, ActionTargetMode.FixedEntity, "spotify:album:x"));
    }

    [Fact]
    public void A_pick_stores_publisher_and_contribution_separately_and_round_trips_through_compose()
    {
        var binding = Actions.PickRules.Bind("wavee.playNext", ActionTargetMode.FixedTrack, "spotify:track:1");
        Assert.Equal("wavee", binding.ProviderId);
        Assert.Equal("playNext", binding.ActionId);
        Assert.Equal("spotify:track:1", binding.TargetKey);
        Assert.Equal("wavee.playNext", Actions.KeyOf(in binding));
    }

    [Fact]
    public void A_dynamic_mode_never_carries_a_stale_target_key()
        => Assert.Null(Actions.PickRules.Bind("wavee.toggleLike", ActionTargetMode.NowPlaying, "spotify:track:1").TargetKey);

    [Fact]
    public void The_mode_index_falls_back_to_the_first_row()
    {
        ActionTargetMode[] modes = [ActionTargetMode.FixedEntity, ActionTargetMode.ActiveRoute];
        Assert.Equal(1, Actions.PickRules.IndexOfMode(modes, ActionTargetMode.ActiveRoute));
        Assert.Equal(0, Actions.PickRules.IndexOfMode(modes, ActionTargetMode.NowPlaying));
    }

    [Fact]
    public void Every_mode_has_a_label_key()
    {
        foreach (var mode in Actions.PickRules.AcceptedModes(ActionTargetModes.All))
            Assert.False(string.IsNullOrEmpty(Actions.PickRules.ModeLocKey(mode)));
    }

    [Theory]
    [InlineData(null, "Road trip", false, true)]
    [InlineData("", "Road trip", false, true)]
    [InlineData("trip", "Road trip", false, true)]
    [InlineData("  TRIP ", "Road trip", false, true)]   // trimmed, case-insensitive
    [InlineData("deep", "Road trip", false, false)]
    [InlineData("deep", "Top level", true, true)]        // a pinned row survives every query: the way back
    public void The_destination_filter_never_hides_a_pinned_row(string? query, string label, bool pinned, bool shown)
        => Assert.Equal(shown, Actions.PickRules.Matches(query, label, pinned));
}
