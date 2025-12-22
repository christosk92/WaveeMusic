using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Connection;
using Wavee.Connect.Playback;
using Wavee.Connect.Protocol;
using Wavee.Core.DependencyInjection;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Core.Storage;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Console;

/// <summary>
/// Interactive console interface for Spotify Connect features using Spectre.Console UI.
/// </summary>
internal sealed class ConnectConsole : IDisposable, IAsyncDisposable
{
    private readonly Session _session;
    private readonly HttpClient _httpClient;
    private readonly SpectreUI _ui;
    private readonly ILogger? _logger;
    private readonly List<IDisposable> _subscriptions = new();
    private AudioPipeline? _audioPipeline;
    private ServiceProvider? _serviceProvider;
    private bool _disposed;

    public ConnectConsole(Session session, HttpClient httpClient, SpectreUI ui, ILogger? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _logger = logger;
    }

    /// <summary>
    /// Runs the interactive console.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_session.DeviceState == null)
        {
            _ui.AddLog("ERR", "Spotify Connect is not enabled. Set EnableConnect = true in SessionConfig.");
            return;
        }

        _ui.AddLog("INF", "Spotify Connect initialized successfully!");
        _ui.AddLog("INF", $"Device: {_session.Config.DeviceName} ({_session.Config.DeviceId[..8]}...)");

