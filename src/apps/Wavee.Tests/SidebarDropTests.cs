// ── Wavee.Tests/SidebarDropTests.cs — the rootlist drop / tree-nav / selection authority, ported from 0.2.9 ───────
//
// Ports the 0.2.9 sidebar organisation suite onto Sidebar.cs's own DROP, TREE NAV, FOLDER FLYOUT AND
// MULTI-SELECTION block (L2655+): the pure geometry behind a rootlist drag (`RootlistSlotResolver`), the
// line/plate cue invariant (`SidebarDropCue`), the ONE legality authority every surface shares
// (`RootlistOps.CheckMove`/`CheckMoves`, `RootlistDropDecision`), the Undo anchor (`RootlistUndoAnchors`), the
// non-mouse organisation verbs (`RootlistTreeNav`, `RootlistSelection`, `RootlistBatchOrder`,
// `SidebarTreeNavLayout`), the folder flyout's drill-in stack (`SidebarFolderTree`, `SidebarFolderFlyoutNav`), the
// tree's multi-selection (`SidebarTreeSelection`), the mid-drag freeze (`SidebarStageHold<T>`), the reorder-clamp
// displacement offset (`SidebarReorderClamp`), and the row's navbar-customization extras (`SidebarNavLayout`).
// Pure, engine-free: no `TestScope.Fresh()`, no `[Collection(EntitiesCollection.Name)]`, no engine loop — matching
// what every type tested here already promises ("Engine-free... so a test can drive the real ... directly").
//
// An earlier pass of this port DROPPED every fact above that touched a type Sidebar.cs declared WITHOUT `public`
// (`SidebarTreeSelection`, `SidebarFolderFlyoutNav`, `SidebarFolderTree`, `SidebarReorderClamp`,
// `SidebarStageHold<T>`, the queue-row `SidebarNavLayout`, `RootlistDropDecision`, `RootlistSlotMapper`,
// `SidebarRowGeometry`) — this assembly reaches Sidebar.cs only via an ordinary `ProjectReference` and the solution
// carries no `InternalsVisibleTo` anywhere, so none of them were reachable from `Wavee.Tests`, contradicting
// Sidebar.cs's own file header ("source-visible to `Wavee.Tests`"). That was confirmed to be an artifact of how the
// file was assembled, not a design decision, and has since been fixed upstream — every type above is now `public`,
// and every fact that was dropped purely for that reason has been restored below.
//
// STILL NOT ported, and why (this remains correct, not an oversight): `RootlistTreeBuilderTests` (filed under
// 0.2.9's RootlistTreeTests.cs) tested a different subsystem entirely — the recursive PlaylistFolder/PlaylistLeaf
// marker-stream builder — which 0.3 does not carry in Sidebar.cs at all (the projection here consumes an
// already-flattened SidebarLibraryEntry list). And 0.3's `RootlistOps` deliberately no longer builds or applies a
// move ("No op is ever built or posted here" — its own doc, right above `CheckMove`), so the 0.2.9
// `TryBuildMove`/`TryBuildMoves`/`PlaylistDiffApplier` write seam, and every 0.2.9 fact that asserted the resulting
// rootlist ORDER after a drop (most of `RootlistDropScenarioTests.cs`, half of `RootlistSlotToOpTests.cs`), has no
// reachable 0.3 equivalent — inventing one would test something Sidebar.cs does not do. `SidebarDragClampTests.cs`'s
// `LibraryV3View`/`SidebarRailDropRules` facts also stay out: those types live in `Sidebar.Modes.cs`/
// `Platform/Drag.cs`, not Sidebar.cs, and are another file's gate.

using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

// ── the shared tree fixture (ported from SidebarTreeFixture.cs) ────────────────────────────────────────────────────
//
// The depth-first FLATTENED shape `SidebarProjectionInput.PlaylistTree` publishes, and the marker stream every rule
// in `RootlistTreeNav`/`RootlistOps`/`RootlistUndoAnchors` decides against. 0.2.9 built the marker stream via
// `Wavee.Backend.Playlists.RootlistTreeBuilder.EntriesFromUris`; that builder is not compiled into 0.3 (parked
// under src/apps/_old), so `MarkersOf` below constructs the `RootlistEntry` rows directly — still balanced
// start/end-group markers in the exact "spotify:start-group:id:name" shape `RootlistOps`'s own (private) `GroupId`
// parses, so the fixture cannot encode a marker shape `RootlistOps` would not itself produce.
//
//   a                       depth 0
//   [Chill]      folder g   depth 0
//       b                   depth 1
//       c                   depth 1
//       [Deep]   folder k   depth 1
//           f               depth 2
//   d                       depth 0
//   [Trailing]   folder h   depth 0
//       e                   depth 1
//
// Deliberately awkward in the two places 0.2.9's defects lived: a folder NESTED inside a folder (so "my siblings"
// cannot be "the rows at my depth"), and a TRAILING folder at the end of the top level (so "after everything" must
// land after its end marker rather than inside it).
static class SidebarTreeFixture
{
    public const string PlaylistUriPrefix = "spotify:playlist:";

    /// <summary>A playlist row. The id is the projection's own (<c>pl:</c> + uri), which is what every verb addresses.</summary>
    public static SidebarLibraryEntry Playlist(string slug, int depth, string parentId = "", string parentName = "")
        => new(Id: SidebarPinId.PlaylistPrefix + PlaylistUriPrefix + slug, Kind: SidebarEntryKind.Playlist,
               Uri: PlaylistUriPrefix + slug, Name: slug, Creator: "", Cover: default, MosaicTiles: null,
               ChildCount: 0, AddedAtMs: 0, SortStamp: 0, LastVisitedTicksUtc: 0, SourceOrder: 0,
               Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { ParentFolderId = parentId, ParentFolderName = parentName };

    /// <summary>A folder row. <c>FolderId</c> is its OWN group id; <c>ParentFolderId</c> is the folder containing it.</summary>
    public static SidebarLibraryEntry Folder(string groupId, string name, int depth,
                                             string parentId = "", string parentName = "")
        => new(Id: SidebarPinId.FolderPrefix + groupId, Kind: SidebarEntryKind.Folder, Uri: "", Name: name,
               Creator: "", Cover: default, MosaicTiles: null, ChildCount: 0, AddedAtMs: 0, SortStamp: 0,
               LastVisitedTicksUtc: 0, SourceOrder: 0, Depth: depth, Circular: false, Flavor: SidebarPlaylistFlavor.None)
        { FolderId = groupId, FolderName = name, ParentFolderId = parentId, ParentFolderName = parentName };

    public static IReadOnlyList<SidebarLibraryEntry> Tree() =>
    [
        Playlist("a", 0),
        Folder("g", "Chill", 0),
        Playlist("b", 1, "g", "Chill"),
        Playlist("c", 1, "g", "Chill"),
        Folder("k", "Deep", 1, "g", "Chill"),
        Playlist("f", 2, "k", "Deep"),
        Playlist("d", 0),
        Folder("h", "Trailing", 0),
        Playlist("e", 1, "h", "Trailing"),
    ];

    public static IReadOnlyList<RootlistEntry> Markers() => MarkersOf(Tree());

    /// <summary>The marker stream a flattened fixture tree stands for, built directly: each row first closes every
    /// open folder whose depth is not shallower than its own, then opens or emits itself; whatever is still open at
    /// the end closes in reverse (innermost first) — the same nesting <c>RootlistOps</c>'s range walk expects.</summary>
    public static IReadOnlyList<RootlistEntry> MarkersOf(IReadOnlyList<SidebarLibraryEntry> tree)
    {
        var list = new List<RootlistEntry>(tree.Count * 2);
        var open = new List<SidebarLibraryEntry>();
        foreach (var e in tree)
        {
            while (open.Count > 0 && open[^1].Depth >= e.Depth)
            {
                var top = open[^1];
                list.Add(new RootlistEntry(list.Count, 2, "spotify:end-group:" + top.FolderId, null, top.Depth));
                open.RemoveAt(open.Count - 1);
            }
            if (e.IsFolder)
            {
                list.Add(new RootlistEntry(list.Count, 1, "spotify:start-group:" + e.FolderId + ":" + e.Name,
                                           e.Name, e.Depth));
                open.Add(e);
            }
            else
            {
                list.Add(new RootlistEntry(list.Count, 0, e.Uri, null, e.Depth));
            }
        }
        for (int i = open.Count - 1; i >= 0; i--)
            list.Add(new RootlistEntry(list.Count, 2, "spotify:end-group:" + open[i].FolderId, null, open[i].Depth));
        return list;
    }

    /// <summary>The seam ref one fixture row moves AS — a folder by group id, a playlist by uri.</summary>
    public static RootlistItemRef Ref(IReadOnlyList<SidebarLibraryEntry> tree, string entryId)
    {
        foreach (var e in tree)
            if (e.Id == entryId) return RootlistTreeNav.RefOf(in e);
        return new RootlistItemRef("", false);
    }

    /// <summary>The entry id of a playlist row, as the verbs address it.</summary>
    public static string Pl(string slug) => SidebarPinId.PlaylistPrefix + PlaylistUriPrefix + slug;

    /// <summary>The entry id of a folder row.</summary>
    public static string Fo(string groupId) => SidebarPinId.FolderPrefix + groupId;
}

/// <summary><c>SidebarRowGeometry</c>'s ladder constants this suite needs, reproduced as literals: the class is
/// declared <c>internal</c> in Sidebar.cs (no <c>public</c> modifier) and this assembly carries no
/// <c>InternalsVisibleTo</c>, so its own values are not reachable from here — see the porting report's MISMATCH
/// note. Kept in one place so every region that needs the ladder cites the same numbers.</summary>
static class RowGeometryLiteral
{
    /// <summary>TreeContentX(0) = IndentFor(0) + LeadingLaneWidth = 4 + 9.</summary>
    public const float TreeContentX0 = 13f;
    /// <summary>== IndentStep.</summary>
    public const float TreeGuideStep = 12f;
    public const float RowInsetRight = 8f;
    public const float TreeEndHeight = 24f;

