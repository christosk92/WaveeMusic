using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage.Abstractions;
using Wavee.Core.Storage.Outbox;
using Wavee.Protocol.Playlist;

namespace Wavee.Core.Playlists.Outbox;

/// <summary>
/// Outbox handler for <c>"playlist.add-tracks"</c>. Posts a chunked Add Op to
/// <c>/playlist/v2/{id}/changes</c>. Cursor (<c>entry.ProgressOffset</c>)
/// advances after every chunk so retries resume rather than replay — Spotify
/// allows duplicate track entries, so naive replay would double-add. Chunk
/// size of 500 keeps each request well below the v2 endpoint's body cap.
/// </summary>
public sealed class PlaylistAddTracksHandler : IOutboxHandler
{
    public const string Kind = "playlist.add-tracks";
    public string OpKind => Kind;

    /// <summary>URIs per single Add Op posted to /playlist/v2/{id}/changes.</summary>
    public const int ChunkSize = 500;

    private readonly SpClient _spClient;
    private readonly ISession _session;
    private readonly IPlaylistCacheService _playlistCache;
    private readonly IMetadataDatabase _database;

    public PlaylistAddTracksHandler(
        SpClient spClient,
        ISession session,
        IPlaylistCacheService playlistCache,
        IMetadataDatabase database)
    {
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _playlistCache = playlistCache ?? throw new ArgumentNullException(nameof(playlistCache));
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>
    /// Enqueue a bulk add-tracks operation. The supplied URIs must be already
    /// validated / normalised by the caller; the handler trusts them and just
    /// serialises into a payload row. Returns immediately — the actual server
    /// write happens when an <see cref="IOutboxProcessor"/> drains the queue.
    /// </summary>
    public static Task EnqueueAsync(
        IMetadataDatabase database,
        string playlistUri,
        IReadOnlyList<string> trackUris,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrEmpty(playlistUri);
        ArgumentNullException.ThrowIfNull(trackUris);
        if (trackUris.Count == 0) return Task.CompletedTask;

        var payload = JsonSerializer.Serialize(
            new PlaylistAddTracksPayload(trackUris),
            PlaylistOutboxJson.Default.PlaylistAddTracksPayload);
        return database.EnqueueOutboxAsync(Kind, playlistUri, payload, ct);
    }

    public async Task ProcessAsync(OutboxEntry entry, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entry.Payload))
            throw new InvalidOperationException("playlist.add-tracks entry missing payload");

        var payload = JsonSerializer.Deserialize(entry.Payload, PlaylistOutboxJson.Default.PlaylistAddTracksPayload)
                      ?? throw new InvalidOperationException("playlist.add-tracks payload deserialization returned null");
        if (payload.Uris is null || payload.Uris.Count == 0) return;

        var playlistUri = entry.PrimaryUri;
        var userData = _session.GetUserData() ?? throw new InvalidOperationException("not authenticated");
        var username = userData.Username;

        // Resume from where the previous attempt left off. Spotify playlists
        // allow duplicate track entries, so we MUST NOT dedupe against current
        // contents — instead we persist the chunk cursor and pick up there.
        var startOffset = entry.ProgressOffset ?? 0;

        for (var offset = startOffset; offset < payload.Uris.Count; offset += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(ChunkSize, payload.Uris.Count - offset);
            var cached = await _playlistCache.GetPlaylistAsync(playlistUri, ct: ct);

            var addOp = new Op
            {
                Kind = Op.Types.Kind.Add,
                Add = new Add { AddLast = true }
            };
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            for (var i = offset; i < offset + take; i++)
            {
                addOp.Add.Items.Add(new Item
                {
                    Uri = payload.Uris[i],
                    Attributes = new ItemAttributes { Timestamp = nowMs },
                });
            }

            var changes = new ListChanges
            {
                BaseRevision = ByteString.CopyFrom(cached.Revision),
                Deltas =
                {
                    new Delta
                    {
                        Ops = { addOp },
                        Info = new ChangeInfo { User = username, Timestamp = nowMs },
                    }
                },
                WantResultingRevisions = true,
                WantSyncResult = true,
                Nonces = { RandomNumberGenerator.GetInt32(1, int.MaxValue) },
            };

            var fresh = await _spClient.ChangePlaylistAsync(playlistUri, changes, ct);
            await _playlistCache.ApplyFreshContentAsync(playlistUri, fresh, ct);

            // Advance cursor BEFORE the next iteration so a crash between here
            // and the next ChangePlaylistAsync doesn't double-apply this chunk.
            await _database.AdvanceOutboxProgressAsync(entry.Id, offset + take, ct);
        }
    }
}

/// <summary>
/// Payload for <see cref="PlaylistAddTracksHandler"/>. Wrapped in a small record
/// so STJ source-gen has a stable type to attach to.
/// </summary>
public sealed record PlaylistAddTracksPayload(IReadOnlyList<string> Uris);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(PlaylistAddTracksPayload))]
internal sealed partial class PlaylistOutboxJson : JsonSerializerContext;
