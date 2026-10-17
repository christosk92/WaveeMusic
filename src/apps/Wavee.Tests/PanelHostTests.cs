// ── Wavee.Tests/PanelHostTests.cs — the right panel as one core host (themes plan C3, X9/X19) ─────────────────────────
// (Shell/PanelHost.cs.) The REAL host over a PRIVATE set of cells (`PanelCells.Fresh()`), an in-memory size store and a
// recording env: nothing here touches `Shell.Ui`, the settings, the video model or the stage.
//
//   STATE          open, mode, committed width, floats, and (on request) the docked video's height and pin.
//   VERBS          show a mode (never closes), toggle it (the showing mode closes), close, commit a resize ONCE, put the video
//                  somewhere, open the stage.
//   SIZES          what the panel STARTS at is clamped; a committed size is persisted through one service.
//   NEXT UP        at most five rows, the tracks among the first five upcoming rows.

using Wavee.Theming;
using Xunit;

namespace Wavee.Tests;

sealed class RecordingPanelEnv : Shell.IPanelEnv
{
    public bool TrackHasVideo;
    public readonly List<string> Calls = [];
    public Action<string>? LastBegin;

    public bool NowTrackHasVideo() => TrackHasVideo;
    public void OpenVideoAt(Video.SurfacePlacement at, bool hasVideo) => Calls.Add("open:" + at + ":" + hasVideo);
    public void CloseDockedVideo() => Calls.Add("closeVideo");
    public void OpenStage(Action<string>? begin, string cause) { LastBegin = begin; Calls.Add("stage:" + cause); }
    public void DemandNowPlaying(int trackSlot, int artistSlot) => Calls.Add("demand:" + trackSlot + ":" + artistSlot);
    public void SetFriendsActive(bool active) => Calls.Add("friends:" + active);
}

sealed class DictionaryPanelSizeStore : Shell.IPanelSizeStore
{
    public readonly Dictionary<Shell.PanelSize, float> Values = [];
    public readonly List<string> Writes = [];

    public float Get(Shell.PanelSize size) => Values.TryGetValue(size, out float v) ? v : 0f;

    public void Set(Shell.PanelSize size, float value)
    {
        Values[size] = value;
        Writes.Add(size + "=" + value);
    }
}

public class PanelHostTests
{
    static (Shell.PanelHost Host, Shell.PanelCells Cells, DictionaryPanelSizeStore Store, RecordingPanelEnv Env) Make()
    {
        var cells = Shell.PanelCells.Fresh();
        var store = new DictionaryPanelSizeStore();
        var env = new RecordingPanelEnv();
        return (new Shell.PanelHost(cells, new Shell.PanelSizes(store), env), cells, store, env);
    }

    // ── state ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_panel_is_closed_on_lyrics_at_the_default_width_and_fits()
    {
        var (host, _, _, _) = Make();
        var state = host.State();
        Assert.False(state.Open);
        Assert.Equal(Shell.RailMode.Lyrics, state.Mode);
        Assert.Equal(Shell.RailDefaultW, state.Width);
        Assert.False(state.Floats);
    }

    [Fact]
    public void A_panel_that_does_not_fit_inline_floats()
    {
        var (host, cells, _, _) = Make();
        cells.Fits.Value = false;
        Assert.True(host.State().Floats);
    }

    [Fact]
    public void The_docked_video_is_part_of_the_state_only_when_asked_for()
    {
        var (host, cells, _, _) = Make();
        cells.DockedVideoHeight.Value = 300f;
        cells.DockedVideoPinned.Value = true;
        Assert.Equal(default(Shell.DockedVideoState), host.State().DockedVideo);
        Assert.Equal(new Shell.DockedVideoState(300f, true), host.State(withDockedVideo: true).DockedVideo);
    }

    [Fact]
    public void The_shell_cells_are_the_signals_every_reader_binds()
    {
        Assert.Same(Shell.Ui.RailOpen, Shell.PanelCells.Frame.Open);
        Assert.Same(Shell.Ui.Mode, Shell.PanelCells.Frame.Mode);
        Assert.Same(Shell.Ui.RailWidth, Shell.PanelCells.Frame.Width);
        Assert.Same(Shell.Ui.RailDragWidth, Shell.PanelCells.Frame.DragWidth);
        Assert.Same(Shell.Ui.DockedVideoHeight, Shell.PanelCells.Frame.DockedVideoHeight);
        Assert.Same(Shell.Ui.DockedVideoHeightPinned, Shell.PanelCells.Frame.DockedVideoPinned);
        Assert.Same(Shell.Ui.RailBodyHeight, Shell.PanelCells.Frame.BodyHeight);
        Assert.Same(Shell.Ui.RailFits, Shell.PanelCells.Frame.Fits);
    }

