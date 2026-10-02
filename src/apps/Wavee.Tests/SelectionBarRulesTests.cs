using Wavee;
using Xunit;

namespace Wavee.Tests;

/// <summary>Owner decision D3: the selection bar and the check lane — pure, engine-free.</summary>
public class SelectionBarRulesTests
{
    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, false)]
    [InlineData(1, false, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    [InlineData(40, false, true)]
    public void Bar_shows_for_two_or_more_or_one_when_armed(int count, bool armed, bool expected)
        => Assert.Equal(expected, SelectionBarRules.Visible(count, armed));

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, true)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    public void Checks_show_when_armed_or_two_or_more(int count, bool armed, bool expected)
        => Assert.Equal(expected, SelectionBarRules.ChecksVisible(count, armed));

    [Fact]
    public void Bar_never_shows_at_zero_even_when_armed()
        => Assert.False(SelectionBarRules.Visible(0, true));

    [Fact]
    public void Episode_args_carry_kind_count_and_actions()
    {
        int exits = 0, alls = 0;
        var a = SelectionLaneArgs.ForEpisodes(3, () => [], () => exits++, () => alls++);
        Assert.Equal(SelectionLaneKind.Episodes, a.Kind);
        Assert.Equal(3, a.Count);
        Assert.True(a.WasVisible);
        Assert.Empty(a.Tracks());
        Assert.False(a.Host().IsSome);
        a.Exit(); a.SelectAll(); a.SelectAll();
        Assert.Equal(1, exits);
        Assert.Equal(2, alls);
    }

    [Fact]
    public void Track_args_default_to_no_host_and_no_episodes()
    {
        var a = SelectionLaneArgs.ForTracks(2, () => [], () => { }, () => { });
        Assert.Equal(SelectionLaneKind.Tracks, a.Kind);
        Assert.Empty(a.Episodes());
        Assert.False(a.Host().IsSome);
    }
}
