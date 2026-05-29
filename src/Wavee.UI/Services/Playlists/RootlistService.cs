using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.Playlists;
using Wavee.Core.Session;
using Wavee.UI.Services.DragDrop;
using Wavee.UI.Services.Infra;
using Wavee.UI.WinUI.Data.Contexts.Helpers;
using Wavee.UI.Contracts;
using Wavee.UI.Models;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Default <see cref="IRootlistService"/>. POSTs every rootlist mutation via
/// <see cref="ISession"/>.SpClient and invalidates the rootlist cache so
/// downstream subscribers reload from the new state.
/// </summary>
public sealed class RootlistService : IRootlistService
{
    private readonly ISession _session;
    private readonly IPlaylistCacheService _playlistCache;
    private readonly IChangeBus _changeBus;
    private readonly ILogger<RootlistService>? _logger;

    public RootlistService(
        ISession session,
        IPlaylistCacheService playlistCache,
        IChangeBus changeBus,
        ILogger<RootlistService>? logger = null)
    {
        _session = session;
        _playlistCache = playlistCache;
        _changeBus = changeBus;
        _logger = logger;
    }

    public async Task<PlaylistSummaryDto> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var userData = _session.GetUserData() ?? throw new InvalidOperationException("CreateFolderAsync requires an authenticated session");
        var username = userData.Username;
        var spClient = _session.SpClient;

        // Folder is a pair of (start-group, end-group) URIs in the rootlist with a
        // shared 16-hex group id. The folder's display name is URL-encoded into
        // the start-group URI (Spotify uses '+' for spaces, not '%20').
        var groupId = RandomNumberGenerator.GetHexString(16, lowercase: true);
        var encodedName = Uri.EscapeDataString(name).Replace("%20", "+");
        var startUri = $"spotify:start-group:{groupId}:{encodedName}";
        var endUri = $"spotify:end-group:{groupId}";

        var rootlist = await _playlistCache.GetRootlistAsync(ct: ct);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Captured wire shape: two separate Ops inside one Delta, prepending at
        // (0, 1). The end-group lands directly after the start-group → folder is empty.
        var changes = new Wavee.Protocol.Playlist.ListChanges
        {
            BaseRevision = ByteString.CopyFrom(rootlist.Revision),
            Deltas =
            {
                new Wavee.Protocol.Playlist.Delta
                {
                    Ops =
                    {
                        new Wavee.Protocol.Playlist.Op
                        {
                            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Add,
                            Add = new Wavee.Protocol.Playlist.Add
                            {
                                FromIndex = 0,
                                Items =
                                {
                                    new Wavee.Protocol.Playlist.Item
                                    {
                                        Uri = startUri,
                                        Attributes = new Wavee.Protocol.Playlist.ItemAttributes { Timestamp = nowMs },
                                    }
                                }
                            }
                        },
                        new Wavee.Protocol.Playlist.Op
                        {
                            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Add,
                            Add = new Wavee.Protocol.Playlist.Add
                            {
                                FromIndex = 1,
                                Items =
                                {
                                    new Wavee.Protocol.Playlist.Item
                                    {
                                        Uri = endUri,
                                        Attributes = new Wavee.Protocol.Playlist.ItemAttributes { Timestamp = nowMs },
                                    }
                                }
                            }
                        },
                    },
                    Info = RootlistGraph.BuildRootlistChangeInfo(username, nowMs),
                }
            },
            WantResultingRevisions = true,
            WantSyncResult = true,
            Nonces = { RootlistGraph.NextRootlistNonce() },
        };

        await spClient.PostRootlistChangesAsync(username, changes, ct);

