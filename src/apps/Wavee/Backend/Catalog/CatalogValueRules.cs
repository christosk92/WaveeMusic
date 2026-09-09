using System;
using System.Linq;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

static class CatalogValueRules
{
    public static CatalogValue Normalize(CatalogValue value, CatalogValue? current) => (value, current) switch
    {
        (TrackIdentityValue incoming, TrackIdentityValue old)
            => incoming with { ArtistUris = Same(incoming.ArtistUris, old.ArtistUris) ? old.ArtistUris : incoming.ArtistUris,
                UnlinkedArtistNames = Same(incoming.UnlinkedArtistNames, old.UnlinkedArtistNames) ? old.UnlinkedArtistNames : incoming.UnlinkedArtistNames },
        (AlbumIdentityValue incoming, AlbumIdentityValue old) when Same(incoming.ArtistUris, old.ArtistUris)
            => incoming with { ArtistUris = old.ArtistUris },
        (DescriptorsValue incoming, DescriptorsValue old) when Same(incoming.Tags, old.Tags)
            => incoming with { Tags = old.Tags },
        (RelationPageValue incoming, RelationPageValue old) when incoming.Items.SequenceEqual(old.Items)
            => incoming with { Items = old.Items },
        _ => value,
    };

    static bool Same(System.Collections.Generic.IReadOnlyList<string>? a, System.Collections.Generic.IReadOnlyList<string>? b)
        => ReferenceEquals(a, b) || a is not null && b is not null && a.SequenceEqual(b, StringComparer.Ordinal);

    public static void Validate(CatalogValue value)
    {
        if (value is PlayCountValue { Count: < 0 }) throw new ArgumentOutOfRangeException(nameof(value));
        if (value is RelationPageValue page)
        {
            if (page.Kind is not (FacetKind.AlbumTracks or FacetKind.ArtistDiscography or FacetKind.ArtistPopular
                or FacetKind.ArtistAppearsOn or FacetKind.ShowEpisodes or FacetKind.AlbumVersions or FacetKind.ArtistRelated))
                throw new ArgumentException("A relation page must identify a relation facet.", nameof(value));
            if (page.Offset < 0 || page.Total < 0 || string.IsNullOrEmpty(page.SnapshotId)
                || (page.Total is { } total && (long)page.Offset + page.Items.Count > total))
                throw new ArgumentException("Relation coverage is inconsistent.", nameof(value));
            var occurrences = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var item in page.Items)
                if (string.IsNullOrEmpty(item.OccurrenceKey) || string.IsNullOrEmpty(item.EntityUri)
                    || !occurrences.Add(item.OccurrenceKey))
                    throw new ArgumentException("Relation occurrence keys must be present and unique within a page.", nameof(value));
        }
        if (value is CatalogDocumentValue document && document.Kind is not (FacetKind.Home or FacetKind.Search or FacetKind.HomeSection or FacetKind.SearchSuggestions))
            throw new ArgumentException("A document must identify a document facet.", nameof(value));
    }
}
