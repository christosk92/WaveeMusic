using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Persistence;

public sealed partial class SqliteColdStore : ICatalogSearchPersistence
{
    public async ValueTask<CatalogSearchCorpus> ReadSearchCorpusAsync(CatalogScope scope, CancellationToken ct)
        => await Task.Run(() => ReadSearchCorpus(scope, ct), ct).ConfigureAwait(false);

    CatalogSearchCorpus ReadSearchCorpus(CatalogScope scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_readLock)
        {
            // One read transaction keeps derived names and ordered edges at the same durable commit.
            using var tx = _read.BeginTransaction();
            var scopes = new Dictionary<long, CatalogScope>();
            using (var command = _read.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "SELECT scope_id,provider,storage_account,provider_account,locale,market,catalogue,tier,explicit_filter,context_known " +
                    "FROM catalog_scope WHERE storage_account=$storage;";
                command.Parameters.AddWithValue("$storage", scope.StorageAccount);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var candidate = new CatalogScope(reader.GetString(1), reader.GetString(3), reader.GetString(4),
                        reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetBoolean(8), reader.GetBoolean(9), reader.GetString(2));
                    if (CatalogSearchScopes.Rank(candidate, scope) >= 0) scopes.Add(reader.GetInt64(0), candidate);
                }
            }
            if (scopes.Count == 0) return CatalogSearchCorpus.Empty;
            // IDs originate in SQLite, never user text. Matching stays Unicode-aware C#, not SQLite NOCASE.
            var ids = string.Join(',', scopes.Keys.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var rows = new List<CatalogSearchRow>();
            using (var command = _read.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "SELECT scope_id,uri,kind,title,album_uri,artist_uris FROM catalog_search WHERE scope_id IN(" + ids + ");";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var kind = (Wavee.Core.EntityKind)reader.GetInt32(2);
                    if (kind is not (Wavee.Core.EntityKind.Artist or Wavee.Core.EntityKind.Album or Wavee.Core.EntityKind.Track)) continue;
                    rows.Add(new(scopes[reader.GetInt64(0)], reader.GetString(1), kind,
                        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? [] : CatalogPayloadCodec.DecodeStrings(Encoding.UTF8.GetBytes(reader.GetString(5)))));
                }
            }
            var pages = new Dictionary<(long Scope, string Parent, FacetKind Facet, string Args), (CatalogSearchPage Header, List<string> Children)>();
            using (var command = _read.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "SELECT scope_id,subject,facet,arguments,payload,revision FROM catalog_resource " +
                    "WHERE scope_id IN(" + ids + ") AND facet IN(30,31);";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    long id = reader.GetInt64(0);
                    string parent = reader.GetString(1), args = reader.GetString(3);
                    var facet = (FacetKind)reader.GetInt32(2);
                    var value = reader.IsDBNull(4) ? null : (RelationPageValue)CatalogPayloadCodec.Decode(facet,
                        PayloadCodec.Decode(reader.GetFieldValue<byte[]>(4)));
                    pages[(id, parent, facet, args)] = (new(scopes[id], parent, facet, args,
                        value?.SnapshotId ?? "", value?.Offset ?? 0, [], reader.GetInt64(5)), []);
                }
            }
            using (var command = _read.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "SELECT scope_id,parent_subject,facet,arguments,child_uri FROM catalog_relation_item " +
                    "WHERE scope_id IN(" + ids + ") AND facet IN(30,31) ORDER BY scope_id,parent_subject,facet,arguments,ordinal;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (pages.TryGetValue((reader.GetInt64(0), reader.GetString(1), (FacetKind)reader.GetInt32(2), reader.GetString(3)), out var page))
                        page.Children.Add(reader.GetString(4));
                }
            }
            tx.Commit();
            return new(rows.ToArray(), pages.Values.Select(page => page.Header with { Children = page.Children.ToArray() }).ToArray());
        }
    }
}
