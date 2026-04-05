using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Bridges the shared <see cref="ImageCacheService"/> to <see cref="ICleanableCache"/>
/// for background cleanup.
/// </summary>
public sealed class ImageCacheCleanupAdapter : ICleanableCache
{
    public string CacheName => "ImageBitmapCache";

    public int CurrentCount => Ioc.Default.GetService<ImageCacheService>()?.Count ?? 0;

    public Task<int> CleanupStaleEntriesAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var removed = Ioc.Default.GetService<ImageCacheService>()?.CleanupStale(maxAge) ?? 0;
        return Task.FromResult(removed);
    }
}
