// ── Wavee.Tests/RootlistDropScenarioTests.cs — the sidebar drop, end to end, over the tree the user actually had ────
//
// Restored from 0.2.9 (gap batch B2, G-043). Four live failures came out of this shape, all from one cause: "is this
// legal" and "where does it land" were decided in several places over several tree models.
//
//   [root folder updated name]          depth 0
//       [named folder update]           depth 1
//           #9                          depth 2
//       updated playlist name           depth 1
//   10's · Careless · 90's · LoL · HSM  depth 0
//
// The path driven here is the one the pane and the library host run in 0.3: a decided SLOT (kind + depth over one row)
// → `RootlistDropDecision.Refine` (map over the FULL flattened tree, check against the marker stream) → the library
// host's writer (`Spotify.Encode.TryBuildMove(s)`) → `Spotify.Encode.ApplyLocally` — asserting the resulting ORDER.
// The pointer GEOMETRY (which slot a pixel means) is `SidebarDropTests`' gate and is not repeated: every slot here is
// stated as the value the resolver publishes. Both representations are built from one description — the projection rows
// by `SidebarTreeFixture`'s helpers, the marker stream the server holds by the production marker encoder.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class RootlistDropScenarioTests
{
    const string Root = "root";
    const string Named = "named";
    const string RootName = "root folder updated name";
    const string NamedName = "named folder update";

    static string Uri(string slug) => SidebarTreeFixture.PlaylistUriPrefix + slug;

    static readonly (string Slug, string Name)[] TopLevel =
        [("tens", "10's"), ("careless", "Careless"), ("nineties", "90's"), ("lol", "LoL"), ("hsm", "HSM")];

    /// <summary>The FULL depth-first flattened tree the projection publishes.</summary>
    static List<SidebarLibraryEntry> Tree(bool withUpdated = true)
    {
        var tree = new List<SidebarLibraryEntry>
        {
            SidebarTreeFixture.Folder(Root, RootName, 0),
            SidebarTreeFixture.Folder(Named, NamedName, 1, Root, RootName),
            SidebarTreeFixture.Playlist("nine", 2, Named, NamedName) with { Name = "#9" },
        };
        if (withUpdated) tree.Add(SidebarTreeFixture.Playlist("updated", 1, Root, RootName) with { Name = "updated playlist name" });
        foreach (var (slug, name) in TopLevel) tree.Add(SidebarTreeFixture.Playlist(slug, 0) with { Name = name });
        return tree;
    }

    /// <summary>The same rootlist as the SERVER holds it: escaped start markers, balanced ends.</summary>
    static List<RootlistEntry> Markers(bool withUpdated = true)
    {
        var uris = new List<string>
        {
            Spotify.Encode.StartGroupUri(Root, RootName),
            Spotify.Encode.StartGroupUri(Named, NamedName),
            Uri("nine"),
            Spotify.Encode.EndGroupUri(Named),
        };
        if (withUpdated) uris.Add(Uri("updated"));
        uris.Add(Spotify.Encode.EndGroupUri(Root));
        foreach (var (slug, _) in TopLevel) uris.Add(Uri(slug));
        return Spotify.Encode.EntriesFromUris(uris);
    }

    static SidebarLibraryEntry Entry(IReadOnlyList<SidebarLibraryEntry> tree, string name)
        => tree.First(e => e.Name == name);

    static int Row(IReadOnlyList<SidebarLibraryEntry> tree, string name)
    {
        for (int i = 0; i < tree.Count; i++) if (tree[i].Name == name) return i;
        throw new Xunit.Sdk.XunitException("no such row: " + name);
    }

    static RootlistItemRef Ref(IReadOnlyList<SidebarLibraryEntry> tree, string name)
    {
        var e = Entry(tree, name);
        return RootlistTreeNav.RefOf(in e);
    }

    /// <summary>A decided slot over row <paramref name="row"/>, refined against the tree and the stream.</summary>
    static SidebarDropSlot Decide(IReadOnlyList<SidebarLibraryEntry> tree, IReadOnlyList<RootlistEntry>? markers, string source,
                                  int row, SidebarDropKind kind, int depth, out RootlistSlotTarget target)
    {
        var cue = new SidebarDropSlot(row, kind, depth, SidebarDropRefusal.None);
        return RootlistDropDecision.Refine(in cue, row < tree.Count ? tree[row].Id : "", tree, markers,
                                           [Ref(tree, source)], out target);
    }

    /// <summary>Build the move a decided slot commits with the host's writer, apply it, and read the order back with
    /// folders as <c>[name</c> … <c>]</c>.</summary>
    static string[] Apply(IReadOnlyList<RootlistEntry> markers, RootlistItemRef source, in RootlistSlotTarget target)
    {
        Assert.True(Spotify.Encode.TryBuildMove(markers, source, target.Ref, target.Placement, out var op, out var reason),
                    "the decided target must build an op; reason = " + reason);
        return Order(Spotify.Encode.ApplyLocally(markers, [op]));
    }

    static string[] Order(IReadOnlyList<RootlistEntry> entries)
    {
        var names = new List<string>(entries.Count);
        foreach (var e in entries)
        {
            if (e.Kind == 1) names.Add("[" + e.GroupName);
            else if (e.Kind == 2) names.Add("]");
            else names.Add(Name(e.Uri));
        }
        return names.ToArray();

        static string Name(string uri)
        {
            string slug = uri[SidebarTreeFixture.PlaylistUriPrefix.Length..];
            if (slug == "nine") return "#9";
            if (slug == "updated") return "updated playlist name";
            foreach (var (s, n) in TopLevel) if (s == slug) return n;
            return slug;
        }
    }

    // ── F1 · a perfectly legal Into ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F1_FilingAPlaylistIntoASiblingFolder_IsLegalAndLandsInside()
    {
        var tree = Tree();
        var markers = Markers();
        var slot = Decide(tree, markers, "updated playlist name", Row(tree, NamedName), SidebarDropKind.Into, 1, out var target);

        Assert.Equal(SidebarDropKind.Into, slot.Kind);
        Assert.Equal(RootlistDropPlacement.Inside, target.Placement);
        Assert.Equal(NamedName, target.DestinationName);
        Assert.False(target.Deposit);
        Assert.Equal(["[" + RootName, "[" + NamedName, "#9", "updated playlist name", "]", "]",
                      "10's", "Careless", "90's", "LoL", "HSM"],
                     Apply(markers, Ref(tree, "updated playlist name"), in target));
    }

    // ── F2 · the header's two bands ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void F2_BeforeTheFolderHeader_LandsTheItemBeforeThatFolderInsideTheSameParent()
    {
        var tree = Tree();
        var markers = Markers();
        var slot = Decide(tree, markers, "#9", Row(tree, NamedName), SidebarDropKind.Before, 1, out var target);

        Assert.Equal((SidebarDropKind.Before, 1), (slot.Kind, slot.Depth));
        Assert.Equal(RootName, target.DestinationName);
        Assert.Equal(["[" + RootName, "#9", "[" + NamedName, "]", "updated playlist name", "]",
                      "10's", "Careless", "90's", "LoL", "HSM"],
                     Apply(markers, Ref(tree, "#9"), in target));
    }

    [Fact]
    public void F2_TheBottomBandOfAnExpandedFolder_IsItsFirstChildSlot()
    {
        var tree = Tree();
        var markers = Markers();
        var slot = Decide(tree, markers, "LoL", Row(tree, RootName), SidebarDropKind.Before, 1, out var target);

        Assert.Equal(SidebarDropKind.Before, slot.Kind);
        Assert.Equal(RootName, target.DestinationName);
        Assert.Equal(["[" + RootName, "LoL", "[" + NamedName, "#9", "]", "updated playlist name", "]",
                      "10's", "Careless", "90's", "HSM"],
                     Apply(markers, Ref(tree, "LoL"), in target));
    }

    // ── F3 · under the folder's last child: depth 1 stays in, depth 0 gets out ──────────────────────────────────────

    [Fact]
    public void F3_AfterTheLastChild_AtTheChildsOwnDepth_LandsInsideTheFolder()
    {
        var tree = Tree();
        var markers = Markers();
        Decide(tree, markers, "LoL", Row(tree, "updated playlist name"), SidebarDropKind.After, 1, out var target);

        Assert.Equal(RootlistDropPlacement.After, target.Placement);
        Assert.Equal(RootName, target.DestinationName);
        Assert.Equal(["[" + RootName, "[" + NamedName, "#9", "]", "updated playlist name", "LoL", "]",
                      "10's", "Careless", "90's", "HSM"],
                     Apply(markers, Ref(tree, "LoL"), in target));
    }

    [Fact]
    public void F3_AfterTheLastChild_AtDepth0_LandsAfterTheFolderAtTopLevel()
    {
        var tree = Tree();
        var markers = Markers();
        Decide(tree, markers, "LoL", Row(tree, "updated playlist name"), SidebarDropKind.After, 0, out var target);

        // Expressed against the FOLDER, not the child — that is what puts it past root's end marker.
        Assert.Equal(new RootlistItemRef(Root, IsFolder: true), target.Ref);
        Assert.Equal("", target.DestinationName);                        // "" ⇒ "Moved to Your Library"
        Assert.Equal(RootName, target.AnchorName);
        Assert.Equal(["[" + RootName, "[" + NamedName, "#9", "]", "updated playlist name", "]",
                      "LoL", "10's", "Careless", "90's", "HSM"],
                     Apply(markers, Ref(tree, "LoL"), in target));
    }

    [Fact]
    public void F3_TheOutdentClimbsExactlyAsFarAsTheDepthPicked()
    {
        var tree = Tree();
        var markers = Markers();
        Decide(tree, markers, "HSM", Row(tree, "#9"), SidebarDropKind.After, 2, out var stay);
        Assert.Equal(new RootlistItemRef(Uri("nine"), false), stay.Ref);
        Assert.Equal(NamedName, stay.DestinationName);

        Decide(tree, markers, "HSM", Row(tree, "#9"), SidebarDropKind.After, 1, out var climb);
        Assert.Equal(new RootlistItemRef(Named, IsFolder: true), climb.Ref);
        Assert.Equal(RootName, climb.DestinationName);
        Assert.Equal(["[" + RootName, "[" + NamedName, "#9", "]", "HSM", "updated playlist name", "]",
                      "10's", "Careless", "90's", "LoL"],
                     Apply(markers, Ref(tree, "HSM"), in climb));
    }

    [Fact]
    public void F3_ATwoLevelOutdent_ClimbsBothFolders()
    {
        var tree = Tree(withUpdated: false);
        var markers = Markers(withUpdated: false);
        Decide(tree, markers, "LoL", Row(tree, "#9"), SidebarDropKind.After, 0, out var target);

        Assert.Equal(new RootlistItemRef(Root, IsFolder: true), target.Ref);
        Assert.Equal("", target.DestinationName);
        Assert.Equal(["[" + RootName, "[" + NamedName, "#9", "]", "]", "LoL", "10's", "Careless", "90's", "HSM"],
                     Apply(markers, Ref(tree, "LoL"), in target));
    }

    // ── F4 · the adjacent no-op refuses, out loud, and the writer agrees ────────────────────────────────────────────

    [Fact]
    public void F4_AnAdjacentNoOp_RefusesAndIsNeverArmed()
    {
        var tree = Tree();
        var markers = Markers();
        var after = Decide(tree, markers, "Careless", Row(tree, "10's"), SidebarDropKind.After, 0, out _);
        Assert.Equal(SidebarDropRefusal.NoOp, after.Refusal);
        Assert.False(after.IsArmed || after.DrawsLine || after.DrawsPlate);

        Assert.Equal(SidebarDropRefusal.NoOp,
                     Decide(tree, markers, "Careless", Row(tree, "90's"), SidebarDropKind.Before, 0, out _).Refusal);
    }

    [Fact]
    public void F4_TheRefusedMove_IsAlsoRefusedByTheWriter()
    {
        Assert.False(Spotify.Encode.TryBuildMove(Markers(), new RootlistItemRef(Uri("careless"), false),
                                                 new RootlistItemRef(Uri("tens"), false), RootlistDropPlacement.After,
                                                 out _, out var reason));
        Assert.Equal(RootlistMoveCheck.NoOp, reason);
    }

    // ── the end of the tree, cycles, no stream ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTreeEndSlot_LandsAfterTheTrailingRow_AtTopLevel()
    {
        var tree = Tree();
        var markers = Markers();
        var slot = Decide(tree, markers, "#9", tree.Count, SidebarDropKind.EndOfList, 0, out var target);
        Assert.True(slot.IsArmed);
        Assert.Equal(new RootlistItemRef(Uri("hsm"), false), target.Ref);
        Assert.Equal(["[" + RootName, "[" + NamedName, "]", "updated playlist name", "]",
                      "10's", "Careless", "90's", "LoL", "HSM", "#9"],
                     Apply(markers, Ref(tree, "#9"), in target));

        Assert.Equal(SidebarDropRefusal.Self, Decide(tree, markers, "HSM", tree.Count, SidebarDropKind.EndOfList, 0, out _).Refusal);
    }

    [Fact]
    public void AFolderDroppedIntoItsOwnSubtree_RefusesWithTheCycleSentence()
    {
        var tree = Tree();
        var markers = Markers();
        Assert.Equal(SidebarDropRefusal.IntoDescendant,
                     Decide(tree, markers, RootName, Row(tree, "#9"), SidebarDropKind.Before, 2, out _).Refusal);
        Assert.Equal(SidebarDropRefusal.IntoDescendant,
                     Decide(tree, markers, RootName, Row(tree, "#9"), SidebarDropKind.After, 2, out _).Refusal);
    }

    [Fact]
    public void WithNoMarkerStream_NoOrderingIsEverArmed()
    {
        var tree = Tree();
        for (int row = 0; row < tree.Count; row++)
            foreach (var kind in new[] { SidebarDropKind.Before, SidebarDropKind.After, SidebarDropKind.Into })
            {
                var slot = Decide(tree, null, "LoL", row, kind, tree[row].Depth, out var target);
                if (target.Deposit) continue;                          // a track copy needs no rootlist order
                Assert.False(slot.IsArmed);
                Assert.NotEqual(SidebarDropRefusal.None, slot.Refusal);
            }
    }

    // ── totality: every armed slot builds, every disarmed one has a reason ──────────────────────────────────────────

    [Fact]
    public void Totality_EveryArmedOrderingBuilds_AndEveryDisarmedSlotHasAReason()
    {
        var tree = Tree();
        var markers = Markers();
        int armed = 0, refused = 0;
        foreach (var source in tree)
            for (int row = 0; row <= tree.Count; row++)
            {
                var kinds = row == tree.Count
                    ? new[] { SidebarDropKind.EndOfList }
                    : new[] { SidebarDropKind.Before, SidebarDropKind.After, SidebarDropKind.Into };
                int maxDepth = row == tree.Count ? 0 : tree[row].Depth + 1;
                foreach (var kind in kinds)
                    for (int depth = 0; depth <= maxDepth; depth++)
                    {
                        var slot = Decide(tree, markers, source.Name, row, kind, depth, out var target);
                        if (!slot.IsArmed)
                        {
                            if (slot.Refusal != SidebarDropRefusal.None) refused++;
                            continue;
                        }
                        armed++;
                        if (target.Deposit) continue;
                        var moves = RootlistBatchOrder.For([RootlistTreeNav.RefOf(in source)], target.Ref, target.Placement,
                                                           kind == SidebarDropKind.EndOfList);
                        Assert.True(Spotify.Encode.TryBuildMoves(markers, moves, out var ops, out var reason),
                                    $"{source.Name} {kind}@{depth} over row {row}: armed, but the writer says {reason}");
                        Assert.Equal(markers.Count, Spotify.Encode.ApplyLocally(markers, ops).Count);
                    }
            }
        Assert.True(armed > 0 && refused > 0, "the sweep must exercise both arms");
    }

    [Fact]
    public void OneDrop_ProducesExactlyOneMove()
    {
        var tree = Tree();
        var markers = Markers();
        var targets = new List<(string What, RootlistSlotTarget Target)>();
        Decide(tree, markers, "LoL", Row(tree, "Careless"), SidebarDropKind.After, 0, out var afterRow);
        targets.Add(("playlist row", afterRow));
        Decide(tree, markers, "LoL", Row(tree, NamedName), SidebarDropKind.Into, 1, out var intoFolder);
        targets.Add(("folder row", intoFolder));
        Decide(tree, markers, "LoL", tree.Count, SidebarDropKind.EndOfList, 0, out var end);
        targets.Add(("tree end", end));
        var tile = new SidebarDropSlot(0, SidebarDropKind.Into, 0, SidebarDropRefusal.None);
        Assert.True(RootlistSlotMapper.TryMap(in tile, Entry(tree, RootName).Id, tree, out var tileTarget));
        targets.Add(("rail folder tile", tileTarget));

        foreach (var (what, target) in targets)
        {
            Assert.True(Spotify.Encode.TryBuildMove(markers, Ref(tree, "LoL"), target.Ref, target.Placement, out var op, out var reason),
                        what + ": " + reason);
            Assert.Equal(PlaylistOpKind.Move, op.Kind);
            Assert.Equal(1, op.Length);                                 // one leaf, one row — exactly one move
        }
    }

    // ── the batch: one decision, one move list, relative order kept ──────────────────────────────────────────────────

    [Fact]
    public void ABatchDrop_IsOneMoveList_AndKeepsRelativeOrder()
    {
        var tree = Tree();
        var markers = Markers();
        var sources = RootlistSelection.Refs(RootlistSelection.Normalize(tree, new[] { Entry(tree, "10's").Id, Entry(tree, "90's").Id }));
        int row = Row(tree, "Careless");
        var cue = new SidebarDropSlot(row, SidebarDropKind.Before, 0, SidebarDropRefusal.None);
        var slot = RootlistDropDecision.Refine(in cue, tree[row].Id, tree, markers, sources, out var target);
        Assert.Equal(SidebarDropKind.Before, slot.Kind);

        var moves = RootlistBatchOrder.For(sources, target.Ref, target.Placement);
        Assert.Equal(2, moves.Count);
        Assert.True(Spotify.Encode.TryBuildMoves(markers, moves, out var ops, out var reason), "reason = " + reason);
        Assert.Equal(["[" + RootName, "[" + NamedName, "#9", "]", "updated playlist name", "]",
                      "10's", "90's", "Careless", "LoL", "HSM"],
                     Order(Spotify.Encode.ApplyLocally(markers, ops)));
    }

    [Fact]
    public void ABatchIntoADescendantOfASelectedFolder_Refuses()
    {
        var tree = Tree();
        var markers = Markers();
        var sources = RootlistSelection.Refs(RootlistSelection.Normalize(tree,
            new[] { Entry(tree, RootName).Id, Entry(tree, "#9").Id, Entry(tree, "LoL").Id }));
        Assert.Equal(2, sources.Count);                                 // "#9" rides inside the selected folder
        int row = Row(tree, NamedName);
        var cue = new SidebarDropSlot(row, SidebarDropKind.Into, 1, SidebarDropRefusal.None);
        var refused = RootlistDropDecision.Refine(in cue, tree[row].Id, tree, markers, sources, out _);
        Assert.Equal(SidebarDropRefusal.IntoDescendant, refused.Refusal);
        Assert.False(refused.IsArmed);
    }
}
