// ── Wavee.Tests/NotifyPanelRulesTests.cs — the notification panel's decisions ────────────────────────────────────
//
// Wave 4 stage B's gate for `Notify` §12 (Platform/Notify.cs): the five filter pills, the empty ladder, relative time,
// the release type pill and the update row's six arms — everything `Shell.UI.cs`'s panel renders BY.
//
//   A REMOTE CATEGORY IS ONLY EMPTY WHEN ITS FEED LOADED. A loading, failed or offline fetch says so; "No Spotify
//   notifications" over a feed that never answered is a lie the user acts on.
//
//   THE UPDATE ROW NEVER OFFERS "LATER" TWICE. Snoozed is the user's answer to Later, so its arm has no Later.

using Xunit;

namespace Wavee.Tests;

public class NotifyPanelFilterTests
{
    static Notification Social() => NotifyRows.ForSocial("s1", 1_000, true, "Followed you", null,
        SocialActionType.Navigate, null, null, null);

    [Theory]
    [InlineData(NotifyFilter.Updates, NotifyCategory.AppUpdate)]
    [InlineData(NotifyFilter.Spotify, NotifyCategory.Social)]
    [InlineData(NotifyFilter.New, NotifyCategory.NewRelease)]
    [InlineData(NotifyFilter.Activity, NotifyCategory.Activity)]
    public void Each_pill_shows_one_category(NotifyFilter pill, NotifyCategory category)
        => Assert.Equal(category, Notify.CategoryOf(pill));

    [Fact]
    public void All_shows_every_category()
    {
        Assert.Null(Notify.CategoryOf(NotifyFilter.All));
        var row = Social();
        Assert.True(Notify.PassesFilter(row, NotifyFilter.All));
        Assert.True(Notify.PassesFilter(row, NotifyFilter.Spotify));
        Assert.False(Notify.PassesFilter(row, NotifyFilter.Updates));
    }

    [Fact]
    public void Clear_exists_only_under_the_activity_pill()
    {
        foreach (var f in Enum.GetValues<NotifyFilter>())
            Assert.Equal(f == NotifyFilter.Activity, Notify.ShowsClear(f));
    }
}

public class NotifyPanelEmptyLadderTests
{
    [Theory]
    [InlineData(Notify.FeedState.Idle, Strings.Notifications.Loading)]
    [InlineData(Notify.FeedState.Loading, Strings.Notifications.Loading)]
    [InlineData(Notify.FeedState.Error, Strings.Notifications.Feed.Error)]
    [InlineData(Notify.FeedState.Offline, Strings.Notifications.Feed.Offline)]
    [InlineData(Notify.FeedState.Empty, Strings.Notifications.Empty.Spotify)]
    [InlineData(Notify.FeedState.Populated, Strings.Notifications.Empty.Spotify)]
    public void The_spotify_pill_says_what_its_feed_actually_did(Notify.FeedState social, string key)
        => Assert.Equal(key, Notify.EmptyMessageKey(NotifyFilter.Spotify, social, Notify.FeedState.Populated));

    [Fact]
    public void The_new_pill_reads_the_releases_feed_not_the_social_one()
    {
        Assert.Equal(Strings.Notifications.Feed.Error,
            Notify.EmptyMessageKey(NotifyFilter.New, Notify.FeedState.Populated, Notify.FeedState.Error));
        Assert.Equal(Strings.Notifications.Empty.New,
            Notify.EmptyMessageKey(NotifyFilter.New, Notify.FeedState.Error, Notify.FeedState.Empty));
    }

    [Fact]
    public void Local_categories_are_empty_whatever_the_remote_feeds_do()
    {
        Assert.Equal(Strings.Notifications.Empty.Updates,
            Notify.EmptyMessageKey(NotifyFilter.Updates, Notify.FeedState.Loading, Notify.FeedState.Loading));
        Assert.Equal(Strings.Notifications.Empty.Activity,
            Notify.EmptyMessageKey(NotifyFilter.Activity, Notify.FeedState.Error, Notify.FeedState.Offline));
    }

    [Fact]
    public void All_is_loading_while_either_feed_has_not_answered()
    {
        Assert.Equal(Strings.Notifications.Loading,
            Notify.EmptyMessageKey(NotifyFilter.All, Notify.FeedState.Populated, Notify.FeedState.Idle));
        Assert.Equal(Strings.Notifications.Loading,
            Notify.EmptyMessageKey(NotifyFilter.All, Notify.FeedState.Loading, Notify.FeedState.Empty));
        Assert.Equal(Strings.Notifications.Empty.All,
            Notify.EmptyMessageKey(NotifyFilter.All, Notify.FeedState.Error, Notify.FeedState.Empty));
    }
}

public class NotifyPanelRowTextTests
{
    const long Min = 60_000, Hour = 60 * Min, Day = 24 * Hour;

