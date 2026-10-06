// ── Wavee.Tests/PlaybackOsTests.cs — the OS surfaces' decisions, without a window ──────────────────────────────────
//
// Wave 3's gate for `Playback/Playback.Os.cs`. No HWND, no COM, no WinRT: chapter 14's wireframes are a table of
// small decisions with a big blast radius, and every one of them is extracted here as a pure function so the table
// is a test rather than a screenshot. The parity checklist still verifies the PIXELS against the kept 0.2.9 build —
// these facts verify the RULES, which is what stops a re-author "simplifying" one of them back into a shipped bug.
//
// The four that each cost a debugging cycle in 0.2.9, in order:
//
//   PLAYING CARRIES NO OVERLAY. The determinate fill IS the playing cue; a play glyph on top of it is redundant, and
//   the state machine only has three states — adding a fourth is how a stale badge outlives its track.
//
//   CHANGING IS TESTED BEFORE PLAYING. A buffering-while-playing tick must read as a transport in flight, or the
//   Win11 flyout shows smooth playback for a track that is still opening.
//
//   THE LIVE TIMELINE IS ZEROED ONCE. Not never (the previous track's 3:47 stays frozen mid-bar for six hours) and
//   not every tick (a cross-process COM RPC per tick, on the UI thread).
//
//   THE JUMP LIST'S TWO HALVES SHARE ONE KEY SPACE. A surface both played and visited must appear once; the whole
//   dedupe rests on the play log and the history composing the SAME route string.

using FluentGpu.Localization;
using FluentGpu.WindowsApi.Media;
using FluentGpu.WindowsApi.Shell;

using Wavee;

using Xunit;

namespace Wavee.Tests;

public class PlaybackTaskbarTableTests
{
    [Fact]
    public void Playing_carries_a_green_fill_and_no_overlay()
    {
        Assert.Equal(Playback.Os.OverlayKind.None, Playback.Os.OverlayFor(hasTrack: true, playing: true));
        Assert.Equal(Playback.Os.ProgressKind.Playing, Playback.Os.ProgressFor(hasTrack: true, playing: true));
    }

    [Fact]
    public void Paused_with_a_track_carries_the_pause_glyph_and_the_yellow_fill()
    {
        Assert.Equal(Playback.Os.OverlayKind.Pause, Playback.Os.OverlayFor(hasTrack: true, playing: false));
        Assert.Equal(Playback.Os.ProgressKind.Paused, Playback.Os.ProgressFor(hasTrack: true, playing: false));
    }

    [Fact]
    public void No_track_clears_both_and_that_is_what_drops_the_position_ticks()
    {
        Assert.Equal(Playback.Os.OverlayKind.None, Playback.Os.OverlayFor(hasTrack: false, playing: false));
        Assert.Equal(Playback.Os.ProgressKind.Idle, Playback.Os.ProgressFor(hasTrack: false, playing: false));
        // `Idle` is also the guard the tick path reads: a stale duration must never repaint a bar that should be
        // gone, so this value carries two meanings on purpose.
        Assert.Equal(Playback.Os.ProgressKind.Idle, Playback.Os.ProgressFor(hasTrack: false, playing: true));
    }
}

public class PlaybackThumbToolbarTests
{
    [Fact]
    public void Playing_with_a_full_queue_lights_all_three_and_shows_pause()
    {
        Playback.Os.ThumbState t = Playback.Os.ThumbsFor(hasTrack: true, playing: true, canPrev: true, canNext: true);
        Assert.True(t.PrevEnabled);
        Assert.True(t.PlayPauseEnabled);
        Assert.True(t.NextEnabled);
        Assert.True(t.ShowPause);
    }

    [Fact]
    public void The_first_track_of_a_queue_greys_previous_and_never_hides_it()
    {
        // Disabled, not hidden: the toolbar is added ONCE per HWND with a frozen count and order, so a hidden button
        // is a hole the shell will not fill back in.
        Playback.Os.ThumbState t = Playback.Os.ThumbsFor(hasTrack: true, playing: false, canPrev: false, canNext: true);
        Assert.False(t.PrevEnabled);
        Assert.True(t.NextEnabled);
        Assert.False(t.ShowPause);
    }

