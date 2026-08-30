using System;
using System.Collections.Generic;
using Wavee;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>
/// THE SIDEBAR DROP, END TO END, OVER THE TREE THE USER ACTUALLY HAD.
///
/// <code>
///   [root folder updated name]          depth 0
///       [named folder update]           depth 1
///           #9                          depth 2
///       updated playlist name           depth 1
///   10's · Careless · 90's · LoL · HSM  depth 0
/// </code>
///
/// <para>Four live failures came out of this exact shape, and every one of them was the same systemic cause: the UI
/// decided "is this legal" and "where does it land" in eight places over three different tree models. This suite drives
/// the whole path the pane now runs — <c>RootlistSlotResolver.Resolve</c> (geometry) → <c>RootlistDropDecision.Refine</c>
/// (map over the FULL projection tree, then check against the REAL marker stream) → <c>RootlistOps.TryBuildMove</c> +
/// <c>ApplyLocally</c> — and asserts the resulting rootlist ORDER, not an intermediate opinion about it.</para>
///
/// <para>Both representations are built from ONE description: the marker stream by the production parser
/// (<c>RootlistTreeBuilder.EntriesFromUris</c>) and the projection by the production projector
/// (<c>SidebarProjection.Build</c>, fully flattened) over the tree that same parser produces. A fixture that hand-wrote
/// both could hide the very drift this is here to prevent.</para>
/// </summary>
public class RootlistDropScenarioTests
{
    // ── the fixture ─────────────────────────────────────────────────────────────────────────────────────────────────

    const string Root = "root";        // group id of "root folder updated name"
    const string Named = "named";      // group id of "named folder update"
    const string RootName = "root folder updated name";
    const string NamedName = "named folder update";

    static string Pl(string slug) => "spotify:playlist:" + slug;

    /// <summary>The user's rootlist, as the SERVER holds it.</summary>
    static IReadOnlyList<RootlistEntry> Markers() => RootlistTreeBuilder.EntriesFromUris(new[]
    {
        RootlistOps.StartGroupUri(Root, RootName),
        RootlistOps.StartGroupUri(Named, NamedName),
        Pl("nine"),
        RootlistOps.EndGroupUri(Named),
        Pl("updated"),
        RootlistOps.EndGroupUri(Root),
        Pl("tens"), Pl("careless"), Pl("nineties"), Pl("lol"), Pl("hsm"),
    });

    /// <summary>The FULL depth-first flattened projection of that stream — collapsed subtrees included, which is what
    /// <c>SidebarProjectionInput.PlaylistTree</c> is and what the drop mapper must resolve against.</summary>
    static List<SidebarLibraryEntry> Tree()
    {
        var nodes = RootlistTreeBuilder.Build(Markers(), uri => new PlaylistSummary(uri, NameOf(uri), "", 0, null));
        var into = new List<SidebarLibraryEntry>(16);
        SidebarProjection.Build(into, SidebarEntryKindMask.PlaylistTree, nodes,
                                Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Show>(),
                                null, null, null, includeFolderChildren: true);
        return into;
    }

    static string NameOf(string uri) => uri switch
    {
        "spotify:playlist:nine" => "#9",
        "spotify:playlist:updated" => "updated playlist name",
        "spotify:playlist:tens" => "10's",
        "spotify:playlist:careless" => "Careless",
        "spotify:playlist:nineties" => "90's",
        "spotify:playlist:lol" => "LoL",
        _ => "HSM",
    };

    /// <summary>The row order a marker stream reads as, with folders as <c>[name]</c> — the assertion shape every
    /// scenario below lands on, because ORDER is the thing the user was watching go wrong.</summary>
    static string[] Order(IReadOnlyList<RootlistEntry> entries)
    {
        var rows = new List<string>(entries.Count);
        foreach (var e in entries)
        {
            if (e.Kind == 0) rows.Add(NameOf(e.Uri));
            else if (e.Kind == 1) rows.Add("[" + (e.GroupName ?? "") + "");
            else rows.Add("]");
        }
        return rows.ToArray();
    }

