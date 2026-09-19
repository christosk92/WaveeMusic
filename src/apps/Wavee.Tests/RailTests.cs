// ── Wavee.Tests/RailTests.cs — the right rail's engine-free decisions ──────────────────────────────────────────────
//
// Ported suites, fact for fact: `RailVideoCouplingTests` (9), `NpvPlayerCatalogTests` (13), `NpvPlayerPrefsTests` (13).
//
// Two input types changed with the port, and nothing else: the rail's mode vocabulary is owner I's `Shell.RailMode`
// (0.2.9's `Details` is `NowPlaying`), and the player prefs are an ADAPTER over owner L's `Prefs.NpvPlayer` — so the
// prefs facts drive the real store through `Platform.UseSettings` and live in the platform collection, which disables
// parallelism for every test that swaps that process-wide backing store.

using Wavee;
using Xunit;

using V = Wavee.Video;
using RailMode = Wavee.Shell.RailMode;

namespace Wavee.Tests;

public class RailVideoCouplingTests
{
    static readonly V.PlacementPolicy Policy = V.PlacementPolicy.Music;
    const V.PlacementSet All = V.PlacementSet.Docked | V.PlacementSet.Floating | V.PlacementSet.Detached | V.PlacementSet.Fullscreen;

    static V.PlacementState Off(V.PlacementSet available = All)
        => V.PlacementState.Initial(Policy) with { Available = available };

    static V.PlacementState At(V.SurfacePlacement p, V.PlacementSet available = All)
        => V.PlacementCore.OpenAt(Off(available), p);

    [Fact]
    public void Docking_a_closed_rail_opens_video_mode_and_an_open_rail_is_left_alone()
    {
        Assert.Equal(RailMode.Video, Rail.VideoCoupling.ModeOnDock(false, RailMode.Lyrics, V.DockedHost.Rail));
        Assert.Null(Rail.VideoCoupling.ModeOnDock(true, RailMode.Lyrics, V.DockedHost.Rail));
        Assert.Null(Rail.VideoCoupling.ModeOnDock(true, RailMode.Video, V.DockedHost.Rail));
    }

    [Fact]
    public void Docking_never_opens_video_mode_when_the_page_stage_will_host_it()
    {
        Assert.Null(Rail.VideoCoupling.ModeOnDock(false, RailMode.Lyrics, V.DockedHost.PageStage));
        Assert.Null(Rail.VideoCoupling.ModeOnDock(false, RailMode.Queue, V.DockedHost.PageStage));
        Assert.Null(Rail.VideoCoupling.ModeOnDock(true, RailMode.Lyrics, V.DockedHost.PageStage));
    }

    [Fact]
    public void Closing_the_rail_is_inert_for_every_placement_but_docked()
    {
        Assert.Equal(V.SurfacePlacement.None, Rail.VideoCoupling.OnRailClosed(At(V.SurfacePlacement.Floating), V.DockedHost.Rail));
        Assert.Equal(V.SurfacePlacement.None, Rail.VideoCoupling.OnRailClosed(At(V.SurfacePlacement.Detached), V.DockedHost.Rail));
        Assert.Equal(V.SurfacePlacement.None, Rail.VideoCoupling.OnRailClosed(Off(), V.DockedHost.Rail));
    }

    [Fact]
    public void Closing_the_rail_while_docked_demotes_to_the_mini_player()
        => Assert.Equal(V.SurfacePlacement.Floating,
            Rail.VideoCoupling.OnRailClosed(At(V.SurfacePlacement.Docked), V.DockedHost.Rail));

    [Fact]
    public void Closing_the_rail_while_the_page_stage_hosts_is_inert()
    {
        Assert.Equal(V.SurfacePlacement.None, Rail.VideoCoupling.OnRailClosed(At(V.SurfacePlacement.Docked), V.DockedHost.PageStage));
        Assert.Equal(V.SurfacePlacement.None, Rail.VideoCoupling.OnRailClosed(At(V.SurfacePlacement.Floating), V.DockedHost.PageStage));
    }

    [Fact]
    public void Reopening_the_rail_re_docks_only_what_the_rail_took_away()
    {
        var demoted = V.PlacementCore.Demote(At(V.SurfacePlacement.Docked), V.SurfacePlacement.Floating);
        Assert.Equal(V.SurfacePlacement.Floating, demoted.Requested);
        Assert.True(Rail.VideoCoupling.ReDockOnRailOpen(demoted));

        Assert.False(Rail.VideoCoupling.ReDockOnRailOpen(At(V.SurfacePlacement.Floating)));             // a deliberate choice
        Assert.False(Rail.VideoCoupling.ReDockOnRailOpen(Off() with { Preferred = V.SurfacePlacement.Docked }));
        Assert.False(Rail.VideoCoupling.ReDockOnRailOpen(At(V.SurfacePlacement.Docked)));               // it never left
        Assert.False(Rail.VideoCoupling.ReDockOnRailOpen(V.PlacementCore.EnterFullscreen(At(V.SurfacePlacement.Docked))));
    }

