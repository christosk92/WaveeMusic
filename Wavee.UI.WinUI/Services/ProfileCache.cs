using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Immutable snapshot of all profile page data.
/// </summary>
public sealed record ProfileSnapshot
{
    public required string DisplayName { get; init; }
    public string? ProfileImageUrl { get; init; }
    public int FollowingCount { get; init; }
    public int PublicPlaylistCount { get; init; }
    public int ProfileColor { get; init; }
    public string? HeroColorHex { get; init; }
    public required List<SpotifyProfileArtist> RecentArtists { get; init; }
    public required List<SpotifyProfilePlaylist> PublicPlaylists { get; init; }
    public required List<SpotifyProfileArtist> FollowingArtists { get; init; }
    public required List<TopTrackItem> TopTracks { get; init; }
    public bool IsCurrentUser { get; init; }
    public string? Username { get; init; }
    public string? UserUri { get; init; }
    public bool IsFollowing { get; init; }
}

/// <summary>
/// Singleton cache for the profile page. Extends <see cref="PageCache{TSnapshot}"/> with
/// profile-specific fetch logic and flat-list diff helpers.
/// </summary>
public sealed class ProfileCache : PageCache<ProfileSnapshot>, IProfileCache
{
    private readonly IColorService _colorService;

    public ProfileCache(IColorService colorService, ILogger<ProfileCache>? logger = null) : base(logger)
    {
        _colorService = colorService;
    }

    protected override Task<ProfileSnapshot> FetchCoreAsync(ISession session, CancellationToken ct)
    {
        var userData = session.GetUserData()
            ?? throw new InvalidOperationException("No user data available");
        return ProfileFetcher.LoadAsync(session, userData.Username, _colorService, Logger, ct);
    }

    // ── Flat-list diff helpers ──

    /// <summary>
    /// Diffs a flat list of artists by Uri. Updates in-place, adds/removes as needed.
    /// </summary>
    public static void DiffArtists(
        ObservableCollection<SpotifyProfileArtist> current,
        List<SpotifyProfileArtist> fresh)
    {
        var freshByUri = new Dictionary<string, int>();
        for (int i = 0; i < fresh.Count; i++)
        {
            if (fresh[i].Uri != null)
                freshByUri[fresh[i].Uri!] = i;
        }

        // Remove items no longer present
        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (current[i].Uri != null && !freshByUri.ContainsKey(current[i].Uri!))
                current.RemoveAt(i);
        }

        // Add/update in correct order
        for (int i = 0; i < fresh.Count; i++)
        {
            if (i < current.Count && current[i].Uri == fresh[i].Uri)
            {
                // Update in-place if changed
                if (current[i].Name != fresh[i].Name || current[i].ImageUrl != fresh[i].ImageUrl)
                    current[i] = fresh[i];
            }
            else
            {
                current.Insert(Math.Min(i, current.Count), fresh[i]);
            }
        }

        while (current.Count > fresh.Count)
            current.RemoveAt(current.Count - 1);
    }

    /// <summary>
    /// Diffs a flat list of playlists by Uri. Updates in-place, adds/removes as needed.
    /// </summary>
    public static void DiffPlaylists(
        ObservableCollection<SpotifyProfilePlaylist> current,
        List<SpotifyProfilePlaylist> fresh)
    {
        var freshByUri = new Dictionary<string, int>();
        for (int i = 0; i < fresh.Count; i++)
        {
            if (fresh[i].Uri != null)
                freshByUri[fresh[i].Uri!] = i;
        }

        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (current[i].Uri != null && !freshByUri.ContainsKey(current[i].Uri!))
                current.RemoveAt(i);
        }

        for (int i = 0; i < fresh.Count; i++)
        {
            if (i < current.Count && current[i].Uri == fresh[i].Uri)
            {
                if (current[i].Name != fresh[i].Name ||
                    current[i].ImageUrl != fresh[i].ImageUrl ||
                    current[i].OwnerName != fresh[i].OwnerName)
                    current[i] = fresh[i];
            }
            else
            {
                current.Insert(Math.Min(i, current.Count), fresh[i]);
            }
        }

        while (current.Count > fresh.Count)
            current.RemoveAt(current.Count - 1);
    }
}
