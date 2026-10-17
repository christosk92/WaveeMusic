// ── Wavee.Tests/PlayerVerbsTests.cs — the player's verbs as one core service (themes plan C2, X1/X5/X6/X7/X8) ─────────
// (Playback/PlayerVerbs.cs, Shell/QueueVerbs.cs, Shell/DeviceVerbs.cs, Shell.PlayerBar.cs `PlayerBarFacts.Seek`.) Pure: the
// DECISIONS behind the verbs are data, so they are unit facts with no reducer, no flyout and no store. The one-line posts
// (`Playback.Next`, …) are exercised by the reducer's own suites.
//
//   LIKE         an episode saves into the saved-episodes list, but only once that list is READY (a write against an unread
//                list would clobber it); anything else is a track like.
//   PLAY NEXT    a drop front-inserts the FIRST 100 tracks, last first, skipping any that never resolved; the toast says "the
//                first N" only when the cap actually cut something.
//   DEVICES      a this-computer click persists the output and, unless we own playback, takes it over; a Connect click
//                transfers to the roster slot resolved at CLICK time, and does nothing for a device that left.
//   CHAPTERS     the rail marks the fetched chapters normalised against the duration, only on a plain episode track rail.

using Xunit;

using DeviceKind = Wavee.Spotify.Decode.DeviceKind;
using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee.Tests;

public class PlayerVerbRulesTests
{
    [Fact]
    public void An_episode_like_writes_the_saved_list_only_once_it_is_ready()
    {
        var episode = new EntityRef(EntityKind.Episode, 7);
        Assert.Equal(PlayerVerbRules.LikeTarget.Episode, PlayerVerbRules.LikeTargetOf(episode, episodeSaveReady: true));
        Assert.Equal(PlayerVerbRules.LikeTarget.None, PlayerVerbRules.LikeTargetOf(episode, episodeSaveReady: false));
    }

    [Fact]
    public void A_track_like_never_waits_for_the_saved_episodes_list()
    {
        var track = new EntityRef(EntityKind.Track, 3);
        Assert.Equal(PlayerVerbRules.LikeTarget.Track, PlayerVerbRules.LikeTargetOf(track, episodeSaveReady: false));
        Assert.Equal(PlayerVerbRules.LikeTarget.Track, PlayerVerbRules.LikeTargetOf(track, episodeSaveReady: true));
    }

    [Fact]
    public void An_empty_deck_falls_to_the_track_arm_which_finds_no_uri_and_does_nothing()
        => Assert.Equal(PlayerVerbRules.LikeTarget.Track, PlayerVerbRules.LikeTargetOf(default, episodeSaveReady: true));

    [Fact]
    public void Shuffle_toggles_the_flag()
    {
        Assert.True(PlayerVerbRules.NextShuffle(false));
        Assert.False(PlayerVerbRules.NextShuffle(true));
    }

    [Fact]
    public void Repeat_cycles_off_context_track_off()
    {
        Assert.Equal(RepeatMode.Context, PlayerVerbRules.NextRepeat(RepeatMode.Off));
        Assert.Equal(RepeatMode.Track, PlayerVerbRules.NextRepeat(RepeatMode.Context));
        Assert.Equal(RepeatMode.Off, PlayerVerbRules.NextRepeat(RepeatMode.Track));
    }

    [Fact]
    public void A_retry_needs_a_row_on_the_deck()
    {
        Assert.False(PlayerVerbRules.CanRetry(default));
        Assert.True(PlayerVerbRules.CanRetry(new EntityRef(EntityKind.Track, 1)));
    }

    [Fact]
    public void The_production_verbs_are_one_cached_instance_behind_the_interface()
    {
        Assert.Same(PlayerVerbs.Default, PlayerVerbs.Default);
        Assert.IsAssignableFrom<IPlayerVerbs>(PlayerVerbs.Default);
    }

    [Fact]
    public void Navigating_to_the_route_that_addresses_nothing_goes_nowhere()
        => PlayerVerbs.Default.Navigate(Shell.Route.None);   // would throw on a shell-less host if it tried to go anywhere
}

