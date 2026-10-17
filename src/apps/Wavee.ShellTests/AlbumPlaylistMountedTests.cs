// ── Wavee.ShellTests/AlbumPlaylistMountedTests.cs — the album and playlist sub-contracts' views, mounted (themes plan 6.1, T9b) ──────────────────────
//
// The pictures of the album page's and the playlist page's detail-frame slots are the structure's since T9b; the core hosts keep the gates, the state, the
// verbs and the decisions (`Wavee.Tests`: `AlbumPlaylistContractTests`, `AlbumPageRulesTests`, `PlaylistHeaderRulesTests`, `DaylistClock*Tests`...). What only a
// mounted tree can say:
//
//   registered   Classic is the registered template of every key
//   artist line  the billed-artist line draws the pile in the Wide mode, the lead face and the "+N" in Medium, the names alone in Narrow, and the chevron only
//                when the click opens a list
//   notice       a verdict is a strip, nothing to say is a zero-height box
//   release      About this release draws its facts, notes and versions once, draws nothing without any, and the outer padding is the hero arm's
//   stack        a capped list section draws its rows in their keyed places, at the plated row's height
//   region       the trailing band is ONE keyed region
//   sections     the music-video section draws the hero card for ONE video and the list sections where the host placed them; no section, no band
//   editor       the inline editor draws the read arm with its pencil for an editable text, the edit arm keyed on its own size, the status chip in its two states,
//                and nothing editable for a viewer who cannot edit
//   access       the copy-invite call to action exists for an administrator and swaps its glyph and word when the link is copied
//   daylist      the flip cells stand while counting, and neither Rolling nor an idle strip draws them
//   smoke        the views with no keyed output (the cover, the owner block, the flyouts, the Tune panel) mount and run their first frames

using FluentGpu.Dsl;
using FluentGpu.Signals;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Xunit;
using Rules = Wavee.Album.PageRules;

namespace Wavee.ShellTests;

public sealed class AlbumPlaylistMountedTests(ShellHost host)
{
    static ViewHarnessOptions UnderClassic() => new() { Composition = ClassicTheme.Create() };

    static readonly Action s_noop = static () => { };

    // ── registered ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Classic_is_the_registered_template_of_every_album_and_playlist_key()
        => host.Run(() =>
        {
            (string Id, string Template)[] expected =
            [
                (ViewKeys.DetailNotice.Id, "classic.detail.notice"),
                (ViewKeys.DetailVerticalTrailing.Id, "classic.detail.vertical-trailing"),
                (ViewKeys.AlbumArtistLine.Id, "classic.album.artist-line"),
                (ViewKeys.AlbumArtistFlyout.Id, "classic.album.artist-flyout"),
                (ViewKeys.AlbumCountdown.Id, "classic.album.countdown"),
                (ViewKeys.AlbumReleasePanel.Id, "classic.album.release-panel"),
                (ViewKeys.AlbumTrailing.Id, "classic.album.trailing"),
                (ViewKeys.AlbumSections.Id, "classic.album.sections"),
                (ViewKeys.AlbumAside.Id, "classic.album.aside"),
                (ViewKeys.AlbumTrailStack.Id, "classic.album.trail-stack"),
                (ViewKeys.AlbumUnresolved.Id, "classic.album.unresolved"),
                (ViewKeys.PlaylistCoverArt.Id, "classic.playlist.cover-art"),
                (ViewKeys.PlaylistCoverEditor.Id, "classic.playlist.cover-editor"),
                (ViewKeys.PlaylistEditor.Id, "classic.playlist.editor"),
                (ViewKeys.PlaylistOwner.Id, "classic.playlist.owner"),
                (ViewKeys.PlaylistMembersFlyout.Id, "classic.playlist.members-flyout"),
                (ViewKeys.PlaylistAccess.Id, "classic.playlist.access"),
                (ViewKeys.PlaylistTune.Id, "classic.playlist.tune"),
                (ViewKeys.PlaylistDaylist.Id, "classic.playlist.daylist"),
                (ViewKeys.PlaylistRecs.Id, "classic.playlist.recs"),
                (ViewKeys.PlaylistRecRow.Id, "classic.playlist.rec-row"),
            ];
            foreach (var (id, template) in expected)
                Assert.Equal(template, ThemeHost.Registry.TemplateIdOf(id));
        });

    // ── the billed-artist line ───────────────────────────────────────────────────────────────────────────────────────────