    [Fact]
    public void Only_the_video_first_body_closes_when_the_video_leaves()
    {
        Assert.True(Rail.VideoCoupling.CloseRailOnVideoLeft(RailMode.Video, true, V.DockedHost.Rail));
        Assert.False(Rail.VideoCoupling.CloseRailOnVideoLeft(RailMode.Video, false, V.DockedHost.Rail));
        foreach (var mode in Enum.GetValues<RailMode>())
            if (mode != RailMode.Video)
                Assert.False(Rail.VideoCoupling.CloseRailOnVideoLeft(mode, true, V.DockedHost.Rail));
    }

    [Fact]
    public void The_rail_never_closes_on_behalf_of_a_card_the_page_stage_was_holding()
    {
        foreach (var mode in Enum.GetValues<RailMode>())
            Assert.False(Rail.VideoCoupling.CloseRailOnVideoLeft(mode, true, V.DockedHost.PageStage));
    }

    [Fact]
    public void The_body_substitutes_queue_for_the_two_card_hosting_bodies_only_and_is_identity_otherwise()
    {
        Assert.Equal(RailMode.Queue, Rail.VideoCoupling.BodyModeFor(RailMode.Video, stageHostsVideo: true));
        Assert.Equal(RailMode.Queue, Rail.VideoCoupling.BodyModeFor(RailMode.NowPlaying, stageHostsVideo: true));
        Assert.Equal(RailMode.Lyrics, Rail.VideoCoupling.BodyModeFor(RailMode.Lyrics, stageHostsVideo: true));
        Assert.Equal(RailMode.Friends, Rail.VideoCoupling.BodyModeFor(RailMode.Friends, stageHostsVideo: true));
        foreach (var mode in Enum.GetValues<RailMode>())
            Assert.Equal(mode, Rail.VideoCoupling.BodyModeFor(mode, stageHostsVideo: false));
    }
}

public class NpvPlayerCatalogTests
{
    static readonly Rail.PlayerCatalog.Preset[] Presets = Rail.PlayerCatalog.Presets;

    [Fact]
    public void Ids_are_contiguous_and_equal_their_index()
    {
        for (int i = 0; i < Presets.Length; i++) Assert.Equal(i, Presets[i].Id);
    }

    [Fact]
    public void Slugs_are_unique_lowercase_ascii_and_PINNED()
    {
        string[] expected = ["record", "cassette", "reel", "cd", "turntable", "ipod", "winamp", "vu", "zune", "wmp", "canvas", "picture"];
        Assert.Equal(expected.Length, Presets.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Presets.Length; i++)
        {
            Assert.Equal(expected[i], Presets[i].Slug);
            Assert.True(seen.Add(Presets[i].Slug));
            foreach (char c in Presets[i].Slug) Assert.InRange(c, 'a', 'z');
        }
    }

    [Fact]
    public void Exactly_twelve_rows_four_per_group()
    {
        Assert.Equal(12, Presets.Length);
        foreach (var group in Enum.GetValues<Rail.PlayerGroup>())
        {
            int n = 0;
            foreach (var p in Presets) if (p.Group == group) n++;
            Assert.Equal(Rail.PlayerCatalog.PerGroup, n);
        }
    }

