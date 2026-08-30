using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

// ── The ONLINE-READ seam (hydration façade design §2.7) ──────────────────────────────────────────────────────────────
// The four reads a catalog source cannot answer from its own store: full-catalog search, as-you-type suggestions (both
// shapes), and the editorial/personalized Home feed. They are READS, not hydration — nothing they return is written
// into the Store — which is why they are a seam of their own rather than another rung on IEntityHydrator.
//
// Wiring discipline: no seam is ever null. StoreLibrarySource takes ONE IOnlineCatalog in its ctor and calls it
// unconditionally; the composition root holds a SwitchableOnlineCatalog whose inner is OfflineOnlineCatalog until
// go-live and again after logout. The "am I logged in?" question therefore exists in exactly one place — the seam's
// inner — instead of four `is { } live` probes on the source.

/// <summary>The online catalog reads. Every method is total: an implementation that cannot answer says so in its
/// return value (null / empty) rather than throwing or being absent.</summary>
public interface IOnlineCatalog
{
    /// <summary>Full-catalog paged search. <c>null</c> means "no online catalog" — the caller then degrades to whatever
    /// offline index it has (StoreLibrarySource falls back to its store track index). A live implementation that FAILS
    /// throws instead, so a broken session is never silently indistinguishable from being logged out.</summary>
    Task<SearchResults?> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default);

    /// <summary>As-you-type query completions (the omnibar). Empty offline.</summary>
    Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default);

    /// <summary>As-you-type completions PLUS the typed entity hits. Empty offline.</summary>
    Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default);

    /// <summary>The editorial/personalized Home feed. <c>null</c> means "no live Home" — the caller then contributes its
    /// degraded library shelves and, critically, does NOT pin/carry a facet chip row it has no live feed to filter.</summary>
    Task<LiveHomeResult?> GetHomeAsync(CancellationToken ct = default);
}

/// <summary>The logged-out / test answer, and the inner every <see cref="SwitchableOnlineCatalog"/> starts and ends on.
/// Named (not a null seam) and intentional: search declines so the caller uses its offline index, suggestions are
/// empty, and Home says "no live feed" so the caller emits its library shelves.</summary>
public sealed class OfflineOnlineCatalog : IOnlineCatalog
{
    public static readonly OfflineOnlineCatalog Instance = new();
    OfflineOnlineCatalog() { }

    static readonly Task<SearchResults?> NoSearch = Task.FromResult<SearchResults?>(null);
    static readonly Task<IReadOnlyList<string>> NoQueries = Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    static readonly Task<SearchSuggestions> NoSuggestions = Task.FromResult(SearchSuggestions.Empty);
    static readonly Task<LiveHomeResult?> NoHome = Task.FromResult<LiveHomeResult?>(null);

    public Task<SearchResults?> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default) => NoSearch;
    public Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default) => NoQueries;
    public Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default) => NoSuggestions;
    public Task<LiveHomeResult?> GetHomeAsync(CancellationToken ct = default) => NoHome;
}

/// <summary>The go-live/offline seam: one stable reference the catalog source holds forever, whose INNER flips between
/// the live Spotify catalog and <see cref="OfflineOnlineCatalog"/>. <see cref="SetInner"/> is the only mutation and the
/// field is volatile, so a call already in flight keeps running against the implementation it started on.</summary>
public sealed class SwitchableOnlineCatalog : IOnlineCatalog
{
    volatile IOnlineCatalog _inner;

    public SwitchableOnlineCatalog() : this(OfflineOnlineCatalog.Instance) { }

    /// <param name="inner">REQUIRED — the offline implementation this starts on. There is no null state.</param>
    public SwitchableOnlineCatalog(IOnlineCatalog inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public IOnlineCatalog Inner => _inner;

    public void SetInner(IOnlineCatalog inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Back to logged-out: the symmetric half of every <see cref="SetInner"/> a live bootstrap performs.</summary>
    public void Reset() => _inner = OfflineOnlineCatalog.Instance;

    public Task<SearchResults?> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default)
        => _inner.SearchAsync(query, facet, offset, limit, ct);

    public Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default)
        => _inner.SuggestAsync(query, ct);

    public Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default)
        => _inner.SuggestRichAsync(query, ct);

    public Task<LiveHomeResult?> GetHomeAsync(CancellationToken ct = default)
        => _inner.GetHomeAsync(ct);
}
