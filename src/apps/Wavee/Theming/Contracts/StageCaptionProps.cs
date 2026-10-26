// ── Theming/Contracts/StageCaptionProps.cs — the props of `Stage.Caption` (themes plan 6.0, T7a1) ──────────────────────────────
//
// THE CAPTION IS A CORE HOST AND A THEME PICTURE. `Stage.CaptionHost` (Shell\Stage.Caption.UI.cs) keeps the playhead clock, the
// view edge wake, the render-thread wipe rows, the break dots' driver and the bookkeeping of the nodes they pose; on each render it
// builds this record ("what ONE render decided") and mounts `Slots.View(ViewKeys.StageCaption, props)`. The structure's template
// lays out the veil, the previous / active / next slots and the dots, and binds the node captures the host's timing poses.
//
// The record is rebuilt on every host render (a line edge, a position report), so it is never equal to the last one and the template
// re-runs with it; the template has no state of its own.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee.Theming.Contracts;

/// <summary>The props of <see cref="ViewKeys.StageCaption"/>.</summary>
public sealed record StageCaptionProps
{
    // ── the allocator's caption geometry (Stage.Layout) ────────────────────────────────────────────────────────────

    /// <summary>The block's width (also the wrap width of every line).</summary>
    public required float Width { get; init; }

    /// <summary>The block's left edge (centred in the face's region).</summary>
    public required float X { get; init; }

    /// <summary>The block's bottom margin from the stage's bottom edge (above the transport and the hairline).</summary>
    public required float Bottom { get; init; }

    /// <summary>The block at its tallest (two active lines plus the context lines when shown): the veil's height before its overhang.</summary>
    public required float BlockMaxHeight { get; init; }

    /// <summary>The active line's type size and line height.</summary>
    public required float ActiveSize { get; init; }

    public required float ActiveLineHeight { get; init; }

    /// <summary>The context lines' (and the secondary line's) type size and line height.</summary>
    public required float ContextSize { get; init; }

    public required float ContextLineHeight { get; init; }

    // ── the slots ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The document generation as text: part of every slot's key, so a new track's line 5 is a NEW keyed slot.</summary>
    public required string Generation { get; init; }

    /// <summary>The previous line's document index and text; null text = no slot.</summary>
    public required int PreviousIndex { get; init; }

    public required string? PreviousText { get; init; }

    /// <summary>The next line's document index and text; null text = no slot.</summary>
    public required int NextIndex { get; init; }

    public required string? NextText { get; init; }

    /// <summary>The active line's document index and text; null text = no active slot (a break, or nothing resolved).</summary>
    public required int ActiveIndex { get; init; }

    public required string? ActiveText { get; init; }

    /// <summary>The translation / romanization the user chose, under the active line; null or empty = none.</summary>
    public string? Secondary { get; init; }

    /// <summary>The active line is word-synced and carries the karaoke wipe at this split; null = a plain, whole-fade line.</summary>
    public StageCaptionWipe? Wipe { get; init; }

    /// <summary>A break (or a long intro) stands in the active slot: the three breathing dots, keyed by <see cref="Anchor"/>.</summary>
    public required bool Dots { get; init; }

    /// <summary>The line the view hangs on (the dots' key).</summary>
    public required int Anchor { get; init; }

    /// <summary>The accent bloom behind the active line.</summary>
    public required ColorF Bloom { get; init; }

    // ── what the host's timing poses ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The wipe node captures: the sung text, its bloom, the dots' row.</summary>
    public required Action<NodeHandle> OnMain { get; init; }

    public required Action<NodeHandle> OnGlow { get; init; }

    public required Action<NodeHandle> OnDots { get; init; }

    /// <summary>The three dots' alpha, driven by the host on the media clock (bind, never write).</summary>
    public required IReadOnlyList<FloatSignal> DotAlpha { get; init; }

    /// <summary>The per-frame ticker or the edge wake (zero-size components the host owns), placed last in the layer.</summary>
    public required IReadOnlyList<Element> Ticker { get; init; }
}

/// <summary>The numbers of the active line's karaoke wipe: how far the sung part reaches, its feather over the measured run, its
/// lift, and the run the render thread steps the split along (0 = not measured yet).</summary>
public readonly record struct StageCaptionWipe(float Split, float Softness, float Lift, float Run);
