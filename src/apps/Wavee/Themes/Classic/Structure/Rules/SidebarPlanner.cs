// ── Themes/Classic/Structure/Rules/SidebarPlanner.cs — Classic's row planner, row diff, analytic extents and mid-drag freeze ─
//
// Moved out of core by T5a (themes plan 5.1 X25/X26, 6.1; BP P5), from `Shell/Sidebar.Planner.cs` (the planner), `Shell/Sidebar.cs`
// (`SidebarRowExtents`, `SidebarStageHold`) and `Shell/Sidebar.UI.cs` (`PlanDiff.RowChanged` / `Changed`).
// The pane (core) owns WHEN a plan is built, published and held (the binder's revision, the drag session, the disclosure
// channel); WHAT the rows are, which of them re-render, how tall they are before anything measures, and that a re-projection
// arriving mid-gesture is parked until the session ends, are the structure's. The pane reaches all of it through
// `NavRowKit` (`ClassicNavRows`), so a theme with another list plans another way and core never names this file.
//
//   PLANNER      `SidebarRowPlanner`   (layout document x projection) -> ONE flat row list, for the expanded pane and the rail.
//                                      Pure: no engine type, no service, no clock. A row's Key is always an existing string, so
//                                      planning allocates no string; the row and entry lists are the caller's `SidebarPlanBuffers`
//                                      and ALIAS.
//   OWNING       `SidebarRowPlanner.OwningSection`  which section would hold a route if it were expanded (X26: the layout branch
//                                      that used to sit in the "one renderer" is the planner's, next to `PlanLibrary`).
//   DIFF         `SidebarPlanDiff`     which realized rows a republish must re-skin.
//   EXTENTS      `SidebarRowExtents`   the analytic row extent that seeds the virtualizing host's extent table.
//   FREEZE       `SidebarStageHold<T>` the mid-drag parking bay: no row jumps while a list is frozen during a drag.

using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

public static class SidebarRowPlanner
{
    /// <summary>The one finite guard on a projected section (a 10k library plans in full).</summary>
    public const int DynamicSectionRowCap = 20_000;
    const int SkeletonRows = 3;
    /// <summary>The grid view's pending tiles (S10, board `C-Sidebar` state loading): eight skeleton tiles, in strips of the
    /// planned column count, so the grid reads as loading at its real cell size instead of an empty pane.</summary>
    const int SkeletonGridTiles = 8;

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
            if (!HasRows(s, in input)) return;
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
    static bool HasRows(SidebarSection s, in SidebarProjectionInput input) => s.Kind switch
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
        // A FAILED rootlist keeps the skeleton here: the Classic layout has no failure voice of its own (the board draws it
        // only in Your Library's head), and an Empty row would claim "no playlists".
        if (input.TreeState != SidebarSourceState.Ready && (tree is null || tree.Count == 0))
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
            // Nothing published yet: the list view shows its skeleton rows, the grid its skeleton tiles at the real cell
            // size (S10). Loaded rows never take this branch, so a pending edge never blanks them. A FAILED source
            // (`SidebarSourceState.Error`) plans neither: the head draws the failure.
            if (st.Compact) return;
            if (options.View == SidebarLibraryView.List) EmitSkeletons(lib, ref st);
            else EmitGridSkeletons(lib, o.GridColumns, ref st);
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
            int cols = System.Math.Clamp(o.GridColumns, SidebarGeometry.GridMinColumns, SidebarGeometry.GridMaxColumns);
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

    // ── which section holds a route (X26) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The first visible top-level section of <paramref name="doc"/> that would hold <paramref name="route"/> if it were
    /// expanded: Pinned for a pinned route, Collections for one of its pages, Playlists for a playlist or folder in the tree,
    /// and Your Library for an entity route under the Library layout. Null otherwise. The planner answers it because the
    /// planner is what knows which layouts exist and what each holds (X26 deleted the layout branch from the renderer).
    /// <paramref name="isPinned"/> asks the pin store about a pin id (null for a route that is no pin).</summary>
    public static string? OwningSection(SidebarLayoutDoc doc, in SidebarProjectionInput input, string route,
                                        System.Func<string?, bool> isPinned)
    {
        if (route.Length == 0) return null;
        if (doc.Find(SidebarSectionKind.Pinned) is { Hidden: false } pinned && isPinned(SidebarPinId.FromRoute(route))) return pinned.Id;
        if (doc.Find(SidebarSectionKind.Collections) is { Hidden: false } collections && ItemsHold(collections.Items, route))
            return collections.Id;
        if (doc.Find(SidebarSectionKind.Playlists) is { Hidden: false } playlists
            && input.PlaylistTree is { } tree && EntriesHold(tree, route)) return playlists.Id;
        if (doc.Layout == SidebarLayoutId.Library && doc.Find(SidebarSectionKind.Library) is { Hidden: false } library
            && input.Library is { } list && EntriesHold(list, route)) return library.Id;
        return null;
    }

