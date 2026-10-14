// ── Shell/StageModel.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the fullscreen STAGE as one core model (themes plan C4, X11): what a stage view draws as DATA (the mode, the face, whether the
// lyrics are timed, the chrome and its counted menu holds, the gallery, the tip, the aspect, the layout state), the decisions
// behind its keys and buttons as PURE rules, and the verbs a stage view calls
//
// Role: CORE
// Spec: docs/plans/wavee/fullscreen-flagship-implementation.md §4, ch 21
//
// Before this, the gallery toggle was written twice (`Stage.UI.cs` SurfaceCore's `ToggleGallery` and the options popover's
// footer), the face-step key was decided inside `OnKey`, the "close the stage, THEN navigate" idiom was spelled out at four
// call sites, and the chrome's menu hold was a counter field of the component. The context (`Stage.StageCtx`) stays the
// stage's ambient signal bag; this is what decides and what executes.
//
// The window, the focus scope and the idle machine's lifetime are `ImmersiveService` (`ImmersiveService.cs`).

namespace Wavee;

/// <summary>The stage, as a view draws it.</summary>
/// <param name="Mode">Lyrics · Visualizer · Queue · Artist.</param>
/// <param name="Face">The selected visualizer face.</param>
/// <param name="HasTimedLyrics">The playing track has timed lyrics (the caption's gate).</param>
/// <param name="ChromeVisible">The idle machine's output: the controls are shown.</param>
/// <param name="MenuHolds">How many menus hold the chrome (the device flyout, the "…" menu, the options popover, the tip).</param>
/// <param name="GalleryOpen">The stored gallery flag (whether it SHOWS also needs the layout's room).</param>
/// <param name="TipSeen">The gallery tip was dismissed once, ever.</param>
/// <param name="Aspect">The layout class (Desktop · Ultrawide · Portrait · Compact).</param>
/// <param name="Look">The layout state: the mode, the effective layout, the spectrum choice, the hero's lyrics, the caption.</param>
/// <param name="LayoutPref">The STORED layout (the effective one may differ: an Artist without a header draws Large art).</param>
public readonly record struct StageModel(Stage.Mode Mode, Visualizer.Kind Face, bool HasTimedLyrics, bool ChromeVisible,
    int MenuHolds, bool GalleryOpen, bool TipSeen, Stage.Aspect Aspect, Stage.Look Look, Stage.VizLayout LayoutPref)
{
    /// <summary>The stage's model off its context, with the subscriptions of a render.</summary>
    public static StageModel Read(Stage.StageCtx ctx, ChromeIdle idle, bool tipSeen)
        => new(ctx.Mode.Value, ctx.Kind.Value, ctx.HasTimedLyrics.Value, ctx.Chrome.Value, idle.MenuHolds, ctx.GalleryOpen.Value,
            tipSeen, ctx.Layout.Value.Aspect, ctx.Look.Value, ctx.LayoutPref.Value);

    /// <summary>The slice the verbs decide on, off the context's peeks.</summary>
    public static StageState StateOf(Stage.StageCtx ctx)
        => new(ctx.Mode.Peek(), ctx.Look.Peek(), ctx.LayoutPref.Peek(), ctx.Kind.Peek(), ctx.GalleryOpen.Peek());

    // ══ THE RULES (pure) ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The prefs a gallery move writes, in order: the stored layout back to Card, the mode to Visualizer, the gallery flag.
    /// A null <see cref="Mode"/> writes none.</summary>
    public readonly record struct GalleryPlan(bool LayoutToCard, Stage.Mode? Mode, bool Open);

    /// <summary>G: toggle the gallery. The gallery exists only in the Card layout: from a layout (or from another mode while a
    /// layout is stored) G goes back to Card AND opens it, since toggling alone would CLOSE a gallery whose stored flag is
    /// still true. Otherwise <see cref="Stage.ModeRules.ToggleGallery"/> (from another mode it switches to Visualizer first).</summary>
    public static GalleryPlan PlanGalleryToggle(bool lookIsLayout, Stage.Mode mode, Stage.VizLayout layoutPref, bool galleryOpen)
    {
        bool layoutStored = lookIsLayout || (mode != Stage.Mode.Visualizer && layoutPref != Stage.VizLayout.Card);
        if (layoutStored)
            return new GalleryPlan(LayoutToCard: true, mode != Stage.Mode.Visualizer ? Stage.Mode.Visualizer : null, Open: true);
        var (m, open) = Stage.ModeRules.ToggleGallery(mode, galleryOpen);
        return new GalleryPlan(LayoutToCard: false, m != mode ? m : null, open);
    }

    /// <summary>The options popover's footer: OPEN the gallery (the same switch G makes, but never closes it).</summary>
    public static GalleryPlan PlanGalleryOpen(Stage.Mode mode, Stage.VizLayout layoutPref)
        => new(LayoutToCard: layoutPref != Stage.VizLayout.Card, mode != Stage.Mode.Visualizer ? Stage.Mode.Visualizer : null, Open: true);

    /// <summary>What a key does to the face. <see cref="None"/> = the key is not ours.</summary>
    public enum FaceMoveKind : byte { None, Layout, Face }

    /// <summary>The key's step: −1 for <c>[</c>, +1 for <c>]</c>, 0 for anything else, for a modified key, and outside Visualizer mode.</summary>
    public static int FaceStepOf(int keyCode, bool ctrl, bool alt, Stage.Mode mode)
        => ctrl || alt || mode != Stage.Mode.Visualizer ? 0 : Stage.ModeRules.FaceStep(keyCode);

    /// <summary>A face step: inside a layout that does not step faces (Artist only: Large art and Centered step the FACE behind
    /// the cover) the keys walk the LAYOUTS from the STORED one; otherwise they walk the faces (Shift: the groups).</summary>
    public readonly record struct FaceMove(FaceMoveKind Kind, Stage.VizLayout Layout, Visualizer.Kind Face);

    public static FaceMove PlanFaceStep(int step, bool group, in StageState state)
    {
        if (step == 0) return default;
        if (state.Look.IsLayout && !Stage.LayoutRules.KeysStepFaces(state.Look.Eff))
            return new FaceMove(FaceMoveKind.Layout, Stage.LayoutRules.Step(state.LayoutPref, step), default);
        var next = group ? Visualizer.Catalog.StepGroup(state.Face, step) : Visualizer.Catalog.Step(state.Face, step);
        return new FaceMove(FaceMoveKind.Face, default, next);
    }
}

