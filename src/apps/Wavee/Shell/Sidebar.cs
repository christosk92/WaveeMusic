// ── Shell/Sidebar.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// projection / binder / sources / geometry / drop / selection / pins — and every pure rule of the sidebar that is not a
// pixel and not a file. Pure, engine-free, allocation-free after warm-up, and source-visible to `Wavee.Tests` — which is
// the point: the sidebar's hard parts are its RULES (where a drop lands, which row is selected, how tall a band is, what
// order pins take in every sort mode), and a rule that lives inside a renderer cannot be pinned by a test.
//
// The sections, in file order:
//
//   CORE       `SidebarPaneBounds` — the expanded width's clamp pair and its default.
//   GEOMETRY   `SidebarRowGeometry`, `SidebarRowExtents` — the one height/indent/art/lane ladder. Art starts at pane
//              centre x = 24 and the label at x = 48 on EVERY row shape; one height per SECTION, never per row (a mixed
//              band breaks both the `Reorderable` slot pitch and the virtualizing host's extent table).
//   ROWS       `SidebarRowKind`, `SidebarRow`/`SidebarRowPlan`, `SidebarProjectionInput`, `SidebarPlanBuffers` — the
//              plan's vocabulary. The planner itself is `Sidebar.Planner.cs`: (document × projection) flattened into ONE
//              array of row kinds, rendered by ONE bound list. The buffers are caller-owned and ALIAS.
//   DIFF       `SidebarRowDiff`, `SidebarRowResolve`, `SidebarPillState` — which realized rows re-render, which row
//              draws selected, and the one lit/dark rule for the accent pill.
//   DROP       `RootlistSlotResolver` + `SidebarDropCue` + `RootlistDropDecision` + `RootlistTreeNav` — ONE resolver,
//              ONE published slot, ONE commit. Line ⟺ ordering, plate ⟺ Into, never both. Every refusal has a
//              sentence; a drop that cannot be honoured never returns silently.
//   SELECTION  `SidebarTreeSelection` — WinUI extended multi-select semantics, keyed by row ID because the tree
//              re-flows constantly.
//   PROJECT    `SidebarProjection`, `SidebarSort`, `SidebarSearch`, `SidebarBinderPipeline` — the library AS edges.
//              In 0.3 it is a read over `User.Me`'s edges, which is why there is no hydration step and no second copy
//              of a playlist's name.
//   LIBRARY    `SidebarLibraryFilters` lives with the layout (`Sidebar.Layout.cs`); the query and the shaping are above.
//   PINS       `SidebarPinId`, `PinSyncRules`, `PinRowRule` — the pin id IS the nav route key (A7).
//   DIAG       `SidebarPaneInvariant` — the settled-frame terminal-state validator.
//
// NOT here: the layout document and its rules (`Sidebar.Layout.cs`), the planner (`Sidebar.Planner.cs`), the stores
// and the binder pump (`Sidebar.Store.cs`, `Sidebar.Host.cs`), and every `Element` (`Sidebar.UI*.cs`).

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

// ── 0. the pane's own width bounds, and the CORE entry points ────────────────────────────────────────────────────────
//
// One clamp pair, owned HERE rather than by the shell, because every writer of the sidebar's expanded width (the
// splitter seam, the keyboard nudge, `Sidebar.Boot`, a diagnostics probe) must go through the same numbers
// or the pane gets persisted at a width it cannot render at. That drift is exactly what a second literal pair caused
// in 0.2.9. `Shell/Shell.cs` READS these; it does not redeclare them. The width BETWEEN the bounds is the user's: there
// is no responsive ladder (the window never moves the sidebar) — the rail's geometry and the resize rules live in
// `Sidebar.Resize.cs`.

/// <summary>The expanded sidebar column's bounds. Issue #84 lowered the floor from 240 to 180 so the pane can go
/// genuinely narrow; 460 is the ceiling. The collapsed rail is 48 DIP (`SidebarRowGeometry.RailWidth`), the same list
/// planned compact.</summary>
public static class SidebarPaneBounds
{
    /// <summary>THE clamp pair. Every writer goes through it.</summary>
    public const float NavPaneMinW = 180f, NavPaneMaxW = 460f;

    /// <summary>The width a fresh pane opens at, and the width Reset returns to.</summary>
    public const float DefaultWidth = 320f;

    /// <summary>The anti-flicker band for a threshold crossing (the regime flip's drag-back distance).</summary>
    public const float NavPaneHysteresisDip = 24f;

    /// <summary>The one clamp.</summary>
    public static float Clamp(float width)
        => width < NavPaneMinW ? NavPaneMinW : width > NavPaneMaxW ? NavPaneMaxW : width;
}

public static partial class Sidebar
{
    // ── the planner, named ───────────────────────────────────────────────────────────────────────────────────────────
    //
    // One thin name over `SidebarRowPlanner` so a caller never has to pick an overload. The buffers are the CALLER's and
    // they ALIAS: the plan points into them, so a caller keeps two and alternates — otherwise the outgoing rows die under
    // the diff that is still reading them.

    /// <summary>(document × projection) → ONE flat row array, for the expanded pane or the 48 rail.</summary>
    public static SidebarRowPlan Plan(SidebarLayoutDoc doc, in SidebarProjectionInput input, in SidebarPlanOptions options,
                                      SidebarPlanBuffers buffers)
        => SidebarRowPlanner.Build(doc, in input, in options, buffers);
}
// ── ROOTLIST MARKER STREAM + MOVE LEGALITY ─────────────────────────────────────────────────────────────────────────
// The marker-stream value shape (RootlistEntry) and the item/placement/move value types every sidebar drop, every
// "Move to folder…" destination and every Alt+↑/↓ verb is expressed against. CheckMove/CheckMoves are the ONE pure
// legality authority: "would this be accepted, and if not why" — never a builder, never a poster, never a store read.
// A UI surface that offers a destination a drag would refuse is a bug; both surfaces MUST call these same functions.

/// <summary>One rootlist target: a playlist uri, or a folder's groupId. <see cref="IsFolder"/> picks which.</summary>
public readonly record struct RootlistItemRef(string Key, bool IsFolder);

/// <summary>Where a dropped/moved item lands relative to its target.</summary>
public enum RootlistDropPlacement : byte { Before, After, Inside }

/// <summary>One element of a rootlist move batch: move <see cref="Source"/> to <see cref="Placement"/> of
/// <see cref="Target"/>. A batch is applied in list order.</summary>
public readonly record struct RootlistMove(RootlistItemRef Source, RootlistItemRef Target, RootlistDropPlacement Placement);

/// <summary>One row of the rootlist marker stream: a playlist uri (Kind 0), or a folder start/end marker (Kind 1/2).
/// This IS the marker stream the pure drop rules below speak — in 0.3 the same facts also live on the
/// <c>Edges.Rootlist</c> <c>RootlistEdge</c> payload, but <see cref="RootlistOps"/> works this flat value shape.
/// <paramref name="AddedAtMs"/> is the server ADD timestamp (unix ms; 0 = not captured): a folder rename re-sends the
/// marker's ORIGINAL create timestamp, so it must survive the round trip.</summary>
public readonly record struct RootlistEntry(int Position, int Kind, string Uri, string? GroupName, int Depth, long AddedAtMs = 0);

/// <summary>Why a rootlist move was refused — or that it wasn't. Each value is its own sentence upstream: these were
/// all one silent <c>false</c>, and "the drag did nothing" was the only symptom the user ever saw.</summary>
public enum RootlistMoveCheck : byte
{
    Ok = 0,
    /// <summary>The source or the target is no longer in the rootlist.</summary>
    Missing = 1,
    /// <summary>Source and target are the same row.</summary>
    SameItem = 2,
    /// <summary>The placement cannot be expressed (Inside something that is not a folder).</summary>
    Invalid = 3,
    /// <summary>The destination is where the item already sits.</summary>
    NoOp = 4,
    /// <summary>A folder filed into its own subtree.</summary>
    Cycle = 5,
}

/// <summary>The pure rootlist move legality check. No op is ever built or posted here — this is the index math a drop
/// cue, a "Move to folder…" picker and the batch writer must all agree with, so none of them can offer or execute a
/// move the others would refuse.</summary>
public static class RootlistOps
{
    /// <summary>Would this single move be accepted, and if not why?</summary>
    public static RootlistMoveCheck CheckMove(IReadOnlyList<RootlistEntry> entries, RootlistItemRef source,
                                               RootlistItemRef target, RootlistDropPlacement placement)
    {
        TryCheckMove(entries, source, target, placement, out _, out _, out _, out var reason);
        return reason;
    }

    /// <summary>Would this ORDERED batch be accepted, and if not why? Each move is checked against the stream the
    /// preceding ones left behind — the same order the server applies one Delta's ops in — so a later move in the
    /// batch is judged against where the earlier ones actually put things, not the stream the batch started from.
    ///
    /// <para>Three batch-only rules, all of them "a batch is not N separate drops":</para>
    /// <list type="bullet">
    /// <item>a source that IS its own target is dropped from the batch up front — dropping a multi-selection right
    /// after one of its own members is a legal GATHER, not a <see cref="RootlistMoveCheck.SameItem"/>. Only when
    /// EVERY move is that self-pair is the whole batch <see cref="RootlistMoveCheck.SameItem"/>;</item>
    /// <item>a per-move <see cref="RootlistMoveCheck.NoOp"/> is skipped (a member already sitting where the batch is
    /// headed does not refuse the other members), while <see cref="RootlistMoveCheck.Cycle"/> /
    /// <see cref="RootlistMoveCheck.Missing"/> / <see cref="RootlistMoveCheck.Invalid"/> on ANY move refuses the
    /// WHOLE batch with that reason — half a filing is worse than none;</item>
    /// <item>two real moves can net to identity, so the FINAL uri stream is compared to the input: equal ⇒
    /// <see cref="RootlistMoveCheck.NoOp"/>.</item>
    /// </list></summary>
    public static RootlistMoveCheck CheckMoves(IReadOnlyList<RootlistEntry> entries, IReadOnlyList<RootlistMove> moves)
    {
        if (moves.Count == 0) return RootlistMoveCheck.NoOp;

        IReadOnlyList<RootlistEntry> current = entries;
        int gathered = 0;
        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            if (move.Source == move.Target) { gathered++; continue; }
            if (TryCheckMove(current, move.Source, move.Target, move.Placement, out int from, out int length, out int to, out var r))
            {
                current = ApplyMoveLocally(current, from, length, to);
                continue;
            }
            if (r is RootlistMoveCheck.NoOp or RootlistMoveCheck.SameItem) continue;
            return r;
        }
        if (gathered == moves.Count) return RootlistMoveCheck.SameItem;
        return SameStream(entries, current) ? RootlistMoveCheck.NoOp : RootlistMoveCheck.Ok;
    }

    /// <summary>The index math shared by <see cref="CheckMove"/> and <see cref="CheckMoves"/>: resolve source/target
    /// ranges, then the destination index for <paramref name="placement"/>, in the exact guard order the caller must
    /// see. Returns the source span (<paramref name="from"/>/<paramref name="length"/>) and destination
    /// (<paramref name="to"/>, expressed against the PRE-removal stream) so a caller can advance local state.</summary>
    static bool TryCheckMove(IReadOnlyList<RootlistEntry> entries, RootlistItemRef source, RootlistItemRef target,
                             RootlistDropPlacement placement, out int from, out int length, out int to,
                             out RootlistMoveCheck reason)
    {
        from = -1; length = 0; to = -1;
        if (!TryRange(entries, source, out int start, out int end)
            || !TryRange(entries, target, out int targetFrom, out int targetEnd))
        {
            reason = RootlistMoveCheck.Missing;
            return false;
        }
        if (start == targetFrom) { reason = RootlistMoveCheck.SameItem; return false; }
        int dest = placement switch
        {
            RootlistDropPlacement.Before => targetFrom,
            RootlistDropPlacement.After => targetEnd,
            RootlistDropPlacement.Inside when target.IsFolder => Math.Max(targetFrom + 1, targetEnd - 1),
            _ => -1,
        };
        // Inside a NON-folder is not a placement this stream can express at all.
        if (dest < 0) { reason = RootlistMoveCheck.Invalid; return false; }
        // Landing on either edge of the span it already occupies is a no-op; STRICTLY inside it is a folder being
        // filed into its own subtree.
        if (dest == start || dest == end) { reason = RootlistMoveCheck.NoOp; return false; }
        if (dest > start && dest < end) { reason = RootlistMoveCheck.Cycle; return false; }
        from = start; length = end - start; to = dest;
        reason = RootlistMoveCheck.Ok;
        return true;
    }

    /// <summary>Resolve a playlist row or balanced folder marker range: a folder's range spans its start marker
    /// through its matching (nesting-aware) end marker.</summary>
    static bool TryRange(IReadOnlyList<RootlistEntry> entries, RootlistItemRef item, out int start, out int end)
    {
        start = -1; end = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            bool match = item.IsFolder
                ? entry.Kind == 1 && string.Equals(GroupId(entry.Uri), item.Key, StringComparison.Ordinal)
                : entry.Kind == 0 && string.Equals(entry.Uri, item.Key, StringComparison.Ordinal);
            if (!match) continue;
            start = i;
            if (!item.IsFolder) { end = i + 1; return true; }
            int nesting = 0;
            for (int j = i; j < entries.Count; j++)
            {
                if (entries[j].Kind == 1) nesting++;
                else if (entries[j].Kind == 2 && --nesting == 0) { end = j + 1; return true; }
            }
            end = entries.Count; // malformed missing end: the intact remaining subtree
            return true;
        }
        return false;
    }

    static string GroupId(string uri)
    {
        const string prefix = "spotify:start-group:";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal)) return uri;
        int name = uri.IndexOf(':', prefix.Length);
        return name < 0 ? uri[prefix.Length..] : uri[prefix.Length..name];
    }

    static bool SameStream(IReadOnlyList<RootlistEntry> a, IReadOnlyList<RootlistEntry> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i].Uri, b[i].Uri, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Advance local state the same way a real move would, for the NEXT batch entry's index math: lift
    /// [<paramref name="from"/>, <paramref name="from"/>+<paramref name="length"/>) and reinsert it at
    /// <paramref name="to"/> (expressed against the pre-removal stream, so a forward move shifts back by the length it
    /// lifted). Kind/Uri are what the legality checks above read, so a plain positional splice is exact for this
    /// purpose without recomputing folder depth.</summary>
    static List<RootlistEntry> ApplyMoveLocally(IReadOnlyList<RootlistEntry> entries, int from, int length, int to)
    {
        var list = new List<RootlistEntry>(entries.Count);
        for (int i = 0; i < entries.Count; i++) list.Add(entries[i]);
        var moved = list.GetRange(from, length);
        list.RemoveRange(from, length);
        int dest = to > from ? to - length : to;
        list.InsertRange(dest, moved);
        for (int i = 0; i < list.Count; i++) list[i] = list[i] with { Position = i };
        return list;
    }
}
/// <summary>THE MARKER STREAM, rebuilt from the flattened projection tree (stage B, J1). 0.2.9 read the stream from the
/// store bridge (<c>IStore.Rootlist()</c>); in 0.3 the tree IS built from the <c>User.Me</c> rootlist edges, so the pane
/// derives the stream back from it and every legality question (<see cref="RootlistOps"/>,
/// <see cref="RootlistDropDecision"/>, <see cref="RootlistTreeNav"/>) is asked against the same facts the rows draw.
/// <para>The tree is depth-first with depths stamped; a folder is identified by its projection id
/// (<c>SidebarLibraryEntry.FolderId</c>), which <see cref="RootlistTreeNav.RefOf"/> addresses and which the stream's
/// group-id parse takes verbatim (it is not a <c>spotify:start-group:</c> uri). An END marker is synthesised when the
/// next entry is at the folder's depth or shallower, and every folder still open at the end is closed.</para></summary>
public static class RootlistMarkerStream
{
    /// <summary>Fill <paramref name="into"/> (cleared first) with the marker stream <paramref name="tree"/> describes.
    /// Entries that are neither a playlist nor a folder are skipped; a null tree yields an empty stream.</summary>
    public static void Build(IReadOnlyList<SidebarLibraryEntry>? tree, List<RootlistEntry> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (tree is null || tree.Count == 0) return;
        var open = new List<int>(8);   // depths of the folders still open, innermost last
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (e.Kind is not (SidebarEntryKind.Playlist or SidebarEntryKind.Folder)) continue;
            int depth = e.Depth < 0 ? 0 : e.Depth;
            while (open.Count > 0 && open[^1] >= depth)
            {
                into.Add(new RootlistEntry(into.Count, 2, "", null, open[^1]));
                open.RemoveAt(open.Count - 1);
            }
            if (e.IsFolder)
            {
                string groupId = e.FolderId.Length > 0 ? e.FolderId : e.Id;
                into.Add(new RootlistEntry(into.Count, 1, groupId, e.Name, depth, e.AddedAtMs));
                open.Add(depth);
            }
            else
            {
                into.Add(new RootlistEntry(into.Count, 0, e.Uri, null, depth, e.AddedAtMs));
            }
        }
        while (open.Count > 0)
        {
            into.Add(new RootlistEntry(into.Count, 2, "", null, open[^1]));
            open.RemoveAt(open.Count - 1);
        }
    }
}

// ── GEOMETRY, EXTENTS, DIFF, RESOLVE, PILL, REORDER, STAGE-HOLD, MENUS, DIAG ─────────────────────────────────────
//
// The sidebar pane's pure numeric ladder and per-row bookkeeping: row/indent/tree-content geometry and the plan
// geometry helpers built on it; the analytic row extent a plan seeds before anything measures; the per-row change
// diff; the row→route selection join; the accent-pill lit rule; the reorder-clamp displacement hint; the mid-drag
// stage-hold parking bay; context-menu verb layout; and the docked-pane terminal-state invariant. Every number here
// is a fact verified against a screenshot ruler — port it exactly, never re-derive it.

/// <summary>Which of the three WinUI row shapes a section's rows take (design V.3). One shape per SECTION, never per
/// row: a band's reorder pitch and the virtualizing host's extent table both assume it.</summary>
public enum SidebarRowShape : byte
{
    /// <summary>Row A — a glyph row (Home, Collections, Settings, a route pin in a glyph section): 36 tall, 16-px glyph.</summary>
    Glyph = 0,
    /// <summary>Row C — an entity row at Default density: 40 tall, 32-px art, title + subtitle.</summary>
    EntityTwoLine = 1,
    /// <summary>Row B — an entity row at Compact density: 36 tall, 24-px art, title only.</summary>
    EntityOneLine = 2,
    /// <summary>Classic with Show covers off: 28 tall, one line. A row with no leading visual has no icon column and its
    /// label at the header's x (pane 16); a FOLDER row still carries its 16-DIP folder mark in the icon column (label at
    /// pane 48), so the tree reads as a tree without covers.</summary>
    Text = 3,
}

/// <summary>THE ONE ROW LADDER, drawn to WinUI NavigationView (NavigationView_themeresources.xaml, "TR"): 36-px rows in
/// a 4,2 margin (TR:217, TR:228), a 40-px icon column whose centre sits at pane x 24 (TR:612), the label at pane x 48
/// (TR:251), trailing content ending at pane W − 18 (TR:604), a 40-px chevron column at pane W − 44..W − 4 (TR:617), 32-px
/// folder indents (WinUI's 31, rounded to the 8-px grid), the 3×16 r2 pill at slot x 32·depth (TR:220-222) and the 1-px tree
/// guides at the folder mark's centre column. Numbers are SLOT space (the list's one 4-px inset is
/// <see cref="PaneEdge"/>). Engine-free so Wavee.Tests pins every one.</summary>
public static class SidebarRowGeometry
{
    // ── the pane ──
    /// <summary>The list's horizontal inset (4) — WinUI's item margin X, applied ONCE around the list
    /// (<c>PaneMetrics.PanePad</c>), never per row.</summary>
    public const float PaneEdge = 4f;
    /// <summary>The pane content grid's top margin (TR:233's −1,3 with the −1 dropped: no content border to tuck under).</summary>
    public const float PaneTopInset = 4f;
    /// <summary>The vertical half of the 4,2 item margin, carried by every row.</summary>
    public const float RowMarginY = 2f;
    /// <summary>The compact rail (TR:208 NavigationViewCompactPaneLength).</summary>
    public const float RailWidth = 48f;
    /// <summary>A compact-rail row: the 48 rail less the two 4-px insets.</summary>
    public const float TileWidth = 40f;

    // ── rows ──
    public const float RowHeight = 36f;
    public const float TwoLineRowHeight = 40f;
    /// <summary>A Classic text row (Show covers off): 28 tall, the label only.</summary>
    public const float TextRowHeight = 28f;
    public const float IconColumn = 40f;
    public const float GlyphSize = 16f;
    /// <summary>A Classic text row's label x when it has NO icon column (no glyph, no leading): the header's text x in
    /// slot space (pane 16). A Text row that carries a glyph (a folder) uses the icon column and <see cref="LabelGap"/> instead.</summary>
    public const float TextLabelX = HeaderTextX;
    /// <summary>The ContentPresenter's 4-px left margin between the icon column and the label (TR:251).</summary>
    public const float LabelGap = 4f;
    /// <summary>The ContentGrid's 14-px right margin (TR:604): trailing content ends at pane W − 18.</summary>
    public const float TrailingPad = 14f;
    /// <summary>The chevron column (TR:617): its −14 margin cancels <see cref="TrailingPad"/>.</summary>
    public const float ChevronColumn = 40f;
    public const float IndentStep = 32f;
    public const int MaxIndentDepth = 3;
    /// <summary>The trailing cluster's gap (count · pin mark · equalizer).</summary>
    public const float TrailingGap = 6f;

    // ── chrome rows ──
    public const float HeaderHeight = 40f;
    /// <summary>The header title's x: pane 16 (TR:229's 16,0) less <see cref="PaneEdge"/>. Slot space: it already draws at pane 16.</summary>
    public const float HeaderTextX = 12f;
    /// <summary>The header title's x in pane space (16), the ruler the tests pin.</summary>
    public const float HeaderTextPaneX = PaneEdge + HeaderTextX;
    /// <summary>A header's inline button (⋯, +, and the footer ⋯): a 28×28 box carrying a 16 glyph, the same as
    /// <c>SidebarLibraryHeadRules.ToolbarIconButton</c>. The chevron keeps its own glyph size.</summary>
    public const float HeaderButton = 28f;
    /// <summary>The glyph inside a header's inline button and every '+' glyph.</summary>
    public const float HeaderGlyph = 16f;
    /// <summary>Every '+' glyph (a header's create, the Library toolbar's create, a folder row's +).</summary>
    public const float PlusGlyph = 16f;
    /// <summary>A row's own trailing button (the folder +): 24 so it fits inside every row shape, including Classic's
    /// 28-px text row.</summary>
    public const float RowButton = 24f;
    /// <summary>6: a header's or the footer's last 28-px button centres on pane W − 24, the chevron column's centre.</summary>
    public const float HeaderTrailingPad = (ChevronColumn - HeaderButton) * 0.5f;
    /// <summary>A separator: the 1-px rule plus its 0,3,0,4 margin (TR:223, TR:247), full pane width.</summary>
    public const float SeparatorHeight = 8f;
    public const float SeparatorLineTop = 4f;
    /// <summary>A quiet one-line hint (an empty Playlists, a search with no match): 40 tall in the 4,2 margin.</summary>
    public const float EmptyHintHeight = 40f;
    /// <summary>The tree's closing drop gutter.</summary>
    public const float TreeEndHeight = 24f;

    // ── the pill ──
    public const float PillW = 3f, PillH = 16f, PillRadius = 2f;

    public static float HeightOf(SidebarRowShape shape) => shape switch
    {
        SidebarRowShape.EntityTwoLine => TwoLineRowHeight,
        SidebarRowShape.Text => TextRowHeight,
        _ => RowHeight,
    };

    /// <summary>A row's slot extent: its height plus the 2 + 2 margin (40 / 44 / 40 / 32).</summary>
    public static float PitchOf(SidebarRowShape shape) => HeightOf(shape) + 2f * RowMarginY;

    /// <summary>The leading visual's edge: glyph 16 · art 32 (Default) · art 24 (Compact).</summary>
    public static float ArtOf(SidebarRowShape shape) => shape switch
    {
        SidebarRowShape.Glyph => GlyphSize,
        SidebarRowShape.EntityTwoLine => 32f,
        SidebarRowShape.Text => 0f,
        _ => 24f,
    };

    public static int ClampDepth(int depth) => depth < 0 ? 0 : depth > MaxIndentDepth ? MaxIndentDepth : depth;

    /// <summary>The content indent for a nesting depth: 32 per level, capped at 3 (96). The FILL stays full width; only
    /// pill, icon and label move (NavigationViewItem.cpp:894-902).</summary>
    public static float IndentFor(int depth) => IndentStep * ClampDepth(depth);

    /// <summary>The pill's x in slot space (pane 4 + 32·depth).</summary>
    public static float PillX(int depth) => IndentFor(depth);

    /// <summary>The pill's y inside a slot whose row is <paramref name="rowHeight"/> tall: centred on the row, below its
    /// 2-px top margin.</summary>
    public static float PillTop(float rowHeight) => RowMarginY + (rowHeight - PillH) * 0.5f;

    /// <summary>Where the drop caret for depth <paramref name="depth"/> starts (slot space): the same x as the pill, so
    /// "insert here at this depth" lines up with the row it describes. Read backwards by the drop resolver.</summary>
    public static float TreeContentX(int depth) => IndentFor(depth);

    /// <summary>The x (slot space) of the 1-px tree guide that joins the rows under an ancestor at level
    /// <paramref name="level"/>: the folder mark's centre column (20) plus 32 per level.</summary>
    public static float TreeGuideX(int level) => IconColumn * 0.5f + IndentStep * level;

    /// <summary>Pane-space rulers (diagnostics, tests).</summary>
    public const float IconCentreX = PaneEdge + IconColumn * 0.5f;              // 24
    public const float LabelX = PaneEdge + IconColumn + LabelGap;               // 48
    /// <summary>A tree guide's x in pane space: pane 24 + 32·level (the folder mark's centre column).</summary>
    public static float TreeGuidePaneX(int level) => PaneEdge + TreeGuideX(level);
    /// <summary>A glyph-less Text row's label x in pane space: the header's x (16) plus the depth indent.</summary>
    public static float TextLabelPaneX(int depth) => PaneEdge + IndentFor(depth) + TextLabelX;
    /// <summary>An icon-column row's label x in pane space: 48 plus the depth indent.</summary>
    public static float LabelPaneX(int depth) => LabelX + IndentFor(depth);
    public static float TrailingRight(float paneWidth) => paneWidth - PaneEdge - TrailingPad;   // W − 18
    public static float ChevronLeft(float paneWidth) => paneWidth - PaneEdge - ChevronColumn;   // W − 44

