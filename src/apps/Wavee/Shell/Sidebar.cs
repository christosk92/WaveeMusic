// ── Shell/Sidebar.cs ───────────────────────────────────────────────────────────────────────────────────────────────
// projection / binder / planner / sources / geometry / drop / selection / edit and every pure rule of §6; PinRowRule
// lands here (A7)
//
// Role: CORE
// Owner: J
// Wave: 4
// Budget: 5000 lines
// Spec: ch 26 §9.4 (9,000 less the 4,000 that leaves for Sidebar.Doc.cs)
//
// Every decision the sidebar makes that is not a pixel and not a file. Pure, engine-free, allocation-free after
// warm-up, and source-visible to `Wavee.Tests` — which is the point: the sidebar's hard parts are its RULES (where a
// drop lands, which row is selected, how tall a band is, what order pins take in every sort mode), and a rule that
// lives inside a renderer cannot be pinned by a test.
//
// The sections, in file order:
//
//   GEOMETRY   `SidebarRowGeometry`, `SidebarRowExtents` — the one height/indent/art/lane ladder. Art starts at pane
//              x = 21 on EVERY row shape; one height per SECTION, never per row (a mixed band breaks both the
//              `Reorderable` slot pitch and the virtualizing host's extent table).
//   PLAN       `SidebarRowPlanner` + `SidebarRow`/`SidebarRowPlan`/`SidebarPlanBuffers` — (document × projection)
//              flattened into ONE array of row kinds, rendered by ONE bound list. The buffers are caller-owned and
//              ALIAS, so the outgoing rows survive the diff.
//   DIFF       `SidebarRowDiff`, `SidebarRowResolve`, `SidebarPillState` — which realized rows re-render, which row
//              draws selected, and the one lit/dark rule for the accent pill.
//   DROP       `RootlistSlotResolver` + `SidebarDropCue` + `RootlistDropDecision` + `RootlistTreeNav` — ONE resolver,
//              ONE published slot, ONE commit. Line ⟺ ordering, plate ⟺ Into, never both. Every refusal has a
//              sentence; a drop that cannot be honoured never returns silently.
//   SELECTION  `SidebarTreeSelection` — WinUI extended multi-select semantics, keyed by row ID because the tree
//              re-flows constantly.
//   PROJECT    `SidebarProjection`, `SidebarSort`, `SidebarSearch`, `SidebarBinderPipeline` — the library AS edges.
//              In 0.2.9 this copied records out of a store; in 0.3 it is a read over `User.Me`'s edges, which is why
//              there is no hydration step and no second copy of a playlist's name.
//   SOURCES    `ISidebarDataSource` and the contribution contract the customizer generates property rows from.
//   EDIT       `SidebarEditPlan` — the customize canvas over the live pane.
//   DESIGN     `SidebarDesignInfo`, `SidebarPaneState`, `SidebarDesignGating`, `SidebarBuiltInDocuments` — slugs,
//              mount keys, width tiers, the chooser gate, and Classic's entire information architecture.
//   V3         `LibraryV3Document`/`View`/`ChipStrip`/`SearchRules`/`Metrics` — Library V3's synthesized document.
//   PINS       `SidebarPinId`, `PinSyncRules`, `PinRowRule` — the pin id IS the nav route key (A7).
//   CUSTOMIZER `SidebarPalette`, `SidebarDisplayValues`, `SidebarConfigJson`, `SidebarNumberEdit` — the pure tables
//              the customizer page renders from.
//   DIAG       `SidebarPaneInvariant` — the settled-frame terminal-state validator.
//
// NOT here: the document and the reducer (`Sidebar.Doc.cs`), the disk and the binder pump (`Sidebar.Host.cs`), and
// every `Element` (`Sidebar.UI.cs`, `Sidebar.Customizer.UI.cs` — stage 2).

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

// ── 0. the pane's own width ladder, and the CORE entry points ────────────────────────────────────────────────────────
//
// One clamp pair, one rail width, one tier ladder — owned HERE rather than by the shell, because every writer of the
// sidebar's width (the splitter seam, the responsive default, the pre-measure seed, `SidebarPaneState.Restore`, a
// diagnostics probe) must go through the same numbers or the pane gets persisted at a width it cannot render at. That
// drift is exactly what a second literal pair caused in 0.2.9. `Shell/Shell.cs` READS these; it does not redeclare
// them, and the values are 0.2.9's `ShellResponsiveLayout` nav-pane block unchanged.

/// <summary>The sidebar column's bounds and its default-width ladder. Issue #84 lowered the floor from 240 to 180 so
/// the pane can go genuinely narrow; 460 is the ceiling. 56 is the collapsed rail — a real surface, not a stub: both
/// layers stay mounted and cross-fade, so text never reflows through a 56-DIP layout.</summary>
public static class SidebarPaneBounds
{
    /// <summary>THE clamp pair. Every writer goes through it.</summary>
    public const float NavPaneMinW = 180f, NavPaneMaxW = 460f;

    /// <summary>The collapsed rail.</summary>
    public const float CompactRailW = 56f;

    /// <summary>Classic's ladder. Each design has its own triple — <see cref="SidebarDesignInfo.Tiers"/> is the owner
    /// of the per-design values; these three are the ones every no-triple overload forwards with.</summary>
    public const float NavPaneNarrowW = 240f, NavPaneMidW = 280f, NavPaneWideW = 320f;

    /// <summary>Viewport ≥ 1400 → the MID tier; ≥ 1800 → the WIDE tier. Identical for all three designs; only the
    /// three tier VALUES differ.</summary>
    public const float NavPaneMidEnterW = 1400f, NavPaneWideEnterW = 1800f;

    /// <summary>Widen immediately, shrink only 24 DIP past the threshold — so 1400 widens at once and the mid tier
    /// holds down to 1376. A ladder with no hysteresis oscillates on a window edge drag.</summary>
    public const float NavPaneHysteresisDip = 24f;

    /// <summary>Classic's tier triple.</summary>
    public static (float Narrow, float Mid, float Wide) ClassicTiers => (NavPaneNarrowW, NavPaneMidW, NavPaneWideW);

    /// <summary>The one clamp.</summary>
    public static float Clamp(float width)
        => width < NavPaneMinW ? NavPaneMinW : width > NavPaneMaxW ? NavPaneMaxW : width;

    /// <summary>The tier a viewport is nominally in, with no hysteresis.</summary>
    public static float NominalNavPaneDefaultFor(float viewportWidth, in (float Narrow, float Mid, float Wide) tiers)
        => viewportWidth >= NavPaneWideEnterW ? tiers.Wide
         : viewportWidth >= NavPaneMidEnterW ? tiers.Mid
         : tiers.Narrow;

    /// <inheritdoc cref="NominalNavPaneDefaultFor(float, in ValueTuple{float, float, float})"/>
    public static float NominalNavPaneDefaultFor(float viewportWidth)
        => NominalNavPaneDefaultFor(viewportWidth, ClassicTiers);

    /// <summary>Pre-measure seed. A zero/unknown viewport (the shell's constructor, before the first bounds callback)
    /// takes the narrow tier; the viewport effect commits the real tier before the first layout.</summary>
    public static float InitialNavPaneDefaultForViewport(float viewportWidth,
                                                         in (float Narrow, float Mid, float Wide) tiers)
        => viewportWidth <= 0f ? tiers.Narrow : NominalNavPaneDefaultFor(viewportWidth, tiers);

    /// <inheritdoc cref="InitialNavPaneDefaultForViewport(float, in ValueTuple{float, float, float})"/>
    public static float InitialNavPaneDefaultForViewport(float viewportWidth)
        => InitialNavPaneDefaultForViewport(viewportWidth, ClassicTiers);

    /// <summary>The default width for a viewport, WITH the shrink hysteresis. `initialized` is false only before the
    /// first real measure, where the nominal tier is taken outright.</summary>
    public static float NavPaneDefaultFor(float viewportWidth, float current, bool initialized,
                                          in (float Narrow, float Mid, float Wide) tiers)
    {
        if (viewportWidth <= 0f) return current;
        if (!initialized) return NominalNavPaneDefaultFor(viewportWidth, tiers);
        float nominal = NominalNavPaneDefaultFor(viewportWidth, tiers);
        if (nominal >= current) return nominal;                       // widen at once
        float dipped = NominalNavPaneDefaultFor(viewportWidth + NavPaneHysteresisDip, tiers);
        return dipped < current ? dipped : current;                    // shrink only past the dip
    }

    /// <inheritdoc cref="NavPaneDefaultFor(float, float, bool, in ValueTuple{float, float, float})"/>
    public static float NavPaneDefaultFor(float viewportWidth, float current, bool initialized)
        => NavPaneDefaultFor(viewportWidth, current, initialized, ClassicTiers);
}

public static partial class Sidebar
{
    // ── the planner, named ───────────────────────────────────────────────────────────────────────────────────────────
    //
    // Three thin names over `SidebarRowPlanner` so a caller never has to pick an overload. The buffers are the
    // CALLER's and they ALIAS: the plan points into them, so a caller keeps two and alternates — otherwise the
    // outgoing rows die under the diff that is still reading them.

    /// <summary>(document × projection) → ONE flat row array, for the expanded pane.</summary>
    public static SidebarRowPlan Plan(SidebarCustomLayout layout, in SidebarProjectionInput input,
                                      SidebarPlanBuffers buffers)
        => SidebarRowPlanner.Build(layout, in input, buffers);

    /// <summary>The 56-DIP rail's own plan — the same document, budgeted down to tiles.</summary>
    public static SidebarRowPlan PlanRail(SidebarCustomLayout layout, in SidebarProjectionInput input,
                                          SidebarPlanBuffers buffers)
        => SidebarRowPlanner.BuildRail(layout, in input, buffers);

    /// <summary>The customize canvas: one uniform card per section, over the LIVE pane. There is no preview of a
    /// sidebar; there is the sidebar.</summary>
    public static SidebarRowPlan PlanEdit(SidebarCustomLayout layout, in SidebarProjectionInput input,
                                          in SidebarEditState edit, SidebarPlanBuffers buffers)
        => SidebarRowPlanner.BuildEdit(layout, in input, in edit, buffers);

    /// <summary>The mount key a design switch remounts under — fresh hooks, fresh section and scroll state. A design
    /// switch is a genuine remount, never a re-render with a different flag.</summary>
    public static string MountKey(SidebarDesign design) => SidebarDesignInfo.MountKey(design);
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

/// <summary>THE ONE ROW-GEOMETRY LADDER: row height/indent/tree-content geometry and the pure plan-geometry helpers
/// (content-Y, route index, direction, folder/section ranges, pin glyph, grid fallback columns). Engine-free
/// (System + the row/plan types only) so <c>Wavee.Tests</c> can pin the ladder directly.</summary>
public static class SidebarRowGeometry
{
    /// <summary>The Classic entity-row height (44) — the number every landed sidebar row already uses.</summary>
    public const float ClassicHeight = 44f;

    // ── THE ONE CONTENT LANE ──
    // Rows and the fixed chrome bands mounted above the list used to compute their own left inset (14 vs 8+6) and
    // disagreed by 6 DIP. ContentLane/ContentLaneEnd name the lane once so nothing invents a third number.

    /// <summary>The pane's horizontal edge inset (8), applied ONCE around the virtualized list; a band mounted above
    /// that list must reproduce it via <see cref="ContentLane"/> rather than padding to 8 on its own.</summary>
    public const float PaneEdge = 8f;

    /// <summary>A row's own leading padding at depth 0 (4) — the base term of <see cref="IndentFor"/>.</summary>
    public const float RowInsetLeft = 4f;

    /// <summary>A row's own trailing padding (8).</summary>
    public const float RowInsetRight = 8f;

    /// <summary>THE CONTENT LANE (12): the x at which pane content begins. Rows reach it as
    /// <see cref="PaneEdge"/> + <see cref="IndentFor"/>(0); a fixed band above the list pads to it directly.</summary>
    public const float ContentLane = PaneEdge + RowInsetLeft;

    /// <summary>The lane's trailing twin (16) — 4 DIP wider than <see cref="ContentLane"/> because the landed row
    /// padding is asymmetric (4 leading / 8 trailing); carried forward as-is.</summary>
    public const float ContentLaneEnd = PaneEdge + RowInsetRight;

    /// <summary>Row height by density: Compact 32 (no room for a subtitle) / Cozy 40 / Cozy+subtitle 44 (Classic's
    /// entity row) / Comfortable 44 or 48 with a subtitle. A glyph/shortcut row never paints a subtitle and lands on
    /// the 40 arm.</summary>
    public static float HeightFor(SidebarDensity density, bool hasSubtitle) => density switch
    {
        SidebarDensity.Compact => 32f,
        SidebarDensity.Comfortable => hasSubtitle ? 48f : 44f,
        _ => hasSubtitle ? 44f : 40f,
    };

    /// <summary>A section's uniform row height straight from its persisted display options.</summary>
    public static float HeightFor(SidebarDisplayOptions? opts)
    {
        var o = opts ?? SidebarDisplayOptions.Default;
        return HeightFor(o.Density, o.Subtitles);
    }

    /// <summary>One nesting level of indent (12). Named because the drop resolver reads the ladder BACKWARDS — it
    /// turns a pointer x into a depth.</summary>
    public const float IndentStep = 12f;

    /// <summary>The deepest level the indent ladder honours; beyond it rows stop marching right.</summary>
    public const int MaxIndentDepth = 4;

    // ── THE ONE TREE-CONTENT ORIGIN ──
    // A tree row is not laid out on IndentFor(depth): it pads once at IndentFor(0), then spends the selection
    // gutter and one connector cell per level. There is NO reserved disclosure cell — the folder's chevron lives in
    // the row's TRAILING cluster, so TreeLeading == StandardLeading at depth 0 regardless of whether a section
    // contains a folder.

    /// <summary>The 3-DIP selection-accent reserve every row leads with.</summary>
    public const float SelGutterWidth = 3f;

    // ── THE ONE LEADING LANE ──
    // Art rows, glyph rows and tree rows used to compute three different distances from the row padding to the
    // leading visual (27 / 29 / 33). LeadingGap/LeadingLaneWidth name the lane once for every row shape.

    /// <summary>The gap between the selection gutter and the leading visual (6) — the same for art, glyph and tree
    /// rows.</summary>
    public const float LeadingGap = 6f;

    /// <summary>The span from a row's <see cref="IndentFor"/> padding to its leading visual (9 = gutter + gap).</summary>
    public const float LeadingLaneWidth = SelGutterWidth + LeadingGap;

    /// <summary>The pane-relative x of a row's art/glyph column at <paramref name="depth"/> (21 at depth 0).</summary>
    public static float ArtX(int depth) => PaneEdge + IndentFor(depth) + LeadingLaneWidth;

    /// <summary>Art/glyph size by density (20 / 32 / 40).</summary>
    public static float ArtFor(SidebarDensity density) => density switch
    {
        SidebarDensity.Compact => 20f,
        SidebarDensity.Comfortable => 40f,
        _ => 32f,
    };

    /// <summary>One tree connector cell (12 — the engine's <c>Spacing.M</c>). Equal to <see cref="IndentStep"/> by
    /// design: a tree level and an indent level are the same step, drawn two different ways.</summary>
    public const float TreeGuideStep = IndentStep;

    /// <summary>The x at which a tree row's CONTENT (its art, and the caret that means "insert at this depth")
    /// begins: <c>IndentFor(0) + LeadingLaneWidth + depth·TreeGuideStep</c> — 19, 31, 43, … Kept as the ORIGINAL
    /// spelling (<c>IndentFor(0) + …</c>, not <c>IndentFor(depth)</c>): the two are equal only while
    /// <see cref="IndentStep"/> == <see cref="TreeGuideStep"/>, and the caret must not break silently if they ever
    /// diverge.</summary>
    public static float TreeContentX(int depth)
    {
        int d = depth < 0 ? 0 : depth > MaxIndentDepth ? MaxIndentDepth : depth;
        return IndentFor(0) + LeadingLaneWidth + d * TreeGuideStep;
    }

    /// <summary>Left padding for a nesting depth: <see cref="RowInsetLeft"/> base + <see cref="IndentStep"/> per
    /// level, clamped at <see cref="MaxIndentDepth"/> levels.</summary>
    public static float IndentFor(int depth)
        => RowInsetLeft + (depth < 0 ? 0 : depth > MaxIndentDepth ? MaxIndentDepth : depth) * IndentStep;

    /// <summary>The section header band's own height (28).</summary>
    public const float HeaderHeight = 28f;

    /// <summary>R3.1.3 — the vertical air above a section header that is not the pane's first row, and the gap
    /// between a header and its first body row.</summary>
    public const float SectionGap = 8f;
    /// <inheritdoc cref="SectionGap"/>
    public const float HeaderBodyGap = 2f;

    /// <summary>An explicit <c>Divider</c> section's band height (16) — the hairline centred 8 DIP below the
    /// previous row.</summary>
    public const float DividerHeight = 16f;

    /// <summary>The quiet empty hint's band height (32).</summary>
    public const float EmptyHintHeight = 32f;

    /// <summary>The Pinned section's empty state IS its drop zone, resting at 56 (it grows to 72 only while a
    /// compatible drag is live — a transient the measured layout corrects on its own).</summary>
    public const float PinDropZoneRestHeight = 56f;

    /// <summary>The inline filter-chip strip an editable <c>EntityList</c> header carries: a 26-DIP pill row + its
    /// 2-DIP bottom padding, joined to the header by a 4-DIP gap. It WRAPS at a narrow pane, so this is the one term
    /// of the ladder that is an honest approximation rather than an identity — the measured seam corrects it on
    /// realize.</summary>
    public const float ChipHeight = 26f;
    /// <inheritdoc cref="ChipHeight"/>
    public const float ChipStripHeight = ChipHeight + 2f;
    /// <inheritdoc cref="ChipStripHeight"/>
    public const float ChipStripGap = 4f;

    /// <summary>The EntityEmbed hero card's height ladder (Compact 56 / Cozy 72 / Comfortable 88).</summary>
    public static float CardHeightFor(SidebarDensity density) => density switch
    {
        SidebarDensity.Compact => 56f,
        SidebarDensity.Comfortable => 88f,
        _ => 72f,
    };

    /// <summary>The actionable degraded state's band (48, or 56 when it carries a reason line).</summary>
    public static float PromptHeight(bool hasReason) => hasReason ? 56f : 48f;

    /// <summary>The <c>TreeEnd</c> chrome row's extent (24) — small enough to read as the tree's closing gutter
    /// rather than as an item.</summary>
    public const float TreeEndHeight = 24f;

    /// <summary>Subtitles are never rendered at Compact density.</summary>
    public static bool SubtitleVisible(SidebarDensity density, string? subtitle)
        => density != SidebarDensity.Compact && subtitle is { Length: > 0 };

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
    /// <summary>R3.1.3 SECTION RHYTHM — the air a header band carries ABOVE it: 8 DIP, suppressed for the pane's
    /// first row (nothing to separate from) and directly after a <c>Divider</c> or bare <c>HeaderLabel</c>, both of
    /// which already supply the gap. The one term of the ladder that depends on the PREVIOUS row.</summary>
    public static float BandTop(IReadOnlyList<SidebarRow> rows, int index)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (index <= 0 || index >= rows.Count) return 0f;
        var prev = rows[index - 1].Kind;
        return prev is SidebarRowKind.Divider or SidebarRowKind.HeaderLabel ? 0f : SidebarRowGeometry.SectionGap;
    }

    /// <summary>The analytic extent of plan row <paramref name="index"/>. <paramref name="section"/> is the row's
    /// section (null ⇒ the slot renders nothing, so the row is 0 tall); <paramref name="editable"/> is the pane's
    /// <c>!Config.ReadOnly</c>, which decides whether an <c>EntityList</c> header carries the inline chip strip.
    /// Returns <see cref="float.NaN"/> for a <c>GridStrip</c> — "use the estimate and correct on measure".</summary>
    public static float HeightOf(IReadOnlyList<SidebarRow> rows, int index, SidebarSectionSpec? section, bool editable)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if ((uint)index >= (uint)rows.Count) return 0f;
        var row = rows[index];
        if (section is null) return 0f;                     // the slot renders Blank (height 0)

