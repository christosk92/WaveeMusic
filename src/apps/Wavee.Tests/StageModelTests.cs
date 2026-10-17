// ── Wavee.Tests/StageModelTests.cs — the fullscreen stage as a core model (themes plan C4, X11) ──────────────────────
// (Shell/StageModel.cs.) Pure: the rules take data, and the verbs run over a recording preferences port, a recording host and a
// state delegate, so nothing here mounts a stage, reads the registry or touches a signal of the playback model.
//
//   ONE GALLERY RULE  G toggles it and the options footer opens it; both are the same switch: the gallery exists only in the
//                     Card layout, so from a layout (or from another mode while a layout is stored) it goes back to Card AND
//                     opens, because toggling alone would CLOSE a gallery whose stored flag is still true.
//   THE FACE KEYS     [ and ] step the faces (Shift: the groups) — except inside the Artist layout, where they walk the layouts
//                     from the STORED one; a modified key and any mode but Visualizer are not ours.
//   NAVIGATE          leave the stage BEFORE navigating: the shell body is collapsed under it.

using Xunit;

namespace Wavee.Tests;

sealed class RecordingStagePrefs : IStagePrefsPort
{
    public readonly List<string> Writes = [];
    public void SetMode(int mode) => Writes.Add("mode:" + mode);
    public void SetLayout(int layout) => Writes.Add("layout:" + layout);
    public void SetGalleryOpen(bool open) => Writes.Add("gallery:" + open);
    public void SetVisualizer(int face) => Writes.Add("face:" + face);
}

sealed class RecordingStageHost : IStageHost
{
    public readonly List<string> Calls = [];
    public void Close(string cause) => Calls.Add("close:" + cause);
    public void OpenOptions() => Calls.Add("options");
    public void Activity() => Calls.Add("activity");
    public void GoTo(Shell.Route route) => Calls.Add("goto:" + route.Kind);
    public void NoteMode(Stage.Mode mode) => Calls.Add("mode:" + mode);
    public void NotePick(Visualizer.Kind face) => Calls.Add("pick:" + face);
}

public class StageGalleryRuleTests
{
    const Stage.Mode Lyrics = Stage.Mode.Lyrics, Viz = Stage.Mode.Visualizer;
    const Stage.VizLayout Card = Stage.VizLayout.Card, LargeArt = Stage.VizLayout.LargeArt;

    [Fact]
    public void In_visualizer_mode_on_the_card_the_gallery_simply_toggles()
    {
        Assert.Equal(new StageModel.GalleryPlan(false, null, false), StageModel.PlanGalleryToggle(false, Viz, Card, galleryOpen: true));
        Assert.Equal(new StageModel.GalleryPlan(false, null, true), StageModel.PlanGalleryToggle(false, Viz, Card, galleryOpen: false));
    }

    [Fact]
    public void From_another_mode_it_switches_to_visualizer_and_opens()
        => Assert.Equal(new StageModel.GalleryPlan(false, Viz, true), StageModel.PlanGalleryToggle(false, Lyrics, Card, galleryOpen: true));

    [Fact]
    public void From_a_layout_it_goes_back_to_card_and_opens_rather_than_closing_a_gallery_whose_flag_is_still_true()
    {
        Assert.Equal(new StageModel.GalleryPlan(true, null, true), StageModel.PlanGalleryToggle(true, Viz, LargeArt, galleryOpen: true));
        // another mode with a layout STORED counts the same, and also switches the mode
        Assert.Equal(new StageModel.GalleryPlan(true, Viz, true), StageModel.PlanGalleryToggle(false, Lyrics, LargeArt, galleryOpen: true));
    }

    [Fact]
    public void The_options_footer_opens_and_never_closes()
    {
        Assert.Equal(new StageModel.GalleryPlan(false, null, true), StageModel.PlanGalleryOpen(Viz, Card));
        Assert.Equal(new StageModel.GalleryPlan(true, Viz, true), StageModel.PlanGalleryOpen(Lyrics, LargeArt));
        Assert.Equal(new StageModel.GalleryPlan(true, null, true), StageModel.PlanGalleryOpen(Viz, Stage.VizLayout.Artist));
    }
}

