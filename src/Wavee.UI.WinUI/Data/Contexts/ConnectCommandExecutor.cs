using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Audio.Queue;
using Wavee.Core.Session;
using Wavee.Core.Audio;
using Wavee.Playback.Contracts;
using Wavee.UI.Models;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Implements <see cref="IPlaybackCommandExecutor"/> by building playback-specific
/// command dictionaries and delegating to <see cref="ConnectCommandClient"/>.
/// </summary>
internal sealed class ConnectCommandExecutor : IPlaybackCommandExecutor, IAudioPipelineControl
{
    private readonly ConnectCommandClient _client;
    private readonly Session _session;
    private readonly ILogger? _logger;
    private IPlaybackEngine? _localEngine;
    private Wavee.AudioIpc.AudioPipelineProxy? _audioPipelineProxy;

    /// <summary>
    /// Enables local playback routing through the given engine.
    /// Called once after AudioPipeline is created in InitializePlaybackEngine.
    /// </summary>
    internal void EnableLocalPlayback(IPlaybackEngine engine)
    {
        _localEngine = engine;
        if (engine is Wavee.AudioIpc.AudioPipelineProxy proxy)
            _audioPipelineProxy = proxy;
    }

    /// <summary>
    /// Wires live audio-pipeline controls. Playback commands route through
    /// PlaybackOrchestrator, but EQ/normalization/quality are AudioHost-level
    /// controls and must still reach the raw IPC proxy.
    /// </summary>
    internal void EnableAudioPipelineControl(Wavee.AudioIpc.AudioPipelineProxy proxy)
        => _audioPipelineProxy = proxy ?? throw new ArgumentNullException(nameof(proxy));

    /// <summary>
    /// Disables local playback routing (e.g. on logout).
    /// </summary>
    internal void DisableLocalPlayback()
    {
        _localEngine = null;
        _audioPipelineProxy = null;
    }

    public ConnectCommandExecutor(ConnectCommandClient client, Session session, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger;
    }

    // ── IAudioPipelineControl ──

    public async Task SwitchQualityAsync(AudioQuality quality, CancellationToken ct = default)
    {
        if (GetPipelineProxy() is { } proxy)
        {
            await proxy.SwitchQualityAsync(quality.ToString(), ct).ConfigureAwait(false);
            return;
        }

        _logger?.LogWarning("SwitchQualityAsync ignored: AudioHost proxy is not connected");
    }

    public void SetNormalizationEnabled(bool enabled)
    {
        if (GetPipelineProxy() is { } proxy)
        {
            _ = proxy.SetNormalizationEnabledAsync(enabled);
            return;
        }

        _logger?.LogWarning("SetNormalizationEnabled ignored: AudioHost proxy is not connected");
    }

    public async Task<EqualizerApplyResult> SetEqualizerAsync(bool enabled, double[]? bandGains, CancellationToken ct = default)
    {
        if (GetPipelineProxy() is { } proxy)
        {
            return await proxy.SetEqualizerAsync(enabled, bandGains, ct).ConfigureAwait(false);
        }

        const string message = "AudioHost proxy is not connected";
        _logger?.LogWarning("SetEqualizerAsync ignored: {Message}", message);
        throw new InvalidOperationException(message);
    }

    private Wavee.AudioIpc.AudioPipelineProxy? GetPipelineProxy()
        => _audioPipelineProxy ?? (_localEngine as Wavee.AudioIpc.AudioPipelineProxy);

    private string? GetTargetDeviceId() =>
        _session.PlaybackState?.CurrentState.ActiveDeviceId;

    private PlaybackResult ToPlaybackResult(ConnectCommandResult r)
    {
        if (r.IsSuccess) return PlaybackResult.Success();
        if (r.IsTimeout) return PlaybackResult.Failure(PlaybackErrorKind.Unavailable, r.ErrorMessage!);

        return r.HttpStatus switch
        {
            HttpStatusCode.Unauthorized => PlaybackResult.Failure(PlaybackErrorKind.Unauthorized, "Session expired."),
            HttpStatusCode.Forbidden => PlaybackResult.Failure(PlaybackErrorKind.PremiumRequired, "Not authorized."),
            HttpStatusCode.NotFound => PlaybackResult.Failure(PlaybackErrorKind.DeviceUnavailable, "Device not found."),
            HttpStatusCode.TooManyRequests => PlaybackResult.Failure(PlaybackErrorKind.RateLimited, "Too many requests."),
            _ => PlaybackResult.Failure(PlaybackErrorKind.Unknown, r.ErrorMessage ?? "Command failed.")
        };
    }

    private Task<PlaybackResult> SendAsync(string endpoint, Dictionary<string, object>? data, CancellationToken ct)
        => SendAsync(endpoint, data, typedPlayCommand: null, ct);

