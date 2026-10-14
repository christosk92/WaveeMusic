// ── Wavee.Tests/LyricsModelTests.cs — the lyrics surface's model: follow, interlude, resync (C5, X15) ───────────────────
//
// `LyricsModel` is the state `Lyrics.ViewCore` used to hold beside the nodes it drives. The ladders and rules it calls are
// pinned in the Lyrics tests (`ResolveLine`, `AdvancePastInterlude`, `Authority`, `Follow`, `Interlude`, `MediaClock`);
// these facts pin the model's own transitions without mounting a view: what a frame resolves to, when the dots and their
// reserved band arm and release, how the follow detaches, counts down and resyncs, which documents may replace the one
// on screen, and what the surface's state says.
//
// The model reads the playback host's statics (nothing playing: the subject and the deck are both empty, so the surface
// owns playback) and the lyrics preferences, so the class joins the platform collection and restores what it touches.

using Xunit;

namespace Wavee.Tests;

[Collection(PlatformCollection.Name)]
public sealed class LyricsModelTests : IDisposable
{
    readonly MemoryAppSettings _store = new();
    readonly int _available = Lyrics.Prefs.Available.Peek();
    readonly bool _playing = Playback.IsPlaying.Peek();

    public LyricsModelTests() => Platform.UseSettings(_store);

    public void Dispose()
    {
        Playback.IsPlaying.Value = _playing;
        Lyrics.Prefs.Available.Value = _available;
        Lyrics.Prefs.SetSecondaryLine(Lyrics.Prefs.None);
        Platform.UseSettings(null);
    }

    static LyricsModel Model(bool large = false) => new(new LyricsSurface(large, OnMedia: large, OnStage: large));

    static Lyrics.Line L(long start, string text, long? end = null, string? translation = null)
        => new(start, text, [], end, translation);

    /// <summary>Three line-synced lines at 0 / 2000 / 4000 ms.</summary>
    static Lyrics.Doc LineSynced(params Lyrics.Line[] lines)
        => new("track-1", true, lines.Length == 0 ? [L(0, "a"), L(2000, "b"), L(4000, "c")] : lines, Lyrics.SyncKind.Line);

    /// <summary>A word-synced first line that is sung out at 1000 ms, then a 9 s instrumental break before the second line.</summary>
    static Lyrics.Doc WithBreak()
    {
        var first = new Lyrics.Line(0, "first", [new Lyrics.Syllable(0, 1000, "first")], 1000, null, null, IsWordByWord: true);
        var second = new Lyrics.Line(10_000, "second", [new Lyrics.Syllable(10_000, 11_000, "second")], 11_000, null, null, IsWordByWord: true);
        return new Lyrics.Doc("track-2", true, [first, second], Lyrics.SyncKind.Syllable);
    }

    // ── the state ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_surface_shows_nothing()
        => Assert.Equal(LyricsState.Nothing, Model().State.Peek());

    [Theory]
    [InlineData(false, false, false, false, LyricsState.Nothing)]
    [InlineData(true, false, false, false, LyricsState.NoLyrics)]       // something plays but has no lyrics id
    [InlineData(true, true, true, false, LyricsState.Loading)]
    [InlineData(true, true, false, true, LyricsState.Failed)]
    public void The_state_follows_what_is_playing_and_whether_the_load_answered(bool anything, bool hasTrack, bool pending, bool failed, LyricsState expected)
        => Assert.Equal(expected, LyricsModel.Classify(anything, hasTrack, pending, failed, null, syncSuppressed: false));

    [Fact]
    public void A_ready_document_is_synced_unsynced_or_empty()
    {
        Assert.Equal(LyricsState.Synced, LyricsModel.Classify(true, true, false, false, LineSynced(), syncSuppressed: false));
        Assert.Equal(LyricsState.NoLyrics, LyricsModel.Classify(true, true, false, false, null, syncSuppressed: false));
        Assert.Equal(LyricsState.NoLyrics, LyricsModel.Classify(true, true, false, false, Lyrics.Doc.Empty("t"), syncSuppressed: false));

        var unsynced = new Lyrics.Doc("t", false, [L(0, "a"), L(0, "b")], Lyrics.SyncKind.Unsynced);
        Assert.Equal(LyricsState.Unsynced, LyricsModel.Classify(true, true, false, false, unsynced, syncSuppressed: false));
    }

    [Fact]
    public void A_video_turns_a_timed_document_into_a_static_one()
        => Assert.Equal(LyricsState.Unsynced, LyricsModel.Classify(true, true, false, false, LineSynced(), syncSuppressed: true));

