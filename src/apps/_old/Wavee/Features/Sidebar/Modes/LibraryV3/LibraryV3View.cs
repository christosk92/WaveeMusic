using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// R3.0.3 — the V3 content ORDER, as one pure pass over the published projection.
///
/// <para>WHY THIS LAYER EXISTS AT ALL (it is the one thing the retired <c>LibraryV3Index</c> was genuinely for). The
/// published projection (<c>SidebarPreferences.Entries</c>) is filtered, sorted and pins-first — but the sort is FLAT
/// (<c>SidebarSort.Apply</c> sorts the whole list by one comparator), so a nested playlist can land ABOVE the folder that
/// contains it. §3.2.7 needs the opposite shape: folders ordered among their siblings by the active sort, each folder's
/// children ordered by the same sort WITHIN the folder. That re-grouping is a display concern — the projection must stay
/// one flat sortable band for the Curated planner and the feeds — so it lives here, and the result is handed to the ONE
/// pane renderer as the planner's tree slice (see <c>LibraryV3Sidebar.ShapeInput</c>).</para>
///
/// <para>It also owns Revision 2's DRILL LEVEL: at a drilled-in level the view is exactly one folder's direct children,
/// flattened to depth 0 — the wide-inline vs narrow-drill decision is therefore a BUILD INPUT, never a renderer branch.</para>
///
/// <para>WHAT IT DOES NOT DO: filter, sort, search, or decide what is pinned. All of that already happened in
/// <c>SidebarBinderPipeline.Shape</c>, and duplicating any of it here would be the fork this unification exists to remove.
/// A collapsed folder's children are likewise ALREADY absent from the published list (the binder projects with
/// <c>isFolderExpanded</c>), which is why this pass needs no expansion predicate.</para>
///
/// <para>ENGINE-FREE (System + Wavee.Core + the Data-layer entry record) and source-included by src/apps/Wavee.Tests, so
/// LibraryV3ViewTests drives the real grouping/drill/order rules.</para>
///
/// <para>ALLOCATION: one output <c>List</c> plus pooled per-folder buckets, reused across rebuilds. A rebuild happens once
/// per plan (a projection publish, a state change, a drill push/pop) — never per frame and never per row.</para>
/// </summary>
sealed class LibraryV3View
{
    /// <summary>Hard recursion guard for the inline folder walk: the data tree is unbounded and a cyclic or absurd tree
    /// must not be able to stack-overflow the UI thread. <see cref="SidebarTree.MaxDepth"/> is the projection's own limit.</summary>
    public const int MaxDepth = SidebarTree.MaxDepth;

    readonly List<SidebarLibraryEntry> _rows = new(256);

    // folder id → parent folder id ("" at top level), walked off the binder's FULLY flattened tree slice. A projected entry
    // knows its CONTAINING folder (FolderId) for every kind EXCEPT a folder row, whose FolderId is its OWN id — so a
    // folder's parent is only recoverable from the tree walk, and this map is that walk memoised on the binder revision.
    readonly Dictionary<string, string> _parentOfFolder = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<int>> _buckets = new(StringComparer.Ordinal);
    readonly List<List<int>> _bucketPool = new();
    readonly List<int> _top = new();
    readonly HashSet<string> _folderRows = new(StringComparer.Ordinal);
    string[]? _folderStack;
    int _parentRevision = int.MinValue;

    /// <summary>The built order — the rows the pane's planner turns into a plan, in exactly this sequence. Every entry
    /// carries a REWRITTEN <c>Depth</c> (its display indent) and <c>SourceOrder</c> (its position here), which is what lets
    /// the planner's <c>CustomOrder</c> comparator reproduce this order verbatim without a rank map.</summary>
    public IReadOnlyList<SidebarLibraryEntry> Rows => _rows;

    public int Count => _rows.Count;

    /// <summary>True when the drilled-into folder is no longer present (unfollowed, filtered away, library reloaded). The
    /// mode component pops the stack rather than showing a level whose breadcrumb points at nothing.
    /// <para>"Missing" means the FOLDER ROW is gone — NOT that it has no children: an empty folder is a legitimate level,
    /// and popping out of it would make an empty folder impossible to open.</para></summary>
    public bool DrillTargetMissing { get; private set; }