    [Theory]
    [InlineData(-5L, RelativeUnit.Now, 0L)]
    [InlineData(Min - 1, RelativeUnit.Now, 0L)]
    [InlineData(Min, RelativeUnit.Minutes, 1L)]
    [InlineData(59 * Min, RelativeUnit.Minutes, 59L)]
    [InlineData(Hour, RelativeUnit.Hours, 1L)]
    [InlineData(Day - 1, RelativeUnit.Hours, 23L)]
    [InlineData(Day, RelativeUnit.Days, 1L)]
    [InlineData(49 * Hour, RelativeUnit.Days, 2L)]
    public void Relative_age_steps_now_minutes_hours_days(long ageMs, RelativeUnit unit, long count)
        => Assert.Equal((unit, count), Notify.RelativeAge(ageMs));

    [Theory]
    [InlineData(NewReleaseKind.Episode, "single", Strings.Notifications.Release.Episode)]
    [InlineData(NewReleaseKind.Album, "SINGLE", Strings.Notifications.Release.Single)]
    [InlineData(NewReleaseKind.Album, "ep", Strings.Notifications.Release.Ep)]
    [InlineData(NewReleaseKind.Album, "Compilation", Strings.Notifications.Release.Compilation)]
    [InlineData(NewReleaseKind.Album, "album", Strings.Notifications.Release.Album)]
    [InlineData(NewReleaseKind.Album, null, Strings.Notifications.Release.Album)]
    public void The_release_pill_has_five_labels(NewReleaseKind kind, string? albumType, string key)
        => Assert.Equal(key, Notify.ReleaseTypeKey(kind, albumType));
}

public class NotifyPanelUpdateRowTests
{
    static AppUpdateSnapshot Snap(AppUpdateState state, string? quad = "0.3.1.42", string? semver = null)
        => new(state, quad, semver, null, 0, null, false, 0);

    [Fact]
    public void Available_offers_update_now_first_then_whats_new_and_later()
    {
        var arm = Notify.UpdateRow(AppUpdateState.Available);
        Assert.Equal(UpdateRowGlyph.Download, arm.Glyph);
        Assert.Equal(Strings.Notifications.Update.AvailableTitle, arm.TitleLocKey);
        Assert.False(arm.ShowsProgress);
        Assert.Equal([UpdateRowAction.UpdateNow, UpdateRowAction.WhatsNew, UpdateRowAction.Later], arm.Actions);
    }

    [Fact]
    public void Snoozed_never_offers_later_again()
        => Assert.DoesNotContain(UpdateRowAction.Later, Notify.UpdateRow(AppUpdateState.Snoozed).Actions);

    [Theory]
    [InlineData(AppUpdateState.Downloading)]
    [InlineData(AppUpdateState.Installing)]
    public void A_running_update_shows_progress_instead_of_buttons(AppUpdateState state)
    {
        var arm = Notify.UpdateRow(state);
        Assert.True(arm.ShowsProgress);
        Assert.Empty(arm.Actions);
        Assert.Equal(UpdateRowGlyph.Refresh, arm.Glyph);
    }

    [Fact]
    public void Completed_and_failed_carry_their_own_verbs()
    {
        var done = Notify.UpdateRow(AppUpdateState.Completed);
        Assert.Equal(UpdateRowGlyph.Success, done.Glyph);
        Assert.Equal([UpdateRowAction.WhatsNew, UpdateRowAction.Dismiss], done.Actions);
        var failed = Notify.UpdateRow(AppUpdateState.Failed);
        Assert.Equal(UpdateRowGlyph.Error, failed.Glyph);
        Assert.Equal([UpdateRowAction.Retry, UpdateRowAction.Dismiss], failed.Actions);
    }

    [Theory]
    [InlineData(AppUpdateState.None)]
    [InlineData(AppUpdateState.Checking)]
    public void The_states_that_never_reach_the_panel_invent_no_copy(AppUpdateState state)
    {
        var arm = Notify.UpdateRow(state);
        Assert.Equal(UpdateRowGlyph.Info, arm.Glyph);
        Assert.Equal("", arm.TitleLocKey);
        Assert.Empty(arm.Actions);
    }

    [Fact]
    public void Whats_new_opens_the_running_build_once_landed_else_the_target()
    {
        Assert.Equal("0.3.0", Notify.NotesVersion(Snap(AppUpdateState.Completed), runningVersion: "0.3.0"));
        Assert.Equal("", Notify.NotesVersion(Snap(AppUpdateState.Completed), runningVersion: null));
        Assert.Equal("0.3.2", Notify.NotesVersion(Snap(AppUpdateState.Available, semver: "0.3.2"), "0.3.0"));
        Assert.Equal("0.3.1", Notify.NotesVersion(Snap(AppUpdateState.Available), "0.3.0"));
    }

    [Theory]
    [InlineData(ToastActionKind.UpdateNow, UpdateRowAction.UpdateNow)]
    [InlineData(ToastActionKind.WhatsNew, UpdateRowAction.WhatsNew)]
    [InlineData(ToastActionKind.Later, UpdateRowAction.Later)]
    [InlineData(ToastActionKind.Retry, UpdateRowAction.Retry)]
    [InlineData(ToastActionKind.OpenReleasePage, UpdateRowAction.OpenReleasePage)]
    public void A_toast_action_is_the_same_verb_the_row_sends(ToastActionKind toast, UpdateRowAction verb)
        => Assert.Equal(verb, Notify.VerbFor(toast));
}
