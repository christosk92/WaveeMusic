using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Protocol.ExtendedMetadata;
using Wavee.Protocol.Metadata;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Fetches full track credits via queryTrackCreditsModal, de-duplicates contributors,
/// and batch-fetches artist portrait images via IExtendedMetadataClient.
/// </summary>
public sealed class TrackCreditsService : ITrackCreditsService
{
    private readonly IPathfinderClient _pathfinder;
    private readonly IExtendedMetadataClient _metadata;
    private readonly ILogger? _logger;

    public TrackCreditsService(
        IPathfinderClient pathfinder,
        IExtendedMetadataClient metadata,
        ILogger? logger = null)
    {
        _pathfinder = pathfinder;
        _metadata = metadata;
        _logger = logger;
    }

    public async Task<TrackCreditsResult> GetCreditsAsync(string trackUri, CancellationToken ct = default)
    {
        // 1. Fetch credits
        var response = await _pathfinder.GetTrackCreditsAsync(trackUri, ct: ct).ConfigureAwait(false);
        var contributors = response.Data?.TrackUnion?.CreditsTrait?.Contributors?.Items;
        var recordLabel = response.Data?.TrackUnion?.CreditsTrait?.Sources?.Items?.FirstOrDefault()?.Name;

        if (contributors is not { Count: > 0 })
        {
            return new TrackCreditsResult { Groups = [], RecordLabel = recordLabel };
        }

        // 2. De-duplicate: same person within the same role group → merge roles
        //    e.g. "Adam Novodor" as Composer + Lyricist in "Composition & Lyrics" → single entry
        //    but keep them separate across groups (Artist vs Performers)
        var deduped = contributors
            .GroupBy(c => (c.Name?.ToLowerInvariant(), c.Uri, c.RoleGroup?.Name ?? "Other"))
            .Select(g => new
            {
                Name = g.First().Name,
                Uri = g.First().Uri,
                Roles = g.Select(c => c.Role).Where(r => r != null).Distinct().ToList(),
                RoleGroup = g.Key.Item3
            })
            .ToList();

        // 3. Build result immediately (no images yet — they load in background)
        var groups = deduped
            .GroupBy(c => c.RoleGroup)
            .OrderBy(g => GetRoleGroupOrder(g.Key))
            .Select(g => new CreditGroupResult
            {
                RoleName = g.Key,
                Contributors = g.Select(c => new CreditContributorResult
                {
                    Name = c.Name,
                    ArtistUri = c.Uri,
                    ImageUrl = null, // Populated async below
                    Roles = c.Roles!
                }).ToList()
            })
            .ToList();

        var result = new TrackCreditsResult { Groups = groups, RecordLabel = recordLabel };

        // 4. Fire-and-forget: batch-fetch artist images in background, update contributors
        var artistUris = deduped
            .Where(c => !string.IsNullOrEmpty(c.Uri))
            .Select(c => c.Uri!)
            .Distinct()
            .ToList();

        if (artistUris.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                using var imageFetchCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    var imageMap = await BatchFetchArtistImagesAsync(artistUris, imageFetchCts.Token).ConfigureAwait(false);
                    foreach (var group in result.Groups)
                    {
                        foreach (var contributor in group.Contributors)
                        {
                            if (contributor.ArtistUri != null && imageMap.TryGetValue(contributor.ArtistUri, out var img))
                                contributor.ImageUrl = img;
                        }
                    }
                    // Images are now populated on the result objects
                }
                catch (OperationCanceledException ex)
                {
                    _logger?.LogDebug(ex, "Artist image enrichment for credits cancelled");
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Artist image enrichment for credits failed");
                }
            }, CancellationToken.None);
        }

        return result;
    }

    private async Task<Dictionary<string, string?>> BatchFetchArtistImagesAsync(
        IReadOnlyList<string> artistUris, CancellationToken ct)
    {
        var map = new Dictionary<string, string?>();
        if (artistUris.Count == 0) return map;

        try
        {
            var requests = artistUris.Select(uri =>
                (uri, (IEnumerable<ExtensionKind>)new[] { ExtensionKind.ArtistV4 }));

            var response = await _metadata.GetBatchedExtensionsAsync(requests, ct).ConfigureAwait(false);

            foreach (var extData in response.GetAllExtensionData(ExtensionKind.ArtistV4))
            {
                var artist = extData.UnpackAs<Artist>();
                if (artist == null || string.IsNullOrEmpty(extData.EntityUri)) continue;

                var portrait = artist.PortraitGroup?.Image.FirstOrDefault()
                               ?? artist.Portrait.FirstOrDefault();

                string? imageUrl = null;
                if (portrait?.FileId != null && !portrait.FileId.IsEmpty)
                {
                    var hex = Convert.ToHexString(portrait.FileId.ToByteArray()).ToLowerInvariant();
                    imageUrl = SpotifyImageHelper.ToHttpsUrl($"spotify:image:{hex}");
                }

                map[extData.EntityUri] = imageUrl;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to batch-fetch artist images for credits");
        }

        return map;
    }

    private static int GetRoleGroupOrder(string roleGroup) => roleGroup switch
    {
        "Artist" => 0,
        "Composition & Lyrics" => 1,
        "Production & Engineering" => 2,
        "Performers" => 3,
        _ => 4
    };
}
