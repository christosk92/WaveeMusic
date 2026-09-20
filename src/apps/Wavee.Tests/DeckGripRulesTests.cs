// ── Wavee.Tests/DeckGripRulesTests.cs — the headshell and the click wheel, as one gesture's rules ─────────────────────
//
// `Deck.GripRules` is what `Deck.Gesture` and the two grips ask. ch 23 §6.4(6) is the headline fact: 0.2.9's headshell
// committed a seek to 0 over an unknown duration while the wheel refused — both grips now refuse.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class DeckGripRulesTests
{
    [Fact]
    public void A_grip_over_an_unknown_duration_is_refused()
        => Assert.False(Deck.GripRules.Enabled(hasPlayable: true, failed: false, canSeek: true, durationMs: 0));

    [Fact]
    public void A_grip_needs_a_playable_unfailed_seekable_item()
    {
        Assert.True(Deck.GripRules.Enabled(true, false, true, 200_000));
        Assert.False(Deck.GripRules.Enabled(hasPlayable: false, false, true, 200_000));
        Assert.False(Deck.GripRules.Enabled(true, failed: true, true, 200_000));
        Assert.False(Deck.GripRules.Enabled(true, false, canSeek: false, 200_000));
    }

    [Fact]
    public void A_drag_is_abandoned_when_the_item_or_the_device_changes_under_it()
    {
        Assert.True(Deck.GripRules.StillCurrent(true, true, true, true));
        Assert.False(Deck.GripRules.StillCurrent(active: false, true, true, true));
        Assert.False(Deck.GripRules.StillCurrent(true, enabled: false, true, true));
        Assert.False(Deck.GripRules.StillCurrent(true, true, sameItem: false, true));
        Assert.False(Deck.GripRules.StillCurrent(true, true, true, sameDevice: false));
    }

    [Fact]
    public void The_clamp_is_the_track_and_an_unknown_duration_clamps_at_zero_only()
    {
        Assert.Equal(200_000, Deck.GripRules.Clamp(250_000, 200_000));
        Assert.Equal(0, Deck.GripRules.Clamp(-5, 200_000));
        Assert.Equal(250_000, Deck.GripRules.Clamp(250_000, 0));
        Assert.Equal(0, Deck.GripRules.Clamp(-5, 0));
    }

    [Fact]
    public void A_groove_fraction_maps_to_milliseconds_and_never_seeks_over_an_unknown_duration()
    {
        Assert.Equal(50_000, Deck.GripRules.MsAt(0.25f, 200_000));
        Assert.Equal(200_000, Deck.GripRules.MsAt(1.5f, 200_000));
        Assert.Equal(0, Deck.GripRules.MsAt(0.8f, 0));
    }

    [Fact]
    public void Crossing_nine_oclock_does_not_jump_a_quarter_of_the_track()
    {
        // atan2 flips from +π to −π across the seam: a small clockwise nudge reads as almost −2π raw.
        float raw = -MathF.PI + 0.05f - (MathF.PI - 0.05f);
        DeckIn.Near(0.1f, Deck.GripRules.Unwrap(raw), 1e-4f);
        DeckIn.Near(-0.1f, Deck.GripRules.Unwrap(-raw), 1e-4f);
        DeckIn.Near(0.3f, Deck.GripRules.Unwrap(0.3f), 1e-6f);
    }

    [Fact]
    public void One_full_turn_of_the_wheel_is_a_quarter_of_the_track()
    {
        float frac = 0.1f;
        for (int i = 0; i < 8; i++) frac = Deck.GripRules.WheelAdvance(frac, MathF.Tau / 8f);
        DeckIn.Near(0.35f, frac, 1e-4f);
        Assert.Equal(1f, Deck.GripRules.WheelAdvance(0.95f, MathF.PI));
        Assert.Equal(0f, Deck.GripRules.WheelAdvance(0.05f, -MathF.PI));
    }

    [Fact]
    public void A_tap_on_the_wheel_cancels_and_only_a_moved_release_over_a_known_duration_commits()
    {
        Assert.False(Deck.GripRules.ReleaseCommits(moved: false, durationMs: 200_000));
        Assert.True(Deck.GripRules.ReleaseCommits(moved: true, durationMs: 200_000));
        Assert.False(Deck.GripRules.ReleaseCommits(moved: true, durationMs: 0));
    }
}