    static SidebarLibraryEntry Entry(IReadOnlyList<SidebarLibraryEntry> tree, string name)
    {
        foreach (var e in tree) if (e.Name == name) return e;
        throw new Xunit.Sdk.XunitException("no such row: " + name);
    }

    static RootlistItemRef Ref(IReadOnlyList<SidebarLibraryEntry> tree, string name)
    {
        var e = Entry(tree, name);
        return RootlistTreeNav.RefOf(in e);
    }

    /// <summary>The row facts the pane publishes for one tree row, straight off the (fully expanded) tree — the same
    /// shape <c>SidebarPaneSlot.TreeRowFacts</c> builds from the plan.</summary>
    static SidebarRowFacts Facts(IReadOnlyList<SidebarLibraryEntry> tree, int i, bool sourceIsSelf = false)
    {
        var e = tree[i];
        int nextDepth = i + 1 < tree.Count ? tree[i + 1].Depth : 0;
        return new SidebarRowFacts(
            IsFolder: e.IsFolder, FolderExpanded: e.IsFolder, FolderHasChildren: nextDepth > e.Depth,
            Depth: e.Depth, NextVisibleDepth: nextDepth, CenterAccepts: e.IsFolder,
            SourceIsSelf: sourceIsSelf, SortedNonCustom: false, RootlistLoaded: true);
    }

    /// <summary>Resolve a pointer over row <paramref name="i"/> and run the pane's whole decision on it.</summary>
    static SidebarDropSlot Decide(IReadOnlyList<SidebarLibraryEntry> tree, IReadOnlyList<RootlistEntry> markers,
                                  string sourceName, int i, float t, float x, out RootlistSlotTarget target)
    {
        var source = Ref(tree, sourceName);
        bool self = tree[i].Name == sourceName;
        var cue = RootlistSlotResolver.Resolve(i, t, x, 44f, Facts(tree, i, self), SidebarDropSlot.None);
        return RootlistDropDecision.Refine(in cue, tree[i].Id, tree, markers, [source], out target);
    }

    /// <summary>Apply the move a decided slot commits, and return the resulting rootlist. The op is built by the
    /// production builder and applied by the PRODUCTION applier (<c>PlaylistDiffApplier</c>, the same index-MOV
    /// semantics the server runs), so this asserts the order the user would actually see.</summary>
    static IReadOnlyList<RootlistEntry> Apply(IReadOnlyList<RootlistEntry> markers, RootlistItemRef source,
                                              in RootlistSlotTarget target)
    {
        Assert.True(RootlistOps.TryBuildMove(markers, source, target.Ref, target.Placement, out var op, out var reason),
                    "the decided target must build an op; reason = " + reason);
        var rows = new List<PlaylistMember>(markers.Count);
        foreach (var e in markers) rows.Add(new PlaylistMember("", e.Uri, null, e.AddedAtMs));
        PlaylistDiffApplier.Apply(rows, new[] { op! });
        var uris = new List<string>(rows.Count);
        foreach (var r in rows) uris.Add(r.ItemUri);
        return RootlistTreeBuilder.EntriesFromUris(uris);
    }

    // ── F1 · the flyout's "Already there" for a perfectly legal Into ────────────────────────────────────────────────

    [Fact]
    public void F1_FilingAPlaylistIntoASiblingFolder_IsLegalAndLandsInside()
    {
        var tree = Tree();
        var markers = Markers();
        // The gesture from screenshot #14: "updated playlist name" onto "named folder update". The deleted flattened
        // check mapped Inside to the folder's END index — which in a list with no end markers is the row right after
        // its last child — and called it a no-op. The real stream has an end marker there, and this is a real move.
        var check = RootlistDropDecision.Check(markers, Ref(tree, "updated playlist name"),
                                               Ref(tree, NamedName), RootlistDropPlacement.Inside);
        Assert.Equal(RootlistMoveCheck.Ok, check);
        Assert.Equal(SidebarDropRefusal.None, RootlistDropDecision.RefusalFor(check));

        // …and through the POINTER: the dead centre of the folder row is the Into plate.
        int row = tree.IndexOf(Entry(tree, NamedName));
        var slot = Decide(tree, markers, "updated playlist name", row, t: 0.5f, x: 200f, out var target);
        Assert.Equal(SidebarDropKind.Into, slot.Kind);
        Assert.Equal(RootlistDropPlacement.Inside, target.Placement);
        Assert.Equal(NamedName, target.DestinationName);           // the toast names the folder it lands IN
        Assert.False(target.Deposit);                               // a folder centre is a MOVE, not a track copy

        Assert.Equal(
            ["[" + RootName, "[" + NamedName, "#9", "updated playlist name", "]", "]",
             "10's", "Careless", "90's", "LoL", "HSM"],
            Order(Apply(markers, Ref(tree, "updated playlist name"), in target)));
    }