    private async Task<PlaybackResult> SendAsync(
        string endpoint, Dictionary<string, object>? data,
        Wavee.Connect.Commands.PlayCommand? typedPlayCommand, CancellationToken ct)
    {
        var target = GetTargetDeviceId();
        var selfId = _session.Config.DeviceId;

        // No active device OR self-targeted: route to local AudioPipeline.
        if (string.IsNullOrEmpty(target) || target == selfId)
        {
            if (_localEngine != null)
            {
                _logger?.LogInformation("[Executor] {Endpoint}: routing LOCAL (target={Target}, self={Self})",
                    endpoint, target ?? "<none>", selfId);

                // For play commands: prefer the typed PlayCommand over lossy dict deserialization
                if (endpoint == "play" && typedPlayCommand != null)
                {
                    try
                    {
                        _logger?.LogDebug("[Executor] play → typed PlayCommand: context={Context}, track={Track}, index={Index}",
                            typedPlayCommand.ContextUri ?? "<none>", typedPlayCommand.TrackUri ?? "<none>", typedPlayCommand.SkipToIndex);
                        await Task.Run(async () => await _localEngine.PlayAsync(typedPlayCommand, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                        _logger?.LogInformation("[Executor] play OK (local engine accepted)");
                        return PlaybackResult.Success();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "[Executor] play FAILED (local engine threw)");
                        return PlaybackResult.Failure(PlaybackErrorKind.Unknown, ex.Message, ex);
                    }
                }

                var localResult = await RouteToLocalEngineAsync(_localEngine, endpoint, data, ct).ConfigureAwait(false);
                if (localResult.IsSuccess)
                    _logger?.LogInformation("[Executor] {Endpoint} OK (local engine accepted)", endpoint);
                else
                    _logger?.LogWarning("[Executor] {Endpoint} FAILED (local engine): {Error}", endpoint, localResult.ErrorMessage);
                return localResult;
            }

            _logger?.LogWarning("[Executor] {Endpoint}: no local engine and no active device — command dropped!", endpoint);
            return PlaybackResult.Failure(PlaybackErrorKind.DeviceUnavailable,
                "No active device and no local playback engine available.");
        }

        // Remote routing.
        _logger?.LogInformation("[Executor] {Endpoint}: routing REMOTE → device={Target}", endpoint, target);
        var waitForAck = ShouldWaitForAck(endpoint);
        var ackTimeout = GetAckTimeout(endpoint);

        ConnectCommandResult result;
        if (endpoint == "play" && typedPlayCommand != null)
        {
            // Strongly-typed envelope (built inside ConnectCommandClient)
            // matches Spotify desktop's wire shape 1:1 — prepare_play_options,
            // play_options, full play_origin, intent_id, connection_type,
            // restrictions, conditional `pages` per context kind.
            result = await _client.SendPlayCommandAsync(
                target,
                typedPlayCommand,
                waitForAck: waitForAck,
                ackTimeout: ackTimeout,
                ct: ct).ConfigureAwait(false);
        }
        else
        {
            // Pause / skip / seek / volume / shuffle / repeat — keep the
            // existing dict path until we extend envelope coverage to them.
            result = await _client.SendCommandAsync(
                target,
                endpoint,
                data,
                waitForAck: waitForAck,
                ackTimeout: ackTimeout,
                ct: ct).ConfigureAwait(false);
        }

        if (result.IsSuccess)
            _logger?.LogInformation("[Executor] {Endpoint} OK (remote, waitAck={WaitAck})", endpoint, waitForAck);
        else
            _logger?.LogWarning("[Executor] {Endpoint} FAILED (remote, waitAck={WaitAck}): {Error}", endpoint, waitForAck, result.ErrorMessage);

        return ToPlaybackResult(result);
    }

    private static TimeSpan GetAckTimeout(string endpoint) => endpoint switch
    {
        // Keep play/start slightly higher than transport toggles.
        "play" or "add_to_queue" or "set_queue" or "transfer" => TimeSpan.FromMilliseconds(2500),

        // Interactive transport commands should fail fast if no confirmation arrives.
        "pause" or "resume" or "skip_next" or "skip_prev" or "seek_to" => TimeSpan.FromMilliseconds(1500),

        // Option toggles are non-critical; short timeout keeps UI snappy.
        "set_shuffling_context" or "set_repeating_context" or "set_repeating_track" or "set_options" or "set_volume" => TimeSpan.FromMilliseconds(1200),

        _ => TimeSpan.FromMilliseconds(2000)
    };

    private static bool ShouldWaitForAck(string endpoint) => endpoint switch
    {
        // In out-of-process mode the UI dealer stream may not emit timely ack signals,
        // so waiting here makes controls feel unresponsive. Transfer is included because
        // the HTTP 200 from /connect-state/v1/connect/transfer is already a server-side
        // confirmation — the dealer ack is a secondary signal that often doesn't arrive
        // within the 2.5s window when the target device is slow to pick up the command.
        "play" or "add_to_queue" or "set_queue" or "pause" or "resume" or "skip_next" or "skip_prev" or "seek_to"
            or "set_shuffling_context" or "set_repeating_context" or "set_repeating_track" or "set_options" or "set_volume"
            or "transfer" => false,
        _ => true
    };

    // ── Playback commands ──

    public Task<PlaybackResult> PlayContextAsync(string contextUri, PlayContextOptions? options, CancellationToken ct)
    {
        var data = new Dictionary<string, object>
        {
            ["context"] = new Dictionary<string, object>
            {
                ["uri"] = contextUri,
                ["url"] = $"context://{contextUri}"
            },
            ["play_origin"] = new Dictionary<string, object>
            {
                ["feature_identifier"] = options?.PlayOriginFeature ?? "wavee",
                ["feature_version"] = "0"
            }
        };

        var commandOptions = new Dictionary<string, object>();

        if (options?.StartTrackUri != null)
            commandOptions["skip_to"] = new Dictionary<string, object> { ["track_uri"] = options.StartTrackUri };
        else if (options?.StartIndex is > 0)
            commandOptions["skip_to"] = new Dictionary<string, object> { ["track_index"] = options.StartIndex.Value };

        var forceEpisodeStartAtZero = options?.PositionMs is null
                                      && (IsEpisodeUri(options?.StartTrackUri)
                                          || IsEpisodeUri(contextUri));

        if (options?.PositionMs is >= 0 || forceEpisodeStartAtZero)
            commandOptions["seek_to"] = options?.PositionMs ?? 0;

        if (options?.Shuffle == true)
            commandOptions["player_options_override"] = new Dictionary<string, object> { ["shuffling_context"] = true };

        if (commandOptions.Count > 0)
            data["options"] = commandOptions;

        var typedCmd = new Wavee.Connect.Commands.PlayCommand
        {
            Endpoint = "play",
            Key = "local/0",
            MessageId = 0,
            MessageIdent = "local",
            SenderDeviceId = "",
            ContextUri = contextUri,
            ContextFeature = PlayOriginFeatureForUri(contextUri),
            TrackUri = options?.StartTrackUri,
            SkipToIndex = options?.StartIndex,
            PositionMs = options?.PositionMs ?? (forceEpisodeStartAtZero ? 0L : null),
        };

        return SendAsync("play", data, typedCmd, ct);
    }

    public Task<PlaybackResult> PlayTracksAsync(IReadOnlyList<string> trackUris, int startIndex, PlaybackContextInfo? context, IReadOnlyList<QueueItem>? richTracks, CancellationToken ct)
    {
        // Normalize bare IDs to full spotify:track: URIs
        var normalizedUris = trackUris.Select(uri =>
            uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri : $"spotify:track:{uri}").ToList();

        // Align rich tracks one-to-one with normalizedUris when supplied. If the
        // caller gave a mismatched count we ignore richTracks — the bare URI
        // list still produces a valid play command.
        var useRichTracks = richTracks is not null && richTracks.Count == normalizedUris.Count;

        var tracks = new List<Dictionary<string, object>>(normalizedUris.Count);
        var pageTracks = new List<Wavee.Connect.Commands.PageTrack>(normalizedUris.Count);
        for (int i = 0; i < normalizedUris.Count; i++)
        {
            var uri = normalizedUris[i];
            var uid = useRichTracks ? (richTracks![i].Uid ?? string.Empty) : string.Empty;
            var metadata = useRichTracks ? richTracks![i].Metadata : null;

            var wireTrack = new Dictionary<string, object>
            {
                ["uri"] = uri,
                ["uid"] = uid
            };
            if (metadata is { Count: > 0 })
            {
                var md = new Dictionary<string, object>(metadata.Count);
                foreach (var kv in metadata) md[kv.Key] = kv.Value ?? string.Empty;
                wireTrack["metadata"] = md;
            }
            tracks.Add(wireTrack);

            pageTracks.Add(new Wavee.Connect.Commands.PageTrack(uri, uid) { Metadata = metadata });
        }

        var contextUri = context?.ContextUri ?? "spotify:internal:queue";

        var data = new Dictionary<string, object>
        {
            ["context"] = new Dictionary<string, object>
            {
                ["uri"] = contextUri,
                ["url"] = $"context://{contextUri}",
                ["pages"] = new[] { new Dictionary<string, object> { ["tracks"] = tracks } }
            },
            ["play_origin"] = new Dictionary<string, object>
            {
                ["feature_identifier"] = PlayOriginFeatureFor(context?.Type),
                ["feature_version"] = "Wavee/1.0.0.0"
            }
        };

        var startUri = startIndex >= 0 && startIndex < normalizedUris.Count
            ? normalizedUris[startIndex]
            : null;
        var forceEpisodeStartAtZero = IsEpisodeUri(startUri);
        var commandOptions = new Dictionary<string, object>();

        if (startIndex > 0)
            commandOptions["skip_to"] = new Dictionary<string, object> { ["track_index"] = startIndex };

        if (forceEpisodeStartAtZero)
            commandOptions["seek_to"] = 0;

        if (commandOptions.Count > 0)
            data["options"] = commandOptions;

        var typedCmd = new Wavee.Connect.Commands.PlayCommand
        {
            Endpoint = "play",
            Key = "local/0",
            MessageId = 0,
            MessageIdent = "local",
            SenderDeviceId = "",
            ContextUri = contextUri,
            ContextDescription = context?.Name,
            ContextImageUrl = context?.ImageUrl,
            ContextFeature = context is null ? null : PlayOriginFeatureFor(context.Type),
            ContextTrackCount = context is null ? null : normalizedUris.Count,
            ContextFormatAttributes = context?.FormatAttributes,
            SkipToIndex = startIndex > 0 ? startIndex : null,
            PositionMs = forceEpisodeStartAtZero ? 0L : null,
            PageTracks = pageTracks,
        };

        return SendAsync("play", data, typedCmd, ct);
    }

    private static string PlayOriginFeatureFor(PlaybackContextType? type) => type switch
    {
        PlaybackContextType.Playlist   => "playlist",
        PlaybackContextType.Album      => "album",
        PlaybackContextType.Artist     => "artist",
        PlaybackContextType.Show       => "show",
        PlaybackContextType.Episode    => "episode",
        // Spotify desktop sends "your_library" (not "collection") for Liked
        // Songs transfer-play. Using their value avoids being classified as
        // an unknown surface server-side.
        PlaybackContextType.LikedSongs => "your_library",
        _                              => "your_library"
    };

    private static string? PlayOriginFeatureForUri(string contextUri) =>
        contextUri switch
        {
            _ when contextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal) => "playlist",
            _ when contextUri.StartsWith("spotify:album:",    StringComparison.Ordinal) => "album",
            _ when contextUri.StartsWith("spotify:artist:",   StringComparison.Ordinal) => "artist",
            _ when contextUri.StartsWith("spotify:show:",     StringComparison.Ordinal) => "show",
            _ when contextUri.StartsWith("spotify:episode:",  StringComparison.Ordinal) => "episode",
            _ when contextUri.Contains("collection",          StringComparison.OrdinalIgnoreCase) => "your_library",
            _ => null
        };

