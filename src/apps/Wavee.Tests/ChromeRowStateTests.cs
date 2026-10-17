// ── Wavee.Tests/ChromeRowStateTests.cs — the chrome row's allocation as the title bar view reads it (themes plan 6.0, T4b) ──
//
// The allocator (`Shell.Chrome`, pinned by `MergedChromeLayoutTests` and `ShellChromeSearchFieldWidthTests`) stays core and keeps its
// promotion hysteresis; the structure's title bar draws what it decided. `Shell.RowStateOf` is the one hand-over: the allocation as the
// contract's plain value (`ChromeRowState`), with the identity chip and the search form as the contract's enums. Everything here is
// that mapping, engine-free: no flag is lost or flipped, each chip form and each search form maps to its own, and the tabs island's
// width is the allocator's own.

using Wavee.Theming.Contracts;
using Xunit;

namespace Wavee.Tests;

public class ChromeRowStateTests
{
    static Shell.Chrome Resolve(float width, Shell.FrameRules.ChipForm chip = Shell.FrameRules.ChipForm.Profile)
        => Shell.Chrome.Resolve(width, 220f, null, chip);

    [Theory]
    [InlineData(Shell.FrameRules.ChipForm.Profile, ChromeChipForm.Profile)]
    [InlineData(Shell.FrameRules.ChipForm.Connecting, ChromeChipForm.Connecting)]
    [InlineData(Shell.FrameRules.ChipForm.Reconnect, ChromeChipForm.Reconnect)]
    [InlineData(Shell.FrameRules.ChipForm.SignIn, ChromeChipForm.SignIn)]
    public void Each_identity_chip_form_maps_to_its_own(Shell.FrameRules.ChipForm core, ChromeChipForm contract)
        => Assert.Equal(contract, Shell.RowStateOf(Resolve(1600f, core)).Chip);

    [Fact]
    public void Every_chip_form_of_the_auth_fold_has_a_distinct_contract_form()
    {
        var seen = new HashSet<ChromeChipForm>();
        foreach (var auth in Enum.GetValues<Shell.AuthState>())
            seen.Add(Shell.RowStateOf(Resolve(1600f, Shell.FrameRules.ChipFor(auth))).Chip);
        Assert.Equal(4, seen.Count);
    }

    [Theory]
    [InlineData(700f, SearchForm.Icon)]
    [InlineData(1180f, SearchForm.Field)]
    [InlineData(1600f, SearchForm.Field)]
    public void The_search_form_follows_the_allocators_mode(float width, SearchForm form)
        => Assert.Equal(form, Shell.RowStateOf(Resolve(width)).Search);

    [Fact]
    public void The_icon_form_carries_the_icon_width_and_the_field_form_a_width_the_row_can_seat()
    {
        var icon = Shell.RowStateOf(Resolve(700f));
        Assert.Equal(SearchForm.Icon, icon.Search);
        Assert.Equal(Shell.Layout.ChromeSearchIconW, icon.SearchWidth);

        var field = Shell.RowStateOf(Resolve(1600f));
        Assert.Equal(SearchForm.Field, field.Search);
        Assert.InRange(field.SearchWidth, Shell.Layout.ChromeSearchMinW, Shell.Layout.ChromeSearchMaxW);
    }

    [Theory]
    [InlineData(500f)]
    [InlineData(700f)]
    [InlineData(1000f)]
    [InlineData(1200f)]
    [InlineData(1360f)]
    [InlineData(1800f)]
    public void No_flag_of_the_allocation_is_lost_or_flipped(float width)
    {
        var c = Resolve(width);
        var s = Shell.RowStateOf(c);
        Assert.Equal(c.ShowName, s.ShowName);
        Assert.Equal(c.ShowActions, s.ShowActions);
        Assert.Equal(c.ShowForward, s.ShowForward);
        Assert.Equal(c.ShowBack, s.ShowBack);
        Assert.Equal(c.ShowNewTab, s.ShowNewTab);
        Assert.Equal(c.ShowTrailing, s.ShowTrailing);
        Assert.Equal(c.SearchWidth, s.SearchWidth);
    }

    [Theory]
    [InlineData(700f)]
    [InlineData(1180f)]
    [InlineData(1600f)]
    public void The_tabs_island_width_is_the_allocators(float width)
    {
        var c = Resolve(width);
        Assert.Equal(c.TabIslandWidth, Shell.RowStateOf(c).TabIslandWidth);
    }

    [Fact]
    public void The_promotion_hysteresis_survives_the_hand_over()
    {
        // The actions stage enters at the budget-checked 1200, and a PROMOTION is re-resolved 40 DIP narrower: from a row that did not
        // have them, 1230 is not yet promoted (its reserve, 1190, is below the entry) and 1240 is. The contract value carries the
        // allocator's decision; it never makes a second one. A demotion is immediate.
        var before = Shell.Chrome.Resolve(1100f, 220f);
        Assert.False(Shell.RowStateOf(before).ShowActions);
        Assert.False(Shell.RowStateOf(Shell.Chrome.Resolve(1230f, 220f, before)).ShowActions);
        var promoted = Shell.Chrome.Resolve(1240f, 220f, before);
        Assert.True(Shell.RowStateOf(promoted).ShowActions);
        Assert.False(Shell.RowStateOf(Shell.Chrome.Resolve(1190f, 220f, promoted)).ShowActions);
    }
}