    // ── modes ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Setting_a_mode_shows_it_and_opens_the_panel_and_never_closes_it()
    {
        var (host, _, _, _) = Make();
        host.SetMode(Shell.RailMode.Queue);
        Assert.True(host.State().Open);
        Assert.Equal(Shell.RailMode.Queue, host.State().Mode);
        host.SetMode(Shell.RailMode.Queue);                    // asking for the showing mode again keeps it open
        Assert.True(host.State().Open);
        Assert.Equal(Shell.RailMode.Queue, host.State().Mode);
    }

    [Fact]
    public void Toggling_the_showing_mode_closes_the_panel_and_another_mode_switches_in_place()
    {
        var (host, _, _, _) = Make();
        host.Toggle(Shell.RailMode.Lyrics);                    // closed -> open on Lyrics
        Assert.True(host.State().Open);
        host.Toggle(Shell.RailMode.Queue);                     // open on another mode -> switch, stay open
        Assert.True(host.State().Open);
        Assert.Equal(Shell.RailMode.Queue, host.State().Mode);
        host.Toggle(Shell.RailMode.Queue);                     // the showing mode -> close
        Assert.False(host.State().Open);
        Assert.Equal(Shell.RailMode.Queue, host.State().Mode); // the mode is remembered for the next open
    }

    [Fact]
    public void Close_closes_whatever_the_panel_shows()
    {
        var (host, _, _, _) = Make();
        host.SetMode(Shell.RailMode.Video);
        host.Close();
        Assert.False(host.State().Open);
    }

    // ── resizing ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_drag_previews_on_its_own_cell_and_commits_the_width_once_on_release()
    {
        var (host, cells, store, _) = Make();
        cells.DragWidth.Value = 420f;
        Assert.Equal(Shell.RailDefaultW, cells.Width.Peek());  // nothing reaches the layout until the release
        host.ResizePanel();
        Assert.Equal(420f, cells.Width.Peek());
        Assert.Equal(420f, cells.DragWidth.Peek());
        Assert.Equal(new[] { "RailWidth=420" }, store.Writes);
        host.ResizePanel();                                    // the pointer ended where the panel already is
        Assert.Single(store.Writes);
    }

    [Fact]
    public void A_drag_past_the_bounds_commits_the_clamped_width()
    {
        var (host, cells, _, _) = Make();
        cells.DragWidth.Value = 9999f;
        host.ResizePanel();
        Assert.Equal(Shell.RailMaxW, cells.Width.Peek());
        Assert.Equal(Shell.RailMaxW, cells.DragWidth.Peek());
    }

    [Fact]
    public void A_non_finite_preview_commits_nothing()
    {
        var (host, cells, store, _) = Make();
        cells.DragWidth.Value = float.NaN;
        host.ResizePanel();
        Assert.Equal(Shell.RailDefaultW, cells.Width.Peek());
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void Resizing_the_docked_video_clamps_pins_and_persists()
    {
        var (host, cells, store, _) = Make();
        cells.BodyHeight.Value = 400f;
        cells.DockedVideoHeight.Value = 300f;
        host.ResizeDockedVideo();
        Assert.Equal(300f, cells.DockedVideoHeight.Peek());
        Assert.True(cells.DockedVideoPinned.Peek());
        Assert.Equal(new[] { "DockedVideoHeight=300" }, store.Writes);
    }

    [Fact]
    public void The_docked_video_never_grows_past_what_the_body_leaves_nor_below_16_by_9()
    {
        var (host, cells, store, _) = Make();
        cells.BodyHeight.Value = 400f;
        cells.DockedVideoHeight.Value = 700f;
        host.ResizeDockedVideo();
        Assert.Equal(400f, cells.DockedVideoHeight.Peek());

        cells.DockedVideoHeight.Value = 50f;
        host.ResizeDockedVideo();
        Assert.Equal(Shell.DockedVideoNaturalH(Shell.RailDefaultW), cells.DockedVideoHeight.Peek());
        Assert.Equal(cells.DockedVideoHeight.Peek(), store.Values[Shell.PanelSize.DockedVideoHeight]);
    }

    // ── the persisted state ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_sizes_are_seeded_clamped_and_the_preview_starts_at_the_committed_width()
    {
        var (host, cells, store, _) = Make();
        store.Values[Shell.PanelSize.RailWidth] = 9999f;       // stale or hand-edited
        store.Values[Shell.PanelSize.DockedVideoHeight] = 0f;  // never sized: follow the 16:9 floor
        host.SeedSizes();
        Assert.Equal(Shell.RailMaxW, cells.Width.Peek());
        Assert.Equal(Shell.RailMaxW, cells.DragWidth.Peek());
        Assert.Equal(Shell.DockedVideoNaturalH(Shell.RailMaxW), cells.DockedVideoHeight.Peek());
        Assert.Empty(store.Writes);                            // seeding never writes
    }

    [Fact]
    public void A_restored_session_opens_the_panel_on_its_mode_and_a_newer_builds_mode_is_ignored()
    {
        var (host, _, _, _) = Make();
        host.RestoreSession(open: true, railMode: (int)Shell.RailMode.Video);
        Assert.True(host.State().Open);
        Assert.Equal(Shell.RailMode.Video, host.State().Mode);

        var (other, _, _, _) = Make();
        other.RestoreSession(open: true, railMode: 99);
        Assert.False(other.State().Open);
        Assert.Equal(Shell.RailMode.Lyrics, other.State().Mode);
        other.RestoreSession(open: true, railMode: -1);
        Assert.False(other.State().Open);
    }

    // ── what the host reaches outside its cells ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Showing_the_video_restamps_the_availability_this_playable_has()
    {
        var (host, _, _, env) = Make();
        env.TrackHasVideo = true;
        host.ShowVideoAt(Video.SurfacePlacement.Docked);
        env.TrackHasVideo = false;
        host.ShowVideoAt(Video.SurfacePlacement.Fullscreen);
        Assert.Equal(new[] { "open:Docked:True", "open:Fullscreen:False" }, env.Calls);
    }

    [Fact]
    public void Hiding_the_video_and_opening_the_stage_go_through_the_env()
    {
        var (host, _, _, env) = Make();
        Action<string> begin = _ => { };
        host.HideVideo();
        host.OpenStage(begin, "rail-header");
        Assert.Equal(new[] { "closeVideo", "stage:rail-header" }, env.Calls);
        Assert.Same(begin, env.LastBegin);
    }

    [Fact]
    public void The_host_forwards_the_now_playing_demand_and_the_friends_activation()
    {
        var (host, _, _, env) = Make();
        host.DemandNowPlaying(5, 9);
        host.SetFriendsActive(true);
        host.SetFriendsActive(false);
        Assert.Equal(new[] { "demand:5:9", "friends:True", "friends:False" }, env.Calls);
    }
}

public class NextUpWindowTests
{
    static readonly EntityRef TrackRow = new(EntityKind.Track, 1);
    static readonly EntityRef EpisodeRow = new(EntityKind.Episode, 2);

