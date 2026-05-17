using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Wavee.Core.DependencyInjection;
using Wavee.Core.Playlists;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.Core.Storage.Outbox;
using Wavee.UI.Models;
using Wavee.UI.Services.Infra;
using Wavee.UI.WinUI.Data.Contexts.Helpers;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Default <see cref="IPlaylistMutationService"/>. Owns every write path that
/// targets a user-owned playlist: create / follow / rename / cover / add /
/// remove / reorder, plus the local-track overlay tables that Wavee layers on
/// top of Spotify playlists.
/// </summary>
public sealed class PlaylistMutationService : IPlaylistMutationService
{
    private readonly IMetadataDatabase _database;
    private readonly IPlaylistCacheService _playlistCache;
    private readonly ISession _session;
    private readonly IOutboxProcessor _outboxProcessor;
    private readonly IChangeBus _changeBus;
    private readonly ILogger<PlaylistMutationService>? _logger;
    private readonly string _databasePath;

    public PlaylistMutationService(
        IMetadataDatabase database,
        IPlaylistCacheService playlistCache,
        ISession session,
        IOutboxProcessor outboxProcessor,
        IChangeBus changeBus,
        WaveeCacheOptions cacheOptions,
        ILogger<PlaylistMutationService>? logger = null)
    {
        _database = database;
        _playlistCache = playlistCache;
        _session = session;
        _outboxProcessor = outboxProcessor ?? throw new ArgumentNullException(nameof(outboxProcessor));
        _changeBus = changeBus;
        _databasePath = cacheOptions.DatabasePath;
        _logger = logger;
    }

    public async Task<PlaylistSummaryDto> CreatePlaylistAsync(string name, IReadOnlyList<string>? trackIds = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var userData = _session.GetUserData() ?? throw new InvalidOperationException("CreatePlaylistAsync requires an authenticated session");
        var username = userData.Username;
        var spClient = _session.SpClient;

        // Step 1: mint an empty playlist; server assigns the URI.
        var created = await spClient.CreateEmptyPlaylistAsync(name, username, ct);
        var newUri = created.Uri;

        // Step 2: prepend the new playlist to the user's rootlist (matches the
        // first-party desktop client — it adds at the top of the list).
        var rootlist = await _playlistCache.GetRootlistAsync(ct: ct);
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
                                FromIndex = 0,
                                Items =
                                {
                                    new Wavee.Protocol.Playlist.Item
                                    {
                                        Uri = newUri,
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

        try
        {
            await spClient.PostRootlistChangesAsync(username, changes, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "CreatePlaylistAsync: rootlist add failed for {Uri}", newUri);
            throw;
        }

        // Step 3: explicitly set the name via UPDATE_LIST_ATTRIBUTES. Spotify's
        // /playlist/v2/playlist create endpoint accepts an attributes.name in
        // the ListUpdateRequest body but appears to ignore it — the playlist
        // is materialised with an empty name. Send a follow-up rename so the
        // user-supplied title actually persists.
        try
        {
            await RenamePlaylistAsync(newUri, name, ct);
        }
        catch (Exception ex)
        {
            // Don't fail the whole create flow if the rename fails — the
            // playlist exists and is in the user's rootlist; they can still
            // rename it manually from the hero. Surface as a warning.
            _logger?.LogWarning(ex, "CreatePlaylistAsync: post-create rename failed for {Uri}", newUri);
        }

        // Tracks-from-selection path is a follow-up — needs ChangePlaylistAsync against
        // the new URI with the freshly returned revision. Out of scope for this cut.
        if (trackIds is { Count: > 0 })
            _logger?.LogInformation("CreatePlaylistAsync: {Count} pending trackIds (track-add deferred)", trackIds.Count);

        return new PlaylistSummaryDto
        {
            Id = newUri,
            Name = name,
            TrackCount = 0,
            IsOwner = true
        };
    }

    public async Task AddTracksToPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        if (trackIds is null || trackIds.Count == 0) return;

        _ = _session.GetUserData()
            ?? throw new InvalidOperationException("AddTracksToPlaylistAsync requires an authenticated session");

        var playlistUri = PlaylistUriHelpers.NormalizePlaylistUri(playlistId);

        var normalized = new List<string>(trackIds.Count);
        foreach (var raw in trackIds)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            normalized.Add(PlaylistUriHelpers.NormalizeTrackUri(raw));
        }
        if (normalized.Count == 0) return;

        await Wavee.Core.Playlists.Outbox.PlaylistAddTracksHandler.EnqueueAsync(
            _database, playlistUri, normalized, ct);

        _ = _outboxProcessor.RunAsync();

        _changeBus.Publish(ChangeScope.Library);
    }

