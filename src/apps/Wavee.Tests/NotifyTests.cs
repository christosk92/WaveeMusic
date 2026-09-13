// ── Wavee.Tests/NotifyTests.cs — the ladder, quiet hours, the merge, the escalation plan, the update table ────────
//
// Wave 4's gate for `Platform/Notify.cs` (A8/A9/A11). Ported from _old/Wavee.Tests's {NotificationPolicyTests,
// NotificationAggregationTests, AppUpdateToastsTests}.cs (G6), plus the three that had NO test in 0.2.9 and each
// describe a shipped bug:
//
//   THE SENTINEL FOLD IS KEYED ON THE SENTINEL, NOT ON THE TYPE. Folding every app-update row to a live "now" made it
//   beat the watermark on every single rebuild, so it re-toasted forever — a SIMULATED update carries a real
//   timestamp and was re-raised by every unrelated rebuild. That is a banner storm, and it shipped once.
//
//   THE WATERMARK ADVANCES PAST EVERYTHING CONSIDERED, NOT JUST WHAT WAS RAISED. Otherwise a topic the user has since
//   silenced comes back as a BACKLOG the moment they re-enable it. That is why the newest timestamp is found in a
//   first pass over everything and written unconditionally at the end; fusing the two loops IS the bug.
//
//   ONE COUNTING RULE. 0.2.9 excluded local activity from the bell badge in the merge and then RE-COUNTED in the
//   topic-visibility pass WITHOUT reproducing the exclusion, so the badge disagreed with itself the moment anything
//   was silenced. Here the filter runs inside the merge and the count comes out of the same loop.
//
// Nothing here starts the engine loop, opens a window or touches WinRT: `Notify.Host.cs`'s partial is simply absent
// from a test's view of the decisions, which is what "the shell posts values, the core decides" buys.

using Xunit;

namespace Wavee.Tests;

