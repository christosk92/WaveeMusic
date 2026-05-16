using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.Protocol.Collection;

namespace Wavee.Core.Library.Spotify.Outbox;

/// <summary>
/// Shared write logic for the library save / remove outbox handlers. Both
/// handlers serialize to the same SpClient.WriteCollectionAsync call with
/// the only difference being the <c>IsRemoved</c> flag on the
/// <see cref="CollectionItem"/>; this dispatcher avoids duplicating the
/// payload-parse + URI-validate + collection-set-mapping logic across both.
/// </summary>
internal static class LibraryOpDispatch
{
    // URI scheme guard: drop malformed entries immediately rather than
    // retry-forever (matches the historical behaviour at SpotifyLibraryService.cs
    // lines 1192-1211).
    public static Task WriteAsync(
        SpClient spClient,
        ISession session,
        OutboxEntry entry,
        bool isRemoved,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry.PrimaryUri) || !entry.PrimaryUri.StartsWith("spotify:", StringComparison.Ordinal))
            throw new InvalidOperationException($"library outbox entry has non-Spotify URI '{entry.PrimaryUri}'; dropping");
        if (entry.PrimaryUri.IndexOf(":wavee:", StringComparison.Ordinal) > 0)
            throw new InvalidOperationException($"library outbox entry contains nested wavee URI '{entry.PrimaryUri}'; dropping");

        var payload = JsonSerializer.Deserialize(entry.Payload ?? "{}", LibraryOpJson.Default.LibraryOpPayload)
                      ?? throw new InvalidOperationException("library outbox payload missing item-type");
        var userData = session.GetUserData()
                       ?? throw new InvalidOperationException("not authenticated");
        var username = userData.Username;
        var set = GetSetForItemType(payload.ItemType);

        var item = new CollectionItem
        {
            Uri = entry.PrimaryUri,
            AddedAt = (int)entry.CreatedAt,
            IsRemoved = isRemoved,
        };

        return spClient.WriteCollectionAsync(username, set, new[] { item });
    }

    // Mirror of the private GetSetForItemType inside SpotifyLibraryService.
    // Lives here so both handlers share it; the SpotifyLibraryService copy
    // can stay as well (still used by sync paths unrelated to the outbox).
    private static string GetSetForItemType(SpotifyLibraryItemType itemType) => itemType switch
    {
        SpotifyLibraryItemType.Track  => "collection",
        SpotifyLibraryItemType.Album  => "collection",
        SpotifyLibraryItemType.Artist => "artist",
        SpotifyLibraryItemType.Show   => "show",
        SpotifyLibraryItemType.YlPin  => "ylpin",
        _ => "collection"
    };
}
