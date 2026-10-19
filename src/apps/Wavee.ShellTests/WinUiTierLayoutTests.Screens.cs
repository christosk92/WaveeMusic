// ── Wavee.ShellTests/WinUiTierLayoutTests.Screens.cs — WinUI's Settings page at every tier size and edge (themes plan 3.5, 6.2; P6 W8b) ──────────
//
// The Settings page in the real frame under WinUI, at the plan sizes and every threshold edge (the harness's table), on the facts and the records, never on pixels:
//
//   · RECORD PER TIER. The title node sits on the frame's inset (the lint's edge) and is the record's size; the cards' column is no wider than the record's cap (1000, 1200 at
//     Huge) and sits on the page's left edge; the page never overflows the content card;
//   · THE SEVEN. The tab strip draws the seven tabs of WinUI's grouping and the open tab is the deep link's (`appearance`, `storage`, `general`);
//   · NO LAYOUT JUMP. A tier crossing with the page open keeps the page's, the title's and the strip's nodes (nothing remounts).

using FluentGpu.Foundation;
using FluentGpu.Scene;
using Wavee;
using Wavee.Themes.WinUi;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class WinUiTierLayoutTests
{
    /// <summary>Settings on Appearance (the deep link `settings/appearance`): the page keys its root `settings:page`.</summary>
    public static readonly TierPage SettingsAppearance = new("settings", new Shell.Route(Shell.RouteKind.Settings, default, Entities.Strings.Intern("appearance".AsSpan())), "settings:page");

    [Theory, MemberData(nameof(PageSizes))]
    public void The_settings_page_holds_its_tier_layout(string size)
        => AssertPage(SettingsAppearance, size, (shell, tiers) =>
        {
            var scene = shell.Host.Scene;
            var layout = WinUiSettingsTiers.Of(tiers);
            var content = scene.AbsoluteRect(Node(shell, WinUiFrameLayout.ContentKey));

            // The title is on the frame's inset: the lint's edge (content card left + the tier's inset). Every WinUI page keys its title alike, so a parked page
            // (Home, kept alive behind Settings) has one too: Settings' is the one inside the content card.
            var titles = shell.FindPrefix(WinUiFrameLayout.PageTitleKey).Where(f => f.Key == WinUiFrameLayout.PageTitleKey)
                .Select(f => scene.AbsoluteRect(f.Node)).Where(r => r.X >= content.X - 0.5f && r.Y >= content.Y - 0.5f).ToList();
            Assert.True(titles.Count == 1, $"{size}: {titles.Count} page titles inside the content card");
            var title = titles[0];
            Assert.True(MathF.Abs(title.X - (content.X + layout.InsetX)) <= 1.5f, $"{size}: the title is at {title.X}, the inset edge is {content.X + layout.InsetX}");
            Assert.True(title.H >= layout.TitleLine - 0.5f, $"{size}: the title's box is {title.H}, under its {layout.TitleLine} line");

            // The cards' column is capped (1000, 1200 at Huge) and left-aligned; it never runs out of the page.
            var cards = scene.AbsoluteRect(Node(shell, "settings:cards"));
            Assert.True(MathF.Abs(cards.X - title.X) <= 1.5f, $"{size}: the cards' column is at {cards.X}, the title's edge is {title.X}");
            if (layout.CapsColumn) Assert.True(cards.W <= layout.ColumnMaxW + 0.5f, $"{size}: the cards' column is {cards.W} wide, over the {layout.ColumnMaxW} cap");
            Assert.True(cards.X + cards.W <= content.X + content.W + 0.5f, $"{size}: the cards run outside the content card");

            // The seven tabs, with Appearance open.
            var strip = Node(shell, "settings:tabs");
            Assert.True(scene.AbsoluteRect(strip).W > 0f, $"{size}: the tab strip has no width");
            Assert.Equal(7, WinUiPolicies.For(WinUiParameters.Default).Settings.Count);
        });

    [Theory]
    [InlineData("general", "settings:tab:general")]
    [InlineData("appearance", "settings:tab:appearance")]
    [InlineData("storage-cache", "settings:tab:storage-cache")]
    [InlineData("privacy-diagnostics", "settings:tab:privacy-diagnostics")]
    [InlineData("about", "settings:tab:about")]
    [InlineData("logs", "settings:tab:privacy-diagnostics")]       // an old alias lands on the tab that holds the area it opened
    [InlineData("downloads", "settings:tab:storage-cache")]
    [InlineData("shortcuts", "settings:tab:general")]
    [InlineData("lyrics", "settings:tab:appearance")]
    public void A_deep_link_opens_its_tab_in_the_real_page(string slug, string laneKey)
        => Mounted(shell =>
        {
            shell.RunFrames(20);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Settings, default, Entities.Strings.Intern(slug.AsSpan())));
            Until(shell, () => shell.FindPrefix(laneKey).Count > 0, slug + " opens " + laneKey);
        }, 1440f, 960f);

    [Fact]
    public void Crossing_a_tier_with_settings_open_keeps_the_page_and_its_strip()
        => Mounted(shell =>
        {
            shell.RunFrames(20);
            Shell.GoTo(SettingsAppearance.Route);
            Until(shell, () => shell.FindPrefix("settings:page").Count > 0, "Settings on screen");
            shell.RunFrames(40);
            var page = Node(shell, "settings:page");
            var strip = Node(shell, "settings:tabs");
            var title = Node(shell, WinUiFrameLayout.PageTitleKey);
            // 1440 -> 800 crosses Standard -> Small; 800 -> 400 crosses Small -> Compact. The nodes are the same ones after each settle.
            foreach (float width in new[] { 800f, 400f, 800f, 1440f })
            {
                shell.Resize(width, 900f);
                shell.RunFrames(60);
                Assert.Equal(page, Node(shell, "settings:page"));
                Assert.Equal(strip, Node(shell, "settings:tabs"));
                Assert.Equal(title, Node(shell, WinUiFrameLayout.PageTitleKey));
            }
        }, 1440f, 900f);
}
