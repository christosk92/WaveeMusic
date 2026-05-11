using FluentAssertions;
using Google.Protobuf;
using Wavee.Core.Playlists;
using Wavee.Protocol.Playlist;
using Xunit;

namespace Wavee.Tests.Core.Playlists;

/// <summary>
/// Tests for SelectedListContentMapper — the pure mapping layer between Spotify's
/// <see cref="SelectedListContent"/> protobuf and our <see cref="CachedPlaylist"/> /
/// <see cref="RootlistSnapshot"/> records.
/// </summary>
public class SelectedListContentMapperTests
{
    private static readonly DateTimeOffset FetchedAt = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MapRootlist_UnnamedFolderStart_IsAcceptedNotDropped()
    {
        // ============================================================
        // WHY: "spotify:start-group:{id}" with no name segment is a legal
        // minimal form. Previously it was silently dropped, which left its
        // matching end-group to pop a different folder and corrupt nesting.
        // ============================================================
        var content = new SelectedListContent
        {
            Contents = new ListItems
            {
                Pos = 0,
                Truncated = false
            }
        };
        content.Contents.Items.Add(new Item { Uri = "spotify:start-group:abc" }); // 3 segments, no name
        content.Contents.Items.Add(new Item { Uri = "spotify:playlist:xyz" });
        content.Contents.Items.Add(new Item { Uri = "spotify:end-group:abc" });
        // MetaItems must stay parallel-by-index
        content.Contents.MetaItems.Add(new MetaItem());
        content.Contents.MetaItems.Add(new MetaItem { Attributes = new ListAttributes { Name = "Inner" } });
        content.Contents.MetaItems.Add(new MetaItem());

        var snapshot = SelectedListContentMapper.MapRootlist(content, FetchedAt);

        snapshot.Items.Should().HaveCount(3);
        snapshot.Items[0].Should().BeOfType<RootlistFolderStart>()
            .Which.Id.Should().Be("abc");
        snapshot.Items[1].Should().BeOfType<RootlistPlaylist>()
            .Which.Uri.Should().Be("spotify:playlist:xyz");
        snapshot.Items[2].Should().BeOfType<RootlistFolderEnd>()
            .Which.Id.Should().Be("abc");

        // And the tree-builder consumes that correctly
        var tree = RootlistTreeBuilder.Build(snapshot.Items);
        tree.Root.Children.Should().HaveCount(1);
        var folder = tree.Root.Children[0].Should().BeOfType<RootlistChildFolder>().Subject.Folder;
        folder.Id.Should().Be("abc");
        folder.Children.Should().HaveCount(1);
        folder.Children[0].Should().BeOfType<RootlistChildPlaylist>()
            .Which.Uri.Should().Be("spotify:playlist:xyz");
    }

    [Fact]
    public void MapRootlist_NamedFolderStart_DecodesPlusAsSpace()
    {
        var content = new SelectedListContent
        {
            Contents = new ListItems { Pos = 0, Truncated = false }
        };
        content.Contents.Items.Add(new Item { Uri = "spotify:start-group:1e65:New+Folder" });
        content.Contents.Items.Add(new Item { Uri = "spotify:end-group:1e65" });
        content.Contents.MetaItems.Add(new MetaItem());
        content.Contents.MetaItems.Add(new MetaItem());

        var snapshot = SelectedListContentMapper.MapRootlist(content, FetchedAt);

        snapshot.Items[0].Should().BeOfType<RootlistFolderStart>()
            .Which.Name.Should().Be("New Folder");
    }

    [Fact]
    public void MapRootlist_DecorationsKeyedOnPlaylistUri_IgnoringFolderMetaItems()
    {
        // ============================================================
        // WHY: MetaItems[] is parallel to Items[] by index, and folder
        // positions hold empty {} MetaItems. The mapper must still key
        // decorations on the playlist URI, not on the index.
        // ============================================================
        var content = new SelectedListContent
        {
            Contents = new ListItems { Pos = 0, Truncated = false }
        };
        content.Contents.Items.Add(new Item { Uri = "spotify:start-group:folder:My" });
        content.Contents.Items.Add(new Item { Uri = "spotify:playlist:P1" });
        content.Contents.Items.Add(new Item { Uri = "spotify:end-group:folder" });
        content.Contents.Items.Add(new Item { Uri = "spotify:playlist:P2" });

        content.Contents.MetaItems.Add(new MetaItem()); // for folder-start
        content.Contents.MetaItems.Add(new MetaItem
        {
            Attributes = new ListAttributes { Name = "Playlist One" },
            Length = 10,
            OwnerUsername = "alice"
        });
        content.Contents.MetaItems.Add(new MetaItem()); // for folder-end
        content.Contents.MetaItems.Add(new MetaItem
        {
            Attributes = new ListAttributes { Name = "Playlist Two" },
            Length = 20,
            OwnerUsername = "bob"
        });

        var snapshot = SelectedListContentMapper.MapRootlist(content, FetchedAt);

        snapshot.Decorations.Should().HaveCount(2);
        snapshot.Decorations["spotify:playlist:P1"].Name.Should().Be("Playlist One");
        snapshot.Decorations["spotify:playlist:P1"].Length.Should().Be(10);
        snapshot.Decorations["spotify:playlist:P1"].OwnerUsername.Should().Be("alice");
        snapshot.Decorations["spotify:playlist:P2"].Name.Should().Be("Playlist Two");
        snapshot.Decorations["spotify:playlist:P2"].OwnerUsername.Should().Be("bob");
    }

