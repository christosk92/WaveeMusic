namespace Wavee.Core.Catalog;

/// <summary>Default means omitted. Specified(null), false, zero and an empty list are real updates.</summary>
public readonly record struct FieldChange<T>(bool IsSpecified, T Value)
{
    public static FieldChange<T> Set(T value) => new(true, value);
    public T Apply(T current) => IsSpecified ? Value : current;
}

public abstract record CatalogPatch
{
    public abstract FacetKind Facet { get; }
    public abstract CatalogValue Apply(CatalogValue? current, bool fillUnknownOnly = false);
}

/// <summary>A complete facet response; it replaces exactly this facet, never another facet on the entity.</summary>
public sealed record ReplaceFacetPatch(CatalogValue Value) : CatalogPatch
{
    public override FacetKind Facet => Value.Facet;
    public override CatalogValue Apply(CatalogValue? current, bool fillUnknownOnly = false)
        => fillUnknownOnly ? CatalogInlineSeeds.Merge(Value, current) : Value switch
        {
            TrackIdentityValue track => track with { KnownFields = ulong.MaxValue },
            PlaylistHeaderValue playlist => playlist with { KnownFields = ulong.MaxValue },
            EpisodeIdentityValue episode => episode with { KnownFields = ulong.MaxValue },
            AlbumIdentityValue album => album with { KnownFields = ulong.MaxValue },
            ArtistIdentityValue artist => artist with { KnownFields = ulong.MaxValue },
            ShowIdentityValue show => show with { KnownFields = ulong.MaxValue },
            UserIdentityValue user => user with { KnownFields = ulong.MaxValue },
            _ => Value,
        };
}

public sealed record TrackIdentityPatch(
    FieldChange<string?> Title = default, FieldChange<IReadOnlyList<string>?> ArtistUris = default,
    FieldChange<string?> AlbumUri = default, FieldChange<long?> DurationMs = default,
    FieldChange<bool?> IsExplicit = default, FieldChange<Image?> Image = default,
    FieldChange<string?> Isrc = default, FieldChange<int?> Year = default,
    FieldChange<string?> CanonicalUri = default, FieldChange<string?> Source = default,
    FieldChange<TrackOrigin> Origin = default,
    FieldChange<IReadOnlyList<string>?> UnlinkedArtistNames = default,
    FieldChange<string?> UnlinkedAlbumName = default) : CatalogPatch
{
    public override FacetKind Facet => FacetKind.TrackIdentity;
    public override CatalogValue Apply(CatalogValue? current, bool fillUnknownOnly = false)
    {
        var row = current switch
        {
            null => new TrackIdentityValue(),
            TrackIdentityValue value => value,
            _ => throw new ArgumentException("Track identity patch received a different facet.", nameof(current)),
        };
        return row with
        {
            Title = Choose(Title, row.Title, fillUnknownOnly, (row.KnownFields & 1) != 0),
            ArtistUris = Choose(ArtistUris, row.ArtistUris, fillUnknownOnly, (row.KnownFields & 2) != 0),
            AlbumUri = Choose(AlbumUri, row.AlbumUri, fillUnknownOnly, (row.KnownFields & 4) != 0),
            DurationMs = Choose(DurationMs, row.DurationMs, fillUnknownOnly, (row.KnownFields & 8) != 0),
            IsExplicit = Choose(IsExplicit, row.IsExplicit, fillUnknownOnly, (row.KnownFields & 16) != 0),
            Image = Choose(Image, row.Image, fillUnknownOnly, (row.KnownFields & 32) != 0),
            Isrc = Choose(Isrc, row.Isrc, fillUnknownOnly, (row.KnownFields & 64) != 0),
            Year = Choose(Year, row.Year, fillUnknownOnly, (row.KnownFields & 128) != 0),
            CanonicalUri = Choose(CanonicalUri, row.CanonicalUri, fillUnknownOnly, (row.KnownFields & 256) != 0),
            Source = Choose(Source, row.Source, fillUnknownOnly, (row.KnownFields & 512) != 0),
            Origin = fillUnknownOnly && (row.KnownFields & 1024) != 0 ? row.Origin : Origin.Apply(row.Origin),
            UnlinkedArtistNames = Choose(UnlinkedArtistNames, row.UnlinkedArtistNames, fillUnknownOnly, (row.KnownFields & 2048) != 0),
            UnlinkedAlbumName = Choose(UnlinkedAlbumName, row.UnlinkedAlbumName, fillUnknownOnly, (row.KnownFields & 4096) != 0),
            KnownFields = row.KnownFields | Bit(Origin.IsSpecified, 10) | (fillUnknownOnly ? 0UL :
                Bit(Title.IsSpecified, 0) | Bit(ArtistUris.IsSpecified, 1) | Bit(AlbumUri.IsSpecified, 2)
                | Bit(DurationMs.IsSpecified, 3) | Bit(IsExplicit.IsSpecified, 4) | Bit(Image.IsSpecified, 5)
                | Bit(Isrc.IsSpecified, 6) | Bit(Year.IsSpecified, 7) | Bit(CanonicalUri.IsSpecified, 8)
                | Bit(Source.IsSpecified, 9) | Bit(UnlinkedArtistNames.IsSpecified, 11) | Bit(UnlinkedAlbumName.IsSpecified, 12)),
        };
    }

    static T Choose<T>(FieldChange<T> patch, T current, bool seed, bool known)
        => seed && (known || current is not null) ? current : patch.Apply(current);
    static ulong Bit(bool specified, int bit) => specified ? 1UL << bit : 0;
}

