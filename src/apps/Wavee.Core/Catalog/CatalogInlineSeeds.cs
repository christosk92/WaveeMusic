namespace Wavee.Core.Catalog;

/// <summary>Inline cards may fill missing identity fields, including an existing partial facet.</summary>
static class CatalogInlineSeeds
{
    public static CatalogValue Merge(CatalogValue seed, CatalogValue? current) => seed switch
    {
        TrackIdentityValue x => new TrackIdentityPatch(Set(x.Title), Set(x.ArtistUris), Set(x.AlbumUri),
            Set(x.DurationMs), Set(x.IsExplicit), Set(x.Image), Set(x.Isrc), Set(x.Year), Set(x.CanonicalUri),
            Set(x.Source), Set(x.Origin), Set(x.UnlinkedArtistNames), Set(x.UnlinkedAlbumName)).Apply(current, true),
        EpisodeIdentityValue x => new EpisodeIdentityPatch(Set(x.Title), Set(x.ShowUri), Set(x.DurationMs),
            Set(x.Image), Set(x.PublishedAt), Set(x.ShowName)).Apply(current, true),
        AlbumIdentityValue x => new AlbumIdentityPatch(Set(x.Name), Set(x.Cover), Set(x.ArtistUris), Set(x.Year),
            Set(x.TrackCount), Set(x.Kind)).Apply(current, true),
        ArtistIdentityValue x => new ArtistIdentityPatch(Set(x.Name), Set(x.Image)).Apply(current, true),
        ShowIdentityValue x => new ShowIdentityPatch(Set(x.Name), Set(x.Publisher), Set(x.Cover),
            Set(x.Description), Set(x.EpisodeCount)).Apply(current, true),
        UserIdentityValue x => new UserIdentityPatch(Set(x.Name), Set(x.Avatar)).Apply(current, true),
        PlaylistHeaderValue x => new PlaylistHeaderPatch(Set(x.Name), Set(x.Description), Set(x.Cover),
            Set(x.OwnerUri), Set(x.TrackCount), Set(x.IsPublic), Set(x.Edition), Set(x.NextUpdateAt),
            Set(x.OwnerName), Set(x.Capabilities), Set(x.Format), Set(x.Source), Set(x.BasePermissionRevision),
            Set(x.Tuning), Set(x.CollaboratorUris), Set(x.CreatedAt), Set(x.DeletedByOwner), Set(x.ChartNewEntries),
            Set(x.ChartUpdatedAt), Set(x.ChartRankType)).Apply(current, true),
        _ => current ?? seed,
    };
    static FieldChange<T> Set<T>(T value) => FieldChange<T>.Set(value);
}