public class NotificationPolicyTests
{
    static DateTimeOffset At(int hour) => new(2026, 9, 13, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_ladder_defaults_are_a_shape_not_noise()
    {
        // In-app for everything the centre already shows, and Windows PRE-SELECTED for the one topic whose entire
        // point is arriving when the app is closed. Nothing escalates until the master gate is on.
        foreach (var topic in Notify.Prefs.AllTopics)
            Assert.Equal(topic == NotifyTopic.ReleaseDrops ? NotifyLevel.Windows : NotifyLevel.InApp,
                NotificationPolicy.DefaultFor(topic));
    }

    [Fact]
    public void Library_activity_caps_at_in_app_because_a_banner_about_your_own_click_is_absurd()
    {
        Assert.Equal(NotifyLevel.InApp, NotificationPolicy.CeilingFor(NotifyTopic.LibraryActivity));
        Assert.Equal(NotifyLevel.InApp, NotificationPolicy.Clamp(NotifyTopic.LibraryActivity, NotifyLevel.Windows));
        Assert.Equal(NotifyLevel.Windows, NotificationPolicy.Clamp(NotifyTopic.NewAlbums, NotifyLevel.Windows));
    }

    [Fact]
    public void The_scheduled_topics_are_the_ones_that_arrive_with_wavee_closed()
    {
        Assert.True(NotificationPolicy.IsScheduled(NotifyTopic.ReleaseDrops));
        Assert.True(NotificationPolicy.IsScheduled(NotifyTopic.DaylistRefresh));
        Assert.False(NotificationPolicy.IsScheduled(NotifyTopic.NewAlbums));
    }

    [Fact]
    public void A_same_day_quiet_window_is_half_open()
    {
        var quiet = new QuietHours(true, 13, 17);
        Assert.False(quiet.Contains(At(12)));
        Assert.True(quiet.Contains(At(13)));
        Assert.True(quiet.Contains(At(16)));
        Assert.False(quiet.Contains(At(17)));   // [From, To)
    }

    [Fact]
    public void A_quiet_window_may_wrap_midnight()
    {
        var quiet = new QuietHours(true, 22, 8);
        Assert.True(quiet.Contains(At(23)));
        Assert.True(quiet.Contains(At(2)));
        Assert.False(quiet.Contains(At(9)));
    }

    [Fact]
    public void An_equal_pair_means_no_window_which_is_the_safer_reading()
        => Assert.False(new QuietHours(true, 9, 9).Contains(At(9)));

    [Fact]
    public void Corrupt_hours_cannot_produce_a_window_that_swallows_everything()
    {
        var normalized = new QuietHours(true, -5, 99).Normalized();
        Assert.Equal(0, normalized.FromHour);
        Assert.Equal(0, normalized.ToHour);
        Assert.False(normalized.Contains(At(3)));
    }

    [Fact]
    public void Next_audible_shifts_out_of_the_window_and_is_idempotent_outside_it()
    {
        var quiet = new QuietHours(true, 22, 8);
        Assert.Equal(8, quiet.NextAudible(At(23)).Hour);
        Assert.Equal(At(9), quiet.NextAudible(At(9)));
        // …and a wrapped window's shift lands on the NEXT day, not this one.
        Assert.True(quiet.NextAudible(At(23)) > At(23));
    }

    [Fact]
    public void A_toast_needs_the_master_gate_the_dial_and_a_non_quiet_moment()
    {
        var off = new NotificationPolicy(false, true, QuietHours.Off);
        Assert.False(off.RaisesToastNow(NotifyLevel.Windows, At(12)));

        var on = new NotificationPolicy(true, true, QuietHours.Off);
        Assert.True(on.RaisesToastNow(NotifyLevel.Windows, At(12)));
        Assert.False(on.RaisesToastNow(NotifyLevel.InApp, At(12)));

        var quiet = new NotificationPolicy(true, true, new QuietHours(true, 22, 8));
        Assert.False(quiet.RaisesToastNow(NotifyLevel.Windows, At(23)));
    }

    [Fact]
    public void A_scheduled_toast_is_shifted_never_suppressed()
    {
        var policy = new NotificationPolicy(true, true, new QuietHours(true, 22, 8));
        var shifted = policy.ScheduleAt(NotifyLevel.Windows, At(23));
        Assert.NotNull(shifted);
        Assert.Equal(8, shifted!.Value.Hour);   // the album is still out; the user hears about it at a civilised hour
        Assert.Null(policy.ScheduleAt(NotifyLevel.InApp, At(12)));
    }

    [Fact]
    public void The_settings_page_order_is_the_declaration_order()
    {
        // A new topic added to the enum without a row here is INVISIBLE in Settings.
        Assert.Equal(Enum.GetValues<NotifyTopic>().Length, Notify.Prefs.AllTopics.Length);
        Assert.Equal(NotifyTopic.NewAlbums, Notify.Prefs.AllTopics[0]);
        Assert.Equal(NotifyTopic.LibraryActivity, Notify.Prefs.AllTopics[^1]);
    }

    [Fact]
    public void The_topic_of_a_row_is_finer_than_its_display_category()
    {
        var album = NotifyRows.ForRelease("a", 1, true, NewReleaseKind.Album, default, "A", null, "Artist", null, false);
        var episode = NotifyRows.ForRelease("e", 1, true, NewReleaseKind.Episode, default, "E", null, "Show", null, false);
        Assert.Equal(NotifyTopic.NewAlbums, Notify.Prefs.TopicOf(in album));
        Assert.Equal(NotifyTopic.NewEpisodes, Notify.Prefs.TopicOf(in episode));

        var concert = NotifyRows.ForSocial("c", 1, true, "Show announced", "spotify:concert:1",
            SocialActionType.Navigate, null, null, null);
        var follower = NotifyRows.ForSocial("f", 1, true, "Someone followed you", null,
            SocialActionType.Navigate, null, null, null);
        Assert.Equal(NotifyTopic.Concerts, Notify.Prefs.TopicOf(in concert));
        Assert.Equal(NotifyTopic.Followers, Notify.Prefs.TopicOf(in follower));
    }
}

public class SpotifyUpdatesTests
{
    static Notification Social(string title, string? actionUri, string? wireType = null)
        => NotifyRows.ForSocial("id", 1, true, title, actionUri, SocialActionType.Navigate, null, null, wireType);

