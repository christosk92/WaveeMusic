// ── Wavee.Tests/VideoSurfaceRulesTests.cs — the video SURFACE's rules (owner K), beside owner V's engine rules ────────
//
// Named `VideoSurfaceRules`, not `VideoRules`: `VideoRulesTests.cs` is owner V's (the video ENGINE's prefetch / seek /
// segment / handoff rules). Every class here is prefixed `Video…` and none collides with V's eight.
//
// Ported suites: `DetachedFullscreenRuleTests` (8), `DockedVideoHostingTests` (10), the `ConnectVideoFacts` block of
// `ConnectStateBuilderTests` (5), `VideoStageInputTests` (3), `VideoAspectPersistenceTests` (4),
// `VideoOverrideMutationCoreTests` (the plan / boundary / reveal / mount facts; the three `HasVideoLatch` facts stay
// with the playback owner that holds the latch), and the pure half of `Actions/VideoOverrideUxTests` (the service-bound
// facts are re-stated against `Video.Overrides`, below). New: `Video.Pip`, and the host chokepoint `Video.State.Commit`.
//
// Host facts (`Video.State`, `Video.Prefs`, `Video.Overrides`) each live in ONE class, because each touches ONE
// process-wide static and xunit runs a class's facts sequentially. No window is opened and no engine loop is started.

using System.IO;
using FluentGpu.Signals;
using Wavee;
using Xunit;

using static Wavee.Video;
using PlayHostRules = Wavee.Playback.Video.HostRules;
using PlaybackState = FluentGpu.Media.PlaybackState;
using RailMode = Wavee.Shell.RailMode;

namespace Wavee.Tests;

static class Vid
{
    public const PlacementSet All = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;

    public static PlacementState Off => PlacementState.Music with { Available = All };

    /// <summary>Watching in the pop-out: resolved Detached, the window reported live.</summary>
    public static PlacementState Detached
        => PlacementCore.WithLive(PlacementCore.OpenAt(Off, SurfacePlacement.Detached), SurfacePlacement.Detached);
}

// ── opening at a home must fold the playable's REAL availability first ───────────────────────────────────────────────

public class VideoOpenAtAvailabilityTests
{
    // The placement menu reads availability LIVE (PlacementMenuRules) and offers "Play in a separate window", but the
    // state's stamped Available is None for a session restored mid-track (it is stamped only at a track boundary or a
    // primary click). Committing OpenAt against that stale stamp resolved to None: the video loaded and played with no
    // surface to show it (2026-09-22). State.OpenAt folds first — this is the pure chain it runs.
    [Fact]
    public void OpenAt_from_a_stale_None_availability_resolves_once_the_playable_has_video()
    {
        var stale = PlacementState.Music with { Available = PlacementSet.None };
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(PlacementCore.OpenAt(stale, SurfacePlacement.Detached)));

        var folded = UpgradeGate.FoldAvailability(stale, hasVideo: true, hostCapable: Vid.All);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(PlacementCore.OpenAt(folded, SurfacePlacement.Detached)));
    }

    [Fact]
    public void OpenAt_still_resolves_to_nothing_when_the_playable_has_no_video()
    {
        var stale = PlacementState.Music with { Available = PlacementSet.None };
        var folded = UpgradeGate.FoldAvailability(stale, hasVideo: false, hostCapable: Vid.All);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(PlacementCore.OpenAt(folded, SurfacePlacement.Detached)));
    }

    [Fact]
    public void OpenAt_respects_a_host_that_cannot_open_a_second_window()
    {
        var stale = PlacementState.Music with { Available = PlacementSet.None };
        var folded = UpgradeGate.FoldAvailability(stale, hasVideo: true, hostCapable: PlacementSet.Docked | PlacementSet.Floating);
        Assert.NotEqual(SurfacePlacement.Detached, PlacementCore.Resolve(PlacementCore.OpenAt(folded, SurfacePlacement.Detached)));
    }
}

// ── the pop-out's own fullscreen mode ────────────────────────────────────────────────────────────────────────────────

public class VideoDetachedFullscreenRuleTests
{
    [Fact]
    public void Entering_fullscreen_from_the_pop_out_keeps_the_placement_detached()
    {
        var s = Vid.Detached;
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(s));
        Assert.True(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(s)));
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(s));
    }

    [Fact]
    public void Show_video_at_fullscreen_is_a_different_placement_and_clears_the_mode()
    {
        var moved = PlacementCore.OpenAt(Vid.Detached, SurfacePlacement.Fullscreen);
        Assert.Equal(SurfacePlacement.Fullscreen, PlacementCore.Resolve(moved));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(moved)));
    }

    [Fact]
    public void The_mode_survives_while_the_placement_stays_detached()
    {
        var next = PlacementCore.WithAvailability(Vid.Detached, Vid.All);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(next));
        Assert.True(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(next)));
    }

    [Fact]
    public void The_mode_is_false_after_the_user_closed_the_pop_out()
    {
        var closed = PlacementCore.HostClosed(Vid.Detached, SurfacePlacement.Detached);
        Assert.NotEqual(SurfacePlacement.Detached, PlacementCore.Resolve(closed));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(closed)));
    }

    [Fact]
    public void The_mode_is_false_after_moving_to_another_placement_or_turning_off()
    {
        foreach (var to in new[] { SurfacePlacement.Docked, SurfacePlacement.Floating, SurfacePlacement.Fullscreen })
            Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(PlacementCore.OpenAt(Vid.Detached, to))));
        var off = PlacementCore.TurnOff(Vid.Detached);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(off));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(off)));
    }

    [Fact]
    public void The_mode_is_false_when_the_detached_placement_becomes_unavailable()
    {
        var noVideo = PlacementCore.WithAvailability(Vid.Detached, PlacementSet.None);
        Assert.Equal(SurfacePlacement.None, PlacementCore.Resolve(noVideo));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(noVideo)));

        var noSecondWindow = PlacementCore.WithAvailability(Vid.Detached, PlacementSet.Docked | PlacementSet.Floating);
        Assert.Equal(SurfacePlacement.Floating, PlacementCore.Resolve(noSecondWindow));
        Assert.False(DetachedFullscreenRule.After(current: true, PlacementCore.Resolve(noSecondWindow)));
    }

    [Fact]
    public void The_rule_never_turns_the_mode_on()
    {
        Assert.False(DetachedFullscreenRule.After(current: false, SurfacePlacement.Detached));
        var reopened = PlacementCore.OpenAt(PlacementCore.HostClosed(Vid.Detached, SurfacePlacement.Detached), SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Detached, PlacementCore.Resolve(reopened));
        Assert.False(DetachedFullscreenRule.After(current: false, PlacementCore.Resolve(reopened)));
    }

    [Fact]
    public void Only_detached_keeps_the_mode()
    {
        foreach (var p in PlacementCore.AllPlacements)
            Assert.Equal(p == SurfacePlacement.Detached, DetachedFullscreenRule.After(current: true, p));
    }
}

// ── docked host arbitration ──────────────────────────────────────────────────────────────────────────────────────────

public class VideoDockedHostingTests
{
    const string Watch = "wavee:module:youtube:vid_9aH1";
    const string Other = "wavee:module:youtube:vid_ZZZZ";
    const string Song = "spotify:track:4cOdK2wGLETKBW3PvgPWqT";

    static readonly RailMode[] AllModes = Enum.GetValues<RailMode>();

    [Fact]
    public void Exactly_one_face_mounts_for_every_placement_and_navigation()
    {
        string?[] actives = [null, "", Watch];
        string?[] playings = [null, "", Watch, Song];
        string?[] owners = [null, "", Watch, Other];

        foreach (var resolved in PlacementCore.AllPlacements)
        foreach (var active in actives)
        foreach (var playing in playings)
        {
            foreach (var owner in owners)
                Assert.True(DockedHosting.MountedFaces(resolved, owner, active, playing) <= 1,
                    $"two faces mounted: resolved={resolved} owner={owner ?? "(null)"} active={active ?? "(null)"} playing={playing ?? "(null)"}");

            int expected = resolved == SurfacePlacement.Docked ? 1 : 0;
            Assert.Equal(expected, DockedHosting.MountedFaces(resolved, active, active, playing));
        }
    }

    [Fact]
    public void A_parked_page_never_mounts()
    {
        Assert.True(DockedHosting.PageStageHosts(Watch, Watch));
        Assert.False(DockedHosting.ShouldMount(DockedFace.PageStage, SurfacePlacement.Docked, Other, Watch, Watch));
        Assert.True(DockedHosting.ShouldMount(DockedFace.PageStage, SurfacePlacement.Docked, Watch, Watch, Watch));
        Assert.False(DockedHosting.ShouldMount(DockedFace.PageStage, SurfacePlacement.Docked, Watch, Other, Watch));
    }

    [Fact]
    public void The_rail_card_yields_whole_when_the_stage_hosts()
    {
        Assert.False(DockedHosting.ShouldMount(DockedFace.Cap, SurfacePlacement.Docked, null, Watch, Watch));
        Assert.Equal(DockedHost.PageStage, DockedHosting.HostFor(SurfacePlacement.Docked, Watch, Watch));
    }

    [Fact]
    public void The_rail_card_hosts_when_the_stage_does_not()
    {
        foreach (var (active, playing) in new (string?, string?)[] { (null, Song), ("", Song), (Watch, Song), (Watch, null) })
        {
            Assert.Equal(DockedHost.Rail, DockedHosting.HostFor(SurfacePlacement.Docked, active, playing));
            Assert.True(DockedHosting.ShouldMount(DockedFace.Cap, SurfacePlacement.Docked, null, active, playing));
            Assert.False(DockedHosting.ShouldMount(DockedFace.PageStage, SurfacePlacement.Docked, active, active, playing));
        }
    }

