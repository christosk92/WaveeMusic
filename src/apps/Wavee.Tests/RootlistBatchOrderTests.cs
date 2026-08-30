using System.Collections.Generic;
using Wavee;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE TWO PURE RULES A MULTI-SELECT ADDS, and nothing else: which items a selection actually carries
/// (<see cref="RootlistSelection"/>) and in which ORDER their moves must be issued
/// (<see cref="RootlistBatchOrder"/>).
///
/// <para>Both are asserted against the REAL builder — <c>RootlistOps.TryBuildMoves</c> over a marker stream, applied by
/// the production applier — because the ordering rule is only true of the index math it exists to survive. A test that
/// restated the rule instead of running it would pass on a batch that lands backwards.</para>
///
/// <para>The worked cases are the design's own: <c>[A,B,C,D,E]</c> with the selection <c>{B,D}</c>, moved After E,
/// Before A, and Inside a folder.</para>
/// </summary>
public class RootlistBatchOrderTests
{
    // ── a flat five-item rootlist ────────────────────────────────────────────────────────────────────────────────────

    static string U(string slug) => "spotify:playlist:" + slug;

    static IReadOnlyList<RootlistEntry> Flat() => RootlistTreeBuilder.EntriesFromUris(
        [U("a"), U("b"), U("c"), U("d"), U("e")]);

    /// <summary>Five items plus a trailing empty folder F, for the Inside case.</summary>
    static IReadOnlyList<RootlistEntry> WithFolder() => RootlistTreeBuilder.EntriesFromUris(
        [U("a"), U("b"), U("c"), U("d"), U("e"),
         RootlistOps.StartGroupUri("f", "F"), RootlistOps.EndGroupUri("f")]);

    static RootlistItemRef P(string slug) => new(U(slug), IsFolder: false);

    static IReadOnlyList<RootlistItemRef> Sources(params string[] slugs)
    {
        var refs = new List<RootlistItemRef>(slugs.Length);
        foreach (string s in slugs) refs.Add(P(s));
        return refs;
    }

    /// <summary>The stream a batch leaves behind, as row labels ("[F" / "]" for the folder markers).</summary>
    static string[] Run(IReadOnlyList<RootlistEntry> markers, IReadOnlyList<RootlistMove> moves)
    {
        Assert.True(RootlistOps.TryBuildMoves(markers, moves, out var ops, out var reason), "reason = " + reason);
        var rows = new List<PlaylistMember>(markers.Count);
        foreach (var e in markers) rows.Add(new PlaylistMember("", e.Uri, null, e.AddedAtMs));
        PlaylistDiffApplier.Apply(rows, ops);
        var uris = new List<string>(rows.Count);
        foreach (var r in rows) uris.Add(r.ItemUri);
        var after = RootlistTreeBuilder.EntriesFromUris(uris);
        var labels = new List<string>(after.Count);
        foreach (var e in after)
            labels.Add(e.Kind == 0 ? e.Uri[(e.Uri.LastIndexOf(':') + 1)..] : e.Kind == 1 ? "[" + (e.GroupName ?? "") : "]");
        return labels.ToArray();
    }

    // ── the direction rule ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AfterAnAnchor_IssuesInREVERSETreeOrder_SoTheRunKeepsItsOrder()
    {
        // Each op lands immediately AFTER the anchor and therefore ahead of everything issued before it. Forward would
        // give [A,C,E,D,B] — the selection arrives at the destination back to front.
        var moves = RootlistBatchOrder.For(Sources("b", "d"), P("e"), RootlistDropPlacement.After);
        Assert.Equal([P("d"), P("b")], [moves[0].Source, moves[1].Source]);
        Assert.Equal(["a", "c", "e", "b", "d"], Run(Flat(), moves));
    }

    [Fact]
    public void BeforeAnAnchor_IssuesInTreeOrder()
    {
        var moves = RootlistBatchOrder.For(Sources("b", "d"), P("a"), RootlistDropPlacement.Before);
        Assert.Equal([P("b"), P("d")], [moves[0].Source, moves[1].Source]);
        Assert.Equal(["b", "d", "a", "c", "e"], Run(Flat(), moves));
    }

    [Fact]
    public void InsideAFolder_IssuesInTreeOrder_AndAppendsInThatOrder()
    {
        var moves = RootlistBatchOrder.For(Sources("b", "d"), new RootlistItemRef("f", IsFolder: true),
                                           RootlistDropPlacement.Inside);
        Assert.Equal([P("b"), P("d")], [moves[0].Source, moves[1].Source]);
        Assert.Equal(["a", "c", "e", "[F", "b", "d", "]"], Run(WithFolder(), moves));
    }

    [Fact]
    public void TheEndOfListSlot_ReversesForTheSameReasonAfterDoes()
    {
        // The tree's END row is expressed as "After the last top-level entry", so the flag and the placement agree —
        // the flag exists so the caller states the intent instead of relying on that coincidence.
        var byFlag = RootlistBatchOrder.For(Sources("b", "d"), P("e"), RootlistDropPlacement.After, endOfList: true);
        var byPlacement = RootlistBatchOrder.For(Sources("b", "d"), P("e"), RootlistDropPlacement.After);
        Assert.Equal(byPlacement, byFlag);
    }