public sealed record PlaylistHeaderPatch(FieldChange<string?> Name = default,
    FieldChange<string?> Description = default, FieldChange<Image?> Cover = default,
    FieldChange<string?> OwnerUri = default, FieldChange<int?> TrackCount = default,
    FieldChange<bool?> IsPublic = default, FieldChange<string?> Edition = default,
    FieldChange<DateTimeOffset?> NextUpdateAt = default,
    FieldChange<string?> OwnerName = default, FieldChange<PlaylistCapabilities?> Capabilities = default,
    FieldChange<string?> Format = default, FieldChange<string?> Source = default,
    FieldChange<string?> BasePermissionRevision = default, FieldChange<PlaylistTuning?> Tuning = default,
    FieldChange<IReadOnlyList<string>?> CollaboratorUris = default, FieldChange<DateTimeOffset?> CreatedAt = default,
    FieldChange<bool?> DeletedByOwner = default, FieldChange<int?> ChartNewEntries = default,
    FieldChange<DateTimeOffset?> ChartUpdatedAt = default, FieldChange<string?> ChartRankType = default) : CatalogPatch
{
    public override FacetKind Facet => FacetKind.PlaylistHeader;
    public override CatalogValue Apply(CatalogValue? current, bool fillUnknownOnly = false)
    {
        var row = current switch
        {
            null => new PlaylistHeaderValue(),
            PlaylistHeaderValue value => value,
            _ => throw new ArgumentException("Playlist header patch received a different facet.", nameof(current)),
        };
        return row with
        {
            Name = Choose(Name, row.Name, fillUnknownOnly, (row.KnownFields & 1) != 0),
            Description = Choose(Description, row.Description, fillUnknownOnly, (row.KnownFields & 2) != 0),
            Cover = Choose(Cover, row.Cover, fillUnknownOnly, (row.KnownFields & 4) != 0),
            OwnerUri = Choose(OwnerUri, row.OwnerUri, fillUnknownOnly, (row.KnownFields & 8) != 0),
            TrackCount = Choose(TrackCount, row.TrackCount, fillUnknownOnly, (row.KnownFields & 16) != 0),
            IsPublic = Choose(IsPublic, row.IsPublic, fillUnknownOnly, (row.KnownFields & 32) != 0),
            Edition = Choose(Edition, row.Edition, fillUnknownOnly, (row.KnownFields & 64) != 0),
            NextUpdateAt = Choose(NextUpdateAt, row.NextUpdateAt, fillUnknownOnly, (row.KnownFields & 128) != 0),
            OwnerName = Choose(OwnerName, row.OwnerName, fillUnknownOnly, (row.KnownFields & 256) != 0),
            Capabilities = Choose(Capabilities, row.Capabilities, fillUnknownOnly, (row.KnownFields & 512) != 0),
            Format = Choose(Format, row.Format, fillUnknownOnly, (row.KnownFields & 1024) != 0),
            Source = Choose(Source, row.Source, fillUnknownOnly, (row.KnownFields & 2048) != 0),
            BasePermissionRevision = Choose(BasePermissionRevision, row.BasePermissionRevision, fillUnknownOnly, (row.KnownFields & 4096) != 0),
            Tuning = Choose(Tuning, row.Tuning, fillUnknownOnly, (row.KnownFields & 8192) != 0),
            CollaboratorUris = Choose(CollaboratorUris, row.CollaboratorUris, fillUnknownOnly, (row.KnownFields & 16384) != 0),
            CreatedAt = Choose(CreatedAt, row.CreatedAt, fillUnknownOnly, (row.KnownFields & 32768) != 0),
            DeletedByOwner = Choose(DeletedByOwner, row.DeletedByOwner, fillUnknownOnly, (row.KnownFields & 65536) != 0),
            ChartNewEntries = Choose(ChartNewEntries, row.ChartNewEntries, fillUnknownOnly, (row.KnownFields & 131072) != 0),
            ChartUpdatedAt = Choose(ChartUpdatedAt, row.ChartUpdatedAt, fillUnknownOnly, (row.KnownFields & 262144) != 0),
            ChartRankType = Choose(ChartRankType, row.ChartRankType, fillUnknownOnly, (row.KnownFields & 524288) != 0),
            KnownFields = row.KnownFields | (fillUnknownOnly ? 0UL :
                Bit(Name.IsSpecified, 0) | Bit(Description.IsSpecified, 1) | Bit(Cover.IsSpecified, 2)
                | Bit(OwnerUri.IsSpecified, 3) | Bit(TrackCount.IsSpecified, 4) | Bit(IsPublic.IsSpecified, 5)
                | Bit(Edition.IsSpecified, 6) | Bit(NextUpdateAt.IsSpecified, 7)
                | Bit(OwnerName.IsSpecified, 8) | Bit(Capabilities.IsSpecified, 9) | Bit(Format.IsSpecified, 10)
                | Bit(Source.IsSpecified, 11) | Bit(BasePermissionRevision.IsSpecified, 12) | Bit(Tuning.IsSpecified, 13)
                | Bit(CollaboratorUris.IsSpecified, 14) | Bit(CreatedAt.IsSpecified, 15) | Bit(DeletedByOwner.IsSpecified, 16)
                | Bit(ChartNewEntries.IsSpecified, 17) | Bit(ChartUpdatedAt.IsSpecified, 18) | Bit(ChartRankType.IsSpecified, 19)),
        };
    }

    static T Choose<T>(FieldChange<T> patch, T current, bool seed, bool known)
        => seed && (known || current is not null) ? current : patch.Apply(current);
    static ulong Bit(bool specified, int bit) => specified ? 1UL << bit : 0;
}