    /// <summary>A subtitle line is drawn only in a two-line row and only when there is text.</summary>
    public static bool SubtitleVisible(SidebarRowShape shape, string? subtitle)
        => shape == SidebarRowShape.EntityTwoLine && subtitle is { Length: > 0 };

    // ── pure plan geometry ──

    /// <summary>The CONTENT-SPACE top of plan row <paramref name="index"/>: the prefix sum of every earlier row's
    /// extent (the pane's rows are contiguous inside one virtualized list, so this IS the row's Y). Returns 0 for a
    /// negative index and clamps an index past the end. <paramref name="extentOf"/> must report the row's MEASURED
    /// height including any rhythm padding, or the result drifts from the rendered layout row by row.</summary>
    public static float ContentYOf(int index, int count, Func<int, float> extentOf)
    {
        if (extentOf is null) throw new ArgumentNullException(nameof(extentOf));
        if (index <= 0) return 0f;
        int stop = index < count ? index : count;
        float y = 0f;
        for (int i = 0; i < stop; i++)
        {
            float e = extentOf(i);
            if (float.IsNaN(e) || e <= 0f) continue;   // a zero/degenerate row contributes nothing
            y += e;
        }
        return y;
    }

    /// <summary>The first plan index whose row resolves to <paramref name="route"/>, or -1. <paramref name="routeAt"/>
    /// is the caller's row→route projection; null/empty means "this row is not a navigation target".</summary>
    public static int IndexOfRoute(int count, Func<int, string?> routeAt, string? route)
    {
        if (routeAt is null) throw new ArgumentNullException(nameof(routeAt));
        if (string.IsNullOrEmpty(route)) return -1;
        for (int i = 0; i < count; i++)
        {
            string? r = routeAt(i);
            if (r is { Length: > 0 } && string.Equals(r, route, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>Which way the selection TRAVELLED: +1 when the new row sits below the old one, -1 above, 0 when the
    /// direction is unknowable (either row off-plan, or the same row). 0 is a first-class answer: a selection
    /// arriving from off-plan (a deep link, a row inside a collapsed section, the first paint) must simply fade in
    /// rather than slide from an invented side.</summary>
    public static int DirectionOf(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || toIndex < 0 || fromIndex == toIndex) return 0;
        return toIndex > fromIndex ? 1 : -1;
    }

    /// <summary>The plan index of a <c>PlaylistTree</c> folder's header row, or −1. Addressed by the folder's OWN
    /// group id, never by row key, so a renamed/re-keyed folder still resolves.</summary>
    public static int FolderHeaderIndexOf(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries,
                                          string folderId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrEmpty(folderId)) return -1;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind != SidebarRowKind.FolderHeader || (uint)row.EntryIndex >= (uint)entries.Count) continue;
            if (string.Equals(entries[row.EntryIndex].FolderId, folderId, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>Resolve the contiguous PREORDER BAND a planned folder header owns — every row after it, in the same
    /// section, that is DEEPER than the folder. That band is exactly what a disclosure inserts on expand and removes
    /// on collapse.</summary>
    public static bool TryFolderDescendantRange(IReadOnlyList<SidebarRow> rows,
                                                IReadOnlyList<SidebarLibraryEntry> entries,
                                                string folderId, out int firstIndex, out int count)
    {
        firstIndex = count = 0;
        int folderIndex = FolderHeaderIndexOf(rows, entries, folderId);
        if (folderIndex < 0) return false;
        var folder = rows[folderIndex];
        if ((uint)folder.EntryIndex >= (uint)entries.Count) return false;
        int depth = entries[folder.EntryIndex].Depth;
        int end = folderIndex + 1;
        while (end < rows.Count)
        {
            var row = rows[end];
            if (!string.Equals(row.SectionId, folder.SectionId, StringComparison.Ordinal)
                || (uint)row.EntryIndex >= (uint)entries.Count
                || entries[row.EntryIndex].Depth <= depth) break;
            end++;
        }
        firstIndex = folderIndex + 1;
        count = end - firstIndex;
        return count > 0;
    }

    /// <summary>H1 (#85) — whether a row draws the trailing pin glyph: the entry survived the pins-first stamp AND
    /// is not a track (a track is never pinnable — locked decision).</summary>
    public static bool ShowsPinGlyph(bool isPinned, bool isTrack) => isPinned && !isTrack;

    // ── grid strip fallback (issue #84) ──
    // The planner owns the column COUNT; re-deriving it from width here would disagree with how the planner already
    // sliced entries into column-wide strips. What the 180-DIP floor (down from 240) broke is that the planned count
    // can no longer be honoured without shrinking cells under the 40-DIP art floor, so the SLOT clamps the column
    // count DOWN as a function of the width it has to render into, wrapping the strip to more lines instead.

    /// <summary>The largest column count in <c>[1, plannedCols]</c> whose cells still clear <paramref name="minCell"/>
    /// at <paramref name="availableWidth"/>. Never exceeds <paramref name="plannedCols"/> and never returns less
    /// than 1.</summary>
    public static int GridFallbackColumns(int plannedCols, float availableWidth, float gap, float minCell)
    {
        int cols = plannedCols < 1 ? 1 : plannedCols;
        for (int c = cols; c > 1; c--)
        {
            float edge = (availableWidth - gap * (c - 1)) / c;
            if (edge >= minCell) return c;
        }
        return 1;
    }

    /// <summary>Resolve the contiguous BODY owned by one planned section header. A different section at the same or
    /// a shallower depth is a structural sibling and terminates the band even when that sibling is a divider or has
    /// no header of its own. Deeper rows belong to a nested CustomGroup subtree and stay inside the parent
    /// disclosure.</summary>
    public static bool TrySectionBodyRange(IReadOnlyList<SidebarRow> rows, string sectionId,
                                           out int firstIndex, out int count)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrEmpty(sectionId))
        {
            firstIndex = count = 0;
            return false;
        }

        int header = -1;
        byte depth = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Kind != SidebarRowKind.SectionHeader
                || !string.Equals(row.SectionId, sectionId, StringComparison.Ordinal)) continue;
            header = i;
            depth = row.Depth;
            break;
        }

        if (header < 0)
        {
            firstIndex = count = 0;
            return false;
        }

        int end = header + 1;
        while (end < rows.Count)
        {
            var row = rows[end];
            if (row.Depth <= depth && !string.Equals(row.SectionId, sectionId, StringComparison.Ordinal)) break;
            end++;
        }

        firstIndex = header + 1;
        count = end - firstIndex;
        return count > 0;
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
            SidebarRowKind.SectionHeader => SidebarRowGeometry.HeaderHeight,
            SidebarRowKind.Divider => SidebarRowGeometry.SeparatorHeight,
            SidebarRowKind.IconRow or SidebarRowKind.SectionTile => SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph),
            SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader or SidebarRowKind.Skeleton => SidebarRowGeometry.PitchOf(section.Shape),
            SidebarRowKind.Empty => SidebarRowGeometry.EmptyHintHeight + 2f * SidebarRowGeometry.RowMarginY,
            SidebarRowKind.DropBand => SidebarRowGeometry.PitchOf(SidebarRowShape.Glyph),
            SidebarRowKind.TreeEnd => SidebarRowGeometry.TreeEndHeight,
            _ => float.NaN,   // GridStrip: estimate, correct on measure
        };
    }
}

/// <summary>The per-row change detector behind the pane's row epochs. A bound row slot is a FROZEN child — a
/// re-plan does not re-render it — so this is what lets a publish bump only the indices whose rendered content can
/// actually have changed, instead of every realized row every time. Pure and engine-free.</summary>
public static class SidebarRowDiff
{
    /// <summary>Fill <paramref name="changed"/> with "row i must re-render". The row RECORD is not enough on its
    /// own: a row addresses its entry by INDEX, so a library refresh can leave every row record identical while the
    /// entry behind it gained a name, a cover or a child count — both are therefore compared. A row present in the
    /// new plan but not the old one always counts as changed, and so does a row whose entry index is valid in one
    /// plan and not the other.</summary>
    /// <param name="changed">Written for indices [0, newRows.Count); the caller sizes it. Any excess is left alone.</param>
    public static void Diff(
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
        IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries,
        Span<bool> changed)
    {
        int n = Math.Min(newRows.Count, changed.Length);
        for (int i = 0; i < n; i++)
            changed[i] = RowChanged(oldRows, oldEntries, newRows, newEntries, i);
    }

    /// <summary>Does row <paramref name="index"/> render differently between the two plans?</summary>
    public static bool RowChanged(
        IReadOnlyList<SidebarRow> oldRows, IReadOnlyList<SidebarLibraryEntry> oldEntries,
        IReadOnlyList<SidebarRow> newRows, IReadOnlyList<SidebarLibraryEntry> newEntries,
        int index)
    {
        if ((uint)index >= (uint)newRows.Count) return false;
        if (index >= oldRows.Count) return true;              // the row is new at this slot
        var row = newRows[index];
        if (!row.Equals(oldRows[index])) return true;
        return !SameEntry(oldEntries, newEntries, row.EntryIndex);
    }

    /// <summary>Compare the entry both plans' row addresses. An out-of-range index on BOTH sides is equal — the row
    /// carries no entry (a header, a divider, a skeleton) and nothing behind it can go stale.</summary>
    static bool SameEntry(IReadOnlyList<SidebarLibraryEntry> oldEntries, IReadOnlyList<SidebarLibraryEntry> newEntries,
                          int entryIndex)
    {
        if (entryIndex < 0) return true;
        bool inOld = entryIndex < oldEntries.Count;
        bool inNew = entryIndex < newEntries.Count;
        if (inOld != inNew) return false;
        return !inNew || oldEntries[entryIndex].Equals(newEntries[entryIndex]);
    }
}

/// <summary>The PURE join rules between a planned <see cref="SidebarRow"/> and what the renderer draws from it:
/// which hand-placed item a row was projected from, and whether a row draws itself SELECTED for a given nav route.
///
/// The pane sweeps the plan ONCE per route change and bumps only the rows that flipped, exactly like now-playing
/// does — that sweep and a row's own <c>Selected</c> flag MUST agree, so this is the one implementation both
/// call.</summary>
public static class SidebarRowResolve
{
    /// <summary>The route a plan row navigates to: a glyph row's key, an entity row's entry route; null for everything
    /// else (headers, separators, folders, tracks, hints, tiles).</summary>
    public static string? RouteOf(in SidebarRow row, IReadOnlyList<SidebarLibraryEntry> entries) => row.Kind switch
    {
        SidebarRowKind.IconRow => row.Key,
        SidebarRowKind.EntityRow when (uint)row.EntryIndex < (uint)entries.Count => entries[row.EntryIndex].RouteKey,
        _ => null,
    };

    public static bool EntrySelects(in SidebarLibraryEntry entry, string route)
        => route.Length > 0 && entry.RouteKey is { Length: > 0 } r && string.Equals(r, route, StringComparison.Ordinal);

    public static bool SelectsRoute(in SidebarRow row, IReadOnlyList<SidebarLibraryEntry> entries, string route)
    {
        if (route.Length == 0 || entries is null) return false;
        if (row.Kind == SidebarRowKind.GridStrip)
        {
            int start = row.EntryIndex, count = row.ItemCount;
            if (start < 0 || count <= 0 || start >= entries.Count) return false;
            if (start + count > entries.Count) count = entries.Count - start;
            for (int i = 0; i < count; i++) if (EntrySelects(entries[start + i], route)) return true;
            return false;
        }
        return RouteOf(in row, entries) is { Length: > 0 } r && string.Equals(r, route, StringComparison.Ordinal);
    }

    /// <summary>Every plan-row index that draws selected for <paramref name="route"/>, in ASCENDING order (the pane
    /// diffs two of these with a linear merge, so the order is part of the contract). <paramref name="into"/> is
    /// caller-owned and is NOT cleared — a warm sweep therefore allocates nothing.</summary>
    public static void Sweep(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries, string route,
                             List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (rows is null || entries is null || route.Length == 0) return;
        for (int i = 0; i < rows.Count; i++) { var row = rows[i]; if (SelectsRoute(in row, entries, route)) into.Add(i); }
    }

    /// <summary>The SYMMETRIC DIFFERENCE of two ascending index lists — the rows that GAINED or LOST the selection,
    /// and therefore exactly the per-row epochs a route edge must bump. A row selected on both sides is deliberately
    /// absent: nothing about its skin changed, and the publish diff already owns any content change it had.
    ///
    /// Both inputs come from <see cref="Sweep"/>, so ascending order is part of the contract and the merge is
    /// linear. <paramref name="into"/> is caller-owned and is NOT cleared.</summary>
    public static void Flipped(IReadOnlyList<int> previous, IReadOnlyList<int> next, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        int a = 0, b = 0;
        int na = previous?.Count ?? 0, nb = next?.Count ?? 0;
        while (a < na || b < nb)
        {
            if (b >= nb) { into.Add(previous![a++]); continue; }
            if (a >= na) { into.Add(next![b++]); continue; }
            int x = previous![a], y = next![b];
            if (x == y) { a++; b++; }
            else if (x < y) { into.Add(x); a++; }
            else { into.Add(y); b++; }
        }
    }
}

/// <summary>The live state of ONE item-owned NavigationView selection indicator (the left accent pill), and the ONE
/// rule that decides whether it is lit.
///
/// The pill used to render <c>Opacity = selected ? 1f : 0f</c> as a MOUNT-TIME literal read out of the slot's
/// snapshot, so its visibility was whatever the last render happened to see; anything that wrote the node's opacity
/// afterwards (the pane's moving-pill transaction, a force-completed flight, a registry entry pointing at a
/// recycled node) left a row lit that the row's own state said must be dark (#22/#23). The pill's opacity is now a
/// BOUND read of this state, the same discipline the drop cue uses. Engine-free (System only).
///
/// <b>The pill means "this is the open route", nothing else.</b> Playback is the <c>|||</c> glyph and never the
/// pill: <see cref="Route"/> is a NAV route key, and <see cref="Lit"/> compares it to the live route only. A row can
/// therefore be playing, hovered, dragged or expanded without ever touching this.</summary>
public readonly record struct SidebarPillState(
    string? Route,
    bool Selected,
    float Indent,
    float Top)
{
    /// <summary>The lit opacity of an accent pill.</summary>
    public const float LitOpacity = 1f;

    /// <summary>The dark opacity of an accent pill (mounted always, drawn only when selected).</summary>
    public const float DarkOpacity = 0f;

    /// <summary>The pill's opacity — DERIVED from <see cref="Selected"/>, never authored as a literal.</summary>
    public float Opacity => Selected ? LitOpacity : DarkOpacity;

    /// <summary>THE RULE: an indicator is lit exactly when the route it was drawn for IS the live nav route. A row
    /// with no route (a folder, a track, chrome) can never be lit, and neither can any row while the live route is
    /// empty. Ordinal by contract — route keys are ids, never display text.</summary>
    public static bool Lit(string? route, string liveRoute)
        => route is { Length: > 0 } r
           && !string.IsNullOrEmpty(liveRoute)
           && string.Equals(r, liveRoute, StringComparison.Ordinal);

    /// <summary>This state re-derived against the live route. The single call every probe makes, so a snapshot taken
    /// at render time can never carry a stale <see cref="Selected"/> into the next frame.</summary>
    public SidebarPillState For(string liveRoute) => this with { Selected = Lit(Route, liveRoute) };

    /// <summary>As <see cref="For(string)"/>, but with the pane's own row-level verdict folded in: the pill is lit
    /// only when the PANE says this row draws selected (<see cref="SidebarRowResolve.SelectsRoute"/>, the one owner
    /// the selection sweep also uses) AND the route this pill was drawn for is still the live one. The conjunction
    /// is what makes a recycled slot dark for the one frame in which its index has moved but its snapshot has
    /// not.</summary>
    public SidebarPillState For(string liveRoute, bool rowSelectsRoute)
        => this with { Selected = rowSelectsRoute && Lit(Route, liveRoute) };
}

