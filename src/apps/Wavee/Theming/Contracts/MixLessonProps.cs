// ── Theming/Contracts/MixLessonProps.cs — the Mix lesson's page and the editor's teaching mode (#48 Mix, owner 2026-11-26) ──────────
//
// "Build a transition together" (Entities/Mix.Lesson.cs): the theme's own Edit transition editor (ViewKeys.MixEditor) in its teaching mode,
// and beside it the guide (ViewKeys.MixLesson): a connected vertical stepper that holds every word and every button. The editor and the
// guide read one model, so they never disagree.
//
// THE LAYER VIEW of the shared timeline (Themes/Common/CommonMixTimeline.Layers.cs): what you hear (both songs' waveforms after every layer,
// their lows as a core, the echo's tail) over a fixed bar window, then one lane per layer, each drawn from MARKS (lines, areas, bars, rules,
// rings, blocks in A's time and the lane's 0..1 height), with its number, name and readout, and at most one handle. A target (where to
// drag) is the hint colour's beacon with a dashed guide from the handle and a tip ("Drag here"); reached, it turns into the success disc.

using System.Collections.Immutable;

namespace Wavee.Theming.Contracts;

/// <summary>A fixed window of the timeline in A's time (ms).</summary>
public readonly record struct MixTimelineWindow(long FromMs, long ToMs);

/// <summary>One bar mark of the ruler: its time in A's ms and its label.</summary>
public readonly record struct MixRulerMark(long Ms, string Label);

/// <summary>What you hear, in A's time from 0, one level every <see cref="StepMs"/>: each song's whole sound (<see cref="OuterA"/>) and its
/// lows (<see cref="CoreA"/>, drawn as the inner core), the echo's tail (over A's lane), the songs' data hues (ARGB, painted through the
/// theme's data-hue ink), the words a solo leaves on a lane ("Only the lows play", "Sunrise is muted for now"). <see cref="Version"/>
/// changes with the picture (the timeline crossfades the old waveform into the new).</summary>
public sealed record MixHeardLevels(float[] OuterA, float[] CoreA, float[] OuterB, float[] CoreB, float[] Echo, int StepMs, uint HueA, uint HueB,
    uint HueEcho, string NoteA, string NoteB, int Version);

/// <summary>A layer lane's handle drags along time or along the lane's value.</summary>
public enum MixLayerAxis : byte { Time, Value }

/// <summary>What a mark draws in a layer lane.</summary>
public enum MixMarkKind : byte
{
    /// <summary>A polyline (<see cref="MixLayerMark.Ys"/> evenly over From..To, or straight from Y0 to Y1).</summary>
    Line,
    /// <summary>The same line dashed.</summary>
    Dash,
    /// <summary>A polyline with its area down to the lane's floor washed in its hue.</summary>
    Area,
    /// <summary>A rounded bar from From to To, centred on Y0, <see cref="MixLayerMark.Size"/> DIP tall.</summary>
    Bar,
    /// <summary>A vertical rule at From, from Y0 to Y1, <see cref="MixLayerMark.Size"/> DIP wide.</summary>
    Rule,
    /// <summary>A ring centred at (From, Y0), <see cref="MixLayerMark.Size"/> DIP across.</summary>
    Ring,
    /// <summary>A filled block from From to To, Y0 to Y1.</summary>
    Block,
    /// <summary>A small label at (From, Y0) (<see cref="MixLayerMark.Text"/>), right-aligned to From.</summary>
    Label,
}

/// <summary>One mark of a layer lane (see <see cref="MixMarkKind"/>). Times are ms in A's time; Y is the lane's height share (0 = top).
/// <see cref="Hue"/> is a data hue (ARGB), 0 for the quiet ink of a layer that is off.</summary>
public sealed record MixLayerMark(MixMarkKind Kind, uint Hue, float FromMs, float ToMs, float Y0, float Y1 = 0f, float Size = 0f, float[]? Ys = null,
    float Alpha = 1f, string Text = "");

/// <summary>A lane's one handle: where it is, which way it drags, its accessible name, and the drag (A's ms and the lane's 0..1 height of the
/// pointer, snapped by the model) and the keys (a step: −1 / +1; <c>big</c>: Page Up / Down).</summary>
public sealed record MixLayerHandle(float Ms, float Y, MixLayerAxis Axis, string Name, Action<float, float> Drag, Action<int, bool> Step,
    bool Nudge = false);

/// <summary>Where the handle should go: the beacon, the dashed guide from the handle, the tip ("Drag here"; "Done" once reached).</summary>
public sealed record MixLayerTarget(float Ms, float Y, string Tip, bool Reached, uint Hue);

/// <summary>One layer lane: its number and name, its readout, its hue (ARGB), whether it is on (its picture in its hue; off: the quiet
/// picture of what it would leave alone), the step's focus (a tinted ground), dimmed (a solo of another layer, a comparison's original),
/// its marks, its handle and target, and its height as a share of the theme's layer lane.</summary>
public sealed record MixLayerLane(string Key, int Number, string Name, string Readout, uint Hue, bool On, bool Focus, bool Dim,
    ImmutableArray<MixLayerMark> Marks, MixLayerHandle? Handle = null, MixLayerTarget? Target = null, float HeightShare = 1f);