    /// <summary>
    /// Rebuild the order.
    /// </summary>
    /// <param name="published">The shaped projection (<c>SidebarEntries.Current</c>).</param>
    /// <param name="skip">How many LEADING entries to drop — the pin band, when it is rendered as its own section (pass 0
    /// when it is not, so the pins stay in the list exactly where the projection put them).</param>
    /// <param name="tree">The binder's fully flattened rootlist tree slice, for the folder→parent map. Null degrades every
    /// folder to top level rather than hiding rows.</param>
    /// <param name="treeRevision">The binder revision <paramref name="tree"/> came from — the parent map's memo key.</param>
    /// <param name="drillFolderId">The folder whose DIRECT children to list, or null/"" for the library root.</param>
    /// <param name="group">Whether to re-group into tree order. False = pass the slice through flat at depth 0, which is
    /// what a search (already flattened) and the grid views (which cannot express disclosure) want.</param>
    public void Build(IReadOnlyList<SidebarLibraryEntry>? published, int skip,
                      IReadOnlyList<SidebarLibraryEntry>? tree, int treeRevision,
                      string? drillFolderId, bool group)
    {
        _rows.Clear();
        DrillTargetMissing = false;
        if (published is null || published.Count == 0)
        {
            // A drill level whose projection is empty is not a MISSING target: the folder row simply is not there yet
            // (cold library). Reporting "missing" here would pop the stack on every cold start.
            ReleaseBuckets();
            _top.Clear();
            _folderRows.Clear();
            return;
        }

        int n = published.Count;
        if (skip < 0) skip = 0;
        if (skip > n) skip = n;

        bool drill = drillFolderId is { Length: > 0 };

        if (!drill && !group)
        {
            for (int i = skip; i < n; i++) Emit(published[i], 0);
            return;
        }

        EnsureParentMap(tree, treeRevision);
        // A drill level buckets EVERY row (a pinned playlist that lives inside the folder must still appear inside it, and
        // the pin band is not rendered at a drilled-in level); the root level buckets only the post-pin remainder.
        BuildBuckets(published, drill ? 0 : skip);

        if (drill)
        {
            DrillTargetMissing = !_folderRows.Contains(drillFolderId!);
            if (!DrillTargetMissing && _buckets.TryGetValue(drillFolderId!, out var kids))
                for (int i = 0; i < kids.Count; i++) Emit(published[kids[i]], 0);
            return;
        }

        EmitLevel(published, _top, 0);
    }

    /// <summary>The parent-folder id of a built row — the sibling band a custom-order drag may move WITHIN (§3.2.9's
    /// folder-boundary clamp). "" for a top-level row.</summary>
    public string ParentOf(int index)
        => (uint)index < (uint)_rows.Count ? ParentKey(_rows[index]) : "";

    /// <summary>Whether two built rows are siblings. A drop aimed across a folder boundary must not commit HERE: this
    /// overlay is V3's LOCAL custom order, and moving an item between folders is a rootlist write. The rootlist is
    /// written only through the resource-drop seam and <c>FolderActions</c>, never through the V3 overlay — so a
    /// cross-boundary drop that this accepted would show a tree the server never agreed to. (Folder CRUD itself is no
    /// longer locked; the old "locked decision 9" is lifted. What stays locked is the WRITER.)</summary>
    public bool SameParent(int a, int b)
        => string.Equals(ParentOf(a), ParentOf(b), StringComparison.Ordinal);

