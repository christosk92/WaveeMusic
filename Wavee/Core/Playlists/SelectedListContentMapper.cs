using Wavee.Protocol.Playlist;

namespace Wavee.Core.Playlists;

public static class SelectedListContentMapper
{
    public static RootlistSnapshot MapRootlist(SelectedListContent content, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(content);

        var items = new List<RootlistEntry>();
        var decorations = new Dictionary<string, RootlistDecoration>(StringComparer.Ordinal);
        var rawItems = content.Contents?.Items ?? [];
        var metaItems = content.Contents?.MetaItems ?? [];

        for (int i = 0; i < rawItems.Count; i++)
        {
            var item = rawItems[i];
            if (TryParseFolderStart(item.Uri, out var folderId, out var folderName))
            {
                items.Add(new RootlistFolderStart(folderId!, folderName!));
                continue;
            }

            if (TryParseFolderEnd(item.Uri, out folderId))
            {
                items.Add(new RootlistFolderEnd(folderId!));
                continue;
            }

            if (!item.Uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
                continue;

            items.Add(new RootlistPlaylist(item.Uri));

            if (i >= metaItems.Count)
                continue;

            var meta = metaItems[i];
            decorations[item.Uri] = new RootlistDecoration
            {
                Revision = meta.Revision?.ToByteArray() ?? [],
                Name = meta.Attributes?.Name,
                Description = meta.Attributes?.Description,
                ImageUrl = PickImageUrl(meta.Attributes),
                OwnerUsername = meta.OwnerUsername,
                Length = meta.Length,
                IsPublic = item.Attributes?.Public ?? false,
                IsCollaborative = meta.Attributes?.Collaborative ?? false,
                StatusCode = InferRootlistStatusCode(meta)
            };
        }

        return new RootlistSnapshot
        {
            Revision = content.Revision?.ToByteArray() ?? [],
            Items = items,
            Decorations = decorations,
            FetchedAt = fetchedAt
        };
    }

    public static CachedPlaylist MapPlaylist(
        string playlistUri,
        SelectedListContent content,
        string? currentUsername,
        DateTimeOffset fetchedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUri);
        ArgumentNullException.ThrowIfNull(content);

        var items = (content.Contents?.Items ?? [])
            .Select(MapPlaylistItem)
            .ToArray();

        return new CachedPlaylist
        {
            Uri = playlistUri,
            Revision = content.Revision?.ToByteArray() ?? [],
            Name = content.Attributes?.Name ?? "Unknown",
            Description = content.Attributes?.Description,
            ImageUrl = PickImageUrl(content.Attributes),
            HeaderImageUrl = PickFormatAttribute(content.Attributes, "header_image_url_desktop"),
            OwnerUsername = content.OwnerUsername ?? "",
            Length = content.Length,
            IsCollaborative = content.Attributes?.Collaborative ?? false,
            DeletedByOwner = content.Attributes?.DeletedByOwner ?? false,
            AbuseReportingEnabled = content.AbuseReportingEnabled,
            BasePermission = MapBasePermission(content.OwnerUsername, currentUsername, content.Capabilities),
            Capabilities = MapCapabilities(content.Capabilities, content.AbuseReportingEnabled),
            Items = items,
            HasContentsSnapshot = content.Contents != null,
            FetchedAt = fetchedAt
        };
    }

    /// <summary>
    /// Look up a single key in <see cref="ListAttributes.FormatAttributes"/>. Spotify
    /// uses this for editorial playlist chrome — e.g. <c>header_image_url_desktop</c>
    /// carries a wide hero image URL. Returns null when the key is absent or empty.
    /// </summary>
    public static string? PickFormatAttribute(ListAttributes? attributes, string key)
    {
        if (attributes?.FormatAttributes.Count is not > 0) return null;
        foreach (var attr in attributes.FormatAttributes)
        {
            if (string.Equals(attr.Key, key, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(attr.Value))
                return attr.Value;
        }
        return null;
    }

    private static CachedPlaylistItem MapPlaylistItem(Item item)
    {
        var timestamp = item.Attributes?.Timestamp ?? 0;
        return new CachedPlaylistItem
        {
            Uri = item.Uri,
            AddedBy = item.Attributes?.AddedBy,
            AddedAt = timestamp > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp) : null,
            ItemId = item.Attributes?.ItemId?.ToByteArray()
        };
    }

    public static CachedPlaylistBasePermission MapBasePermission(
        string? ownerUsername,
        string? currentUsername,
        Capabilities? capabilities)
    {
        if (!string.IsNullOrWhiteSpace(ownerUsername) &&
            !string.IsNullOrWhiteSpace(currentUsername) &&
            string.Equals(ownerUsername, currentUsername, StringComparison.OrdinalIgnoreCase))
        {
            return CachedPlaylistBasePermission.Owner;
        }

        return capabilities?.CanEditItems == true
            ? CachedPlaylistBasePermission.Contributor
            : CachedPlaylistBasePermission.Viewer;
    }

    public static CachedPlaylistCapabilities MapCapabilities(
        Capabilities? capabilities,
        bool abuseReportingEnabled)
    {
        if (capabilities == null)
        {
            return CachedPlaylistCapabilities.ViewOnly with
            {
                CanAbuseReport = abuseReportingEnabled
            };
        }

        return new CachedPlaylistCapabilities
        {
            CanView = capabilities.CanView,
            CanEditItems = capabilities.CanEditItems,
            CanAdministratePermissions = capabilities.CanAdministratePermissions,
            CanCancelMembership = capabilities.CanCancelMembership,
            CanAbuseReport = abuseReportingEnabled
        };
    }

    public static string? PickImageUrl(ListAttributes? attributes)
    {
        if (attributes?.PictureSize.Count is not > 0)
            return null;

        return attributes.PictureSize.FirstOrDefault(static picture => picture.TargetName == "default")?.Url
            ?? attributes.PictureSize.FirstOrDefault()?.Url;
    }

    private static bool TryParseFolderStart(string uri, out string? folderId, out string? folderName)
    {
        folderId = null;
        folderName = null;

        if (!uri.StartsWith("spotify:start-group:", StringComparison.Ordinal))
            return false;

        // "spotify:start-group:{id}:{name}" is the canonical form, but "spotify:start-group:{id}"
        // (no name segment) is also legal. Previously we silently dropped the 3-segment form,
        // which left the matching end-group to pop a different folder off the stack and cascaded
        // into wrong subfolder nesting. Accept both shapes.
        var parts = uri.Split(':', 4);
        if (parts.Length < 3 || string.IsNullOrEmpty(parts[2]))
            return false;

        folderId = parts[2];
        folderName = parts.Length >= 4 ? DecodeFolderName(parts[3]) : "";
        return true;
    }

    private static bool TryParseFolderEnd(string uri, out string? folderId)
    {
        folderId = null;
        if (!uri.StartsWith("spotify:end-group:", StringComparison.Ordinal))
            return false;

        var parts = uri.Split(':', 3);
        if (parts.Length < 3)
            return false;

        folderId = parts[2];
        return true;
    }

    private static string DecodeFolderName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName))
            return "Folder";

        return Uri.UnescapeDataString(rawName.Replace("+", " ", StringComparison.Ordinal));
    }

    private static int InferRootlistStatusCode(MetaItem meta)
    {
        if (meta.Attributes != null || meta.HasRevision || meta.HasLength || meta.HasOwnerUsername)
            return 200;

        return 404;
    }
}
