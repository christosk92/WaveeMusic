namespace Wavee.Core.Catalog;

public sealed record EpisodeIdentityPatch(
    FieldChange<string?> Title = default,
    FieldChange<string?> ShowUri = default,
    FieldChange<long?> DurationMs = default,
    FieldChange<Image?> Image = default,
    FieldChange<DateTimeOffset?> PublishedAt = default, FieldChange<string?> ShowName = default) : CatalogPatch
{
    public override FacetKind Facet => FacetKind.EpisodeIdentity;
    public override CatalogValue Apply(CatalogValue? current, bool fillUnknownOnly = false)
    {
        var row = current switch
        {
            null => new EpisodeIdentityValue(),
            EpisodeIdentityValue value => value,
            _ => throw new ArgumentException("Patch received a different facet.", nameof(current)),
        };
        return row with
        {
            Title = IdentityPatchFields.Choose(Title, row.Title, fillUnknownOnly, (row.KnownFields & 1UL) != 0),
            ShowUri = IdentityPatchFields.Choose(ShowUri, row.ShowUri, fillUnknownOnly, (row.KnownFields & 2UL) != 0),
            DurationMs = IdentityPatchFields.Choose(DurationMs, row.DurationMs, fillUnknownOnly, (row.KnownFields & 4UL) != 0),
            Image = IdentityPatchFields.Choose(Image, row.Image, fillUnknownOnly, (row.KnownFields & 8UL) != 0),
            PublishedAt = IdentityPatchFields.Choose(PublishedAt, row.PublishedAt, fillUnknownOnly, (row.KnownFields & 16UL) != 0),
            ShowName = IdentityPatchFields.Choose(ShowName, row.ShowName, fillUnknownOnly, (row.KnownFields & 32UL) != 0),
            KnownFields = row.KnownFields | (fillUnknownOnly ? 0UL :
                (ShowName.IsSpecified ? 32UL : 0UL) | (Title.IsSpecified ? 1UL : 0UL) | (ShowUri.IsSpecified ? 2UL : 0UL) | (DurationMs.IsSpecified ? 4UL : 0UL) | (Image.IsSpecified ? 8UL : 0UL) | (PublishedAt.IsSpecified ? 16UL : 0UL)),
        };
    }
}

public sealed record AlbumIdentityPatch(
    FieldChange<string?> Name = default,
    FieldChange<Image?> Cover = default,
    FieldChange<IReadOnlyList<string>?> ArtistUris = default,
    FieldChange<int?> Year = default,
    FieldChange<int?> TrackCount = default,
    FieldChange<AlbumKind> Kind = default) : CatalogPatch
{
    public override FacetKind Facet => FacetKind.AlbumIdentity;
    public override CatalogValue Apply(CatalogValue? current, bool fillUnknownOnly = false)
    {
        var row = current switch
        {
            null => new AlbumIdentityValue(),
            AlbumIdentityValue value => value,
            _ => throw new ArgumentException("Patch received a different facet.", nameof(current)),
        };
        return row with
        {
            Name = IdentityPatchFields.Choose(Name, row.Name, fillUnknownOnly, (row.KnownFields & 1UL) != 0),
            Cover = IdentityPatchFields.Choose(Cover, row.Cover, fillUnknownOnly, (row.KnownFields & 2UL) != 0),
            ArtistUris = IdentityPatchFields.Choose(ArtistUris, row.ArtistUris, fillUnknownOnly, (row.KnownFields & 4UL) != 0),
            Year = IdentityPatchFields.Choose(Year, row.Year, fillUnknownOnly, (row.KnownFields & 8UL) != 0),
            TrackCount = IdentityPatchFields.Choose(TrackCount, row.TrackCount, fillUnknownOnly, (row.KnownFields & 16UL) != 0),
            Kind = fillUnknownOnly && (row.KnownFields & 32UL) != 0 ? row.Kind : Kind.Apply(row.Kind),
            KnownFields = row.KnownFields | (Kind.IsSpecified ? 32UL : 0UL) | (fillUnknownOnly ? 0UL :
                (Name.IsSpecified ? 1UL : 0UL) | (Cover.IsSpecified ? 2UL : 0UL) | (ArtistUris.IsSpecified ? 4UL : 0UL) | (Year.IsSpecified ? 8UL : 0UL) | (TrackCount.IsSpecified ? 16UL : 0UL) | (Kind.IsSpecified ? 32UL : 0UL)),
        };
    }
}