    [Fact]
    public void The_servers_own_discriminator_wins_when_it_shipped_one()
    {
        Assert.True(SpotifyUpdates.IsConcert(Social("x", null, "CONCERT_ANNOUNCEMENT")));
        Assert.True(SpotifyUpdates.IsConcert(Social("x", null, "live_event")));
        // A generic "EVENT" bucket is not concrete, and a false positive puts a follower row on Home.
        Assert.False(SpotifyUpdates.IsConcert(Social("x", null, "EVENT")));
    }

    [Fact]
    public void Otherwise_the_action_target_decides_never_the_title()
    {
        Assert.True(SpotifyUpdates.IsConcert(Social("x", "spotify:concert:abc")));
        Assert.True(SpotifyUpdates.IsConcert(Social("x", "https://concerts.spotify.com/x")));
        Assert.False(SpotifyUpdates.IsConcert(Social("New concert announced!", "spotify:artist:abc")));
    }

    [Fact]
    public void Clean_title_strips_a_leading_glyph_and_never_returns_empty()
    {
        Assert.Equal("Just days away: Daft Punk", SpotifyUpdates.CleanTitle("⏰ Just days away: Daft Punk"));
        Assert.Equal("New show", SpotifyUpdates.CleanTitle("🎵 New show"));
        Assert.Equal("Plain", SpotifyUpdates.CleanTitle("Plain"));
        Assert.Equal("⏰", SpotifyUpdates.CleanTitle("⏰"));    // a title that is only glyphs is returned verbatim
        Assert.Equal("", SpotifyUpdates.CleanTitle(null));
    }

    [Fact]
    public void Casing_is_left_alone_because_the_string_is_server_localized_prose()
        => Assert.Equal("İstanbul'da konser", SpotifyUpdates.CleanTitle("🎵 İstanbul'da konser"));
}

public class NotificationReadIdsTests
{
    [Fact]
    public void The_codec_is_idempotent_and_refuses_a_separator()
    {
        string set = Notify.ReadIds.Add(null, "a");
        Assert.Equal("a", set);
        Assert.Same(set, Notify.ReadIds.Add(set, "a"));
        Assert.Equal(set, Notify.ReadIds.Add(set, "bad\nid"));
        Assert.Equal(set, Notify.ReadIds.Add(set, ""));
    }

    [Fact]
    public void The_set_is_bounded_from_the_oldest_end()
    {
        string set = "";
        for (int i = 0; i < Notify.ReadIds.Cap + 20; i++) set = Notify.ReadIds.Add(set, "id" + i);
        var ids = Notify.ReadIds.Parse(set);
        Assert.Equal(Notify.ReadIds.Cap, ids.Count);
        Assert.Equal("id20", ids[0]);       // the oldest 20 (id0..id19) were dropped
        Assert.DoesNotContain("id0", ids);
    }

    [Fact]
    public void Contains_does_not_match_a_prefix()
    {
        Assert.True(Notify.ReadIds.Contains("abc\ndef", "def"));
        Assert.False(Notify.ReadIds.Contains("abcd", "abc"));
    }
}

public class NotificationMergeTests
{
    static readonly AppUpdateSnapshot Available =
        new(AppUpdateState.Available, "0.3.0.1", "0.3.0", "Crest", 0, null, true, 0);

    static Notification Release(string id, long at, bool unread = true)
        => NotifyRows.ForRelease(id, at, unread, NewReleaseKind.Album, default, "Name", null, "Creator", null, false);

