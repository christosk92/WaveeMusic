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
public sealed class PageHostCacheCleanupAdapter : ICleanableCache
{
    public string CacheName => "PageHostCache";

    public int CurrentCount
    {
        get
        {
            try
            {
                var dispatcher = MainWindow.Instance.DispatcherQueue;
                if (dispatcher is null || !dispatcher.HasThreadAccess)
                    return 0;

                return ShellViewModel.TabInstances.Sum(tab => tab.ContentHost.CachedPageCount);
            }
            catch
            {
                return 0;
            }
        }
    }

    public Task<int> CleanupStaleEntriesAsync(TimeSpan maxAge, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> ClearAsync(CancellationToken ct = default)
        => RunOnUiThreadAsync(DropCollapsedPageCaches, ct);

    private static int DropCollapsedPageCaches()
    {
        var dropped = 0;
        foreach (var tab in ShellViewModel.TabInstances.ToArray())
        {
            try
            {
                if (tab.IsSleeping)
                    continue;

                var host = tab.ContentHost;
                var before = host.CachedPageCount;
                var prior = host.CacheSize;
                host.CacheSize = 0;
                host.CacheSize = prior;
                dropped += Math.Max(0, before - host.CachedPageCount);
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