/// <summary>The displacement hint for a reorder gesture whose destination was CLAMPED.
///
/// Normally the engine's own <c>ReorderList.OffsetFor</c> answers this — and still does everywhere no clamp is
/// configured. But a clamp says "the gap opens HERE, not where the pointer is", and the engine's hint is computed
/// from the target it holds internally, which the app cannot set without reaching into the control's gesture state.
/// So the one clamped case reproduces the hint from the snapped destination instead: the sidebar's bands lift ONE
/// row at one uniform band extent with no inter-row spacing — exactly the shape <c>ReorderList.OffsetFor</c>
/// reduces to there. Engine-free, so a test can pin the gap against the same fixture the slot clamp uses.</summary>
public static class SidebarReorderClamp
{
    /// <summary>Where sibling <paramref name="slot"/> sits while the row at <paramref name="from"/> is heading for
    /// <paramref name="to"/>: the rows between the two close the gap the lift left (or part to make room),
    /// everything else — and the lifted row itself — stays put.</summary>
    public static float Offset(int slot, int from, int to, float extent)
    {
        if (slot < 0 || from < 0 || to < 0 || from == to || slot == from) return 0f;
        if (to > from && slot > from && slot <= to) return -extent;
        if (to < from && slot >= to && slot < from) return extent;
        return 0f;
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
public sealed class SidebarStageHold<TStage> where TStage : class
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

/// <summary>A settled pane observation (the presentation as decided, plus the column's laid-out width).</summary>
public readonly record struct SidebarPaneFrameSnapshot(
    SidebarLayoutId Layout,
    SidebarPaneMode Mode,
    SidebarWindowBand Band,
    bool UserCollapsed,
    bool OverlayOpen,
    float PreferredExpandedWidth,
    float PresentedWidth,
    float RenderedPaneWidth,
    bool PaneHidden = false);

/// <summary>All terminal-state violations detected in one observation. Flags make one diagnostic edge sufficient
/// even when one bad state breaks width and mode at the same time.</summary>
[Flags]
public enum SidebarPaneInvariantFault : ushort
{
    None = 0,
    NonFiniteValue = 1 << 0,
    PreferredWidthOutOfRange = 1 << 1,
    RailWidthMismatch = 1 << 2,
    ExpandedWidthOutOfRange = 1 << 3,
    PresentedExceedsPreferred = 1 << 4,
    ExpandedWidthMismatch = 1 << 5,
    MinimalNotEmpty = 1 << 6,
    ModeBandMismatch = 1 << 7,
}

/// <summary>Pure terminal-state validator behind the screenshot/layout probe and the runtime edge diagnostic. It does
/// not attempt to validate an in-flight animation: callers invoke it only after the pane transition settles.</summary>
public static class SidebarPaneInvariant
{
    public const float Tolerance = 0.5f;

    public static SidebarPaneInvariantFault Inspect(in SidebarPaneFrameSnapshot s)
    {
        if (!float.IsFinite(s.PreferredExpandedWidth) || !float.IsFinite(s.PresentedWidth) || !float.IsFinite(s.RenderedPaneWidth))
            return SidebarPaneInvariantFault.NonFiniteValue;
        var fault = SidebarPaneInvariantFault.None;
        if (!InExpandedRange(s.PreferredExpandedWidth)) fault |= SidebarPaneInvariantFault.PreferredWidthOutOfRange;
        // A forced band can only present its forced mode.
        // A hidden pane (Zune) presents Minimal in every band.
        if (s.PaneHidden
                ? s.Mode != SidebarPaneMode.Minimal
                : (s.Band == SidebarWindowBand.Narrow && s.Mode != SidebarPaneMode.Compact)
                  || (s.Band == SidebarWindowBand.Tiny && s.Mode != SidebarPaneMode.Minimal))
            fault |= SidebarPaneInvariantFault.ModeBandMismatch;
        switch (s.Mode)
        {
            case SidebarPaneMode.Compact:
                if (!Near(s.RenderedPaneWidth, SidebarRowGeometry.RailWidth)) fault |= SidebarPaneInvariantFault.RailWidthMismatch;
                break;
            case SidebarPaneMode.Minimal:
                if (!Near(s.RenderedPaneWidth, 0f)) fault |= SidebarPaneInvariantFault.MinimalNotEmpty;
                break;
            default:
                if (!InExpandedRange(s.PresentedWidth)) fault |= SidebarPaneInvariantFault.ExpandedWidthOutOfRange;
                if (s.PresentedWidth > s.PreferredExpandedWidth + Tolerance) fault |= SidebarPaneInvariantFault.PresentedExceedsPreferred;
                if (!Near(s.RenderedPaneWidth, s.PresentedWidth)) fault |= SidebarPaneInvariantFault.ExpandedWidthMismatch;
                break;
        }
        return fault;
    }

    public static bool IsValid(in SidebarPaneFrameSnapshot state) => Inspect(state) == SidebarPaneInvariantFault.None;

    public static string FaultName(SidebarPaneInvariantFault fault)
    {
        if (fault == SidebarPaneInvariantFault.None) return "none";
        if (fault == SidebarPaneInvariantFault.NonFiniteValue) return "non_finite";
        return ((ushort)fault).ToString(CultureInfo.InvariantCulture);
    }

    static bool InExpandedRange(float width) =>
        width >= SidebarPaneBounds.NavPaneMinW - Tolerance && width <= SidebarPaneBounds.NavPaneMaxW + Tolerance;

    static bool Near(float actual, float expected) => MathF.Abs(actual - expected) <= Tolerance;
}
// ── ROW PLANNER ──────────────────────────────────────────────────────────────────────────────────────────────────
// The unified sidebar row model plus the ONE function that flattens (document x projection) into a flat SidebarRow[].
// A PlaylistTree or EntityList over a 10k library cannot virtualize as a Grow=1 child inside an outer ScrollView, so
// the whole pane plans into one flat row list (headers, dividers, rows, grid strips, cards, prompts, placeholders,
// empty/skeleton rows) rendered by ONE bound list. The planner is pure — no engine type, no service, no clock — and
// allocates no string during planning: a row's Key is always an existing string (an entry's Id, an item's Key, or a
// section's Id). Row/entry storage is caller-owned (SidebarPlanBuffers), so a warm re-plan reuses capacity.

/// <summary>The row families the unified list carries. This is also the ONE persisted pin-kind vocabulary (a pin and a
/// projected row can never disagree about what an entity "is"). <see cref="Track"/> is produced ONLY by a data source
/// that yields tracks (queue, now-playing, artist top tracks) — never by the projection, because a track is not a
/// library entity, and it is deliberately absent from every <see cref="SidebarEntryKindMask"/> bit so no filter,
/// qualifier or EntityList query can ever emit one, and a track is never pinnable.</summary>
public enum SidebarEntryKind : byte
{
    AppRoute = 0, Playlist = 1, Folder = 2, Album = 3, Artist = 4, Show = 5, Track = 6,
}

/// <summary>Playlist provenance for the Your Library chips. <see cref="None"/> = unknown — the chips stay hidden
/// unless at least two distinct non-None flavors are present.</summary>
public enum SidebarPlaylistFlavor : byte { None = 0, ByYou = 1, BySpotify = 2, Mixed = 3 }

/// <summary>Which kinds a projection pass should emit. A mask (not a single kind) because every consumer asks for a
/// SET: the "All" filter wants everything, the `SidebarLibraryFilter.Podcasts` chip wants shows only.</summary>
[Flags]
public enum SidebarEntryKindMask : byte
{
    None = 0,
    Playlist = 1,
    Folder = 2,
    Album = 4,
    Artist = 8,
    Show = 16,
    /// <summary>The playlist tree as the sidebar shows it: leaves AND their folders.</summary>
    PlaylistTree = Playlist | Folder,
    All = Playlist | Folder | Album | Artist | Show,
}

/// <summary>Mask helpers — the one place a filter/query vocabulary is translated into projection kinds, so no surface
/// hand-rolls the mapping.</summary>
public static class SidebarEntryKinds
{
    public static SidebarEntryKindMask Of(SidebarEntryKind kind) => kind switch
    {
        SidebarEntryKind.Playlist => SidebarEntryKindMask.Playlist,
        SidebarEntryKind.Folder => SidebarEntryKindMask.Folder,
        SidebarEntryKind.Album => SidebarEntryKindMask.Album,
        SidebarEntryKind.Artist => SidebarEntryKindMask.Artist,
        SidebarEntryKind.Show => SidebarEntryKindMask.Show,
        // AppRoute rows are authored, never projected; Track rows come from a data source and are never a member of a
        // kind mask — both therefore match no filter.
        _ => SidebarEntryKindMask.None,
    };

    public static bool Has(SidebarEntryKindMask mask, SidebarEntryKind kind) => (mask & Of(kind)) != 0;

    /// <summary>The library chip -> projection kinds. Playlists includes folders (a folder IS part of the playlist tree).
    /// Podcasts and Audiobooks are both Show rows: the mask cannot split them, so <see cref="SidebarBinderPipeline.Shape"/>
    /// does the exact match.</summary>
    public static SidebarEntryKindMask From(SidebarLibraryFilter filter) => filter switch
    {
        SidebarLibraryFilter.Playlists => SidebarEntryKindMask.PlaylistTree,
        SidebarLibraryFilter.Podcasts or SidebarLibraryFilter.Audiobooks => SidebarEntryKindMask.Show,
        SidebarLibraryFilter.Albums => SidebarEntryKindMask.Album,
        SidebarLibraryFilter.Artists => SidebarEntryKindMask.Artist,
        _ => SidebarEntryKindMask.All,
    };
}

/// <summary>
/// One row of the unified sidebar list, projected from the library + recency sources. A readonly record STRUCT: the
/// projection fills a reusable <c>List&lt;T&gt;</c> owned by the mode component, so a rebuild allocates only when the
/// list grows — no per-frame LINQ, no per-row closures.
///
/// The positional members are the projection's own vocabulary. The trailing <c>init</c> members are display facts a
/// surface needs but cannot derive (owner flags, the containing folder, an album's first artist) plus
/// <see cref="IsPinned"/>, which the pin projection stamps. Everything else is computed, so the consumer names
/// (<c>RouteKey</c>, <c>TrackCount</c>, <c>OwnerName</c>, <c>Publisher</c>, <c>FolderDepth</c>, <c>IsFolder</c>,
/// <c>PinKey</c>) resolve without a second parallel record.
/// </summary>
public readonly record struct SidebarLibraryEntry(
    string Id,                             // the pin/route id — also the recency key and the custom-order key
    SidebarEntryKind Kind,
    string Uri,                            // "" for AppRoute and Folder
    string Name,
    string Creator,                        // playlist OwnerName - album joined artists - "" artist - show Publisher - "" folder/route
    StringId Cover,
    IReadOnlyList<StringId>? MosaicTiles,    // cover-less playlists (2x2 mosaic) + a folder's first child covers; else null
    int ChildCount,                        // playlist/album TrackCount - folder DIRECT child count - 0 for artist/show/route
    long AddedAtMs,                        // 0 = unknown (see SortStamp)
    long SortStamp,                        // the resolved "recently added" key — never 0 for a sortable kind
    long LastVisitedTicksUtc,              // 0 = never visited
    int SourceOrder,                       // rootlist position for playlists/folders; else the source list index
    int Depth,                             // folder nesting depth (0 = top level)
    bool Circular,                         // artist avatars
    SidebarPlaylistFlavor Flavor)
{
    /// <summary>True when this row is in the pin store — stamped by the pin projection, never guessed by a surface.</summary>
    public bool IsPinned { get; init; }

    /// <summary>True when the entity's only source of truth — the rootlist, for a folder — does not contain it. A
    /// missing row renders visible-but-disabled with a reason (never auto-removed).</summary>
    public bool Missing { get; init; }

    /// <summary>Bug H (playlists) / trap 5 (pins of any resolvable kind): true once the row's own Identity has
    /// actually landed — for a PLAYLIST, <see cref="PlaylistFields.Identity"/> (title, cover, owner); for an
    /// unlisted Album/Artist/Show PIN, <c>Sidebar.Host.ResolveLivePin</c>'s own <c>Knows(Identity)</c> gate on the
    /// matching entity table. Default false is the landed-safe polarity — a row minted before its identity arrives
    /// must not be mistaken for a resolved one, and (trap 5) the pin band must not fall back to a raw id/uri
    /// fragment as a title while this is false (<see cref="SidebarProjection.ShouldShowUriFallbackTitle"/>).
    /// <b>Does NOT imply the track count is known</b> — <see cref="CountKnown"/> is the bit for that (bug A1:
    /// Identity's own route can stamp it applied while carrying no length at all). Meaningless for a Folder
    /// (<see cref="Missing"/>/<see cref="CountKnown"/> are its bits) or any non-pinnable row kind.</summary>
    public bool IdentityKnown { get; init; }

    /// <summary>Bug A1: true once a PLAYLIST row's REAL track count has landed — stamped from
    /// <see cref="PlaylistFields.TrackCount"/> alone (<c>Playlist.Knows</c>), never from <see cref="IdentityKnown"/>
    /// or from <c>TrackCount == 0</c>, both of which a thin answer (ListMetadataV2, ext kind 205 — no length field)
    /// also produces. Default false is the landed-safe polarity: an unresolved row must not paint a confident
    /// "0 songs". <c>Sidebar.PaneText.SubtitleOf</c> (Sidebar.UI.Rows.cs) gates the "N songs" subtitle on this bit
    /// for a playlist. For a FOLDER pin awaiting the rootlist's first answer this session
    /// (<see cref="SidebarProjection.ResolveFolderPinState"/>, trap 5) this is also stamped false — the data side is
    /// correct, and <c>Sidebar.UI.Slot.cs</c>'s <c>FolderRow</c> gates its "N items" subtitle on it too.
    /// <para>The MEMBERSHIP fallback in <see cref="SidebarProjection.PlaylistCount"/> only counts when it carries a
    /// real number: a Partial page with no total and a Failed edge are both "resident" while knowing no length, and
    /// reading either as a count is what painted "0 songs" on every playlist.</para>
    /// Meaningless for every other kind.</summary>
    public bool CountKnown { get; init; }

    /// <summary>A playlist holding at least one episode — its subtitle counts "items", not "songs" (design P.1). Stamped by
    /// the projection from <c>Playlist.EpisodeCount</c>; false for every other kind.</summary>
    public bool HasEpisodes { get; init; }

    /// <summary>A PLAYLIST row that is the account's Your Episodes (<see cref="Playlist.IsYourEpisodes"/>): its count is
    /// episodes, not songs.</summary>
    public bool Episodes { get; init; }

    /// <summary>uri -> last-played unix ms from local + server listening history, stamped by the projection for
    /// Playlist/Album/Artist/Show rows. 0 = never played. This is what the Recents sort mode sorts on — NOT
    /// <see cref="LastVisitedTicksUtc"/>, which is navigation recency and feeds only the "recently opened" feed.</summary>
    public long LastPlayedMs { get; init; }

    // Field-backed so a default(SidebarLibraryEntry) (a scratch-list slot) still reads "" rather than null — these are
    // display strings a row concatenates without a null check.
    readonly string? _folderId;
    readonly string? _folderName;
    readonly string? _parentFolderId;
    readonly string? _parentFolderName;
    readonly string? _firstArtistName;

    /// <summary>The rootlist group id of the folder CONTAINING this row ("" at top level). For a folder row itself
    /// this is its OWN id, so a row never has to strip the "folder:" prefix off <see cref="Id"/>.</summary>
    public string FolderId { get => _folderId ?? ""; init => _folderId = value; }

    /// <summary>Display name of the folder containing this row ("" at top level; a folder row carries its own name).</summary>
    public string FolderName { get => _folderName ?? ""; init => _folderName = value; }

    /// <summary>The group id of the folder this row SITS IN ("" at top level) — for a folder row that is its PARENT,
    /// which <see cref="FolderId"/> cannot express. It is what the "Move out of {folder}" verb needs.</summary>
    public string ParentFolderId { get => _parentFolderId ?? ""; init => _parentFolderId = value; }

    /// <summary>Display name of <see cref="ParentFolderId"/> ("" at top level).</summary>
    public string ParentFolderName { get => _parentFolderName ?? ""; init => _parentFolderName = value; }

    /// <summary>Playlist ownership — gates the owner-only menu block.</summary>
    public bool IsOwner { get; init; }

    /// <summary>Whether the playlist is editable by the current user.</summary>
    public bool CanEdit { get; init; }

    /// <summary>A saved show that is an AUDIOBOOK (Spotify files both under SavedShows; the flags decide —
    /// <c>LibraryAudiobookFilter.IsAudiobookRow</c>). Splits the Podcasts and Audiobooks chips.</summary>
    public bool IsAudiobook { get; init; }

    /// <summary>An album's FIRST billed artist, uncollapsed (<see cref="Creator"/> is the joined display string). ""
    /// for every other kind. Kept as a reference to the source string — no substring allocation.</summary>
    public string FirstArtistName { get => _firstArtistName ?? ""; init => _firstArtistName = value; }

    // ── computed aliases (no storage, no second record) ────────────────────────────────────────────────────────────
    public bool IsPlayable => Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Album or SidebarEntryKind.Show
                                   or SidebarEntryKind.Track;
    public bool IsFolder => Kind == SidebarEntryKind.Folder;

    /// <summary>True for a row that PLAYS on activation instead of navigating — a track has no detail route.</summary>
    public bool IsTrack => Kind == SidebarEntryKind.Track;

    /// <summary>The nav route this row opens — <see cref="Id"/> for every navigable kind, null for a folder (expands
    /// in place; never navigates) and for a track (it plays).</summary>
    public string? RouteKey => IsFolder || IsTrack ? null : Id;

    /// <summary>The pin-store key for this row. The id IS the pin id for every pinnable kind.</summary>
    public string PinKey => Id;

    public int TrackCount => Kind is SidebarEntryKind.Playlist or SidebarEntryKind.Album ? ChildCount : 0;
    public string OwnerName => Kind == SidebarEntryKind.Playlist ? Creator : "";
    public string Publisher => Kind == SidebarEntryKind.Show ? Creator : "";
    public int FolderDepth => Depth;

    /// <summary>Qualifier-chip match. A qualifier of 0 (Any/Unknown) matches everything; the non-zero values of the
    /// Your Library chips are byte-identical to <see cref="SidebarPlaylistFlavor"/>, so one byte
    /// comparison serves them all.</summary>
    public bool MatchesQualifier(byte qualifier) => qualifier == 0 || (byte)Flavor == qualifier;

    /// <summary>An APP-ROUTE row (Home / Search / Liked / ...). Authored by the surface, never produced by the
    /// projection: the label + glyph are engine-bound and resolved by the renderer. The route key IS the id.</summary>
    public static SidebarLibraryEntry ForRoute(string routeKey, string name, int sourceOrder = 0, long lastVisitedTicksUtc = 0) =>
        new(routeKey, SidebarEntryKind.AppRoute, "", name, "", default, null,
            ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: lastVisitedTicksUtc,
            SourceOrder: sourceOrder, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = "", FolderName = "", FirstArtistName = "" };
}

// ── the plan itself: SidebarLayoutDoc + the live projection, planned into ONE flat row list ─────────────────────
/// <summary>The row vocabulary — also the ItemsView's <c>ContentType</c>, so each kind recycles in its own pool.</summary>
public enum SidebarRowKind : byte
{
    SectionHeader = 0,   // 40-px header (expanded only)
    Divider       = 2,   // the 8-px full-width separator between two rendered sections
    IconRow       = 3,   // a glyph row for an app route (Home, a Collections page); Key = the route key, EntryIndex = -1
    EntityRow     = 4,   // a projected entry (an entity, a route pin, the Library's Liked row); EntryIndex >= 0
    FolderHeader  = 5,   // a rootlist folder; disclosure in the trailing chevron column
    GridStrip     = 6,   // Library grid: [EntryIndex, ItemCount] cells
    Empty         = 8,   // a quiet one-line hint (an empty Playlists, a search with no match)
    Skeleton      = 9,   // the source is pending and nothing is known yet
    TreeEnd       = 14,  // the tree's closing drop gutter
    SectionTile   = 15,  // a collapsed section in the compact rail
    DropBand      = 16,  // empty Pinned while a pinnable drag is live: "Drop here to pin"
}

/// <summary>POD. No strings are allocated during planning: labels resolve at render time from the referenced
/// section/item/entry, never copied into the plan.
/// <para><b>How a row joins its data.</b> PROJECTED rows (Pinned, JumpBackIn, EntityList, PlaylistTree, NewReleases,
/// Concerts) carry <c>EntryIndex &gt;= 0</c> into <see cref="SidebarRowPlan.Entries"/> and <c>Key == entry.Id</c>.
/// HAND-PLACED rows (shortcuts/CustomGroup items, the EntityEmbed card) carry <c>Key == item.Key</c> and
/// <c>EntryIndex</c> is the resolved entry or -1 (missing-entity retention: render from the item's fallback,
/// dimmed). Chrome rows carry <c>Key == section.Id</c> and <c>EntryIndex == -1</c>.</para></summary>
public readonly record struct SidebarRow(
    SidebarRowKind Kind,
    string SectionId,
    byte Depth,          // 0 top-level, 1 inside a CustomGroup / rootlist folder, 2 deeper folder nesting...
    int EntryIndex,      // index into the plan's entry list; -1 when not applicable
    int ItemCount,       // GridStrip only: how many entries this strip draws
    string Key);         // the stable reconciler/selection key

/// <summary>The planner's output. <paramref name="Entries"/> is the flattened, per-section-ordered entry slices the
/// rows index into; <paramref name="Revision"/> is echoed from the input so the ItemsView can key its DepKey on it.</summary>
public readonly record struct SidebarRowPlan(
    IReadOnlyList<SidebarRow> Rows,
    IReadOnlyList<SidebarLibraryEntry> Entries,
    int Revision);

/// <summary>A source's readiness. Ordered so <c>default</c> is <c>Ready</c> — a <c>default(SidebarProjectionInput)</c>
/// must plan real (empty) content, not a screenful of skeletons.</summary>
public enum SidebarSourceState : byte { Ready = 0, Pending = 1, Error = 2 }

/// <summary>Everything outside the document the plan depends on. Every list is nullable so a headless test can omit it.</summary>
public readonly record struct SidebarProjectionInput(
    // The library projection: Classic reads it for nothing; Library reads the mode-shaped list (filtered, searched,
    // sorted, the pin band removed, tree-regrouped when folders apply).
    IReadOnlyList<SidebarLibraryEntry>? Library = null,
    // The rootlist tree, depth-first flattened, folders as SidebarEntryKind.Folder entries — Classic's Playlists.
    IReadOnlyList<SidebarLibraryEntry>? PlaylistTree = null,
    // Every pin, resolved, in pin order (entities, folders, routes, modules).
    IReadOnlyList<SidebarLibraryEntry>? Pins = null,
    // Recently played contexts, resolved (never a nameless row), newest first.
    IReadOnlyList<SidebarLibraryEntry>? Played = null,
    // New releases from followed artists, newest first.
    IReadOnlyList<SidebarLibraryEntry>? NewReleases = null,
    IReadOnlySet<string>? PinnedIds = null,
    // Expanded rootlist folder ids. null = everything expanded (the headless default).
    IReadOnlySet<string>? ExpandedFolders = null,
    // Classic's Playlists filter / Library's search, normalized by the planner.
    string? Search = null,
    SidebarSourceState LibraryState = SidebarSourceState.Ready,
    SidebarSourceState TreeState = SidebarSourceState.Ready,
    // Library: the localized "Liked Songs" title (the Liked row's label and its search match).
    string? LikedTitle = null,
    // Library: Library is a depth-stamped tree (folders inline) rather than a flat sorted list.
    bool LibraryIsTree = false,
    int Revision = 0);

/// <summary>Caller-owned row/entry storage. Hand the SAME instance to every <c>Build</c> for a given pane and a warm
/// re-plan reuses its capacity (the 10k-library alloc bound). The returned plan's lists ALIAS these buffers, so a plan
/// is only valid until the next Build on the same buffers — exactly the UseMemo lifetime it is built for.</summary>
public sealed class SidebarPlanBuffers
{
    internal readonly List<SidebarRow> Rows = new(256);
    internal readonly List<SidebarLibraryEntry> Entries = new(256);
}

// ── DROP, TREE NAV, FOLDER FLYOUT AND MULTI-SELECTION ─────────────────────────────────────────────────────────────
//
// The rootlist drag-and-drop authority, and the non-mouse verbs that share it. ONE pure resolver turns a pointer
// position over a row into a (kind, depth, refusal) slot; ONE decision layer maps an armed slot onto the real
// marker-stream destination and checks it against the SAME legality the keyboard/menu verbs and the folder picker
// ask; ONE selection model and ONE flyout stack carry the rest of non-mouse tree organisation. Nothing outside this
// block computes a placement, a refusal reason, or "which folders may this be filed into" — every surface (row,
// TreeEnd gutter, rail tile, folder picker) publishes and commits exactly what these types decide.

// ── the drop-slot resolver ─────────────────────────────────────────────────────────────────────────────────────────
//
// ONE pure function turns (row facts, t, xInRow) into a SidebarDropSlot = (kind, depth, refusal). A row renders a
// LINE (Before/After/EndOfList, indented to the resolved depth) or a PLATE (Into) — never both, never
// neither-while-armed. Refusal lifts "folder into its own descendant" / "already there" from a silent `false` up to
// the sentence the drag chip shows.

/// <summary>What a rootlist drop at the resolved position MEANS. <see cref="None"/> is "nothing is armed here" — also
/// what a REFUSED slot reports, so a refused row draws neither a line nor a plate.</summary>
public enum SidebarDropKind : byte
{
    None = 0,
    /// <summary>An insertion line ABOVE the row, at <see cref="SidebarDropSlot.Depth"/>.</summary>
    Before = 1,
    /// <summary>An insertion line BELOW the row, at <see cref="SidebarDropSlot.Depth"/> — may be SHALLOWER than the
    /// row's own depth, which is the "move out of this folder" gesture.</summary>
    After = 2,
    /// <summary>Into the row: a folder takes the item as a child, an editable playlist takes the payload's TRACKS.
    /// The plate — and the ONLY kind that draws one.</summary>
    Into = 3,
    /// <summary>The end of the whole tree, at depth 0 — the <c>TreeEnd</c> chrome row's only slot.</summary>
    EndOfList = 4,
}

/// <summary>Why a drop at this position is refused, in the order the table evaluates. Each value maps to exactly one
/// caption, because a refusing drop target is transparent to the engine and the caption is the ONLY thing that
/// reaches the user.</summary>
public enum SidebarDropRefusal : byte
{
    None = 0,
    /// <summary>The row IS the dragged item.</summary>
    Self = 1,
    /// <summary>Into the dragged FOLDER itself.</summary>
    IntoItself = 2,
    /// <summary>The row lives inside the dragged folder — filing a folder into its own subtree.</summary>
    IntoDescendant = 3,
    /// <summary>The resolved destination is where the item already is.</summary>
    NoOp = 4,
    /// <summary>The list is showing a non-custom SORT, so a positional insert cannot be honoured.</summary>
    SortedList = 5,
    /// <summary>The rootlist has not arrived, so there is no order to write into.</summary>
    NotLoaded = 6,
    /// <summary>The geometry is degenerate (no plan row, no viewport, no scene) — refuse with a reason rather than
    /// guess a placement.</summary>
    Unavailable = 7,
    /// <summary>The library write this gesture needs has no seam installed (<c>Sidebar.LibraryWrites</c> or the one
    /// member of it) — the "never a silent no-op" arm of every drop, "+" and menu verb that writes the library.</summary>
    WritesUnavailable = 8,
}

/// <summary>One localized sentence per refusal, keyed by the RAW dotted loc key (verified present in
/// <c>assets/loc/en-US.json</c> under <c>drag.*</c>). Engine-free by construction: the Pane layer wraps this with its
/// own <c>Loc.Get</c>/<c>Strings</c> lookup — the table itself, and the rule that every non-<see cref="SidebarDropRefusal.None"/>
/// value has ONE entry, lives here so a refusal can never reach the UI as a silent <c>return false</c>.</summary>
public static class SidebarDropRefusalText
{
    /// <summary>The dotted loc key for a refusal, or "" for <see cref="SidebarDropRefusal.None"/> (nothing to say).</summary>
    public static string LocKey(SidebarDropRefusal refusal) => refusal switch
    {
        SidebarDropRefusal.Self => "drag.cantMoveHere",
        SidebarDropRefusal.Unavailable => "drag.cantMoveHere",
        SidebarDropRefusal.IntoItself => "drag.cantMoveIntoItself",
        SidebarDropRefusal.IntoDescendant => "drag.cantMoveIntoItself",
        SidebarDropRefusal.NoOp => "drag.alreadyThere",
        SidebarDropRefusal.SortedList => "drag.clearSortingToReorder",
        SidebarDropRefusal.NotLoaded => "drag.stillLoading",
        SidebarDropRefusal.WritesUnavailable => "drag.libraryUnavailable",
        _ => "",
    };
}

/// <summary>Everything the resolver needs about ONE row, and nothing about the renderer. The one payload-dependent
/// member (<see cref="SourceIsSelf"/>) is computed at HOVER, because that is the first moment the payload exists; the
/// structural ones come from the plan.</summary>
/// <param name="NextVisibleDepth">Depth of the next VISIBLE tree row; 0 when this is the last tree row. Together with
/// <paramref name="Depth"/> it is the whole depth-ambiguity story: a slot is ambiguous iff the two differ downward.</param>
/// <param name="CenterAccepts">This row has a dead centre that DEPOSITS: a folder (always), or an editable playlist
/// whose centre takes the payload's tracks.</param>
/// <param name="SourceIsSelf">This row IS the dragged item. Every OTHER legality question — cycle, no-op — is the
/// marker stream's, never re-derived here.</param>
/// <param name="IsListEnd">The synthetic <c>TreeEnd</c> row: the whole row is one <see cref="SidebarDropKind.EndOfList"/>
/// slot at depth 0, with no bands at all.</param>
public readonly record struct SidebarRowFacts(
    bool IsFolder,
    bool FolderExpanded,
    bool FolderHasChildren,
    int Depth,
    int NextVisibleDepth,
    bool CenterAccepts,
    bool SourceIsSelf,
    bool SortedNonCustom,
    bool RootlistLoaded)
{
    public bool IsListEnd { get; init; }
}

/// <summary>The published slot: WHERE the drop lands, at WHAT depth, or WHY it will not.
/// <para><b>Invariant:</b> a slot with a <see cref="Refusal"/> always carries <see cref="SidebarDropKind.None"/>. The
/// cue therefore has one rule — line ⟺ Before/After/EndOfList, plate ⟺ Into — and a refusal draws neither.</para></summary>
public readonly record struct SidebarDropSlot(int PlanIndex, SidebarDropKind Kind, int Depth, SidebarDropRefusal Refusal)
{
    public static readonly SidebarDropSlot None = new(-1, SidebarDropKind.None, 0, SidebarDropRefusal.None);

    /// <summary>Is anything actually armed here (a line or a plate)?</summary>
    public bool IsArmed => Kind != SidebarDropKind.None;

    /// <summary>Delegates to <see cref="SidebarDropCue.DrawsLine"/> — two hand-written copies of "which kinds draw a
    /// line" is exactly the drift the one-rule invariant exists to prevent.</summary>
    public bool DrawsLine => SidebarDropCue.DrawsLine(Kind);

    /// <summary>Delegates to <see cref="SidebarDropCue.DrawsPlate"/> for the same reason as <see cref="DrawsLine"/>.</summary>
    public bool DrawsPlate => SidebarDropCue.DrawsPlate(Kind);
}

/// <summary>The pure geometry + legality rules behind every sidebar rootlist drop.</summary>
public static class RootlistSlotResolver
{
    /// <summary>Fraction of the row height each edge band claims, before the clamp.</summary>
    public const float EdgeFraction = 0.30f;
    /// <summary>Minimum edge-band height. Below this the band is smaller than the pointer's own precision.</summary>
    public const float MinEdge = 10f;
    /// <summary>Maximum edge-band height. Above this a 48-DIP comfortable row loses its centre entirely.</summary>
    public const float MaxEdge = 16f;
    /// <summary>Hysteresis around each depth boundary (DIP). Without it the line flickers between two depths while
    /// the hand holds still.</summary>
    public const float DepthHysteresis = 4f;

    /// <summary>The edge-band height for a row: <c>clamp(0.30·h, 10, 16)</c>, capped at half the row. ONE band for
    /// every payload and ownership state.</summary>
    public static float EdgeFor(float rowHeight)
    {
        if (!float.IsFinite(rowHeight) || rowHeight <= 0f) return MinEdge;
        float edge = rowHeight * EdgeFraction;
        if (edge < MinEdge) edge = MinEdge;
        if (edge > MaxEdge) edge = MaxEdge;
        // A degenerate (very short) row cannot carry two bands and a centre; half the row is the honest cap.
        float half = rowHeight * 0.5f;
        return edge > half ? half : edge;
    }

    /// <summary>The depths an <see cref="SidebarDropKind.After"/> slot on this row may address.
    /// <para><c>Max</c> is the row's own depth. <c>Min</c> is the next visible row's depth, clamped so it never
    /// exceeds <c>Max</c>. <c>Min &lt; Max</c> is exactly "after the last visible child of a (possibly nested)
    /// folder" — the one genuinely ambiguous place.</para></summary>
    public static (int Min, int Max) DepthRange(in SidebarRowFacts f)
    {
        int max = f.Depth < 0 ? 0 : f.Depth;
        int min = f.NextVisibleDepth < 0 ? 0 : f.NextVisibleDepth;
        if (min > max) min = max;
        return (min, max);
    }

    /// <summary>Resolve one pointer position over one row into the slot it means.</summary>
    /// <param name="planIndex">The row's plan index; negative = degenerate ⇒ <see cref="SidebarDropRefusal.Unavailable"/>.</param>
    /// <param name="t">Normalized vertical position inside the row, 0 = top edge, 1 = bottom edge.</param>
    /// <param name="xInRow">Pointer X relative to the row's own left edge — the DEPTH channel.</param>
    /// <param name="rowHeight">The row's measured extent.</param>
    /// <param name="previous">The slot published on the previous move, for depth hysteresis. Pass
    /// <see cref="SidebarDropSlot.None"/> when there is none.</param>
    public static SidebarDropSlot Resolve(int planIndex, float t, float xInRow, float rowHeight,
                                          in SidebarRowFacts f, in SidebarDropSlot previous)
    {
        if (planIndex < 0 || !float.IsFinite(t) || !float.IsFinite(rowHeight) || rowHeight <= 0f)
            return new SidebarDropSlot(planIndex, SidebarDropKind.None, 0, SidebarDropRefusal.Unavailable);

        // The zone first: two refusals below (IntoItself, SortedList) depend on WHICH zone was picked.
        var (kind, depth) = Zone(t, xInRow, rowHeight, in f, in previous);

        var refusal = Refuse(kind, in f);
        return refusal == SidebarDropRefusal.None
            ? new SidebarDropSlot(planIndex, kind, depth, SidebarDropRefusal.None)
            : new SidebarDropSlot(planIndex, SidebarDropKind.None, depth, refusal);
    }

    static SidebarDropRefusal Refuse(SidebarDropKind kind, in SidebarRowFacts f)
    {
        if (!f.RootlistLoaded) return SidebarDropRefusal.NotLoaded;
        // Into the dragged folder itself gets its OWN sentence: "can't move a folder into itself" says the thing the
        // user tried, where the generic "can't move here" would leave them guessing which rule they hit.
        if (f.SourceIsSelf) return kind == SidebarDropKind.Into ? SidebarDropRefusal.IntoItself : SidebarDropRefusal.Self;
        // A non-custom SORT cannot show a positional insert; a DEPOSIT into a folder/playlist is unaffected by sort.
        if (f.SortedNonCustom && kind is SidebarDropKind.Before or SidebarDropKind.After or SidebarDropKind.EndOfList)
            return SidebarDropRefusal.SortedList;
        return SidebarDropRefusal.None;
    }

    static (SidebarDropKind Kind, int Depth) Zone(float t, float xInRow, float rowHeight,
                                                  in SidebarRowFacts f, in SidebarDropSlot previous)
    {
        // The tree's END: one whole-row slot at the root level. No bands — there is nothing below it to be "after".
        if (f.IsListEnd) return (SidebarDropKind.EndOfList, 0);

        int depth = f.Depth < 0 ? 0 : f.Depth;
        float edge = EdgeFor(rowHeight);
        float top = edge / rowHeight;
        float bottom = 1f - top;

        if (f.IsFolder)
        {
            // Dropping ON a folder means INTO it (Explorer/Finder/VS Code/Spotify all agree).
            if (t < top) return (SidebarDropKind.Before, depth);
            if (t > bottom)
                // The bottom band of an EXPANDED header is the precise "first child" slot: the line indents one step
                // and the drop lands ahead of the folder's current first child.
                return f.FolderExpanded && f.FolderHasChildren
                    ? (SidebarDropKind.Before, depth + 1)
                    : (SidebarDropKind.After, PickDepth(xInRow, in f, in previous));
            return (SidebarDropKind.Into, depth);
        }

        if (f.CenterAccepts)
        {
            // The retained copy gesture: an editable playlist's centre deposits the payload's tracks. Survives only
            // because the PLATE now distinguishes it from the two edge bands, which draw a line.
            if (t < top) return (SidebarDropKind.Before, depth);
            if (t > bottom) return (SidebarDropKind.After, PickDepth(xInRow, in f, in previous));
            return (SidebarDropKind.Into, depth);
        }

        // Everything else: two zones, no dead centre. A row that cannot take a deposit must not reserve half its
        // height for one.
        return t < 0.5f
            ? (SidebarDropKind.Before, depth)
            : (SidebarDropKind.After, PickDepth(xInRow, in f, in previous));
    }

    /// <summary>The DEPTH channel: only an <see cref="SidebarDropKind.After"/> slot spanning more than one depth
    /// reads the pointer's X at all. The default — pointer over the row's LABEL — is <c>Max</c> ("stay at this row's
    /// depth"); travelling LEFT outdents one step per indent level.</summary>
    static int PickDepth(float xInRow, in SidebarRowFacts f, in SidebarDropSlot previous)
    {
        var (min, max) = DepthRange(in f);
        if (min >= max) return max;
        if (!float.IsFinite(xInRow)) return max;

        // THE LADDER IS THE ROW'S OWN. `TreeContentX(d)` is where a tree row at depth d starts drawing (32 per level
        // from the row origin, the pill's x) — so one step left is one outdent. There is no reserved disclosure cell
        // (the folder chevron is trailing).
        float steps = (xInRow - SidebarRowGeometry.TreeContentX(0)) / SidebarRowGeometry.IndentStep;
        int picked = (int)MathF.Round(steps);
        if (picked < min) picked = min;
        if (picked > max) picked = max;

        // HYSTERESIS. Without it the boundary between two depths sits under a still hand and the line flickers. The
        // previous depth is held until the pointer is a clear 4 DIP past the boundary that would leave it.
        if (previous.Kind == SidebarDropKind.After && previous.Depth >= min && previous.Depth <= max
            && previous.Depth != picked)
        {
            float boundary = SidebarRowGeometry.TreeContentX(0)
                + (previous.Depth + (picked > previous.Depth ? 0.5f : -0.5f)) * SidebarRowGeometry.IndentStep;
            float travelled = MathF.Abs(xInRow - boundary);
            if (travelled < DepthHysteresis) return previous.Depth;
        }
        return picked;
    }
}

/// <summary>The insertion cue's PURE geometry + its one invariant, in the engine-free layer so a test can drive the
/// real mapping.
/// <para><b>The invariant:</b> <b>line ⟺ Before/After/EndOfList, plate ⟺ Into, never both, never
/// neither-while-armed.</b> Three outcomes used to share one accent plate, so the surface could not say which of them
/// a drop meant.</para></summary>
public static class SidebarDropCue
{
    /// <summary>The caret's stroke (2 DIP).</summary>
    public const float LineThickness = 2f;
    /// <summary>Its corner radius — enough to round a 2-DIP bar's ends without reading as a pill.</summary>
    public const float LineCorner = 1f;
    /// <summary>The terminal dot at the left cap (6 DIP): what makes a hairline read as an insertion caret rather
    /// than a divider.</summary>
    public const float DotSize = 6f;

    /// <summary>Does this slot draw the line? (The one predicate the row's Opacity bind reads.)</summary>
    public static bool DrawsLine(SidebarDropKind kind)
        => kind is SidebarDropKind.Before or SidebarDropKind.After or SidebarDropKind.EndOfList;

    /// <summary>Does this slot draw the accent plate? (The one predicate the row's Fill/Border binds read.)</summary>
    public static bool DrawsPlate(SidebarDropKind kind) => kind == SidebarDropKind.Into;

    /// <summary>The caret's width: the row's content lane from <see cref="SidebarRowGeometry.TreeContentX"/> to the
    /// row's right edge. ONE origin with the caret's transform and with <c>PickDepth</c>.</summary>
    public static float LineWidth(float contentWidth, int depth)
    {
        float w = contentWidth - SidebarRowGeometry.TreeContentX(depth);
        return w > 0f ? w : 0f;
    }

    /// <summary>The caret's Y inside its row: the TOP edge for Before (and the tree's end marker, whose whole row is
    /// the slot), the bottom edge for After.</summary>
    public static float LineY(SidebarDropKind kind, float rowHeight)
        => kind is SidebarDropKind.Before or SidebarDropKind.EndOfList
            ? 0f
            : MathF.Max(0f, rowHeight - LineThickness);
}

/// <summary>Where a rootlist item CAME FROM, so a completed move can be offered back as Undo.
/// <para>Pure, over the SAME depth-first flattened tree the planner consumes (the FULL tree, not the
/// expansion-filtered plan), so the anchor is the item's real pre-move sibling and not whichever row happened to be
/// visible. Expressed as an ordinary <c>(RootlistItemRef, RootlistDropPlacement)</c> pair, so the inverse rides the
/// very same move seam the forward move did.</para></summary>
public static class RootlistUndoAnchors
{
    /// <summary>Resolve the move that would put <paramref name="entryId"/> back exactly where it is now. The N=1
    /// sugar over <see cref="TryResolveMany"/>.
    /// <para>False when there is nothing to anchor against (the item is the tree's only member, or not in the tree at
    /// all) — the caller then shows its toast WITHOUT an Undo action.</para></summary>
    public static bool TryResolve(IReadOnlyList<SidebarLibraryEntry>? tree, string entryId,
                                  out RootlistItemRef anchor, out RootlistDropPlacement placement)
    {
        anchor = default;
        placement = RootlistDropPlacement.After;
        if (!TryResolveMany(tree, [entryId], out var undo) || undo.Count != 1) return false;
        anchor = undo[0].Target;
        placement = undo[0].Placement;
        return true;
    }

    /// <summary>Resolve the BATCH that would put a whole multi-selection back exactly where it is now, computed
    /// BEFORE the move (once the rootlist has shifted, where the items used to be is unknowable). Each item anchors
    /// on its previous UNSELECTED sibling (After) → its next unselected sibling (Before) → its parent folder
    /// (Inside) — unselected siblings are the only rows guaranteed not to have moved. The emitted ORDER is
    /// <see cref="RootlistBatchOrder.For"/>'s, applied per run of items sharing an anchor. False if ANY item has no
    /// resolvable anchor: a partial Undo would scatter the rest, so the toast carries none.</summary>
    public static bool TryResolveMany(IReadOnlyList<SidebarLibraryEntry>? tree, IReadOnlyList<string> ids,
                                      out IReadOnlyList<RootlistMove> undo)
    {
        undo = Array.Empty<RootlistMove>();
        if (tree is null || ids is null || ids.Count == 0) return false;

        // The selection as the MOVE sees it: tree order, descendants of a selected folder dropped.
        var selected = RootlistSelection.Normalize(tree, ids);
        if (selected.Count == 0) return false;
        var chosen = new HashSet<string>(selected.Count, StringComparer.Ordinal);
        for (int i = 0; i < selected.Count; i++) chosen.Add(selected[i].Id);

        var sources = new List<RootlistItemRef>(selected.Count);
        var anchors = new List<(RootlistItemRef Target, RootlistDropPlacement Placement)>(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            var self = selected[i];
            var source = RefOf(self);
            if (source.Key.Length == 0) return false;
            if (!TryAnchor(tree, in self, chosen, out var target, out var placement)) return false;
            sources.Add(source);
            anchors.Add((target, placement));
        }

        var moves = new List<RootlistMove>(sources.Count);
        for (int start = 0; start < sources.Count;)
        {
            int end = start + 1;
            while (end < sources.Count && anchors[end] == anchors[start]) end++;
            var run = sources.GetRange(start, end - start);
            moves.AddRange(RootlistBatchOrder.For(run, anchors[start].Target, anchors[start].Placement));
            start = end;
        }
        if (moves.Count == 0) return false;
        undo = moves;
        return true;
    }

    /// <summary>Where ONE item came from, expressed against a row the batch is NOT moving.</summary>
    static bool TryAnchor(IReadOnlyList<SidebarLibraryEntry> tree, in SidebarLibraryEntry self,
                          HashSet<string> selected, out RootlistItemRef anchor, out RootlistDropPlacement placement)
    {
        anchor = default;
        placement = RootlistDropPlacement.After;

        int at = -1;
        for (int i = 0; i < tree.Count; i++)
            if (string.Equals(tree[i].Id, self.Id, StringComparison.Ordinal)) { at = i; break; }
        if (at < 0) return false;
        int depth = self.Depth;

        // The PREVIOUS unselected sibling — the exact anchor whenever one exists.
        for (int i = at - 1; i >= 0; i--)
        {
            int d = tree[i].Depth;
            if (d > depth) continue;                 // a descendant of an earlier sibling
            if (d < depth) break;                    // the parent: this item is the first (unselected-wise) child
            if (selected.Contains(tree[i].Id)) continue;
            anchor = RefOf(tree[i]);
            placement = RootlistDropPlacement.After;
            return anchor.Key.Length > 0;
        }

        // First among its unselected siblings: anchor on the next one instead.
        for (int i = at + 1; i < tree.Count; i++)
        {
            int d = tree[i].Depth;
            if (d > depth) continue;                 // this item's own subtree, or a later sibling's
            if (d < depth) break;                    // out of the folder: there is no next sibling
            if (selected.Contains(tree[i].Id)) continue;
            anchor = RefOf(tree[i]);
            placement = RootlistDropPlacement.Before;
            return anchor.Key.Length > 0;
        }

        // The only UNSELECTED-adjacent child of its folder: append back into that folder.
        if (self.ParentFolderId is { Length: > 0 } parent)
        {
            anchor = new RootlistItemRef(parent, IsFolder: true);
            placement = RootlistDropPlacement.Inside;
            return true;
        }
        return false;   // the only top-level item — there is no move to undo
    }

    // The rootlist reference one entry moves AS is ONE rule and lives in RootlistTreeNav.RefOf.
    static RootlistItemRef RefOf(in SidebarLibraryEntry entry) => RootlistTreeNav.RefOf(in entry);
}

// ── the drop decision: MAP then CHECK ─────────────────────────────────────────────────────────────────────────────
//
// THE ONE ANSWER TO "where does this drop land, and may it?". MAP turns the geometry cue plus the FULL projection
// tree into a destination; CHECK asks the store's own marker stream via RootlistOps — the same authority the
// keyboard/menu verbs and the folder picker use, so nothing here can disagree with them. A cue that cannot be mapped
// is never armed; it publishes Unavailable rather than a placement that would silently drop nothing.

/// <summary>A resolved rootlist destination: the tree entry it is expressed against, the seam ref + placement the
/// mutation rides, and the folder the item will END UP IN.
/// <para><see cref="DestinationName"/> is the toast's subject, computed from the MAPPED TARGET (never from whichever
/// row the pointer happened to be over). Empty = the top level.</para>
/// <para><see cref="Deposit"/> marks the one slot that is not a rootlist move at all — dropping a playlist on an
/// editable playlist's centre to copy its songs.</para></summary>
/// <param name="AnchorName">The name of the entry the placement is expressed AGAINST — for an outdent, the folder
/// the item is leaving. Distinct from <paramref name="DestinationName"/>: they used to be answered by the same
/// (wrong) field.</param>
public readonly record struct RootlistSlotTarget(string EntryId, RootlistItemRef Ref, RootlistDropPlacement Placement,
                                          string DestinationName, string AnchorName, bool Deposit);

/// <summary>Cue + tree → destination. GEOMETRY IS NOT LEGALITY: nothing here refuses a move, and nothing here reads
/// the visible (expansion-filtered) plan.</summary>
public static class RootlistSlotMapper
{
    /// <summary>Map one armed cue onto the rootlist destination it means. <paramref name="rowEntryId"/> is the entry
    /// behind the hovered plan row ("" for the synthetic <c>TreeEnd</c> row). <paramref name="tree"/> must be the
    /// FULL flattened tree (collapsed subtrees included) so "first child" and the outdent walk still resolve inside
    /// a collapsed folder. False = no destination; the caller must publish <c>Unavailable</c>, never an armed slot.</summary>
    public static bool TryMap(in SidebarDropSlot cue, string rowEntryId,
                              IReadOnlyList<SidebarLibraryEntry>? tree, out RootlistSlotTarget target)
    {
        target = default;
        if (tree is null || tree.Count == 0) return false;

        // The tree's END marker is CHROME: it stands for no entity, so it resolves before the row lookup. Its anchor
        // is the last TOP-LEVEL entry, whose exclusive range end lands after a trailing folder's whole subtree.
        if (cue.Kind == SidebarDropKind.EndOfList)
        {
            if (!TryLastTopLevel(tree, out var last)) return false;
            target = new RootlistSlotTarget(last.Id, RootlistTreeNav.RefOf(in last),
                                            RootlistDropPlacement.After, "", last.Name, false);
            return true;
        }

        if (!RootlistTreeNav.TryEntry(tree, rowEntryId, out var entry)) return false;

        switch (cue.Kind)
        {
            case SidebarDropKind.Into:
                // A PLAYLIST's centre takes the payload's TRACKS — not a rootlist move at all. A FOLDER's centre
                // takes the item as a child.
                target = entry.IsFolder
                    ? new RootlistSlotTarget(entry.Id, RootlistTreeNav.RefOf(in entry),
                                             RootlistDropPlacement.Inside, entry.Name, entry.Name, false)
                    : new RootlistSlotTarget(entry.Id, default, RootlistDropPlacement.Inside, entry.Name, entry.Name, true);
                return true;

            case SidebarDropKind.Before when cue.Depth > entry.Depth && entry.IsFolder:
                // The bottom band of an EXPANDED folder header: the precise "first child" slot. An EMPTY folder has
                // no child to land before, so "inside it" is the same place.
                if (TryFirstChild(tree, in entry, out var firstChild))
                {
                    target = new RootlistSlotTarget(firstChild.Id, RootlistTreeNav.RefOf(in firstChild),
                                                    RootlistDropPlacement.Before, entry.Name, firstChild.Name, false);
                    return true;
                }
                target = new RootlistSlotTarget(entry.Id, RootlistTreeNav.RefOf(in entry),
                                                RootlistDropPlacement.Inside, entry.Name, entry.Name, false);
                return true;

            case SidebarDropKind.Before:
                target = new RootlistSlotTarget(entry.Id, RootlistTreeNav.RefOf(in entry),
                                                RootlistDropPlacement.Before, entry.ParentFolderName, entry.Name, false);
                return true;

            case SidebarDropKind.After when cue.Depth < entry.Depth:
                // THE OUTDENT: "after the last child of a folder", aimed left, means AFTER THE FOLDER. Expressing it
                // against the CHILD (what the shifted depth pick forced) lands it straight back inside.
                if (!TryAncestorFolder(tree, in entry, entry.Depth - cue.Depth, out var ancestor)) return false;
                target = new RootlistSlotTarget(ancestor.Id, RootlistTreeNav.RefOf(in ancestor),
                                                RootlistDropPlacement.After, ancestor.ParentFolderName, ancestor.Name, false);
                return true;

            case SidebarDropKind.After:
                target = new RootlistSlotTarget(entry.Id, RootlistTreeNav.RefOf(in entry),
                                                RootlistDropPlacement.After, entry.ParentFolderName, entry.Name, false);
                return true;

            default:
                return false;
        }
    }

    /// <summary>The folder's FIRST child in the full tree — the entry directly after it, one level deeper.</summary>
    static bool TryFirstChild(IReadOnlyList<SidebarLibraryEntry> tree, in SidebarLibraryEntry folder,
                              out SidebarLibraryEntry child)
    {
        child = default;
        for (int i = 0; i < tree.Count; i++)
        {
            if (!string.Equals(tree[i].Id, folder.Id, StringComparison.Ordinal)) continue;
            if (i + 1 >= tree.Count) return false;
            var next = tree[i + 1];
            if (next.Depth != folder.Depth + 1) return false;      // the folder is empty (or collapsed out of the tree)
            child = next;
            return true;
        }
        return false;
    }

    /// <summary>Walk <paramref name="levels"/> containing folders up from <paramref name="entry"/>, over the FULL tree.</summary>
    static bool TryAncestorFolder(IReadOnlyList<SidebarLibraryEntry> tree, in SidebarLibraryEntry entry, int levels,
                                  out SidebarLibraryEntry folder)
    {
        folder = entry;
        if (levels <= 0) return false;
        for (int i = 0; i < levels; i++)
        {
            if (folder.ParentFolderId is not { Length: > 0 } parent
                || !RootlistTreeNav.TryFolder(tree, parent, out folder)) return false;
        }
        return folder.IsFolder;
    }

    /// <summary>The last TOP-LEVEL tree entry — the anchor "move to the end" files against.</summary>
    static bool TryLastTopLevel(IReadOnlyList<SidebarLibraryEntry> tree, out SidebarLibraryEntry entry)
    {
        entry = default;
        for (int i = tree.Count - 1; i >= 0; i--)
            if (tree[i].Depth == 0) { entry = tree[i]; return true; }
        return false;
    }
}

/// <summary>THE published-slot decision: map, then check against the marker stream, then refuse or arm. Every
/// rootlist drop surface publishes what this returns and commits the target it hands back, so a cue and its mutation
/// cannot describe different destinations.</summary>
public static class RootlistDropDecision
{
    /// <summary>The ONE <see cref="RootlistMoveCheck"/> → <see cref="SidebarDropRefusal"/> table. Every one of these
    /// used to be a silent <c>false</c> three layers below the pointer.</summary>
    public static SidebarDropRefusal RefusalFor(RootlistMoveCheck check) => check switch
    {
        RootlistMoveCheck.Ok => SidebarDropRefusal.None,
        RootlistMoveCheck.NoOp => SidebarDropRefusal.NoOp,
        RootlistMoveCheck.Cycle => SidebarDropRefusal.IntoDescendant,
        RootlistMoveCheck.SameItem => SidebarDropRefusal.Self,
        // Missing (source/target not in the stream) and Invalid (a placement the stream cannot express) are both
        // "this position has no meaning" — the honest refusal, never a guessed placement.
        _ => SidebarDropRefusal.Unavailable,
    };

    /// <summary>Refine a GEOMETRY cue into the slot the surface publishes, and hand back the destination it commits.
    /// An unarmed cue passes through untouched; an armed one with no mapping is DISARMED with
    /// <see cref="SidebarDropRefusal.Unavailable"/> rather than left publishing a slot nothing could map. A mapped
    /// ordering is then checked against <paramref name="markers"/>.</summary>
    /// <param name="sources">The dragged items as the rootlist addresses them, in TREE ORDER (one item = a list of
    /// one). A source that IS the hovered target does NOT refuse the batch — it drops as a legal GATHER; hovering one
    /// of your OWN rows still says <see cref="SidebarDropRefusal.Self"/>, from the resolver's <c>SourceIsSelf</c>
    /// fact, which refuses the cue BEFORE it reaches here.</param>
    /// <param name="markers">The store's marker stream. Null/empty refuses every ordering
    /// <see cref="SidebarDropRefusal.Unavailable"/> rather than arming against indices nobody has.</param>
    public static SidebarDropSlot Refine(in SidebarDropSlot cue, string rowEntryId,
                                         IReadOnlyList<SidebarLibraryEntry>? tree,
                                         IReadOnlyList<RootlistEntry>? markers,
                                         IReadOnlyList<RootlistItemRef> sources, out RootlistSlotTarget target)
    {
        target = default;
        if (!cue.IsArmed) return cue;
        if (!RootlistSlotMapper.TryMap(in cue, rowEntryId, tree, out target))
            return new SidebarDropSlot(cue.PlanIndex, SidebarDropKind.None, cue.Depth, SidebarDropRefusal.Unavailable);
        if (target.Deposit) return cue;                       // a track copy is not a rootlist ORDERING at all

        var refusal = RefusalFor(Check(markers, sources, target.Ref, target.Placement,
                                       cue.Kind == SidebarDropKind.EndOfList));
        return refusal == SidebarDropRefusal.None
            ? cue
            : new SidebarDropSlot(cue.PlanIndex, SidebarDropKind.None, cue.Depth, refusal);
    }

    /// <summary>The legality question, asked of the ONE authority. Every caller — hover, commit, the rail tile's
    /// accept predicate, the folder picker, the bridge — goes through this.
    /// <para>The batch it asks about is <see cref="RootlistBatchOrder.For"/>'s — the SAME ordered move list the
    /// commit issues, so the cue cannot answer a different question from the write.</para></summary>
    public static RootlistMoveCheck Check(IReadOnlyList<RootlistEntry>? markers,
                                          IReadOnlyList<RootlistItemRef>? sources,
                                          RootlistItemRef target, RootlistDropPlacement placement,
                                          bool endOfList = false)
    {
        if (markers is null || markers.Count == 0) return RootlistMoveCheck.Missing;
        if (sources is null || sources.Count == 0 || target.Key.Length == 0) return RootlistMoveCheck.Missing;
        for (int i = 0; i < sources.Count; i++)
            if (sources[i].Key.Length == 0) return RootlistMoveCheck.Missing;
        return RootlistOps.CheckMoves(markers, RootlistBatchOrder.For(sources, target, placement, endOfList));
    }

    /// <summary>The N=1 sugar. One item is a batch of one, so it answers through the very same builder.</summary>
    public static RootlistMoveCheck Check(IReadOnlyList<RootlistEntry>? markers, RootlistItemRef source,
                                          RootlistItemRef target, RootlistDropPlacement placement)
        => Check(markers, [source], target, placement);
}

// ── tree navigation: siblings, folder picker, selection and batch order ──────────────────────────────────────────────
//
// The non-mouse half of sidebar organisation. A menu verb, an Alt+arrow accelerator and the folder picker all ask the
// SAME depth-first flattened tree the drop mapper resolves against: who are my siblings, where am I among them, and
// which folders may I be filed into. STRUCTURE is answered here; LEGALITY is asked of RootlistDropDecision.Check —
// the very same authority the drop cue refuses with — so the picker can never offer a destination a drag would
// refuse. Every verb these answer feed commits through the ONE seam a drop uses, so the menu, keyboard and pointer
// cannot disagree about what a move is or what its Undo restores.

/// <summary>Where one entry sits among its SIBLINGS (the entries sharing its parent folder), and the two neighbours a
/// Move up / Move down addresses.
/// <para><see cref="Previous"/>/<see cref="Next"/> carry an empty <c>Key</c> when the run has no neighbour on that
/// side — exactly when the verb is ABSENT from the menu rather than present-and-dead.</para></summary>
public readonly record struct RootlistSiblingRun(int Position, int Count, RootlistItemRef Previous, RootlistItemRef Next)
{
    /// <summary>"This entry is not in the tree" — no run, and therefore no move verbs at all.</summary>
    public static readonly RootlistSiblingRun None =
        new(-1, 0, new RootlistItemRef("", false), new RootlistItemRef("", false));

    public bool IsEmpty => Position < 0;

    /// <summary>There is a previous sibling to land BEFORE.</summary>
    public bool CanMoveUp => Position > 0 && Previous.Key.Length > 0;

    /// <summary>There is a next sibling to land AFTER.</summary>
    public bool CanMoveDown => Position >= 0 && Position < Count - 1 && Next.Key.Length > 0;
}

/// <summary>Which move verbs a rootlist TREE row's menu offers.</summary>
public readonly record struct SidebarTreeNavLayout(bool MoveUp, bool MoveDown, bool MoveToFolder)
{
    public bool IsEmpty => !MoveUp && !MoveDown && !MoveToFolder;

    /// <summary>Verbs at the ENDS of the run are absent, never disabled: "Move up" on the first sibling would be a
    /// promise the command refuses. "Move to folder…" survives at both ends, but not when the picker would have
    /// nowhere to offer.</summary>
    public static SidebarTreeNavLayout Decide(in RootlistSiblingRun run, bool hasDestinations)
        => new(run.CanMoveUp, run.CanMoveDown, hasDestinations);
}

/// <summary>One row of the "Move to folder…" picker: a real folder, or the pinned TOP LEVEL row (<see cref="FolderId"/>
/// empty, and <see cref="Name"/> empty because its label is localized chrome the pure layer must not resolve).
/// <see cref="Depth"/> is the folder's own tree depth, rendered as indentation.</summary>
public readonly record struct RootlistFolderChoice(string FolderId, string Name, int Depth)
{
    /// <summary>The pinned "Top level" row — Your Library's own end, the one destination that is not a folder.</summary>
    public bool IsTopLevel => FolderId.Length == 0;
}

/// <summary>Sibling runs, folder destinations and the top-level anchor — the three pure questions behind the
/// sidebar's keyboard/menu organisation verbs.</summary>
public static class RootlistTreeNav
{
    /// <summary>Where <paramref name="entryId"/> sits among its siblings, and the neighbours on either side.
    /// <para>Siblings are the entries sharing this one's <c>ParentFolderId</c> ("" at top level) in tree order — NOT
    /// "the entries at the same depth", which would fuse two different folders' children into one run.</para></summary>
    public static RootlistSiblingRun Siblings(IReadOnlyList<SidebarLibraryEntry>? tree, string entryId)
    {
        if (tree is null || tree.Count == 0 || string.IsNullOrEmpty(entryId)) return RootlistSiblingRun.None;

        string parent = "";
        bool found = false;
        for (int i = 0; i < tree.Count; i++)
        {
            if (!string.Equals(tree[i].Id, entryId, StringComparison.Ordinal)) continue;
            parent = tree[i].ParentFolderId;
            found = true;
            break;
        }
        if (!found) return RootlistSiblingRun.None;

        int position = -1, count = 0;
        var previous = new RootlistItemRef("", false);
        var next = new RootlistItemRef("", false);
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (!string.Equals(e.ParentFolderId, parent, StringComparison.Ordinal)) continue;
            if (string.Equals(e.Id, entryId, StringComparison.Ordinal)) { position = count; }
            else if (position < 0) previous = RefOf(in e);            // the last sibling seen BEFORE us
            else if (next.Key.Length == 0) next = RefOf(in e);        // the first sibling seen after us
            count++;
        }
        return position < 0 ? RootlistSiblingRun.None : new RootlistSiblingRun(position, count, previous, next);
    }

    /// <summary>EVERY destination the "Move to folder…" picker offers, in render order: the pinned <b>Top level</b>
    /// row first, then the legal folders in tree order.</summary>
    public static void PickerDestinations(IReadOnlyList<SidebarLibraryEntry>? tree,
                                          IReadOnlyList<RootlistEntry>? markers, IReadOnlyList<string> sourceIds,
                                          List<RootlistFolderChoice> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        var sources = RootlistSelection.Refs(RootlistSelection.Normalize(tree, sourceIds));
        if (sources.Count == 0) return;
        if (TryTopLevelAnchor(tree, markers, sourceIds, out _)) into.Add(new RootlistFolderChoice("", "", 0));
        FolderChoices(tree, markers, sources, into);
    }

    /// <summary>The N=1 sugar. A selection of one is a batch of one.</summary>
    public static void PickerDestinations(IReadOnlyList<SidebarLibraryEntry>? tree,
                                          IReadOnlyList<RootlistEntry>? markers, string sourceId,
                                          List<RootlistFolderChoice> into)
        => PickerDestinations(tree, markers, [sourceId], into);

    /// <summary>Is there anywhere at all to file <paramref name="sourceIds"/>? The allocation-free question behind
    /// the menu's "Move to folder…" row — a verb that would open an EMPTY picker must be absent, not present and
    /// useless.</summary>
    public static bool HasDestinations(IReadOnlyList<SidebarLibraryEntry>? tree,
                                       IReadOnlyList<RootlistEntry>? markers, IReadOnlyList<string> sourceIds)
    {
        if (tree is null) return false;
        var sources = RootlistSelection.Refs(RootlistSelection.Normalize(tree, sourceIds));
        if (sources.Count == 0) return false;
        if (TryTopLevelAnchor(tree, markers, sourceIds, out _)) return true;
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (!e.IsFolder || e.FolderId.Length == 0) continue;
            if (RootlistDropDecision.Check(markers, sources, RefOf(in e), RootlistDropPlacement.Inside)
                == RootlistMoveCheck.Ok) return true;
        }
        return false;
    }