    [Fact]
    public void Publishing_a_state_is_value_gated_and_readable()
    {
        var model = Model();
        model.PublishState(LyricsState.Loading);
        Assert.Equal(LyricsState.Loading, model.State.Peek());
        model.PublishState(LyricsState.Synced);
        Assert.Equal(LyricsState.Synced, model.State.Peek());
    }

    // ── the frame: follow the lines ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_frame_lights_the_line_the_clock_is_in_and_a_hand_off_is_one_change()
    {
        var model = Model();
        var doc = LineSynced();

        var first = model.Step(doc, 2500);
        Assert.Equal(1, first.Active);
        Assert.Equal(1, first.Voice);
        Assert.True(first.ActiveChanged);
        Assert.True(first.VoiceChanged);
        Assert.Equal(-1, first.PreviousVoice);
        Assert.Equal(1, model.ActiveLine.Peek());
        Assert.Equal(1, model.VoiceLine.Peek());
        Assert.Equal(2500f, model.NowMs.Peek());

        var same = model.Step(doc, 2600);                                         // still inside the same line
        Assert.False(same.ActiveChanged);
        Assert.False(same.VoiceChanged);

        var next = model.Step(doc, 4500);
        Assert.Equal(2, next.Active);
        Assert.True(next.ActiveChanged);
        Assert.Equal(1, next.PreviousVoice);
    }

    [Fact]
    public void The_eye_leads_the_voice_by_the_lead_time()
    {
        var model = Model();

        var frame = model.Step(LineSynced(), 2000 - Lyrics.LeadMs + 10);          // just inside the lead window of line 1

        Assert.Equal(1, frame.Active);                                            // emphasis and follow are already on it…
        Assert.Equal(0, frame.Voice);                                             // …while the voice is still on line 0
    }

    [Fact]
    public void A_line_stops_being_the_voice_at_its_own_sung_out_point()
    {
        var model = Model();
        var doc = LineSynced(L(0, "a", end: 1000), L(2000, "b"));

        var frame = model.Step(doc, 1500);

        Assert.Equal(0, frame.Active);                                            // the lit line stays (a short gap is no interlude)
        Assert.Equal(-1, frame.Voice);                                            // but nobody is singing it any more
    }

    [Fact]
    public void Before_the_first_line_nothing_is_lit()
    {
        var model = Model();

        var frame = model.Step(LineSynced(L(1000, "a"), L(3000, "b")), 100);

        Assert.Equal(-1, frame.Active);
        Assert.Equal(-1, frame.Voice);
    }

    [Fact]
    public void Retiring_the_lines_clears_both()
    {
        var model = Model();
        model.Step(LineSynced(), 2500);

        model.RetireLines();

        Assert.Equal(-1, model.ActiveLine.Peek());
        Assert.Equal(-1, model.VoiceLine.Peek());
    }

    [Fact]
    public void The_voice_lines_word_progress_binds_the_step_time()
    {
        var model = Model();
        var line = new Lyrics.Line(0, "ab", [new Lyrics.Syllable(0, 1000, "a"), new Lyrics.Syllable(1000, 2000, "b")], 2000, null, null, IsWordByWord: true);
        model.Doc = new Lyrics.Doc("t", true, [line], Lyrics.SyncKind.Syllable);

        Assert.Equal(0f, model.VoiceWordProgress.Current());                      // no voice line yet

        model.Step(model.Doc, 1000);
        float mid = model.VoiceWordProgress.Current();
        model.Step(model.Doc, 1900);
        float late = model.VoiceWordProgress.Current();

        Assert.Equal(LyricsModel.WordProgress(line, 1000), mid);
        Assert.InRange(mid, 0.01f, 0.99f);
        Assert.True(late > mid);
    }

    [Fact]
    public void The_opening_seed_is_the_line_at_the_start_position_and_none_for_a_static_document()
    {
        var model = Model();

        Assert.Equal(1, model.SeedActive(LineSynced(), 2500));
        Assert.Equal(-1, model.SeedActive(new Lyrics.Doc("t", false, [L(0, "a")], Lyrics.SyncKind.Unsynced), 2500));
    }

    [Fact]
    public void Publishing_the_seed_retires_the_voice_for_a_static_document()
    {
        var model = Model();
        model.VoiceLine.Value = 2;

        model.PublishSeed(new Lyrics.Doc("t", false, [L(0, "a")], Lyrics.SyncKind.Unsynced), -1);

        Assert.Equal(-1, model.ActiveLine.Peek());
        Assert.Equal(-1, model.VoiceLine.Peek());
    }