public class StageFaceStepRuleTests
{
    static StageState State(Stage.Mode mode = Stage.Mode.Visualizer, Stage.VizLayout eff = Stage.VizLayout.Card,
        Stage.VizLayout stored = Stage.VizLayout.Card, Visualizer.Kind? face = null)
        => new(mode, new Stage.Look(mode, eff, Stage.SpectrumStyle.Bars, false, false), stored, face ?? Visualizer.Catalog.Default, true);

    [Fact]
    public void The_bracket_keys_step_back_and_forward_only_unmodified_in_visualizer_mode()
    {
        Assert.Equal(-1, StageModel.FaceStepOf(Stage.ModeRules.PreviousFaceKey, false, false, Stage.Mode.Visualizer));
        Assert.Equal(1, StageModel.FaceStepOf(Stage.ModeRules.NextFaceKey, false, false, Stage.Mode.Visualizer));
        Assert.Equal(0, StageModel.FaceStepOf(Stage.ModeRules.NextFaceKey, true, false, Stage.Mode.Visualizer));
        Assert.Equal(0, StageModel.FaceStepOf(Stage.ModeRules.NextFaceKey, false, true, Stage.Mode.Visualizer));
        Assert.Equal(0, StageModel.FaceStepOf(Stage.ModeRules.NextFaceKey, false, false, Stage.Mode.Lyrics));
        Assert.Equal(0, StageModel.FaceStepOf(65, false, false, Stage.Mode.Visualizer));
    }

    [Fact]
    public void On_the_card_the_keys_walk_the_faces_and_shift_walks_the_groups()
    {
        var state = State();
        var one = StageModel.PlanFaceStep(1, false, in state);
        Assert.Equal(StageModel.FaceMoveKind.Face, one.Kind);
        Assert.Equal(Visualizer.Catalog.Step(state.Face, 1), one.Face);

        var group = StageModel.PlanFaceStep(-1, true, in state);
        Assert.Equal(StageModel.FaceMoveKind.Face, group.Kind);
        Assert.Equal(Visualizer.Catalog.StepGroup(state.Face, -1), group.Face);
    }

    [Fact]
    public void Inside_large_art_and_centered_the_keys_still_step_the_face_behind_the_cover()
    {
        var state = State(eff: Stage.VizLayout.LargeArt, stored: Stage.VizLayout.LargeArt);
        Assert.Equal(StageModel.FaceMoveKind.Face, StageModel.PlanFaceStep(1, false, in state).Kind);
    }

    [Fact]
    public void Inside_the_artist_layout_the_keys_walk_the_layouts_from_the_STORED_one()
    {
        var state = State(eff: Stage.VizLayout.Artist, stored: Stage.VizLayout.Artist);
        var forward = StageModel.PlanFaceStep(1, false, in state);
        Assert.Equal(StageModel.FaceMoveKind.Layout, forward.Kind);
        Assert.Equal(Stage.LayoutRules.Step(Stage.VizLayout.Artist, 1), forward.Layout);

        // an Artist without a header DRAWS Large art but steps on from the stored Artist
        var drawsLargeArt = State(eff: Stage.VizLayout.LargeArt, stored: Stage.VizLayout.Artist);
        Assert.Equal(StageModel.FaceMoveKind.Face, StageModel.PlanFaceStep(1, false, in drawsLargeArt).Kind);
    }

    [Fact]
    public void A_zero_step_is_no_move()
    {
        var state = State();
        Assert.Equal(StageModel.FaceMoveKind.None, StageModel.PlanFaceStep(0, false, in state).Kind);
    }
}

public class StageVerbsTests
{
    static (StageVerbs Verbs, RecordingStagePrefs Prefs, RecordingStageHost Host) Make(StageState state)
    {
        var prefs = new RecordingStagePrefs();
        var host = new RecordingStageHost();
        return (new StageVerbs(prefs, () => state, host), prefs, host);
    }

    static StageState Plain(Stage.Mode mode, Stage.VizLayout stored = Stage.VizLayout.Card, bool galleryOpen = true)
        => new(mode, Stage.Look.Plain(mode), stored, Visualizer.Catalog.Default, galleryOpen);