    /// <summary>The N=1 sugar (the row menu's "Move to folder…" arm asks about exactly one row).</summary>
    public static bool HasDestinations(IReadOnlyList<SidebarLibraryEntry>? tree,
                                       IReadOnlyList<RootlistEntry>? markers, string sourceId)
        => HasDestinations(tree, markers, [sourceId]);

    /// <summary>The folders <paramref name="sources"/> may be filed into, in tree order, APPENDED to
    /// <paramref name="into"/> (caller-owned, so the picker's list costs no allocation per keystroke).
    /// <para>Legality is <see cref="RootlistDropDecision.Check"/> — the SAME authority the drop cue refuses with —
    /// so the picker cannot offer a destination a drag would refuse.</para></summary>
    static void FolderChoices(IReadOnlyList<SidebarLibraryEntry>? tree, IReadOnlyList<RootlistEntry>? markers,
                              IReadOnlyList<RootlistItemRef> sources, List<RootlistFolderChoice> into)
    {
        if (tree is null || tree.Count == 0 || sources.Count == 0) return;
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (!e.IsFolder || e.FolderId.Length == 0) continue;
            if (RootlistDropDecision.Check(markers, sources, RefOf(in e), RootlistDropPlacement.Inside)
                != RootlistMoveCheck.Ok) continue;
            into.Add(new RootlistFolderChoice(e.FolderId, e.Name, e.Depth));
        }
    }

    /// <summary>The anchor behind the picker's pinned <b>Top level</b> row: the LAST top-level entry, landed After.
    /// <para>False when there is no such move to make — the tree is empty, or the source is itself the last
    /// top-level entry. The row is then absent rather than dead.</para></summary>
    public static bool TryTopLevelAnchor(IReadOnlyList<SidebarLibraryEntry>? tree,
                                         IReadOnlyList<RootlistEntry>? markers, IReadOnlyList<string> sourceIds,
                                         out RootlistItemRef anchor)
    {
        anchor = new RootlistItemRef("", false);
        if (tree is null || tree.Count == 0) return false;
        var sources = RootlistSelection.Refs(RootlistSelection.Normalize(tree, sourceIds));
        if (sources.Count == 0) return false;
        int last = -1;
        for (int i = 0; i < tree.Count; i++)
            if (tree[i].Depth == 0) last = i;
        if (last < 0) return false;
        var entry = tree[last];
        // The batch rides the SAME anchor: the last top-level entry, landed After. A selection that CONTAINS that
        // entry is not refused — the builder drops the self-pair as a legal GATHER.
        if (RootlistDropDecision.Check(markers, sources, RefOf(in entry), RootlistDropPlacement.After,
                                       endOfList: true) != RootlistMoveCheck.Ok) return false;
        anchor = RefOf(in entry);
        return anchor.Key.Length > 0;
    }

    /// <summary>The N=1 sugar.</summary>
    public static bool TryTopLevelAnchor(IReadOnlyList<SidebarLibraryEntry>? tree,
                                         IReadOnlyList<RootlistEntry>? markers, string sourceId,
                                         out RootlistItemRef anchor)
        => TryTopLevelAnchor(tree, markers, [sourceId], out anchor);

    /// <summary>The entry with this id, or false. One linear scan — a menu open is not a hot path.</summary>
    public static bool TryEntry(IReadOnlyList<SidebarLibraryEntry>? tree, string entryId, out SidebarLibraryEntry entry)
    {
        entry = default;
        if (tree is null || string.IsNullOrEmpty(entryId)) return false;
        for (int i = 0; i < tree.Count; i++)
        {
            if (!string.Equals(tree[i].Id, entryId, StringComparison.Ordinal)) continue;
            entry = tree[i];
            return true;
        }
        return false;
    }

    /// <summary>The FOLDER entry with this group id, or false — the "which folder am I in" lookup behind Move out of.</summary>
    public static bool TryFolder(IReadOnlyList<SidebarLibraryEntry>? tree, string folderId, out SidebarLibraryEntry folder)
    {
        folder = default;
        if (tree is null || string.IsNullOrEmpty(folderId)) return false;
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (!e.IsFolder || !string.Equals(e.FolderId, folderId, StringComparison.Ordinal)) continue;
            folder = e;
            return true;
        }
        return false;
    }

    /// <summary>THE rootlist reference one entry moves AS — a folder by its group id, a playlist by its uri. The ONE
    /// owner for the entry form (<c>RootlistUndoAnchors</c> resolves through this rather than re-deriving it).</summary>
    public static RootlistItemRef RefOf(in SidebarLibraryEntry entry)
        => entry.IsFolder
            ? new RootlistItemRef(entry.FolderId, IsFolder: true)
            : new RootlistItemRef(entry.Uri, IsFolder: false);
}

