// ── Theming/Contracts/DiagnosticsProps.cs — the props and the document model of the Common diagnostics pages (themes plan 6.5, T11c) ──
//
// A diagnostics page is a DOCUMENT: a glyph and a title over a list of `DiagNode`s. The core builds the nodes (it decides what a fact is
// and how it reads: "—" for an unknown one, the invariant number formats, the local-time stamps) and the view draws them. `Build` runs
// INSIDE the view's render, so a `.Value` read in it subscribes the view exactly as the old page component's reads did.
//
// The nodes are plain records over strings, enums and delegates. The widgets that hold state of their own (a poll, a toggle, text boxes)
// are nodes too: the core builds ONE instance of each (a stable object, so the keyed component under it skips on an unchanged page
// render) and the view mounts the component that draws it.

using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.DiagnosticsPlayback"/>, <see cref="ViewKeys.DiagnosticsConnect"/> and
/// <see cref="ViewKeys.DiagnosticsCapture"/>: one frame (pinned header over a scroll keyed on the route) over the document the core
/// builds.</summary>
public sealed record DiagnosticsPageProps
{
    /// <summary>The header's glyph.</summary>
    public required string Glyph { get; init; }

    /// <summary>The header's title.</summary>
    public required string Title { get; init; }

    /// <summary>The scroll key (the route's own).</summary>
    public required string ScrollKey { get; init; }

    /// <summary>Builds the document. The argument is the capture page's expanded root id (-1: none); the other pages ignore it. Runs inside
    /// the view's render: the signals it reads re-render the view.</summary>
    public required Func<long, IReadOnlyList<DiagNode>> Build { get; init; }

    /// <summary>While this reads true the view re-renders every <see cref="PollMs"/> (the capture page's live poll; off, the snapshot is a
    /// fixed empty array and polling would repaint nothing). Null: never.</summary>
    public IReadSignal<bool>? PollWhile { get; init; }

    /// <summary>The poll period in milliseconds (ignored without <see cref="PollWhile"/>).</summary>
    public float PollMs { get; init; }
}

/// <summary>The tone of a status glyph (the view maps it to the theme's system fills).</summary>
public enum DiagTone : byte { Attention, Success, Critical, Caution, Muted }

/// <summary>The margin above a separator rule (the view maps it to its rules).</summary>
public enum DiagGap : byte { Hairline, Tight, Normal, Wide }

/// <summary>One node of a diagnostics document.</summary>
public abstract record DiagNode;

/// <summary>A coloured status glyph and heading over wrapped body copy (a card's inline block, or a page-level one).</summary>
public sealed record DiagStatus(string Glyph, DiagTone Tone, string Heading, string Body) : DiagNode;

/// <summary>A titled card over its nodes.</summary>
public sealed record DiagCard(string Title, IReadOnlyList<DiagNode> Items) : DiagNode;

/// <summary>Label and value. The core has already turned a blank value into the dash. <see cref="Wide"/> uses the Connect page's wider label column.</summary>
public sealed record DiagRow(string Label, string Value, bool Wide = false) : DiagNode;

/// <summary>Wrapped secondary body copy.</summary>
public sealed record DiagBody(string Text) : DiagNode;

/// <summary>Wrapped tertiary micro-meta copy (a caption).</summary>
public sealed record DiagNote(string Text) : DiagNode;

/// <summary>A hairline rule with the given room above it.</summary>
public sealed record DiagSeparator(DiagGap Gap) : DiagNode;

/// <summary>A semibold twelve-point line (a sub-heading); primary or secondary ink.</summary>
public sealed record DiagHeading(string Text, bool Primary) : DiagNode;

/// <summary>A twelve-point line; primary or secondary ink; wrapped or not.</summary>
public sealed record DiagLine(string Text, bool Primary, bool Wrap = true) : DiagNode;

/// <summary>A presence chip: only its background carries the severity.</summary>
public sealed record DiagChip(string Label, bool Present);

/// <summary>Nodes in a tight column (the tiles card's invalidation reasons).</summary>
public sealed record DiagGroup(IReadOnlyList<DiagNode> Items) : DiagNode;

/// <summary>One GPU pass of the tiles card: its kind, its target size and its time.</summary>
public sealed record DiagPassRow(string Kind, string Target, string Ms) : DiagNode;

/// <summary>A name (semibold, primary) followed by presence chips.</summary>
public sealed record DiagNamed(string Name, IReadOnlyList<DiagChip> Chips) : DiagNode;

/// <summary>One button of a <see cref="DiagButtons"/> row. <see cref="Click"/> runs the verb; <see cref="CopyText"/> (when set) is put on the
/// clipboard first and <see cref="Click"/> then runs as the "copied" follow-up; <see cref="Refresh"/> re-renders the page (it re-reads the build).</summary>
public sealed record DiagButton(string Label, bool Accent = false, Action? Click = null, Func<string>? CopyText = null, bool Refresh = false);

/// <summary>A row of buttons.</summary>
public sealed record DiagButtons(IReadOnlyList<DiagButton> Buttons) : DiagNode;

/// <summary>A hyperlink button over a caption (the updater's "Repair auto-update" and why).</summary>
public sealed record DiagLink(string Label, Action Click, string Hint) : DiagNode;

/// <summary>One capture anomaly: a critical dot, its kind, its summary and its root id.</summary>
public sealed record DiagAnomaly(string Kind, string Summary, string RootId) : DiagNode;