        switch (row.Kind)
        {
            case SidebarRowKind.SectionHeader:
                return BandTop(rows, index) + SidebarRowGeometry.HeaderHeight
                     + (CarriesChipStrip(section, editable)
                            ? SidebarRowGeometry.ChipStripGap + SidebarRowGeometry.ChipStripHeight : 0f)
                     + SidebarRowGeometry.HeaderBodyGap;

            case SidebarRowKind.HeaderLabel:
                return BandTop(rows, index) + SidebarRowGeometry.HeaderHeight + SidebarRowGeometry.HeaderBodyGap;

            case SidebarRowKind.Divider:
                return SidebarRowGeometry.DividerHeight;

            // Every item-shaped kind is the section's ONE uniform row height (iron rule 4: one height per SECTION).
            case SidebarRowKind.IconRow:
            case SidebarRowKind.EntityRow:
            case SidebarRowKind.Placeholder:
            case SidebarRowKind.FolderHeader:
            case SidebarRowKind.Skeleton:
                return SidebarRowGeometry.HeightFor(section.Opts);

            case SidebarRowKind.Empty:
                return EmptyHeight(section);

            case SidebarRowKind.TreeEnd:
                return SidebarRowGeometry.TreeEndHeight;

            case SidebarRowKind.EntityCard:
                return SidebarRowGeometry.CardHeightFor(section.Opts.Density);

            case SidebarRowKind.PromptRow:
                // A Concerts prompt never carries a reason line; the reason string is a render-time resolution, so
                // the ladder takes the taller shape only for a kind that can have one.
                return SidebarRowGeometry.PromptHeight(section.Kind != SidebarSectionKind.Concerts);

            case SidebarRowKind.SectionCard:
                return SidebarRowGeometry.ClassicHeight;    // the ONE edit-card height

            case SidebarRowKind.GridStrip:
            default:
                return float.NaN;                           // not analytic — estimate, then correct on measure
        }
    }

    /// <summary>An <c>EntityList</c> header carries the inline filter chips only when the pane is editable, the
    /// section asked for them, and the section is open.</summary>
    static bool CarriesChipStrip(SidebarSectionSpec section, bool editable)
        => editable && section.Kind == SidebarSectionKind.EntityList
           && section.Opts.InlineControls && !section.Collapsed;

    /// <summary>A section that resolved to zero rows: Pinned's empty state IS its (unconditional) drop zone, a
    /// <c>HideBody</c> section draws nothing at all, an <c>ActionCard</c> borrows the section's row height, and the
    /// default is the quiet 32-DIP hint.</summary>
    static float EmptyHeight(SidebarSectionSpec section)
    {
        if (section.Kind == SidebarSectionKind.Pinned) return SidebarRowGeometry.PinDropZoneRestHeight;
        var behavior = SidebarSectionKinds.EmptyBehaviorFor(section.Kind, section.Opts.EmptyBehavior);
        return behavior switch
        {
            SidebarEmptyBehavior.HideBody => 0f,
            SidebarEmptyBehavior.ActionCard => SidebarRowGeometry.HeightFor(section.Opts),
            _ => SidebarRowGeometry.EmptyHintHeight,
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
    /// <summary>The hand-placed item a plan row was projected from (a hand-placed row carries <c>Key == item.Key</c>,
    /// unique within its section). Also finds a Pinned OVERRIDE row's side-table entry, which is what makes an
    /// alias/icon override apply to a pinned row.</summary>
    public static SidebarItemSpec? ItemOf(SidebarSectionSpec section, string key)
    {
        var items = section.ItemList;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Hidden) continue;
            if (string.Equals(item.Key, key, StringComparison.Ordinal)) return item;
            if (string.Equals(item.Id, key, StringComparison.Ordinal)) return item;
        }
        return null;
    }

    /// <summary>The ONE selection rule for a PROJECTED entity (an entity row, a grid cell, a card): it draws
    /// selected when its nav route IS the live route. A folder and a track have no route, so neither can ever be
    /// the selected row.</summary>
    public static bool EntrySelects(in SidebarLibraryEntry entry, string route)
        => route.Length > 0
           && entry.RouteKey is { Length: > 0 } r
           && string.Equals(r, route, StringComparison.Ordinal);

    /// <summary>Does the plan row draw itself SELECTED for <paramref name="route"/>? Resolved EXACTLY as the slot
    /// draws it, kind by kind:
    /// <list type="bullet">
    /// <item><c>IconRow</c>/<c>EntityRow</c>/<c>Placeholder</c> — an ACTION item never selects; then the projected
    /// entry; then a hand-placed TRACK never selects and a hand-placed ROUTE selects on its own key; a missing-entity
    /// retention row never selects.</item>
    /// <item><c>EntityCard</c> — the resolved entry's route, or the pin route derived from its uri when unresolved.</item>
    /// <item><c>GridStrip</c> — one route per CELL, so the ROW is "selected" when ANY cell in its range is (the unit
    /// the pane's per-row epoch can address).</item>
    /// <item>everything else (headers, dividers, folders, empties, skeletons, create rows, prompts) — never.</item>
    /// </list></summary>
    public static bool SelectsRoute(in SidebarRow row, IReadOnlyList<SidebarLibraryEntry> entries,
                                    SidebarSectionSpec? section, string route)
    {
        if (route.Length == 0 || entries is null) return false;
        bool resolved = row.EntryIndex >= 0 && row.EntryIndex < entries.Count;
        switch (row.Kind)
        {
            case SidebarRowKind.IconRow:
            case SidebarRowKind.EntityRow:
            case SidebarRowKind.Placeholder:
            {
                var item = section is null ? null : ItemOf(section, row.Key);
                if (item is { Target: SidebarItemTarget.Action }) return false;
                if (resolved) return EntrySelects(entries[row.EntryIndex], route);
                if (item is { Target: SidebarItemTarget.Track }) return false;
                return item is { Target: SidebarItemTarget.Route }
                       && string.Equals(item.Key, route, StringComparison.Ordinal);
            }

            case SidebarRowKind.EntityCard:
            {
                string uri = resolved ? entries[row.EntryIndex].Uri : "";
                string? key = resolved ? entries[row.EntryIndex].RouteKey : SidebarPinId.FromUri(uri);
                return key is { Length: > 0 } && string.Equals(key, route, StringComparison.Ordinal);
            }

            case SidebarRowKind.GridStrip:
            {
                int start = row.EntryIndex;
                int count = row.ItemCount;
                if (start < 0 || count <= 0 || start >= entries.Count) return false;
                if (start + count > entries.Count) count = entries.Count - start;
                for (int i = 0; i < count; i++)
                    if (EntrySelects(entries[start + i], route)) return true;
                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>Every plan-row index that draws selected for <paramref name="route"/>, in ASCENDING order (the pane
    /// diffs two of these with a linear merge, so the order is part of the contract). <paramref name="into"/> is
    /// caller-owned and is NOT cleared — a warm sweep therefore allocates nothing.</summary>
    public static void Sweep(IReadOnlyList<SidebarRow> rows, IReadOnlyList<SidebarLibraryEntry> entries,
                             Func<string, SidebarSectionSpec?> sectionOf, string route, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (rows is null || entries is null || route.Length == 0) return;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var section = sectionOf?.Invoke(row.SectionId);
            if (SelectsRoute(in row, entries, section, route)) into.Add(i);
        }
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

/// <summary>Which navbar-customization verbs a sidebar row's context menu offers — the queue-row extras, for the
/// left pane. Engine-free so <c>Wavee.Tests</c> drives the real rule.
///
/// Drag is one of several ways to reorder, never the only one (P6). Explicit Move up / Move down stay available
/// when the in-place reorder band is disarmed (an expanded folder in Pinned, a single remaining item). Remove is the
/// authored-list verb (a StaticLinks / CustomGroup / Shortcuts item the user placed); a Pinned row's remove is
/// Unpin, which already lives in the pin-state slot of the entity menu and is therefore not duplicated here.</summary>
public readonly record struct SidebarNavLayout(bool MoveUp, bool MoveDown, bool Remove)
{
    public bool IsEmpty => !MoveUp && !MoveDown && !Remove;

    /// <summary><paramref name="orderIndex"/> is this row's slot in the list that actually moves (a reorder band,
    /// the pin store when that band is disarmed, or -1 when the row has no order of its own — a projected library
    /// leaf). <paramref name="removable"/> is true only for a hand-placed item the document will actually
    /// drop.</summary>
    public static SidebarNavLayout Decide(int orderIndex, int orderCount, bool removable)
    {
        bool ordered = orderIndex >= 0 && orderCount > 1;
        return new(
            MoveUp: ordered && orderIndex > 0,
            MoveDown: ordered && orderIndex < orderCount - 1,
            Remove: removable);
    }
}

/// <summary>A settled docked-pane observation. Deliberately separate from the persisted preference triple — these
/// are rendered TERMINAL-STATE facts captured by the shell after a transition settles.</summary>
public readonly record struct SidebarPaneFrameSnapshot(
    SidebarDesign Design,
    bool UserCollapsed,
    bool PresentedCompact,
    float PreferredExpandedWidth,
    float RenderedPaneWidth,
    float ExpandedOpacity,
    float RailOpacity,
    bool ExpandedHitTestVisible,
    bool RailHitTestVisible);

/// <summary>All terminal-state violations detected in one observation. Flags make one diagnostic edge sufficient
/// even when one bad state breaks width, opacity, and hit testing at the same time.</summary>
[Flags]
public enum SidebarPaneInvariantFault : ushort
{
    None = 0,
    NonFiniteValue = 1 << 0,
    PreferredWidthOutOfRange = 1 << 1,
    CompactWidthMismatch = 1 << 2,
    ExpandedWidthOutOfRange = 1 << 3,
    ExpandedWidthMismatch = 1 << 4,
    LayerOpacityMismatch = 1 << 5,
    HitTestOwnerMismatch = 1 << 6,
}

/// <summary>Pure terminal-state validator behind the screenshot/layout probe and the runtime edge diagnostic. It does
/// not attempt to validate an in-flight animation: callers invoke it only after the pane transition settles.</summary>
public static class SidebarPaneInvariant
{
    public const float Tolerance = 0.5f;

    public static SidebarPaneInvariantFault Inspect(in SidebarPaneFrameSnapshot state)
    {
        if (!float.IsFinite(state.PreferredExpandedWidth)
            || !float.IsFinite(state.RenderedPaneWidth)
            || !float.IsFinite(state.ExpandedOpacity)
            || !float.IsFinite(state.RailOpacity))
            return SidebarPaneInvariantFault.NonFiniteValue;

        SidebarPaneInvariantFault fault = SidebarPaneInvariantFault.None;
        if (!InExpandedRange(state.PreferredExpandedWidth))
            fault |= SidebarPaneInvariantFault.PreferredWidthOutOfRange;

        if (state.PresentedCompact)
        {
            if (!Near(state.RenderedPaneWidth, SidebarPaneBounds.CompactRailW))
                fault |= SidebarPaneInvariantFault.CompactWidthMismatch;
            if (!Near(state.ExpandedOpacity, 0f) || !Near(state.RailOpacity, 1f))
                fault |= SidebarPaneInvariantFault.LayerOpacityMismatch;
            if (state.ExpandedHitTestVisible || !state.RailHitTestVisible)
                fault |= SidebarPaneInvariantFault.HitTestOwnerMismatch;
        }
        else
        {
            if (!InExpandedRange(state.RenderedPaneWidth))
                fault |= SidebarPaneInvariantFault.ExpandedWidthOutOfRange;
            if (!Near(state.RenderedPaneWidth, state.PreferredExpandedWidth))
                fault |= SidebarPaneInvariantFault.ExpandedWidthMismatch;
            if (!Near(state.ExpandedOpacity, 1f) || !Near(state.RailOpacity, 0f))
                fault |= SidebarPaneInvariantFault.LayerOpacityMismatch;
            if (!state.ExpandedHitTestVisible || state.RailHitTestVisible)
                fault |= SidebarPaneInvariantFault.HitTestOwnerMismatch;
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
        width >= SidebarPaneBounds.NavPaneMinW - Tolerance
        && width <= SidebarPaneBounds.NavPaneMaxW + Tolerance;

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

/// <summary>Playlist provenance for the V3 qualifier chips. <see cref="None"/> = unknown — the chips stay hidden
/// unless at least two distinct non-None flavors are present.</summary>
public enum SidebarPlaylistFlavor : byte { None = 0, ByYou = 1, BySpotify = 2, Mixed = 3 }

/// <summary>Which kinds a projection pass should emit. A mask (not a single kind) because every consumer asks for a
/// SET: the V3 "All" filter wants everything, a Curated EntityList section wants its query's kinds, the Podcasts chip
/// wants shows only.</summary>
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

    /// <summary>The V3 chip row -> kinds. Playlists includes folders (a folder IS part of the playlist tree); every
    /// other chip is a single kind.</summary>
    public static SidebarEntryKindMask From(SidebarV3Filter filter) => filter switch
    {
        SidebarV3Filter.Playlists => SidebarEntryKindMask.PlaylistTree,
        SidebarV3Filter.Podcasts => SidebarEntryKindMask.Show,
        SidebarV3Filter.Albums => SidebarEntryKindMask.Album,
        SidebarV3Filter.Artists => SidebarEntryKindMask.Artist,
        _ => SidebarEntryKindMask.All,
    };

    /// <summary>A Curated <c>SidebarEntityQuery.Kinds</c> -> projection kinds. The Core mask has no folder bit, so a
    /// query that asks for playlists gets the tree (folders included) — that is what the section renders.</summary>
    public static SidebarEntryKindMask From(SidebarEntityKinds kinds)
    {
        var m = SidebarEntryKindMask.None;
        if ((kinds & SidebarEntityKinds.Playlists) != 0) m |= SidebarEntryKindMask.PlaylistTree;
        if ((kinds & SidebarEntityKinds.Albums) != 0) m |= SidebarEntryKindMask.Album;
        if ((kinds & SidebarEntityKinds.Artists) != 0) m |= SidebarEntryKindMask.Artist;
        if ((kinds & SidebarEntityKinds.Shows) != 0) m |= SidebarEntryKindMask.Show;
        return m;
    }
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

    /// <summary>Qualifier-chip match. A qualifier of 0 (Any/Unknown) matches everything; the non-zero values of the V3
    /// and Core qualifier vocabularies are byte-identical to <see cref="SidebarPlaylistFlavor"/>, so one byte
    /// comparison serves both.</summary>
    public bool MatchesQualifier(byte qualifier) => qualifier == 0 || (byte)Flavor == qualifier;

    public bool MatchesQualifier(SidebarV3Qualifier qualifier) => MatchesQualifier((byte)qualifier);

    /// <summary>An APP-ROUTE row (Home / Search / Liked / ...). Authored by the surface, never produced by the
    /// projection: the label + glyph are engine-bound and resolved by the renderer. The route key IS the id.</summary>
    public static SidebarLibraryEntry ForRoute(string routeKey, string name, int sourceOrder = 0, long lastVisitedTicksUtc = 0) =>
        new(routeKey, SidebarEntryKind.AppRoute, "", name, "", default, null,
            ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: lastVisitedTicksUtc,
            SourceOrder: sourceOrder, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = "", FolderName = "", FirstArtistName = "" };
}

// ── the plan itself: SidebarCustomLayout + the live projection, planned into ONE flat row list ─────────────────────

/// <summary>The row vocabulary the Curated renderer switches on. Also the ItemsView's <c>ContentType</c>, so each kind
/// gets its own recycling pool.</summary>
public enum SidebarRowKind : byte
{
    SectionHeader = 0,   // clickable, toggles Collapsed
    HeaderLabel   = 1,   // Kind == Header (no chevron)
    Divider       = 2,
    IconRow       = 3,   // glyph + label (+ optional count badge)
    EntityRow     = 4,   // artwork + label (+ optional subtitle)
    FolderHeader  = 5,   // a PlaylistTree folder; indent-aware, clickable
    GridStrip     = 6,   // one row of a grid section: [EntryIndex, ItemCount] into the plan's entries
    Placeholder   = 7,   // a missing entity (fallback title/art, dimmed)
    Empty         = 8,   // a section resolved to zero rows
    Skeleton      = 9,   // the section's source is still pending
    // 10 was CreateAction (a PlaylistTree section's trailing "+" row), DELETED — the affordance is the section
    // header's "+" now. Left unused rather than reclaimed: renumbering would re-pool every existing row (ContentType).
    EntityCard    = 11,  // the EntityEmbed hero card: taller row, cover-left, play affordance
    PromptRow     = 12,  // an actionable degraded state (e.g. Concerts' "Set your location" row)
    // EDIT MODE ONLY — one uniform-height card standing in for a whole section on the customize canvas: grip, glyph,
    // title, count, eye, "...". ItemCount carries the card's honest count (-1 = none); EntryIndex stays -1.
    SectionCard   = 13,
    // The PlaylistTree's closing gutter (24 DIP), planned right after the last tree row. Exists because "top level,
    // at the end" had no drop target: the old create row squatted that slot and accepted rootlist payloads, so
    // dragging a playlist below everything duplicated it instead of moving it. APPENDED, never inserted.
    TreeEnd       = 14,
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

/// <summary>One <c>SidebarSectionKind.Extension</c> section's resolved rows: a WINDOW into
/// <see cref="SidebarProjectionInput.ExtensionEntries"/> plus the health/availability the binder observed.
///
/// <para>This is the whole planner-side extension contract, and it keeps the planner PURE: the binder resolves the
/// contribution id through the registry, fills the shared entry pool and records this struct; the planner only reads
/// it and never switches on an extension id (the forward-compat guardrail).</para></summary>
/// <param name="NeedsPrompt">The source's degraded state is ACTIONABLE (Concerts with no location) — the section
/// plans one <c>PromptRow</c> instead of an empty caption, even though the source itself is Ready.</param>
public readonly record struct SidebarSectionSlice(
    int Start,
    int Count,
    SidebarSourceState State = SidebarSourceState.Ready,
    SidebarContributionAvailability Availability = SidebarContributionAvailability.Live,
    bool NeedsPrompt = false);

/// <summary>sectionId -> its resolved extension slice. An interface (not a dictionary) so the binder's reusable table
/// can back it without materialising anything per rebuild.</summary>
public interface ISidebarSectionSlices
{
    bool TryGet(string sectionId, out SidebarSectionSlice slice);
}

/// <summary>Everything outside the document the plan depends on. Every slice is nullable so a headless test, the fake
/// backend, or a live-only adapter that is not registered can simply omit it.</summary>
public readonly record struct SidebarProjectionInput(
    // The unified library projection (playlists/albums/artists/shows), in source order — EntityList's input.
    IReadOnlyList<SidebarLibraryEntry>? Library = null,
    // The rootlist tree, DEPTH-FIRST FLATTENED with Depth stamped and folders carried as SidebarEntryKind.Folder
    // entries. Flattened rather than a tree so planning a 10k rootlist is one linear pass with no recursion and no
    // per-node allocation.
    IReadOnlyList<SidebarLibraryEntry>? PlaylistTree = null,
    // The shared pin store, resolved, in pin order.
    IReadOnlyList<SidebarLibraryEntry>? Pins = null,
    // Navigation recency, newest first, deduped by uri.
    IReadOnlyList<SidebarLibraryEntry>? Visited = null,
    // Playback recency, context-first, newest first, deduped.
    IReadOnlyList<SidebarLibraryEntry>? Played = null,
    // New releases from followed artists, newest first.
    IReadOnlyList<SidebarLibraryEntry>? NewReleases = null,
    // Upcoming concerts, soonest first. Name = event title, Creator = venue, SortStamp = the event's epoch-ms.
    IReadOnlyList<SidebarLibraryEntry>? Concerts = null,
    // Resolves a hand-placed item's Key (a spotify uri) to its projected entry. A miss is the missing-entity path,
    // never a dropped row.
    IReadOnlyDictionary<string, SidebarLibraryEntry>? ByUri = null,
    // The pinned entry ids — pins sort first inside every EntityList sort mode.
    IReadOnlySet<string>? PinnedIds = null,
    // Expanded rootlist folder ids. null means "everything expanded" (the headless default).
    IReadOnlySet<string>? ExpandedFolders = null,
    // The library-only search text. Filters EntityList and PlaylistTree; never shortcuts or links.
    string? Search = null,
    SidebarSourceState LibraryState = SidebarSourceState.Ready,
    SidebarSourceState TreeState = SidebarSourceState.Ready,
    SidebarSourceState RecentsState = SidebarSourceState.Ready,
    SidebarSourceState NewReleasesState = SidebarSourceState.Ready,
    SidebarSourceState ConcertsState = SidebarSourceState.Ready,
    // True when the user has no location yet — Concerts then plans one actionable PromptRow.
    bool ConcertsLocationUnset = false,
    // The caller's composite revision (document + projection + pins + search + culture epoch). Echoed into the plan
    // so Build stays deterministic — the planner never carries hidden counter state.
    int Revision = 0,
    // ── extension contributions ────────────────────────────────────────────────────────────────────────────────────
    // ONE shared pool holding every Extension section's rows back to back, and the sectionId -> window table over it.
    IReadOnlyList<SidebarLibraryEntry>? ExtensionEntries = null,
    ISidebarSectionSlices? ExtensionSlices = null,
    // ── rail options ────────────────────────────────────────────────────────────────────────────────────────────────
    // How many PlaylistTree tiles the 56-DIP RAIL may draw. 0 = unbounded. The rail is still bounded by RailTileCap,
    // but a tree is the only UNBOUNDED source in the rail: a 200-playlist rootlist would consume the global 40-tile
    // budget and silently push every LATER section's tiles out of the rail entirely. A per-tree ceiling keeps document
    // order from becoming a race for tiles.
    int RailTreeCap = 0);

/// <summary>Caller-owned row/entry storage. Hand the SAME instance to every <c>Build</c> for a given pane and a warm
/// re-plan reuses its capacity (the 10k-library alloc bound). The returned plan's lists ALIAS these buffers, so a plan
/// is only valid until the next Build on the same buffers — exactly the UseMemo lifetime it is built for.</summary>
public sealed class SidebarPlanBuffers
{
    internal readonly List<SidebarRow> Rows = new(256);
    internal readonly List<SidebarLibraryEntry> Entries = new(256);
    internal readonly List<int> TreeParents = new(256);
    internal readonly List<byte> TreeVisible = new(256);
    internal readonly List<int> TreeLeaves = new(256);
    internal readonly List<int> TreeCursors = new(256);
    internal readonly List<int> TreeAncestors = new(16);
}

public static class SidebarRowPlanner
{
    /// <summary>Rail tiles are capped (the rail stays scrollable) — beyond this a rail is noise.</summary>
    public const int RailTileCap = 40;

    /// <summary>The guard on a HAND-AUTHORED item list (StaticLinks / CustomGroup / Pinned overrides). Unreachable in
    /// practice: the layout reducer already caps those at 500 items per section.</summary>
    public const int SectionRowCap = 5000;

    /// <summary>The guard on a PROJECTED section (EntityList / PlaylistTree / Pinned / JumpBackIn / feeds).
    /// <para>DEVIATION, deliberate: one spec clause gives a single <c>SectionRowCap = 5000</c> "a section never plans
    /// more than this many rows", while another requires a 10 000-entry EntityList to plan IN FULL — the app is sized
    /// for 10k+ libraries. Truncating a real library at 5 000 rows would silently hide half of it — a correctness bug,
    /// not a guard. So <see cref="SectionRowCap"/> keeps its job for authored lists, and projected sections get this
    /// (still finite) ceiling instead.</para></summary>
    public const int DynamicSectionRowCap = 20_000;

    public const int RailPinnedCap = 8;
    public const int RailJumpBackInCap = 4;
    public const int RailEntityListCap = 20;

    const int SkeletonRows = 3;

    /// <summary>Deterministic: same inputs -> identical plan. Called from a UseMemo keyed on a DepKey of
    /// (documentRevision, projectionRevision, pinRevision, searchText, cultureEpoch).</summary>
    public static SidebarRowPlan Build(SidebarCustomLayout layout, in SidebarProjectionInput input,
        SidebarPlanBuffers? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var st = Begin(buffers);
        st.ExcludePinned = HasPinnedSection(layout);

        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++) PlanSection(sections[i], 0, in input, ref st);

        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    /// <summary>The 56-DIP rail plan: tiles only, from sections with <c>ShowInRail</c>.</summary>
    public static SidebarRowPlan BuildRail(SidebarCustomLayout layout, in SidebarProjectionInput input,
        SidebarPlanBuffers? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var st = Begin(buffers);
        st.ExcludePinned = HasPinnedSection(layout);
        int tiles = 0;

        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Hidden || !SidebarSectionKinds.IsKnown(s.Kind)) continue;
            // ShowInRail is the ONE option Header/Divider honour, so it gates them too.
            if (!s.Opts.ShowInRail) continue;

            // A rail has no headings: a Header collapses into the same compact divider a Divider draws.
            if (s.Kind is SidebarSectionKind.Divider or SidebarSectionKind.Header)
            {
                st.DividerPending = true;
                st.DividerSectionId = s.Id;
                st.DividerDepth = 0;
                continue;
            }
            RailSection(s, in input, ref st, ref tiles);
        }

        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    /// <summary>PHASE 2 — the edit projection of the same document. A SEPARATE entry point (not a flag on
    /// <see cref="Build"/>) so the normal path stays byte-identical: every top-level section becomes ONE
    /// <see cref="SidebarRowKind.SectionCard"/> row, and the expanded section(s) plan their ordinary body right
    /// underneath via the same per-kind planners the live pane uses.
    /// <para>Three differences from <see cref="Build"/>: a hidden section still gets a (dimmed) card but never a
    /// body; no <c>SectionHeader</c> row is emitted (the card IS the header); an unknown section kind plans no card.
    /// A revealed body also ignores the section's persisted <c>Collapsed</c> bit — expanding a card is the EDITOR's
    /// reveal and leaves the document's own collapse state untouched.</para></summary>
    public static SidebarRowPlan BuildEdit(SidebarCustomLayout layout, in SidebarProjectionInput input,
        in SidebarEditState edit, SidebarPlanBuffers? buffers = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var st = Begin(buffers);

        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (!SidebarSectionKinds.IsKnown(s.Kind)) continue;
            // The card is a chrome row like any other: Key == section.Id, EntryIndex == -1, no string allocated.
            Add(ref st, new SidebarRow(SidebarRowKind.SectionCard, s.Id, 0, -1,
                                       SidebarEditPlan.CardCount(s), s.Id));
            if (SidebarEditPlan.ShowsBody(in edit, s)) PlanBody(s, 0, in input, ref st);
        }

        return new SidebarRowPlan(st.Rows, st.Entries, input.Revision);
    }

    // ── expanded plan ────────────────────────────────────────────────────────────────────────────────────────────────

    static void PlanSection(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        // An authored-off section contributes no rows, no rail tiles and no projection work. An unknown (future) kind
        // renders as nothing — it stays in the document and round-trips untouched.
        if (s.Hidden || !SidebarSectionKinds.IsKnown(s.Kind)) return;

        if (s.Kind == SidebarSectionKind.Divider)
        {
            st.DividerPending = true;      // flushed by the next row: leading/trailing drop, consecutive collapse
            st.DividerSectionId = s.Id;
            st.DividerDepth = depth;
            return;
        }

        if (s.Kind == SidebarSectionKind.Header)
        {
            Add(ref st, new SidebarRow(SidebarRowKind.HeaderLabel, s.Id, depth, -1, 0, s.Id));
            return;
        }

        // The shortcuts/top-bar band (Home, Search, ...) is never collapsible chrome, so it must never claim a
        // SectionHeader row of its own. TitleLocKey stays on the spec (the customizer still names the band); this
        // only suppresses the ROW. With no header row here, the quick layout menu host falls through to the next
        // SectionHeader — which the pane already picks as the plan's first header row.
        if ((s.Title is not null || s.TitleLocKey is not null) && !SidebarIds.IsTopBar(s.Id))
            Add(ref st, new SidebarRow(SidebarRowKind.SectionHeader, s.Id, depth, -1, 0, s.Id));

        if (s.Collapsed) return;

        PlanBody(s, depth, in input, ref st);
    }

    /// <summary>A section's BODY rows — everything after its header. Split out of <see cref="PlanSection"/> so the
    /// edit projection (<see cref="BuildEdit"/>) can plan a real body under a card without also planning a second
    /// header; <see cref="PlanSection"/> is unchanged in behaviour (header, collapse gate, then this).</summary>
    static void PlanBody(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        switch (s.Kind)
        {
            case SidebarSectionKind.Pinned: PlanPinned(s, depth, in input, ref st); break;
            case SidebarSectionKind.JumpBackIn: PlanJumpBackIn(s, depth, in input, ref st); break;
            case SidebarSectionKind.CollectionShortcuts:
            case SidebarSectionKind.StaticLinks: PlanItems(s, depth, in input, ref st, iconRows: true); break;
            case SidebarSectionKind.PlaylistTree: PlanPlaylistTree(s, depth, in input, ref st); break;
            case SidebarSectionKind.EntityList: PlanEntityList(s, depth, in input, ref st); break;
            case SidebarSectionKind.CustomGroup: PlanGroup(s, depth, in input, ref st); break;
            case SidebarSectionKind.EntityEmbed: PlanEmbed(s, depth, in input, ref st); break;
            case SidebarSectionKind.NewReleases:
                PlanFeed(s, depth, input.NewReleases, input.NewReleasesState, ref st);
                break;
            case SidebarSectionKind.Concerts: PlanConcerts(s, depth, in input, ref st); break;
            case SidebarSectionKind.Extension: PlanExtension(s, depth, in input, ref st); break;
        }
    }

    /// <summary>An extension contribution: the binder already resolved it into a window over
    /// <see cref="SidebarProjectionInput.ExtensionEntries"/>. The section KEEPS its spec in every degraded case — a
    /// missing / disabled / schema-incompatible contribution plans exactly ONE actionable <c>PromptRow</c> ("Manage
    /// extension"), never a silent disappearance and never a removal from the document.</summary>
    static void PlanExtension(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var slices = input.ExtensionSlices;
        if (slices is null || !slices.TryGet(s.Id, out var slice)
            || slice.Availability is SidebarContributionAvailability.Missing
                                  or SidebarContributionAvailability.Disabled
                                  or SidebarContributionAvailability.Incompatible)
        {
            Add(ref st, Chrome(SidebarRowKind.PromptRow, s, depth));
            return;
        }

        // Clamp the window defensively: the pool and the table are published together, but a stale table must degrade
        // to "empty", never index out of range.
        var pool = input.ExtensionEntries;
        int available = pool?.Count ?? 0;
        int start = slice.Start, count = slice.Count;
        if (start < 0 || count <= 0 || start >= available) count = 0;
        else if (start + count > available) count = available - start;

        if (count == 0)
        {
            // An actionable degraded state beats both a skeleton and an empty caption (the Concerts "Set your location" row).
            if (slice.NeedsPrompt) Add(ref st, Chrome(SidebarRowKind.PromptRow, s, depth));
            else if (slice.State == SidebarSourceState.Pending) EmitSkeletons(s, depth, ref st);
            else Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
            return;
        }

        int cap = Cap(s, DynamicSectionRowCap);
        if (count > cap) count = cap;
        int at = st.Entries.Count;
        for (int i = 0; i < count; i++) st.Entries.Add(pool![start + i]);
        EmitProjected(s, depth, at, count, ref st);
    }

    static void PlanPinned(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var pins = input.Pins;
        int start = st.Entries.Count;
        int cap = Cap(s, DynamicSectionRowCap);
        bool grid = s.Opts.Presentation == SidebarPresentation.Grid;
        if (pins is not null)
            for (int i = 0; i < pins.Count && st.Entries.Count - start < cap; i++)
            {
                var pin = pins[i];
                if (IsHiddenOverride(s, pin)) continue;

                int at = st.Entries.Count;
                st.Entries.Add(pin);
                if (!grid)
                    Add(ref st, new SidebarRow(pin.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow,
                        s.Id, depth, at, 0, pin.Id));

                if (pin.IsFolder && IsExpanded(input, FolderId(in pin)))
                    AppendPinnedFolderChildren(s, depth, in pin, start, cap, grid, in input, ref st);
            }

        int count = st.Entries.Count - start;
        // Empty Pinned is the real DropZone row ("Drop items here to pin"), not a caption.
        if (count == 0) { Add(ref st, Chrome(SidebarRowKind.Empty, s, depth)); return; }
        if (grid) EmitProjected(s, depth, start, count, ref st);
    }

    /// <summary>Expand one pinned folder against the canonical flattened rootlist. The pinned folder itself is a root
    /// row in this section; descendants keep only their depth RELATIVE to that root. Nested disclosures obey the same
    /// shared expansion set as PlaylistTree, and the section's item cap bounds roots plus descendants together.</summary>
    static void AppendPinnedFolderChildren(SidebarSectionSpec s, byte depth, in SidebarLibraryEntry pin,
        int sectionStart, int cap, bool grid, in SidebarProjectionInput input, ref PlanState st)
    {
        var tree = input.PlaylistTree;
        if (tree is null || tree.Count == 0) return;

        string folderId = FolderId(in pin);
        int root = -1;
        for (int i = 0; i < tree.Count; i++)
        {
            var candidate = tree[i];
            if (!candidate.IsFolder) continue;
            if (string.Equals(candidate.Id, pin.Id, StringComparison.Ordinal)
                || string.Equals(FolderId(in candidate), folderId, StringComparison.Ordinal))
            {
                root = i;
                break;
            }
        }
        if (root < 0) return;

        int rootDepth = tree[root].Depth;
        for (int i = root + 1; i < tree.Count && st.Entries.Count - sectionStart < cap; i++)
        {
            var child = tree[i];
            if (child.Depth <= rootDepth) break;

            int at = st.Entries.Count;
            st.Entries.Add(child);
            if (!grid)
            {
                int relativeDepth = Math.Max(1, child.Depth - rootDepth);
                byte rowDepth = (byte)Math.Min(depth + relativeDepth, byte.MaxValue);
                Add(ref st, new SidebarRow(child.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow,
                    s.Id, rowDepth, at, 0, child.Id));
            }

            if (child.IsFolder && !IsExpanded(input, FolderId(in child)))
            {
                int collapsedDepth = child.Depth;
                while (i + 1 < tree.Count && tree[i + 1].Depth > collapsedDepth) i++;
            }
        }
    }

    static void PlanJumpBackIn(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var src = s.Opts.Recents == SidebarRecentsSource.Played ? input.Played : input.Visited;
        if (input.RecentsState == SidebarSourceState.Pending && (src is null || src.Count == 0))
        {
            EmitSkeletons(s, depth, ref st);
            return;
        }
        PlanTopN(s, depth, src, ref st);
    }

    static void PlanFeed(SidebarSectionSpec s, byte depth, IReadOnlyList<SidebarLibraryEntry>? src,
        SidebarSourceState state, ref PlanState st)
    {
        if (state == SidebarSourceState.Pending && (src is null || src.Count == 0))
        {
            EmitSkeletons(s, depth, ref st);
            return;
        }
        PlanTopN(s, depth, src, ref st);
    }

    static void PlanTopN(SidebarSectionSpec s, byte depth, IReadOnlyList<SidebarLibraryEntry>? src, ref PlanState st)
    {
        int start = st.Entries.Count;
        int cap = Cap(s, DynamicSectionRowCap);
        if (src is not null)
            for (int i = 0; i < src.Count && st.Entries.Count - start < cap; i++) st.Entries.Add(src[i]);

        int count = st.Entries.Count - start;
        if (count == 0) { Add(ref st, Chrome(SidebarRowKind.Empty, s, depth)); return; }
        EmitProjected(s, depth, start, count, ref st);
    }

    static void PlanConcerts(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        // Location unset is an ACTIONABLE degraded state, not an empty list.
        if (input.ConcertsLocationUnset) { Add(ref st, Chrome(SidebarRowKind.PromptRow, s, depth)); return; }
        PlanFeed(s, depth, input.Concerts, input.ConcertsState, ref st);
    }

    static void PlanPlaylistTree(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var tree = input.PlaylistTree;
        if (input.TreeState == SidebarSourceState.Pending && (tree is null || tree.Count == 0))
        {
            EmitSkeletons(s, depth, ref st);
            return;
        }
        if (tree is null || tree.Count == 0)
        {
            // The kind's ordinary Empty hint, and nothing else. The affordance is the header's "+" now.
            Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
            return;
        }

        string? search = Search(input);
        if (search is not null || s.Opts.Presentation == SidebarPresentation.Grid)
            PlanFlatPlaylistTree(s, depth, tree, search, in input, ref st);
        else if (s.Query is null)
            PlanSourcePlaylistTree(s, depth, tree, in input, ref st);
        else
            PlanQueriedPlaylistTree(s, depth, tree, s.Query, in input, ref st);

        // The closing gutter, and only where there is a tree to close: an empty section's placeholder is not
        // something you can drop AFTER, and a skeleton has no order yet. It is the section's LAST row outright.
        if (EmittedTreeRows(in st)) Add(ref st, Chrome(SidebarRowKind.TreeEnd, s, depth));
    }

    /// <summary>Did the tree body just emit a real, orderable row? A trailing Empty/Skeleton means it did not.</summary>
    static bool EmittedTreeRows(in PlanState st)
    {
        var rows = st.Rows;
        if (rows.Count == 0) return false;
        return rows[rows.Count - 1].Kind is SidebarRowKind.EntityRow or SidebarRowKind.FolderHeader
                                          or SidebarRowKind.GridStrip;
    }

    static void PlanSourcePlaylistTree(SidebarSectionSpec s, byte depth,
        IReadOnlyList<SidebarLibraryEntry> tree, in SidebarProjectionInput input, ref PlanState st)
    {
        int emitted = 0;
        int hidePinDepth = -1;   // >= 0 while walking a pinned folder's subtree (reachable through the pin, not here)
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
                if (e.Kind == SidebarEntryKind.Folder) hidePinDepth = e.Depth;
                continue;
            }

            byte d = (byte)Math.Min(depth + e.Depth, byte.MaxValue);
            int at = st.Entries.Count;
            st.Entries.Add(e);
            if (e.Kind == SidebarEntryKind.Folder)
            {
                Add(ref st, new SidebarRow(SidebarRowKind.FolderHeader, s.Id, d, at, 0, e.Id));
                emitted++;
                if (!IsExpanded(input, e.FolderId))
                {
                    int myDepth = e.Depth;
                    while (i + 1 < tree.Count && tree[i + 1].Depth > myDepth) i++;
                }
                continue;
            }
            Add(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, d, at, 0, e.Id));
            emitted++;
        }
        if (emitted == 0) Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
    }

    static void PlanFlatPlaylistTree(SidebarSectionSpec s, byte depth,
        IReadOnlyList<SidebarLibraryEntry> tree, string? search, in SidebarProjectionInput input, ref PlanState st)
    {
        var q = SidebarSectionKinds.EffectiveQuery(SidebarSectionKind.PlaylistTree, s.Query);
        int start = st.Entries.Count;
        int hidePinDepth = -1;
        for (int i = 0; i < tree.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
        {
            var e = tree[i];
            if (hidePinDepth >= 0)
            {
                if (e.Depth > hidePinDepth) continue;
                hidePinDepth = -1;
            }
            if (HiddenByPin(in input, in st, in e))
            {
                if (e.Kind == SidebarEntryKind.Folder) hidePinDepth = e.Depth;
                continue;
            }
            if (e.Kind == SidebarEntryKind.Folder || !TreeLeafMatches(q, in e, search)) continue;
            st.Entries.Add(e);
        }

        int count = st.Entries.Count - start;
        if (count == 0) { Add(ref st, Chrome(SidebarRowKind.Empty, s, depth)); return; }
        if (s.Query is not null && count > 1)
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(st.Entries).Slice(start, count)
                .Sort(new EntryOrder(q.Sort, q.Descending, input.PinnedIds));
        EmitProjected(s, depth, start, count, ref st);
    }

    /// <summary>Filter a flattened preorder without breaking its tree: leaf slots sort only against other leaf slots
    /// under the same immediate parent; folders keep their structural source positions and survive iff a descendant
    /// leaf survives. Scratch lists belong to SidebarPlanBuffers, so a warm re-plan allocates nothing.</summary>
    static void PlanQueriedPlaylistTree(SidebarSectionSpec s, byte depth,
        IReadOnlyList<SidebarLibraryEntry> tree, SidebarEntityQuery q,
        in SidebarProjectionInput input, ref PlanState st)
    {
        var parents = st.TreeParents;
        var visible = st.TreeVisible;
        var leaves = st.TreeLeaves;
        var cursors = st.TreeCursors;
        if (!PrepareQueriedPlaylistTree(tree, q, in input, ref st))
        {
            Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
            return;
        }

        int emitted = 0;
        for (int i = 0; i < tree.Count && emitted < DynamicSectionRowCap; i++)
        {
            if (visible[i] == 0) continue;
            var source = tree[i];
            byte d = (byte)Math.Min(depth + source.Depth, byte.MaxValue);
            if (source.Kind == SidebarEntryKind.Folder)
            {
                int at = st.Entries.Count;
                st.Entries.Add(source);
                Add(ref st, new SidebarRow(SidebarRowKind.FolderHeader, s.Id, d, at, 0, source.Id));
                emitted++;
                if (!IsExpanded(input, source.FolderId))
                {
                    int myDepth = source.Depth;
                    while (i + 1 < tree.Count && tree[i + 1].Depth > myDepth) i++;
                }
                continue;
            }

            int parentSlot = parents[i] + 1;
            int sortedSource = leaves[cursors[parentSlot]++];
            var leaf = tree[sortedSource];
            int entryAt = st.Entries.Count;
            st.Entries.Add(leaf);
            Add(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, d, entryAt, 0, leaf.Id));
            emitted++;
        }
    }

    static bool PrepareQueriedPlaylistTree(IReadOnlyList<SidebarLibraryEntry> tree, SidebarEntityQuery q,
        in SidebarProjectionInput input, ref PlanState st)
    {
        var parents = st.TreeParents;
        var visible = st.TreeVisible;
        var leaves = st.TreeLeaves;
        var cursors = st.TreeCursors;
        var ancestors = st.TreeAncestors;
        parents.Clear();
        visible.Clear();
        leaves.Clear();
        cursors.Clear();
        ancestors.Clear();

        int hidePinDepth = -1;
        for (int i = 0; i < tree.Count; i++)
        {
            var e = tree[i];
            while (ancestors.Count > 0 && tree[ancestors[^1]].Depth >= e.Depth)
                ancestors.RemoveAt(ancestors.Count - 1);

            int parent = ancestors.Count == 0 ? -1 : ancestors[^1];
            parents.Add(parent);
            visible.Add(0);

            bool inHiddenSubtree = hidePinDepth >= 0 && e.Depth > hidePinDepth;
            if (!inHiddenSubtree && hidePinDepth >= 0) hidePinDepth = -1;   // exited a previously hidden subtree

            if (e.Kind == SidebarEntryKind.Folder)
            {
                ancestors.Add(i);
                if (!inHiddenSubtree && HiddenByPin(in input, in st, in e)) hidePinDepth = e.Depth;
                continue;
            }
            if (inHiddenSubtree || HiddenByPin(in input, in st, in e)) continue;
            if (!TreeLeafMatches(q, in e, search: null)) continue;

            visible[i] = 1;
            leaves.Add(i);
            for (int a = 0; a < ancestors.Count; a++) visible[ancestors[a]] = 1;
        }

        if (leaves.Count == 0) return false;
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(leaves).Sort(
            new TreeLeafOrder(tree, parents, new EntryOrder(q.Sort, q.Descending, input.PinnedIds)));

        for (int i = 0; i <= tree.Count; i++) cursors.Add(-1);
        for (int i = 0; i < leaves.Count; i++)
        {
            int slot = parents[leaves[i]] + 1;
            if (cursors[slot] < 0) cursors[slot] = i;
        }
        return true;
    }

    static bool TreeLeafMatches(SidebarEntityQuery q, in SidebarLibraryEntry e, string? search)
    {
        if (!KindMatches(q.Kinds, e.Kind)) return false;
        if (q.Qualifier != SidebarPlaylistQualifier.Any &&
            (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier((byte)q.Qualifier))) return false;
        if (!UriMatches(q, in e)) return false;
        return search is null || SidebarSearch.Matches(in e, search);
    }

    static void PlanEntityList(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var lib = input.Library;
        if (input.LibraryState == SidebarSourceState.Pending && (lib is null || lib.Count == 0))
        {
            EmitSkeletons(s, depth, ref st);
            return;
        }

        var q = s.Query ?? SidebarEntityQuery.Default;
        string? search = Search(input);
        int start = st.Entries.Count;

        if (lib is not null)
            for (int i = 0; i < lib.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
            {
                var e = lib[i];
                if (!KindMatches(q.Kinds, e.Kind)) continue;
                if (q.Qualifier != SidebarPlaylistQualifier.Any &&
                    (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier((byte)q.Qualifier))) continue;
                if (!UriMatches(q, in e)) continue;
                if (search is not null && !SidebarSearch.Matches(in e, search)) continue;
                if (HiddenByPin(in input, in st, in e)) continue;
                st.Entries.Add(e);
            }

        int count = st.Entries.Count - start;
        if (count == 0) { Add(ref st, Chrome(SidebarRowKind.Empty, s, depth)); return; }

        // Sort the slice in place — no temporary list, no boxed comparer (struct comparer, generic Span.Sort).
        if (count > 1)
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(st.Entries).Slice(start, count)
                .Sort(new EntryOrder(q.Sort, q.Descending, input.PinnedIds));

        int cap = Cap(s, DynamicSectionRowCap);
        if (count > cap)
        {
            // MaxItems truncates the PLAN, never the document.
            st.Entries.RemoveRange(start + cap, count - cap);
            count = cap;
        }

        EmitProjected(s, depth, start, count, ref st);
    }

    static void PlanGroup(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        int before = st.Rows.Count;
        byte inner = (byte)Math.Min(depth + 1, byte.MaxValue);
        PlanItems(s, inner, in input, ref st, iconRows: true, emitEmpty: false);

        var kids = s.ChildList;
        for (int i = 0; i < kids.Count; i++) PlanSection(kids[i], inner, in input, ref st);

        if (st.Rows.Count == before) Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
    }

    static void PlanEmbed(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st)
    {
        var items = s.ItemList;
        if (items.Count == 0 || items[0].Hidden)
        {
            Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
            return;
        }

        var item = items[0];
        int idx = Resolve(in input, item.Key, ref st);
        // A missing entity is STILL a card (dimmed, from fallback title/image, play affordance hidden) —
        // EntryIndex == -1 is the signal, and the item is never auto-removed.
        Add(ref st, new SidebarRow(SidebarRowKind.EntityCard, s.Id, depth, idx, 0, item.Key));
    }

    static void PlanItems(SidebarSectionSpec s, byte depth, in SidebarProjectionInput input, ref PlanState st,
        bool iconRows, bool emitEmpty = true)
    {
        var items = s.ItemList;
        int emitted = 0;
        for (int i = 0; i < items.Count && emitted < SectionRowCap; i++)
        {
            var item = items[i];
            if (item.Hidden) continue;

            if (item.Target == SidebarItemTarget.Route)
            {
                // A pinned route (e.g. "liked", or Home) already draws as a pin — skip the shortcut's own row.
                if (IsRouteHiddenByPin(in input, in st, item.Key)) continue;
                // Routes are glyph rows — a hand-picked page has no artwork and never resolves against the projection.
                Add(ref st, new SidebarRow(iconRows ? SidebarRowKind.IconRow : SidebarRowKind.EntityRow,
                    s.Id, depth, -1, 0, item.Key));
                emitted++;
                continue;
            }

            int idx = Resolve(in input, item.Key, ref st);
            if (item.Target == SidebarItemTarget.Track)
            {
                // A track has no detail route, and a HAND-PLACED track is not part of the library projection either
                // (only a feed source emits SidebarEntryKind.Track rows): the row renders from the item spec and
                // PLAYS on click. Tracks are never pinnable, so no HiddenByPin check applies here.
                Add(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, depth, idx, 0, item.Key));
                emitted++;
                continue;
            }

            // An ENTITY item whose resolved entry is pinned already draws as a pin — skip the shortcut's own row. An
            // unresolved item (idx < 0) has nothing to test and keeps the existing Placeholder behaviour.
            if (idx >= 0 && HiddenByPin(in input, in st, st.Entries[idx]))
            {
                st.Entries.RemoveAt(idx);   // Resolve just appended it at the tail — undo so no orphan entry lingers
                continue;
            }

            Add(ref st, new SidebarRow(idx >= 0 ? SidebarRowKind.EntityRow : SidebarRowKind.Placeholder,
                s.Id, depth, idx, 0, item.Key));
            emitted++;
        }

        if (emitted == 0 && emitEmpty) Add(ref st, Chrome(SidebarRowKind.Empty, s, depth));
    }

    // ── rail plan ────────────────────────────────────────────────────────────────────────────────────────────────────

    static void RailSection(SidebarSectionSpec s, in SidebarProjectionInput input, ref PlanState st, ref int tiles)
    {
        switch (s.Kind)
        {
            case SidebarSectionKind.Pinned:
                RailFrom(s, input.Pins, Cap(s, RailPinnedCap), skipHidden: true, ref st, ref tiles);
                break;

            case SidebarSectionKind.JumpBackIn:
                RailFrom(s, s.Opts.Recents == SidebarRecentsSource.Played ? input.Played : input.Visited,
                    Cap(s, RailJumpBackInCap), skipHidden: false, ref st, ref tiles);
                break;

            case SidebarSectionKind.CollectionShortcuts:
            case SidebarSectionKind.StaticLinks:
                RailItems(s, in input, ref st, ref tiles);
                break;

            case SidebarSectionKind.PlaylistTree:
                RailTree(s, in input, ref st, ref tiles);
                break;

            case SidebarSectionKind.EntityList:
                RailEntityList(s, in input, ref st, ref tiles);
                break;

            case SidebarSectionKind.CustomGroup:
                RailItems(s, in input, ref st, ref tiles);
                var kids = s.ChildList;
                for (int i = 0; i < kids.Count; i++)
                {
                    var k = kids[i];
                    if (k.Hidden || !k.Opts.ShowInRail || !SidebarSectionKinds.IsKnown(k.Kind)) continue;
                    if (k.Kind is SidebarSectionKind.Divider or SidebarSectionKind.Header) continue;
                    RailSection(k, in input, ref st, ref tiles);   // children's tiles, flattened
                }
                break;

            case SidebarSectionKind.EntityEmbed:
            {
                var items = s.ItemList;
                if (items.Count == 0 || items[0].Hidden) break;
                int idx = Resolve(in input, items[0].Key, ref st);
                if (idx < 0) break;                                 // placeholder items contribute no tile
                AddTile(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, idx, 0, items[0].Key), ref tiles);
                break;
            }

            case SidebarSectionKind.Concerts:
                // One glyph tile navigating to the hub (a feed has no single cover).
                AddTile(ref st, new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, s.Id), ref tiles);
                break;

            case SidebarSectionKind.Extension:
                // A contribution gets ONE glyph tile in the rail: a 56-DIP strip cannot express a third-party list,
                // and an unresolved contribution must not be able to fill the rail with prompts either. Tapping it
                // expands.
                AddTile(ref st, new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, s.Id), ref tiles);
                break;

            // NewReleases: ShowInRail is forced off for this kind — a releases feed has no meaningful single tile.
            case SidebarSectionKind.NewReleases:
            default:
                break;
        }
    }

    static void RailFrom(SidebarSectionSpec s, IReadOnlyList<SidebarLibraryEntry>? src, int cap, bool skipHidden,
        ref PlanState st, ref int tiles)
    {
        if (src is null) return;
        int n = 0;
        for (int i = 0; i < src.Count && n < cap; i++)
        {
            if (skipHidden && IsHiddenOverride(s, src[i])) continue;
            int idx = st.Entries.Count;
            st.Entries.Add(src[i]);
            // A pinned/Jump-Back-In FOLDER needs the same FolderHeader tile RailTree draws (a glyph opening the
            // rail's flyout) — never EntityRow, whose art/click paths assume a non-folder entry.
            var kind = src[i].IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow;
            if (!AddTile(ref st, new SidebarRow(kind, s.Id, 0, idx, 0, src[i].Id), ref tiles))
            {
                st.Entries.RemoveAt(idx);   // the cap swallowed the tile — do not leak an orphan entry
                return;
            }
            n++;
        }
    }

    static void RailItems(SidebarSectionSpec s, in SidebarProjectionInput input, ref PlanState st, ref int tiles)
    {
        var items = s.ItemList;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Hidden) continue;

            if (item.Target == SidebarItemTarget.Route)
            {
                if (IsRouteHiddenByPin(in input, in st, item.Key)) continue;
                if (!AddTile(ref st, new SidebarRow(SidebarRowKind.IconRow, s.Id, 0, -1, 0, item.Key), ref tiles))
                    return;
                continue;
            }
            if (item.Target == SidebarItemTarget.Track) continue;   // a track tile in a text-less rail is unreadable

            int idx = Resolve(in input, item.Key, ref st);
            if (idx < 0) continue;                                   // placeholder items are skipped
            if (HiddenByPin(in input, in st, st.Entries[idx]))
            {
                st.Entries.RemoveAt(idx);
                continue;
            }
            if (!AddTile(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, idx, 0, item.Key), ref tiles))
            {
                st.Entries.RemoveAt(idx);
                return;
            }
        }
    }

    static void RailTree(SidebarSectionSpec s, in SidebarProjectionInput input, ref PlanState st, ref int tiles)
    {
        var tree = input.PlaylistTree;
        if (tree is null || tree.Count == 0) return;
        // The caller's per-tree ceiling (0 = unbounded). See SidebarProjectionInput.RailTreeCap.
        int cap = input.RailTreeCap > 0 ? Math.Min(input.RailTreeCap, RailTileCap) : RailTileCap;
        string? search = Search(input);

        // A grid (and a search result) has no folder chrome — same flatten-to-entities projection the expanded pane
        // uses. DELIBERATELY EXEMPT from the top-level-only rule below: this arm draws no folder tiles at all, so
        // filtering by depth would make a nested playlist unreachable rather than merely un-tiled.
        if (search is not null || s.Opts.Presentation == SidebarPresentation.Grid)
        {
            var q = SidebarSectionKinds.EffectiveQuery(SidebarSectionKind.PlaylistTree, s.Query);
            int start = st.Entries.Count;
            int hidePinDepth = -1;
            for (int i = 0; i < tree.Count && st.Entries.Count - start < DynamicSectionRowCap; i++)
            {
                var entry = tree[i];
                if (hidePinDepth >= 0)
                {
                    if (entry.Depth > hidePinDepth) continue;
                    hidePinDepth = -1;
                }
                if (HiddenByPin(in input, in st, in entry))
                {
                    if (entry.Kind == SidebarEntryKind.Folder) hidePinDepth = entry.Depth;
                    continue;
                }
                if (entry.Kind == SidebarEntryKind.Folder || !TreeLeafMatches(q, in entry, search)) continue;
                st.Entries.Add(entry);
            }

            int count = st.Entries.Count - start;
            if (s.Query is not null && count > 1)
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(st.Entries).Slice(start, count)
                    .Sort(new EntryOrder(q.Sort, q.Descending, input.PinnedIds));
            if (count > cap)
            {
                st.Entries.RemoveRange(start + cap, count - cap);
                count = cap;
            }
            for (int i = 0; i < count; i++)
            {
                var entry = st.Entries[start + i];
                if (AddTile(ref st,
                    new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, start + i, 0, entry.Id), ref tiles)) continue;
                st.Entries.RemoveRange(start + i, count - i);
                return;
            }
            return;
        }

        // With no query, the persisted rootlist order remains byte-for-byte today's rail. With a query, prune folders
        // whose descendants no longer match and sort leaf slots only within their immediate folder.
        if (s.Query is not null && !PrepareQueriedPlaylistTree(tree, s.Query, in input, ref st)) return;
        int drawn = 0;
        for (int i = 0; i < tree.Count && drawn < cap; i++)
        {
            if (s.Query is not null && st.TreeVisible[i] == 0) continue;
            var source = tree[i];
            // TOP LEVEL ONLY: a 56-DIP strip has no indent lane, so a nested tile read as an unexplained flat pile
            // and doubled an expanded folder's tile count. A folder's contents stay reachable via its own flyout.
            // Skipped BEFORE the per-parent sibling cursor below, so a slot we never draw never consumes it.
            if (source.Depth > 0) continue;
            SidebarLibraryEntry e;
            if (s.Query is null || source.Kind == SidebarEntryKind.Folder) e = source;
            else
            {
                int parentSlot = st.TreeParents[i] + 1;
                e = tree[st.TreeLeaves[st.TreeCursors[parentSlot]++]];
            }
            // A pinned top-level entry (or folder) already has its own rail tile from the Pinned section — skip it
            // here. Tested against the DRAWN entry, not the walked source slot: with a query, TreeCursors can
            // reassign a different (reordered) leaf onto this position, so `source` and `e` diverge and only `e` is
            // what the tile would actually show.
            if (HiddenByPin(in input, in st, in e)) continue;
            int idx = st.Entries.Count;
            st.Entries.Add(e);
            var kind = e.Kind == SidebarEntryKind.Folder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow;
            if (!AddTile(ref st, new SidebarRow(kind, s.Id, 0, idx, 0, e.Id), ref tiles))
            {
                st.Entries.RemoveAt(idx);
                return;
            }
            drawn++;
        }
    }

    static void RailEntityList(SidebarSectionSpec s, in SidebarProjectionInput input, ref PlanState st, ref int tiles)
    {
        var lib = input.Library;
        if (lib is null) return;

        var q = s.Query ?? SidebarEntityQuery.Default;
        int cap = s.Opts.MaxItems > 0 ? Math.Min(s.Opts.MaxItems, RailEntityListCap) : RailEntityListCap;
        int start = st.Entries.Count;

        for (int i = 0; i < lib.Count; i++)
        {
            var e = lib[i];
            if (!KindMatches(q.Kinds, e.Kind)) continue;
            if (q.Qualifier != SidebarPlaylistQualifier.Any &&
                (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier((byte)q.Qualifier))) continue;
            if (!UriMatches(q, in e)) continue;
            if (HiddenByPin(in input, in st, in e)) continue;
            st.Entries.Add(e);
        }

        int count = st.Entries.Count - start;
        if (count == 0) return;
        if (count > 1)
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(st.Entries).Slice(start, count)
                .Sort(new EntryOrder(q.Sort, q.Descending, input.PinnedIds));
        if (count > cap) { st.Entries.RemoveRange(start + cap, count - cap); count = cap; }

        for (int i = 0; i < count; i++)
            if (!AddTile(ref st, new SidebarRow(SidebarRowKind.EntityRow, s.Id, 0, start + i, 0,
                    st.Entries[start + i].Id), ref tiles))
                return;
    }

    // ── shared plumbing ──────────────────────────────────────────────────────────────────────────────────────────────

    struct PlanState
    {
        public List<SidebarRow> Rows;
        public List<SidebarLibraryEntry> Entries;
        public List<int> TreeParents;
        public List<byte> TreeVisible;
        public List<int> TreeLeaves;
        public List<int> TreeCursors;
        public List<int> TreeAncestors;
        public bool DividerPending;
        public string? DividerSectionId;
        public byte DividerDepth;
        // Set once by Build/BuildRail (HasPinnedSection): does the document have a visible Pinned section? Gates
        // HiddenByPin/IsRouteHiddenByPin — stays false for BuildEdit, whose customize canvas must show every item.
        public bool ExcludePinned;
    }

    static PlanState Begin(SidebarPlanBuffers? buffers)
    {
        var rows = buffers?.Rows ?? new List<SidebarRow>(64);
        var entries = buffers?.Entries ?? new List<SidebarLibraryEntry>(64);
        var treeParents = buffers?.TreeParents ?? new List<int>(64);
        var treeVisible = buffers?.TreeVisible ?? new List<byte>(64);
        var treeLeaves = buffers?.TreeLeaves ?? new List<int>(64);
        var treeCursors = buffers?.TreeCursors ?? new List<int>(64);
        var treeAncestors = buffers?.TreeAncestors ?? new List<int>(8);
        rows.Clear();
        entries.Clear();
        treeParents.Clear();
        treeVisible.Clear();
        treeLeaves.Clear();
        treeCursors.Clear();
        treeAncestors.Clear();
        return new PlanState
        {
            Rows = rows,
            Entries = entries,
            TreeParents = treeParents,
            TreeVisible = treeVisible,
            TreeLeaves = treeLeaves,
            TreeCursors = treeCursors,
            TreeAncestors = treeAncestors,
        };
    }

    /// <summary>The one row sink. A pending divider resolves HERE, which is what makes leading/trailing dividers
    /// vanish and consecutive dividers collapse — no post-pass, no second walk:
    ///   * TRAILING — a divider that is never followed by a row is simply never flushed;
    ///   * LEADING  — a divider flushed before any row exists has nothing to separate, so it is dropped;
    ///   * CONSECUTIVE — a run of dividers keeps overwriting the pending slot, so only the last one draws.
    /// A hidden or empty-and-invisible section in between therefore cannot strand a rule either.</summary>
    static void Add(ref PlanState st, in SidebarRow row)
    {
        if (st.DividerPending)
        {
            st.DividerPending = false;
            var id = st.DividerSectionId!;
            byte depth = st.DividerDepth;
            st.DividerSectionId = null;
            st.DividerDepth = 0;
            if (st.Rows.Count > 0) st.Rows.Add(new SidebarRow(SidebarRowKind.Divider, id, depth, -1, 0, id));
        }
        st.Rows.Add(row);
    }

    static bool AddTile(ref PlanState st, in SidebarRow row, ref int tiles)
    {
        if (tiles >= RailTileCap) return false;
        Add(ref st, row);
        tiles++;
        return true;
    }

    static SidebarRow Chrome(SidebarRowKind kind, SidebarSectionSpec s, byte depth)
        => new(kind, s.Id, depth, -1, 0, s.Id);

    static void EmitSkeletons(SidebarSectionSpec s, byte depth, ref PlanState st)
    {
        for (int i = 0; i < SkeletonRows; i++) Add(ref st, Chrome(SidebarRowKind.Skeleton, s, depth));
    }

    /// <summary>Turns an already-appended entry slice into rows: one EntityRow each, or GridColumns-wide GridStrips.</summary>
    static void EmitProjected(SidebarSectionSpec s, byte depth, int start, int count, ref PlanState st)
    {
        if (s.Opts.Presentation == SidebarPresentation.Grid)
        {
            int cols = Math.Clamp(s.Opts.GridColumns, 2, 4);
            for (int i = 0; i < count; i += cols)
                Add(ref st, new SidebarRow(SidebarRowKind.GridStrip, s.Id, depth, start + i,
                    Math.Min(cols, count - i), s.Id));
            return;
        }
        for (int i = 0; i < count; i++)
        {
            var e = st.Entries[start + i];
            // A folder can reach a projected section two ways: pinned (playlist folders are pinnable) or via a
            // Playlists-kinded EntityList (which maps to the whole tree).
            Add(ref st, new SidebarRow(e.IsFolder ? SidebarRowKind.FolderHeader : SidebarRowKind.EntityRow,
                s.Id, depth, start + i, 0, e.Id));
        }
    }

    static int Resolve(in SidebarProjectionInput input, string key, ref PlanState st)
    {
        if (input.ByUri is null || key.Length == 0) return -1;
        if (!input.ByUri.TryGetValue(key, out var e)) return -1;
        int idx = st.Entries.Count;
        st.Entries.Add(e);
        return idx;
    }

    static int Cap(SidebarSectionSpec s, int hard)
    {
        int m = s.Opts.MaxItems;
        return m > 0 ? Math.Min(m, hard) : hard;
    }

    static bool IsHiddenOverride(SidebarSectionSpec s, in SidebarLibraryEntry e)
    {
        var items = s.ItemList;
        for (int i = 0; i < items.Count; i++)
        {
            if (!items[i].Hidden) continue;
            var k = items[i].Key;
            if (string.Equals(k, e.Uri, StringComparison.Ordinal) ||
                string.Equals(k, e.Id, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    static bool IsExpanded(in SidebarProjectionInput input, string folderId)
        => input.ExpandedFolders is null || input.ExpandedFolders.Contains(folderId);

    /// <summary>Once an item is pinned it must not ALSO appear in the normal lists — one predicate, applied everywhere
    /// a library entry or tree entry is walked. <c>PlanPinned</c> itself is untouched — this only hides an entry from
    /// EVERYTHING ELSE.
    /// <para><see cref="PlanState.ExcludePinned"/> gates the whole rule: a document WITHOUT a visible Pinned section
    /// keeps showing pinned items in place — otherwise they would vanish from the sidebar entirely.</para></summary>
    static bool HiddenByPin(in SidebarProjectionInput input, in PlanState st, in SidebarLibraryEntry e)
        => st.ExcludePinned && (e.IsPinned || (input.PinnedIds is { } p && p.Contains(e.Id)));

    /// <summary>The shortcut-item mirror of <see cref="HiddenByPin"/> for a <c>Route</c> target (Classic's "Liked
    /// Songs" row, a pinned "Home", ...), which has no projected <see cref="SidebarLibraryEntry"/> to test — only the
    /// route's own pin identity against the pin set.</summary>
    static bool IsRouteHiddenByPin(in SidebarProjectionInput input, in PlanState st, string routeKey)
    {
        if (!st.ExcludePinned || input.PinnedIds is not { } pins) return false;
        return SidebarPinId.FromRoute(routeKey) is { } pinId && pins.Contains(pinId);
    }

    /// <summary>Does the document have a visible Pinned section — top-level or one level deep inside a CustomGroup
    /// (the only nesting a section may have)? Computed once per Build/BuildRail and stashed on PlanState so every
    /// walker reads a flag instead of re-scanning the document.</summary>
    static bool HasPinnedSection(SidebarCustomLayout layout)
    {
        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Kind == SidebarSectionKind.Pinned && !s.Hidden) return true;
            var kids = s.ChildList;
            for (int j = 0; j < kids.Count; j++)
                if (kids[j].Kind == SidebarSectionKind.Pinned && !kids[j].Hidden) return true;
        }
        return false;
    }

    static string FolderId(in SidebarLibraryEntry entry)
        => entry.FolderId.Length > 0 ? entry.FolderId : SidebarPinId.FolderIdOf(entry.Id);

    /// <summary>The trimmed library-only query, or null when the pane is not searching.</summary>
    static string? Search(in SidebarProjectionInput input)
    {
        var q = SidebarSearch.Normalize(input.Search);
        return q.Length == 0 ? null : q;
    }

    static bool KindMatches(SidebarEntityKinds kinds, SidebarEntryKind kind)
        => SidebarEntryKinds.Has(SidebarEntryKinds.From(kinds), kind);

    /// <summary>The query's include/exclude uri sets — "only these artists" without turning the section into a
    /// manually maintained item list. <c>Include</c> is a WHITELIST (null/empty = everything passes) and
    /// <c>Exclude</c> ALWAYS wins, so a uri named in both is excluded. Each key is matched against the entry's uri OR
    /// its id, because an authored list may legitimately be written in either vocabulary.</summary>
    static bool UriMatches(SidebarEntityQuery q, in SidebarLibraryEntry e)
    {
        var exclude = q.ExcludeUris;
        if (exclude is { Count: > 0 })
            for (int i = 0; i < exclude.Count; i++)
                if (SameEntity(exclude[i], in e)) return false;

        var include = q.IncludeUris;
        if (include is not { Count: > 0 }) return true;
        for (int i = 0; i < include.Count; i++)
            if (SameEntity(include[i], in e)) return true;
        return false;
    }

    static bool SameEntity(string? key, in SidebarLibraryEntry e)
        => key is { Length: > 0 }
           && (string.Equals(key, e.Uri, StringComparison.Ordinal) || string.Equals(key, e.Id, StringComparison.Ordinal));

    /// <summary>An empty rank map: <c>SidebarSortMode.CustomOrder</c> is Mode B's LOCAL overlay, which a Curated
    /// EntityList has no access to — <c>SidebarSort.Custom</c> with no ranks is exactly the documented degradation
    /// (pure SourceOrder + the ordinal Id tiebreak), so the order is still total and deterministic.</summary>
    static readonly Dictionary<string, int> NoRanks = new(0, StringComparer.Ordinal);

    /// <summary>Total order over the projection: pins first, then the section's sort mode. The per-mode comparison is
    /// SidebarSort's — the one owner of sidebar collation — so a Curated EntityList and the V3 list can never drift
    /// apart. A struct comparer + the generic Span.Sort overload means no boxing and no per-plan delegate.</summary>
    readonly struct EntryOrder : IComparer<SidebarLibraryEntry>
    {
        readonly SidebarSortMode _mode;
        readonly bool _desc;
        readonly IReadOnlySet<string>? _pins;

        public EntryOrder(SidebarSortMode mode, bool descending, IReadOnlySet<string>? pins)
        {
            _mode = mode;
            // DIRECTION RECONCILIATION. SidebarSort's `desc` means "REVERSE this comparator's natural direction", and
            // its recency comparators are naturally newest-first; the Core query's `Descending` means "descending"
            // literally. Map per mode so the default query (Recents, Descending: true) really is newest-first and
            // PlaylistsAlphabetical (Descending: false) really is A->Z.
            _desc = mode is SidebarSortMode.Recents or SidebarSortMode.RecentlyAdded ? !descending : descending;
            _pins = pins;
        }

        public int Compare(SidebarLibraryEntry a, SidebarLibraryEntry b)
        {
            // Pins sort first in EVERY sort mode. The caller's explicit set wins; otherwise the projection's own
            // IsPinned stamp is the authority.
            bool pa = _pins?.Contains(a.Id) ?? a.IsPinned;
            bool pb = _pins?.Contains(b.Id) ?? b.IsPinned;
            if (pa != pb) return pa ? -1 : 1;

            return _mode switch
            {
                SidebarSortMode.RecentlyAdded => SidebarSort.RecentlyAdded(in a, in b, _desc),
                SidebarSortMode.Alphabetical => SidebarSort.Alphabetical(in a, in b, _desc),
                SidebarSortMode.Creator => SidebarSort.Creator(in a, in b, _desc),
                SidebarSortMode.CustomOrder => SidebarSort.Custom(in a, in b, NoRanks),
                _ => SidebarSort.Recents(in a, in b, _desc),
            };
        }
    }

    /// <summary>Sort leaf source indices into parent bands, then by the shared entry order inside each band.</summary>
    readonly struct TreeLeafOrder : IComparer<int>
    {
        readonly IReadOnlyList<SidebarLibraryEntry> _tree;
        readonly IReadOnlyList<int> _parents;
        readonly EntryOrder _entries;

        public TreeLeafOrder(IReadOnlyList<SidebarLibraryEntry> tree, IReadOnlyList<int> parents, EntryOrder entries)
        {
            _tree = tree;
            _parents = parents;
            _entries = entries;
        }

        public int Compare(int a, int b)
        {
            int parent = _parents[a].CompareTo(_parents[b]);
            return parent != 0 ? parent : _entries.Compare(_tree[a], _tree[b]);
        }
    }
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

        // THE LADDER IS THE ROW'S OWN. `TreeContentX(d)` is where a tree row at depth d actually starts drawing —
        // the row's leading lane (gutter + gap) plus one connector cell per level — so one cell left is one outdent.
        // There is no reserved disclosure cell (the folder chevron is trailing).
        float steps = (xInRow - SidebarRowGeometry.TreeContentX(0)) / SidebarRowGeometry.TreeGuideStep;
        int picked = (int)MathF.Round(steps);
        if (picked < min) picked = min;
        if (picked > max) picked = max;

        // HYSTERESIS. Without it the boundary between two depths sits under a still hand and the line flickers. The
        // previous depth is held until the pointer is a clear 4 DIP past the boundary that would leave it.
        if (previous.Kind == SidebarDropKind.After && previous.Depth >= min && previous.Depth <= max
            && previous.Depth != picked)
        {
            float boundary = SidebarRowGeometry.TreeContentX(0)
                + (previous.Depth + (picked > previous.Depth ? 0.5f : -0.5f)) * SidebarRowGeometry.TreeGuideStep;
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
    /// row's trailing inset. ONE origin with the caret's transform and with <c>PickDepth</c>.</summary>
    public static float LineWidth(float contentWidth, int depth)
    {
        float w = contentWidth - SidebarRowGeometry.TreeContentX(depth) - SidebarRowGeometry.RowInsetRight;
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
// ── PROJECTION, SHAPING PIPELINE, SORT, SEARCH & THE DATA-SOURCE CONTRACT ─────────────────────────────────────────────
//
// The pure half of the binder: turns `User.Me`'s edges into the flat `SidebarLibraryEntry` list every design renders
// (SidebarProjection), filters/sorts/pins-first-partitions that list for a view (SidebarSort, SidebarSearch,
// SidebarBinderPipeline), decides which extension section resolves to which row slice (SidebarDataSourceTable /
// SidebarExtensionSlices / SidebarContributionCache), and the contribution CONTRACT third-party sources implement
// (SidebarDataSource). Nothing here awaits, fetches, hydrates or touches a store — that half is Sidebar.Host.cs.

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
    /// <see cref="SidebarV3Sort.Custom"/>; null/empty degrades to pure SourceOrder.</summary>
    public static Comparison<SidebarLibraryEntry> For(SidebarV3Sort sort, bool desc,
                                                     IReadOnlyList<string>? customOrder = null)
    {
        switch (sort)
        {
            case SidebarV3Sort.RecentlyAdded: return desc ? s_addedDesc : s_addedAsc;
            case SidebarV3Sort.Alphabetical: return desc ? s_alphaDesc : s_alphaAsc;
            case SidebarV3Sort.Creator: return desc ? s_creatorDesc : s_creatorAsc;
            case SidebarV3Sort.Custom:
            {
                // Rank map: O(1) lookups instead of an IndexOf per comparison. `desc` is deliberately IGNORED.
                var rank = BuildRanks(customOrder);
                return (a, b) => Custom(in a, in b, rank);
            }
            default: return desc ? s_recentsDesc : s_recentsAsc;
        }
    }

    /// <summary>Sort in place. Filters run BEFORE the sort; pins are partitioned AFTER it.</summary>
    public static void Apply(List<SidebarLibraryEntry> list, SidebarV3Sort sort, bool desc,
                             IReadOnlyList<string>? customOrder = null)
    {
        if (list.Count > 1) list.Sort(For(sort, desc, customOrder));
    }

    /// <summary>Custom exists only under the Playlists filter; elsewhere it falls back to Alphabetical FOR DISPLAY
    /// while the persisted preference is left untouched.</summary>
    public static SidebarV3Sort Effective(SidebarV3Sort sort, SidebarV3Filter filter) =>
        sort == SidebarV3Sort.Custom && filter != SidebarV3Filter.Playlists ? SidebarV3Sort.Alphabetical : sort;

    /// <summary>True when the direction affordance should be shown at all (Custom has no inverse).</summary>
    public static bool SupportsDirection(SidebarV3Sort sort) => sort != SidebarV3Sort.Custom;

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
// GAP: RootlistEdge carries FolderName but no FolderId (contract §8's snippet). A folder's pin/route identity is
// therefore derived from its FolderStart edge's own Position, formatted as "folder:<position>" — stable across a
// rebuild, but it MOVES if the folder itself is repositioned in the rootlist. The honest fix is an Edges.cs column
// (RootlistEdge.FolderId), reported rather than invented here.
// GAP: a cover-less playlist's own 2×2 mosaic (0.2.9's PlaylistSummary.MosaicTiles, built from the playlist's own
// track covers) has no Entities equivalent; a playlist row's MosaicTiles is always null. The FOLDER mosaic (first ≤4
// CHILD playlist covers) is unaffected and ports in full.

public readonly record struct SidebarProjectionResult(int Count, byte FlavorMask, int NewFirstSeenStamps);

public static class SidebarProjection
{
    static readonly StringBuilder s_join = new(64);

    /// <summary>Fill <paramref name="into"/> (CLEARED first) with the unified entry list for the requested kinds, read
    /// straight off <paramref name="u"/>'s edges. <paramref name="includeFolderChildren"/> false ⇒ a collapsed folder's
    /// children are not emitted (still folded into its mosaic/child-count); true ⇒ fully flattened.
    /// <paramref name="lastPlayed"/> is uri → last-played unix ms; null/absent ⇒ every row's LastPlayedMs stays 0.</summary>
    public static SidebarProjectionResult Build(
        List<SidebarLibraryEntry> into,
        in User u,
        SidebarEntryKindMask kinds,
        SidebarFirstSeen? firstSeen,
        SidebarRecency? recency,
        bool includeFolderChildren,
        Func<string, bool>? isFolderExpanded = null,
        IReadOnlyDictionary<string, long>? lastPlayed = null)
    {
        into.Clear();
        var rec = recency ?? SidebarRecency.Empty;
        var seen = firstSeen ?? SidebarFirstSeen.Frozen;
        int stampsBefore = seen.NewStamps;
        byte flavorMask = 0;

        bool wantPlaylists = (kinds & SidebarEntryKindMask.Playlist) != 0;
        bool wantFolders = (kinds & SidebarEntryKindMask.Folder) != 0;
        if ((wantPlaylists || wantFolders) && u.RootlistState != EdgeState.Unknown)
            WalkRootlist(into, in u, wantPlaylists, wantFolders, includeFolderChildren, isFolderExpanded,
                         rec, seen, lastPlayed, ref flavorMask);

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
                into.Add(new SidebarLibraryEntry(
                    id, SidebarEntryKind.Artist, uri, ar.Name, "",
                    ar.ImageId, null, 0, added,
                    SortStamp: added > 0 ? added : seen.Stamp(id),
                    LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                    SourceOrder: i, Depth: 0, Circular: true, Flavor: SidebarPlaylistFlavor.None)
                { FolderId = "", FolderName = "", FirstArtistName = "", LastPlayedMs = LastPlayed(lastPlayed, uri) });
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
                into.Add(new SidebarLibraryEntry(
                    id, SidebarEntryKind.Show, uri, Entities.Strings.Resolve(sh.TitleId),
                    Entities.Strings.Resolve(sh.PublisherId),
                    sh.ImageId, null, 0, added,
                    SortStamp: added > 0 ? added : seen.Stamp(id),
                    LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                    SourceOrder: i, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                { FolderId = "", FolderName = "", FirstArtistName = "", LastPlayedMs = LastPlayed(lastPlayed, uri) });
            }
        }

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

    // App seconds -> unix ms for an edge's AddedAt. 0 means "never dated" and stays 0 so the first-seen fallback
    // can fire; any other value converts, including the negative app seconds of dates older than this launch.
    static long AddedMs(int appSeconds) => appSeconds == 0 ? 0L : Store.ToUnix(appSeconds) * 1000L;

    static void WalkRootlist(
        List<SidebarLibraryEntry> into, in User u,
        bool wantPlaylists, bool wantFolders, bool includeFolderChildren, Func<string, bool>? isFolderExpanded,
        SidebarRecency rec, SidebarFirstSeen seen, IReadOnlyDictionary<string, long>? lastPlayed, ref byte flavorMask)
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
                    string folderId = SidebarPinId.FolderPrefix + ((int)edge.Position).ToString(CultureInfo.InvariantCulture);
                    string folderName = Entities.Strings.Resolve(edge.FolderName);
                    bool descend = includeFolderChildren || !wantFolders || (isFolderExpanded?.Invoke(folderId) ?? false);
                    int rowIndex = -1;
                    if (visible && wantFolders)
                    {
                        string parentId = stack.Count > 0 ? stack[stack.Count - 1].Id : "";
                        string parentName = stack.Count > 0 ? stack[stack.Count - 1].Name : "";
                        rowIndex = into.Count;
                        into.Add(new SidebarLibraryEntry(
                            folderId, SidebarEntryKind.Folder, "", folderName, "",
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
                        into[frame.RowIndex] = into[frame.RowIndex] with { ChildCount = frame.ChildCount, MosaicTiles = frame.Tiles };
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

                    string uri = p.Uri.Text;
                    string id = SidebarPinId.PlaylistPrefix + uri;
                    var flavor = FlavorOf(p);
                    flavorMask |= (byte)(1 << (int)flavor);
                    // Playlists have no add timestamp anywhere but the local first-seen proxy — the rootlist is an
                    // ordered marker stream, and RootlistEdge.AddedAt is documented LOCAL (contract DATA GAPS).
                    long added = AddedMs(edge.AddedAt);
                    string parentId = stack.Count > 0 ? stack[stack.Count - 1].Id : "";
                    string parentName = stack.Count > 0 ? stack[stack.Count - 1].Name : "";
                    into.Add(new SidebarLibraryEntry(
                        id, SidebarEntryKind.Playlist, uri, Entities.Strings.Resolve(p.TitleId), OwnerNameOf(in p),
                        p.ImageId, null, p.TrackCount, added,
                        SortStamp: added > 0 ? added : seen.Stamp(id),
                        LastVisitedTicksUtc: rec.LastVisitedTicks(id),
                        SourceOrder: order++, Depth: edge.Depth, Circular: false, Flavor: flavor)
                    {
                        FolderId = parentId, FolderName = parentName,
                        ParentFolderId = parentId, ParentFolderName = parentName,
                        IsOwner = p.IsOwner, CanEdit = p.Editable, FirstArtistName = "",
                        LastPlayedMs = LastPlayed(lastPlayed, uri),
                    });
                    break;
                }
            }
        }
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
    long PlaybackEpoch = 0) // queue revision + now-playing identity
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
            return (long)h;
        }
    }

    static ulong Mix(ulong h, uint v)
    {
        unchecked { h ^= v; h *= 1099511628211UL; return h; }
    }
}

/// <summary>The Library-V3 view state a rebuild shapes the published entry list with.</summary>
public readonly record struct SidebarV3Query(
    SidebarV3Filter Filter = SidebarV3Filter.All,
    SidebarV3Qualifier Qualifier = SidebarV3Qualifier.Any,
    SidebarV3Sort Sort = SidebarV3Sort.Recents,
    bool Descending = true,
    string? Search = null,
    bool QualifiersAvailable = false);

/// <summary>What one shaping pass produced: how many rows were published and how long the leading pin band is.</summary>
public readonly record struct SidebarEntriesShape(int Count, int PinCount);

public static class SidebarBinderPipeline
{
    /// <summary>Shape the unified projection into the list a V3/Classic surface renders: FILTER (kinds → qualifier →
    /// search), then SORT, then the pins-first partition — the order that makes pins lead in every sort mode.
    /// <paramref name="all"/> is the full source-order projection (every kind); <paramref name="into"/> and
    /// <paramref name="scratch"/> are caller-owned and reused.
    ///
    /// <para>A persisted qualifier other than Any is treated as Any whenever the data does not support the chips
    /// (<c>QualifiersAvailable == false</c>). A persisted Custom sort outside the Playlists filter falls back to
    /// Alphabetical FOR DISPLAY, leaving the preference untouched (<see cref="SidebarSort.Effective"/>).</para></summary>
    public static SidebarEntriesShape Project(
        IReadOnlyList<SidebarLibraryEntry>? all,
        List<SidebarLibraryEntry> into,
        List<SidebarLibraryEntry> scratch,
        in SidebarV3Query query,
        IReadOnlyList<SidebarPin>? pins = null,
        IReadOnlyList<string>? customOrder = null)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(scratch);
        into.Clear();
        if (all is null || all.Count == 0) return new SidebarEntriesShape(0, 0);

        var kinds = SidebarEntryKinds.From(query.Filter);
        for (int i = 0; i < all.Count; i++)
            if (SidebarEntryKinds.Has(kinds, all[i].Kind)) into.Add(all[i]);

        return Shape(into, scratch, in query, pins, customOrder);
    }

    /// <summary>The IN-PLACE half of <see cref="Project"/>, for a list that already holds exactly the kinds the filter
    /// wants: compact by qualifier + search, then sort, then partition pins to the front.</summary>
    public static SidebarEntriesShape Shape(
        List<SidebarLibraryEntry> list,
        List<SidebarLibraryEntry> scratch,
        in SidebarV3Query query,
        IReadOnlyList<SidebarPin>? pins = null,
        IReadOnlyList<string>? customOrder = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scratch);

        string search = SidebarSearch.Normalize(query.Search);
        bool searching = search.Length > 0;
        byte qualifier = query.QualifiersAvailable ? (byte)query.Qualifier : (byte)0;

        if (searching || qualifier != 0)
        {
            int write = 0;
            for (int read = 0; read < list.Count; read++)
            {
                var e = list[read];
                // Searching FLATTENS: matching leaves only, no folder chrome.
                if (searching && e.Kind == SidebarEntryKind.Folder) continue;
                if (qualifier != 0 && (e.Kind != SidebarEntryKind.Playlist || !e.MatchesQualifier(qualifier))) continue;
                if (searching && !SidebarSearch.Matches(in e, search)) continue;
                list[write++] = e;
            }
            if (write < list.Count) list.RemoveRange(write, list.Count - write);
        }

        SidebarSort.Apply(list, SidebarSort.Effective(query.Sort, query.Filter), query.Descending, customOrder);
        int band = SidebarProjection.PinsFirst(list, pins, scratch);
        return new SidebarEntriesShape(list.Count, band);
    }

    /// <summary>Build the row for a pin the live projection does NOT know — an editorial/Spotify-owned entity never
    /// saved to the user's own library/rootlist. Renders the pin's own offline display cache first (an unresolved pin
    /// must never disappear), then OVERLAYS <paramref name="hydrated"/> once the binder resolves the handle.</summary>
    public static SidebarLibraryEntry ResolveUnlistedPin(SidebarPin pin, int sourceOrder, SidebarLibraryEntry? hydrated)
    {
        // A folder pin the rootlist walk does not know is genuinely GONE — a folder has no separate hydration path.
        bool folder = pin.Kind == SidebarEntryKind.Folder;
        var baseEntry = new SidebarLibraryEntry(
            pin.Id, pin.Kind, pin.Uri, pin.Name, "", StringId.Empty, null,
            ChildCount: 0, AddedAtMs: pin.AddedAtMs, SortStamp: pin.AddedAtMs, LastVisitedTicksUtc: 0,
            SourceOrder: sourceOrder, Depth: 0, Circular: pin.Kind == SidebarEntryKind.Artist,
            Flavor: SidebarPlaylistFlavor.None)
        {
            IsPinned = true, FolderId = folder ? SidebarPinId.FolderIdOf(pin.Id) : "", FolderName = "",
            FirstArtistName = "", Missing = folder,
        };

        if (hydrated is not { } h) return baseEntry;
        return baseEntry with
        {
            Cover = h.Cover.IsEmpty ? baseEntry.Cover : h.Cover,
            ChildCount = h.ChildCount,
            Creator = h.Creator.Length > 0 ? h.Creator : baseEntry.Creator,
        };
    }

    /// <summary>Resolve EVERY <c>SidebarSectionKind.Extension</c> section (top level + one nesting level) into a row
    /// slice, appending rows into the shared <paramref name="entries"/> pool. The planner stays PURE — this resolves
    /// contributions, the planner only reads the slice table.</summary>
    /// <param name="cache">The per-contribution last-good snapshot — a source that fails after having served rows
    /// replays its snapshot as <see cref="SidebarContributionAvailability.Cached"/> rather than blanking the section.</param>
    public static void ResolveExtensions(
        SidebarCustomLayout? layout,
        ISidebarContributionHost? host,
        List<SidebarLibraryEntry> entries,
        SidebarExtensionSlices slices,
        SidebarContributionCache? cache = null,
        string? search = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(slices);
        entries.Clear();
        slices.Clear();
        if (layout is null) return;

        var sections = layout.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Kind == SidebarSectionKind.Extension) slices.Set(s.Id, Resolve(s, host, entries, cache, search));
            var kids = s.ChildList;
            for (int j = 0; j < kids.Count; j++)
            {
                var k = kids[j];
                if (k.Kind == SidebarSectionKind.Extension) slices.Set(k.Id, Resolve(k, host, entries, cache, search));
            }
        }
    }

    /// <summary>Resolve ONE extension section. Never throws: a contributed source that throws is reported as an Error
    /// slice, with its last-good snapshot replayed when there is one.</summary>
    public static SidebarSectionSlice Resolve(
        SidebarSectionSpec section,
        ISidebarContributionHost? host,
        List<SidebarLibraryEntry> entries,
        SidebarContributionCache? cache = null,
        string? search = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(entries);

        var xref = section.Extension;
        if (xref is null) return Unavailable(SidebarContributionAvailability.Missing);

        string sourceId = SidebarContributions.SourceId(xref.ExtensionId, xref.ContributionId);
        if (sourceId.Length == 0) return Unavailable(SidebarContributionAvailability.Missing);

        ISidebarDataSource? source = null;
        var availability = SidebarContributionAvailability.Missing;
        if (host is not null) source = host.Resolve(sourceId, out availability);
        if (source is null)
            return Unavailable(availability == SidebarContributionAvailability.Live
                ? SidebarContributionAvailability.Missing : availability);

        // A document authored against a NEWER config schema than this build knows: keep the section, say so, change
        // nothing.
        if (xref.SchemaVersion > source.ConfigSchema.Version)
            return Unavailable(SidebarContributionAvailability.Incompatible);

        int start = entries.Count;
        var request = new SidebarSourceRequest(new SidebarSourceConfig(xref.Config), section.Opts.MaxItems, search);
        int count;
        var state = SidebarSourceState.Ready;
        bool prompt = false;
        try
        {
            source.EnsureFresh(request);
            count = source.Fill(entries, request);
            if (count < 0) count = 0;
            if (entries.Count - start != count) count = Math.Max(0, entries.Count - start);
            state = source.State;
            prompt = source.NeedsPrompt;
        }
        catch (Exception)
        {
            if (entries.Count > start) entries.RemoveRange(start, entries.Count - start);   // no partial fill leaks
            count = 0;
            state = SidebarSourceState.Error;
        }

        if (count == 0 && state == SidebarSourceState.Error && cache is not null)
        {
            int replayed = cache.TryReplay(sourceId, entries);
            if (replayed > 0)
                return new SidebarSectionSlice(start, replayed, SidebarSourceState.Ready,
                                               SidebarContributionAvailability.Cached);
        }

        if (count > 0) cache?.Store(sourceId, entries, start, count);
        return new SidebarSectionSlice(start, count, state, SidebarContributionAvailability.Live, prompt);
    }

    static SidebarSectionSlice Unavailable(SidebarContributionAvailability availability)
        => new(0, 0, SidebarSourceState.Error, availability);
}