    [Fact]
    public void With_no_track_the_middle_button_is_disabled_and_the_outer_two_are_left_alone()
    {
        // Deliberately NOT forced false: the outer two are whatever the unified state says, which is what keeps them
        // live while a foreign Connect device owns a queue we can still skip.
        Playback.Os.ThumbState t = Playback.Os.ThumbsFor(hasTrack: false, playing: false, canPrev: true, canNext: true);
        Assert.False(t.PlayPauseEnabled);
        Assert.True(t.PrevEnabled);
        Assert.True(t.NextEnabled);
    }

    [Fact]
    public void The_middle_glyph_follows_the_transport_and_nothing_else()
    {
        Assert.True(Playback.Os.ThumbsFor(true, true, false, false).ShowPause);
        Assert.False(Playback.Os.ThumbsFor(true, false, false, false).ShowPause);
        Assert.True(Playback.Os.ThumbsFor(false, true, false, false).ShowPause);
    }
}

public class PlaybackSmtcRuleTests
{
    [Fact]
    public void No_track_closes_the_session_rather_than_showing_an_empty_card()
        => Assert.Equal(MediaPlaybackStatus.Closed,
            Playback.Os.StatusFor(hasTrack: false, buffering: false, playing: false));

    [Fact]
    public void Buffering_is_tested_before_playing()
    {
        // Ordering, not a coincidence: a buffering-while-playing tick must read as a transport in flight (W3).
        Assert.Equal(MediaPlaybackStatus.Changing,
            Playback.Os.StatusFor(hasTrack: true, buffering: true, playing: true));
        Assert.Equal(MediaPlaybackStatus.Playing,
            Playback.Os.StatusFor(hasTrack: true, buffering: false, playing: true));
        Assert.Equal(MediaPlaybackStatus.Paused,
            Playback.Os.StatusFor(hasTrack: true, buffering: false, playing: false));
    }

    [Fact]
    public void A_live_broadcast_zeroes_the_timeline_exactly_once()
    {
        Assert.True(Playback.Os.ZeroTimelineOnce(durationMs: 0, isLive: true, alreadyCleared: false));
        Assert.False(Playback.Os.ZeroTimelineOnce(durationMs: 0, isLive: true, alreadyCleared: true));
    }

    [Fact]
    public void A_finite_track_never_zeroes_it()
        => Assert.False(Playback.Os.ZeroTimelineOnce(durationMs: 227_000, isLive: false, alreadyCleared: false));

    [Fact]
    public void An_unknown_duration_on_a_non_live_source_is_not_a_live_stream()
        // Zero duration is ALSO what "not known yet" looks like; only the source's own liveness may zero the bar.
        => Assert.False(Playback.Os.ZeroTimelineOnce(durationMs: 0, isLive: false, alreadyCleared: false));

    [Theory]
    [InlineData(EntityKind.Track, false, Playback.Os.CardShape.Track)]
    [InlineData(EntityKind.Track, true, Playback.Os.CardShape.Episode)]
    [InlineData(EntityKind.Episode, false, Playback.Os.CardShape.Episode)]
    [InlineData(EntityKind.Album, false, Playback.Os.CardShape.Empty)]
    [InlineData(EntityKind.Unknown, false, Playback.Os.CardShape.Empty)]
    public void The_card_layout_follows_the_rows_kind_and_never_reads_an_episode_as_a_track(
        EntityKind kind, bool podcastTrack, Playback.Os.CardShape expected)
        // G-083: 0.3 read the Track table for every row, so an episode's card showed another row's text.
        => Assert.Equal(expected, Playback.Os.CardShapeFor(kind, podcastTrack));

