using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Local;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Windows-shell-backed implementation of <see cref="IVideoThumbnailExtractor"/>.
/// Delegates to <see cref="StorageFile.GetThumbnailAsync(ThumbnailMode, uint, ThumbnailOptions)"/>
/// in <see cref="ThumbnailMode.VideosView"/>, which returns the same frame
/// thumbnail Windows Explorer uses for a video file.
/// </summary>
/// <remarks>
/// The scanner pipeline is synchronous on a background thread, so blocking
/// on the async StorageFile call is safe — there's no UI sync context to
/// deadlock on. We still wrap in try/catch and return null on any failure
/// (file unreachable, codec missing, RPC error, etc.) so a single bad file
/// can't poison the whole scan.
/// </remarks>
public sealed class WindowsVideoThumbnailExtractor : IVideoThumbnailExtractor
{
    private const uint RequestedSize = 256;
    private readonly ILogger<WindowsVideoThumbnailExtractor>? _logger;

    public WindowsVideoThumbnailExtractor(ILogger<WindowsVideoThumbnailExtractor>? logger = null)
    {
        _logger = logger;
    }

    public byte[]? Extract(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var result = ExtractAsync(path).GetAwaiter().GetResult();
            if (result is null || result.Length == 0)
                _logger?.LogInformation("[thumb] no shell thumbnail available for {Path}", path);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[thumb] video thumbnail extraction failed for {Path}", path);
            return null;
        }
    }

    private static async Task<byte[]?> ExtractAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);

        // Try VideosView first — fast, uses Windows' cached shell thumbnail
        // if one exists. For screen recordings and HEVC .mkv files Windows
        // often returns empty here because no cached thumb was generated.
        var bytes = await TryGetThumbAsync(file, ThumbnailMode.VideosView).ConfigureAwait(false);
        if (bytes is { Length: > 0 }) return bytes;

        // Fallback: SingleItem mode is more aggressive — it forces frame
        // extraction on-demand rather than relying on the shell index.
        bytes = await TryGetThumbAsync(file, ThumbnailMode.SingleItem).ConfigureAwait(false);
        return bytes;
    }

    private static async Task<byte[]?> TryGetThumbAsync(StorageFile file, ThumbnailMode mode)
    {
        using var thumb = await file.GetThumbnailAsync(
            mode,
            RequestedSize,
            ThumbnailOptions.UseCurrentScale);
        if (thumb is null || thumb.Size == 0) return null;

        // StorageItemThumbnail is an IRandomAccessStreamWithContentType.
        // AsStreamForRead() bridges to System.IO.Stream so we can copy it
        // into a byte[] cleanly — Windows hands us a JPEG by default.
        using var ms = new MemoryStream();
        using var input = thumb.AsStreamForRead();
        await input.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }
}
