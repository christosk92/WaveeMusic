using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core.Http.Pathfinder;

namespace Wavee.UI.Contracts;

/// <summary>
/// Drains <c>ISession</c> out of <c>HomeViewModel</c> + <c>BrowseViewModel</c> +
/// <c>HomeFeedCache</c>. Wraps the three Pathfinder calls those surfaces use,
/// plus a connectivity gate so callers don't reach for <c>_session.IsConnected()</c>.
///
/// <para>Phase 3 deliverable: the only place that may reference <c>ISession</c>
/// from a ViewModel is the home feed pipeline. With this service in place, the
/// home VM/cache stops needing the session at all.</para>
/// </summary>
public interface IHomeFeedService
{
    /// <summary>
    /// True only when there is an authenticated, live session. Use this as
    /// the gate before any of the Get* calls.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Personalized home feed. Returns null when no session is available.
    /// </summary>
    Task<HomeResponse?> GetHomeAsync(int sectionItemsLimit = 10, string? facet = null, CancellationToken ct = default);

    /// <summary>
    /// Browse-All top-level surface. Returns null when no session is available
    /// or on transport failure.
    /// </summary>
    Task<BrowseAllResponse?> GetBrowseAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Browse page for a specific <c>spotify:page:</c> URI.
    /// </summary>
    Task<BrowsePageResponse?> GetBrowsePageAsync(string uri, CancellationToken ct = default);

    /// <summary>
    /// Preview-media lookup for the home-feed baseline items.
    /// </summary>
    Task<FeedBaselineLookupResponse?> GetFeedBaselineLookupAsync(IReadOnlyList<string> uris, CancellationToken ct = default);
}