    [Fact]
    public void The_rail_card_mounts_in_every_body_when_docked_resolves()
    {
        foreach (var mode in AllModes)
        {
            Assert.Equal(mode, Rail.VideoCoupling.BodyModeFor(mode, stageHostsVideo: false));
            var substituted = Rail.VideoCoupling.BodyModeFor(mode, stageHostsVideo: true);

            Assert.True(DockedHosting.ShouldMount(DockedFace.Cap, SurfacePlacement.Docked, null, "", Song));
            Assert.True(DockedHosting.ShouldMount(DockedFace.Cap, SurfacePlacement.Docked, null, Watch, Song));
            Assert.False(DockedHosting.ShouldMount(DockedFace.Cap, SurfacePlacement.Docked, null, Watch, Watch));
            Assert.True(substituted is RailMode.Queue or RailMode.Lyrics or RailMode.Friends);
        }
    }

    [Fact]
    public void A_non_docked_placement_mounts_nothing_and_hands_the_surface_back_to_the_rail()
    {
        foreach (var resolved in PlacementCore.AllPlacements)
        {
            if (resolved == SurfacePlacement.Docked) continue;
            foreach (var face in DockedHosting.AllFaces)
            {
                Assert.False(DockedHosting.ShouldMount(face, resolved, Watch, Watch, Watch));
                Assert.False(DockedHosting.ShouldMount(face, resolved, null, "", Song));
            }
            Assert.Equal(DockedHost.Rail, DockedHosting.HostFor(resolved, Watch, Watch));
        }
    }

    [Fact]
    public void Page_stage_hosts_is_ordinal_and_rejects_empty()
    {
        Assert.True(DockedHosting.PageStageHosts(Watch, Watch));
        Assert.False(DockedHosting.PageStageHosts(null, null));
        Assert.False(DockedHosting.PageStageHosts("", ""));
        Assert.False(DockedHosting.PageStageHosts("", null));
        Assert.False(DockedHosting.PageStageHosts(Watch, null));
        Assert.False(DockedHosting.PageStageHosts(Watch, ""));
        Assert.False(DockedHosting.PageStageHosts(null, Watch));
        Assert.False(DockedHosting.PageStageHosts("", Watch));
        Assert.False(DockedHosting.PageStageHosts(Watch, Watch.ToUpperInvariant()));
        Assert.False(DockedHosting.PageStageHosts(Watch.ToUpperInvariant(), Watch));
        Assert.False(DockedHosting.PageStageHosts(Watch, Other));
    }

    [Fact]
    public void Host_of_maps_both_faces_and_never_disagrees_with_host_for()
    {
        Assert.Equal(DockedHost.Rail, DockedHosting.HostOf(DockedFace.Cap));
        Assert.Equal(DockedHost.PageStage, DockedHosting.HostOf(DockedFace.PageStage));
        foreach (var face in DockedHosting.AllFaces)
        foreach (var (active, playing) in new (string?, string?)[] { (Watch, Watch), (Watch, Song), ("", Song) })
        {
            string? owner = face == DockedFace.PageStage ? active : null;
            if (!DockedHosting.ShouldMount(face, SurfacePlacement.Docked, owner, active, playing)) continue;
            Assert.Equal(DockedHosting.HostFor(SurfacePlacement.Docked, active, playing), DockedHosting.HostOf(face));
        }
    }

    [Fact]
    public void The_docked_bit_survives_a_narrow_window_on_a_watch_page()
    {
        Assert.True(DockedHosting.DockedHostAvailable(railFits: false, pageStageWouldHost: DockedHosting.PageStageHosts(Watch, Watch)));
        Assert.False(DockedHosting.DockedHostAvailable(railFits: false, pageStageWouldHost: DockedHosting.PageStageHosts("", Song)));
        Assert.False(DockedHosting.DockedHostAvailable(railFits: false, pageStageWouldHost: DockedHosting.PageStageHosts(Watch, Song)));
        Assert.True(DockedHosting.DockedHostAvailable(railFits: true, pageStageWouldHost: false));
        Assert.True(DockedHosting.DockedHostAvailable(railFits: true, pageStageWouldHost: true));
    }

    [Fact]
    public void The_one_surface_fold_survives_arbitrary_histories()
    {
        var placements = PlacementCore.AllPlacements;
        var sets = new[]
        {
            PlacementSet.None, PlacementSet.Docked, PlacementSet.Floating, PlacementSet.Docked | PlacementSet.Floating, Vid.All,
        };
        var kinds = Enum.GetValues<PlacementCommandKind>();
        string?[] actives = [null, "", Watch, Other];
        string?[] playings = [null, Watch, Other, Song];

        uint rng = 0x0DEC0DEu;
        uint Next() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; }

        for (int seq = 0; seq < 1500; seq++)
        {
            var s = PlacementState.Music;
            for (int step = 0; step < 24; step++)
            {
                var cmd = new PlacementCommand(kinds[Next() % (uint)kinds.Length],
                    placements[Next() % (uint)placements.Length], sets[Next() % (uint)sets.Length]);
                s = PlacementCore.Apply(s, cmd);
                string? active = actives[Next() % (uint)actives.Length];
                string? playing = playings[Next() % (uint)playings.Length];

                var resolved = PlacementCore.Resolve(s);
                foreach (var owner in new[] { active, Other, null, "" })
                    Assert.True(DockedHosting.MountedFaces(resolved, owner, active, playing) <= 1, $"seq {seq} step {step}: {cmd}");
                Assert.Equal(resolved == SurfacePlacement.Docked ? 1 : 0, DockedHosting.MountedFaces(resolved, active, active, playing));

                var host = DockedHosting.HostFor(resolved, active, playing);
                foreach (var face in DockedHosting.AllFaces)
                {
                    string? owner = face == DockedFace.PageStage ? active : null;
                    if (DockedHosting.ShouldMount(face, resolved, owner, active, playing))
                        Assert.Equal(host, DockedHosting.HostOf(face));
                }
            }
        }
    }
}

// ── the Connect-wire half of the no-mid-track-swap rule ──────────────────────────────────────────────────────────────

public class VideoConnectFactsTests
{
    const string Uri = "spotify:track:a";
    const string Gid = "10302a7889774d2f8b1aef877119786c";

    [Fact]
    public void The_first_observation_of_a_track_is_only_a_baseline()
        => Assert.False(new ConnectVideoFacts().Observe(Uri, hasVideo: true, videoGidHex: Gid));

    [Fact]
    public void An_association_landing_mid_track_announces_exactly_once()
    {
        var facts = new ConnectVideoFacts();
        Assert.False(facts.Observe(Uri, false, null));
        Assert.True(facts.Observe(Uri, true, null));
        Assert.False(facts.Observe(Uri, true, null));
        Assert.False(facts.Observe(Uri, true, null));
    }

    [Fact]
    public void A_gid_landing_after_the_badge_announces_again()
    {
        var facts = new ConnectVideoFacts();
        Assert.False(facts.Observe(Uri, false, null));
        Assert.True(facts.Observe(Uri, true, null));
        Assert.True(facts.Observe(Uri, true, Gid));
        Assert.False(facts.Observe(Uri, true, Gid));
    }

    [Fact]
    public void A_track_change_never_announces()
    {
        var facts = new ConnectVideoFacts();
        Assert.False(facts.Observe(Uri, false, null));
        Assert.True(facts.Observe(Uri, true, Gid));
        Assert.False(facts.Observe("spotify:track:b", true, "6c68d9d90e50486aafc6119885f04c3f"));
        Assert.False(facts.Observe(null, false, null));
    }

    [Fact]
    public void Losing_video_never_announces()
    {
        var facts = new ConnectVideoFacts();
        Assert.False(facts.Observe(Uri, true, Gid));
        Assert.False(facts.Observe(Uri, false, null));
    }
}

// ── stage input, mount, aspect ───────────────────────────────────────────────────────────────────────────────────────

public class VideoStageInputTests
{
    [Fact]
    public void Only_the_dedicated_window_hides_the_cursor_windowed()
    {
        Assert.True(StageInput.HidesCursorWindowed(TransportOwner.PopOut));
        Assert.False(StageInput.HidesCursorWindowed(TransportOwner.Docked));
        Assert.False(StageInput.HidesCursorWindowed(TransportOwner.Fullscreen));
        Assert.False(StageInput.HidesCursorWindowed(TransportOwner.GlobalBar));
    }
}

public class VideoSurfaceMountTests
{
    [Fact]
    public void The_stage_mounts_whenever_a_player_exists_even_with_a_null_source()
    {
        Assert.True(SurfaceMount.ShouldMountPlayerStage(playerPresent: true));
        Assert.False(SurfaceMount.ShouldMountPlayerStage(playerPresent: false));
    }

    /// <summary>F174: the stage wrapper never changes shape at first frame (always a ZStack of [stage, poster]); the poster is
    /// collapsed, not removed, once the picture is the only thing on screen.</summary>
    [Theory]
    [InlineData(true, JoinVisual.Video, false)]
    [InlineData(true, JoinVisual.Poster, true)]
    [InlineData(true, JoinVisual.Working, true)]
    [InlineData(true, JoinVisual.Failed, true)]
    [InlineData(false, JoinVisual.Video, true)]
    [InlineData(false, JoinVisual.Poster, true)]
    public void The_poster_is_collapsed_only_when_the_live_picture_is_alone(bool stage, JoinVisual join, bool shown)
        => Assert.Equal(shown, SurfaceMount.PosterShown(stage, join));
}