    // ── F2 · the top band of a folder header, at the folder's own depth ─────────────────────────────────────────────

    [Fact]
    public void F2_BeforeTheFolderHeader_LandsTheItemBeforeThatFolderInsideTheSameParent()
    {
        var tree = Tree();
        var markers = Markers();
        // Screenshot #15: #9 dragged to the TOP band of "named folder update". The line is drawn at the folder's own
        // depth (1) and the drop lands #9 immediately before it — still inside "root folder updated name".
        int row = tree.IndexOf(Entry(tree, NamedName));
        var slot = Decide(tree, markers, "#9", row, t: 0.02f, x: 200f, out var target);
        Assert.Equal(SidebarDropKind.Before, slot.Kind);
        Assert.Equal(1, slot.Depth);
        Assert.Equal(RootlistDropPlacement.Before, target.Placement);
        Assert.Equal(RootName, target.DestinationName);             // the parent it ends up in

        Assert.Equal(
            ["[" + RootName, "#9", "[" + NamedName, "]", "updated playlist name", "]",
             "10's", "Careless", "90's", "LoL", "HSM"],
            Order(Apply(markers, Ref(tree, "#9"), in target)));
    }

    [Fact]
    public void F2_TheBottomBandOfAnExpandedFolder_IsItsFirstChildSlot()
    {
        var tree = Tree();
        var markers = Markers();
        // The one slot that needs the FULL tree to map: "first child of this folder". The pane used to read the next
        // VISIBLE plan row, so the slot resolved to nothing whenever that child was inside a collapsed subtree — and
        // an unmappable slot was still ARMED, which is the drop that silently did nothing.
        int row = tree.IndexOf(Entry(tree, RootName));
        var slot = Decide(tree, markers, "LoL", row, t: 0.98f, x: 200f, out var target);
        Assert.Equal(SidebarDropKind.Before, slot.Kind);
        Assert.Equal(1, slot.Depth);
        Assert.Equal(RootName, target.DestinationName);

        Assert.Equal(
            ["[" + RootName, "LoL", "[" + NamedName, "#9", "]", "updated playlist name", "]",
             "10's", "Careless", "90's", "HSM"],
            Order(Apply(markers, Ref(tree, "LoL"), in target)));
    }

    // ── F3 · the gap under the folder's last child: depth 1 stays in, depth 0 gets out ──────────────────────────────

    [Fact]
    public void F3_AfterTheLastChild_AtTheChildsOwnDepth_LandsInsideTheFolder()
    {
        var tree = Tree();
        var markers = Markers();
        // Pointer over the LABEL (x past the ladder) ⇒ depth 1 ⇒ "after updated playlist name", i.e. root's new last
        // child. The toast names the folder it lands in, which is the row's parent here.
        int row = tree.IndexOf(Entry(tree, "updated playlist name"));
        var slot = Decide(tree, markers, "LoL", row, t: 0.98f, x: 200f, out var target);
        Assert.Equal((SidebarDropKind.After, 1), (slot.Kind, slot.Depth));
        Assert.Equal(RootlistDropPlacement.After, target.Placement);
        Assert.Equal(RootName, target.DestinationName);

        Assert.Equal(
            ["[" + RootName, "[" + NamedName, "#9", "]", "updated playlist name", "LoL", "]",
             "10's", "Careless", "90's", "HSM"],
            Order(Apply(markers, Ref(tree, "LoL"), in target)));
    }

