using System.IO.Pipes;
using System.Reactive.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wavee.AudioIpc;
using Wavee.Connect;
using Wavee.Connect.Playback;
using Wavee.Connect.Playback.Sinks;
using Wavee.Core.Session;

namespace Wavee.AudioHost;

/// <summary>
/// Hosts the AudioPipeline in a separate process and exposes it via Named Pipes IPC.
/// The audio process owns the Session, AudioPipeline, and PortAudioSink — completely
/// isolated from the UI process GC.
/// </summary>
internal sealed class AudioHostService : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly string _credentialsPath;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private Session? _session;
    private AudioPipeline? _pipeline;
    private IpcPipeTransport? _transport;
    private IDisposable? _stateSubscription;
    private IDisposable? _errorSubscription;

    public AudioHostService(string pipeName, string credentialsPath, ILogger logger)
    {
        _pipeName = pipeName;
        _credentialsPath = credentialsPath;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;

        _logger.LogInformation("AudioHost starting — pipe={PipeName}", _pipeName);

        // Create named pipe server and wait for UI process to connect
        var pipeServer = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        _logger.LogInformation("Waiting for UI process to connect...");
        await pipeServer.WaitForConnectionAsync(token);
        _logger.LogInformation("UI process connected");

        _transport = new IpcPipeTransport(pipeServer, _logger);

        // Wait for credentials message from UI process
        var credMsg = await _transport.ReceiveAsync(token);
        if (credMsg?.Type != "credentials")
        {
            _logger.LogError("Expected credentials message, got: {Type}", credMsg?.Type);
            return;
        }

        var creds = IpcPayloadHelper.Deserialize<CredentialsHandshake>(credMsg);
        if (creds == null)
        {
            _logger.LogError("Failed to deserialize credentials");
            return;
        }

        // Create session and connect
        _logger.LogInformation("Connecting to Spotify as device {DeviceId}...", creds.DeviceId);
        await InitializeSessionAsync(creds, token);

        // Create audio pipeline
        InitializePipeline(creds);

        // Send ready message
        await _transport.SendAsync(IpcMessageTypes.Ready,
            IpcPayloadHelper.SerializeToUtf8(new AudioHostReady { DeviceId = creds.DeviceId, PipeName = _pipeName }), ct: token);

        // Subscribe to pipeline state and errors
        SubscribeToPipelineEvents(token);

        // Process commands
        _logger.LogInformation("AudioHost ready — processing commands");
        await ProcessCommandsAsync(token);
    }

    private async Task InitializeSessionAsync(CredentialsHandshake creds, CancellationToken ct)
    {
        var httpFactory = new SimpleHttpClientFactory();
        var config = new SessionConfig { DeviceId = creds.DeviceId };
        _session = Session.Create(config, httpFactory, _logger);

        // Reconstruct credentials from the stored auth data passed by the UI process
        var credentials = new Wavee.Core.Authentication.Credentials
        {
            Username = creds.Username,
            AuthType = Wavee.Protocol.AuthenticationType.AuthenticationStoredSpotifyCredentials,
            AuthData = Convert.FromBase64String(creds.StoredCredential)
        };

        await _session.ConnectAsync(credentials, null, ct);
        _logger.LogInformation("Session connected");
    }

    private void InitializePipeline(CredentialsHandshake creds)
    {
        if (_session == null) throw new InvalidOperationException("Session not initialized");

        var sink = new PortAudioSink(_logger);
        var httpClient = new HttpClient();

        // Create metadata database — shares the same DB file as the UI process (read-only safe via SQLite WAL)
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Wavee", "metadata.db");
        var metadataDb = new Wavee.Core.Storage.MetadataDatabase(dbPath, maxHotCacheSize: 256, _logger);
        var cacheService = new Wavee.Core.Storage.CacheService(metadataDb, logger: _logger);
        var extendedMetadataClient = new Wavee.Core.Http.ExtendedMetadataClient(
            _session,
            httpClient,
            metadataDb,
            _logger);

        _pipeline = AudioPipelineFactory.CreateSpotifyPipeline(
            _session,
            (Wavee.Core.Http.SpClient)_session.SpClient,
            httpClient,
            options: new AudioPipelineOptions(),
            metadataDatabase: metadataDb,
            cacheService: cacheService,
            extendedMetadataClient: extendedMetadataClient,
            deviceId: creds.DeviceId,
            eventService: _session.Events,
            commandHandler: _session.CommandHandler,
            deviceStateManager: _session.DeviceState,
            logger: _logger);

        // Enable PlaybackStateManager's on-demand enrichment path for incomplete
        // cluster track payloads (title/artist/artwork gaps).
        _session.PlaybackState?.SetMetadataClient(extendedMetadataClient);

        // Enable bidirectional mode for Spotify Connect state publishing
        _session.PlaybackState?.EnableBidirectionalMode(
            _pipeline,
            (Wavee.Core.Http.SpClient)_session.SpClient,
            _session);

        _pipeline.SubscribeToConnectionState(_session.ConnectionState);

        _logger.LogInformation("AudioPipeline created");
    }

    private void SubscribeToPipelineEvents(CancellationToken ct)
    {
        if (_pipeline == null || _transport == null) return;

        // Stream state updates to UI
        // Stream unified playback state (cluster + local) to UI.
        // This keeps UI reactive even if the UI process dealer stream is transiently stale.
        var playbackStateManager = _session?.PlaybackState;
        if (playbackStateManager != null)
        {
            _stateSubscription = playbackStateManager.StateChanges
                .Publish(shared =>
                {
                    // Critical changes (track, status, device, etc.) — send immediately
                    var critical = shared.Where(s =>
                        s.Changes != StateChanges.None &&
                        s.Changes != StateChanges.Position);

                    // Position-only updates — throttle to reduce IPC chatter
                    var positionOnly = shared
                        .Where(s => s.Changes == StateChanges.Position)
                        .Sample(TimeSpan.FromMilliseconds(250));

                    return critical.Merge(positionOnly);
                })
                .Subscribe(state =>
                {
                    if (ct.IsCancellationRequested) return;
                    var snapshot = MapToSnapshot(state, 0);
                    _ = _transport.SendAsync(IpcMessageTypes.StateUpdate, IpcPayloadHelper.SerializeToUtf8(snapshot), ct: CancellationToken.None);
                });
        }
        else
        {
            // Fallback: local engine state only (should not normally happen)
            _stateSubscription = _pipeline.StateChanges
                .Sample(TimeSpan.FromMilliseconds(100))
                .Subscribe(state =>
                {
                    if (ct.IsCancellationRequested) return;
                    var snapshot = MapToSnapshot(state);
                    _ = _transport.SendAsync(IpcMessageTypes.StateUpdate, IpcPayloadHelper.SerializeToUtf8(snapshot), ct: CancellationToken.None);
                });
        }

        // Stream errors to UI
        _errorSubscription = _pipeline.Errors.Subscribe(error =>
        {
            if (ct.IsCancellationRequested) return;
            var msg = new PlaybackErrorMessage
            {
                ErrorType = error.ErrorType.ToString(),
                Message = error.Message
            };
            _ = _transport.SendAsync(IpcMessageTypes.Error, IpcPayloadHelper.SerializeToUtf8(msg), ct: CancellationToken.None);
        });
    }

    private async Task ProcessCommandsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _transport != null)
        {
            IpcMessage? msg;
            try
            {
                msg = await _transport.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Pipe disconnected");
                break;
            }

            if (msg == null)
            {
                _logger.LogInformation("Pipe closed by UI process");
                break;
            }

            try
            {
                await HandleCommandAsync(msg, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling command {Type}", msg.Type);
                await _transport.SendAsync(IpcMessageTypes.CommandResult,
                    IpcPayloadHelper.SerializeToUtf8(new CommandResultMessage
                    {
                        RequestId = msg.Id,
                        Success = false,
                        ErrorMessage = ex.Message
                    }), ct: ct);
            }
        }
    }

    private async Task HandleCommandAsync(IpcMessage msg, CancellationToken ct)
    {
        if (_pipeline == null) return;

        switch (msg.Type)
        {
            case IpcMessageTypes.PlayContext:
            {
                var cmd = IpcPayloadHelper.Deserialize<PlayContextCommand>(msg);
                if (cmd == null) break;
                var playCmd = new Wavee.Connect.Commands.PlayCommand
                {
                    Endpoint = "play",
                    Key = "",
                    MessageId = 0,
                    MessageIdent = "",
                    SenderDeviceId = _session?.Config.DeviceId ?? "",
                    ContextUri = cmd.ContextUri,
                    TrackUri = cmd.TrackUri,
                    SkipToIndex = cmd.TrackIndex,
                    PositionMs = cmd.PositionMs,
                    PageTracks = cmd.PageTracks?.Select(t =>
                        new Wavee.Connect.Commands.PageTrack(t.Uri, t.Uid ?? "")).ToList(),
                };
                await _pipeline.PlayAsync(playCmd, ct);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.Resume:
                await _pipeline.ResumeAsync(ct);
                await SendOk(msg.Id, ct);
                break;
            case IpcMessageTypes.Pause:
                await _pipeline.PauseAsync(ct);
                await SendOk(msg.Id, ct);
                break;
            case IpcMessageTypes.Stop:
                await _pipeline.StopAsync(ct);
                await SendOk(msg.Id, ct);
                break;
            case IpcMessageTypes.SkipNext:
                await _pipeline.SkipNextAsync(ct);
                await SendOk(msg.Id, ct);
                break;
            case IpcMessageTypes.SkipPrevious:
                await _pipeline.SkipPreviousAsync(ct);
                await SendOk(msg.Id, ct);
                break;
            case IpcMessageTypes.Seek:
            {
                var cmd = IpcPayloadHelper.Deserialize<SeekCommand>(msg);
                if (cmd != null) await _pipeline.SeekAsync(cmd.PositionMs, ct);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.SetVolume:
            {
                var cmd = IpcPayloadHelper.Deserialize<SetVolumeCommand>(msg);
                if (cmd != null) await _pipeline.SetVolumeAsync(cmd.VolumePercent / 100f, ct);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.SetShuffle:
            {
                var cmd = IpcPayloadHelper.Deserialize<SetShuffleCommand>(msg);
                if (cmd != null) await _pipeline.SetShuffleAsync(cmd.Enabled, ct);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.SetRepeat:
            {
                var cmd = IpcPayloadHelper.Deserialize<SetRepeatCommand>(msg);
                if (cmd != null)
                {
                    switch (cmd.State)
                    {
                        case "context":
                            await _pipeline.SetRepeatContextAsync(true, ct);
                            await _pipeline.SetRepeatTrackAsync(false, ct);
                            break;
                        case "track":
                            await _pipeline.SetRepeatContextAsync(false, ct);
                            await _pipeline.SetRepeatTrackAsync(true, ct);
                            break;
                        default: // "off"
                            await _pipeline.SetRepeatContextAsync(false, ct);
                            await _pipeline.SetRepeatTrackAsync(false, ct);
                            break;
                    }
                }
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.AddToQueue:
            {
                // AddToQueue is not on IPlaybackEngine directly — would need a command
                _logger.LogWarning("AddToQueue via IPC not yet implemented");
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.SetNormalization:
            {
                var cmd = IpcPayloadHelper.Deserialize<SetNormalizationCommand>(msg);
                if (cmd != null) _pipeline.SetNormalizationEnabled(cmd.Enabled);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.SwitchQuality:
            {
                var cmd = IpcPayloadHelper.Deserialize<SwitchQualityCommand>(msg);
                if (cmd != null && Enum.TryParse<Wavee.Core.Audio.AudioQuality>(cmd.Quality, true, out var q))
                    await _pipeline.SwitchQualityAsync(q, ct);
                await SendOk(msg.Id, ct);
                break;
            }
            case IpcMessageTypes.Ping:
                _logger.LogDebug("Ping received, sending Pong");
                await _transport!.SendAsync(IpcMessageTypes.Pong, msg.Id, ct);
                break;
            case IpcMessageTypes.Shutdown:
                _logger.LogInformation("Shutdown requested by UI process");
                _cts.Cancel();
                break;
            default:
                _logger.LogWarning("Unknown command type: {Type}", msg.Type);
                break;
        }
    }

    private async Task SendOk(long requestId, CancellationToken ct)
    {
        if (_transport == null) return;
        await _transport.SendAsync(IpcMessageTypes.CommandResult,
            IpcPayloadHelper.SerializeToUtf8(new CommandResultMessage { RequestId = requestId, Success = true }), ct: ct);
    }

    private static PlaybackStateSnapshot MapToSnapshot(LocalPlaybackState state)
    {
        return new PlaybackStateSnapshot
        {
            Source = "local",
            TrackUri = state.TrackUri,
            TrackUid = state.TrackUid,
            TrackTitle = state.TrackTitle,
            TrackArtist = state.TrackArtist,
            TrackAlbum = state.TrackAlbum,
            AlbumUri = state.AlbumUri,
            ArtistUri = state.ArtistUri,
            ImageUrl = state.ImageUrl,
            ImageLargeUrl = state.ImageLargeUrl,
            ContextUri = state.ContextUri,
            PositionMs = state.PositionMs,
            DurationMs = state.DurationMs,
            IsPlaying = state.IsPlaying,
            IsPaused = state.IsPaused,
            IsBuffering = state.IsBuffering,
            Shuffling = state.Shuffling,
            RepeatingContext = state.RepeatingContext,
            RepeatingTrack = state.RepeatingTrack,
            CanSeek = state.CanSeek,
            Timestamp = state.Timestamp,
        };
    }

    private static PlaybackStateSnapshot MapToSnapshot(PlaybackState state, long underrunCount)
    {
        return new PlaybackStateSnapshot
        {
            Source = state.Source == StateSource.Cluster ? "cluster" : "local",
            TrackUri = state.Track?.Uri,
            TrackUid = state.Track?.Uid,
            TrackTitle = state.Track?.Title,
            TrackArtist = state.Track?.Artist,
            TrackAlbum = state.Track?.Album,
            AlbumUri = state.Track?.AlbumUri,
            ArtistUri = state.Track?.ArtistUri,
            ImageUrl = state.Track?.ImageUrl,
            ImageLargeUrl = state.Track?.ImageLargeUrl,
            ContextUri = state.ContextUri,
            PositionMs = PlaybackStateHelpers.CalculateCurrentPosition(state),
            DurationMs = state.DurationMs,
            IsPlaying = state.Status == PlaybackStatus.Playing,
            IsPaused = state.Status == PlaybackStatus.Paused,
            IsBuffering = state.Status == PlaybackStatus.Buffering,
            Shuffling = state.Options.Shuffling,
            RepeatingContext = state.Options.RepeatingContext,
            RepeatingTrack = state.Options.RepeatingTrack,
            Volume = state.Volume,
            IsVolumeRestricted = state.IsVolumeRestricted,
            CanSeek = state.CanSeek,
            ActiveDeviceId = state.ActiveDeviceId,
            ActiveDeviceName = state.ActiveDeviceName,
            Changes = (int)state.Changes,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UnderrunCount = underrunCount,
        };
    }
    public async ValueTask DisposeAsync()
    {
        _stateSubscription?.Dispose();
        _errorSubscription?.Dispose();

        if (_transport != null)
            await _transport.DisposeAsync();

        _cts.Dispose();
    }
}

/// <summary>
/// Minimal IHttpClientFactory for the audio process (no DI needed).
/// </summary>
internal sealed class SimpleHttpClientFactory : System.Net.Http.IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
