using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Playlists;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// One-shot entity preview used to fill the in-bar suggestion card after the user
/// pastes a Spotify URL / URI into the omnibar. Returns null on any failure — the
/// caller falls back to a synthetic "Open {kind}" suggestion.
/// </summary>
public interface ISpotifyLinkPreviewService
{
    Task<LinkPreview?> ResolveAsync(SpotifyLink link, CancellationToken ct);
}

public sealed record LinkPreview(string Title, string? Subtitle, string? ImageUrl);

public sealed class SpotifyLinkPreviewService : ISpotifyLinkPreviewService
{
    private readonly IPathfinderClient _pathfinder;
    private readonly ISpClient _spClient;
    private readonly ILogger<SpotifyLinkPreviewService>? _logger;

    public SpotifyLinkPreviewService(
        IPathfinderClient pathfinder,
        ISpClient spClient,
        ILogger<SpotifyLinkPreviewService>? logger = null)
    {
        _pathfinder = pathfinder;
        _spClient = spClient;
        _logger = logger;
    }

    public async Task<LinkPreview?> ResolveAsync(SpotifyLink link, CancellationToken ct)
    {
        try
        {
            return link.Kind switch
            {
                SpotifyLinkKind.Track => await ResolveTrackAsync(link.CanonicalUri, ct).ConfigureAwait(false),
                SpotifyLinkKind.Album => await ResolveAlbumAsync(link.CanonicalUri, ct).ConfigureAwait(false),
                SpotifyLinkKind.Artist => await ResolveArtistAsync(link.CanonicalUri, ct).ConfigureAwait(false),
                SpotifyLinkKind.Playlist => await ResolvePlaylistAsync(link.CanonicalUri, ct).ConfigureAwait(false),
                SpotifyLinkKind.Show => await ResolveShowAsync(link.CanonicalUri, ct).ConfigureAwait(false),
                SpotifyLinkKind.Episode => await ResolveEpisodeAsync(link.CanonicalUri, ct).ConfigureAwait(false),
                SpotifyLinkKind.User => await ResolveUserAsync(link, ct).ConfigureAwait(false),
                SpotifyLinkKind.LikedSongs => new LinkPreview("Liked Songs", "Playlist", null),
                SpotifyLinkKind.YourEpisodes => new LinkPreview("Your Episodes", "Podcasts", null),
                SpotifyLinkKind.Genre => new LinkPreview("Open browse page", null, null),
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[Omnibar] Link preview fetch failed for {Uri}", link.CanonicalUri);
            return null;
        }
    }

    private async Task<LinkPreview?> ResolveAlbumAsync(string uri, CancellationToken ct)
    {
        var resp = await _pathfinder.GetAlbumAsync(uri, ct).ConfigureAwait(false);
        var album = resp.Data?.AlbumUnion;
        if (album?.Name is null) return null;
        var artist = album.Artists?.Items?.FirstOrDefault()?.Profile?.Name;
        return new LinkPreview(album.Name, artist ?? "Album", PickSmallestImage(album.CoverArt?.Sources));
    }

    private async Task<LinkPreview?> ResolveArtistAsync(string uri, CancellationToken ct)
    {
        var resp = await _pathfinder.GetArtistOverviewAsync(uri, ct).ConfigureAwait(false);
        var artist = resp.Data?.ArtistUnion;
        if (artist?.Profile?.Name is null) return null;
        return new LinkPreview(artist.Profile.Name, "Artist", PickSmallestImage(artist.Visuals?.AvatarImage?.Sources));
    }

    private async Task<LinkPreview?> ResolveTrackAsync(string uri, CancellationToken ct)
    {
        var resp = await _pathfinder.GetTrackAsync(uri, ct).ConfigureAwait(false);
        var track = resp.Data?.TrackUnion;
        if (track?.Name is null) return null;
        var artist = track.FirstArtist?.Items?.FirstOrDefault()?.Profile?.Name;
        return new LinkPreview(track.Name, artist ?? "Track", PickSmallestImage(track.AlbumOfTrack?.CoverArt?.Sources));
    }

    private async Task<LinkPreview?> ResolveShowAsync(string uri, CancellationToken ct)
    {
        var resp = await _pathfinder.GetShowMetadataAsync(uri, ct).ConfigureAwait(false);
        var show = resp.Data?.PodcastUnionV2;
        if (show?.Name is null) return null;
        return new LinkPreview(show.Name, show.Publisher?.Name ?? "Podcast", PickSmallestShowImage(show.CoverArt?.Sources));
    }

    private async Task<LinkPreview?> ResolveEpisodeAsync(string uri, CancellationToken ct)
    {
        var resp = await _pathfinder.GetEpisodeOrChapterAsync(uri, ct).ConfigureAwait(false);
        var ep = resp.Data?.EpisodeUnionV2;
        if (ep?.Name is null) return null;
        return new LinkPreview(ep.Name, ep.PodcastV2?.Data?.Name ?? "Episode", PickSmallestImage(ep.CoverArt?.Sources));
    }

    private async Task<LinkPreview?> ResolvePlaylistAsync(string uri, CancellationToken ct)
    {
        // length=0 — skip the track list, we only need attributes (name + cover).
        var content = await _spClient.GetPlaylistAsync(uri, decorate: null, start: 0, length: 0, cancellationToken: ct)
            .ConfigureAwait(false);
        var attrs = content?.Attributes;
        if (string.IsNullOrWhiteSpace(attrs?.Name)) return null;
        return new LinkPreview(attrs!.Name, "Playlist", SelectedListContentMapper.PickImageUrl(attrs));
    }

    private async Task<LinkPreview?> ResolveUserAsync(SpotifyLink link, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(link.EntityId)) return null;
        var profile = await _spClient.GetUserProfileAsync(link.EntityId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(profile?.EffectiveDisplayName)) return null;
        return new LinkPreview(profile!.EffectiveDisplayName!, "Profile", profile.EffectiveImageUrl);
    }

    private static string? PickSmallestImage(List<ArtistImageSource>? sources)
    {
        if (sources is null || sources.Count == 0) return null;
        ArtistImageSource? best = null;
        foreach (var s in sources)
        {
            if (string.IsNullOrEmpty(s?.Url)) continue;
            if (best is null || (s.Width ?? int.MaxValue) < (best.Width ?? int.MaxValue))
                best = s;
        }
        return best?.Url;
    }

    private static string? PickSmallestShowImage(List<PathfinderShowImageSource>? sources)
    {
        if (sources is null || sources.Count == 0) return null;
        PathfinderShowImageSource? best = null;
        foreach (var s in sources)
        {
            if (string.IsNullOrEmpty(s?.Url)) continue;
            if (best is null || (s.Width ?? int.MaxValue) < (best.Width ?? int.MaxValue))
                best = s;
        }
        return best?.Url;
    }
}