    // ── the interlude ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_real_break_moves_the_lit_line_on_and_reports_its_bounds()
    {
        var model = Model();

        var frame = model.Step(WithBreak(), 3000);

        Assert.Equal(1, frame.Active);                                            // the finished line is retired
        Assert.Equal(-1, frame.Voice);
        Assert.Equal(1000, frame.GapStart);
        Assert.Equal(10_000, frame.GapEnd);
    }

    [Fact]
    public void The_dots_are_up_while_following_through_the_break_and_gone_before_the_next_line()
    {
        var model = Model();

        Assert.True(model.DotsUp(1000, 10_000, 3000));
        Assert.False(model.DotsUp(1000, 10_000, 10_000 - Lyrics.Interlude.DotsExitMs));   // they leave a beat early
        Assert.False(model.DotsUp(0, 0, 3000));                                    // not in a break at all

        model.SetFollowMode(Lyrics.FollowMode.DetachedActive);                     // a detached follow owns no overlay
        Assert.False(model.DotsUp(1000, 10_000, 3000));
    }

    [Fact]
    public void The_reserved_band_arms_on_the_active_line_releases_on_the_hand_off_and_relatches_for_a_few_frames()
    {
        var model = Model();

        Assert.True(model.ShouldArmReserve(dotsUp: true, active: 1));
        Assert.False(model.ShouldArmReserve(dotsUp: false, active: 1));
        Assert.True(model.SetReserve(1, out int previous));
        Assert.Equal(-1, previous);
        Assert.False(model.SetReserve(1, out _));                                  // already there: no re-arm
        Assert.False(model.ShouldArmReserve(dotsUp: true, active: 1));
        Assert.Equal(Lyrics.Follow.ReserveRelatchFrames, model.ReserveRelatchFrames);

        Assert.Equal(Lyrics.Interlude.ReserveDip(false), model.ReserveOf(1));
        Assert.Equal(0f, model.ReserveOf(0));
        Assert.Equal(0f, model.ReserveOf(-1));

        Assert.False(model.ShouldReleaseReserve(activeChanged: false, active: 2));
        Assert.False(model.ShouldReleaseReserve(activeChanged: true, active: 1));  // the band's own line is still active
        Assert.True(model.ShouldReleaseReserve(activeChanged: true, active: 2));

        int taken = 0;
        while (model.TakeRelatchFrame()) taken++;
        Assert.Equal(Lyrics.Follow.ReserveRelatchFrames, taken);
        Assert.Equal(0, model.ReserveRelatchFrames);
    }

    [Fact]
    public void The_reserve_is_sized_for_the_surface()
    {
        var rail = Model(large: false);
        var stage = Model(large: true);
        rail.SetReserve(0, out _);
        stage.SetReserve(0, out _);

        Assert.Equal(Lyrics.Interlude.ReserveDip(false), rail.ReserveOf(0));
        Assert.Equal(Lyrics.Interlude.ReserveDip(true), stage.ReserveOf(0));
    }

    // ── the follow and the resync ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_surface_follows_and_needs_no_resync()
    {
        var model = Model();

        Assert.Equal(Lyrics.FollowMode.Following, model.Follow.Peek());
        Assert.False(model.ResyncNeeded);
        Assert.Equal(1f, model.ResyncProgress.Peek());
    }

    [Fact]
    public void Grabbing_the_scroll_detaches_and_the_pill_appears_leaving_following_retires_the_overlays()
    {
        var model = Model();

        Assert.Equal(ScrollEdge.Detach, model.OnScrollActivity(userScrollActive: true, nowMs: 1000));
        var change = model.SetFollowMode(Lyrics.FollowMode.DetachedActive);

        Assert.True(change.Changed);
        Assert.True(change.LeftFollowing);
        Assert.True(change.SuppressionFlipped);                                   // the depth of field turns off with the follow
        Assert.True(model.ResyncNeeded);
        Assert.False(model.SetFollowMode(Lyrics.FollowMode.DetachedActive).Changed);   // an equal write is nothing
    }

    [Fact]
    public void Letting_go_starts_the_countdown_and_the_pill_stays_up()
    {
        var model = Model();
        model.SetFollowMode(Lyrics.FollowMode.DetachedActive);

        Assert.Equal(ScrollEdge.Idle, model.OnScrollActivity(userScrollActive: false, nowMs: 10_000));
        var change = model.SetFollowMode(Lyrics.FollowMode.DetachedIdle);

        Assert.False(change.SuppressionFlipped);                                  // still detached: still no blur
        Assert.True(model.ResyncNeeded);
    }

