using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE SIDEBAR DROP RESOLVER — the pure geometry behind every playlist/folder drag in the sidebar tree.
///
/// <para>It replaced <c>SidebarPane.RootlistPlacementFor</c>, which had <b>zero</b> tests (D18) and three defects that a
/// test would have caught on sight: the zone geometry changed size with the payload (11 vs 22 DIP for the same row), the
/// pointer's X was never read at all so depth could be neither shown nor chosen, and a degenerate viewport silently
/// guessed a placement instead of refusing. Every one of those is pinned below.</para>
/// </summary>
public class RootlistSlotResolverTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>Resolve with the pointer parked over the row's LABEL (x far past the indent ladder) — the default
    /// position, and therefore the one that must mean "stay at this row's depth".</summary>
    static SidebarDropSlot At(float t, in SidebarRowFacts f, float h = 44f, float x = 200f)
        => RootlistSlotResolver.Resolve(3, t, x, h, in f, SidebarDropSlot.None);

    // ── the edge band ────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Heights))]
    public void EdgeBand_IsThirtyPercentClampedBetweenTenAndSixteen(float h)
    {
        float edge = RootlistSlotResolver.EdgeFor(h);
        Assert.Equal(System.Math.Clamp(h * 0.30f, 10f, 16f), edge, 3);
        // It must always leave a centre: two bands can never consume the whole row.
        Assert.True(edge * 2f < h);
    }

    [Fact]
    public void EdgeBand_DependsOnTheRowAlone_NeverOnThePayload()
    {
        // D6: the OLD geometry used 11-DIP bands for one payload and 22-DIP bands for another on the SAME row, so an
        // identical pointer position meant different things depending on what you happened to be dragging. The band is
        // now a function of the row height and nothing else — there is no payload parameter to pass.
        Assert.Equal(13.2f, RootlistSlotResolver.EdgeFor(44f), 3);
        // A pathologically short row still leaves a centre: never more than half of it.
        Assert.Equal(2f, RootlistSlotResolver.EdgeFor(4f), 3);
        Assert.Equal(RootlistSlotResolver.MinEdge, RootlistSlotResolver.EdgeFor(0f), 3);
    }

    // ── zones ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Heights))]
    public void PlainRow_IsTwoZones_WithNoDeadCentre(float h)
    {
        var f = Leaf();
        Assert.Equal(SidebarDropKind.Before, At(0.49f, in f, h).Kind);
        Assert.Equal(SidebarDropKind.After, At(0.51f, in f, h).Kind);
        // A row that cannot take a deposit must not reserve half its height for one (D4's other half).
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
        // The precise "make it the folder's first item" gesture — the line indents one step and the drop lands ahead of
        // the current first child. Without it, "into a folder" always meant "append last" and the header was the only
        // anchor there was (D7).
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
        // Explorer / Finder / VS Code / Spotify all agree; this is the one convention the old build did honour, and it
        // must survive the rewrite.
        foreach (var expanded in new[] { true, false })
        foreach (var children in new[] { true, false })
            Assert.Equal(SidebarDropKind.Into, At(0.5f, FolderRow(expanded, children)).Kind);
    }

    // ── depth ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DepthRange_IsAmbiguousOnlyAfterAFoldersLastVisibleChild()
    {
        // A fixture tree, flattened exactly as the plan holds it:
        //   f1                (folder, depth 0, expanded)
        //     a               (leaf,   depth 1)
        //     f2              (folder, depth 1, expanded)
        //       b             (leaf,   depth 2)   ← LAST visible child of f2 AND of f1: ambiguous, 0..2
        //   f3                (folder, depth 0, COLLAPSED — its subtree is not visible)
        //   f4                (folder, depth 0, EMPTY)
        //   c                 (leaf,   depth 0)                     ← last row: ambiguous only by being last
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
            Assert.Equal(System.Math.Min(next, depths[i]), min);
            // Ambiguous ⇔ the next visible row is SHALLOWER than this one — i.e. this row closes one or more folders.
            Assert.Equal(next < depths[i], min < max);
        }
    }

    [Fact]
    public void DepthRange_ClampsAFolderHeaderToItsOwnDepth()
    {
        // The next visible row is the folder's own CHILD (deeper). There is no slot below a header that lands outside
        // the header, so Min may never exceed Max.
        var f = FolderRow(expanded: true, hasChildren: true, depth: 0, nextDepth: 1);
        Assert.Equal((0, 0), RootlistSlotResolver.DepthRange(in f));
    }

    [Fact]
    public void DepthPick_DefaultsToTheRowsOwnDepth()
    {
        // The pointer sits over the LABEL for almost every gesture — so the default must be "stay where you are", and
        // outdenting must be something the user does on purpose by travelling left.
        var f = Leaf(depth: 2, nextDepth: 0);
        Assert.Equal(2, At(0.9f, in f, x: 200f).Depth);
    }

    [Theory]
    [InlineData(25f, 0)]     // TreeContentX(0) — where a depth-0 row starts drawing
    [InlineData(37f, 1)]     // TreeContentX(1)
    [InlineData(49f, 2)]     // TreeContentX(2): the row's own depth
    [InlineData(999f, 2)]    // past the ladder: clamped to Max
    [InlineData(-50f, 0)]    // before the row: clamped to Min
    public void DepthPick_ReadsTheTreeContentLadderFromPointerX(float x, int expected)
    {
        // THE LADDER IS THE ROW'S OWN. It used to be `IndentFor` (6 + 12·d), ~19 DIP left of where a tree row actually
        // draws — so every band sat under the connector art, depth 0 needed x < 12, and the outdent gesture was
        // effectively unreachable with a pointer (F3). `TreeContentX` is the sum the row itself lays out.
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
        // THE NUMERIC CHECK BEHIND F3. Parked on the row's own content origin the pick is that depth; half a step plus
        // 5 DIP to the LEFT of it — a deliberate slide, still inside the row — it is one shallower. depth 1: 37 → 26.
        // depth 2: 49 → 38.
        var f = Leaf(depth: depth, nextDepth: 0);
        float here = SidebarRowGeometry.TreeContentX(depth);
        float outdent = here - SidebarRowGeometry.TreeGuideStep / 2f - 5f;
        Assert.Equal(depth, At(0.9f, in f, x: here).Depth);
        Assert.Equal(depth - 1, At(0.9f, in f, x: outdent).Depth);
    }

    [Fact]
    public void DepthPick_HoldsThePreviousDepthInsideTheHysteresisBand()
    {
        var f = Leaf(depth: 2, nextDepth: 0);
        var previous = new SidebarDropSlot(3, SidebarDropKind.After, 1, SidebarDropRefusal.None);
        // The 1→2 boundary sits at TreeContentX(1) + 0.5·TreeGuideStep = 43. Inside 4 DIP of it the previous holds…
        Assert.Equal(1, RootlistSlotResolver.Resolve(3, 0.9f, 44f, 44f, in f, in previous).Depth);
        // …and past it the pick commits.
        Assert.Equal(2, RootlistSlotResolver.Resolve(3, 0.9f, 49f, 44f, in f, in previous).Depth);
        // With no previous slot there is nothing to hold: the raw pick wins.
        Assert.Equal(2, RootlistSlotResolver.Resolve(3, 0.9f, 44f, 44f, in f, SidebarDropSlot.None).Depth);
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

    // ── the tree's end marker ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TreeEndRow_IsOneWholeRowSlotAtDepthZero()
    {
        var f = Leaf() with { IsListEnd = true };
        foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            var slot = RootlistSlotResolver.Resolve(9, t, 200f, SidebarRowGeometry.TreeEndHeight, in f,
                                                    SidebarDropSlot.None);
            Assert.Equal(SidebarDropKind.EndOfList, slot.Kind);
            Assert.Equal(0, slot.Depth);
        }
    }

    // ── degenerate geometry (D17) ────────────────────────────────────────────────────────────────────────────────────

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

    // ── the undo anchor ──────────────────────────────────────────────────────────────────────────────────────────────

    static SidebarLibraryEntry Pl(string slug, int depth = 0, string parent = "")
        => new("pl:spotify:playlist:" + slug, SidebarEntryKind.Playlist, "spotify:playlist:" + slug, slug, "",
               null, null, 0, 0, 1, 0, 0, depth, false, SidebarPlaylistFlavor.None) { ParentFolderId = parent };

    static SidebarLibraryEntry Fold(string id, int depth = 0, string parent = "")
        => new("folder:" + id, SidebarEntryKind.Folder, "", id, "", null, null, 0, 0, 1, 0, 0, depth, false,
               SidebarPlaylistFlavor.None) { FolderId = id, ParentFolderId = parent };

    static List<SidebarLibraryEntry> Tree() =>
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
        Assert.True(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:c", out var anchor, out var placement));
        Assert.Equal(new RootlistItemRef("spotify:playlist:b", false), anchor);
        Assert.Equal(RootlistDropPlacement.After, placement);

        // A folder's previous sibling is the folder itself, addressed by group id — never one of its children.
        Assert.True(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:d", out var afterFolder, out _));
        Assert.Equal(new RootlistItemRef("g", true), afterFolder);
    }

    [Fact]
    public void UndoAnchor_FallsToTheNextSiblingForTheFirstChild()
    {
        Assert.True(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:b", out var anchor, out var placement));
        Assert.Equal(new RootlistItemRef("spotify:playlist:c", false), anchor);
        Assert.Equal(RootlistDropPlacement.Before, placement);

        // Top level, first: the next TOP-LEVEL sibling, which is the folder (its whole subtree is skipped).
        Assert.True(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:a", out var first, out var before));
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
        Assert.False(RootlistUndoAnchors.TryResolve(Tree(), "pl:spotify:playlist:missing", out _, out _));
        Assert.False(RootlistUndoAnchors.TryResolve(null, "pl:spotify:playlist:a", out _, out _));
    }

    // ── the BATCH anchor (Undo for a multi-select) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BatchAnchors_SkipTheOtherSELECTEDRows_SoEveryAnchorSurvivesTheMove()
    {
        // b and c are adjacent selected siblings inside g. c may NOT anchor on b — b is in flight too. Both fall back
        // to the previous unselected sibling, which for the first child of a folder is the folder itself (Inside).
        Assert.True(RootlistUndoAnchors.TryResolveMany(Tree(),
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
    public void BatchAnchors_ShareAnAfterAnchor_AndThereforeReplayInREVERSE()
    {
        // a · [g(b,c)] · d — select the folder g and d. g anchors After a; d anchors After g, which is selected, so it
        // falls through to… a as well. Two moves onto ONE After anchor: issuing them forward would swap them, so the
        // run reverses (the same rule RootlistBatchOrder.For owns).
        Assert.True(RootlistUndoAnchors.TryResolveMany(Tree(), ["folder:g", "pl:spotify:playlist:d"], out var undo));
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
        Assert.True(RootlistUndoAnchors.TryResolveMany(Tree(), ["folder:g", "pl:spotify:playlist:b"], out var undo));
        Assert.Single(undo);
        Assert.Equal(new RootlistItemRef("g", true), undo[0].Source);

        // The whole tree selected: the first top-level item has nothing left to anchor against, so the batch has NO
        // undo at all rather than a partial one that would scatter the rest.
        List<SidebarLibraryEntry> lone = [Pl("a")];
        Assert.False(RootlistUndoAnchors.TryResolveMany(lone, ["pl:spotify:playlist:a"], out _));
        Assert.False(RootlistUndoAnchors.TryResolveMany(Tree(), [], out _));
        Assert.False(RootlistUndoAnchors.TryResolveMany(null, ["pl:spotify:playlist:a"], out _));
    }

    [Fact]
    public void TheSingleAnchor_IsTheBatchOfONE()
    {
        // No parallel single-item rule: TryResolve is TryResolveMany with a list of one, and it must keep answering
        // exactly what it answered before.
        foreach (string id in new[] { "pl:spotify:playlist:a", "pl:spotify:playlist:b", "pl:spotify:playlist:c",
                                      "pl:spotify:playlist:d", "folder:g" })
        {
            bool one = RootlistUndoAnchors.TryResolve(Tree(), id, out var anchor, out var placement);
            bool many = RootlistUndoAnchors.TryResolveMany(Tree(), [id], out var undo);
            Assert.Equal(one, many);
            if (!one) continue;
            Assert.Single(undo);
            Assert.Equal(anchor, undo[0].Target);
            Assert.Equal(placement, undo[0].Placement);
        }
    }
}
