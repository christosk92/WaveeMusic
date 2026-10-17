// ── Wavee.ShellTests/ZunePlaylistLikedMountedTests.cs — Zune's playlist and Liked Songs, mounted under Zune (themes plan 6.3; P5 Z6b1) ─────────────────
//
// The app mounted under Zune (`ZuneHost`) against the fake dataset (pl1: a four-song playlist; Liked Songs: the 166 liked tracks):
//
//   registered   Zune's own registration draws the ten playlist keys and the four Liked keys (zune.*), each under its own template id
//   playlist     at 1440 x 960 pl1 draws Zune's detail frame (the panorama, the identity column) over Zune's track table, and the page publishes its row 2:
//                the view "songs" (current), the actions play and shuffle, and the band draws them
//   liked        Liked Songs draws the frame's head arm (no rail: the panorama and the meta over the list) with Zune's table, and publishes play and
//                shuffle (its views are the library route's row, which stands)
//   rules        the cover looks Zune draws (stock, mosaic, tone) and how the engine's nine fold into them; the tier records (Compact targets are 40)
//
// There is no empty-playlist fixture in the fake dataset, so the lead of an empty playlist (Z-State-Empty-Playlist) is pinned through its rules: the field is the
// full width with the pill's corner at Compact and the board's 560 measure with a square corner elsewhere, and the lead reserves a height at every tier.
//
// Anything that loads waits on the wall clock (`UntilBackground`), never a frame budget. Every fact puts Classic back alone (`ZuneHost.Mounted`).

using FluentGpu.Foundation;
using Wavee;
using Wavee.Themes.Zune;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using static Wavee.ShellTests.ShellProbe;
using FluentGpu.Theming;

namespace Wavee.ShellTests;

public sealed class ZunePlaylistLikedMountedTests(ShellHost host)
{
    const float W = 1440f, H = 960f;

    static readonly Shell.Route s_playlist = Shell.For(EntityUri.Parse("spotify:playlist:pl1"), "Playlist");
    static readonly Shell.Route s_liked = new(Shell.RouteKind.Liked);

    const string ListHeaderKey = "zune.list.header", RowDropKey = "zune.row.drop";

    static PageChrome? Chrome() => Sidebar.DockedPaneProps.Band!.Chrome.Peek();

    // ── the registry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_draws_every_playlist_and_liked_key()
    {
        var builder = new ViewRegistryBuilder();
        ZuneStructure.RegisterOwn(builder);
        var registry = builder.Build();
        string[] ids =
        [
            ViewKeys.PlaylistCoverArt.Id, ViewKeys.PlaylistCoverEditor.Id, ViewKeys.PlaylistEditor.Id, ViewKeys.PlaylistOwner.Id,
            ViewKeys.PlaylistMembersFlyout.Id, ViewKeys.PlaylistAccess.Id, ViewKeys.PlaylistTune.Id, ViewKeys.PlaylistDaylist.Id,
            ViewKeys.PlaylistRecs.Id, ViewKeys.PlaylistRecRow.Id,
            ViewKeys.LikedCover.Id, ViewKeys.LikedCoverPicker.Id, ViewKeys.LikedCoverStyleFlyout.Id, ViewKeys.LikedChipBar.Id,
        ];
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            string template = registry.TemplateIdOf(id) ?? "";
            Assert.True(template.StartsWith("zune.", StringComparison.Ordinal), $"{id} is drawn by '{template}'");
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.Equal(ids.Length, templates.Count);
    }

    // ── the rules (pure) ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zune_folds_the_nine_liked_cover_looks_into_the_three_it_draws()
    {
        Assert.Equal(LikedCoverStyle.Stock, ZunePlaylistRules.LookOf(LikedCoverStyle.Stock));
        Assert.Equal(LikedCoverStyle.Tone, ZunePlaylistRules.LookOf(LikedCoverStyle.Tone));
        foreach (var style in new[] { LikedCoverStyle.Lens, LikedCoverStyle.Wall, LikedCoverStyle.Rainbow, LikedCoverStyle.Marquee, LikedCoverStyle.Feature,
                                      LikedCoverStyle.Mosaic, LikedCoverStyle.Stack })
            Assert.Equal(LikedCoverStyle.Mosaic, ZunePlaylistRules.LookOf(style));

        // The picker writes the engine's persisted index: every look Zune offers is in the engine's order.
        foreach (var look in ZunePlaylistRules.ZuneLooks)
        {
            int index = ZunePlaylistRules.PickerIndexOf(look);
            Assert.InRange(index, 0, LikedCoverRules.PickerOrder.Length - 1);
            Assert.Equal(look, LikedCoverRules.PickerOrder[index]);
        }
    }

