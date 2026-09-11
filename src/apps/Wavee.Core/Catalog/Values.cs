namespace Wavee.Core.Catalog;

/// <summary>Closed domain payloads, never a bag of arbitrary properties or embedded entity graphs.</summary>
public abstract record CatalogValue
{
    public abstract FacetKind Facet { get; }
}

public sealed record TrackIdentityValue(string? Title = null, IReadOnlyList<string>? ArtistUris = null,
    string? AlbumUri = null, long? DurationMs = null, bool? IsExplicit = null, Image? Image = null,
    string? Isrc = null, int? Year = null, string? CanonicalUri = null, string? Source = null,
    TrackOrigin Origin = TrackOrigin.Streamed, ulong KnownFields = 0) : CatalogValue
{
    public override FacetKind Facet => FacetKind.TrackIdentity;
    public IReadOnlyList<string>? UnlinkedArtistNames { get; init; }
    public string? UnlinkedAlbumName { get; init; }
}

public sealed record EpisodeIdentityValue(string? Title = null, string? ShowUri = null,
    long? DurationMs = null, Image? Image = null, DateTimeOffset? PublishedAt = null, ulong KnownFields = 0, string? ShowName = null) : CatalogValue
{ public override FacetKind Facet => FacetKind.EpisodeIdentity; }

public sealed record AlbumIdentityValue(string? Name = null, Image? Cover = null,
    IReadOnlyList<string>? ArtistUris = null, int? Year = null, int? TrackCount = null,
    AlbumKind Kind = AlbumKind.Album, ulong KnownFields = 0) : CatalogValue
{ public override FacetKind Facet => FacetKind.AlbumIdentity; }

public sealed record ArtistIdentityValue(string? Name = null, Image? Image = null, ulong KnownFields = 0) : CatalogValue
{ public override FacetKind Facet => FacetKind.ArtistIdentity; }

