namespace Wavee.Core.Catalog;

public abstract record QuerySpec<T>(CatalogScope Scope);
public sealed record EntityCardQuery(CatalogScope Scope, string Uri) : QuerySpec<EntityCardSnapshot>(Scope);
public sealed record EntityCardSnapshot(string Uri, string Title, string? Subtitle, Image? Image, Track? Playable)
{
    public int? ChildCount { get; init; }
}

public sealed record PlaylistDetailQuery(CatalogScope Scope, string Uri) : QuerySpec<Playlist>(Scope);
public sealed record AlbumDetailQuery(CatalogScope Scope, string Uri) : QuerySpec<Album>(Scope);
public sealed record ArtistIdentityQuery(CatalogScope Scope, string Uri) : QuerySpec<Artist>(Scope);
public sealed record ArtistDetailQuery(CatalogScope Scope, string Uri) : QuerySpec<Artist>(Scope);
public sealed record ArtistDiscographyQuery(CatalogScope Scope, string Uri, DiscographyKind Kind,
    int Offset = 0, int Limit = 60) : QuerySpec<DiscographyPage>(Scope);
/// <summary>A retained release collection. Membership spans pages; identity demand belongs to its current view.</summary>
public sealed record ArtistReleasesQuery(CatalogScope Scope, string Uri, DiscographyKind? Kind = null)
    : QuerySpec<DiscographyPage>(Scope);
public sealed record HomeSectionQuery(CatalogScope Scope, string Uri, int Offset = 0, int Limit = 50)
    : QuerySpec<HomeSectionPageResult?>(Scope);
public sealed record ShowDetailQuery(CatalogScope Scope, string Uri) : QuerySpec<Show>(Scope);
public sealed record LikedSongsQuery(CatalogScope Scope) : QuerySpec<IReadOnlyList<Track>>(Scope);
public sealed record SavedAlbumsQuery(CatalogScope Scope) : QuerySpec<IReadOnlyList<Album>>(Scope);
public sealed record SavedArtistsQuery(CatalogScope Scope) : QuerySpec<IReadOnlyList<Artist>>(Scope);
public sealed record SavedShowsQuery(CatalogScope Scope) : QuerySpec<IReadOnlyList<Show>>(Scope);
public sealed record LibrarySearchQuery(CatalogScope Scope, string Text, LibrarySearchScope SearchScope)
    : QuerySpec<LibrarySearchResults>(Scope);
public sealed record HomeQuery(CatalogScope Scope, string? Facet = null) : QuerySpec<HomeFeed>(Scope);
public sealed record SearchQuery(CatalogScope Scope, string Text, SearchFacet Facet = SearchFacet.All,
    int Offset = 0, int Limit = 30) : QuerySpec<SearchResults>(Scope);
public sealed record SearchSuggestionsQuery(CatalogScope Scope, string Text) : QuerySpec<SearchSuggestions>(Scope);
public sealed record SidebarLibraryQuery(CatalogScope Scope) : QuerySpec<LibraryQuerySnapshot>(Scope);
public sealed record PlaylistTargetsQuery(CatalogScope Scope) : QuerySpec<PlaylistTargetsSnapshot>(Scope);
public sealed record PlaylistTargetsSnapshot(IReadOnlyList<Playlist> Playlists, bool MembershipKnown)
{
    public static PlaylistTargetsSnapshot Empty { get; } = new([], false);
}
public sealed record QueueQuery(CatalogScope Scope) : QuerySpec<QueueQuerySnapshot>(Scope);

/// <summary>The sidebar's library, joined once per real change.
/// <para><b>Structural equality.</b> Every member here is a collection, which a record compares by REFERENCE, so the
/// synthesized equality answered "changed" for a snapshot carrying the very same library and the sidebar rebuilt
/// itself on every catalog publication. Equality is therefore the item counts plus <see cref="ContentHash"/> — a fold
/// of every row the sidebar can render, taken through the rows' own structural hashes by the producing definition.
/// A snapshot with no fold (0: a literal, a test seed, a loading placeholder) has no content to
/// compare and falls back to reference identity, so a missing fold can only ever cost a redundant publication —
/// never a missed one.</para></summary>
public sealed record LibraryQuerySnapshot(IReadOnlyList<LibraryItem> Entries,
    IReadOnlyList<PlaylistNode> Tree, LibraryStats Stats, IReadOnlyDictionary<string, long> AddedAt)
{
    public IReadOnlyList<Album> Albums { get; init; } = [];
    public IReadOnlyList<Artist> Artists { get; init; } = [];
    public IReadOnlyList<Show> Shows { get; init; } = [];
    public IReadOnlyList<PlaylistSummary> Playlists { get; init; } = [];
    /// <summary>A fold of the joined rows (uris, names, subtitles, covers) and their added-at stamps. 0 = not folded.</summary>
    public long ContentHash { get; init; }

    public bool Equals(LibraryQuerySnapshot? other)
        => other is not null && (ReferenceEquals(this, other)
            || (ContentHash != 0 && ContentHash == other.ContentHash && Stats == other.Stats
                && Entries.Count == other.Entries.Count && Tree.Count == other.Tree.Count
                && Albums.Count == other.Albums.Count && Artists.Count == other.Artists.Count
                && Shows.Count == other.Shows.Count && Playlists.Count == other.Playlists.Count
                && AddedAt.Count == other.AddedAt.Count));

    public override int GetHashCode()
        => ContentHash == 0 ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this)
            : HashCode.Combine(ContentHash, Entries.Count, Tree.Count, Albums.Count, Artists.Count, Shows.Count, Playlists.Count);
}