    [Fact]
    public void The_card_is_pushed_again_when_the_same_rows_metadata_or_art_lands()
    {
        // G-084: a transferred row starts before its metadata is known; the identity alone would never re-push it.
        var id = EntityId.ForGid(EntityKind.Track, (UInt128)0xC0FFEEUL);
        var cold = new Playback.Os.CardKey(id, Knows: false, Image: 0);
        Assert.NotEqual(cold, cold with { Knows = true });
        Assert.NotEqual(cold with { Knows = true }, cold with { Knows = true, Image = 7 });
        Assert.Equal(cold, new Playback.Os.CardKey(id, false, 0));        // a pause or a tick changes none of the three
    }
}

public class PlaybackPowerPolicyTests
{
    [Theory]
    [InlineData(Playback.Phase.Playing, true, Playback.PlayableKind.Audio, Playback.Os.AwakeKind.System)]
    [InlineData(Playback.Phase.Playing, true, Playback.PlayableKind.LocalFile, Playback.Os.AwakeKind.System)]
    [InlineData(Playback.Phase.Playing, true, Playback.PlayableKind.Video, Playback.Os.AwakeKind.Display)]
    [InlineData(Playback.Phase.Paused, true, Playback.PlayableKind.Video, Playback.Os.AwakeKind.None)]
    [InlineData(Playback.Phase.Loading, true, Playback.PlayableKind.Audio, Playback.Os.AwakeKind.None)]
    [InlineData(Playback.Phase.Playing, false, Playback.PlayableKind.Video, Playback.Os.AwakeKind.None)]
    public void Keep_awake_is_ours_only_while_we_play_and_the_display_only_for_video(
        Playback.Phase phase, bool routesLocal, Playback.PlayableKind kind, Playback.Os.AwakeKind expected)
        // G-085: decided from the state being published; a phone playing somebody's music keeps nothing awake here.
        => Assert.Equal(expected, Playback.Os.AwakeFor(phase, routesLocal, kind));
}

public class PlaybackJumpListTests
{
    static Playback.Os.JumpRow Row(string route, string title = "", EntityKind kind = EntityKind.Album)
        => new(route, title, (byte)kind);

    [Fact]
    public void The_play_log_comes_first_and_the_history_fills_what_is_left()
    {
        Playback.Os.JumpRow[] log = [Row("album:a"), Row("pl:b")];
        Playback.Os.JumpRow[] history = [Row("artist:c"), Row("show:d")];
        var into = new Playback.Os.JumpRow[6];
        int n = Playback.Os.JumpList.Pick(log, history, into);
        Assert.Equal(4, n);
        Assert.Equal("album:a", into[0].Route);
        Assert.Equal("pl:b", into[1].Route);
        Assert.Equal("artist:c", into[2].Route);
        Assert.Equal("show:d", into[3].Route);
    }

    [Fact]
    public void A_surface_both_played_and_visited_appears_once()
    {
        // The two halves share ONE key space — the composed route — which is the whole reason one `seen` set works.
        Playback.Os.JumpRow[] log = [Row("album:a")];
        Playback.Os.JumpRow[] history = [Row("album:a"), Row("pl:b")];
        var into = new Playback.Os.JumpRow[6];
        int n = Playback.Os.JumpList.Pick(log, history, into);
        Assert.Equal(2, n);
        Assert.Equal("album:a", into[0].Route);
        Assert.Equal("pl:b", into[1].Route);
    }

    [Fact]
    public void The_category_is_capped_and_keeps_the_newest()
    {
        Playback.Os.JumpRow[] log =
        [
            Row("album:1"), Row("album:2"), Row("album:3"), Row("album:4"), Row("album:5"), Row("album:6"), Row("album:7"),
        ];
        var into = new Playback.Os.JumpRow[Playback.Os.JumpList.CategoryCap];
        int n = Playback.Os.JumpList.Pick(log, [], into);
        Assert.Equal(Playback.Os.JumpList.CategoryCap, n);
        Assert.Equal("album:1", into[0].Route);      // newest-first in, newest-first out
        Assert.Equal("album:6", into[^1].Route);
    }

