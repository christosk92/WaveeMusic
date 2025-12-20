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
/// Rich interactive console interface for Spotify Connect features.
/// </summary>
internal sealed class ConnectConsole : IDisposable, IAsyncDisposable
{
    private readonly Session _session;
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly List<IDisposable> _subscriptions = new();
    private AudioPipeline? _audioPipeline;
    private ServiceProvider? _serviceProvider;
    private bool _watchEnabled;
    private bool _disposed;

    public ConnectConsole(Session session, HttpClient httpClient, ILogger? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
    }

    /// <summary>
    /// Runs the interactive command loop.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_session.DeviceState == null)
        {
            WriteError("Spotify Connect is not enabled. Set EnableConnect = true in SessionConfig.");
            return;
        }

        WriteSuccess("Spotify Connect initialized successfully!");
        WriteInfo($"Device: {_session.Config.DeviceName} ({_session.Config.DeviceId})");

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
            WriteSuccess($"Cache services initialized (database: {dbPath})");
        }
        catch (Exception ex)
        {
            WriteWarning($"Cache services initialization failed: {ex.Message}");
            WriteInfo("Extended metadata caching will not be available.");
        }

        // Initialize audio pipeline for local playback
        try
        {
            // Get services from DI container (if available)
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
                _logger);

            // Subscribe to local playback state changes
            _subscriptions.Add(_audioPipeline.StateChanges.Subscribe(OnLocalPlaybackStateChanged));

            // Enable bidirectional mode so local playback is reported to Spotify
            if (_session.PlaybackState != null)
            {
                _session.PlaybackState.EnableBidirectionalMode(
                    _audioPipeline,
                    _session.SpClient,
                    _session);
                WriteSuccess("Audio pipeline initialized - bidirectional playback enabled!");
            }
            else
            {
                WriteSuccess("Audio pipeline initialized - playback ready!");
            }
        }
        catch (Exception ex)
        {
            WriteWarning($"Audio pipeline initialization failed: {ex.Message}");
            WriteInfo("Playback commands will not be available.");
        }

        WriteInfo($"Type 'help' for available commands");
        System.Console.WriteLine();

        // Subscribe to all Connect events
        SubscribeToCommandEvents();
        SubscribeToPlaybackStateEvents();
        SubscribeToConnectionEvents();
        SubscribeToVolumeChanges();

        while (!cancellationToken.IsCancellationRequested)
        {
            System.Console.Write("> ");
            var input = System.Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
                continue;

            try
            {
                var shouldExit = await HandleCommandAsync(input, cancellationToken);
                if (shouldExit)
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                WriteError($"Command failed: {ex.Message}");
            }

            System.Console.WriteLine();
        }
    }

    private async Task<bool> HandleCommandAsync(string input, CancellationToken cancellationToken)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var command = parts[0].ToLower();  // Only lowercase the command, not arguments

        switch (command)
        {
            case "help":
                ShowHelp();
                return false;

            case "status":
                ShowStatus();
                return false;

            case "device":
                await HandleDeviceCommandAsync(parts, cancellationToken);
                return false;

            case "volume":
                await HandleVolumeCommandAsync(parts, cancellationToken);
                return false;

            case "watch":
                HandleWatchCommand(parts);
                return false;

            case "info":
                ShowDeviceInfo();
                return false;

            case "play":
                await HandlePlayCommandAsync(parts, cancellationToken);
                return false;

            case "pause":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.PauseAsync(cancellationToken);
                    WriteSuccess("Paused");
                }
                else
                {
                    WriteError("Audio pipeline not available");
                }
                return false;

            case "resume":
                if (_audioPipeline != null)
                {
                    await _audioPipeline.ResumeAsync(cancellationToken);
                    WriteSuccess("Resumed");
                }
                else
                {
                    WriteError("Audio pipeline not available");
                }
                return false;

            case "seek":
                await HandleSeekCommandAsync(parts, cancellationToken);
                return false;

            case "clear":
                System.Console.Clear();
                return false;

            case "quit":
            case "exit":
                WriteInfo("Shutting down gracefully...");
                return true;

            default:
                WriteError($"Unknown command: {command}. Type 'help' for available commands.");
                return false;
        }
    }

    private async Task HandlePlayCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (_audioPipeline == null)
        {
            WriteError("Audio pipeline not available");
            return;
        }

        if (parts.Length < 2)
        {
            WriteError("Usage: play <spotify:track:xxx>");
            return;
        }

        var trackUri = parts[1];
        WriteInfo($"Loading {trackUri}...");

        try
        {
            // Create a local play command (not from dealer, so use placeholder values)
            var command = new PlayCommand
            {
                Endpoint = "play",
                MessageIdent = "local",
                MessageId = 0,
                SenderDeviceId = _session.Config.DeviceId,
                Key = "local/0",
                TrackUri = trackUri
            };
            await _audioPipeline.PlayAsync(command, cancellationToken);
        }
        catch (Exception ex)
        {
            WriteError($"Playback failed: {ex.Message}");
        }
    }

    private async Task HandleSeekCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (_audioPipeline == null)
        {
            WriteError("Audio pipeline not available");
            return;
        }

        if (parts.Length < 2 || !int.TryParse(parts[1], out var seconds))
        {
            WriteError("Usage: seek <seconds>");
            return;
        }

        var positionMs = seconds * 1000L;
        await _audioPipeline.SeekAsync(positionMs, cancellationToken);
        WriteSuccess($"Seeked to {FormatTimeSpan(positionMs)}");
    }

    private void OnLocalPlaybackStateChanged(LocalPlaybackState state)
    {
        if (!string.IsNullOrEmpty(state.TrackUri) && state.IsPlaying)
        {
            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.Write("[AUDIO] ");
            System.Console.ForegroundColor = ConsoleColor.White;
            var status = state.IsPaused ? "Paused" : "Playing";
            System.Console.WriteLine($"{status} - {FormatTimeSpan(state.PositionMs)} / {FormatTimeSpan(state.DurationMs)}");
            System.Console.ResetColor();
            System.Console.Write("> ");
        }
    }

    private void ShowHelp()
    {
        WriteHeader("Available Commands:");
        System.Console.WriteLine("  help                 Show this help message");
        System.Console.WriteLine("  status               Display Connect connection status");
        System.Console.WriteLine("  device on|off        Toggle device active state");
        System.Console.WriteLine("  volume               Show current volume");
        System.Console.WriteLine("  volume set <0-100>   Set volume percentage");
        System.Console.WriteLine("  volume +|-           Increase/decrease volume by 5%");
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.WriteLine("  Playback:");
        System.Console.ResetColor();
        System.Console.WriteLine("  play <uri>           Play a track (e.g., play spotify:track:4iV5W9uYEdYUVa79Axb7Rh)");
        System.Console.WriteLine("  pause                Pause playback");
        System.Console.WriteLine("  resume               Resume playback");
        System.Console.WriteLine("  seek <seconds>       Seek to position in seconds");
        System.Console.WriteLine();
        System.Console.WriteLine("  watch start|stop     Toggle raw dealer message monitoring");
        System.Console.WriteLine("  info                 Show device information");
        System.Console.WriteLine("  clear                Clear console");
        System.Console.WriteLine("  quit                 Exit application");
        System.Console.WriteLine();
        System.Console.WriteLine("Live Events (automatically displayed):");
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine("  [CMD]                Remote control commands (Play, Pause, etc.)");
        System.Console.ForegroundColor = ConsoleColor.Magenta;
        System.Console.WriteLine("  [STATE]              Playback state changes (Track, Position, etc.)");
        System.Console.ForegroundColor = ConsoleColor.Yellow;
        System.Console.WriteLine("  [DEALER]             Connection status changes");
        System.Console.ResetColor();
    }

    private void ShowStatus()
    {
        WriteHeader("Spotify Connect Status:");

        // Dealer client status
        if (_session.Dealer != null)
        {
            var connectionId = GetConnectionId();
            var state = _session.Dealer.CurrentState;

            System.Console.WriteLine($"  Dealer:       {FormatConnectionState(state)}");
            if (!string.IsNullOrEmpty(connectionId))
            {
                System.Console.WriteLine($"  Connection:   {connectionId}");
            }
        }
        else
        {
            WriteError("  Dealer:       Disabled");
        }

        // Device state
        if (_session.DeviceState != null)
        {
            var isActive = _session.DeviceState.IsActive;
            var volumePct = _session.GetVolumePercentage() ?? 0;

            System.Console.WriteLine($"  Device:       {(isActive ? FormatSuccess("Active") : FormatWarning("Inactive"))}");
            System.Console.WriteLine($"  Volume:       {volumePct}% {RenderVolumeBar(volumePct)}");
        }
        else
        {
            WriteError("  Device:       Disabled");
        }

        // Watch status
        System.Console.WriteLine($"  Monitoring:   {(_watchEnabled ? FormatSuccess("Enabled") : "Disabled")}");
    }

    private async Task HandleDeviceCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 2)
        {
            WriteError("Usage: device on|off");
            return;
        }

        var action = parts[1].ToLower();
        switch (action)
        {
            case "on":
            case "active":
                var activated = await _session.SetDeviceActiveAsync(true, cancellationToken);
                if (activated)
                    WriteSuccess("Device is now active and visible in Spotify Connect");
                else
                    WriteError("Failed to activate device");
                break;

            case "off":
            case "inactive":
                var deactivated = await _session.SetDeviceActiveAsync(false, cancellationToken);
                if (deactivated)
                    WriteWarning("Device is now inactive and hidden from Spotify Connect");
                else
                    WriteError("Failed to deactivate device");
                break;

            default:
                WriteError($"Unknown device action: {action}. Use 'on' or 'off'.");
                break;
        }
    }

    private async Task HandleVolumeCommandAsync(string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length == 1)
        {
            // Just "volume" - show current volume
            var volumePct = _session.GetVolumePercentage() ?? 0;
            System.Console.WriteLine($"Current volume: {volumePct}% {RenderVolumeBar(volumePct)}");
            return;
        }

        var action = parts[1].ToLower();

        switch (action)
        {
            case "set":
                if (parts.Length < 3 || !int.TryParse(parts[2], out var targetPct))
                {
                    WriteError("Usage: volume set <0-100>");
                    return;
                }

                var setResult = await _session.SetVolumePercentageAsync(targetPct, cancellationToken);
                if (setResult)
                    WriteSuccess($"Volume set to {targetPct}% {RenderVolumeBar(targetPct)}");
                else
                    WriteError("Failed to set volume");
                break;

            case "+":
                await AdjustVolumeAsync(+5, cancellationToken);
                break;

            case "-":
                await AdjustVolumeAsync(-5, cancellationToken);
                break;

            default:
                // Try parsing as direct percentage
                if (int.TryParse(action, out var pct))
                {
                    var result = await _session.SetVolumePercentageAsync(pct, cancellationToken);
                    if (result)
                        WriteSuccess($"Volume set to {pct}% {RenderVolumeBar(pct)}");
                    else
                        WriteError("Failed to set volume");
                }
                else
                {
                    WriteError("Usage: volume [set <0-100>] [+|-]");
                }
                break;
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
            WriteSuccess($"{direction} volume to {newPct}% {RenderVolumeBar(newPct)}");
        }
        else
        {
            WriteError("Failed to adjust volume");
        }
    }

    private void HandleWatchCommand(string[] parts)
    {
        if (parts.Length < 2)
        {
            WriteError("Usage: watch start|stop");
            return;
        }

        var action = parts[1].ToLower();
        switch (action)
        {
            case "start":
            case "on":
                if (_watchEnabled)
                {
                    WriteWarning("Watch mode already enabled");
                }
                else
                {
                    SubscribeToMessages();
                    _watchEnabled = true;
                    WriteSuccess("Live monitoring enabled - dealer messages will be displayed");
                }
                break;

            case "stop":
            case "off":
                if (!_watchEnabled)
                {
                    WriteWarning("Watch mode already disabled");
                }
                else
                {
                    UnsubscribeFromMessages();
                    _watchEnabled = false;
                    WriteInfo("Live monitoring disabled");
                }
                break;

            default:
                WriteError($"Unknown watch action: {action}. Use 'start' or 'stop'.");
                break;
        }
    }

    private void ShowDeviceInfo()
    {
        WriteHeader("Device Information:");
        System.Console.WriteLine($"  Name:         {_session.Config.DeviceName}");
        System.Console.WriteLine($"  ID:           {_session.Config.DeviceId}");
        System.Console.WriteLine($"  Type:         {_session.Config.DeviceType}");
        System.Console.WriteLine($"  Client ID:    {_session.Config.GetClientId()[..16]}...");

        if (_session.DeviceState != null)
        {
            var volumePct = _session.GetVolumePercentage() ?? 0;
            var volumeRaw = _session.DeviceState.CurrentVolume;
            System.Console.WriteLine($"  Volume:       {volumePct}% (raw: {volumeRaw}/65535)");
            System.Console.WriteLine($"  Active:       {_session.DeviceState.IsActive}");
        }
    }

    private void SubscribeToCommandEvents()
    {
        if (_session.CommandHandler == null)
            return;

        // Play command
        _subscriptions.Add(_session.CommandHandler.PlayCommands.Subscribe(cmd =>
        {
            var details = $"Track: {cmd.TrackUri ?? "N/A"}, Context: {cmd.ContextUri ?? "N/A"}";
            if (cmd.Options != null)
                details += $" (shuffle: {cmd.Options.ShufflingContext}, repeat: {cmd.Options.RepeatingContext})";
            WriteCommand("Play", details);
        }));

        // Pause command
        _subscriptions.Add(_session.CommandHandler.PauseCommands.Subscribe(_ =>
            WriteCommand("Pause")));

        // Resume command
        _subscriptions.Add(_session.CommandHandler.ResumeCommands.Subscribe(_ =>
            WriteCommand("Resume")));

        // Seek command
        _subscriptions.Add(_session.CommandHandler.SeekCommands.Subscribe(cmd =>
            WriteCommand("Seek", $"Position: {FormatTimeSpan(cmd.PositionMs)}")));

        // Shuffle command
        _subscriptions.Add(_session.CommandHandler.ShuffleCommands.Subscribe(cmd =>
            WriteCommand("Shuffle", cmd.Enabled ? "Enabled" : "Disabled")));

        // Repeat Context command
        _subscriptions.Add(_session.CommandHandler.RepeatContextCommands.Subscribe(cmd =>
            WriteCommand("Repeat Context", cmd.Enabled ? "Enabled" : "Disabled")));

        // Repeat Track command
        _subscriptions.Add(_session.CommandHandler.RepeatTrackCommands.Subscribe(cmd =>
            WriteCommand("Repeat Track", cmd.Enabled ? "Enabled" : "Disabled")));

        // Skip Next command
        _subscriptions.Add(_session.CommandHandler.SkipNextCommands.Subscribe(_ =>
            WriteCommand("Skip Next")));

        // Skip Prev command
        _subscriptions.Add(_session.CommandHandler.SkipPrevCommands.Subscribe(_ =>
            WriteCommand("Skip Prev")));

        // Transfer command
        _subscriptions.Add(_session.CommandHandler.TransferCommands.Subscribe(cmd =>
            WriteCommand("Transfer", "Playback transferred to this device")));
    }

    private void SubscribeToPlaybackStateEvents()
    {
        if (_session.PlaybackState == null)
            return;

        // Track changed
        _subscriptions.Add(_session.PlaybackState.TrackChanged.Subscribe(state =>
        {
            if (state.Track != null)
            {
                var details = $"\"{state.Track.Title}\" - {state.Track.Artist}";
                if (!string.IsNullOrEmpty(state.Track.Album))
                    details += $" ({state.Track.Album})";
                WriteStateChange("Track", details);
            }
        }));

        // Playback status changed
        _subscriptions.Add(_session.PlaybackState.PlaybackStatusChanged.Subscribe(state =>
            WriteStateChange("Status", state.Status.ToString())));

        // Position changed (only log if significant)
        _subscriptions.Add(_session.PlaybackState.PositionChanged
            .Throttle(TimeSpan.FromSeconds(1)) // Real-time updates
            .Subscribe(state =>
            {
                var current = FormatTimeSpan(state.PositionMs);
                var total = FormatTimeSpan(state.DurationMs);
                WriteStateChange("Position", $"{current} / {total}");
            }));

        // Active device changed
        _subscriptions.Add(_session.PlaybackState.ActiveDeviceChanged.Subscribe(state =>
            WriteStateChange("Device", $"{state.ActiveDeviceId} (active)")));

        // Options changed
        _subscriptions.Add(_session.PlaybackState.OptionsChanged.Subscribe(state =>
        {
            var shuffle = state.Options.Shuffling ? "ON" : "OFF";
            var repeat = state.Options.RepeatingContext ? "Context" :
                        state.Options.RepeatingTrack ? "Track" : "OFF";
            WriteStateChange("Options", $"Shuffle: {shuffle}, Repeat: {repeat}");
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

            var details = state == ConnectionState.Connected && _session.Dealer.CurrentConnectionId != null
                ? $"ID: {_session.Dealer.CurrentConnectionId}"
                : null;

            WriteDealerEvent(status, details);
        }));
    }

    private void WriteCommand(string command, string? details = null)
    {
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.Write($"[CMD] {command}");
        if (details != null)
        {
            System.Console.ForegroundColor = ConsoleColor.White;
            System.Console.Write($" → {details}");
        }
        System.Console.WriteLine();
        System.Console.ResetColor();
        System.Console.Write("> ");
    }

    private void WriteStateChange(string changeType, string details)
    {
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Magenta;
        System.Console.Write($"[STATE] {changeType}");
        System.Console.ForegroundColor = ConsoleColor.White;
        System.Console.WriteLine($" → {details}");
        System.Console.ResetColor();
        System.Console.Write("> ");
    }

    private void WriteDealerEvent(string status, string? details = null)
    {
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Yellow;
        System.Console.Write($"[DEALER] {status}");
        if (details != null)
        {
            System.Console.ForegroundColor = ConsoleColor.White;
            System.Console.Write($" → {details}");
        }
        System.Console.WriteLine();
        System.Console.ResetColor();
        System.Console.Write("> ");
    }

    private string FormatTimeSpan(long milliseconds)
    {
        var ts = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    private void SubscribeToVolumeChanges()
    {
        if (_session.DeviceState?.Volume == null)
            return;

        var subscription = _session.DeviceState.Volume
            .DistinctUntilChanged()
            .Skip(1) // Skip initial value
            .Subscribe(volume =>
            {
                var pct = ConnectStateHelpers.VolumeToPercentage(volume);
                System.Console.WriteLine();
                WriteNotification($"Volume changed remotely: {pct}% {RenderVolumeBar(pct)}");
                System.Console.Write("> ");
            });

        _subscriptions.Add(subscription);
    }

    private void SubscribeToMessages()
    {
        if (_session.Dealer?.Messages == null)
            return;

        var subscription = _session.Dealer.Messages
            .Subscribe(message =>
            {
                System.Console.WriteLine();
                WriteNotification($"[Dealer Message] {message.Uri}");
                if (message.Headers.Count > 0)
                {
                    foreach (var header in message.Headers)
                    {
                        System.Console.WriteLine($"  {header.Key}: {header.Value}");
                    }
                }
                System.Console.Write("> ");
            });

        _subscriptions.Add(subscription);
    }

    private void UnsubscribeFromMessages()
    {
        // Keep volume subscription, only remove message subscription
        if (_subscriptions.Count > 1)
        {
            _subscriptions[1].Dispose();
            _subscriptions.RemoveAt(1);
        }
    }

    private string GetConnectionId()
    {
        if (_session.Dealer?.CurrentConnectionId == null)
            return string.Empty;

        return _session.Dealer.CurrentConnectionId;
    }

    private string RenderVolumeBar(int percentage)
    {
        const int barWidth = 20;
        var filled = (int)Math.Round(percentage / 100.0 * barWidth);
        var empty = barWidth - filled;

        return $"[{new string('█', filled)}{new string('░', empty)}]";
    }

    private string FormatConnectionState(ConnectionState state)
    {
        return state switch
        {
            ConnectionState.Connected => FormatSuccess("Connected"),
            ConnectionState.Connecting => FormatWarning("Connecting..."),
            ConnectionState.Disconnected => FormatError("Disconnected"),
            _ => state.ToString()
        };
    }

    private void WriteHeader(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine(message);
        System.Console.ResetColor();
    }

    private void WriteSuccess(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.WriteLine(message);
        System.Console.ResetColor();
    }

    private void WriteError(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Red;
        System.Console.WriteLine(message);
        System.Console.ResetColor();
    }

    private void WriteWarning(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Yellow;
        System.Console.WriteLine(message);
        System.Console.ResetColor();
    }

    private void WriteInfo(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Gray;
        System.Console.WriteLine(message);
        System.Console.ResetColor();
    }

    private void WriteNotification(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Magenta;
        System.Console.WriteLine(message);
        System.Console.ResetColor();
    }

    private string FormatSuccess(string text)
    {
        return $"\u001b[32m{text}\u001b[0m";  // Green
    }

    private string FormatError(string text)
    {
        return $"\u001b[31m{text}\u001b[0m";  // Red
    }

    private string FormatWarning(string text)
    {
        return $"\u001b[33m{text}\u001b[0m";  // Yellow
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var subscription in _subscriptions)
        {
            subscription?.Dispose();
        }
        _subscriptions.Clear();

        // Synchronously dispose audio pipeline (blocking)
        if (_audioPipeline != null)
        {
            _audioPipeline.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _audioPipeline = null;
        }

        // Dispose DI container (disposes all registered services)
        _serviceProvider?.Dispose();
        _serviceProvider = null;

        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        foreach (var subscription in _subscriptions)
        {
            subscription?.Dispose();
        }
        _subscriptions.Clear();

        if (_audioPipeline != null)
        {
            await _audioPipeline.DisposeAsync();
            _audioPipeline = null;
        }

        // Dispose DI container (disposes all registered services)
        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync();
            _serviceProvider = null;
        }

        _disposed = true;
    }
}
