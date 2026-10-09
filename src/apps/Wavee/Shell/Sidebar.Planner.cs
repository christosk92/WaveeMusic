// ── Shell/Sidebar.Planner.cs ───────────────────────────────────────────────────────────────────────────────────────
// (layout document × projection) → ONE flat row list, for the expanded pane and the compact rail alike
//
// Role: CORE
// Spec: sidebar-rework-implementation.md §P3.5 · design V.3, V.7, V.9, P.1, P.2a
//
// Pure: no engine type, no service, no clock. A row's Key is always an existing string (an entry's Id, a route key, a
// section id), so planning allocates no string; the row/entry lists are the caller's SidebarPlanBuffers and alias.

using System.Collections.Generic;

namespace Wavee;

/// <summary>What the plan is FOR, beyond the document and the projection.</summary>
/// <param name="Mode">Compact plans the rail: no headers, a collapsed section is one <see cref="SidebarRowKind.SectionTile"/>,
/// trees show their top level only (a folder is a flyout).</param>
/// <param name="PinDropArmed">A pinnable drag is live: an empty Pinned plans its drop band.</param>
/// <param name="Filter">Library's active chip.</param>
/// <param name="GridColumns">Library's grid column count (derived from the pane width by the caller).</param>
/// <param name="FoldersInline">Library: an expanded PINNED folder lists its children under it (the wide pane, not drilled).
/// False while the narrow/drawer pane drills into folders instead (its folder click drills, so nothing expands inline).
/// Classic ignores it.</param>
/// <param name="Drilled">Library: the list shows ONE folder level (the narrow pane or the drawer drilled in). That level is
/// the folder's children only: no pin rows, no "Drop here to pin" band, no Liked Songs row — today's V3 hides both while
/// drilled (<c>PinsBandVisible</c> / <c>LikedVisible</c> are <c>!Drilled</c>), and a pinned playlist inside the folder
/// would otherwise show twice (the shaped level does not skip pins while drilled). Classic ignores it.</param>
public readonly record struct SidebarPlanOptions(
    SidebarPaneMode Mode = SidebarPaneMode.Expanded,
    bool PinDropArmed = false,
    SidebarLibraryFilter Filter = SidebarLibraryFilter.None,
    int GridColumns = 2,
    bool FoldersInline = true,
    bool Drilled = false);

public static class SidebarRowPlanner
{
    /// <summary>The one finite guard on a projected section (a 10k library plans in full).</summary>
    public const int DynamicSectionRowCap = 20_000;
    const int SkeletonRows = 3;

