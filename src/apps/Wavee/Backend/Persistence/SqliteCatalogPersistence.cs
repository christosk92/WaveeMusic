using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Wavee.Backend.Catalog;
using Wavee.Core.Catalog;

namespace Wavee.Backend.Persistence;

public sealed partial class SqliteColdStore : ICatalogPersistence
{
    // Finding #8: a corrupt row is recorded here (never on `_read`, which cannot write) and purged the next time
    // a write-connection operation runs — see `PurgeCorruptCatalogRowsLocked`.
    readonly ConcurrentQueue<ResourceKey> _corruptCatalogKeys = new();
    // Read-path scope ids. A catalog_scope row is immutable once written and is never deleted at runtime, so an
    // id can be cached for the life of the store. `_writtenScopeIds` holds the ids a write transaction created or
    // observed while it was still open (the write connection sees its own uncommitted rows): they are cleared when
    // a catalog write begins and only published to the cache once that transaction has actually committed.
    readonly object _scopeIdGate = new();
    readonly Dictionary<CatalogScope, long> _catalogScopeIds = new();
    readonly List<(CatalogScope Scope, long Id)> _writtenScopeIds = new();
    int _readStatements;

    /// <summary>SQL statements the catalog read path actually executed. A 1500-key resident wave must stay O(1)
    /// (one scope lookup + one <c>IN</c> per facet), not one statement per key.</summary>
    public int ReadStatements => Volatile.Read(ref _readStatements);

    void EnsureCatalogSchema()
    {
        lock (_connLock)
        {
            ExecLocked("""
                CREATE TABLE IF NOT EXISTS catalog_scope (
                  scope_id INTEGER PRIMARY KEY, provider TEXT NOT NULL, storage_account TEXT NOT NULL,
                  provider_account TEXT NOT NULL, locale TEXT NOT NULL, market TEXT NOT NULL,
                  catalogue TEXT NOT NULL, tier INTEGER NOT NULL, explicit_filter INTEGER NOT NULL,
                  context_known INTEGER NOT NULL CHECK(context_known IN(0,1)),
                  UNIQUE(provider,storage_account,provider_account,locale,market,catalogue,tier,explicit_filter,context_known));
                CREATE TABLE IF NOT EXISTS catalog_resource (
                  scope_id INTEGER NOT NULL, subject TEXT NOT NULL, facet INTEGER NOT NULL, arguments TEXT NOT NULL,
                  state INTEGER NOT NULL CHECK(state IN(1,2)), provenance INTEGER NOT NULL,
                  payload_version INTEGER NOT NULL, fmt INTEGER NOT NULL, payload BLOB, size INTEGER NOT NULL,
                  revision INTEGER NOT NULL, fetched_at INTEGER NOT NULL, expires_at INTEGER NOT NULL,
                  updated_at INTEGER NOT NULL, last_access INTEGER NOT NULL,
                  PRIMARY KEY(scope_id,subject,facet,arguments),
                  FOREIGN KEY(scope_id) REFERENCES catalog_scope(scope_id), CHECK(state<>2 OR payload IS NULL)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS catalog_relation_item (
                  scope_id INTEGER NOT NULL, parent_subject TEXT NOT NULL, facet INTEGER NOT NULL, arguments TEXT NOT NULL,
                  ordinal INTEGER NOT NULL, occurrence_key TEXT NOT NULL, child_uri TEXT NOT NULL,
                  context_version INTEGER NOT NULL, context_payload BLOB, size INTEGER NOT NULL,
                  PRIMARY KEY(scope_id,parent_subject,facet,arguments,ordinal),
                  FOREIGN KEY(scope_id,parent_subject,facet,arguments)
                    REFERENCES catalog_resource(scope_id,subject,facet,arguments) ON DELETE CASCADE) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_catalog_relation_child ON catalog_relation_item(scope_id,child_uri);
                CREATE INDEX IF NOT EXISTS ix_catalog_resource_gc ON catalog_resource(facet,last_access,updated_at);
                CREATE TABLE IF NOT EXISTS catalog_search (
                  scope_id INTEGER NOT NULL, uri TEXT NOT NULL, kind INTEGER NOT NULL, title TEXT, subtitle TEXT,
                  image_url TEXT, duration_ms INTEGER, flags INTEGER NOT NULL, album_uri TEXT, artist_uris TEXT,
                  revision INTEGER NOT NULL, PRIMARY KEY(scope_id,uri),
                  FOREIGN KEY(scope_id) REFERENCES catalog_scope(scope_id)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_catalog_search_album ON catalog_search(scope_id,album_uri);
                CREATE TABLE IF NOT EXISTS extension_cache (
                  scope_id INTEGER NOT NULL, entity_uri TEXT NOT NULL, extension_kind INTEGER NOT NULL,
                  wire_status INTEGER NOT NULL CHECK(wire_status IN(200,404)), payload BLOB, etag TEXT,
                  offline_ttl INTEGER NOT NULL, expires_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
                  last_access INTEGER NOT NULL, PRIMARY KEY(scope_id,entity_uri,extension_kind),
                  FOREIGN KEY(scope_id) REFERENCES catalog_scope(scope_id),
                  CHECK(wire_status<>404 OR(payload IS NULL AND etag IS NULL))) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_extension_cache_expiry ON extension_cache(expires_at);
                CREATE INDEX IF NOT EXISTS ix_extension_cache_lru ON extension_cache(last_access);
                -- SqliteColdStore.CatalogGcHasWork probes `updated_at<cutoff`. Without this index that probe is a
                -- full scan of every cached payload (73 ms on a 124 MB library); with it, a covering seek (0.5 ms).
                CREATE INDEX IF NOT EXISTS ix_extension_cache_gc ON extension_cache(updated_at);
                """);
        }
    }