    private static bool IsEpisodeUri(string? uri)
        => uri?.StartsWith("spotify:episode:", StringComparison.Ordinal) == true;

    public Task<PlaybackResult> ResumeAsync(CancellationToken ct)
        => SendAsync("resume", null, ct);

    public Task<PlaybackResult> PauseAsync(CancellationToken ct)
        => SendAsync("pause", null, ct);

    public Task<PlaybackResult> SkipNextAsync(CancellationToken ct)
        => SendAsync("skip_next", null, ct);

    public Task<PlaybackResult> SkipPreviousAsync(CancellationToken ct)
        => SendAsync("skip_prev", null, ct);

    public Task<PlaybackResult> SeekAsync(long positionMs, CancellationToken ct)
        => SendAsync("seek_to", new Dictionary<string, object> { ["value"] = positionMs }, ct);

    public Task<PlaybackResult> SetShuffleAsync(bool enabled, CancellationToken ct)
        => SendAsync("set_shuffling_context", new Dictionary<string, object> { ["value"] = enabled }, ct);

    public Task<PlaybackResult> SetRepeatAsync(string state, CancellationToken ct)
    {
        var (endpoint, value) = state switch
        {
            "track" => ("set_repeating_track", true),
            "context" => ("set_repeating_context", true),
            _ => ("set_repeating_context", false)
        };
        return SendAsync(endpoint, new Dictionary<string, object> { ["value"] = value }, ct);
    }

