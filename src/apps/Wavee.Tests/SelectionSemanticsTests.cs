// ── Wavee.Tests/SelectionSemanticsTests.cs — right-click ↔ multi-selection (Explorer rules) ─────────────────────────
//
// Wave 4.5's gate for `Track.TargetResolver` (Entities/Track.Rules.cs), ported VERBATIM from 0.2.9's
// Actions/SelectionSemanticsTests (149 lines) over the REAL engine SelectionModel: inside a ≥2 selection → act on all of
// it, selection kept; outside → collapse to the clicked row first; the playlist host runs AFTER the settle.
//
// What moved: a track is a handle (so the fixtures boot a fake scope — `ActionTarget.ForTracks` reads the first
// track's title), a non-track row is the `default` handle rather than null, and `PlaylistHost.Rows` is the list of
// ORIGINAL indices (the 0.3 action table resolves uri/item id from the indices at execute time), so 0.2.9's
// per-host-row Uri/ItemId assertions become the index plus the target track they name.

using FluentGpu.Controls;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class SelectionSemanticsTests
{
    static Track[] Ten()
    {
        TestScope.Fresh();
        var t = new Track[10];
        for (int i = 0; i < 10; i++) t[i] = Entities.Track(EntityUri.Parse("spotify:track:t" + i));
        return t;
    }

    static SelectionModel NewSel(int count = 10) => new() { ItemCount = count, Mode = ItemsSelectionMode.Extended };

    static Func<int, Track> At(Track[] ten) => i => (uint)i < (uint)ten.Length ? ten[i] : default;

    [Fact]
    public void RightClick_InsideMultiSelection_TargetsAllSelected_KeepsSelection()
    {
        var ten = Ten();
        var sel = NewSel();
        sel.SelectRange(2, 4);

        var target = Track.TargetResolver.Resolve(sel, At(ten), itemIndex: 3, host: null);

        Assert.NotNull(target);
        Assert.Equal(3, target!.Value.Count);
        // Display (index) order — 2, 3, 4.
        Assert.Equal(new[] { ten[2], ten[3], ten[4] }, new[] { target.Value.Tracks[0], target.Value.Tracks[1], target.Value.Tracks[2] });
        // Selection intact.
        Assert.Equal(3, sel.SelectedCount);
        Assert.True(sel.IsSelected(2) && sel.IsSelected(3) && sel.IsSelected(4));
    }

    [Fact]
    public void RightClick_OutsideSelection_CollapsesToClickedRow()
    {
        var ten = Ten();
        var sel = NewSel();
        sel.SelectRange(2, 4);

        var target = Track.TargetResolver.Resolve(sel, At(ten), itemIndex: 7, host: null);

        Assert.NotNull(target);
        Assert.Equal(1, target!.Value.Count);
        Assert.Equal(ten[7], target.Value.Tracks[0]);
        // Explorer: the selection re-anchored to the clicked row.
        Assert.Equal(1, sel.SelectedCount);
        Assert.True(sel.IsSelected(7));
        Assert.False(sel.IsSelected(3));
    }

    [Fact]
    public void RightClick_OnUnselectedRow_WithNoSelection_SelectsIt()
    {
        var ten = Ten();
        var sel = NewSel();
        var target = Track.TargetResolver.Resolve(sel, At(ten), itemIndex: 5, host: null);

        Assert.Equal(ten[5], target!.Value.Tracks[0]);
        Assert.True(sel.IsSelected(5));
        Assert.Equal(1, sel.SelectedCount);
    }

    [Fact]
    public void TrackStartOffset_NonTrackRows_YieldNoMenu_AndOffsetRowsResolve()
    {
        // A vertical-layout list: item 0/1 are hero/chrome (no track), tracks start at item 2 (trackStart = 2).
        var ten = Ten();
        const int trackStart = 2;
        var sel = NewSel(count: 12);
        Track OffsetAt(int i) { int d = i - trackStart; return (uint)d < (uint)ten.Length ? ten[d] : default; }

        // A right-click on the hero row resolves no track → no menu.
        Assert.Null(Track.TargetResolver.Resolve(sel, OffsetAt, itemIndex: 0, host: null));

        // Item 5 = display 3 → t3.
        var target = Track.TargetResolver.Resolve(sel, OffsetAt, itemIndex: 5, host: null);
        Assert.Equal(ten[3], target!.Value.Tracks[0]);
        Assert.True(sel.IsSelected(5));   // selection lives in ITEM index space
    }

    [Fact]
    public void MultiSelection_SkipsNonTrackRows_InTargetCount()
    {
        // Rows past the track window (e.g. recommendation slots) resolve no track → excluded from the target.
        var ten = Ten();
        var sel = NewSel(count: 12);
        sel.SelectRange(8, 11);   // 8, 9 are tracks; 10, 11 are not (At returns default past index 9)

        var target = Track.TargetResolver.Resolve(sel, At(ten), itemIndex: 9, host: null);

        Assert.Equal(2, target!.Value.Count);
        Assert.Equal(new[] { ten[8], ten[9] }, new[] { target.Value.Tracks[0], target.Value.Tracks[1] });
    }

    [Fact]
    public void HostRuns_AfterSelectionSettles_AndCarriesDisplayOrderedRows()
    {
        var ten = Ten();
        var at = At(ten);
        var sel = NewSel();
        sel.SelectRange(2, 4);
        var playlist = EntityUri.Parse("spotify:playlist:p");
        const PlaylistCaps caps = PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.CanEditMetadata
                                | PlaylistCaps.IsOwner;

        // The table's host contract: map the CURRENT selection (post-settle) to original-index rows.
        PlaylistHost Host()
        {
            var rows = new List<int>();
            for (int i = 0; i < sel.ItemCount; i++)
                if (sel.IsSelected(i) && at(i).IsValid)
                    rows.Add(i);
            return rows.Count == 0 ? PlaylistHost.None : new PlaylistHost(playlist, caps, rows);
        }

        // Collapse case: right-click OUTSIDE the selection — the host must see the COLLAPSED selection (row 7 only).
        var target = Track.TargetResolver.Resolve(sel, at, itemIndex: 7, Host);

        Assert.True(target!.Value.Host.IsSome);
        var hostRows = target.Value.Host.Rows;
        Assert.Single(hostRows);
        Assert.Equal(7, hostRows[0]);
        Assert.Equal(ten[7], target.Value.Tracks[0]);

        // Multi case: right-click INSIDE the (new) selection after extending it.
        sel.SelectRange(1, 2);   // now {1, 2, 7}
        var multi = Track.TargetResolver.Resolve(sel, at, itemIndex: 2, Host);
        Assert.Equal(3, multi!.Value.Count);
        var rows2 = multi.Value.Host.Rows;
        Assert.Equal(3, rows2.Count);
        Assert.Equal(new[] { 1, 2, 7 }, new[] { rows2[0], rows2[1], rows2[2] });   // display order
    }

    [Fact]
    public void RemoveGate_ComposesWithResolvedHost()
    {
        // The resolver + rules together: an editable host with rows enables Remove-from-this-playlist.
        var ten = Ten();
        var sel = NewSel();
        var playlist = EntityUri.Parse("spotify:playlist:p");
        const PlaylistCaps caps = PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.CanEditMetadata
                                | PlaylistCaps.IsOwner;
        var target = Track.TargetResolver.Resolve(sel, At(ten), 4, () => new PlaylistHost(playlist, caps, [4]));
        Assert.True(ActionRules.CanRemoveFromPlaylist(target!.Value.Host));
    }
}
