using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;

namespace Wavee.UI.WinUI.Services;

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
internal partial class AppSettingsJsonContext : JsonSerializerContext { }

public sealed class SettingsService : ISettingsService, IDisposable
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wavee", "settings.json");

    private AppSettings _settings = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private readonly ILogger? _logger;
    private const int DebounceMs = 500;

    public AppSettings Settings => _settings;

    public SettingsService(ILogger<SettingsService>? logger = null)
    {
        _logger = logger;
    }

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = await File.ReadAllTextAsync(SettingsPath);
                _settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                _logger?.LogInformation("Settings loaded from {Path}", SettingsPath);
            }
            else
            {
                _logger?.LogInformation("No settings file found, using defaults");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load settings, using defaults");
            _settings = new AppSettings();
        }
    }

    public void Update(Action<AppSettings> modifier)
    {
        modifier(_settings);
        DebounceSave();
    }

    public async Task SaveAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_settings, AppSettingsJsonContext.Default.AppSettings);
            await File.WriteAllTextAsync(SettingsPath, json);
            _logger?.LogDebug("Settings saved to {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save settings");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void DebounceSave()
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        _ = Task.Delay(DebounceMs, token).ContinueWith(async _ =>
        {
            if (!token.IsCancellationRequested)
                await SaveAsync();
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        // Force final save
        try { SaveAsync().GetAwaiter().GetResult(); }
        catch { /* Best effort on dispose */ }
    }
}