    [Fact]
    public void A_routeless_row_is_dropped_rather_than_published_as_a_dead_link()
    {
        Playback.Os.JumpRow[] log = [Row(""), Row("album:a")];
        var into = new Playback.Os.JumpRow[6];
        Assert.Equal(1, Playback.Os.JumpList.Pick(log, [], into));
        Assert.Equal("album:a", into[0].Route);
    }

    [Fact]
    public void A_cold_start_publishes_an_empty_category_so_the_heading_is_not_drawn_at_all()
    {
        // W16: `AppendCategory` is skipped on a zero count, which is the CORRECT cold start — an empty "Jump back in"
        // heading with nothing under it reads as a broken feature.
        var into = new Playback.Os.JumpRow[6];
        Assert.Equal(0, Playback.Os.JumpList.Pick([], [], into));
    }

    [Fact]
    public void An_unnamed_row_falls_back_to_the_kind_word_and_never_to_a_raw_uri()
    {
        // The fourth step of the four-step fallback. The catalogue has carried these eleven keys since 0.2.9 and
        // nothing called them; a raw `spotify:album:…` in a jump list is the failure they exist to prevent.
        Assert.NotEmpty(Playback.Os.JumpList.KindLabel((byte)EntityKind.Album));
        Assert.NotEmpty(Playback.Os.JumpList.KindLabel((byte)EntityKind.Playlist));
        Assert.NotEmpty(Playback.Os.JumpList.KindLabel((byte)EntityKind.Artist));
        Assert.NotEmpty(Playback.Os.JumpList.KindLabel((byte)EntityKind.Show));
        Assert.NotEmpty(Playback.Os.JumpList.KindLabel((byte)EntityKind.Collection));
        Assert.NotEmpty(Playback.Os.JumpList.KindLabel((byte)EntityKind.Unknown));
    }

    [Fact]
    public void The_kinds_are_distinguishable_from_one_another()
    {
        string album = Playback.Os.JumpList.KindLabel((byte)EntityKind.Album);
        string playlist = Playback.Os.JumpList.KindLabel((byte)EntityKind.Playlist);
        string artist = Playback.Os.JumpList.KindLabel((byte)EntityKind.Artist);
        Assert.NotEqual(album, playlist);
        Assert.NotEqual(playlist, artist);
    }

    [Theory]
    [InlineData(Playback.Phase.Playing, "wavee://pause")]
    [InlineData(Playback.Phase.Paused, "wavee://resume")]
    [InlineData(Playback.Phase.Loading, "wavee://resume")]
    [InlineData(Playback.Phase.Idle, "wavee://resume")]
    public void The_transport_task_matches_the_published_phase(Playback.Phase published, string argument)
    {
        // The verb is decided from the state being PUBLISHED. It used to read `PhaseSignal`, which the drain writes only
        // after the sinks ran, so the list said "Pause" while paused and "Resume" while playing.
        bool playing = Playback.Os.JumpList.ShowsPauseTask(published);
        JumpTask[] tasks = Playback.Os.JumpList.Tasks(playing, @"C:\w.exe", null, null);
        Assert.Equal(2, tasks.Length);
        Assert.Equal(argument, tasks[0].Arguments);
        Assert.Equal(Loc.Get(playing ? Strings.Jumplist.Pause : Strings.Jumplist.Resume), tasks[0].Title);
        Assert.Equal("wavee://open?route=search", tasks[1].Arguments);
    }

    [Fact]
    public void The_rebuild_floor_is_a_minute_and_it_is_the_track_boundary_s_floor_only()
        // A skip storm must not hammer `ICustomDestinationList` with a Begin/Append/Commit transaction per skip; a
        // play/pause edge skips the floor, because that verb is the one the user compares against the app.
        => Assert.Equal(60_000L, Playback.Os.JumpList.RebuildMinIntervalMs);
}

