using System;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// A GPU-resident cached image. Owns a <see cref="LoadedImageSurface"/> that
/// can be consumed via <see cref="CompositionSurfaceBrush"/>. No CPU-side
/// decoded bitmap is retained — once the surface uploads to the GPU, the
/// decoded pixels are released.
///
/// <para>
/// Lifetime is owned by <see cref="ImageCacheService"/>. Consumers never call
/// <see cref="Dispose"/> directly; the cache disposes the surface on LRU
/// eviction, <see cref="ImageCacheService.ClearUnpinned"/>, or hard
/// <see cref="ImageCacheService.Clear"/>.
/// </para>
/// </summary>
public sealed class CachedImage : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// The GPU-backed surface. Pass to <c>compositor.CreateSurfaceBrush(surface)</c>
    /// to bind it to a <c>SpriteVisual</c>.
    /// </summary>
    public LoadedImageSurface Surface { get; }

    /// <summary>
    /// Source URL the surface was loaded from (already https-resolved by
    /// <c>SpotifyImageHelper.ToHttpsUrl</c> before the cache hit).
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Decode bucket (64 / 128 / 256 / 512) the surface was sized for.
    /// </summary>
    public int DecodePixelSize { get; }

    /// <summary>
    /// True once <see cref="LoadedImageSurface.LoadCompleted"/> has fired
    /// successfully. Driven by the cache; consumers read this to decide
    /// whether to show a placeholder.
    /// </summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    /// True if the last load attempt failed (network / decode error). The
    /// cache invalidates failed entries on demand so the next
    /// <see cref="ImageCacheService.GetOrCreate"/> retries.
    /// </summary>
    public bool LoadFailed { get; private set; }

    /// <summary>
    /// Natural decode size reported by <see cref="LoadedImageSurface.NaturalSize"/>.
    /// Default to <c>Size.Empty</c> until the load completes; consumers
    /// usually don't need this and let Composition stretch handle layout.
    /// </summary>
    public Size NaturalSize { get; private set; }

    /// <summary>
    /// Raised once when the surface load finishes (success or failure).
    /// Subsequent subscribers fire synchronously if already loaded.
    /// </summary>
    public event EventHandler? LoadCompleted;

    internal CachedImage(LoadedImageSurface surface, string url, int decodePixelSize)
    {
        Surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Url = url;
        DecodePixelSize = decodePixelSize;
        surface.LoadCompleted += OnSurfaceLoadCompleted;
    }

    private void OnSurfaceLoadCompleted(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs args)
    {
        if (args.Status == LoadedImageSourceLoadStatus.Success)
        {
            IsLoaded = true;
            try { NaturalSize = sender.NaturalSize; }
            catch { /* surface may already be disposed during shutdown */ }
        }
        else
        {
            LoadFailed = true;
        }

        var subs = LoadCompleted?.GetInvocationList()?.Length ?? 0;
        var urlTail = Url.Length > 18 ? "…" + Url[^18..] : Url;
        System.Diagnostics.Debug.WriteLine(
            $"[CachedImg] LoadCompleted status={args.Status} subscribers={subs} url={urlTail}");

        try { LoadCompleted?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CachedImg] LoadCompleted invoke threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds a handler and fires it synchronously if the surface is already
    /// loaded. Used by <c>CompositionImage</c> to avoid the cache-hit
    /// hang-at-placeholder bug where consumers subscribe AFTER load completes.
    /// </summary>
    public void AddLoadCompletedHandler(EventHandler handler)
    {
        LoadCompleted += handler;
        if (IsLoaded || LoadFailed)
            handler(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Surface.LoadCompleted -= OnSurfaceLoadCompleted; }
        catch { }
        try { Surface.Dispose(); }
        catch { /* composition can be torn down during window close */ }
    }
}
