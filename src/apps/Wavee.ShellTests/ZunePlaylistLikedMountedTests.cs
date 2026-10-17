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
//   tuning (T2)  playlist and Liked set their title at the ALBUM's panorama step per tier (Zune is a large typeface: no kind has a smaller step of its
//                own) with a band that clears the descender (A3) and a long title that WRAPS, the band hugging it; an owned playlist's panorama IS
//                its inline editor (Z6b1 must-fix: the rename is reachable); Liked draws no page-bar play / shuffle while the band carries them (A4)
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

    /// <summary>pl0: the account's own playlist (the fake dataset's owner capabilities: its metadata is editable).</summary>
    static readonly Shell.Route s_ownPlaylist = Shell.For(EntityUri.Parse("spotify:playlist:pl0"), "Playlist");
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

    [Fact]
    public void Playlist_and_liked_use_the_albums_panorama_step_and_the_band_clears_its_descender()
    {
        // The page's title is the record's step for EVERY kind: Liked used to take its own 40/48 (Compact, Small) and 64/72 (Standard), a playlist 64/72
        // up to Standard. Zune is a large typeface, so the playlist and Liked pages read the album's record (64 compact and small, 120 Standard, 150 Huge).
        foreach (var (tier, size) in new[]
                 {
                     (WidthTier.Compact, 64f), (WidthTier.Small, 64f), (WidthTier.Standard, 120f), (WidthTier.Huge, 150f), (WidthTier.Huge4K, 150f),
                 })
        {
            var lay = ZuneDetailTiers.For(tier);
            float step = lay.PanoramaSize > 0f ? lay.PanoramaSize : lay.PanoramaLarge ? 120f : 64f;      // the role's 120 where the record carries none
            Assert.Equal(size, step);
            Assert.True(lay.PanoramaH >= size * 1.25f + 8f - 0.5f, $"{tier}: the band clears its descender ({lay.PanoramaH})");
            Assert.True(lay.PanoramaWraps, $"{tier}: a long title wraps and the band hugs it");
            Assert.Equal(2, lay.PanoramaLines);
        }
        Assert.Equal(158f, ZuneDetailRules.Standard.PanoramaH);   // Liked Songs at Standard: the 120 band, not 88
    }

    // ── the playlist ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_owned_playlists_panorama_is_its_inline_editor()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_ownPlaylist);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.PanoramaKey).IsNull, "Zune's panorama on pl0");
            UntilBackground(shell, () => !shell.Find(ZunePlaylistRules.EditorReadKey).IsNull, "the inline editor's read arm on the owned playlist");
            shell.RunFrames(60);
            var scene = shell.Host.Scene;
            var band = scene.AbsoluteRect(Node(shell, ZuneDetailRules.PanoramaKey));
            var editor = scene.AbsoluteRect(Node(shell, ZunePlaylistRules.EditorReadKey));
            // The rename is reachable where the title is (Z6b1 must-fix): the read arm stands inside the panorama band, not only in a folded column.
            Assert.True(editor.X >= band.X - 0.5f && editor.Y >= band.Y + ZuneDetailRules.Standard.PanoramaNudgeY - 0.5f &&   // lifted by the tier's optical nudge, as the title is
                        editor.X + editor.W <= band.X + band.W + 0.5f
                        && editor.Y + editor.H <= band.Y + band.H + 0.5f, $"the editor ({editor}) stands in the panorama band ({band})");
            Assert.True(editor.W > 0f && editor.H > 0f, "the editor is laid out");
        }, W, H);

    [Fact]
    public void The_playlist_and_liked_panoramas_take_the_albums_step_mounted()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_playlist);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.RailKey).IsNull, "pl1's identity column");
            shell.RunFrames(60);
            var band = shell.Host.Scene.AbsoluteRect(Node(shell, ZuneDetailRules.PanoramaKey));
            Assert.True(band.H >= ZuneDetailRules.Standard.PanoramaH - 0.5f, $"the playlist band is the album's 120 band at least ({band})");

            Shell.GoTo(s_liked);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.HeadKey).IsNull, "Liked Songs' head arm");
            shell.RunFrames(60);
            var liked = shell.Host.Scene.AbsoluteRect(Node(shell, ZuneDetailRules.PanoramaKey));
            Assert.True(liked.H >= ZuneDetailRules.Standard.PanoramaH - 0.5f, $"Liked uses the album's step: its band is the 120 band at least, not a smaller 64 one ({liked})");
        }, W, H);

    [Fact]
    public void A_long_title_wraps_in_the_compact_hero_and_the_band_grows_instead_of_truncating()
        => ZuneHost.Mounted(host, shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(s_liked);
            UntilBackground(shell, () => !shell.Find(ZuneDetailRules.HeroKey).IsNull, "Zune's vertical hero on Liked Songs at 300");
            shell.RunFrames(60);
            // "Liked Songs" at 64 is wider than a 300 window's page column, so it breaks by words over two lines and the band hugs both (it was one clipped line):
            // the band is taller than the one-line floor, at least two lines of the 72 step.
            var band = shell.Host.Scene.AbsoluteRect(Node(shell, ZuneDetailRules.PanoramaKey));
            Assert.True(band.H >= 2f * 72f - 1f, $"the Compact band hugs a two-line 64/72 title ({band}), over the {ZuneDetailRules.Compact.PanoramaH} one-line floor");
        }, 300f, 860f);

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

            // One home per verb (audit A4): the band carries play and shuffle, so the page bar leads with the chips, not a second play / shuffle.
            shell.RunFrames(30);
            Assert.True(shell.Find("lead:play").IsNull, "no page-bar play while the band carries it");
            Assert.True(shell.Find("lead:shuffle").IsNull, "no page-bar shuffle while the band carries it");
        }, W, H);
}