/// <summary>#115: the jump list's COM transaction runs on a dedicated STA thread, through a mailbox that keeps at most one
/// pending write per AUMID target (newest wins), skips a write equal to what the target last committed, and keeps the
/// arrival order of different targets. These are the rules; the COM half is the engine's.</summary>
public class PlaybackJumpListMailboxTests
{
    static Playback.Os.JumpListWrite Write(bool playing, string? aumid = null, params string[] routes)
    {
        var items = new JumpListItem[routes.Length];
        for (int i = 0; i < routes.Length; i++)
            items[i] = new JumpListItem(routes[i], @"C:\w.exe", "wavee://open?route=" + routes[i], null, routes[i]);
        JumpTask[] tasks =
        [
            new(playing ? "Pause" : "Resume", @"C:\w.exe", playing ? "wavee://pause" : "wavee://resume"),
            new("Search", @"C:\w.exe", "wavee://open?route=search"),
        ];
        return Playback.Os.JumpListWrite.Publish("Jump back in", items, tasks, aumid);
    }

    [Fact]
    public void Writes_compare_by_content_not_by_reference()
    {
        Assert.Equal(Write(true, null, "album:a"), Write(true, null, "album:a"));
        Assert.NotEqual(Write(true, null, "album:a"), Write(false, null, "album:a"));        // the Pause/Resume task
        Assert.NotEqual(Write(true, null, "album:a"), Write(true, null, "album:b"));         // a category row
        Assert.NotEqual(Write(true, null, "album:a"), Write(true, "{AUMID}", "album:a"));    // the target
        Assert.NotEqual(Write(true), Playback.Os.JumpListWrite.Clear(null));
    }

    [Fact]
    public void A_burst_for_one_target_keeps_only_the_newest_write()
    {
        var box = new Playback.Os.JumpListMailbox();
        for (int i = 0; i < 50; i++) box.Offer(Write(i % 2 == 0));
        box.Offer(Write(false));                 // the state the user ended on
        Assert.Equal(1, box.Count);              // no queue growth, however long the burst
        Assert.True(box.TryTake(out var w));
        Assert.Equal(Write(false), w);
        Assert.False(box.TryTake(out _));
    }

    [Fact]
    public void A_write_equal_to_the_last_commit_for_its_target_is_skipped()
    {
        var box = new Playback.Os.JumpListMailbox();
        box.Offer(Write(true));
        Assert.True(box.TryTake(out var first));
        box.Done(first, committed: true);

        box.Offer(Write(false));                 // pause...
        box.Offer(Write(true));                  // ...undone before the worker got to it
        Assert.False(box.TryTake(out _));        // the shell already shows exactly this
        Assert.Equal(1, box.Skipped);

        box.Offer(Write(false));                 // a real change still goes out
        Assert.True(box.TryTake(out var next));
        Assert.Equal(Write(false), next);
    }

    [Fact]
    public void A_failed_write_is_not_remembered_so_the_same_write_retries()
    {
        var box = new Playback.Os.JumpListMailbox();
        box.Offer(Write(true));
        Assert.True(box.TryTake(out var w));
        box.Done(w, committed: false);
        box.Offer(Write(true));
        Assert.True(box.TryTake(out _));
    }

    [Fact]
    public void Different_targets_keep_their_arrival_order_and_each_coalesces_in_place()
    {
        // The process-default publish goes out first and the toast AUMID's after it — both lists are written, as before.
        var box = new Playback.Os.JumpListMailbox();
        box.Offer(Write(false, null));
        box.Offer(Write(false, "{AUMID}"));
        box.Offer(Write(true, null));            // replaces the default target's write without jumping the line
        Assert.Equal(2, box.Count);
        Assert.True(box.TryTake(out var a));
        Assert.True(box.TryTake(out var b));
        Assert.Equal(Write(true, null), a);
        Assert.Equal(Write(false, "{AUMID}"), b);
    }