/// <summary>The sidebar's contribution lookup: source id → source, plus a per-source enable flag. An engine-free,
/// source-included type so resolution is unit-tested against the real host; M3's sandboxed host can replace it wholly
/// by implementing <see cref="ISidebarContributionHost"/>.</summary>
public sealed class SidebarDataSourceTable : ISidebarContributionHost
{
    readonly Dictionary<string, ISidebarDataSource> _sources = new(StringComparer.Ordinal);
    readonly List<ISidebarDataSource> _ordered = new();
    readonly HashSet<string> _disabled = new(StringComparer.Ordinal);

    public int Count => _sources.Count;

    public void Add(ISidebarDataSource? source)
    {
        if (source is null || string.IsNullOrEmpty(source.Id)) return;
        // A re-registration replaces the source IN PLACE, so the registration order the customizer lists never moves.
        if (_sources.TryGetValue(source.Id, out var previous)) _ordered[_ordered.IndexOf(previous)] = source;
        else _ordered.Add(source);
        _sources[source.Id] = source;
    }

    /// <summary>Every source in REGISTRATION order — the customizer's contribution-pick list (ch 26 W3), which a
    /// dictionary's enumeration order does not promise.</summary>
    public IReadOnlyList<ISidebarDataSource> Ordered => _ordered;