public class PlayNextDropPlanTests
{
    static bool[] AllValid(int n) => Enumerable.Repeat(true, n).ToArray();

    [Fact]
    public void A_small_drop_inserts_everything_last_first_so_the_dropped_order_is_the_play_order()
    {
        var plan = Shell.QueueVerbs.PlanPlayNext(AllValid(3));
        Assert.Equal(new[] { 2, 1, 0 }, plan.Order);
        Assert.Equal(3, plan.Count);
        Assert.False(plan.Truncated);
    }

    [Fact]
    public void A_track_that_never_resolved_is_skipped_and_not_counted()
    {
        var plan = Shell.QueueVerbs.PlanPlayNext([true, false, true, false]);
        Assert.Equal(new[] { 2, 0 }, plan.Order);
        Assert.Equal(2, plan.Count);
    }

    [Fact]
    public void A_drop_over_the_cap_keeps_the_first_hundred_and_says_so()
    {
        int total = Shell.PlayerBarRules.MaxPlayNextDrop + 37;
        var plan = Shell.QueueVerbs.PlanPlayNext(AllValid(total));
        Assert.Equal(Shell.PlayerBarRules.MaxPlayNextDrop, plan.Count);
        Assert.Equal(Shell.PlayerBarRules.MaxPlayNextDrop - 1, plan.Order[0]);   // the kept prefix is inserted last-first
        Assert.Equal(0, plan.Order[^1]);
        Assert.True(plan.Truncated);
    }

    [Fact]
    public void A_drop_exactly_at_the_cap_is_whole_and_not_truncated()
    {
        var plan = Shell.QueueVerbs.PlanPlayNext(AllValid(Shell.PlayerBarRules.MaxPlayNextDrop));
        Assert.Equal(Shell.PlayerBarRules.MaxPlayNextDrop, plan.Count);
        Assert.False(plan.Truncated);
    }

    [Fact]
    public void Tracks_past_the_cap_are_never_inserted_even_when_earlier_ones_were_invalid()
    {
        var valid = AllValid(Shell.PlayerBarRules.MaxPlayNextDrop + 5);
        valid[0] = false;
        var plan = Shell.QueueVerbs.PlanPlayNext(valid);
        Assert.Equal(Shell.PlayerBarRules.MaxPlayNextDrop - 1, plan.Count);      // 99: the cap is on the prefix, not on the survivors
        Assert.DoesNotContain(Shell.PlayerBarRules.MaxPlayNextDrop, plan.Order);
        Assert.True(plan.Truncated);
    }

    [Fact]
    public void An_empty_drop_plans_nothing()
    {
        var plan = Shell.QueueVerbs.PlanPlayNext([]);
        Assert.Empty(plan.Order);
        Assert.Equal(0, plan.Count);
        Assert.False(plan.Truncated);
    }
}

public class DeviceVerbPlanTests
{
    static Playback.Devices.Row Row(string id, DeviceKind kind) => new() { Id = id, Name = id, Kind = kind };

    static readonly Playback.Devices.Row[] Roster = [Row("pc", DeviceKind.ThisDevice), Row("phone", DeviceKind.Phone)];

    [Fact]
    public void The_system_default_row_persists_a_null_output_and_pulls_playback_here_when_we_are_not_the_owner()
    {
        var plan = Shell.DeviceVerbs.PlanOf(new Shell.DeviceIntent(Shell.DeviceIntentKind.SelectLocal, LocalId: "", TakeOver: true), Roster);
        Assert.True(plan.SelectLocal);
        Assert.Null(plan.LocalId);
        Assert.True(plan.TakeOver);
        Assert.Equal(-1, plan.TransferSlot);
    }

    [Fact]
    public void A_specific_output_persists_its_id_and_does_not_take_over_when_we_already_own_playback()
    {
        var plan = Shell.DeviceVerbs.PlanOf(new Shell.DeviceIntent(Shell.DeviceIntentKind.SelectLocal, LocalId: "speakers", TakeOver: false), Roster);
        Assert.True(plan.SelectLocal);
        Assert.Equal("speakers", plan.LocalId);
        Assert.False(plan.TakeOver);
    }

