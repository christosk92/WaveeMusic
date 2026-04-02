using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core.Http.Lyrics;

namespace Wavee.UI.WinUI.Services.Lyrics;

public sealed class MemoryLyricsCacheService : ILyricsCacheService
{
    private const int MaxEntries = 100;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public (LyricsResponse Response, Dictionary<int, List<LrcWordTiming>>? WordTimings)? TryGet(string key)
    {
        if (_cache.TryGetValue(Normalize(key), out var entry) && DateTime.UtcNow - entry.Created < Ttl)
            return (entry.Response, entry.WordTimings);

        return null;
    }

    public void Set(string key, LyricsResponse response, Dictionary<int, List<LrcWordTiming>>? wordTimings)
    {
        var normalized = Normalize(key);
        _cache[normalized] = new CacheEntry(response, wordTimings, DateTime.UtcNow);

        // Evict oldest entries if over capacity
        if (_cache.Count > MaxEntries)
        {
            var oldest = _cache
                .OrderBy(x => x.Value.Created)
                .Take(_cache.Count - MaxEntries)
                .Select(x => x.Key)
                .ToList();

            foreach (var k in oldest)
                _cache.TryRemove(k, out _);
        }
    }

    private static string Normalize(string key) => key.ToLowerInvariant().Trim();

    private sealed record CacheEntry(
        LyricsResponse Response,
        Dictionary<int, List<LrcWordTiming>>? WordTimings,
        DateTime Created);
}