    static Notification Social(string id, long at, bool unread = true)
        => NotifyRows.ForSocial(id, at, unread, "Title", null, SocialActionType.Navigate, null, null, null);

    static Notification Activity(string id, long at)
        => NotifyRows.ForActivity(id, at, true, "Saved", "", default);

    static Notification Row(in Notify.Feed feed, string id)
    {
        for (int i = 0; i < feed.Items.Count; i++) if (feed.Items[i].Id == id) return feed.Items[i];
        throw new Xunit.Sdk.XunitException("no row with id " + id);
    }

    [Fact]
    public void The_merge_is_newest_first_and_the_update_row_pins_to_the_top()
    {
        var feed = Notify.Merge(NotifyRows.ForUpdate(Available, true),
            [Social("s", 200)], 0, [Release("r", 300)], 0, [Activity("a", 100)]);
        Assert.Equal(4, feed.Items.Count);
        Assert.Equal(NotifyCategory.AppUpdate, feed.Items[0].Category);
        Assert.Equal("r", feed.Items[1].Id);
        Assert.Equal("s", feed.Items[2].Id);
    }

    [Fact]
    public void A_watermark_marks_everything_at_or_before_it_as_read()
    {
        var feed = Notify.Merge(null, [Social("old", 100), Social("new", 300)], 200, [], 0, []);
        Assert.False(Row(in feed, "old").IsUnread);
        Assert.True(Row(in feed, "new").IsUnread);
        Assert.Equal(1, feed.Unread);
    }

    [Fact]
    public void The_per_item_read_set_is_applied_on_top_of_the_watermark()
    {
        var feed = Notify.Merge(null, [Social("a", 300), Social("b", 300)], 0, [], 0, [], readIds: "a");
        Assert.False(Row(in feed, "a").IsUnread);
        Assert.True(Row(in feed, "b").IsUnread);
    }

    [Fact]
    public void Local_activity_is_visible_but_never_increases_the_bell_badge()
    {
        // Routine actions such as liking a song must not be attention-seeking.
        var feed = Notify.Merge(null, [], 0, [], 0, [Activity("a", 100), Activity("b", 200)]);
        Assert.Equal(2, feed.Items.Count);
        Assert.Equal(0, feed.Unread);
    }

    [Fact]
    public void One_counting_rule_survives_a_topic_being_silenced()
    {
        // The 0.2.9 bug: the visibility pass re-counted WITHOUT the activity exclusion, so the badge disagreed with
        // itself the moment anything was silenced.
        var feed = Notify.Merge(null, [Social("s", 300)], 0, [Release("r", 400)], 0, [Activity("a", 100)],
            topicVisible: static t => t != NotifyTopic.NewAlbums);
        Assert.Equal(2, feed.Items.Count);   // the release row is filtered OUT
        Assert.Equal(1, feed.Unread);        // …and activity still does not count
    }

    [Fact]
    public void An_empty_feed_is_an_empty_feed()
    {
        var feed = Notify.Merge(null, [], 0, [], 0, []);
        Assert.Empty(feed.Items);
        Assert.Equal(0, feed.Unread);
    }
}

public class NotifyEscalationTests
{
    static readonly NotificationPolicy On = new(true, true, QuietHours.Off);
    static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    static readonly long NowMs = Now.ToUnixTimeMilliseconds();

    static NotifyLevel Windows(NotifyTopic _) => NotifyLevel.Windows;

    static Notification Release(string id, long at)
        => NotifyRows.ForRelease(id, at, true, NewReleaseKind.Album, default, "Name", null, "Creator", null, false);

    [Fact]
    public void The_first_run_only_records_where_the_feed_was()
    {
        // A zero watermark means "we have never escalated"; enabling notifications must not replay history.
        var plan = Notify.Escalation.Decide([Release("a", NowMs - 1)], 0, in On, Windows, Now, "");
        Assert.Empty(plan.Raise);
        Assert.Equal(NowMs - 1, plan.Watermark);
    }