    [Fact]
    public void A_release_that_was_never_a_grab_is_nothing()
    {
        var model = Model();

        Assert.Equal(ScrollEdge.None, model.OnScrollActivity(userScrollActive: false, nowMs: 10_000));
        Assert.Equal(Lyrics.FollowMode.Following, model.Follow.Peek());
    }

    [Fact]
    public void The_countdown_runs_down_the_ring_and_asks_for_the_resync_when_it_runs_out()
    {
        var model = Model();
        model.SetFollowMode(Lyrics.FollowMode.DetachedActive);
        model.OnScrollActivity(userScrollActive: false, nowMs: 10_000);
        model.SetFollowMode(Lyrics.FollowMode.DetachedIdle);

        Assert.Equal(FollowTick.None, model.TickFollow(10_000 + Lyrics.Follow.ResyncIdleMs / 2));
        Assert.InRange(model.ResyncProgress.Peek(), 0.45f, 0.55f);

        Assert.Equal(FollowTick.BeginResync, model.TickFollow(10_000 + Lyrics.Follow.ResyncIdleMs));
        Assert.Equal(0f, model.ResyncProgress.Peek());
    }

    [Fact]
    public void A_resync_in_flight_keeps_driving_until_it_lands_and_following_returns()
    {
        var model = Model();
        model.SetFollowMode(Lyrics.FollowMode.DetachedIdle);
        model.ClearCountdown();

        var change = model.SetFollowMode(Lyrics.FollowMode.Resyncing);
        Assert.True(change.Changed);
        Assert.False(model.ResyncNeeded);                                          // the pill is gone while the glide runs
        Assert.Equal(FollowTick.DriveResync, model.TickFollow(5000));

        var landed = model.SetFollowMode(Lyrics.FollowMode.Following);
        Assert.False(landed.LeftFollowing);
        Assert.True(landed.SuppressionFlipped);                                    // the blur ladder returns only once the glide has landed
        Assert.Equal(FollowTick.None, model.TickFollow(6000));
    }

    [Fact]
    public void Clearing_the_countdown_refills_the_ring()
    {
        var model = Model();
        model.SetFollowMode(Lyrics.FollowMode.DetachedActive);
        model.OnScrollActivity(userScrollActive: false, nowMs: 0);
        model.SetFollowMode(Lyrics.FollowMode.DetachedIdle);
        model.TickFollow(Lyrics.Follow.ResyncIdleMs / 2);
        Assert.True(model.ResyncProgress.Peek() < 1f);

        model.ClearCountdown();

        Assert.Equal(1f, model.ResyncProgress.Peek());
    }

    [Fact]
    public void Only_a_detached_follow_suppresses_the_depth_of_field()
    {
        Assert.False(LyricsModel.SuppressesDof(Lyrics.FollowMode.Following));
        Assert.True(LyricsModel.SuppressesDof(Lyrics.FollowMode.DetachedActive));
        Assert.True(LyricsModel.SuppressesDof(Lyrics.FollowMode.DetachedIdle));
        Assert.True(LyricsModel.SuppressesDof(Lyrics.FollowMode.Resyncing));
    }

    // ── a better document mid-song ──────────────────────────────────────────────────────────────────────────────────

    static Lyrics.Doc WordSynced()
        => new("track-1", true, [new Lyrics.Line(0, "a", [new Lyrics.Syllable(0, 500, "a")], 500, null, null, IsWordByWord: true)], Lyrics.SyncKind.Syllable);

    [Fact]
    public void A_document_that_is_not_richer_is_ignored()
    {
        var model = Model();
        model.Doc = LineSynced();

        Assert.Equal(UpgradeVerdict.Ignore, model.OfferUpgrade(LineSynced(), model.Doc));
        Assert.Null(model.PendingUpgrade);
    }

    [Fact]
    public void An_empty_document_is_ignored_whatever_it_is_offered_over()
        => Assert.Equal(UpgradeVerdict.Ignore, Model().OfferUpgrade(Lyrics.Doc.Empty("t"), null));

    [Fact]
    public void A_richer_document_lands_at_once_when_nothing_is_lit()
    {
        var model = Model();
        model.Doc = LineSynced();
        Playback.IsPlaying.Value = true;

        Assert.Equal(UpgradeVerdict.Apply, model.OfferUpgrade(WordSynced(), model.Doc));   // no active line yet: nothing to interrupt
        Assert.Null(model.PendingUpgrade);
    }