    static AlbumArtistLineProps Line(Detail.RailArtistMode mode, bool menu)
        => new(mode, new ArtistLineSpec(
            [new Controls.Face("Ada", null), new Controls.Face("Bea", null), new Controls.Face("Cy", null)],
            "Ada, Bea, Cy", 3, 0, 320f, "All artists", menu, s_noop, null, null));

    [Fact]
    public void The_artist_line_draws_the_pile_in_the_wide_mode_with_its_chevron_when_the_click_opens_a_list()
        => ViewHarness.With(host, ViewKeys.AlbumArtistLine, Line(Detail.RailArtistMode.Wide, menu: true), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("artist-line"));
            Assert.True(harness.Contains("trigger"));
            Assert.True(harness.Contains("faces:pile"));
            Assert.True(harness.Contains("names"));
            Assert.True(harness.Contains("chevron"));
            Assert.False(harness.Contains("faces:lead"));
            Assert.False(harness.Contains("plus"));
            var line = harness.AbsoluteBounds(harness.Node("artist-line"));
            Assert.True(line.H >= Detail.RailArtistLine.HeightOf(Detail.RailArtistMode.Wide) - 0.5f, "the line keeps the mode's reserved height");
        }, UnderClassic());

    [Fact]
    public void The_artist_line_draws_the_lead_face_and_the_plus_n_in_the_medium_mode()
        => ViewHarness.With(host, ViewKeys.AlbumArtistLine, Line(Detail.RailArtistMode.Medium, menu: true), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("faces:lead"));
            Assert.True(harness.Contains("plus"));
            Assert.False(harness.Contains("faces:pile"));
        }, UnderClassic());

    [Fact]
    public void The_artist_line_draws_the_names_alone_in_the_narrow_mode_and_no_chevron_for_a_single_artist()
        => ViewHarness.With(host, ViewKeys.AlbumArtistLine, Line(Detail.RailArtistMode.Narrow, menu: false), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("names"));
            Assert.False(harness.Contains("faces:pile"));
            Assert.False(harness.Contains("faces:lead"));
            Assert.False(harness.Contains("chevron"));
        }, UnderClassic());

    [Fact]
    public void An_album_with_no_resolvable_artist_has_no_attribution_row()
        => ViewHarness.With(host, ViewKeys.AlbumArtistLine, new AlbumArtistLineProps(Detail.RailArtistMode.Wide, null), harness =>
        {
            harness.RunFrames(5);
            Assert.False(harness.Contains("artist-line"));
            Assert.False(harness.Contains("trigger"));
        }, UnderClassic());

    // ── the notice strip ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_verdict_is_a_strip_and_nothing_to_say_is_a_zero_height_box()
    {
        ViewHarness.With(host, ViewKeys.DetailNotice, new DetailNoticeProps(DetailNotice.Deleted, false, s_noop), harness =>
        {
            harness.RunFrames(5);
            Assert.True(harness.AbsoluteBounds(harness.Node("detail-notice")).H > 0f);
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.DetailNotice, new DetailNoticeProps(DetailNotice.None, false, null), harness =>
        {
            harness.RunFrames(5);
            Assert.Equal(0f, harness.AbsoluteBounds(harness.Node("detail-notice")).H, 0.5f);
        }, UnderClassic());

        // the minified verdict is only stated where the page does not self-heal a thin tracklist
        ViewHarness.With(host, ViewKeys.DetailNotice, new DetailNoticeProps(DetailNotice.MinifiedAlbum, false, null), harness =>
        {
            harness.RunFrames(5);
            Assert.Equal(0f, harness.AbsoluteBounds(harness.Node("detail-notice")).H, 0.5f);
        }, UnderClassic());
    }

    // ── about this release ───────────────────────────────────────────────────────────────────────────────────────────────

    static AlbumReleasePanelProps Release(bool outerPadding)
        => new(outerPadding, "12 March 2021", "Released", "Ada Records", ["℗ 2021 Ada Records"],
            [new AlbumVersionChoice("Deluxe · 2022 · ALBUM", s_noop)]);

    [Fact]
    public void About_this_release_draws_its_facts_its_notes_and_its_versions_once()
        => ViewHarness.With(host, ViewKeys.AlbumReleasePanel, Release(outerPadding: false), harness =>
        {
            harness.RunFrames(10);
            foreach (string key in new[] { "release-panel", "release-about", "release-title", "release-facts", "fact:released", "fact:label",
                                           "release-notes", "note:0", "release-versions" })
                Assert.True(harness.Contains(key), key);
            var panel = harness.AbsoluteBounds(harness.Node("release-panel"));
            var title = harness.AbsoluteBounds(harness.Node("release-title"));
            Assert.Equal(panel.X, title.X, 0.5f);   // the rail's row supplies its own padding: the panel adds none
        }, UnderClassic());

    [Fact]
    public void About_this_release_carries_the_hero_arms_outer_padding()
        => ViewHarness.With(host, ViewKeys.AlbumReleasePanel, Release(outerPadding: true), harness =>
        {
            harness.RunFrames(10);
            var panel = harness.AbsoluteBounds(harness.Node("release-panel"));
            var title = harness.AbsoluteBounds(harness.Node("release-title"));
            Assert.Equal(panel.X + Spacing.L, title.X, 0.5f);
        }, UnderClassic());

    [Fact]
    public void About_this_release_draws_nothing_without_a_fact_a_note_or_a_version()
        => ViewHarness.With(host, ViewKeys.AlbumReleasePanel, new AlbumReleasePanelProps(true, null, "", null, [], []), harness =>
        {
            harness.RunFrames(5);
            Assert.False(harness.Contains("release-panel"));
        }, UnderClassic());

    // ── the stack ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_list_section_draws_its_rows_in_their_keyed_places_at_the_plated_rows_height()
    {
        TrailRowData Merch(int n) => new(TrailRowKind.Merch, Merch: new MerchRowData("merch:" + n, "Tour tee " + n, "$25", null, null));
        ViewHarness.With(host, ViewKeys.AlbumTrailStack, new AlbumTrailStackProps("Merch", 7, 3, s_noop, [Merch(1), Merch(2), Merch(3)]), harness =>
        {
            harness.RunFrames(10);
            Assert.Equal(3, harness.FindPrefix("merch:").Count);
            var first = harness.AbsoluteBounds(harness.Node("merch:1"));
            var second = harness.AbsoluteBounds(harness.Node("merch:2"));
            Assert.Equal(ClassicAlbumLayout.MerchRowH, first.H, 0.5f);
            Assert.True(second.Y >= first.Y + first.H - 0.5f, "the rows stack in order");
        }, UnderClassic());
    }

    // ── the region ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_trailing_band_is_one_keyed_region()
        => ViewHarness.With(host, ViewKeys.AlbumTrailing,
            new AlbumTrailingProps("trailing-skel:fixture", new Rules.TrailingShape(About: true, Fans: true, RowBlocks: 1), static () => true,
                static () => new BoxEl { Height = 40f }),
            harness =>
            {
                harness.RunFrames(10);
                Assert.True(harness.Contains("trailing-skel:fixture"));
            }, UnderClassic());

    // ── the sections ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static AlbumVideoCard Video(int member)
        => new("video:" + member, "spotify:track:fixture" + member, "Ada - Live", null, "Music video · 3:12", "3:12", s_noop, static () => null,
            static () => null);

    [Fact]
    public void The_music_video_section_draws_the_hero_card_for_one_video_and_the_list_sections_where_the_host_placed_them()
        => ViewHarness.With(host, ViewKeys.AlbumSections, new AlbumSectionsProps
        {
            Any = true, Videos = [Video(7)], Fans = [], Lists = [new BoxEl { Key = "stack:fixture", Height = 30f }],
        }, harness =>
        {
            harness.RunFrames(10);
            var hero = harness.AbsoluteBounds(harness.Node("video:7"));
            Assert.True(hero.H >= ClassicAlbumLayout.HeroThumbH + 2f * Spacing.M - 0.5f, "the card holds its thumb inside its padding");
            Assert.True(harness.Contains("stack:fixture"));
            Assert.True(harness.AbsoluteBounds(harness.Node("stack:fixture")).Y >= hero.Y + hero.H - 0.5f, "the lists stand under the video");
        }, UnderClassic());

    [Fact]
    public void No_section_draws_no_band()
        => ViewHarness.With(host, ViewKeys.AlbumSections, new AlbumSectionsProps { Any = false, Videos = [], Fans = [], Lists = [] }, harness =>
        {
            harness.RunFrames(5);
            Assert.Empty(harness.FindPrefix("video:"));
            Assert.Empty(harness.FindPrefix("fan:"));
        }, UnderClassic());

    [Fact]
    public void The_vertical_arms_trailing_body_stands_the_prerelease_card_the_release_facts_and_the_band_in_the_rails_keyed_rows()
        => ViewHarness.With(host, ViewKeys.DetailVerticalTrailing,
            new DetailVerticalTrailingProps(new BoxEl { Height = 20f }, new BoxEl { Height = 20f }, new BoxEl { Height = 20f }), harness =>
            {
                harness.RunFrames(60);   // the late row fades up and shoves: measure where they land
                var pre = harness.AbsoluteBounds(harness.Node("vtrail:prerelease"));
                var release = harness.AbsoluteBounds(harness.Node("vtrail:release"));
                var trailing = harness.AbsoluteBounds(harness.Node("vtrail:trailing"));
                Assert.True(release.Y >= pre.Y + pre.H - 0.5f);
                Assert.True(trailing.Y >= release.Y + release.H - 0.5f);
            }, UnderClassic());

    [Fact]
    public void The_vertical_arms_trailing_body_draws_only_the_pieces_the_page_has()
        => ViewHarness.With(host, ViewKeys.DetailVerticalTrailing, new DetailVerticalTrailingProps(null, null, new BoxEl { Height = 20f }), harness =>
        {
            harness.RunFrames(10);
            Assert.False(harness.Contains("vtrail:prerelease"));
            Assert.False(harness.Contains("vtrail:release"));
            Assert.True(harness.Contains("vtrail:trailing"));
        }, UnderClassic());

    // ── the inline editor ────────────────────────────────────────────────────────────────────────────────────────────────

    static PlaylistEditorProps Editor(bool description = false, bool editable = true, bool editing = false, int status = 0, float size = 28f)
    {
        var draft = new Signal<string>("Road trip");
        return new PlaylistEditorProps(description, size, description ? float.NaN : 34f, "Road trip", editable, editing, status, () => draft,
            s_noop, s_noop, s_noop, static _ => { }, static () => default, static _ => { });
    }

    [Fact]
    public void An_editable_text_draws_the_read_arm_with_its_pencil_and_a_viewer_who_cannot_edit_gets_the_plain_run()
    {
        ViewHarness.With(host, ViewKeys.PlaylistEditor, Editor(), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("pl-swap"));
            Assert.True(harness.Contains("pl-read"));
            Assert.False(harness.Contains("pl-edit"));
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.PlaylistEditor, Editor(editable: false), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("pl-swap"));
            Assert.False(harness.Contains("pl-read"));
        }, UnderClassic());
    }

    [Fact]
    public void The_edit_arm_is_keyed_on_its_own_font_size_so_a_new_size_remounts_the_field()
    {
        ViewHarness.With(host, ViewKeys.PlaylistEditor, Editor(editing: true, size: 28f), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("pl-edit"));
            Assert.False(harness.Contains("pl-read"));
            Assert.True(harness.Contains("pl-field:28"));
        }, UnderClassic());

        // the description's field is 12-DIP text whatever width the frame gave it
        ViewHarness.With(host, ViewKeys.PlaylistEditor, Editor(description: true, editing: true, size: 320f), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("pl-field:12"));
        }, UnderClassic());
    }

    [Fact]
    public void The_status_chip_stands_under_the_editor_in_its_two_states_and_the_descriptions_idle_row_is_closed()
    {
        ViewHarness.With(host, ViewKeys.PlaylistEditor, Editor(status: 1), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("pl-status"));
            Assert.True(harness.Contains("ico:saving"));
            Assert.True(harness.Contains("txt:saving"));
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.PlaylistEditor, Editor(status: 2), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("ico:saved"));
            Assert.True(harness.Contains("txt:saved"));
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.PlaylistEditor, Editor(description: true, status: 0), harness =>
        {
            harness.RunFrames(10);
            Assert.False(harness.Contains("pl-status"));
            Assert.Equal(0f, harness.AbsoluteBounds(harness.Node("pl-status-row")).H, 0.5f);
        }, UnderClassic());
    }

    // ── the access panel ─────────────────────────────────────────────────────────────────────────────────────────────────

    static PlaylistAccessProps Access(bool admin, int inviteState)
    {
        var collaborative = new Signal<bool>(false);
        var visible = new Signal<bool>(true);
        return new PlaylistAccessProps(300f, admin, admin, inviteState, true, false, false, () => collaborative, () => visible, s_noop,
            static _ => { }, static _ => { });
    }

    [Fact]
    public void The_copy_invite_call_to_action_exists_for_an_administrator_and_swaps_its_glyph_and_word_when_the_link_is_copied()
    {
        ViewHarness.With(host, ViewKeys.PlaylistAccess, Access(admin: true, inviteState: 0), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("cta-ico:0"));
            Assert.True(harness.Contains("cta-txt:0"));
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.PlaylistAccess, Access(admin: true, inviteState: 2), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("cta-ico:2"));
            Assert.True(harness.Contains("cta-txt:1"));
        }, UnderClassic());

        ViewHarness.With(host, ViewKeys.PlaylistAccess, Access(admin: false, inviteState: 0), harness =>
        {
            harness.RunFrames(10);
            Assert.False(harness.Contains("cta-ico:0"));
        }, UnderClassic());
    }

    // ── the daylist strip ────────────────────────────────────────────────────────────────────────────────────────────────

    static PlaylistDaylistProps Daylist(DaylistCountdown.Phase phase, bool cells, string? caption = "Next update at 6:00 AM")
        => new(phase, false, caption, cells, 3, 12, 45, Compact: true, static () => default);

    [Fact]
    public void The_flip_cells_stand_while_counting_and_neither_rolling_nor_an_idle_strip_draws_them()
    {
        string[] cells = ["h1", "h0", "c1", "m1", "m0", "c2", "s1", "s0"];
        ViewHarness.With(host, ViewKeys.PlaylistDaylist, Daylist(DaylistCountdown.Phase.Counting, cells: true), harness =>
        {
            harness.RunFrames(10);
            foreach (string key in cells) Assert.True(harness.Contains(key), key);
        }, UnderClassic());

        foreach (var props in new[]
        {
            Daylist(DaylistCountdown.Phase.Counting, cells: false),   // the clock mode that shows only the update time
            Daylist(DaylistCountdown.Phase.Rolling, cells: true, caption: null),
            Daylist(DaylistCountdown.Phase.Idle, cells: true, caption: null),
        })
            ViewHarness.With(host, ViewKeys.PlaylistDaylist, props, harness =>
            {
                harness.RunFrames(10);
                foreach (string key in cells) Assert.False(harness.Contains(key), key);
            }, UnderClassic());
    }

    // ── the recommendations ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_recommended_section_draws_its_header_row_over_its_rows()
        => ViewHarness.With(host, ViewKeys.PlaylistRecs, new PlaylistRecsProps(Loading: false, Empty: true, s_noop, []), harness =>
        {
            harness.RunFrames(10);
            Assert.True(harness.Contains("rec:header"));
            Assert.Empty(harness.FindPrefix("rec:").Where(n => n.Key != "rec:header").ToList());
        }, UnderClassic());

    // ── smoke: the views with no keyed output ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_cover_the_owner_block_the_flyouts_and_the_tune_panel_mount_and_run_their_first_frames()
    {
        ViewHarness.With(host, ViewKeys.PlaylistCoverArt, new PlaylistCoverArtProps(200f, 256, null, null), harness => harness.RunFrames(5), UnderClassic());

        ViewHarness.With(host, ViewKeys.PlaylistCoverEditor,
            new PlaylistCoverEditorProps(new PlaylistCoverArtProps(200f, 256, null, null), static () => false, s_noop, static _ => { }),
            harness => harness.RunFrames(5), UnderClassic());

        Controls.Face[] faces = [new("Ada", null), new("Bea", null)];
        foreach (bool pile in new[] { true, false })
            ViewHarness.With(host, ViewKeys.PlaylistOwner,
                new PlaylistOwnerProps(320f, pile, pile ? faces : [], 0, "2 collaborators", "Ada", null, s_noop, true, s_noop, s_noop,
                    static _ => { }, static _ => { }),
                harness => harness.RunFrames(5), UnderClassic());

        PersonFlyoutRow[] rows = [new("Ada", null, s_noop), new("Bea", null, null)];
        ViewHarness.With(host, ViewKeys.AlbumArtistFlyout, new PersonFlyoutProps(rows), harness => harness.RunFrames(5), UnderClassic());
        ViewHarness.With(host, ViewKeys.PlaylistMembersFlyout, new PersonFlyoutProps(rows), harness => harness.RunFrames(5), UnderClassic());

        TuneOption[] options =
        [
            new("chill", "Chill", TuningOptionKind.Choice), new("focus", "Focus", TuningOptionKind.Choice), new("reset", null, TuningOptionKind.Reset),
        ];
        ViewHarness.With(host, ViewKeys.PlaylistTune, new PlaylistTuneProps(336f, options, "chill", static _ => { }), harness => harness.RunFrames(5),
            UnderClassic());

        ViewHarness.With(host, ViewKeys.AlbumUnresolved, AlbumUnresolvedProps.Instance, harness => harness.RunFrames(5), UnderClassic());
    }
}