    [Fact]
    public void F3_AfterTheLastChild_SlidLeftToDepth0_LandsAfterTheFolderAtTopLevel()
    {
        var tree = Tree();
        var markers = Markers();
        // THE OUTDENT, and the one gesture the shifted origin made unreachable: with `IndentFor` the depth-0 band
        // needed x < 12; on the row's real content ladder it is x < 31, a deliberate slide the hand can make.
        int row = tree.IndexOf(Entry(tree, "updated playlist name"));
        var slot = Decide(tree, markers, "LoL", row, t: 0.98f, x: 26f, out var target);
        Assert.Equal((SidebarDropKind.After, 0), (slot.Kind, slot.Depth));
        // Expressed against the FOLDER, not against the child — that is what puts it past root's end marker.
        Assert.Equal(new RootlistItemRef(Root, IsFolder: true), target.Ref);
        Assert.Equal(RootlistDropPlacement.After, target.Placement);
        Assert.Equal("", target.DestinationName);                   // "" ⇒ the toast says "Moved to Your Library"
        Assert.Equal(RootName, target.AnchorName);                  // …while the chip says "Move out of root folder…"

        Assert.Equal(
            ["[" + RootName, "[" + NamedName, "#9", "]", "updated playlist name", "]",
             "LoL", "10's", "Careless", "90's", "HSM"],
            Order(Apply(markers, Ref(tree, "LoL"), in target)));
    }

    [Fact]
    public void F3_TheOutdentClimbsExactlyAsFarAsTheDepthTheHandPicked()
    {
        var tree = Tree();
        var markers = Markers();
        // The gap under "#9" (depth 2) is bounded BELOW by the next visible row's depth: "updated playlist name" sits
        // at depth 1, so this gap can only mean depth 2 or depth 1 — it cannot mean "top level", because a row at the
        // top level cannot be inserted between two rows that are both inside the folder. The depth the hand picks is
        // the number of levels the mapper climbs, and nothing else decides it.
        int row = tree.IndexOf(Entry(tree, "#9"));
        Assert.Equal((1, 2), RootlistSlotResolver.DepthRange(Facts(tree, row)));

        var stay = Decide(tree, markers, "HSM", row, t: 0.98f, x: 200f, out var stayTarget);
        Assert.Equal((SidebarDropKind.After, 2), (stay.Kind, stay.Depth));
        Assert.Equal(new RootlistItemRef(Pl("nine"), false), stayTarget.Ref);
        Assert.Equal(NamedName, stayTarget.DestinationName);

        var climb = Decide(tree, markers, "HSM", row, t: 0.98f, x: 38f, out var climbTarget);
        Assert.Equal((SidebarDropKind.After, 1), (climb.Kind, climb.Depth));
        Assert.Equal(new RootlistItemRef(Named, IsFolder: true), climbTarget.Ref);
        Assert.Equal(RootName, climbTarget.DestinationName);      // out of "named", into "root"
        Assert.Equal(
            ["[" + RootName, "[" + NamedName, "#9", "]", "HSM", "updated playlist name", "]",
             "10's", "Careless", "90's", "LoL"],
            Order(Apply(markers, Ref(tree, "HSM"), in climbTarget)));
    }