public sealed record PlaylistHeaderValue(string? Name = null, string? Description = null,
    Image? Cover = null, string? OwnerUri = null, int? TrackCount = null, bool? IsPublic = null,
    string? Edition = null, DateTimeOffset? NextUpdateAt = null, ulong KnownFields = 0) : CatalogValue
{
    public override FacetKind Facet => FacetKind.PlaylistHeader;
    public string? OwnerName { get; init; }
    public PlaylistCapabilities? Capabilities { get; init; }
    public string? Format { get; init; }
    public string? Source { get; init; }
    public string? BasePermissionRevision { get; init; }
    public PlaylistTuning? Tuning { get; init; }
    public IReadOnlyList<string>? CollaboratorUris { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public bool? DeletedByOwner { get; init; }
    public int? ChartNewEntries { get; init; }
    public DateTimeOffset? ChartUpdatedAt { get; init; }
    public string? ChartRankType { get; init; }
}

public sealed record ShowIdentityValue(string? Name = null, string? Publisher = null,
    Image? Cover = null, string? Description = null, int? EpisodeCount = null, ulong KnownFields = 0) : CatalogValue
{ public override FacetKind Facet => FacetKind.ShowIdentity; }

public sealed record UserIdentityValue(string? Name = null, Image? Avatar = null, ulong KnownFields = 0) : CatalogValue
{ public override FacetKind Facet => FacetKind.UserIdentity; }

public sealed record PlayCountValue(long Count) : CatalogValue
{ public override FacetKind Facet => FacetKind.PlayCount; }
public sealed record DescriptorsValue(IReadOnlyList<string> Tags) : CatalogValue
{ public override FacetKind Facet => FacetKind.Descriptors; }
public sealed record AudioAttributesValue(double? TempoBpm, string? MusicalKey, string? CamelotCode,
    uint? CamelotColor) : CatalogValue
{ public override FacetKind Facet => FacetKind.AudioAttributes; }
public sealed record PublishingValue(string? Copyright, string? ReleaseDate, string? Precision = null) : CatalogValue
{ public override FacetKind Facet => FacetKind.Publishing; }
public sealed record AvailabilityValue(Availability Verdict, DateTimeOffset? AvailableAt = null) : CatalogValue
{ public override FacetKind Facet => FacetKind.Availability; }
public sealed record VideoAssociationValue(VideoAssociation Association) : CatalogValue
{ public override FacetKind Facet => FacetKind.VideoAssociation; }
public sealed record VisualIdentityValue(string ImageUri, uint? Background, uint? Text, uint? Accent) : CatalogValue
{
    public override FacetKind Facet => FacetKind.VisualIdentity;
    public IReadOnlyList<string>? ImageUris { get; init; }
    public uint? BackgroundTinted { get; init; }
    public uint? TextSubdued { get; init; }
}
public sealed record AlbumDetailValue(string? Label, string? CourtesyLine, int DiscCount = 1,
    string? ShareUrl = null, bool IsPreRelease = false, DateTimeOffset? PreReleaseEnd = null) : CatalogValue
{
    public override FacetKind Facet => FacetKind.AlbumDetail;
    public IReadOnlyList<string>? MoreByArtistUris { get; init; }
}
public sealed record ArtistOverviewValue(long? MonthlyListeners = null, long? Followers = null,
    bool? Verified = null, int? WorldRank = null, string? Bio = null, Image? HeaderImage = null,
    string? LatestReleaseUri = null, string? PinnedUri = null) : CatalogValue
{
    public override FacetKind Facet => FacetKind.ArtistOverview;
    public PinnedItem? Pinned { get; init; }
    public CatalogArtistExtras? Extras { get; init; }
    public IReadOnlyList<string>? PopularReleaseUris { get; init; }
    public int? AlbumsTotal { get; init; }
    public int? SinglesTotal { get; init; }
    public int? CompilationsTotal { get; init; }
}

public sealed record CatalogArtistExtras(IReadOnlyList<Concert>? Concerts = null,
    IReadOnlyList<MerchItem>? Merch = null, IReadOnlyList<string>? PlaylistUris = null,
    IReadOnlyList<string>? MusicVideoUris = null, IReadOnlyList<TopCity>? TopCities = null,
    IReadOnlyList<ExternalLink>? ExternalLinks = null, IReadOnlyList<Image>? Gallery = null,
    IReadOnlyList<string>? RelatedArtistUris = null, TourBanner? Tour = null,
    ArtistWatchFeed? WatchFeed = null, ArtistPreRelease? PreRelease = null);
public sealed record EpisodeDetailValue(string? Description) : CatalogValue
{ public override FacetKind Facet => FacetKind.EpisodeDetail; }

public sealed record PlaylistRevisionValue(string Revision) : CatalogValue
{ public override FacetKind Facet => FacetKind.PlaylistRevision; }

/// <summary>A transport document reference. Payload bytes are retained once by the transport persistence port.</summary>
public sealed record ExtensionDocumentValue(int ExtensionKind, string? Etag) : CatalogValue
{ public override FacetKind Facet => FacetKind.ExtensionDocument; }

public enum RelationCoverage : byte { Partial, Complete }
public sealed record RelationContext(int? DiscNumber = null, int? TrackNumber = null,
    ChartEntry? Chart = null);
public sealed record CatalogRelationItem(string OccurrenceKey, string EntityUri, RelationContext? Context = null);

/// <summary>One source page. A relation query combines only pages of the same SnapshotId.</summary>
public sealed record RelationPageValue(FacetKind Kind, string SnapshotId, string? SourceRevision,
    int Offset, int? Total, string? NextCursor, RelationCoverage Coverage,
    IReadOnlyList<CatalogRelationItem> Items) : CatalogValue
{ public override FacetKind Facet => Kind; }

public sealed record CatalogDocumentItem(string OccurrenceKey, string EntityUri)
{
    public HomeCardKind? HomeKind { get; init; }
    public string? Eyebrow { get; init; }
    public HomeCardMeta? HomeMeta { get; init; }
    public CatalogSearchHitMeta? SearchMeta { get; init; }
    public SearchSuggestionKind? SuggestionKind { get; init; }
    public string? PresentationTitle { get; init; }
    public Image? PresentationImage { get; init; }
}
public sealed record CatalogSearchHitMeta(SearchHitKind Kind, string Subtitle, string TypeLabel,
    bool RoundImage, bool Followable, bool MatchedLyrics, string? AccessLabel,
    string? Detail = null, string? Meta = null, bool MatchedTitle = false);
public sealed record CatalogDocumentSection(string OccurrenceKey, string? Title,
    IReadOnlyList<CatalogDocumentItem> Items)
{
    public string? Uri { get; init; }
    public string? Subtitle { get; init; }
    public int? TotalCount { get; init; }
    public int? RawItemCount { get; init; }
    public int UnsupportedCount { get; init; }
    public int DuplicateCount { get; init; }
    public SearchFacet? SearchFacet { get; init; }
}
public sealed record CatalogHomeGroup(string OccurrenceKey, HomeGroupKind Kind, string? Title,
    IReadOnlyList<CatalogDocumentItem> Items, string? Subtitle = null, string? Uri = null, int TotalCount = 0);
public sealed record CatalogDocumentValue(FacetKind Kind, IReadOnlyList<CatalogDocumentSection> Sections,
    string? NextCursor = null) : CatalogValue
{
    public override FacetKind Facet => Kind;
    public IReadOnlyList<CatalogHomeGroup>? HomeGroups { get; init; }
    public IReadOnlyList<HomeChip>? HomeChips { get; init; }
    public string? Greeting { get; init; }
    public string? HomeFacet { get; init; }
    public SearchFacet? SearchFacet { get; init; }
    public IReadOnlyList<SearchChip>? SearchChips { get; init; }
    public IReadOnlyList<SearchGenre>? SearchGenres { get; init; }
    public IReadOnlyDictionary<SearchFacet, int>? SearchTotals { get; init; }
    public IReadOnlyList<string>? SuggestedQueries { get; init; }
}