    [Fact]
    public void Every_preset_has_an_option_and_every_option_two_unique_choices()
    {
        foreach (var p in Presets)
        {
            Assert.True(p.Options.Length >= 1, $"{p.Slug} has no options");
            foreach (var o in p.Options)
            {
                Assert.True(o.Choices.Length >= 2, $"{p.Slug}.{o.Slug} has fewer than 2 choices");
                var slugs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in o.Choices) Assert.True(slugs.Add(c.Slug));
            }
        }
    }

    [Fact]
    public void Every_label_key_starts_with_player_dot()
    {
        foreach (var p in Presets)
        {
            Assert.StartsWith("player.", p.LabelKey, StringComparison.Ordinal);
            Assert.StartsWith("player.", p.ShortLabelKey, StringComparison.Ordinal);
            foreach (var o in p.Options)
            {
                Assert.StartsWith("player.", o.LabelKey, StringComparison.Ordinal);
                foreach (var c in o.Choices) Assert.StartsWith("player.", c.LabelKey, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Only_the_album_colour_swatch_derives_from_the_cover()
    {
        foreach (var p in Presets)
            foreach (var o in p.Options)
                if (o.Kind == Rail.OptionKind.Swatch)
                    foreach (var c in o.Choices)
                        Assert.True(c.Swatch != Rail.PlayerCatalog.FromCover || c.Slug == "album",
                            $"{p.Slug}.{o.Slug}.{c.Slug} derives from the cover");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(12)]
    public void An_unknown_id_falls_back_to_record(int id)
        => Assert.Equal(Rail.PlayerCatalog.Record, Rail.PlayerCatalog.ById(id).Id);

    [Fact]
    public void The_default_preset_is_record() => Assert.Equal(Rail.PlayerCatalog.Record, Rail.PlayerCatalog.DefaultPresetId);

    [Fact]
    public void IsPresetId_is_true_only_for_real_ids()
    {
        for (int i = 0; i < Presets.Length; i++) Assert.True(Rail.PlayerCatalog.IsPresetId(i));
        Assert.False(Rail.PlayerCatalog.IsPresetId(-1));
        Assert.False(Rail.PlayerCatalog.IsPresetId(Presets.Length));
    }

    [Fact]
    public void Group_fills_only_that_groups_presets()
    {
        var dest = new Rail.PlayerCatalog.Preset[Presets.Length];
        int n = Rail.PlayerCatalog.Group(Rail.PlayerGroup.Devices, dest);
        Assert.Equal(Rail.PlayerCatalog.PerGroup, n);
        for (int i = 0; i < n; i++) Assert.Equal(Rail.PlayerGroup.Devices, dest[i].Group);
    }

    [Fact]
    public void Option_finds_by_slug_or_falls_back_to_the_first()
    {
        var record = Rail.PlayerCatalog.ById(Rail.PlayerCatalog.Record);
        Assert.Equal("rpm", Rail.PlayerCatalog.Option(record, "rpm").Slug);
        Assert.Equal(record.Options[0].Slug, Rail.PlayerCatalog.Option(record, "does-not-exist").Slug);
    }

    [Fact]
    public void Record_and_turntable_share_option_rows_but_are_distinct_presets()
    {
        var record = Rail.PlayerCatalog.ById(Rail.PlayerCatalog.Record);
        var turntable = Rail.PlayerCatalog.ById(Rail.PlayerCatalog.Turntable);
        Assert.Equal(record.Options.Length, turntable.Options.Length);
        for (int i = 0; i < record.Options.Length; i++) Assert.Equal(record.Options[i].Slug, turntable.Options[i].Slug);
        Assert.NotEqual(record.Slug, turntable.Slug);
    }

    [Fact]
    public void Every_preset_maps_to_a_deck_model()
    {
        // The catalog and the physics dispatch must never disagree: a preset with no model would be a silent blank.
        var seed = new Deck.Input(0, false, Deck.TransportPhase.Idle, false, false, false, false, false,
            Deck.Boundary.None, null, null, 0, 0, false, 33.33f, false);
        foreach (var p in Presets)
            Assert.NotNull(Deck.Models.Create(p, static () => null, seed));
    }
}

[Collection(PlatformCollection.Name)]
public sealed class NpvPlayerPrefsTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public NpvPlayerPrefsTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    static Rail.PlayerCatalog.Preset P(int id) => Rail.PlayerCatalog.ById(id);

    [Theory]
    [InlineData(-1, Rail.PlayerPrefs.Cover)]
    [InlineData(0, Rail.PlayerPrefs.Cover)]
    [InlineData(1, Rail.PlayerPrefs.Player)]
    [InlineData(2, Rail.PlayerPrefs.Cover)]
    [InlineData(99, Rail.PlayerPrefs.Cover)]
    public void Anything_not_player_is_the_cover(int stray, int expected)
        => Assert.Equal(expected, Rail.PlayerPrefs.ClampPresentation(stray));

    [Fact]
    public void A_stray_style_falls_back_to_record()
    {
        Assert.Equal(Rail.PlayerCatalog.Record, Rail.PlayerPrefs.ClampStyle(-1));
        Assert.Equal(Rail.PlayerCatalog.Record, Rail.PlayerPrefs.ClampStyle(999));
        Assert.Equal(Rail.PlayerCatalog.Turntable, Rail.PlayerPrefs.ClampStyle(Rail.PlayerCatalog.Turntable));
    }

    [Fact]
    public void An_out_of_range_choice_falls_back_to_zero()
    {
        var rpm = Rail.PlayerCatalog.Option(P(Rail.PlayerCatalog.Record), "rpm");
        Assert.Equal(0, Rail.PlayerPrefs.ClampChoice(rpm, -1));
        Assert.Equal(0, Rail.PlayerPrefs.ClampChoice(rpm, 999));
        Assert.Equal(1, Rail.PlayerPrefs.ClampChoice(rpm, 1));
    }

    [Fact]
    public void An_empty_store_reads_as_cover_and_record()
    {
        Assert.Equal(Rail.PlayerPrefs.Cover, Rail.PlayerPrefs.Presentation());
        Assert.Equal(Rail.PlayerCatalog.Record, Rail.PlayerPrefs.Style());
    }

    [Fact]
    public void A_write_round_trips_and_bumps_the_ONE_epoch_once()
    {
        int before = Prefs.NpvPlayer.Epoch.Peek();
        Rail.PlayerPrefs.SetPresentation(Rail.PlayerPrefs.Player, Rail.NpvDiagnostics.SourceHeader);
        Assert.Equal(Rail.PlayerPrefs.Player, Rail.PlayerPrefs.Presentation());
        Assert.Equal(before + 1, Prefs.NpvPlayer.Epoch.Peek());
        Assert.Same(Prefs.NpvPlayer.Epoch, Rail.PlayerPrefs.Epoch);   // an adapter, never a second authority
        Assert.Same(Prefs.NpvPlayer.StyleFlyoutOpen, Rail.PlayerPrefs.StyleFlyoutOpen);
    }

    [Fact]
    public void Toggle_alternates()
    {
        Rail.PlayerPrefs.TogglePresentation("test");
        Assert.Equal(Rail.PlayerPrefs.Player, Rail.PlayerPrefs.Presentation());
        Rail.PlayerPrefs.TogglePresentation("test");
        Assert.Equal(Rail.PlayerPrefs.Cover, Rail.PlayerPrefs.Presentation());
    }

    [Fact]
    public void Picking_a_style_also_shows_it()
    {
        Rail.PlayerPrefs.SetStyle(Rail.PlayerCatalog.Turntable, Rail.NpvDiagnostics.SourceFlyout);
        Assert.Equal(Rail.PlayerCatalog.Turntable, Rail.PlayerPrefs.Style());
        Assert.Equal(Rail.PlayerPrefs.Player, Rail.PlayerPrefs.Presentation());
        Assert.Equal(Rail.PlayerCatalog.Turntable, Rail.PlayerPrefs.CurrentPreset().Id);
    }

    [Fact]
    public void Option_keys_follow_the_persisted_convention()
        => Assert.Equal("npv.player.turntable.finish", Platform.Keys.NpvOption("turntable", "finish").Name);

    [Fact]
    public void Record_and_turntable_keep_separate_option_keys()
    {
        var record = P(Rail.PlayerCatalog.Record);
        var turntable = P(Rail.PlayerCatalog.Turntable);
        Rail.PlayerPrefs.SetChoice(record, Rail.PlayerCatalog.Option(record, "rpm"), 1, "test");
        Assert.Equal(1, Rail.PlayerPrefs.Choice(record, "rpm"));
        Assert.Equal(0, Rail.PlayerPrefs.Choice(turntable, "rpm"));
        Assert.False(_store.WasWritten(Platform.Keys.NpvOption(turntable.Slug, "rpm")));
    }

    [Fact]
    public void Next_style_wraps_picture_back_to_record()
    {
        Rail.PlayerPrefs.SetStyle(Rail.PlayerCatalog.Picture, "test");
        Rail.PlayerPrefs.NextStyle("test");
        Assert.Equal(Rail.PlayerCatalog.Record, Rail.PlayerPrefs.Style());
    }

    [Fact]
    public void ChoiceSlug_is_what_a_face_branches_on()
    {
        var record = P(Rail.PlayerCatalog.Record);
        Rail.PlayerPrefs.SetChoice(record, Rail.PlayerCatalog.Option(record, "rpm"), 1, "test");
        Assert.Equal("45", Rail.PlayerPrefs.ChoiceSlug(record, "rpm"));
    }

    [Fact]
    public void A_hand_edited_out_of_range_choice_clamps_on_READ()
    {
        var record = P(Rail.PlayerCatalog.Record);
        _store.Set(Platform.Keys.NpvOption(record.Slug, "rpm"), 999);
        Assert.Equal(0, Rail.PlayerPrefs.Choice(record, Rail.PlayerCatalog.Option(record, "rpm")));
        _store.Set(Platform.Keys.NpvPlayerStyle, 77);
        Assert.Equal(Rail.PlayerCatalog.Record, Rail.PlayerPrefs.Style());
    }
}
