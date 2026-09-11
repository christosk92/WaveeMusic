using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Persistence;

public sealed partial class MemoryDataPersistence : ICatalogSearchPersistence
{
    public ValueTask<CatalogSearchCorpus> ReadSearchCorpusAsync(CatalogScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows = new List<CatalogSearchRow>();
        var pages = new List<CatalogSearchPage>();
        lock (_gate)
        {
            foreach (var record in _catalog.Values)
            {
                ct.ThrowIfCancellationRequested();
                if (CatalogSearchScopes.Rank(record.Key.Scope, scope) < 0) continue;
                var key = record.Key;
                if (key.Facet is FacetKind.TrackIdentity or FacetKind.AlbumIdentity or FacetKind.ArtistIdentity)
                {
                    var title = record.Value switch { TrackIdentityValue t => t.Title, AlbumIdentityValue a => a.Name,
                        ArtistIdentityValue a => a.Name, _ => null };
                    var artists = record.Value switch { TrackIdentityValue t => t.ArtistUris,
                        AlbumIdentityValue a => a.ArtistUris, _ => null };
                    rows.Add(new(key.Scope, key.Subject, EntityUri.KindOf(key.Subject), title,
                        (record.Value as TrackIdentityValue)?.AlbumUri, artists ?? []));
                }
                else if (key.Facet is FacetKind.AlbumTracks or FacetKind.ArtistDiscography)
                {
                    var page = record.Value as RelationPageValue;
                    pages.Add(new(key.Scope, key.Subject, key.Facet, key.Arguments.ToStorageKey(), page?.SnapshotId ?? "", page?.Offset ?? key.Arguments.Offset,
                        page?.Items.Select(item => item.EntityUri).ToArray() ?? [], record.Revision));
                }
            }
        }
        return ValueTask.FromResult(new CatalogSearchCorpus(rows.ToArray(), pages.ToArray()));
    }
}