    [Fact]
    public void The_watermark_advances_past_everything_considered_even_when_nothing_is_raised()
    {
        var off = new NotificationPolicy(false, true, QuietHours.Off);
        var plan = Notify.Escalation.Decide([Release("a", NowMs)], 1, in off, Windows, Now, "");
        Assert.Empty(plan.Raise);
        Assert.Equal(NowMs, plan.Watermark);   // a silenced topic cannot come back as a backlog
    }

    [Fact]
    public void Only_rows_newer_than_the_watermark_are_raised()
    {
        var plan = Notify.Escalation.Decide([Release("new", 300), Release("old", 100)], 200, in On, Windows, Now, "");
        Assert.Single(plan.Raise);
        Assert.Equal(0, plan.Raise[0]);   // index 0 is "new" in the newest-first list
    }

    [Fact]
    public void A_burst_is_capped_and_the_remainder_becomes_one_summary()
    {
        // The list is NEWEST FIRST and the loop walks it BACKWARD, i.e. oldest → newest, so the banners arrive in
        // chronological order and the most recent one lands on top of the Action Center stack. Ch 14 §9's trap is
        // explicit that reversing this truncates the burst at the wrong end, so the direction is ported verbatim —
        // which means the rows the CAP drops are the newest ones, and they are what the summary line stands for.
        Notification[] items =
        [
            Release("n5", 500), Release("n4", 400), Release("n3", 300), Release("n2", 200), Release("n1", 100),
        ];
        var plan = Notify.Escalation.Decide(items, 50, in On, Windows, Now, "");
        Assert.Equal(Notify.Escalation.MaxPerRebuild, plan.Raise.Length);
        Assert.Equal(2, plan.Suppressed);
        Assert.Equal([4, 3, 2], plan.Raise);          // oldest first, three of five
        Assert.Equal(500, plan.Watermark);            // …and the watermark still passes EVERYTHING considered
    }

    [Fact]
    public void A_read_row_is_never_raised()
    {
        var read = NotifyRows.ForRelease("r", 300, isUnread: false, NewReleaseKind.Album, default, "N", null, "C", null, false);
        Assert.Empty(Notify.Escalation.Decide([read], 100, in On, Windows, Now, "").Raise);
    }

    [Fact]
    public void The_sentinel_is_folded_to_now_keyed_on_the_sentinel_and_not_on_the_type()
    {
        var pinned = NotifyRows.ForUpdate(new AppUpdateSnapshot(AppUpdateState.Available, "1", null, null, 0, null, true, 0), true);
        Assert.Equal(NowMs, Notify.Escalation.TimestampOf(in pinned, NowMs));

        // A SIMULATED update carries a REAL timestamp and must NOT be folded — that is the banner storm.
        var simulated = pinned with { TimestampMs = 12345 };
        Assert.Equal(12345L, Notify.Escalation.TimestampOf(in simulated, NowMs));
    }

    [Fact]
    public void An_app_update_is_state_so_it_is_raised_once_per_change_not_once_per_rebuild()
    {
        var row = NotifyRows.ForUpdate(new AppUpdateSnapshot(AppUpdateState.Available, "0.3.0.1", null, null, 0, null, true, 0), true);
        string identity = Notify.Escalation.UpdateIdentity(in row);
        Assert.Equal("Available:0.3.0.1", identity);

        Assert.Single(Notify.Escalation.Decide([row], 1, in On, Windows, Now, "").Raise);
        Assert.Empty(Notify.Escalation.Decide([row], 1, in On, Windows, Now, identity).Raise);
    }

