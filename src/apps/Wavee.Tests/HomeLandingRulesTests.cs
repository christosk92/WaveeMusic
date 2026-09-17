// ── Wavee.Tests/HomeLandingRulesTests.cs — the landing's small rules the renderer and the estimators share ──────────
//
// ch 10 §9 defects #1 (the module width a shell receives), #4 and #5 (the greeting / chip row's estimate arms), the
// greeting's server-first rule, the handle suppression (W3) and the tail's editorial tiers (W5). Pure: no Loc, no
// engine loop.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class HomeLandingRulesTests
{
    [Theory]
    [InlineData(1160f, 1088f)]      // the typical content pane: A = 1160 − 72
    [InlineData(1600f, 1528f)]      // exactly the cap
    [InlineData(2400f, 1528f)]      // past the cap: the row stops growing
    [InlineData(0f, 1028f)]         // unmeasured: the 1,100 fallback, gutters off
    [InlineData(40f, 1f)]           // narrower than the gutters: floored, never negative
    public void Available_caps_then_takes_the_gutters_off(float cross, float expected)
        => Assert.Equal(expected, HomeLandingRules.Available(cross));

    [Fact]
    public void The_module_gap_comes_from_the_available_width_not_the_cross_size()
    {
        // Defect #1: a 1,120-DIP pane is ≥ 1,080 on the outside but 1,048 inside the gutters — the gap is the NARROW rung.
        float available = HomeLandingRules.Available(1120f);
        Assert.Equal(1048f, available);
        Assert.Equal(HomeModuleLayout.ModuleGapNarrow, HomeModuleLayout.Gap(available));
    }

    [Theory]
    [InlineData(0, HomeGreetingWord.Evening)]
    [InlineData(4, HomeGreetingWord.Evening)]
    [InlineData(5, HomeGreetingWord.Morning)]
    [InlineData(11, HomeGreetingWord.Morning)]
    [InlineData(12, HomeGreetingWord.Afternoon)]
    [InlineData(17, HomeGreetingWord.Afternoon)]
    [InlineData(18, HomeGreetingWord.Evening)]
    [InlineData(23, HomeGreetingWord.Evening)]
    public void The_local_clock_buckets_only_when_the_server_said_nothing(int hour, HomeGreetingWord expected)
    {
        Assert.Equal(expected, HomeLandingRules.GreetingWord(null, hour));
        Assert.Equal(expected, HomeLandingRules.GreetingWord("   ", hour));
    }

    [Fact]
    public void The_servers_greeting_wins_at_every_hour()
    {
        for (int hour = 0; hour < 24; hour++)
            Assert.Equal(HomeGreetingWord.Server, HomeLandingRules.GreetingWord("Good morning", hour));
    }

    [Theory]
    [InlineData("31l77y2al5lnn4mxa7ueknkrnjgq", true)]    // a 28-char user-id hash
    [InlineData("abcdefghijklmnopqrst", true)]            // exactly 20, no space
    [InlineData("abcdefghijklmnopqrs", false)]            // 19
    [InlineData("Christos Karapasias Longname", false)]   // long but has a space: a display name
    [InlineData("Christos", false)]
    public void A_long_spaceless_name_is_a_handle(string name, bool handle)
        => Assert.Equal(handle, HomeLandingRules.LooksLikeHandle(name));

    [Theory]
    [InlineData(1088f, 288f, 3)]
    [InlineData(900f, 288f, 3)]
    [InlineData(899f, 240f, 2)]
    [InlineData(600f, 240f, 2)]
    [InlineData(599f, 220f, 2)]
    public void The_editorial_destination_steps_at_900_and_600(float width, float height, int lines)
    {
        var m = HomeLandingRules.WideEditorial(width);
        Assert.Equal(height, m.Height);
        Assert.Equal(lines, m.Lines);
        Assert.Equal(2f * height + 20f, HomeLandingRules.TailExtent(width));
    }

    [Theory]
    [InlineData(true, 3, 40f)]      // hero + chips: the strip alone
    [InlineData(true, 0, 28f)]      // hero, no chips: the customize entry alone (defect #5 — no phantom strip)
    [InlineData(false, 3, 136f)]    // no hero + chips: greeting 84 + 12 + strip 40 (defect #4)
    [InlineData(false, 0, 84f)]     // no hero, no chips: the greeting alone
    public void The_chip_rows_estimate_has_exactly_the_arms_the_block_composes(bool hasHero, int chips, float expected)
        => Assert.Equal(expected, HomeLandingRules.ChipsContent(hasHero, chips));
}
