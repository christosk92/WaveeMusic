// ── Wavee.Tests/AlbumPlaylistContractTests.cs — the album and playlist sub-contracts of the detail frame and Classic's registration (themes plan 6.0, T9b) ──
//
// T9b gives the album page's and the playlist page's pictures a key each (`Theming\Contracts\ViewKeys.DetailAlbumPlaylist.cs`); the core hosts
// (`Album.FacePileHost`, `Playlist.InlineEditor`, `Playlist.OwnerBlock`...) keep the gates, the state and the verbs and mount them. What is pure about the contract:
// the ids (a published id never changes), Classic registering every key under a distinct template id, the equality the props promise (a record that carries
// closures a host rebuilds per render never compares equal to another instance, a plain-data record compares by value so an untouched recommendation row
// re-renders nothing), and the numbers the pictures own that the core's skeleton reserves are pinned against.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Wavee.Theming.Contracts;
using Xunit;
using Rules = Wavee.Album.PageRules;

namespace Wavee.Tests;

public class AlbumPlaylistContractTests
{
    static readonly (string Id, string Template)[] s_keys =
    [
        (ViewKeys.DetailNotice.Id, "classic.detail.notice"),
        (ViewKeys.DetailVerticalTrailing.Id, "classic.detail.vertical-trailing"),
        (ViewKeys.AlbumArtistLine.Id, "classic.album.artist-line"),
        (ViewKeys.AlbumArtistFlyout.Id, "classic.album.artist-flyout"),
        (ViewKeys.AlbumCountdown.Id, "classic.album.countdown"),
        (ViewKeys.AlbumReleasePanel.Id, "classic.album.release-panel"),
        (ViewKeys.AlbumTrailing.Id, "classic.album.trailing"),
        (ViewKeys.AlbumSections.Id, "classic.album.sections"),
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

    [Fact]
    public void The_published_ids_are_the_plans()
    {
        Assert.Equal("Detail.Notice", ViewKeys.DetailNotice.Id);
        Assert.Equal("Detail.VerticalTrailing", ViewKeys.DetailVerticalTrailing.Id);
        Assert.Equal("Album.ArtistLine", ViewKeys.AlbumArtistLine.Id);
        Assert.Equal("Album.ArtistFlyout", ViewKeys.AlbumArtistFlyout.Id);
        Assert.Equal("Album.Countdown", ViewKeys.AlbumCountdown.Id);
        Assert.Equal("Album.ReleasePanel", ViewKeys.AlbumReleasePanel.Id);
        Assert.Equal("Album.Trailing", ViewKeys.AlbumTrailing.Id);
        Assert.Equal("Album.Sections", ViewKeys.AlbumSections.Id);
        Assert.Equal("Album.TrailStack", ViewKeys.AlbumTrailStack.Id);
        Assert.Equal("Album.Unresolved", ViewKeys.AlbumUnresolved.Id);
        Assert.Equal("Playlist.CoverArt", ViewKeys.PlaylistCoverArt.Id);
        Assert.Equal("Playlist.CoverEditor", ViewKeys.PlaylistCoverEditor.Id);
        Assert.Equal("Playlist.Editor", ViewKeys.PlaylistEditor.Id);
        Assert.Equal("Playlist.Owner", ViewKeys.PlaylistOwner.Id);
        Assert.Equal("Playlist.MembersFlyout", ViewKeys.PlaylistMembersFlyout.Id);
        Assert.Equal("Playlist.Access", ViewKeys.PlaylistAccess.Id);
        Assert.Equal("Playlist.Tune", ViewKeys.PlaylistTune.Id);
        Assert.Equal("Playlist.Daylist", ViewKeys.PlaylistDaylist.Id);
        Assert.Equal("Playlist.Recs", ViewKeys.PlaylistRecs.Id);
        Assert.Equal("Playlist.RecRow", ViewKeys.PlaylistRecRow.Id);
    }

    [Fact]
    public void Classic_registers_every_album_and_playlist_key_under_a_distinct_template_id()
    {
        var classic = ClassicTheme.Create();
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, expected) in s_keys)
        {
            string template = classic.Registry.TemplateIdOf(id) ?? "";
            Assert.Equal(expected, template);
            Assert.True(templates.Add(template), "template id registered twice: " + template);
        }
        Assert.Empty(classic.Validate());
    }

    [Fact]
    public void Records_that_carry_per_render_closures_never_compare_equal_to_another_instance()
    {
        // the artist line's faces list is mutated in place by its host: an "equal" record would keep the old faces
        Assert.False(new AlbumArtistLineProps(Detail.RailArtistMode.Wide, null).Equals(new AlbumArtistLineProps(Detail.RailArtistMode.Wide, null)));

        PersonFlyoutProps Flyout() => new([new PersonFlyoutRow("Ada", null, null)]);
        Assert.False(Flyout().Equals(Flyout()));

        AlbumReleasePanelProps Release() => new(false, "March 2021", "Released", null, [], []);
        var release = Release();
        Assert.True(release.Equals(release));
        Assert.False(release.Equals(Release()));

        PlaylistRecsProps Recs() => new(false, false, static () => { }, []);
        Assert.False(Recs().Equals(Recs()));

        AlbumTrailStackProps Stack() => new("More by Ada", 7, 5, static () => { }, []);
        Assert.False(Stack().Equals(Stack()));
    }

    [Fact]
    public void A_plain_data_record_compares_by_value_so_an_untouched_row_re_renders_nothing()
    {
        Action<int> add = static _ => { };
        Action<int> play = static _ => { };
        Assert.Equal(new PlaylistRecRowProps(7, false, add, play), new PlaylistRecRowProps(7, false, add, play));
        Assert.NotEqual(new PlaylistRecRowProps(7, false, add, play), new PlaylistRecRowProps(7, true, add, play));
        Assert.NotEqual(new PlaylistRecRowProps(7, false, add, play), new PlaylistRecRowProps(8, false, add, play));

        Func<ColorF> accent = static () => default;
        Assert.Equal(new AlbumCountdownProps(false, TimeSpan.FromSeconds(5), false, accent, default),
                     new AlbumCountdownProps(false, TimeSpan.FromSeconds(5), false, accent, default));
        Assert.NotEqual(new AlbumCountdownProps(false, TimeSpan.FromSeconds(5), false, accent, default),
                        new AlbumCountdownProps(false, TimeSpan.FromSeconds(4), false, accent, default));

        Assert.Same(AlbumUnresolvedProps.Instance, AlbumUnresolvedProps.Instance);
    }

    // ── the numbers the pictures own, against the reserves the core's skeleton states ─────────────────────────────────────────────────

    [Fact]
    public void The_music_video_hero_keeps_its_numbers_and_its_fab_is_the_video_shapes()
    {
        Assert.Equal(200f, ClassicAlbumLayout.HeroThumbW);
        Assert.Equal(116f, ClassicAlbumLayout.HeroThumbH);
        // "The hero's badge and a cell's badge are the same object": the shelf cell is `Shape.Video`, whose FAB is 44 and
        // visible at rest; the hero's overlay takes that FAB from the shape rather than restating it.
        Assert.Equal(44f, ClassicAlbumLayout.HeroFab);
        Assert.Equal(Shape.Video.Fab, ClassicAlbumLayout.HeroFab);
        Assert.Equal(PlayReveal.Always, Shape.Video.Play);
    }

    [Fact]
    public void The_skeletons_video_reserve_is_the_heros_own_thumb()
    {
        Assert.Equal(ClassicAlbumLayout.HeroThumbW, Rules.SkelVideoThumbW);
        Assert.Equal(ClassicAlbumLayout.HeroThumbH, Rules.SkelVideoThumbH);
        // the shelf arm's cards fit between these two (16:9 cards want more room than the 148-188 square-card range)
        Assert.True(ClassicAlbumLayout.VideoCardMinW < ClassicAlbumLayout.VideoCardMaxW);
        Assert.Equal(16f / 9f, ClassicAlbumLayout.VideoAspect, 0.0001f);
    }

    [Fact]
    public void A_merch_row_and_a_blank_row_are_the_plated_rows_floor()
    {
        Assert.Equal(Rules.SkelRowH, ClassicAlbumLayout.MerchRowH);
        Assert.Equal(Rules.SkelRowH, ClassicAlbumLayout.PlaceholderRowH);
    }

    [Fact]
    public void The_invite_pill_is_the_labelled_arm_from_260_and_the_round_arm_below_it_and_unconstrained()
    {
        Assert.False(ClassicPlaylistLayout.InviteIsWide(float.NaN));
        Assert.False(ClassicPlaylistLayout.InviteIsWide(float.PositiveInfinity));
        Assert.False(ClassicPlaylistLayout.InviteIsWide(259.9f));
        Assert.True(ClassicPlaylistLayout.InviteIsWide(260f));
        Assert.True(ClassicPlaylistLayout.InviteIsWide(320f));
    }

    [Fact]
    public void The_owner_row_is_reserved_at_the_taller_arms_height()
    {
        // the collaborator pile's framed face is taller than the owner run's 24 DIP portrait and the 28 DIP invite pill:
        // flipping "Collaborative" swaps the arms, so the row is as tall as the tallest of them, always
        Assert.Equal(Controls.FaceOuter, ClassicPlaylistLayout.OwnerRowHeight);
        Assert.True(ClassicPlaylistLayout.OwnerRowHeight >= ClassicPlaylistLayout.OwnerAvatar);
        Assert.True(ClassicPlaylistLayout.OwnerRowHeight >= ClassicPlaylistLayout.InviteRoundEdge);
    }
}