    public Task<PlaybackResult> SetPlaybackSpeedAsync(double speed, CancellationToken ct)
    {
        var normalized = Math.Clamp(speed, 0.5, 3.5);
        return SendAsync("set_options", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["playback_speed"] = normalized
            }
        }, ct);
    }

    public Task<PlaybackResult> SetVolumeAsync(int volumePercent, CancellationToken ct)
        => SendAsync("set_volume", new Dictionary<string, object>
        {
            ["value"] = (int)(Math.Clamp(volumePercent, 0, 100) / 100.0 * 65535)
        }, ct);

    /// <summary>
    /// "Add to Queue" — appends the track to the user queue.
    /// Locally: post-context bucket (plays after context exhausts).
    /// Remotely: appended at the tail of the remote's user queue via set_queue
    /// (Spotify Connect doesn't model a separate post-context bucket).
    /// </summary>
    public Task<PlaybackResult> AddToQueueAsync(string trackUri, CancellationToken ct)
        => SendQueueMutationAsync(trackUri, atHead: false, ct);

    /// <summary>
    /// "Play Next" — inserts the track at the head of the user queue so it
    /// plays immediately after the current track. Local + remote both supported;
    /// remote sends set_queue with the new entry at index 0 (uid="q0").
    /// </summary>
    public Task<PlaybackResult> PlayNextAsync(string trackUri, CancellationToken ct)
        => SendQueueMutationAsync(trackUri, atHead: true, ct);

    private async Task<PlaybackResult> SendQueueMutationAsync(
        string trackUri, bool atHead, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(trackUri))
            return PlaybackResult.Failure(PlaybackErrorKind.Unknown, "Track URI is required.");

        var target = GetTargetDeviceId();
        var selfId = _session.Config.DeviceId;
        var op = atHead ? "play_next" : "add_to_queue";

        // LOCAL — drive the orchestrator's PlaybackQueue directly.
        if (string.IsNullOrEmpty(target) || target == selfId)
        {
            if (_localEngine == null)
            {
                _logger?.LogWarning(
                    "[Executor] {Op}: no local engine and no active device — command dropped",
                    op);
                return PlaybackResult.Failure(
                    PlaybackErrorKind.DeviceUnavailable,
                    "No active device and no local playback engine available.");
            }

            try
            {
                _logger?.LogInformation("[Executor] {Op}: routing LOCAL → {Uri}", op, trackUri);
                if (atHead)
                    await _localEngine.PlayNextAsync(trackUri, ct).ConfigureAwait(false);
                else
                    await _localEngine.EnqueueAsync(trackUri, ct).ConfigureAwait(false);
                return PlaybackResult.Success();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[Executor] {Op} FAILED (local engine threw)", op);
                return PlaybackResult.Failure(PlaybackErrorKind.Unknown, ex.Message, ex);
            }
        }

        // REMOTE — send set_queue with the full user-queue snapshot to mimic
        // the desktop client. Connect doesn't expose a "post-context" bucket,
        // so atHead=false collapses to "append at tail of remote user queue".
        var body = BuildSetQueueBody(trackUri, atHead);
        _logger?.LogInformation(
            "[Executor] {Op}: routing REMOTE set_queue → device={Target}, queueSize={Size}",
            op, target, ((System.Collections.ICollection)body["next_tracks"]).Count);

        var result = await _client.SendCommandAsync(
            target,
            "set_queue",
            body,
            waitForAck: false,
            ackTimeout: TimeSpan.FromMilliseconds(2000),
            ct: ct).ConfigureAwait(false);

        if (result.IsSuccess)
            _logger?.LogInformation("[Executor] {Op} OK (remote set_queue)", op);
        else
            _logger?.LogWarning("[Executor] {Op} FAILED (remote set_queue): {Error}", op, result.ErrorMessage);

        return ToPlaybackResult(result);
    }

    /// <summary>
    /// Builds the JSON body for set_queue: a full snapshot of the remote
    /// user queue with the new track inserted at index 0 (Play Next) or
    /// appended at the tail (Add to Queue). Existing queued entries are
    /// read from the latest cluster state via PlaybackStateManager and
    /// emitted with bare metadata (the remote already has rich data for
    /// them).
    /// </summary>
    private Dictionary<string, object> BuildSetQueueBody(string trackUri, bool atHead)
    {
        // Read currently-queued tracks from the remote cluster snapshot. Tracks
        // with IsUserQueued=true are the "user queue" entries (provider="queue").
        var clusterNext = _session.PlaybackState?.CurrentState.NextTracks
                          ?? (IReadOnlyList<TrackReference>)System.Array.Empty<TrackReference>();

        var existingQueued = new List<object>();
        foreach (var t in clusterNext)
        {
            if (!t.IsUserQueued) continue;
            existingQueued.Add(new Dictionary<string, object>
            {
                ["uri"] = t.Uri,
                ["provider"] = "queue",
                ["metadata"] = new Dictionary<string, string> { ["is_queued"] = "true" }
            });
        }

        var newEntry = new Dictionary<string, object>
        {
            ["uri"] = trackUri,
            ["uid"] = "q0",
            ["provider"] = "queue",
            ["metadata"] = new Dictionary<string, string> { ["is_queued"] = "true" }
        };

        var nextTracks = new List<object>(existingQueued.Count + 1);
        if (atHead)
        {
            nextTracks.Add(newEntry);
            nextTracks.AddRange(existingQueued);
        }
        else
        {
            nextTracks.AddRange(existingQueued);
            nextTracks.Add(newEntry);
        }

        return new Dictionary<string, object>
        {
            ["next_tracks"] = nextTracks
        };
    }

    private async Task<PlaybackResult> RouteToLocalEngineAsync(
        IPlaybackEngine engine, string endpoint, Dictionary<string, object>? data, CancellationToken ct)
    {
        try
        {
            _logger?.LogInformation("Routing {Endpoint} to local AudioPipeline", endpoint);

            switch (endpoint)
            {
                case "resume":
                    // If the in-memory queue is unloaded (e.g. fresh app start: AudioHost has not
                    // started playing yet but PlaybackOrchestrator.PlaybackQueue is empty), seed
                    // the queue from cluster state so that skip-next/prev work correctly.
                    if (engine.CurrentState.CurrentIndex < 0)
                    {
                        var clusterState = _session.PlaybackState?.CurrentState;
                        var seedContext  = clusterState?.ContextUri;
                        var seedTrack    = clusterState?.Track?.Uri;
                        var seedUid      = clusterState?.Track?.Uid;

                        // Prefer AudioHost's reported position (warm-start), else cluster position
                        var enginePos = engine.CurrentState.PositionMs;
                        var seedPos   = enginePos > 0 ? enginePos : (clusterState?.PositionMs ?? 0);

                        if (!string.IsNullOrEmpty(seedContext) && !string.IsNullOrEmpty(seedTrack))
                        {
                            _logger?.LogInformation(
                                "[Executor] resume: queue empty — seeding from cluster: context={Context}, track={Track}, pos={Pos}ms (enginePos={EPos}ms, clusterPos={CPos}ms)",
                                seedContext, seedTrack, seedPos, enginePos, clusterState?.PositionMs ?? 0);

                            // For "spotify:internal:queue" context, PlaybackOrchestrator's context-resolver
                            // branch is skipped. We must supply PageTracks so the queue is populated.
                            List<Wavee.Connect.Commands.PageTrack>? pageTracks = null;
                            int? skipToIndex = null;

                            if (seedContext == "spotify:internal:queue")
                            {
                                // Build a flat track list: prevTracks (oldest first) + current + nextTracks
                                var prevTracks = clusterState?.PrevTracks ?? [];
                                var nextTracks = clusterState?.NextTracks ?? [];

                                pageTracks = [];
                                foreach (var t in prevTracks)
                                    pageTracks.Add(new Wavee.Connect.Commands.PageTrack(t.Uri, t.Uid));

                                skipToIndex = pageTracks.Count; // current track lands here
                                pageTracks.Add(new Wavee.Connect.Commands.PageTrack(seedTrack, seedUid ?? ""));

                                foreach (var t in nextTracks)
                                    pageTracks.Add(new Wavee.Connect.Commands.PageTrack(t.Uri, t.Uid));

                                _logger?.LogInformation(
                                    "[Executor] resume: built PageTracks for internal queue: prev={Prev}, current=@{Idx}, next={Next}, total={Total}",
                                    prevTracks.Count, skipToIndex, nextTracks.Count, pageTracks.Count);
                            }

                            var seedCmd = new Wavee.Connect.Commands.PlayCommand
                            {
                                Endpoint       = "play",
                                Key            = "local/0",
                                MessageId      = 0,
                                MessageIdent   = "local",
                                SenderDeviceId = "",
                                ContextUri     = seedContext,
                                TrackUri       = seedTrack,
                                TrackUid       = seedUid,
                                PositionMs     = seedPos > 0 ? seedPos : null,
                                PageTracks     = pageTracks,
                                SkipToIndex    = skipToIndex,
                            };
                            await engine.PlayAsync(seedCmd, ct).ConfigureAwait(false);
                            break;
                        }

                        _logger?.LogWarning(
                            "[Executor] resume: queue empty and no cluster context — falling through to plain ResumeAsync");
                    }
                    await engine.ResumeAsync(ct).ConfigureAwait(false);
                    break;
                case "pause":
                    await engine.PauseAsync(ct).ConfigureAwait(false);
                    break;
                case "skip_next":
                    await engine.SkipNextAsync(ct).ConfigureAwait(false);
                    break;
                case "skip_prev":
                    await engine.SkipPreviousAsync(ct).ConfigureAwait(false);
                    break;
                case "seek_to":
                    if (data?.TryGetValue("value", out var seekVal) == true && seekVal is long seekMs)
                        await engine.SeekAsync(seekMs, ct).ConfigureAwait(false);
                    else if (data?.TryGetValue("value", out var seekObj) == true)
                        await engine.SeekAsync(Convert.ToInt64(seekObj), ct).ConfigureAwait(false);
                    break;
                case "set_shuffling_context":
                    if (data?.TryGetValue("value", out var shuffleVal) == true)
                        await engine.SetShuffleAsync(Convert.ToBoolean(shuffleVal), ct).ConfigureAwait(false);
                    break;
                case "set_repeating_context":
                    if (data?.TryGetValue("value", out var repeatCtxVal) == true)
                        await engine.SetRepeatContextAsync(Convert.ToBoolean(repeatCtxVal), ct).ConfigureAwait(false);
                    break;
                case "set_repeating_track":
                    if (data?.TryGetValue("value", out var repeatTrkVal) == true)
                        await engine.SetRepeatTrackAsync(Convert.ToBoolean(repeatTrkVal), ct).ConfigureAwait(false);
                    break;
                case "set_options":
                    if (data?.TryGetValue("options", out var optionsObj) == true
                        && optionsObj is Dictionary<string, object> options
                        && options.ContainsKey("playback_speed"))
                    {
                        return PlaybackResult.Failure(
                            PlaybackErrorKind.Unavailable,
                            "Playback speed is not supported by the local audio engine yet.");
                    }
                    break;
                case "set_volume":
                    if (data?.TryGetValue("value", out var volVal) == true)
                    {
                        var vol65535 = Convert.ToInt32(volVal);
                        await engine.SetVolumeAsync((float)(vol65535 / 65535.0)).ConfigureAwait(false);
                    }
                    break;
                case "play":
                    var playCmd = BuildPlayCommand(data);
                    await engine.PlayAsync(playCmd, ct).ConfigureAwait(false);
                    break;
                default:
                    _logger?.LogDebug("Unhandled local endpoint: {Endpoint}", endpoint);
                    break;
            }

            return PlaybackResult.Success();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Local engine failed for {Endpoint}", endpoint);
            return PlaybackResult.Failure(PlaybackErrorKind.Unknown, ex.Message);
        }
    }

    private static Wavee.Connect.Commands.PlayCommand BuildPlayCommand(Dictionary<string, object>? data)
    {
        string? contextUri = null;
        string? trackUri = null;
        int? skipToIndex = null;
        string? contextDescription = null;
        List<Wavee.Connect.Commands.PageTrack>? pageTracks = null;

        if (data?.TryGetValue("context", out var ctxObj) == true && ctxObj is Dictionary<string, object> ctx)
        {
            contextUri = ctx.GetValueOrDefault("uri") as string;

            // Optional display name for the context ("Huh Gak", "Discover Weekly", "OK Computer")
            // — plumbed through to PlayerState.context_metadata["context_description"] so remote
            // Now Playing cards can show it. Callers populate via context["description"] or
            // context["metadata"]["context_description"] when they know the name.
            contextDescription = ctx.GetValueOrDefault("description") as string;
            if (string.IsNullOrEmpty(contextDescription)
                && ctx.TryGetValue("metadata", out var metaObj)
                && metaObj is Dictionary<string, object> metaDict)
            {
                contextDescription = metaDict.GetValueOrDefault("context_description") as string;
            }

            // Extract inline tracks from context pages (e.g. PlayTracksAsync embeds them here)
            if (ctx.TryGetValue("pages", out var pagesObj) && pagesObj is System.Collections.IEnumerable pages)
            {
                foreach (var page in pages)
                {
                    if (page is Dictionary<string, object> pageDict
                        && pageDict.TryGetValue("tracks", out var tracksObj)
                        && tracksObj is System.Collections.IEnumerable tracks)
                    {
                        pageTracks ??= [];
                        foreach (var t in tracks)
                        {
                            if (t is Dictionary<string, object> trackDict)
                            {
                                pageTracks.Add(new Wavee.Connect.Commands.PageTrack(
                                    trackDict.GetValueOrDefault("uri") as string ?? "",
                                    trackDict.GetValueOrDefault("uid") as string ?? ""));
                            }
                        }
                    }
                }
            }
        }

        if (data?.TryGetValue("options", out var optObj) == true && optObj is Dictionary<string, object> opts)
        {
            if (opts.TryGetValue("skip_to", out var skipObj) && skipObj is Dictionary<string, object> skip)
            {
                trackUri = skip.GetValueOrDefault("track_uri") as string;
                if (skip.TryGetValue("track_index", out var idxVal))
                    skipToIndex = Convert.ToInt32(idxVal);
            }
        }

        return new Wavee.Connect.Commands.PlayCommand
        {
            Endpoint = "play",
            Key = "local/0",
            MessageId = 0,
            MessageIdent = "local",
            SenderDeviceId = "",
            ContextUri = contextUri,
            TrackUri = trackUri,
            SkipToIndex = skipToIndex,
            PageTracks = pageTracks,
            ContextDescription = contextDescription
        };
    }

    public async Task<PlaybackResult> TransferPlaybackAsync(string deviceId, bool startPlaying, CancellationToken ct)
    {
        var selfDeviceId = _session.Config.DeviceId;
        var isSelfTransfer = string.Equals(deviceId, selfDeviceId, StringComparison.Ordinal);

        // Self-transfer ("take over playback on this device") cannot use the Spotify
        // connect-state transfer endpoint — posting from/self/to/self is rejected with
        // HTTP 400 by the server when we're not already the active device. Instead,
        // route through PlaybackStateManager.ResumeAsync with userInitiated=true, which
        // runs the "ghost resume" path: it reads the current cluster track/context and
        // starts local playback, implicitly making us the active Connect device.
        if (isSelfTransfer)
        {
            var stateManager = _session.PlaybackState;
            if (stateManager == null)
                return PlaybackResult.Failure(PlaybackErrorKind.Unavailable, "Playback state manager not available");

            try
            {
                await stateManager.ResumeAsync(userInitiated: true).ConfigureAwait(false);
                return PlaybackResult.Success();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Self-transfer (ghost resume) failed");
                return PlaybackResult.Failure(PlaybackErrorKind.Unknown, ex.Message, ex);
            }
        }

        var result = await _client.SendCommandAsync(deviceId, "transfer", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["restore_paused"] = startPlaying ? "restore" : "pause"
            }
        }, waitForAck: ShouldWaitForAck("transfer"), ackTimeout: GetAckTimeout("transfer"), ct: ct).ConfigureAwait(false);

        // If we're transferring playback AWAY from this device to another Spotify device,
        // stop the local engine. In proxy-only mode the PlaybackStateManager's "another
        // device became active" handler never fires (cluster updates are suppressed), so
        // we'd otherwise keep playing alongside the target device.
        if (result.IsSuccess && _localEngine != null)
        {
            try
            {
                await _localEngine.StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to stop local engine after transferring away");
            }
        }

        return ToPlaybackResult(result);
    }

    public async Task<PlaybackResult> SwitchAudioOutputAsync(int deviceIndex, CancellationToken ct)
    {
        if (_localEngine == null)
        {
            _logger?.LogWarning("[SwitchAudioOutput] Audio engine not available");
            return PlaybackResult.Failure(PlaybackErrorKind.DeviceUnavailable, "Audio engine not available");
        }

        _logger?.LogInformation("[SwitchAudioOutput] Switching to device index {DeviceIndex}", deviceIndex);
        try
        {
            await _localEngine.SwitchAudioOutputAsync(deviceIndex, ct).ConfigureAwait(false);
            _logger?.LogInformation("[SwitchAudioOutput] Device switch IPC sent OK");
            return PlaybackResult.Success();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SwitchAudioOutput] IPC call failed for device index {DeviceIndex}", deviceIndex);
            return PlaybackResult.Failure(PlaybackErrorKind.Unknown, ex.Message, ex);
        }
    }

    public async Task<PlaybackResult> SwitchToVideoAsync(
        string? manifestIdOverride,
        string? videoTrackUriOverride,
        CancellationToken ct)
    {
        if (_localEngine == null)
        {
            _logger?.LogWarning("[SwitchToVideo] Local playback engine not available");
            return PlaybackResult.Failure(PlaybackErrorKind.DeviceUnavailable, "Playback engine not available");
        }

        _logger?.LogInformation("[SwitchToVideo] Switching current track to music-video engine (manifest={Manifest}, videoUri={VideoUri})",
            manifestIdOverride ?? "<none>",
            videoTrackUriOverride ?? "<none>");
        try
        {
            await _localEngine.SwitchToVideoAsync(manifestIdOverride, videoTrackUriOverride, ct).ConfigureAwait(false);
            return PlaybackResult.Success();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SwitchToVideo] Failed to switch to video");
            return PlaybackResult.Failure(PlaybackErrorKind.Unknown, ex.Message, ex);
        }
    }

    public async Task<PlaybackResult> SwitchToAudioAsync(CancellationToken ct)
    {
        if (_localEngine == null)
        {
            _logger?.LogWarning("[SwitchToAudio] Local playback engine not available");
            return PlaybackResult.Failure(PlaybackErrorKind.DeviceUnavailable, "Playback engine not available");
        }

        _logger?.LogInformation("[SwitchToAudio] Switching current music video back to audio");
        try
        {
            await _localEngine.SwitchToAudioAsync(ct).ConfigureAwait(false);
            return PlaybackResult.Success();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SwitchToAudio] Failed to switch to audio");
            return PlaybackResult.Failure(PlaybackErrorKind.Unknown, ex.Message, ex);
        }
    }
}
