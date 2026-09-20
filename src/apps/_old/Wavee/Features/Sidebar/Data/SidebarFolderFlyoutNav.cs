using System;
using System.Collections.Generic;

namespace Wavee;

// The PURE half of the collapsed rail's folder flyout (`Features/Sidebar/Pane/SidebarRailFolderFlyout.cs`).
//
// ENGINE-FREE BY CONSTRUCTION (System + the already-engine-free SidebarLibraryEntry), like the rest of `Data\`: this file
// is source-included by src/apps/Wavee.Tests, so `SidebarFolderFlyoutNavTests` drives the REAL drill-in rules instead of
// a copy of them. Nothing here may reference Signal<T>, Element, Icons, Loc or Tok.
//
// WHY A MODEL AND NOT JUST A SIGNAL. The concert date flyout (`ConcertDateFlyout`) drills in with one `Signal<int>` —
// it has exactly two levels and a fixed root, so an int IS the whole model. A folder flyout is unbounded: the stack has
// to remember the NAME of every level it came through (the back header names the level you return to), refuse a cycle,
// and mint a stable page key for the slide. Those are three rules with edge cases, so they live where a test can reach
// them and the component only renders what they decide.

/// <summary>Direct-child lookups over the flattened depth-first rootlist tree the projection publishes
/// (<c>SidebarProjectionInput.PlaylistTree</c>).
///
/// <para><b>Containment is <see cref="SidebarLibraryEntry.ParentFolderId"/>, and only that.</b> A row's
/// <c>FolderId</c> means two different things by kind — for a LEAF it is the folder it sits in, but for a FOLDER it is
/// that folder's OWN group id — so a lookup written against it silently makes every folder its own child.
/// <c>ParentFolderId</c> is the field the projection fills with "the folder I am inside" for both kinds
/// (<c>SidebarProjection.Walk</c>), which is exactly the question being asked here.</para>
///
/// <para><b>ONE containment definition.</b> Over the FULL flattened tree
/// (<c>includeFolderChildren: true</c> — what <c>SidebarProjectionInput.PlaylistTree</c> is), this predicate selects
/// exactly the folder's <c>PlaylistFolder.Items</c>, in the same order, which is also what the projection counts into
/// <c>SidebarLibraryEntry.ChildCount</c>. The flyout used to LIST rows from this scan while its rows' subtitles read
/// <c>ChildCount</c> — two definitions of "what is in this folder", and a folder that showed "0 items" over a list of
/// them. <see cref="ChildCount"/> is the count of THIS list, so the rows and the number cannot disagree; the
/// equivalence with the projection's own count is pinned by <c>SidebarFolderFlyoutNavTests</c>.</para></summary>
static class SidebarFolderTree
{
    /// <summary>Fill <paramref name="into"/> (CLEARED first) with the DIRECT children of <paramref name="folderId"/>, in
    /// rootlist order — sub-folders and leaves interleaved exactly as the tree carries them. Returns the count.
    /// An unknown / empty folder id yields zero rows rather than the whole tree.</summary>
    public static int Children(IReadOnlyList<SidebarLibraryEntry>? tree, string folderId,
                               List<SidebarLibraryEntry> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (tree is null || string.IsNullOrEmpty(folderId)) return 0;
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (string.Equals(e.ParentFolderId, folderId, StringComparison.Ordinal)) into.Add(e);
        }
        return into.Count;
    }

    /// <summary>How many DIRECT children <paramref name="folderId"/> has — the count of exactly the list
    /// <see cref="Children"/> fills, computed without one. Every "N items" the flyout renders comes from here.</summary>
    public static int ChildCount(IReadOnlyList<SidebarLibraryEntry>? tree, string folderId)
    {
        if (tree is null || string.IsNullOrEmpty(folderId)) return 0;
        int n = 0;
        for (int i = 0; i < tree.Count; i++)
            if (string.Equals(tree[i].ParentFolderId, folderId, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>The folder row whose OWN group id is <paramref name="folderId"/>.</summary>
    public static bool TryFolder(IReadOnlyList<SidebarLibraryEntry>? tree, string folderId,
                                 out SidebarLibraryEntry folder)
    {
        folder = default;
        if (tree is null || string.IsNullOrEmpty(folderId)) return false;
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (e.Kind != SidebarEntryKind.Folder || !string.Equals(e.FolderId, folderId, StringComparison.Ordinal))
                continue;
            folder = e;
            return true;
        }
        return false;
    }
}

/// <summary>The flyout's drill-in STACK — the same navigation shape the concert date flyout uses (one page at a time,
/// forward slides in, back mirrors), generalised to an unbounded folder chain.
///
/// <para>Mutable and NOT thread-safe by design: it is owned by one flyout component on the UI thread, exactly like the
/// concert flyout's view signal. Every mutator returns whether it CHANGED anything, so the component can bump its
/// render epoch only on a real move (and can pick the slide direction from the same answer).</para></summary>
sealed class SidebarFolderFlyoutNav
{
    /// <summary>One level of the stack. The name is carried, not re-resolved: the folder may be renamed or deleted while
    /// the flyout is open, and the back header must still name the level the user actually came through.</summary>
    public readonly record struct Level(string FolderId, string Name);

    readonly List<Level> _stack = new(4);

    public SidebarFolderFlyoutNav(string rootFolderId, string rootName)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootFolderId);
        _stack.Add(new Level(rootFolderId, rootName ?? ""));
    }

    /// <summary>How many levels deep the flyout is. 1 = the folder the rail tile opened.</summary>
    public int Depth => _stack.Count;

    /// <summary>True once at least one sub-folder has been pushed — the header's back chevron is present iff this is.</summary>
    public bool CanGoBack => _stack.Count > 1;

    /// <summary>The level being shown.</summary>
    public Level Current => _stack[_stack.Count - 1];

    /// <summary>The level <see cref="Pop"/> would return to, or the root when there is none (so a caller can announce
    /// "Back to {name}" without indexing the stack itself).</summary>
    public Level Parent => _stack[Math.Max(0, _stack.Count - 2)];

    /// <summary>Drill into a sub-folder. Refuses an empty id and refuses a folder ALREADY on the stack — a rootlist
    /// cycle cannot exist, but a stale projection mid-move can briefly describe one, and an unbounded push would then
    /// grow the stack until the user gave up on Back.</summary>
    public bool Push(string folderId, string name)
    {
        if (string.IsNullOrEmpty(folderId)) return false;
        for (int i = 0; i < _stack.Count; i++)
            if (string.Equals(_stack[i].FolderId, folderId, StringComparison.Ordinal)) return false;
        _stack.Add(new Level(folderId, name ?? ""));
        return true;
    }

    /// <summary>Back one level. False at the root — the caller decides what a back gesture means there (the landed
    /// answer is "nothing", so a stray Backspace cannot close a flyout the user is still reading).</summary>
    public bool Pop()
    {
        if (_stack.Count <= 1) return false;
        _stack.RemoveAt(_stack.Count - 1);
        return true;
    }

    /// <summary>The reconciler KEY for the level being shown. Depth is part of it deliberately: pushing A→B→A is
    /// refused, but popping to a level and drilling into a DIFFERENT folder must still read as a forward move, and two
    /// levels that happened to share an id would otherwise reconcile as one page and skip the slide.</summary>
    public string PageKey => Depth.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Current.FolderId;
}