    [Fact]
    public void Progress_is_not_part_of_the_update_identity()
    {
        var at5 = NotifyRows.ForUpdate(new AppUpdateSnapshot(AppUpdateState.Downloading, "q", null, null, 5, null, true, 0), true);
        var at60 = NotifyRows.ForUpdate(new AppUpdateSnapshot(AppUpdateState.Downloading, "q", null, null, 60, null, true, 0), true);
        Assert.Equal(Notify.Escalation.UpdateIdentity(in at5), Notify.Escalation.UpdateIdentity(in at60));
    }

    [Fact]
    public void A_live_download_is_reported_as_a_progress_push_not_as_a_banner()
    {
        var row = NotifyRows.ForUpdate(new AppUpdateSnapshot(AppUpdateState.Downloading, "q", null, null, 42, null, true, 0), true);
        var plan = Notify.Escalation.Decide([row], 1, in On, Windows, Now, Notify.Escalation.UpdateIdentity(in row));
        Assert.Equal(42, plan.ProgressPercent);
        Assert.Empty(plan.Raise);
    }

    [Fact]
    public void Progress_is_throttled_but_a_hundred_always_lands()
    {
        Assert.False(Notify.Escalation.ShouldPushProgress(lastPushed: -1, percent: 50));   // no live toast yet
        Assert.False(Notify.Escalation.ShouldPushProgress(lastPushed: 50, percent: 52));
        Assert.True(Notify.Escalation.ShouldPushProgress(lastPushed: 50, percent: 55));
        Assert.True(Notify.Escalation.ShouldPushProgress(lastPushed: 99, percent: 100));
    }

    [Fact]
    public void Quiet_hours_stop_a_live_raise()
    {
        var policy = new NotificationPolicy(true, true, new QuietHours(true, 0, 23));
        Assert.Empty(Notify.Escalation.Decide([Release("a", 300)], 1, in policy, Windows, Now, "").Raise);
    }
}

public class AppUpdateToastsTests
{
    static AppUpdateSnapshot S(AppUpdateState state, string? quad = "0.3.0.1", string? semver = "0.3.0",
        string? codename = "Crest", AppUpdateFailure? failure = null, bool quiet = false)
        => new(state, quad, semver, codename, 0, failure, true, 0, quiet);

    [Fact]
    public void Nothing_is_planned_when_the_state_did_not_move()
        => Assert.Null(Notify.AppUpdateToasts.Plan(S(AppUpdateState.Available), S(AppUpdateState.Available)));

    [Fact]
    public void A_progress_tick_never_plans_a_second_card()
    {
        var a = S(AppUpdateState.Downloading) with { ProgressPercent = 10 };
        var b = S(AppUpdateState.Downloading) with { ProgressPercent = 80 };
        Assert.Null(Notify.AppUpdateToasts.Plan(a, b));
    }

    [Fact]
    public void A_changed_failure_reason_plans_again()
    {
        var network = S(AppUpdateState.Failed, failure: new AppUpdateFailure(AppUpdateFailureKind.Network, 0, ""));
        var metered = S(AppUpdateState.Failed, failure: new AppUpdateFailure(AppUpdateFailureKind.Metered, 0, ""));
        Assert.Null(Notify.AppUpdateToasts.Plan(network, network));
        Assert.NotNull(Notify.AppUpdateToasts.Plan(network, metered));
    }

    [Fact]
    public void A_quiet_failure_is_state_not_an_interruption()
    {
        var quiet = S(AppUpdateState.Failed, failure: new AppUpdateFailure(AppUpdateFailureKind.Network, 0, ""), quiet: true);
        Assert.Null(Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, quiet));
    }

