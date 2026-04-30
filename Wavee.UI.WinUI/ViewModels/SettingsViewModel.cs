using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog.Events;
// Processors now live in AudioHost — EQ config goes via IPC
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.Core.Session;
using Wavee.Core.Time;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private enum PendingRestartArea
    {
        Localization,
        SpotifyMetadata,
        Cache,
        Connection,
    }

    private sealed record RestartSensitiveSettingsSnapshot(
        string Language,
        string SpotifyMetadataLanguage,
        CachingProfile CachingProfile,
        bool CacheEnabled,
        long CacheSizeLimitBytes,
        bool AutoReconnect,
        int ConnectionTimeoutSeconds);

    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly InMemorySink _inMemorySink;
    private readonly ISession? _session;
    private readonly IUpdateService? _updateService;
    private readonly IMetadataDatabase? _metadataDatabase;
    private readonly IMessenger? _messenger;
    private readonly INotificationService? _notificationService;
    private readonly ILogger? _logger;
    private readonly RestartSensitiveSettingsSnapshot _launchRestartSettings;
    private readonly HashSet<PendingRestartArea> _pendingRestartAreas = [];
    private bool _disposed;

    private static readonly string LogDirectory = AppPaths.LogsDirectory;

    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wavee", "AudioCache");

    // ── Diagnostics ──
    public ObservableCollection<LogEntry> LogEntries { get; }

    /// <summary>
    /// Filtered view of log entries based on level filters and search text.
    /// </summary>
    public ObservableCollection<LogEntry> FilteredLogEntries { get; } = [];

    [RelayCommand]
    private void ClearLogs()
    {
        _inMemorySink.Clear();
        FilteredLogEntries.Clear();
    }

    public TabItemParameter TabItemParameter { get; } = new()
    {
        InitialPageType = typeof(Views.SettingsPage)
    };

    public event EventHandler<TabItemParameter>? ContentChanged;

    public string AppVersion => _updateService?.CurrentVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "0.0.0";

    // ── Update Service ──

    public IUpdateService? UpdateService => _updateService;

    public bool HasUpdateError => _updateService?.Status == UpdateStatus.Error;

    public string DistributionModeDisplay => _updateService?.Distribution switch
    {
        DistributionMode.Store => AppLocalization.GetString("DistributionMode_Store"),
        DistributionMode.Sideloaded => AppLocalization.GetString("DistributionMode_Sideloaded"),
        _ => AppLocalization.GetString("DistributionMode_Portable")
    };

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (_updateService == null) return;
        await _updateService.CheckForUpdateAsync();
    }

    public LocalFilesViewModel? LocalFiles { get; }

    public SettingsViewModel(ISettingsService settingsService, IThemeService themeService, InMemorySink inMemorySink,
        IAudioPipelineControl? pipelineControl = null,
        ISession? session = null,
        IUpdateService? updateService = null,
        IMetadataDatabase? metadataDatabase = null,
        IMessenger? messenger = null,
        INotificationService? notificationService = null,
        ILogger<SettingsViewModel>? logger = null,
        LocalFilesViewModel? localFiles = null)
    {
        LocalFiles = localFiles;
        _settingsService = settingsService;
        _themeService = themeService;
        _inMemorySink = inMemorySink;
        _pipelineControl = pipelineControl;
        _session = session;
        _updateService = updateService;
        _metadataDatabase = metadataDatabase;
        _messenger = messenger;
        _notificationService = notificationService;
        _logger = logger;
        LogEntries = inMemorySink.Entries;

        // Initialize from persisted settings
        var s = _settingsService.Settings;
        _launchRestartSettings = CaptureRestartSensitiveSettings(s);

        _selectedThemeIndex = s.Theme switch
        {
            "Light" => 0,
            "Dark" => 1,
            _ => 2 // Default / System
        };

        _selectedLanguageIndex = AppLocalization.NormalizeLanguage(s.Language) switch
        {
            "en-US" => 1,
            "ko-KR" => 2,
            _ => 0
        };

        var spotifyMetadataLanguage = SpotifyMetadataLanguageSettings.NormalizeSetting(s.SpotifyMetadataLanguage);
        _isSpotifyMetadataSameAsApp = spotifyMetadataLanguage == SpotifyMetadataLanguageSettings.MatchApp;
        _spotifyMetadataCustomLanguageCode = _isSpotifyMetadataSameAsApp ? string.Empty : spotifyMetadataLanguage;

        _trackClickIndex = s.TrackClickBehavior == "SingleTap" ? 0 : 1;

        _defaultPlayActionIndex = s.DefaultPlayAction switch
        {
            "PlayAndClear" => 0,
            "PlayNext" => 1,
            "PlayLater" => 2,
            _ => 0
        };

        _askPlayAction = s.AskPlayAction;

        _audioPresetIndex = s.AudioPreset switch
        {
            "Radio" => 1,
            _ => 0
        };

        _audioQualityIndex = s.AudioQuality switch
        {
            "Normal" => 0,
            "High" => 1,
            _ => 2 // VeryHigh
        };

        _normalizationEnabled = s.NormalizationEnabled;
        _autoplayEnabled = s.AutoplayEnabled;
        _showLocalFilesOnHome = s.ShowLocalFilesOnHome;

        // Initialize lyrics sources from persisted prefs or defaults
        InitializeLyricsSources(s);

        _cacheEnabled = s.CacheEnabled;
        _cacheSizeLimitIndex = s.CacheSizeLimitBytes switch
        {
            500L * 1024 * 1024 => 0,
            1L * 1024 * 1024 * 1024 => 1,
            2L * 1024 * 1024 * 1024 => 2,
            5L * 1024 * 1024 * 1024 => 3,
            _ => 1 // default 1GB
        };

        // Initialize zoom level from persisted settings
        _zoomLevelIndex = Array.IndexOf(ZoomStops, Math.Round(s.ZoomLevel, 1));
        if (_zoomLevelIndex < 0) _zoomLevelIndex = 3; // default 100%

        // Initialize caching profile slider from persisted settings.
        // Slider uses double so it binds to Slider.Value without a converter.
        _cachingProfileIndex = (double)(int)s.CachingProfile;

        _autoReconnect = s.AutoReconnect;
        _connectionTimeoutIndex = s.ConnectionTimeoutSeconds switch
        {
            10 => 0,
            30 => 1,
            60 => 2,
            _ => 1
        };

        // Listen to update service status changes for HasUpdateError
        if (_updateService != null)
        {
            _updateService.PropertyChanged += OnUpdateServicePropertyChanged;
        }

        // Initialize clock sync display + start live countdown timer
        UpdateClockDisplay();
        StartClockTimer();

        // Subscribe to log entry changes to maintain filtered view
        LogEntries.CollectionChanged += OnLogEntriesCollectionChanged;

        // Initial populate
        RefreshFilteredLogs();
        RefreshPastLogs();
        UpdatePendingRestartState();
    }

    private void OnUpdateServicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.UpdateStatus) || e.PropertyName == "Status")
            OnPropertyChanged(nameof(HasUpdateError));
    }

    private void OnLogEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (LogEntry entry in e.NewItems)
            {
                if (PassesFilter(entry))
                    FilteredLogEntries.Insert(0, entry);
            }
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            foreach (LogEntry entry in e.OldItems)
                FilteredLogEntries.Remove(entry);
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            FilteredLogEntries.Clear();
        }
    }

    // ── General ──

    [ObservableProperty]
    private int _selectedThemeIndex;

    partial void OnSelectedThemeIndexChanged(int value)
    {
        var theme = value switch
        {
            0 => ElementTheme.Light,
            1 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        _themeService.SetTheme(theme);
        _settingsService.Update(s => s.Theme = theme.ToString());
    }

    [ObservableProperty]
    private int _selectedLanguageIndex;

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        var language = value switch
        {
            1 => "en-US",
            2 => "ko-KR",
            _ => "system"
        };

        _settingsService.Update(s => s.Language = language);
        UpdatePendingRestartState();
    }

    [ObservableProperty]
    private bool _isSpotifyMetadataSameAsApp = true;

    [ObservableProperty]
    private string _spotifyMetadataCustomLanguageCode = string.Empty;

    public bool IsSpotifyMetadataCustomCodeVisible => !IsSpotifyMetadataSameAsApp;

    partial void OnIsSpotifyMetadataSameAsAppChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSpotifyMetadataCustomCodeVisible));
        PersistSpotifyMetadataLanguageSetting();
    }

    partial void OnSpotifyMetadataCustomLanguageCodeChanged(string value)
    {
        var normalized = SpotifyMetadataLanguageSettings.NormalizeLocaleCode(value);
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
        {
            SpotifyMetadataCustomLanguageCode = normalized;
            return;
        }

        PersistSpotifyMetadataLanguageSetting();
    }

    private void PersistSpotifyMetadataLanguageSetting()
    {
        var spotifyMetadataLanguage = IsSpotifyMetadataSameAsApp
            ? SpotifyMetadataLanguageSettings.MatchApp
            : SpotifyMetadataLanguageSettings.NormalizeLocaleCode(SpotifyMetadataCustomLanguageCode);

        _settingsService.Update(s => s.SpotifyMetadataLanguage = spotifyMetadataLanguage);
        UpdatePendingRestartState();
    }

    [ObservableProperty]
    private bool _isRestartInProgress;

    [ObservableProperty]
    private bool _hasRestartError;

    partial void OnIsRestartInProgressChanged(bool value)
    {
        OnPropertyChanged(nameof(PendingRestartTitle));
        OnPropertyChanged(nameof(PendingRestartMessage));
        OnPropertyChanged(nameof(RestartNowActionText));
        OnPropertyChanged(nameof(PendingRestartSeverity));
        OnPropertyChanged(nameof(CanRestartNow));
    }

    partial void OnHasRestartErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(PendingRestartTitle));
        OnPropertyChanged(nameof(PendingRestartMessage));
        OnPropertyChanged(nameof(PendingRestartSeverity));
    }

    public bool HasPendingRestart => _pendingRestartAreas.Count > 0;

    public string PendingRestartTitle => HasRestartError
        ? AppLocalization.GetString("Settings_RestartFailedTitle")
        : AppLocalization.GetString("Settings_RestartPendingTitle");

    public string PendingRestartMessage => HasPendingRestart
        ? HasRestartError
            ? AppLocalization.GetString("Settings_RestartFailedMessage")
            : AppLocalization.Format("Settings_RestartPendingMessage", FormatPendingRestartAreas())
        : string.Empty;

    public string RestartNowActionText => IsRestartInProgress
        ? AppLocalization.GetString("Settings_Restarting")
        : AppLocalization.GetString("Settings_RestartNow");

    public InfoBarSeverity PendingRestartSeverity => HasRestartError
        ? InfoBarSeverity.Error
        : InfoBarSeverity.Informational;

    public bool CanRestartNow => HasPendingRestart && !IsRestartInProgress;

    [RelayCommand]
    private async Task RestartNowAsync()
    {
        if (!CanRestartNow)
        {
            return;
        }

        try
        {
            HasRestartError = false;
            IsRestartInProgress = true;
            await MainWindow.Instance.RestartApplicationAsync();
        }
        catch (Exception ex)
        {
            IsRestartInProgress = false;
            HasRestartError = true;
            _logger?.LogWarning(ex, "Failed to restart application from settings");
        }
    }

    // ── Zoom / Display scaling ──

    private static readonly double[] ZoomStops = [0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.3];

    public event EventHandler<double>? ZoomChanged;

    [ObservableProperty]
    private int _zoomLevelIndex = 3; // default 100%

    public string ZoomLevelDisplay => $"{(int)(ZoomStops[ZoomLevelIndex] * 100)}%";

    public string ZoomPreviewLabel => ZoomStops[ZoomLevelIndex] switch
    {
        <= 0.8 => AppLocalization.GetString("Settings_Zoom_Compact"),
        <= 1.1 => AppLocalization.GetString("Settings_Zoom_Default"),
        _ => AppLocalization.GetString("Settings_Zoom_Spacious")
    };

    partial void OnZoomLevelIndexChanged(int value)
    {
        if (value < 0 || value >= ZoomStops.Length) return;
        var zoom = ZoomStops[value];
        _settingsService.Update(s => s.ZoomLevel = zoom);
        OnPropertyChanged(nameof(ZoomLevelDisplay));
        OnPropertyChanged(nameof(ZoomPreviewLabel));
        ZoomChanged?.Invoke(this, zoom);
    }

    [RelayCommand]
    private void ResetZoom()
    {
        ZoomLevelIndex = 3; // 100%
    }

    // ── Caching profile ──

    /// <summary>
    /// Slider-bound index (0-3) mapping to the <see cref="CachingProfile"/> enum.
    /// Using double so the slider binds directly to Slider.Value without a converter.
    /// Changes take effect after restart (cache services are singletons built at startup).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CachingProfileSummary))]
    private double _cachingProfileIndex;

    /// <summary>
    /// "Medium · ~120 MB estimated in caches" — updates live as the user drags the slider.
    /// </summary>
    public string CachingProfileSummary
    {
        get
        {
            var profile = IndexToProfile(_cachingProfileIndex);
            return AppLocalization.Format(
                "Settings_CachingProfileSummary",
                CachingProfilePresets.GetDisplayName(profile),
                CachingProfilePresets.FormatEstimate(profile));
        }
    }

    partial void OnCachingProfileIndexChanged(double value)
    {
        var profile = IndexToProfile(value);
        _settingsService.Update(s => s.CachingProfile = profile);
        UpdatePendingRestartState();
    }

    private static CachingProfile IndexToProfile(double index)
    {
        var i = Math.Clamp((int)Math.Round(index), 0, 3);
        return (CachingProfile)i;
    }

    // ── Playback ──

    [ObservableProperty]
    private int _trackClickIndex;

    partial void OnTrackClickIndexChanged(int value)
    {
        var behavior = value == 0 ? "SingleTap" : "DoubleTap";
        _settingsService.Update(s => s.TrackClickBehavior = behavior);
    }

    [ObservableProperty]
    private int _defaultPlayActionIndex;

    partial void OnDefaultPlayActionIndexChanged(int value)
    {
        var action = value switch
        {
            0 => "PlayAndClear",
            1 => "PlayNext",
            2 => "PlayLater",
            _ => "PlayAndClear"
        };
        _settingsService.Update(s => s.DefaultPlayAction = action);
    }

    [ObservableProperty]
    private bool _askPlayAction;

    partial void OnAskPlayActionChanged(bool value)
    {
        _settingsService.Update(s => s.AskPlayAction = value);
    }

    [ObservableProperty]
    private int _audioPresetIndex;

    partial void OnAudioPresetIndexChanged(int value)
    {
        var preset = value switch
        {
            1 => "Radio",
            _ => "None"
        };
        _settingsService.Update(s => s.AudioPreset = preset);
    }

    // ── Audio quality & normalization ──

    [ObservableProperty]
    private int _audioQualityIndex;

    partial void OnAudioQualityIndexChanged(int value)
    {
        var quality = value switch
        {
            0 => "Normal",
            1 => "High",
            _ => "VeryHigh"
        };
        _settingsService.Update(s => s.AudioQuality = quality);

        // Switch quality live on the playing track
        var coreQuality = value switch
        {
            0 => Wavee.Core.Audio.AudioQuality.Normal,
            1 => Wavee.Core.Audio.AudioQuality.High,
            _ => Wavee.Core.Audio.AudioQuality.VeryHigh
        };
        if (_pipelineControl != null)
            _ = _pipelineControl.SwitchQualityAsync(coreQuality, CancellationToken.None);
    }

    [ObservableProperty]
    private bool _normalizationEnabled;

    partial void OnNormalizationEnabledChanged(bool value)
    {
        _settingsService.Update(s => s.NormalizationEnabled = value);

        // Toggle normalization processor live
        _pipelineControl?.SetNormalizationEnabled(value);
    }

    [ObservableProperty]
    private bool _autoplayEnabled;

    partial void OnAutoplayEnabledChanged(bool value)
    {
        // Orchestrator reads AppSettings.AutoplayEnabled via a Func callback
        // wired in AppLifecycleHelper, so this mutation takes effect on the
        // next end-of-context evaluation with no further plumbing.
        _settingsService.Update(s => s.AutoplayEnabled = value);
    }

    // ── Verbose logging ──

    [ObservableProperty]
    private bool _showLocalFilesOnHome;

    partial void OnShowLocalFilesOnHomeChanged(bool value)
    {
        _settingsService.Update(s => s.ShowLocalFilesOnHome = value);
        WeakReferenceMessenger.Default.Send(new HomeLocalFilesVisibilityChangedMessage(value));
    }

    /// <summary>
    /// When on, drops the Serilog minimum level to Verbose for the UI process and starts the
    /// audio process with --verbose on its next launch. Persisted to settings.json.
    /// </summary>
    public bool VerboseLoggingEnabled
    {
        get => _settingsService.Settings.VerboseLoggingEnabled;
        set
        {
            if (_settingsService.Settings.VerboseLoggingEnabled == value) return;
            _settingsService.Update(s => s.VerboseLoggingEnabled = value);
            AppLifecycleHelper.SetVerboseLogging(value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// When on, reveals the in-app memory diagnostics panel under Settings → Diagnostics.
    /// Persisted so a leak hunt across an app restart keeps the same workflow.
    /// Toggling it on starts the periodic background logger; off stops it.
    /// </summary>
    public bool MemoryDiagnosticsEnabled
    {
        get => _settingsService.Settings.MemoryDiagnosticsEnabled;
        set
        {
            if (_settingsService.Settings.MemoryDiagnosticsEnabled == value) return;
            _settingsService.Update(s => s.MemoryDiagnosticsEnabled = value);
            AppLifecycleHelper.SetMemoryDiagnostics(value);
            OnPropertyChanged();
        }
    }

    // ── Lyrics sources ──

    private static readonly (string Name, string Description)[] DefaultLyricsSources =
    [
        ("AMLL-TTML-DB", "Syllable-synced TTML lyrics (GitHub)"),
        ("LRCLIB", "Open-source LRC lyrics database"),
        ("QQMusic", "QQ Music lyrics (Chinese service)"),
        ("Kugou", "Kugou lyrics database"),
        ("Netease", "NetEase Cloud Music lyrics"),
        ("Musixmatch", "Large Western lyrics database"),
    ];

    public ObservableCollection<LyricsSourceItem> LyricsSources { get; } = [];

    private void InitializeLyricsSources(AppSettings s)
    {
        LyricsSources.Clear();

        if (s.LyricsSourcePreferences is { Count: > 0 })
        {
            // Restore persisted order + enabled state
            foreach (var pref in s.LyricsSourcePreferences)
            {
                var desc = DefaultLyricsSources.FirstOrDefault(d =>
                    d.Name.Equals(pref.Name, StringComparison.OrdinalIgnoreCase)).Description ?? "";
                var item = new LyricsSourceItem { Name = pref.Name, Description = desc, IsEnabled = pref.IsEnabled };
                item.PropertyChanged += (_, _) => PersistLyricsSources();
                LyricsSources.Add(item);
            }

            // Add any new providers not yet in saved prefs
            foreach (var (name, desc) in DefaultLyricsSources)
            {
                if (LyricsSources.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var item = new LyricsSourceItem { Name = name, Description = desc, IsEnabled = true };
                item.PropertyChanged += (_, _) => PersistLyricsSources();
                LyricsSources.Add(item);
            }
        }
        else
        {
            // First run — populate defaults
            foreach (var (name, desc) in DefaultLyricsSources)
            {
                var item = new LyricsSourceItem { Name = name, Description = desc, IsEnabled = true };
                item.PropertyChanged += (_, _) => PersistLyricsSources();
                LyricsSources.Add(item);
            }
        }

        LyricsSources.CollectionChanged += (_, _) => PersistLyricsSources();
    }

    private void PersistLyricsSources()
    {
        _settingsService.Update(s =>
        {
            s.LyricsSourcePreferences = LyricsSources.Select(x => new LyricsSourcePref
            {
                Name = x.Name,
                IsEnabled = x.IsEnabled,
            }).ToList();
        });
    }

    [RelayCommand]
    private void MoveLyricsSourceUp(LyricsSourceItem item)
    {
        var idx = LyricsSources.IndexOf(item);
        if (idx > 0)
            LyricsSources.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveLyricsSourceDown(LyricsSourceItem item)
    {
        var idx = LyricsSources.IndexOf(item);
        if (idx >= 0 && idx < LyricsSources.Count - 1)
            LyricsSources.Move(idx, idx + 1);
    }

    // ── Cache (requires restart) ──

    [ObservableProperty]
    private bool _cacheEnabled;

    partial void OnCacheEnabledChanged(bool value)
    {
        _settingsService.Update(s => s.CacheEnabled = value);
        UpdatePendingRestartState();
    }

    [ObservableProperty]
    private int _cacheSizeLimitIndex;

    partial void OnCacheSizeLimitIndexChanged(int value)
    {
        var bytes = value switch
        {
            0 => 500L * 1024 * 1024,      // 500 MB
            1 => 1L * 1024 * 1024 * 1024,  // 1 GB
            2 => 2L * 1024 * 1024 * 1024,  // 2 GB
            3 => 5L * 1024 * 1024 * 1024,  // 5 GB
            _ => 1L * 1024 * 1024 * 1024
        };
        _settingsService.Update(s => s.CacheSizeLimitBytes = bytes);
        UpdatePendingRestartState();
    }

    public string CacheLocationDisplay => CacheDirectory;

    public string CacheSizeDisplay
    {
        get
        {
            try
            {
                if (!Directory.Exists(CacheDirectory)) return AppLocalization.GetString("Size_ZeroMb");
                var size = new DirectoryInfo(CacheDirectory)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
                return size switch
                {
                    < 1024 * 1024 => string.Format(System.Globalization.CultureInfo.CurrentUICulture, "{0:F1} KB", size / 1024.0),
                    < 1024L * 1024 * 1024 => string.Format(System.Globalization.CultureInfo.CurrentUICulture, "{0:F1} MB", size / (1024.0 * 1024)),
                    _ => string.Format(System.Globalization.CultureInfo.CurrentUICulture, "{0:F2} GB", size / (1024.0 * 1024 * 1024))
                };
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "Failed to calculate cache size"); return AppLocalization.GetString("State_Unknown"); }
        }
    }

    [RelayCommand]
    private void ClearCache()
    {
        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch (Exception ex) { _logger?.LogDebug(ex, "Failed to delete cache file {File}", file); }
                }
            }
            OnPropertyChanged(nameof(CacheSizeDisplay));
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Failed to clear cache"); }
    }

    [RelayCommand]
    private async Task ClearCollectionRevisionsAsync(XamlRoot? xamlRoot)
    {
        if (_metadataDatabase == null)
        {
            _logger?.LogWarning("ClearCollectionRevisions invoked but metadata database is unavailable");
            return;
        }

        if (xamlRoot != null)
        {
            var dialog = new ContentDialog
            {
                Title = "Clear collection revisions?",
                Content = "This forces a full refresh of your Spotify library. Your local cached lists (Liked Songs, albums, artists, shows) will be rebuilt from the server. Useful if something looks out of date.",
                PrimaryButtonText = "Clear & resync",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        try
        {
            await _metadataDatabase.ClearAllSyncStateAsync();
            _messenger?.Send(new RequestLibrarySyncMessage());
            _notificationService?.Show(
                "Collection revisions cleared — resyncing your library…",
                NotificationSeverity.Success,
                TimeSpan.FromSeconds(4));
            _logger?.LogInformation("User cleared collection revisions; full resync requested");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear collection revisions");
            _notificationService?.Show(
                "Could not clear collection revisions.",
                NotificationSeverity.Error,
                TimeSpan.FromSeconds(4));
        }
    }

    // ── Connection (requires restart) ──

    [ObservableProperty]
    private bool _autoReconnect;

    partial void OnAutoReconnectChanged(bool value)
    {
        _settingsService.Update(s => s.AutoReconnect = value);
        UpdatePendingRestartState();
    }

    [ObservableProperty]
    private int _connectionTimeoutIndex;

    partial void OnConnectionTimeoutIndexChanged(int value)
    {
        var seconds = value switch
        {
            0 => 10,
            1 => 30,
            2 => 60,
            _ => 30
        };
        _settingsService.Update(s => s.ConnectionTimeoutSeconds = seconds);
        UpdatePendingRestartState();
    }

    private RestartSensitiveSettingsSnapshot CaptureRestartSensitiveSettings(AppSettings settings)
    {
        return new RestartSensitiveSettingsSnapshot(
            Language: AppLocalization.NormalizeLanguage(settings.Language),
            SpotifyMetadataLanguage: SpotifyMetadataLanguageSettings.NormalizeSetting(settings.SpotifyMetadataLanguage),
            CachingProfile: settings.CachingProfile,
            CacheEnabled: settings.CacheEnabled,
            CacheSizeLimitBytes: settings.CacheSizeLimitBytes,
            AutoReconnect: settings.AutoReconnect,
            ConnectionTimeoutSeconds: settings.ConnectionTimeoutSeconds);
    }

    private void UpdatePendingRestartState()
    {
        var current = CaptureRestartSensitiveSettings(_settingsService.Settings);
        var pending = new HashSet<PendingRestartArea>();

        if (!string.Equals(current.Language, _launchRestartSettings.Language, StringComparison.Ordinal))
        {
            pending.Add(PendingRestartArea.Localization);
        }

        if (!string.Equals(current.SpotifyMetadataLanguage, _launchRestartSettings.SpotifyMetadataLanguage, StringComparison.Ordinal))
        {
            pending.Add(PendingRestartArea.SpotifyMetadata);
        }

        if (current.CachingProfile != _launchRestartSettings.CachingProfile ||
            current.CacheEnabled != _launchRestartSettings.CacheEnabled ||
            current.CacheSizeLimitBytes != _launchRestartSettings.CacheSizeLimitBytes)
        {
            pending.Add(PendingRestartArea.Cache);
        }

        if (current.AutoReconnect != _launchRestartSettings.AutoReconnect ||
            current.ConnectionTimeoutSeconds != _launchRestartSettings.ConnectionTimeoutSeconds)
        {
            pending.Add(PendingRestartArea.Connection);
        }

        if (_pendingRestartAreas.SetEquals(pending))
        {
            return;
        }

        _pendingRestartAreas.Clear();
        foreach (var area in pending)
        {
            _pendingRestartAreas.Add(area);
        }

        OnPropertyChanged(nameof(HasPendingRestart));
        OnPropertyChanged(nameof(PendingRestartTitle));
        OnPropertyChanged(nameof(PendingRestartMessage));
        OnPropertyChanged(nameof(PendingRestartSeverity));
        OnPropertyChanged(nameof(CanRestartNow));

        if (!HasPendingRestart)
        {
            HasRestartError = false;
        }
    }

    private string FormatPendingRestartAreas()
    {
        var labels = new List<string>();

        if (_pendingRestartAreas.Contains(PendingRestartArea.Localization))
        {
            labels.Add(AppLocalization.GetString("Settings_RestartArea_Localization"));
        }

        if (_pendingRestartAreas.Contains(PendingRestartArea.SpotifyMetadata))
        {
            labels.Add(AppLocalization.GetString("Settings_RestartArea_SpotifyMetadata"));
        }

        if (_pendingRestartAreas.Contains(PendingRestartArea.Cache))
        {
            labels.Add(AppLocalization.GetString("Settings_RestartArea_Cache"));
        }

        if (_pendingRestartAreas.Contains(PendingRestartArea.Connection))
        {
            labels.Add(AppLocalization.GetString("Settings_RestartArea_Connection"));
        }

        return string.Join(", ", labels);
    }

    // ── Clock Sync ──

    [ObservableProperty]
    private long _clockOffsetMs;

    [ObservableProperty]
    private long _clockLastRttMs;

    [ObservableProperty]
    private bool _clockIsSynced;

    [ObservableProperty]
    private string _clockLastSyncDisplay = AppLocalization.GetString("Clock_Never");

    [ObservableProperty]
    private string _clockNextSyncCountdown = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    private int _clockSyncIntervalIndex = 1; // default 10 min

    private DispatcherTimer? _clockTimer;

    partial void OnClockSyncIntervalIndexChanged(int value)
    {
        var minutes = value switch
        {
            0 => 5,
            1 => 10,
            2 => 15,
            3 => 30,
            _ => 10
        };
        if (_session?.Clock is { } clock)
            clock.SyncIntervalMinutes = minutes;
    }

    [RelayCommand]
    private async Task RefreshClockAsync()
    {
        if (_session?.Clock is not { } clock) return;
        await clock.SyncAsync();
        UpdateClockDisplay();
    }

    private void StartClockTimer()
    {
        if (_session?.Clock is null) return;
        if (_clockTimer != null) return;
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += OnClockTimerTick;
        _clockTimer.Start();
    }

    private void OnClockTimerTick(object? sender, object e) => UpdateClockCountdown();

    private void UpdateClockCountdown()
    {
        if (_session?.Clock is not { } clock || !clock.IsSynced)
        {
            ClockNextSyncCountdown = AppLocalization.GetString("State_EmDash");
            return;
        }

        var nextSync = clock.LastSyncUtc + TimeSpan.FromMinutes(clock.SyncIntervalMinutes);
        var remaining = nextSync - DateTimeOffset.UtcNow;
        if (remaining.TotalSeconds <= 0)
            ClockNextSyncCountdown = AppLocalization.GetString("Clock_Syncing");
        else if (remaining.TotalMinutes >= 1)
            ClockNextSyncCountdown = $"{(int)remaining.TotalMinutes}m {remaining.Seconds:D2}s";
        else
            ClockNextSyncCountdown = $"{remaining.Seconds}s";
    }

    private void UpdateClockDisplay()
    {
        if (_session?.Clock is not { } clock) return;
        ClockOffsetMs = clock.OffsetMs;
        ClockLastRttMs = clock.LastRttMs;
        ClockIsSynced = clock.IsSynced;
        ClockLastSyncDisplay = clock.IsSynced
            ? AppLocalization.Format("Clock_SyncedAt", clock.LastSyncUtc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentUICulture))
            : AppLocalization.GetString("Clock_Never");
        UpdateClockCountdown();
    }

    // ── Audio Pipeline Health ──

    [ObservableProperty]
    private string _audioPipelineMode = AppLocalization.GetString("AudioPipeline_InProcess");

    [ObservableProperty]
    private string _audioPipelineStatus = AppLocalization.GetString("State_Unknown");

    [ObservableProperty]
    private int _audioPipelinePid;

    [ObservableProperty]
    private int _audioRestartCount;

    [ObservableProperty]
    private long _audioUnderrunCount;

    [ObservableProperty]
    private string _audioGcStats = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    private string _audioProfilerTop = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    private string _audioUiStalls = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    private string _audioThroughput = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    private string _audioStateFreshness = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    private double _audioLastRttMs;

    // Chart reference — set by the page after InitializeComponent
    public Action<double[], int, string>? UpdateRttChart { get; set; }

    private DispatcherTimer? _audioDiagTimer;

    public void StartAudioDiagnostics()
    {
        AudioPipelineMode = AppLocalization.GetString("AudioPipeline_InProcess");
        AudioPipelineStatus = AppLocalization.GetString("State_Unknown");
        AudioGcStats = AppLocalization.GetString("State_EmDash");
        AudioProfilerTop = AppLocalization.GetString("State_EmDash");
        AudioUiStalls = AppLocalization.GetString("State_EmDash");
        AudioThroughput = AppLocalization.GetString("State_EmDash");
        AudioStateFreshness = AppLocalization.GetString("State_EmDash");

        if (_audioDiagTimer != null) return;
        _audioDiagTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _audioDiagTimer.Tick += OnAudioDiagTimerTick;
        _audioDiagTimer.Start();
        RefreshAudioDiagnostics();
    }

    public void StopAudioDiagnostics()
    {
        if (_audioDiagTimer != null)
        {
            _audioDiagTimer.Stop();
            _audioDiagTimer.Tick -= OnAudioDiagTimerTick;
        }
        _audioDiagTimer = null;
    }

    private void OnAudioDiagTimerTick(object? sender, object e) => RefreshAudioDiagnostics();

    private void RefreshAudioDiagnostics()
    {
        // Pipeline mode + process manager state
        var mgr = AppLifecycleHelper.AudioProcessManager;
        if (mgr != null)
        {
            AudioPipelineMode = AppLocalization.GetString("AudioPipeline_OutOfProcess");
            AudioPipelineStatus = mgr.State.ToString();
            AudioPipelinePid = mgr.ProcessId;
            AudioRestartCount = mgr.RestartCount;
        }
        else if (AppLifecycleHelper.UseOutOfProcessAudio)
        {
            AudioPipelineMode = AppLocalization.GetString("AudioPipeline_OutOfProcess");
            AudioPipelineStatus = AppLocalization.GetString("AudioPipeline_NotStarted");
        }
        else
        {
            AudioPipelineMode = AppLocalization.GetString("AudioPipeline_InProcess");
            AudioPipelineStatus = AppLocalization.GetString("State_Active");
        }

        // Profiler stats
        var profiler = UiOperationProfiler.Instance;
        if (profiler != null)
        {
            AudioUnderrunCount = profiler.AudioUnderrunCount;
            var gc = profiler.CumulativeGc;
            AudioGcStats = $"Gen0: {gc.Gen0}  Gen1: {gc.Gen1}  Gen2: {gc.Gen2}";

            var topOps = profiler.GetTopOperations(3);
            if (topOps.Count > 0)
            {
                AudioProfilerTop = string.Join("\n", topOps.Select(
                    op => $"{op.Name}: max={op.MaxMs:F0}ms avg={op.AvgMs:F0}ms (n={op.Count})"));
            }
            else
            {
                AudioProfilerTop = AppLocalization.GetString("AudioPipeline_NoOperationsRecorded");
            }

            AudioUiStalls = $"Underruns: {profiler.AudioUnderrunCount}";
        }

        // IPC metrics from proxy
        var proxy = mgr?.Proxy;
        if (proxy != null)
        {
            AudioThroughput = $"sent: {proxy.MessagesSent}  recv: {proxy.MessagesReceived}";
            var freshness = proxy.StateFreshnessMs;
            AudioStateFreshness = freshness < 1
                ? AppLocalization.GetString("State_EmDash")
                : AppLocalization.Format("AudioPipeline_MillisecondsAgo", freshness.ToString("F0", System.Globalization.CultureInfo.CurrentUICulture));
            AudioLastRttMs = proxy.LastRttMs;

            // Update chart
            UpdateRttChart?.Invoke(proxy.RttHistory, proxy.RttHistoryCount, "ms");
        }
        else
        {
            AudioThroughput = AppLocalization.GetString("State_EmDash");
            AudioStateFreshness = AppLocalization.GetString("State_EmDash");
        }
    }

    // ── Log filters ──

    [ObservableProperty]
    private bool _showVerbose;

    [ObservableProperty]
    private bool _showDebug;

    [ObservableProperty]
    private bool _showInfo = true;

    [ObservableProperty]
    private bool _showWarning = true;

    [ObservableProperty]
    private bool _showError = true;

    [ObservableProperty]
    private bool _showFatal = true;

    [ObservableProperty]
    private string _logSearchText = "";

    partial void OnShowVerboseChanged(bool value) => RefreshFilteredLogs();
    partial void OnShowDebugChanged(bool value) => RefreshFilteredLogs();
    partial void OnShowInfoChanged(bool value) => RefreshFilteredLogs();
    partial void OnShowWarningChanged(bool value) => RefreshFilteredLogs();
    partial void OnShowErrorChanged(bool value) => RefreshFilteredLogs();
    partial void OnShowFatalChanged(bool value) => RefreshFilteredLogs();
    partial void OnLogSearchTextChanged(string value) => RefreshFilteredLogs();

    private bool PassesFilter(LogEntry entry)
    {
        var levelOk = entry.Level switch
        {
            LogEventLevel.Verbose => ShowVerbose,
            LogEventLevel.Debug => ShowDebug,
            LogEventLevel.Information => ShowInfo,
            LogEventLevel.Warning => ShowWarning,
            LogEventLevel.Error => ShowError,
            LogEventLevel.Fatal => ShowFatal,
            _ => true
        };
        if (!levelOk) return false;

        if (!string.IsNullOrWhiteSpace(LogSearchText))
        {
            return entry.Message.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase)
                || entry.Category.Contains(LogSearchText, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private void RefreshFilteredLogs()
    {
        FilteredLogEntries.Clear();
        for (var i = LogEntries.Count - 1; i >= 0; i--)
        {
            if (PassesFilter(LogEntries[i]))
                FilteredLogEntries.Add(LogEntries[i]);
        }
    }

    // ── Past logs ──

    public ObservableCollection<PastLogFile> PastLogFiles { get; } = [];

    [RelayCommand]
    private void RefreshPastLogs()
    {
        PastLogFiles.Clear();
        try
        {
            if (!Directory.Exists(LogDirectory)) return;
            foreach (var file in new DirectoryInfo(LogDirectory)
                .EnumerateFiles("wavee*.log")
                .OrderByDescending(f => f.LastWriteTime))
            {
                PastLogFiles.Add(new PastLogFile
                {
                    FileName = file.Name,
                    FilePath = file.FullName,
                    FileSize = file.Length switch
                    {
                        < 1024 => $"{file.Length} B",
                        < 1024 * 1024 => $"{file.Length / 1024.0:F1} KB",
                        _ => $"{file.Length / (1024.0 * 1024):F1} MB"
                    },
                    LastModified = file.LastWriteTime
                });
            }
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Failed to enumerate log files"); }
    }

    [RelayCommand]
    private void OpenLogFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Failed to open log file {Path}", path); }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            if (Directory.Exists(LogDirectory))
                Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Failed to open logs folder"); }
    }

    // ── Equalizer ──

    private static readonly int[] EqFrequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    private static readonly string[] EqFrequencyLabels =
        ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    public static readonly string[] EqPresetNames =
        ["Flat", "Bass Boost", "Treble Boost", "Vocal", "Radio"];

    private static readonly string[] EqPresetDescriptions =
    [
        "No processing — flat, transparent playback",
        "Enhanced low-end warmth and punch for bass-heavy genres",
        "Crisp highs and sparkle for acoustic and classical music",
        "Boosted mids to bring vocals forward in the mix",
        "FM broadcast sound — punchy, loud, and consistent"
    ];

    private static readonly Dictionary<string, double[]> EqPresets = new()
    {
        ["Flat"]         = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        ["Bass Boost"]   = [6, 5, 4, 2, 0, 0, 0, 0, 0, 0],
        ["Treble Boost"] = [0, 0, 0, 0, 0, 1, 2, 3, 4, 5],
        ["Vocal"]        = [-2, -1, 0, 2, 4, 4, 2, 0, -1, -2],
        ["Radio"]        = [0, 2, -2, 0, 0, 2, 4, 2, 2, 2],
    };

    public string EqPresetDescription =>
        SelectedEqPresetIndex >= 0 && SelectedEqPresetIndex < EqPresetDescriptions.Length
            ? EqPresetDescriptions[SelectedEqPresetIndex]
            : "";

    public ObservableCollection<EqualizerBandViewModel> EqBands { get; } = [];

    // EQ control goes through IPC to AudioHost via IAudioPipelineControl
    private Data.Contracts.IAudioPipelineControl? _pipelineControl;

    [ObservableProperty]
    private bool _isEqualizerEnabled;

    [ObservableProperty]
    private int _selectedEqPresetIndex;

    public void InitializeEqualizer(Data.Contracts.IAudioPipelineControl? control)
    {
        _pipelineControl = control;

        var s = _settingsService.Settings;
        _isEqualizerEnabled = s.EqualizerEnabled;

        _selectedEqPresetIndex = Array.IndexOf(EqPresetNames, s.EqualizerPreset);
        if (_selectedEqPresetIndex < 0) _selectedEqPresetIndex = 0;

        EqBands.Clear();
        for (var i = 0; i < 10; i++)
        {
            var gain = i < s.EqualizerBandGains.Length ? s.EqualizerBandGains[i] : 0.0;
            var band = new EqualizerBandViewModel(i, EqFrequencies[i], EqFrequencyLabels[i], gain);
            band.GainChanged += OnBandGainChanged;
            EqBands.Add(band);
        }

        SendEqToAudioHost();

        OnPropertyChanged(nameof(IsEqualizerEnabled));
        OnPropertyChanged(nameof(SelectedEqPresetIndex));

        _logger?.LogInformation("Equalizer initialized: preset={Preset}, enabled={Enabled}, bands={Bands}",
            EqPresetNames[_selectedEqPresetIndex], _isEqualizerEnabled, EqBands.Count);
    }

    partial void OnIsEqualizerEnabledChanged(bool value)
    {
        _settingsService.Update(s => s.EqualizerEnabled = value);
        SendEqToAudioHost();
        _logger?.LogInformation("Equalizer toggled: {State}", value ? "ON" : "OFF");
    }

    partial void OnSelectedEqPresetIndexChanged(int value)
    {
        if (value < 0 || value >= EqPresetNames.Length) return;
        var presetName = EqPresetNames[value];
        if (EqPresets.TryGetValue(presetName, out var gains))
        {
            for (var i = 0; i < EqBands.Count && i < gains.Length; i++)
                EqBands[i].GainDb = gains[i];
        }
        _settingsService.Update(s => s.EqualizerPreset = presetName);
        OnPropertyChanged(nameof(EqPresetDescription));
        SendEqToAudioHost();
        _logger?.LogInformation("Equalizer preset changed to: {Preset}", presetName);
    }

    // Single timer reused across all band changes — preset switch fires 10 band
    // updates and the prior implementation allocated a CTS + Task + continuation
    // per call. A Change()-able Timer just resets the deadline with no allocation.
    private System.Threading.Timer? _eqDebounceTimer;

    private void OnBandGainChanged(int bandIndex, double gainDb)
    {
        _settingsService.Update(s =>
        {
            if (bandIndex < s.EqualizerBandGains.Length)
                s.EqualizerBandGains[bandIndex] = gainDb;
        });

        // Lazily create on first use; subsequent calls just reset the deadline.
        var timer = _eqDebounceTimer ??= new System.Threading.Timer(
            _ => SendEqToAudioHost(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        timer.Change(50, System.Threading.Timeout.Infinite);
    }

    [RelayCommand]
    private void ResetEq()
    {
        SelectedEqPresetIndex = 0; // Flat
    }

    private double[] GetBandGains() => EqBands.Select(b => b.GainDb).ToArray();

    private void SendEqToAudioHost()
    {
        _ = _pipelineControl?.SetEqualizerAsync(IsEqualizerEnabled, GetBandGains());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_updateService != null)
            _updateService.PropertyChanged -= OnUpdateServicePropertyChanged;

        LogEntries.CollectionChanged -= OnLogEntriesCollectionChanged;

        if (_clockTimer != null)
        {
            _clockTimer.Stop();
            _clockTimer.Tick -= OnClockTimerTick;
            _clockTimer = null;
        }

        StopAudioDiagnostics();

        _eqDebounceTimer?.Dispose();
        _eqDebounceTimer = null;

        foreach (var band in EqBands)
            band.GainChanged -= OnBandGainChanged;

        UpdateRttChart = null;
        ZoomChanged = null;
        ContentChanged = null;
    }
}

public sealed class EqualizerBandViewModel : ObservableObject
{
    private readonly int _index;
    private double _gainDb;

    public EqualizerBandViewModel(int index, int frequencyHz, string frequencyLabel, double gainDb)
    {
        _index = index;
        FrequencyHz = frequencyHz;
        FrequencyLabel = frequencyLabel;
        _gainDb = gainDb;
    }

    public int FrequencyHz { get; }
    public string FrequencyLabel { get; }

    public double GainDb
    {
        get => _gainDb;
        set
        {
            value = Math.Clamp(value, -12.0, 12.0);
            if (SetProperty(ref _gainDb, value))
            {
                OnPropertyChanged(nameof(NormalizedGain));
                GainChanged?.Invoke(_index, value);
            }
        }
    }

    /// <summary>
    /// 0.0 = -12dB (bottom), 0.5 = 0dB (center), 1.0 = +12dB (top).
    /// Used for Y-position in the curve control.
    /// </summary>
    public double NormalizedGain => (GainDb + 12.0) / 24.0;

    public event Action<int, double>? GainChanged;
}

public sealed partial class LyricsSourceItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    [ObservableProperty]
    private bool _isEnabled;
}

public sealed class PastLogFile
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string FileSize { get; init; } = "";
    public DateTime LastModified { get; init; }
}