    /// <summary>A registered source regardless of its enable flag (the options surface asks "is it registered at all").</summary>
    public bool TryGet(string? sourceId, [NotNullWhen(true)] out ISidebarDataSource? source)
    {
        source = null;
        return sourceId is { Length: > 0 } && _sources.TryGetValue(sourceId, out source);
    }

    /// <summary>Turn a registered contribution off without unregistering it — the honest Disabled row rather than a
    /// section that silently vanishes.</summary>
    public void SetEnabled(string sourceId, bool enabled)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        if (enabled) _disabled.Remove(sourceId);
        else _disabled.Add(sourceId);
    }

    public bool IsEnabled(string sourceId) => !_disabled.Contains(sourceId);

    public ISidebarDataSource? Resolve(string sourceId, out SidebarContributionAvailability availability)
    {
        if (!_sources.TryGetValue(sourceId, out var source))
        {
            availability = SidebarContributionAvailability.Missing;
            return null;
        }
        if (_disabled.Contains(sourceId))
        {
            availability = SidebarContributionAvailability.Disabled;
            return null;
        }
        availability = SidebarContributionAvailability.Live;
        return source;
    }

    /// <summary>Every registered source, for lifecycle attach + the customizer's palette.</summary>
    public IEnumerable<ISidebarDataSource> All => _sources.Values;

    public SidebarSourceState StateOf(string sourceId)
        => _sources.TryGetValue(sourceId, out var s) ? s.State : SidebarSourceState.Error;
}