    static int[] Pick(bool has, int start, int length, int capacity, params EntityRef[] queue)
    {
        Span<int> into = stackalloc int[capacity];
        int n = Shell.NextUpWindow.Pick(has, start, length, into, i => queue[i]);
        return into[..n].ToArray();
    }

    [Fact]
    public void The_window_is_at_most_five()
        => Assert.Equal(5, Shell.NextUpWindow.Max);

    [Fact]
    public void Nothing_upcoming_is_an_empty_window()
        => Assert.Empty(Pick(false, 0, 0, Shell.NextUpWindow.Max, TrackRow));

    [Fact]
    public void A_long_queue_shows_the_first_five_upcoming_tracks()
    {
        var queue = Enumerable.Repeat(TrackRow, 12).ToArray();
        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, Pick(true, 3, 9, Shell.NextUpWindow.Max, queue));
    }

    [Fact]
    public void A_short_run_shows_what_there_is()
    {
        var queue = Enumerable.Repeat(TrackRow, 6).ToArray();
        Assert.Equal(new[] { 4, 5 }, Pick(true, 4, 2, Shell.NextUpWindow.Max, queue));
    }

    [Fact]
    public void Episodes_and_empty_refs_are_not_panel_rows_and_the_window_does_not_reach_past_the_first_five_to_make_up_the_count()
    {
        EntityRef[] queue = [TrackRow, EpisodeRow, TrackRow, default, TrackRow, TrackRow, TrackRow];
        Assert.Equal(new[] { 0, 2, 4 }, Pick(true, 0, 7, Shell.NextUpWindow.Max, queue));
    }
}

[Collection(PlatformCollection.Name)]
public sealed class PanelSizesSettingsTests : IDisposable
{
    readonly MemoryAppSettings _store = new();

    public PanelSizesSettingsTests() => Platform.UseSettings(_store);
    public void Dispose() => Platform.UseSettings(null);

    [Fact]
    public void The_shared_sizes_persist_to_the_theme_state_keys_and_read_back_clamped()
    {
        var sizes = Shell.PanelSizes.Shared;
        sizes.SaveWidth(420f);
        sizes.SaveDockedVideoHeight(250f);
        Assert.Equal(420f, ThemeState.Get(Shell.PanelSizeKeys.Width));
        Assert.Equal(250f, ThemeState.Get(Shell.PanelSizeKeys.DockedVideoHeight));
        Assert.Equal("panelWidth", Shell.PanelSizeKeys.Width.Id);
        Assert.Equal("dockedVideoHeight", Shell.PanelSizeKeys.DockedVideoHeight.Id);
        Assert.Equal(420f, sizes.LoadWidth());
        Assert.Equal(250f, sizes.LoadDockedVideoHeight(420f));    // above the 16:9 floor of 236.25

        sizes.SaveWidth(10f);
        Assert.Equal(Shell.RailMinW, sizes.LoadWidth());
    }

    [Fact]
    public void An_unset_docked_height_follows_the_sixteen_by_nine_floor()
        => Assert.Equal(Shell.DockedVideoNaturalH(340f), Shell.PanelSizes.Shared.LoadDockedVideoHeight(340f));
}
