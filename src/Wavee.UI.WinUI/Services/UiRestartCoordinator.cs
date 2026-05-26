using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers.Application;

namespace Wavee.UI.WinUI.Services;

internal sealed class UiRestartCoordinator
{
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(90);

    private readonly ISettingsService? _settingsService;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<UiRestartCoordinator>? _logger;
    private int _restartInProgress;

    public UiRestartCoordinator(
        ISettingsService? settingsService = null,
        INotificationService? notificationService = null,
        ILogger<UiRestartCoordinator>? logger = null)
    {
        _settingsService = settingsService;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task RestartUiOnlyAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _restartInProgress, 1, 0) != 0)
            return;

        Process? newUiProcess = null;
        string? handoffFilePath = null;
        try
        {
            var manager = AppLifecycleHelper.AudioProcessManager
                ?? throw new InvalidOperationException("Audio process manager is not available.");
            if (manager.ProcessId <= 0 || manager.Proxy?.IsConnected != true)
                throw new InvalidOperationException("Audio process is not connected.");

            if (_settingsService is not null)
                await _settingsService.SaveAsync().ConfigureAwait(false);

            var pipeName = $"WaveeAudioHandoff_{Environment.ProcessId}_{Guid.NewGuid():N}";
            var sessionId = Guid.NewGuid().ToString("N");
            var launchToken = CreateLaunchToken();
            handoffFilePath = WriteHandoffFile(manager.ProcessId, pipeName, sessionId, launchToken);

            var processPath = ResolveCurrentProcessPath();
            newUiProcess = Process.Start(new ProcessStartInfo(processPath)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
                Arguments = $"{QuoteArg(UiHandoffLaunchOptions.CommandLineSwitch)} {QuoteArg(handoffFilePath)}"
            });

            if (newUiProcess is null)
                throw new InvalidOperationException("Could not start replacement UI process.");

            await manager.PrepareUiHandoffAsync(
                newUiProcess.Id,
                pipeName,
                sessionId,
                launchToken,
                HandoffTimeout,
                ct).ConfigureAwait(false);

            App.BeginUiHandoffExit();
            _logger?.LogInformation("UI-only restart handoff prepared; exiting current UI process");
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "UI-only restart failed");
            TryKillReplacement(newUiProcess);
            TryDeleteFile(handoffFilePath);
            _notificationService?.Show(new NotificationInfo
            {
                Message = "Could not restart the UI without restarting audio.",
                Severity = NotificationSeverity.Error,
                AutoDismissAfter = TimeSpan.FromSeconds(8)
            });
            Interlocked.Exchange(ref _restartInProgress, 0);
        }
    }

    private static string WriteHandoffFile(
        int audioHostProcessId,
        string pipeName,
        string sessionId,
        string launchToken)
    {
        var dir = Path.Combine(AppPaths.AppDataDirectory, "handoff");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"ui-handoff-{Guid.NewGuid():N}.json");
        var payload = new UiHandoffFilePayload
        {
            AudioHostProcessId = audioHostProcessId,
            PipeName = pipeName,
            SessionId = sessionId,
            LaunchToken = launchToken,
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, UiHandoffJsonContext.Default.UiHandoffFilePayload));
        return path;
    }

    private static string ResolveCurrentProcessPath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            processPath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(processPath))
            throw new InvalidOperationException("Could not determine the current process path.");
        return processPath;
    }

    private static string CreateLaunchToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string QuoteArg(string value)
        => value.Contains(' ') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;

    private static void TryKillReplacement(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
