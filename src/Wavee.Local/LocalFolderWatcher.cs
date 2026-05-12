using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Wavee.Local;

/// <summary>
/// One <see cref="FileSystemWatcher"/> per watched folder. Events are debounced
/// for 500 ms via a single-consumer channel — multiple raw events for the same
/// path coalesce into one indexer call. Only fires while Wavee is running;
/// gaps are filled by the next startup scan.
/// </summary>
public sealed class LocalFolderWatcher : IDisposable
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".m4b", ".mp4", ".mov", ".aac",
        ".ogg", ".opus", ".wma", ".aiff", ".aif",
    };

    private readonly ILocalLibraryService _service;
    private readonly ILogger? _logger;
    private readonly Dictionary<int, FileSystemWatcher> _watchers = new();
    private readonly Channel<WatchEvent> _events;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;
    private readonly Dictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _seenLock = new();
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    public LocalFolderWatcher(ILocalLibraryService service, ILogger? logger = null)
    {
        _service = service;
        _logger = logger;
        _events = Channel.CreateUnbounded<WatchEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _consumerTask = Task.Run(ConsumeLoopAsync);
    }

    public void StartFolder(LocalLibraryFolder folder)
    {
        if (_watchers.ContainsKey(folder.Id)) return;
        if (!Directory.Exists(folder.Path))
        {
            _logger?.LogWarning("Cannot watch missing folder: {Path}", folder.Path);
            return;
        }

        try
        {
            var w = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = folder.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            w.Created += (_, e) => Enqueue(WatchEventKind.ChangedOrCreated, e.FullPath);
            w.Changed += (_, e) => Enqueue(WatchEventKind.ChangedOrCreated, e.FullPath);
            w.Deleted += (_, e) => Enqueue(WatchEventKind.Deleted, e.FullPath);
            w.Renamed += (_, e) =>
            {
                Enqueue(WatchEventKind.Deleted, e.OldFullPath);
                Enqueue(WatchEventKind.ChangedOrCreated, e.FullPath);
            };
            w.Error += (_, e) => _logger?.LogWarning(e.GetException(), "FileSystemWatcher error on {Path}", folder.Path);

            _watchers[folder.Id] = w;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create watcher for {Path}", folder.Path);
        }
    }

    public void StopFolder(int folderId)
    {
        if (_watchers.Remove(folderId, out var w))
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Watcher dispose failed"); }
        }
    }

    private void Enqueue(WatchEventKind kind, string path)
    {
        if (!AudioExtensions.Contains(Path.GetExtension(path))) return;
        lock (_seenLock)
            _lastSeen[path] = DateTime.UtcNow;
        _events.Writer.TryWrite(new WatchEvent(kind, path));
    }

    private async Task ConsumeLoopAsync()
    {
        try
        {
            await foreach (var evt in _events.Reader.ReadAllAsync(_cts.Token))
            {
                // Debounce: if another event arrived for this path within the last DebounceWindow,
                // skip and let the most recent event handler process it after the window settles.
                DateTime stamp;
                lock (_seenLock)
                {
                    if (!_lastSeen.TryGetValue(evt.Path, out stamp)) continue;
                }

                var elapsed = DateTime.UtcNow - stamp;
                if (elapsed < DebounceWindow)
                {
                    await Task.Delay(DebounceWindow - elapsed, _cts.Token);
                    lock (_seenLock)
                    {
                        if (!_lastSeen.TryGetValue(evt.Path, out var current)) continue;
                        if (current != stamp) continue; // newer event arrived; let that one win.
                        _lastSeen.Remove(evt.Path);
                    }
                }
                else
                {
                    lock (_seenLock) _lastSeen.Remove(evt.Path);
                }

                try
                {
                    if (evt.Kind == WatchEventKind.Deleted)
                        await _service.NotifyFileDeletedAsync(evt.Path, _cts.Token);
                    else
                        await _service.NotifyFileChangedAsync(evt.Path, _cts.Token);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Watcher dispatch failed for {Path}", evt.Path);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var w in _watchers.Values)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* ignore */ }
        }
        _watchers.Clear();
        _events.Writer.TryComplete();
        try { _consumerTask.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        _cts.Dispose();
    }

    private enum WatchEventKind { ChangedOrCreated, Deleted }
    private sealed record WatchEvent(WatchEventKind Kind, string Path);
}