    [Fact]
    public void F3_ATwoLevelOutdent_ClimbsBothFolders()
    {
        // The same gesture where the tree DOES allow it: with "updated playlist name" gone, "#9" is the last row of
        // both folders at once, so the gap under it spans depth 2 → 0 and the deepest pick must climb TWO containing
        // folders — landing after ROOT's end marker, not after "named folder update" and not back inside either.
        var markers = RootlistTreeBuilder.EntriesFromUris(new[]
        {
            RootlistOps.StartGroupUri(Root, RootName),
            RootlistOps.StartGroupUri(Named, NamedName),
            Pl("nine"),
            RootlistOps.EndGroupUri(Named),
            RootlistOps.EndGroupUri(Root),
            Pl("tens"), Pl("lol"),
        });
        var nodes = RootlistTreeBuilder.Build(markers, uri => new PlaylistSummary(uri, NameOf(uri), "", 0, null));
        var tree = new List<SidebarLibraryEntry>(8);
        SidebarProjection.Build(tree, SidebarEntryKindMask.PlaylistTree, nodes,
                                Array.Empty<Album>(), Array.Empty<Artist>(), Array.Empty<Show>(),
                                null, null, null, includeFolderChildren: true);

        int row = tree.IndexOf(Entry(tree, "#9"));
        Assert.Equal((0, 2), RootlistSlotResolver.DepthRange(Facts(tree, row)));
        var slot = Decide(tree, markers, "LoL", row, t: 0.98f, x: 26f, out var target);
        Assert.Equal((SidebarDropKind.After, 0), (slot.Kind, slot.Depth));
        Assert.Equal(new RootlistItemRef(Root, IsFolder: true), target.Ref);
        Assert.Equal("", target.DestinationName);                 // "Moved to Your Library"
        Assert.Equal(RootName, target.AnchorName);                // "Move out of root folder updated name"
        Assert.Equal(
            ["[" + RootName, "[" + NamedName, "#9", "]", "]", "LoL", "10's"],
            Order(Apply(markers, Ref(tree, "LoL"), in target)));
    }

    // ── F4 · the adjacent no-op refuses, and refuses out loud ───────────────────────────────────────────────────────

    [Fact]
    public void F4_AnAdjacentNoOp_RefusesAndIsNeverArmed()
    {
        var tree = Tree();
        var markers = Markers();
        // "Careless" dropped just under "10's" is where it already is. It must refuse — line and plate BOTH off, a
        // sentence on the chip — rather than arm a cue and post a success toast for a move that never happened.
        int row = tree.IndexOf(Entry(tree, "10's"));
        var slot = Decide(tree, markers, "Careless", row, t: 0.98f, x: 200f, out _);
        Assert.Equal(SidebarDropRefusal.NoOp, slot.Refusal);
        Assert.Equal(SidebarDropKind.None, slot.Kind);
        Assert.False(slot.DrawsLine || slot.DrawsPlate);

        // …and the same gesture from the other side (before the row below me).
        int below = tree.IndexOf(Entry(tree, "90's"));
        Assert.Equal(SidebarDropRefusal.NoOp,
                     Decide(tree, markers, "Careless", below, t: 0.02f, x: 200f, out _).Refusal);
    }

    [Fact]
    public void F4_TheRefusedMove_IsAlsoRefusedByTheWriter_NotSilentlySkipped()
    {
        // The other half of the same failure: even if a refused destination somehow reached the seam, the op cannot be
        // built — and that must be a typed THROW, not the quiet `return` that let the caller toast success.
        var markers = Markers();
        Assert.False(RootlistOps.TryBuildMove(markers, new RootlistItemRef(Pl("careless"), false),
                                              new RootlistItemRef(Pl("tens"), false),
                                              RootlistDropPlacement.After, out _, out var reason));
        Assert.Equal(RootlistMoveCheck.NoOp, reason);
    }

    // ── the structural invariants, over EVERY armed slot the resolver can produce ───────────────────────────────────

    /// <summary>Every (row, zone, depth) the geometry can produce, for every possible drag source.</summary>
    public static IEnumerable<(int Row, float T, float X)> Positions(int rows)
    {
        foreach (int i in Rows(rows))
            foreach (float t in new[] { 0f, 0.05f, 0.3f, 0.5f, 0.7f, 0.95f, 1f })
                foreach (float x in new[] { -20f, 0f, 25f, 31f, 37f, 43f, 49f, 120f, 400f })
                    yield return (i, t, x);
    }

    static IEnumerable<int> Rows(int rows)
    {
        for (int i = 0; i < rows; i++) yield return i;
    }