        // Initialize DI container with cache services
        try
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Wavee",
                "metadata.db");

            var services = new ServiceCollection();
            services.AddWaveeCache(options =>
            {
                options.DatabasePath = dbPath;
            });

            _serviceProvider = services.BuildServiceProvider();
            _ui.AddLog("INF", $"Cache services initialized");
        }
        catch (Exception ex)
        {
            _ui.AddLog("WRN", $"Cache services initialization failed: {ex.Message}");
        }

        // Initialize audio pipeline for local playback
        try
        {
            var metadataDatabase = _serviceProvider?.GetService<IMetadataDatabase>();
            var cacheService = _serviceProvider?.GetService<ICacheService>();

            _audioPipeline = AudioPipelineFactory.CreateSpotifyPipeline(
                _session,
                _session.SpClient,
                _httpClient,
                AudioPipelineOptions.Default,
                metadataDatabase,
                cacheService,
                _session.Config.DeviceId,
                _session.Events,
                _session.CommandHandler,
                _session.DeviceState,
                _logger);

            // Subscribe to local playback state changes
            _subscriptions.Add(_audioPipeline.StateChanges.Subscribe(OnLocalPlaybackStateChanged));

            // Enable bidirectional mode
            if (_session.PlaybackState != null)
            {
                _session.PlaybackState.EnableBidirectionalMode(
                    _audioPipeline,
                    _session.SpClient,
                    _session);
                _ui.AddLog("INF", "Audio pipeline initialized - bidirectional playback enabled!");
            }
            else
            {
                _ui.AddLog("INF", "Audio pipeline initialized - playback ready!");
            }
        }
        catch (Exception ex)
        {
            _ui.AddLog("WRN", $"Audio pipeline initialization failed: {ex.Message}");
        }

        _ui.AddLog("INF", "Type 'help' for available commands");

        // Subscribe to all Connect events
        SubscribeToCommandEvents();
        SubscribeToPlaybackStateEvents();
        SubscribeToConnectionEvents();
        SubscribeToVolumeChanges();

        // Update initial device state
        _ui.UpdateDevice(
            _session.Config.DeviceName,
            _session.Config.DeviceId,
            _session.DeviceState?.IsActive ?? false);

        // Update initial volume
        var initialVolume = _session.GetVolumePercentage() ?? 50;
        _ui.UpdateVolume(initialVolume);

        // Set up command handler
        _ui.SetCommandHandler(HandleCommandAsync);

        // Run the UI (this blocks until exit)
        await _ui.RunAsync(cancellationToken);
    }

    private async Task<bool> HandleCommandAsync(string input, CancellationToken cancellationToken)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var command = parts[0].ToLower();

        switch (command)
        {
            case "help":
                ShowHelp();
                return false;

            case "device":
                await HandleDeviceCommandAsync(parts, cancellationToken);
                return false;

            case "volume":
            case "vol":
                await HandleVolumeCommandAsync(parts, cancellationToken);
                return false;

            case "play":
                await HandlePlayCommandAsync(parts, cancellationToken);
                return false;

            case "pause":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.PauseAsync(cancellationToken);
                    _ui.AddLog("INF", "Paused");
                }
                else
                {
                    _ui.AddLog("ERR", "Audio pipeline not available");
                }
                return false;

            case "resume":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.ResumeAsync(cancellationToken);
                    _ui.AddLog("INF", "Resumed");
                }
                else
                {
                    _ui.AddLog("ERR", "Audio pipeline not available");
                }
                return false;

            case "seek":
                await HandleSeekCommandAsync(parts, cancellationToken);
                return false;

            case "next":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.SkipNextAsync(cancellationToken);
                    _ui.AddLog("INF", "Skipped to next track");
                }
                return false;

            case "prev":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.SkipPreviousAsync(cancellationToken);
                    _ui.AddLog("INF", "Skipped to previous track");
                }
                return false;

            case "quit":
            case "exit":
            case "q":
                _ui.AddLog("INF", "Shutting down...");
                return true;

            default:
                _ui.AddLog("WRN", $"Unknown command: {command}. Type 'help' for available commands.");
                return false;
        }
    }

    private async Task HandlePlayCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (_audioPipeline == null)
        {
            _ui.AddLog("ERR", "Audio pipeline not available");
            return;
        }

        if (parts.Length < 2)
        {
            _ui.AddLog("WRN", "Usage: play <spotify:track:xxx> or <file_path.mp3>");
            return;
        }

        // Join remaining parts in case file path has spaces, and trim any quotes
        var uri = string.Join(" ", parts.Skip(1)).Trim('"');
        _ui.AddLog("INF", $"Loading {uri}...");

        try
        {
            // Check if it's a local file (file:// URI or file path)
            var isLocalFile = uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                              Path.IsPathRooted(uri) ||
                              File.Exists(uri);

            // Check if it's a Spotify context (playlist, album, artist, show)
            var isContext = !isLocalFile && (
                            uri.Contains(":playlist:") || uri.Contains("/playlist/") ||
                            uri.Contains(":album:") || uri.Contains("/album/") ||
                            uri.Contains(":artist:") || uri.Contains("/artist/") ||
                            uri.Contains(":show:") || uri.Contains("/show/"));

            var command = new PlayCommand
            {
                Endpoint = "play",
                MessageIdent = "local",
                MessageId = 0,
                SenderDeviceId = _session.Config.DeviceId,
                Key = "local/0",
                TrackUri = isContext ? null : uri,
                ContextUri = isContext ? uri : null
            };

            await _audioPipeline.PlayAsync(command, cancellationToken);
        }
        catch (Exception ex)
        {
            _ui.AddLog("ERR", $"Playback failed: {ex.Message}");
        }
    }

    private async Task HandleSeekCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (_audioPipeline == null)
        {
            _ui.AddLog("ERR", "Audio pipeline not available");
            return;
        }

        if (parts.Length < 2 || !int.TryParse(parts[1], out var seconds))
        {
            _ui.AddLog("WRN", "Usage: seek <seconds>");
            return;
        }

        var positionMs = seconds * 1000L;
        await _audioPipeline.SeekAsync(positionMs, cancellationToken);
        _ui.AddLog("INF", $"Seeked to {FormatTimeSpan(positionMs)}");
    }

    private void OnLocalPlaybackStateChanged(LocalPlaybackState state)
    {
        _ui.UpdateNowPlaying(
            state.TrackTitle,
            state.TrackArtist,
            state.TrackAlbum,
            state.PositionMs,
            state.DurationMs,
            state.IsPlaying,
            state.IsPaused);

        // Update device active status based on playback state
        // Device is active when playing or paused (track loaded)
        var deviceActive = state.IsPlaying || state.IsPaused;
        _ui.UpdateDevice(_session.Config.DeviceName, _session.Config.DeviceId, deviceActive);
    }

    private void ShowHelp()
    {
        _ui.AddLog("INF", "--- Commands ---");
        _ui.AddLog("INF", "play <uri>  - Play track, context, or local file");
        _ui.AddLog("INF", "pause       - Pause playback");
        _ui.AddLog("INF", "resume      - Resume playback");
        _ui.AddLog("INF", "next        - Skip to next track");
        _ui.AddLog("INF", "prev        - Skip to previous track");
        _ui.AddLog("INF", "seek <sec>  - Seek to position");
        _ui.AddLog("INF", "vol [0-100] - Set or show volume");
        _ui.AddLog("INF", "device on|off - Toggle device active");
        _ui.AddLog("INF", "quit        - Exit application");
    }

    private async Task HandleDeviceCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 2)
        {
            _ui.AddLog("WRN", "Usage: device on|off");
            return;
        }

        var action = parts[1].ToLower();
        switch (action)
        {
            case "on":
            case "active":
                var activated = await _session.SetDeviceActiveAsync(true, cancellationToken);
                if (activated)
                {
                    _ui.AddLog("INF", "Device is now active");
                    _ui.UpdateDevice(_session.Config.DeviceName, _session.Config.DeviceId, true);
                }
                else
                    _ui.AddLog("ERR", "Failed to activate device");
                break;

            case "off":
            case "inactive":
                var deactivated = await _session.SetDeviceActiveAsync(false, cancellationToken);
                if (deactivated)
                {
                    _ui.AddLog("INF", "Device is now inactive");
                    _ui.UpdateDevice(_session.Config.DeviceName, _session.Config.DeviceId, false);
                }
                else
                    _ui.AddLog("ERR", "Failed to deactivate device");
                break;

            default:
                _ui.AddLog("WRN", $"Unknown device action: {action}. Use 'on' or 'off'.");
                break;
        }
    }

    private async Task HandleVolumeCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length == 1)
        {
            var volumePct = _session.GetVolumePercentage() ?? 0;
            _ui.AddLog("INF", $"Current volume: {volumePct}%");
            return;
        }

        var arg = parts[1].ToLower();

        if (arg == "+" || arg == "up")
        {
            await AdjustVolumeAsync(+5, cancellationToken);
        }
        else if (arg == "-" || arg == "down")
        {
            await AdjustVolumeAsync(-5, cancellationToken);
        }
        else if (int.TryParse(arg, out var pct))
        {
            var result = await _session.SetVolumePercentageAsync(pct, cancellationToken);
            if (result)
            {
                _ui.AddLog("INF", $"Volume set to {pct}%");
                _ui.UpdateVolume(pct);
            }
            else
                _ui.AddLog("ERR", "Failed to set volume");
        }
        else
        {
            _ui.AddLog("WRN", "Usage: vol [0-100] or vol +/-");
        }
    }

    private async Task AdjustVolumeAsync(int delta, CancellationToken cancellationToken)
    {
        var currentPct = _session.GetVolumePercentage() ?? 50;
        var newPct = Math.Clamp(currentPct + delta, 0, 100);

        var result = await _session.SetVolumePercentageAsync(newPct, cancellationToken);
        if (result)
        {
            var direction = delta > 0 ? "Increased" : "Decreased";
            _ui.AddLog("INF", $"{direction} volume to {newPct}%");
            _ui.UpdateVolume(newPct);
        }
        else
        {
            _ui.AddLog("ERR", "Failed to adjust volume");
        }
    }

    private void SubscribeToCommandEvents()
    {
        if (_session.CommandHandler == null)
            return;

        _subscriptions.Add(_session.CommandHandler.PlayCommands.Subscribe(cmd =>
        {
            var details = $"Track: {cmd.TrackUri ?? "N/A"}, Context: {cmd.ContextUri ?? "N/A"}";
            _ui.AddLog("CMD", $"Play - {details}");
        }));

        _subscriptions.Add(_session.CommandHandler.PauseCommands.Subscribe(_ =>
            _ui.AddLog("CMD", "Pause")));

        _subscriptions.Add(_session.CommandHandler.ResumeCommands.Subscribe(_ =>
            _ui.AddLog("CMD", "Resume")));

        _subscriptions.Add(_session.CommandHandler.SeekCommands.Subscribe(cmd =>
            _ui.AddLog("CMD", $"Seek to {FormatTimeSpan(cmd.PositionMs)}")));

        _subscriptions.Add(_session.CommandHandler.ShuffleCommands.Subscribe(cmd =>
        {
            _ui.AddLog("CMD", $"Shuffle {(cmd.Enabled ? "ON" : "OFF")}");
            _ui.UpdateOptions(cmd.Enabled, false, false); // TODO: track all options
        }));

        _subscriptions.Add(_session.CommandHandler.SkipNextCommands.Subscribe(_ =>
            _ui.AddLog("CMD", "Skip Next")));

        _subscriptions.Add(_session.CommandHandler.SkipPrevCommands.Subscribe(_ =>
            _ui.AddLog("CMD", "Skip Prev")));

        _subscriptions.Add(_session.CommandHandler.TransferCommands.Subscribe(_ =>
        {
            _ui.AddLog("CMD", "Transfer - Playback transferred to this device");
            _ui.UpdateDevice(_session.Config.DeviceName, _session.Config.DeviceId, true);
        }));
    }

    private void SubscribeToPlaybackStateEvents()
    {
        if (_session.PlaybackState == null)
            return;

        _subscriptions.Add(_session.PlaybackState.TrackChanged.Subscribe(state =>
        {
            if (state.Track != null)
            {
                _ui.UpdateNowPlaying(
                    state.Track.Title,
                    state.Track.Artist,
                    state.Track.Album,
                    state.PositionMs,
                    state.DurationMs,
                    state.Status == PlaybackStatus.Playing,
                    state.Status == PlaybackStatus.Paused);
            }
        }));

        _subscriptions.Add(_session.PlaybackState.PlaybackStatusChanged.Subscribe(state =>
        {
            var isPlaying = state.Status == PlaybackStatus.Playing;
            var isPaused = state.Status == PlaybackStatus.Paused;
            _ui.UpdateNowPlaying(null, null, null, state.PositionMs, state.DurationMs, isPlaying, isPaused);
        }));

        _subscriptions.Add(_session.PlaybackState.PositionChanged
            .Throttle(TimeSpan.FromMilliseconds(500))
            .Subscribe(state => _ui.UpdatePosition(state.PositionMs)));

        _subscriptions.Add(_session.PlaybackState.OptionsChanged.Subscribe(state =>
        {
            _ui.UpdateOptions(
                state.Options.Shuffling,
                state.Options.RepeatingContext,
                state.Options.RepeatingTrack);
        }));
    }

    private void SubscribeToConnectionEvents()
    {
        if (_session.Dealer == null)
            return;

        _subscriptions.Add(_session.Dealer.ConnectionState.Subscribe(state =>
        {
            var status = state switch
            {
                ConnectionState.Connected => "Connected",
                ConnectionState.Connecting => "Connecting...",
                ConnectionState.Disconnected => "Disconnected",
                _ => state.ToString()
            };

            _ui.UpdateDealerStatus(status);
        }));
    }

    private void SubscribeToVolumeChanges()
    {
        if (_session.DeviceState?.Volume == null)
            return;

        var subscription = _session.DeviceState.Volume
            .DistinctUntilChanged()
            .Subscribe(volume =>
            {
                var pct = ConnectStateHelpers.VolumeToPercentage(volume);
                _ui.UpdateVolume(pct);
            });

        _subscriptions.Add(subscription);
    }

    private static string FormatTimeSpan(long milliseconds)
    {
        var ts = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var subscription in _subscriptions)
            subscription?.Dispose();
        _subscriptions.Clear();

        if (_audioPipeline != null)
        {
            _audioPipeline.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _audioPipeline = null;
        }

        _serviceProvider?.Dispose();
        _serviceProvider = null;

        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        foreach (var subscription in _subscriptions)
            subscription?.Dispose();
        _subscriptions.Clear();

        if (_audioPipeline != null)
        {
            await _audioPipeline.DisposeAsync();
            _audioPipeline = null;
        }

        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null;
        }

        _disposed = true;
    }
}
