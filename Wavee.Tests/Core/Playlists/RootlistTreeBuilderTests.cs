using FluentAssertions;
using Wavee.Core.Playlists;
using Xunit;

namespace Wavee.Tests.Core.Playlists;

/// <summary>
/// Tests for RootlistTreeBuilder.
/// Locks in ID-matched pop, self-heal at EOF, stray-end-group ignore, and most
/// importantly the **arrival-order preservation** of folders interleaved with
/// playlists at the same level.
/// </summary>
public class RootlistTreeBuilderTests
{
    [Fact]
    public void Build_EmptyList_ReturnsEmptyRoot()
    {
        var tree = RootlistTreeBuilder.Build(Array.Empty<RootlistEntry>());

        tree.Root.Children.Should().BeEmpty();
    }

    [Fact]
    public void Build_FlatPlaylists_AllInRoot()
    {
        var items = new RootlistEntry[]
        {
            new RootlistPlaylist("spotify:playlist:A"),
            new RootlistPlaylist("spotify:playlist:B"),
            new RootlistPlaylist("spotify:playlist:C")
        };

        var tree = RootlistTreeBuilder.Build(items);

        tree.Root.Children.Should().HaveCount(3);
        tree.Root.Children.Select(PlaylistUri).Should().Equal(
            "spotify:playlist:A", "spotify:playlist:B", "spotify:playlist:C");
    }

    [Fact]
    public void Build_SimpleFolderWithPlaylist_PlaylistInsideFolder()
    {
        var items = new RootlistEntry[]
        {
            new RootlistFolderStart("1e65", "New Folder"),
            new RootlistPlaylist("spotify:playlist:A"),
            new RootlistFolderEnd("1e65")
        };

        var tree = RootlistTreeBuilder.Build(items);

        tree.Root.Children.Should().HaveCount(1);
        var folder = AsFolder(tree.Root.Children[0]);
        folder.Id.Should().Be("1e65");
        folder.Name.Should().Be("New Folder");
        folder.Children.Should().HaveCount(1);
        PlaylistUri(folder.Children[0]).Should().Be("spotify:playlist:A");
    }

    [Fact]
    public void Build_BriefPayloadNesting_Matches4LevelStructure()
    {
        // ============================================================
        // WHY: Mirrors the user-supplied rootlist sample from the brief —
        // folder 1e65 with one playlist, folder 8f04 containing nested
        // folder 25ea with one playlist and a sibling playlist, then a
        // top-level playlist after all folders close.
        // ============================================================
        var items = new RootlistEntry[]
        {
            new RootlistFolderStart("1e65", "New Folder"),
            new RootlistPlaylist("spotify:playlist:inner-1e65"),
            new RootlistFolderEnd("1e65"),
            new RootlistFolderStart("8f04", "New Folder"),
            new RootlistFolderStart("25ea", "New Folder"),
            new RootlistPlaylist("spotify:playlist:inner-25ea"),
            new RootlistFolderEnd("25ea"),
            new RootlistPlaylist("spotify:playlist:sibling-of-25ea"),
            new RootlistFolderEnd("8f04"),
            new RootlistPlaylist("spotify:playlist:top-level")
        };

        var tree = RootlistTreeBuilder.Build(items);

        // Root: [folder 1e65, folder 8f04, playlist top-level] in order
        tree.Root.Children.Should().HaveCount(3);
        AsFolder(tree.Root.Children[0]).Id.Should().Be("1e65");
        AsFolder(tree.Root.Children[1]).Id.Should().Be("8f04");
        PlaylistUri(tree.Root.Children[2]).Should().Be("spotify:playlist:top-level");

        // Folder 1e65: one playlist
        var folder1e65 = AsFolder(tree.Root.Children[0]);
        folder1e65.Children.Should().HaveCount(1);
        PlaylistUri(folder1e65.Children[0]).Should().Be("spotify:playlist:inner-1e65");

        // Folder 8f04: [nested 25ea, sibling playlist] in order
        var folder8f04 = AsFolder(tree.Root.Children[1]);
        folder8f04.Children.Should().HaveCount(2);
        AsFolder(folder8f04.Children[0]).Id.Should().Be("25ea");
        PlaylistUri(folder8f04.Children[1]).Should().Be("spotify:playlist:sibling-of-25ea");

        // Folder 25ea: one inner playlist
        var folder25ea = AsFolder(folder8f04.Children[0]);
        folder25ea.Children.Should().HaveCount(1);
        PlaylistUri(folder25ea.Children[0]).Should().Be("spotify:playlist:inner-25ea");
    }

