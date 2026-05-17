using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wavee.Core.Http.Pathfinder;
using Wavee.Core.Session;
using Wavee.UI.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Default <see cref="IHomeFeedService"/>. Thin wrapper over
/// <see cref="ISession.Pathfinder"/> with a connectivity gate. Lives in
/// <c>Wavee.UI.WinUI</c> for now (next to the other session-touching impls);
/// the public contract in <c>Wavee.UI.Contracts</c> keeps the home VM /
/// cache testable from <c>Wavee.UI.Tests</c>.
/// </summary>
public sealed class HomeFeedService : IHomeFeedService
{
    private readonly ISession? _session;
    private readonly ILogger<HomeFeedService>? _logger;

    public HomeFeedService(ISession? session = null, ILogger<HomeFeedService>? logger = null)
    {
        _session = session;
        _logger = logger;
    }

    public bool IsAvailable => _session is not null && _session.IsConnected();

    public async Task<HomeResponse?> GetHomeAsync(int sectionItemsLimit = 10, string? facet = null, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        try
        {
            return await _session!.Pathfinder.GetHomeAsync(sectionItemsLimit, facet, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetHomeAsync failed");
            return null;
        }
    }

    public async Task<BrowseAllResponse?> GetBrowseAllAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        try
        {
            return await _session!.Pathfinder.GetBrowseAllAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetBrowseAllAsync failed");
            return null;
        }
    }

    public async Task<BrowsePageResponse?> GetBrowsePageAsync(string uri, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        try
        {
            return await _session!.Pathfinder.GetBrowsePageAsync(uri, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetBrowsePageAsync({Uri}) failed", uri);
            return null;
        }
    }

    public async Task<FeedBaselineLookupResponse?> GetFeedBaselineLookupAsync(IReadOnlyList<string> uris, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        try
        {
            return await _session!.Pathfinder.GetFeedBaselineLookupAsync(uris, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetFeedBaselineLookupAsync failed for {Count} URIs", uris.Count);
            return null;
        }
    }
}
