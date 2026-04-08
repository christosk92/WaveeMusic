using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Connect.Playback;
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
        if (_localEngine is AudioPipeline pipeline)
            await pipeline.SwitchQualityAsync(quality, ct).ConfigureAwait(false);
    }

    public void SetNormalizationEnabled(bool enabled)
    {
        if (_localEngine is AudioPipeline pipeline)
            pipeline.SetNormalizationEnabled(enabled);
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
                _logger?.LogInformation("Routing {Endpoint} to local engine (target={Target})", endpoint, target ?? "none");

                // For play commands: prefer the typed PlayCommand over lossy dict deserialization
                if (endpoint == "play" && typedPlayCommand != null)
                {
                    try
                    {
                        _logger?.LogInformation("Routing play to local AudioPipeline");
                        await Task.Run(async () => await _localEngine.PlayAsync(typedPlayCommand, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                        return PlaybackResult.Success();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Local engine failed for play");
                        return PlaybackResult.Failure(PlaybackErrorKind.Unknown, ex.Message, ex);
                    }
                }

                return await RouteToLocalEngineAsync(_localEngine, endpoint, data, ct).ConfigureAwait(false);
            }

            return PlaybackResult.Failure(PlaybackErrorKind.DeviceUnavailable,
                "No active device and no local playback engine available.");
        }

        // Remote: always use dict for JSON serialization
        var ackTimeout = GetAckTimeout(endpoint);
        var result = await _client.SendCommandAsync(target, endpoint, data, ackTimeout: ackTimeout, ct: ct).ConfigureAwait(false);

        // The server already accepted the command (HTTP 2xx). Missing dealer ack is often
        // transient and should not stall interactive playback with retries.
        if (result.IsTimeout && AcceptTimeoutAsSuccess(endpoint))
        {
            _logger?.LogWarning("Treating ack timeout as success for endpoint {Endpoint} (timeout={TimeoutMs}ms)", endpoint, ackTimeout.TotalMilliseconds);
            return PlaybackResult.Success();
        }

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

    private static bool AcceptTimeoutAsSuccess(string endpoint) => endpoint switch
    {
        "play" or "add_to_queue" or "pause" or "resume" or "skip_next" or "skip_prev" or "seek_to"
            or "set_shuffling_context" or "set_repeating_context" or "set_repeating_track" or "set_volume" => true,
        _ => false
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
        var result = await _client.SendCommandAsync(deviceId, "transfer", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["restore_paused"] = startPlaying ? "restore" : "pause"
            }
        }, ackTimeout: GetAckTimeout("transfer"), ct: ct).ConfigureAwait(false);

        return ToPlaybackResult(result);
    }
}