    public static float TreeContentX(int depth) => TreeContentX0 + depth * TreeGuideStep;
}

// ── RootlistOps: the ONE legality authority every surface shares ───────────────────────────────────────────────────
//
// Ported from RootlistRefusalTests.cs's marker-stream section (including "TheTableIsTotal", restored now that
// RootlistDropDecision is public) and RootlistSlotToOpTests.cs's CheckMove-only facts. Still DROPPED from both:
// every fact that observed the resulting rootlist ORDER after a move (RootlistOps.TryBuildMove +
// PlaylistDiffApplier.Apply) — 0.3's RootlistOps deliberately never builds or applies an op ("No op is ever built or
// posted here"), so there is no writer to observe through Sidebar.cs — including "AFolderMovesItsWholeSubtree_
// MarkersAndAll" (asserts a TryBuildMove op's FromIndex/Length — no such method exists in 0.3).
public class RootlistOpsTests
{
    static string A => SidebarTreeFixture.Pl("a");
    static string B => SidebarTreeFixture.Pl("b");
    static string C => SidebarTreeFixture.Pl("c");
    static string D => SidebarTreeFixture.Pl("d");
    static string G => SidebarTreeFixture.Fo("g");
    static string K => SidebarTreeFixture.Fo("k");

    static RootlistMoveCheck Check(string sourceId, string targetId, RootlistDropPlacement placement)
    {
        var tree = SidebarTreeFixture.Tree();
        return RootlistOps.CheckMove(SidebarTreeFixture.Markers(),
            SidebarTreeFixture.Ref(tree, sourceId), SidebarTreeFixture.Ref(tree, targetId), placement);
    }

    [Fact]
    public void NoOp_RefusesBothEdgesOfTheSpanTheItemAlreadyOccupies()
    {
        // "Before the row right after me" and "after the row right before me" are the same place I am already in.
        Assert.Equal(RootlistMoveCheck.NoOp, Check(B, C, RootlistDropPlacement.Before));
        Assert.Equal(RootlistMoveCheck.NoOp, Check(C, B, RootlistDropPlacement.After));
        // Onto itself, either way round, is SameItem rather than NoOp — a different sentence for a different cause.
        Assert.Equal(RootlistMoveCheck.SameItem, Check(B, B, RootlistDropPlacement.Before));
        Assert.Equal(RootlistMoveCheck.SameItem, Check(B, B, RootlistDropPlacement.After));
        // The folder's LAST child, appended back into that same folder.
        Assert.Equal(RootlistMoveCheck.NoOp, Check(K, G, RootlistDropPlacement.Inside));
    }

    [Fact]
    public void RealMoves_AreNotRefused()
    {
        Assert.Equal(RootlistMoveCheck.Ok, Check(A, C, RootlistDropPlacement.After));
        Assert.Equal(RootlistMoveCheck.Ok, Check(B, G, RootlistDropPlacement.After));
        Assert.Equal(RootlistMoveCheck.Ok, Check(A, G, RootlistDropPlacement.Inside));
        Assert.Equal(RootlistMoveCheck.Ok, Check(D, A, RootlistDropPlacement.Before));
        // Filing a NON-last child into its own folder is a real move (it becomes the last one) — not the no-op a
        // flattened-list check (mapping Inside to the folder's END index) would mistake it for.
        Assert.Equal(RootlistMoveCheck.Ok, Check(B, G, RootlistDropPlacement.Inside));
    }

    [Fact]
    public void Cycle_RefusesAFolderFiledIntoItsOwnSubtree()
    {
        Assert.Equal(RootlistMoveCheck.Cycle, Check(G, B, RootlistDropPlacement.Before));
        Assert.Equal(RootlistMoveCheck.Cycle, Check(G, B, RootlistDropPlacement.After));
        Assert.Equal(RootlistMoveCheck.Cycle, Check(G, K, RootlistDropPlacement.Inside));
        // Into the dragged folder ITSELF is the identity answer, distinct from filing into one of its descendants.
        Assert.Equal(RootlistMoveCheck.SameItem, Check(G, G, RootlistDropPlacement.Inside));
    }

    [Fact]
    public void AnUnknownRowIsRefusedRatherThanArmed()
    {
        // The OPPOSITE of a flattened-list guess: a destination this stream cannot find has no index, so Missing is
        // the honest answer rather than "the tree may not be showing it, so allow it".
        Assert.Equal(RootlistMoveCheck.Missing, Check("pl:missing", A, RootlistDropPlacement.After));
        Assert.Equal(RootlistMoveCheck.Missing, Check(A, "pl:missing", RootlistDropPlacement.After));
    }

    [Fact]
    public void InsideALeaf_IsNotAPlacementTheStreamCanExpress()
    {
        Assert.Equal(RootlistMoveCheck.Invalid, Check(A, D, RootlistDropPlacement.Inside));
    }

    [Fact]
    public void CheckMoves_OfAnEmptyBatch_IsANoOp()
    {
        Assert.Equal(RootlistMoveCheck.NoOp, RootlistOps.CheckMoves(SidebarTreeFixture.Markers(), []));
    }