    [Fact]
    public void A_sign_out_clear_replaces_a_pending_publish_and_a_later_publish_replaces_the_clear()
    {
        var box = new Playback.Os.JumpListMailbox();
        box.Offer(Write(true, "{AUMID}", "album:a"));
        box.Offer(Playback.Os.JumpListWrite.Clear("{AUMID}"));
        Assert.True(box.TryTake(out var w));
        Assert.True(w.IsClear);                  // the previous account's rows never reach the shell

        box.Offer(Playback.Os.JumpListWrite.Clear("{AUMID}"));
        box.Offer(Write(true, "{AUMID}"));
        Assert.True(box.TryTake(out var p));
        Assert.False(p.IsClear);
    }

    [Fact]
    public void The_pending_set_is_bounded_even_across_targets()
    {
        var box = new Playback.Os.JumpListMailbox();
        for (int i = 0; i < 20; i++) box.Offer(Write(true, "{T" + i + "}"));
        Assert.Equal(Playback.Os.JumpListMailbox.MaxTargets, box.Count);
        Assert.True(box.TryTake(out var oldestKept));
        Assert.Equal("{T16}", oldestKept.Aumid);  // the oldest waiting targets were the ones dropped
    }

    [Fact]
    public void The_worker_runs_writes_on_an_sta_thread_off_the_caller_and_coalesces_while_busy()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var applied = new List<Playback.Os.JumpListWrite>();
        int callerThread = Environment.CurrentManagedThreadId;
        int applyThread = -1;
        ApartmentState apartment = ApartmentState.Unknown;
        var worker = new Playback.Os.JumpListWorker(w =>
        {
            lock (applied)
            {
                applied.Add(w);
                applyThread = Environment.CurrentManagedThreadId;
                apartment = Thread.CurrentThread.GetApartmentState();
            }
            firstEntered.Set();
            release.Wait(TimeSpan.FromSeconds(10));    // a slow COM transaction
            return true;
        }, "Wavee.ShellSTA.Test");

        worker.Submit(Write(true));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
        // Six edges arrive while the first transaction is still running: one write is left waiting, not six.
        for (int i = 0; i < 6; i++) worker.Submit(Write(i % 2 == 0 ? false : true, null, "album:" + i));
        Assert.Equal(1, worker.Stats.Pending);
        release.Set();

        Assert.True(worker.Shutdown(5_000));           // drains what is pending, then the bounded join returns
        lock (applied)
        {
            Assert.Equal(2, applied.Count);
            Assert.Equal(Write(true, null, "album:5"), applied[1]);   // the newest state, never an intermediate one
            Assert.NotEqual(callerThread, applyThread);
            Assert.Equal(ApartmentState.STA, apartment);
        }
        worker.Submit(Write(false));                    // after shutdown: dropped, never a new thread
        Assert.Equal(0, worker.Stats.Pending);
    }

    [Fact]
    public void A_throwing_apply_is_fail_soft_and_the_worker_keeps_going()
    {
        int calls = 0;
        var worker = new Playback.Os.JumpListWorker(w =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("shell refused");
            return true;
        }, "Wavee.ShellSTA.Test");
        worker.Submit(Write(true));
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 1, TimeSpan.FromSeconds(10));
        worker.Submit(Write(true));                    // identical, but the first FAILED, so it is not skipped
        Assert.True(worker.Shutdown(5_000));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void Shutdown_without_a_write_never_starts_a_thread()
        => Assert.True(new Playback.Os.JumpListWorker(_ => true).Shutdown(0));
}

public class PlaybackAppIconTests
{
    [Fact]
    public void A_missing_icon_is_null_and_not_an_exception()
    {
        // A dev tree without the content copy is a DEGRADED state, not an error state: the toolbar still publishes
        // with tooltips and working clicks (W13), and the jump list falls back to the exe.
        string? glyph = Playback.Os.AppIcon.TaskbarGlyph("a-glyph-that-does-not-exist");
        Assert.Null(glyph);
    }

    [Fact]
    public void The_resolver_answers_the_same_path_every_time()
        // The app icon is probed once and cached; the four taskbar glyphs are probed per use because they are read at
        // human rate. Neither may answer differently on a second call.
        => Assert.Equal(Playback.Os.AppIcon.Path(), Playback.Os.AppIcon.Path());
}
