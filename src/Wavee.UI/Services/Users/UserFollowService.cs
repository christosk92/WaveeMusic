using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Session;
using Wavee.UI.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Default <see cref="IUserFollowService"/>. Thin wrapper over
/// <c>ISession.SpClient.FollowUserAsync / UnfollowUserAsync</c> with a
/// connectivity gate. Lives in <c>Wavee.UI.WinUI</c> for now (the
/// SpClient-touching impls all collected here); the public contract in
/// <c>Wavee.UI.Contracts</c> keeps ProfileViewModel out of <c>Wavee.Core.*</c>.
/// </summary>
public sealed class UserFollowService : IUserFollowService
{
    private readonly ISession? _session;
    private readonly ILogger<UserFollowService>? _logger;

    public UserFollowService(ISession? session = null, ILogger<UserFollowService>? logger = null)
    {
        _session = session;
        _logger = logger;
    }

    public bool IsAvailable => _session is not null && _session.IsConnected();

    public async Task<bool> FollowAsync(string username, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrEmpty(username)) return false;
        try
        {
            return await _session!.SpClient.FollowUserAsync(username, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FollowAsync failed for {User}", username);
            return false;
        }
    }

    public async Task<bool> UnfollowAsync(string username, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrEmpty(username)) return false;
        try
        {
            return await _session!.SpClient.UnfollowUserAsync(username, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "UnfollowAsync failed for {User}", username);
            return false;
        }
    }
}
