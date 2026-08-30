using System;
using System.Collections.Generic;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE "MOVE TO FOLDER…" DESTINATION LIST. Move up/down walk one sibling at a time and "Move out of {parent}" climbs
/// exactly one level, so filing a playlist into a folder that is not adjacent to it used to be a drag and nothing else
/// (D12). The picker names every legal destination at once.
///
/// <para><b>The list is not its own rule.</b> Legality is <c>RootlistOps.CheckMove</c> over the rootlist MARKER STREAM
/// — the one authority the drop cue refuses with — so the picker cannot offer a destination a drag would refuse. These
/// tests drive the pure builder; the flyout shell around it is <c>PlaylistPickerPanel</c>'s, re-used.</para>
/// </summary>
public class RootlistFolderPickerTests
{
    static List<RootlistFolderChoice> Destinations(string sourceId)
    {
        var into = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(SidebarTreeFixture.Tree(), SidebarTreeFixture.Markers(), sourceId, into);
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
        // Top level leads because it is the destination with no folder to scroll to, and the un-nest is what a user
        // reaching for this verb most often wants.
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
        // Chill into Chill is "a folder into itself"; Chill into Deep is "into its own descendant". Both are refused by
        // the drop cue, so neither may be offered here — the picker and the drag answer to one table.
        Assert.Equal(new[] { "<top>", "Trailing" }, Names(Destinations(SidebarTreeFixture.Fo("g"))));
    }

    [Fact]
    public void TheFolderARowIsAlreadyTheLastChildOf_IsExcluded()
    {
        // e is Trailing's only (therefore last) child: "Inside Trailing" appends where it already is. That is the
        // adjacent no-op the refusal table calls AlreadyThere, and a picker row for it would do nothing visible.
        var tree = SidebarTreeFixture.Tree();
        Assert.Equal(SidebarDropRefusal.NoOp, RootlistDropDecision.RefusalFor(RootlistDropDecision.Check(
            SidebarTreeFixture.Markers(), SidebarTreeFixture.Ref(tree, SidebarTreeFixture.Pl("e")),
            SidebarTreeFixture.Ref(tree, SidebarTreeFixture.Fo("h")), RootlistDropPlacement.Inside)));
        // …but TOP LEVEL is offered, and that is the correction the marker stream brings. e is the last row of the
        // flattened TREE, so the deleted flattened check called "after the trailing folder" the position it already
        // occupies — it cannot tell "after the folder" from "after the folder's last child", because it has no end
        // marker to put between them. In the real stream e sits BEFORE h's end marker, so landing after that marker
        // lifts it out of the folder: a genuine move, and one the user has every right to be offered.
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
    public void TheTopLevelAnchorIsTheLastTopLevelENTRY_SoATrailingFolderIsPassed()
    {
        // The anchor is the trailing FOLDER, landed After — whose exclusive range end is outside it. Anchoring on the
        // folder's last CHILD instead is exactly the D2 shape that put an item back inside the folder it left.
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
        // …and with no MARKER STREAM there is nothing to decide against, so nothing is offered. A destination list
        // built without the authority would be a guess.
        RootlistTreeNav.PickerDestinations(SidebarTreeFixture.Tree(), null, SidebarTreeFixture.Pl("a"), into);
        Assert.Empty(into);
    }

    // ── the BATCH: "Move {n} to folder…" ───────────────────────────────────────────────────────────────────────────

    static List<RootlistFolderChoice> Destinations(params string[] sourceIds)
    {
        var into = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(SidebarTreeFixture.Tree(), SidebarTreeFixture.Markers(), sourceIds, into);
        return into;
    }

    [Fact]
    public void ASelectionOffersOnlyTheDestinationsEVERYMemberMayEnter()
    {
        // a + Chill(g). "Deep" is gone: it lives INSIDE Chill, so filing Chill into it is a cycle and a destination
        // that refuses ONE member refuses the whole batch — half a filing is worse than none.
        //
        // "Chill" itself SURVIVES, and that is the batch rule rather than an oversight: the builder drops the
        // Chill→Chill self-pair as a legal GATHER, so picking it files "a" inside Chill and leaves Chill where it is.
        // The picker cannot answer that question a second way — legality is RootlistDropDecision.Check and nothing
        // else. (Chill selected ALONE is excluded, because then EVERY move is the self-pair: SameItem.)
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
    public void TheSingleRowListIsTheBATCHOfOne()
    {
        // No parallel single-item path: the 1-id overload is the N-id builder with a list of one.
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
                                                     System.Array.Empty<string>()));
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
