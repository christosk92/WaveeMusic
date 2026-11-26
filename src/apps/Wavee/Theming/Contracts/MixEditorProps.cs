// ── Theming/Contracts/MixEditorProps.cs — the Mix transition editor's picture (#48 Mix M6, full-support plan §2.5) ─────────
//
// One pair of a mixed playlist, edited in the right panel ("Edit transition", opened from the pair's chip in the list) or full-page
// (its expand button): the core host (`MixEditorHost`, Entities/Mix.Editor.Host.cs) keeps the draft, the undo stack, the snapping, the
// save and its states (in `MixEditSession`, so the panel and the page edit one draft); a theme draws what these props carry and calls
// their verbs. One template draws both sizes (`Expanded`). Every string is final (localized, formatted); every number a theme needs to
// draw the timeline is in `MixTimelineModel`, which the shared timeline (Themes/Common/CommonMixTimeline.cs) draws in each theme's look.

using System.Collections.Immutable;

namespace Wavee.Theming.Contracts;

/// <summary>One choice in a row of choices (a preset chip, a style in an ingredient's menu).</summary>
public sealed record MixChoice(int Id, string Label, bool On);

/// <summary>One ingredient (Volume, EQ, Filter, Effects, Looping): its label, the style it plays now (<see cref="Value"/>, the preset's own
/// or the override), whether that is an override of the preset's style, and the styles it offers.</summary>
public sealed record MixIngredientModel(string Key, string Label, string Value, bool Overridden, ImmutableArray<MixChoice> Choices, Action<int> Pick);

/// <summary>The transition's length stepper: one beat of the outgoing song (not beatmatched) or one bar step (beatmatched, recipe 1.8). Its
/// value is <see cref="MixReadout.Length"/> (live while a point is dragged).</summary>
public sealed record MixLengthModel(string Label, bool CanLess, bool CanMore, Action Less, Action More);

/// <summary>The editor's numbers: "Mix out 3:44.0", "In 0:08.2", "4.1 s · 8 beats". They follow a drag on every pointer move.</summary>
public readonly record struct MixReadout(string Out, string In, string Length);

/// <summary>One song of the pair as the editor heads it (Spotify's "Unloving You • Anson Seabra  ! 122 BPM  2B").</summary>
/// <param name="Tempo">"122 BPM", or "" when unknown.</param>
/// <param name="Key">The Camelot key ("2B"), or "" when unknown.</param>
/// <param name="TempoApart">The two tempos are far apart (a quick cut sounds best): the tempo carries a warning mark.</param>
public sealed record MixTrackFacts(string Title, string Artist, string? CoverUrl, string Tempo, string Key, bool TempoApart);

/// <summary>A drag on the timeline, previewed before it is committed: the out-point, the in-point, the overlap (ms) or a curve's bend (−1..1).</summary>
public enum MixTimelineEditKind : byte { Out, In, Overlap, BendOut, BendIn }

/// <summary>One previewed drag value (<see cref="MixTimelineModel.Preview"/>).</summary>
public readonly record struct MixTimelineEdit(MixTimelineEditKind Kind, long Ms, float Bend);

