// ── Wavee.ShellTests/WinUiTierLayoutTests.Frame.cs — WinUI's frame at every tier (P6 W2a) ───────────────────────────────────────────────
//
// The frame family's own checks over the harness (`WinUiTierLayoutTests.cs`), on the laid-out nodes, never on pixels:
//
//   · WinUI draws the frame and the title bar itself (`winui.*` templates);
//   · the title bar is the board's two rows: the 40 tab strip over the 48 toolbar (88), the tier's search (the field 520 at Standard, 640 at
//     Huge, at most 520 at Small; the magnifier at Compact), Forward from Small, pin and Friends from Standard, ⋯ only at Small;
//   · the content card sits under the title bar beside the nav column and above the play bar (Mini 64 at Compact, 72 elsewhere);
//   · tabs off: the strip's row goes (one 48 bar), the toolbar keeps its place against the page, the Wavee mark shows from Small up, and tabs
//     on brings the strip back.

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class WinUiTierLayoutTests
{
    /// <summary>The board sizes the title bar is drawn at, one per tier (and Short).</summary>
    public static TheoryData<string> TitleBarSizes() => new()
    {
        "compact 400x860", "short 800x330", "small 800x600", "standard 1440x960", "huge 2560x1440", "huge4k 3840x2160",
        "edge 641", "edge 1008", "edge 1920", "edge 616 from above", "edge 983 from above", "edge 1895 from above",
    };

    [Theory, MemberData(nameof(TitleBarSizes))]
    public void The_title_bar_is_the_boards_two_rows_at_every_tier(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var s = Sizes[size];
        Assert.True(WinUiDraws(ViewKeys.ShellFrame.Id), "WinUI draws its own frame");
        Assert.True(WinUiDraws(ViewKeys.ShellTitleBar.Id), "WinUI draws its own title bar");
        var scene = shell.Host.Scene;
        RectF At(string key) => scene.AbsoluteRect(Node(shell, key));
        bool Shown(string key) => shell.FindPrefix(key).Any(n => scene.AbsoluteRect(n.Node).W > 0f && !scene.InCollapsedSubtree(n.Node));

        var bar = WinUiTiers.TitleBarOf(tiers);
        var frame = WinUiTiers.FrameOf(tiers);
        Assert.Same(WinUiTiers.TitleBar(s.Window), bar);

        // Two rows: the 40 strip over the 48 toolbar.
        var title = At(WinUiFrameLayout.TitleBarKey);
        Assert.Equal(frame.TitleBarH(tabs: true), title.H, 0.5f);
        Assert.Equal(WinUiTiers.TabStripH, At(WinUiFrameRules.TabRowKey).H, 0.5f);
        var toolbar = At(WinUiFrameRules.ToolbarKey);
        Assert.Equal(WinUiTiers.ToolbarH, toolbar.H, 0.5f);
        Assert.Equal(title.Y + WinUiTiers.TabStripH, toolbar.Y, 0.5f);

        // The toolbar's clusters per tier.
        Assert.True(Shown(WinUiFrameRules.BackKey), $"{size}: Back");
        Assert.Equal(bar.Forward, Shown(WinUiFrameRules.ForwardKey));
        Assert.Equal(bar.PinAndFriends, Shown(WinUiFrameRules.FriendsKey));
        Assert.Equal(bar.More, Shown(WinUiFrameRules.MoreKey));
        Assert.True(Shown(WinUiFrameRules.BellKey), $"{size}: the bell");
        Assert.Equal(!bar.SearchField, Shown(WinUiFrameRules.SearchButtonKey));
        Assert.Equal(bar.SearchField, Shown(WinUiFrameRules.SearchFieldKey));
        if (bar.SearchField)
        {
            var field = At(WinUiFrameRules.SearchFieldKey);
            Assert.True(field.W <= bar.SearchW + 0.5f, $"{size}: the search is {field.W}, over the tier's {bar.SearchW}");
            if (!bar.SearchFills) Assert.Equal(bar.SearchW, field.W, 0.5f);
            // On the window's centre line.
            Assert.Equal(s.Width / 2f, field.X + field.W / 2f, 1f);
            Assert.True(field.Y >= toolbar.Y && field.Y + field.H <= toolbar.Y + toolbar.H + 0.5f, $"{size}: the search sits in the toolbar");
        }

        // The content card under the title bar, beside the nav column, above the play bar (its height is the player lane's, W4a).
        var card = At(WinUiFrameLayout.ContentKey);
        var player = At(WinUiFrameLayout.PlayerBarKey);
        Assert.Equal(title.Y + title.H, card.Y, 0.5f);
        Assert.Equal(card.Y + card.H, player.Y, 0.5f);
        Assert.Equal(Sidebar.PresentedWidth.Peek(), card.X, 0.5f);
        Assert.Equal(frame.SquareCard, s.Window == WidthTier.Compact);
    });

    [Theory]
    [InlineData("compact 400x860")]
    [InlineData("small 800x600")]
    [InlineData("standard 1440x960")]
    [InlineData("huge 2560x1440")]
    public void Tabs_off_folds_the_strip_row_into_one_48_bar_and_back(string size) => AssertPage(Home, size, (shell, tiers) =>
    {
        var scene = shell.Host.Scene;
        RectF At(string key) => scene.AbsoluteRect(Node(shell, key));
        bool Shown(string key) => shell.FindPrefix(key).Any(n => scene.AbsoluteRect(n.Node).W > 0f && !scene.InCollapsedSubtree(n.Node));
        var frame = WinUiTiers.FrameOf(tiers);
        var bar = WinUiTiers.TitleBarOf(tiers);
        try
        {
            _ = ThemeHost.Apply(new OptionChange(WinUiTheme.Id, WinUiStructure.TabsQualifiedId, false));
            shell.RunFrames(40);
            var caps = ThemeHost.Facts.Peek().Capabilities;
            Assert.True(caps.Tabs && caps.HiddenTabStrip, "tabs off hides the strip and keeps the workspace");
            Assert.Equal(frame.TitleBarH(tabs: false), At(WinUiFrameLayout.TitleBarKey).H, 0.5f);
            Assert.False(Shown(WinUiFrameRules.TabRowKey), $"{size}: no strip row with tabs off");
            Assert.False(Shown(WinUiFrameRules.StripKey), $"{size}: no strip with tabs off");
            Assert.Equal(bar.Mark, Shown(WinUiFrameRules.MarkKey));
            var toolbar = At(WinUiFrameRules.ToolbarKey);
            Assert.Equal(0f, toolbar.Y, 0.5f);
            Assert.Equal(toolbar.Y + toolbar.H, At(WinUiFrameLayout.ContentKey).Y, 0.5f);
        }
        finally
        {
            _ = ThemeHost.Apply(new OptionChange(WinUiTheme.Id, WinUiStructure.TabsQualifiedId, true));
            shell.RunFrames(40);
        }
        Assert.Equal(frame.TitleBarH(tabs: true), At(WinUiFrameLayout.TitleBarKey).H, 0.5f);
        Assert.True(Shown(WinUiFrameRules.StripKey), $"{size}: the strip comes back");
    });
}