    [Fact]
    public void Setting_the_mode_writes_it_then_says_so()
    {
        var (verbs, prefs, host) = Make(Plain(Stage.Mode.Lyrics));
        verbs.SetMode(Stage.Mode.Queue);
        Assert.Equal(new[] { "mode:" + (int)Stage.Mode.Queue }, prefs.Writes);
        Assert.Equal(new[] { "mode:Queue" }, host.Calls);
    }

    [Fact]
    public void G_from_another_mode_with_a_layout_stored_writes_card_then_visualizer_then_open()
    {
        var (verbs, prefs, _) = Make(Plain(Stage.Mode.Lyrics, stored: Stage.VizLayout.Centered));
        verbs.ToggleGallery();
        Assert.Equal(new[] { "layout:0", "mode:" + (int)Stage.Mode.Visualizer, "gallery:True" }, prefs.Writes);
    }

    [Fact]
    public void G_on_an_open_gallery_in_visualizer_mode_closes_it_and_writes_nothing_else()
    {
        var (verbs, prefs, _) = Make(Plain(Stage.Mode.Visualizer, galleryOpen: true));
        verbs.ToggleGallery();
        Assert.Equal(new[] { "gallery:False" }, prefs.Writes);
    }

    [Fact]
    public void The_options_footer_opens_the_gallery_through_the_same_rule()
    {
        var (verbs, prefs, _) = Make(Plain(Stage.Mode.Visualizer, galleryOpen: false));
        verbs.OpenGallery();
        Assert.Equal(new[] { "gallery:True" }, prefs.Writes);
    }

    [Fact]
    public void Stepping_a_face_writes_it_and_notes_the_pick_but_stepping_a_layout_writes_only_the_layout()
    {
        var card = new StageState(Stage.Mode.Visualizer, Stage.Look.Plain(Stage.Mode.Visualizer), Stage.VizLayout.Card,
            Visualizer.Catalog.Default, true);
        var (verbs, prefs, host) = Make(card);
        verbs.StepFace(1, group: false);
        var next = Visualizer.Catalog.Step(Visualizer.Catalog.Default, 1);
        Assert.Equal(new[] { "face:" + (int)next }, prefs.Writes);
        Assert.Equal(new[] { "pick:" + next }, host.Calls);

        var artist = new StageState(Stage.Mode.Visualizer,
            new Stage.Look(Stage.Mode.Visualizer, Stage.VizLayout.Artist, Stage.SpectrumStyle.Bars, false, false),
            Stage.VizLayout.Artist, Visualizer.Catalog.Default, true);
        var (layoutVerbs, layoutPrefs, layoutHost) = Make(artist);
        layoutVerbs.StepFace(1, group: false);
        Assert.Equal(new[] { "layout:" + (int)Stage.LayoutRules.Step(Stage.VizLayout.Artist, 1) }, layoutPrefs.Writes);
        Assert.Empty(layoutHost.Calls);
    }

    [Fact]
    public void Picking_a_face_writes_it_and_notes_the_pick()
    {
        var (verbs, prefs, host) = Make(Plain(Stage.Mode.Visualizer));
        verbs.SetFace(Visualizer.Kind.Aurora);
        Assert.Equal(new[] { "face:" + (int)Visualizer.Kind.Aurora }, prefs.Writes);
        Assert.Equal(new[] { "pick:Aurora" }, host.Calls);
    }

    [Fact]
    public void Exit_closes_with_the_exit_button_cause_and_options_and_activity_forward_to_the_surface()
    {
        var (verbs, _, host) = Make(Plain(Stage.Mode.Lyrics));
        verbs.Exit();
        verbs.OpenOptions();
        verbs.Activity();
        Assert.Equal(new[] { "close:exit-button", "options", "activity" }, host.Calls);
    }

    [Fact]
    public void Navigating_leaves_the_stage_first()
    {
        var (verbs, _, host) = Make(Plain(Stage.Mode.Lyrics));
        verbs.Navigate(new Shell.Route(Shell.RouteKind.Home));
        Assert.Equal(new[] { "close:navigate", "goto:Home" }, host.Calls);
    }
}