public sealed record ArtistIdentityPatch(
    FieldChange<string?> Name = default,
    FieldChange<Image?> Image = default) : CatalogPatch
{
    public override FacetKind Facet => FacetKind.ArtistIdentity;
    public override CatalogValue Apply(CatalogValue? current, bool fillUnknownOnly = false)
    {
        var row = current switch
        {
            null => new ArtistIdentityValue(),
            ArtistIdentityValue value => value,
            _ => throw new ArgumentException("Patch received a different facet.", nameof(current)),
        };
        return row with
        {
            Name = IdentityPatchFields.Choose(Name, row.Name, fillUnknownOnly, (row.KnownFields & 1UL) != 0),
            Image = IdentityPatchFields.Choose(Image, row.Image, fillUnknownOnly, (row.KnownFields & 2UL) != 0),
            KnownFields = row.KnownFields | (fillUnknownOnly ? 0UL :
                (Name.IsSpecified ? 1UL : 0UL) | (Image.IsSpecified ? 2UL : 0UL)),
        };
    }
}

public sealed record ShowIdentityPatch(
    FieldChange<string?> Name = default,
    FieldChange<string?> Publisher = default,
    FieldChange<Image?> Cover = default,
    FieldChange<string?> Description = default,
    FieldChange<int?> EpisodeCount = default) : CatalogPatch
{
    public override FacetKind Facet => FacetKind.ShowIdentity;
    public override CatalogValue Apply(CatalogValue? current, bool fillUnknownOnly = false)
    {
        var row = current switch
        {
            null => new ShowIdentityValue(),
            ShowIdentityValue value => value,
            _ => throw new ArgumentException("Patch received a different facet.", nameof(current)),
        };
        return row with
        {
            Name = IdentityPatchFields.Choose(Name, row.Name, fillUnknownOnly, (row.KnownFields & 1UL) != 0),
            Publisher = IdentityPatchFields.Choose(Publisher, row.Publisher, fillUnknownOnly, (row.KnownFields & 2UL) != 0),
            Cover = IdentityPatchFields.Choose(Cover, row.Cover, fillUnknownOnly, (row.KnownFields & 4UL) != 0),
            Description = IdentityPatchFields.Choose(Description, row.Description, fillUnknownOnly, (row.KnownFields & 8UL) != 0),
            EpisodeCount = IdentityPatchFields.Choose(EpisodeCount, row.EpisodeCount, fillUnknownOnly, (row.KnownFields & 16UL) != 0),
            KnownFields = row.KnownFields | (fillUnknownOnly ? 0UL :
                (Name.IsSpecified ? 1UL : 0UL) | (Publisher.IsSpecified ? 2UL : 0UL) | (Cover.IsSpecified ? 4UL : 0UL) | (Description.IsSpecified ? 8UL : 0UL) | (EpisodeCount.IsSpecified ? 16UL : 0UL)),
        };
    }
}

public sealed record UserIdentityPatch(
    FieldChange<string?> Name = default,
    FieldChange<Image?> Avatar = default) : CatalogPatch
{
    public override FacetKind Facet => FacetKind.UserIdentity;
    public override CatalogValue Apply(CatalogValue? current, bool fillUnknownOnly = false)
    {
        var row = current switch
        {
            null => new UserIdentityValue(),
            UserIdentityValue value => value,
            _ => throw new ArgumentException("Patch received a different facet.", nameof(current)),
        };
        return row with
        {
            Name = IdentityPatchFields.Choose(Name, row.Name, fillUnknownOnly, (row.KnownFields & 1UL) != 0),
            Avatar = IdentityPatchFields.Choose(Avatar, row.Avatar, fillUnknownOnly, (row.KnownFields & 2UL) != 0),
            KnownFields = row.KnownFields | (fillUnknownOnly ? 0UL :
                (Name.IsSpecified ? 1UL : 0UL) | (Avatar.IsSpecified ? 2UL : 0UL)),
        };
    }
}

static class IdentityPatchFields
{
    internal static T Choose<T>(FieldChange<T> patch, T current, bool seed, bool known)
        => seed && (known || current is not null) ? current : patch.Apply(current);
}
