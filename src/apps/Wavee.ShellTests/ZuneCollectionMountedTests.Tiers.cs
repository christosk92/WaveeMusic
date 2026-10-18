// ── Wavee.ShellTests/ZuneCollectionMountedTests.Tiers.cs — Zune's library on the Huge columns and the Short rung (themes plan 3.5, 6.3; P5 ZT7) ───
//
// The app mounted under Zune (`ZuneHost`) against the fake dataset:
//
//   2560 x 1440   the navigator and the pane fill the page column (the audit's open item: a 960 list beside a 440 pane left 816 of 2464 empty); no horizontal overflow
//   3840 x 2160   the same on the 2400 cap, centred (the page's content starts at the band's x)
//   1440 x 330    the Short rung: the tightened page keeps a row of the navigator on screen under a 28 head
//   400 x 330     Compact on the Short rung: the drill-in's head is ONE 40 row
//
// Anything that loads waits on the wall clock (`UntilBackground`), never a frame budget. Every fact puts Classic back alone (`ZuneHost.Mounted`).

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed partial class ZuneCollectionMountedTests
{
    /// <summary>Albums at <paramref name="w"/> x <paramref name="h"/>: the columns (the artists, Huge's artist hero, the navigator, the right column) fill most of the page's content width and never leave the page.</summary>
    void AlbumsFill(float w, float h, float content)
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoLibrary(shell, Shell.RouteKind.LibraryAlbums);
                UntilBackground(shell, () => !shell.Find(ZuneLibraryRules.NavKey).IsNull && !shell.Find(ZuneLibraryRules.PageKey).IsNull
                    && (!shell.Find(ZuneLibraryRules.PaneKey).IsNull || !shell.Find(ZuneLibraryRules.EmptyKey).IsNull), "the library's navigator and its right column");
                shell.RunFrames(60);   // the entrance slide and the columns' width ease settle
                var scene = shell.Host.Scene;
                var page = scene.AbsoluteRect(Node(shell, ZuneLibraryRules.PageKey));
                var nav = scene.AbsoluteRect(Node(shell, ZuneLibraryRules.NavKey));
                var right = scene.AbsoluteRect(Node(shell, !shell.Find(ZuneLibraryRules.PaneKey).IsNull ? ZuneLibraryRules.PaneKey : ZuneLibraryRules.EmptyKey));
                float gutter = ZuneLibraryTiers.For(w >= 3200f ? WidthTier.Huge4K : WidthTier.Huge, single: false).Gutter;
                float inner = MathF.Min(content, page.W - 2f * gutter);
                // The albums page leads with the "all artists" column (and, at Huge, the artist's hero column): the columns' reach starts at the first of them.
                float left = nav.X;
                if (!shell.Find(ZuneLibraryRules.GroupsKey).IsNull) left = MathF.Min(left, scene.AbsoluteRect(Node(shell, ZuneLibraryRules.GroupsKey)).X);
                float reach = right.X + right.W - left;
                Assert.True(right.X + right.W <= page.X + page.W + 0.5f, $"no horizontal overflow ({right} in {page})");
                Assert.True(left >= page.X - 0.5f, $"the first column starts inside the page ({left} in {page})");
                Assert.True(reach >= inner * 0.8f, $"the two columns fill the page column ({reach} of {inner})");
                Assert.True(right.W <= 1200.5f, $"the pane stops at its cap ({right.W})");
            }
            finally { GoHome(shell); }
        }, w, h);

    [Fact]
    public void Albums_at_2560_fill_the_page_column() => AlbumsFill(2560f, 1440f, 2400f);

    [Fact]
    public void Albums_at_3840_fill_the_2400_column() => AlbumsFill(3840f, 2160f, 2400f);

    [Fact]
    public void The_short_rung_keeps_a_row_of_the_navigator_on_screen()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoLibrary(shell, Shell.RouteKind.LibraryAlbums);
                UntilBackground(shell, () => !shell.Find(ZuneLibraryRules.NavKey).IsNull && !shell.Find(ZuneLibraryRules.HeadKey).IsNull, "the library at 330 tall");
                shell.RunFrames(60);
                var scene = shell.Host.Scene;
                var head = scene.AbsoluteRect(Node(shell, ZuneLibraryRules.HeadKey));
                var nav = scene.AbsoluteRect(Node(shell, ZuneLibraryRules.NavKey));
                Assert.True(head.H <= ZuneLibraryTiers.For(WidthTier.Standard, single: false).HeadH + 0.5f, $"the head is one row ({head})");
                Assert.True(nav.H >= 40f, $"a row of the navigator stays on screen ({nav})");
                Assert.True(head.Y + head.H <= nav.Y + 0.5f, $"the head stands over the navigator ({head} over {nav})");
            }
            finally { GoHome(shell); }
        }, W, 330f);

    [Fact]
    public void Compact_on_the_short_rung_draws_a_one_row_head_of_40()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            try
            {
                GoLibrary(shell, Shell.RouteKind.LibraryAlbums);
                UntilBackground(shell, () => !shell.Find(ZuneLibraryRules.CollapsedKey).IsNull && !shell.Find(ZuneLibraryRules.HeadKey).IsNull, "the drill-in at 400 x 330");
                shell.RunFrames(60);
                var head = shell.Host.Scene.AbsoluteRect(Node(shell, ZuneLibraryRules.HeadKey));
                var content = shell.Host.Scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
                Assert.True(head.H >= 40f - 0.5f && head.H <= 40f + 0.5f, $"the head is one 40 row ({head})");
                Assert.True(head.X >= content.X - 0.5f && head.X + head.W <= content.X + content.W + 0.5f, $"the head fits the page ({head} in {content})");
            }
            finally { GoHome(shell); }
        }, 400f, 330f);
}