    public async Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        if (trackIds is null || trackIds.Count == 0) return;

        var userData = _session.GetUserData()
            ?? throw new InvalidOperationException("RemoveTracksFromPlaylistAsync requires an authenticated session");
        var username = userData.Username;
        var spClient = _session.SpClient;
        var playlistUri = PlaylistUriHelpers.NormalizePlaylistUri(playlistId);

        var remOp = new Wavee.Protocol.Playlist.Op
        {
            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Rem,
            Rem = new Wavee.Protocol.Playlist.Rem
            {
                ItemsAsKey = true,
            }
        };
        foreach (var raw in trackIds)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            remOp.Rem.Items.Add(new Wavee.Protocol.Playlist.Item
            {
                Uri = PlaylistUriHelpers.NormalizeTrackUri(raw),
            });
        }
        if (remOp.Rem.Items.Count == 0) return;

        var cached = await _playlistCache.GetPlaylistAsync(playlistUri, ct: ct);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var changes = new Wavee.Protocol.Playlist.ListChanges
        {
            BaseRevision = ByteString.CopyFrom(cached.Revision),
            Deltas =
            {
                new Wavee.Protocol.Playlist.Delta
                {
                    Ops = { remOp },
                    Info = new Wavee.Protocol.Playlist.ChangeInfo
                    {
                        User = username,
                        Timestamp = nowMs,
                    },
                }
            },
            WantResultingRevisions = true,
            WantSyncResult = true,
            Nonces = { RandomNumberGenerator.GetInt32(1, int.MaxValue) },
        };

        var fresh = await spClient.ChangePlaylistAsync(playlistUri, changes, ct);
        await _playlistCache.ApplyFreshContentAsync(playlistUri, fresh, ct);
        _changeBus.Publish(ChangeScope.Library);
    }

    public async Task ReorderTracksInPlaylistAsync(
        string playlistId,
        int fromIndex,
        int length,
        int toIndex,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        if (length <= 0) return;
        if (fromIndex < 0) throw new ArgumentOutOfRangeException(nameof(fromIndex));
        if (toIndex < 0) throw new ArgumentOutOfRangeException(nameof(toIndex));
        if (toIndex >= fromIndex && toIndex < fromIndex + length) return;

        var userData = _session.GetUserData()
            ?? throw new InvalidOperationException("ReorderTracksInPlaylistAsync requires an authenticated session");
        var username = userData.Username;
        var spClient = _session.SpClient;
        var playlistUri = PlaylistUriHelpers.NormalizePlaylistUri(playlistId);

        var cached = await _playlistCache.GetPlaylistAsync(playlistUri, ct: ct);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var changes = new Wavee.Protocol.Playlist.ListChanges
        {
            BaseRevision = ByteString.CopyFrom(cached.Revision),
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
                    Info = new Wavee.Protocol.Playlist.ChangeInfo
                    {
                        User = username,
                        Timestamp = nowMs,
                    },
                }
            },
            WantResultingRevisions = true,
            WantSyncResult = true,
            Nonces = { RandomNumberGenerator.GetInt32(1, int.MaxValue) },
        };

        var fresh = await spClient.ChangePlaylistAsync(playlistUri, changes, ct);
        await _playlistCache.ApplyFreshContentAsync(playlistUri, fresh, ct);
        _changeBus.Publish(ChangeScope.Library);
    }

    public async Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentNullException.ThrowIfNull(newName);

        var partial = new Wavee.Protocol.Playlist.ListAttributesPartialState
        {
            Values = new Wavee.Protocol.Playlist.ListAttributes { Name = newName.Trim() },
        };
        await PostAttributeChangeAsync(playlistId, partial, ct);
    }

    public async Task UpdatePlaylistDescriptionAsync(string playlistId, string description, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        description ??= string.Empty;

        var partial = new Wavee.Protocol.Playlist.ListAttributesPartialState
        {
            Values = new Wavee.Protocol.Playlist.ListAttributes { Description = description },
        };
        // Empty string = clear; signal the clear explicitly via no_value so the
        // server treats the field as removed rather than set-to-empty (matches
        // first-party desktop wire behaviour).
        if (description.Length == 0)
            partial.NoValue.Add(Wavee.Protocol.Playlist.ListAttributeKind.ListDescription);

        await PostAttributeChangeAsync(playlistId, partial, ct);
    }

    public async Task UpdatePlaylistCoverAsync(string playlistId, byte[] jpegBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentNullException.ThrowIfNull(jpegBytes);
        if (jpegBytes.Length == 0) throw new ArgumentException("jpegBytes must not be empty", nameof(jpegBytes));

        var spClient = _session.SpClient;

        // Step 1: hand the raw JPEG to image-upload, get an opaque upload token.
        var uploadToken = await spClient.UploadPlaylistImageAsync(jpegBytes, ct);
        // Step 2: register the upload against this playlist, get the 20-byte picture id.
        var pictureId = await spClient.RegisterPlaylistImageAsync(playlistId, uploadToken, ct);

        // Step 3: set ListAttributes.picture to the new id via UPDATE_LIST_ATTRIBUTES.
        var partial = new Wavee.Protocol.Playlist.ListAttributesPartialState
        {
            Values = new Wavee.Protocol.Playlist.ListAttributes
            {
                Picture = ByteString.CopyFrom(pictureId),
            },
        };
        await PostAttributeChangeAsync(playlistId, partial, ct);
    }

    public async Task RemovePlaylistCoverAsync(string playlistId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        var partial = new Wavee.Protocol.Playlist.ListAttributesPartialState
        {
            Values = new Wavee.Protocol.Playlist.ListAttributes(),
        };
        partial.NoValue.Add(Wavee.Protocol.Playlist.ListAttributeKind.ListPicture);
        await PostAttributeChangeAsync(playlistId, partial, ct);
    }

    /// <summary>
    /// Shared envelope builder for the four UPDATE_LIST_ATTRIBUTES flows.
    /// </summary>
    private async Task<Wavee.Protocol.Playlist.SelectedListContent> PostAttributeChangeAsync(
        string playlistUri,
        Wavee.Protocol.Playlist.ListAttributesPartialState partial,
        CancellationToken ct)
    {
        var userData = _session.GetUserData()
            ?? throw new InvalidOperationException("Playlist mutation requires an authenticated session");
        var username = userData.Username;
        var spClient = _session.SpClient;

        var cached = await _playlistCache.GetPlaylistAsync(playlistUri, ct: ct);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var changes = new Wavee.Protocol.Playlist.ListChanges
        {
            BaseRevision = ByteString.CopyFrom(cached.Revision),
            Deltas =
            {
                new Wavee.Protocol.Playlist.Delta
                {
                    Ops =
                    {
                        new Wavee.Protocol.Playlist.Op
                        {
                            Kind = Wavee.Protocol.Playlist.Op.Types.Kind.UpdateListAttributes,
                            UpdateListAttributes = new Wavee.Protocol.Playlist.UpdateListAttributes
                            {
                                NewAttributes = partial,
                            }
                        }
                    },
                    Info = new Wavee.Protocol.Playlist.ChangeInfo
                    {
                        User = username,
                        Timestamp = nowMs,
                    },
                }
            },
            WantResultingRevisions = true,
            WantSyncResult = true,
            Nonces = { RandomNumberGenerator.GetInt32(1, int.MaxValue) },
        };

        var fresh = await spClient.ChangePlaylistAsync(playlistUri, changes, ct);
        await _playlistCache.ApplyFreshContentAsync(playlistUri, fresh, ct);
        return fresh;
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        var userData = _session.GetUserData()
            ?? throw new InvalidOperationException("DeletePlaylistAsync requires an authenticated session");
        var username = userData.Username;
        var playlistUri = PlaylistUriHelpers.NormalizePlaylistUri(playlistId);

        var rootlist = await _playlistCache.GetRootlistAsync(ct: ct);
        var index = RootlistGraph.FindRootlistPlaylistIndex(rootlist, playlistUri);

        if (index < 0)
        {
            rootlist = await _playlistCache.GetRootlistAsync(forceRefresh: true, ct);
            index = RootlistGraph.FindRootlistPlaylistIndex(rootlist, playlistUri);
        }

        if (index < 0)
            throw new InvalidOperationException($"Playlist '{playlistUri}' is not in the current user's rootlist.");

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
                                FromIndex = index,
                                Length = 1,
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
            _logger?.LogDebug(ex, "DeletePlaylistAsync: rootlist refresh failed after removing {Uri}", playlistUri);
        }

        _changeBus.Publish(ChangeScope.Playlists);
        _changeBus.Publish(ChangeScope.Library);
    }

    public async Task SetPlaylistFollowedAsync(string playlistId, bool followed, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        var userData = _session.GetUserData()
            ?? throw new InvalidOperationException("SetPlaylistFollowedAsync requires an authenticated session");
        var username = userData.Username;
        var playlistUri = PlaylistUriHelpers.NormalizePlaylistUri(playlistId);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Spotify's desktop client treats "follow playlist" as "add to my rootlist"
        // and "unfollow" as "remove from my rootlist" — the same path
        // DeletePlaylistAsync / CreatePlaylistAsync use.
        var rootlist = await _playlistCache.GetRootlistAsync(ct: ct);
        var existingIndex = RootlistGraph.FindRootlistPlaylistIndex(rootlist, playlistUri);

        if (followed && existingIndex >= 0) return;
        if (!followed && existingIndex < 0) return;

        Wavee.Protocol.Playlist.Op op;
        if (followed)
        {
            op = new Wavee.Protocol.Playlist.Op
            {
                Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Add,
                Add = new Wavee.Protocol.Playlist.Add
                {
                    FromIndex = 0,
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
            };
        }
        else
        {
            op = new Wavee.Protocol.Playlist.Op
            {
                Kind = Wavee.Protocol.Playlist.Op.Types.Kind.Rem,
                Rem = new Wavee.Protocol.Playlist.Rem
                {
                    FromIndex = existingIndex,
                    Length = 1,
                }
            };
        }

        var changes = new Wavee.Protocol.Playlist.ListChanges
        {
            BaseRevision = ByteString.CopyFrom(rootlist.Revision),
            Deltas =
            {
                new Wavee.Protocol.Playlist.Delta
                {
                    Ops = { op },
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
            _logger?.LogDebug(ex, "SetPlaylistFollowedAsync: rootlist refresh failed after toggling {Uri}", playlistUri);
        }

        _changeBus.Publish(ChangeScope.Playlists);
        _changeBus.Publish(ChangeScope.Library);
    }

    public async Task<IReadOnlyList<RecommendedTrackResult>> GetPlaylistRecommendationsAsync(
        string playlistUri,
        IReadOnlyList<string>? skipUris = null,
        int numResults = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(playlistUri)) return Array.Empty<RecommendedTrackResult>();

        var fullPlaylistUri = PlaylistUriHelpers.NormalizePlaylistUri(playlistUri);
        var spClient = _session.SpClient;
        var trackSkipIds = PlaylistUriHelpers.NormalizeTrackSkipIds(skipUris);

        var response = await spClient.ExtendPlaylistAsync(
            fullPlaylistUri,
            trackSkipIds,
            numResults,
            ct).ConfigureAwait(false);

        if (response is null)
            throw new InvalidOperationException(
                $"Playlist extender returned no response for {fullPlaylistUri}");

        var recommendedTracks = response.RecommendedTracks;
        if (recommendedTracks is { Count: > 0 })
        {
            var recommendedResults = new List<RecommendedTrackResult>(recommendedTracks.Count);
            foreach (var t in recommendedTracks)
            {
                var uri = !string.IsNullOrWhiteSpace(t.OriginalId)
                    ? t.OriginalId
                    : !string.IsNullOrWhiteSpace(t.Id)
                        ? $"spotify:track:{t.Id}"
                        : null;
                if (string.IsNullOrWhiteSpace(uri)) continue;

                var id = !string.IsNullOrWhiteSpace(t.Id)
                    ? t.Id
                    : PlaylistUriHelpers.ExtractBareId(uri, "spotify:track:");
                var artistNames = t.Artists is { Count: > 0 }
                    ? string.Join(", ", t.Artists.Select(a => a.Name).Where(n => !string.IsNullOrEmpty(n)))
                    : null;

                recommendedResults.Add(new RecommendedTrackResult
                {
                    Uri = uri,
                    Id = id,
                    Name = t.Name,
                    ArtistNames = artistNames,
                    AlbumName = t.Album?.Name,
                    ImageUrl = t.Album?.ImageUrl ?? t.Album?.LargeImageUrl,
                    Duration = t.Duration > 0 ? TimeSpan.FromMilliseconds(t.Duration) : TimeSpan.Zero,
                    OriginalIndex = recommendedResults.Count + 1,
                });
            }

            return recommendedResults;
        }

        var tracks = response.Tracks;
        if (tracks is null || tracks.Count == 0)
            return Array.Empty<RecommendedTrackResult>();

        var results = new List<RecommendedTrackResult>(tracks.Count);
        foreach (var t in tracks)
        {
            if (string.IsNullOrEmpty(t.TrackUri)) continue;

            var meta = t.Metadata;
            var name = meta?.Name ?? meta?.TrackName;
            var artists = meta?.ArtistList;
            var artistNames = artists is { Count: > 0 }
                ? string.Join(", ", artists.Select(a => a.Name).Where(n => !string.IsNullOrEmpty(n)))
                : null;

            results.Add(new RecommendedTrackResult
            {
                Uri = t.TrackUri,
                Id = t.TrackUri.Split(':').LastOrDefault() ?? t.TrackUri,
                Name = name,
                ArtistNames = artistNames,
                AlbumName = meta?.AlbumName,
                ImageUrl = meta?.TrackImageUri,
                Duration = meta is { Duration: > 0 } ? TimeSpan.FromMilliseconds(meta.Duration) : TimeSpan.Zero,
                OriginalIndex = results.Count + 1,
            });
        }
        return results;
    }

    // ── Local-track playlist overlays ──

    public Task AddLocalTracksToPlaylistAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playlistUri) || trackUris is null || trackUris.Count == 0)
            return Task.CompletedTask;

        using var conn = OpenLocalConnection();
        using var tx = conn.BeginTransaction();

        int basePosition;
        using (var probe = conn.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = "SELECT COALESCE(MAX(position), -1) FROM playlist_overlay_items WHERE playlist_uri = $u;";
            probe.Parameters.AddWithValue("$u", playlistUri);
            basePosition = Convert.ToInt32(probe.ExecuteScalar()) + 1;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (int i = 0; i < trackUris.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO playlist_overlay_items (playlist_uri, item_uri, position, added_at, added_by)
                VALUES ($p, $i, $pos, $at, 'wavee:local');
                """;
            cmd.Parameters.AddWithValue("$p", playlistUri);
            cmd.Parameters.AddWithValue("$i", trackUris[i]);
            cmd.Parameters.AddWithValue("$pos", basePosition + i);
            cmd.Parameters.AddWithValue("$at", now + i);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        _changeBus.Publish(ChangeScope.Library);
        return Task.CompletedTask;
    }

    public Task RemoveLocalOverlayTracksAsync(string playlistUri, IReadOnlyList<string> trackUris, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playlistUri) || trackUris is null || trackUris.Count == 0)
            return Task.CompletedTask;

        using var conn = OpenLocalConnection();
        using var tx = conn.BeginTransaction();
        foreach (var u in trackUris)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM playlist_overlay_items WHERE playlist_uri = $p AND item_uri = $i;";
            cmd.Parameters.AddWithValue("$p", playlistUri);
            cmd.Parameters.AddWithValue("$i", u);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        _changeBus.Publish(ChangeScope.Library);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PlaylistOverlayRow>> GetPlaylistOverlayRowsAsync(string playlistUri, CancellationToken ct = default)
    {
        var list = new List<PlaylistOverlayRow>();
        if (string.IsNullOrEmpty(playlistUri))
            return Task.FromResult<IReadOnlyList<PlaylistOverlayRow>>(list);

        using var conn = OpenLocalConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT item_uri, position, added_at, added_by
            FROM playlist_overlay_items
            WHERE playlist_uri = $p
            ORDER BY position, added_at;
            """;
        cmd.Parameters.AddWithValue("$p", playlistUri);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new PlaylistOverlayRow(
                TrackUri: r.GetString(0),
                Position: r.GetInt32(1),
                AddedAt: r.GetInt64(2),
                AddedBy: r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return Task.FromResult<IReadOnlyList<PlaylistOverlayRow>>(list);
    }

    public Task ReorderPlaylistOverlayAsync(string playlistUri, IReadOnlyList<string> orderedTrackUris, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(playlistUri) || orderedTrackUris is null) return Task.CompletedTask;

        using var conn = OpenLocalConnection();
        using var tx = conn.BeginTransaction();
        for (int i = 0; i < orderedTrackUris.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE playlist_overlay_items SET position = $pos WHERE playlist_uri = $p AND item_uri = $i;";
            cmd.Parameters.AddWithValue("$pos", i);
            cmd.Parameters.AddWithValue("$p", playlistUri);
            cmd.Parameters.AddWithValue("$i", orderedTrackUris[i]);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        _changeBus.Publish(ChangeScope.Library);
        return Task.CompletedTask;
    }

    private Microsoft.Data.Sqlite.SqliteConnection OpenLocalConnection()
    {
        var b = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
        };
        var c = new Microsoft.Data.Sqlite.SqliteConnection(b.ConnectionString);
        c.Open();
        return c;
    }
}