    [Fact]
    public void Totality_EveryArmedSlotHasAMapping_AndEveryDisarmedOneHasAReason()
    {
        var tree = Tree();
        var markers = Markers();
        int armed = 0, refused = 0;
        foreach (var src in tree)
        {
            var source = RootlistTreeNav.RefOf(in src);
            foreach (var (i, t, x) in Positions(tree.Count))
            {
                bool self = tree[i].Id == src.Id;
                var cue = RootlistSlotResolver.Resolve(i, t, x, 44f, Facts(tree, i, self), SidebarDropSlot.None);
                var slot = RootlistDropDecision.Refine(in cue, tree[i].Id, tree, markers, [source], out var target);

                if (slot.IsArmed)
                {
                    armed++;
                    // ARMED ⇒ MAPPED. The published cue is a promise, and the commit runs this very decision again.
                    Assert.True(RootlistSlotMapper.TryMap(in slot, tree[i].Id, tree, out var again));
                    Assert.Equal(target, again);
                    Assert.True(target.Deposit || target.Ref.Key.Length > 0);
                    // A real ordering that is armed must BUILD — no armed slot may reach a writer that refuses it.
                    if (!target.Deposit)
                        Assert.True(RootlistOps.TryBuildMove(markers, source, target.Ref, target.Placement, out _, out _));
                }
                else
                {
                    refused++;
                    // NOT ARMED ⇒ THERE IS A REASON. Never a blank slot the surface cannot explain.
                    Assert.NotEqual(SidebarDropRefusal.None, slot.Refusal);
                    Assert.False(slot.DrawsLine || slot.DrawsPlate);
                }
            }
        }
        Assert.True(armed > 0 && refused > 0, "the sweep must exercise both arms");
    }

    [Fact]
    public void TheTreeEndRow_LandsAfterTheTrailingSubtree_AtTopLevel()
    {
        var tree = Tree();
        var markers = Markers();
        // The synthetic TreeEnd gutter stands for no entity: it is expressed against the LAST TOP-LEVEL entry, whose
        // exclusive range end is past a trailing folder's whole subtree.
        var facts = new SidebarRowFacts(false, false, false, 0, 0, false, false, false, true) { IsListEnd = true };
        var cue = RootlistSlotResolver.Resolve(tree.Count, 0.5f, 100f, 24f, in facts, SidebarDropSlot.None);
        Assert.Equal(SidebarDropKind.EndOfList, cue.Kind);
        var slot = RootlistDropDecision.Refine(in cue, "", tree, markers, [Ref(tree, "#9")], out var target);
        Assert.True(slot.IsArmed);
        Assert.Equal(new RootlistItemRef(Pl("hsm"), false), target.Ref);
        Assert.Equal("", target.DestinationName);

        Assert.Equal(
            ["[" + RootName, "[" + NamedName, "]", "updated playlist name", "]",
             "10's", "Careless", "90's", "LoL", "HSM", "#9"],
            Order(Apply(markers, Ref(tree, "#9"), in target)));

        // …and for the row that is ALREADY last, it is the no-op it looks like.
        var already = RootlistDropDecision.Refine(in cue, "", tree, markers, [Ref(tree, "HSM")], out _);
        Assert.Equal(SidebarDropRefusal.Self, already.Refusal);
    }

    [Fact]
    public void AFolderDraggedOverItsOwnSubtree_RefusesWithTheCycleSentence()
    {
        var tree = Tree();
        var markers = Markers();
        // The pane no longer re-derives this from the visible plan's ParentFolderId chain; the marker stream answers
        // it per resolved destination, so the row shows the reason instead of a dead cursor.
        int row = tree.IndexOf(Entry(tree, "#9"));
        Assert.Equal(SidebarDropRefusal.IntoDescendant,
                     Decide(tree, markers, RootName, row, t: 0.5f, x: 200f, out _).Refusal);
        Assert.Equal(SidebarDropRefusal.IntoDescendant,
                     Decide(tree, markers, RootName, row, t: 0.02f, x: 200f, out _).Refusal);
    }

    [Fact]
    public void WithNoMarkerStream_NothingIsEverArmed()
    {
        var tree = Tree();
        // A build with no store has no order to write into. Refusing is the truth; arming would be a promise the
        // writer cannot keep.
        foreach (var (i, t, x) in Positions(tree.Count))
        {
            var cue = RootlistSlotResolver.Resolve(i, t, x, 44f, Facts(tree, i), SidebarDropSlot.None);
            var slot = RootlistDropDecision.Refine(in cue, tree[i].Id, tree, null, [Ref(tree, "LoL")], out var target);
            if (target.Deposit) continue;                  // a track copy needs no rootlist order at all
            Assert.False(slot.IsArmed);
            Assert.NotEqual(SidebarDropRefusal.None, slot.Refusal);
        }
    }