    [Fact]
    public void TheTableIsTotal_AndMapsEveryCheckToExactlyOneRefusal()
    {
        // Every value of the ONE table, so a new RootlistMoveCheck cannot land silently in the default arm without
        // this failing first. Restored now that RootlistDropDecision is public.
        Assert.Equal(SidebarDropRefusal.None, RootlistDropDecision.RefusalFor(RootlistMoveCheck.Ok));
        Assert.Equal(SidebarDropRefusal.NoOp, RootlistDropDecision.RefusalFor(RootlistMoveCheck.NoOp));
        Assert.Equal(SidebarDropRefusal.IntoDescendant, RootlistDropDecision.RefusalFor(RootlistMoveCheck.Cycle));
        Assert.Equal(SidebarDropRefusal.Self, RootlistDropDecision.RefusalFor(RootlistMoveCheck.SameItem));
        Assert.Equal(SidebarDropRefusal.Unavailable, RootlistDropDecision.RefusalFor(RootlistMoveCheck.Missing));
        Assert.Equal(SidebarDropRefusal.Unavailable, RootlistDropDecision.RefusalFor(RootlistMoveCheck.Invalid));
    }
}

// ── RootlistSlotResolver: the pure geometry + legality behind every sidebar rootlist drop ──────────────────────────
//
// Ported from RootlistSlotResolverTests.cs (geometry/depth/undo-anchor sections) and RootlistRefusalTests.cs's
// resolver-level refusal facts (Self/IntoItself/SortedList/NotLoaded/Unavailable — these read only the resolver's
// own SidebarRowFacts flags, no marker stream involved, so nothing here needed dropping).
public class RootlistSlotResolverTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The three canonical row heights the density ladder produces (32 compact / 44 cozy+subtitle / 48
    /// comfortable+subtitle). The edge band must behave identically at all three.</summary>
    public static TheoryData<float> Heights => new() { 32f, 44f, 48f };

    static SidebarRowFacts Leaf(int depth = 0, int nextDepth = -1, bool centerAccepts = false) => new(
        IsFolder: false, FolderExpanded: false, FolderHasChildren: false,
        Depth: depth, NextVisibleDepth: nextDepth < 0 ? depth : nextDepth,
        CenterAccepts: centerAccepts,
        SourceIsSelf: false, SortedNonCustom: false, RootlistLoaded: true);

    static SidebarRowFacts FolderRow(bool expanded, bool hasChildren, int depth = 0, int nextDepth = -1) => new(
        IsFolder: true, FolderExpanded: expanded, FolderHasChildren: hasChildren,
        Depth: depth, NextVisibleDepth: nextDepth < 0 ? depth : nextDepth,
        CenterAccepts: true,
        SourceIsSelf: false, SortedNonCustom: false, RootlistLoaded: true);

    static SidebarRowFacts Row(bool folder = false, bool self = false, bool sorted = false, bool loaded = true) => new(
        IsFolder: folder, FolderExpanded: false, FolderHasChildren: false,
        Depth: 0, NextVisibleDepth: 0, CenterAccepts: folder,
        SourceIsSelf: self, SortedNonCustom: sorted, RootlistLoaded: loaded);

    /// <summary>Resolve with the pointer parked over the row's LABEL (x far past the indent ladder) — the default
    /// position, and therefore the one that must mean "stay at this row's depth".</summary>
    static SidebarDropSlot At(float t, in SidebarRowFacts f, float h = 44f, float x = 200f)
        => RootlistSlotResolver.Resolve(3, t, x, h, in f, SidebarDropSlot.None);

    // ── the edge band ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Heights))]
    public void EdgeBand_IsThirtyPercentClampedBetweenTenAndSixteen(float h)
    {
        float edge = RootlistSlotResolver.EdgeFor(h);
        Assert.Equal(Math.Clamp(h * 0.30f, 10f, 16f), edge, 3);
        // It must always leave a centre: two bands can never consume the whole row.
        Assert.True(edge * 2f < h);
    }

    [Fact]
    public void EdgeBand_DependsOnTheRowAlone_NeverOnThePayload()
    {
        // The band is a function of the row height and nothing else — there is no payload parameter to pass.
        Assert.Equal(13.2f, RootlistSlotResolver.EdgeFor(44f), 3);
        // A pathologically short row still leaves a centre: never more than half of it.
        Assert.Equal(2f, RootlistSlotResolver.EdgeFor(4f), 3);
        Assert.Equal(RootlistSlotResolver.MinEdge, RootlistSlotResolver.EdgeFor(0f), 3);
    }

    // ── zones ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Heights))]
    public void PlainRow_IsTwoZones_WithNoDeadCentre(float h)
    {
        var f = Leaf();
        Assert.Equal(SidebarDropKind.Before, At(0.49f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.After, At(0.51f, in f, h).Kind);
        // A row that cannot take a deposit must not reserve half its height for one.
        Assert.NotEqual(SidebarDropKind.Into, At(0.5f, in f, h).Kind);
    }

    [Theory]
    [MemberData(nameof(Heights))]
    public void EditablePlaylistWithTrackPayload_IsThreeZones_CentreDeposits(float h)
    {
        var f = Leaf(centerAccepts: true);
        float edge = RootlistSlotResolver.EdgeFor(h) / h;
        Assert.Equal(SidebarDropKind.Before, At(edge * 0.5f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.Into, At(0.5f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.After, At(1f - edge * 0.5f, in f, h).Kind);
    }

    [Theory]
    [MemberData(nameof(Heights))]
    public void CollapsedFolder_TopIsBefore_CentreIsInto_BottomIsAfter(float h)
    {
        var f = FolderRow(expanded: false, hasChildren: true);
        Assert.Equal(SidebarDropKind.Before, At(0.01f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.Into, At(0.5f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.After, At(0.99f, in f, h).Kind);
    }

    [Theory]
    [MemberData(nameof(Heights))]
    public void ExpandedFolderWithChildren_BottomBand_IsTheFirstChildSlot(float h)
    {
        // The precise "make it the folder's first item" gesture: the line indents one step and the drop lands
        // ahead of the current first child.
        var f = FolderRow(expanded: true, hasChildren: true, depth: 1, nextDepth: 2);
        var slot = At(0.99f, in f, h);
        Assert.Equal(SidebarDropKind.Before, slot.Kind);
        Assert.Equal(2, slot.Depth);
    }

    [Fact]
    public void ExpandedFolderWithNoChildren_BottomBand_IsAfterTheFolder()
    {
        var f = FolderRow(expanded: true, hasChildren: false, depth: 1, nextDepth: 0);
        var slot = At(0.99f, in f, x: 200f);
        Assert.Equal(SidebarDropKind.After, slot.Kind);
        Assert.Equal(1, slot.Depth);   // the pointer is over the label ⇒ stay at this row's depth
    }

    [Fact]
    public void DroppingOnAFolder_AlwaysMeansIntoIt()
    {
        // Explorer / Finder / VS Code / Spotify all agree.
        foreach (var expanded in new[] { true, false })
        foreach (var children in new[] { true, false })
            Assert.Equal(SidebarDropKind.Into, At(0.5f, FolderRow(expanded, children)).Kind);
    }

    // ── depth ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DepthRange_IsAmbiguousOnlyAfterAFoldersLastVisibleChild()
    {
        var depths = new[] { 0, 1, 1, 2, 0, 0, 0 };
        var folders = new[] { true, false, true, false, true, true, false };
        for (int i = 0; i < depths.Length; i++)
        {
            int next = i + 1 < depths.Length ? depths[i + 1] : 0;
            var f = folders[i]
                ? FolderRow(expanded: true, hasChildren: next > depths[i], depth: depths[i], nextDepth: next)
                : Leaf(depths[i], next);
            var (min, max) = RootlistSlotResolver.DepthRange(in f);
            Assert.Equal(depths[i], max);
            Assert.Equal(Math.Min(next, depths[i]), min);
            // Ambiguous ⇔ the next visible row is SHALLOWER than this one — this row closes one or more folders.
            Assert.Equal(next < depths[i], min < max);
        }
    }

    [Fact]
    public void DepthRange_ClampsAFolderHeaderToItsOwnDepth()
    {
        var f = FolderRow(expanded: true, hasChildren: true, depth: 0, nextDepth: 1);
        Assert.Equal((0, 0), RootlistSlotResolver.DepthRange(in f));
    }

    [Fact]
    public void DepthPick_DefaultsToTheRowsOwnDepth()
    {
        var f = Leaf(depth: 2, nextDepth: 0);
        Assert.Equal(2, At(0.9f, in f, x: 200f).Depth);
    }

    [Theory]
    [InlineData(13f, 0)]     // TreeContentX(0) — where a depth-0 row starts drawing
    [InlineData(25f, 1)]     // TreeContentX(1)
    [InlineData(37f, 2)]     // TreeContentX(2): the row's own depth
    [InlineData(999f, 2)]    // past the ladder: clamped to Max
    [InlineData(-50f, 0)]    // before the row: clamped to Min
    public void DepthPick_ReadsTheTreeContentLadderFromPointerX(float x, int expected)
    {
        var f = Leaf(depth: 2, nextDepth: 0);
        var slot = At(0.9f, in f, x: x);
        Assert.Equal(SidebarDropKind.After, slot.Kind);
        Assert.Equal(expected, slot.Depth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DepthPick_TheOutdentBandIsReachable(int depth)
    {
        // Parked on the row's own content origin the pick is that depth; half a step plus 5 DIP to the LEFT of it —
        // a deliberate slide, still inside the row — it is one shallower. depth 1: 31 → 20. depth 2: 43 → 32.
        var f = Leaf(depth: depth, nextDepth: 0);
        float here = RowGeometryLiteral.TreeContentX(depth);
        float outdent = here - RowGeometryLiteral.TreeGuideStep / 2f - 5f;
        Assert.Equal(depth, At(0.9f, in f, x: here).Depth);
        Assert.Equal(depth - 1, At(0.9f, in f, x: outdent).Depth);
    }

    [Fact]
    public void DepthPick_HoldsThePreviousDepthInsideTheHysteresisBand()
    {
        var f = Leaf(depth: 2, nextDepth: 0);
        var previous = new SidebarDropSlot(3, SidebarDropKind.After, 1, SidebarDropRefusal.None);
        // The 1→2 boundary sits at TreeContentX(1) + 0.5·TreeGuideStep = 31. Inside 4 DIP of it the previous holds…
        Assert.Equal(1, RootlistSlotResolver.Resolve(3, 0.9f, 32f, 44f, in f, in previous).Depth);
        // …and past it the pick commits.
        Assert.Equal(2, RootlistSlotResolver.Resolve(3, 0.9f, 37f, 44f, in f, in previous).Depth);
        // With no previous slot there is nothing to hold: the raw pick wins.
        Assert.Equal(2, RootlistSlotResolver.Resolve(3, 0.9f, 32f, 44f, in f, SidebarDropSlot.None).Depth);
    }

    [Fact]
    public void DepthPick_IsInertWhenTheSlotIsUnambiguous()
    {
        var f = Leaf(depth: 1, nextDepth: 1);
        foreach (float x in new[] { -10f, 0f, 25f, 37f, 300f })
            Assert.Equal(1, At(0.9f, in f, x: x).Depth);
    }

    [Fact]
    public void DepthPick_IsNotReadForBeforeOrInto()
    {
        var leaf = Leaf(depth: 2, nextDepth: 0);
        Assert.Equal(2, At(0.1f, in leaf, x: 0f).Depth);                 // Before stays at the row's depth
        var folder = FolderRow(expanded: false, hasChildren: true, depth: 2, nextDepth: 0);
        Assert.Equal(2, At(0.5f, in folder, x: 0f).Depth);               // Into is the row itself
    }

    // ── the tree's end marker ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TreeEndRow_IsOneWholeRowSlotAtDepthZero()
    {
        var f = Leaf() with { IsListEnd = true };
        foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            var slot = RootlistSlotResolver.Resolve(9, t, 200f, RowGeometryLiteral.TreeEndHeight, in f,
                                                    SidebarDropSlot.None);
            Assert.Equal(SidebarDropKind.EndOfList, slot.Kind);
            Assert.Equal(0, slot.Depth);
        }
    }

    // ── degenerate geometry ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1, 0.5f, 44f)]      // no plan row
    [InlineData(3, 0.5f, 0f)]        // no measured extent
    [InlineData(3, float.NaN, 44f)]  // no viewport ⇒ no resolvable t
    public void DegenerateGeometry_RefusesWithAReason_NeverGuesses(int planIndex, float t, float h)
    {
        var slot = RootlistSlotResolver.Resolve(planIndex, t, 100f, h, Leaf(), SidebarDropSlot.None);
        Assert.Equal(SidebarDropKind.None, slot.Kind);
        Assert.Equal(SidebarDropRefusal.Unavailable, slot.Refusal);
    }

    // ── refusal, decided from the resolver's OWN row facts (no marker stream needed) ──────────────────────────────────

    [Fact]
    public void Self_RefusesTheRowTheDragCameFrom()
    {
        var slot = At(0.1f, Row(self: true));
        Assert.Equal(SidebarDropRefusal.Self, slot.Refusal);
        Assert.Equal(SidebarDropKind.None, slot.Kind);
    }

    [Fact]
    public void IntoItself_IsItsOwnSentence_ForAFoldersCentre()
    {
        // "Can't move a folder into itself" names what the user tried; the generic "can't move here" would leave
        // them guessing which rule they hit.
        Assert.Equal(SidebarDropRefusal.IntoItself, At(0.5f, Row(folder: true, self: true)).Refusal);
        // The same folder's EDGES are the ordinary self refusal — the user is aiming at an ordering, not the folder.
        Assert.Equal(SidebarDropRefusal.Self, At(0.02f, Row(folder: true, self: true)).Refusal);
    }

    [Fact]
    public void SortedList_RefusesOrderingsOnly_IntoStaysLegal()
    {
        // A non-custom sort cannot SHOW a positional insert, so an ordering is refused — but a deposit into a
        // folder or a playlist needs no position at all.
        Assert.Equal(SidebarDropRefusal.SortedList, At(0.1f, Row(sorted: true)).Refusal);
        Assert.Equal(SidebarDropRefusal.SortedList, At(0.9f, Row(sorted: true)).Refusal);
        var into = At(0.5f, Row(folder: true, sorted: true));
        Assert.Equal(SidebarDropRefusal.None, into.Refusal);
        Assert.Equal(SidebarDropKind.Into, into.Kind);

        var end = RootlistSlotResolver.Resolve(2, 0.5f, 200f, 24f,
            Row(sorted: true) with { IsListEnd = true }, SidebarDropSlot.None);
        Assert.Equal(SidebarDropRefusal.SortedList, end.Refusal);
    }

    [Fact]
    public void NotLoaded_RefusesEverything_BeforeAnyOtherRule()
    {
        // Checked FIRST, so it is the reason the user sees even when another rule would also apply.
        Assert.Equal(SidebarDropRefusal.NotLoaded, At(0.5f, Row(folder: true, self: true, loaded: false)).Refusal);
        Assert.Equal(SidebarDropRefusal.NotLoaded, At(0.1f, Row(loaded: false)).Refusal);
    }

    [Fact]
    public void Unavailable_IsTheDegenerateGeometryRefusal()
    {
        Assert.Equal(SidebarDropRefusal.Unavailable,
            RootlistSlotResolver.Resolve(-1, 0.5f, 200f, 44f, Row(), SidebarDropSlot.None).Refusal);
    }

    // ── the undo anchor ─────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Pl(string slug, int depth = 0, string parent = "")
        => new("pl:spotify:playlist:" + slug, SidebarEntryKind.Playlist, "spotify:playlist:" + slug, slug, "",
               default, null, 0, 0, 1, 0, 0, depth, false, SidebarPlaylistFlavor.None) { ParentFolderId = parent };

    static SidebarLibraryEntry Fold(string id, int depth = 0, string parent = "")
        => new("folder:" + id, SidebarEntryKind.Folder, "", id, "", default, null, 0, 0, 1, 0, 0, depth, false,
               SidebarPlaylistFlavor.None) { FolderId = id, ParentFolderId = parent };

    static List<SidebarLibraryEntry> UndoTree() =>
    [
        Pl("a"),
        Fold("g"),
        Pl("b", 1, "g"),
        Pl("c", 1, "g"),
        Pl("d"),
    ];

    [Fact]
    public void UndoAnchor_IsThePreviousSiblingWhereverOneExists()
    {
        Assert.True(RootlistUndoAnchors.TryResolve(UndoTree(), "pl:spotify:playlist:c", out var anchor, out var placement));
        Assert.Equal(new RootlistItemRef("spotify:playlist:b", false), anchor);
        Assert.Equal(RootlistDropPlacement.After, placement);

        // A folder's previous sibling is the folder itself, addressed by group id — never one of its children.
        Assert.True(RootlistUndoAnchors.TryResolve(UndoTree(), "pl:spotify:playlist:d", out var afterFolder, out _));
        Assert.Equal(new RootlistItemRef("g", true), afterFolder);
    }

    [Fact]
    public void UndoAnchor_FallsToTheNextSiblingForTheFirstChild()
    {
        Assert.True(RootlistUndoAnchors.TryResolve(UndoTree(), "pl:spotify:playlist:b", out var anchor, out var placement));
        Assert.Equal(new RootlistItemRef("spotify:playlist:c", false), anchor);
        Assert.Equal(RootlistDropPlacement.Before, placement);

        // Top level, first: the next TOP-LEVEL sibling, which is the folder (its whole subtree is skipped).
        Assert.True(RootlistUndoAnchors.TryResolve(UndoTree(), "pl:spotify:playlist:a", out var first, out var before));
        Assert.Equal(new RootlistItemRef("g", true), first);
        Assert.Equal(RootlistDropPlacement.Before, before);
    }

    [Fact]
    public void UndoAnchor_IsTheParentFolderForAnOnlyChild_AndAbsentWhenThereIsNothingToAnchorAgainst()
    {
        List<SidebarLibraryEntry> only = [Fold("g"), Pl("b", 1, "g")];
        Assert.True(RootlistUndoAnchors.TryResolve(only, "pl:spotify:playlist:b", out var anchor, out var placement));
        Assert.Equal(new RootlistItemRef("g", true), anchor);
        Assert.Equal(RootlistDropPlacement.Inside, placement);

        // The only top-level item: there is no move to undo, and inventing one would land it somewhere else.
        List<SidebarLibraryEntry> lone = [Pl("a")];
        Assert.False(RootlistUndoAnchors.TryResolve(lone, "pl:spotify:playlist:a", out _, out _));
        Assert.False(RootlistUndoAnchors.TryResolve(UndoTree(), "pl:spotify:playlist:missing", out _, out _));
        Assert.False(RootlistUndoAnchors.TryResolve(null, "pl:spotify:playlist:a", out _, out _));
    }

    // ── the BATCH anchor (Undo for a multi-select) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BatchAnchors_SkipTheOtherSelectedRows_SoEveryAnchorSurvivesTheMove()
    {
        // b and c are adjacent selected siblings inside g. c may NOT anchor on b — b is in flight too. Both fall
        // back to the previous unselected sibling, which for the first child of a folder is the folder itself.
        Assert.True(RootlistUndoAnchors.TryResolveMany(UndoTree(),
            ["pl:spotify:playlist:b", "pl:spotify:playlist:c"], out var undo));
        Assert.Equal(2, undo.Count);
        foreach (var m in undo)
        {
            Assert.Equal(new RootlistItemRef("g", true), m.Target);
            Assert.Equal(RootlistDropPlacement.Inside, m.Placement);
        }
        // Inside appends, so the run replays FORWARD and b lands ahead of c again.
        Assert.Equal(new RootlistItemRef("spotify:playlist:b", false), undo[0].Source);
        Assert.Equal(new RootlistItemRef("spotify:playlist:c", false), undo[1].Source);
    }

    [Fact]
    public void BatchAnchors_ShareAnAfterAnchor_AndThereforeReplayInReverse()
    {
        // a · [g(b,c)] · d — select the folder g and d. g anchors After a; d anchors After g, which is selected, so
        // it falls through to a as well. Two moves onto ONE After anchor reverse (the same rule RootlistBatchOrder
        // owns), or issuing them forward would swap them.
        Assert.True(RootlistUndoAnchors.TryResolveMany(UndoTree(), ["folder:g", "pl:spotify:playlist:d"], out var undo));
        Assert.Equal(2, undo.Count);
        foreach (var m in undo)
        {
            Assert.Equal(new RootlistItemRef("spotify:playlist:a", false), m.Target);
            Assert.Equal(RootlistDropPlacement.After, m.Placement);
        }
        Assert.Equal(new RootlistItemRef("spotify:playlist:d", false), undo[0].Source);   // reversed
        Assert.Equal(new RootlistItemRef("g", true), undo[1].Source);
    }

    [Fact]
    public void BatchAnchors_DropTheDescendantsOfASelectedFolder_AndRefuseWhenAnyItemHasNoAnchor()
    {
        // b rides inside g. Undoing it separately would address an index g's own op has already moved.
        Assert.True(RootlistUndoAnchors.TryResolveMany(UndoTree(), ["folder:g", "pl:spotify:playlist:b"], out var undo));
        Assert.Single(undo);
        Assert.Equal(new RootlistItemRef("g", true), undo[0].Source);

        // The whole tree selected: the first top-level item has nothing left to anchor against, so the batch has
        // NO undo at all rather than a partial one that would scatter the rest.
        List<SidebarLibraryEntry> lone = [Pl("a")];
        Assert.False(RootlistUndoAnchors.TryResolveMany(lone, ["pl:spotify:playlist:a"], out _));
        Assert.False(RootlistUndoAnchors.TryResolveMany(UndoTree(), [], out _));
        Assert.False(RootlistUndoAnchors.TryResolveMany(null, ["pl:spotify:playlist:a"], out _));
    }

    [Fact]
    public void TheSingleAnchor_IsTheBatchOfOne()
    {
        foreach (string id in new[] { "pl:spotify:playlist:a", "pl:spotify:playlist:b", "pl:spotify:playlist:c",
                                      "pl:spotify:playlist:d", "folder:g" })
        {
            bool one = RootlistUndoAnchors.TryResolve(UndoTree(), id, out var anchor, out var placement);
            bool many = RootlistUndoAnchors.TryResolveMany(UndoTree(), [id], out var undo);
            Assert.Equal(one, many);
            if (!one) continue;
            Assert.Single(undo);
            Assert.Equal(anchor, undo[0].Target);
            Assert.Equal(placement, undo[0].Placement);
        }
    }
}

// ── SidebarDropCue: line ⟺ ordering, plate ⟺ Into, never both, never neither-while-armed ────────────────────────────
//
// Full port of SidebarDropCueTests.cs. SidebarRowGeometry references replaced with RowGeometryLiteral (see its doc).
public class SidebarDropCueTests
{
    static SidebarDropSlot Slot(SidebarDropKind kind, int depth = 0)
        => new(4, kind, depth, SidebarDropRefusal.None);

    [Theory]
    [InlineData(SidebarDropKind.Before)]
    [InlineData(SidebarDropKind.After)]
    [InlineData(SidebarDropKind.EndOfList)]
    public void OrderingKinds_DrawTheLineAndNeverThePlate(SidebarDropKind kind)
    {
        Assert.True(SidebarDropCue.DrawsLine(kind));
        Assert.False(SidebarDropCue.DrawsPlate(kind));
        var slot = Slot(kind);
        Assert.True(slot.DrawsLine);
        Assert.False(slot.DrawsPlate);
        Assert.True(slot.IsArmed);
    }

    [Fact]
    public void Into_DrawsThePlateAndNeverTheLine()
    {
        Assert.True(SidebarDropCue.DrawsPlate(SidebarDropKind.Into));
        Assert.False(SidebarDropCue.DrawsLine(SidebarDropKind.Into));
        var slot = Slot(SidebarDropKind.Into);
        Assert.True(slot.DrawsPlate);
        Assert.False(slot.DrawsLine);
        Assert.True(slot.IsArmed);
    }

    [Fact]
    public void NoKindEverDrawsBoth_AndNoneDrawsNeither()
    {
        foreach (SidebarDropKind kind in Enum.GetValues<SidebarDropKind>())
        {
            bool line = SidebarDropCue.DrawsLine(kind);
            bool plate = SidebarDropCue.DrawsPlate(kind);
            Assert.False(line && plate);
            // ...and exactly one cue for every ARMED kind: an armed slot that drew nothing would be a target the
            // user cannot see, the other half of the same defect.
            Assert.Equal(kind != SidebarDropKind.None, line || plate);
        }
    }

    [Theory]
    [InlineData(SidebarDropRefusal.Self)]
    [InlineData(SidebarDropRefusal.IntoItself)]
    [InlineData(SidebarDropRefusal.IntoDescendant)]
    [InlineData(SidebarDropRefusal.NoOp)]
    [InlineData(SidebarDropRefusal.SortedList)]
    [InlineData(SidebarDropRefusal.NotLoaded)]
    [InlineData(SidebarDropRefusal.Unavailable)]
    public void ARefusedSlotDrawsNeither(SidebarDropRefusal refusal)
    {
        var slot = new SidebarDropSlot(4, SidebarDropKind.None, 2, refusal);
        Assert.False(slot.DrawsLine);
        Assert.False(slot.DrawsPlate);
        Assert.False(slot.IsArmed);
    }

    [Fact]
    public void RefusalsFromTheResolverAlwaysCarryKindNone()
    {
        var facts = new SidebarRowFacts(
            IsFolder: true, FolderExpanded: false, FolderHasChildren: true, Depth: 1, NextVisibleDepth: 0,
            CenterAccepts: true, SourceIsSelf: true, SortedNonCustom: true, RootlistLoaded: true);
        foreach (float t in new[] { 0f, 0.2f, 0.5f, 0.8f, 1f })
        {
            var slot = RootlistSlotResolver.Resolve(4, t, 100f, 44f, in facts, SidebarDropSlot.None);
            Assert.NotEqual(SidebarDropRefusal.None, slot.Refusal);
            Assert.False(slot.DrawsLine || slot.DrawsPlate);
        }
    }

    // ── the caret's geometry ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LineY_IsTheTopEdgeForBefore_AndTheBottomEdgeForAfter()
    {
        Assert.Equal(0f, SidebarDropCue.LineY(SidebarDropKind.Before, 44f));
        Assert.Equal(44f - SidebarDropCue.LineThickness, SidebarDropCue.LineY(SidebarDropKind.After, 44f));
        Assert.Equal(0f, SidebarDropCue.LineY(SidebarDropKind.EndOfList, RowGeometryLiteral.TreeEndHeight));
        // A degenerate row never produces a negative offset.
        Assert.Equal(0f, SidebarDropCue.LineY(SidebarDropKind.After, 1f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void LineWidth_IsTheContentLaneMinusTheDepthIndent(int depth)
    {
        const float content = 300f;
        float expected = content - RowGeometryLiteral.TreeContentX(depth) - RowGeometryLiteral.RowInsetRight;
        Assert.Equal(expected, SidebarDropCue.LineWidth(content, depth), 3);
        // A deeper caret is strictly shorter — that IS the depth cue.
        if (depth > 0)
            Assert.True(SidebarDropCue.LineWidth(content, depth) < SidebarDropCue.LineWidth(content, depth - 1));
    }

    [Fact]
    public void LineWidth_NeverGoesNegative()
    {
        Assert.Equal(0f, SidebarDropCue.LineWidth(0f, 0));
        Assert.Equal(0f, SidebarDropCue.LineWidth(4f, 3));
    }
}

// ── SidebarDropRefusalText: every refusal always SAYS something ────────────────────────────────────────────────────
//
// New coverage (not a 0.2.9 file — 0.3 factored the caption table out on its own): pins the total-mapping invariant
// the task calls out by name, since the table itself (unlike RootlistDropDecision) is public.
public class SidebarDropRefusalTextTests
{
    [Theory]
    [InlineData(SidebarDropRefusal.Self)]
    [InlineData(SidebarDropRefusal.IntoItself)]
    [InlineData(SidebarDropRefusal.IntoDescendant)]
    [InlineData(SidebarDropRefusal.NoOp)]
    [InlineData(SidebarDropRefusal.SortedList)]
    [InlineData(SidebarDropRefusal.NotLoaded)]
    [InlineData(SidebarDropRefusal.Unavailable)]
    public void EveryRefusal_HasItsOwnLocKey(SidebarDropRefusal refusal)
        => Assert.NotEqual("", SidebarDropRefusalText.LocKey(refusal));

    [Fact]
    public void NoneRefusal_HasNoSentence_BecauseNothingWasRefused()
        => Assert.Equal("", SidebarDropRefusalText.LocKey(SidebarDropRefusal.None));

    [Fact]
    public void DistinctRefusalsCanShareACaption_ButEveryOneResolves()
    {
        // Self/Unavailable share "cantMoveHere"; IntoItself/IntoDescendant share "cantMoveIntoItself" — sharing a
        // caption is fine, an unmapped refusal reaching the UI as "" is not.
        Assert.Equal(SidebarDropRefusalText.LocKey(SidebarDropRefusal.Self),
                     SidebarDropRefusalText.LocKey(SidebarDropRefusal.Unavailable));
        Assert.Equal(SidebarDropRefusalText.LocKey(SidebarDropRefusal.IntoItself),
                     SidebarDropRefusalText.LocKey(SidebarDropRefusal.IntoDescendant));
    }
}

// ── RootlistFolderPickerTests: the "Move to folder…" destination list ──────────────────────────────────────────────
//
// Per the task's own instruction, ported ONLY for the facts about RootlistTreeNav's destination list and legality
// (the dialog shell is stage-2 UI). One fact adapted rather than dropped: "TheFolderARowIsAlreadyTheLastChildOf_
// IsExcluded" called RootlistDropDecision.RefusalFor(RootlistDropDecision.Check(...)) directly — both internal and
// unreachable — so it now asserts the RootlistMoveCheck.NoOp verdict from RootlistOps.CheckMove, the SAME authority
// RootlistDropDecision.Check reduces to (its own doc: "the batch it asks about is RootlistBatchOrder.For's").
public class RootlistFolderPickerTests
{
    static List<RootlistFolderChoice> Destinations(string sourceId)
    {
        var into = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(SidebarTreeFixture.Tree(), SidebarTreeFixture.Markers(), sourceId, into);
        return into;
    }

    static List<RootlistFolderChoice> Destinations(params string[] sourceIds)
    {
        var into = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(SidebarTreeFixture.Tree(), SidebarTreeFixture.Markers(), sourceIds, into);
        return into;
    }

    static string[] Names(IReadOnlyList<RootlistFolderChoice> rows)
    {
        var names = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++) names[i] = rows[i].IsTopLevel ? "<top>" : rows[i].Name;
        return names;
    }

    [Fact]
    public void TopLevelIsPinnedFirst_ThenTheFoldersInTreeOrder()
    {
        Assert.Equal(new[] { "<top>", "Chill", "Deep", "Trailing" }, Names(Destinations(SidebarTreeFixture.Pl("a"))));
        Assert.True(Destinations(SidebarTreeFixture.Pl("a"))[0].IsTopLevel);
    }

    [Fact]
    public void FoldersCarryTheirTreeDepth_SoTheListReadsAsNested()
    {
        var rows = Destinations(SidebarTreeFixture.Pl("a"));
        Assert.Equal(0, rows[1].Depth);       // Chill, top level
        Assert.Equal(1, rows[2].Depth);       // Deep, inside Chill — indented one step by the picker
        Assert.Equal(0, rows[3].Depth);       // Trailing, back at the top
        Assert.Equal("g", rows[1].FolderId);
        Assert.Equal("", rows[0].FolderId);   // the top-level row addresses no folder at all
    }

    [Fact]
    public void ADraggedFoldersOwnSubtreeIsExcluded()
    {
        // Chill into Chill is "a folder into itself"; Chill into Deep is "into its own descendant". Both are
        // refused by the drop cue, so neither may be offered here — the picker and the drag answer to one table.
        Assert.Equal(new[] { "<top>", "Trailing" }, Names(Destinations(SidebarTreeFixture.Fo("g"))));
    }

    [Fact]
    public void TheFolderARowIsAlreadyTheLastChildOf_IsExcluded()
    {
        // e is Trailing's only (therefore last) child: "Inside Trailing" appends where it already is — the
        // adjacent no-op RootlistOps.CheckMove calls NoOp.
        var tree = SidebarTreeFixture.Tree();
        Assert.Equal(RootlistMoveCheck.NoOp, RootlistOps.CheckMove(
            SidebarTreeFixture.Markers(), SidebarTreeFixture.Ref(tree, SidebarTreeFixture.Pl("e")),
            SidebarTreeFixture.Ref(tree, SidebarTreeFixture.Fo("h")), RootlistDropPlacement.Inside));
        // …but TOP LEVEL is offered: e sits BEFORE h's end marker in the real stream, so landing after that marker
        // lifts it out of the folder — a genuine move the user has every right to be offered.
        Assert.Equal(new[] { "<top>", "Chill", "Deep" }, Names(Destinations(SidebarTreeFixture.Pl("e"))));
    }

    [Fact]
    public void TheLastTopLevelEntry_GetsNoTopLevelRow()
    {
        // Trailing already IS the end of the top level — "after itself" is where it is. Absent, never a dead row.
        var rows = Destinations(SidebarTreeFixture.Fo("h"));
        Assert.DoesNotContain(rows, r => r.IsTopLevel);
        Assert.Equal(new[] { "Chill", "Deep" }, Names(rows));
        Assert.False(RootlistTreeNav.TryTopLevelAnchor(SidebarTreeFixture.Tree(), SidebarTreeFixture.Markers(),
                                                       SidebarTreeFixture.Fo("h"), out _));
    }

    [Fact]
    public void TheTopLevelAnchorIsTheLastTopLevelEntry_SoATrailingFolderIsPassed()
    {
        // The anchor is the trailing FOLDER, landed After — whose exclusive range end is outside it.
        Assert.True(RootlistTreeNav.TryTopLevelAnchor(SidebarTreeFixture.Tree(), SidebarTreeFixture.Markers(),
                                                      SidebarTreeFixture.Pl("b"), out var anchor));
        Assert.Equal(new RootlistItemRef("h", IsFolder: true), anchor);
    }

    [Fact]
    public void AnUnknownSourceOrAnEmptyTree_OffersNothingRatherThanEverything()
    {
        var into = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(null, SidebarTreeFixture.Markers(), SidebarTreeFixture.Pl("a"), into);
        Assert.Empty(into);
        RootlistTreeNav.PickerDestinations(Array.Empty<SidebarLibraryEntry>(), SidebarTreeFixture.Markers(),
                                           SidebarTreeFixture.Pl("a"), into);
        Assert.Empty(into);
        // …and with no MARKER STREAM there is nothing to decide against, so nothing is offered.
        RootlistTreeNav.PickerDestinations(SidebarTreeFixture.Tree(), null, SidebarTreeFixture.Pl("a"), into);
        Assert.Empty(into);
    }

    // ── the BATCH: "Move {n} to folder…" ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASelectionOffersOnlyTheDestinationsEveryMemberMayEnter()
    {
        // a + Chill(g). "Deep" is gone: it lives INSIDE Chill, so filing Chill into it is a cycle, and a
        // destination that refuses ONE member refuses the whole batch. "Chill" itself SURVIVES — the builder drops
        // the Chill→Chill self-pair as a legal GATHER, so picking it files "a" inside Chill and leaves Chill put.
        Assert.Equal(new[] { "<top>", "Chill", "Trailing" },
                     Names(Destinations(SidebarTreeFixture.Pl("a"), SidebarTreeFixture.Fo("g"))));

        // Two ordinary playlists keep the whole list.
        Assert.Equal(new[] { "<top>", "Chill", "Deep", "Trailing" },
                     Names(Destinations(SidebarTreeFixture.Pl("a"), SidebarTreeFixture.Pl("d"))));
    }

    [Fact]
    public void ARowRidingInsideASelectedFolderIsNormalisedAway_NotAskedAbout()
    {
        // b is inside Chill. Selecting both is "move Chill", and the destination list must be Chill's — not the
        // intersection with a child that is going along for the ride anyway.
        Assert.Equal(Names(Destinations(SidebarTreeFixture.Fo("g"))),
                     Names(Destinations(SidebarTreeFixture.Fo("g"), SidebarTreeFixture.Pl("b"))));
    }

    [Fact]
    public void TheSingleRowListIsTheBatchOfOne()
    {
        foreach (string id in new[] { SidebarTreeFixture.Pl("a"), SidebarTreeFixture.Pl("e"),
                                      SidebarTreeFixture.Fo("g"), SidebarTreeFixture.Fo("h") })
            Assert.Equal(Names(Destinations(id)), Names(Destinations(new[] { id })));
    }

    [Fact]
    public void AnEmptyOrEntirelyUnknownSelection_OffersNothing()
    {
        Assert.Empty(Destinations());
        Assert.Empty(Destinations("pl:spotify:playlist:ghost"));
        Assert.False(RootlistTreeNav.HasDestinations(SidebarTreeFixture.Tree(), SidebarTreeFixture.Markers(),
                                                     Array.Empty<string>()));
    }

    [Fact]
    public void HasDestinations_AgreesWithTheListItSummarises()
    {
        foreach (string id in new[]
                 {
                     SidebarTreeFixture.Pl("a"), SidebarTreeFixture.Pl("e"), SidebarTreeFixture.Pl("f"),
                     SidebarTreeFixture.Fo("g"), SidebarTreeFixture.Fo("h"), "pl:spotify:playlist:ghost",
                 })
            Assert.Equal(Destinations(id).Count > 0,
                         RootlistTreeNav.HasDestinations(SidebarTreeFixture.Tree(), SidebarTreeFixture.Markers(), id));
    }
}

// ── SidebarNavExtrasTests: the tree row's Move up / Move down / Move to folder menu extras ─────────────────────────
//
// Full port of SidebarNavExtrasTests.cs — SidebarTreeNavLayout is public, distinct from the internal (queue-row)
// SidebarNavLayout the 0.2.9 SidebarNavLayoutTests.cs targeted (see the header note; that file is DROPPED).
public class SidebarNavExtrasTests
{
    static SidebarTreeNavLayout Layout(string id)
    {
        var tree = SidebarTreeFixture.Tree();
        return SidebarTreeNavLayout.Decide(RootlistTreeNav.Siblings(tree, id),
                                           RootlistTreeNav.HasDestinations(tree, SidebarTreeFixture.Markers(), id));
    }

    [Fact]
    public void AMiddleTreeRow_OffersAllThreeVerbs()
    {
        // c sits between b and [Deep] inside Chill: both orderings are real, and there are folders to file it into.
        var mid = Layout(SidebarTreeFixture.Pl("c"));
        Assert.True(mid.MoveUp);
        Assert.True(mid.MoveDown);
        Assert.True(mid.MoveToFolder);
        Assert.False(mid.IsEmpty);
    }

    [Fact]
    public void TheEndsOfTheSiblingRunDropTheVerbTheyCannotHonour()
    {
        var first = Layout(SidebarTreeFixture.Pl("a"));       // first at top level
        Assert.False(first.MoveUp);
        Assert.True(first.MoveDown);

        var last = Layout(SidebarTreeFixture.Fo("h"));        // last at top level
        Assert.True(last.MoveUp);
        Assert.False(last.MoveDown);

        var only = Layout(SidebarTreeFixture.Pl("f"));        // Deep's only child
        Assert.False(only.MoveUp);
        Assert.False(only.MoveDown);
        Assert.True(only.MoveToFolder);                       // it can still leave the folder it is alone in
    }

    [Fact]
    public void AFolderRowGetsTheSameVerbsAsAPlaylistRow()
    {
        // ONE renderer, so Classic / Library V3 / Curated share this; a folder is an ordinary sibling in its run.
        var folder = Layout(SidebarTreeFixture.Fo("k"));      // Deep, last among Chill's three children
        Assert.True(folder.MoveUp);
        Assert.False(folder.MoveDown);
        Assert.True(folder.MoveToFolder);
    }

    [Fact]
    public void ARowWithNoRunAndNowhereToGo_YieldsNoExtrasAtAll()
        => Assert.True(SidebarTreeNavLayout.Decide(RootlistSiblingRun.None, hasDestinations: false).IsEmpty);
}

// ── RootlistSelection + RootlistBatchOrder: normalize the selection, then order the batch against ONE anchor ──────
//
// New region distilling the underlying facts from RootlistDropScenarioTests.cs's ABatchArmsTheSameCue /
// ABatchDrop_IssuesExactlyOneMoveList. That scenario file stays DROPPED as a whole: RootlistDropDecision.Refine and
// RootlistSlotMapper.TryMap are public now, but its end-to-end assertions observe the resulting rootlist ORDER
// after a drop via the removed TryBuildMove/PlaylistDiffApplier apply step, which has no 0.3 equivalent — that half
// cannot be restored without inventing a writer Sidebar.cs does not have. What survives here is the
// selection-normalize and batch-ordering rule itself, plus a direct cross-check of "the picker never offers what
// the writer would refuse" using RootlistTreeNav.PickerDestinations + RootlistOps.CheckMoves.
public class RootlistSelectionAndBatchOrderTests
{
    [Fact]
    public void Normalize_DropsDescendantsOfASelectedFolder_AndOrdersByTree()
    {
        var tree = SidebarTreeFixture.Tree();
        // g (Chill) selected alongside its own child c: c rides inside g's subtree and must not appear twice.
        var selected = RootlistSelection.Normalize(tree, new HashSet<string>(StringComparer.Ordinal)
            { SidebarTreeFixture.Fo("g"), SidebarTreeFixture.Pl("c"), SidebarTreeFixture.Pl("a") });
        Assert.Equal(new[] { SidebarTreeFixture.Pl("a"), SidebarTreeFixture.Fo("g") }, Ids(selected));
    }

    [Fact]
    public void Refs_IsTheSeamShapeAnOrderedSelectionRidesAs()
    {
        var tree = SidebarTreeFixture.Tree();
        var selected = RootlistSelection.Normalize(tree,
            new[] { SidebarTreeFixture.Pl("a"), SidebarTreeFixture.Fo("g") });
        var refs = RootlistSelection.Refs(selected);
        Assert.Equal(new[]
        {
            new RootlistItemRef("spotify:playlist:a", false),
            new RootlistItemRef("g", true),
        }, refs);
    }

    [Fact]
    public void BatchOrder_IteratesForwardForBeforeAndInside_ReverseForAfterAndEndOfList()
    {
        var b = new RootlistItemRef("spotify:playlist:b", false);
        var d = new RootlistItemRef("spotify:playlist:d", false);
        var target = new RootlistItemRef("spotify:playlist:x", false);

        var before = RootlistBatchOrder.For([b, d], target, RootlistDropPlacement.Before);
        Assert.Equal(new[] { b, d }, before.Select(m => m.Source));

        var inside = RootlistBatchOrder.For([b, d], target, RootlistDropPlacement.Inside);
        Assert.Equal(new[] { b, d }, inside.Select(m => m.Source));

        var after = RootlistBatchOrder.For([b, d], target, RootlistDropPlacement.After);
        Assert.Equal(new[] { d, b }, after.Select(m => m.Source));   // reversed: each lands just after the anchor

        // The tree's END slot reverses for the same reason even when expressed as Before (the anchor's own After).
        var endOfList = RootlistBatchOrder.For([b, d], target, RootlistDropPlacement.Before, endOfList: true);
        Assert.Equal(new[] { d, b }, endOfList.Select(m => m.Source));
    }

    [Fact]
    public void EveryMoveInABatch_TargetsTheSameAnchorAndPlacement()
    {
        var b = new RootlistItemRef("spotify:playlist:b", false);
        var d = new RootlistItemRef("spotify:playlist:d", false);
        var target = new RootlistItemRef("g", true);

        var moves = RootlistBatchOrder.For([b, d], target, RootlistDropPlacement.Inside);
        Assert.All(moves, m =>
        {
            Assert.Equal(target, m.Target);
            Assert.Equal(RootlistDropPlacement.Inside, m.Placement);
        });
    }

    [Fact]
    public void ThePickersLegalityAgreesWithTheWritersLegality_ForEveryOfferedFolder()
    {
        // RootlistDropDecision.Check (internal, unreachable from this assembly) is what PickerDestinations filters
        // through; RootlistOps.CheckMoves is the authority it reduces to. Cross-checking through the public surface
        // pins the SAME invariant the internal type exists to guarantee: the picker can never offer a destination a
        // drag would refuse.
        var tree = SidebarTreeFixture.Tree();
        var markers = SidebarTreeFixture.Markers();
        var into = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(tree, markers, SidebarTreeFixture.Pl("b"), into);

        var sources = RootlistSelection.Refs(
            RootlistSelection.Normalize(tree, new[] { SidebarTreeFixture.Pl("b") }));
        foreach (var choice in into)
        {
            if (choice.IsTopLevel) continue;
            var target = new RootlistItemRef(choice.FolderId, IsFolder: true);
            var moves = RootlistBatchOrder.For(sources, target, RootlistDropPlacement.Inside);
            Assert.Equal(RootlistMoveCheck.Ok, RootlistOps.CheckMoves(markers, moves));
        }
    }

    static string[] Ids(IReadOnlyList<SidebarLibraryEntry> rows)
    {
        var ids = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++) ids[i] = rows[i].Id;
        return ids;
    }
}

// ── SidebarTreeSelection: the playlist tree's multi-selection, restored ────────────────────────────────────────────
//
// Full port of SidebarTreeSelectionTests.cs, restored now that SidebarTreeSelection is public. Pins WinUI's
// SelectionModel Extended arm ported by KEY (never by index — the sidebar's tree re-flows constantly): Shift
// replaces the selection with the anchor range, Ctrl toggles, a plain interaction clears-and-selects unless the
// item is already selected, and every single-item operation moves the anchor.
public class SidebarTreeSelectionTests
{
    static readonly string[] Order = ["a", "b", "c", "d", "e"];

    static SidebarTreeSelection New() => new();

    static string[] Sel(SidebarTreeSelection s) => s.Ordered(Order).ToArray();

    // ── the Extended trio ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APlainInteraction_ReplacesTheSelection_AndIsANoOpOnARowAlreadyAlone()
    {
        var s = New();
        Assert.True(s.Interact("b", ctrl: false, shift: false, Order));
        Assert.Equal(["b"], Sel(s));
        Assert.Equal("b", s.Anchor);

        // Already the whole selection: nothing CHANGES, which is what lets a plain press on a selected row start a
        // drag instead of collapsing the selection under the pointer.
        Assert.False(s.Interact("b", ctrl: false, shift: false, Order));
        Assert.Equal(["b"], Sel(s));

        Assert.True(s.Interact("d", ctrl: false, shift: false, Order));
        Assert.Equal(["d"], Sel(s));
        Assert.Equal("d", s.Anchor);
    }

    [Fact]
    public void CtrlToggles_AndMovesTheAnchorEitherWay()
    {
        var s = New();
        s.Interact("b", false, false, Order);
        Assert.True(s.Interact("d", ctrl: true, shift: false, Order));
        Assert.Equal(["b", "d"], Sel(s));
        Assert.Equal("d", s.Anchor);

        Assert.True(s.Interact("b", ctrl: true, shift: false, Order));   // toggles OFF
        Assert.Equal(["d"], Sel(s));
        Assert.Equal("b", s.Anchor);                                     // Deselect moves it too (SelectionModel does)
    }

    [Fact]
    public void ShiftSelectsTheAnchorRange_InEitherDirection_AndLeavesTheAnchorPut()
    {
        var s = New();
        s.Interact("b", false, false, Order);
        Assert.True(s.Interact("d", ctrl: false, shift: true, Order));
        Assert.Equal(["b", "c", "d"], Sel(s));
        Assert.Equal("b", s.Anchor);

        // A SECOND Shift re-ranges from the SAME anchor rather than walking, and it REPLACES (never accumulates).
        Assert.True(s.Interact("a", ctrl: false, shift: true, Order));
        Assert.Equal(["a", "b"], Sel(s));
        Assert.Equal("b", s.Anchor);
    }

    [Fact]
    public void ShiftWithNoResolvableAnchor_SelectsJustThatRow()
    {
        var s = New();
        // No anchor at all.
        Assert.True(s.Interact("c", ctrl: false, shift: true, Order));
        Assert.Equal(["c"], Sel(s));

        // …and an anchor that has left the tree: refusing here would read as a dead modifier.
        var t = New();
        t.Interact("e", false, false, Order);
        Assert.True(t.Interact("b", ctrl: false, shift: true, ["a", "b", "c"]));
        Assert.Equal(["b"], t.Ordered(["a", "b", "c"]).ToArray());
    }

    [Fact]
    public void ARangeThatChangesNothing_ReportsNoChange()
    {
        var s = New();
        s.Interact("b", false, false, Order);
        Assert.True(s.Interact("d", false, true, Order));
        Assert.False(s.Interact("d", false, true, Order));   // same range, same set
    }

    // ── the check lane ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheLaneIsVisibleAtTwoSelected_OrWheneverCheckModeIsOn()
    {
        var s = New();
        Assert.False(s.CheckLaneVisible);

        s.Toggle("a");
        Assert.False(s.CheckLaneVisible);        // one row is still "the row I clicked"
        s.Toggle("b");
        Assert.True(s.CheckLaneVisible);         // two is a set and needs a visible handle

        var t = New();
        Assert.True(t.SetCheckMode(true));
        Assert.True(t.CheckLaneVisible);         // explicit mode survives an EMPTY selection…
        Assert.Equal(0, t.Count);
        t.Toggle("a");
        Assert.True(t.CheckLaneVisible);         // …which is what lets the first click pick the first row
    }

    [Fact]
    public void ClearLeavesCheckMode_AndEmptiesTheAnchor()
    {
        var s = New();
        s.SetCheckMode(true);
        s.Interact("b", false, false, Order);
        Assert.True(s.Clear());
        Assert.Equal(0, s.Count);
        Assert.Null(s.Anchor);
        Assert.False(s.CheckMode);
        Assert.False(s.CheckLaneVisible);
        Assert.False(s.Clear());                 // idempotent — a second Escape bumps nothing
    }

    // ── prune ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PruneDropsRowsTheTreeNoLongerShows_AndTheAnchorWithThem()
    {
        var s = New();
        s.Interact("b", false, false, Order);
        s.Interact("d", true, false, Order);     // {b, d}, anchor d

        // "c" collapsed away under a folder, and so did "d".
        Assert.True(s.Prune(["a", "b", "e"]));
        Assert.Equal(["b"], s.Ordered(["a", "b", "e"]).ToArray());
        Assert.Null(s.Anchor);

        Assert.False(s.Prune(["a", "b", "e"]));  // nothing left to drop
    }

    [Fact]
    public void PruneAgainstAnEmptyOrderKeepsTheSelection()
    {
        // A transient frame (a pending projection, a section that planned nothing) is not evidence that a row is
        // gone — emptying the selection there would make a library refresh silently cancel a multi-select.
        var s = New();
        s.Interact("b", false, false, Order);
        Assert.False(s.Prune([]));
        Assert.Equal(1, s.Count);
        Assert.False(s.Prune(null));
        Assert.Equal(1, s.Count);
    }

    // ── ordering ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrderedIsTreeOrder_NotClickOrder()
    {
        var s = New();
        s.Toggle("d");
        s.Toggle("a");
        s.Toggle("c");
        Assert.Equal(["a", "c", "d"], Sel(s));
        // …and an id the visible order does not hold simply drops out, for the same reason Prune drops it.
        Assert.Equal(["a", "c"], s.Ordered(["a", "b", "c"]).ToArray());
        Assert.Empty(s.Ordered([]));
    }

    [Fact]
    public void IdsIsTheSetTheNormalizerTakes()
    {
        var s = New();
        s.Toggle("a");
        s.Toggle("c");
        Assert.True(s.Ids.Contains("a"));
        Assert.False(s.Ids.Contains("b"));
        Assert.Equal(2, s.Ids.Count);
        Assert.True(s.Contains("c"));
        Assert.False(s.Contains(""));
        Assert.False(s.Contains(null));
    }

    [Fact]
    public void AnEmptyIdIsNeverASelection()
    {
        var s = New();
        Assert.False(s.Interact("", false, false, Order));
        Assert.False(s.Toggle(""));
        Assert.Equal(0, s.Count);
    }
}

// ── SidebarFolderTree + SidebarFolderFlyoutNav: the collapsed rail's folder flyout, restored ───────────────────────
//
// Full port of SidebarFolderFlyoutNavTests.cs, restored now that both types are public. Containment is
// ParentFolderId, and only that (a row's FolderId means something different for a leaf vs. a folder); ChildCount is
// the count of exactly the rows Children lists; the drill-in stack refuses a cycle and mints PageKey = depth + ":" +
// folderId so two levels sharing a depth do not reconcile as one page and swallow the slide.
public class SidebarFolderFlyoutNavTests
{
    // ── direct children ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Children_AreDirectOnly_AndKeepRootlistOrder()
    {
        var tree = SidebarTreeFixture.Tree();
        var into = new List<SidebarLibraryEntry>();

        Assert.Equal(3, SidebarFolderTree.Children(tree, "g", into));
        Assert.Equal(new[] { SidebarTreeFixture.Pl("b"), SidebarTreeFixture.Pl("c"), SidebarTreeFixture.Fo("k") },
            Ids(into));
    }

    /// <summary>A folder's own <c>FolderId</c> is ITSELF, so a lookup written against that field makes every folder
    /// its own child. Containment is <c>ParentFolderId</c> for both kinds — this is the guard on that.</summary>
    [Fact]
    public void Children_NeverContainTheFolderItself()
    {
        var into = new List<SidebarLibraryEntry>();
        SidebarFolderTree.Children(SidebarTreeFixture.Tree(), "g", into);

        Assert.DoesNotContain(into, e => e.Id == SidebarTreeFixture.Fo("g"));
    }

    [Fact]
    public void Children_OfANestedFolder_AreReachable()
    {
        var into = new List<SidebarLibraryEntry>();

        Assert.Equal(1, SidebarFolderTree.Children(SidebarTreeFixture.Tree(), "k", into));
        Assert.Equal(SidebarTreeFixture.Pl("f"), into[0].Id);
    }

    [Fact]
    public void Children_OfAnEmptyOrUnknownFolder_AreNone()
    {
        var tree = SidebarTreeFixture.Tree();
        var into = new List<SidebarLibraryEntry> { SidebarTreeFixture.Playlist("stale", 0) };

        // Empty is NOT "the whole tree": the buffer is cleared, and an unknown id yields nothing rather than
        // everything.
        Assert.Equal(0, SidebarFolderTree.Children(tree, "no-such-folder", into));
        Assert.Empty(into);
        Assert.Equal(0, SidebarFolderTree.Children(tree, "", into));
        Assert.Equal(0, SidebarFolderTree.Children(null, "g", into));
    }

    [Fact]
    public void TryFolder_ResolvesByGroupId_NotByEntryId()
    {
        var tree = SidebarTreeFixture.Tree();

        Assert.True(SidebarFolderTree.TryFolder(tree, "k", out var deep));
        Assert.Equal("Deep", deep.Name);
        Assert.Equal("g", deep.ParentFolderId);
        // The ENTRY id ("folder:k") is not the group id — passing it must miss rather than half-match.
        Assert.False(SidebarFolderTree.TryFolder(tree, SidebarTreeFixture.Fo("k"), out _));
    }

    // ── the drill-in stack ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Root_HasNoBack()
    {
        var nav = new SidebarFolderFlyoutNav("g", "Chill");

        Assert.Equal(1, nav.Depth);
        Assert.False(nav.CanGoBack);
        Assert.Equal("g", nav.Current.FolderId);
        Assert.Equal("Chill", nav.Current.Name);
        // Parent clamps to the root, so a caller can name "back to X" without indexing the stack.
        Assert.Equal("g", nav.Parent.FolderId);
        Assert.False(nav.Pop());
        Assert.Equal(1, nav.Depth);
    }

    [Fact]
    public void Push_DrillsIn_AndBackReturnsToTheParentLevel()
    {
        var nav = new SidebarFolderFlyoutNav("g", "Chill");

        Assert.True(nav.Push("k", "Deep"));
        Assert.Equal(2, nav.Depth);
        Assert.True(nav.CanGoBack);
        Assert.Equal("Deep", nav.Current.Name);
        Assert.Equal("Chill", nav.Parent.Name);

        Assert.True(nav.Pop());
        Assert.Equal(1, nav.Depth);
        Assert.False(nav.CanGoBack);
        Assert.Equal("g", nav.Current.FolderId);
    }

    [Fact]
    public void Push_RefusesAnEmptyId_AndAFolderAlreadyOnTheStack()
    {
        var nav = new SidebarFolderFlyoutNav("g", "Chill");
        nav.Push("k", "Deep");

        Assert.False(nav.Push("", "nothing"));
        // A rootlist cycle cannot exist, but a stale projection mid-move can briefly describe one; an unbounded
        // push would grow the stack until Back became useless.
        Assert.False(nav.Push("g", "Chill"));
        Assert.False(nav.Push("k", "Deep again"));
        Assert.Equal(2, nav.Depth);
    }

    [Fact]
    public void Push_CarriesTheNameItWasGiven_NotALiveLookup()
    {
        // The level name is captured at push time on purpose: the folder can be renamed or deleted while the
        // flyout is open, and the back header must still name the level the user actually came through.
        var nav = new SidebarFolderFlyoutNav("g", "Chill");
        nav.Push("k", "Deep");

        Assert.Equal("Deep", nav.Current.Name);
        Assert.Equal("Chill", nav.Parent.Name);
    }

    [Fact]
    public void PageKey_ChangesOnEveryMove_SoTheSlideCannotBeSkipped()
    {
        var nav = new SidebarFolderFlyoutNav("g", "Chill");
        string root = nav.PageKey;

        nav.Push("k", "Deep");
        string deep = nav.PageKey;
        Assert.NotEqual(root, deep);

        nav.Pop();
        Assert.Equal(root, nav.PageKey);

        // A DIFFERENT folder at the same depth is a different page — two levels that happened to share a depth must
        // not reconcile as one and swallow the transition.
        nav.Push("h", "Trailing");
        Assert.NotEqual(deep, nav.PageKey);
    }

    [Fact]
    public void DeepChain_PopsOneLevelAtATime()
    {
        var nav = new SidebarFolderFlyoutNav("a", "A");
        Assert.True(nav.Push("b", "B"));
        Assert.True(nav.Push("c", "C"));
        Assert.Equal(3, nav.Depth);

        Assert.True(nav.Pop());
        Assert.Equal("b", nav.Current.FolderId);
        Assert.True(nav.Pop());
        Assert.Equal("a", nav.Current.FolderId);
        Assert.False(nav.Pop());
    }

    // ── ONE containment definition ───────────────────────────────────────────────────────────────────────────────────
    //
    // The flyout LISTED rows from the ParentFolderId scan and printed "N items" from the projection's own
    // ChildCount — two definitions of "what is in this folder". Both come from one place. Pinned here against a
    // fixture tree with ChildCount hand-stamped to the real per-folder count (SidebarTreeFixture's shared Tree()
    // hardcodes ChildCount: 0 for every row — a fixture invariant other regions rely on — so this uses a local
    // variant instead of changing the shared one).
    static List<SidebarLibraryEntry> Projected() =>
    [
        SidebarTreeFixture.Playlist("a", 0),
        SidebarTreeFixture.Folder("g", "Chill", 0) with { ChildCount = 3 },
        SidebarTreeFixture.Playlist("b", 1, "g", "Chill"),
        SidebarTreeFixture.Playlist("c", 1, "g", "Chill"),
        SidebarTreeFixture.Folder("k", "Deep", 1, "g", "Chill") with { ChildCount = 1 },
        SidebarTreeFixture.Playlist("f", 2, "k", "Deep"),
        SidebarTreeFixture.Playlist("d", 0),
        SidebarTreeFixture.Folder("empty", "Empty", 0) with { ChildCount = 0 },
    ];

    [Fact]
    public void ChildCount_IsTheCountOfExactlyTheRowsTheFlyoutLists()
    {
        var tree = Projected();
        var rows = new List<SidebarLibraryEntry>();
        foreach (var f in tree)
        {
            if (f.Kind != SidebarEntryKind.Folder) continue;
            int listed = SidebarFolderTree.Children(tree, f.FolderId, rows);
            // the number the flyout renders ≡ the rows it renders ≡ the projection's own Items count
            Assert.Equal(listed, SidebarFolderTree.ChildCount(tree, f.FolderId));
            Assert.Equal(listed, f.ChildCount);
        }
    }

    [Fact]
    public void ZeroItems_IsImpossibleWhileTheFolderHasAny()
    {
        var tree = Projected();
        var rows = new List<SidebarLibraryEntry>();
        foreach (string folder in new[] { "g", "k" })
        {
            Assert.NotEqual(0, SidebarFolderTree.Children(tree, folder, rows));
            Assert.NotEqual(0, SidebarFolderTree.ChildCount(tree, folder));
        }
        // …and an EMPTY folder still reads zero on both channels — the count is not "unknown", it is none.
        Assert.Equal(0, SidebarFolderTree.Children(tree, "empty", rows));
        Assert.Equal(0, SidebarFolderTree.ChildCount(tree, "empty"));
    }

    static string[] Ids(IReadOnlyList<SidebarLibraryEntry> rows)
    {
        var ids = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++) ids[i] = rows[i].Id;
        return ids;
    }
}

// ── SidebarReorderClamp: the reorder-clamp displacement offset, restored ───────────────────────────────────────────
//
// Only the SidebarReorderClamp.Offset facts from SidebarDragClampTests.cs — restored now that the type is public.
// The rest of that old file (LibraryV3View.ClampToSiblingRun, SidebarRailDropRules) is left out on purpose: those
// types live in Sidebar.Modes.cs and Platform/Drag.cs, not Sidebar.cs, and are a different file's gate.
public class SidebarDragClampTests
{
    [Fact]
    public void TheClampedGap_MovesOnlyTheRowsBetweenSourceAndDestination()
    {
        const float h = 40f;
        // Downward: the rows the lifted one passes close up behind it.
        Assert.Equal(0f, SidebarReorderClamp.Offset(slot: 0, from: 0, to: 3, h));   // the lifted row itself
        Assert.Equal(-h, SidebarReorderClamp.Offset(slot: 1, from: 0, to: 3, h));
        Assert.Equal(-h, SidebarReorderClamp.Offset(slot: 3, from: 0, to: 3, h));
        Assert.Equal(0f, SidebarReorderClamp.Offset(slot: 4, from: 0, to: 3, h));   // past the destination
        // Upward: they part to make room.
        Assert.Equal(h, SidebarReorderClamp.Offset(slot: 1, from: 3, to: 1, h));
        Assert.Equal(h, SidebarReorderClamp.Offset(slot: 2, from: 3, to: 1, h));
        Assert.Equal(0f, SidebarReorderClamp.Offset(slot: 0, from: 3, to: 1, h));
        // A no-move draws no gap.
        Assert.Equal(0f, SidebarReorderClamp.Offset(slot: 2, from: 2, to: 2, h));
    }
}

// ── SidebarStageHold: the mid-drag parking bay, restored ────────────────────────────────────────────────────────────
//
// Full port of SidebarDropFreezeTests.cs, restored now that SidebarStageHold<TStage> is public. Drives the real
// state machine directly: a stage published mid-session is HELD, not published; a burst during one gesture
// converges to the newest stage; the flush runs exactly once; and a stage that arrives after the session ended is
// never held — it publishes normally, because nothing will ever flush it.
public class SidebarDropFreezeTests
{
    static SidebarStageHold<string> Bay() => new();

    [Fact]
    public void AStagePublishedMidSession_IsHeld_NotPublished()
    {
        var bay = Bay();
        Assert.True(bay.TryHold(sessionLive: true, "stage-1"));
        Assert.True(bay.HasHeld);
    }

    [Fact]
    public void ABurstDuringOneSession_ConvergesToTheNewestStage()
    {
        var bay = Bay();
        // A dealer push, our own ack and a background revalidate can all land inside one gesture. Only the last one
        // describes the library the user will be looking at when the drag ends.
        Assert.True(bay.TryHold(sessionLive: true, "stage-1"));
        Assert.True(bay.TryHold(sessionLive: true, "stage-2"));
        Assert.True(bay.TryHold(sessionLive: true, "stage-3"));

        Assert.True(bay.TryFlush(out string? flushed));
        Assert.Equal("stage-3", flushed);
    }

    [Fact]
    public void TheFlush_HappensExactlyOnce()
    {
        var bay = Bay();
        bay.TryHold(sessionLive: true, "stage-1");

        Assert.True(bay.TryFlush(out string? first));
        Assert.Equal("stage-1", first);
        // The watcher's layout effect is edge-keyed, but the pane also discards on every publish — flushing the
        // same stage twice would re-publish a plan the list has already diffed against.
        Assert.False(bay.TryFlush(out string? second));
        Assert.Null(second);
        Assert.False(bay.HasHeld);
    }

    [Fact]
    public void AStageThatArrivesAfterTheSessionEnded_PublishesNormally()
    {
        var bay = Bay();
        // THE RACE THAT MATTERS. Nothing will flush this one — no session end is coming — so the hold must decline
        // it and let the caller publish on the spot.
        Assert.False(bay.TryHold(sessionLive: false, "stage-late"));
        Assert.False(bay.HasHeld);
        Assert.False(bay.TryFlush(out _));
    }

    [Fact]
    public void AFlushWithNothingParked_IsANoOp()
    {
        // Every session end calls the flush, and the overwhelming majority of drags never held anything (a track
        // drag is not frozen at all).
        Assert.False(Bay().TryFlush(out string? stage));
        Assert.Null(stage);
    }

    [Fact]
    public void Discard_DropsTheParkedStage_SoAnUnmountMidSessionLeaksNothing()
    {
        var bay = Bay();
        bay.TryHold(sessionLive: true, "stage-1");
        bay.Discard();

        Assert.False(bay.HasHeld);
        Assert.False(bay.TryFlush(out _));
    }

    [Fact]
    public void ASessionThatEndsAndBeginsAgain_HoldsAndFlushesIndependently()
    {
        var bay = Bay();
        bay.TryHold(sessionLive: true, "first-session");
        Assert.True(bay.TryFlush(out string? first));
        Assert.Equal("first-session", first);

        bay.TryHold(sessionLive: true, "second-session");
        Assert.True(bay.TryFlush(out string? second));
        Assert.Equal("second-session", second);
    }
}

// ── SidebarNavLayout: the row's navbar-customization extras, restored ──────────────────────────────────────────────
//
// Full port of SidebarNavLayoutTests.cs, restored now that SidebarNavLayout is public. Distinct from the (also
// public) SidebarTreeNavLayout already exercised by SidebarNavExtrasTests above: this one decides Move up / Move
// down / Remove for a navbar-customization row (a reorder band or the pin store), not the rootlist tree's Move up /
// Move down / Move to folder.
public class SidebarNavLayoutTests
{
    [Fact]
    public void AMiddleItem_CanMoveBothWays()
    {
        var layout = SidebarNavLayout.Decide(orderIndex: 1, orderCount: 3, removable: false);
        Assert.True(layout.MoveUp);
        Assert.True(layout.MoveDown);
        Assert.False(layout.Remove);
        Assert.False(layout.IsEmpty);
    }

    [Fact]
    public void TheFirstItem_CanOnlyMoveDown()
    {
        var layout = SidebarNavLayout.Decide(0, 3, removable: false);
        Assert.False(layout.MoveUp);
        Assert.True(layout.MoveDown);
    }

    [Fact]
    public void TheLastItem_CanOnlyMoveUp()
    {
        var layout = SidebarNavLayout.Decide(2, 3, removable: false);
        Assert.True(layout.MoveUp);
        Assert.False(layout.MoveDown);
    }

    [Fact]
    public void ALoneItem_CannotMove()
    {
        var layout = SidebarNavLayout.Decide(0, 1, removable: false);
        Assert.True(layout.IsEmpty);
    }

    [Fact]
    public void AProjectedLeaf_WithNoOrder_OffersNothing()
    {
        var layout = SidebarNavLayout.Decide(-1, 0, removable: false);
        Assert.True(layout.IsEmpty);
    }

    [Fact]
    public void AnAuthoredItem_CanBeRemovedEvenWhenItCannotMove()
    {
        var layout = SidebarNavLayout.Decide(0, 1, removable: true);
        Assert.False(layout.MoveUp);
        Assert.False(layout.MoveDown);
        Assert.True(layout.Remove);
        Assert.False(layout.IsEmpty);
    }
}
