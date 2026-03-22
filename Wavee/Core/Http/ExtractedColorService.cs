using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;

namespace Wavee.Core.Http;

public sealed class ExtractedColorService
{
    private readonly PathfinderClient _pathfinder;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, ExtractedColor> _cache = new();

    public ExtractedColorService(PathfinderClient pathfinder, ILogger? logger = null)
    {
        _pathfinder = pathfinder;
        _logger = logger;
    }

    public async Task<ExtractedColor?> GetColorAsync(string imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(imageUrl)) return null;
        if (_cache.TryGetValue(imageUrl, out var cached)) return cached;

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
            if (_cache.TryGetValue(url, out var cached))
                result[url] = cached;
            else
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

                        _cache.TryAdd(missing[i], color);
                        result[missing[i]] = color;
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

    public void ClearCache() => _cache.Clear();
}