/// <summary>THE SELECTION → SOURCES rule. A multi-select is not "the ids the user clicked": it is those ids in TREE
/// ORDER with every descendant of a selected FOLDER dropped, because a folder already carries its subtree with it and
/// moving a child riding inside its own parent is a move against an index that will not exist once the parent's op
/// has been applied.
/// <para>The pane's drag source, the row menu's "Move {n} to folder…" and the folder picker all normalise through
/// this ONE function, so the payload, the cue and the commit can never disagree about which items a gesture is
/// carrying.</para></summary>
public static class RootlistSelection
{
    /// <summary>The selected entries in TREE ORDER, with the descendants of any selected folder removed.</summary>
    public static IReadOnlyList<SidebarLibraryEntry> Normalize(IReadOnlyList<SidebarLibraryEntry>? tree,
                                                              IReadOnlySet<string>? ids)
    {
        if (tree is null || tree.Count == 0 || ids is null || ids.Count == 0)
            return Array.Empty<SidebarLibraryEntry>();

        var picked = new List<SidebarLibraryEntry>(ids.Count);
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            if (!ids.Contains(e.Id)) continue;
            picked.Add(e);
            if (!e.IsFolder) continue;
            // The whole subtree rides WITH the folder: skip every following row deeper than it, selected or not.
            int depth = e.Depth;
            while (i + 1 < tree.Count && tree[i + 1].Depth > depth) i++;
        }
        return picked;
    }

    /// <summary>The id-list overload — the shape a menu verb and the picker hold. Same rule, one HashSet.</summary>
    public static IReadOnlyList<SidebarLibraryEntry> Normalize(IReadOnlyList<SidebarLibraryEntry>? tree,
                                                              IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count == 0) return Array.Empty<SidebarLibraryEntry>();
        var set = new HashSet<string>(ids.Count, StringComparer.Ordinal);
        for (int i = 0; i < ids.Count; i++) set.Add(ids[i]);
        return Normalize(tree, set);
    }

    /// <summary>The seam refs of an ORDERED entry run (<see cref="RootlistTreeNav.RefOf"/>). Entries with no
    /// addressable ref drop out rather than reaching the seam as an empty key.</summary>
    public static IReadOnlyList<RootlistItemRef> Refs(IReadOnlyList<SidebarLibraryEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return Array.Empty<RootlistItemRef>();
        var refs = new List<RootlistItemRef>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var r = RootlistTreeNav.RefOf(entries[i]);
            if (r.Key.Length > 0) refs.Add(r);
        }
        return refs;
    }
}

/// <summary>THE SAME-TARGET ORDERING RULE — the one reason a batch move is not N separate drops. Every move in a
/// batch lands ADJACENT to the SAME anchor, so the issue order decides the end order: <b>Before(t)</b>/<b>Inside(folder)</b>
/// iterate sources in TREE ORDER (each lands just before the anchor, or appended last); <b>After(t)</b> — including
/// the tree's END slot — iterates in REVERSE tree order, since each op lands just after the anchor and therefore
/// ahead of everything issued before it. E.g. on <c>[A,B,C,D,E]</c> selecting <c>{B,D}</c>, <c>After E</c> reversed
/// (D then B) gives <c>[A,C,E,B,D]</c> — forward would wrongly give <c>[A,C,E,D,B]</c>.</summary>
public static class RootlistBatchOrder
{
    /// <summary>The ordered batch one drop / one menu verb issues, ready for the move seam.</summary>
    /// <param name="orderedSources">The selection in TREE ORDER (<see cref="RootlistSelection"/>).</param>
    /// <param name="endOfList">This is the tree's END slot — already expressed as <see cref="RootlistDropPlacement.After"/>
    /// the last top-level entry, so it reverses for the same reason; the flag states the intent rather than relying
    /// on that coincidence.</param>
    public static IReadOnlyList<RootlistMove> For(IReadOnlyList<RootlistItemRef>? orderedSources,
                                                 RootlistItemRef target, RootlistDropPlacement placement,
                                                 bool endOfList = false)
    {
        if (orderedSources is null || orderedSources.Count == 0) return Array.Empty<RootlistMove>();
        bool reverse = endOfList || placement == RootlistDropPlacement.After;
        var moves = new List<RootlistMove>(orderedSources.Count);
        for (int i = 0; i < orderedSources.Count; i++)
        {
            var source = orderedSources[reverse ? orderedSources.Count - 1 - i : i];
            if (source.Key.Length == 0) continue;
            moves.Add(new RootlistMove(source, target, placement));
        }
        return moves;
    }
}

// ── the folder flyout's drill-in stack ────────────────────────────────────────────────────────────────────────────
//
// The pure half of the collapsed rail's folder flyout. Unlike the concert-date flyout's single Signal<int> (two
// fixed levels), a folder flyout is unbounded: the stack remembers the NAME of every level it came through, refuses a
// cycle, and mints a stable page key for the slide.

/// <summary>Direct-child lookups over the flattened depth-first rootlist tree the projection publishes.
/// <para><b>Containment is <see cref="SidebarLibraryEntry.ParentFolderId"/>, and only that.</b> A row's
/// <c>FolderId</c> means two different things by kind — for a LEAF it is the folder it sits in, but for a FOLDER it
/// is that folder's OWN group id — so a lookup written against it silently makes every folder its own child.</para>
/// <para><see cref="ChildCount"/> is the count of exactly the list <see cref="Children"/> fills, so the rows and the
/// "N items" caption cannot disagree.</para></summary>
public static class SidebarFolderTree
{
    /// <summary>Fill <paramref name="into"/> (CLEARED first) with the DIRECT children of <paramref name="folderId"/>,
    /// in rootlist order — sub-folders and leaves interleaved exactly as the tree carries them. Returns the count.</summary>
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
    /// <see cref="Children"/> fills, computed without one.</summary>
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

/// <summary>The flyout's drill-in STACK: one page at a time, forward slides in, back mirrors, generalised to an
/// unbounded folder chain.
/// <para>Mutable and NOT thread-safe by design: owned by one flyout component on the UI thread. Every mutator returns
/// whether it CHANGED anything, so the component can bump its render epoch only on a real move.</para></summary>
public sealed class SidebarFolderFlyoutNav
{
    /// <summary>One level of the stack. The name is carried, not re-resolved: the folder may be renamed or deleted
    /// while the flyout is open, and the back header must still name the level the user actually came through.</summary>
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

    /// <summary>The level <see cref="Pop"/> would return to, or the root when there is none.</summary>
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

    /// <summary>Back one level. False at the root — the caller decides what a back gesture means there.</summary>
    public bool Pop()
    {
        if (_stack.Count <= 1) return false;
        _stack.RemoveAt(_stack.Count - 1);
        return true;
    }

    /// <summary>The reconciler KEY for the level being shown. Depth is deliberately part of it: pushing A→B→A is
    /// refused, but popping to a level and drilling into a DIFFERENT folder must still read as a forward move, and
    /// two levels sharing an id would otherwise reconcile as one page and skip the slide.</summary>
    public string PageKey => Depth.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Current.FolderId;
}

// ── the tree's multi-selection ────────────────────────────────────────────────────────────────────────────────────
//
// Pure, engine-free, keyed by ROW ID rather than index — the sidebar's tree re-flows under the user constantly (a
// folder collapses, a projection lands, a search filters), so an index selected one frame names a different playlist
// the next. Ports WinUI's EXTENDED selector arm rule for rule: Shift replaces the selection with the anchor range,
// Ctrl toggles, a plain interaction clears-and-selects unless the item is already selected. The visible order is an
// ARGUMENT, never state — the caller passes the current plan order into the two operations that need it.

public sealed class SidebarTreeSelection
{
    readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    readonly List<string> _scratch = new();
    string? _anchor;
    bool _checkMode;

    /// <summary>How many rows are selected.</summary>
    public int Count => _ids.Count;

    /// <summary>The selected ids as a SET — the shape <c>RootlistSelection.Normalize</c> takes. Live, not a copy.</summary>
    public IReadOnlySet<string> Ids => _ids;

    /// <summary>The Shift-range anchor (WinUI <c>SelectionModel.AnchorIndex</c>, by key). Null = none.</summary>
    public string? Anchor => _anchor;

    /// <summary>Is the user in explicit CHECK MODE (entered from the row menu's "Select")? Distinct from
    /// <see cref="CheckLaneVisible"/>: check mode survives the selection emptying, which lets a user turn the lane on
    /// and then pick their first row.</summary>
    public bool CheckMode => _checkMode;

    /// <summary>Is the checkbox lane on screen? Explicit check mode, OR two or more rows selected — one selected row
    /// is still "the row I clicked", two is a set and needs a visible handle.</summary>
    public bool CheckLaneVisible => _checkMode || _ids.Count >= 2;

    public bool Contains(string? id) => id is { Length: > 0 } && _ids.Contains(id);

    /// <summary>WinUI's EXTENDED interaction, by key. Shift replaces the selection with the range from the anchor;
    /// Ctrl toggles; a plain interaction clears and selects, and is a NO-OP on a row that is already selected (which
    /// is what makes "click one of my five selected rows and drag" possible at all).
    /// <para>A Shift with no resolvable anchor degrades to selecting just this row rather than doing nothing.</para></summary>
    public bool Interact(string id, bool ctrl, bool shift, IReadOnlyList<string>? visibleOrder)
    {
        if (id is not { Length: > 0 }) return false;
        if (shift) return SelectRangeTo(id, visibleOrder);
        if (ctrl) return Toggle(id);
        if (_ids.Count == 1 && _ids.Contains(id)) { _anchor = id; return false; }
        return Replace(id);
    }

    /// <summary>Ctrl-click: add or remove this one row. The anchor follows it either way.</summary>
    public bool Toggle(string id)
    {
        if (id is not { Length: > 0 }) return false;
        _anchor = id;
        return _ids.Contains(id) ? _ids.Remove(id) : _ids.Add(id);
    }

    /// <summary>Shift-click: REPLACE the selection with the inclusive range between the anchor and <paramref name="id"/>
    /// over the currently visible tree order. The anchor does not move (a second Shift-click re-ranges from the same
    /// origin instead of walking).</summary>
    public bool SelectRangeTo(string id, IReadOnlyList<string>? visibleOrder)
    {
        if (id is not { Length: > 0 }) return false;
        int to = IndexOf(visibleOrder, id);
        int from = _anchor is { Length: > 0 } a ? IndexOf(visibleOrder, a) : -1;
        if (to < 0 || from < 0) return Replace(id);          // the anchor left the tree — select just this row

        if (from > to) (from, to) = (to, from);
        _scratch.Clear();
        for (int i = from; i <= to; i++) _scratch.Add(visibleOrder![i]);
        bool changed = _scratch.Count != _ids.Count;
        if (!changed)
            for (int i = 0; i < _scratch.Count && !changed; i++) changed = !_ids.Contains(_scratch[i]);
        if (!changed) return false;
        _ids.Clear();
        for (int i = 0; i < _scratch.Count; i++) _ids.Add(_scratch[i]);
        return true;   // _anchor deliberately unchanged
    }

    /// <summary>Empty the selection AND leave check mode — the Escape gesture, and the plain-click reset.</summary>
    public bool Clear()
    {
        bool changed = _ids.Count > 0 || _checkMode || _anchor is not null;
        _ids.Clear();
        _anchor = null;
        _checkMode = false;
        return changed;
    }

    /// <summary>Enter or leave explicit check mode. Leaving does NOT clear the selection: the lane can also be
    /// showing because two rows are selected, and the caller decides whether "done" means "deselect".</summary>
    public bool SetCheckMode(bool on)
    {
        if (_checkMode == on) return false;
        _checkMode = on;
        return true;
    }

    /// <summary>Drop every id the visible tree no longer holds — a folder collapsed, a search filtered, a projection
    /// landed. Run it with every plan: a selection naming rows nobody can see would drag items the user cannot point
    /// at. The anchor is pruned with them.</summary>
    public bool Prune(IReadOnlyList<string>? visibleOrder)
    {
        if (_ids.Count == 0 && _anchor is null) return false;
        bool changed = false;
        if (visibleOrder is null || visibleOrder.Count == 0)
        {
            // No tree at all (a pending projection, a section that planned nothing): keep the selection rather than
            // silently emptying it on a transient frame.
            return false;
        }
        _scratch.Clear();
        foreach (string id in _ids)
            if (IndexOf(visibleOrder, id) < 0) _scratch.Add(id);
        for (int i = 0; i < _scratch.Count; i++) changed |= _ids.Remove(_scratch[i]);
        if (_anchor is { Length: > 0 } a && IndexOf(visibleOrder, a) < 0) { _anchor = null; changed = true; }
        return changed;
    }

    /// <summary>The selection in TREE ORDER — the shape a payload, a batch move and a picker all want. Ids the
    /// visible order does not hold are dropped, for the same reason <see cref="Prune"/> drops them.</summary>
    public IReadOnlyList<string> Ordered(IReadOnlyList<string>? visibleOrder)
    {
        if (_ids.Count == 0 || visibleOrder is null || visibleOrder.Count == 0) return Array.Empty<string>();
        var ordered = new List<string>(_ids.Count);
        for (int i = 0; i < visibleOrder.Count; i++)
            if (_ids.Contains(visibleOrder[i])) ordered.Add(visibleOrder[i]);
        return ordered;
    }

    static int IndexOf(IReadOnlyList<string>? order, string id)
    {
        if (order is null) return -1;
        for (int i = 0; i < order.Count; i++)
            if (string.Equals(order[i], id, StringComparison.Ordinal)) return i;
        return -1;
    }

    bool Replace(string id)
    {
        bool changed = _ids.Count != 1 || !_ids.Contains(id);
        _ids.Clear();
        _ids.Add(id);
        _anchor = id;
        return changed;
    }
}
// ── PROJECTION, SHAPING PIPELINE, SORT, SEARCH ───────────────────────────────────────────────────────────────────────
//
// The pure half of the binder: turns `User.Me`'s edges into the flat `SidebarLibraryEntry` list both layouts render
// (SidebarProjection) and filters/sorts/pins-first-partitions it for Your Library (SidebarSort, SidebarSearch,
// SidebarBinderPipeline). Nothing here awaits, fetches, hydrates or touches a store — that half is Sidebar.Host.cs.

// ── SidebarSort — the five row orders (ported verbatim) ──────────────────────────────────────────────────────────────
//
// Every comparator ends in an ordinal Id compare: List<T>.Sort is unstable, so two equal keys would otherwise reshuffle
// between rebuilds — visible as row flicker under the FLIP transitions.

public static class SidebarSort
{
    static StringComparer? s_name;

    /// <summary>Localized name/creator collation, built ONCE per UI culture and cached. No article stripping: "The
    /// Beatles" sorts under T, exactly like Spotify.</summary>
    public static StringComparer NameComparer =>
        s_name ??= StringComparer.Create(CultureInfo.CurrentUICulture, ignoreCase: true);

    /// <summary>Test/culture-switch hook: drop the cached collator so the next access rebuilds it.</summary>
    public static void ResetCollator() => s_name = null;

    static readonly Comparison<SidebarLibraryEntry> s_recentsAsc = static (a, b) => Recents(in a, in b, desc: false);
    static readonly Comparison<SidebarLibraryEntry> s_recentsDesc = static (a, b) => Recents(in a, in b, desc: true);
    static readonly Comparison<SidebarLibraryEntry> s_addedAsc = static (a, b) => RecentlyAdded(in a, in b, desc: false);
    static readonly Comparison<SidebarLibraryEntry> s_addedDesc = static (a, b) => RecentlyAdded(in a, in b, desc: true);
    static readonly Comparison<SidebarLibraryEntry> s_alphaAsc = static (a, b) => Alphabetical(in a, in b, desc: false);
    static readonly Comparison<SidebarLibraryEntry> s_alphaDesc = static (a, b) => Alphabetical(in a, in b, desc: true);
    static readonly Comparison<SidebarLibraryEntry> s_creatorAsc = static (a, b) => Creator(in a, in b, desc: false);
    static readonly Comparison<SidebarLibraryEntry> s_creatorDesc = static (a, b) => Creator(in a, in b, desc: true);

    /// <summary>The comparator for a (sort, direction) pair. <paramref name="customOrder"/> is read only for
    /// <see cref="SidebarLibrarySort.CustomOrder"/>; null/empty degrades to pure SourceOrder.</summary>
    public static Comparison<SidebarLibraryEntry> For(SidebarLibrarySort sort, bool desc,
                                                     IReadOnlyList<string>? customOrder = null)
    {
        switch (sort)
        {
            case SidebarLibrarySort.RecentlyAdded: return desc ? s_addedDesc : s_addedAsc;
            case SidebarLibrarySort.Alphabetical: return desc ? s_alphaDesc : s_alphaAsc;
            case SidebarLibrarySort.Creator: return desc ? s_creatorDesc : s_creatorAsc;
            case SidebarLibrarySort.CustomOrder:
            {
                // Rank map: O(1) lookups instead of an IndexOf per comparison. `desc` is deliberately IGNORED.
                var rank = BuildRanks(customOrder);
                return (a, b) => Custom(in a, in b, rank);
            }
            default: return desc ? s_recentsDesc : s_recentsAsc;
        }
    }

    /// <summary>Sort in place. Filters run BEFORE the sort; pins are partitioned AFTER it.</summary>
    public static void Apply(List<SidebarLibraryEntry> list, SidebarLibrarySort sort, bool desc,
                             IReadOnlyList<string>? customOrder = null)
    {
        if (list.Count > 1) list.Sort(For(sort, desc, customOrder));
    }

    /// <summary>Custom exists only under the Playlists filter; elsewhere it falls back to Alphabetical FOR DISPLAY
    /// while the persisted preference is left untouched.</summary>
    public static SidebarLibrarySort Effective(SidebarLibrarySort sort, SidebarLibraryFilter filter) =>
        sort == SidebarLibrarySort.CustomOrder && filter != SidebarLibraryFilter.Playlists ? SidebarLibrarySort.Alphabetical : sort;

    /// <summary>True when the direction affordance should be shown at all (Custom has no inverse).</summary>
    public static bool SupportsDirection(SidebarLibrarySort sort) => sort != SidebarLibrarySort.CustomOrder;

    public static Dictionary<string, int> BuildRanks(IReadOnlyList<string>? order)
    {
        var rank = new Dictionary<string, int>(order?.Count ?? 0, StringComparer.Ordinal);
        if (order is null) return rank;
        for (int i = 0; i < order.Count; i++)
        {
            var id = order[i];
            if (!string.IsNullOrEmpty(id)) rank.TryAdd(id, i);        // first occurrence wins
        }
        return rank;
    }