/// <summary>sectionId → slice, reused across rebuilds.</summary>
public sealed class SidebarExtensionSlices : ISidebarSectionSlices
{
    readonly Dictionary<string, SidebarSectionSlice> _slices = new(StringComparer.Ordinal);

    public int Count => _slices.Count;

    public void Clear() => _slices.Clear();

    public void Set(string sectionId, SidebarSectionSlice slice)
    {
        if (!string.IsNullOrEmpty(sectionId)) _slices[sectionId] = slice;
    }

    public bool TryGet(string sectionId, out SidebarSectionSlice slice) => _slices.TryGetValue(sectionId, out slice);

    /// <summary>The availability a surface shows as a badge/placeholder reason. Missing for an unknown section id.</summary>
    public SidebarContributionAvailability AvailabilityOf(string sectionId)
        => _slices.TryGetValue(sectionId, out var s) ? s.Availability : SidebarContributionAvailability.Missing;
}

/// <summary>The per-contribution LAST-GOOD snapshot — the stale-badge seam. First-party sources are always live, so
/// this only fires when a contributed source that HAD rows starts failing. Bounded: one list per contribution id,
/// each capped at <see cref="PerSourceCap"/> rows.</summary>
public sealed class SidebarContributionCache
{
    public const int PerSourceCap = 200;