public sealed record QueueQuerySnapshot(IReadOnlyList<QueueEntry> Rows, Track? Current,
    string? ContextUri, string? ContextName, long StructuralRevision);

public enum QueryPriority : byte { Prefetch, Visible, Playback }

/// <summary>What a page asks of its query: <paramref name="Facets"/> are the DISPLAY facets it renders on every member
/// (play counts, video associations, …), applied to the whole model. Identity is never a display facet — every
/// definition demands each member's own identity by kind — so an identity facet here is a category error, refused at
/// construction rather than fanned across kinds (a <c>ShowIdentity</c> keyed on an album uri is an HTTP 400 that fails
/// the whole 300-subject batch it rides in).</summary>
public sealed record QueryDemand(bool Active, QueryPriority Priority, IReadOnlyList<FacetKind> Facets)
{
    public IReadOnlyList<FacetKind> Facets { get; init; } = DisplayFacets(Facets);

    public static QueryDemand None { get; } = new(false, QueryPriority.Visible, []);
    public static QueryDemand Initial { get; } = new(true, QueryPriority.Visible, []);

    /// <summary>The facets a definition derives from a member's uri; a page cannot ask for them.</summary>
    public static bool IsIdentityFacet(FacetKind facet) => facet is FacetKind.TrackIdentity or FacetKind.EpisodeIdentity
        or FacetKind.AlbumIdentity or FacetKind.ArtistIdentity or FacetKind.PlaylistHeader or FacetKind.ShowIdentity
        or FacetKind.UserIdentity;

    static IReadOnlyList<FacetKind> DisplayFacets(IReadOnlyList<FacetKind> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);
        foreach (var facet in facets)
            if (IsIdentityFacet(facet))
                throw new ArgumentException($"{facet} is an identity facet; definitions derive identity from each member's kind. Demand only display facets.", nameof(facets));
        return facets;
    }
}

/// <summary><paramref name="Superseded"/>: at least one read resource's key names a scope that is no longer the
/// repository's active one (a session moved to another account/scope while this node was still attached). It is
/// never a failure — a re-acquire under the new scope is the fix, already in flight for every reactive site — so
/// it must not be conflated with <paramref name="IsOffline"/> (an inactive-scope resource's Offline activity is
/// excluded from that computation for the same reason).</summary>
public sealed record QueryStatus(bool HasPrimaryData, bool IsRefreshing, bool IsOffline, bool Superseded = false);
public sealed record FacetProblem(ResourceKey Resource, Knowledge Knowledge, ResourceError? Error);
public sealed record QuerySnapshot<T>(long Revision, long OrderRevision, T Value,
    QueryStatus Status, IReadOnlyList<FacetProblem> Problems)
{
    /// <summary>Query/replica failure without an associated catalog resource. Cached primary data remains usable.</summary>
    public ResourceError? Failure { get; init; }
    /// <summary>Knowledge of every consumed/requested facet, including unknown fields. Never a live store view.</summary>
    public IReadOnlyDictionary<ResourceKey, ResourceSnapshot> Resources { get; init; } = ResourceMap.Empty;
    /// <summary>Identity-stable data/knowledge projection, independent of request activity and errors.</summary>
    public IReadOnlyDictionary<ResourceKey, QueryFact> Facts { get; init; } = QueryFacts.Empty;
    public long FactsRevision { get; init; }
    /// <summary>The keys the query is currently asking the catalog for under its active demand. A key that was merely
    /// read while joining the value is in <see cref="Resources"/> but not here until demand names it.</summary>
    public IReadOnlyList<ResourceKey> Demanded { get; init; } = [];
}
public sealed record RefreshResult(IReadOnlyList<FacetProblem> Problems)
{
    public ResourceError? Failure { get; init; }
    public bool Succeeded => Problems.Count == 0 && Failure is null;
}

public interface IQueryHandle<T> : IDisposable
{
    QuerySnapshot<T> Current { get; }
    IObservable<QuerySnapshot<T>> Changes { get; }
    void SetDemand(QueryDemand demand);
    ValueTask<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default);
}

public interface IQueryService
{
    IQueryHandle<T> Acquire<T>(QuerySpec<T> query);
    Task<QuerySnapshot<T>> ReadOnceAsync<T>(QuerySpec<T> query, QueryDemand? demand = null,
        CancellationToken cancellationToken = default);
}
