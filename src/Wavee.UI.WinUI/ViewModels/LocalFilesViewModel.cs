using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.Local;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Wavee.UI.WinUI.ViewModels;

/// <summary>
/// View-model backing the "Local Files" section in the Storage &amp; Network
/// settings page. Wraps <see cref="ILocalLibraryService"/>: list folders,
/// add/remove/toggle/rescan, observe sync progress.
/// </summary>
public sealed partial class LocalFilesViewModel : ObservableObject, IDisposable
{
    private readonly ILocalLibraryService _service;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger? _logger;
    private readonly IDisposable _progressSub;

    public ObservableCollection<LocalFolderRow> Folders { get; } = new();

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private int _progressTotal;

    [ObservableProperty]
    private int _progressDone;

    [ObservableProperty]
    private string? _progressCurrentFile;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Two-way bound to the Settings "Online metadata lookups" toggle. Flipping
    /// it calls Pause/Resume on <see cref="Wavee.Local.Enrichment.ILocalEnrichmentService"/>.
    /// Defaults to whatever the enrichment service reports (false when stub
    /// secrets prevent it from running).
    /// </summary>
    public bool EnrichmentEnabled
    {
        get => _enrichmentEnabled;
        set
        {
            if (_enrichmentEnabled == value) return;
            _enrichmentEnabled = value;
            OnPropertyChanged();
            _ = ApplyEnrichmentEnabledAsync(value);
        }
    }
    private bool _enrichmentEnabled;

    private static async Task ApplyEnrichmentEnabledAsync(bool enabled)
    {
        var svc = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.Enrichment.ILocalEnrichmentService>();
        if (svc is null) return;
        if (enabled) await svc.ResumeAsync();
        else         await svc.PauseAsync();
    }

    // ── TMDB integration — BYO token ──────────────────────────────────────

    /// <summary>The candidate token typed into the Settings PasswordBox.
    /// Cleared on successful Verify (we persist via the token store instead).</summary>
    [ObservableProperty]
    private string _tmdbTokenInput = string.Empty;

    /// <summary>True iff a token is currently stored in the DPAPI blob —
    /// drives the status pill, the Clear button's visibility, and every
    /// "Sync with TMDB" / "Set up TMDB" CTA across the app.</summary>
    [ObservableProperty]
    private bool _isTokenConfigured;

    [ObservableProperty]
    private TmdbStatus _tmdbStatus = TmdbStatus.NotConfigured;

    /// <summary>Optional human-readable status (e.g. error message from a
    /// failed Verify call). Shown next to the status pill.</summary>
    [ObservableProperty]
    private string? _tmdbStatusMessage;

    [RelayCommand]
    private async Task VerifyTokenAsync()
    {
        var token = TmdbTokenInput?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            TmdbStatus = TmdbStatus.NotConfigured;
            TmdbStatusMessage = "Paste a TMDB v4 read-access token to continue.";
            return;
        }

        TmdbStatus = TmdbStatus.Testing;
        TmdbStatusMessage = null;

        var http = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<System.Net.Http.IHttpClientFactory>()?.CreateClient("enrichment")
            ?? new System.Net.Http.HttpClient();
        var (ok, error) = await Wavee.Local.Enrichment.TmdbAdapter.VerifyAsync(
            token, http, System.Threading.CancellationToken.None);
        if (!ok)
        {
            TmdbStatus = TmdbStatus.InvalidToken;
            TmdbStatusMessage = error ?? "Token validation failed.";
            return;
        }

