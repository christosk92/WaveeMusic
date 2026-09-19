using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using Wavee.Core;

namespace Wavee;

// The ONE place the right-click ↔ multi-selection semantics live (Explorer rules), shared by every virtualized track
// list. Engine-free apart from SelectionModel (portable — Wavee.Tests source-includes it next to this file):
//   • the clicked row IS part of a ≥2 multi-selection → the target is ALL selected tracks (display order),
//     selection kept;
//   • the clicked row is NOT selected (or the selection is a lone other row) → DeselectAll + Select(row) FIRST
//     (Explorer), the target is that one row.
// The host thunk runs AFTER the selection settles, so a playlist host resolves rows for exactly the target set.
public static class TrackTargetResolver
{
    /// <summary>Resolve the action target for a context request on <paramref name="itemIndex"/> (ITEM index — the
    /// SelectionModel's index space; <paramref name="trackAt"/> maps an item index to its track, returning null for
    /// non-track rows such as headers/recommendations). Returns null when the row carries no track (no menu).</summary>
    public static ActionTarget? Resolve(
        SelectionModel selection, Func<int, Track?> trackAt, int itemIndex, Func<PlaylistHost?> host)
    {
        if (trackAt(itemIndex) is not { } clicked) return null;

        bool partOfMulti = selection.IsSelected(itemIndex) && SelectedTrackCount(selection, trackAt) >= 2;
        if (!partOfMulti)
        {
            // Explorer: right-click outside the selection re-anchors it to the clicked row first.
            if (!(selection.IsSelected(itemIndex) && selection.SelectedCount == 1))
            {
                selection.DeselectAll();
                selection.Select(itemIndex);
            }
            return ActionTarget.ForTracks(new[] { clicked }, host());
        }

        // Multi: every selected TRACK row, in display (index) order; selection stays.
        var tracks = new List<Track>(selection.SelectedCount);
        for (int r = 0; r < selection.RangeCount; r++)
        {
            var (start, end) = selection.GetRange(r);
            for (int i = start; i <= end; i++)
                if (trackAt(i) is { } t) tracks.Add(t);
        }
        return ActionTarget.ForTracks(tracks, host());
    }

    static int SelectedTrackCount(SelectionModel selection, Func<int, Track?> trackAt)
    {
        int n = 0;
        for (int r = 0; r < selection.RangeCount; r++)
        {
            var (start, end) = selection.GetRange(r);
            for (int i = start; i <= end && n < 2; i++)
                if (trackAt(i) is not null) n++;
            if (n >= 2) break;
        }
        return n;
    }
}
