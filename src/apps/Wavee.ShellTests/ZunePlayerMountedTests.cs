// ── Wavee.ShellTests/ZunePlayerMountedTests.cs — Zune's play bar and panel bodies, mounted (P5 Z4a) ──────────────────────────────────
//
// The real shell under Zune (`ZuneHost`) at 1440 x 960 (the Standard window tier) while a fixture track plays:
//
//   registered   every key Z4a owns is drawn by a `zune.*` template (the stand-in's Classic views are gone for them)
//   policies     the bar's six levels (0 / 640 / 760 / 1240 / 1920 / 3200, hold 24, the reserved 72) and the levels each window tier may use;
//                the panel 340 wide and never resized, full height, a sheet / floating / inline / two docked by window tier, always
//                floating with panelMode AlwaysOverlay
//   the bar      its keys are Zune's; the seek row is the 2-px line at the bar's top edge; the disc is the level's (Z-Part-PlayBar: 40);
//                the identity column is the full level's 320; the bar fills its 72 slot at the window's foot
//   the panel    it docks at 340 at Standard; the queue body draws its mode words; the lyrics body has ONE title (the pivot head's): its
//                header is the meta row alone, with no heading-sized text and no close of its own
//
// Anything that loads (the fixture's rows, the panel's body) is waited for in real time (`UntilBackground`).

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ZunePlayerMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f;
    const string Track = "spotify:track:tr40";

    static RectF Rect(Shell.HeadlessShell shell, string key) => shell.Host.Scene.AbsoluteRect(Node(shell, key));

    static void ClosePanel(Shell.HeadlessShell shell)
    {
        Shell.PanelHost.Shared.Close();
        shell.RunFrames(30);
    }

    // ── the registry and the policies ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_draws_every_key_of_the_player_lane()
        => host.Run(() =>
        {
            ZuneHost.Activate();
            try
            {
                (string Id, string Template)[] expected =
                [
                    (ViewKeys.PlayerBar.Id, "zune.player.bar"),
                    (ViewKeys.SeekRail.Id, "zune.player.seek-rail"),
                    (ViewKeys.SeekChapters.Id, "zune.player.seek-chapters"),
                    (ViewKeys.ChapterTip.Id, "zune.player.chapter-tip"),
                    (ViewKeys.SeekReadout.Id, "zune.player.seek-readout"),
                    (ViewKeys.TimeLabel.Id, "zune.player.time-label"),
                    (ViewKeys.DevicePicker.Id, "zune.player.device-picker"),
                    (ViewKeys.OnMediaTransport.Id, "zune.player.on-media-transport"),
                    (ViewKeys.NowPlayingHero.Id, "zune.player.now-playing-hero"),
                    (ViewKeys.DeckStyleFlyout.Id, "zune.player.deck-style-flyout"),
                    (ViewKeys.LyricsPeek.Id, "zune.player.lyrics-peek"),
                    (ViewKeys.PanelFriends.Id, "zune.panel.friends"),
                    (ViewKeys.PanelLyricsHeader.Id, "zune.panel.lyrics-header"),
                    (ViewKeys.PanelNextUp.Id, "zune.panel.next-up"),
                    (ViewKeys.QueuePanel.Id, "zune.queue.panel"),
                ];
                foreach (var (id, template) in expected)
                    Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
            }
            finally { ZuneHost.Restore(); }
        });

    [Fact]
    public void The_bar_policy_is_the_boards_six_levels_with_the_24_hold()
    {
        var bar = ZunePolicies.For(ZuneParameters.Default).PlayerBar;
        Assert.Same(ZunePlayerTiers.Policy, bar);
        Assert.Empty(bar.Validate());
        float[] starts = [0f, 640f, 760f, 1240f, 1920f, 3200f];
        Assert.Equal(starts, bar.Tiers.Select(t => t.MinWidth).ToArray());
        Assert.Equal(24f, bar.Hysteresis);
        Assert.Equal(ZuneTiers.BarReserveH, bar.Height);

        // The levels each window tier may use (the records'), and each level's identity column is its look's (the view finds the look by it).
        Assert.Equal(new PlayerBarWindowRule((int)ZuneBarLevel.Mini, (int)ZuneBarLevel.Mini), bar.WindowRule((int)WidthTier.Compact));
        Assert.Equal(new PlayerBarWindowRule((int)ZuneBarLevel.Narrow, (int)ZuneBarLevel.Medium), bar.WindowRule((int)WidthTier.Small));
        Assert.Equal(new PlayerBarWindowRule((int)ZuneBarLevel.Medium, (int)ZuneBarLevel.Full), bar.WindowRule((int)WidthTier.Standard));
        Assert.Equal(new PlayerBarWindowRule((int)ZuneBarLevel.Wide, (int)ZuneBarLevel.Wide), bar.WindowRule((int)WidthTier.Huge));
        Assert.Equal(new PlayerBarWindowRule((int)ZuneBarLevel.Big, (int)ZuneBarLevel.Big), bar.WindowRule((int)WidthTier.Huge4K));
        for (int i = 0; i < bar.Tiers.Length; i++)
        {
            var look = ZunePlayerTiers.Look((ZuneBarLevel)i);
            Assert.Equal(look.IdW, bar.Tiers[i].LeftW);
            Assert.Equal(look.Disc, bar.Tiers[i].PrimaryBox);
            Assert.Same(look, ZunePlayerTiers.LookOf(new PlayerBarMetrics { LeftW = bar.Tiers[i].LeftW }, WidthTier.Standard));
        }
        // Every level carries the device route and the identity; the heart from medium, the full tool set from full.
        Assert.Equal(0, (int)(bar.Tiers[(int)ZuneBarLevel.Narrow].VisibleSlots & PlayerBarSlots.LikeSlot));
        Assert.NotEqual(0, (int)(bar.Tiers[(int)ZuneBarLevel.Medium].VisibleSlots & PlayerBarSlots.LikeSlot));
        Assert.NotEqual(0, (int)(bar.Tiers[(int)ZuneBarLevel.Full].VisibleSlots & PlayerBarSlots.Expand));
        // The bar's slot is the reserved 72 at every tier but Huge 4K's 88; the mini bar is 64 inside it.
        Assert.Equal(ZuneTiers.BarReserveH, ZunePlayerTiers.Bar(WidthTier.Compact).SlotH);
        Assert.True(ZunePlayerTiers.Bar(WidthTier.Compact).BarH < ZunePlayerTiers.Bar(WidthTier.Compact).SlotH);
        Assert.Equal(ZuneTiers.Frame(WidthTier.Huge4K).BarH, ZunePlayerTiers.Bar(WidthTier.Huge4K).SlotH);
    }

    [Fact]
    public void The_panel_policy_is_340_full_height_and_placed_by_window_tier()
    {
        var panel = ZunePolicies.For(ZuneParameters.Default).Panel;
        Assert.Empty(panel.Validate());
        Assert.Equal(ZunePlayerTiers.PanelW, panel.MinWidth);
        Assert.Equal(ZunePlayerTiers.PanelW, panel.MaxWidth);
        Assert.Equal(ZunePlayerTiers.PanelW, panel.ClampWidth(500f));
        Assert.True(panel.FullHeight);
        Assert.Equal(PanelPlacement.Sheet, panel.For(WidthTier.Compact).Placement);
        Assert.Equal(PanelPlacement.Float, panel.For(WidthTier.Small).Placement);
        Assert.Equal(PanelPlacement.Dock, panel.For(WidthTier.Standard).Placement);
        Assert.Equal(1, panel.For(WidthTier.Standard).MaxDocked);
        Assert.True(panel.For(WidthTier.Huge).DocksTwo);
        Assert.True(panel.For(WidthTier.Huge4K).DocksTwo);

        var overlay = ZunePolicies.For(ZuneParameters.Default with { PanelMode = ZunePanelMode.AlwaysOverlay }).Panel;
        Assert.Equal(PanelPlacement.Float, overlay.For(WidthTier.Standard).Placement);
        Assert.Equal(PanelPlacement.Float, overlay.For(WidthTier.Huge4K).Placement);
        Assert.Equal(PanelPlacement.Sheet, overlay.For(WidthTier.Compact).Placement);

        var one = ZunePolicies.For(ZuneParameters.Default with { MaxDockedPanes = 1 }).Panel;
        Assert.False(one.For(WidthTier.Huge).DocksTwo);
    }

    // ── the bar ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_bar_draws_the_two_px_seek_line_over_the_full_level_with_the_boards_disc()
        => ZuneHost.Mounted(host, shell =>
        {
            Play(Track, fromMs: 20_000);
            UntilBackground(shell, () => !shell.Find(ZunePlayerLayout.DiscKey).IsNull && !shell.Find(ZunePlayerLayout.SeekRowKey).IsNull,
                "the Zune bar's disc and seek row mounted");
            shell.RunFrames(30);

            Assert.Equal(WidthTier.Standard, ThemeHost.Facts.Peek().Tiers.Window);
            var look = ZunePlayerTiers.Look(ZuneBarLevel.Full);
            var slot = Rect(shell, ZuneFrameLayout.PlayerBarKey);
            var bar = Rect(shell, ZunePlayerLayout.BarKey);
            Assert.Equal(ZuneTiers.BarReserveH, slot.H, 0.5f);
            Assert.Equal(H, slot.Y + slot.H, 0.5f);                                // at the window's foot
            Assert.Equal(slot.H, bar.H, 0.5f);                                      // the desktop bar fills its slot
            Assert.Equal(W, bar.W, 0.5f);

            var seek = Rect(shell, ZunePlayerLayout.SeekRowKey);
            Assert.Equal(ZunePlayerTiers.SeekLineH, seek.H, 0.5f);                 // the seek row is the 2-px line ...
            Assert.Equal(bar.Y, seek.Y, 0.5f);                                     // ... along the bar's top edge
            Assert.Equal(bar.W, seek.W, 0.5f);                                     // ... full width
            var row = Rect(shell, ZunePlayerLayout.RowKey);
            Assert.Equal(bar.Y + bar.H, row.Y + row.H, 0.5f);
            Assert.Equal(bar.H - ZunePlayerTiers.SeekLineH, row.H, 0.5f);          // the transport row is the rest (70)

            var disc = Rect(shell, ZunePlayerLayout.DiscKey);
            Assert.Equal(look.Disc, disc.W, 0.5f);
            Assert.Equal(disc.W, disc.H, 0.5f);
            Assert.True(disc.Y >= row.Y - 0.5f && disc.Y + disc.H <= row.Y + row.H + 0.5f, "the disc sits in the transport row");

            var left = Rect(shell, ZunePlayerLayout.LeftKey);
            Assert.Equal(look.IdW, left.W, 0.5f);                                  // 1440 is the full level: the 320 identity column
            Assert.Equal(look.PadLeft, left.X, 0.5f);
            var right = Rect(shell, ZunePlayerLayout.RightKey);
            Assert.True(left.X + left.W <= disc.X && disc.X + disc.W <= right.X, "identity · transport · tools, in that order, never overlapping");
            Assert.True(right.X + right.W <= W + 0.5f, "the tools stay in the window");
        }, W, H);

    // ── the panel ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_panel_docks_at_340_at_standard_and_the_queue_draws_its_mode_words()
        => ZuneHost.Mounted(host, shell =>
        {
            Play(Track, fromMs: 20_000);
            shell.RunFrames(20);
            try
            {
                Shell.PanelHost.Shared.SetMode(Shell.RailMode.Queue);
                UntilBackground(shell, () => !shell.Find(ZunePlayerLayout.QueueModesKey).IsNull, "the queue body's mode words");
                shell.RunFrames(40);
                Assert.Equal(PanelPlacement.Dock, Shell.Ui.PanelPlacement.Peek());
                Assert.Equal(ZunePlayerTiers.PanelW, Shell.Ui.RailWidth.Peek(), 0.5f);
                var panel = Rect(shell, ZuneFrameRules.PanelHostKey);
                Assert.Equal(ZunePlayerTiers.PanelW, panel.W, 0.5f);
                Assert.Equal(W, panel.X + panel.W, 0.5f);

                var modes = Rect(shell, ZunePlayerLayout.QueueModesKey);
                Assert.Equal(ZuneQueueRules.ModeRowH, modes.H, 0.5f);
                Assert.True(modes.X >= panel.X - 0.5f && modes.X + modes.W <= panel.X + panel.W + 0.5f, "the mode words are in the panel");
            }
            finally { ClosePanel(shell); }
        }, W, H);

    [Fact]
    public void The_lyrics_body_has_one_title_the_pivot_heads()
        => ZuneHost.Mounted(host, shell =>
        {
            Play(Track, fromMs: 20_000);
            shell.RunFrames(20);
            try
            {
                Shell.PanelHost.Shared.SetMode(Shell.RailMode.Lyrics);
                UntilBackground(shell, () => !shell.Find(ZunePlayerLayout.LyricsMetaKey).IsNull, "the lyrics header's meta row");
                shell.RunFrames(40);
                var scene = shell.Host.Scene;

                // The pivot head carries the title ("lyrics", current) and the one close.
                Assert.False(shell.Find(ZuneFrameRules.PanelPivotKeyPrefix + PanelMode.Lyrics).IsNull, "the lyrics pivot");
                // Count in the primary pane only: the frame also mounts the second pane's host (Huge's queue beside it), closed and zero
                // wide at Standard, whose own panel draws its own head with the same keys.
                var primary = new HashSet<NodeHandle>(Subtree(scene, Node(shell, ZuneFrameRules.PanelHostKey)));
                Assert.Single(shell.FindPrefix(ZuneFrameRules.PanelCloseKey), f => f.Key == ZuneFrameRules.PanelCloseKey && primary.Contains(f.Node));

                // The header is the meta row alone: as tall as the board's 28, nothing heading-sized in it (no second title).
                var meta = Node(shell, ZunePlayerLayout.LyricsMetaKey);
                Assert.Equal(ZunePanelBodies.MetaRowH, scene.AbsoluteRect(meta).H, 0.5f);
                foreach (var n in Subtree(scene, meta))
                {
                    float size = scene.Layout(n).TextStyle.SizeDip;
                    Assert.True(size <= ZunePanelBodies.MetaGlyph + 0.5f, $"a text of {size} DIP in the lyrics header: only the meta row's words and glyphs belong there");
                }
                // ... and it sits under the head, inside the panel.
                var head = scene.AbsoluteRect(shell.FindPrefix(ZuneFrameRules.PanelHeadKey).Single(f => f.Key == ZuneFrameRules.PanelHeadKey && primary.Contains(f.Node)).Node);
                Assert.True(scene.AbsoluteRect(meta).Y >= head.Y + head.H - 0.5f, "the meta row is under the pivot head");
            }
            finally { ClosePanel(shell); }
        }, W, H);
}