    /// <summary>D11 — the same boundary, applied DURING the gesture: the slot a drag from <paramref name="from"/> may
    /// actually reach when the pointer asks for <paramref name="to"/>.
    ///
    /// <para>A drop the overlay cannot honour used to animate all the way across a folder boundary and then silently not
    /// commit (<see cref="SameParent"/> bailing at the end). Snapping the REQUESTED slot to the nearest one inside the
    /// source's sibling run means the gap never opens across a boundary in the first place, so what the user sees is
    /// what commits, and the commit-time bail becomes the invariant rather than the feedback.</para>
    ///
    /// <para>The run is the SET of same-parent rows, not a contiguous span: an expanded folder's children sit between
    /// two top-level siblings, so a top-level drag has to be able to travel PAST them (nearest legal slot on either
    /// side) while a child drag stays boxed inside its folder (there is no legal slot outside it). Ties go to the lower
    /// slot — either is equally legal and the choice only has to be deterministic.</para></summary>
    public int ClampToSiblingRun(int from, int to)
    {
        int n = _rows.Count;
        if (n == 0 || (uint)from >= (uint)n) return to;
        if (to < 0) to = 0;
        else if (to >= n) to = n - 1;
        if (SameParent(from, to)) return to;

        string parent = ParentKey(_rows[from]);
        int below = -1, above = -1;
        for (int i = to - 1; i >= 0; i--)
            if (string.Equals(ParentKey(_rows[i]), parent, StringComparison.Ordinal)) { below = i; break; }
        for (int i = to + 1; i < n; i++)
            if (string.Equals(ParentKey(_rows[i]), parent, StringComparison.Ordinal)) { above = i; break; }
        // `from` is itself in the run, so at least one side always resolves; the fallback is a no-move.
        if (below < 0) return above < 0 ? from : above;
        if (above < 0) return below;
        return to - below <= above - to ? below : above;
    }

    /// <summary>The stable key (entry id) at a built index — what the pane's reorder band reports per slot, so a caller can
    /// verify that band slot n really is view row n before committing.</summary>
    public string KeyAt(int index) => (uint)index < (uint)_rows.Count ? _rows[index].Id : "";

    /// <summary>Materialize the ENTIRE visible order into <paramref name="into"/> as entry ids, with the row at
    /// <paramref name="from"/> moved to <paramref name="to"/>. §3.2.9's "on any user move the whole current visible order is
    /// written", which is what makes later appends stable without ever rewriting the overlay again (F.7.10).</summary>
    public void MaterializeOrder(List<string> into, int from, int to)
    {
        into.Clear();
        for (int i = 0; i < _rows.Count; i++)
        {
            int slot = MovedIndex(i, from, to);                    // which VIEW slot supplies row i after the move
            if ((uint)slot >= (uint)_rows.Count) continue;
            var e = _rows[slot];
            // An authored route row (Liked Songs) and a track row have no place in a playlist order.
            if (e.Kind is SidebarEntryKind.AppRoute or SidebarEntryKind.Track) continue;
            if (e.Id.Length > 0) into.Add(e.Id);
        }
    }

    // The permutation a single remove-at-from/insert-at-to applies, read backwards (which VIEW slot supplies row i).
    static int MovedIndex(int i, int from, int to)
    {
        if (from == to) return i;
        if (from < to)
        {
            if (i < from || i > to) return i;
            return i == to ? from : i + 1;
        }
        if (i < to || i > from) return i;
        return i == to ? from : i - 1;
    }

    // ── the rebuild ──────────────────────────────────────────────────────────────────────────────────────────────────

    // Depth-first emission of one sibling level: a folder row is followed by its children, which are present in the
    // projection only when the folder is expanded (the binder's own gate) — so an expansion test here would be a second,
    // driftable copy of that rule.
    void EmitLevel(IReadOnlyList<SidebarLibraryEntry> src, List<int> level, int depth)
    {
        for (int i = 0; i < level.Count; i++)
        {
            int at = level[i];
            var e = src[at];
            Emit(e, depth);
            if (depth >= MaxDepth || !e.IsFolder) continue;
            if (_buckets.TryGetValue(e.FolderId, out var kids)) EmitLevel(src, kids, depth + 1);
        }
    }

    void BuildBuckets(IReadOnlyList<SidebarLibraryEntry> src, int from)
    {
        _top.Clear();
        ReleaseBuckets();
        _folderRows.Clear();

        for (int i = from; i < src.Count; i++)
            if (src[i].IsFolder && src[i].FolderId.Length > 0) _folderRows.Add(src[i].FolderId);

        for (int i = from; i < src.Count; i++)
        {
            string parent = ParentKey(src[i]);
            // A row whose parent folder is NOT itself a visible row (the folder is pinned into the band, the lens dropped
            // folder rows, or the tree map is cold) is promoted to top level. Nothing is ever hidden because its container
            // happens to be elsewhere — that would silently lose playlists.
            if (parent.Length == 0 || !_folderRows.Contains(parent)) _top.Add(i);
            else Bucket(parent).Add(i);
        }
    }

    string ParentKey(in SidebarLibraryEntry e)
    {
        if (!e.IsFolder) return e.FolderId;
        return _parentOfFolder.TryGetValue(e.FolderId, out var p) ? p : "";
    }