/// <summary>What the shared timeline draws and edits: both songs' waveforms (0..1 levels every <see cref="StepMs"/>), their beat grids
/// (start ms; a downbeat flagged by <see cref="DownA"/> / <see cref="DownB"/>), the transition in A's time (out-point, overlap) and where B
/// enters (in-point; B runs at <see cref="SpeedB"/>), the volume curves (and the filter's cutoff when a filter moves) sampled over each side's
/// window, and the snapping, preview and commit verbs. <see cref="Editable"/> false: drawn, not dragged ("No transition").</summary>
public sealed record MixTimelineModel
{
    public required float[] LevelsA { get; init; }
    public required float[] LevelsB { get; init; }
    public required int StepMs { get; init; }
    public required uint[] BeatsA { get; init; }
    public required bool[] DownA { get; init; }
    public required uint[] BeatsB { get; init; }
    public required bool[] DownB { get; init; }
    public required long DurationA { get; init; }
    public required long DurationB { get; init; }
    public required long OutStartMs { get; init; }
    public required long OverlapMs { get; init; }
    public required long InStartMs { get; init; }
    /// <summary>B's curves' window in mixer time (its fade-in may differ from the overlap: reference §7.1).</summary>
    public required long InWindowMs { get; init; }
    public required float SpeedB { get; init; }
    public required float[] VolumeOut { get; init; }
    public required float[] VolumeIn { get; init; }
    public float[]? CutoffOut { get; init; }
    public float[]? CutoffIn { get; init; }
    public required bool Editable { get; init; }
    /// <summary>"No transition": A runs to its end and B starts at 0; no band, no curves.</summary>
    public required bool None { get; init; }
    public required string LabelA { get; init; }
    public required string LabelB { get; init; }
    /// <summary>The nearest allowed out-point to a dragged one (A's beats; bar phase kept when beatmatched).</summary>
    public required Func<long, long> SnapOut { get; init; }
    public required Func<long, long> SnapIn { get; init; }
    /// <summary>The nearest allowed overlap to a dragged one (whole beats, or whole bars when beatmatched).</summary>
    public required Func<long, long> SnapOverlap { get; init; }
    /// <summary>The picture with one dragged value in place, before it is committed: the timeline redraws its band, waveforms and curves from it on
    /// every pointer move whose snapped value changed, and the editor's <see cref="MixEditorProps.Readout"/> follows.</summary>
    public required Func<MixTimelineEdit, MixTimelineModel> Preview { get; init; }
    /// <summary>The drag ended without a change (or was cancelled): the readout goes back to the draft's.</summary>
    public required Action EndPreview { get; init; }
    public required Action<long> CommitOut { get; init; }
    public required Action<long> CommitIn { get; init; }
    public required Action<long> CommitOverlap { get; init; }
    /// <summary>The volume curve's bend on one side (−1..1), committed from the curve's handle: a hand-drawn curve (recipe 2.6 / 2.7).</summary>
    public required Action<bool, float> CommitBend { get; init; }
    /// <summary>The bend each side's handle shows (0 = the style's own curve).</summary>
    public required float BendOut { get; init; }
    public required float BendIn { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.MixEditor"/>.</summary>
public sealed record MixEditorProps
{
    /// <summary>"Edit transition".</summary>
    public required string Title { get; init; }
    /// <summary>The full-page size (the expand button's): the larger timeline, the overview strip, the ingredients in a grid. False: the right
    /// panel's size.</summary>
    public required bool Expanded { get; init; }
    /// <summary>Expand to the full page, or back into the panel.</summary>
    public required Action ToggleExpand { get; init; }
    public required string ExpandLabel { get; init; }
    /// <summary>Close the editor: an unsaved edit is dropped (the pair plays as it did), a preview stops.</summary>
    public required Action Close { get; init; }
    public required string CloseLabel { get; init; }

    /// <summary>The outgoing song (above the timeline) and the incoming one (below it).</summary>
    public required MixTrackFacts A { get; init; }
    public required MixTrackFacts B { get; init; }
    /// <summary>"118 → 120 BPM · 1A → 2A — these sit well together." (the tempo mark's tooltip).</summary>
    public required string Compat { get; init; }

    /// <summary>"Presets".</summary>
    public required string PresetsLabel { get; init; }
    /// <summary>Auto, the nine presets Spotify offers in its order, No transition (reference §1.2).</summary>
    public required ImmutableArray<MixChoice> Presets { get; init; }
    /// <summary>The thirteen presets Spotify uses internally (Auto picks them), by name; one is <see cref="MixChoice.On"/> when it plays.</summary>
    public required ImmutableArray<MixChoice> MorePresets { get; init; }
    public required string MoreLabel { get; init; }
    public required bool MoreOn { get; init; }
    public required Action<int> PickPreset { get; init; }
    /// <summary>"Auto chose Fade", "Custom", or "".</summary>
    public required string State { get; init; }
    public required string Sentence { get; init; }

    public required MixTimelineModel Timeline { get; init; }
    /// <summary>The numbers, read live (a bound thunk: a drag moves them without a re-render).</summary>
    public required Func<MixReadout> Readout { get; init; }
    public required MixLengthModel Length { get; init; }

    public required ImmutableArray<MixIngredientModel> Ingredients { get; init; }
    public required string BeatmatchLabel { get; init; }
    public required bool Beatmatched { get; init; }
    public required bool BeatmatchAvailable { get; init; }
    /// <summary>"" or "Beatmatch unavailable" (no grid or tempos too far apart).</summary>
    public required string BeatmatchNote { get; init; }
    public required Action<bool> SetBeatmatch { get; init; }
    /// <summary>Clears every style and curve override (the preset's own styles again).</summary>
    public required Action ClearCustom { get; init; }
    public required string ClearCustomLabel { get; init; }
    public required bool Custom { get; init; }

    public required bool CanUndo { get; init; }
    public required bool CanRedo { get; init; }
    public required Action Undo { get; init; }
    public required Action Redo { get; init; }

    /// <summary>"Hear it" / "Stop": the preview seam (`MixHearIt`); unavailable until a preview engine installs itself.</summary>
    public required string PreviewLabel { get; init; }
    public required bool PreviewAvailable { get; init; }
    public required bool Previewing { get; init; }
    public required Action TogglePreview { get; init; }

    /// <summary>The save line: "Saving to Spotify…", "Not saved to Spotify yet…", "Saved on this PC only", or "".</summary>
    public required string SaveLine { get; init; }
    public required bool Saving { get; init; }
    public required bool ShowRetry { get; init; }
    public required Action Retry { get; init; }
    public required bool Dirty { get; init; }
    public required bool CanReset { get; init; }
    public required Action Reset { get; init; }
    public required Action Save { get; init; }

    public required string UndoLabel { get; init; }
    public required string RedoLabel { get; init; }
    public required string ResetLabel { get; init; }
    public required string SaveLabel { get; init; }
    public required string RetryLabel { get; init; }

    /// <summary>Save's prompt on a mixed playlist the user can't edit (the official client's): shown inline over the actions after Save while the
    /// edit is unsaved, "" when not. <see cref="Duplicate"/> copies the playlist with this edit in it (Duplicate Mix, M8) and opens the copy;
    /// <see cref="SaveHere"/> keeps the edit on this PC as Save did before. Save itself rests while the prompt asks (one home per verb).</summary>
    public string PromptTitle { get; init; } = "";
    public string PromptBody { get; init; } = "";
    public string DuplicateLabel { get; init; } = "";
    /// <summary>Duplicate can go out now (online, none running).</summary>
    public bool CanDuplicate { get; init; }
    public Action? Duplicate { get; init; }
    public string SaveHereLabel { get; init; } = "";
    public Action? SaveHere { get; init; }
}

/// <summary>What a transition chip says (<see cref="MixSeamProps.Kind"/>): Spotify's "Auto" (a sparkle), a preset by name or "Custom" (a
/// waveform), "No transition".</summary>
public enum MixSeamKind : byte { Auto, Preset, Custom, None }

/// <summary>The props of <see cref="ViewKeys.MixSeam"/>: the chip between two rows of a mixed playlist, on the thin connector that runs from
/// the row above's artwork to the row below's. The table reserves the lane (<see cref="LaneHeight"/>) under every row while the list shows
/// transitions; the chip sits on the connector at <see cref="Indent"/> (the rows' artwork centre).</summary>
public sealed record MixSeamProps
{
    /// <summary>"Auto", "Fade", "Custom", "No transition".</summary>
    public required string Label { get; init; }
    public required MixSeamKind Kind { get; init; }
    /// <summary>The pair's own state when it is not Spotify's: "This PC" (saved on this PC only), "Not saved" (waiting for Spotify), or "".</summary>
    public required string Note { get; init; }
    /// <summary>The editor is open on this pair (the chip shows the pencil and its selected face).</summary>
    public required bool Selected { get; init; }
    /// <summary>Opens the pair's editor (click, Enter, Space).</summary>
    public required Action Open { get; init; }
    /// <summary>The chip's accessible name: "Edit transition: Auto".</summary>
    public required string Name { get; init; }
    /// <summary>The lane's height (the table's pitch less the row).</summary>
    public required float LaneHeight { get; init; }
    /// <summary>The connector's x: the rows' artwork centre (their lead column's start when the rows show no art).</summary>
    public required float Indent { get; init; }
    /// <summary>Where the chip starts: the rows' artwork's left edge (the lead column's start without art), as Spotify lines it up.</summary>
    public required float Lead { get; init; }
    /// <summary>How far the connector reaches past the lane, above and below, to the artwork's edges in the rows around it (0 without art).</summary>
    public required float Reach { get; init; }
    /// <summary>The window is Compact: the chip takes the 40-DIP touch target.</summary>
    public required bool Touch { get; init; }
    /// <summary>The lane arrived with Mix turning on: its timeline draws in after this many ms (the core's stagger down the screen), the line from
    /// the artwork above into the chip, the chip, then on to the artwork below. Negative: born at rest (no motion).</summary>
    public float Reveal { get; init; } = -1f;
}
