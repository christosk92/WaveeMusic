// ── Wavee.Tests/SeekTargetTests.cs — A3's other pure gate: where a committed seek actually lands ───────────────────
//
// `Playback.SeekTarget.Clamp` is the one clamp `DoSeek` runs every committed seek through — a local drag and a
// controller's `SeekTo` alike (`PlaybackStepTests.A_controllers_seek_goes_through_the_same_clamp_as_a_local_one`).
// It reserves a small tail so a seek can never itself place the deck at the exact inaudible instant `MirrorSnapshot`
// (A2) had to learn to recognise from the other direction.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class SeekTargetTests
{
    [Fact]
    public void A_position_well_inside_the_duration_is_unchanged()
    {
        Assert.Equal(90_000, Playback.SeekTarget.Clamp(90_000, 200_000));
    }

    [Fact]
    public void A_negative_request_clamps_to_zero()
    {
        Assert.Equal(0, Playback.SeekTarget.Clamp(-5, 200_000));
    }

    [Fact]
    public void A_request_past_the_duration_clamps_to_the_duration_minus_the_tail_guard()
    {
        Assert.Equal(200_000 - Playback.SeekTarget.TailGuardMs, Playback.SeekTarget.Clamp(999_999, 200_000));
    }

    [Fact]
    public void A_request_exactly_at_the_duration_is_also_pulled_back_by_the_guard()
    {
        Assert.Equal(200_000 - Playback.SeekTarget.TailGuardMs, Playback.SeekTarget.Clamp(200_000, 200_000));
    }

    [Fact]
    public void A_duration_shorter_than_the_guard_floors_at_zero_rather_than_going_negative()
    {
        Assert.Equal(0, Playback.SeekTarget.Clamp(999, 400));
    }

    [Fact]
    public void An_unknown_duration_clamps_only_the_lower_bound()
    {
        Assert.Equal(999_999, Playback.SeekTarget.Clamp(999_999, 0));
        Assert.Equal(0, Playback.SeekTarget.Clamp(-1, 0));
    }
}
