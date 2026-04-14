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

    /// <summary>
    /// Enables local playback routing through the given engine.
    /// Called once after AudioPipeline is created in InitializePlaybackEngine.
    /// </summary>
    internal void EnableLocalPlayback(IPlaybackEngine engine) => _localEngine = engine;

    /// <summary>
    /// Disables local playback routing (e.g. on logout).
    /// </summary>
    internal void DisableLocalPlayback() => _localEngine = null;

    public ConnectCommandExecutor(ConnectCommandClient client, Session session, ILogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger;
    }

    // ── IAudioPipelineControl ──

    public async Task SwitchQualityAsync(AudioQuality quality, CancellationToken ct = default)
    {
        if (_localEngine is Wavee.AudioIpc.AudioPipelineProxy proxy)
            await proxy.SwitchQualityAsync(quality.ToString(), ct).ConfigureAwait(false);
    }

    public void SetNormalizationEnabled(bool enabled)
    {
        if (_localEngine is Wavee.AudioIpc.AudioPipelineProxy proxy)
            _ = proxy.SetNormalizationEnabledAsync(enabled);
    }

    public async Task SetEqualizerAsync(bool enabled, double[]? bandGains, CancellationToken ct = default)
    {
        if (_localEngine is Wavee.AudioIpc.AudioPipelineProxy proxy)
            await proxy.SetEqualizerAsync(enabled, bandGains, ct).ConfigureAwait(false);
    }

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

        // Remote: always use dict for JSON serialization
        _logger?.LogInformation("[Executor] {Endpoint}: routing REMOTE → device={Target}", endpoint, target);
        var waitForAck = ShouldWaitForAck(endpoint);
        var ackTimeout = GetAckTimeout(endpoint);
        var result = await _client.SendCommandAsync(
            target,
            endpoint,
            data,
            waitForAck: waitForAck,
            ackTimeout: ackTimeout,
            ct: ct).ConfigureAwait(false);

        if (result.IsSuccess)
            _logger?.LogInformation("[Executor] {Endpoint} OK (remote, waitAck={WaitAck})", endpoint, waitForAck);
        else
            _logger?.LogWarning("[Executor] {Endpoint} FAILED (remote, waitAck={WaitAck}): {Error}", endpoint, waitForAck, result.ErrorMessage);

        return ToPlaybackResult(result);
    }

    private static TimeSpan GetAckTimeout(string endpoint) => endpoint switch
    {
        // Keep play/start slightly higher than transport toggles.
        "play" or "add_to_queue" or "transfer" => TimeSpan.FromMilliseconds(2500),

        // Interactive transport commands should fail fast if no confirmation arrives.
        "pause" or "resume" or "skip_next" or "skip_prev" or "seek_to" => TimeSpan.FromMilliseconds(1500),

        // Option toggles are non-critical; short timeout keeps UI snappy.
        "set_shuffling_context" or "set_repeating_context" or "set_repeating_track" or "set_volume" => TimeSpan.FromMilliseconds(1200),

        _ => TimeSpan.FromMilliseconds(2000)
    };

    private static bool ShouldWaitForAck(string endpoint) => endpoint switch
    {
        // In out-of-process mode the UI dealer stream may not emit timely ack signals,
        // so waiting here makes controls feel unresponsive. Transfer is included because
        // the HTTP 200 from /connect-state/v1/connect/transfer is already a server-side
        // confirmation — the dealer ack is a secondary signal that often doesn't arrive
        // within the 2.5s window when the target device is slow to pick up the command.
        "play" or "add_to_queue" or "pause" or "resume" or "skip_next" or "skip_prev" or "seek_to"
            or "set_shuffling_context" or "set_repeating_context" or "set_repeating_track" or "set_volume"
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

        if (options?.PositionMs is > 0)
            commandOptions["seek_to"] = options.PositionMs.Value;

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
            TrackUri = options?.StartTrackUri,
            SkipToIndex = options?.StartIndex,
            PositionMs = options?.PositionMs,
        };

        return SendAsync("play", data, typedCmd, ct);
    }

    public Task<PlaybackResult> PlayTracksAsync(IReadOnlyList<string> trackUris, int startIndex, CancellationToken ct)
    {
        // Normalize bare IDs to full spotify:track: URIs
        var normalizedUris = trackUris.Select(uri =>
            uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri : $"spotify:track:{uri}").ToList();

        var tracks = normalizedUris.Select(uri => new Dictionary<string, object>
        {
            ["uri"] = uri,
            ["uid"] = ""
        }).ToList();

        var data = new Dictionary<string, object>
        {
            ["context"] = new Dictionary<string, object>
            {
                ["uri"] = "spotify:internal:queue",
                ["pages"] = new[] { new Dictionary<string, object> { ["tracks"] = tracks } }
            }
        };

        if (startIndex > 0)
            data["options"] = new Dictionary<string, object>
            {
                ["skip_to"] = new Dictionary<string, object> { ["track_index"] = startIndex }
            };

        var typedCmd = new Wavee.Connect.Commands.PlayCommand
        {
            Endpoint = "play",
            Key = "local/0",
            MessageId = 0,
            MessageIdent = "local",
            SenderDeviceId = "",
            ContextUri = "spotify:internal:queue",
            SkipToIndex = startIndex > 0 ? startIndex : null,
            PageTracks = normalizedUris.Select(uri => new Wavee.Connect.Commands.PageTrack(uri, "")).ToList(),
        };

        return SendAsync("play", data, typedCmd, ct);
    }

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

    public Task<PlaybackResult> SetVolumeAsync(int volumePercent, CancellationToken ct)
        => SendAsync("set_volume", new Dictionary<string, object>
        {
            ["value"] = (int)(Math.Clamp(volumePercent, 0, 100) / 100.0 * 65535)
        }, ct);

    public Task<PlaybackResult> AddToQueueAsync(string trackUri, CancellationToken ct)
        => SendAsync("add_to_queue", new Dictionary<string, object>
        {
            ["track"] = new Dictionary<string, object>
            {
                ["uri"] = trackUri,
                ["provider"] = "queue",
                ["metadata"] = new Dictionary<string, string>()
            }
        }, ct);

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
        List<Wavee.Connect.Commands.PageTrack>? pageTracks = null;

        if (data?.TryGetValue("context", out var ctxObj) == true && ctxObj is Dictionary<string, object> ctx)
        {
            contextUri = ctx.GetValueOrDefault("uri") as string;

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
            PageTracks = pageTracks
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
}