    [Fact]
    public void Build_StrayEndGroup_IsIgnoredAndRootIntact()
    {
        var items = new RootlistEntry[]
        {
            new RootlistPlaylist("spotify:playlist:A"),
            new RootlistFolderEnd("nonexistent"),
            new RootlistPlaylist("spotify:playlist:B")
        };

        var tree = RootlistTreeBuilder.Build(items);

        tree.Root.Children.Should().HaveCount(2);
        tree.Root.Children.Select(PlaylistUri).Should().Equal(
            "spotify:playlist:A", "spotify:playlist:B");
    }

    [Fact]
    public void Build_UnclosedFolderAtEof_IsAutoClosed()
    {
        var items = new RootlistEntry[]
        {
            new RootlistFolderStart("abc", "Orphan"),
            new RootlistPlaylist("spotify:playlist:inside-orphan")
        };

        var tree = RootlistTreeBuilder.Build(items);

        tree.Root.Children.Should().HaveCount(1);
        var folder = AsFolder(tree.Root.Children[0]);
        folder.Id.Should().Be("abc");
        folder.Children.Should().HaveCount(1);
        PlaylistUri(folder.Children[0]).Should().Be("spotify:playlist:inside-orphan");
    }

    [Fact]
    public void Build_MismatchedEndGroupId_PopsByIdNotBlindly()
    {
        // ============================================================
        // WHY: [start A, start B, end A] should close A (and force-close
        // B as a child of A). Blind pop would close B and leave A open,
        // which is the cascading-corruption bug.
        // ============================================================
        var items = new RootlistEntry[]
        {
            new RootlistFolderStart("A", "Outer"),
            new RootlistFolderStart("B", "Inner"),
            new RootlistPlaylist("spotify:playlist:inner-B"),
            new RootlistFolderEnd("A") // closes A; B is auto-closed inside A
        };

        var tree = RootlistTreeBuilder.Build(items);

        tree.Root.Children.Should().HaveCount(1);
        var folderA = AsFolder(tree.Root.Children[0]);
        folderA.Id.Should().Be("A");
        folderA.Children.Should().HaveCount(1);
        var folderB = AsFolder(folderA.Children[0]);
        folderB.Id.Should().Be("B");
        folderB.Children.Should().HaveCount(1);
        PlaylistUri(folderB.Children[0]).Should().Be("spotify:playlist:inner-B");
    }

    [Fact]
    public void Build_OrderPreserved_FoldersAndPlaylistsInterleaved()
    {
        // ============================================================
        // WHY: This is the bug the user hit — folders rendered at the
        // bottom because the consumer flattened the tree by dumping all
        // playlists first, then all folders. Server order is authoritative
        // and the tree must preserve it exactly at every level.
        // ============================================================
        var items = new RootlistEntry[]
        {
            new RootlistPlaylist("spotify:playlist:P1"),
            new RootlistFolderStart("F1", "Folder 1"),
            new RootlistPlaylist("spotify:playlist:P2"),
            new RootlistFolderEnd("F1"),
            new RootlistPlaylist("spotify:playlist:P3"),
            new RootlistFolderStart("F2", "Folder 2"),
            new RootlistPlaylist("spotify:playlist:P4"),
            new RootlistFolderEnd("F2"),
            new RootlistPlaylist("spotify:playlist:P5")
        };

        var tree = RootlistTreeBuilder.Build(items);

        tree.Root.Children.Should().HaveCount(5);
        PlaylistUri(tree.Root.Children[0]).Should().Be("spotify:playlist:P1");
        AsFolder(tree.Root.Children[1]).Id.Should().Be("F1");
        PlaylistUri(tree.Root.Children[2]).Should().Be("spotify:playlist:P3");
        AsFolder(tree.Root.Children[3]).Id.Should().Be("F2");
        PlaylistUri(tree.Root.Children[4]).Should().Be("spotify:playlist:P5");
    }

    [Fact]
    public void Build_FolderBetweenTwoPlaylists_StaysBetweenThem()
    {
        // The smallest possible reproducer of the user's bug.
        var items = new RootlistEntry[]
        {
            new RootlistPlaylist("spotify:playlist:before"),
            new RootlistFolderStart("F", "Middle"),
            new RootlistFolderEnd("F"),
            new RootlistPlaylist("spotify:playlist:after")
        };

        var tree = RootlistTreeBuilder.Build(items);

        tree.Root.Children.Should().HaveCount(3);
        PlaylistUri(tree.Root.Children[0]).Should().Be("spotify:playlist:before");
        AsFolder(tree.Root.Children[1]).Id.Should().Be("F");
        PlaylistUri(tree.Root.Children[2]).Should().Be("spotify:playlist:after");
    }

    // ── helpers ──

    private static string PlaylistUri(RootlistChild child) =>
        child.Should().BeOfType<RootlistChildPlaylist>().Subject.Uri;

    private static RootlistNode AsFolder(RootlistChild child) =>
        child.Should().BeOfType<RootlistChildFolder>().Subject.Folder;
}