    [Fact]
    public void A_richer_document_lands_at_once_when_paused()
    {
        var model = Model();
        model.Doc = LineSynced();
        model.ActiveLine.Value = 1;
        Playback.IsPlaying.Value = false;

        Assert.Equal(UpgradeVerdict.Apply, model.OfferUpgrade(WordSynced(), model.Doc));
    }

    [Fact]
    public void A_richer_document_mid_line_is_held_for_the_next_hand_off()
    {
        var model = Model();
        model.Doc = LineSynced();
        model.ActiveLine.Value = 1;
        Playback.IsPlaying.Value = true;
        var upgrade = WordSynced();

        Assert.Equal(UpgradeVerdict.Hold, model.OfferUpgrade(upgrade, model.Doc));
        Assert.Same(upgrade, model.PendingUpgrade);
    }

    // ── the second line ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_second_line_follows_the_session_mode()
    {
        var model = Model();
        Lyrics.Prefs.SetSecondaryLine(Lyrics.Prefs.None);
        Assert.Equal(Lyrics.Prefs.None, model.SyncSecondLine());

        Lyrics.Prefs.SetSecondaryLine(Lyrics.Prefs.Translation);

        Assert.Equal(Lyrics.Prefs.Translation, model.SyncSecondLine());
        Assert.Equal(Lyrics.Prefs.Translation, model.SecondLine.Peek());
    }

    [Fact]
    public void A_timed_document_publishes_the_layers_it_carries_and_a_static_one_publishes_none()
    {
        var model = Model();

        model.PublishSecondaryAvailability(LineSynced(L(0, "a", translation: "x"), L(2000, "b")));
        Assert.Equal(Lyrics.Prefs.HasTranslation, Lyrics.Prefs.Available.Peek());

        model.PublishSecondaryAvailability(new Lyrics.Doc("t", false, [L(0, "a", translation: "x")], Lyrics.SyncKind.Unsynced));
        Assert.Equal(0, Lyrics.Prefs.Available.Peek());

        model.PublishSecondaryAvailability(LineSynced(L(0, "a", translation: "x")));
        model.PublishSecondaryAvailability(null);
        Assert.Equal(0, Lyrics.Prefs.Available.Peek());
    }

    [Fact]
    public void The_globe_cycles_through_the_layers_the_document_has_and_back_to_none()
    {
        Lyrics.Prefs.Available.Value = Lyrics.Prefs.HasTranslation;
        Lyrics.Prefs.SetSecondaryLine(Lyrics.Prefs.None);

        LyricsVerbs.Global.ToggleSecondLine();
        Assert.Equal(Lyrics.Prefs.Translation, Lyrics.Prefs.SecondaryLine());

        LyricsVerbs.Global.ToggleSecondLine();                                      // romanization is skipped: the document lacks it
        Assert.Equal(Lyrics.Prefs.None, Lyrics.Prefs.SecondaryLine());
    }

    // ── the clock ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_probe_drives_the_clock_synchronously_and_reads_as_playing()
    {
        var model = Model();

        long now = model.ReadMediaTime(probeNowMs: 4321, out bool playing, out bool jumped);

        Assert.Equal(4321, now);
        Assert.True(playing);
        Assert.False(jumped);
    }

    private sealed class RecordingSink : ILyricsSceneSink
    {
        public readonly List<string> Calls = [];
        public void ResetFollow() => Calls.Add("reset");
        public void SeekLanded() => Calls.Add("landed");
        public void ResyncRequested() => Calls.Add("resync");
    }

    [Fact]
    public void The_resync_verb_reaches_the_view()
    {
        var model = Model();
        var sink = new RecordingSink();
        model.Sink = sink;

        model.Verbs.Resync();

        Assert.Equal(new[] { "resync" }, sink.Calls);
    }

    [Fact]
    public void A_seek_to_a_line_that_is_not_there_does_nothing_at_all()
    {
        var model = Model();
        var sink = new RecordingSink();
        model.Sink = sink;

        model.Verbs.SeekToLine(0);                                                 // no document yet
        model.Doc = LineSynced();
        model.Verbs.SeekToLine(-1);
        model.Verbs.SeekToLine(3);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void The_header_verbs_that_need_a_line_or_a_view_are_inert_without_a_model()
    {
        LyricsVerbs.Global.SeekToLine(0);
        LyricsVerbs.Global.Resync();
    }
}
