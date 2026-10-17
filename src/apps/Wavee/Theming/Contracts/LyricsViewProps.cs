// ── Theming/Contracts/LyricsViewProps.cs — the props of `Lyrics.View` and the host it draws from (themes plan 6.0, T7b) ───────
//
// THE LYRICS SURFACE IS A CORE HOST AND A THEME PICTURE. `Lyrics.ViewCore` (Shell\Lyrics.UI.cs) keeps what decides and drives: the
// document slot and its upgrade gate, the media clock, the active / voice lines, the follow and resync machine, the cascade, the
// depth-of-field ramp, the word wipe and the nodes they write, the motion demand, the ticker. On each render it builds this record and
// mounts `Slots.View(ViewKeys.LyricsView, props)`; the structure's template lays the layers out. The rows and the list around them are
// the style (`LyricsLinesStyle`), which the host calls where the document is on screen.
//
// A theme reaches the core only through `ILyricsHost`: the mount config and facts a row, an overlay or the list reads, and the few
// verbs they press. A signal read inside a template's render subscribes THAT render (a row re-renders on its own emphasis, an overlay on
// its own flag), exactly as the old components' bodies did.
//
// The record is rebuilt on every host render, so it is never equal to the last one and the template re-runs with it; the template has no
// state of its own.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.LyricsView"/>.</summary>
public sealed record LyricsViewProps
{
    /// <summary>The core host: what the layers read and press.</summary>
    public required ILyricsHost Host { get; init; }

    /// <summary>Not null = nothing to read (nothing playing, a track with no lyrics id): the surface shows this one centred line in
    /// place of the layers.</summary>
    public string? Message { get; init; }

    /// <summary>The track-keyed document boundary (the store sync, the skeleton region and the list or the unsynced block in it).</summary>
    public Element? Body { get; init; }

    /// <summary>The frame stepper's host (zero size), mounted only while the surface is visible and owns playback.</summary>
    public Element? Ticker { get; init; }

    /// <summary>The developer layer (the lyrics-search debug pill and overlay; zero size unless developer mode is on).</summary>
    public Element? Debug { get; init; }

    /// <summary>The on-device AI disclosure under the reading layers; null on the podcast reader (episodes are never timed).</summary>
    public Element? Footer { get; init; }
}

/// <summary>What the podcast peek's gate needs: the quiet "length · language" line and the verb that starts the episode.</summary>
public readonly record struct LyricsPeekGate(string Meta, Action Play);

/// <summary>The core lyrics host as a theme sees it (<c>Lyrics.ViewCore</c> implements it).</summary>
public interface ILyricsHost
{
    // ── what this mount is (frozen for the host's life) ────────────────────────────────────────────────────────────

    /// <summary>Stage metrics (type, gutter, band, σ ladder) rather than the rail's.</summary>
    bool Large { get; }

    /// <summary>The fullscreen stage's pane: its reading column is capped and left-anchored (the blur strength there is always 0).</summary>
    bool OnStage { get; }

    /// <summary>The stage's type scale over the authored metrics (1 on the rail and the reader).</summary>
    float TypeScale { get; }

    /// <summary>The ink the surface paints with (theme rungs on the rail, the stage's own ladder on media).</summary>
    Lyrics.Ink Ink { get; }

    /// <summary>The ART ACCENT the stage's active-line pill fills with (a mount-time signal instance); null on the rail and the reader.</summary>
    IReadSignal<ColorF>? Accent { get; }

    /// <summary>The focal band (where the active row's content centres, as a fraction of the viewport).</summary>
    float Band { get; }

    // ── what is on screen (live: reads subscribe the caller) ───────────────────────────────────────────────────────

    /// <summary>The row type and rhythm of the document on screen (the timed surface's, at the stage's scale).</summary>
    Lyrics.RowMetrics Metrics { get; }

    /// <summary>The surface shows a podcast transcript.</summary>
    bool IsPodcast { get; }

    /// <summary>The surface's subject is the playing playable.</summary>
    bool OwnsPlayback { get; }

    /// <summary>Same as <see cref="OwnsPlayback"/>, read so the caller subscribes to playback identity.</summary>
    bool ObservedPlaybackOwner { get; }

    // ── the lines (the style's) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The variable-height layout the synced list draws over (one per document; it also feeds the follow).</summary>
    IVirtualLayout EnsureLayout();

    /// <summary>The list's one scroll handle (the follow moves it; its motion is the user-scroll edge).</summary>
    ScrollHandle Scroll { get; }

    /// <summary>The list's viewport node was realized.</summary>
    void SetViewport(NodeHandle viewport);

    /// <summary>The row's identity: it changes when a document swap remounts rows.</summary>
    string RowKey(int index);

    /// <summary>One value-gated signal per line carrying its packed emphasis word (distance, past, reserve).</summary>
    Signal<int> EmphasisOf(int index);

    /// <summary>The line's halo (bloom) fade, or null out of range.</summary>
    FloatSignal? GlowOf(int index);

    /// <summary>The secondary-line mode (none, translation, romanization) as a signal the rows read.</summary>
    Signal<int> SecondaryLine { get; }

    /// <summary>The strength multiplier of the active line's halo (read only inside the active branch).</summary>
    FloatSignal HaloScale { get; }

    /// <summary>The media time of the last step.</summary>
    FloatSignal Now { get; }

    /// <summary>The follow scroll's mode (peek it: a row never re-renders on it).</summary>
    Signal<Lyrics.FollowMode> Follow { get; }

    /// <summary>The depth-of-field σ line <paramref name="index"/>'s node carries right now.</summary>
    float DofShownFor(int index);

    /// <summary>The wipe feather of a line (a fraction over its measured run).</summary>
    float SoftnessOfLine(int index);

    /// <summary>The row's text, its halo and its depth-of-field box were realized: the frame driver writes them by handle.</summary>
    void ReportLineNode(int index, NodeHandle node);

    void ReportGlowNode(int index, NodeHandle node);

    void ReportDofNode(int index, NodeHandle node);

    /// <summary>A line was tapped: back to live, commit the seek.</summary>
    void SeekToLine(int index);

    /// <summary>The depth blur is on (the strength is above 0; the stage's is always 0).</summary>
    bool BlurEnabled { get; }

    /// <summary>The podcast peek's gate facts (read at build time).</summary>
    LyricsPeekGate PeekGate();

    // ── the overlays ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The three breathing dots are up (subscribing).</summary>
    bool DotsShown();

    /// <summary>The dots' fills, driven by the frame lane (bind, never write).</summary>
    IReadOnlyList<FloatSignal> DotAlpha { get; }

    /// <summary>The dots' row was realized: the frame lane writes its breath.</summary>
    void OnDotsRealized(NodeHandle row);

    /// <summary>A video suppresses timed sync (subscribing): the note shows.</summary>
    bool VideoNoteShown();

    /// <summary>The Resync pill is up (subscribing).</summary>
    bool ResyncShown();

    /// <summary>The pill's countdown ring (1 = full).</summary>
    FloatSignal ResyncProgress { get; }

    /// <summary>The pill was pressed: re-attach the follow now.</summary>
    void Resync();
}