/// <summary>One row of an expanded capture tree.</summary>
public sealed record DiagTreeRow(int Depth, string Time, string Label);

/// <summary>One recent capture root: a status dot, its time, its label, its effect count and the tree toggle. <see cref="Expanded"/> is the
/// core's reading of the build argument; <see cref="Tree"/> is set only then (an empty list draws <see cref="EmptyTree"/>).</summary>
public sealed record DiagRoot(long RootId, DiagTone Tone, string Time, string Label, string EffectCount, bool Expanded, string ToggleLabel,
    IReadOnlyList<DiagTreeRow>? Tree, string EmptyTree) : DiagNode;

// ── the stateful widgets ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A card that re-reads itself on a poll (the playback health card: one lock-free metrics read per second).</summary>
public sealed record DiagHealth(Func<DiagCard> Read, float PollMs) : DiagNode;

/// <summary>One result row of the podcast wire check.</summary>
public sealed record DiagWireRow(string Op, string Status, string Bytes, string Root, bool RootFound);

/// <summary>The podcast wire check's verbs and numbers.</summary>
public interface IDiagWireHost
{
    /// <summary>Signed in (a tracked read): the Run button is disabled otherwise.</summary>
    bool Live { get; }

    /// <summary>The playing episode's and show's uris (empty strings when no episode plays): prefilled once, never over a typed field.</summary>
    (string ShowUri, string EpisodeUri) Prefill();

    /// <summary>Runs the checks (an api worker thread does the network; this only awaits) and maps the results to rows.</summary>
    Task<IReadOnlyList<DiagWireRow>> Run(string showUri, string episodeUri, bool saveCaptures);

    /// <summary>The captures were saved: says where (UI thread).</summary>
    void Saved();
}

/// <summary>The podcast wire check card: the texts and the host.</summary>
public sealed record DiagWireCheck(string Title, string Body, string ShowUriLabel, string EpisodeUriLabel, string SaveCapturesLabel,
    string RunLabel, string RunningLabel, string ColOp, string ColStatus, string ColBytes, string ColRoot, IDiagWireHost Host) : DiagNode;

/// <summary>The scroll probe card's state and verbs (the core's `ScrollProbePrefs`).</summary>
public interface IDiagScrollHost
{
    /// <summary>The probe level (Off, Summary, Trace) the strip shows.</summary>
    Signal<int> Level { get; }

    /// <summary>The feel profile's index in <see cref="ProfileNames"/>.</summary>
    Signal<int> Profile { get; }

    /// <summary>The named feel presets.</summary>
    IReadOnlyList<string> ProfileNames { get; }

    /// <summary>Sets and persists the level.</summary>
    void SetLevel(int level);

    /// <summary>Sets and persists the profile.</summary>
    void SetProfile(int index);

    /// <summary>Exports the CSV at the window's device scale and says so (a toast).</summary>
    void Export(float deviceScale);
}

/// <summary>The scroll probe card: its texts and the host.</summary>
public sealed record DiagScroll(string Title, string ProbeLevelLabel, IReadOnlyList<string> LevelLabels, string LevelBody, string ProfileLabel,
    string ExportLabel, string Caption, IDiagScrollHost Host) : DiagNode;

/// <summary>The tile census card's read and its GPU pass toggle. The card owns a poll and the toggle's signal.</summary>
public interface IDiagTilesHost
{
    /// <summary>The engine host is up (the toggle is disabled without it).</summary>
    bool HasHost { get; }

    /// <summary>The pass timing's state when the card mounts (a probe or a flag may have turned it on).</summary>
    bool PassTimingInitial { get; }

    /// <summary>Reads the census (and, with the pass timing on, the last retired frame's breakdown) into a card. The card holds one
    /// <see cref="DiagToggle"/> where the pass-timing switch sits.</summary>
    DiagCard Read(bool passTimingOn);

    /// <summary>Turns the GPU pass timing on or off (a session knob, never persisted).</summary>
    void SetPassTiming(bool on);
}

/// <summary>The tiles card.</summary>
public sealed record DiagTiles(IDiagTilesHost Host, float PollMs) : DiagNode;

/// <summary>The switch's slot inside a card a widget owns: the widget supplies the signal and the handler.</summary>
public sealed record DiagToggle(string Label, bool Enabled) : DiagNode;

/// <summary>What the evidence card shows right now.</summary>
public readonly record struct DiagEvidenceView(bool Healthy, string Facts, bool Busy, string LastFolder);   // LastFolder: already a dash when there is none

/// <summary>A pixel query's answer.</summary>
public readonly record struct DiagEvidenceAnswer(IReadOnlyList<EvidencePixelRow> Rows, string Queried);

/// <summary>The evidence card's reads and verbs.</summary>
public interface IDiagEvidenceHost
{
    /// <summary>The live invariants (read on every poll).</summary>
    DiagEvidenceView Read();

    /// <summary>Queries the latest composite at a point in DIP (null: there is no engine host, nothing changes).</summary>
    DiagEvidenceAnswer? Query(float x, float y);

    /// <summary>Requests an evidence bundle export under a tag.</summary>
    void Export(string tag);
}

/// <summary>The evidence card: its texts and the host.</summary>
public sealed record DiagEvidence(string Title, string HealthyTitle, string WarningTitle, string PixelLabel, string DipLabel, string QueryLabel,
    string PickLabel, string PickingLabel, string ExportLabel, string ExportingLabel, string TagLabel, string LastLabel, string Caption,
    float PollMs, IDiagEvidenceHost Host) : DiagNode;
