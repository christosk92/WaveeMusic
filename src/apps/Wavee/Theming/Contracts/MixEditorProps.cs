// ── Theming/Contracts/MixEditorProps.cs — the Mix transition editor's picture (#48 Mix M6, full-support plan §2.5) ─────────
//
// One pair of a mixed playlist, edited: the core host (`MixEditorHost`, Entities/Mix.Editor.Host.cs) keeps the draft, the undo stack, the
// snapping, the save and its states; a theme draws what these props carry and calls their verbs. Every string is final (localized,
// formatted); every number a theme needs to draw the timeline is in `MixTimelineModel`, which the shared timeline (Themes/Common/Mix) draws
// in each theme's look.

using System.Collections.Immutable;

namespace Wavee.Theming.Contracts;

/// <summary>One choice in a row of choices (a preset chip, a style in an ingredient's menu).</summary>
public sealed record MixChoice(int Id, string Label, bool On);

/// <summary>One Customize ingredient (Volume, EQ, Filter, Effects, Looping): its label, the style it plays now (<see cref="Value"/>,
/// the preset's own or the override), whether that is an override of the preset's style, and the styles it offers.</summary>
public sealed record MixIngredientModel(string Key, string Label, string Value, bool Overridden, ImmutableArray<MixChoice> Choices, Action<int> Pick);

/// <summary>The transition's length: in beats of the outgoing song (not beatmatched) or in bars (beatmatched, recipe 1.8).</summary>
public sealed record MixLengthModel(string Label, string Value, bool CanLess, bool CanMore, Action Less, Action More);

/// <summary>What the shared timeline draws and edits: both songs' waveforms (0..1 levels every <see cref="StepMs"/>), their beat grids
/// (start ms; a downbeat flagged by <see cref="DownA"/> / <see cref="DownB"/>), the transition in A's time (out-point, overlap) and where B
/// enters (in-point; B runs at <see cref="SpeedB"/>), the volume curves (and the filter's cutoff when a filter moves) sampled over each side's
/// window, and the snapping and commit verbs. <see cref="Editable"/> false: drawn, not dragged ("No transition").</summary>
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
    /// <summary>The readout while a point is dragged: the out-point, the in-point, the overlap.</summary>
    public required Func<long, string> FormatOut { get; init; }
    public required Func<long, string> FormatIn { get; init; }
    public required Func<long, string> FormatOverlap { get; init; }
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
    public required string From { get; init; }
    public required string To { get; init; }
    /// <summary>"118 → 120 BPM · 1A → 2A — these sit well together."</summary>
    public required string Compat { get; init; }
    /// <summary>"2 of 10".</summary>
    public required string Position { get; init; }
    public required bool CanPrev { get; init; }
    public required bool CanNext { get; init; }
    public required Action Prev { get; init; }
    public required Action Next { get; init; }

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
    /// <summary>"Mix out 3:44.0", "In 0:08.2", "Overlap 4.1 s · 8 beats".</summary>
    public required string OutReadout { get; init; }
    public required string InReadout { get; init; }
    public required MixLengthModel Length { get; init; }

    public required string CustomizeLabel { get; init; }
    public required bool CustomizeOpen { get; init; }
    public required Action ToggleCustomize { get; init; }
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
    public required Action Cancel { get; init; }
    public required Action Save { get; init; }

    public required string UndoLabel { get; init; }
    public required string RedoLabel { get; init; }
    public required string ResetLabel { get; init; }
    public required string CancelLabel { get; init; }
    public required string SaveLabel { get; init; }
    public required string RetryLabel { get; init; }
    public required string PrevLabel { get; init; }
    public required string NextLabel { get; init; }

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