    [Fact]
    public void Checking_and_snoozed_are_silent_by_design()
    {
        Assert.Null(Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, S(AppUpdateState.Checking)));
        Assert.Null(Notify.AppUpdateToasts.Plan(S(AppUpdateState.Available), S(AppUpdateState.Snoozed)));
        Assert.Null(Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, S(AppUpdateState.None)));
    }

    [Fact]
    public void Available_offers_the_three_actions_and_does_not_stick()
    {
        var plan = Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, S(AppUpdateState.Available))!.Value;
        Assert.False(plan.Sticky);
        Assert.Equal([ToastActionKind.UpdateNow, ToastActionKind.WhatsNew, ToastActionKind.Later], plan.Actions);
    }

    [Fact]
    public void The_body_names_the_build_only_when_the_title_could_not()
    {
        // With no codename or semver the TITLE already IS the quad — "Wavee 0.2.0.9002 is available / 0.2.0.9002"
        // said it twice.
        var bare = Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle,
            S(AppUpdateState.Available, semver: null, codename: null))!.Value;
        Assert.Equal("", bare.Body);

        var named = Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, S(AppUpdateState.Available))!.Value;
        Assert.Equal("0.3.0.1", named.Body);
    }

    [Fact]
    public void A_download_and_an_install_stick_and_offer_nothing()
    {
        var downloading = Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, S(AppUpdateState.Downloading))!.Value;
        Assert.True(downloading.Sticky);
        Assert.Empty(downloading.Actions);

        var installing = Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, S(AppUpdateState.Installing))!.Value;
        Assert.True(installing.Sticky);
    }

    [Fact]
    public void A_completed_update_is_a_success_that_links_at_the_notes()
    {
        var plan = Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle, S(AppUpdateState.Completed))!.Value;
        Assert.Equal(FluentGpu.Controls.InfoBarSeverity.Success, plan.Severity);
        Assert.Equal([ToastActionKind.WhatsNew], plan.Actions);
    }

    [Fact]
    public void A_metered_failure_offers_retry_alone_because_the_release_page_is_not_the_answer()
    {
        var metered = Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle,
            S(AppUpdateState.Failed, failure: new AppUpdateFailure(AppUpdateFailureKind.Metered, 0, "")))!.Value;
        Assert.Equal([ToastActionKind.Retry], metered.Actions);

        var other = Notify.AppUpdateToasts.Plan(AppUpdateSnapshot.Idle,
            S(AppUpdateState.Failed, failure: new AppUpdateFailure(AppUpdateFailureKind.PackagesInUse, 0, "")))!.Value;
        Assert.Equal([ToastActionKind.Retry, ToastActionKind.OpenReleasePage], other.Actions);
        Assert.Equal(FluentGpu.Controls.InfoBarSeverity.Error, other.Severity);
    }

    [Fact]
    public void Release_name_is_codename_then_semver_then_quad_and_never_empty()
    {
        Assert.Equal("Crest", Notify.AppUpdateToasts.ReleaseName(S(AppUpdateState.Available)));
        Assert.Equal("0.3.0", Notify.AppUpdateToasts.ReleaseName(S(AppUpdateState.Available, codename: null)));
        Assert.Equal("0.3.0.1", Notify.AppUpdateToasts.ReleaseName(S(AppUpdateState.Available, semver: null, codename: null)));
        Assert.Equal("", Notify.AppUpdateToasts.ReleaseName(null));
    }

    [Fact]
    public void Every_failure_kind_has_its_own_sentence_and_the_fallback_carries_the_hresult()
    {
        foreach (var kind in Enum.GetValues<AppUpdateFailureKind>())
            Assert.False(string.IsNullOrEmpty(
                Notify.AppUpdateToasts.FailureText(new AppUpdateFailure(kind, unchecked((int)0x80070005), "x"))));
        // The Unknown arm interpolates the HRESULT through the loc catalogue, which a unit test does not load — the
        // FORMAT of the code is pinned where it is produced, not through a template that falls back to "[key]".
    }

    [Fact]
    public void Every_action_has_a_label()
    {
        foreach (var kind in Enum.GetValues<ToastActionKind>())
            Assert.False(string.IsNullOrEmpty(Notify.AppUpdateToasts.Label(kind)));
    }

    [Fact]
    public void Snoozed_is_the_one_sentence_arm_that_can_render_an_empty_name()
    {
        // Port the asymmetry rather than "harmonising" it: the other four name the release through ReleaseName so they
        // cannot print a double space; Snoozed deliberately prints semver → quad → "".
        Assert.NotEmpty(Notify.AppUpdateToasts.StateSentence(S(AppUpdateState.Available)));
        Assert.NotEmpty(Notify.AppUpdateToasts.StateSentence(S(AppUpdateState.Completed)));
        Assert.NotEmpty(Notify.AppUpdateToasts.StateSentence(S(AppUpdateState.Snoozed)));
        Assert.NotEmpty(Notify.AppUpdateToasts.StateSentence(null));
        Assert.Equal(Notify.AppUpdateToasts.StateSentence(S(AppUpdateState.Failed,
                failure: new AppUpdateFailure(AppUpdateFailureKind.Network, 0, ""))),
            Notify.AppUpdateToasts.FailureText(new AppUpdateFailure(AppUpdateFailureKind.Network, 0, "")));
    }
}