/// <summary>The slice of the stage the verbs decide on.</summary>
public readonly record struct StageState(Stage.Mode Mode, Stage.Look Look, Stage.VizLayout LayoutPref, Visualizer.Kind Face, bool GalleryOpen);

/// <summary>The stage's stored preferences, as the verbs write them.</summary>
public interface IStagePrefsPort
{
    void SetMode(int mode);
    void SetLayout(int layout);
    void SetGalleryOpen(bool open);
    void SetVisualizer(int face);
}

/// <summary>The production port: <c>Prefs.Stage</c>.</summary>
public sealed class StagePrefsPort : IStagePrefsPort
{
    public static readonly StagePrefsPort Shared = new();

    public void SetMode(int mode) => Prefs.Stage.SetMode(mode);
    public void SetLayout(int layout) => Prefs.Stage.SetLayout(layout);
    public void SetGalleryOpen(bool open) => Prefs.Stage.SetGalleryOpen(open);
    public void SetVisualizer(int face) => Prefs.Stage.SetVisualizer(face);
}

/// <summary>What the stage's verbs reach outside the preferences: the surface that is mounted.</summary>
public interface IStageHost
{
    /// <summary>Close the stage (the shared-element flight back to the bar is the host's).</summary>
    void Close(string cause);

    /// <summary>Open the options popover, or close it if open.</summary>
    void OpenOptions();

    /// <summary>The pointer is active (the idle machine's reveal).</summary>
    void Activity();

    /// <summary>Go to a route (the shell's).</summary>
    void GoTo(Shell.Route route);

    /// <summary>The mode / face changed (the always-on diagnostics line).</summary>
    void NoteMode(Stage.Mode mode);

    void NotePick(Visualizer.Kind face);
}

/// <summary>What a stage view can ask of the stage.</summary>
public interface IStageVerbs
{
    void SetMode(Stage.Mode mode);

    /// <summary>A face step (<c>[</c> = −1, <c>]</c> = +1; <paramref name="group"/>: the previous / next GROUP).</summary>
    void StepFace(int step, bool group);

    void SetFace(Visualizer.Kind face);

    /// <summary>G: toggle the gallery — ONE rule, whoever asks.</summary>
    void ToggleGallery();

    /// <summary>Open the gallery (never closes it): the options footer.</summary>
    void OpenGallery();

    void Exit();

    void OpenOptions();

    void Activity();

    /// <summary>Leave the stage BEFORE navigating (V-U29): the shell body is collapsed under it.</summary>
    void Navigate(Shell.Route route);
}

public sealed class StageVerbs(IStagePrefsPort prefs, Func<StageState> state, IStageHost host) : IStageVerbs
{
    public void SetMode(Stage.Mode mode)
    {
        prefs.SetMode((int)mode);
        host.NoteMode(mode);
    }

    public void StepFace(int step, bool group)
    {
        var move = StageModel.PlanFaceStep(step, group, state());
        switch (move.Kind)
        {
            case StageModel.FaceMoveKind.Layout:
                prefs.SetLayout((int)move.Layout);
                break;
            case StageModel.FaceMoveKind.Face:
                prefs.SetVisualizer((int)move.Face);
                host.NotePick(move.Face);
                break;
        }
    }

    public void SetFace(Visualizer.Kind face)
    {
        prefs.SetVisualizer((int)face);
        host.NotePick(face);
    }

    public void ToggleGallery()
    {
        var s = state();
        Apply(StageModel.PlanGalleryToggle(s.Look.IsLayout, s.Mode, s.LayoutPref, s.GalleryOpen));
    }

    public void OpenGallery()
    {
        var s = state();
        Apply(StageModel.PlanGalleryOpen(s.Mode, s.LayoutPref));
    }

    void Apply(StageModel.GalleryPlan plan)
    {
        if (plan.LayoutToCard) prefs.SetLayout((int)Stage.VizLayout.Card);
        if (plan.Mode is { } mode) prefs.SetMode((int)mode);
        prefs.SetGalleryOpen(plan.Open);
    }

    public void Exit() => host.Close("exit-button");

    public void OpenOptions() => host.OpenOptions();

    public void Activity() => host.Activity();

    public void Navigate(Shell.Route route)
    {
        host.Close("navigate");
        host.GoTo(route);
    }

    /// <summary>The same close-then-navigate for a caller that holds only the shared-element <paramref name="begin"/> (a pane
    /// component, a link) and not the stage's verbs: leave the stage BEFORE navigating (V-U29).</summary>
    public static void NavigateClosingStage(Action<string>? begin, Shell.Route route)
    {
        Stage.Close(begin, "navigate");
        Shell.GoTo(route);
    }
}