    readonly Dictionary<string, List<SidebarLibraryEntry>> _snapshots = new(StringComparer.Ordinal);

    public int Count => _snapshots.Count;

    public bool Has(string sourceId) => _snapshots.TryGetValue(sourceId, out var s) && s.Count > 0;

    /// <summary>Copy <paramref name="count"/> rows starting at <paramref name="start"/> into this contribution's
    /// snapshot, replacing whatever was there.</summary>
    public void Store(string sourceId, IReadOnlyList<SidebarLibraryEntry> entries, int start, int count)
    {
        if (string.IsNullOrEmpty(sourceId) || count <= 0) return;
        if (start < 0 || start + count > entries.Count) return;
        if (!_snapshots.TryGetValue(sourceId, out var snap)) _snapshots[sourceId] = snap = new List<SidebarLibraryEntry>(count);
        snap.Clear();
        int n = count < PerSourceCap ? count : PerSourceCap;
        for (int i = 0; i < n; i++) snap.Add(entries[start + i]);
    }

    /// <summary>Append this contribution's snapshot to <paramref name="into"/>; returns how many rows were replayed.</summary>
    public int TryReplay(string sourceId, List<SidebarLibraryEntry> into)
    {
        if (string.IsNullOrEmpty(sourceId) || !_snapshots.TryGetValue(sourceId, out var snap) || snap.Count == 0) return 0;
        for (int i = 0; i < snap.Count; i++) into.Add(snap[i]);
        return snap.Count;
    }

    public void Forget(string sourceId) => _snapshots.Remove(sourceId);

    public void Clear() => _snapshots.Clear();
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

// ── SidebarSourceMap — the pure feed→entry mappers that survive the port ────────────────────────────────────────────
//
// TRIMMED from 0.2.9: `FromTrack`/`Tracks` (queue/now-playing rows), `NewReleases` and `FromEvent` needed
// `ArtistRef`/`Image`/`NewReleaseNotification`/`ConcertRoutes` — none confirmed in the 0.3 Entities surface this part
// owns — and are DROPPED (see the report) for the owner who ports the concrete Sidebar.Host.cs data sources, who reads
// the real Track/Album handle shapes directly. What ports is the shared glue every kept mapper needs, plus the two
// named decisions: an offline feed maps to Ready (empty, not broken), and an unresolved played context is emitted with
// an EMPTY Name — the surface's "render dimmed from the uri" signal.

/// <summary>The engine-free shape of one "recently played" row: the context the user pressed play on, or the track
/// itself when a play had no context.</summary>
public readonly record struct SidebarPlayedContext(string Uri, SidebarEntryKind Kind, long PlayedAtMs)
{
    public bool IsTrack => Kind == SidebarEntryKind.Track;
}

/// <summary>What a contributed FEED (new releases, concerts) has to say about itself. 0.2.9 spelled this
/// <c>NotificationFeedState</c> and it lived with the notification service; in 0.3 the feed seams belong to
/// <c>Sidebar.Host.cs</c>, so the vocabulary comes with them. Members and order are 0.2.9's.</summary>
public enum SidebarFeedState : byte { Idle, Loading, Populated, Empty, Offline, Error }

public static class SidebarSourceMap
{
    /// <summary>A service feed's state → the planner's source state. Offline maps to READY on purpose: an offline feed
    /// is EMPTY, not broken, and must render its empty caption rather than a permanent skeleton.</summary>
    public static SidebarSourceState FromFeedState(SidebarFeedState state) => state switch
    {
        SidebarFeedState.Idle or SidebarFeedState.Loading => SidebarSourceState.Pending,
        SidebarFeedState.Error => SidebarSourceState.Error,
        _ => SidebarSourceState.Ready,   // Populated / Empty / Offline
    };

    /// <summary>One track as a sidebar row. The queue and the now-playing feeds are the only sources that emit track
    /// rows, and they share this ONE builder so a queued track and the now-playing track cannot drift apart.</summary>
    /// <param name="t">The handle; the caller filters an invalid one out first.</param>
    /// <param name="order">Position within the feed — a track row carries no other order.</param>
    /// <param name="stampMs">The PLAY time for a played feed; 0 for a queue.</param>
    public static SidebarLibraryEntry FromTrack(Track t, int order, long stampMs = 0)
    {
        var artists = t.ArtistSlots;
        string uri = t.Uri.Text;
        return new SidebarLibraryEntry(uri, SidebarEntryKind.Track, uri,
            Entities.Strings.Resolve(t.TitleId), JoinTrackArtists(artists),
            t.ImageId, null,
            ChildCount: 0, AddedAtMs: 0,
            SortStamp: stampMs,
            LastVisitedTicksUtc: 0,
            SourceOrder: order, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        {
            FolderId = "", FolderName = "",
            FirstArtistName = artists.Length > 0 ? new Artist(artists[0]).Name : "",
        };
    }

    /// <summary>Append up to <paramref name="max"/> tracks. Deduped by uri — a queue legitimately repeats a track, but
    /// the sidebar's row KEY must stay unique or the reconciler collapses the two rows into one.</summary>
    public static int Tracks(IReadOnlyList<Track>? tracks, List<SidebarLibraryEntry> into, int max)
    {
        if (tracks is null || tracks.Count == 0 || max <= 0) return 0;
        int n = 0;
        for (int i = 0; i < tracks.Count && n < max; i++)
        {
            var t = tracks[i];
            if (!t.IsValid) continue;
            string uri = t.Uri.Text;
            if (uri.Length == 0 || ContainsId(into, uri)) continue;
            into.Add(FromTrack(t, n));
            n++;
        }
        return n;
    }

    // "A, B, C" capped at three, then "…" — the same shape SidebarProjection joins album artists with. One artist
    // returns the interned name with no allocation.
    static string JoinTrackArtists(ReadOnlySpan<int> artistSlots)
    {
        if (artistSlots.Length == 0) return "";
        if (artistSlots.Length == 1) return new Artist(artistSlots[0]).Name;
        var sb = new StringBuilder(48);
        int n = artistSlots.Length < 3 ? artistSlots.Length : 3;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(new Artist(artistSlots[i]).Name);
        }
        if (artistSlots.Length > n) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>The play log's context-first rows, resolved against the library projection. A context the projection
    /// knows becomes its real entry (art, creator, count) stamped with the PLAY time; one it does not know is still
    /// emitted — with an EMPTY Name, the surface's "unavailable, render dimmed from the uri" signal.</summary>
    /// <param name="contexts">Newest-first, already deduped.</param>
    /// <param name="byId">The projection's id/uri → entry index.</param>
    public static int Played(IReadOnlyList<SidebarPlayedContext>? contexts, SidebarSourceIndex byId,
                             List<SidebarLibraryEntry> into, int max)
    {
        if (contexts is null || contexts.Count == 0 || max <= 0) return 0;
        int n = 0;
        for (int i = 0; i < contexts.Count && n < max; i++)
        {
            var c = contexts[i];
            if (c.Uri.Length == 0) continue;

            if (c.IsTrack)
            {
                if (ContainsId(into, c.Uri)) continue;
                into.Add(new SidebarLibraryEntry(
                    c.Uri, SidebarEntryKind.Track, c.Uri, "", "", StringId.Empty, null,
                    ChildCount: 0, AddedAtMs: 0, SortStamp: c.PlayedAtMs, LastVisitedTicksUtc: 0,
                    SourceOrder: n, Depth: 0, Circular: false, Flavor: SidebarPlaylistFlavor.None)
                { FolderId = "", FolderName = "", FirstArtistName = "" });
                n++;
                continue;
            }

            string? id = SidebarPinId.FromUri(c.Uri);
            if (id is null || ContainsId(into, id)) continue;

            if (byId.TryGet(id, out var known))
            {
                into.Add(known with { SortStamp = c.PlayedAtMs, SourceOrder = n });
                n++;
                continue;
            }

            into.Add(new SidebarLibraryEntry(
                id, c.Kind, c.Uri, "", "", StringId.Empty, null,
                ChildCount: 0, AddedAtMs: 0, SortStamp: c.PlayedAtMs, LastVisitedTicksUtc: 0,
                SourceOrder: n, Depth: 0, Circular: c.Kind == SidebarEntryKind.Artist,
                Flavor: SidebarPlaylistFlavor.None)
            { FolderId = "", FolderName = "", FirstArtistName = "" });
            n++;
        }
        return n;
    }

    /// <summary>The navigation log's newest-first distinct route keys, resolved against the projection. Generic
    /// accessors keep the history row shape out of this layer. Pass STATIC lambdas.</summary>
    /// <param name="entriesOldestFirst">The history store's own order.</param>
    public static int Visited<T>(IReadOnlyList<T>? entriesOldestFirst, Func<T, string> keyOf, Func<T, long> ticksUtcOf,
                                 SidebarSourceIndex byId, List<SidebarLibraryEntry> into, int max)
    {
        if (entriesOldestFirst is null || entriesOldestFirst.Count == 0 || max <= 0) return 0;
        int n = 0;
        for (int i = entriesOldestFirst.Count - 1; i >= 0 && n < max; i--)
        {
            string key = keyOf(entriesOldestFirst[i]);
            if (string.IsNullOrEmpty(key) || ContainsId(into, key)) continue;   // newest wins — we walk backwards
            long ticks = ticksUtcOf(entriesOldestFirst[i]);

            if (byId.TryGet(key, out var known)) into.Add(known with { LastVisitedTicksUtc = ticks, SourceOrder = n });
            else into.Add(SidebarLibraryEntry.ForRoute(key, "", n, ticks));
            n++;
        }
        return n;
    }

    /// <summary>Linear duplicate check. O(n²) on purpose: every caller is a top-N feed (tens of rows at most).</summary>
    static bool ContainsId(List<SidebarLibraryEntry> list, string id)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i].Id, id, StringComparison.Ordinal)) return true;
        return false;
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

// ── SidebarDataSource — the contribution CONTRACT (ported near-verbatim) ────────────────────────────────────────────
//
// ONE interface for first-party and (later) third-party row producers. `wavee.library` registers through exactly the
// same call an external extension will use. Health is a plain property + a plain event (never a Signal<T>): the
// concrete adapters in Sidebar.Host.cs hold the engine-bound services and raise Changed on the UI thread.

public enum SidebarSourceItemType : byte { Entity = 0, Track = 1, Event = 2, Route = 3, Mixed = 4 }

[Flags]
public enum SidebarSourceFilters : byte
{
    None = 0,
    Kinds = 1,
    Qualifier = 2,
    Search = 4,
    IncludeExcludeUris = 8,
}

[Flags]
public enum SidebarSourceSorts : byte
{
    None = 0,
    SourceOrder = 1,
    Recents = 2,
    RecentlyAdded = 4,
    Alphabetical = 8,
    Creator = 16,
    CustomOrder = 32,
    All = SourceOrder | Recents | RecentlyAdded | Alphabetical | Creator | CustomOrder,
}

/// <summary>How much a source can be asked for at once. TopN = the whole (small) list every time; Paged honours
/// <see cref="SidebarSourceRequest.Page"/>.</summary>
public enum SidebarSourcePaging : byte { None = 0, TopN = 1, Paged = 2 }

/// <summary>The property-control families the customizer can generate from a schema. Deliberately semantic — never a
/// raw colour/pixel/duration.</summary>
public enum SidebarConfigFieldKind : byte { String = 0, Int = 1, Bool = 2, EntityUri = 3, Enum = 4, UriList = 5 }

/// <summary>One generated property control. <paramref name="LabelLocKey"/> is a loc KEY, never a literal.</summary>
public sealed record SidebarConfigField(
    string Key,
    SidebarConfigFieldKind Kind,
    string LabelLocKey,
    bool Required = false,
    string? DefaultJson = null,
    int Min = 0,
    int Max = 0,
    IReadOnlyList<string>? EnumValues = null);

/// <summary>A source's configuration schema. <paramref name="Version"/> is compared against the document's
/// <c>SchemaVersion</c>: a document authored by a NEWER schema resolves to Incompatible and keeps its spec.</summary>
public sealed record SidebarConfigSchema(int Version, IReadOnlyList<SidebarConfigField> Fields)
{
    public static readonly SidebarConfigSchema None = new(1, Array.Empty<SidebarConfigField>());

    public SidebarConfigField? Find(string key)
    {
        for (int i = 0; i < Fields.Count; i++)
            if (string.Equals(Fields[i].Key, key, StringComparison.Ordinal)) return Fields[i];
        return null;
    }
}

/// <summary>An OPAQUE section configuration with typed, never-throwing readers. A wrong-typed, absent or disposed
/// element yields the fallback — a hand-edited document must degrade, never crash the sidebar.</summary>
public readonly record struct SidebarSourceConfig(JsonElement Value)
{
    public static readonly SidebarSourceConfig Empty = default;

    public bool IsObject
    {
        get { try { return Value.ValueKind == JsonValueKind.Object; } catch (Exception) { return false; } }
    }

    public string? Str(string key, string? fallback = null)
    {
        if (!TryProp(key, out var p)) return fallback;
        try { return p.ValueKind == JsonValueKind.String ? p.GetString() : fallback; }
        catch (Exception) { return fallback; }
    }

    public int Int(string key, int fallback = 0)
    {
        if (!TryProp(key, out var p)) return fallback;
        try { return p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) ? v : fallback; }
        catch (Exception) { return fallback; }
    }

    public bool Bool(string key, bool fallback = false)
    {
        if (!TryProp(key, out var p)) return fallback;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    /// <summary>A string array property, appended into <paramref name="into"/>. Returns how many were appended.</summary>
    public int Strings(string key, List<string> into)
    {
        if (!TryProp(key, out var p)) return 0;
        try
        {
            if (p.ValueKind != JsonValueKind.Array) return 0;
            int n = 0;
            foreach (var item in p.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                if (item.GetString() is { Length: > 0 } s) { into.Add(s); n++; }
            }
            return n;
        }
        catch (Exception) { return 0; }
    }

    bool TryProp(string key, out JsonElement prop)
    {
        prop = default;
        try
        {
            if (Value.ValueKind != JsonValueKind.Object) return false;
            return Value.TryGetProperty(key, out prop);
        }
        catch (Exception) { return false; }   // a JsonElement whose document was disposed
    }
}

/// <summary>What the binder asks a source for. A POD passed by <c>in</c> — a fill is on the rebuild path, which
/// allocates nothing.</summary>
public readonly record struct SidebarSourceRequest(
    SidebarSourceConfig Config,
    int MaxItems = 0,
    string? Search = null,
    int Page = 0)
{
    public static readonly SidebarSourceRequest Default = new(SidebarSourceConfig.Empty);
}

/// <summary>Whether a section's contribution is LIVE, replayed from the last-good snapshot, or not served at all. The
/// planner turns the non-Live-non-Cached values into ONE actionable "Manage extension" prompt row.</summary>
public enum SidebarContributionAvailability : byte
{
    Live = 0,
    Cached = 1,
    Missing = 2,
    Disabled = 3,
    Incompatible = 4,
}

/// <summary>A registered sidebar row producer — the same call an external extension will use.</summary>
public interface ISidebarDataSource
{
    /// <summary>The namespaced stable id — <c>extensionId + "." + contributionId</c>.</summary>
    string Id { get; }

    SidebarConfigSchema ConfigSchema { get; }
    SidebarSourceItemType ItemType { get; }
    SidebarSourceFilters SupportedFilters { get; }
    SidebarSourceSorts SupportedSorts { get; }
    SidebarSourcePaging Paging { get; }

    /// <summary>The health signal, as a UI-thread property. The binder surfaces it verbatim as the planner's
    /// <see cref="SidebarSourceState"/>.</summary>
    SidebarSourceState State { get; }

    /// <summary>Why the source is not Ready, as a loc KEY (null when Ready or nothing useful to say).</summary>
    string? StateDetailLocKey { get; }

    /// <summary>True when the degraded state is ACTIONABLE rather than empty (Concerts with no location).</summary>
    bool NeedsPrompt { get; }

    /// <summary>Kick any warm/refresh this source needs. Idempotent, non-blocking, must never throw.</summary>
    void EnsureFresh(in SidebarSourceRequest request);

    /// <summary>APPEND this source's current rows to <paramref name="into"/> and return how many were appended. No
    /// LINQ, no closures, no per-row allocation, never a blocking wait.</summary>
    int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request);

    /// <summary>Raised on the UI thread after this source's rows or State changed.</summary>
    event Action? Changed;
}

/// <summary>Convenience base: the Changed plumbing, the health fields and sane declared capabilities.</summary>
public abstract class SidebarDataSourceBase : ISidebarDataSource
{
    protected SidebarDataSourceBase(string id) => Id = id;

    public string Id { get; }
    public virtual SidebarConfigSchema ConfigSchema => SidebarConfigSchema.None;
    public virtual SidebarSourceItemType ItemType => SidebarSourceItemType.Entity;
    public virtual SidebarSourceFilters SupportedFilters => SidebarSourceFilters.None;
    public virtual SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;
    public virtual SidebarSourcePaging Paging => SidebarSourcePaging.TopN;

    public SidebarSourceState State { get; protected set; } = SidebarSourceState.Ready;
    public string? StateDetailLocKey { get; protected set; }
    public bool NeedsPrompt { get; protected set; }

    public event Action? Changed;

    public virtual void EnsureFresh(in SidebarSourceRequest request) { }