public class VideoAspectPersistenceTests
{
    [Theory]
    [InlineData(AspectPreference.Fit, "fit")]
    [InlineData(AspectPreference.Crop, "crop")]
    [InlineData(AspectPreference.Stretch, "stretch")]
    [InlineData(AspectPreference.Native, "native")]
    [InlineData(AspectPreference.Custom, "custom")]
    public void Mode_tokens_round_trip(AspectPreference mode, string token)
    {
        Assert.Equal(token, AspectPersistence.SaveMode(mode));
        Assert.Equal(mode, AspectPersistence.LoadMode(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FIT")]
    [InlineData("future-mode")]
    public void A_missing_or_corrupt_mode_falls_back_to_fit(string? raw)
        => Assert.Equal(AspectPreference.Fit, AspectPersistence.LoadMode(raw));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(100.0)]
    public void An_invalid_ratio_falls_back_to_sixteen_by_nine(double raw)
        => Assert.Equal(AspectPersistence.DefaultCustomRatio, AspectPersistence.LoadRatio(raw));

    [Fact]
    public void The_settings_store_restores_mode_and_ratio_across_instances()
    {
        var settings = new MemoryAppSettings();
        settings.Set(Platform.Keys.VideoAspectMode, AspectPersistence.SaveMode(AspectPreference.Custom));
        settings.Set(Platform.Keys.VideoCustomAspectRatio, 2.39);

        Assert.Equal(AspectPreference.Custom, AspectPersistence.LoadMode(settings.Get(Platform.Keys.VideoAspectMode)));
        Assert.Equal(2.39, AspectPersistence.LoadRatio(settings.Get(Platform.Keys.VideoCustomAspectRatio)));
    }
}

// ── local video attachments: the pure UX decisions ───────────────────────────────────────────────────────────────────

public class VideoOverrideUxTests
{
    static bool NoDirs(string _) => false;
    static bool AllDirs(string _) => true;

    static OverrideRow Row(string uri, long addedAt, string title, string? artists = null, string file = "clip.mp4")
        => new(new OverrideRecord(uri, @"C:\v\" + file, addedAt, ""), OverrideStatus.Ok, title, artists, file);

    static OverrideDecision Present(OverrideRecord r) => OverrideUx.Decide(true, in r, quarantined: false, static _ => true);

    [Fact]
    public void The_menu_offers_attach_only_with_no_attachment()
        => Assert.Equal(MenuItems.Attach, OverrideUx.MenuFor(true, "spotify:track:a", true, false, OverrideTier.None));

    [Fact]
    public void An_attached_file_offers_replace_remove_and_reveal_never_attach()
    {
        var items = OverrideUx.MenuFor(true, "spotify:track:a", true, true, OverrideTier.UseOverride);
        Assert.Equal(MenuItems.Replace | MenuItems.Remove | MenuItems.ShowInExplorer, items);
        Assert.Equal(MenuItems.None, items & MenuItems.Attach);
        Assert.Equal(MenuItems.None, items & MenuItems.Locate);
    }

    [Fact]
    public void A_broken_link_swaps_reveal_for_locate()
    {
        var items = OverrideUx.MenuFor(true, "spotify:track:a", true, true, OverrideTier.Broken);
        Assert.Equal(MenuItems.Replace | MenuItems.Remove | MenuItems.Locate, items);
        Assert.Equal(MenuItems.None, items & MenuItems.ShowInExplorer);
    }

    [Fact]
    public void A_quarantined_file_still_offers_reveal_and_repair_but_not_locate()
        => Assert.Equal(MenuItems.Replace | MenuItems.Remove | MenuItems.ShowInExplorer,
            OverrideUx.MenuFor(true, "spotify:track:a", true, true, OverrideTier.Quarantined));

    [Fact]
    public void Multi_selection_or_no_roster_or_no_uri_hides_the_submenu_entirely()
    {
        Assert.Equal(MenuItems.None, OverrideUx.MenuFor(false, "spotify:track:a", true, true, OverrideTier.UseOverride));
        Assert.Equal(MenuItems.None, OverrideUx.MenuFor(true, "spotify:track:a", false, true, OverrideTier.UseOverride));
        Assert.Equal(MenuItems.None, OverrideUx.MenuFor(true, "", true, true, OverrideTier.UseOverride));
        Assert.Equal(MenuItems.None, OverrideUx.MenuFor(true, null, true, true, OverrideTier.UseOverride));
    }

    [Theory]
    [InlineData(@"C:\v\a.mp4", true)]
    [InlineData(@"C:\v\a.MP4", true)]
    [InlineData(@"C:\v\a.Mp4", true)]
    [InlineData(@"C:\v\a.mkv", false)]
    [InlineData(@"C:\v\amp4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Is_mp4_is_case_insensitive_and_extension_exact(string? path, bool expected)
        => Assert.Equal(expected, OverrideUx.IsMp4(path));

    [Fact]
    public void The_playable_filter_and_the_audio_predicate_agree()
    {
        var (_, spec) = OverrideUx.PlayableFilter("Playable");
        foreach (var ext in spec.Split(';'))
        {
            string sample = @"C:\m\x" + ext.TrimStart('*');
            Assert.True(OverrideUx.IsAudioFile(sample) || OverrideUx.IsMp4(sample), sample);
        }
        Assert.Equal(("Video", "*.mp4"), OverrideUx.PickerFilter("Video"));
        Assert.False(OverrideUx.IsAudioFile(@"C:\m\x.wav"));
    }

    [Fact]
    public void Validate_rejects_a_non_mp4_before_it_ever_touches_the_disk()
    {
        bool probed = false;
        Assert.Equal(AttachRejection.NotMp4, OverrideUx.Validate(@"C:\v\a.mkv", _ => { probed = true; return true; }));
        Assert.False(probed);
    }

    [Fact]
    public void Validate_rejects_a_missing_file_and_treats_a_throwing_probe_as_missing()
    {
        Assert.Equal(AttachRejection.NotFound, OverrideUx.Validate(@"C:\v\a.mp4", _ => false));
        Assert.Equal(AttachRejection.NotFound, OverrideUx.Validate(@"\\nas\v\a.mp4", _ => throw new IOException()));
        Assert.Equal(AttachRejection.None, OverrideUx.Validate(@"C:\v\a.mp4", _ => true));
    }

    [Fact]
    public void First_mp4_takes_the_first_video_in_a_mixed_drop()
    {
        Assert.Equal(@"C:\v\b.mp4", OverrideUx.FirstMp4([@"C:\v\a.txt", @"C:\v\b.mp4", @"C:\v\c.mp4"]));
        Assert.Null(OverrideUx.FirstMp4([@"C:\v\a.txt", @"C:\v\b.mkv"]));
        Assert.Null(OverrideUx.FirstMp4([]));
        Assert.Null(OverrideUx.FirstMp4(null));
    }

    [Fact]
    public void The_tier_walk_checks_quarantine_before_existence()
    {
        var rec = new OverrideRecord("spotify:track:a", @"C:\v\a.mp4", 1, "local:video:aaaa");
        Assert.Equal(OverrideTier.None, OverrideUx.Decide(false, in rec, false, static _ => true).Tier);
        Assert.Equal(OverrideTier.UseOverride, OverrideUx.Decide(true, in rec, false, static _ => true).Tier);
        Assert.True(OverrideUx.Decide(true, in rec, false, static _ => true).Wins);
        Assert.Equal(OverrideTier.Broken, OverrideUx.Decide(true, in rec, false, static _ => false).Tier);
        Assert.Equal(OverrideTier.Broken, OverrideUx.Decide(true, in rec, false, static _ => throw new IOException()).Tier);
        Assert.Equal(OverrideTier.Quarantined, OverrideUx.Decide(true, in rec, true, static _ => false).Tier);
    }

    [Fact]
    public void The_status_chip_splits_broken_on_whether_the_volume_is_mounted()
    {
        var rec = new OverrideRecord("spotify:track:a", @"C:\v\gone.mp4", 1, "k");
        var ok = OverrideUx.Decide(true, in rec, false, static _ => true);
        var bad = OverrideUx.Decide(true, in rec, true, static _ => true);
        var broken = OverrideUx.Decide(true, in rec, false, static _ => false);

        Assert.Equal(OverrideStatus.Ok, OverrideUx.StatusOf(ok, AllDirs));
        Assert.Equal(OverrideStatus.Unplayable, OverrideUx.StatusOf(bad, AllDirs));
        Assert.Equal(OverrideStatus.Missing, OverrideUx.StatusOf(broken, AllDirs));
        Assert.Equal(OverrideStatus.DriveOffline, OverrideUx.StatusOf(broken, NoDirs));
    }

    [Fact]
    public void Drive_offline_rows_are_neither_locatable_nor_revealable()
    {
        var offline = new OverrideRow(default, OverrideStatus.DriveOffline, "t", null, "a.mp4");
        var missing = new OverrideRow(default, OverrideStatus.Missing, "t", null, "a.mp4");
        var ok = new OverrideRow(default, OverrideStatus.Ok, "t", null, "a.mp4");
        var bad = new OverrideRow(default, OverrideStatus.Unplayable, "t", null, "a.mp4");
        Assert.False(offline.CanLocate);
        Assert.False(offline.CanReveal);
        Assert.True(missing.CanLocate);
        Assert.False(missing.CanReveal);
        Assert.False(ok.CanLocate);
        Assert.True(ok.CanReveal);
        Assert.True(bad.CanReveal);
    }

    [Fact]
    public void Nearest_existing_ancestor_walks_up_to_the_closest_surviving_folder()
    {
        string full = Path.GetFullPath(@"C:\media\videos\2026\clip.mp4");
        string keep = Path.GetDirectoryName(Path.GetDirectoryName(full)!)!;
        Assert.Equal(keep, OverrideUx.NearestExistingAncestor(full, d => d.Length <= keep.Length));
    }

    [Fact]
    public void Nearest_existing_ancestor_is_null_when_nothing_on_the_chain_exists()
    {
        Assert.Null(OverrideUx.NearestExistingAncestor(@"Z:\gone\clip.mp4", NoDirs));
        Assert.Null(OverrideUx.NearestExistingAncestor(null, AllDirs));
        Assert.Null(OverrideUx.NearestExistingAncestor("", AllDirs));
    }

    [Fact]
    public void The_roster_lists_every_attachment_newest_first_with_file_name_and_status()
    {
        OverrideRecord[] all =
        [
            new("spotify:track:a", @"C:\v\a.mp4", 1_000, "ka"),
            new("spotify:track:b", @"C:\v\b.mp4", 5_000, "kb"),
        ];
        var rows = OverrideUx.BuildRoster(all, Present, AllDirs);
        Assert.Equal(2, rows.Count);
        Assert.Equal("spotify:track:b", rows[0].Uri);
        Assert.Equal("spotify:track:a", rows[1].Uri);
        Assert.Equal("b.mp4", rows[0].FileName);
        Assert.All(rows, r => Assert.Equal(OverrideStatus.Ok, r.Status));
    }

    [Fact]
    public void The_roster_resolves_title_and_credits_and_falls_back_to_the_uri()
    {
        OverrideRecord[] all =
        [
            new("spotify:track:known", @"C:\v\a.mp4", 2, "k1"),
            new("spotify:track:stranger", @"C:\v\b.mp4", 1, "k2"),
        ];
        var rows = OverrideUx.BuildRoster(all, Present, AllDirs,
            title: uri => uri == "spotify:track:known" ? "Real Title" : null,
            artistLine: uri => uri == "spotify:track:known" ? "An Artist, Another" : null);

        Assert.Equal("Real Title", rows[0].Title);
        Assert.Equal("An Artist, Another", rows[0].Subtitle);
        Assert.Equal("spotify:track:stranger", rows[1].Title);
        Assert.Null(rows[1].Subtitle);
    }

    [Fact]
    public void The_roster_is_empty_without_records_and_survives_a_throwing_resolver()
    {
        Assert.Empty(OverrideUx.BuildRoster(null, Present, AllDirs));
        Assert.Empty(OverrideUx.BuildRoster([], Present, AllDirs));
        OverrideRecord[] one = [new("spotify:track:a", @"C:\v\a.mp4", 1, "k")];
        var rows = OverrideUx.BuildRoster(one, Present, AllDirs, _ => throw new InvalidOperationException("store is busy"));
        var row = Assert.Single(rows);
        Assert.Equal("spotify:track:a", row.Title);
    }

    [Fact]
    public void Recently_added_takes_the_newest_n_newest_first_even_from_unsorted_input()
    {
        OverrideRow[] rows =
        [
            Row("spotify:track:c", 3_000, "C"), Row("spotify:track:a", 1_000, "A"), Row("spotify:track:e", 5_000, "E"),
            Row("spotify:track:b", 2_000, "B"), Row("spotify:track:d", 4_000, "D"),
        ];
        var recent = OverrideUx.RecentlyAdded(rows, 3);
        Assert.Equal(new[] { "E", "D", "C" }, recent.ConvertAll(r => r.Title));
        Assert.Equal("C", rows[0].Title);   // the input is never reordered under the caller
    }

    [Fact]
    public void Recently_added_ties_break_on_uri_so_a_same_second_batch_never_shuffles()
    {
        OverrideRow[] rows = [Row("spotify:track:c", 7, "C"), Row("spotify:track:a", 7, "A"), Row("spotify:track:b", 7, "B")];
        OverrideRow[] reversed = [rows[2], rows[1], rows[0]];
        var first = OverrideUx.RecentlyAdded(rows, 3).ConvertAll(r => r.Uri);
        var again = OverrideUx.RecentlyAdded(reversed, 3).ConvertAll(r => r.Uri);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b", "spotify:track:c" }, first);
        Assert.Equal(first, again);
    }

    [Fact]
    public void Recently_added_returns_everything_when_short_and_nothing_for_degenerate_input()
    {
        OverrideRow[] rows = [Row("spotify:track:a", 1, "A"), Row("spotify:track:b", 2, "B")];
        Assert.Equal(2, OverrideUx.RecentlyAdded(rows, 5).Count);
        Assert.Empty(OverrideUx.RecentlyAdded(rows, 0));
        Assert.Empty(OverrideUx.RecentlyAdded([]));
        Assert.Empty(OverrideUx.RecentlyAdded(null));
        var twenty = new OverrideRow[20];
        for (int i = 0; i < twenty.Length; i++) twenty[i] = Row("spotify:track:" + i, i, "T" + i);
        Assert.Equal(OverrideUx.RecentCount, OverrideUx.RecentlyAdded(twenty).Count);
    }

    [Fact]
    public void Search_matches_title_credits_and_file_name_case_insensitively()
    {
        OverrideRow[] rows =
        [
            Row("spotify:track:a", 3, "Midnight City", "M83", "midnight-live.mp4"),
            Row("spotify:track:b", 2, "Outro", "M83", "outro.mp4"),
            Row("spotify:track:c", 1, "Teardrop", "Massive Attack", "tear.MP4"),
        ];
        Assert.Equal("spotify:track:a", Assert.Single(OverrideUx.Search(rows, "midnight")).Uri);
        Assert.Equal("spotify:track:a", Assert.Single(OverrideUx.Search(rows, "MIDNIGHT")).Uri);
        Assert.Equal(2, OverrideUx.Search(rows, "m83").Count);
        Assert.Equal("spotify:track:c", Assert.Single(OverrideUx.Search(rows, "tear.mp4")).Uri);
        Assert.Empty(OverrideUx.Search(rows, "nothing here"));
    }

    [Fact]
    public void Search_preserves_roster_order_and_an_empty_query_restores_everything()
    {
        OverrideRow[] rows = [Row("spotify:track:a", 3, "Alpha", "Band"), Row("spotify:track:b", 2, "Beta", "Band"), Row("spotify:track:c", 1, "Gamma", "Band")];
        var hits = OverrideUx.Search(rows, "band");
        Assert.Equal(3, hits.Count);
        Assert.Equal("Alpha", hits[0].Title);
        Assert.Equal("Gamma", hits[2].Title);
        Assert.Same(rows, OverrideUx.Search(rows, ""));
        Assert.Same(rows, OverrideUx.Search(rows, "   "));
        Assert.Same(rows, OverrideUx.Search(rows, null));
        Assert.Empty(OverrideUx.Search([], "a"));
        Assert.Empty(OverrideUx.Search(null, "a"));
    }

    [Fact]
    public void Search_ignores_surrounding_whitespace_but_never_matches_on_the_path()
    {
        OverrideRow[] rows = [Row("spotify:track:a", 1, "Alpha", "Band", "clip.mp4")];
        Assert.Single(OverrideUx.Search(rows, "  alpha  "));
        Assert.Empty(OverrideUx.Search(rows, @"C:\v"));
    }

    [Fact]
    public void Is_searching_treats_whitespace_as_no_query()
    {
        Assert.False(OverrideUx.IsSearching(null));
        Assert.False(OverrideUx.IsSearching(""));
        Assert.False(OverrideUx.IsSearching("   "));
        Assert.True(OverrideUx.IsSearching("a"));
    }

    [Fact]
    public void The_root_section_walks_empty_recent_results_no_matches()
    {
        Assert.Equal(ManagerSection.Empty, OverrideUx.RootSection(0, null, 0));
        Assert.Equal(ManagerSection.Empty, OverrideUx.RootSection(0, "anything", 0));
        Assert.Equal(ManagerSection.Recent, OverrideUx.RootSection(9, null, 0));
        Assert.Equal(ManagerSection.Recent, OverrideUx.RootSection(9, "  ", 0));
        Assert.Equal(ManagerSection.Results, OverrideUx.RootSection(9, "mid", 2));
        Assert.Equal(ManagerSection.NoMatches, OverrideUx.RootSection(9, "zzz", 0));
        Assert.Equal(ManagerSection.Recent, OverrideUx.RootSection(9, "", 0));
    }

    [Fact]
    public void Browse_all_shows_whenever_something_is_attached_and_no_query_is_live()
    {
        Assert.True(OverrideUx.ShowsBrowseAll(1, null));
        Assert.True(OverrideUx.ShowsBrowseAll(50, "  "));
        Assert.False(OverrideUx.ShowsBrowseAll(0, null));
        Assert.False(OverrideUx.ShowsBrowseAll(50, "mid"));
    }

    [Fact]
    public void Title_subtitle_and_file_name_fall_back_honestly()
    {
        Assert.Equal("spotify:track:x", OverrideUx.TitleFor("spotify:track:x", null));
        Assert.Equal("spotify:track:x", OverrideUx.TitleFor("spotify:track:x", ""));
        Assert.Equal("T", OverrideUx.TitleFor("spotify:track:x", "T"));
        Assert.Null(OverrideUx.SubtitleFor(""));
        Assert.Equal("A", OverrideUx.SubtitleFor("A"));
        Assert.Equal("", OverrideUx.FileNameOf(null));
        Assert.Equal("clip.mp4", OverrideUx.FileNameOf(@"C:\v\clip.mp4"));
    }
}

public class VideoOverrideMutationTests
{
    const string KeyA = "local:video:aaaa";
    const string KeyB = "local:video:bbbb";

    [Fact]
    public void Attach_does_not_clear_the_has_video_latch_and_clears_dead_only()
    {
        var p = OverrideMutation.Plan(OverrideMutationKind.Attach, isCurrentPlayable: true, videoAlreadyActive: false,
            previousSourceKey: null, nextSourceKey: KeyA);
        Assert.False(p.ClearHasVideoLatch);
        Assert.True(p.ClearDeadVideoLatch);
        Assert.True(p.CommitHasVideoUpgrade);
        Assert.False(p.ForceReloadIfVideo);
        Assert.True(p.RevealSurfaceIfCurrent);
    }

    [Fact]
    public void Remove_clears_the_has_video_latch()
    {
        var p = OverrideMutation.Plan(OverrideMutationKind.Remove, true, true, KeyA, null);
        Assert.True(p.ClearHasVideoLatch);
        Assert.True(p.ClearDeadVideoLatch);
        Assert.False(p.ForceReloadIfVideo);
        Assert.False(p.RevealSurfaceIfCurrent);
    }

    [Fact]
    public void Replace_with_the_same_key_does_not_force_a_reload()
    {
        var p = OverrideMutation.Plan(OverrideMutationKind.Replace, true, true, KeyA, KeyA);
        Assert.False(p.ClearHasVideoLatch);
        Assert.False(p.ForceReloadIfVideo);
    }

    [Fact]
    public void Replace_with_a_changed_key_while_video_is_active_forces_a_reload()
    {
        var p = OverrideMutation.Plan(OverrideMutationKind.Replace, true, true, KeyA, KeyB);
        Assert.False(p.ClearHasVideoLatch);
        Assert.True(p.ForceReloadIfVideo);
        Assert.True(p.RevealSurfaceIfCurrent);
    }

    [Fact]
    public void Attach_while_audio_does_not_force_a_reload()
        => Assert.False(OverrideMutation.Plan(OverrideMutationKind.Attach, true, false, null, KeyA).ForceReloadIfVideo);

    [Fact]
    public void A_mutation_to_another_playable_never_reveals_or_reloads()
    {
        var p = OverrideMutation.Plan(OverrideMutationKind.Replace, isCurrentPlayable: false, true, KeyA, KeyB);
        Assert.False(p.ForceReloadIfVideo);
        Assert.False(p.RevealSurfaceIfCurrent);
    }

    [Fact]
    public void A_null_next_uri_is_not_a_real_track_boundary()
    {
        Assert.False(OverrideMutation.IsRealTrackBoundary("spotify:track:a", null));
        Assert.False(OverrideMutation.IsRealTrackBoundary("spotify:track:a", ""));
        Assert.False(OverrideMutation.IsRealTrackBoundary(null, "spotify:track:a"));
        Assert.False(OverrideMutation.IsRealTrackBoundary("spotify:track:a", "spotify:track:a"));
        Assert.True(OverrideMutation.IsRealTrackBoundary("spotify:track:a", "spotify:track:b"));
    }

    [Fact]
    public void Can_reveal_requires_has_video_committed()
    {
        Assert.False(OverrideMutation.CanReveal(isCurrent: true, hasVideoCommitted: false, alreadyActive: false));
        Assert.False(OverrideMutation.CanReveal(isCurrent: false, hasVideoCommitted: true, alreadyActive: false));
        Assert.False(OverrideMutation.CanReveal(isCurrent: true, hasVideoCommitted: true, alreadyActive: true));
        Assert.True(OverrideMutation.CanReveal(isCurrent: true, hasVideoCommitted: true, alreadyActive: false));
    }
}

// ── the in-window mini player's geometry ─────────────────────────────────────────────────────────────────────────────

public class VideoPipTests
{
    [Fact]
    public void The_fallback_ratio_is_the_0_2_9_default_rect_not_nine_sixteenths()
    {
        Assert.Equal(202f / 360f, Pip.FallbackRatio);
        Assert.NotEqual(0.5625f, Pip.FallbackRatio);
    }

    [Fact]
    public void A_landscape_source_fits_its_width()
        => Assert.Equal(202.5f, Pip.FitHeight(360f, 9f / 16f, 900f), 2);

    [Fact]
    public void A_portrait_source_is_capped_by_the_viewport_and_never_walks_off_the_bottom()
        => Assert.Equal(600f - Pip.ReserveBottom - Pip.ReserveTop, Pip.FitHeight(360f, 16f / 9f, 600f), 2);

    [Fact]
    public void The_floor_wins_the_tie_in_a_very_short_window_and_for_a_tiny_width()
    {
        Assert.Equal(Pip.MinH, Pip.FitHeight(360f, 16f / 9f, 200f));
        Assert.Equal(Pip.MinH, Pip.FitHeight(100f, 9f / 16f, 900f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void An_unreported_or_bogus_ratio_uses_the_fallback(float ratio)
        => Assert.Equal(Pip.DefaultH, Pip.FitHeight(Pip.DefaultW, ratio, 900f), 2);

    [Fact]
    public void Clamp_pulls_a_parked_card_back_inside_a_shrunken_viewport()
    {
        float x = 2000f, y = 2000f;
        Pip.ClampToViewport(ref x, ref y, 360f, 202f, 1280f, 720f);
        Assert.Equal(920f, x);
        Assert.Equal(518f, y);

        x = -50f; y = -50f;
        Pip.ClampToViewport(ref x, ref y, 360f, 202f, 1280f, 720f);
        Assert.Equal(0f, x);
        Assert.Equal(0f, y);

        x = 10f; y = 10f;
        Pip.ClampToViewport(ref x, ref y, 360f, 202f, 200f, 100f);   // the card is bigger than the viewport
        Assert.Equal(0f, x);
        Assert.Equal(0f, y);
    }

    [Fact]
    public void The_anchor_is_bottom_right_above_the_player_bar()
    {
        Assert.Equal((904f, 430f), Pip.Anchor(360f, 202f, 1280f, 720f));
        Assert.Equal((0f, 0f), Pip.Anchor(360f, 202f, 100f, 100f));
    }
}

// ── host: the ONE placement chokepoint ───────────────────────────────────────────────────────────────────────────────

public sealed class VideoStateTests : IDisposable
{
    public VideoStateTests() => Reset();
    public void Dispose() => Reset();

    static void Reset()
    {
        Video.State.HostCapability.Value = PlacementSet.Docked | PlacementSet.Floating;
        Video.State.DetachedFullscreen.Value = false;
        Video.State.EndPopOutHandoff();
        foreach (var p in PlacementCore.AllPlacements) Video.State.ReportLive(p, mounted: false);
        Video.State.Commit(PlacementState.Music);
    }

    [Fact]
    public void The_first_primary_click_docks_and_republishes_the_transport_owner()
    {
        Video.State.TogglePrimary(hasVideo: true);
        Assert.Equal(SurfacePlacement.Docked, Video.State.Resolved);
        Assert.True(Video.State.IsActive);
        Assert.Equal(TransportOwner.Docked, Video.State.Transport.Peek());

        Video.State.TogglePrimary(hasVideo: true);
        Assert.Equal(SurfacePlacement.None, Video.State.Resolved);
        Assert.Equal(TransportOwner.GlobalBar, Video.State.Transport.Peek());
    }

    [Fact]
    public void The_conservative_host_seed_degrades_a_detached_request_to_the_mini_player()
    {
        Video.State.FoldAvailability(hasVideo: true);
        Video.State.OpenAt(SurfacePlacement.Detached);
        Assert.Equal(SurfacePlacement.Floating, Video.State.Resolved);
        Assert.Equal(SurfacePlacement.Detached, Video.State.Surface.Peek().Requested);
    }

    [Fact]
    public void Commit_drops_the_pop_out_fullscreen_bit_the_moment_the_placement_leaves_detached()
    {
        Video.State.HostCapability.Value = Vid.All;
        Video.State.FoldAvailability(hasVideo: true);
        Video.State.OpenAt(SurfacePlacement.Detached);
        Video.State.DetachedFullscreen.Value = true;                 // the pop-out's own toggle

        Video.State.FoldAvailability(hasVideo: true);                // a commit that keeps Detached keeps the mode
        Assert.True(Video.State.DetachedFullscreen.Peek());
        Assert.Equal(TransportOwner.PopOut, Video.State.Transport.Peek());

        Video.State.ReportClosed(SurfacePlacement.Detached);         // Alt+F4 → the mini player, and the mode is gone
        Assert.Equal(SurfacePlacement.Floating, Video.State.Resolved);
        Assert.False(Video.State.DetachedFullscreen.Peek());

        Video.State.OpenAt(SurfacePlacement.Detached);               // reopening starts windowed
        Assert.False(Video.State.DetachedFullscreen.Peek());
    }

    [Fact]
    public void Live_reports_are_scoped_per_surface()
    {
        Video.State.ReportLive(SurfacePlacement.Detached, mounted: true);
        Video.State.ReportLive(SurfacePlacement.Floating, mounted: false);
        Assert.Equal(SurfacePlacement.Detached, Video.State.LiveSurface);
        Video.State.ReportLive(SurfacePlacement.Detached, mounted: false);
        Assert.Equal(SurfacePlacement.None, Video.State.LiveSurface);
    }

    // F170: Live is reality bookkeeping written from passive effects. It used to ride the shared Surface signal, so every
    // report woke the player bar, the rail frame, the video surfaces and the keeper for a value nothing resolves from.
    [Fact]
    public void Live_reports_never_wake_a_surface_subscriber()
    {
        Video.State.FoldAvailability(hasVideo: true);
        Video.State.OpenAt(SurfacePlacement.Docked);
        var runtime = new ReactiveRuntime();
        int runs = 0;
        using var reader = new Effect(runtime, () => { _ = Video.State.Surface.Value; runs++; });
        runtime.Flush();
        Assert.Equal(1, runs);

        Video.State.ReportLive(SurfacePlacement.Docked, mounted: false);
        Video.State.ReportLive(SurfacePlacement.Floating, mounted: true);
        Video.State.ReportLive(SurfacePlacement.Floating, mounted: false);
        runtime.Flush();
        Assert.Equal(1, runs);                                        // no re-render for any report
        Assert.Equal(SurfacePlacement.None, Video.State.LiveSurface);

        Video.State.OpenAt(SurfacePlacement.Floating);                // a real placement change still wakes it, once
        runtime.Flush();
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Live_reports_do_not_touch_the_published_placement_value()
    {
        Video.State.FoldAvailability(hasVideo: true);
        Video.State.OpenAt(SurfacePlacement.Floating);
        var before = Video.State.Surface.Peek();
        Video.State.ReportLive(SurfacePlacement.Floating, mounted: true);
        Assert.Equal(before, Video.State.Surface.Peek());
        Assert.Equal(SurfacePlacement.Floating, Video.State.LiveSurface);
    }

    // F180: the pop-out hand-off the main presenter's release rides on.
    [Fact]
    public void Pop_out_hand_off_releases_the_main_presenter_on_the_first_presented_frame()
    {
        Assert.False(Video.State.PopOutPresented.Peek());
        Video.State.ReportPopOutPresented();                          // nothing in flight: inert
        Assert.False(Video.State.PopOutPresented.Peek());

        Video.State.BeginPopOutHandoff();
        Assert.False(Video.State.PopOutPresented.Peek());
        Video.State.ReportPopOutPresented();
        Assert.True(Video.State.PopOutPresented.Peek());

        Video.State.EndPopOutHandoff();                               // the window went away: the next pop-out starts clean
        Assert.False(Video.State.PopOutPresented.Peek());
    }

    [Fact]
    public void Pop_out_hand_off_grace_expiry_releases_the_main_presenter_too()
    {
        Video.State.BeginPopOutHandoff();
        Video.State.ExpirePopOutHandoff();
        Assert.True(Video.State.PopOutPresented.Peek());
        Video.State.EndPopOutHandoff();

        Video.State.ExpirePopOutHandoff();                            // a stale timer with no hand-off in flight is inert
        Assert.False(Video.State.PopOutPresented.Peek());
    }

    [Fact]
    public void A_later_report_inside_the_pop_out_does_not_re_release_a_finished_hand_off()
    {
        Video.State.BeginPopOutHandoff();
        Video.State.ReportPopOutPresented();
        Video.State.EndPopOutHandoff();
        Video.State.ReportPopOutPresented();                          // a generation remount inside the window reports again
        Assert.False(Video.State.PopOutPresented.Peek());
    }

    // E3: a warm (parked) pop-out keeps its mounted element, so its first-surface report never fires again; the reveal of a
    // REUSED window is therefore what releases the hand-off. A fresh window's reveal must not (its element reports it).
    [Fact]
    public void Pop_out_hand_off_is_released_by_the_reveal_of_a_reused_window_only()
    {
        Video.State.BeginPopOutHandoff();
        Video.State.ReportPopOutRevealed(reusedWindow: false);
        Assert.False(Video.State.PopOutPresented.Peek());             // fresh window: waits for its element's first presented surface

        Video.State.ReportPopOutRevealed(reusedWindow: true);
        Assert.True(Video.State.PopOutPresented.Peek());              // warm window: the reveal is the presentation (no 2 s grace)

        Video.State.EndPopOutHandoff();
        Video.State.ReportPopOutRevealed(reusedWindow: true);         // no hand-off in flight: inert
        Assert.False(Video.State.PopOutPresented.Peek());
    }

    [Fact]
    public void Main_window_fullscreen_takes_the_transport_and_exits_home()
    {
        Video.State.HostCapability.Value = Vid.All;
        Video.State.FoldAvailability(hasVideo: true);
        Video.State.OpenAt(SurfacePlacement.Docked);
        Video.State.EnterFullscreen();
        Assert.Equal(TransportOwner.Fullscreen, Video.State.Transport.Peek());
        Video.State.ExitFullscreen();
        Assert.Equal(SurfacePlacement.Docked, Video.State.Resolved);
        Video.State.Demote(SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.Floating, Video.State.Resolved);
        Assert.Equal(SurfacePlacement.Docked, Video.State.Surface.Peek().Preferred);
    }
}

public class VideoPrefsTests
{
    [Fact]
    public void Always_on_top_defaults_true_and_round_trips_with_an_epoch_bump()
    {
        Assert.True(Video.Prefs.AlwaysOnTop(null));
        var s = new MemoryAppSettings();
        Assert.True(Video.Prefs.AlwaysOnTop(s));
        int before = Video.Prefs.Epoch.Peek();
        Video.Prefs.SetAlwaysOnTop(s, false);
        Assert.False(Video.Prefs.AlwaysOnTop(s));
        Assert.True(Video.Prefs.Epoch.Peek() > before);
    }

    [Fact]
    public void The_aspect_policy_persists_by_name_and_a_bogus_custom_ratio_is_clamped_on_write()
    {
        var s = new MemoryAppSettings();
        Assert.Equal(AspectPreference.Fit, Video.Prefs.Aspect(s));
        Assert.Equal(AspectPersistence.DefaultCustomRatio, Video.Prefs.CustomRatio(null));

        Video.Prefs.SetAspect(s, AspectPreference.Custom, double.NaN);
        Assert.Equal(AspectPreference.Custom, Video.Prefs.Aspect(s));
        Assert.Equal("custom", s.Get(Platform.Keys.VideoAspectMode));
        Assert.Equal(AspectPersistence.DefaultCustomRatio, Video.Prefs.CustomRatio(s));

        Video.Prefs.SetAspect(s, AspectPreference.Crop, 2.39);       // a non-custom mode never writes the ratio
        Assert.Equal(AspectPreference.Crop, Video.Prefs.Aspect(s));
        Assert.Equal(AspectPersistence.DefaultCustomRatio, Video.Prefs.CustomRatio(s));
    }
}

// ── host: the warm override roster ───────────────────────────────────────────────────────────────────────────────────

public sealed class VideoOverridesRosterTests : IDisposable
{
    const string A = "spotify:track:a";
    readonly MemoryAppSettings _settings = new();

    public VideoOverridesRosterTests()
    {
        Video.Overrides.FileExists = static _ => true;
        Video.Overrides.DirectoryExists = static _ => true;
        Video.Overrides.Attach(_settings);
    }

    public void Dispose()
    {
        Video.Overrides.Attach(null);
        Video.Overrides.FileExists = File.Exists;
        Video.Overrides.DirectoryExists = Directory.Exists;
    }

    [Fact]
    public void A_duplicate_attach_is_the_replace_and_the_uri_stays_the_primary_key()
    {
        Assert.True(Video.Overrides.Present);
        Assert.Equal(OverrideMutationKind.Attach, Video.Overrides.Attach(A, @"C:\v\first.mp4", "k1", 100));
        Assert.Equal(OverrideMutationKind.Replace, Video.Overrides.Attach(A, @"C:\v\second.mp4", "k2", 200));
        int count = Video.Overrides.Count;
        Assert.Equal(1, count);
        Assert.True(Video.Overrides.TryGet(A, out var rec));
        Assert.Equal(@"C:\v\second.mp4", rec.Path);
    }

    [Fact]
    public void A_refused_path_changes_nothing()
    {
        Assert.Null(Video.Overrides.Attach(A, @"C:\v\a.mkv", "k", 1));
        Video.Overrides.FileExists = static _ => false;
        Assert.Null(Video.Overrides.Attach(A, @"C:\v\a.mp4", "k", 1));
        Assert.Null(Video.Overrides.Attach("", @"C:\v\a.mp4", "k", 1));
        int count = Video.Overrides.Count;
        Assert.Equal(0, count);
        Assert.False(Video.Overrides.Has(A));
    }

    [Fact]
    public void The_roster_survives_a_restart_through_the_store()
    {
        Video.Overrides.Attach(A, @"C:\v\a.mp4", "k1", 100);
        Video.Overrides.Attach("spotify:episode:e1", @"D:\clips\e.mp4", "k2", 200);

        Video.Overrides.Attach(_settings);                           // a fresh load from the same store
        Assert.Equal(2, Video.Overrides.Count);
        Assert.True(Video.Overrides.TryGet("spotify:episode:e1", out var e));
        Assert.Equal(@"D:\clips\e.mp4", e.Path);
        Assert.Equal(200, e.AddedAtUnix);
        Assert.Equal("k2", e.SourceKey);
    }

    [Fact]
    public void A_malformed_store_line_is_skipped_not_fatal()
    {
        _settings.Set(Video.Overrides.StoreKey, "spotify:track:a\t5\tk\tC:\\v\\a.mp4\nnot a record\n\t1\tk\tC:\\x.mp4\nspotify:track:b\tNaN\tk\tC:\\v\\b.mp4");
        Video.Overrides.Attach(_settings);
        Assert.Equal(2, Video.Overrides.Count);
        Assert.True(Video.Overrides.TryGet("spotify:track:b", out var b));
        Assert.Equal(0, b.AddedAtUnix);
    }

    [Fact]
    public void Remove_is_idempotent_and_the_undo_of_a_remove_reattaches()
    {
        Video.Overrides.Attach(A, @"C:\v\a.mp4", "k", 1);
        Assert.True(Video.Overrides.Remove(A));
        Assert.False(Video.Overrides.Remove(A));
        Assert.Empty(Video.Overrides.All());
        Assert.Equal(OverrideMutationKind.Attach, Video.Overrides.Attach(A, @"C:\v\a.mp4", "k", 2));
        Assert.True(Video.Overrides.Has(A));
    }

    [Fact]
    public void Quarantine_skips_the_file_until_a_replace_re_arms_it()
    {
        Video.Overrides.Attach(A, @"C:\v\a.mp4", "k", 1);
        Video.Overrides.Quarantined(A, "k");
        Assert.Equal(OverrideTier.Quarantined, Video.Overrides.Decide(A).Tier);
        Assert.True(Video.Overrides.Has(A));                         // still "the user wants video here"

        Video.Overrides.Attach(A, @"C:\v\a.mp4", "k", 2);            // re-picking the same path IS the repair gesture
        Assert.Equal(OverrideTier.UseOverride, Video.Overrides.Decide(A).Tier);
    }

    [Fact]
    public void A_broken_link_is_announced_once_per_session_per_uri()
    {
        Video.Overrides.Attach(A, @"C:\v\gone.mp4", "k", 1);
        Video.Overrides.FileExists = static _ => false;
        int warnings = 0;
        void OnBroken(string _) => warnings++;
        Video.Overrides.BrokenLink += OnBroken;
        try
        {
            Assert.Equal(OverrideTier.Broken, Video.Overrides.Decide(A).Tier);
            Assert.Equal(OverrideTier.Broken, Video.Overrides.Decide(A).Tier);
            Assert.Equal(1, warnings);
        }
        finally { Video.Overrides.BrokenLink -= OnBroken; }
    }

    [Fact]
    public void Every_mutation_is_announced_with_its_kind_and_bumps_the_epoch()
    {
        var seen = new List<OverrideMutationKind>();
        void OnChanged(string uri, OverrideMutationKind kind) { if (uri == A) seen.Add(kind); }
        Video.Overrides.Changed += OnChanged;
        try
        {
            int before = Video.Overrides.Epoch.Peek();
            Video.Overrides.Attach(A, @"C:\v\a.mp4", "k", 1);
            Video.Overrides.Attach(A, @"C:\v\b.mp4", "k2", 2);
            Video.Overrides.Remove(A);
            Assert.Equal(new[] { OverrideMutationKind.Attach, OverrideMutationKind.Replace, OverrideMutationKind.Remove }, seen);
            Assert.True(Video.Overrides.Epoch.Peek() >= before + 3);
        }
        finally { Video.Overrides.Changed -= OnChanged; }
    }

    [Fact]
    public void The_menu_is_uri_keyed_so_an_episode_gets_the_same_submenu_as_a_track()
    {
        Video.Overrides.Attach("spotify:episode:e1", @"C:\v\e.mp4", "k", 1);
        Assert.Equal(MenuItems.Attach, MenuFor(A));
        Assert.NotEqual(MenuItems.None, MenuFor("spotify:episode:e1") & MenuItems.Replace);

        static MenuItems MenuFor(string uri)
            => OverrideUx.MenuFor(true, uri, Video.Overrides.Present, Video.Overrides.Has(uri), Video.Overrides.Decide(uri).Tier);
    }

    [Fact]
    public void With_no_store_attached_the_feature_is_unreachable()
    {
        Video.Overrides.Attach(null);
        Assert.False(Video.Overrides.Present);
        Assert.False(Video.Overrides.Has(A));
        Assert.Equal(MenuItems.None, OverrideUx.MenuFor(true, A, Video.Overrides.Present, false, OverrideTier.None));
    }

    [Fact]
    public void The_roster_builds_from_the_warm_view_newest_first()
    {
        Video.Overrides.Attach(A, @"C:\v\alpha.mp4", "k1", 1);
        Video.Overrides.Attach("spotify:track:b", @"C:\v\beta.mp4", "k2", 2);
        Video.Overrides.Attach("spotify:track:c", @"C:\v\gamma.mp4", "k3", 3);

        var roster = OverrideUx.BuildRoster(Video.Overrides.All(), Video.Overrides.DecideRecord, Video.Overrides.DirectoryExists);
        Assert.Equal(new[] { "spotify:track:c", "spotify:track:b" }, OverrideUx.RecentlyAdded(roster, 2).ConvertAll(r => r.Uri));
        Assert.Equal(ManagerSection.Results, OverrideUx.RootSection(roster.Count, "beta", OverrideUx.Search(roster, "beta").Count));
        Assert.Equal("spotify:track:b", Assert.Single(OverrideUx.Search(roster, "beta.mp4")).Uri);
    }

    // ── the Video ▸ verbs' shared enablement gate (Track.Menu.Video.cs) ─────────────────────────────────────────────
    //
    // The five verbs' IsEnabled predicates are all `Track.OffersVideoVerb`, which is `OverrideUx.MenuFor` over the live
    // roster — so a row `VideoItem` builds and the verb behind it can never disagree. These facts pin that agreement in
    // the three states the submenu actually has; no window, no action table, no entity store.

    [Fact]
    public void With_nothing_attached_the_submenu_offers_only_attach()
    {
        Assert.True(Track.OffersVideoVerb(MenuItems.Attach, A));
        Assert.False(Track.OffersVideoVerb(MenuItems.Replace, A));
        Assert.False(Track.OffersVideoVerb(MenuItems.Locate, A));
        Assert.False(Track.OffersVideoVerb(MenuItems.ShowInExplorer, A));
        Assert.False(Track.OffersVideoVerb(MenuItems.Remove, A));
    }

    [Fact]
    public void With_a_healthy_attachment_the_submenu_offers_replace_reveal_and_remove_but_never_attach()
    {
        Video.Overrides.Attach(A, @"C:\v\a.mp4", "k", 1);
        Assert.Equal(OverrideTier.UseOverride, Video.Overrides.Decide(A).Tier);

        Assert.False(Track.OffersVideoVerb(MenuItems.Attach, A));      // the uri is the primary key: attach IS replace
        Assert.True(Track.OffersVideoVerb(MenuItems.Replace, A));
        Assert.True(Track.OffersVideoVerb(MenuItems.ShowInExplorer, A));
        Assert.True(Track.OffersVideoVerb(MenuItems.Remove, A));
        Assert.False(Track.OffersVideoVerb(MenuItems.Locate, A));      // nothing to repair
    }

    [Fact]
    public void With_a_broken_link_the_submenu_offers_locate_and_never_reveal()
    {
        Video.Overrides.Attach(A, @"C:\v\gone.mp4", "k", 1);
        Video.Overrides.FileExists = static _ => false;
        Assert.Equal(OverrideTier.Broken, Video.Overrides.Decide(A).Tier);

        Assert.True(Track.OffersVideoVerb(MenuItems.Locate, A));
        Assert.True(Track.OffersVideoVerb(MenuItems.Replace, A));
        Assert.True(Track.OffersVideoVerb(MenuItems.Remove, A));
        Assert.False(Track.OffersVideoVerb(MenuItems.ShowInExplorer, A));   // revealing a file that is gone is a lie
        Assert.False(Track.OffersVideoVerb(MenuItems.Attach, A));
    }

    [Fact]
    public void Without_a_playable_uri_or_a_roster_no_video_verb_is_offered()
    {
        Assert.False(Track.OffersVideoVerb(MenuItems.Attach, null));
        Assert.False(Track.OffersVideoVerb(MenuItems.Attach, ""));

        Video.Overrides.Attach(null);                                  // the feature's kill switch (Dispose restores it)
        Assert.False(Track.OffersVideoVerb(MenuItems.Attach, A));
        Assert.False(Track.OffersVideoVerb(MenuItems.Remove, A));
    }
}

// ── the Video ▸ verbs are actually REGISTERED ────────────────────────────────────────────────────────────────────────

/// <summary>The defect this pins: the submenu shipped in `Track.Menu.cs` while the five verbs behind it did not, so
/// `Actions.Menu.Row` returned null for every row and `VideoItem` dropped the submenu entirely. In the Entities
/// collection because it calls the same `Track.InstallActions()` `BootOrderingTests` does, and `AppActions` is a
/// process-wide table.</summary>
[Collection(EntitiesCollection.Name)]
public class VideoMenuVerbRegistrationTests
{
    [Fact]
    public void Install_registers_the_five_video_verbs_the_submenu_asks_for()
    {
        Track.InstallActions();

        Assert.NotNull(AppActions.Find(ActionId.AttachVideo));
        Assert.NotNull(AppActions.Find(ActionId.ReplaceVideo));
        Assert.NotNull(AppActions.Find(ActionId.LocateVideo));
        Assert.NotNull(AppActions.Find(ActionId.ShowVideoInExplorer));
        Assert.NotNull(AppActions.Find(ActionId.RemoveVideo));

        // Detach is the one destructive row, and it is the one that carries the flag (it sits behind the separator).
        Assert.True(AppActions.Find(ActionId.RemoveVideo)!.Destructive);
        Assert.False(AppActions.Find(ActionId.AttachVideo)!.Destructive);

        // Semantic icon keys, never glyphs — the pair that must differ so a submenu reads at a glance.
        Assert.Equal(ActionIcons.Video, AppActions.Find(ActionId.AttachVideo)!.IconKey);
        Assert.Equal(ActionIcons.Replace, AppActions.Find(ActionId.ReplaceVideo)!.IconKey);
        Assert.Equal(ActionIcons.Locate, AppActions.Find(ActionId.LocateVideo)!.IconKey);
        Assert.Equal(ActionIcons.RevealFolder, AppActions.Find(ActionId.ShowVideoInExplorer)!.IconKey);
        Assert.Equal(ActionIcons.Remove, AppActions.Find(ActionId.RemoveVideo)!.IconKey);

        // None of the five is a toggle row.
        Assert.False(AppActions.Find(ActionId.AttachVideo)!.IsToggle);
        Assert.False(AppActions.Find(ActionId.RemoveVideo)!.IsToggle);
    }
}

public class VideoMainWindowHoleTests
{
    [Fact]
    public void Docked_pip_and_fullscreen_share_the_main_window_hole()
    {
        Assert.True(MainWindowHole.Owns(SurfacePlacement.Docked));
        Assert.True(MainWindowHole.Owns(SurfacePlacement.Floating));
        Assert.True(MainWindowHole.Owns(SurfacePlacement.Fullscreen));
        Assert.False(MainWindowHole.Owns(SurfacePlacement.Detached));
        Assert.False(MainWindowHole.Owns(SurfacePlacement.None));
    }

    [Fact]
    public void Engine_transport_stays_off_on_the_shared_hole()
        => Assert.False(MainWindowHole.EngineTransportEnabled);

    // F169: the stay-mounted presenter's FollowRect (engine, resolved after layout and the animation tick) answers the docked
    // reservation node only while Docked; the mini player and fullscreen keep their own bound geometry. If this rule widened,
    // the PiP's remembered rect or fullscreen's viewport size would be overwritten by a reservation that is not in the tree.
    [Theory]
    [InlineData(SurfacePlacement.Docked, true)]
    [InlineData(SurfacePlacement.Floating, false)]
    [InlineData(SurfacePlacement.Fullscreen, false)]
    [InlineData(SurfacePlacement.Detached, false)]
    [InlineData(SurfacePlacement.None, false)]
    public void Only_a_docked_presenter_follows_the_rail_reservation(SurfacePlacement placement, bool follows)
        => Assert.Equal(follows, MainWindowHole.FollowsReservation(placement));

    [Fact]
    public void A_docked_resolve_is_what_the_presenter_follows()
    {
        // The overlay's FollowRect reads the RESOLVED placement (the derived truth), exactly as its bound geometry does.
        Assert.True(MainWindowHole.FollowsReservation(PlacementCore.Resolve(
            PlacementCore.OpenAt(Vid.Off, SurfacePlacement.Docked))));
        Assert.False(MainWindowHole.FollowsReservation(PlacementCore.Resolve(
            PlacementCore.OpenAt(Vid.Off, SurfacePlacement.Floating))));
    }

    // F168: the immersive stage covers a docked video. Covering must HIDE the presenter, never unmount it: the same slot
    // shows again on uncover (the header of Video.UI.cs promises ONE stay-mounted presenter across Docked, PiP and fullscreen).
    // F132 moved the session's state pump onto the player, so this no longer keeps the playhead alive; it keeps the slot.
    [Fact]
    public void Immersive_cover_keeps_the_docked_presenter_mounted_and_hides_it()
    {
        Assert.True(MainWindowHole.Mounted(SurfacePlacement.Docked, popOutPresented: false));
        Assert.False(MainWindowHole.Hidden(SurfacePlacement.Docked, immersive: false));
        Assert.True(MainWindowHole.Hidden(SurfacePlacement.Docked, immersive: true));
    }

    [Theory]
    [InlineData(SurfacePlacement.Floating)]
    [InlineData(SurfacePlacement.Fullscreen)]
    public void Only_a_docked_video_is_covered_by_the_immersive_stage(SurfacePlacement placement)
    {
        Assert.True(MainWindowHole.Mounted(placement, popOutPresented: false));
        Assert.False(MainWindowHole.Hidden(placement, immersive: true));   // PiP floats above it, fullscreen replaces it
    }

    // F179: the header promises ONE stay-mounted presenter for Docked, PiP and fullscreen. The pure rule is pinned for every
    // reachable combination, so a change that unmounts on a placement hop or on the immersive cover (the shape of the F168
    // regression) fails here, not on a user's screen. The element and slot identity themselves are the PipSurface's `mounted`
    // gate (`MainWindowHole.Mounted`), which is the only thing that returns an empty BoxEl. The same walk is pinned through a
    // real headless AppHost by the engine's `gate.video.presenter-stays-mounted` (FluentGpu.VerticalSlice, ControlsSuite.StayMountedPresenter):
    // one MediaPlayerElement instance, one VideoSurface slot and a pump in every step, across the placements, the cover and the hand-off.
    [Theory]
    [InlineData(SurfacePlacement.Docked, false)]
    [InlineData(SurfacePlacement.Docked, true)]
    [InlineData(SurfacePlacement.Floating, false)]
    [InlineData(SurfacePlacement.Floating, true)]
    [InlineData(SurfacePlacement.Fullscreen, false)]
    [InlineData(SurfacePlacement.Fullscreen, true)]
    public void The_main_window_presenter_stays_mounted_for_docked_pip_and_fullscreen_whatever_the_immersive_cover(
        SurfacePlacement placement, bool immersive)
    {
        Assert.True(MainWindowHole.Owns(placement));
        Assert.True(MainWindowHole.Mounted(placement, popOutPresented: false));
        Assert.True(MainWindowHole.Mounted(placement, popOutPresented: true));   // a stale pop-out report never unmounts a main-window placement
        // The cover decides visibility only, and only for the docked video (PiP floats above it, fullscreen replaces it).
        Assert.Equal(placement == SurfacePlacement.Docked && immersive, MainWindowHole.Hidden(placement, immersive));
    }

    [Fact]
    public void Hopping_between_the_three_main_window_placements_never_passes_through_an_unmounted_state()
    {
        var hops = new[] { SurfacePlacement.Docked, SurfacePlacement.Floating, SurfacePlacement.Fullscreen, SurfacePlacement.Floating, SurfacePlacement.Docked };
        foreach (bool immersive in new[] { false, true })
            foreach (SurfacePlacement p in hops)
                Assert.True(MainWindowHole.Mounted(p, popOutPresented: false), $"{p} immersive={immersive}");
    }

    [Fact]
    public void Nothing_is_mounted_when_video_is_off()
    {
        Assert.False(MainWindowHole.Mounted(SurfacePlacement.None, popOutPresented: false));
        Assert.False(MainWindowHole.Mounted(SurfacePlacement.None, popOutPresented: true));
    }

    // F180: make-before-break. Moving to the pop-out keeps the main presenter mounted (hidden) until the pop-out has
    // presented, then releases it, so there is never a moment with no presenter, and never two VISIBLE ones.
    [Fact]
    public void Moving_to_the_pop_out_is_make_before_break()
    {
        Assert.True(MainWindowHole.Mounted(SurfacePlacement.Detached, popOutPresented: false));
        Assert.True(MainWindowHole.Hidden(SurfacePlacement.Detached, immersive: false));
        Assert.False(MainWindowHole.Mounted(SurfacePlacement.Detached, popOutPresented: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pop_out_that_is_up_is_never_shown_alongside_a_visible_main_presenter(bool immersive)
    {
        foreach (bool presented in new[] { false, true })
            Assert.False(MainWindowHole.Mounted(SurfacePlacement.Detached, presented)
                && !MainWindowHole.Hidden(SurfacePlacement.Detached, immersive));
    }

    /// <summary>The transition walk the plan pins: docked, PiP, fullscreen, immersive and detached each keep exactly one
    /// VISIBLE presenter (zero when off), and the only moment two presenters are MOUNTED is the Detached overlap, where
    /// the main one is hidden.</summary>
    [Fact]
    public void Every_placement_transition_keeps_one_visible_presenter()
    {
        var steps = new (SurfacePlacement P, bool Immersive, bool PopOutPresented, int Visible, int Mounted)[]
        {
            (SurfacePlacement.None,       false, false, 0, 0),
            (SurfacePlacement.Docked,     false, false, 1, 1),
            (SurfacePlacement.Docked,     true,  false, 0, 1),   // immersive over docked: hidden, still the pump
            (SurfacePlacement.Docked,     false, false, 1, 1),   // uncovered: the same presenter shows again
            (SurfacePlacement.Floating,   false, false, 1, 1),
            (SurfacePlacement.Fullscreen, false, false, 1, 1),
            (SurfacePlacement.Detached,   false, false, 1, 2),   // overlap: hidden main + the opening pop-out
            (SurfacePlacement.Detached,   true,  false, 1, 2),
            (SurfacePlacement.Detached,   false, true,  1, 1),   // released: only the pop-out
            (SurfacePlacement.Floating,   false, false, 1, 1),
        };
        foreach (var s in steps)
        {
            bool mainMounted = MainWindowHole.Mounted(s.P, s.PopOutPresented);
            bool mainVisible = mainMounted && !MainWindowHole.Hidden(s.P, s.Immersive);
            bool popOut = s.P == SurfacePlacement.Detached;
            Assert.Equal(s.Visible, (mainVisible ? 1 : 0) + (popOut ? 1 : 0));
            Assert.Equal(s.Mounted, (mainMounted ? 1 : 0) + (popOut ? 1 : 0));
        }
    }

    [Fact]
    public void On_media_transport_is_the_shared_player_on_every_visible_placement()
    {
        Assert.Equal("onmedia.transport", OnMedia.TransportId);
        Assert.Equal(OnMedia.TransportId, OnMediaTransport.Id);
        Assert.True(OnMediaTransport.UsesSharedPlayer(SurfacePlacement.Docked));
        Assert.True(OnMediaTransport.UsesSharedPlayer(SurfacePlacement.Floating));
        Assert.True(OnMediaTransport.UsesSharedPlayer(SurfacePlacement.Detached));
        Assert.True(OnMediaTransport.UsesSharedPlayer(SurfacePlacement.Fullscreen));
        Assert.False(OnMediaTransport.UsesSharedPlayer(SurfacePlacement.None));
    }
}

// F179: the always-on warning for a playing session whose control plane has gone quiet. The player pumps its own state (F132);
// the host watches the heartbeat (`MediaPlayer.StatePumpAgeMs`) and `HostRules.StatePumpStalled` decides when it is overdue.
public class VideoStatePumpHeartbeatTests
{
    [Theory]
    [InlineData(PlaybackState.Playing, 0, false)]
    [InlineData(PlaybackState.Playing, 1_200, false)]     // one slow position tick is not a stall
    [InlineData(PlaybackState.Playing, 3_000, false)]     // the budget itself is still healthy
    [InlineData(PlaybackState.Playing, 3_001, true)]
    [InlineData(PlaybackState.Playing, 60_000, true)]
    [InlineData(PlaybackState.Paused, 60_000, false)]     // a paused session is quiet by design
    [InlineData(PlaybackState.Opening, 60_000, false)]
    [InlineData(PlaybackState.Buffering, 60_000, false)]
    [InlineData(PlaybackState.Ended, 60_000, false)]
    [InlineData(PlaybackState.Failed, 60_000, false)]
    [InlineData(PlaybackState.Idle, 60_000, false)]
    public void A_clear_session_is_stalled_only_while_playing_past_the_position_tick_budget(PlaybackState state, long idleMs, bool stalled)
        => Assert.Equal(stalled, PlayHostRules.StatePumpStalled(state, idleMs, protectedSession: false));

    [Theory]
    [InlineData(PlaybackState.Playing, 3_001, false)]     // protected samples are not pump triggers: segment-paced events only
    [InlineData(PlaybackState.Playing, 8_000, false)]
    [InlineData(PlaybackState.Playing, 15_000, false)]
    [InlineData(PlaybackState.Playing, 15_001, true)]
    [InlineData(PlaybackState.Paused, 60_000, false)]
    public void A_protected_session_gets_a_budget_of_several_segments(PlaybackState state, long idleMs, bool stalled)
        => Assert.Equal(stalled, PlayHostRules.StatePumpStalled(state, idleMs, protectedSession: true));

    [Fact]
    public void The_protected_budget_is_wider_than_the_clear_one()
        => Assert.True(PlayHostRules.ProtectedStatePumpStallMs > PlayHostRules.StatePumpStallMs);
}