    [Fact]
    public void MapPlaylist_CollaboratorCapabilities_MapsAllFields()
    {
        // ============================================================
        // WHY: Verifies the collaborator case from the brief — non-owner
        // with CanEditItems=true, CanEditMetadata=false. These flags gate
        // UI affordances (add-tracks CTA visible, rename disabled).
        // ============================================================
        var content = new SelectedListContent
        {
            Revision = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
            Attributes = new ListAttributes
            {
                Name = "Bordspel met bier eh",
                Collaborative = true,
                DeletedByOwner = true
            },
            OwnerUsername = "brikvd",
            AbuseReportingEnabled = true,
            Capabilities = new Capabilities
            {
                CanView = true,
                CanEditItems = true,
                CanEditMetadata = false,
                CanAdministratePermissions = false,
                CanCancelMembership = false
            },
            Contents = new ListItems { Pos = 0, Truncated = false }
        };

        var cached = SelectedListContentMapper.MapPlaylist(
            "spotify:playlist:collab",
            content,
            currentUsername: "someoneElse",
            FetchedAt);

        cached.Uri.Should().Be("spotify:playlist:collab");
        cached.Name.Should().Be("Bordspel met bier eh");
        cached.OwnerUsername.Should().Be("brikvd");
        cached.IsCollaborative.Should().BeTrue();
        cached.DeletedByOwner.Should().BeTrue();
        cached.AbuseReportingEnabled.Should().BeTrue();
        cached.BasePermission.Should().Be(CachedPlaylistBasePermission.Contributor);
        cached.Capabilities.CanEditItems.Should().BeTrue();
        cached.Capabilities.CanAbuseReport.Should().BeTrue();
    }

    [Fact]
    public void MapPlaylist_OwnerMatch_BasePermissionIsOwner()
    {
        var content = new SelectedListContent
        {
            OwnerUsername = "alice",
            Attributes = new ListAttributes { Name = "Mine" },
            Contents = new ListItems { Pos = 0, Truncated = false },
            Capabilities = new Capabilities { CanEditItems = true }
        };

        var cached = SelectedListContentMapper.MapPlaylist(
            "spotify:playlist:mine",
            content,
            currentUsername: "alice",
            FetchedAt);

        cached.BasePermission.Should().Be(CachedPlaylistBasePermission.Owner);
    }

    [Fact]
    public void MapPlaylist_OwnerMatch_NormalizesSpotifyUserUri()
    {
        var content = new SelectedListContent
        {
            OwnerUsername = "spotify:user:alice",
            Attributes = new ListAttributes { Name = "Mine" },
            Contents = new ListItems { Pos = 0, Truncated = false },
            Capabilities = new Capabilities()
        };

        var cached = SelectedListContentMapper.MapPlaylist(
            "spotify:playlist:mine",
            content,
            currentUsername: "alice",
            FetchedAt);

        cached.BasePermission.Should().Be(CachedPlaylistBasePermission.Owner);
    }

    [Fact]
    public void MapPlaylist_ItemAttributes_PreservedOnCachedItem()
    {
        var itemIdBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var content = new SelectedListContent
        {
            Attributes = new ListAttributes { Name = "Test" },
            Contents = new ListItems { Pos = 0, Truncated = false }
        };
        content.Contents.Items.Add(new Item
        {
            Uri = "spotify:track:abc123",
            Attributes = new ItemAttributes
            {
                AddedBy = "alice",
                Timestamp = 1_700_000_000_000, // ms
                ItemId = ByteString.CopyFrom(itemIdBytes)
            }
        });

        var cached = SelectedListContentMapper.MapPlaylist(
            "spotify:playlist:x",
            content,
            currentUsername: null,
            FetchedAt);

        cached.Items.Should().HaveCount(1);
        cached.Items[0].Uri.Should().Be("spotify:track:abc123");
        cached.Items[0].AddedBy.Should().Be("alice");
        cached.Items[0].AddedAt.Should().NotBeNull();
        cached.Items[0].ItemId.Should().Equal(itemIdBytes);
    }
}