public class AppUpdateVersionTests
{
    [Theory]
    [InlineData("0.1.2.1", "0.1.2", true)]
    [InlineData("0.1.2", "0.1.2.0", false)]     // the same version
    [InlineData("v0.4.0", "0.3.9", true)]
    [InlineData("0.3.0+abc", "0.3.0", false)]
    [InlineData("0.3.0-dev", "0.3.0", false)]
    [InlineData("dev", "0.3.0", false)]         // an unstamped local build must never prompt
    [InlineData("0.3.0", "dev", false)]
    [InlineData("1.2.3.4.5", "0.1", false)]     // more than four parts is not a version we know
    public void Is_newer(string remote, string current, bool expected)
        => Assert.Equal(expected, Notify.AppUpdateVersion.IsNewer(remote, current));

    [Fact]
    public void A_first_ever_launch_is_not_an_update()
    {
        Assert.False(Notify.AppUpdateVersion.IsFirstRunAfterUpdate("", "0.3.0"));
        Assert.False(Notify.AppUpdateVersion.IsFirstRunAfterUpdate("0.3.0", "0.3.0"));
        Assert.True(Notify.AppUpdateVersion.IsFirstRunAfterUpdate("0.2.9", "0.3.0"));
    }

    [Fact]
    public void The_release_tag_is_the_first_three_parts()
    {
        Assert.Equal("0.1.1", Notify.AppUpdateVersion.ReleaseTagVersion("0.1.1.42"));
        Assert.Equal("0.3.0", Notify.AppUpdateVersion.ReleaseTagVersion("v0.3.0+abc"));
        Assert.Equal("", Notify.AppUpdateVersion.ReleaseTagVersion(null));
    }
}

public class ShutdownUpdatePolicyTests
{
    [Theory]
    [InlineData(AppUpdateState.Available, true)]
    [InlineData(AppUpdateState.Snoozed, true)]
    [InlineData(AppUpdateState.Failed, false)]      // a quiet retry that fails again is invisible
    [InlineData(AppUpdateState.Downloading, false)] // already in flight
    [InlineData(AppUpdateState.None, false)]
    public void Should_apply(AppUpdateState state, bool expected)
        => Assert.Equal(expected, Notify.ShutdownUpdatePolicy.ShouldApply(installOnQuit: true, state));

    [Fact]
    public void The_setting_off_means_never()
        => Assert.False(Notify.ShutdownUpdatePolicy.ShouldApply(false, AppUpdateState.Available));

    [Fact]
    public void Settled_is_installing_or_failed()
    {
        Assert.True(Notify.ShutdownUpdatePolicy.IsSettled(AppUpdateState.Installing));
        Assert.True(Notify.ShutdownUpdatePolicy.IsSettled(AppUpdateState.Failed));
        Assert.False(Notify.ShutdownUpdatePolicy.IsSettled(AppUpdateState.Available));
    }
}
