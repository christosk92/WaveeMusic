// ── Wavee.Tests/ContainerMenuRulesTests.cs — the one album / playlist / artist / show menu's verb table ────────────────
//
// ContainerMenuRules.For is the pure table Menus.Container composes over the registered verbs. These pin the order per
// kind (so the five old builders cannot drift apart again) and what the hero "…" drops.

using System.Linq;
using Xunit;

namespace Wavee.Tests;

public class ContainerMenuRulesTests
{
    static ContainerVerb[] V(params ContainerVerb[] verbs) => verbs;

    static ContainerVerb[] Strip(TargetKind k, bool liked = false, bool onPage = false) => ContainerMenuRules.For(k, liked, onPage).Strip;
    static ContainerVerb[] Rows(TargetKind k, bool liked = false, bool onPage = false) => ContainerMenuRules.For(k, liked, onPage).Rows;

    [Fact]
    public void An_album_card_is_strip_then_deposit_open_pin_artist_share()
    {
        Assert.Equal(V(ContainerVerb.Play, ContainerVerb.PlayNext, ContainerVerb.AddToQueue, ContainerVerb.Save), Strip(TargetKind.Album));
        Assert.Equal(V(ContainerVerb.AddToPlaylist, ContainerVerb.Open, ContainerVerb.Pin, ContainerVerb.GoToArtist, ContainerVerb.Share),
                     Rows(TargetKind.Album));
    }

    [Fact]
    public void A_playlist_is_an_album_without_go_to_artist()
    {
        Assert.Equal(Strip(TargetKind.Album), Strip(TargetKind.Playlist));
        Assert.Equal(V(ContainerVerb.AddToPlaylist, ContainerVerb.Open, ContainerVerb.Pin, ContainerVerb.Share), Rows(TargetKind.Playlist));
    }

    [Fact]
    public void Liked_songs_drops_save_and_nothing_else()
    {
        Assert.Equal(V(ContainerVerb.Play, ContainerVerb.PlayNext, ContainerVerb.AddToQueue), Strip(TargetKind.Playlist, liked: true));
        Assert.Equal(Rows(TargetKind.Playlist), Rows(TargetKind.Playlist, liked: true));
        Assert.Equal(Strip(TargetKind.Album), Strip(TargetKind.Album, liked: true));   // liked only means something for a playlist
    }

    [Fact]
    public void An_artist_has_no_queue_pair_and_no_deposit_but_a_radio()
    {
        Assert.Equal(V(ContainerVerb.Play, ContainerVerb.Save), Strip(TargetKind.Artist));
        Assert.Equal(V(ContainerVerb.Open, ContainerVerb.Pin, ContainerVerb.ArtistRadio, ContainerVerb.Share), Rows(TargetKind.Artist));
    }

    [Fact]
    public void A_show_is_the_container_strip_over_open_pin_share()
    {
        Assert.Equal(Strip(TargetKind.Album), Strip(TargetKind.Show));
        Assert.Equal(V(ContainerVerb.Open, ContainerVerb.Pin, ContainerVerb.Share), Rows(TargetKind.Show));
    }

    [Theory]
    [InlineData(TargetKind.Album)]
    [InlineData(TargetKind.Playlist)]
    [InlineData(TargetKind.Artist)]
    [InlineData(TargetKind.Show)]
    public void On_a_page_the_strip_dissolves_and_open_play_save_and_go_to_artist_go(TargetKind kind)
    {
        var plan = ContainerMenuRules.For(kind, liked: false, onPage: true);
        Assert.Empty(plan.Strip);
        Assert.DoesNotContain(ContainerVerb.Play, plan.Rows);
        Assert.DoesNotContain(ContainerVerb.Save, plan.Rows);
        Assert.DoesNotContain(ContainerVerb.Open, plan.Rows);
        Assert.DoesNotContain(ContainerVerb.GoToArtist, plan.Rows);
        Assert.Equal(ContainerVerb.Share, plan.Rows[^1]);
        Assert.Contains(ContainerVerb.Pin, plan.Rows);
    }

    [Fact]
    public void The_hero_menus_keep_their_card_order()
    {
        Assert.Equal(V(ContainerVerb.PlayNext, ContainerVerb.AddToQueue, ContainerVerb.AddToPlaylist, ContainerVerb.Pin, ContainerVerb.Share),
                     Rows(TargetKind.Album, onPage: true));
        Assert.Equal(Rows(TargetKind.Album, onPage: true), Rows(TargetKind.Playlist, onPage: true));
        Assert.Equal(V(ContainerVerb.PlayNext, ContainerVerb.AddToQueue, ContainerVerb.Pin, ContainerVerb.Share), Rows(TargetKind.Show, onPage: true));
        Assert.Equal(V(ContainerVerb.Pin, ContainerVerb.ArtistRadio, ContainerVerb.Share), Rows(TargetKind.Artist, onPage: true));
    }

    [Theory]
    [InlineData(TargetKind.None)]
    [InlineData(TargetKind.Tracks)]
    [InlineData(TargetKind.Episode)]
    [InlineData(TargetKind.QueueEntry)]
    [InlineData(TargetKind.SidebarItem)]
    [InlineData(TargetKind.NowPlaying)]
    public void Other_kinds_have_no_container_menu(TargetKind kind)
        => Assert.True(ContainerMenuRules.For(kind, liked: false, onPage: false).IsEmpty);

    [Fact]
    public void Every_plan_ends_in_share_and_never_repeats_a_verb()
    {
        foreach (var kind in new[] { TargetKind.Album, TargetKind.Playlist, TargetKind.Artist, TargetKind.Show })
            foreach (bool page in new[] { false, true })
            {
                var plan = ContainerMenuRules.For(kind, false, page);
                var all = plan.Strip.Concat(plan.Rows).ToArray();
                Assert.Equal(all.Length, all.Distinct().Count());
                Assert.Equal(ContainerVerb.Share, plan.Rows[^1]);
            }
    }
}
