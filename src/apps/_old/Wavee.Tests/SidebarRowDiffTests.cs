using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

// SidebarRowDiff decides which of the pane's per-row epochs a publish bumps, i.e. which realized rows re-render. It is
// the engine-free half of that mechanism, split under Features/Sidebar/Data/ precisely so it could be pinned here.
//
// Two directions matter, and they fail differently:
//   - too EAGER is only a perf regression (the old pane-wide version bumped everything, every publish);
//   - too LAZY is a correctness bug — a realized row keeps drawing the previous plan's content. The entry cases below
//     are that risk in concrete form: a row addresses its entry by INDEX, so a library refresh can leave every row
//     record byte-identical while the entry behind it gained a name, a cover or a child count.
public sealed class SidebarRowDiffTests
{
    static SidebarRow Row(string key, int entryIndex = -1, string section = "s",
                          SidebarRowKind kind = SidebarRowKind.EntityRow)
        => new(kind, section, 0, entryIndex, 0, key);

    static SidebarLibraryEntry Entry(string id, string name = "n", string uri = "spotify:playlist:" + "x")
        => new(id, SidebarEntryKind.Playlist, uri, name, "", null, null, 0, 0, 0, 0, 0, 0, false,
               SidebarPlaylistFlavor.None);

    static readonly IReadOnlyList<SidebarLibraryEntry> NoEntries = Array.Empty<SidebarLibraryEntry>();

    [Fact]
    public void IdenticalPlans_ChangeNothing()
    {
        var rows = new[] { Row("a"), Row("b"), Row("c") };
        var next = new[] { Row("a"), Row("b"), Row("c") };
        Span<bool> changed = new bool[3];
        SidebarRowDiff.Diff(rows, NoEntries, next, NoEntries, changed);
        Assert.Equal(new[] { false, false, false }, changed.ToArray());
    }

    [Fact]
    public void OnlyTheRowsWhoseRecordMoved_AreMarked()
    {
        var rows = new[] { Row("a"), Row("b"), Row("c") };
        var next = new[] { Row("a"), Row("B!"), Row("c") };
        Span<bool> changed = new bool[3];
        SidebarRowDiff.Diff(rows, NoEntries, next, NoEntries, changed);
        Assert.Equal(new[] { false, true, false }, changed.ToArray());
    }

    [Fact]
    public void RowsBeyondTheOldPlan_AreAlwaysMarked()
    {
        var rows = new[] { Row("a") };
        var next = new[] { Row("a"), Row("b") };
        Span<bool> changed = new bool[2];
        SidebarRowDiff.Diff(rows, NoEntries, next, NoEntries, changed);
        Assert.Equal(new[] { false, true }, changed.ToArray());
    }

    [Fact]
    public void AShrunkPlan_OnlyReportsTheRowsItStillHas()
    {
        var rows = new[] { Row("a"), Row("b"), Row("c") };
        var next = new[] { Row("a") };
        Span<bool> changed = new bool[3];
        changed[1] = changed[2] = false;
        SidebarRowDiff.Diff(rows, NoEntries, next, NoEntries, changed);
        Assert.False(changed[0]);
        // Indices past the new plan are left alone — the pane never bumps an epoch no slot can legally address.
        Assert.False(changed[1]);
        Assert.False(changed[2]);
    }

    // The regression this type exists for: same row records, different entity behind one of them.
    [Fact]
    public void AChangedEntry_MarksTheRowThatAddressesIt_EvenWithAnIdenticalRowRecord()
    {
        var rows = new[] { Row("a", entryIndex: 0), Row("b", entryIndex: 1) };
        var oldEntries = new[] { Entry("p1", "Old name"), Entry("p2") };
        var newEntries = new[] { Entry("p1", "New name"), Entry("p2") };
        Span<bool> changed = new bool[2];
        SidebarRowDiff.Diff(rows, oldEntries, rows, newEntries, changed);
        Assert.Equal(new[] { true, false }, changed.ToArray());
    }

    [Fact]
    public void AnEntryIndexThatBecomesOutOfRange_MarksTheRow()
    {
        var rows = new[] { Row("a", entryIndex: 1) };
        var oldEntries = new[] { Entry("p1"), Entry("p2") };
        var newEntries = new[] { Entry("p1") };
        Span<bool> changed = new bool[1];
        SidebarRowDiff.Diff(rows, oldEntries, rows, newEntries, changed);
        Assert.True(changed[0]);
    }

    [Fact]
    public void EntrylessRows_IgnoreTheEntryListEntirely()
    {
        // A header/divider/skeleton carries EntryIndex -1: nothing behind it can go stale, so a wholesale entry-list
        // churn must not re-render it.
        var rows = new[] { Row("h", entryIndex: -1, kind: SidebarRowKind.SectionHeader) };
        Span<bool> changed = new bool[1];
        SidebarRowDiff.Diff(rows, new[] { Entry("p1") }, rows, new[] { Entry("p9", "different") }, changed);
        Assert.False(changed[0]);
    }

    [Fact]
    public void RowChanged_IsFalseOutsideTheNewPlan()
        => Assert.False(SidebarRowDiff.RowChanged(
            new[] { Row("a") }, NoEntries, new[] { Row("a") }, NoEntries, index: 5));
}