    // ── the comparators ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Recents: PLAYED (<see cref="SidebarLibraryEntry.LastPlayedMs"/>), not opened. A played-block-first,
    /// never-played-block-second partition — the split is applied BEFORE <paramref name="desc"/>, which only reverses
    /// the ordering WITHIN each block.</summary>
    public static int Recents(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
    {
        bool ap = a.LastPlayedMs > 0, bp = b.LastPlayedMs > 0;
        if (ap != bp) return ap ? -1 : 1;

        int c = ap
            ? b.LastPlayedMs.CompareTo(a.LastPlayedMs)
            : b.SortStamp.CompareTo(a.SortStamp);
        if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return desc ? -c : c;
    }

    /// <summary>Recently added: the resolved SortStamp descending, then SourceOrder, then Name. Playlists have no
    /// server add-date, so their stamp is the local first-observation proxy (<see cref="SidebarFirstSeen"/>).</summary>
    public static int RecentlyAdded(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
    {
        int c = b.SortStamp.CompareTo(a.SortStamp);
        if (c == 0) c = a.SourceOrder.CompareTo(b.SourceOrder);
        if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return desc ? -c : c;
    }

    /// <summary>Alphabetical by Name, then Creator, then Id.</summary>
    public static int Alphabetical(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
    {
        int c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = NameComparer.Compare(a.Creator, b.Creator);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return desc ? -c : c;
    }

    /// <summary>By Creator, with EMPTY creators last ALWAYS — that partition is not reversed by <paramref name="desc"/>.
    /// Then Name, then Id.</summary>
    public static int Creator(in SidebarLibraryEntry a, in SidebarLibraryEntry b, bool desc)
    {
        bool ac = a.Creator.Length > 0, bc = b.Creator.Length > 0;
        if (ac != bc) return ac ? -1 : 1;

        int c = ac ? NameComparer.Compare(a.Creator, b.Creator) : 0;
        if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return desc ? -c : c;
    }

    /// <summary>Local custom overlay: ids the user ordered come first in their stored order; every id absent from the
    /// order APPENDS after all known ids in SourceOrder ascending (the stable-append rule). `desc` never applies.</summary>
    public static int Custom(in SidebarLibraryEntry a, in SidebarLibraryEntry b, Dictionary<string, int> rank)
    {
        bool ak = rank.TryGetValue(a.Id, out int ra);
        bool bk = rank.TryGetValue(b.Id, out int rb);
        if (ak != bk) return ak ? -1 : 1;

        int c = ak ? ra.CompareTo(rb) : a.SourceOrder.CompareTo(b.SourceOrder);
        if (c == 0) c = NameComparer.Compare(a.Name, b.Name);
        if (c == 0) c = string.CompareOrdinal(a.Id, b.Id);
        return c;
    }
}

// ── SidebarSearch — the library-only match (ported verbatim) ─────────────────────────────────────────────────────────
//
// Diacritics-insensitive, allocation-free. `InvariantGlobalization=true` kills CompareInfo.IgnoreNonSpace, so the
// matcher probes the capability once and falls back to a hand-folded Latin-1 scan that is equally allocation-free.

public static class SidebarSearch
{
    /// <summary>True when the runtime's collator folds diacritics itself (ICU present).</summary>
    public static readonly bool CollatorFoldsDiacritics = ProbeCollator();

    const CompareOptions CollatorOpts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    static bool ProbeCollator()
    {
        try { return CultureInfo.InvariantCulture.CompareInfo.IndexOf("é", "e", CollatorOpts) >= 0; }
        catch (PlatformNotSupportedException) { return false; }   // invariant mode rejects IgnoreNonSpace outright
        catch (ArgumentException) { return false; }
    }

    /// <summary>Library-ONLY match: the sidebar search never hits the network. Case- and diacritics-insensitive
    /// substring, ALLOCATION-FREE. <see cref="SidebarLibraryEntry.Creator"/> participates only for 2+ character
    /// queries, so a single letter does not match every album by an artist whose name contains it.</summary>
    public static bool Matches(in SidebarLibraryEntry e, string query) => Matches(e.Name, e.Creator, query);

    /// <summary>The same match against a bare (name, creator) pair — for a row that is not a projected entry.</summary>
    public static bool Matches(string? name, string? creator, string query)
    {
        if (query.Length == 0) return true;
        if (name is { Length: > 0 } && Contains(name, query)) return true;
        return query.Length >= 2 && creator is { Length: > 0 } && Contains(creator, query);
    }

    /// <summary>Normalize a raw search box value (trimmed, never null). Called ONCE per keystroke, never per row.</summary>
    public static string Normalize(string? raw) => raw is null ? "" : raw.Trim();

    public static bool Contains(string haystack, string needle)
    {
        if (needle.Length == 0) return true;
        if (needle.Length > haystack.Length) return false;
        if (CollatorFoldsDiacritics)
            return CultureInfo.CurrentUICulture.CompareInfo.IndexOf(haystack, needle, CollatorOpts) >= 0;
        return FoldedContains(haystack, needle);
    }

    // ── the invariant-mode fallback: a folded two-pointer scan (no allocations, no culture data) ───────────────────────
    static bool FoldedContains(string hay, string needle)
    {
        int n = needle.Length, last = hay.Length - n;
        for (int i = 0; i <= last; i++)
        {
            int k = 0;
            while (k < n && Fold(hay[i + k]) == Fold(needle[k])) k++;
            if (k == n) return true;
        }
        return false;
    }

    // Latin-1 Supplement (U+00C0..U+00FF) folded to its base letter, lowercased. Beyond that range the fold is
    // case-only (the honest limit without a full Unicode decomposition table, which invariant mode has no data for).
    const string Latin1Fold =
        "aaaaaaaceeeeiiiidnooooo×ouuuuyþs" +
        "aaaaaaaceeeeiiiidnooooo÷ouuuuyþy";

    static char Fold(char c)
    {
        if (c < 128) return (uint)(c - 'A') <= 'Z' - 'A' ? (char)(c + 32) : c;
        if (c >= 'À' && c <= 'ÿ') return Latin1Fold[c - 'À'];
        return char.ToLowerInvariant(c);
    }
}

// ── SidebarProjection — REDESIGNED over Entities edges ───────────────────────────────────────────────────────────────
//
// In 0.2.9 this copied from LibraryStore/PlaylistSummary. In 0.3 the projection IS a read over `User.Me`'s edges: the
// rootlist is walked as the FLAT marker stream the wire sends (RootlistKind.FolderStart/FolderEnd, with each edge's own
// Depth), not a materialised tree, and Albums/Artists/Shows are the saved/followed/saved-show relations in their own
// (newest-saved-first) order. Emission order, the folder 2×2 mosaic, Circular for artists, the joined-artist string
// capped at 3, FlavorOf, QualifiersAvailable and PinsFirst's leading pin band all port as DECISIONS — only the source
// of each field changed. Flavor stays DERIVED (Playlist.IsOwner / owner name / Playlist.Editable), never a stored
// column, so "the data does not say ⇒ None" survives.
//
// FOLDER IDENTITY (decision D10, 0.2.9's convention): a folder's identity is the rootlist GROUP id the wire carries
// (`spotify:start-group:<hex>:<name>`), landed on its FolderStart edge as `RootlistEdge.FolderId`. The row's
// `FolderId` is the BARE hex, its `Id` is `SidebarPinId.ForFolder(hex)` ("folder:<hex>"), and every child's
// `ParentFolderId` is the bare hex of the folder it sits in — so a folder keeps its expansion, its pin and its sync
// identity when it is renamed OR moved, and `v3.expandedFolders` / folder pins written by 0.2.9 match again. A
// FolderStart that arrived WITHOUT a group id (a malformed answer) is keyed "~<position>": unique within the tree so
// the list can still key its rows, and unable to collide with any real hex id, a persisted pin or a synced uri.
// G-059 (fixed): a cover-less playlist's own 2×2 mosaic — first ≤4 distinct member-track album covers, membership
// order, the SAME rule Entities/Playlist.UI.cs's `MosaicTiles` gives the playlist DETAIL page's own cover (kept
// aligned by comment, not by call: that rule resolves each cover to a URL for its two other callers, which is not
// what a projected row wants). `PlaylistMosaicTiles` below collects the tracks' own `Track.ImageId` StringIds
// directly — no url resolve, no per-rebuild string allocation — exactly like the FOLDER mosaic's tiles (first ≤4
// CHILD playlist covers), which is unaffected and ports in full. `Controls.ArtUrl` builds the url at RENDER time for
// both, same as every other cover.

public readonly record struct SidebarProjectionResult(int Count, byte FlavorMask, int NewFirstSeenStamps);

/// <summary>Trap 5: what an unresolved FOLDER pin should render as (<see cref="SidebarProjection.ResolveFolderPinState"/>).
/// <c>Normal</c> means the caller already found it live and this verdict is moot. <c>Pending</c> is the honest
/// "don't know yet" — title only, no subtitle, never the disabled "missing" row. <c>Missing</c> is the confident
/// negative, earned only once the rootlist has actually answered and still does not carry the folder.</summary>
public enum SidebarPinFolderState : byte { Normal = 0, Pending = 1, Missing = 2 }

public static class SidebarProjection
{
    static readonly StringBuilder s_join = new(64);

    // E3: reused across rebuilds (single-writer, UI thread, C1) — `WalkRootlist` collects the playlist SLOTS
    // that need an ask into these instead of calling `Entities.Ensure`/`EnsureEdge` per row, and `Build` issues at
    // most ONE span-form call of each after the walk. Before this, an un-identified rootlist rented two
    // pooled Fetch buffers and pumped the network once PER ROW.
    static readonly List<int> s_ensureIdentitySlots = new(32);
    static readonly List<int> s_ensureAlbumIdentitySlots = new(32);
    static readonly List<int> s_ensureArtistIdentitySlots = new(32);
    static readonly List<int> s_ensureShowIdentitySlots = new(32);
    // D2: only ever holds a cover-less row's mosaic-membership ask now — the count-driven ask (bug A1's
    // `ShouldEnsureCount`) is deleted outright; `Build` flushes this at `FetchPriority.Prefetch`, never `Visible`.
    static readonly List<int> s_ensureTracksSlots = new(32);
    // The member TRACK slots (not playlists) a cover-less row's mosaic still needs the album/image of — see
    // `ShouldWarmMosaicTracks`. Flushed as one span-form `Entities.Ensure` on the Tracks table at Prefetch.
    static readonly List<int> s_ensureMosaicTrackSlots = new(64);

    /// <summary>How many leading member tracks a cover-less playlist's mosaic is warmed and repainted over. The
    /// membership answer (`PlaylistRevision`) lands bare uri-only track slots — no album, no image — so the tiles
    /// need each leading track's own Identity group, which nothing but the playlist's PAGE used to ask for: the
    /// rail showed an empty tile until the page had been opened once, every launch. The warm asks this many, and
    /// <see cref="SidebarLibraryFingerprint"/> folds the same prefix's row versions so the landing actually
    /// repaints; the two must stay one number, or a tile that landed past the fold never redraws.</summary>
    public const int MosaicTrackPrefix = 8;

    /// <summary>G-059: a cover-less playlist's own 2×2 mosaic — the first ≤4 distinct member-track album covers, in
    /// membership order, THE SAME RULE as <see cref="Playlist.MosaicTiles"/> (Entities/Playlist.UI.cs:95, the
    /// playlist DETAIL page's own cover rule) — keep the two aligned if either changes. That rule resolves each
    /// track's image to a URL (its two other callers want one, for a palette/deposit-art fallback); this one does
    /// NOT — it collects the tracks' own <see cref="Track.ImageId"/> directly, already-interned StringIds with no
    /// resolve, no string concat and no new intern per rebuild, exactly like a folder's tiles
    /// (<c>p.ImageId</c> in <c>WalkRootlist</c>'s FolderFrame fold below). <see cref="Controls.ArtUrl"/> builds the
    /// url at RENDER time, same as every other cover.
    ///
    /// <para>Null while the tracks edge has not landed (so the row keeps showing the placeholder instead of flashing
    /// "no cover", and re-tries once <see cref="Playlist.MembershipState"/> moves) or once loaded none of them carry
    /// a cover; 1-3 tiles is a legitimate single-cover result (the row's cover slot, Sidebar's own <c>Cover.Art</c>,
    /// takes tile 0 when there are fewer than four — same as the folder mosaic).</para></summary>
    internal static List<StringId>? PlaylistMosaicTiles(in Playlist p)
    {
        if (p.MembershipState == EdgeState.Unknown) return null;
        Span<int> albums = stackalloc int[4];
        List<StringId>? tiles = null;
        var slots = p.TrackSlots;
        for (int i = 0; i < slots.Length; i++)
        {
            int n = tiles?.Count ?? 0;
            if (n >= 4) break;
            var t = new Track(slots[i]);
            int album = t.AlbumSlot;
            if (album <= Table.None || albums[..n].IndexOf(album) >= 0) continue;
            StringId image = t.ImageId;
            if (image.IsEmpty) continue;
            albums[n] = album;
            (tiles ??= new List<StringId>(4)).Add(image);
        }
        return tiles;
    }

    /// <summary>Fill <paramref name="into"/> (CLEARED first) with the unified entry list for the requested kinds, read
    /// straight off <paramref name="u"/>'s edges. <paramref name="includeFolderChildren"/> false ⇒ a collapsed folder's
    /// children are not emitted (still folded into its mosaic/child-count); true ⇒ fully flattened.
    /// <paramref name="lastPlayed"/> is uri → last-played unix ms; null/absent ⇒ every row's LastPlayedMs stays 0.
    /// <paramref name="ensureIdentity"/> — Bug H: true only for the ONE caller whose <paramref name="includeFolderChildren"/>
    /// / <paramref name="isFolderExpanded"/> pair reflects the pane's REAL fold state (the published-entries pass,
    /// <c>Sidebar.Host.Rebuild</c>'s "buffer" build) — never the structural full/tree passes, whose
    /// <paramref name="includeFolderChildren"/>:true forces every row "visible" regardless of collapse. Passing true
    /// from one of those would warm a whole hundreds-deep rootlist's identity/membership on every rebuild.</summary>
    public static SidebarProjectionResult Build(
        List<SidebarLibraryEntry> into,
        in User u,
        SidebarEntryKindMask kinds,
        SidebarFirstSeen? firstSeen,
        SidebarRecency? recency,
        bool includeFolderChildren,
        Func<string, bool>? isFolderExpanded = null,
        IReadOnlyDictionary<string, long>? lastPlayed = null,
        bool ensureIdentity = false)
    {
        into.Clear();
        var rec = recency ?? SidebarRecency.Empty;
        var seen = firstSeen ?? SidebarFirstSeen.Frozen;
        int stampsBefore = seen.NewStamps;
        byte flavorMask = 0;

        bool wantPlaylists = (kinds & SidebarEntryKindMask.Playlist) != 0;
        bool wantFolders = (kinds & SidebarEntryKindMask.Folder) != 0;
        s_ensureIdentitySlots.Clear();
        s_ensureAlbumIdentitySlots.Clear();
        s_ensureArtistIdentitySlots.Clear();
        s_ensureShowIdentitySlots.Clear();
        s_ensureTracksSlots.Clear();
        s_ensureMosaicTrackSlots.Clear();
        if ((wantPlaylists || wantFolders) && u.RootlistState != EdgeState.Unknown)
            WalkRootlist(into, in u, wantPlaylists, wantFolders, includeFolderChildren, isFolderExpanded,
                         rec, seen, lastPlayed, ref flavorMask, ensureIdentity);
        // E3: ONE span-form ask per group, after the walk, instead of one `Fetch.Plan` per un-identified/un-counted
        // row — `WalkRootlist` only ever populates these when `ensureIdentity` is true (the one fold-state-real pass).
        if (s_ensureIdentitySlots.Count > 0)
            Entities.Ensure(Entities.Current.Playlists, CollectionsMarshal.AsSpan(s_ensureIdentitySlots),
                (uint)PlaylistFields.Identity, FetchPriority.Visible);
        // D2: a cover-less row's mosaic wants its member tracks (never a count any more — see `WalkRootlist`'s
        // playlist branch) — a tile, not a page, so it never competes with the pane's own visible asks.
        if (s_ensureTracksSlots.Count > 0)
            Entities.EnsureEdge(FetchEdge.PlaylistTracks, CollectionsMarshal.AsSpan(s_ensureTracksSlots),
                priority: FetchPriority.Prefetch);
        // The mosaic's own data: the leading member tracks' album + image, one batched ask at Prefetch (a tile, not
        // a page). `Fetch.Plan` reads the store before the network, so on a relaunch a track that ever answered
        // fills from disk with no request at all; only genuinely never-seen tracks ride a TrackV4 batch.
        if (s_ensureMosaicTrackSlots.Count > 0)
            Entities.Ensure(Entities.Current.Tracks, CollectionsMarshal.AsSpan(s_ensureMosaicTrackSlots),
                (uint)MosaicTrackFields, FetchPriority.Prefetch);

        if ((kinds & SidebarEntryKindMask.Album) != 0)
        {
            var slots = u.SavedAlbumSlots;
            var edges = User.Relation(LibraryEdgeKind.SavedAlbums).Payload(u.Slot);
            for (int i = 0; i < slots.Length; i++)
            {
                var al = new Album(slots[i]);
                string uri = al.Uri.Text;
                string id = SidebarPinId.AlbumPrefix + uri;
                long added = i < edges.Length ? AddedMs(edges[i].AddedAt) : 0L;
                bool identityKnown = al.Knows(AlbumFields.Identity);
                if (ShouldEnsureIdentity(ensureIdentity, identityKnown)) s_ensureAlbumIdentitySlots.Add(al.Slot);
                var artistSlots = al.ArtistSlots;
                into.Add(new SidebarLibraryEntry(
                    id, SidebarEntryKind.Album, uri, al.Title, JoinArtistNames(artistSlots),
                    al.ImageId, null, al.TrackCount, added,
                    SortStamp: added > 0 ? added : seen.Stamp(id),
                    LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                    SourceOrder: i, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                {
                    FolderId = "", FolderName = "",
                    FirstArtistName = artistSlots.Length > 0 ? new Artist(artistSlots[0]).Name : "",
                    LastPlayedMs = LastPlayed(lastPlayed, uri),
                    IdentityKnown = identityKnown,
                });
            }
        }

        if ((kinds & SidebarEntryKindMask.Artist) != 0)
        {
            var slots = u.FollowedArtistSlots;
            var edges = User.Relation(LibraryEdgeKind.FollowedArtists).Payload(u.Slot);
            for (int i = 0; i < slots.Length; i++)
            {
                var ar = new Artist(slots[i]);
                string uri = ar.Uri.Text;
                string id = SidebarPinId.ArtistPrefix + uri;
                long added = i < edges.Length ? AddedMs(edges[i].AddedAt) : 0L;
                bool identityKnown = ar.Knows(ArtistFields.Identity);
                if (ShouldEnsureIdentity(ensureIdentity, identityKnown)) s_ensureArtistIdentitySlots.Add(ar.Slot);
                into.Add(new SidebarLibraryEntry(
                    id, SidebarEntryKind.Artist, uri, ar.Name, "",
                    ar.ImageId, null, 0, added,
                    SortStamp: added > 0 ? added : seen.Stamp(id),
                    LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                    SourceOrder: i, Depth: 0, Circular: true, Flavor: SidebarPlaylistFlavor.None)
                {
                    FolderId = "", FolderName = "", FirstArtistName = "", LastPlayedMs = LastPlayed(lastPlayed, uri),
                    IdentityKnown = identityKnown,
                });
            }
        }

        if ((kinds & SidebarEntryKindMask.Show) != 0)
        {
            var slots = u.SavedShowSlots;
            var edges = User.Relation(LibraryEdgeKind.SavedShows).Payload(u.Slot);
            for (int i = 0; i < slots.Length; i++)
            {
                var sh = new Show(slots[i]);
                string uri = sh.Uri.Text;
                string id = SidebarPinId.ShowPrefix + uri;
                long added = i < edges.Length ? AddedMs(edges[i].AddedAt) : 0L;
                bool identityKnown = sh.Knows(ShowFields.Identity);
                if (ShouldEnsureIdentity(ensureIdentity, identityKnown)) s_ensureShowIdentitySlots.Add(sh.Slot);
                into.Add(new SidebarLibraryEntry(
                    id, SidebarEntryKind.Show, uri, Entities.Strings.Resolve(sh.TitleId),
                    Entities.Strings.Resolve(sh.PublisherId),
                    sh.ImageId, null, 0, added,
                    SortStamp: added > 0 ? added : seen.Stamp(id),
                    LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                    SourceOrder: i, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                {
                    FolderId = "", FolderName = "", FirstArtistName = "", LastPlayedMs = LastPlayed(lastPlayed, uri),
                    IdentityKnown = identityKnown, IsAudiobook = LibraryAudiobookFilter.IsAudiobookRow(sh.Flags),
                });
            }
        }

        if (s_ensureAlbumIdentitySlots.Count > 0)
            Entities.Ensure(Entities.Current.Albums, CollectionsMarshal.AsSpan(s_ensureAlbumIdentitySlots),
                (uint)AlbumFields.Identity, FetchPriority.Visible);
        if (s_ensureArtistIdentitySlots.Count > 0)
            Entities.Ensure(Entities.Current.Artists, CollectionsMarshal.AsSpan(s_ensureArtistIdentitySlots),
                (uint)ArtistFields.Identity, FetchPriority.Visible);
        if (s_ensureShowIdentitySlots.Count > 0)
            Entities.Ensure(Entities.Current.Shows, CollectionsMarshal.AsSpan(s_ensureShowIdentitySlots),
                (uint)ShowFields.Identity, FetchPriority.Visible);

        return new SidebarProjectionResult(into.Count, flavorMask, seen.NewStamps - stampsBefore);
    }

    // One frame per open folder: its pin id/name, the index of its OWN row in `into` (-1 = no row was emitted, patched
    // at FolderEnd with the child count + mosaic), whether its children are walked at all, and the running tally.
    struct FolderFrame
    {
        public string Id, Name;
        public int RowIndex;
        public bool Descend;
        public int ChildCount;
        public List<StringId>? Tiles;
        public FolderFrame(string id, string name, int rowIndex, bool descend)
        { Id = id; Name = name; RowIndex = rowIndex; Descend = descend; ChildCount = 0; Tiles = null; }
    }

    // An edge's AddedAt (UNIX seconds, WP-4.5 gap 1) -> unix ms. 0 (or anything before the epoch) means "never dated"
    // and stays 0 so the first-seen fallback can fire.
    static long AddedMs(int unixSeconds) => unixSeconds <= 0 ? 0L : unixSeconds * 1000L;

    static void WalkRootlist(
        List<SidebarLibraryEntry> into, in User u,
        bool wantPlaylists, bool wantFolders, bool includeFolderChildren, Func<string, bool>? isFolderExpanded,
        SidebarRecency rec, SidebarFirstSeen seen, IReadOnlyDictionary<string, long>? lastPlayed, ref byte flavorMask,
        bool ensureIdentity = false)
    {
        var targets = u.RootlistSlots;
        var payload = u.Rootlist;
        int n = targets.Length;
        if (n == 0) return;

        var stack = new List<FolderFrame>(4);
        int order = 0;

        for (int i = 0; i < n; i++)
        {
            var edge = payload[i];
            bool visible = stack.Count == 0 || stack[stack.Count - 1].Descend;

            switch ((RootlistKind)edge.Kind)
            {
                case RootlistKind.FolderStart:
                {
                    string folderId = FolderIdOf(in edge);
                    string folderName = Entities.Strings.Resolve(edge.FolderName);
                    bool descend = includeFolderChildren || !wantFolders || (isFolderExpanded?.Invoke(folderId) ?? false);
                    int rowIndex = -1;
                    if (visible && wantFolders)
                    {
                        string parentId = stack.Count > 0 ? stack[stack.Count - 1].Id : "";
                        string parentName = stack.Count > 0 ? stack[stack.Count - 1].Name : "";
                        rowIndex = into.Count;
                        into.Add(new SidebarLibraryEntry(
                            SidebarPinId.ForFolder(folderId), SidebarEntryKind.Folder, "", folderName, "",
                            StringId.Empty, null, 0, 0,
                            SortStamp: 0, LastVisitedTicksUtc: 0,
                            SourceOrder: order++, Depth: edge.Depth, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                        {
                            FolderId = folderId, FolderName = folderName,
                            ParentFolderId = parentId, ParentFolderName = parentName, FirstArtistName = "",
                        });
                    }
                    // A sub-folder is a DIRECT child of its enclosing folder, exactly like a playlist (0.2.9 counted
                    // both). It has no cover of its own, so it adds to the count but never to the mosaic.
                    if (stack.Count > 0)
                    {
                        var parent = stack[stack.Count - 1];
                        parent.ChildCount++;
                        stack[stack.Count - 1] = parent;
                    }
                    stack.Add(new FolderFrame(folderId, folderName, rowIndex, visible && descend));
                    break;
                }

                case RootlistKind.FolderEnd:
                {
                    if (stack.Count == 0) break;
                    var frame = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    if (frame.RowIndex >= 0)
                        // A real FolderEnd landed — the count is exactly what was walked, never a placeholder.
                        into[frame.RowIndex] = into[frame.RowIndex] with
                            { ChildCount = frame.ChildCount, MosaicTiles = frame.Tiles, CountKnown = true };
                    break;
                }

                default: // Item
                {
                    var p = new Playlist(targets[i]);

                    // Fold into the immediately enclosing folder's facts UNCONDITIONALLY — a collapsed folder must
                    // still know its own child count and mosaic.
                    if (stack.Count > 0)
                    {
                        var top = stack[stack.Count - 1];
                        top.ChildCount++;
                        if ((top.Tiles?.Count ?? 0) < 4 && !p.ImageId.IsEmpty)
                            (top.Tiles ??= new List<StringId>(4)).Add(p.ImageId);
                        stack[stack.Count - 1] = top;
                    }

                    if (!visible || !wantPlaylists) break;

                    // Bug H: a VISIBLE row's identity is ensured from the pane, not from opening the playlist's own
                    // page — the same pattern `Sidebar.Host.ResolveLivePin` already applies to unlisted pins. Gated
                    // on the caller-controlled `ensureIdentity` (see its doc on `Build`): only the ONE rebuild pass
                    // whose `includeFolderChildren`/`isFolderExpanded` pair reflects the pane's REAL fold state sets
                    // it true, so this never fires from the structural full/tree passes that flatten the WHOLE
                    // rootlist regardless of collapse.
                    bool identityKnown = p.Knows(PlaylistFields.Identity);
                    // E3: collected, never asked here — `Build` issues ONE span-form `Entities.Ensure` after the
                    // whole walk (see `s_ensureIdentitySlots`'s doc).
                    if (ShouldEnsureIdentity(ensureIdentity, identityKnown)) s_ensureIdentitySlots.Add(p.Slot);

                    string uri = p.Uri.Text;
                    string id = SidebarPinId.PlaylistPrefix + uri;
                    var flavor = FlavorOf(p);
                    flavorMask |= (byte)(1 << (int)flavor);
                    // Playlists have no add timestamp anywhere but the local first-seen proxy — the rootlist is an
                    // ordered marker stream, and RootlistEdge.AddedAt is documented LOCAL (contract DATA GAPS).
                    long added = AddedMs(edge.AddedAt);
                    string parentId = stack.Count > 0 ? stack[stack.Count - 1].Id : "";
                    string parentName = stack.Count > 0 ? stack[stack.Count - 1].Name : "";
                    // G-059: a cover of its own always wins; only a cover-less row pays for the track walk.
                    List<StringId>? mosaic = null;
                    bool hasCover = !p.ImageId.IsEmpty;
                    if (!hasCover) mosaic = PlaylistMosaicTiles(in p);

                    // D2 (issue #4): the count is a ROW fact (`PlaylistFields.TrackCount`, persisted with the list
                    // from this wave on) — it is never asked for on its own any more. The deleted `ShouldEnsureCount`
                    // (bug A1) used to force a full `PlaylistTracks` read of every un-counted VISIBLE playlist just
                    // to learn a number — the ~30 full `GET /playlist/v2/playlist/{id}` reads proven at every boot.
                    // A playlist whose count has not landed shows none. One fallback costs nothing: when membership
                    // is ALREADY resident for some other reason (a cover-less row's own mosaic ask, or the page
                    // having been opened) its `Total` IS the real count — bug A1's own proven shape (Identity landed
                    // off a thin ListMetadataV2 answer that never carries a length) is exactly the case this
                    // recovers for free, with no second ask.
                    bool trackCountKnown = p.Knows(PlaylistFields.TrackCount);
                    bool membershipResident = p.MembershipState != EdgeState.Unknown;
                    PlaylistCount(trackCountKnown, p.TrackCount, membershipResident, p.MembershipTotal,
                        out bool countKnown, out int trackCount);
                    // Bug H's membership ensure stays cover-gated — only a cover-less row's mosaic still needs the
                    // member list at all; a covered row's subtitle never asks for anything any more.
                    bool needsMembership = mosaic is null && ShouldEnsureMembership(ensureIdentity, hasCover, p.MembershipState);
                    if (needsMembership) s_ensureTracksSlots.Add(p.Slot);
                    // Once membership HAS landed the tiles still need the leading tracks' own album/image (the
                    // answer lands bare uri-only slots) — collected here, asked once by `Build` at Prefetch.
                    if (ShouldWarmMosaicTracks(ensureIdentity, hasCover, p.MembershipState, mosaic?.Count ?? 0))
                        CollectMosaicTrackSlots(p.TrackSlots, s_ensureMosaicTrackSlots);

                    into.Add(new SidebarLibraryEntry(
                        id, SidebarEntryKind.Playlist, uri, Entities.Strings.Resolve(p.TitleId), OwnerNameOf(in p),
                        p.ImageId, mosaic, trackCount, added,
                        SortStamp: added > 0 ? added : seen.Stamp(id),
                        LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                        SourceOrder: order++, Depth: edge.Depth, Circular: false, Flavor: flavor)
                    {
                        FolderId = parentId, FolderName = parentName,
                        ParentFolderId = parentId, ParentFolderName = parentName,
                        IsOwner = p.IsOwner, CanEdit = p.Editable, FirstArtistName = "",
                        LastPlayedMs = LastPlayed(lastPlayed, uri),
                        IdentityKnown = identityKnown,
                        CountKnown = countKnown,
                        HasEpisodes = p.EpisodeCount > 0,
                        Episodes = p.IsYourEpisodes,
                    });
                    break;
                }
            }
        }

        // A3: a folder whose `spotify:end-group:` marker never arrived (a truncated/malformed answer, or the wire
        // simply stopped short) leaves its frame open — WITHOUT this drain its row keeps the literal 0 stamped at
        // FolderStart forever, a confident "0 items" for a folder that may hold hundreds. Patch every still-open
        // frame with whatever was actually walked before it, the SAME patch a real FolderEnd applies above — never
        // a re-derived "unknown" state, just the honest count of what this answer actually carried.
        for (int fi = 0; fi < stack.Count; fi++)
        {
            var frame = stack[fi];
            if (frame.RowIndex >= 0)
                // The drained count IS the honest count of what this answer carried — not a guess, so CountKnown
                // too, the same as a real FolderEnd above.
                into[frame.RowIndex] = into[frame.RowIndex] with
                    { ChildCount = frame.ChildCount, MosaicTiles = frame.Tiles, CountKnown = true };
        }
    }

    /// <summary>The count a sidebar playlist row paints. The row fact first; a known 0 never beats a resident
    /// membership that already has rows (a persisted empty hole after the list has been opened).
    /// <para><b>RESIDENT IS NOT THE SAME AS COUNTED</b> (bug A1's last corner). <c>membershipResident</c> is
    /// "the membership state is not Unknown", which a FAILED edge and a PARTIAL page whose answer carried no total
    /// both satisfy while knowing nothing whatever about length — and both read <c>membershipTotal == 0</c>. Taking
    /// residency alone as "the count is known" turned each of those into a confident "0 songs", which is the very
    /// sentence this rule exists to stop. So the membership arm only counts when it carries a REAL number; a
    /// genuine zero is the ROW FACT's to state (<c>PlaylistFields.TrackCount</c>, which is set even for an empty
    /// list), and a row with neither says nothing at all.</para></summary>
    public static void PlaylistCount(bool trackCountKnown, int trackCount, bool membershipResident, int membershipTotal,
                                     out bool known, out int count)
    {
        bool membershipCounts = membershipResident && membershipTotal > 0;
        known = trackCountKnown || membershipCounts;
        count = membershipCounts && (!trackCountKnown || trackCount == 0) ? membershipTotal : trackCount;
    }

    /// <summary>Bug H, the pure half of "ensure a visible playlist row's identity": true only when the caller has
    /// said these rows are genuinely visible (<paramref name="ensureIdentity"/> — see <see cref="Build"/>'s doc) AND
    /// the identity has not already landed. Pulled out of <c>WalkRootlist</c> so the RULE is testable without
    /// touching <c>Entities</c> at all — "the projection requests Identity for the rows it emits" is this predicate
    /// returning true, not a side-effecting call a test would have to fake a whole entity table to observe.</summary>
    public static bool ShouldEnsureIdentity(bool ensureIdentity, bool identityKnown) => ensureIdentity && !identityKnown;

    /// <summary>The same rule for a cover-less row's MEMBERSHIP edge (the mosaic fallback's data source): only for a
    /// row with no cover of its own, only when genuinely visible, and only while membership has not landed — never
    /// for every cover-less playlist in a large rootlist.</summary>
    public static bool ShouldEnsureMembership(bool ensureIdentity, bool hasCover, EdgeState membershipState)
        => ensureIdentity && !hasCover && membershipState == EdgeState.Unknown;

    /// <summary>The mosaic warm's pure half: a genuinely visible row (<paramref name="ensureIdentity"/>), with no
    /// cover of its own, whose membership HAS landed (before that there are no member slots to ask about — that
    /// window is <see cref="ShouldEnsureMembership"/>'s) and whose mosaic is still short of four tiles. A full
    /// mosaic asks for nothing, so a settled row costs no plan call at all on later rebuilds.</summary>
    public static bool ShouldWarmMosaicTracks(bool ensureIdentity, bool hasCover, EdgeState membershipState, int tileCount)
        => ensureIdentity && !hasCover && membershipState != EdgeState.Unknown && tileCount < 4;

    /// <summary>What the mosaic reads off a member track: its album (the distinct-tile key) and its image. A subset
    /// of <see cref="TrackFields.Identity"/> that the persisted track shape restores from disk, so a relaunch
    /// answers it without a request; the page's own <c>Row</c> ask supersets it and dedupes against it.</summary>
    public const TrackFields MosaicTrackFields = TrackFields.Album | TrackFields.Image;

    /// <summary>The first <see cref="MosaicTrackPrefix"/> member slots that do not yet know
    /// <see cref="MosaicTrackFields"/>, appended to <paramref name="into"/> in membership order. Bounded by the same
    /// prefix the fingerprint folds, so every landing this asks for is one the row repaints on.</summary>
    internal static void CollectMosaicTrackSlots(ReadOnlySpan<int> members, List<int> into)
    {
        int n = members.Length < MosaicTrackPrefix ? members.Length : MosaicTrackPrefix;
        for (int i = 0; i < n; i++)
        {
            var t = new Track(members[i]);
            if (!t.IsValid || t.Knows(MosaicTrackFields)) continue;
            into.Add(members[i]);
        }
    }

    /// <summary>Trap 5, the folder-pin half: an unresolved FOLDER pin (no hydration path — the rootlist walk is its
    /// only source of truth) must not render a confident negative before the account's
    /// rootlist has even answered once THIS session. The rootlist relation is network-only, never persisted, so
    /// every cold launch starts <see cref="EdgeState.Unknown"/> — for that window "not found in the projection"
    /// means "not yet known", not "gone". <see cref="SidebarPinFolderState.Missing"/> (the confident negative,
    /// "Not in your library on this device yet") is only correct once <paramref name="rootlistState"/> shows the
    /// relation actually answered (anything but Unknown) and the folder is still absent.</summary>
    public static SidebarPinFolderState ResolveFolderPinState(EdgeState rootlistState, bool foundInProjection)
        => foundInProjection ? SidebarPinFolderState.Normal
         : rootlistState == EdgeState.Unknown ? SidebarPinFolderState.Pending
         : SidebarPinFolderState.Missing;

    /// <summary>Trap 5 + Your Library: a row whose Identity has not landed must not fall back to a raw id/uri
    /// fragment as its title. ShortUri is only for a resolved nameless entity. <paramref name="isPinned"/> is kept
    /// so call sites stay one predicate; the pin/library distinction is no longer a gate.</summary>
    public static bool ShouldShowUriFallbackTitle(bool isPinned, bool identityKnown)
    {
        _ = isPinned;
        return identityKnown;
    }

    /// <summary>Playlist provenance, derived from facts the Entities row actually carries (never a stored column).
    /// <see cref="SidebarPlaylistFlavor.None"/> means "the data does not say", never "mine".</summary>
    public static SidebarPlaylistFlavor FlavorOf(in Playlist p)
    {
        if (p.IsOwner) return SidebarPlaylistFlavor.ByYou;
        string ownerName = OwnerNameOf(in p);
        if (string.Equals(ownerName, "Spotify", StringComparison.OrdinalIgnoreCase)) return SidebarPlaylistFlavor.BySpotify;
        if (p.Editable) return SidebarPlaylistFlavor.Mixed;                 // collaborative
        if (ownerName.Length > 0) return SidebarPlaylistFlavor.Mixed;       // someone else's
        return SidebarPlaylistFlavor.None;                                  // unknown
    }

    /// <summary>"Qualifier chips only when the data supports them", made mechanical: at least TWO distinct non-unknown
    /// flavors must be present. Hand-folded popcount — System.Numerics is not in scope for this file.</summary>
    public static bool QualifiersAvailable(byte flavorMask)
    {
        int v = flavorMask & 0b1110, count = 0;
        while (v != 0) { count += v & 1; v >>= 1; }
        return count >= 2;
    }

    /// <summary>Stable-partition so PINNED entries lead, in PIN ORDER, followed by the rest in sort order. Applies to
    /// EVERY sort mode including Custom. Returns the leading pin band's length; pinned rows are stamped
    /// <see cref="SidebarLibraryEntry.IsPinned"/> in place.</summary>
    public static int PinsFirst(List<SidebarLibraryEntry> list, IReadOnlyList<SidebarPin>? pins,
                                List<SidebarLibraryEntry> scratch)
    {
        if (pins is null || pins.Count == 0 || list.Count == 0) return 0;

        var rank = new Dictionary<string, int>(pins.Count, StringComparer.Ordinal);
        for (int i = 0; i < pins.Count; i++)
            if (pins[i].Id is { Length: > 0 } id) rank.TryAdd(id, i);

        var foundAt = new Dictionary<int, int>(pins.Count);          // pin index → index in list
        for (int i = 0; i < list.Count; i++)
            if (rank.TryGetValue(list[i].Id, out int pi)) foundAt.TryAdd(pi, i);
        if (foundAt.Count == 0) return 0;

        scratch.Clear();
        for (int pi = 0; pi < pins.Count; pi++)
            if (foundAt.TryGetValue(pi, out int li)) scratch.Add(list[li] with { IsPinned = true });
        int band = scratch.Count;
        for (int i = 0; i < list.Count; i++)
            if (!rank.ContainsKey(list[i].Id)) scratch.Add(list[i]);

        list.Clear();
        for (int i = 0; i < scratch.Count; i++) list.Add(scratch[i]);
        return band;
    }

    /// <summary>Allocating convenience overload (tests, cold paths).</summary>
    public static int PinsFirst(List<SidebarLibraryEntry> list, IReadOnlyList<SidebarPin>? pins)
        => PinsFirst(list, pins, new List<SidebarLibraryEntry>(list.Count));

    /// <summary>Append every projected id into <paramref name="into"/> — the "still present" set
    /// <see cref="SidebarFirstSeen.PruneTo"/> needs on save.</summary>
    public static void CollectIds(IReadOnlyList<SidebarLibraryEntry> list, List<string> into)
    {
        for (int i = 0; i < list.Count; i++) into.Add(list[i].Id);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A FolderStart edge's group id — the bare hex the wire named the folder with (see the region header), or
    /// "~&lt;position&gt;" for a folder that arrived without one. The interned string is returned as-is: no
    /// allocation for a well-formed folder.</summary>
    public static string FolderIdOf(in RootlistEdge edge)
    {
        string hex = Entities.Strings.Resolve(edge.FolderId);
        return hex.Length > 0 ? hex : "~" + ((int)edge.Position).ToString(CultureInfo.InvariantCulture);
    }

    static string OwnerNameOf(in Playlist p)
    {
        var owner = p.Owner;
        return owner.IsValid ? Entities.Strings.Resolve(owner.NameId) : "";
    }

    static long LastPlayed(IReadOnlyDictionary<string, long>? lastPlayed, string? uri) =>
        uri is { Length: > 0 } && lastPlayed is { Count: > 0 } lp && lp.TryGetValue(uri, out long ms) ? ms : 0L;

    // "A, B, C" capped at three names, then "…". One artist returns the source string with no allocation.
    static string JoinArtistNames(ReadOnlySpan<int> artistSlots)
    {
        if (artistSlots.Length == 0) return "";
        if (artistSlots.Length == 1) return new Artist(artistSlots[0]).Name;
        s_join.Clear();
        int n = artistSlots.Length < 3 ? artistSlots.Length : 3;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) s_join.Append(", ");
            s_join.Append(new Artist(artistSlots[i]).Name);
        }
        if (artistSlots.Length > n) s_join.Append('…');
        return s_join.ToString();
    }
}

// ── SidebarBinderPipeline — the pure shaping half ─────────────────────────────────────────────────────────────────────
//
// One 0.3 change inside the trigger fold below: 0.2.9's `LibraryEpoch` was a REFERENCE-identity fold over LibraryStore's
// published cells, because every Refresh/Fill minted a new list instance and instance identity was therefore an exact
// content epoch. There is no such instance in 0.3 — the binder folds the account's per-edge `Version` counters instead
// (`Sidebar.Host.cs`'s `LibraryEpoch()`), which is strictly better: a rename inside a same-length list still moves it,
// and nothing depends on an allocation address. The fold itself, its lanes and `PackV3` are 0.2.9's.

/// <summary>Everything a rebuild depends on, folded to one comparable value. The binder PEEKS these (it never
/// subscribes) and rebuilds only if the fold moved, so a redundant pump or an external <c>Sync()</c> costs one struct
/// compare.</summary>
public readonly record struct SidebarBinderTriggers(
    int LibraryEpoch = 0,
    int PinsVersion = 0,
    int HistoryVersion = 0,
    int PlayLogRevision = 0,
    int LayoutVersion = 0,
    int FolderVersion = 0,
    int OrderVersion = 0,
    int CultureEpoch = 0,
    int V3State = 0,        // packed filter | qualifier | sort | desc | design
    int SearchHash = 0,
    int SourceEpoch = 0,    // bumped by the binder when any registered source raises Changed
    long PlaybackEpoch = 0, // queue revision + now-playing identity
    long LibraryRows = 0,   // SidebarLibraryFingerprint: the row versions of every entity the projection reads
    long FeedTables = 0)    // the publication counters of the tables a DEMANDED feed reads (queue, top tracks, concerts)
{
    /// <summary>Pack the V3 view state (+ the active design) into one lane. Ints, not the enums, because that is how
    /// the preferences store them.</summary>
    public static int PackV3(int design, int filter, int qualifier, int sort, bool descending)
        => (design & 0xF) | ((filter & 0xFF) << 4) | ((qualifier & 0xFF) << 12)
         | ((sort & 0xFF) << 20) | (descending ? 1 << 28 : 0);

    /// <summary>A 64-bit avalanche of every lane — the binder's change gate. Deterministic: the same lanes give the
    /// same fold, so the gate can never depend on anything but the epochs above.</summary>
    public long Fold()
    {
        unchecked
        {
            ulong h = 1469598103934665603UL;
            h = Mix(h, (uint)LibraryEpoch);
            h = Mix(h, (uint)PinsVersion);
            h = Mix(h, (uint)HistoryVersion);
            h = Mix(h, (uint)PlayLogRevision);
            h = Mix(h, (uint)LayoutVersion);
            h = Mix(h, (uint)FolderVersion);
            h = Mix(h, (uint)OrderVersion);
            h = Mix(h, (uint)CultureEpoch);
            h = Mix(h, (uint)V3State);
            h = Mix(h, (uint)SearchHash);
            h = Mix(h, (uint)SourceEpoch);
            h = Mix(h, (uint)PlaybackEpoch);
            h = Mix(h, (uint)(PlaybackEpoch >> 32));
            h = Mix(h, (uint)LibraryRows);
            h = Mix(h, (uint)(LibraryRows >> 32));
            h = Mix(h, (uint)FeedTables);
            h = Mix(h, (uint)(FeedTables >> 32));
            return (long)h;
        }
    }

    static ulong Mix(ulong h, uint v) => SidebarLibraryFingerprint.Mix(h, v);
}

/// <summary>
/// THE REBUILD GATE'S CONTENT LANE (G-180, sidebar decision D8). Every table the projection joins — Playlists, Users,
/// Albums, Artists, Shows — publishes <c>Changed</c> for ANY row in the app, so a gate on those counters rebuilds the
/// whole three-pass library every time an unrelated album page hydrates. Every row, though, carries a generational
/// <c>Version</c> that bumps on each write to THAT row (<c>Table.Version</c>, D8). This folds exactly the rows the
/// projection reads — the rootlist's playlists and their owners, the saved albums with their first three billed
/// artists, the followed artists, the saved shows, and the entity rows an unlisted pin resolves through — into one
/// 64-bit value: a hydration of a sidebar row moves it, a hydration anywhere else does not.
///
/// <para>Zero allocation, a few column loads per library row, no string ever materialised: cheap enough to run on
/// every pump wake. Folder markers carry no row (their names ride the rootlist edge, whose own version is in
/// <c>LibraryEpoch</c>).</para>
/// </summary>
public static class SidebarLibraryFingerprint
{
    /// <summary>The FNV-1a offset basis every sidebar fold starts from.</summary>
    public const ulong Seed = 1469598103934665603UL;

    /// <summary>One FNV-1a step.</summary>
    public static ulong Mix(ulong h, uint v)
    {
        unchecked { h ^= v; h *= 1099511628211UL; return h; }
    }

    /// <summary>The fold for <paramref name="u"/>'s library plus <paramref name="extra"/> rows (an unlisted pin's
    /// entity). An out-of-range or zero slot folds a constant, so a hole cannot alias a real row's version.</summary>
    public static long Of(in User u, ReadOnlySpan<EntityRef> extra)
    {
        var scope = Entities.Current;
        var edges = scope.Edges;
        ulong h = Seed;

        var playlists = scope.Playlists;
        var users = scope.Users;
        var rootlist = u.RootlistSlots;
        for (int i = 0; i < rootlist.Length; i++) h = PlaylistRow(h, playlists, users, rootlist[i]);

        var albums = scope.Albums;
        var artists = scope.Artists;
        var saved = u.SavedAlbumSlots;
        for (int i = 0; i < saved.Length; i++)
        {
            int slot = saved[i];
            h = Row(h, albums, slot);
            if ((uint)slot >= (uint)albums.Count) continue;
            h = Mix(h, edges.AlbumArtists.Version(slot));
            var billed = edges.AlbumArtists.Targets(slot);
            int n = billed.Length < 3 ? billed.Length : 3;
            for (int j = 0; j < n; j++) h = Row(h, artists, billed[j]);
        }

        var followed = u.FollowedArtistSlots;
        for (int i = 0; i < followed.Length; i++) h = Row(h, artists, followed[i]);

        var shows = scope.Shows;
        var savedShows = u.SavedShowSlots;
        for (int i = 0; i < savedShows.Length; i++) h = Row(h, shows, savedShows[i]);

        for (int i = 0; i < extra.Length; i++)
        {
            var r = extra[i];
            h = r.Kind switch
            {
                EntityKind.Playlist => PlaylistRow(h, playlists, users, r.Slot),
                EntityKind.Album => Row(h, albums, r.Slot),
                EntityKind.Artist => Row(h, artists, r.Slot),
                EntityKind.Show => Row(h, shows, r.Slot),
                _ => Mix(h, 0xFFFF_FFFFu),
            };
        }
        return (long)h;
    }

    static ulong Row(ulong h, Table table, int slot)
        => slot > Table.None && slot < table.Count ? Mix(h, table.Version[slot]) : Mix(h, 0xFFFF_FFFEu);

    // Bug A2: the mosaic needs each member TRACK's own AlbumSlot/ImageId, which lands in a LATER drain than the
    // membership edge itself (the batched `SidebarProjection.MosaicTrackFields` ask over the bare uri-only slots the
    // edge answer created) — the edge's own Version does not move for that, only the track row's does. Folded over
    // exactly the prefix that ask covers (`SidebarProjection.MosaicTrackPrefix`), so every landing it caused repaints.
    const int MosaicFoldCap = SidebarProjection.MosaicTrackPrefix;

    // A playlist row names its OWNER through the Users table (the creator line and the ByYou/BySpotify flavor), so the
    // owner row's version rides with it.
    static ulong PlaylistRow(ulong h, PlaylistTable playlists, Table users, int slot)
    {
        h = Row(h, playlists, slot);
        if (slot <= Table.None || slot >= playlists.Count) return h;
        h = Row(h, users, playlists.Owner[slot]);
        // G-059: a cover-less playlist's 2×2 mosaic depends on the PlaylistTracks edge, a table this row's OWN version
        // does not move for (an edge is a separate relation, versioned separately — Playlist.MembershipVersion). Fold
        // it in ONLY for a playlist that actually has no cover of its own, so every other row pays nothing extra.
        if (playlists.Image[slot].IsEmpty)
        {
            h = Mix(h, Entities.Current.Edges.PlaylistTracks.Version(slot));
            // Bug A2: fold a bounded prefix of the member tracks' OWN row versions too, so a track's Identity
            // landing after the edge already did moves the fold and the tiles actually repaint.
            var tracks = Entities.Current.Tracks;
            var members = new Playlist(slot).TrackSlots;
            int n = members.Length < MosaicFoldCap ? members.Length : MosaicFoldCap;
            for (int i = 0; i < n; i++) h = Row(h, tracks, members[i]);
        }
        else if (!new Playlist(slot).Knows(PlaylistFields.TrackCount))
        {
            // D2: a covered row whose count never landed reads it off resident membership (`WalkRootlist`'s
            // fallback), so the edge's version must move this row too — else the count appears only on an unrelated
            // rebuild.
            h = Mix(h, Entities.Current.Edges.PlaylistTracks.Version(slot));
        }
        return h;
    }
}

/// <summary>Your Library's shaping state: the chip, the hidden kinds, the sort and the search.</summary>
public readonly record struct SidebarLibraryQuery(
    SidebarLibraryFilter Filter = SidebarLibraryFilter.None,
    SidebarLibraryKinds HiddenKinds = SidebarLibraryKinds.None,
    SidebarLibrarySort Sort = SidebarLibrarySort.Recents,
    bool Descending = false,
    string? Search = null);

/// <summary>What one shaping pass produced: how many rows were published and how long the leading pin band is.</summary>
public readonly record struct SidebarEntriesShape(int Count, int PinCount);

public static class SidebarBinderPipeline
{
    /// <summary>Shape the unified projection into the list Your Library renders: FILTER (kinds, the chip's exact match,
    /// search), then SORT, then the pins-first partition — the order that makes pins lead in every sort mode.
    /// <paramref name="all"/> is the full source-order projection (every kind); <paramref name="into"/> and
    /// <paramref name="scratch"/> are caller-owned and reused.
    ///
    /// <para>A persisted Custom sort outside the Playlists filter falls back to Alphabetical FOR DISPLAY, leaving the
    /// preference untouched (<see cref="SidebarSort.Effective"/>).</para></summary>
    public static SidebarEntriesShape Project(
        IReadOnlyList<SidebarLibraryEntry>? all,
        List<SidebarLibraryEntry> into,
        List<SidebarLibraryEntry> scratch,
        in SidebarLibraryQuery query,
        IReadOnlyList<SidebarPin>? pins = null)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(scratch);
        into.Clear();
        if (all is null || all.Count == 0) return new SidebarEntriesShape(0, 0);

        var kinds = SidebarEntryKinds.From(query.Filter);
        for (int i = 0; i < all.Count; i++)
            if (SidebarEntryKinds.Has(kinds, all[i].Kind)) into.Add(all[i]);

        return Shape(into, scratch, in query, pins);
    }

    /// <summary>The IN-PLACE half of <see cref="Project"/>, for a list that already holds the kinds the filter's MASK wants:
    /// compact by hidden kinds + the chip's exact match (the Podcasts/Audiobooks split the mask cannot express) + search,
    /// then sort, then partition pins to the front.</summary>
    public static SidebarEntriesShape Shape(
        List<SidebarLibraryEntry> list,
        List<SidebarLibraryEntry> scratch,
        in SidebarLibraryQuery query,
        IReadOnlyList<SidebarPin>? pins = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scratch);

        string search = SidebarSearch.Normalize(query.Search);
        bool searching = search.Length > 0;
        // Not only while searching: the kind mask cannot split Podcasts from Audiobooks (both are Show) and does not know the
        // kinds hidden through Filters, so the loop also runs for a hidden kind and for those two chips.
        bool narrow = searching || query.HiddenKinds != SidebarLibraryKinds.None
                      || query.Filter is SidebarLibraryFilter.Podcasts or SidebarLibraryFilter.Audiobooks;

        if (narrow)
        {
            int write = 0;
            for (int read = 0; read < list.Count; read++)
            {
                var e = list[read];
                if (SidebarLibraryFilters.IsHidden(query.HiddenKinds, in e)) continue;
                if (query.Filter != SidebarLibraryFilter.None && !SidebarLibraryFilters.Matches(query.Filter, in e)) continue;
                if (searching)
                {
                    // Searching FLATTENS: matching leaves only, no folder chrome.
                    if (e.Kind == SidebarEntryKind.Folder) continue;
                    if (!SidebarSearch.Matches(in e, search)) continue;
                }
                list[write++] = e;
            }
            if (write < list.Count) list.RemoveRange(write, list.Count - write);
        }

        // No custom-order overlay: Custom order is the rootlist's own source order.
        SidebarSort.Apply(list, SidebarSort.Effective(query.Sort, query.Filter), query.Descending, null);
        int band = SidebarProjection.PinsFirst(list, pins, scratch);
        return new SidebarEntriesShape(list.Count, band);
    }

    /// <summary>Build the row for a pin the live projection does NOT know — an editorial/Spotify-owned entity never
    /// saved to the user's own library/rootlist. Renders the pin's own offline display cache first (an unresolved pin
    /// must never disappear), then OVERLAYS <paramref name="hydrated"/> once the binder resolves the handle.
    /// <para>Trap 5: <paramref name="rootlistState"/> defaults to <see cref="EdgeState.Complete"/> — a caller that
    /// does not pass it (every non-folder-pin-state test, and any future caller that genuinely does not care) keeps
    /// the pre-existing "not found ⇒ missing" behaviour; only a FOLDER pin ever reads it at all.</para></summary>
    public static SidebarLibraryEntry ResolveUnlistedPin(SidebarPin pin, int sourceOrder, SidebarLibraryEntry? hydrated,
        EdgeState rootlistState = EdgeState.Complete)
    {
        // A folder pin has no separate hydration path (below) — its ONLY source of truth is the rootlist walk that
        // already ran this rebuild, so "not found" here means "not in the account's rootlist" only once that
        // rootlist has genuinely answered THIS session (bug A follow-up, trap 5: the rootlist is network-only,
        // never persisted, so a "not found" this early is "not yet known", not "gone").
        bool folder = pin.Kind == SidebarEntryKind.Folder;
        var folderState = folder ? SidebarProjection.ResolveFolderPinState(rootlistState, foundInProjection: false)
                                  : SidebarPinFolderState.Normal;
        var baseEntry = new SidebarLibraryEntry(
            pin.Id, pin.Kind, pin.Uri, pin.Name, "", StringId.Empty, null,
            ChildCount: 0, AddedAtMs: pin.AddedAtMs, SortStamp: pin.AddedAtMs, LastVisitedTicksUtc: 0,
            SourceOrder: sourceOrder, Depth: 0, Circular: pin.Kind == SidebarEntryKind.Artist,
            Flavor: SidebarPlaylistFlavor.None)
        {
            IsPinned = true, FolderId = folder ? SidebarPinId.FolderIdOf(pin.Id) : "", FolderName = "",
            FirstArtistName = "", Missing = folderState == SidebarPinFolderState.Missing,
            // Pending or Missing, there is no real item count to show either way — never a confident "0 items"
            // (the folder subtitle's own gate; see SidebarLibraryEntry.CountKnown's doc).
            CountKnown = false,
        };

        if (hydrated is not { } h) return baseEntry;
        return baseEntry with
        {
            Name = h.Name.Length > 0 ? h.Name : baseEntry.Name,          // the ylpin bridge mints "" — the entity names it
            Cover = h.Cover.IsEmpty ? baseEntry.Cover : h.Cover,
            // G-059: baseEntry's is always null (the pin's own display cache carries no mosaic) — h's is
            // ResolveLivePin's fresh read, so it wins outright whenever hydration ran at all.
            MosaicTiles = h.MosaicTiles,
            ChildCount = h.ChildCount,
            Creator = h.Creator.Length > 0 ? h.Creator : baseEntry.Creator,
            FirstArtistName = h.FirstArtistName.Length > 0 ? h.FirstArtistName : baseEntry.FirstArtistName,
            // Bug H: `hydrated` is non-null only past ResolveLivePin's own `Knows(Identity)` gate, so the merged
            // row's identity IS known — never leave this on baseEntry's default false, or an unlisted pin's
            // subtitle stays hidden forever even once hydration lands.
            IdentityKnown = true,
            // Bug A1: unlike Identity, the count is NOT unconditionally true past that same gate — ResolveLivePin
            // stamps its own `CountKnown` from the real `PlaylistFields.TrackCount` bit (never hardcoded), and this
            // overlay must forward it rather than repeat the A1 mistake one layer up.
            CountKnown = h.CountKnown,
        };
    }

}

// ── SidebarEntriesShadow — whether a rebuild bumps the version ───────────────────────────────────────────────────────
//
// A shadow snapshot of the last published projection, compared exactly, so a rebuild that produced byte-identical
// content does not bump the version — otherwise a redundant re-projection storms every bound sidebar pane. Superseded
// in spirit by Entities' per-table Version/Changed counters, but the PROPERTY still ports: a publish that changed
// nothing must not bump Changed.

public readonly record struct SidebarEntriesMeta(
    int State,
    Exception? Error,
    bool AnyContributingKindPending,
    bool QualifiersAvailable,
    int PinCount);

public sealed class SidebarEntriesShadow
{
    readonly List<SidebarLibraryEntry> _published = new();
    SidebarEntriesMeta _meta;
    bool _seeded;

    public IReadOnlyList<SidebarLibraryEntry> Published => _published;

    /// <summary>Record a completed rebuild. Returns true when it DIFFERS from the last one (the caller must bump its
    /// version), false when identical. The first publish always counts as a change.</summary>
    public bool Publish(IReadOnlyList<SidebarLibraryEntry> entries, in SidebarEntriesMeta meta)
    {
        bool sameRows = SameRows(entries);
        if (_seeded && sameRows && _meta.Equals(meta)) return false;
        _seeded = true;
        _meta = meta;
        if (!sameRows) Capture(entries);
        return true;
    }

    void Capture(IReadOnlyList<SidebarLibraryEntry> entries)
    {
        _published.Clear();
        if (entries is null) return;
        if (_published.Capacity < entries.Count) _published.Capacity = entries.Count;
        for (int i = 0; i < entries.Count; i++) _published.Add(entries[i]);
    }

    bool SameRows(IReadOnlyList<SidebarLibraryEntry> entries)
    {
        if (entries is null) return _published.Count == 0;
        if (entries.Count != _published.Count) return false;
        for (int i = 0; i < entries.Count; i++)
        {
            var a = _published[i];
            var b = entries[i];
            if (!SameEntry(in a, in b)) return false;
        }
        return true;
    }

    /// <summary>Exact entry equality. <c>MosaicTiles</c> is an <c>IReadOnlyList&lt;StringId&gt;</c> the projection
    /// materialises fresh on every rebuild, so it is compared BY VALUE — never a reference compare, which would report
    /// every folder row as changed on every pass.</summary>
    public static bool SameEntry(in SidebarLibraryEntry a, in SidebarLibraryEntry b)
    {
        if (ReferenceEquals(a.MosaicTiles, b.MosaicTiles)) return a.Equals(b);
        if (!SameTiles(a.MosaicTiles, b.MosaicTiles)) return false;
        return (a with { MosaicTiles = b.MosaicTiles }).Equals(b);
    }

    static bool SameTiles(IReadOnlyList<StringId>? a, IReadOnlyList<StringId>? b)
    {
        if (a is null || b is null) return false;   // the ReferenceEquals caller already handled null == null
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }
}

/// <summary>id/uri → projected entry, built ONCE per rebuild off the unified projection and shared by every feed
/// adapter. Both keys live in one map: entry ids and bare uris are disjoint namespaces, so one lookup serves both.</summary>
public sealed class SidebarSourceIndex
{
    public static readonly SidebarSourceIndex Empty = new();

    readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
    IReadOnlyList<SidebarLibraryEntry> _entries = Array.Empty<SidebarLibraryEntry>();

    public int Count => _byKey.Count;

    /// <summary>Point the index at a freshly built projection. ALIASED, not copied — must stay alive and unmodified
    /// until the next rebuild.</summary>
    public void Rebuild(IReadOnlyList<SidebarLibraryEntry> entries)
    {
        _entries = entries;
        _byKey.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Id.Length > 0) _byKey.TryAdd(e.Id, i);
            if (e.Uri.Length > 0) _byKey.TryAdd(e.Uri, i);
        }
    }

    public bool TryGet(string? key, out SidebarLibraryEntry entry)
    {
        if (key is { Length: > 0 } && _byKey.TryGetValue(key, out int i) && (uint)i < (uint)_entries.Count)
        {
            entry = _entries[i];
            return true;
        }
        entry = default;
        return false;
    }

    /// <summary>The IReadOnlyDictionary face the planner wants, without materialising a second map.</summary>
    public IReadOnlyDictionary<string, SidebarLibraryEntry> AsLookup() => _view ??= new View(this);
    View? _view;

    sealed class View(SidebarSourceIndex owner) : IReadOnlyDictionary<string, SidebarLibraryEntry>
    {
        public bool TryGetValue(string key, out SidebarLibraryEntry value) => owner.TryGet(key, out value);
        public bool ContainsKey(string key) => owner._byKey.ContainsKey(key);
        public SidebarLibraryEntry this[string key] =>
            owner.TryGet(key, out var v) ? v : throw new KeyNotFoundException(key);
        public int Count => owner._byKey.Count;
        public IEnumerable<string> Keys => owner._byKey.Keys;

        public IEnumerable<SidebarLibraryEntry> Values
        {
            get { foreach (var kv in owner._byKey) yield return owner._entries[kv.Value]; }
        }

        public IEnumerator<KeyValuePair<string, SidebarLibraryEntry>> GetEnumerator()
        {
            foreach (var kv in owner._byKey)
                yield return new KeyValuePair<string, SidebarLibraryEntry>(kv.Key, owner._entries[kv.Value]);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

// ── PINS, THE PIN MENU ROW AND THE EDIT PLAN ─────────────────────────────────────────────────────────────────────────
//
// The pin id IS the nav route key — which is why a pinned row renders through the same route table as any other
// destination with no extra plumbing, why the recency join is an identity lookup, and why a pin survives a library
// refresh. `PinRowRule` lands here by arbitration A7: it is pin semantics, it lives beside `SidebarPinId`, and the
// menu that shows its row is chapter 25's.

// ── 5. the pin identity scheme ───────────────────────────────────────────────────────────────────────────────────────

// The pin id IS the nav route key for every navigable kind. That buys three things at once: a pinned row renders its
// label/glyph through the shell's route lookup with no extra plumbing, the recency join against history is an
// identity lookup on the route name, and a pin survives a library refresh because it never depends on a list index.

/// <summary>One pinned sidebar item. <see cref="Id"/> is the STABLE identity and also the nav route key for every
/// kind except <see cref="SidebarEntryKind.Folder"/>. <see cref="Name"/>/<see cref="Uri"/> are a display CACHE so a
/// pinned row paints instantly offline before the library resolves; they are refreshed by the projection and are
/// never the source of truth. <see cref="Kind"/> is <see cref="SidebarEntryKind"/> — the SAME vocabulary the
/// projection uses.</summary>
public sealed record SidebarPin(string Id, SidebarEntryKind Kind, string Uri, string Name, long AddedAtMs)
{
    /// <summary>Consumer alias — the pin store keys on <see cref="Id"/>.</summary>
    public string Key => Id;

    /// <summary>The nav route this pin opens; "" for a folder (it expands in place, it never navigates).</summary>
    public string RouteKey => SidebarPinId.RouteOf(Id) ?? "";
}

public static class SidebarPinId
{
    // ── prefixes (one place, so a parse and a build can never disagree) ──
    public const string PlaylistPrefix = "pl:";
    public const string AlbumPrefix = "album:";
    public const string ArtistPrefix = "artist:";
    public const string ShowPrefix = "show:";
    public const string FolderPrefix = "folder:";

    /// <summary>The pre-seeded app routes shown by pickers. Dynamic route families (see
    /// <see cref="PinnableRoutePrefixes"/>) are also pinnable when reached; their instances only exist at runtime,
    /// so they are recognised by prefix rather than enumerated here.</summary>
    /// <remarks>"recents" is the full recently-played page: a destination a user may pin, never one the shell
    /// mandates.</remarks>
    public static readonly string[] PinnableRoutes =
        ["search", "albums", "artists", "podcasts", "audiobooks", "local", "history", "recents"];

    /// <summary>Real, durable pages that <see cref="FromRoute"/> accepts but that is deliberately not
    /// seeded — reachable from an artist page, not offered alongside Home and Search. Pinnable when REACHED,
    /// not suggested.</summary>
    public static readonly string[] AlsoPinnableRoutes = ["concerts"];

    /// <summary>The dynamic route FAMILIES a pin may address: the entity kinds (whose ids double as pin ids), plus
    /// the app's own generated pages. Spelled as LITERALS because this file is engine-free and cannot see the
    /// engine-bound files that own the constants. Adding a route family means adding it here too — an UNRECOGNISED
    /// key is refused, so a third-party entity scheme or a typo can never become a pin that renders as a
    /// fallback.</summary>
    public static readonly string[] PinnableRoutePrefixes =
    [
        PlaylistPrefix, AlbumPrefix, ArtistPrefix, ShowPrefix, FolderPrefix,
        "prerelease:", "home-section:", "browse:", "disco:", "artist-concerts:",
        // A page a playback MODULE describes (`module:wavee:module:<id>:<b64(entityId)>`) — durable because the id is
        // the module's own stable entity id, not a session handle.
        "module:",
    ];

    /// <summary>Real pages that are never pins. The first three are tooling/editor surfaces; the last is a report
    /// reached from a dialog.</summary>
    static readonly string[] UnpinnableRoutes =
        ["settings", "api-console", "home-customize", "playback-diagnostics"];

    /// <summary>One dated event. Its page is real and navigable, but a concert happens and is then over, so a pin
    /// would decay into a dead row — the durable destinations are the hub ("concerts") and an artist's schedule
    /// ("artist-concerts:"), both of which <see cref="FromRoute"/> accepts.</summary>
    const string EventRoutePrefix = "concert:";

    /// <summary>Liked Songs is a ROUTE pin, not a playlist pin, so a pin made from the detail page and a pin made
    /// from a sidebar row are the SAME pin.</summary>
    public const string LikedSongsUri = EntityUri.LikedCollection;

    /// <summary>The one pin identity a store / menu / drop must use. Accepts a pin id, a bare entity uri, or a route
    /// key and returns the canonical id — so a card drop that carried <c>spotify:playlist:…</c> and a menu that
    /// looks up <c>pl:spotify:playlist:…</c> resolve to the SAME pin. Null = not pinnable.</summary>
    public static string? Canonical(string? idOrUri)
    {
        if (string.IsNullOrEmpty(idOrUri)) return null;
        if (KindOf(idOrUri) != SidebarEntryKind.AppRoute) return idOrUri;   // already a prefixed pin id
        if (idOrUri.StartsWith("spotify:", StringComparison.Ordinal)
            || idOrUri.StartsWith("wavee:", StringComparison.Ordinal))
            return FromUri(idOrUri);                                   // an entity uri never becomes a route pin
        return FromRoute(idOrUri);
    }

    /// <summary>The legacy raw-uri form a card/hero drop used to persist as the pin id (the payload's uri, not the
    /// pin id). Empty when the id is a route or folder. Used so a pinned-check still finds those rows until the
    /// store migrates them.</summary>
    public static string LegacyUriAlias(string? pinId)
    {
        if (string.IsNullOrEmpty(pinId)) return "";
        string uri = UriOf(pinId);
        return uri.Length > 0 && !string.Equals(uri, pinId, StringComparison.Ordinal) ? uri : "";
    }

    /// <summary>uri → pin id. Null = not pinnable. Tracks and episodes are NEVER pinnable, enforced HERE, in one
    /// function, rather than per menu.</summary>
    public static string? FromUri(string? uri) => uri switch
    {
        null or "" => null,
        // Every liked spelling collapses to the ONE route pin.
        var u when EntityUri.IsLikedCollection(u) => "liked",                           // a ROUTE pin
        // Playlists are pinnable from either provider (Spotify AND session-local `wavee:playlist:*`);
        // album/artist/show stay Spotify-only, as the schemes were.
        var u when EntityUri.KindOf(u) == EntityKind.Playlist => PlaylistPrefix + u,
        // A rootlist folder's wire uri (`spotify:folder:<hex>`) — the spelling a folder DRAG payload carries, so a folder
        // dropped on the pin band pins exactly like one pinned from its menu (PinSyncRules.TryPinId is the same map).
        // Ahead of the Parse arms: a folder uri is not a catalogue entity and must not be interned as one.
        var u when EntityUri.FolderIdOf(u).Length > 0 => ForFolder(EntityUri.FolderIdOf(u).ToString()),
        var u when EntityUri.Parse(u) is { Provider: EntityProvider.Spotify, Kind: EntityKind.Album } => AlbumPrefix + u,
        var u when EntityUri.Parse(u) is { Provider: EntityProvider.Spotify, Kind: EntityKind.Artist } => ArtistPrefix + u,
        var u when EntityUri.Parse(u) is { Provider: EntityProvider.Spotify, Kind: EntityKind.Show } => ShowPrefix + u,
        _ => null,                                                                      // tracks, episodes, everything else
    };

    /// <summary>Route key → pin id, and the app's one route RECOGNISER. Every durable application destination is
    /// stable enough to pin — the seeded <see cref="PinnableRoutes"/> set, <see cref="AlsoPinnableRoutes"/>, and
    /// the dynamic <see cref="PinnableRoutePrefixes"/> families. Refused: tooling/editor surfaces, one dated event,
    /// and anything UNRECOGNISED.
    ///
    /// <para>That last clause is load-bearing: callers use this as a recogniser, not just a policy filter. A version
    /// that returned every non-empty string made "is this stored key a route?" unanswerable — a third-party entity
    /// uri came back as a route pin with an empty entity uri, and any typo became a pin that painted as a
    /// fallback.</para></summary>
    public static string? FromRoute(string? routeKey)
    {
        if (string.IsNullOrWhiteSpace(routeKey)) return null;
        if (SidebarPinRules.IsFixedRoute(routeKey)) return null;

        for (int i = 0; i < UnpinnableRoutes.Length; i++)
            if (string.Equals(UnpinnableRoutes[i], routeKey, StringComparison.Ordinal)) return null;

        // Checked BEFORE the prefix families: an explicit refusal documents the event/hub split.
        if (routeKey.StartsWith(EventRoutePrefix, StringComparison.Ordinal)) return null;

        for (int i = 0; i < PinnableRoutePrefixes.Length; i++)
            if (routeKey.StartsWith(PinnableRoutePrefixes[i], StringComparison.Ordinal)) return routeKey;

        for (int i = 0; i < PinnableRoutes.Length; i++)
            if (string.Equals(PinnableRoutes[i], routeKey, StringComparison.Ordinal)) return routeKey;

        for (int i = 0; i < AlsoPinnableRoutes.Length; i++)
            if (string.Equals(AlsoPinnableRoutes[i], routeKey, StringComparison.Ordinal)) return routeKey;

        return null;
    }

    public static bool IsPinnableRoute(string? routeKey) => FromRoute(routeKey) is not null;

    /// <summary>A rootlist group id → its pin id. Folders are pinnable even though they never navigate.</summary>
    public static string ForFolder(string folderId) => FolderPrefix + folderId;

    /// <summary>Prefix dispatch; no known prefix ⇒ <see cref="SidebarEntryKind.AppRoute"/> (the bare-route form).</summary>
    public static SidebarEntryKind KindOf(string? pinId) =>
        pinId is null ? SidebarEntryKind.AppRoute
        : pinId.StartsWith(PlaylistPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Playlist
        : pinId.StartsWith(AlbumPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Album
        : pinId.StartsWith(ArtistPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Artist
        : pinId.StartsWith(ShowPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Show
        : pinId.StartsWith(FolderPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Folder
        : SidebarEntryKind.AppRoute;

    /// <summary>The nav route a pin opens. Null for <see cref="SidebarEntryKind.Folder"/> — every other kind's id
    /// IS its route key.</summary>
    public static string? RouteOf(string pinId) => KindOf(pinId) == SidebarEntryKind.Folder ? null : pinId;

    /// <summary>The entity uri behind a pin id ("" for a route or folder pin). The inverse of <see cref="FromUri"/>
    /// for the prefixed kinds — used when a pinned row needs a play/share target and the display cache is stale.</summary>
    public static string UriOf(string pinId) => KindOf(pinId) switch
    {
        SidebarEntryKind.Playlist => pinId.Substring(PlaylistPrefix.Length),
        SidebarEntryKind.Album => pinId.Substring(AlbumPrefix.Length),
        SidebarEntryKind.Artist => pinId.Substring(ArtistPrefix.Length),
        SidebarEntryKind.Show => pinId.Substring(ShowPrefix.Length),
        SidebarEntryKind.AppRoute when string.Equals(pinId, "liked", StringComparison.Ordinal) => LikedSongsUri,
        _ => "",
    };

    /// <summary>The rootlist group id behind a folder pin ("" when the pin is not a folder).</summary>
    public static string FolderIdOf(string pinId) =>
        KindOf(pinId) == SidebarEntryKind.Folder ? pinId.Substring(FolderPrefix.Length) : "";

    /// <summary>Whether a <see cref="SidebarEntryKind"/> can ever back a pin. <see cref="SidebarEntryKind.Track"/> is
    /// the ONE refusal — every other kind is either a real navigable library entity or the bare-route family. Call
    /// this at every pin-CREATION boundary rather than inferring pinnability from a fallback.</summary>
    public static bool IsPinnable(SidebarEntryKind kind) => kind != SidebarEntryKind.Track;

    /// <summary>The pin id for a projected entry — the entry Id already IS the pin id, so this is the identity with
    /// the not-pinnable kinds screened out: internal routes and TRACK rows (queue / now playing / artist top tracks)
    /// are never pinnable.</summary>
    public static string? FromEntry(in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.AppRoute => FromRoute(e.Id),
        SidebarEntryKind.Track => null,
        _ => e.Id,
    };
}

// ── 6. pin sync with Spotify's ylpin set ─────────────────────────────────────────────────────────────────────────────

/// <summary>The ONE rule for which sidebar pins mirror Spotify's <c>ylpin</c> set, and how a pin id and a wire uri
/// map onto each other. Everything that is NOT a Spotify playlist/album/artist/show, a rootlist folder, or the Liked
/// Songs route stays a local-only pin — app routes, <c>wavee:</c> playlists.
///
/// <para>Liked Songs is <c>spotify:collection</c> on the wire (captured with <c>--spotify-collection pins</c>). The
/// READ side already accepts every spelling <see cref="EntityUri.IsLikedCollection"/> recognises, so this is the one
/// write-side spelling that matters.</para></summary>
public static class PinSyncRules
{
    /// <summary>pin id → the collection2v2 item uri, or null when this pin is local-only.</summary>
    public static string? TryWireUri(string? pinId, string username)
    {
        if (string.IsNullOrEmpty(pinId)) return null;
        if (SidebarPinRules.IsFixedRoute(pinId)) return null;
        switch (SidebarPinId.KindOf(pinId))
        {
            case SidebarEntryKind.Playlist:
            case SidebarEntryKind.Album:
            case SidebarEntryKind.Artist:
            case SidebarEntryKind.Show:
                string uri = SidebarPinId.UriOf(pinId);
                return uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri : null;
            case SidebarEntryKind.Folder:
                string folderId = SidebarPinId.FolderIdOf(pinId);
                return folderId.Length > 0 ? EntityUri.FolderPrefix + folderId : null;
            default:
                return null;   // app routes only
        }
    }

    /// <summary>wire uri → the canonical pin id, or null when the server sent something this client cannot pin
    /// (a track, an episode, an unknown scheme, a binary blob).</summary>
    public static string? TryPinId(string? wireUri)
    {
        if (string.IsNullOrEmpty(wireUri) || !wireUri.StartsWith("spotify:", StringComparison.Ordinal)) return null;
        if (EntityUri.FolderIdOf(wireUri) is { Length: > 0 } folderId) return SidebarPinId.ForFolder(folderId.ToString());
        var id = SidebarPinId.FromUri(wireUri);   // every Liked spelling becomes "liked", which the fixed-route guard refuses
        return id is null || SidebarPinRules.IsFixedRoute(id) ? null : id;
    }

    public static bool IsSyncable(string? pinId, string username) => TryWireUri(pinId, username) is not null;
}

// ── 7. the pin menu-row decision ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>Which pin row a menu shows. An ABSOLUTE-state pair, never a toggle — matching Spotify's own menu, which
/// shows exactly one of "Pin to sidebar" / "Unpin from sidebar" (a mis-checked toggle would invert the user's
/// intent).</summary>
public enum PinRowKind : byte
{
    /// <summary>No row at all — the target is not pinnable, or the pin store is absent (the feature's kill switch).
    /// The menu omits the row rather than showing a dead one.</summary>
    None = 0,
    Pin = 1,
    Unpin = 2,
}

public static class PinRowRule
{
    /// <summary>The one rule. <paramref name="hasStore"/> false ⇒ <see cref="PinRowKind.None"/> (a host with no pin
    /// store has no pins at all, so a row would be a lie); an unpinnable target (decided upstream by
    /// <see cref="SidebarPinId"/>) ⇒ <see cref="PinRowKind.None"/>; otherwise the row is whichever verb applies to
    /// the CURRENT pinned state.</summary>
    public static PinRowKind Decide(bool hasStore, string? pinId, bool isPinned)
    {
        if (!hasStore || string.IsNullOrEmpty(pinId) || SidebarPinRules.IsFixedRoute(pinId)) return PinRowKind.None;
        return isPinned ? PinRowKind.Unpin : PinRowKind.Pin;
    }
}

/// <summary>What the folder rename prompt commits (G-171, 0.2.9 <c>FolderActions.Rename</c>): the TRIMMED text, or null
/// when there is nothing to send — a blank name (a folder with no name is indistinguishable in a list of folders) or
/// the name it already has (a no-op write that would still round-trip the rootlist and toast).</summary>
public static class SidebarFolderRename
{
    public static string? Commit(string? typed, string current)
    {
        string next = (typed ?? "").Trim();
        if (next.Length == 0 || string.Equals(next, current, StringComparison.Ordinal)) return null;
        return next;
    }
}