        return new PlaylistSummaryDto
        {
            Id = $"spotify:start-group:{groupId}",
            Name = name,
            TrackCount = 0,
            IsOwner = true
        };
    }

    public async Task MovePlaylistInRootlistAsync(
        string sourceUri,
        string targetUri,
        DropPosition position,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUri);
        if (string.Equals(sourceUri, targetUri, StringComparison.OrdinalIgnoreCase)) return;

        var userData = _session.GetUserData()
            ?? throw new InvalidOperationException("MovePlaylistInRootlistAsync requires an authenticated session");
        var username = userData.Username;

        var rootlist = await _playlistCache.GetRootlistAsync(ct: ct);
        var fromIndex = RootlistGraph.FindRootlistEntryIndex(rootlist, sourceUri);
        var targetIndex = RootlistGraph.FindRootlistEntryIndex(rootlist, targetUri);

        if (fromIndex < 0 || targetIndex < 0)
        {
            rootlist = await _playlistCache.GetRootlistAsync(forceRefresh: true, ct);
            fromIndex = RootlistGraph.FindRootlistEntryIndex(rootlist, sourceUri);
            targetIndex = RootlistGraph.FindRootlistEntryIndex(rootlist, targetUri);
        }
        if (targetIndex < 0) throw new InvalidOperationException($"Target '{targetUri}' is not in the current user's rootlist.");

        var targetLength = RootlistGraph.RootlistEntrySpan(rootlist, targetIndex);

        // Compute desired insert position in the original list. The proto's
        // Mov is interpreted by the server as: remove [fromIndex .. fromIndex+length-1],
        // then insert at toIndex in the post-removal list. Spotify's Web API uses
        // the same insert_before convention.
        int toIndex = position switch
        {
            DropPosition.Before => targetIndex,
            DropPosition.After  => targetIndex + targetLength,
            // Inside is only meaningful for folder targets; fall back to After otherwise.
            DropPosition.Inside => rootlist.Items[targetIndex] is RootlistFolderStart
                ? targetIndex + 1
                : targetIndex + targetLength,
            _ => targetIndex + targetLength,
        };

        if (fromIndex < 0)
        {
            // Source isn't in the rootlist yet — this is a drag from outside
            // the library (a discovery / search / page content card). Follow
            // the playlist at the computed target position in one shot rather
            // than failing with "Source not in rootlist", which is what the
            // old Mov-only path did.
            await PostRootlistAddAsync(username, rootlist, sourceUri, toIndex, ct);
            return;
        }

        var sourceLength = RootlistGraph.RootlistEntrySpan(rootlist, fromIndex);

        // No-op when the source already sits exactly where the user is dropping.
        if (toIndex == fromIndex || (toIndex > fromIndex && toIndex <= fromIndex + sourceLength)) return;

        await PostRootlistMovAsync(username, rootlist, fromIndex, sourceLength, toIndex, ct);
    }

    public Task MovePlaylistIntoFolderAsync(
        string playlistUri,
        string folderStartUri,
        CancellationToken ct = default)
    {
        // Insert at the very top of the folder's children (right after start-group).
        return MovePlaylistInRootlistAsync(playlistUri, folderStartUri, DropPosition.Inside, ct);
    }

    /// <summary>
    /// Inserts a single playlist into the user's rootlist at <paramref name="toIndex"/>
    /// (= follow + place in one delta).
    /// </summary>
    private async Task PostRootlistAddAsync(
        string username,
        RootlistSnapshot rootlist,
        string playlistUri,
        int toIndex,
        CancellationToken ct)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changes = new Wavee.Protocol.Playlist.ListChanges
        {
            BaseRevision = ByteString.CopyFrom(rootlist.Revision),
            Deltas =
            {
                new Wavee.Protocol.Playlist.Delta
                {
                    Ops =
                    {
                        new Wavee.Protocol.Playlist.Op
                        {
                            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Add,
                            Add = new Wavee.Protocol.Playlist.Add
                            {
                                FromIndex = toIndex,
                                Items =
                                {
                                    new Wavee.Protocol.Playlist.Item
                                    {
                                        Uri = playlistUri,
                                        Attributes = new Wavee.Protocol.Playlist.ItemAttributes
                                        {
                                            Timestamp = nowMs,
                                            Public = true,
                                        }
                                    }
                                }
                            }
                        }
                    },
                    Info = RootlistGraph.BuildRootlistChangeInfo(username, nowMs),
                }
            },
            WantResultingRevisions = true,
            WantSyncResult = true,
            Nonces = { RootlistGraph.NextRootlistNonce() },
        };

        await _session.SpClient.PostRootlistChangesAsync(username, changes, ct);
        await _playlistCache.InvalidateAsync(PlaylistCacheUris.Rootlist, ct);

        try
        {
            await _playlistCache.GetRootlistAsync(forceRefresh: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Rootlist refresh after Add failed (uri={Uri}, to={To})", playlistUri, toIndex);
        }

        _changeBus.Publish(ChangeScope.Playlists);
        _changeBus.Publish(ChangeScope.Library);
    }

    public async Task RenameFolderAsync(
        string folderStartUri,
        string newName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderStartUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var userData = _session.GetUserData()
            ?? throw new InvalidOperationException("RenameFolderAsync requires an authenticated session");
        var username = userData.Username;

        var rootlist = await _playlistCache.GetRootlistAsync(ct: ct);
        var startIdx = RootlistGraph.FindRootlistFolderStartIndex(rootlist, folderStartUri);
        if (startIdx < 0)
        {
            rootlist = await _playlistCache.GetRootlistAsync(forceRefresh: true, ct);
            startIdx = RootlistGraph.FindRootlistFolderStartIndex(rootlist, folderStartUri);
        }
        if (startIdx < 0)
            throw new InvalidOperationException($"Folder '{folderStartUri}' is not in the current user's rootlist.");
        if (rootlist.Items[startIdx] is not RootlistFolderStart existing)
            throw new InvalidOperationException($"Rootlist index {startIdx} is not a folder start.");

        // Folder rename = Rem the old start-group marker + Add a new one at the
        // same index whose URI encodes the new name. The end-group marker isn't
        // touched (it carries no name).
        var groupId = existing.Id;
        var encodedName = Uri.EscapeDataString(newName).Replace("%20", "+");
        var newStartUri = $"spotify:start-group:{groupId}:{encodedName}";

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changes = new Wavee.Protocol.Playlist.ListChanges
        {
            BaseRevision = ByteString.CopyFrom(rootlist.Revision),
            Deltas =
            {
                new Wavee.Protocol.Playlist.Delta
                {
                    Ops =
                    {
                        new Wavee.Protocol.Playlist.Op
                        {
                            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Rem,
                            Rem = new Wavee.Protocol.Playlist.Rem
                            {
                                FromIndex = startIdx,
                                Length = 1,
                                ItemsAsKey = true,
                            }
                        },
                        new Wavee.Protocol.Playlist.Op
                        {
                            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Add,
                            Add = new Wavee.Protocol.Playlist.Add
                            {
                                FromIndex = startIdx,
                                Items =
                                {
                                    new Wavee.Protocol.Playlist.Item
                                    {
                                        Uri = newStartUri,
                                        Attributes = new Wavee.Protocol.Playlist.ItemAttributes { Timestamp = nowMs },
                                    }
                                }
                            }
                        },
                    },
                    Info = RootlistGraph.BuildRootlistChangeInfo(username, nowMs),
                }
            },
            WantResultingRevisions = true,
            WantSyncResult = true,
            Nonces = { RootlistGraph.NextRootlistNonce() },
        };

        await _session.SpClient.PostRootlistChangesAsync(username, changes, ct);
        await _playlistCache.InvalidateAsync(PlaylistCacheUris.Rootlist, ct);
        try { await _playlistCache.GetRootlistAsync(forceRefresh: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Rootlist refresh after RenameFolder failed (folder={Uri})", folderStartUri);
        }
        _changeBus.Publish(ChangeScope.Playlists);
        _changeBus.Publish(ChangeScope.Library);
    }

    public async Task DeleteFolderAsync(string folderStartUri, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderStartUri);

        var userData = _session.GetUserData()
            ?? throw new InvalidOperationException("DeleteFolderAsync requires an authenticated session");
        var username = userData.Username;

        var rootlist = await _playlistCache.GetRootlistAsync(ct: ct);
        var startIdx = RootlistGraph.FindRootlistFolderStartIndex(rootlist, folderStartUri);
        if (startIdx < 0)
        {
            rootlist = await _playlistCache.GetRootlistAsync(forceRefresh: true, ct);
            startIdx = RootlistGraph.FindRootlistFolderStartIndex(rootlist, folderStartUri);
        }
        if (startIdx < 0)
            throw new InvalidOperationException($"Folder '{folderStartUri}' is not in the current user's rootlist.");

        var endIdx = RootlistGraph.FindMatchingFolderEndIndex(rootlist, startIdx);
        if (endIdx < 0)
            throw new InvalidOperationException($"Folder '{folderStartUri}' has no matching end marker.");

        // Spotify's wire-level "delete folder" = Rem both start-group and
        // end-group markers. The playlists nested inside stay where they
        // were — they're already real rootlist entries; the markers just
        // bracketed them. Order matters: remove end first so the start
        // index doesn't shift mid-delta.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changes = new Wavee.Protocol.Playlist.ListChanges
        {
            BaseRevision = ByteString.CopyFrom(rootlist.Revision),
            Deltas =
            {
                new Wavee.Protocol.Playlist.Delta
                {
                    Ops =
                    {
                        new Wavee.Protocol.Playlist.Op
                        {
                            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Rem,
                            Rem = new Wavee.Protocol.Playlist.Rem
                            {
                                FromIndex = endIdx,
                                Length = 1,
                                ItemsAsKey = true,
                            }
                        },
                        new Wavee.Protocol.Playlist.Op
                        {
                            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Rem,
                            Rem = new Wavee.Protocol.Playlist.Rem
                            {
                                FromIndex = startIdx,
                                Length = 1,
                                ItemsAsKey = true,
                            }
                        },
                    },
                    Info = RootlistGraph.BuildRootlistChangeInfo(username, nowMs),
                }
            },
            WantResultingRevisions = true,
            WantSyncResult = true,
            Nonces = { RootlistGraph.NextRootlistNonce() },
        };

        await _session.SpClient.PostRootlistChangesAsync(username, changes, ct);
        await _playlistCache.InvalidateAsync(PlaylistCacheUris.Rootlist, ct);
        try { await _playlistCache.GetRootlistAsync(forceRefresh: true, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Rootlist refresh after DeleteFolder failed (folder={Uri})", folderStartUri);
        }
        _changeBus.Publish(ChangeScope.Playlists);
        _changeBus.Publish(ChangeScope.Library);
    }

    private async Task PostRootlistMovAsync(
        string username,
        RootlistSnapshot rootlist,
        int fromIndex,
        int length,
        int toIndex,
        CancellationToken ct)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var changes = new Wavee.Protocol.Playlist.ListChanges
        {
            BaseRevision = ByteString.CopyFrom(rootlist.Revision),
            Deltas =
            {
                new Wavee.Protocol.Playlist.Delta
                {
                    Ops =
                    {
                        new Wavee.Protocol.Playlist.Op
                        {
                            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Mov,
                            Mov = new Wavee.Protocol.Playlist.Mov
                            {
                                FromIndex = fromIndex,
                                Length = length,
                                ToIndex = toIndex,
                            }
                        }
                    },
                    Info = RootlistGraph.BuildRootlistChangeInfo(username, nowMs),
                }
            },
            WantResultingRevisions = true,
            WantSyncResult = true,
            Nonces = { RootlistGraph.NextRootlistNonce() },
        };

        await _session.SpClient.PostRootlistChangesAsync(username, changes, ct);
        await _playlistCache.InvalidateAsync(PlaylistCacheUris.Rootlist, ct);

        try
        {
            await _playlistCache.GetRootlistAsync(forceRefresh: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "Rootlist refresh after Mov failed (from={From}, length={Length}, to={To})", fromIndex, length, toIndex);
        }

        _changeBus.Publish(ChangeScope.Playlists);
        _changeBus.Publish(ChangeScope.Library);
    }
}