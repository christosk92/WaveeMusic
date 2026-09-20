// ── Wavee.Tests/RootlistMarkerStreamTests.cs — the marker stream derived from the flattened projection tree ─────────
//
// Stage B (J1): the pane derives the rootlist marker stream back from `SidebarProjectionInput.PlaylistTree`
// (`RootlistMarkerStream.Build`) so every legality question — the drop cue, "Move to folder…", Alt+↑/↓ — is asked
// against the same facts the rows draw. These facts pin the stream's SHAPE (balanced start/end markers, depths, the
// trailing-folder close) and, more importantly, that the legality authority answers IDENTICALLY over the derived stream
// and over the hand-built wire-shaped fixture stream (`SidebarTreeFixture.Markers`). Pure: no scope, no engine.

using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;
using Xunit;

namespace Wavee.Tests;

public sealed class RootlistMarkerStreamTests
{
    static List<RootlistEntry> Built(IReadOnlyList<SidebarLibraryEntry> tree)
    {
        var into = new List<RootlistEntry>();
        RootlistMarkerStream.Build(tree, into);
        return into;
    }

    [Fact]
    public void Null_or_empty_tree_yields_an_empty_stream()
    {
        var into = new List<RootlistEntry> { new(0, 0, "stale", null, 0) };
        RootlistMarkerStream.Build(null, into);
        Assert.Empty(into);
        RootlistMarkerStream.Build(Array.Empty<SidebarLibraryEntry>(), into);
        Assert.Empty(into);
    }

    [Fact]
    public void Kinds_and_depths_match_the_wire_shaped_fixture()
    {
        var tree = SidebarTreeFixture.Tree();
        var built = Built(tree);
        var wire = SidebarTreeFixture.Markers();
        Assert.Equal(wire.Count, built.Count);
        for (int i = 0; i < wire.Count; i++)
        {
            Assert.Equal(wire[i].Kind, built[i].Kind);
            Assert.Equal(wire[i].Depth, built[i].Depth);
            Assert.Equal(i, built[i].Position);
        }
    }

    [Fact]
    public void Playlists_carry_their_uri_and_folders_their_projection_group_id()
    {
        var built = Built(SidebarTreeFixture.Tree());
        Assert.Equal(SidebarTreeFixture.PlaylistUriPrefix + "a", built[0].Uri);
        Assert.Equal(1, built[1].Kind);
        Assert.Equal("g", built[1].Uri);
        Assert.Equal("Chill", built[1].GroupName);
    }

    [Fact]
    public void Every_open_folder_is_closed_including_a_trailing_one()
    {
        var built = Built(SidebarTreeFixture.Tree());
        int balance = 0;
        foreach (var e in built)
        {
            if (e.Kind == 1) balance++;
            else if (e.Kind == 2) balance--;
            Assert.True(balance >= 0);
        }
        Assert.Equal(0, balance);
        Assert.Equal(2, built[^1].Kind);
    }

    [Fact]
    public void An_empty_folder_opens_and_closes_before_its_next_sibling()
    {
        var tree = new[]
        {
            SidebarTreeFixture.Folder("x", "Empty", 0),
            SidebarTreeFixture.Playlist("z", 0),
        };
        var built = Built(tree);
        Assert.Equal(new[] { 1, 2, 0 }, built.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public void Non_rootlist_kinds_are_skipped()
    {
        var route = SidebarLibraryEntry.ForRoute("liked", "Liked Songs");
        var tree = new[] { route, SidebarTreeFixture.Playlist("a", 0) };
        var built = Built(tree);
        Assert.Single(built);
        Assert.Equal(0, built[0].Kind);
    }

    [Theory]
    [InlineData("pl:a", false, "g", true, RootlistDropPlacement.Inside)]      // a playlist into a folder
    [InlineData("folder:g", true, "k", true, RootlistDropPlacement.Inside)]   // a folder into its own descendant
    [InlineData("pl:f", false, "k", true, RootlistDropPlacement.Inside)]      // already the last child: no-op
    [InlineData("pl:e", false, "pl:a", false, RootlistDropPlacement.Before)]  // out of a trailing folder
    [InlineData("pl:d", false, "h", true, RootlistDropPlacement.After)]       // after a trailing folder
    [InlineData("pl:b", false, "pl:b", false, RootlistDropPlacement.After)]   // onto itself
    public void Legality_over_the_derived_stream_matches_the_wire_stream(
        string sourceId, bool sourceIsFolder, string targetKey, bool targetIsFolder, RootlistDropPlacement placement)
    {
        var tree = SidebarTreeFixture.Tree();
        var source = Resolve(tree, sourceId, sourceIsFolder);
        var target = targetIsFolder
            ? new RootlistItemRef(targetKey, IsFolder: true)
            : Resolve(tree, targetKey, false);
        var viaWire = RootlistOps.CheckMove(SidebarTreeFixture.Markers(), source, target, placement);
        var viaDerived = RootlistOps.CheckMove(Built(tree), source, target, placement);
        Assert.Equal(viaWire, viaDerived);
    }

    [Fact]
    public void Picker_destinations_agree_over_both_streams()
    {
        var tree = SidebarTreeFixture.Tree();
        var viaWire = new List<RootlistFolderChoice>();
        var viaDerived = new List<RootlistFolderChoice>();
        RootlistTreeNav.PickerDestinations(tree, SidebarTreeFixture.Markers(), SidebarTreeFixture.Pl("b"), viaWire);
        RootlistTreeNav.PickerDestinations(tree, Built(tree), SidebarTreeFixture.Pl("b"), viaDerived);
        Assert.Equal(viaWire, viaDerived);
    }

    static RootlistItemRef Resolve(IReadOnlyList<SidebarLibraryEntry> tree, string shortId, bool folder)
    {
        if (folder) return new RootlistItemRef(shortId.StartsWith("folder:", StringComparison.Ordinal) ? shortId[7..] : shortId, true);
        string slug = shortId.StartsWith("pl:", StringComparison.Ordinal) ? shortId[3..] : shortId;
        return SidebarTreeFixture.Ref(tree, SidebarTreeFixture.Pl(slug));
    }
}