    [Fact]
    public void OneDrop_ProducesExactlyOneMove()
    {
        var tree = Tree();
        var markers = Markers();
        // The commit consumes ONE decided target and issues ONE `MoveRootlistItemAsync`. Pinned here as: every surface
        // the pane commits through — a tree row, a folder row, the TreeEnd gutter, the rail/flyout folder tile —
        // resolves to exactly one (ref, placement), and applying it moves exactly one subtree.
        var cases = new List<(string What, RootlistSlotTarget Target)>();

        Decide(tree, markers, "LoL", tree.IndexOf(Entry(tree, "Careless")), 0.98f, 200f, out var afterRow);
        cases.Add(("playlist row", afterRow));
        Decide(tree, markers, "LoL", tree.IndexOf(Entry(tree, NamedName)), 0.5f, 200f, out var intoFolder);
        cases.Add(("folder row", intoFolder));

        var endFacts = new SidebarRowFacts(false, false, false, 0, 0, false, false, false, true) { IsListEnd = true };
        var endCue = RootlistSlotResolver.Resolve(tree.Count, 0.5f, 100f, 24f, in endFacts, SidebarDropSlot.None);
        RootlistDropDecision.Refine(in endCue, "", tree, markers, [Ref(tree, "LoL")], out var endTarget);
        cases.Add(("tree end", endTarget));

        // The rail folder tile / flyout row: Into, and only Into.
        var tile = new SidebarDropSlot(0, SidebarDropKind.Into, 0, SidebarDropRefusal.None);
        Assert.True(RootlistSlotMapper.TryMap(in tile, Entry(tree, RootName).Id, tree, out var tileTarget));
        cases.Add(("rail folder tile", tileTarget));

        foreach (var (what, target) in cases)
        {
            Assert.True(target.Ref.Key.Length > 0, what);
            Assert.True(RootlistOps.TryBuildMove(markers, Ref(tree, "LoL"), target.Ref, target.Placement,
                                                 out var op, out var reason), what + ": " + reason);
            Assert.Equal(PlaylistOpKind.Move, op!.Kind);
            Assert.Equal(1, op!.Length);                                   // one leaf, one row — exactly one move
            Assert.Equal(markers.Count, Apply(markers, Ref(tree, "LoL"), in target).Count);
        }
    }


    // ── the BATCH: a multi-select is ONE decision and ONE move, never N drops ───────────────────────────────────────

