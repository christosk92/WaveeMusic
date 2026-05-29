using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog.Events;
// Processors now live in AudioHost — EQ config goes via IPC
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.Core.Session;
using Wavee.Core.Time;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Helpers.Application;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

[global::WinRT.GeneratedBindableCustomProperty]
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
    private readonly IPlaybackStateService? _playbackStateService;
    private readonly IUpdateService? _updateService;
    private readonly IMetadataDatabase? _metadataDatabase;
    private readonly IMessenger? _messenger;
    private readonly INotificationService? _notificationService;
    private readonly ILogger? _logger;
    private readonly DispatcherQueue? _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
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
    public bool IsLocalFilesFeatureEnabled => AppFeatureFlags.LocalFilesEnabled;

    public SettingsViewModel(ISettingsService settingsService, IThemeService themeService, InMemorySink inMemorySink,
        IAudioPipelineControl? pipelineControl = null,
        IPlaybackStateService? playbackStateService = null,
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
        _playbackStateService = playbackStateService;
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

        SelectedThemeIndex = s.Theme switch
        {
            "Light" => 0,
            "Dark" => 1,
            _ => 2 // Default / System
        };

        SelectedLanguageIndex = AppLocalization.NormalizeLanguage(s.Language) switch
        {
            "en-US" => 1,
            "ko-KR" => 2,
            _ => 0
        };

        var spotifyMetadataLanguage = SpotifyMetadataLanguageSettings.NormalizeSetting(s.SpotifyMetadataLanguage);
        IsSpotifyMetadataSameAsApp = spotifyMetadataLanguage == SpotifyMetadataLanguageSettings.MatchApp;
        SpotifyMetadataCustomLanguageCode = IsSpotifyMetadataSameAsApp ? string.Empty : spotifyMetadataLanguage;

        TrackClickIndex = s.TrackClickBehavior == "SingleTap" ? 0 : 1;

        DefaultPlayActionIndex = s.DefaultPlayAction switch
        {
            "PlayAndClear" => 0,
            "PlayNext" => 1,
            "PlayLater" => 2,
            _ => 0
        };

        AskPlayAction = s.AskPlayAction;

        AudioPresetIndex = s.AudioPreset switch
        {
            "Radio" => 1,
            _ => 0
        };

        AudioQualityIndex = s.AudioQuality switch
        {
            "Normal" => 0,
            "High" => 1,
            _ => 2 // VeryHigh
        };

        NormalizationEnabled = s.NormalizationEnabled;
        AutoplayEnabled = s.AutoplayEnabled;
        IsPrivateSession = s.IsPrivateSession;
        MinimizeToTrayOnClose = s.MinimizeToTrayOnClose;
        ShowDockedPlayerWithFloatingPlayer = s.ShowDockedPlayerWithFloatingPlayer;
        ShowLocalFilesOnHome = s.ShowLocalFilesOnHome;

        // Initialize lyrics sources from persisted prefs or defaults
        InitializeLyricsSources(s);

        CacheEnabled = s.CacheEnabled;
        CacheSizeLimitIndex = s.CacheSizeLimitBytes switch
        {
            500L * 1024 * 1024 => 0,
            1L * 1024 * 1024 * 1024 => 1,
            2L * 1024 * 1024 * 1024 => 2,
            5L * 1024 * 1024 * 1024 => 3,
            _ => 1 // default 1GB
        };

        // Initialize zoom level from persisted settings
        ZoomLevelIndex = Array.IndexOf(ZoomStops, Math.Round(s.ZoomLevel, 1));
        if (ZoomLevelIndex < 0) ZoomLevelIndex = 3; // default 100%

        // Initialize caching profile slider from persisted settings.
        // Slider uses double so it binds to Slider.Value without a converter.
        CachingProfileIndex = (double)(int)s.CachingProfile;

        AutoReconnect = s.AutoReconnect;
        ConnectionTimeoutIndex = s.ConnectionTimeoutSeconds switch
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

        if (_playbackStateService != null)
        {
            _playbackStateService.PropertyChanged += OnPlaybackStatePropertyChanged;
        }

        // Initialize clock sync display + start live countdown timer
        UpdateClockDisplay();
        StartClockTimer();

        if (AppFeatureFlags.DiagnosticsEnabled)
        {
            // Subscribe to log entry changes to maintain filtered view.
            LogEntries.CollectionChanged += OnLogEntriesCollectionChanged;
            RefreshFilteredLogs();
            RefreshPastLogs();
        }

        UpdatePendingRestartState();
    }

    private void OnUpdateServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Services.UpdateStatus) && e.PropertyName != "Status")
            return;

        // UpdateService raises PropertyChanged on whatever thread its async
        // CheckForUpdateAsync continuation completes on, which is rarely the
        // UI thread. Re-raising HasUpdateError synchronously from here makes
        // x:Bind's generated PropertyChanged handler call back into
        // Application.Current.Resources (LookupConverter), which is thread-
        // affine and throws RPC_E_WRONG_THREAD (0x8001010E) → finalizer
        // rethrow → TaskSchedulerUnobservedTaskException. Marshal explicitly.
        var dispatcher = _dispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
            OnPropertyChanged(nameof(HasUpdateError));
        else
            dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(HasUpdateError)));
    }

    private void OnPlaybackStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName is nameof(IPlaybackStateService.CurrentTrackNormalizationGainDb)
                or nameof(IPlaybackStateService.CurrentTrackNormalizationPeak)
                or nameof(IPlaybackStateService.CurrentTrackId))
        {
            RaiseNormalizationDescriptionChanged();
        }
    }

    private void RaiseNormalizationDescriptionChanged()
    {
        var dispatcher = _dispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            OnPropertyChanged(nameof(NormalizationDescription));
        }
        else
        {
            dispatcher.TryEnqueue(() => OnPropertyChanged(nameof(NormalizationDescription)));
        }
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
    public partial int SelectedThemeIndex { get; set; }

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
    public partial int SelectedLanguageIndex { get; set; }

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
    public partial bool IsSpotifyMetadataSameAsApp { get; set; } = true;

    [ObservableProperty]
    public partial string SpotifyMetadataCustomLanguageCode { get; set; } = string.Empty;

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
    public partial bool IsRestartInProgress { get; set; }

    [ObservableProperty]
    public partial bool HasRestartError { get; set; }

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

    /// <summary>Number of valid <see cref="ZoomLevelIndex"/> values (7).
    /// Exposed so ShellPage's keyboard / HUD step helpers can clamp without
    /// duplicating the stop table.</summary>
    public static int ZoomStopCount => ZoomStops.Length;

    /// <summary>Default <see cref="ZoomLevelIndex"/> (3 → 100%). Used by the
    /// Ctrl+0 reset path and the in-Settings Reset button.</summary>
    public const int ZoomDefaultIndex = 3;

    public event EventHandler<double>? ZoomChanged;

    [ObservableProperty]
    public partial int ZoomLevelIndex { get; set; } = ZoomDefaultIndex;

    partial void OnZoomLevelIndexChanged(int value)
    {
        if (value < 0 || value >= ZoomStops.Length) return;
        var zoom = ZoomStops[value];
        _settingsService.Update(s => s.ZoomLevel = zoom);
        ZoomChanged?.Invoke(this, zoom);
    }

    [RelayCommand]
    private void ResetZoom()
    {
        ZoomLevelIndex = ZoomDefaultIndex;
    }

    // ── Caching profile ──

    /// <summary>
    /// Slider-bound index (0-3) mapping to the <see cref="CachingProfile"/> enum.
    /// Using double so the slider binds directly to Slider.Value without a converter.
    /// Changes take effect after restart (cache services are singletons built at startup).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CachingProfileSummary))]
    public partial double CachingProfileIndex { get; set; }

    /// <summary>
    /// "Medium · ~120 MB estimated in caches" — updates live as the user drags the slider.
    /// </summary>
    public string CachingProfileSummary
    {
        get
        {
            var profile = IndexToProfile(CachingProfileIndex);
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
    public partial int TrackClickIndex { get; set; }

    partial void OnTrackClickIndexChanged(int value)
    {
        var behavior = value == 0 ? "SingleTap" : "DoubleTap";
        _settingsService.Update(s => s.TrackClickBehavior = behavior);
    }

    [ObservableProperty]
    public partial int DefaultPlayActionIndex { get; set; }

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
    public partial bool AskPlayAction { get; set; }

    partial void OnAskPlayActionChanged(bool value)
    {
        _settingsService.Update(s => s.AskPlayAction = value);
    }

    [ObservableProperty]
    public partial int AudioPresetIndex { get; set; }

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
    public partial int AudioQualityIndex { get; set; }

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
    public partial bool NormalizationEnabled { get; set; }

    public string NormalizationDescription
    {
        get
        {
            var gain = _playbackStateService?.CurrentTrackNormalizationGainDb;
            var peak = _playbackStateService?.CurrentTrackNormalizationPeak;
            return gain.HasValue && peak.HasValue
                ? $"Parsed from current stream: gain {gain.Value:+0.00;-0.00;0.00} dB, peak {peak.Value:0.0000}"
                : "Parsed gain and peak will appear while a Spotify stream is loaded";
        }
    }

    partial void OnNormalizationEnabledChanged(bool value)
    {
        _settingsService.Update(s => s.NormalizationEnabled = value);

        // Toggle normalization processor live
        _pipelineControl?.SetNormalizationEnabled(value);
    }

    [ObservableProperty]
    public partial bool AutoplayEnabled { get; set; }

    partial void OnAutoplayEnabledChanged(bool value)
    {
        // Orchestrator reads AppSettings.AutoplayEnabled via a Func callback
        // wired in AppLifecycleHelper, so this mutation takes effect on the
        // next end-of-context evaluation with no further plumbing.
        _settingsService.Update(s => s.AutoplayEnabled = value);
        WeakReferenceMessenger.Default.Send(new AutoplayEnabledChangedMessage(value));
    }

    [ObservableProperty]
    public partial bool IsPrivateSession { get; set; }

    partial void OnIsPrivateSessionChanged(bool value)
    {
        _settingsService.Update(s => s.IsPrivateSession = value);
        WeakReferenceMessenger.Default.Send(new PrivateSessionChangedMessage(value));
    }

    [ObservableProperty]
    public partial bool MinimizeToTrayOnClose { get; set; }

    partial void OnMinimizeToTrayOnCloseChanged(bool value)
    {
        _settingsService.Update(s => s.MinimizeToTrayOnClose = value);
        WeakReferenceMessenger.Default.Send(new MinimizeToTrayChangedMessage(value));
    }

    // ── Verbose logging ──

    [ObservableProperty]
    public partial bool ShowDockedPlayerWithFloatingPlayer { get; set; }

    partial void OnShowDockedPlayerWithFloatingPlayerChanged(bool value)
    {
        _settingsService.Update(s => s.ShowDockedPlayerWithFloatingPlayer = value);
        WeakReferenceMessenger.Default.Send(new DockedPlayerWithFloatingPlayerVisibilityChangedMessage(value));
    }

    [ObservableProperty]
    public partial bool ShowLocalFilesOnHome { get; set; }

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
        get => AppFeatureFlags.DiagnosticsEnabled && _settingsService.Settings.VerboseLoggingEnabled;
        set
        {
            if (!AppFeatureFlags.DiagnosticsEnabled) return;
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
        get => AppFeatureFlags.DiagnosticsEnabled && _settingsService.Settings.MemoryDiagnosticsEnabled;
        set
        {
            if (!AppFeatureFlags.DiagnosticsEnabled)
            {
                AppLifecycleHelper.SetMemoryDiagnostics(false);
                return;
            }

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
    public partial bool CacheEnabled { get; set; }

    partial void OnCacheEnabledChanged(bool value)
    {
        _settingsService.Update(s => s.CacheEnabled = value);
        UpdatePendingRestartState();
    }

    [ObservableProperty]
    public partial int CacheSizeLimitIndex { get; set; }

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
                Title = AppLocalization.GetString("Settings_ClearRevisionsTitle"),
                Content = AppLocalization.GetString("Settings_ClearRevisionsContent"),
                PrimaryButtonText = AppLocalization.GetString("Settings_ClearAndResync"),
                CloseButtonText = AppLocalization.GetString("Dialog_Cancel"),
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
    public partial bool AutoReconnect { get; set; }

    partial void OnAutoReconnectChanged(bool value)
    {
        _settingsService.Update(s => s.AutoReconnect = value);
        UpdatePendingRestartState();
    }

    [ObservableProperty]
    public partial int ConnectionTimeoutIndex { get; set; }

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
    public partial long ClockOffsetMs { get; set; }

    [ObservableProperty]
    public partial long ClockLastRttMs { get; set; }

    [ObservableProperty]
    public partial bool ClockIsSynced { get; set; }

    [ObservableProperty]
    public partial string ClockLastSyncDisplay { get; set; } = AppLocalization.GetString("Clock_Never");

    [ObservableProperty]
    public partial string ClockNextSyncCountdown { get; set; } = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    public partial int ClockSyncIntervalIndex { get; set; } = 1; // default 10 min

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
    public partial string AudioPipelineMode { get; set; } = AppLocalization.GetString("AudioPipeline_InProcess");

    [ObservableProperty]
    public partial string AudioPipelineStatus { get; set; } = AppLocalization.GetString("State_Unknown");

    [ObservableProperty]
    public partial int AudioPipelinePid { get; set; }

    [ObservableProperty]
    public partial int AudioRestartCount { get; set; }

    [ObservableProperty]
    public partial long AudioUnderrunCount { get; set; }

    [ObservableProperty]
    public partial string AudioGcStats { get; set; } = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    public partial string AudioProfilerTop { get; set; } = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    public partial string AudioUiStalls { get; set; } = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    public partial string AudioThroughput { get; set; } = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    public partial string AudioStateFreshness { get; set; } = AppLocalization.GetString("State_EmDash");

    [ObservableProperty]
    public partial double AudioLastRttMs { get; set; }

    // Chart reference — set by the page after InitializeComponent
    public Action<double[], int, string>? UpdateRttChart { get; set; }

    private DispatcherTimer? _audioDiagTimer;

    public void StartAudioDiagnostics()
    {
        if (!AppFeatureFlags.DiagnosticsEnabled)
            return;

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
    public partial bool ShowVerbose { get; set; }

    [ObservableProperty]
    public partial bool ShowDebug { get; set; }

    [ObservableProperty]
    public partial bool ShowInfo { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowWarning { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowError { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowFatal { get; set; } = true;

    [ObservableProperty]
    public partial string LogSearchText { get; set; } = "";

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

    [ObservableProperty]
    public partial string NavigationReportStatus { get; set; } = "";

    [RelayCommand]
    private void CopyNavigationHealthReport()
    {
        try
        {
            var diag = Wavee.UI.WinUI.Diagnostics.NavigationDiagnostics.Instance;
            var report = diag?.GenerateReport()
                ?? "Navigation diagnostics not initialized.";

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(report);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);

            NavigationReportStatus = $"Copied {report.Length} chars to clipboard at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to copy navigation health report");
            NavigationReportStatus = "Copy failed — see log for details.";
        }
    }

    // ── Equalizer ──

    private static readonly int[] EqFrequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    private static readonly string[] EqFrequencyLabels =
        ["31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    public static readonly string[] EqPresetNames =
        ["Flat", "Bass Boost", "Treble Boost", "Vocal", "Radio", "EQ Proof"];

    private static readonly string[] EqPresetDescriptions =
    [
        "No processing — flat, transparent playback",
        "Enhanced low-end warmth and punch for bass-heavy genres",
        "Crisp highs and sparkle for acoustic and classical music",
        "Boosted mids to bring vocals forward in the mix",
        "FM broadcast sound — punchy, loud, and consistent",
        "Extreme test curve. If this sounds normal, AudioHost is not applying EQ."
    ];

    private static readonly Dictionary<string, double[]> EqPresets = new()
    {
        ["Flat"]         = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        ["Bass Boost"]   = [6, 5, 4, 2, 0, 0, 0, 0, 0, 0],
        ["Treble Boost"] = [0, 0, 0, 0, 0, 1, 2, 3, 4, 5],
        ["Vocal"]        = [-2, -1, 0, 2, 4, 4, 2, 0, -1, -2],
        ["Radio"]        = [0, 2, -2, 0, 0, 2, 4, 2, 2, 2],
        // Deliberately ugly verification preset: alternating full boost/cut
        // across the 10 graphic-EQ bands. This should be unmistakable.
        ["EQ Proof"]     = [12, -12, 12, -12, 12, -12, 12, -12, 12, -12],
    };

    public string EqPresetDescription =>
        SelectedEqPresetIndex >= 0 && SelectedEqPresetIndex < EqPresetDescriptions.Length
            ? EqPresetDescriptions[SelectedEqPresetIndex]
            : "";

    public ObservableCollection<EqualizerBandViewModel> EqBands { get; } = [];

    // EQ control goes through IPC to AudioHost via IAudioPipelineControl
    private Wavee.UI.Contracts.IAudioPipelineControl? _pipelineControl;

    [ObservableProperty]
    public partial bool IsEqualizerEnabled { get; set; }

    [ObservableProperty]
    public partial int SelectedEqPresetIndex { get; set; }

    [ObservableProperty]
    public partial string EqualizerApplyStatus { get; set; } = "AudioHost: not applied yet";

    [ObservableProperty]
    public partial bool IsEqualizerApplying { get; set; }

    [ObservableProperty]
    public partial bool EqualizerApplySucceeded { get; set; }

    private long _equalizerApplyVersion;

    public void InitializeEqualizer(Wavee.UI.Contracts.IAudioPipelineControl? control)
    {
        _pipelineControl = control;

        var s = _settingsService.Settings;
        IsEqualizerEnabled = s.EqualizerEnabled;

        SelectedEqPresetIndex = Array.IndexOf(EqPresetNames, s.EqualizerPreset);
        if (SelectedEqPresetIndex < 0) SelectedEqPresetIndex = 0;

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
            EqPresetNames[SelectedEqPresetIndex], IsEqualizerEnabled, EqBands.Count);
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
        _ = SendEqToAudioHostAsync();
    }

    private async Task SendEqToAudioHostAsync()
    {
        var version = Interlocked.Increment(ref _equalizerApplyVersion);
        var enabled = IsEqualizerEnabled;
        var gains = GetBandGains();
        var preset = SelectedEqPresetIndex >= 0 && SelectedEqPresetIndex < EqPresetNames.Length
            ? EqPresetNames[SelectedEqPresetIndex]
            : "Custom";

        SetEqualizerApplyStatus(
            enabled
                ? $"AudioHost: applying {preset}..."
                : "AudioHost: disabling equalizer...",
            applying: true,
            succeeded: false);

        if (_pipelineControl is null)
        {
            SetEqualizerApplyStatus("AudioHost: not connected, EQ was only saved", applying: false, succeeded: false);
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await _pipelineControl.SetEqualizerAsync(enabled, gains, timeout.Token).ConfigureAwait(false);

            if (Volatile.Read(ref _equalizerApplyVersion) != version)
                return;

            var now = DateTime.Now.ToString("HH:mm:ss");
            SetEqualizerApplyStatus(
                enabled
                    ? result.ObservedAudioBuffer
                        ? $"AudioHost: verified {preset} on audio buffer at {now}"
                        : $"AudioHost: installed {preset}; waiting for playback to verify"
                    : $"AudioHost: equalizer off at {now}",
                applying: false,
                succeeded: !enabled || result.ObservedAudioBuffer);
            _logger?.LogInformation(
                "Equalizer AudioHost result: enabled={Enabled}, preset={Preset}, bands={Bands}, installed={Installed}, observedAudio={ObservedAudio}, message={Message}",
                enabled, preset, gains.Length, result.Installed, result.ObservedAudioBuffer, result.Message);
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref _equalizerApplyVersion) != version)
                return;

            SetEqualizerApplyStatus("AudioHost: EQ apply timed out", applying: false, succeeded: false);
            _notificationService?.Show(new NotificationInfo
            {
                Message = "Equalizer was saved, but AudioHost did not acknowledge it.",
                Severity = NotificationSeverity.Warning,
                AutoDismissAfter = TimeSpan.FromSeconds(5)
            });
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _equalizerApplyVersion) != version)
                return;

            SetEqualizerApplyStatus($"AudioHost: EQ apply failed ({ex.Message})", applying: false, succeeded: false);
            _logger?.LogWarning(ex, "Equalizer apply failed");
            _notificationService?.Show(new NotificationInfo
            {
                Message = "Equalizer was saved, but AudioHost rejected the live update.",
                Severity = NotificationSeverity.Warning,
                AutoDismissAfter = TimeSpan.FromSeconds(5)
            });
        }
    }

    private void SetEqualizerApplyStatus(string status, bool applying, bool succeeded)
    {
        void Apply()
        {
            EqualizerApplyStatus = status;
            IsEqualizerApplying = applying;
            EqualizerApplySucceeded = succeeded;
        }

        var dispatcher = _dispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            dispatcher.TryEnqueue(Apply);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_updateService != null)
            _updateService.PropertyChanged -= OnUpdateServicePropertyChanged;

        if (_playbackStateService != null)
            _playbackStateService.PropertyChanged -= OnPlaybackStatePropertyChanged;

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

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class EqualizerBandViewModel : ObservableObject
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

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class LyricsSourceItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }
}

public sealed class PastLogFile
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string FileSize { get; init; } = "";
    public DateTime LastModified { get; init; }
}