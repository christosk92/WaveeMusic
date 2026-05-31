using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;
using Wavee.UI.Contracts;

namespace Wavee.UI.Services.Tracks;

/// <summary>
/// Builds the <see cref="ArtistSpotlight"/> from Spotify's NPV artist+track query
/// (<see cref="ITrackDetailsService.GetTrackDetailsAsync"/>). Results are cached in-process by track
/// URI (so a swipe back is instant), and <see cref="PrefetchAsync"/> warms the next cards. Pure
/// service layer — no UI, no I/O beyond the details service it delegates to.
/// </summary>
public sealed class ArtistSpotlightResolver : IArtistSpotlightResolver
{
    private const int MaxCredits = 6;

    private readonly ITrackDetailsService _details;
    private readonly ILogger<ArtistSpotlightResolver>? _logger;
    private readonly ConcurrentDictionary<string, ArtistSpotlight?> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(2);

    public ArtistSpotlightResolver(ITrackDetailsService details, ILogger<ArtistSpotlightResolver>? logger = null)
    {
        _details = details;
        _logger = logger;
    }

    public async Task<ArtistSpotlight?> ResolveAsync(string trackUri, string? artistUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri) || string.IsNullOrEmpty(artistUri)) return null;
        if (_cache.TryGetValue(trackUri, out var cached)) return cached;

        // The playlist DTO may carry a bare artist id or a full spotify: URI — NPV wants a URI.
        var normalizedArtistUri = artistUri.StartsWith("spotify:", StringComparison.Ordinal)
            ? artistUri
            : $"spotify:artist:{artistUri}";

        ArtistSpotlight? spotlight = null;
        try
        {
            var response = await _details.GetTrackDetailsAsync(normalizedArtistUri, trackUri, ct: ct).ConfigureAwait(false);
            spotlight = Map(response);
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Artist spotlight resolve failed for {Uri}", trackUri); }

        _cache[trackUri] = spotlight;   // cache null too — don't re-query a failed/empty track
        return spotlight;
    }

    public ArtistSpotlight? TryPeek(string trackUri)
        => !string.IsNullOrEmpty(trackUri) && _cache.TryGetValue(trackUri, out var s) ? s : null;

    public async Task PrefetchAsync(IReadOnlyList<(string TrackUri, string? ArtistUri)> tracks, CancellationToken ct = default)
    {
        var tasks = tracks
            .Where(t => !string.IsNullOrEmpty(t.TrackUri) && !string.IsNullOrEmpty(t.ArtistUri) && !_cache.ContainsKey(t.TrackUri))
            .Select(async t =>
            {
                try
                {
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try { await ResolveAsync(t.TrackUri, t.ArtistUri, ct).ConfigureAwait(false); }
                    finally { _gate.Release(); }
                }
                catch { /* never throw from prefetch */ }
            });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static ArtistSpotlight? Map(NpvArtistResponse? response)
    {
        var artist = response?.Data?.ArtistUnion;
        if (artist is null) return null;

        var name = artist.Profile?.Name;
        if (string.IsNullOrEmpty(name)) return null;

        var credits = (response?.Data?.TrackUnion?.CreditsTrait?.Contributors?.Items ?? new List<NpvContributor>())
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .Take(MaxCredits)
            .Select(c => new ArtistCredit(c.Name!, c.Role ?? c.RoleGroup?.Name ?? ""))
            .ToList();

        var canvasUrl = response?.Data?.TrackUnion?.Canvas?.Url;
        if (!string.IsNullOrEmpty(canvasUrl) && !canvasUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            canvasUrl = null;

        return new ArtistSpotlight(
            ArtistName: name!,
            AvatarUrl: PickAvatar(artist.Visuals?.AvatarImage?.Sources),
            MonthlyListeners: artist.Stats?.MonthlyListeners ?? 0,
            Bio: StripHtml(artist.Profile?.Biography?.Text),
            Credits: credits,
            CanvasUrl: canvasUrl);
    }

    /// <summary>Pick a ~mid-size avatar (≈240 px) for the circular header, falling back to any URL.</summary>
    private static string? PickAvatar(List<ArtistImageSource>? sources)
    {
        if (sources is not { Count: > 0 }) return null;
        var withUrl = sources.Where(s => !string.IsNullOrEmpty(s.Url)).ToList();
        if (withUrl.Count == 0) return null;
        var best = withUrl
            .OrderBy(s => Math.Abs((s.Width ?? 320) - 240))
            .First();
        return best.Url;
    }

    /// <summary>Spotify bios may contain a little HTML (&lt;a&gt; / &lt;b&gt;); strip tags for plain display.</summary>
    private static string? StripHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (text.IndexOf('<') < 0) return text.Trim();
        var sb = new StringBuilder(text.Length);
        var inTag = false;
        foreach (var ch in text)
        {
            if (ch == '<') inTag = true;
            else if (ch == '>') inTag = false;
            else if (!inTag) sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
