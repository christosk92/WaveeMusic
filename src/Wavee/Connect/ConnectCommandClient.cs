using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Commands;
using Wavee.Connect.Commands.Wire;
using Wavee.Connect.Diagnostics;
using Wavee.Core;
using Wavee.Core.Session;

namespace Wavee.Connect;

/// <summary>
/// Generic, reusable client for sending Spotify Connect State commands.
/// Protocol: POST connect-state/v1/player/command/from/{from}/to/{to}
/// with ack-based confirmation via dealer cluster updates.
/// </summary>
/// <remarks>
/// This client knows nothing about specific command semantics (play, pause, etc.).
/// It sends any command endpoint with optional data and waits for ack confirmation.
/// Reusable for playback, volume, transfer, queue, and future command types.
/// </remarks>
public sealed class ConnectCommandClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultAckTimeout = TimeSpan.FromSeconds(10);

    private readonly Session _session;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly IRemoteStateRecorder? _remoteStateRecorder;

    // Pending ack_ids waiting for confirmation
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAcks = new();
    private IDisposable? _clusterSubscription;
    private bool _subscribed;
    private bool _disposed;

    /// <summary>
    /// Creates a new ConnectCommandClient. DealerClient subscription is deferred
    /// until first use (Session.Dealer may be null during DI construction).
    /// </summary>
    public ConnectCommandClient(
        Session session,
        HttpClient httpClient,
        ILogger? logger = null,
        IRemoteStateRecorder? remoteStateRecorder = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
        _remoteStateRecorder = remoteStateRecorder;
    }

    private void EnsureSubscribed()
    {
        if (_subscribed) return;
        var dealer = _session.Dealer;
        if (dealer == null) return;

        _clusterSubscription = dealer.Messages
            .Where(m => m.Uri.StartsWith("hm://connect-state/", StringComparison.OrdinalIgnoreCase))
            .Subscribe(OnClusterUpdate);
        _subscribed = true;
        _logger?.LogDebug("ConnectCommandClient subscribed to dealer cluster updates");
        _remoteStateRecorder.Record(
            kind: RemoteStateEventKind.SubscriptionRegistered,
            direction: RemoteStateDirection.Internal,
            summary: "ConnectCommandClient -> hm://connect-state/* (ack confirmation)");
    }

    /// <summary>
    /// Sends a command to a target device and waits for ack confirmation.
    /// </summary>
    /// <param name="targetDeviceId">Device ID to send the command to.</param>
    /// <param name="endpoint">Command endpoint (e.g. "pause", "resume", "play", "seek_to").</param>
    /// <param name="commandData">Additional command data merged into the command object. May be null for simple commands.</param>
    /// <param name="ackTimeout">How long to wait for ack. Defaults to 10 seconds.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ConnectCommandResult> SendCommandAsync(
        string targetDeviceId,
        string endpoint,
        Dictionary<string, object>? commandData = null,
        bool waitForAck = true,
        TimeSpan? ackTimeout = null,
        CancellationToken ct = default)
    {
        EnsureSubscribed();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var timeout = ackTimeout ?? DefaultAckTimeout;

        try
        {
            var accessToken = await _session.GetAccessTokenAsync(ct);
            var baseUrl = _session.SpClient.BaseUrl;
            var url = $"{baseUrl}/connect-state/v1/player/command/from/{_session.Config.DeviceId}/to/{targetDeviceId}";

            // Build command body
            var commandId = Guid.NewGuid().ToString("N");
            var command = new Dictionary<string, object>
            {
                ["endpoint"] = endpoint,
                ["logging_params"] = new Dictionary<string, object>
                {
                    ["page_instance_ids"] = Array.Empty<string>(),
                    ["interaction_ids"] = Array.Empty<string>(),
                    ["command_id"] = commandId
                }
            };

            // Merge additional command data
            if (commandData != null)
            {
                foreach (var (key, value) in commandData)
                {
                    if (key != "endpoint") // Don't overwrite endpoint
                        command[key] = value;
                }
            }

            var body = new Dictionary<string, object> { ["command"] = command };
            var json = SerializeDynamic(body);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            request.Content = new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));

            _logger?.LogDebug("Connect command: {Endpoint} → from/{From}/to/{To}",
                endpoint, _session.Config.DeviceId, targetDeviceId);

            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusMessage = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                _logger?.LogWarning("Connect command {Endpoint} failed: {Status}", endpoint, statusMessage);
                return ConnectCommandResult.Failure(statusMessage, response.StatusCode);
            }

            // Parse ack_id
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            string? ackId = null;
            try
            {
                var ackResponse = JsonSerializer.Deserialize(responseJson, ConnectAckJsonContext.Default.ConnectAckResponse);
                ackId = ackResponse?.AckId;
            }
            catch { /* Response may not be JSON — that's ok */ }

            if (string.IsNullOrEmpty(ackId))
            {
                _logger?.LogDebug("Connect command {Endpoint} accepted (no ack_id)", endpoint);
                return ConnectCommandResult.Success(null);
            }

            if (!waitForAck)
            {
                _logger?.LogDebug("Connect command {Endpoint} accepted (ack wait bypassed): {AckId}", endpoint, ackId);
                return ConnectCommandResult.Success(ackId);
            }

            // Wait for ack confirmation from dealer
            _logger?.LogDebug("Waiting for ack: {AckId} (timeout: {Timeout}s)", ackId, timeout.TotalSeconds);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[ackId] = tcs;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                await tcs.Task.WaitAsync(timeoutCts.Token);
                _logger?.LogInformation("Connect command {Endpoint} confirmed (ack: {AckId})", endpoint, ackId);
                return ConnectCommandResult.Success(ackId);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogWarning("Connect command {Endpoint} timed out (ack: {AckId})", endpoint, ackId);
                return ConnectCommandResult.Timeout(ackId, timeout);
            }
            finally
            {
                _pendingAcks.TryRemove(ackId, out _);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ConnectCommandResult.Failure("Cancelled.", null);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Connect command {Endpoint} network error", endpoint);
            return ConnectCommandResult.Failure($"Network error: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error in connect command: {Endpoint}", endpoint);
            return ConnectCommandResult.Failure($"Unexpected error: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Sends a `play` command to a remote device, building the wire envelope
    /// (matches Spotify desktop 1:1 — prepare_play_options, play_options,
    /// intent_id, connection_type, full play_origin, conditional `pages` per
    /// context kind) from a typed <see cref="PlayCommand"/>.
    /// Use this for <c>endpoint:"play"</c>; other endpoints keep using
    /// <see cref="SendCommandAsync"/>.
    /// </summary>
    public Task<ConnectCommandResult> SendPlayCommandAsync(
        string targetDeviceId,
        PlayCommand cmd,
        bool waitForAck = true,
        TimeSpan? ackTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var envelope = BuildRemotePlayEnvelope(cmd, _session.Config.DeviceId);
        return SendPlayEnvelopeAsync(targetDeviceId, envelope, waitForAck, ackTimeout, ct);
    }

    /// <summary>
    /// Library / collection contexts (<c>spotify:user:&lt;id&gt;:collection</c>)
    /// are sent without a <c>pages</c> array — the remote target resolves
    /// the track list from <c>context.url</c> + sort/filter query string.
    /// Playlists / albums / radio stations include pages with real uids so
    /// the remote sees the canonical order.
    /// </summary>
    private static bool IsCollectionContext(string contextUri) =>
        contextUri.Contains(":collection", StringComparison.OrdinalIgnoreCase);

    private static string PlayOriginFeatureForUri(string contextUri) => contextUri switch
    {
        _ when contextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal) => "playlist",
        _ when contextUri.StartsWith("spotify:album:",    StringComparison.Ordinal) => "album",
        _ when contextUri.StartsWith("spotify:artist:",   StringComparison.Ordinal) => "artist",
        _ when contextUri.StartsWith("spotify:show:",     StringComparison.Ordinal) => "show",
        _ when contextUri.StartsWith("spotify:episode:",  StringComparison.Ordinal) => "episode",
        _ when contextUri.Contains("collection",          StringComparison.OrdinalIgnoreCase) => "your_library",
        _ => "your_library"
    };

    /// <summary>
    /// Builds the strongly-typed wire envelope for a remote `play` command.
    /// Matches Spotify desktop's shape 1:1.
    /// </summary>
    private static RemoteCommandEnvelope BuildRemotePlayEnvelope(PlayCommand cmd, string senderDeviceId)
    {
        var contextUri = cmd.ContextUri ?? "spotify:internal:queue";
        var isCollection = IsCollectionContext(contextUri);
        var feature = cmd.ContextFeature ?? PlayOriginFeatureForUri(contextUri);

        // ── Pages (only for non-collection contexts that have rich tracks) ──
        IReadOnlyList<RemoteContextPage>? pages = null;
        if (!isCollection && cmd.PageTracks is { Count: > 0 } pt)
        {
            var wireTracks = new List<RemoteContextTrack>(pt.Count);
            foreach (var t in pt)
            {
                IReadOnlyDictionary<string, string>? trackMeta = null;
                if (t.Metadata is { Count: > 0 } md)
                {
                    var copy = new Dictionary<string, string>(md.Count);
                    foreach (var kv in md) copy[kv.Key] = kv.Value ?? string.Empty;
                    trackMeta = copy;
                }

                wireTracks.Add(new RemoteContextTrack
                {
                    Uri = t.Uri,
                    Uid = t.Uid ?? string.Empty,
                    Metadata = trackMeta
                });
            }
            pages = [new RemoteContextPage { Tracks = wireTracks }];
        }

        // ── Context metadata (passed through verbatim from playlist resolve) ──
        IReadOnlyDictionary<string, string> contextMetadata = cmd.ContextFormatAttributes is { Count: > 0 } fa
            ? new Dictionary<string, string>(fa)
            : new Dictionary<string, string>();

        // ── Skip-to ──
        // For library/collection: { track_uri, track_index } only. For
        // playlists/albums: include track_uid when we have it (from PageTracks).
        // Derive track_uri from PageTracks when the typed command didn't set
        // it (PlayTracksAsync skips that), matching Spotify desktop's habit of
        // always emitting track_uri.
        RemoteSkipTo? skipTo = null;
        var startTrackUri = cmd.TrackUri;
        var startIndex = cmd.SkipToIndex;
        string? startUid = cmd.TrackUid;

        if (string.IsNullOrEmpty(startTrackUri)
            && cmd.PageTracks is { Count: > 0 } srcPages
            && (startIndex ?? 0) >= 0
            && (startIndex ?? 0) < srcPages.Count)
        {
            var idx = startIndex ?? 0;
            startTrackUri = srcPages[idx].Uri;
            if (string.IsNullOrEmpty(startUid)) startUid = srcPages[idx].Uid;
        }

        if (!string.IsNullOrEmpty(startTrackUri) || startIndex.HasValue || !string.IsNullOrEmpty(startUid))
        {
            skipTo = new RemoteSkipTo
            {
                TrackUri = string.IsNullOrEmpty(startTrackUri) ? null : startTrackUri,
                TrackUid = isCollection ? null : (string.IsNullOrEmpty(startUid) ? null : startUid),
                TrackIndex = startIndex
            };
        }

        // ── session_id from playlist_volatile_context_id when available ──
        string sessionId = "";
        if (cmd.ContextFormatAttributes is { } fa2
            && fa2.TryGetValue("playlist_volatile_context_id", out var volId)
            && !string.IsNullOrEmpty(volId))
        {
            sessionId = volId;
        }

        var preparePlayOptions = new RemotePreparePlayOptions
        {
            SkipTo = skipTo,
            PlayerOptionsOverride = new RemotePlayerOptionsOverride
            {
                ShufflingContext = cmd.Options?.ShufflingContext ?? false
            },
            SessionId = sessionId
        };

        var playOrigin = new RemotePlayOrigin
        {
            FeatureIdentifier = feature,
            FeatureVersion = SpotifyClientIdentity.XpuiSnapshotVersion,
            ReferrerIdentifier = "your_library"
        };

        var loggingParams = new RemoteLoggingParams
        {
            CommandInitiatedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PageInstanceIds = [Guid.NewGuid().ToString()],
            InteractionIds = [Guid.NewGuid().ToString()],
            DeviceIdentifier = senderDeviceId,
            CommandId = Guid.NewGuid().ToString("N")
        };

        return new RemoteCommandEnvelope
        {
            Command = new RemoteCommand
            {
                Endpoint = "play",
                Context = new RemoteContext
                {
                    EntityUri = contextUri,
                    Uri = contextUri,
                    Url = $"context://{contextUri}",
                    Metadata = contextMetadata,
                    Pages = pages
                },
                PlayOrigin = playOrigin,
                PreparePlayOptions = preparePlayOptions,
                PlayOptions = new RemotePlayOptions(),
                LoggingParams = loggingParams
            },
            IntentId = Guid.NewGuid().ToString("N")
        };
    }

    private async Task<ConnectCommandResult> SendPlayEnvelopeAsync(
        string targetDeviceId,
        RemoteCommandEnvelope envelope,
        bool waitForAck = true,
        TimeSpan? ackTimeout = null,
        CancellationToken ct = default)
    {
        EnsureSubscribed();
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDeviceId);
        ArgumentNullException.ThrowIfNull(envelope);

        var timeout = ackTimeout ?? DefaultAckTimeout;

        try
        {
            var accessToken = await _session.GetAccessTokenAsync(ct);
            var baseUrl = _session.SpClient.BaseUrl;
            var url = $"{baseUrl}/connect-state/v1/player/command/from/{_session.Config.DeviceId}/to/{targetDeviceId}";

            var json = JsonSerializer.Serialize(envelope, RemotePlayCommandJsonContext.Default.RemoteCommandEnvelope);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
            request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

            _logger?.LogDebug("Connect play envelope: from/{From}/to/{To} ({Bytes} bytes)",
                _session.Config.DeviceId, targetDeviceId, json.Length);

            using var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusMessage = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                _logger?.LogWarning("Connect play envelope failed: {Status}", statusMessage);
                return ConnectCommandResult.Failure(statusMessage, response.StatusCode);
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            string? ackId = null;
            try
            {
                var ackResponse = JsonSerializer.Deserialize(responseJson, ConnectAckJsonContext.Default.ConnectAckResponse);
                ackId = ackResponse?.AckId;
            }
            catch { /* response may not be JSON */ }

            if (string.IsNullOrEmpty(ackId))
                return ConnectCommandResult.Success(null);

            if (!waitForAck)
                return ConnectCommandResult.Success(ackId);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[ackId] = tcs;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                await tcs.Task.WaitAsync(timeoutCts.Token);
                return ConnectCommandResult.Success(ackId);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return ConnectCommandResult.Timeout(ackId, timeout);
            }
            finally
            {
                _pendingAcks.TryRemove(ackId, out _);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ConnectCommandResult.Failure("Cancelled.", null);
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogWarning(ex, "Connect play envelope network error");
            return ConnectCommandResult.Failure($"Network error: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error sending play envelope");
            return ConnectCommandResult.Failure($"Unexpected error: {ex.Message}", null);
        }
    }

    /// <summary>
    /// AOT-safe serialization of nested Dictionary/Array/primitive structures.
    /// </summary>
    private static string SerializeDynamic(object value)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        WriteDynamicValue(writer, value);
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteDynamicValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case Dictionary<string, object> dict:
                writer.WriteStartObject();
                foreach (var (k, v) in dict)
                {
                    writer.WritePropertyName(k);
                    WriteDynamicValue(writer, v);
                }
                writer.WriteEndObject();
                break;
            case Dictionary<string, string> strDict:
                writer.WriteStartObject();
                foreach (var (k, v) in strDict)
                {
                    writer.WritePropertyName(k);
                    writer.WriteStringValue(v);
                }
                writer.WriteEndObject();
                break;
            case System.Collections.IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                    WriteDynamicValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private void OnClusterUpdate(Protocol.DealerMessage _)
    {
        // Any cluster update confirms all pending commands
        foreach (var (ackId, tcs) in _pendingAcks)
        {
            if (tcs.TrySetResult(true))
                _logger?.LogTrace("Ack confirmed by cluster update: {AckId}", ackId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _clusterSubscription?.Dispose();
        foreach (var (_, tcs) in _pendingAcks)
            tcs.TrySetCanceled();
        _pendingAcks.Clear();
    }
}

/// <summary>
/// Result of a Connect State command.
/// </summary>
public sealed record ConnectCommandResult
{
    public bool IsSuccess { get; init; }
    public bool IsTimeout { get; init; }
    public string? AckId { get; init; }
    public string? ErrorMessage { get; init; }
    public HttpStatusCode? HttpStatus { get; init; }

    public static ConnectCommandResult Success(string? ackId) =>
        new() { IsSuccess = true, AckId = ackId };

    public static ConnectCommandResult Failure(string message, HttpStatusCode? status) =>
        new() { IsSuccess = false, ErrorMessage = message, HttpStatus = status };

    public static ConnectCommandResult Timeout(string ackId, TimeSpan timeout) =>
        new() { IsSuccess = false, IsTimeout = true, AckId = ackId,
            ErrorMessage = $"Command not confirmed within {timeout.TotalSeconds}s." };
}

// ── JSON ──

internal sealed record ConnectAckResponse
{
    [JsonPropertyName("ack_id")]
    public string? AckId { get; init; }
}

[JsonSerializable(typeof(ConnectAckResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ConnectAckJsonContext : JsonSerializerContext { }
