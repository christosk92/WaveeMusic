using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>Paging the items inside ONE Home section — the read behind Home's "Show all". Source-agnostic seam so the
/// page never touches Pathfinder directly.
///
/// This is deliberately NOT <see cref="IBrowseService.GetSectionAsync"/>: a <c>spotify:section:</c> URI belongs to the
/// Home document, and <c>browseSection</c> is a BROWSE resource that was only ever inferred to accept one. The live
/// implementation is <c>Wavee.SpotifyLive.SpotifyHomeSectionService</c>, over the real <c>homeSection</c> operation.</summary>
public interface IHomeSectionSource
{
    /// <summary>One page of a Home section's items. Null when the section could not be read at all (no session, or the
    /// server refused) — the caller surfaces that as a failure rather than as an empty section, because "the endpoint
    /// rejected this" and "this section is empty" are different answers and only one of them is retryable.</summary>
    Task<HomeSectionPageResult?> GetHomeSectionAsync(string sectionUri, int offset, CancellationToken ct = default);
}

/// <summary>A stable home-section identity the UI holds for the whole session: go-live installs the live Pathfinder
/// adapter and logout resets it, so a mounted page never re-resolves a service nor keeps a session-bound one alive
/// across a login change. The seam is REQUIRED, never nullable — offline it is the named
/// <see cref="NullHomeSectionService"/>, not null.</summary>
public sealed class SwitchableHomeSectionService : IHomeSectionSource
{
    volatile IHomeSectionSource _inner = NullHomeSectionService.Instance;

    public void SetInner(IHomeSectionSource inner) => _inner = inner ?? NullHomeSectionService.Instance;
    public void Reset() => _inner = NullHomeSectionService.Instance;

    public Task<HomeSectionPageResult?> GetHomeSectionAsync(string sectionUri, int offset, CancellationToken ct = default)
        => _inner.GetHomeSectionAsync(sectionUri, offset, ct);
}

/// <summary>The offline / fake-backend home-section source, named with intent: there is no Pathfinder without a live
/// Spotify session, so a drill-in shows whatever Home seeded and pages no further. Deliberately NOT a nullable seam —
/// a null service would make every call site re-invent this answer.</summary>
public sealed class NullHomeSectionService : IHomeSectionSource
{
    public static readonly NullHomeSectionService Instance = new();

    public Task<HomeSectionPageResult?> GetHomeSectionAsync(string sectionUri, int offset, CancellationToken ct = default)
        => Task.FromResult<HomeSectionPageResult?>(null);
}
