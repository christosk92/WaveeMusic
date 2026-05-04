using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wavee.Core.Library.Local;
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

    public LocalFilesViewModel(ILocalLibraryService service, ILogger<LocalFilesViewModel>? logger = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException("LocalFilesViewModel must be constructed on a UI thread.");

        _progressSub = _service.SyncProgress.Subscribe(OnProgress);
        _ = LoadFoldersAsync();
    }

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
    }
}

public sealed partial class LocalFolderRow : ObservableObject
{
    public required int Id { get; init; }
    public required string Path { get; init; }
    public required int FileCount { get; init; }
    public required string LastScanDisplay { get; init; }
    public required string StatusDisplay { get; init; }

    public static LocalFolderRow From(LocalLibraryFolder f)
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
        return new LocalFolderRow
        {
            Id = f.Id,
            Path = f.Path,
            FileCount = f.FileCount,
            LastScanDisplay = lastScan,
            StatusDisplay = status,
        };
    }
}