    /// <summary>The batch refine over a real selection: legal batches arm the SAME cue a single item does, and both
    /// batch-only refusals still speak — <c>Self</c> when the pointer is over one of the rows the drag is carrying
    /// (the resolver's <c>SourceIsSelf</c> fact), <c>IntoDescendant</c> when the target lives inside a selected
    /// folder.</summary>
    [Fact]
    public void ABatchArmsTheSameCue_AndKeepsBothRefusalSentences()
    {
        var tree = Tree();
        var markers = Markers();
        // The selection: the root folder and a playlist far below it. Normalisation is what keeps "#9" (which rides
        // inside that folder) out of the batch.
        var selection = RootlistSelection.Normalize(tree, [Entry(tree, RootName).Id, Entry(tree, "#9").Id,
                                                           Entry(tree, "LoL").Id]);
        Assert.Equal([RootName, "LoL"], Names(selection));
        var sources = RootlistSelection.Refs(selection);

        // LEGAL: below "HSM", at the top level. One armed line, exactly as a single item gets.
        int hsm = tree.IndexOf(Entry(tree, "HSM"));
        var cue = RootlistSlotResolver.Resolve(hsm, 0.98f, 200f, 44f, Facts(tree, hsm), SidebarDropSlot.None);
        var slot = RootlistDropDecision.Refine(in cue, tree[hsm].Id, tree, markers, sources, out var target);
        Assert.Equal(SidebarDropKind.After, slot.Kind);
        Assert.Equal(SidebarDropRefusal.None, slot.Refusal);
        Assert.Equal(RootlistDropPlacement.After, target.Placement);

        // SELF: the pointer is over one of the dragged rows. The refusal comes from the resolver's payload fact —
        // `WaveeResourceDrop.IsSource` feeds it — and it fires BEFORE the marker stream is ever consulted, which is
        // why "into myself" and "before myself" can be two different sentences.
        int lol = tree.IndexOf(Entry(tree, "LoL"));
        var selfCue = RootlistSlotResolver.Resolve(lol, 0.98f, 200f, 44f, Facts(tree, lol, sourceIsSelf: true),
                                                   SidebarDropSlot.None);
        Assert.Equal(SidebarDropRefusal.Self, selfCue.Refusal);
        Assert.False(RootlistDropDecision.Refine(in selfCue, tree[lol].Id, tree, markers, sources, out _).IsArmed);

        // INTO A DESCENDANT: "named folder update" is inside the selected root folder.
        int named = tree.IndexOf(Entry(tree, NamedName));
        var intoCue = RootlistSlotResolver.Resolve(named, 0.5f, 200f, 44f, Facts(tree, named), SidebarDropSlot.None);
        Assert.Equal(SidebarDropKind.Into, intoCue.Kind);
        var refused = RootlistDropDecision.Refine(in intoCue, tree[named].Id, tree, markers, sources, out _);
        Assert.Equal(SidebarDropRefusal.IntoDescendant, refused.Refusal);
        Assert.False(refused.IsArmed);
    }

    /// <summary>ONE drop issues ONE <c>MoveRootlistItemsAsync</c> — the batch is a single ordered move list, and its
    /// ops are built and applied as one Delta.</summary>
    [Fact]
    public void ABatchDrop_IssuesExactlyOneMoveList_AndKeepsRelativeOrder()
    {
        var tree = Tree();
        var markers = Markers();
        var sources = RootlistSelection.Refs(
            RootlistSelection.Normalize(tree, [Entry(tree, "10's").Id, Entry(tree, "90's").Id]));

        int careless = tree.IndexOf(Entry(tree, "Careless"));
        var cue = RootlistSlotResolver.Resolve(careless, 0.02f, 200f, 44f, Facts(tree, careless), SidebarDropSlot.None);
        var slot = RootlistDropDecision.Refine(in cue, tree[careless].Id, tree, markers, sources, out var target);
        Assert.Equal(SidebarDropKind.Before, slot.Kind);

        // ONE list, one per source, all against the one anchor — this is what the commit hands the seam.
        var moves = RootlistBatchOrder.For(sources, target.Ref, target.Placement);
        Assert.Equal(2, moves.Count);
        foreach (var m in moves)
        {
            Assert.Equal(target.Ref, m.Target);
            Assert.Equal(target.Placement, m.Placement);
        }

        // …and the order the user sees: 10's stays ahead of 90's, with Careless after both.
        Assert.True(RootlistOps.TryBuildMoves(markers, moves, out var ops, out var reason), "reason = " + reason);
        Assert.Equal(
            ["[" + RootName, "[" + NamedName, "#9", "]", "updated playlist name", "]",
             "10's", "90's", "Careless", "LoL", "HSM"],
            Order(ApplyOps(markers, ops)));
    }

    static IReadOnlyList<RootlistEntry> ApplyOps(IReadOnlyList<RootlistEntry> markers, IReadOnlyList<PlaylistOp> ops)
    {
        var rows = new List<PlaylistMember>(markers.Count);
        foreach (var e in markers) rows.Add(new PlaylistMember("", e.Uri, null, e.AddedAtMs));
        PlaylistDiffApplier.Apply(rows, ops);
        var uris = new List<string>(rows.Count);
        foreach (var r in rows) uris.Add(r.ItemUri);
        return RootlistTreeBuilder.EntriesFromUris(uris);
    }

    static string[] Names(IReadOnlyList<SidebarLibraryEntry> entries)
    {
        var names = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++) names[i] = entries[i].Name;
        return names;
    }
}
