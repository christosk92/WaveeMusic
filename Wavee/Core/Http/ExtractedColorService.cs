using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Http;

/// <summary>
/// Extracted color service with 3-tier caching: hot (in-memory) → SQLite → API.
/// Singleton — shared across all pages, persists across navigation.
/// </summary>
public sealed class ExtractedColorService : IColorService
{
    private const int MaxHotCacheSize = 500;

    private readonly IPathfinderClient _pathfinder;
    private readonly IMetadataDatabase _db;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ExtractedColor> _hot = new();

    public ExtractedColorService(IPathfinderClient pathfinder, IMetadataDatabase db, ILogger? logger = null)
    {
        _pathfinder = pathfinder;
        _db = db;
        _logger = logger;
    }

    public async Task<ExtractedColor?> GetColorAsync(string imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(imageUrl)) return null;

        // 1. Hot cache
        if (_hot.TryGetValue(imageUrl, out var cached)) return cached;

        // 2. SQLite
        try
        {
            var dbResult = await _db.GetColorCacheAsync(imageUrl, ct);
            if (dbResult.HasValue)
            {
                var color = new ExtractedColor(dbResult.Value.DarkHex, dbResult.Value.LightHex, dbResult.Value.RawHex);
                TryAddBounded(imageUrl, color);
                return color;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "SQLite color cache read failed for {Url}", imageUrl);
        }

        // 3. API
        var colors = await GetColorsAsync([imageUrl], ct);
        return colors.GetValueOrDefault(imageUrl);
    }

    public async Task<Dictionary<string, ExtractedColor>> GetColorsAsync(
        IReadOnlyList<string> imageUrls, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ExtractedColor>();
        var missing = new List<string>();

        foreach (var url in imageUrls)
        {
            if (_hot.TryGetValue(url, out var cached))
            {
                result[url] = cached;
                continue;
            }

            // Check SQLite
            try
            {
                var dbResult = await _db.GetColorCacheAsync(url, ct);
                if (dbResult.HasValue)
                {
                    var color = new ExtractedColor(dbResult.Value.DarkHex, dbResult.Value.LightHex, dbResult.Value.RawHex);
                    TryAddBounded(url, color);
                    result[url] = color;
                    continue;
                }
            }
            catch { /* fall through to API */ }

            missing.Add(url);
        }

        if (missing.Count > 0)
        {
            try
            {
                var response = await _pathfinder.GetExtractedColorsAsync(missing, ct);
                var entries = response.Data?.ExtractedColors;

                if (entries != null)
                {
                    for (int i = 0; i < Math.Min(missing.Count, entries.Count); i++)
                    {
                        var entry = entries[i];
                        var color = new ExtractedColor(
                            entry.ColorDark?.Hex,
                            entry.ColorLight?.Hex,
                            entry.ColorRaw?.Hex);

                        TryAddBounded(missing[i], color);
                        result[missing[i]] = color;

                        // Persist to SQLite (fire-and-forget, don't block the caller)
                        var url = missing[i];
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _db.SetColorCacheAsync(url, color.DarkHex, color.LightHex, color.RawHex);
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogDebug(ex, "Failed to persist color to SQLite for {Url}", url);
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch extracted colors for {Count} images", missing.Count);
            }
        }

        return result;
    }

    /// <summary>
    /// Adds to hot cache with bounded eviction. When the cache exceeds MaxHotCacheSize,
    /// it is cleared entirely — colors are cheap to re-fetch from SQLite (tier 2).
    /// </summary>
    private void TryAddBounded(string key, ExtractedColor color)
    {
        if (_hot.Count >= MaxHotCacheSize)
            _hot.Clear();

        _hot.TryAdd(key, color);
    }
}