        var tokenStore = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore is null)
        {
            TmdbStatus = TmdbStatus.InvalidToken;
            TmdbStatusMessage = "Token store unavailable.";
            return;
        }

        await tokenStore.SetTokenAsync(token);
        TmdbStatus = TmdbStatus.Connected;
        TmdbStatusMessage = null;
        TmdbTokenInput = string.Empty; // wipe the textbox; the real value lives in DPAPI now
        // Explicit-sync model: do NOT auto-enqueue. The user clicks Sync
        // on Shows / Movies / detail pages (or "Run now" below) when they
        // want enrichment to actually happen.
    }

    [RelayCommand]
    private async Task ClearTokenAsync()
    {
        var tokenStore = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore is null) return;
        await tokenStore.SetTokenAsync(null);
        TmdbTokenInput = string.Empty;
        TmdbStatus = TmdbStatus.NotConfigured;
        TmdbStatusMessage = null;
    }

    public LocalFilesViewModel(ILocalLibraryService service, ILogger<LocalFilesViewModel>? logger = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException("LocalFilesViewModel must be constructed on a UI thread.");

        _progressSub = _service.SyncProgress.Subscribe(OnProgress);

        // Seed TMDB state from the token store + subscribe to changes so
        // every external Set/Clear flows back to the UI.
        var tokenStore = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.Enrichment.ITmdbTokenStore>();
        if (tokenStore is not null)
        {
            _isTokenConfigured = tokenStore.HasToken;
            _tmdbStatus = _isTokenConfigured ? TmdbStatus.Connected : TmdbStatus.NotConfigured;
            _tokenStoreSub = tokenStore.HasTokenChanged.Subscribe(present =>
                _dispatcher.TryEnqueue(() =>
                {
                    IsTokenConfigured = present;
                    if (!present) { TmdbStatus = TmdbStatus.NotConfigured; TmdbStatusMessage = null; }
                }));
        }

        _ = LoadFoldersAsync();
    }

    private IDisposable? _tokenStoreSub;

    public bool IsServiceAvailable => _service is not null;

    private void OnProgress(LocalSyncProgress p)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ProgressTotal = p.TotalFiles;
            ProgressDone = p.ProcessedFiles;
            ProgressCurrentFile = p.CurrentPath is null ? null : Path.GetFileName(p.CurrentPath);
            IsScanning = _service.IsScanning;

            // Final tick (CurrentPath is null) ⇒ scan finished. Always write a
            // status so the UI doesn't get stuck on a stale "Scanning…" string
            // when the folder turns out to be empty / unsupported / OneDrive-
            // dehydrated and never produces a per-file progress event.
            if (p.CurrentPath is null)
            {
                StatusMessage = p.TotalFiles == 0
                    ? "No supported audio files found."
                    : $"Scanned {p.TotalFiles} files.";
            }
            else if (p.ProcessedFiles >= p.TotalFiles && p.TotalFiles > 0)
            {
                StatusMessage = $"Scanned {p.TotalFiles} files.";
            }
        });
    }

    private async Task LoadFoldersAsync()
    {
        try
        {
            var rows = await _service.GetWatchedFoldersAsync();
            _dispatcher.TryEnqueue(() =>
            {
                Folders.Clear();
                foreach (var f in rows)
                    Folders.Add(LocalFolderRow.From(f));
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load watched folders");
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync(XamlRoot? xamlRoot)
    {
        try
        {
            // FolderPicker — WinUI 3 requires a window handle.
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary,
            };
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            await _service.AddWatchedFolderAsync(folder.Path, includeSubfolders: true);
            await LoadFoldersAsync();
            StatusMessage = $"Added {folder.Path}";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Add folder failed");
            StatusMessage = "Could not add folder: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(LocalFolderRow? row)
    {
        if (row is null) return;
        try
        {
            await _service.RemoveWatchedFolderAsync(row.Id);
            Folders.Remove(row);
            StatusMessage = $"Removed {row.Path}";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Remove folder failed");
            StatusMessage = "Could not remove folder: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RescanAllAsync()
    {
        try
        {
            StatusMessage = "Scanning…";
            await _service.TriggerRescanAsync();
            await LoadFoldersAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Rescan failed");
            StatusMessage = "Rescan failed: " + ex.Message;
        }
    }

    public void Dispose()
    {
        _progressSub.Dispose();
        _tokenStoreSub?.Dispose();
    }
}

/// <summary>UI status for the TMDB integration card.</summary>
public enum TmdbStatus
{
    NotConfigured = 0,
    Testing = 1,
    Connected = 2,
    InvalidToken = 3,
}

public sealed partial class LocalFolderRow : ObservableObject
{
    public required int Id { get; init; }
    public required string Path { get; init; }
    public required int FileCount { get; init; }
    public required string LastScanDisplay { get; init; }
    public required string StatusDisplay { get; init; }

    /// <summary>
    /// Index into the per-folder kind-hint ComboBox in Settings:
    /// 0=Auto, 1=Music, 2=MusicVideo, 3=TvEpisode, 4=Movie, 5=Other.
    /// Setter writes to the watched-folder row via
    /// <see cref="Wavee.Local.ILocalLibraryService.SetWatchedFolderExpectedKindAsync"/>.
    /// </summary>
    private int _expectedKindIndex;
    public int ExpectedKindIndex
    {
        get => _expectedKindIndex;
        set
        {
            if (_expectedKindIndex == value) return;
            _expectedKindIndex = value;
            OnPropertyChanged();
            _ = PersistExpectedKindAsync(value);
        }
    }

    private async Task PersistExpectedKindAsync(int comboIndex)
    {
        var service = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<Wavee.Local.ILocalLibraryService>();
        if (service is null) return;
        Wavee.Local.Classification.LocalContentKind? kind = comboIndex switch
        {
            1 => Wavee.Local.Classification.LocalContentKind.Music,
            2 => Wavee.Local.Classification.LocalContentKind.MusicVideo,
            3 => Wavee.Local.Classification.LocalContentKind.TvEpisode,
            4 => Wavee.Local.Classification.LocalContentKind.Movie,
            5 => Wavee.Local.Classification.LocalContentKind.Other,
            _ => null,
        };
        await service.SetWatchedFolderExpectedKindAsync(Id, kind);
    }

    public static LocalFolderRow From(LocalLibraryFolder f, int expectedKindIndex = 0)
    {
        var status = f.LastScanStatus switch
        {
            "ok" => $"OK · {f.FileCount} files",
            "partial" => $"Partial · {f.FileCount} files · {f.LastScanError}",
            "error" => $"Error · {f.LastScanError}",
            _ => f.FileCount > 0 ? $"{f.FileCount} files" : "Pending scan…",
        };
        var lastScan = f.LastScanAt is { } unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("g")
            : "Never";
        var row = new LocalFolderRow
        {
            Id = f.Id,
            Path = f.Path,
            FileCount = f.FileCount,
            LastScanDisplay = lastScan,
            StatusDisplay = status,
        };
        // Seed the index without triggering the DB-write setter — direct field
        // assignment skips PersistExpectedKindAsync. (Round-trip from the saved
        // value requires extending LocalLibraryFolder + GetWatchedFoldersAsync
        // with the expected_kind column; until that lands the dropdown starts
        // at Auto each app launch and user selections still persist.)
        row._expectedKindIndex = expectedKindIndex;
        return row;
    }
}