    static bool ItemsHold(IReadOnlyList<string> items, string route)
    {
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i], route, System.StringComparison.Ordinal)) return true;
        return false;
    }

    static bool EntriesHold(IReadOnlyList<SidebarLibraryEntry> entries, string route)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (string.Equals(entry.Id, route, System.StringComparison.Ordinal) || SidebarRowResolve.EntrySelects(in entry, route)) return true;
        }
        return false;
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

    /// <summary>The grid's pending strips: <see cref="SkeletonGridTiles"/> tiles as Skeleton rows whose <c>ItemCount</c> is the
    /// strip's tile count (a list skeleton carries 0), in the planned column count, exactly like the strips that replace them.</summary>
    static void EmitGridSkeletons(SidebarSection s, int gridColumns, ref PlanState st)
    {
        int cols = System.Math.Clamp(gridColumns, SidebarGeometry.GridMinColumns, SidebarGeometry.GridMaxColumns);
        for (int i = 0; i < SkeletonGridTiles; i += cols)
            st.Rows.Add(new SidebarRow(SidebarRowKind.Skeleton, s.Id, 0, -1, System.Math.Min(cols, SkeletonGridTiles - i), s.Id));
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

/// <summary>THE PANE'S PUBLISH DIFF (W3-A2): which realized rows a republish must re-skin, and whether a plan moved at
/// all. The shape rule: a row is new at its slot, its record differs, or the entry it addresses differs — and the entry
/// compare is <see cref="SidebarEntriesShadow.SameEntry"/>, whose <c>MosaicTiles</c> leg is BY VALUE. The projection materialises a folder's (and a cover-less playlist's)
/// tile list fresh on every rebuild, so the record's compiler equality — a REFERENCE compare on that one member — read
/// every folder row and every mosaic row as changed on every re-plan that followed a rebuild: two slots, a disclosure
/// chevron and a selection pill re-rendered per publication with nothing about them moved (the <c>PaneSlot×2</c> /
/// <c>Chevron×1</c> / <c>SelectionPill×1</c> lines of the scroll census). Public so the rule is pinned by facts
/// (<c>SidebarWiringTests</c>); allocation-free. The ordered EDIT SCRIPT between two plans (<c>Sidebar.PlanDiff.Splices</c>) is
/// not a picture rule and stays core: the key-matched publish's motion reads it.</summary>
public static class SidebarPlanDiff
{
    /// <summary>Does row <paramref name="index"/> of the new plan render differently from the same slot of the old?</summary>
    public static bool RowChanged(
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
        IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries,
        int index)
    {
        if ((uint)index >= (uint)newRows.Count) return false;
        if (index >= oldRows.Count) return true;              // the row is new at this slot
        var row = newRows[index];
        if (!row.Equals(oldRows[index])) return true;
        int entry = row.EntryIndex;
        if (entry < 0) return false;                          // a header / divider / skeleton carries no entry
        bool inOld = entry < oldEntries.Count;
        bool inNew = entry < newEntries.Count;
        if (inOld != inNew) return true;
        return inNew && !SidebarEntriesShadow.SameEntry(oldEntries[entry], newEntries[entry]);
    }

    /// <summary>Did the plan move at all — a different row count, or any slot <see cref="RowChanged"/>? The rail's
    /// version gate: a re-plan that reproduced the same rail rows over the same entries bumps nothing.</summary>
    public static bool Changed(
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
        IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries)
    {
        if (oldRows.Count != newRows.Count) return true;
        for (int i = 0; i < newRows.Count; i++)
            if (RowChanged(oldRows, oldEntries, newRows, newEntries, i)) return true;
        return false;
    }
}

/// <summary>The pane's ANALYTIC ROW EXTENT — the height a planned row will occupy, computed from the plan alone (a
/// pure function of row kind × section display options × previous row kind). It SEEDS the virtualizing host's
/// extent table so a folder expand/collapse does not shuffle every row above and below it while unrealized rows
/// measure themselves for the first time — the geometry is right before anything is realized.
///
/// It is a SEED, not a substitute for measurement: a <c>GridStrip</c> (and a header's wrapping chip strip) cannot be
/// predicted exactly and report their best analytic guess; the measured seam corrects them on realize, exactly as
/// every row does today.</summary>
public static class SidebarRowExtents
{
    /// <summary>The analytic extent of plan row <paramref name="index"/>, read off the row's kind and its section's shape: one
    /// SLOT (row + its 2 + 2 margin) per item kind, 40 per header, 8 per separator. NaN for a grid strip (estimate, then
    /// correct on measure). A glyph row inside an entity section takes the SECTION's pitch, so a band never mixes two.</summary>
    public static float HeightOf(IReadOnlyList<SidebarRow> rows, int index, SidebarSection? section)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if ((uint)index >= (uint)rows.Count || section is null) return 0f;
        return rows[index].Kind switch
        {
            SidebarRowKind.SectionHeader => SidebarGeometry.HeaderHeight,
            SidebarRowKind.Divider => SidebarGeometry.SeparatorHeight,
            SidebarRowKind.IconRow or SidebarRowKind.SectionTile => SidebarGeometry.PitchOf(SidebarRowShape.Glyph),
            // A grid skeleton strip (ItemCount > 0) is measured like the strip it stands for.
            SidebarRowKind.Skeleton when rows[index].ItemCount > 0 => float.NaN,
            SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader or SidebarRowKind.Skeleton => SidebarGeometry.PitchOf(section.Shape),
            SidebarRowKind.Empty => SidebarGeometry.EmptyHintHeight + 2f * SidebarGeometry.RowMarginY,
            SidebarRowKind.DropBand => SidebarGeometry.PitchOf(SidebarRowShape.Glyph),
            SidebarRowKind.TreeEnd => SidebarGeometry.TreeEndHeight,
            _ => float.NaN,   // GridStrip: estimate, correct on measure
        };
    }
}

