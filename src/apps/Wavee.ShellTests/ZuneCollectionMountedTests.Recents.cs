// ── Wavee.ShellTests/ZuneCollectionMountedTests.Recents.cs — Zune's Recents page, mounted under Zune (themes plan 6.3; P5 Z7) ───────────────
//
// The app mounted under Zune (`ZuneHost`) at 1440 x 960 (Standard), against the fake dataset:
//
//   registered   Zune's own registration draws Pages.Recents (zune.recents.page)
//   page         the real shell draws Zune's page (its column and the overview aside on the frame's gutter), never Classic's masthead column
//   row 2        the band's row 2 is the four pivots ("all" first and lit) and the "overview" word, and a click on a pivot switches it
//
// Anything that loads waits on the wall clock (`UntilBackground`), never a frame budget. Every fact puts Classic back alone (`ZuneHost.Mounted`).

using FluentGpu.Foundation;
using FluentGpu.Pal;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class ZuneCollectionMountedTests
{
    const float RecentsW = 1440f, RecentsH = 960f;

    static void RecentsClick(Shell.HeadlessShell shell, NodeHandle node)
    {
        var r = shell.Host.Scene.AbsoluteRect(node);
        var p = new Point2(r.X + r.W / 2f, r.Y + r.H / 2f);
        shell.Window.QueueInput(new InputEvent(InputKind.PointerMove, p, 0, 0, TimestampMs: 2000));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, TimestampMs: 2010));
        shell.Window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, TimestampMs: 2060));
        shell.RunFrames(3);
    }

    static PageChrome? RecentsChrome() => Sidebar.DockedPaneProps.Band!.Chrome.Peek();

    static void GoRecents(Shell.HeadlessShell shell)
    {
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Recents));
        shell.RunFrames(10);
    }

    static void RecentsGoHome(Shell.HeadlessShell shell)
    {
        Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
        shell.RunFrames(10);
    }

    [Fact]
    public void Zune_draws_the_recents_page()
    {
        var builder = new FluentGpu.Theming.ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var registry = builder.Build();
        Assert.Equal("zune.recents.page", registry.TemplateIdOf(ViewKeys.RecentsPage.Id));
    }

    [Fact]
    public void Recents_mounts_zunes_page_with_the_overview_beside_the_list()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoRecents(shell);
                UntilBackground(shell, () => !shell.Find(ZuneRecentsRules.PageKey).IsNull && !shell.Find(ZuneRecentsRules.ColumnKey).IsNull,
                    "Zune's recents page and its column");
                Assert.Equal("zune.recents.page", ThemeHost.Registry.TemplateIdOf(ViewKeys.RecentsPage.Id));
                // Classic's column (the masthead, the pivot strip) is not drawn: the pivots are the band's row 2.
                Assert.True(shell.Find("recents:column").IsNull, "Classic's recents column is not drawn under Zune");

                // The page stands on the frame's gutter, inside the window: no horizontal overflow.
                var scene = shell.Host.Scene;
                var page = scene.AbsoluteRect(Node(shell, ZuneRecentsRules.PageKey));
                var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
                Assert.True(page.X >= content.X - 0.5f && page.X + page.W <= content.X + content.W + 0.5f, $"the page fits the content ({page} in {content})");

                // At Standard the overview stands beside the list at the record's width and gap.
                var layout = ZuneRecentsTiers.For(WidthTier.Standard);
                var aside = scene.AbsoluteRect(Node(shell, ZuneRecentsRules.AsideKey));
                Assert.True(MathF.Abs(aside.W - (layout.AsideGap + layout.AsideW)) < 1f, $"the overview's width ({aside.W})");
                var column = scene.AbsoluteRect(Node(shell, ZuneRecentsRules.ColumnKey));
                Assert.True(column.X + column.W <= aside.X + 0.5f, "the list column stands left of the overview");
            }
            finally { RecentsGoHome(shell); }
        }, RecentsW, RecentsH);

    [Fact]
    public void Recents_row_two_is_the_four_pivots_and_the_overview_word_and_a_click_switches_a_pivot()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoRecents(shell);
                UntilBackground(shell, () => RecentsChrome() is { Views.Count: 4 } && !shell.Find(ZuneBandRules.Row2Key).IsNull,
                    "the recents pivots in the band's row 2");
                var chrome = RecentsChrome()!;
                Assert.Equal(new[] { "all", "music", "podcasts", "artists" }, chrome.Views.Select(v => v.Id).ToArray());
                Assert.Equal("all", chrome.CurrentView?.Id);
                Assert.Contains(chrome.Actions, a => a.Id == "overview");
                Until(shell, () => shell.FindPrefix(ZuneBandRules.ViewKeyPrefix).Count == chrome.Views.Count, "every pivot drawn in row 2");

                // A click on the podcasts pivot lights it, and back through "all".
                RecentsClick(shell, Node(shell, ZuneBandRules.ViewKeyPrefix + "podcasts"));
                UntilBackground(shell, () => RecentsChrome()?.CurrentView?.Id == "podcasts", "the podcasts pivot is lit in row 2");
                Assert.False(shell.Find(ZuneBandRules.ViewKeyPrefix + "podcasts").IsNull, "the lit pivot is drawn");
                RecentsClick(shell, Node(shell, ZuneBandRules.ViewKeyPrefix + "all"));
                UntilBackground(shell, () => RecentsChrome()?.CurrentView?.Id == "all", "all is lit again");
            }
            finally { RecentsGoHome(shell); }
        }, RecentsW, RecentsH);
}
