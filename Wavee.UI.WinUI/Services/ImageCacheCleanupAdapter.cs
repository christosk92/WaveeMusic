using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Storage.Abstractions;
using Wavee.UI.WinUI.Converters;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Bridges the static StringToImageSourceConverter cache to ICleanableCache
/// for background cleanup. Needed because the converter is instantiated by XAML,
/// not by DI.
/// </summary>
public sealed class ImageCacheCleanupAdapter : ICleanableCache
{
    public string CacheName => "ImageBitmapCache";

    public int CurrentCount => StringToImageSourceConverter.CacheCount;

    public Task<int> CleanupStaleEntriesAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var removed = StringToImageSourceConverter.CleanupStaleEntries(maxAge);
        return Task.FromResult(removed);
    }
}
