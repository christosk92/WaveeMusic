using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Catalog;

/// <summary>Thin derived rows, read locally before matching. Entity payloads are read only for selected results.</summary>
public interface ICatalogSearchPersistence
{
    ValueTask<CatalogSearchCorpus> ReadSearchCorpusAsync(CatalogScope scope, CancellationToken ct);
}

public sealed record CatalogSearchRow(CatalogScope Scope, string Uri, EntityKind Kind, string? Title,
    string? AlbumUri, IReadOnlyList<string> ArtistUris);
public sealed record CatalogSearchPage(CatalogScope Scope, string ParentUri, FacetKind Facet, string ArgumentsKey, string SnapshotId, int Offset,
    IReadOnlyList<string> Children, long Revision);
public sealed record CatalogSearchCorpus(IReadOnlyList<CatalogSearchRow> Rows, IReadOnlyList<CatalogSearchPage> Pages)
{
    public static CatalogSearchCorpus Empty { get; } = new([], []);
}

public static class CatalogSearchScopes
{
    /// <summary>Providers are federated; all account and catalog context dimensions remain exact.</summary>
    public static int Rank(CatalogScope candidate, CatalogScope current)
    {
        if (candidate == current with { Provider = candidate.Provider }) return 0;
        if (!current.ContextKnown || candidate.ContextKnown || candidate.StorageAccount != current.StorageAccount
            || candidate.ProviderAccount.Length != 0 || candidate.Market.Length != 0 || candidate.Catalogue.Length != 0
            || candidate.Tier != -1 || candidate.ExplicitFilter) return -1;
        return candidate.Locale == current.Locale ? 1 : candidate.Locale.Length == 0 ? 2 : -1;
    }
}
