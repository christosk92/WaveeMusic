using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Default <see cref="IPlaylistPermissionService"/>. Most operations are
/// stubs today (logged + no-op); <see cref="GetPlaylistMembersAsync"/> is
/// wired through <see cref="ISession"/>'s SpClient.
/// </summary>
public sealed class PlaylistPermissionService : IPlaylistPermissionService
{
    private readonly ISession _session;
    private readonly ILogger<PlaylistPermissionService>? _logger;

    public PlaylistPermissionService(
        ISession session,
        ILogger<PlaylistPermissionService>? logger = null)
    {
        _session = session;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlaylistMemberResult>> GetPlaylistMembersAsync(
        string playlistId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        try
        {
            var raw = await _session.SpClient.GetPlaylistMembersAsync(playlistId, ct);
            if (raw.Count == 0) return Array.Empty<PlaylistMemberResult>();

            var results = new List<PlaylistMemberResult>(raw.Count);
            foreach (var m in raw)
            {
                results.Add(new PlaylistMemberResult
                {
                    UserId = m.UserId,
                    Username = m.Username,
                    DisplayName = m.DisplayName,
                    AvatarUrl = m.ImageUrl,
                    Role = MapPermissionLevel(m.PermissionLevel),
                });
            }
            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "GetPlaylistMembersAsync failed for {Id} — returning empty", playlistId);
            return Array.Empty<PlaylistMemberResult>();
        }
    }

    public Task SetPlaylistMemberRoleAsync(
        string playlistId, string memberUserId, PlaylistMemberRole role,
        CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "SetPlaylistMemberRoleAsync stub invoked: playlistId={Id}, member={MemberId}, role={Role} (no backend wire-up yet)",
            playlistId, memberUserId, role);
        return Task.CompletedTask;
    }

    public Task RemovePlaylistMemberAsync(
        string playlistId, string memberUserId, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "RemovePlaylistMemberAsync stub invoked: playlistId={Id}, member={MemberId} (no backend wire-up yet)",
            playlistId, memberUserId);
        return Task.CompletedTask;
    }

    public Task<PlaylistInviteLink> CreatePlaylistInviteLinkAsync(
        string playlistId, PlaylistMemberRole grantedRole, TimeSpan ttl,
        CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "CreatePlaylistInviteLinkAsync stub invoked: playlistId={Id}, role={Role}, ttl={Ttl} (no backend wire-up yet — returning placeholder)",
            playlistId, grantedRole, ttl);

        // Compose a plausible-looking placeholder so the UI renders end-to-end.
        // Token is random; real impl returns it from the permission-grant endpoint.
        var bareId = playlistId.StartsWith("spotify:playlist:", StringComparison.Ordinal)
            ? playlistId["spotify:playlist:".Length..]
            : playlistId;
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        return Task.FromResult(new PlaylistInviteLink
        {
            Token = token,
            ShareUrl = $"https://open.spotify.com/playlist/{bareId}?pt={token}",
            CreatedAt = DateTimeOffset.UtcNow,
            Ttl = ttl,
            GrantedRole = grantedRole
        });
    }

    public Task SetPlaylistCollaborativeAsync(
        string playlistId, bool collaborative, CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "SetPlaylistCollaborativeAsync stub invoked: playlistId={Id}, collaborative={Value} (no backend wire-up yet)",
            playlistId, collaborative);
        return Task.CompletedTask;
    }

    private static PlaylistMemberRole MapPermissionLevel(string level) => level?.ToUpperInvariant() switch
    {
        "OWNER" => PlaylistMemberRole.Owner,
        "CONTRIBUTOR" => PlaylistMemberRole.Contributor,
        "BLOCKED" => PlaylistMemberRole.Blocked,
        _ => PlaylistMemberRole.Viewer,
    };
}