/// <summary>THE MID-DRAG PARKING BAY for a sidebar plan stage.
///
/// A rootlist organisation drag aims at the tree's ROWS. A re-projection that arrives mid-gesture (a dealer push
/// from another device, our own optimistic ack, a background revalidate) re-keys those rows under the pointer, and
/// the drop then lands somewhere the user did not aim. So the newest stage is HELD here instead of published, and
/// applied on SESSION END (drop, cancel and Escape alike).
///
/// LAST WRITER WINS: a burst of publishes during one gesture converges to the newest stage, which is the only one
/// worth applying. A stage that arrives AFTER the session ended is never held — <see cref="TryHold"/> says so, and
/// the caller publishes it normally.
///
/// Generic and engine-free so a test can drive the real state machine without knowing what a stage IS.</summary>
public sealed class SidebarStageHold<TStage> : INavStageHold<TStage> where TStage : class
{
    TStage? _held;

    /// <summary>Is a stage parked right now? Diagnostics and tests only — the pane never branches on it.</summary>
    public bool HasHeld => _held is not null;

    /// <summary>Hold <paramref name="stage"/> instead of publishing it, when <paramref name="sessionLive"/>.
    /// Returns TRUE when the stage was parked — the caller must NOT publish. Returns FALSE when no session is live,
    /// which is the race that matters: a stage produced after the drag ended publishes on the spot rather than
    /// waiting for a flush that will never come.</summary>
    public bool TryHold(bool sessionLive, TStage stage)
    {
        if (!sessionLive) return false;
        _held = stage;
        return true;
    }

    /// <summary>Hand back the parked stage EXACTLY ONCE. The bay is emptied before the caller publishes, so a
    /// publish that re-enters this type cannot flush the same stage twice.</summary>
    public bool TryFlush(out TStage? stage)
    {
        stage = _held;
        _held = null;
        return stage is not null;
    }

    /// <summary>Drop the parked stage without publishing it — the pane is going away (unmount), or a stage got
    /// published through another path and the parked one is now stale.</summary>
    public void Discard() => _held = null;
}