    public abstract int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request);

    /// <summary>Publish a health change + notify. No-op when nothing moved, so a poll-shaped adapter cannot spin the
    /// binder.</summary>
    protected void SetHealth(SidebarSourceState state, string? detailLocKey = null, bool needsPrompt = false)
    {
        if (State == state && NeedsPrompt == needsPrompt
            && string.Equals(StateDetailLocKey, detailLocKey, StringComparison.Ordinal)) return;
        State = state;
        StateDetailLocKey = detailLocKey;
        NeedsPrompt = needsPrompt;
        Raise();
    }

    /// <summary>Publish a health change WITHOUT notifying — the only setter a <see cref="Fill"/> may use, so a
    /// per-section verdict never re-enters the binder mid-rebuild.</summary>
    protected void SetHealthQuiet(SidebarSourceState state, string? detailLocKey = null, bool needsPrompt = false)
    {
        State = state;
        StateDetailLocKey = detailLocKey;
        NeedsPrompt = needsPrompt;
    }

    /// <summary>Notify the binder that the ROWS changed (health unchanged). NEVER from <see cref="Fill"/>.</summary>
    protected void Raise() => Changed?.Invoke();
}

/// <summary>The host that resolves a contribution id to a source. An interface, not a delegate, so the availability
/// verdict travels WITH the lookup — "registered but disabled" and "never registered" are different rows.</summary>
public interface ISidebarContributionHost
{
    ISidebarDataSource? Resolve(string sourceId, out SidebarContributionAvailability availability);
}

/// <summary>The first-party contribution ids + the ONE place a source id is composed or split. New UI must never
/// switch on an extension id — it resolves through the host.</summary>
public static class SidebarContributions
{
    /// <summary>The trusted first-party extension id — first-party is literally an extension named "wavee".</summary>
    public const string WaveeExtensionId = "wavee";

    public const string Library = "wavee.library";
    public const string HistoryVisited = "wavee.history.visited";
    public const string HistoryPlayed = "wavee.history.played";
    public const string PlaylistTree = "wavee.playlistTree";
    public const string ArtistTopTracks = "wavee.artist.topTracks";
    public const string NewReleases = "wavee.newReleases";
    public const string Concerts = "wavee.concerts";
    public const string Queue = "wavee.queue";
    public const string NowPlaying = "wavee.nowPlaying";

    /// <summary>Every first-party source id, in registration order.</summary>
    public static readonly string[] FirstParty =
    [
        Library, HistoryVisited, HistoryPlayed, PlaylistTree, ArtistTopTracks,
        NewReleases, Concerts, Queue, NowPlaying,
    ];

    /// <summary><c>extensionId + "." + contributionId</c>. Empty when either half is missing. A
    /// <paramref name="contributionId"/> that is ALREADY fully qualified is taken as-is rather than double-prefixed.</summary>
    public static string SourceId(string? extensionId, string? contributionId)
    {
        if (string.IsNullOrEmpty(contributionId)) return "";
        if (string.IsNullOrEmpty(extensionId)) return "";
        if (contributionId!.Length > extensionId!.Length
            && contributionId[extensionId.Length] == '.'
            && contributionId.StartsWith(extensionId, StringComparison.Ordinal)) return contributionId;
        return extensionId + "." + contributionId;
    }

    /// <summary>The contribution half of a first-party source id ("library", "artist.topTracks", …).</summary>
    public static string ContributionOf(string sourceId)
    {
        int dot = sourceId.IndexOf('.');
        return dot < 0 || dot + 1 >= sourceId.Length ? "" : sourceId[(dot + 1)..];
    }

    public static bool IsFirstParty(string? sourceId)
    {
        if (sourceId is null) return false;
        for (int i = 0; i < FirstParty.Length; i++)
            if (string.Equals(FirstParty[i], sourceId, StringComparison.Ordinal)) return true;
        return false;
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
    /// <remarks>"recents" is the full recently-played page. It is offered by the CUSTOMIZER only and is deliberately
    /// NOT in the default top bar — a destination a user may add, not one the shell mandates.</remarks>
    public static readonly string[] PinnableRoutes =
        ["home", "search", "albums", "artists", "liked", "podcasts", "local", "history", "recents"];

    /// <summary>Real, durable pages that <see cref="FromRoute"/> accepts but that the curated picker deliberately
    /// does NOT seed — reachable from an artist page, not offered alongside Home and Search. Pinnable when REACHED,
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
        ["settings", "api-console", "sidebar-customize", "home-customize", "playback-diagnostics"];

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
        var u when EntityUri.Parse(u) is { Provider: EntityProvider.Spotify, Kind: EntityKind.Album } => AlbumPrefix + u,
        var u when EntityUri.Parse(u) is { Provider: EntityProvider.Spotify, Kind: EntityKind.Artist } => ArtistPrefix + u,
        var u when EntityUri.Parse(u) is { Provider: EntityProvider.Spotify, Kind: EntityKind.Show } => ShowPrefix + u,
        _ => null,                                                                      // tracks, episodes, everything else
    };

    /// <summary>Route key → pin id, and the app's one route RECOGNISER. Every durable application destination is
    /// stable enough to pin — the curated <see cref="PinnableRoutes"/> set, <see cref="AlsoPinnableRoutes"/>, and
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
    /// <summary>The wire uri Liked Songs pins as, inside the "pins" (ylpin) set — bare <c>spotify:collection</c>,
    /// NOT the <c>:tracks</c>-suffixed or user-namespaced forms the catalog/routing layer uses elsewhere.</summary>
    public const string LikedWireUri = "spotify:collection";

    /// <summary>pin id → the collection2v2 item uri, or null when this pin is local-only.</summary>
    public static string? TryWireUri(string? pinId, string username)
    {
        if (string.IsNullOrEmpty(pinId)) return null;
        if (string.Equals(pinId, "liked", StringComparison.Ordinal)) return LikedWireUri;
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
        return SidebarPinId.FromUri(wireUri);   // collapses every Liked spelling onto "liked"; refuses tracks/episodes
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
        if (!hasStore || string.IsNullOrEmpty(pinId)) return PinRowKind.None;
        return isPinned ? PinRowKind.Unpin : PinRowKind.Pin;
    }
}

// ── 8. the edit session as a value, and the pure rules over it ─────────────────────────────────────────────────────

// "Customize" is a MODE OVER THE LIVE PANE, not a page that redraws the sidebar. Everything the pane needs to know
// about that mode is this one POD record, handed to the renderer through a config delegate — a delegate, never a
// snapshot, because the config freezes at mount and a value member would pin frame 1's session forever. The one
// thing this section must NOT gain is a branch on `SidebarDesign`: only Curated supplies an edit session, but the
// rules below only ever see "there is a session" / "there is not".

/// <summary>
/// One live edit session, as a value. Read fresh from the pane config on every render.
///
/// <para><paramref name="ExpandedSection"/> — the ONE section whose real rows are revealed under its card (null =
/// every section is a card). One at a time on purpose: a 60-row expanded sidebar turns section dragging into a
/// scroll-fight, and a card-only plan has the uniform pitch reordering wants.</para>
///
/// <para><paramref name="ShowContents"/> — the companion page's "Show section contents" switch. True reveals every
/// (visible) section's body at once, for item-level work; it deliberately DISARMS section drag, because the card run
/// is then no longer contiguous (see <see cref="SidebarEditPlan.SectionsReorderable"/>).</para>
///
/// <para><paramref name="OptionsSection"/> — the section whose per-section options popover is open. Deliberately NOT
/// part of <see cref="SidebarEditPlan.Fold"/>: opening a popover changes nothing about the planned rows.</para>
/// </summary>
public readonly record struct SidebarEditState(
    string? ExpandedSection = null,
    bool ShowContents = false,
    string? OptionsSection = null);

/// <summary>
/// What a palette chip carries while it is being dragged onto the canvas — the whole <c>AddSection</c> argument list
/// minus the index, so the drop site does not have to know what a palette entry is. A record rather than a struct
/// because the drag payload travels as <c>object?</c> anyway, allocated once per gesture.
///
/// <para><paramref name="Label"/> is the already-localized name the drag chip shows, resolved at composition time:
/// the chip resolver runs inside the 0-alloc frame region while a drag is live, so it may look a string up but must
/// never build one.</para>
/// </summary>
public sealed record SidebarSectionDropPayload(
    SidebarSectionKind Kind,
    string Label,
    SidebarItemSpec? Item = null,
    SidebarExtensionRef? Extension = null);

/// <summary>The pure rules an edit session implies. Engine-free, so Wavee.Tests drives the real ones.</summary>
public static class SidebarEditPlan
{
    /// <summary>The drag KIND shared by the section-card reorder band and the companion palette's chips. ONE owner:
    /// the pane reads this const rather than re-spelling the literal, because a drag kind typed twice is a drop that
    /// silently accepts nothing.</summary>
    public const string SectionDragKind = "wavee.sidebar.section";

    /// <summary>Does this section reveal its real rows under its card?
    ///
    /// <para>A HIDDEN section never does, even while it is the expanded one: its body contributes nothing to the
    /// live sidebar, and drawing rows the user's own sidebar does not have would be the editor lying about the
    /// artifact it edits. The card itself stays — dimmed, with its eye-off badge — nothing vanishes into an
    /// invisible elsewhere.</para></summary>
    public static bool ShowsBody(in SidebarEditState edit, SidebarSectionSpec section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Hidden || !HasBody(section.Kind)) return false;
        if (edit.ShowContents) return true;
        return edit.ExpandedSection is { Length: > 0 } id
               && string.Equals(id, section.Id, StringComparison.Ordinal);
    }

    /// <summary>Can this kind reveal anything at all under its card? A Divider and a Header are pure chrome — the
    /// planner has no body arm for either — so their cards carry no disclosure mark and are not expandable.</summary>
    public static bool HasBody(SidebarSectionKind kind)
        => kind is not (SidebarSectionKind.Divider or SidebarSectionKind.Header);

    /// <summary>Is the section-card drag band armed for this session?
    ///
    /// <para>Only while EVERY section is a card. A reorderable band is one CONTIGUOUS run of plan rows at ONE
    /// uniform pitch; the moment a section expands, its body rows split the card run in two and the slot math would
    /// address body rows as if they were cards. Explicit Move up / Move down stay available from every card's "…"
    /// menu, so a section can always be reordered — drag is one of several ways, never the only way.</para></summary>
    public static bool SectionsReorderable(in SidebarEditState edit)
        => !edit.ShowContents && edit.ExpandedSection is not { Length: > 0 };

    /// <summary>Is this card the PINNED head — the materialised Shortcuts band (the top-bar sentinel)?
    ///
    /// <para>The sentinel is not in the document's <c>Sections</c>, so move/hide/duplicate/remove addressed at it
    /// are all rejections. Its card therefore carries no grip, no eye and no "…": an affordance that silently rejects
    /// is strictly worse than an affordance that is not offered. Its ITEMS are still fully editable — expanding the
    /// card reveals the real rows, whose reorder routes through the top-bar commands.</para></summary>
    public static bool IsPinnedCard(string? sectionId) => SidebarIds.IsTopBar(sectionId);

    /// <summary>The honest count a card may show beside its title, or -1 for "this section has no count worth
    /// claiming".
    ///
    /// <para>Deliberately NOT "how many rows would this section plan": that is only knowable by planning the body,
    /// and planning a 10,000-entry list once per card per re-plan to print a number would be a real cost for a
    /// decoration. A card counts what the DOCUMENT holds — a group's child sections, an authored item list's visible
    /// items — and a projected section (Pinned / PlaylistTree / EntityList / a feed / a contribution) shows nothing
    /// rather than a number it would have to guess.</para></summary>
    public static int CardCount(SidebarSectionSpec section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Kind == SidebarSectionKind.CustomGroup) return section.ChildList.Count;
        if (!SidebarSectionKinds.AcceptsItems(section.Kind)) return -1;
        // Pinned "items" are display OVERRIDES for pins made elsewhere, not the pin list — counting them would print
        // "0" over a band showing twelve pins.
        if (section.Kind == SidebarSectionKind.Pinned) return -1;

        var items = section.ItemList;
        int n = 0;
        for (int i = 0; i < items.Count; i++)
            if (!items[i].Hidden) n++;
        return n;
    }

    /// <summary>The session folded into one int for the pane's plan dep-key. <c>OptionsSection</c> is excluded — see
    /// the record's remarks.</summary>
    public static int Fold(in SidebarEditState? edit)
    {
        if (edit is not { } e) return 0;
        unchecked
        {
            int h = e.ShowContents ? 0x5f5f_0001 : 0x5f5f_0002;   // never 0: "no session" must not collide with "session"
            if (e.ExpandedSection is { Length: > 0 } id) h = h * 31 + StringComparer.Ordinal.GetHashCode(id);
            return h;
        }
    }

    /// <summary>Translate one committed section-card drag into the undoable <c>MoveSection</c> command, or null when
    /// there is nothing honest to dispatch.
    ///
    /// <para>Two index spaces meet here: BAND SLOTS enumerate the SectionCard rows of the plan (the document's
    /// top-level sections in order minus any kind this build does not understand and minus the pinned Shortcuts
    /// head, which the band never covers); <c>MoveSection.NewIndex</c> is an index into
    /// <paramref name="document"/><c>.Sections</c> interpreted AFTER the removal. The two are bridged through the
    /// NEIGHBOUR the drop landed above — the only translation that stays exact when a card is missing from the
    /// middle of the run.</para></summary>
    /// <param name="document">The PERSISTED document, never the render-path document the pane plans from: the
    /// latter carries the materialised Shortcuts section at index 0, so every index in it is one too high for a
    /// command the reducer will execute.</param>
    /// <param name="rows">The published plan rows.</param>
    /// <param name="bandStart">Plan index of band slot 0.</param>
    /// <param name="bandCount">Number of cards in the band.</param>
    /// <param name="from">The lifted card's band slot.</param>
    /// <param name="to">The committed band slot, post-removal.</param>
    public static SidebarCommand? ToMoveSection(SidebarCustomLayout? document, IReadOnlyList<SidebarRow>? rows,
                                                int bandStart, int bandCount, int from, int to)
    {
        if (document is null || rows is null) return null;
        if (bandCount <= 1 || from == to) return null;
        if ((uint)from >= (uint)bandCount || (uint)to >= (uint)bandCount) return null;

        string movingId = SectionIdAt(rows, bandStart, bandCount, from);
        if (movingId.Length == 0 || IsPinnedCard(movingId)) return null;   // the sentinel is not in `Sections`

        var moving = document.Locate(movingId);
        if (moving.Index < 0 || moving.Parent is not null) return null;    // a card is always a TOP-LEVEL section

        // The post-removal band holds bandCount-1 cards, so slot bandCount-1 is "append". Any other slot names the
        // card the moved one lands ABOVE; its ORIGINAL slot is shifted by one wherever the removal was above it.
        int successorSlot = to >= bandCount - 1 ? -1 : (to < from ? to : to + 1);

        int newIndex;
        if (successorSlot < 0)
        {
            newIndex = document.Sections.Count - 1;                        // post-removal tail
        }
        else
        {
            string successorId = SectionIdAt(rows, bandStart, bandCount, successorSlot);
            var successor = document.Locate(successorId);
            if (successor.Index < 0 || successor.Parent is not null) return null;
            newIndex = successor.Index > moving.Index ? successor.Index - 1 : successor.Index;
        }

        if (newIndex < 0 || newIndex == moving.Index) return null;         // a no-op is silence, not a rejection
        return new MoveSection(movingId, null, newIndex);
    }

    /// <summary>Translate one palette chip dropped ON a section card into the undoable <c>AddSection</c>, or null
    /// when there is nothing honest to dispatch.
    ///
    /// <para>The drop convention is "insert BEFORE the card you aimed at" — the same neighbour-bridging discipline
    /// <see cref="ToMoveSection"/> uses: the canvas enumerates CARDS (top-level sections this build understands,
    /// plus the materialised Shortcuts head) while <c>AddSection.Index</c> is an index into
    /// <paramref name="document"/><c>.Sections</c>.</para>
    ///
    /// <para>The pinned Shortcuts head is not in <c>Sections</c>, so a drop on it resolves to index 0 — "above
    /// everything the reducer can address". A null/blank <paramref name="beforeSectionId"/> means "no card under the
    /// pointer" and APPENDS.</para></summary>
    /// <param name="document">The PERSISTED document, never the render-path document: the latter carries the
    /// materialised Shortcuts section at index 0, so every index in it is one too high.</param>
    public static SidebarCommand? ToAddSection(SidebarCustomLayout? document, string? beforeSectionId,
                                               SidebarSectionDropPayload? payload)
    {
        if (document is null || payload is null) return null;
        if (!SidebarSectionKinds.IsKnown(payload.Kind)) return null;

        int index = document.Sections.Count;                       // no card under the pointer ⇒ append
        if (beforeSectionId is { Length: > 0 } id && !IsPinnedCard(id))
        {
            var at = document.Locate(id);
            // A child card is not a top-level slot; refusing is better than silently filing the new section
            // somewhere the cue never pointed.
            if (at.Index < 0 || at.Parent is not null) return null;
            index = at.Index;
        }
        else if (beforeSectionId is { Length: > 0 })
        {
            index = 0;                                             // the Shortcuts head: above every addressable section
        }

        return new AddSection(payload.Kind, index, ParentId: null, Item: payload.Item, Extension: payload.Extension);
    }

    /// <summary>The section id at a band slot, or "" when the slot is out of the plan.</summary>
    public static string SectionIdAt(IReadOnlyList<SidebarRow>? rows, int bandStart, int bandCount, int slot)
    {
        if (rows is null || (uint)slot >= (uint)bandCount) return "";
        int index = bandStart + slot;
        if ((uint)index >= (uint)rows.Count) return "";
        var row = rows[index];
        return row.Kind == SidebarRowKind.SectionCard ? row.SectionId : "";
    }
}
// ── CUSTOMIZER PURE TABLES: palette, query-panel shape, number editor, display projection, config rewriter ────────────

// The customizer page's PURE model: the searchable section palette (including the Destinations group), the query-panel
// shape a section kind owns, the discrete-number editor's normalization rule, the display-option projection the
// generated property controls bind, and the opaque extension-config rewriter. Engine-free by construction — the page
// (stage 2) renders FROM these tables; it never grows a second copy of a kind switch or a config writer.

/// <summary>The query controls a section kind owns, so the property panel never grows a second, untested kind switch.</summary>
public readonly record struct SidebarQueryPanelShape(bool ShowKinds, bool ShowQualifier)
{
    public static SidebarQueryPanelShape For(SidebarSectionKind kind, bool qualifiersAvailable) => kind switch
    {
        SidebarSectionKind.PlaylistTree => new(false, true),
        SidebarSectionKind.EntityList => new(true, qualifiersAvailable),
        _ => new(false, false),
    };
}

/// <summary>The discrete-number editor's one normalization rule. The UI uses the returned integer both for dispatch and
/// for rejection snap-back.</summary>
public static class SidebarNumberEdit
{
    public static int Normalize(double value, int min, int max)
        => Math.Clamp((int)Math.Round(value), min, max);
}

// ── the palette (grouped + searchable; Destinations included) ──────────────────────────────────────────────────────

/// <summary>The palette's groups. <see cref="Destinations"/> is APPENDED (7) rather than inserted at 0 even though it
/// renders FIRST: the numeric order is only this enum's storage — <see cref="SidebarPalette.Groups"/> is the single
/// authority on render order, and renumbering the six that shipped would silently rewrite every existing entry's
/// group.</summary>
public enum SidebarPaletteGroup : byte
{
    Navigation = 0, Library = 1, Playback = 2, DynamicFeeds = 3, Layout = 4, Actions = 5, Extensions = 6,

    /// <summary>Real app pages, so typing "home" answers with <b>Home</b> instead of "Links — shortcuts to pages like
    /// Home or Search". Entries are generated from <c>SidebarPinId.PinnableRoutes</c> plus the extra destinations that
    /// are reachable but not pinnable; their labels resolve from the route key at the UI edge, so they can never
    /// disagree with the tab strip or the breadcrumb.</summary>
    Destinations = 7,
}

/// <summary>What the palette ADDS when clicked. Kept as data (not a switch in a render) so the palette, its search
/// filter and the tests all read one table.</summary>
public enum SidebarPaletteAdd : byte
{
    /// <summary>A plain <c>AddSection(Kind)</c>.</summary>
    Section = 0,
    /// <summary>An <c>AddSection(JumpBackIn)</c> that then flips its recents source to the play log.</summary>
    RecentlyPlayed = 1,
    /// <summary>An <c>AddSection(Extension, Extension: ref)</c> for <see cref="SidebarPaletteEntry.ContributionId"/>.</summary>
    Contribution = 2,
    /// <summary>The action picker, then <c>AddSection(StaticLinks, Item: the bound action item)</c> — ONE undo step.</summary>
    ActionShortcut = 3,
    /// <summary>The contribution picker (every registered source), then <see cref="Contribution"/>.</summary>
    AnyContribution = 4,
    /// <summary>A pre-seeded StaticLinks section containing the localized Liked Songs route.</summary>
    LikedSongsShortcut = 5,

    /// <summary>An app PAGE: one undoable <c>AddSection(StaticLinks, Item: the route)</c>. Appends into a StaticLinks
    /// subject instead of minting a sibling (<see cref="SidebarPalette.AppendsToSelection"/>).</summary>
    Destination = 6,

    /// <summary>DEFECT 7 — a bare Links section that opens the destination picker immediately, so it is never left
    /// with zero items.</summary>
    LinksWithPicker = 7,
}

/// <summary>One palette row. <paramref name="IconName"/> is a GLYPH NAME (this table is engine-free — the app-side
/// palette view maps it). <paramref name="RouteKey"/> is set only on <see cref="SidebarPaletteGroup.Destinations"/>
/// rows, which carry NO name loc key on purpose — a destination's label is owned by the route table at the UI edge
/// (the tab strip, the breadcrumb, the pinned rows); minting a second spelling here is the drift the single-owner
/// rule exists to catch.</summary>
public sealed record SidebarPaletteEntry(
    string Id,
    SidebarPaletteGroup Group,
    SidebarSectionKind Kind,
    SidebarPaletteAdd Add,
    string NameLocKey,
    string DescriptionLocKey,
    string IconName,
    string? ContributionId = null,
    string? RouteKey = null);