    [Fact]
    public void A_connect_click_transfers_to_the_slot_resolved_now()
    {
        var plan = Shell.DeviceVerbs.PlanOf(new Shell.DeviceIntent(Shell.DeviceIntentKind.TransferTo, ConnectId: "PHONE"), Roster);
        Assert.False(plan.SelectLocal);
        Assert.Equal(1, plan.TransferSlot);                    // ordinal-ignore-case, against the roster as it is NOW
    }

    [Fact]
    public void A_connect_click_for_a_device_that_left_the_roster_does_nothing()
    {
        var plan = Shell.DeviceVerbs.PlanOf(new Shell.DeviceIntent(Shell.DeviceIntentKind.TransferTo, ConnectId: "gone"), Roster);
        Assert.Equal(Shell.DeviceVerbs.Plan.Nothing, plan);
    }

    [Fact]
    public void A_row_with_no_intent_does_nothing()
        => Assert.Equal(Shell.DeviceVerbs.Plan.Nothing, Shell.DeviceVerbs.PlanOf(default, Roster));

    [Fact]
    public void The_asked_quality_is_the_persisted_choice_clamped_onto_the_ladder()
    {
        Assert.Equal(Spotify.Audio.Quality.High160, Shell.DevicePickerFacts.QualityOf(1));
        Assert.Equal((Spotify.Audio.Quality)Platform.Network.QualityMin, Shell.DevicePickerFacts.QualityOf(-4));
        Assert.Equal((Spotify.Audio.Quality)Platform.Network.QualityMax, Shell.DevicePickerFacts.QualityOf(99));
    }
}

public class SeekChapterFactTests
{
    static Spotify.Podcasts.Chapter Chapter(string title, int startMs, int endMs = 0) => new("c:" + startMs, title, startMs, endMs);

    [Fact]
    public void Only_a_plain_episode_rail_carries_chapters()
    {
        Assert.True(Shell.PlayerBarFacts.Seek.ChapterRail(EntityKind.Episode, Shell.SeekRailMode.Track));
        Assert.False(Shell.PlayerBarFacts.Seek.ChapterRail(EntityKind.Episode, Shell.SeekRailMode.Dvr));
        Assert.False(Shell.PlayerBarFacts.Seek.ChapterRail(EntityKind.Episode, Shell.SeekRailMode.Line));
        Assert.False(Shell.PlayerBarFacts.Seek.ChapterRail(EntityKind.Track, Shell.SeekRailMode.Track));
    }

    [Fact]
    public void Chapters_are_marked_only_once_the_request_is_ready()
    {
        Spotify.Podcasts.Chapter[] items = [Chapter("Intro", 0)];
        Assert.Empty(Shell.PlayerBarFacts.Seek.Chapters(ready: false, items, 100_000));
        Assert.Single(Shell.PlayerBarFacts.Seek.Chapters(ready: true, items, 100_000));
    }

    [Fact]
    public void Ready_chapters_are_sorted_clipped_to_the_duration_and_each_ends_where_the_next_starts()
    {
        Spotify.Podcasts.Chapter[] items = [Chapter("Two", 90_000), Chapter("One", 0), Chapter("Three", 180_000), Chapter("Past the end", 400_000)];
        var chapters = Shell.PlayerBarFacts.Seek.Chapters(ready: true, items, 200_000);
        Assert.Equal(3, chapters.Length);
        Assert.Equal(new[] { "One", "Two", "Three" }, chapters.Select(c => c.Title));
        Assert.Equal(new[] { 0, 90_000, 180_000 }, chapters.Select(c => c.StartMs));
        Assert.Equal(new[] { 90_000, 180_000, 200_000 }, chapters.Select(c => c.EndMs));
    }

    [Fact]
    public void An_unknown_duration_has_no_chapters_to_mark()
        => Assert.Empty(Shell.PlayerBarFacts.Seek.Chapters(ready: true, [Chapter("Intro", 0)], 0));
}
