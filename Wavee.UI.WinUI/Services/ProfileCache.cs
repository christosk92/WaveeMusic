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
}

/// <summary>
/// Singleton cache for the profile page. Extends <see cref="PageCache{TSnapshot}"/> with
/// profile-specific fetch logic and flat-list diff helpers.
/// </summary>
public sealed class ProfileCache : PageCache<ProfileSnapshot>
{
    public ProfileCache(ILogger<ProfileCache>? logger = null) : base(logger)
    {
    }

    protected override async Task<ProfileSnapshot> FetchCoreAsync(ISession session, CancellationToken ct)
    {
        var userData = session.GetUserData()
            ?? throw new InvalidOperationException("No user data available");

        var profile = await session.SpClient.GetUserProfileAsync(userData.Username);

        var displayName = profile.EffectiveDisplayName ?? userData.Username;
        var profileImageUrl = profile.EffectiveImageUrl;
        var followingCount = profile.FollowingCount ?? 0;
        var publicPlaylistCount = profile.TotalPublicPlaylistsCount ?? 0;
        var profileColor = profile.Color ?? 0;

        // Fetch extracted color for hero gradient
        string? heroColorHex = null;
        if (!string.IsNullOrEmpty(profileImageUrl))
        {
            try
            {
                var colorResponse = await session.Pathfinder.GetExtractedColorsAsync(
                    new List<string> { profileImageUrl });
                var entry = colorResponse?.Data?.ExtractedColors?.FirstOrDefault();
                if (entry != null)
                    heroColorHex = entry.ColorDark?.Hex ?? entry.ColorRaw?.Hex;
            }
            catch { /* Non-fatal: hero gradient is cosmetic */ }
        }

        // Recent artists (from profile initially)
        var recentArtists = profile.RecentlyPlayedArtists?.ToList() ?? [];

        // Public playlists
        var publicPlaylists = profile.PublicPlaylists?.ToList() ?? [];

        // Following
        var followingArtists = new List<SpotifyProfileArtist>();
        try
        {
            var following = await session.SpClient.GetUserFollowingAsync(userData.Username);
            if (following.Profiles != null)
                followingArtists.AddRange(following.Profiles);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to fetch following list");
        }

        // Top content (replaces recent artists with better data)
        var topTracks = new List<TopTrackItem>();
        try
        {
            var topContent = await session.Pathfinder.GetUserTopContentAsync(artistLimit: 10, trackLimit: 10);
            if (topContent?.Data?.Me?.Profile?.TopArtists?.Items != null)
            {
                recentArtists = topContent.Data.Me.Profile.TopArtists.Items
                    .Where(i => i.Data != null)
                    .Select(i => new SpotifyProfileArtist
                    {
                        Name = i.Data!.Profile?.Name,
                        Uri = i.Data.Uri,
                        ImageUrl = i.Data.Visuals?.AvatarImage?.Sources?.FirstOrDefault()?.Url
                    })
                    .ToList();
            }
            if (topContent?.Data?.Me?.Profile?.TopTracks?.Items != null)
            {
                topTracks = topContent.Data.Me.Profile.TopTracks.Items.ToList();
            }
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Failed to fetch top content from Pathfinder");
        }

        Logger?.LogDebug("Profile cached: {Artists} artists, {Playlists} playlists, {Tracks} tracks",
            recentArtists.Count, publicPlaylists.Count, topTracks.Count);

        return new ProfileSnapshot
        {
            DisplayName = displayName,
            ProfileImageUrl = profileImageUrl,
            FollowingCount = followingCount,
            PublicPlaylistCount = publicPlaylistCount,
            ProfileColor = profileColor,
            HeroColorHex = heroColorHex,
            RecentArtists = recentArtists,
            PublicPlaylists = publicPlaylists,
            FollowingArtists = followingArtists,
            TopTracks = topTracks
        };
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