    /// <summary>Deterministic: same inputs → identical plan.</summary>
    public static SidebarRowPlan Build(SidebarLayoutDoc doc, in SidebarProjectionInput input, in SidebarPlanOptions options,
        SidebarPlanBuffers? buffers = null)
    {
        System.ArgumentNullException.ThrowIfNull(doc);
        var st = Begin(buffers);
        st.Compact = options.Mode == SidebarPaneMode.Compact;
        st.Dedupe = SidebarVisibilityRules.DedupesPins(doc, st.Compact);
        if (doc.Layout == SidebarLayoutId.Library) PlanLibrary(doc, in input, in options, ref st);
        else
        {
            var sections = doc.Sections;
            for (int i = 0; i < sections.Count; i++) PlanSection(sections[i], in input, in options, ref st);
        }
        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    /// <summary>One section's body, expanded, top level only for trees — a compact section tile's flyout.</summary>
    public static SidebarRowPlan BuildSection(SidebarLayoutDoc doc, string sectionId, in SidebarProjectionInput input,
        SidebarPlanBuffers? buffers = null)
    {
        var st = Begin(buffers);
        st.Compact = true;   // flyout rows: top level only, no hints, no gutters
        st.Dedupe = SidebarVisibilityRules.DedupesPins(doc, compact: true);
        if (doc.Find(sectionId) is { } s) PlanBody(s, in input, default, ref st);
        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    // ── Classic ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One Classic section: a separator before every rendered section but the first, then its header (expanded)
    /// or nothing (compact), then its body — or, collapsed, only the header (expanded) / one tile (compact). A section with
    /// no rows is not rendered at all (D9), except Playlists, whose "+" is how content starts.</summary>
    static void PlanSection(SidebarSection s, in SidebarProjectionInput input, in SidebarPlanOptions o, ref PlanState st)
    {
        if (s.Hidden || s.Kind == SidebarSectionKind.Settings) return;          // Settings is the footer
        bool titled = s.Kind != SidebarSectionKind.Home;
        if (s.Collapsed && titled)
        {
            if (!HasRows(s, in input, in o, in st)) return;
            if (st.Rows.Count > 0) st.Rows.Add(Chrome(SidebarRowKind.Divider, s));
            st.Rows.Add(Chrome(st.Compact ? SidebarRowKind.SectionTile : SidebarRowKind.SectionHeader, s));
            return;
        }
        int rowMark = st.Rows.Count, entryMark = st.Entries.Count;
        if (rowMark > 0) st.Rows.Add(Chrome(SidebarRowKind.Divider, s));
        if (titled && !st.Compact) st.Rows.Add(Chrome(SidebarRowKind.SectionHeader, s));
        int bodyStart = st.Rows.Count;
        PlanBody(s, in input, in o, ref st);
        bool keepsEmpty = s.Kind == SidebarSectionKind.Playlists && !st.Compact;
        if (st.Rows.Count == bodyStart && !keepsEmpty) Truncate(ref st, rowMark, entryMark);
    }

    static void PlanBody(SidebarSection s, in SidebarProjectionInput input, in SidebarPlanOptions o, ref PlanState st)
    {
        switch (s.Kind)
        {
            case SidebarSectionKind.Home:
                st.Rows.Add(new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, SidebarCatalogue.HomeRoute));
                break;
            case SidebarSectionKind.Pinned: PlanPins(s, in input, in o, ref st); break;
            case SidebarSectionKind.Collections: PlanCollections(s, in input, ref st); break;
            case SidebarSectionKind.Playlists: PlanPlaylists(s, in input, ref st); break;
            case SidebarSectionKind.Recent: PlanFeed(s, input.Played, ref st); break;
            case SidebarSectionKind.NewReleases: PlanFeed(s, input.NewReleases, ref st); break;
        }
    }

    /// <summary>Would the section plan any body row? Cheap: it counts, it never plans (a collapsed 10k Playlists costs a
    /// branch, not a walk).</summary>
    static bool HasRows(SidebarSection s, in SidebarProjectionInput input, in SidebarPlanOptions o, in PlanState st) => s.Kind switch
    {
        SidebarSectionKind.Pinned => input.Pins is { Count: > 0 },
        SidebarSectionKind.Collections => s.Items.Count > 0,
        SidebarSectionKind.Playlists => true,
        SidebarSectionKind.Recent => input.Played is { Count: > 0 },
        SidebarSectionKind.NewReleases => input.NewReleases is { Count: > 0 },
        _ => true,
    };

    static void PlanPins(SidebarSection s, in SidebarProjectionInput input, in SidebarPlanOptions o, ref PlanState st)
    {
        int start = st.Entries.Count;
        var pins = input.Pins;
        if (pins is not null)
            for (int i = 0; i < pins.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
            {
                var pin = pins[i];
                int at = st.Entries.Count;
                st.Entries.Add(pin);
                st.Rows.Add(new SidebarRow(pin.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, s.Id, 0, at, 0, pin.Id));
                if (pin.IsFolder && !st.Compact && IsExpanded(in input, FolderId(in pin)))
                    AppendPinnedFolderChildren(s, in pin, start, in input, ref st);
            }
        // Empty Pinned is hidden by emptiness — except while a pinnable drag is live: then the dashed band appears here.
        if (st.Entries.Count == start && o.PinDropArmed && !st.Compact)
            st.Rows.Add(Chrome(SidebarRowKind.DropBand, s));
    }

    static void PlanCollections(SidebarSection s, in SidebarProjectionInput input, ref PlanState st)
    {
        var items = s.Items;
        for (int i = 0; i < items.Count; i++)
        {
            string key = items[i];
            // A pinned page is drawn in Pinned (and only there) while the dedupe applies.
            if (st.Dedupe && input.PinnedIds is { } pinned && SidebarPinId.FromRoute(key) is { } pinId && pinned.Contains(pinId))
                continue;
            st.Rows.Add(new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, key));
        }
    }

    static void PlanPlaylists(SidebarSection s, in SidebarProjectionInput input, ref PlanState st)
    {
        var tree = input.PlaylistTree;
        if (input.TreeState == SidebarSourceState.Pending && (tree is null || tree.Count == 0))
        {
            if (!st.Compact) EmitSkeletons(s, ref st);
            return;
        }
        if (tree is null || tree.Count == 0)
        {
            if (!st.Compact) st.Rows.Add(Chrome(SidebarRowKind.Empty, s));
            return;
        }
        string? search = Search(in input);
        int start = st.Rows.Count;
        if (search is not null) PlanFlatTree(s, tree, search, in input, ref st);
        else PlanTree(s, tree, in input, ref st);
        if (st.Rows.Count == start) { if (!st.Compact) st.Rows.Add(Chrome(SidebarRowKind.Empty, s)); return; }
        // The closing gutter: "top level, at the end" is a drop target (never in the rail, never under a search).
        if (!st.Compact && search is null) st.Rows.Add(Chrome(SidebarRowKind.TreeEnd, s));
    }

    /// <summary>The rootlist in its own order, folders honouring the shared expansion set; a pinned subtree is skipped
    /// while the dedupe applies; the rail keeps the top level only.</summary>
    static void PlanTree(SidebarSection s, IReadOnlyList<SidebarLibraryEntry> tree, in SidebarProjectionInput input, ref PlanState st)
    {
        int emitted = 0;
        int hidePinDepth = -1;
        for (int i = 0; i < tree.Count && emitted < DynamicSectionRowCap; i++)
        {
            var e = tree[i];
            if (hidePinDepth >= 0)
            {
                if (e.Depth > hidePinDepth) continue;
                hidePinDepth = -1;
            }
            if (HiddenByPin(in input, in st, in e))
            {
                if (e.IsFolder) hidePinDepth = e.Depth;
                continue;
            }
            if (st.Compact && e.Depth > 0) continue;
            int at = st.Entries.Count;
            st.Entries.Add(e);
            byte d = (byte)System.Math.Min(e.Depth, byte.MaxValue);
            if (e.IsFolder)
            {
                st.Rows.Add(new SidebarRow(SidebarRowKind.FolderHeader, s.Id, d, at, 0, e.Id));
                emitted++;
                if (st.Compact || !IsExpanded(in input, e.FolderId))
                {
                    int myDepth = e.Depth;
                    while (i + 1 < tree.Count && tree[i + 1].Depth > myDepth) i++;
                }
                continue;
            }
            st.Rows.Add(new SidebarRow(SidebarRowKind.EntityRow, s.Id, d, at, 0, e.Id));
            emitted++;
        }
    }

    /// <summary>Classic's Ctrl+F filter flattens: matching playlists only, no folder chrome, rootlist order.</summary>
    static void PlanFlatTree(SidebarSection s, IReadOnlyList<SidebarLibraryEntry> tree, string search,
        in SidebarProjectionInput input, ref PlanState st)
    {
        int hidePinDepth = -1;
        for (int i = 0; i < tree.Count && st.Rows.Count < DynamicSectionRowCap; i++)
        {
            var e = tree[i];
            if (hidePinDepth >= 0)
            {
                if (e.Depth > hidePinDepth) continue;
                hidePinDepth = -1;
            }
            if (HiddenByPin(in input, in st, in e))
            {
                if (e.IsFolder) hidePinDepth = e.Depth;
                continue;
            }
            if (e.IsFolder || !SidebarSearch.Matches(in e, search)) continue;
            int at = st.Entries.Count;
            st.Entries.Add(e);
            st.Rows.Add(new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, at, 0, e.Id));
        }
    }

