using System.Reactive.Linq;
using Wavee.Connect;
using Wavee.Connect.Connection;
using Wavee.Connect.Protocol;
using Wavee.Core.Session;

namespace Wavee.Console;

/// <summary>
/// Rich interactive console interface for Spotify Connect features.
/// </summary>
internal sealed class ConnectConsole : IDisposable
{
    private readonly Session _session;
    private readonly List<IDisposable> _subscriptions = new();
    private bool _watchEnabled;
    private bool _disposed;

    public ConnectConsole(Session session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
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
        WriteInfo($"Type 'help' for available commands");
        System.Console.WriteLine();

        // Subscribe to volume changes
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
        var parts = input.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var command = parts[0];

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

    private void ShowHelp()
    {
        WriteHeader("Available Commands:");
        System.Console.WriteLine("  help                 Show this help message");
        System.Console.WriteLine("  status               Display Connect connection status");
        System.Console.WriteLine("  device on|off        Toggle device active state");
        System.Console.WriteLine("  volume               Show current volume");
        System.Console.WriteLine("  volume set <0-100>   Set volume percentage");
        System.Console.WriteLine("  volume +|-           Increase/decrease volume by 5%");
        System.Console.WriteLine("  watch start|stop     Toggle live event monitoring");
        System.Console.WriteLine("  info                 Show device information");
        System.Console.WriteLine("  clear                Clear console");
        System.Console.WriteLine("  quit                 Exit application");
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

        _disposed = true;
    }
}
