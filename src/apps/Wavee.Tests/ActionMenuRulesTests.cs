// ── Wavee.Tests/ActionMenuRulesTests.cs — the menu grammar's pure half + the profile menu's row table ──────────────
//
// `Actions.MenuRules` and `Actions.ProfileRules` (Platform/Actions.Rules.cs). The menus themselves are built at open
// time over engine types; every decision they take is here, so a stray divider, an extra "Move up" under "Delete", or a
// bell that shows in two places at once is a failing fact rather than a screenshot argument.

using Xunit;

namespace Wavee.Tests;

public class ActionMenuRulesTests
{
    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(10, 10)]
    [InlineData(250, 10)]   // the rest are one "More playlists…" row away
    public void The_deposit_submenu_lists_at_most_ten_playlists_inline(int available, int inline)
        => Assert.Equal(inline, Actions.MenuRules.InlineCount(available));

    [Fact]
    public void A_group_never_opens_with_a_stray_or_doubled_divider()
    {
        Assert.False(Actions.MenuRules.NeedsSeparator(0, false));   // nothing above: no leading divider
        Assert.False(Actions.MenuRules.NeedsSeparator(3, true));    // already separated: never doubled
        Assert.True(Actions.MenuRules.NeedsSeparator(3, false));
    }

    [Fact]
    public void Layout_extras_land_in_front_of_a_trailing_destructive_block()
    {
        // [Add ▸, Open, —, Delete playlist] → extras go at index 2, in front of the separator, never under Delete.
        Assert.Equal(2, Actions.MenuRules.ExtrasInsertIndex(4, secondToLastIsSeparator: true));
        // No destructive tail → appended.
        Assert.Equal(4, Actions.MenuRules.ExtrasInsertIndex(4, secondToLastIsSeparator: false));
        Assert.Equal(1, Actions.MenuRules.ExtrasInsertIndex(1, secondToLastIsSeparator: false));
        Assert.Equal(0, Actions.MenuRules.ExtrasInsertIndex(0, secondToLastIsSeparator: false));
    }

    [Fact]
    public void The_profile_menu_folds_bell_and_friends_in_only_when_the_row_dropped_them()
    {
        Actions.ProfileRow[] wideExpected =
        [
            Actions.ProfileRow.Account, Actions.ProfileRow.Settings, Actions.ProfileRow.Play,
            Actions.ProfileRow.Separator, Actions.ProfileRow.Theme, Actions.ProfileRow.Separator, Actions.ProfileRow.LogOut,
        ];
        Assert.Equal(wideExpected, Actions.ProfileRules.Rows(canPlay: true, actionsInMenu: false, hasNotifications: true));

        Actions.ProfileRow[] foldedExpected =
        [
            Actions.ProfileRow.Account, Actions.ProfileRow.Settings, Actions.ProfileRow.Play,
            Actions.ProfileRow.Separator, Actions.ProfileRow.Notifications, Actions.ProfileRow.Friends,
            Actions.ProfileRow.Separator, Actions.ProfileRow.Theme, Actions.ProfileRow.Separator, Actions.ProfileRow.LogOut,
        ];
        Assert.Equal(foldedExpected, Actions.ProfileRules.Rows(canPlay: true, actionsInMenu: true, hasNotifications: true));
    }

    [Fact]
    public void Play_is_absent_not_disabled_when_the_build_cannot_play_anything_of_its_own()
    {
        var rows = Actions.ProfileRules.Rows(canPlay: false, actionsInMenu: false, hasNotifications: true);
        Assert.DoesNotContain(Actions.ProfileRow.Play, rows);
        Assert.Equal(Actions.ProfileRow.Settings, rows[1]);
    }

    [Fact]
    public void Friends_still_folds_when_there_is_no_notification_panel_to_open()
    {
        var rows = Actions.ProfileRules.Rows(canPlay: false, actionsInMenu: true, hasNotifications: false);
        Assert.DoesNotContain(Actions.ProfileRow.Notifications, rows);
        Assert.Contains(Actions.ProfileRow.Friends, rows);
        Assert.Equal(3, Array.FindAll(rows, r => r == Actions.ProfileRow.Separator).Length);
    }

    [Fact]
    public void The_name_cap_is_the_budget_less_the_gap_and_the_named_padding()
        => Assert.Equal(76f, Actions.ProfileRules.NameCap(Shell.Layout.ChromeProfileNameW));

    [Fact]
    public void The_theme_row_names_the_target_theme()
    {
        Assert.True(Actions.ProfileRules.OffersLightTheme(currentlyDark: true));
        Assert.False(Actions.ProfileRules.OffersLightTheme(currentlyDark: false));
    }

    [Fact]
    public void The_tier_row_waits_for_a_known_tier_instead_of_guessing_free()
    {
        Assert.False(Actions.ProfileRules.ShowsTier(Spotify.Tier.Unknown));
        Assert.True(Actions.ProfileRules.ShowsTier(Spotify.Tier.Free));
        Assert.True(Actions.ProfileRules.ShowsTier(Spotify.Tier.Premium));
    }
}

[Collection(EntitiesCollection.Name)]
public class ActionShareRulesTests
{
    [Fact]
    public void A_single_spotify_container_is_shareable_and_a_foreign_one_is_not()
    {
        var album = ActionTarget.ForAlbum(EntityUri.Parse("spotify:album:1TSZDcvlPtAnekTaItI3qO"), "Album");
        Assert.True(Actions.MenuRules.SingleShareableUri(in album).IsValid);

        var local = ActionTarget.ForPlaylist(EntityUri.Parse("wavee:playlist:local"), "Local");
        Assert.False(Actions.MenuRules.SingleShareableUri(in local).IsValid);
    }

    [Fact]
    public void A_target_with_no_uri_has_nothing_to_share()
    {
        var none = ActionTarget.ForAlbum(default, "");
        Assert.False(Actions.MenuRules.SingleShareableUri(in none).IsValid);
        Assert.False(Actions.MenuRules.IsShareable(default));
    }
}