    void Emit(in SidebarLibraryEntry e, int depth)
    {
        int d = depth < 0 ? 0 : depth > MaxDepth ? MaxDepth : depth;
        // Depth is the DISPLAY indent the pane's row planner stamps onto its rows; SourceOrder is this row's position,
        // which is what makes a re-sort by the planner's CustomOrder comparator (SourceOrder ascending) a no-op.
        _rows.Add(e with { Depth = d, SourceOrder = _rows.Count });
    }

    List<int> Bucket(string folderId)
    {
        if (_buckets.TryGetValue(folderId, out var list)) return list;
        if (_bucketPool.Count > 0)
        {
            list = _bucketPool[_bucketPool.Count - 1];
            _bucketPool.RemoveAt(_bucketPool.Count - 1);
            list.Clear();
        }
        else
        {
            list = new List<int>(8);
        }
        _buckets[folderId] = list;
        return list;
    }

    void ReleaseBuckets()
    {
        foreach (var kv in _buckets) _bucketPool.Add(kv.Value);
        _buckets.Clear();
    }

    // The folder→parent map, walked off the binder's tree slice (depth-first, pre-order, folders included, FULLY flattened
    // regardless of expansion — which is exactly why it can answer "who contains this folder" when the published list
    // cannot). Memoised on the binder revision: it only moves when the rootlist does.
    void EnsureParentMap(IReadOnlyList<SidebarLibraryEntry>? tree, int revision)
    {
        if (tree is null)
        {
            if (_parentRevision != int.MinValue) { _parentOfFolder.Clear(); _parentRevision = int.MinValue; }
            return;
        }
        if (revision == _parentRevision && _parentOfFolder.Count > 0) return;
        _parentRevision = revision;
        _parentOfFolder.Clear();

        var stack = _folderStack ??= NewStack();
        for (int i = 0; i < stack.Length; i++) stack[i] = "";
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            int d = e.Depth;
            if (d < 0 || d + 1 >= stack.Length) continue;
            if (!e.IsFolder || e.FolderId.Length == 0) continue;
            _parentOfFolder[e.FolderId] = stack[d];
            stack[d + 1] = e.FolderId;
        }
    }

    static string[] NewStack()
    {
        var s = new string[MaxDepth + 2];
        for (int i = 0; i < s.Length; i++) s[i] = "";
        return s;
    }
}

/// <summary>
/// A reusable WINDOW over a projection list — <c>[start, start+count)</c> without copying a single entry.
///
/// <para>It is how the mode component hands the pane its pin band: the shaped projection already leads with the surviving
/// pins (pin order, lens-aware), so the pin section's rows and the library section's rows are two windows over ONE list and
/// can never disagree about which pins survived the active filter. One instance is reused for the pane's life, because a
/// plan only reads it synchronously during <c>SidebarRowPlanner.Build</c>.</para>
/// </summary>
sealed class LibraryV3Window : IReadOnlyList<SidebarLibraryEntry>
{
    /// <summary>The out-of-range fallback. NOT <c>default(SidebarLibraryEntry)</c>: a default instance's positional string
    /// members are null, and a row built from one would hand <c>TextEl</c> a null label.</summary>
    static readonly SidebarLibraryEntry Blank = SidebarLibraryEntry.ForRoute("", "");

    IReadOnlyList<SidebarLibraryEntry>? _source;
    int _start;
    int _count;

    public void Set(IReadOnlyList<SidebarLibraryEntry>? source, int start, int count)
    {
        _source = source;
        int n = source?.Count ?? 0;
        if (start < 0) start = 0;
        if (start > n) start = n;
        if (count < 0) count = 0;
        if (start + count > n) count = n - start;
        _start = start;
        _count = count;
    }

    public int Count => _count;

    /// <summary>Bounds-checked against BOTH the window and the LIVE source: the projection publishes into one reused
    /// <c>List</c>, so a window taken before a rebuild that shrank the list must degrade to a blank row rather than throw.</summary>
    public SidebarLibraryEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count || _source is not { } src) return Blank;
            int at = _start + index;
            return (uint)at < (uint)src.Count ? src[at] : Blank;
        }
    }

    public IEnumerator<SidebarLibraryEntry> GetEnumerator()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
