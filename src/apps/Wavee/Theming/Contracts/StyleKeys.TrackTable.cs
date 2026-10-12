// ── Theming/Contracts/StyleKeys.TrackTable.cs — the track table's list style key (themes plan 6.0, T8a) ───────────────────────
//
// T8a publishes the first list-shaped key: the track table's rows. The record grows with T8b (header, command / list bar,
// filter card, selection bar, placeholder) and T8c; the id never changes.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class StyleKeys
{
    /// <summary>The track table's list style: the Modern and Classic row templates (<see cref="TrackListStyle"/>). Read by the
    /// core table host (<c>Track.TableHost</c>) through the ambient view registry.</summary>
    public static readonly StyleKey<TrackListStyle> TrackTable = new("Track.Table");
}