    [Fact]
    public void The_tier_records_keep_every_target_and_the_empty_lead_sane()
    {
        foreach (var tier in new[] { WidthTier.Compact, WidthTier.Small, WidthTier.Standard, WidthTier.Huge, WidthTier.Huge4K })
        {
            var lay = ZunePlaylistTiers.For(tier);
            Assert.Equal(tier, lay.Tier);
            Assert.True(lay.Target >= 32f, $"{tier}: a command is at least 32");
            Assert.True(lay.CoverStrip >= 28f, $"{tier}: the cover strip");
            Assert.True(lay.LeadMinHeight > 0f && lay.LeadFieldH >= 40f, $"{tier}: the lead reserves a height and its field is a target");
        }
        Assert.True(ZunePlaylistTiers.Compact.Target >= 40f && ZunePlaylistTiers.Huge4K.Target >= 40f, "Compact and Huge 4K targets are 40");
        Assert.True(ZunePlaylistTiers.Compact.LeadFieldFull && ZunePlaylistTiers.Compact.LeadFieldRadius > 0f, "Compact: the full-width pill field");
        Assert.False(ZunePlaylistTiers.Standard.LeadFieldFull);
        Assert.Equal(0f, ZunePlaylistTiers.Standard.LeadFieldRadius);
        Assert.Equal(560f, ZunePlaylistRules.LeadFieldW);
        // A view mounted alone (no frame resolved the tiers) reads Standard's record.
        Assert.Same(ZunePlaylistTiers.Standard, ZunePlaylistTiers.Of(default));
    }

    // ── the playlist ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_playlist_draws_zunes_frame_with_zunes_track_table_and_publishes_its_row_two()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_playlist);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.FrameKey).IsNull, "Zune's detail frame on pl1");
            Assert.Equal("zune.detail.frame", ThemeHost.Registry.TemplateIdOf(ViewKeys.DetailFrame.Id));
            Assert.Equal("zune.playlist.owner", ThemeHost.Registry.TemplateIdOf(ViewKeys.PlaylistOwner.Id));
            Assert.True(shell.Find("detail:two-column").IsNull, "Classic's two-column page is not drawn");
            Assert.False(shell.Find(ZuneDetailRules.PanoramaKey).IsNull, "the panorama band");
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.RailKey).IsNull, "the identity column");
            UntilBackground(shell, () => shell.FindPrefix(RowDropKey).Count > 0 && !shell.Find(ListHeaderKey).IsNull, "Zune's track table on pl1");

            // The page stays inside the content card: no horizontal overflow of the frame.
            shell.RunFrames(60);   // the page's entrance slide (Shell.PageTransition) settles before the page's rect is read
            var scene = shell.Host.Scene;
            var frame = scene.AbsoluteRect(Node(shell, ZuneDetailRules.FrameKey));
            var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
            Assert.True(frame.X >= content.X - 0.5f && frame.X + frame.W <= content.X + content.W + 0.5f, $"the frame fits the page ({frame} in {content})");

            // Row 2: the view "songs" (current) and the actions play and shuffle; the band draws them.
            UntilBackground(shell, () => Chrome() is { Actions.Count: > 0 } c && c.CurrentView?.Id == "songs", "pl1's row 2");
            var chrome = Chrome()!;
            Assert.Contains(chrome.Actions, a => a.Kind == PageActionKind.Play);
            Assert.Contains(chrome.Actions, a => a.Kind == PageActionKind.Shuffle);
            UntilBackground(shell, () => !shell.Find(ZuneBandRules.Row2Key).IsNull, "the band's row 2");
            Assert.False(shell.Find(ZuneBandRules.ViewKeyPrefix + "songs").IsNull, "the songs view in the band");
            Assert.False(shell.Find(ZuneBandRules.ActionKeyPrefix + "play").IsNull, "the play action in the band");
            Assert.False(shell.Find(ZuneBandRules.ActionKeyPrefix + "shuffle").IsNull, "the shuffle action in the band");
        }, W, H);

    [Fact]
    public void A_compact_window_draws_a_playlist_in_the_vertical_arm_without_overflow()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_playlist);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.HeroKey).IsNull, "Zune's vertical hero on pl1 at Compact");
            Assert.True(shell.Find(ZuneDetailRules.RailKey).IsNull, "no identity column in the vertical arm");
            shell.RunFrames(60);
            var scene = shell.Host.Scene;
            var frame = scene.AbsoluteRect(Node(shell, ZuneDetailRules.FrameKey));
            var content = scene.AbsoluteRect(Node(shell, ZuneFrameLayout.ContentKey));
            Assert.True(frame.X >= content.X - 0.5f && frame.X + frame.W <= content.X + content.W + 0.5f, $"the frame fits the page ({frame} in {content})");
        }, 400f, 860f);

    // ── Liked Songs ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Liked_songs_draws_the_head_arm_over_zunes_table_and_publishes_play_and_shuffle()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_liked);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.FrameKey).IsNull, "Zune's detail frame on Liked Songs");
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.HeadKey).IsNull, "the head arm's meta line");
            Assert.True(shell.Find(ZuneDetailRules.RailKey).IsNull, "Liked Songs has no identity column");
            Assert.False(shell.Find(ZuneDetailRules.PanoramaKey).IsNull, "the panorama band");
            UntilBackground(shell, () => shell.FindPrefix(RowDropKey).Count > 0 && !shell.Find(ListHeaderKey).IsNull, "Zune's track table on Liked Songs");

            UntilBackground(shell, () => Chrome() is { Actions.Count: > 0 }, "Liked Songs' row 2 actions");
            var chrome = Chrome()!;
            Assert.Contains(chrome.Actions, a => a.Kind == PageActionKind.Play);
            Assert.Contains(chrome.Actions, a => a.Kind == PageActionKind.Shuffle);
            UntilBackground(shell, () => !shell.Find(ZuneBandRules.ActionKeyPrefix + "play").IsNull, "the play action in the band");
        }, W, H);
}
