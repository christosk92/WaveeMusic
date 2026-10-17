// ── Theming/Contracts/ViewKeys.Queue.cs — the queue's published keys (themes plan 6.0, T7d) ───────────────────────────────────
//
// T7d publishes the queue's two surfaces: the right panel's queue body (the mode pills, "Playing from", the now-playing card and the three
// sections' lane) and the fullscreen stage's Up next column (its header with the Autoplay switch, and the same lane in the stage's ink). The
// MODEL (`Shell.QueueModel`: the section split, the counts, the autoplay lane), the verbs (`IQueueVerbs`), the page counters, the realized
// slots, the reorder lane and its drag, drop and keyboard rules, and the row's context menu stay core (`Queue.RailPanelHost`,
// `Queue.StagePaneHost`).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The panel's queue body: the shuffle, repeat and autoplay pills, "Playing from", the now-playing card, the sections with their
    /// headers, rows and "Show more", and the nothing-playing vacancy. Mounted by <c>Queue.RailPanelHost</c> (the core host). Props:
    /// <see cref="QueuePanelProps"/>.</summary>
    public static readonly ViewKey<QueuePanelProps> QueuePanel = new("Queue.Panel");

    /// <summary>The stage's Up next column: the title and the Autoplay switch over the scrolling lane, in the stage's ink at the stage's
    /// queue scale. Mounted by <c>Queue.StagePaneHost</c> (the core host). Props: <see cref="QueueStagePaneProps"/>.</summary>
    public static readonly ViewKey<QueueStagePaneProps> QueueStagePane = new("Queue.StagePane");
}
