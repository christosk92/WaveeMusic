using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Shared profile-loading logic. Used by <see cref="ProfileCache"/> for the authenticated
/// user (with caching + background refresh) and by <see cref="ProfileService"/> for
/// arbitrary users (one-shot, no caching). Auth-only top-content (Pathfinder) is skipped
/// when the loaded profile is not the current user.
/// </summary>
internal static class ProfileFetcher
{
    private const string UserUriPrefix = "spotify:user:";

    public static async Task<ProfileSnapshot> LoadAsync(
        ISession session,
        string usernameOrUri,
        IColorService colorService,
        ILogger? logger,
        CancellationToken ct)
    {
        var username = NormalizeUsername(usernameOrUri);
        var profile = await session.SpClient.GetUserProfileAsync(username, ct);

        var displayName = profile.EffectiveDisplayName ?? username;
        var profileImageUrl = profile.EffectiveImageUrl;
        var followingCount = profile.FollowingCount ?? 0;
        var publicPlaylistCount = profile.TotalPublicPlaylistsCount ?? 0;
        var profileColor = profile.Color ?? 0;
        var isCurrentUser = profile.IsCurrentUser ?? IsAuthenticatedUser(session, username);

        string? heroColorHex = null;
        if (!string.IsNullOrEmpty(profileImageUrl))
        {
            try
            {
                var color = await colorService.GetColorAsync(profileImageUrl, ct).ConfigureAwait(false);
                heroColorHex = color?.DarkHex ?? color?.RawHex ?? color?.LightHex;
            }
            catch { /* cosmetic */ }
        }

        var recentArtists = profile.RecentlyPlayedArtists?.ToList() ?? [];
        var publicPlaylists = profile.PublicPlaylists?.ToList() ?? [];

        var followingArtists = new List<SpotifyProfileArtist>();
        try
        {
            var following = await session.SpClient.GetUserFollowingAsync(username, ct);
            if (following.Profiles != null)
                followingArtists.AddRange(following.Profiles);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to fetch following list for {Username}", username);
        }

        var topTracks = new List<TopTrackItem>();
        if (isCurrentUser)
        {
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
                    topTracks = topContent.Data.Me.Profile.TopTracks.Items.ToList();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to fetch top content from Pathfinder");
            }
        }

        logger?.LogDebug("Profile loaded: user={Username} self={IsSelf} artists={A} playlists={P} tracks={T}",
            username, isCurrentUser, recentArtists.Count, publicPlaylists.Count, topTracks.Count);

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
            TopTracks = topTracks,
            IsCurrentUser = isCurrentUser,
            Username = username,
            UserUri = $"spotify:user:{username}",
            IsFollowing = false
        };
    }

    public static string NormalizeUsername(string usernameOrUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usernameOrUri);
        return usernameOrUri.StartsWith(UserUriPrefix, StringComparison.Ordinal)
            ? usernameOrUri[UserUriPrefix.Length..]
            : usernameOrUri;
    }

    public static bool IsAuthenticatedUser(ISession session, string username)
    {
        var auth = session.GetUserData()?.Username;
        return !string.IsNullOrEmpty(auth)
            && string.Equals(auth, username, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Default <see cref="Wavee.UI.Contracts.IUserProfileService"/>. Wraps the
/// ISession that <see cref="ProfileFetcher"/> needs so neither ProfileCache nor
/// ProfileViewModel needs to hold the raw session.
/// </summary>
public sealed class ProfileService : Wavee.UI.Contracts.IUserProfileService
{
    private readonly ISession? _session;
    private readonly IColorService _colorService;
    private readonly ILogger<ProfileService>? _logger;

    public ProfileService(
        IColorService colorService,
        ISession? session = null,
        ILogger<ProfileService>? logger = null)
    {
        _session = session;
        _colorService = colorService;
        _logger = logger;
    }

    public bool IsAvailable => _session is not null && _session.IsConnected();

    public string? AuthenticatedUsername => _session?.GetUserData()?.Username;

    public async Task<object> LoadAsync(string usernameOrUri, CancellationToken ct = default)
    {
        if (_session is null)
            throw new InvalidOperationException("ProfileService cannot load — no session is wired.");
        return await ProfileFetcher.LoadAsync(_session, usernameOrUri, _colorService, _logger, ct)
            .ConfigureAwait(false);
    }

    public async Task<object> LoadAuthenticatedUserAsync(CancellationToken ct = default)
    {
        if (_session is null)
            throw new InvalidOperationException("ProfileService cannot load — no session is wired.");
        var username = _session.GetUserData()?.Username
            ?? throw new InvalidOperationException("Authenticated user has no username.");
        return await ProfileFetcher.LoadAsync(_session, username, _colorService, _logger, ct)
            .ConfigureAwait(false);
    }
}