/// <summary>The palette table + its pure search filter.</summary>
public static class SidebarPalette
{
    /// <summary>The SECTION half of the palette, in group order: 4 Navigation, 3 Library, 3 Playback, 4 Dynamic feeds,
    /// 3 Layout, 1 Actions, 1 Extensions — 19 rows. "Queue" and "Now Playing" are two distinct first-party
    /// contributions (<c>wavee.queue</c>, <c>wavee.nowPlaying</c>) with their own loc keys, so they are two rows, not
    /// one.</summary>
    public static readonly SidebarPaletteEntry[] Sections =
    [
        // Navigation
        new("pinned", SidebarPaletteGroup.Navigation, SidebarSectionKind.Pinned, SidebarPaletteAdd.Section,
            "sidebar.section.pinned", "sidebar.section.pinnedSub", "Pin"),
        new("shortcuts", SidebarPaletteGroup.Navigation, SidebarSectionKind.CollectionShortcuts,
            SidebarPaletteAdd.Section, "sidebar.section.shortcuts", "sidebar.section.shortcutsSub", "Heart"),
        new("likedSongs", SidebarPaletteGroup.Navigation, SidebarSectionKind.StaticLinks,
            SidebarPaletteAdd.LikedSongsShortcut, "nav.likedSongs", "sidebar.customizer.likedSongsSub", "Heart"),
        // DEFECT 7 — a bare "Links" section used to add a zero-item section that plans as one generic grey hint, and
        // adding it twice gave two identical dead rows. It now opens the destination picker on the way in.
        new("staticLinks", SidebarPaletteGroup.Navigation, SidebarSectionKind.StaticLinks,
            SidebarPaletteAdd.LinksWithPicker, "sidebar.section.staticLinks", "sidebar.section.staticLinksSub", "Link"),

        // Library
        new("playlistTree", SidebarPaletteGroup.Library, SidebarSectionKind.PlaylistTree, SidebarPaletteAdd.Section,
            "sidebar.section.playlistTree", "sidebar.section.playlistTreeSub", "Folder"),
        new("entityList", SidebarPaletteGroup.Library, SidebarSectionKind.EntityList, SidebarPaletteAdd.Section,
            "sidebar.section.entityList", "sidebar.section.entityListSub", "Filter"),
        new("entityEmbed", SidebarPaletteGroup.Library, SidebarSectionKind.EntityEmbed, SidebarPaletteAdd.Section,
            "sidebar.section.entityEmbed", "sidebar.section.entityEmbedSub", "FavoriteStar"),

        // Playback
        new("recentlyPlayed", SidebarPaletteGroup.Playback, SidebarSectionKind.JumpBackIn,
            SidebarPaletteAdd.RecentlyPlayed, "sidebar.section.recentlyPlayed", "sidebar.section.recentlyPlayedSub",
            "Headphones"),
        new("queue", SidebarPaletteGroup.Playback, SidebarSectionKind.Extension, SidebarPaletteAdd.Contribution,
            "sidebar.section.queue", "sidebar.section.queueSub", "Queue", SidebarContributions.Queue),
        new("nowPlaying", SidebarPaletteGroup.Playback, SidebarSectionKind.Extension, SidebarPaletteAdd.Contribution,
            "sidebar.section.nowPlaying", "sidebar.section.nowPlayingSub", "Play", SidebarContributions.NowPlaying),

        // Dynamic feeds
        new("jumpBackIn", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.JumpBackIn, SidebarPaletteAdd.Section,
            "sidebar.section.jumpBackIn", "sidebar.section.jumpBackInSub", "Clock"),
        new("artistTopTracks", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.Extension,
            SidebarPaletteAdd.Contribution, "sidebar.section.artistTopTracks", "sidebar.section.artistTopTracksSub",
            "Contact", SidebarContributions.ArtistTopTracks),
        new("newReleases", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.NewReleases, SidebarPaletteAdd.Section,
            "sidebar.section.newReleases", "sidebar.section.newReleasesSub", "Album"),
        new("concerts", SidebarPaletteGroup.DynamicFeeds, SidebarSectionKind.Concerts, SidebarPaletteAdd.Section,
            "sidebar.section.concerts", "sidebar.section.concertsSub", "Calendar"),

        // Layout
        new("group", SidebarPaletteGroup.Layout, SidebarSectionKind.CustomGroup, SidebarPaletteAdd.Section,
            "sidebar.section.group", "sidebar.section.groupSub", "Grid"),
        new("header", SidebarPaletteGroup.Layout, SidebarSectionKind.Header, SidebarPaletteAdd.Section,
            "sidebar.section.header", "sidebar.section.headerSub", "Font"),
        new("divider", SidebarPaletteGroup.Layout, SidebarSectionKind.Divider, SidebarPaletteAdd.Section,
            "sidebar.section.divider", "sidebar.section.dividerSub", "Remove"),

        // Actions
        new("actionShortcut", SidebarPaletteGroup.Actions, SidebarSectionKind.StaticLinks,
            SidebarPaletteAdd.ActionShortcut, "sidebar.customizer.itemAction", "sidebar.customizer.itemActionSub",
            "RefineSparkle"),

        // Extensions
        new("extension", SidebarPaletteGroup.Extensions, SidebarSectionKind.Extension,
            SidebarPaletteAdd.AnyContribution, "sidebar.section.extension", "sidebar.section.extensionSub", "Code"),
    ];

    /// <summary>Real destinations NOT in <c>SidebarPinId.PinnableRoutes</c>: settings is refused by
    /// <c>SidebarPinId.FromRoute</c> as a tooling surface; the concerts hub is pinnable
    /// (<c>SidebarPinId.AlsoPinnableRoutes</c>) but absent from the curated PIN picker on purpose.
    ///
    /// <para>DECLARED ABOVE <see cref="Destinations"/> ON PURPOSE: C# runs static field initializers in TEXTUAL order,
    /// so declaring this below the field that reads it would leave it null inside <c>BuildDestinations</c> and ship an
    /// empty Destinations group.</para></summary>
    static readonly string[] ExtraDestinationRoutes = ["settings", ConcertsRoute];

    /// <summary>Literal, not a shared constant — this table is source-included by <c>Wavee.Tests</c>, which cannot see
    /// the engine-bound owner of the concerts route.</summary>
    const string ConcertsRoute = "concerts";

    /// <summary><c>SidebarPinId.PinnableRoutes</c> ∪ <see cref="ExtraDestinationRoutes"/> — 11 rows, always; there is
    /// no developer-mode gate on this table. Every entry is <c>StaticLinks</c> +
    /// <see cref="SidebarPaletteAdd.Destination"/>, one undo step; no icon override — a route row resolves its glyph
    /// from the route table at the row site.</summary>
    public static readonly SidebarPaletteEntry[] Destinations = BuildDestinations();

    static SidebarPaletteEntry[] BuildDestinations()
    {
        var routes = SidebarPinId.PinnableRoutes;
        var extra = ExtraDestinationRoutes;
        var into = new SidebarPaletteEntry[routes.Length + extra.Length];
        for (int i = 0; i < routes.Length; i++) into[i] = Destination(routes[i]);
        for (int i = 0; i < extra.Length; i++) into[routes.Length + i] = Destination(extra[i]);
        return into;
    }

    /// <summary>One destination row. The name key is EMPTY — the label resolves from the route at the UI edge; the
    /// description is one shared string for the whole group.</summary>
    static SidebarPaletteEntry Destination(string routeKey) => new(
        "dest:" + routeKey, SidebarPaletteGroup.Destinations, SidebarSectionKind.StaticLinks,
        SidebarPaletteAdd.Destination, "", DestinationSubLocKey, "Link", ContributionId: null, RouteKey: routeKey);

    /// <summary>The one description every destination row shares.</summary>
    public const string DestinationSubLocKey = "sidebar.customizer.destinationSub";

    /// <summary>The WHOLE palette: destinations first, then the section kinds. One array, so <see cref="Filter"/>, the
    /// grouping loop and the tests all read one table.</summary>
    public static readonly SidebarPaletteEntry[] All = Concat(Destinations, Sections);

    static SidebarPaletteEntry[] Concat(SidebarPaletteEntry[] a, SidebarPaletteEntry[] b)
    {
        var into = new SidebarPaletteEntry[a.Length + b.Length];
        Array.Copy(a, into, a.Length);
        Array.Copy(b, 0, into, a.Length, b.Length);
        return into;
    }

    /// <summary>Render order. DESTINATIONS FIRST: a user who types (or scrolls looking for) "home" meets the page
    /// before they meet the abstraction that could hold it. The remaining six keep the order they shipped in.</summary>
    public static readonly SidebarPaletteGroup[] Groups =
    [
        SidebarPaletteGroup.Destinations,
        SidebarPaletteGroup.Navigation, SidebarPaletteGroup.Library, SidebarPaletteGroup.Playback,
        SidebarPaletteGroup.DynamicFeeds, SidebarPaletteGroup.Layout, SidebarPaletteGroup.Actions,
        SidebarPaletteGroup.Extensions,
    ];

    /// <summary>The palette entry that NAMES a contribution id, or null. Looking it up here means the pick list says
    /// "Queue" where a first-party name is known and falls back to the raw id exactly once where it is not.</summary>
    public static SidebarPaletteEntry? EntryForContribution(string? contributionId)
    {
        if (contributionId is not { Length: > 0 }) return null;
        for (int i = 0; i < Sections.Length; i++)
        {
            var e = Sections[i];
            if (e.ContributionId is { Length: > 0 } id
                && string.Equals(id, contributionId, StringComparison.Ordinal)) return e;
        }
        return null;
    }

    /// <summary>Can this entry be DRAGGED onto the canvas? A drag must resolve to ONE <c>AddSection</c> at the drop
    /// position: the two entries that open a modal first, the contribution-picker entry, and "Recently played"
    /// (deliberately TWO commands) cannot, so they stay click-only rather than lying about the outcome.</summary>
    public static bool CanDrag(SidebarPaletteAdd add) => add is SidebarPaletteAdd.Section
        or SidebarPaletteAdd.Contribution or SidebarPaletteAdd.LikedSongsShortcut or SidebarPaletteAdd.Destination;

    /// <summary>Does clicking this entry APPEND to the selected section instead of creating a sibling? Only a
    /// destination does, and only into a <c>StaticLinks</c> section — anywhere else would be a
    /// <c>KindDoesNotAcceptItems</c> rejection dressed up as a feature.</summary>
    public static bool AppendsToSelection(SidebarPaletteEntry? entry, SidebarSectionSpec? selected)
        => entry is { Add: SidebarPaletteAdd.Destination, RouteKey.Length: > 0 }
           && selected is { Kind: SidebarSectionKind.StaticLinks };

    public static string GroupLocKey(SidebarPaletteGroup group) => group switch
    {
        SidebarPaletteGroup.Destinations => "sidebar.palette.destinations",
        SidebarPaletteGroup.Navigation => "sidebar.palette.navigation",
        SidebarPaletteGroup.Library => "sidebar.palette.library",
        SidebarPaletteGroup.Playback => "sidebar.palette.playback",
        SidebarPaletteGroup.DynamicFeeds => "sidebar.palette.dynamic",
        SidebarPaletteGroup.Layout => "sidebar.palette.layout",
        SidebarPaletteGroup.Actions => "sidebar.palette.actions",
        _ => "sidebar.palette.extensions",
    };

    /// <summary>Trim + lowercase, "" for nothing typed. Normalized ONCE per keystroke, never per row.</summary>
    public static string NormalizeQuery(string? query)
        => string.IsNullOrWhiteSpace(query) ? "" : query!.Trim().ToLowerInvariant();

    /// <summary>Token-wise contains: EVERY whitespace-separated token of the (already normalized) query must appear in
    /// the label or the description, so "top art" finds "Artist top tracks". An empty query matches everything.</summary>
    public static bool Matches(string normalizedQuery, string? label, string? description)
    {
        if (normalizedQuery.Length == 0) return true;
        int i = 0;
        while (i < normalizedQuery.Length)
        {
            while (i < normalizedQuery.Length && normalizedQuery[i] == ' ') i++;
            int start = i;
            while (i < normalizedQuery.Length && normalizedQuery[i] != ' ') i++;
            if (i == start) break;
            var token = normalizedQuery.AsSpan(start, i - start);
            bool hit = Contains(label, token) || Contains(description, token);
            if (!hit) return false;
        }
        return true;

        static bool Contains(string? haystack, ReadOnlySpan<char> token)
            => haystack is { Length: > 0 } && haystack.AsSpan().Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Append the entries matching <paramref name="query"/> (in table order) to <paramref name="into"/>; the
    /// two projections are delegates because the localized strings live at the UI edge. Every one of the 11
    /// destination rows is offered — there is no developer-mode gate in this build. Returns how many were
    /// appended.</summary>
    public static int Filter(string? query, Func<SidebarPaletteEntry, string> labelOf,
                            Func<SidebarPaletteEntry, string?>? descriptionOf, List<SidebarPaletteEntry> into)
    {
        ArgumentNullException.ThrowIfNull(labelOf);
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        string q = NormalizeQuery(query);
        for (int i = 0; i < All.Length; i++)
        {
            var e = All[i];
            if (!Matches(q, labelOf(e), descriptionOf?.Invoke(e))) continue;
            into.Add(e);
        }
        return into.Count;
    }
}

// ── display options: the property panel's row order + the int projection its generated controls bind ────────────────

/// <summary>The display-option half of the property panel, kept pure so the panel is a RENDERER of this table rather
/// than a hand-written per-kind form (the drift <c>SidebarSectionKinds.AllowsDisplayField</c> exists to prevent).</summary>
public static class SidebarDisplayValues
{
    /// <summary>Row order in the property panel. Which of these a KIND actually shows is
    /// <c>SidebarSectionKinds.AllowsDisplayField</c>'s answer — never a second table.</summary>
    public static readonly SidebarDisplayField[] Order =
    [
        SidebarDisplayField.Density,
        SidebarDisplayField.Presentation,
        SidebarDisplayField.GridColumns,
        SidebarDisplayField.Artwork,
        SidebarDisplayField.Subtitles,
        SidebarDisplayField.CountBadges,
        SidebarDisplayField.InlineControls,
        SidebarDisplayField.PlayButton,
        SidebarDisplayField.RecentsSource,
        SidebarDisplayField.MaxItems,
        SidebarDisplayField.EmptyBehavior,
        SidebarDisplayField.CollapsedByDefault,
        SidebarDisplayField.ShowInRail,
    ];

    /// <summary>The field's current value as the int <c>SetDisplayOption</c> carries (bools encode 0/1) — the exact
    /// inverse of <c>SidebarLayoutReducer.WithField</c>.</summary>
    public static int Read(SidebarDisplayOptions? options, SidebarDisplayField field)
    {
        var o = options ?? SidebarDisplayOptions.Default;
        return field switch
        {
            SidebarDisplayField.Density => (int)o.Density,
            SidebarDisplayField.Presentation => (int)o.Presentation,
            SidebarDisplayField.Artwork => o.Artwork ? 1 : 0,
            SidebarDisplayField.Subtitles => o.Subtitles ? 1 : 0,
            SidebarDisplayField.CountBadges => o.CountBadges ? 1 : 0,
            SidebarDisplayField.CollapsedByDefault => o.CollapsedByDefault ? 1 : 0,
            SidebarDisplayField.ShowInRail => o.ShowInRail ? 1 : 0,
            SidebarDisplayField.MaxItems => o.MaxItems,
            SidebarDisplayField.GridColumns => o.GridColumns,
            SidebarDisplayField.InlineControls => o.InlineControls ? 1 : 0,
            SidebarDisplayField.PlayButton => o.PlayButton ? 1 : 0,
            SidebarDisplayField.RecentsSource => (int)o.Recents,
            SidebarDisplayField.EmptyBehavior => (int)o.EmptyBehavior,
            _ => 0,
        };
    }

    /// <summary>True for the fields the panel renders as a toggle (everything that encodes 0/1).</summary>
    public static bool IsFlag(SidebarDisplayField field) => field is SidebarDisplayField.Artwork
        or SidebarDisplayField.Subtitles or SidebarDisplayField.CountBadges
        or SidebarDisplayField.CollapsedByDefault or SidebarDisplayField.ShowInRail
        or SidebarDisplayField.InlineControls or SidebarDisplayField.PlayButton;

    /// <summary>The row's label loc key (the catalog's <c>sidebar.option.*</c> family).</summary>
    public static string LabelLocKey(SidebarDisplayField field) => field switch
    {
        SidebarDisplayField.Density => "sidebar.option.density",
        SidebarDisplayField.Presentation => "sidebar.option.presentation",
        SidebarDisplayField.Artwork => "sidebar.option.artwork",
        SidebarDisplayField.Subtitles => "sidebar.option.subtitles",
        SidebarDisplayField.CountBadges => "sidebar.option.countBadges",
        SidebarDisplayField.CollapsedByDefault => "sidebar.option.collapsedByDefault",
        SidebarDisplayField.ShowInRail => "sidebar.option.showInRail",
        SidebarDisplayField.MaxItems => "sidebar.option.maxItems",
        SidebarDisplayField.GridColumns => "sidebar.option.gridColumns",
        SidebarDisplayField.InlineControls => "sidebar.a11y.sortView",          // the inline filter/sort row
        SidebarDisplayField.PlayButton => "detail.play",
        SidebarDisplayField.RecentsSource => "sidebar.option.sortRecents",
        SidebarDisplayField.EmptyBehavior => "sidebar.option.emptyBehavior",
        _ => "",
    };

    /// <summary>The choice labels for the ENUM fields (empty for flags and numbers). The ORDER here is load-bearing:
    /// index i must be enum value i, which <c>DisplayValues_EveryFieldRoundTripsEveryChoiceThePanelCanOffer</c>
    /// pins.</summary>
    public static string[] ChoiceLocKeys(SidebarDisplayField field) => field switch
    {
        SidebarDisplayField.Density =>
            ["sidebar.option.densityCompact", "sidebar.option.densityCozy", "sidebar.option.densityComfortable"],
        SidebarDisplayField.Presentation =>
            ["sidebar.option.presentationList", "sidebar.option.presentationGrid"],
        SidebarDisplayField.RecentsSource =>
            ["sidebar.recents.sourceVisited", "sidebar.recents.sourcePlayed"],
        SidebarDisplayField.EmptyBehavior =>
            [
                "sidebar.option.emptyDefault", "sidebar.option.emptyHide",
                "sidebar.option.emptyCompact", "sidebar.option.emptyAction",
            ],
        _ => Array.Empty<string>(),
    };
}

// ── opaque extension config: the writer behind the schema-generated property controls ─────────────────────────────────

/// <summary>Rewrites an <c>SidebarExtensionRef.Config</c> object one field at a time — the ONE place the customizer
/// turns a generated control's value back into JSON. Every write COPIES the untouched members through verbatim, so a
/// config member this build's schema does not know survives an edit. Never throws: a non-object config degrades to
/// just the edited member. Uses <see cref="Utf8JsonWriter"/>/<see cref="JsonElement"/> directly — no reflection-based
/// serialization.</summary>
public static class SidebarConfigJson
{
    /// <summary>The config a freshly added contributed section starts from: <c>{}</c> plus every schema field that
    /// declares a <c>DefaultJson</c>, so a queue/top-tracks section is bounded before the inspector is touched.</summary>
    public static JsonElement Defaults(SidebarConfigSchema? schema)
    {
        if (schema is null || schema.Fields.Count == 0) return SidebarJson.EmptyObject;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            var fields = schema.Fields;
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                if (f.DefaultJson is not { Length: > 0 } raw) continue;
                if (!TryWriteRaw(w, f.Key, raw)) continue;
            }
            w.WriteEndObject();
        }
        return Parse(buffer);
    }

    public static JsonElement WithString(JsonElement config, string key, string? value)
        => Rewrite(config, key, value is null ? null : w => w.WriteStringValue(value));

    public static JsonElement WithInt(JsonElement config, string key, int value)
        => Rewrite(config, key, w => w.WriteNumberValue(value));

    public static JsonElement WithBool(JsonElement config, string key, bool value)
        => Rewrite(config, key, w => w.WriteBooleanValue(value));

    /// <summary>A string array (the <c>UriList</c> field kind). A null/empty list REMOVES the member rather than
    /// storing <c>[]</c> — the same "empty normalizes to absent" rule the query's uri sets follow.</summary>
    public static JsonElement WithStrings(JsonElement config, string key, IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return Rewrite(config, key, null);
        return Rewrite(config, key, w =>
        {
            w.WriteStartArray();
            for (int i = 0; i < values.Count; i++)
            {
                string one = values[i]?.Trim() ?? "";
                if (one.Length == 0) continue;
                w.WriteStringValue(one);
            }
            w.WriteEndArray();
        });
    }

    /// <summary>Copy every member except <paramref name="key"/>, then write <paramref name="write"/> under it (null
    /// <paramref name="write"/> = remove the member).</summary>
    public static JsonElement Rewrite(JsonElement config, string key, Action<Utf8JsonWriter>? write)
    {
        if (string.IsNullOrEmpty(key)) return SidebarJson.Own(config);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            try
            {
                if (config.ValueKind == JsonValueKind.Object)
                    foreach (var prop in config.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, key, StringComparison.Ordinal)) continue;
                        prop.WriteTo(w);
                    }
            }
            catch (Exception) { /* a disposed/mangled element degrades to "just the edited member" */ }

            if (write is not null)
            {
                w.WritePropertyName(key);
                write(w);
            }
            w.WriteEndObject();
        }
        return Parse(buffer);
    }

    static bool TryWriteRaw(Utf8JsonWriter w, string key, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            w.WritePropertyName(key);
            doc.RootElement.WriteTo(w);
            return true;
        }
        catch (Exception) { return false; }
    }

    static JsonElement Parse(System.Buffers.ArrayBufferWriter<byte> buffer)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }
        catch (Exception) { return SidebarJson.EmptyObject; }
    }
}
