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
    static ContainerVerb[] Rows(TargetKind k, bool liked = false, bool onPage = false, bool actionRow = false) => ContainerMenuRules.For(k, liked, onPage, actionRow).Rows;

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

    // ── D2a: the action row (the Play split + a Share button) takes three verbs out of the album / playlist "…" ──

    [Theory]
    [InlineData(TargetKind.Album, false)]
    [InlineData(TargetKind.Playlist, false)]
    public void An_action_row_page_drops_play_next_add_to_queue_and_share_and_keeps_the_rest(TargetKind kind, bool liked)
    {
        var page = ContainerMenuRules.For(kind, liked, onPage: true);
        var row = ContainerMenuRules.For(kind, liked, onPage: true, actionRow: true);
        Assert.Empty(row.Strip);
        Assert.DoesNotContain(ContainerVerb.PlayNext, row.Rows);
        Assert.DoesNotContain(ContainerVerb.AddToQueue, row.Rows);
        Assert.DoesNotContain(ContainerVerb.Share, row.Rows);
        Assert.Contains(ContainerVerb.AddToPlaylist, row.Rows);
        Assert.Contains(ContainerVerb.Pin, row.Rows);
        // Derived from the on-page plan: its rows in the same order, minus the three.
        Assert.Equal(page.Rows.Where(v => v is not (ContainerVerb.PlayNext or ContainerVerb.AddToQueue or ContainerVerb.Share)), row.Rows);
    }

    [Fact]
    public void The_album_action_row_menu_is_deposit_then_pin()
    {
        Assert.Equal(V(ContainerVerb.AddToPlaylist, ContainerVerb.Pin), Rows(TargetKind.Album, onPage: true, actionRow: true));
        Assert.Equal(Rows(TargetKind.Album, onPage: true, actionRow: true), Rows(TargetKind.Playlist, onPage: true, actionRow: true));
    }

    [Fact]
    public void The_on_page_plan_without_an_action_row_is_unchanged()
    {
        Assert.Equal(V(ContainerVerb.PlayNext, ContainerVerb.AddToQueue, ContainerVerb.AddToPlaylist, ContainerVerb.Pin, ContainerVerb.Share),
                     Rows(TargetKind.Album, onPage: true));
        Assert.Equal(Rows(TargetKind.Album, onPage: true), ContainerMenuRules.For(TargetKind.Album, false, true, actionRow: false).Rows);
        // actionRow means nothing off a page, or on a kind without the split.
        Assert.Equal(Rows(TargetKind.Album), ContainerMenuRules.For(TargetKind.Album, false, false, actionRow: true).Rows);
    }

    [Theory]
    [InlineData(TargetKind.Album)]
    [InlineData(TargetKind.Playlist)]
    public void The_verbs_the_action_row_drops_run_the_ids_the_split_runs(TargetKind kind)
    {
        // What actionRow moves OUT of the "…": the on-page rows the action-row plan no longer has, in the on-page order.
        var row = ContainerMenuRules.For(kind, liked: false, onPage: true, actionRow: true).Rows;
        var moved = ContainerMenuRules.For(kind, liked: false, onPage: true).Rows.Where(v => Array.IndexOf(row, v) < 0).ToArray();
        Assert.Equal(V(ContainerVerb.PlayNext, ContainerVerb.AddToQueue, ContainerVerb.Share), moved);
        var queueIds = moved.Where(v => v != ContainerVerb.Share).Select(Menus.IdOf).ToArray();
        Assert.Equal(new[] { ActionId.PlayContextNext, ActionId.AddContextToQueue }, queueIds);
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