    static void PlanFeed(SidebarSection s, IReadOnlyList<SidebarLibraryEntry>? src, ref PlanState st)
    {
        if (src is null) return;
        int limit = s.Limit > 0 ? s.Limit : int.MaxValue;
        for (int i = 0; i < src.Count && i < limit; i++)
        {
            int at = st.Entries.Count;
            st.Entries.Add(src[i]);
            st.Rows.Add(new SidebarRow(src[i].IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, s.Id, 0, at, 0, src[i].Id));
        }
    }

    // ── Library (design P.2a) ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Your Library's scroller: the pins (no header, a pin mark on each row), then Liked Songs (no chip or the
    /// Playlists chip), then the mode-shaped list (List rows or Grid strips). The head (Home, dropdown, chips, toolbar) is
    /// fixed chrome above the list and is not planned.</summary>
    static void PlanLibrary(SidebarLayoutDoc doc, in SidebarProjectionInput input, in SidebarPlanOptions o, ref PlanState st)
    {
        var lib = doc.Find(SidebarSectionKind.Library);
        if (lib is null) return;
        var pinned = doc.Find(SidebarSectionKind.Pinned);
        var options = doc.Library;
        string? search = Search(in input);

        int pinRows = 0;
        int pinStart = st.Entries.Count;
        // A drilled level is ONE folder's children: no pins, no drop band, no Liked row (o.Drilled; ShowsLiked reads it).
        if (pinned is { Hidden: false } && !o.Drilled && input.Pins is { } pins)
            for (int i = 0; i < pins.Count && pinRows < DynamicSectionRowCap; i++)
            {
                var pin = pins[i];
                if (!SidebarVisibilityRules.ShowsPinInLibrary(in pin, o.Filter, search, options.HiddenKinds)) continue;
                int at = st.Entries.Count;
                st.Entries.Add(pin);
                st.Rows.Add(new SidebarRow(pin.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, pinned.Id, 0, at, 0, pin.Id));
                pinRows++;
                // The wide pane opens a pinned folder IN PLACE, exactly as Classic's Pinned does (its subtree is deduped
                // out of the list below, so this is the only place its children can show). Not in the rail, not under a
                // search (a search flattens), not while the narrow pane drills (its folder click drills instead).
                if (pin.IsFolder && !st.Compact && o.FoldersInline && search is null && IsExpanded(in input, FolderId(in pin)))
                    AppendPinnedFolderChildren(pinned, in pin, pinStart, in input, ref st);
            }
        if (pinRows == 0 && pinned is { Hidden: false } && !o.Drilled && o.PinDropArmed && !st.Compact)
            st.Rows.Add(Chrome(SidebarRowKind.DropBand, pinned));

        bool liked = SidebarVisibilityRules.ShowsLiked(options, o.Filter, search, input.LikedTitle, o.Drilled);
        if (liked)
        {
            int at = st.Entries.Count;
            st.Entries.Add(SidebarLibraryEntry.ForRoute(SidebarCatalogue.LikedRoute, input.LikedTitle ?? ""));
            st.Rows.Add(new SidebarRow(SidebarRowKind.EntityRow, lib.Id, 0, at, 0, SidebarCatalogue.LikedRoute));
        }

        var list = input.Library;
        if (input.LibraryState == SidebarSourceState.Pending && (list is null || list.Count == 0))
        {
            // Nothing published yet: the list view shows its skeletons; the grid shows nothing (no grey placeholder
            // tiles). Loaded rows never take this branch, so a pending edge never blanks them.
            if (!st.Compact && options.View == SidebarLibraryView.List) EmitSkeletons(lib, ref st);
            return;
        }
        int start = st.Entries.Count;
        if (list is not null)
            for (int i = 0; i < list.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
            {
                var e = list[i];
                if (st.Compact && e.Depth > 0) continue;   // the rail tiles the top level; a folder tile opens a flyout
                st.Entries.Add(e);
            }
        int count = st.Entries.Count - start;
        // No Empty row in Library: the head owns the empty state (V3Chrome's band in P3-P4; from P5 LibraryHead's, which
        // counts exactly the rows planned here through SidebarLibraryEmptyRules.ScrollerRows). Two owners drew two
        // contradicting messages ("No albums" over "your library is empty"; "empty" above Liked Songs).
        if (count == 0) return;
        if (options.View == SidebarLibraryView.Grid && !st.Compact)
        {
            int cols = System.Math.Clamp(o.GridColumns, 2, 4);
            for (int i = 0; i < count; i += cols)
                st.Rows.Add(new SidebarRow(SidebarRowKind.GridStrip, lib.Id, 0, start + i, System.Math.Min(cols, count - i), lib.Id));
            return;
        }
        for (int i = 0; i < count; i++)
        {
            var e = st.Entries[start + i];
            st.Rows.Add(new SidebarRow(e.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, lib.Id,
                (byte)System.Math.Min(e.Depth, byte.MaxValue), start + i, 0, e.Id));
        }
        // Custom order IS the rootlist: its end gutter takes "move to the end" drops.
        if (!st.Compact && input.LibraryIsTree && options.Sort == SidebarLibrarySort.CustomOrder
            && o.Filter is SidebarLibraryFilter.None or SidebarLibraryFilter.Playlists && search is null)
            st.Rows.Add(Chrome(SidebarRowKind.TreeEnd, lib));
    }

    // ── shared plumbing ───────────────────────────────────────────────────────────────────────────────────────────────

    struct PlanState
    {
        public List<SidebarRow> Rows;
        public List<SidebarLibraryEntry> Entries;
        public bool Compact;
        public bool Dedupe;
    }

    static PlanState Begin(SidebarPlanBuffers? buffers)
    {
        var rows = buffers?.Rows ?? new List<SidebarRow>(64);
        var entries = buffers?.Entries ?? new List<SidebarLibraryEntry>(64);
        rows.Clear();
        entries.Clear();
        return new PlanState { Rows = rows, Entries = entries };
    }

    static void Truncate(ref PlanState st, int rowMark, int entryMark)
    {
        if (st.Rows.Count > rowMark) st.Rows.RemoveRange(rowMark, st.Rows.Count - rowMark);
        if (st.Entries.Count > entryMark) st.Entries.RemoveRange(entryMark, st.Entries.Count - entryMark);
    }

    static SidebarRow Chrome(SidebarRowKind kind, SidebarSection s) => new(kind, s.Id, 0, -1, 0, s.Id);

    static void EmitSkeletons(SidebarSection s, ref PlanState st)
    {
        for (int i = 0; i < SkeletonRows; i++) st.Rows.Add(Chrome(SidebarRowKind.Skeleton, s));
    }

    /// <summary>Expand one pinned folder against the canonical flattened rootlist (unchanged rule: descendants keep their
    /// depth RELATIVE to the pinned folder; nested disclosures obey the shared expansion set).</summary>
    static void AppendPinnedFolderChildren(SidebarSection s, in SidebarLibraryEntry pin, int sectionStart,
        in SidebarProjectionInput input, ref PlanState st)
    {
        var tree = input.PlaylistTree;
        if (tree is null || tree.Count == 0) return;
        string folderId = FolderId(in pin);
        int root = -1;
        for (int i = 0; i < tree.Count; i++)
        {
            var c = tree[i];
            if (!c.IsFolder) continue;
            if (string.Equals(c.Id, pin.Id, System.StringComparison.Ordinal)
                || string.Equals(FolderId(in c), folderId, System.StringComparison.Ordinal)) { root = i; break; }
        }
        if (root < 0) return;
        int rootDepth = tree[root].Depth;
        for (int i = root + 1; i < tree.Count && st.Entries.Count - sectionStart < DynamicSectionRowCap; i++)
        {
            var child = tree[i];
            if (child.Depth <= rootDepth) break;
            int at = st.Entries.Count;
            st.Entries.Add(child);
            byte rowDepth = (byte)System.Math.Min(System.Math.Max(1, child.Depth - rootDepth), byte.MaxValue);
            st.Rows.Add(new SidebarRow(child.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow, s.Id, rowDepth, at, 0, child.Id));
            if (child.IsFolder && !IsExpanded(in input, FolderId(in child)))
            {
                int collapsedDepth = child.Depth;
                while (i + 1 < tree.Count && tree[i + 1].Depth > collapsedDepth) i++;
            }
        }
    }

    static bool IsExpanded(in SidebarProjectionInput input, string folderId)
        => input.ExpandedFolders is null || input.ExpandedFolders.Contains(folderId);

    static bool HiddenByPin(in SidebarProjectionInput input, in PlanState st, in SidebarLibraryEntry e)
        => st.Dedupe && (e.IsPinned || (input.PinnedIds is { } p && p.Contains(e.Id)));

    static string FolderId(in SidebarLibraryEntry entry)
        => entry.FolderId.Length > 0 ? entry.FolderId : SidebarPinId.FolderIdOf(entry.Id);

    static string? Search(in SidebarProjectionInput input)
    {
        var q = SidebarSearch.Normalize(input.Search);
        return q.Length == 0 ? null : q;
    }
}
