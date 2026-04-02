using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Serilog.Events;
using Wavee.Connect.Playback.Processors;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IThemeService _themeService;
    private readonly InMemorySink _inMemorySink;

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Wavee", "logs");

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

    public string AppVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public SettingsViewModel(ISettingsService settingsService, IThemeService themeService, InMemorySink inMemorySink)
    {
        _settingsService = settingsService;
        _themeService = themeService;
        _inMemorySink = inMemorySink;
        LogEntries = inMemorySink.Entries;

        // Initialize from persisted settings
        var s = _settingsService.Settings;

        _selectedThemeIndex = s.Theme switch
        {
            "Light" => 0,
            "Dark" => 1,
            _ => 2 // Default / System
        };

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

        _selectedLyricsSourceIndex = s.LyricsSource == "Spotify" ? 0 : 1;

        // Multi-provider lyrics settings
        var lp = s.LyricsProviders;
        _searchStrategyIndex = lp.SearchStrategy == "BestMatch" ? 1 : 0;
        _matchThresholdIndex = lp.DefaultMatchThreshold switch
        {
            <= 30 => 0,
            <= 70 => 1,
            <= 90 => 2,
            _ => 3
        };
        foreach (var entry in lp.Providers)
        {
            var item = new LyricsProviderItemViewModel(entry.Id, GetProviderDisplayName(entry.Id), entry.IsEnabled);
            item.Changed += PersistLyricsProviders;
            LyricsProviderItems.Add(item);
        }

        _cacheEnabled = s.CacheEnabled;
        _cacheSizeLimitIndex = s.CacheSizeLimitBytes switch
        {
            500L * 1024 * 1024 => 0,
            1L * 1024 * 1024 * 1024 => 1,
            2L * 1024 * 1024 * 1024 => 2,
            5L * 1024 * 1024 * 1024 => 3,
            _ => 1 // default 1GB
        };

        _autoReconnect = s.AutoReconnect;
        _connectionTimeoutIndex = s.ConnectionTimeoutSeconds switch
        {
            10 => 0,
            30 => 1,
            60 => 2,
            _ => 1
        };

        // Subscribe to log entry changes to maintain filtered view
        LogEntries.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (LogEntry entry in e.NewItems)
                {
                    if (PassesFilter(entry))
                        FilteredLogEntries.Insert(0, entry);
                }
            }
            else if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                FilteredLogEntries.Clear();
            }
        };

        // Initial populate
        RefreshFilteredLogs();
        RefreshPastLogs();
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
        var pipeline = (CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Data.Contexts.IPlaybackCommandExecutor>() as Data.Contexts.ConnectCommandExecutor)?.LocalEngine
            as Wavee.Connect.Playback.AudioPipeline;
        if (pipeline != null)
            _ = pipeline.SwitchQualityAsync(coreQuality, System.Threading.CancellationToken.None);
    }

    [ObservableProperty]
    private bool _normalizationEnabled;

    partial void OnNormalizationEnabledChanged(bool value)
    {
        _settingsService.Update(s => s.NormalizationEnabled = value);

        // Toggle normalization processor live
        var pipeline = (CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Data.Contexts.IPlaybackCommandExecutor>() as Data.Contexts.ConnectCommandExecutor)?.LocalEngine
            as Wavee.Connect.Playback.AudioPipeline;
        if (pipeline != null)
            pipeline.SetNormalizationEnabled(value);
    }

    // ── Lyrics ──

    [ObservableProperty]
    private int _selectedLyricsSourceIndex;

    partial void OnSelectedLyricsSourceIndexChanged(int value)
    {
        _settingsService.Update(s => s.LyricsSource = value == 1 ? "LRCLIB" : "Spotify");
    }

    public ObservableCollection<LyricsProviderItemViewModel> LyricsProviderItems { get; } = [];

    [ObservableProperty]
    private int _searchStrategyIndex;

    partial void OnSearchStrategyIndexChanged(int value)
    {
        var strategy = value == 1 ? "BestMatch" : "Sequential";
        _settingsService.Update(s => s.LyricsProviders.SearchStrategy = strategy);
    }

    [ObservableProperty]
    private int _matchThresholdIndex;

    partial void OnMatchThresholdIndexChanged(int value)
    {
        var threshold = value switch { 0 => 30, 1 => 70, 2 => 90, 3 => 100, _ => 70 };
        _settingsService.Update(s => s.LyricsProviders.DefaultMatchThreshold = threshold);
    }

    [RelayCommand]
    private void MoveProviderUp(LyricsProviderItemViewModel item)
    {
        var idx = LyricsProviderItems.IndexOf(item);
        if (idx <= 0) return;
        LyricsProviderItems.Move(idx, idx - 1);
        PersistLyricsProviders();
    }

    [RelayCommand]
    private void MoveProviderDown(LyricsProviderItemViewModel item)
    {
        var idx = LyricsProviderItems.IndexOf(item);
        if (idx < 0 || idx >= LyricsProviderItems.Count - 1) return;
        LyricsProviderItems.Move(idx, idx + 1);
        PersistLyricsProviders();
    }

    private void PersistLyricsProviders()
    {
        _settingsService.Update(s =>
        {
            s.LyricsProviders.Providers = LyricsProviderItems
                .Select(p => new LyricsProviderEntry { Id = p.Id, IsEnabled = p.IsEnabled })
                .ToList();
        });
    }

    private static string GetProviderDisplayName(string id) => id switch
    {
        "Musixmatch" => "Musixmatch",
        "QQMusic" => "QQ Music",
        "Netease" => "NetEase Cloud Music",
        "LRCLIB" => "LRCLIB",
        "Spotify" => "Spotify",
        "AppleMusic" => "Apple Music",
        "Kugou" => "Kugou",
        "SodaMusic" => "Soda Music",
        _ => id
    };

    // ── Cache (requires restart) ──

    [ObservableProperty]
    private bool _cacheEnabled;

    partial void OnCacheEnabledChanged(bool value)
    {
        _settingsService.Update(s => s.CacheEnabled = value);
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
    }

    public string CacheLocationDisplay => CacheDirectory;

    public string CacheSizeDisplay
    {
        get
        {
            try
            {
                if (!Directory.Exists(CacheDirectory)) return "0 MB";
                var size = new DirectoryInfo(CacheDirectory)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
                return size switch
                {
                    < 1024 * 1024 => $"{size / 1024.0:F1} KB",
                    < 1024L * 1024 * 1024 => $"{size / (1024.0 * 1024):F1} MB",
                    _ => $"{size / (1024.0 * 1024 * 1024):F2} GB"
                };
            }
            catch { return "Unknown"; }
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
                    try { File.Delete(file); } catch { }
                }
            }
            OnPropertyChanged(nameof(CacheSizeDisplay));
        }
        catch { }
    }

    // ── Connection (requires restart) ──

    [ObservableProperty]
    private bool _autoReconnect;

    partial void OnAutoReconnectChanged(bool value)
    {
        _settingsService.Update(s => s.AutoReconnect = value);
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
        catch { }
    }

    [RelayCommand]
    private void OpenLogFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            if (Directory.Exists(LogDirectory))
                Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
        }
        catch { }
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

    private EqualizerProcessor? _equalizerProcessor;

    [ObservableProperty]
    private bool _isEqualizerEnabled;

    [ObservableProperty]
    private int _selectedEqPresetIndex;

    public void InitializeEqualizer(EqualizerProcessor processor)
    {
        _equalizerProcessor = processor;

        var s = _settingsService.Settings;
        _isEqualizerEnabled = s.EqualizerEnabled;

        // Find preset index
        _selectedEqPresetIndex = Array.IndexOf(EqPresetNames, s.EqualizerPreset);
        if (_selectedEqPresetIndex < 0) _selectedEqPresetIndex = 0;

        // Initialize bands
        EqBands.Clear();
        for (var i = 0; i < 10; i++)
        {
            var gain = i < s.EqualizerBandGains.Length ? s.EqualizerBandGains[i] : 0.0;
            var band = new EqualizerBandViewModel(i, EqFrequencies[i], EqFrequencyLabels[i], gain);
            band.GainChanged += OnBandGainChanged;
            EqBands.Add(band);
        }

        // Apply to processor
        ApplyEqToProcessor();

        OnPropertyChanged(nameof(IsEqualizerEnabled));
        OnPropertyChanged(nameof(SelectedEqPresetIndex));
    }

    partial void OnIsEqualizerEnabledChanged(bool value)
    {
        if (_equalizerProcessor != null)
            _equalizerProcessor.IsEnabled = value;
        _settingsService.Update(s => s.EqualizerEnabled = value);
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
    }

    private CancellationTokenSource? _eqRefreshCts;

    private void OnBandGainChanged(int bandIndex, double gainDb)
    {
        // Update processor band immediately (no filter rebuild yet)
        if (_equalizerProcessor != null && bandIndex < _equalizerProcessor.Bands.Count)
            _equalizerProcessor.Bands[bandIndex].GainDb = gainDb;

        // Persist
        _settingsService.Update(s =>
        {
            if (bandIndex < s.EqualizerBandGains.Length)
                s.EqualizerBandGains[bandIndex] = gainDb;
        });

        // Debounce the filter rebuild + buffer flush — preset changes fire 10 band updates,
        // so wait 50ms for them all to settle before rebuilding once
        _eqRefreshCts?.Cancel();
        _eqRefreshCts = new CancellationTokenSource();
        var token = _eqRefreshCts.Token;
        _ = Task.Delay(50, token).ContinueWith(_ =>
        {
            _equalizerProcessor?.RefreshFilters();
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    [RelayCommand]
    private void ResetEq()
    {
        SelectedEqPresetIndex = 0; // Flat
    }

    private void ApplyEqToProcessor()
    {
        if (_equalizerProcessor == null) return;

        _equalizerProcessor.IsEnabled = IsEqualizerEnabled;
        _equalizerProcessor.ClearBands();
        _equalizerProcessor.CreateGraphicEq10Band();

        // Apply persisted gains
        for (var i = 0; i < EqBands.Count && i < _equalizerProcessor.Bands.Count; i++)
            _equalizerProcessor.Bands[i].GainDb = EqBands[i].GainDb;

        _equalizerProcessor.RefreshFilters();
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

public sealed class PastLogFile
{
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string FileSize { get; init; } = "";
    public DateTime LastModified { get; init; }
}

public sealed partial class LyricsProviderItemViewModel : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public event Action? Changed;

    public LyricsProviderItemViewModel(string id, string displayName, bool isEnabled)
    {
        Id = id;
        DisplayName = displayName;
        _isEnabled = isEnabled;
    }

    partial void OnIsEnabledChanged(bool value) => Changed?.Invoke();
}
