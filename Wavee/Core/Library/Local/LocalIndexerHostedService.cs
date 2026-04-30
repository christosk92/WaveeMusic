using Microsoft.Extensions.Logging;

namespace Wavee.Core.Library.Local;

/// <summary>
/// Long-lived component that drives the local indexer for the lifetime of the
/// app. On start: runs an initial scan and wires <see cref="LocalFolderWatcher"/>
/// for live updates. A periodic timer (default 6 h) re-scans to pick up changes
/// the watcher missed (e.g. files added while the app was closed, network
/// shares with no notification support).
///
/// <para>
/// Not bound to any DI lifetime contract — start it explicitly from app
/// composition with <see cref="StartAsync"/> and dispose at shutdown.
/// </para>
/// </summary>
public sealed class LocalIndexerHostedService : IDisposable, IAsyncDisposable
{
    private readonly LocalLibraryService _service;
    private readonly LocalFolderWatcher _watcher;
    private readonly ILogger? _logger;
    private readonly TimeSpan _periodicInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _periodicTask;

    public LocalIndexerHostedService(
        LocalLibraryService service,
        LocalFolderWatcher watcher,
        ILogger? logger = null,
        TimeSpan? periodicInterval = null)
    {
        _service = service;
        _watcher = watcher;
        _logger = logger;
        _periodicInterval = periodicInterval ?? TimeSpan.FromHours(6);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var folders = await _service.GetWatchedFoldersAsync(ct);
        foreach (var folder in folders)
        {
            if (folder.Enabled) _watcher.StartFolder(folder);
        }

        // Background: initial scan, then periodic re-scan loop.
        _periodicTask = Task.Run(async () =>
        {
            try
            {
                await _service.RunScanAsync(folderId: null, _cts.Token);
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(_periodicInterval, _cts.Token);
                    await _service.RunScanAsync(folderId: null, _cts.Token);
                }
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Local indexer periodic loop failed");
            }
        }, _cts.Token);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _watcher.Dispose(); } catch { /* ignore */ }
        try { _periodicTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _watcher.Dispose(); } catch { /* ignore */ }
        if (_periodicTask is not null)
        {
            try { await _periodicTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}
