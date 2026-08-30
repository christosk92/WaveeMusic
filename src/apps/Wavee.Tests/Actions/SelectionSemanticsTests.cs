using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests.Actions;

// The Explorer right-click ↔ multi-selection semantics (Actions/TrackTargetResolver.cs) over the REAL SelectionModel
// (source-included from src/FluentGpu.Controls): inside a ≥2 selection → act on all of it, selection kept; outside →
// collapse to the clicked row first. Plus the playlist-host row resolution contract (host runs AFTER the settle).
public class SelectionSemanticsTests
{
    static readonly Track[] Ten = MkTen();

    static Track[] MkTen()
    {
        var t = new Track[10];
        for (int i = 0; i < 10; i++) t[i] = T.Mk("t" + i);
        return t;
    }

    static SelectionModel NewSel(int count = 10) => new() { ItemCount = count, Mode = ItemsSelectionMode.Extended };

    static Track? At(int i) => (uint)i < (uint)Ten.Length ? Ten[i] : null;

    [Fact]
    public void RightClick_InsideMultiSelection_TargetsAllSelected_KeepsSelection()
    {
        var sel = NewSel();
        sel.SelectRange(2, 4);

        var target = TrackTargetResolver.Resolve(sel, At, itemIndex: 3, host: static () => null);

        Assert.NotNull(target);
        Assert.Equal(3, target!.Value.Count);
        // Display (index) order — 2, 3, 4.
        Assert.Equal(new[] { "t2", "t3", "t4" }, new[] { target.Value.Tracks[0].Id, target.Value.Tracks[1].Id, target.Value.Tracks[2].Id });
        // Selection intact.
        Assert.Equal(3, sel.SelectedCount);
        Assert.True(sel.IsSelected(2) && sel.IsSelected(3) && sel.IsSelected(4));
    }

    [Fact]
    public void RightClick_OutsideSelection_CollapsesToClickedRow()
    {
        var sel = NewSel();
        sel.SelectRange(2, 4);

        var target = TrackTargetResolver.Resolve(sel, At, itemIndex: 7, host: static () => null);

        Assert.NotNull(target);
        Assert.Equal(1, target!.Value.Count);
        Assert.Equal("t7", target.Value.Tracks[0].Id);
        // Explorer: the selection re-anchored to the clicked row.
        Assert.Equal(1, sel.SelectedCount);
        Assert.True(sel.IsSelected(7));
        Assert.False(sel.IsSelected(3));
    }

    [Fact]
    public void RightClick_OnUnselectedRow_WithNoSelection_SelectsIt()
    {
        var sel = NewSel();
        var target = TrackTargetResolver.Resolve(sel, At, itemIndex: 5, host: static () => null);

        Assert.Equal("t5", target!.Value.Tracks[0].Id);
        Assert.True(sel.IsSelected(5));
        Assert.Equal(1, sel.SelectedCount);
    }

    [Fact]
    public void TrackStartOffset_NonTrackRows_YieldNoMenu_AndOffsetRowsResolve()
    {
        // A vertical-layout list: item 0/1 are hero/chrome (no track), tracks start at item 2 (trackStart = 2).
        const int trackStart = 2;
        var sel = NewSel(count: 12);
        Track? OffsetAt(int i) { int d = i - trackStart; return (uint)d < (uint)Ten.Length ? Ten[d] : null; }

        // A right-click on the hero row resolves no track → no menu.
        Assert.Null(TrackTargetResolver.Resolve(sel, OffsetAt, itemIndex: 0, host: static () => null));

        // Item 5 = display 3 → t3.
        var target = TrackTargetResolver.Resolve(sel, OffsetAt, itemIndex: 5, host: static () => null);
        Assert.Equal("t3", target!.Value.Tracks[0].Id);
        Assert.True(sel.IsSelected(5));   // selection lives in ITEM index space
    }

    [Fact]
    public void MultiSelection_SkipsNonTrackRows_InTargetCount()
    {
        // Rows past the track window (e.g. recommendation slots) return null → excluded from the target.
        var sel = NewSel(count: 12);
        sel.SelectRange(8, 11);   // 8, 9 are tracks; 10, 11 are not (At returns null past index 9)

        var target = TrackTargetResolver.Resolve(sel, At, itemIndex: 9, host: static () => null);

        Assert.Equal(2, target!.Value.Count);
        Assert.Equal(new[] { "t8", "t9" }, new[] { target.Value.Tracks[0].Id, target.Value.Tracks[1].Id });
    }

    [Fact]
    public void HostRuns_AfterSelectionSettles_AndCarriesDisplayOrderedRows()
    {
        var sel = NewSel();
        sel.SelectRange(2, 4);
        var caps = new PlaylistCapabilities(CanView: true, CanEditItems: true, CanEditMetadata: true, IsCollaborative: false, IsOwner: true);

        // The DetailTracks.HostInfo contract: map the CURRENT selection (post-settle) to original-index row refs.
        PlaylistHost? Host()
        {
            var rows = new List<PlaylistRowRef>();
            for (int i = 0; i < sel.ItemCount; i++)
                if (sel.IsSelected(i) && At(i) is { } t)
                    rows.Add(new PlaylistRowRef(i, t.Uri, t.ContextUid ?? ""));
            return rows.Count == 0 ? null : new PlaylistHost("spotify:playlist:p", caps, rows);
        }

        // Collapse case: right-click OUTSIDE the selection — the host must see the COLLAPSED selection (row 7 only).
        var target = TrackTargetResolver.Resolve(sel, At, itemIndex: 7, Host);

        Assert.NotNull(target!.Value.Host);
        var hostRows = target.Value.Host!.Rows;
        Assert.Single(hostRows);
        Assert.Equal(7, hostRows[0].Index);
        Assert.Equal("spotify:track:t7", hostRows[0].Uri);
        Assert.Equal("uid-t7", hostRows[0].ItemId);

        // Multi case: right-click INSIDE the (new) selection after extending it.
        sel.SelectRange(1, 2);   // now {1, 2, 7}
        var multi = TrackTargetResolver.Resolve(sel, At, itemIndex: 2, Host);
        Assert.Equal(3, multi!.Value.Count);
        var rows2 = multi.Value.Host!.Rows;
        Assert.Equal(3, rows2.Count);
        Assert.Equal(new[] { 1, 2, 7 }, new[] { rows2[0].Index, rows2[1].Index, rows2[2].Index });   // display order
    }

    [Fact]
    public void RemoveGate_ComposesWithResolvedHost()
    {
        // The resolver + rules together: an editable host with rows enables Remove-from-this-playlist.
        var sel = NewSel();
        var caps = new PlaylistCapabilities(true, true, true, false, true);
        var target = TrackTargetResolver.Resolve(sel, At, 4,
            () => new PlaylistHost("spotify:playlist:p", caps, new[] { new PlaylistRowRef(4, "spotify:track:t4", "uid-t4") }));
        Assert.True(ActionRules.CanRemoveFromPlaylist(target!.Value.Host));
    }
}
