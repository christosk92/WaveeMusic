using System.Collections.Generic;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Allocation-free retained-size estimate for diagnostics and memory shedding, not disk accounting.</summary>
internal static class CatalogResidentSize
{
    public static long Estimate(ResourceKey key, CatalogValue? value) => 320 + Text(key.Subject) + Value(value);
    static long Text(string? text) => text is null ? 0 : 24 + 2L * text.Length;
    static long Strings(IReadOnlyList<string>? values)
    {
        if (values is null) return 0;
        long bytes = 24 + values.Count * 8L;
        foreach (var value in values) bytes += Text(value);
        return bytes;
    }
    static long Value(CatalogValue? value) => value switch
    {
        null => 0,
        TrackIdentityValue x => 128 + Text(x.Title) + Strings(x.ArtistUris) + Strings(x.UnlinkedArtistNames)
            + Text(x.AlbumUri) + Text(x.UnlinkedAlbumName) + Text(x.CanonicalUri) + Text(x.Image?.Url),
        EpisodeIdentityValue x => 96 + Text(x.Title) + Text(x.ShowUri) + Text(x.ShowName) + Text(x.Image?.Url),
        AlbumIdentityValue x => 96 + Text(x.Name) + Strings(x.ArtistUris) + Text(x.Cover?.Url),
        ArtistIdentityValue x => 48 + Text(x.Name) + Text(x.Image?.Url),
        UserIdentityValue x => 48 + Text(x.Name) + Text(x.Avatar?.Url),
        ShowIdentityValue x => 96 + Text(x.Name) + Text(x.Description) + Text(x.Publisher) + Text(x.Cover?.Url),
        PlaylistHeaderValue x => 256 + Text(x.Name) + Text(x.Description) + Text(x.OwnerUri) + Text(x.OwnerName) + Strings(x.CollaboratorUris) + Text(x.Cover?.Url),
        AlbumDetailValue x => 96 + Text(x.Label) + Text(x.CourtesyLine) + Strings(x.MoreByArtistUris),
        ArtistOverviewValue x => 256 + Text(x.Bio) + Strings(x.PopularReleaseUris) + Extras(x.Extras),
        EpisodeDetailValue x => 32 + Text(x.Description),
        DescriptorsValue x => 32 + Strings(x.Tags),
        RelationPageValue x => Relation(x),
        CatalogDocumentValue x => Document(x),
        _ => 128,
    };
    static long Relation(RelationPageValue page)
    {
        long bytes = 96 + Text(page.SnapshotId) + Text(page.SourceRevision) + Text(page.NextCursor) + page.Items.Count * 8L;
        foreach (var row in page.Items) bytes += 48 + Text(row.OccurrenceKey) + Text(row.EntityUri) + (row.Context is null ? 0 : 96);
        return bytes;
    }
    static long Items(IReadOnlyList<CatalogDocumentItem> items)
    {
        long bytes = 24 + items.Count * 8L;
        foreach (var item in items) bytes += 160 + Text(item.OccurrenceKey) + Text(item.EntityUri) + Text(item.PresentationTitle)
            + Text(item.Eyebrow) + Text(item.SearchMeta?.Subtitle) + Text(item.SearchMeta?.Detail);
        return bytes;
    }
    static long Document(CatalogDocumentValue document)
    {
        long bytes = 192 + Text(document.Greeting) + Strings(document.SuggestedQueries);
        foreach (var section in document.Sections) bytes += 96 + Text(section.Title) + Text(section.Subtitle) + Items(section.Items);
        foreach (var group in document.HomeGroups ?? []) bytes += 96 + Text(group.Title) + Text(group.Subtitle) + Items(group.Items);
        return bytes;
    }
    static long Extras(CatalogArtistExtras? extras) => extras is null ? 0 : 256
        + Strings(extras.PlaylistUris) + Strings(extras.MusicVideoUris) + Strings(extras.RelatedArtistUris)
        + 512L * ((extras.Concerts?.Count ?? 0) + (extras.Merch?.Count ?? 0) + (extras.Gallery?.Count ?? 0)
            + (extras.TopCities?.Count ?? 0) + (extras.ExternalLinks?.Count ?? 0));
}
