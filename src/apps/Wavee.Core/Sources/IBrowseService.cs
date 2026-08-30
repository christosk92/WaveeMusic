using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>Spotify's Browse tree: the category directory, one category page, and paging the items inside one of that
/// page's sections. Source-agnostic seam so the UI never touches Pathfinder directly.
///
/// The two paging calls are INDEPENDENT axes and must not be conflated:
///   <see cref="GetPageAsync"/> pages the SECTIONS of a page (its own <c>sectionOffset</c>);
///   <see cref="GetSectionAsync"/> pages the ITEMS inside ONE section.
/// Advancing one never advances the other.</summary>
public interface IBrowseService
{
    /// <summary>Every browse category (~70). Empty when browse is unavailable — the caller renders an empty state
    /// rather than treating it as an error, since an offline/logged-out session legitimately has no browse.</summary>
    Task<IReadOnlyList<BrowseCategory>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>One category page. Never null: a page that resolves to nothing comes back with
    /// <see cref="BrowsePageModel.IsEmpty"/> set, because Spotify really does answer 200 with an empty body.</summary>
    Task<BrowsePageModel?> GetPageAsync(string pageUri, int sectionOffset = 0, CancellationToken ct = default);

    /// <summary>The next page of items inside ONE section (the "Show all" affordance on a shelf).</summary>
    Task<BrowseSection?> GetSectionAsync(string sectionUri, int offset, CancellationToken ct = default);
}

/// <summary>The offline / logged-out browse service: no categories, no pages. Keeps the UI on ONE code path (an empty
/// directory renders the same empty state as a failed fetch) instead of forcing null checks through every caller.</summary>
public sealed class NullBrowseService : IBrowseService
{
    public static readonly NullBrowseService Instance = new();

    public Task<IReadOnlyList<BrowseCategory>> GetCategoriesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BrowseCategory>>(System.Array.Empty<BrowseCategory>());

    public Task<BrowsePageModel?> GetPageAsync(string pageUri, int sectionOffset = 0, CancellationToken ct = default)
        => Task.FromResult<BrowsePageModel?>(null);

    public Task<BrowseSection?> GetSectionAsync(string sectionUri, int offset, CancellationToken ct = default)
        => Task.FromResult<BrowseSection?>(null);
}