/// <summary>The editor's teaching mode: the pair it plays ("Night Drive → Sunrise", the songs' hues), what plays now (its status chip; its
/// hue when one layer is in focus), and whether it sounds (the chip's level bars).</summary>
public sealed record MixTeachModel(string SongA, string SongB, uint HueA, uint HueB, string Status, uint StatusHue, bool Playing);

/// <summary>One step of the guide (see <see cref="MixLessonProps"/>).</summary>
/// <param name="Key">"cut", "vol", "eq", "filter", "echo", "tempo", "final".</param>
/// <param name="Marker">The node's number ("1"), or a glyph (the cut, the flag).</param>
/// <param name="Subtitle">Its line; "Set for you" when it was passed with Auto's value.</param>
public sealed record MixLessonStepModel(string Key, string Title, string Subtitle, FluentGpu.Controls.StepState State, string Marker, bool MarkerIsGlyph,
    bool Passed, uint Hue);

/// <summary>A task of the open step: a drag (its words and its live value), a listen (play / stop), or the comparison.</summary>
public enum MixLessonTaskKind : byte { Drag, Listen, Compare }

/// <summary>Where a task stands: waiting (locked until the one before is done), now, done, playing.</summary>
public enum MixLessonTaskState : byte { Wait, Now, Done, Playing }

/// <summary>One task row of the open step.</summary>
/// <param name="Words">The task's words: a drag's instruction (rich: <c>**bold**</c> for a term, <c>[[…]]</c> for the marker's words), a
/// listen's label ("Hear just the bass"; "Stop" while it plays).</param>
/// <param name="Hint">What to listen for (shown while the task is the current one or plays), the drag's live value ("Swap on bar 9 → 7").</param>
/// <param name="Value">The drag's live value (bound: a drag moves it without a re-render), else null.</param>
/// <param name="Press">The button (null for a drag).</param>
public sealed record MixLessonTask(string Key, MixLessonTaskKind Kind, MixLessonTaskState State, string Words, string Hint, Func<string>? Value,
    Action? Press);

/// <summary>"Hear the original": the play / stop button, the two versions (Original | With …), which one plays, the line on what to listen for.</summary>
public sealed record MixLessonCompare(string Title, string Hint, string OriginalLabel, string WithLabel, bool Enabled, bool Playing, int Selected,
    Action Toggle, Action<int> Select, string PlayLabel, string StopLabel);

/// <summary>The open step's body: its concept lines (rich, short), its tasks, the comparison, the line when it is done, Next.</summary>
public sealed record MixLessonBody(ImmutableArray<string> Lines, ImmutableArray<MixLessonTask> Tasks, MixLessonCompare? Compare, string DoneLine,
    string NextLabel, bool NextAccent, string NextHint, Action? Next, ImmutableArray<(int Number, uint Hue, string Name, string Job)> Recap,
    string ClosingNote, string RestartLabel, Action? Restart);

/// <summary>The props of <see cref="ViewKeys.MixLesson"/>: the page (the editor, the hero, the guide).</summary>
public sealed record MixLessonProps
{
    /// <summary>The editor: the theme's own Edit transition in its teaching mode (a mounted ViewKeys.MixEditor), placed by the template.</summary>
    public required FluentGpu.Dsl.Element Editor { get; init; }
    /// <summary>"Wavee · Learn Mix", the hero's title, its line, Listen first (Stop while it plays) and Show me, the sound note.</summary>
    public required string Eyebrow { get; init; }
    public required string Title { get; init; }
    public required string Lead { get; init; }
    public required string ListenLabel { get; init; }
    public required bool ListenPlaying { get; init; }
    public required Action Listen { get; init; }
    public required string ShowMeLabel { get; init; }
    public required Action ShowMe { get; init; }
    /// <summary>"Turn your sound on. …" (rich).</summary>
    public required string SoundNote { get; init; }
    /// <summary>The intro is open (the hero carries the page's primary action).</summary>
    public required bool AtIntro { get; init; }
    /// <summary>"Build it, one layer at a time", "7 steps" / "Step 3 of 7", the progress 0..1.</summary>
    public required string StepsTitle { get; init; }
    public required string StepsCount { get; init; }
    public required float Progress { get; init; }
    public required ImmutableArray<MixLessonStepModel> Steps { get; init; }
    /// <summary>The open step's index in <see cref="Steps"/> (−1: the intro).</summary>
    public required int Open { get; init; }
    public required MixLessonBody? Body { get; init; }
    public required Action<int> Select { get; init; }
    /// <summary>Leave the lesson (back to where it was opened).</summary>
    public required Action Close { get; init; }
    public required string CloseLabel { get; init; }
    /// <summary>The hint colour (ARGB data hue) the guide's "pink marker" words take.</summary>
    public required uint TargetHue { get; init; }
}