    public async ValueTask<IReadOnlyList<CatalogRecord?>> ReadManyAsync(IReadOnlyList<ResourceKey> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return Array.Empty<CatalogRecord?>();
        return await Task.Run(() => ReadCatalogBatch(keys, ct), ct).ConfigureAwait(false);
    }

    public async ValueTask<CatalogTransportRecord?> ReadTransportAsync(CatalogScope scope, string subject,
        int extensionKind, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (_readLock)
            {
                var scopeId = FindCatalogScope(_read, scope, null);
                if (scopeId is null) return null;
                using var command = _read.CreateCommand();
                command.CommandText = "SELECT etag,payload,updated_at FROM extension_cache " +
                    "WHERE scope_id=$scope AND entity_uri=$uri AND extension_kind=$kind AND wire_status=200;";
                command.Parameters.AddWithValue("$scope", scopeId.Value);
                command.Parameters.AddWithValue("$uri", subject);
                command.Parameters.AddWithValue("$kind", extensionKind);
                using var reader = command.ExecuteReader();
                return reader.Read() && !reader.IsDBNull(1)
                    ? new CatalogTransportRecord(scope, subject, extensionKind, reader.IsDBNull(0) ? null : reader.GetString(0),
                        reader.GetFieldValue<byte[]>(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)))
                    : null;
            }
        }, ct).ConfigureAwait(false);
    }

    // One statement per (scope, facet) group. A 1500-key identity wave used to be chunked at 400 subjects and
    // decoded under the read lock; that was hundreds of milliseconds of serialized JSON on the launch path.
    const int SubjectsPerStatement = 16_000;

    IReadOnlyList<CatalogRecord?> ReadCatalogBatch(IReadOnlyList<ResourceKey> keys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var records = new CatalogRecord?[keys.Count];
        var all = new List<int>(keys.Count);
        for (int i = 0; i < keys.Count; i++) all.Add(i);
        var missing = ReadAndDecodePass(keys, all, records, static key => key.Scope, importedContext: false, ct);
        var imported = new List<int>();
        foreach (int index in missing) if (ImportedFallbackApplies(keys[index])) imported.Add(index);
        if (imported.Count == 0) return records;
        var stillMissing = ReadAndDecodePass(keys, imported, records, ImportedScope, importedContext: true, ct);
        var localeless = new List<int>();
        foreach (int index in stillMissing) if (keys[index].Scope.Locale.Length > 0) localeless.Add(index);
        if (localeless.Count > 0)
            ReadAndDecodePass(keys, localeless, records, static key => ImportedScope(key) with { Locale = "" },
                importedContext: true, ct);
        return records;
    }

    List<int> ReadAndDecodePass(IReadOnlyList<ResourceKey> keys, List<int> indices, CatalogRecord?[] records,
        Func<ResourceKey, CatalogScope> scopeOf, bool importedContext, CancellationToken ct)
    {
        List<RawCatalogRow> raws;
        List<int> unresolved;
        lock (_readLock) (unresolved, raws) = ReadCatalogPass(keys, indices, scopeOf, importedContext, ct);
        foreach (var raw in raws)
        {
            CatalogValue? value = null;
            string? corruptReason = null;
            if (raw.Version != CatalogPayloadCodec.Version) corruptReason = "payload_version mismatch";
            else
                try { value = raw.Payload is null ? null : CatalogPayloadCodec.Decode(raw.Facet, PayloadCodec.Decode(raw.Payload)); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException
                    or ArgumentException or OverflowException or IOException)
                { corruptReason = ex.GetType().Name; }
            if (corruptReason is not null)
            {
                WaveeLog.Instance.Event(WaveeLogLevel.Warning, "catalog", "catalog.corrupt.row",
                    "Corrupt catalog row treated as a miss and queued for purge.", fields:
                    [WaveeLogField.Of("subject", raw.Subject), WaveeLogField.Of("facet", raw.Facet.ToString()),
                     WaveeLogField.Of("reason", corruptReason)]);
                _corruptCatalogKeys.Enqueue(keys[raw.Owners[0]] with { Scope = raw.Scope });
                unresolved.AddRange(raw.Owners);
                continue;
            }
            if (value is RelationPageValue page)
                value = page with { Items = raw.Items ?? [] };
            var knowledge = (Knowledge)raw.State;
            foreach (int index in raw.Owners)
            {
                if (importedContext && knowledge != Knowledge.Present) continue;
                var record = new CatalogRecord(keys[index], knowledge, value, (CatalogProvenance)raw.Provenance,
                    DateTimeOffset.FromUnixTimeMilliseconds(raw.Fetched),
                    DateTimeOffset.FromUnixTimeMilliseconds(raw.Expires), raw.Revision);
                records[index] = importedContext
                    ? record with { Value = WithoutContext(value), Provenance = CatalogProvenance.ImportedUnknownContext,
                        FetchedAt = default, ExpiresAt = default, Revision = 0 }
                    : record;
            }
        }
        return unresolved;
    }

    /// <summary>SQL half of a pass: groups <paramref name="indices"/> by (scope, facet) and issues one
    /// <c>subject IN (…)</c> per group. Payloads are returned raw so decode can run off <c>_readLock</c>.</summary>
    (List<int> Unresolved, List<RawCatalogRow> Raws) ReadCatalogPass(IReadOnlyList<ResourceKey> keys, List<int> indices,
        Func<ResourceKey, CatalogScope> scopeOf, bool importedContext, CancellationToken ct)
    {
        var unresolved = new List<int>();
        var raws = new List<RawCatalogRow>();
        var groups = new Dictionary<(CatalogScope Scope, FacetKind Facet), List<int>>();
        foreach (int index in indices)
        {
            var bucket = (scopeOf(keys[index]), keys[index].Facet);
            if (!groups.TryGetValue(bucket, out var owned)) groups[bucket] = owned = [];
            owned.Add(index);
        }
        foreach (var (group, members) in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadScopeId(group.Scope) is not { } scopeId) { unresolved.AddRange(members); continue; }
            var sharing = new Dictionary<(string Subject, string Arguments), List<int>>();
            var subjects = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (int index in members)
            {
                var row = (keys[index].Subject, keys[index].Arguments.ToStorageKey());
                if (!sharing.TryGetValue(row, out var owners)) sharing[row] = owners = [];
                owners.Add(index);
                if (seen.Add(keys[index].Subject)) subjects.Add(keys[index].Subject);
            }
            string? onlyArguments = null;
            foreach (var variant in sharing.Keys)
            { if (onlyArguments is null) onlyArguments = variant.Arguments; else if (onlyArguments != variant.Arguments) { onlyArguments = null; break; } }
            var resolved = new HashSet<int>();
            for (int start = 0; start < subjects.Count; start += SubjectsPerStatement)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(SubjectsPerStatement, subjects.Count - start);
                var text = new StringBuilder("SELECT subject,arguments,state,provenance,payload_version,payload,revision," +
                    "fetched_at,expires_at FROM catalog_resource WHERE scope_id=$scope AND facet=$facet AND ");
                using var command = _read.CreateCommand();
                command.Parameters.AddWithValue("$scope", scopeId);
                command.Parameters.AddWithValue("$facet", (int)group.Facet);
                if (onlyArguments is not null)
                {
                    text.Append("arguments=$args AND ");
                    command.Parameters.AddWithValue("$args", onlyArguments);
                }
                text.Append("subject IN (");
                for (int i = 0; i < count; i++)
                {
                    string name = "$s" + i.ToString(CultureInfo.InvariantCulture);
                    if (i > 0) text.Append(',');
                    text.Append(name);
                    command.Parameters.AddWithValue(name, subjects[start + i]);
                }
                command.CommandText = text.Append(");").ToString();
                Interlocked.Increment(ref _readStatements);
                var rows = new List<(string Subject, string Arguments, int State, int Provenance, int Version,
                    byte[]? Payload, long Revision, long Fetched, long Expires)>();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                            reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5),
                            reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8)));
                    }
                foreach (var row in rows)
                {
                    if (!sharing.TryGetValue((row.Subject, row.Arguments), out var owners)) continue;
                    foreach (int index in owners) resolved.Add(index);
                    CatalogRelationItem[]? items = null;
                    if (IsRelationFacet(group.Facet))
                        items = ReadRelationItems(scopeId, row.Subject, group.Facet, row.Arguments);
                    raws.Add(new(group.Scope, group.Facet, row.Subject, owners, row.State, row.Provenance, row.Version,
                        row.Payload, row.Revision, row.Fetched, row.Expires, items));
                    if (importedContext && (Knowledge)row.State != Knowledge.Present)
                    { /* still resolved — an imported non-Present row stops the locale-less fallthrough */ }
                }
            }
            foreach (int index in members) if (!resolved.Contains(index)) unresolved.Add(index);
        }
        return (unresolved, raws);
    }

    static bool IsRelationFacet(FacetKind facet)
        => facet is FacetKind.AlbumTracks or FacetKind.AlbumVersions or FacetKind.ArtistPopular
            or FacetKind.ArtistDiscography or FacetKind.ArtistAppearsOn or FacetKind.ShowEpisodes
            or FacetKind.ArtistRelated;

    readonly record struct RawCatalogRow(CatalogScope Scope, FacetKind Facet, string Subject, List<int> Owners,
        int State, int Provenance, int Version, byte[]? Payload, long Revision, long Fetched, long Expires,
        CatalogRelationItem[]? Items);

    /// <summary>A row read outside its authenticated context keeps only the fields that do not depend on one.</summary>
    static CatalogValue? WithoutContext(CatalogValue? value) => value is PlaylistHeaderValue header
        ? header with { Capabilities = null, Tuning = null, BasePermissionRevision = null, IsPublic = null }
        : value;

    static bool ImportedFallbackApplies(ResourceKey key)
        => key.Scope.ContextKnown && key.Facet is (FacetKind.TrackIdentity or FacetKind.EpisodeIdentity
            or FacetKind.AlbumIdentity or FacetKind.ArtistIdentity or FacetKind.PlaylistHeader or FacetKind.ShowIdentity
            or FacetKind.UserIdentity or FacetKind.PlayCount or FacetKind.Descriptors or FacetKind.AudioAttributes
            or FacetKind.Publishing or FacetKind.VisualIdentity or FacetKind.EpisodeDetail or FacetKind.VideoAssociation
            or FacetKind.AlbumDetail or FacetKind.ArtistOverview or FacetKind.AlbumTracks or FacetKind.AlbumVersions
            or FacetKind.ArtistPopular or FacetKind.ArtistDiscography or FacetKind.ArtistAppearsOn or FacetKind.ShowEpisodes);

    static CatalogScope ImportedScope(ResourceKey key) => key.Scope with { ProviderAccount = "", Market = "",
        Catalogue = "", Tier = -1, ExplicitFilter = false, ContextKnown = false };

    CatalogRelationItem[] ReadRelationItems(long scope, string subject, FacetKind facet, string arguments)
    {
        var items = new List<CatalogRelationItem>();
        using var command = _read.CreateCommand();
        command.CommandText = "SELECT occurrence_key,child_uri,context_payload FROM catalog_relation_item " +
            "WHERE scope_id=$scope AND parent_subject=$subject AND facet=$facet AND arguments=$args ORDER BY ordinal;";
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$facet", (int)facet);
        command.Parameters.AddWithValue("$args", arguments);
        Interlocked.Increment(ref _readStatements);
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null :
            CatalogPayloadCodec.DecodeRelationContext(reader.GetFieldValue<byte[]>(2))));
        return items.ToArray();
    }

    /// <summary>Scope ids for the read path. A catalog_scope row is immutable once committed and is never deleted
    /// at runtime (only a schema reset drops the table, before any read can run), so a found id is cached forever.</summary>
    long? ReadScopeId(CatalogScope scope)
    {
        lock (_scopeIdGate) if (_catalogScopeIds.TryGetValue(scope, out long cached)) return cached;
        Interlocked.Increment(ref _readStatements);
        if (FindCatalogScope(_read, scope, null) is not { } id) return null;
        lock (_scopeIdGate) _catalogScopeIds[scope] = id;
        return id;
    }

    public ValueTask CommitAsync(CatalogCommit commit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            PurgeCorruptCatalogRowsLocked(tx);
            WriteCatalogCommitLocked(commit, tx, ct);
            tx.Commit();
            PublishWrittenScopeIdsLocked();
        }
        return ValueTask.CompletedTask;
    }

    // Finding #8: drains keys `ReadCatalog` found corrupt (on the read-only connection) using the write connection.
    // `catalog_relation_item` cascades on delete; the identity-facet range also backs a `catalog_search` row.
    void PurgeCorruptCatalogRowsLocked(SqliteTransaction tx)
    {
        while (_corruptCatalogKeys.TryDequeue(out var key))
        {
            if (FindCatalogScope(_conn, key.Scope, tx) is not { } scope) continue;
            using (var delete = ReplicaCommand(tx, "DELETE FROM catalog_resource WHERE scope_id=$scope AND subject=$subject AND facet=$facet AND arguments=$args;"))
            { AddCatalogKey(delete, scope, key); delete.ExecuteNonQuery(); }
            if ((int)key.Facet is >= 1 and <= 7)
                using (var search = ReplicaCommand(tx, "DELETE FROM catalog_search WHERE scope_id=$scope AND uri=$uri;", ("$scope", scope), ("$uri", key.Subject)))
                    search.ExecuteNonQuery();
        }
    }

    void WriteCatalogCommitLocked(CatalogCommit commit, SqliteTransaction tx, CancellationToken ct)
    {
            // A previous transaction that rolled back (or one this store cannot see commit — the replica writer
            // owns its own transaction) leaves ids here that were never durable. Drop them before this one runs.
            _writtenScopeIds.Clear();
            foreach (var record in commit.Records)
            {
                ct.ThrowIfCancellationRequested();
                WriteCatalogRecordLocked(record, tx);
            }
            if (commit.Transports is not null)
                foreach (var record in commit.Transports)
                {
                    ct.ThrowIfCancellationRequested();
                    var scope = GetOrCreateCatalogScopeLocked(record.Scope, tx);
                    ExecuteReplicaLocked(tx, "INSERT INTO extension_cache(scope_id,entity_uri,extension_kind,wire_status,payload,etag,offline_ttl,expires_at,updated_at,last_access) " +
                        "VALUES($scope,$uri,$kind,200,$payload,$etag,0,0,$at,$at) " +
                        "ON CONFLICT(scope_id,entity_uri,extension_kind) DO UPDATE SET wire_status=200,payload=excluded.payload," +
                        "etag=excluded.etag,updated_at=excluded.updated_at,last_access=excluded.last_access;",
                        ("$scope", scope), ("$uri", record.Subject), ("$kind", record.ExtensionKind),
                        ("$payload", record.Payload), ("$etag", record.Etag), ("$at", record.StoredAt.ToUnixTimeMilliseconds()));
                }
            ExecuteReplicaLocked(tx, "INSERT INTO meta(key,value) VALUES('catalog_revision',$v) " +
                "ON CONFLICT(key) DO UPDATE SET value=MAX(CAST(value AS INTEGER),CAST(excluded.value AS INTEGER));", ("$v", commit.Revision));
    }

    void WriteCatalogRecordLocked(CatalogRecord record, SqliteTransaction tx)
    {
        if (record.Knowledge is not (Knowledge.Present or Knowledge.Absent)) return;
        if (record.Knowledge == Knowledge.Present && record.Value is null)
            throw new InvalidOperationException("Present catalog resources require a typed value.");
        if (record.Value is { } value && value.Facet != record.Key.Facet)
            throw new InvalidOperationException("Catalog resource key and value facets differ.");
        long scope = GetOrCreateCatalogScopeLocked(record.Key.Scope, tx);
        var key = record.Key;
        var page = record.Value as RelationPageValue;
        var storedValue = page is null ? record.Value : page with { Items = Array.Empty<CatalogRelationItem>() };
        byte[]? payload = storedValue is null ? null : PayloadCodec.Encode(CatalogPayloadCodec.Encode(storedValue), PayloadCodec.FmtZstd);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var write = ReplicaCommand(tx, "INSERT INTO catalog_resource(scope_id,subject,facet,arguments,state,provenance,payload_version," +
            "fmt,payload,size,revision,fetched_at,expires_at,updated_at,last_access) " +
            "VALUES($scope,$subject,$facet,$args,$state,$provenance,$pv,$fmt,$payload,$size,$rev,$fetched,$expires,$now,$now) " +
            "ON CONFLICT(scope_id,subject,facet,arguments) DO UPDATE SET state=excluded.state,provenance=excluded.provenance," +
            "payload_version=excluded.payload_version,fmt=excluded.fmt,payload=excluded.payload,size=excluded.size,revision=excluded.revision," +
            "fetched_at=excluded.fetched_at,expires_at=excluded.expires_at,updated_at=excluded.updated_at " +
            "WHERE excluded.revision>=catalog_resource.revision;",
            ("$state", (int)record.Knowledge), ("$provenance", (int)record.Provenance), ("$pv", CatalogPayloadCodec.Version),
            ("$fmt", PayloadCodec.FormatOf(payload)), ("$payload", payload), ("$size", payload?.Length ?? 0), ("$rev", record.Revision),
            ("$fetched", record.FetchedAt.ToUnixTimeMilliseconds()), ("$expires", record.ExpiresAt.ToUnixTimeMilliseconds()), ("$now", now));
        AddCatalogKey(write, scope, key);
        if (write.ExecuteNonQuery() == 0) return;
        using (var remove = ReplicaCommand(tx, "DELETE FROM catalog_relation_item WHERE scope_id=$scope AND parent_subject=$subject " +
            "AND facet=$facet AND arguments=$args;"))
        {
            AddCatalogKey(remove, scope, key);
            remove.ExecuteNonQuery();
        }
        if (page is not null)
        {
            using var insert = ReplicaCommand(tx, "INSERT INTO catalog_relation_item(scope_id,parent_subject,facet,arguments,ordinal," +
                "occurrence_key,child_uri,context_version,context_payload,size) VALUES($scope,$subject,$facet,$args,$ordinal,$occurrence,$child,1,$context,$size);",
                ("$ordinal", 0), ("$occurrence", ""), ("$child", ""), ("$context", null), ("$size", 0));
            AddCatalogKey(insert, scope, key);
            for (int i = 0; i < page.Items.Count; i++)
            {
                var item = page.Items[i];
                byte[]? context = item.Context is null ? null : CatalogPayloadCodec.EncodeRelationContext(item.Context);
                insert.Parameters["$ordinal"].Value = i;
                insert.Parameters["$occurrence"].Value = item.OccurrenceKey;
                insert.Parameters["$child"].Value = item.EntityUri;
                insert.Parameters["$context"].Value = (object?)context ?? DBNull.Value;
                insert.Parameters["$size"].Value = (context?.Length ?? 0) + System.Text.Encoding.UTF8.GetByteCount(item.EntityUri)
                    + System.Text.Encoding.UTF8.GetByteCount(item.OccurrenceKey);
                insert.ExecuteNonQuery();
            }
        }
        WriteCatalogSearchLocked(scope, record, tx);
    }

    void WriteCatalogSearchLocked(long scope, CatalogRecord record, SqliteTransaction tx)
    {
        var v = record.Value;
        if ((int)record.Key.Facet is < 1 or > 7) return;
        string? title = v switch
        {
            TrackIdentityValue t => t.Title, EpisodeIdentityValue e => e.Title, AlbumIdentityValue a => a.Name,
            ArtistIdentityValue a => a.Name, PlaylistHeaderValue p => p.Name, ShowIdentityValue s => s.Name,
            UserIdentityValue u => u.Name, _ => null,
        };
        string? image = v switch
        {
            TrackIdentityValue t => t.Image?.Url, EpisodeIdentityValue e => e.Image?.Url, AlbumIdentityValue a => a.Cover?.Url,
            ArtistIdentityValue a => a.Image?.Url, PlaylistHeaderValue p => p.Cover?.Url, ShowIdentityValue s => s.Cover?.Url,
            UserIdentityValue u => u.Avatar?.Url, _ => null,
        };
        var track = v as TrackIdentityValue;
        ExecuteReplicaLocked(tx, "INSERT INTO catalog_search(scope_id,uri,kind,title,image_url,duration_ms,flags,album_uri,artist_uris,revision) " +
            "VALUES($s,$u,$k,$t,$i,$d,$f,$a,$artists,$r) ON CONFLICT(scope_id,uri) DO UPDATE SET kind=excluded.kind,title=excluded.title," +
            "image_url=excluded.image_url,duration_ms=excluded.duration_ms,flags=excluded.flags,album_uri=excluded.album_uri," +
            "artist_uris=excluded.artist_uris,revision=excluded.revision;", ("$s", scope), ("$u", record.Key.Subject),
            ("$k", (int)Wavee.Core.EntityUri.KindOf(record.Key.Subject)), ("$t", title), ("$i", image),
            ("$d", track?.DurationMs), ("$f", track?.IsExplicit == true ? 1 : 0), ("$a", track?.AlbumUri),
            ("$artists", (track?.ArtistUris ?? (record.Value as AlbumIdentityValue)?.ArtistUris) is { } artists
                ? System.Text.Encoding.UTF8.GetString(CatalogPayloadCodec.EncodeStrings(artists.ToArray())) : null),
            ("$r", record.Revision));
    }

    long GetOrCreateCatalogScopeLocked(CatalogScope scope, SqliteTransaction tx)
    {
        if (FindCatalogScope(_conn, scope, tx) is { } existing) { _writtenScopeIds.Add((scope, existing)); return existing; }
        using var command = _conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO catalog_scope(provider,storage_account,provider_account,locale,market,catalogue,tier,explicit_filter,context_known) " +
            "VALUES($provider,$storage,$account,$locale,$market,$catalogue,$tier,$explicit,$known); SELECT last_insert_rowid();";
        AddCatalogScope(command, scope);
        long created = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        _writtenScopeIds.Add((scope, created));
        return created;
    }

    /// <summary>Moves the ids a just-committed write transaction created into the read cache, so the very next
    /// cold read of a scope this session created answers without a catalog_scope lookup.</summary>
    void PublishWrittenScopeIdsLocked()
    {
        if (_writtenScopeIds.Count == 0) return;
        lock (_scopeIdGate) foreach (var (scope, id) in _writtenScopeIds) _catalogScopeIds[scope] = id;
        _writtenScopeIds.Clear();
    }

    static long? FindCatalogScope(SqliteConnection connection, CatalogScope scope, SqliteTransaction? tx)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT scope_id FROM catalog_scope WHERE provider=$provider AND storage_account=$storage AND provider_account=$account " +
            "AND locale=$locale AND market=$market AND catalogue=$catalogue AND tier=$tier AND explicit_filter=$explicit AND context_known=$known;";
        AddCatalogScope(command, scope);
        return command.ExecuteScalar() is long id ? id : null;
    }

    static void AddCatalogScope(SqliteCommand command, CatalogScope scope)
    {
        command.Parameters.AddWithValue("$provider", scope.Provider);
        command.Parameters.AddWithValue("$storage", scope.StorageAccount);
        command.Parameters.AddWithValue("$account", scope.ProviderAccount);
        command.Parameters.AddWithValue("$locale", scope.Locale);
        command.Parameters.AddWithValue("$market", scope.Market);
        command.Parameters.AddWithValue("$catalogue", scope.Catalogue);
        command.Parameters.AddWithValue("$tier", scope.Tier);
        command.Parameters.AddWithValue("$explicit", scope.ExplicitFilter ? 1 : 0);
        command.Parameters.AddWithValue("$known", scope.ContextKnown ? 1 : 0);
    }

    static void AddCatalogKey(SqliteCommand command, long scope, ResourceKey key)
    {
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$subject", key.Subject);
        command.Parameters.AddWithValue("$facet", (int)key.Facet);
        command.Parameters.AddWithValue("$args", key.Arguments.ToStorageKey());
    }
}