    [Fact]
    public void ASelectionOfONE_IsTheSameListEitherWay()
    {
        // The whole point of "single = list of one": the direction rule cannot change a one-item batch.
        foreach (var placement in new[] { RootlistDropPlacement.Before, RootlistDropPlacement.After })
        {
            var moves = RootlistBatchOrder.For(Sources("b"), P("e"), placement);
            Assert.Single(moves);
            Assert.Equal(new RootlistMove(P("b"), P("e"), placement), moves[0]);
        }
    }

    [Fact]
    public void EmptyAndKeylessSourcesDropOut_RatherThanReachingTheSeam()
    {
        Assert.Empty(RootlistBatchOrder.For(null, P("e"), RootlistDropPlacement.After));
        Assert.Empty(RootlistBatchOrder.For([], P("e"), RootlistDropPlacement.After));
        Assert.Single(RootlistBatchOrder.For([new RootlistItemRef("", false), P("b")], P("e"),
                                             RootlistDropPlacement.After));
    }

    // ── the "gather": dropping a selection onto one of its OWN members ───────────────────────────────────────────────

    [Fact]
    public void DroppingASelectionAfterOneOfItsOwnMembers_IsALegalGather_NotSameItem()
    {
        // The target IS in the selection. That self-pair is dropped by the builder and the rest close up around it —
        // which is the whole point of aiming a multi-select at one of its own rows.
        var moves = RootlistBatchOrder.For(Sources("b", "d"), P("b"), RootlistDropPlacement.After);
        Assert.Equal(RootlistMoveCheck.Ok, RootlistOps.CheckMoves(Flat(), moves));
        Assert.Equal(["a", "b", "d", "c", "e"], Run(Flat(), moves));

        // …but a batch of ONE onto itself is still SameItem, which is what keeps the single-item refusal sentence.
        Assert.Equal(RootlistMoveCheck.SameItem,
                     RootlistOps.CheckMoves(Flat(), RootlistBatchOrder.For(Sources("b"), P("b"),
                                                                          RootlistDropPlacement.After)));
    }

    // ── normalisation: what the selection actually carries ───────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_KeepsTreeOrder_RegardlessOfClickOrder()
    {
        var tree = SidebarTreeFixture.Tree();
        var picked = RootlistSelection.Normalize(tree,
            [SidebarTreeFixture.Pl("d"), SidebarTreeFixture.Pl("a"), SidebarTreeFixture.Pl("c")]);
        Assert.Equal(["a", "c", "d"], NamesOf(picked));
    }

    [Fact]
    public void Normalize_DropsTheDescendantsOfASelectedFolder()
    {
        // Chill(g) contains b, c and Deep(k), which contains f. Selecting the folder AND its contents must carry the
        // folder alone: a child riding inside its own parent would be moved against an index the parent's op moved.
        var tree = SidebarTreeFixture.Tree();
        var picked = RootlistSelection.Normalize(tree,
            [SidebarTreeFixture.Fo("g"), SidebarTreeFixture.Pl("b"), SidebarTreeFixture.Pl("f"),
             SidebarTreeFixture.Fo("k"), SidebarTreeFixture.Pl("d")]);
        Assert.Equal(["Chill", "d"], NamesOf(picked));

        // A nested folder selected WITHOUT its ancestor still carries its own subtree and nothing more.
        Assert.Equal(["Deep"], NamesOf(RootlistSelection.Normalize(tree,
            [SidebarTreeFixture.Fo("k"), SidebarTreeFixture.Pl("f")])));
    }

    [Fact]
    public void Normalize_IgnoresUnknownIdsAndEmptyInput()
    {
        var tree = SidebarTreeFixture.Tree();
        Assert.Empty(RootlistSelection.Normalize(tree, ["pl:spotify:playlist:ghost"]));
        Assert.Empty(RootlistSelection.Normalize(tree, (IReadOnlyList<string>?)null));
        Assert.Empty(RootlistSelection.Normalize(null, [SidebarTreeFixture.Pl("a")]));
        Assert.Empty(RootlistSelection.Refs(null));
    }

    [Fact]
    public void Refs_AddressAFolderByGroupId_AndAPlaylistByUri()
    {
        var tree = SidebarTreeFixture.Tree();
        var refs = RootlistSelection.Refs(RootlistSelection.Normalize(tree,
            [SidebarTreeFixture.Fo("g"), SidebarTreeFixture.Pl("d")]));
        Assert.Equal([new RootlistItemRef("g", true), new RootlistItemRef("spotify:playlist:d", false)], refs);
    }

    static string[] NamesOf(IReadOnlyList<SidebarLibraryEntry> entries)
    {
        var names = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++) names[i] = entries[i].Name;
        return names;
    }
}
