using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Lets the memory budget shed collapsed PageHost cache entries. Active pages
/// stay mounted; only hidden cached pages are evicted.
/// </summary>
[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class PageHostCacheCleanupAdapter : ICleanableCache
{
    private const int WarmCollapsedPagesToKeepPerTab = 1;
    private int _lastKnownCount;

    public string CacheName => "PageHostCache";

    public int CurrentCount
    {
        get
        {
            try
            {
                var dispatcher = MainWindow.Instance.DispatcherQueue;
                if (dispatcher is null || !dispatcher.HasThreadAccess)
                    return Volatile.Read(ref _lastKnownCount);

                var count = CountCachedPages();
                Volatile.Write(ref _lastKnownCount, count);
                return count;
            }
            catch
            {
                return Volatile.Read(ref _lastKnownCount);
            }
        }
    }

    public Task<int> CleanupStaleEntriesAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        _ = maxAge;
        return RunOnUiThreadAsync(DropOlderCollapsedPageCaches, ct);
    }

    public Task<int> ClearAsync(CancellationToken ct = default)
        => RunOnUiThreadAsync(DropCollapsedPageCaches, ct);

    private static int CountCachedPages()
        => ShellViewModel.TabInstances.Sum(tab => tab.ContentHost.CachedPageCount);

    private int DropOlderCollapsedPageCaches()
    {
        var dropped = DropCollapsedPageCaches(keepNewestCollapsed: WarmCollapsedPagesToKeepPerTab);
        Volatile.Write(ref _lastKnownCount, CountCachedPages());
        return dropped;
    }

    private static int DropCollapsedPageCaches()
        => DropCollapsedPageCaches(keepNewestCollapsed: 0);

    private static int DropCollapsedPageCaches(int keepNewestCollapsed)
    {
        var dropped = 0;
        foreach (var tab in ShellViewModel.TabInstances.ToArray())
        {
            try
            {
                dropped += tab.ContentHost.EvictCollapsedPages(keepNewestCollapsed);
            }
            catch
            {
                // Best-effort pressure cleanup.
            }
        }

        return dropped;
    }

    private static Task<int> RunOnUiThreadAsync(Func<int> action, CancellationToken ct)
    {
        var dispatcher = MainWindow.Instance.DispatcherQueue;
        if (dispatcher is null)
            return Task.FromResult(0);
        if (dispatcher.HasThreadAccess)
            return Task.FromResult(action());

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(ct);
                        return;
                    }

                    tcs.TrySetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            return Task.FromResult(0);
        }

        return tcs.Task;
    }
}
