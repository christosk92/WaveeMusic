# Catalog schema reset + fetch throughput + playback epoch — implementation plan

Date: 2026-09-06. Branch `feat/library-v3-1` (the uncommitted hydration-facade refactor).
Supersedes the "Persistence v11 and migration" section of `catalog-state-replacement-implementation.md`.

## Why

Three user-visible failures on the current checkout, all diagnosed from the app log
(`%LOCALAPPDATA%\Wavee\logs\wavee-20260906.log`), two screen recordings and the code:

1. **The v11 cache migration is not worth shipping.** ~1,100 lines (`SqliteCatalogMigration.*`,
   `SqliteReplicaMigration`, recovery tables, a blocking startup dialog) exist only to convert the *cache* of an
   older schema into the new facet tables. The plan doc itself records a 69-second migration on a real install.
   The cache is re-fetchable; the library replicas re-sync on first launch anyway (`sync: initial hydrate` in the log).
   Decision (user, 2026-09-06): delete the migration; an older schema is dropped and rebuilt from the network.
2. **Clicking a track on a page does nothing.** Log: `play intent origin=play-context-track` is followed only by
   `command … action=Adopt` and never by `play resolved`. `NowPlayingProjection` captures `_catalogEpoch` in its
   constructor; the live stack is built (`golive.stack`) *before* `LiveSessionHost` installs the session
   (`Data.SetSessionAsync` → `CatalogRepository.SetSessionCore` → `_epoch++`). Every later
   `ObserveTracksAsync` → `SeedManyAsync` throws `OperationCanceledException("Inline observations belong to a previous
   catalog session.")`, `LocalPlaySpecAsync` retires the intent (the `Adopt`), and the UI's `AsyncCommandSet`
   swallows the `OperationCanceledException` silently.
3. **First open of an album / the sidebar after a reset shows blank rows or raw Spotify ids for seconds.**
   `ResourceCoordinator.WorkerCount = 2`, and `SpotifyCatalogResourceProvider.BatchGroup` returns a *per-subject*
   group for every non-extended-metadata facet, so 33 rootlist playlist headers are 33 batches served two at a time,
   each a full HTTP round trip, and the album page's track-identity wave queues behind them. Inside a batch the
   provider fetches envelopes and album documents sequentially, and reads the transport cache one key at a time.
   Nothing is logged per batch, so the log could not show any of this.

## Lanes (parallel, disjoint files)

| Lane | Owner | Files |
|---|---|---|
| A — schema reset | catalog agent | `Backend/Persistence/SqliteColdStore.cs`, `SqliteReplicaPersistence.cs`, `SqliteCatalogPersistence.cs`, `SqliteCatalogSearch.cs`, `CatalogCacheMaintenance.cs`, `ArtistOverview.cs`, `PayloadCodec.cs`, delete `SqliteCatalogMigration*.cs`, `SqliteReplicaMigration.cs`, `CatalogMigrationModels.cs`; tests `CatalogMigrationTests.cs` (delete; keep `CatalogTestDb`), `CatalogRetentionTests.cs`, `SqliteReplicaPersistenceTests.cs`, `Backend/VideoOverrideStoreTests.cs`, `ArtistOverviewDocTests.cs`, new `SqliteSchemaResetTests.cs` |
| B — app gate, strings, docs | app agent | `WaveeApp.cs`, `App/Services.cs`, delete `Features/Shell/CatalogMigrationDialog.cs` + `CatalogMigrationPresentation.cs`, `assets/loc/en-US.json`, tests `CatalogMigrationPresentationTests.cs` (delete), `docs/plans/wavee/catalog-state-replacement-implementation.md`, `CHANGELOG.md`, `.claude/skills/wavee/*.md` |
| C — playback epoch | playback agent | `Backend/PlaybackProjection.cs`, `Backend/PlaybackController.cs` (one catch), a test beside `PlaybackCatalogTestHost.cs` |
| D — fetch throughput + batch log | fetch agent | `Backend/Catalog/ResourceCoordinator.cs`, `SpotifyLive/Catalog/SpotifyCatalogResourceProvider.cs`, tests `ResourceCoordinatorTests.cs` |

Only the orchestrator builds (Debug + Release), runs `Wavee.Tests`, and launches.

---

## Lane A — schema version 12: reset instead of migrate

Rule: **a `library.db` whose `meta.schema_version` is not 12 has every catalog and replica table dropped and
recreated empty.** Nothing is converted. User-owned tables survive: `video_override`, `recent_surfaces`,
`activity_log` (written by `SqliteActivityStore` into the same file) and the `cache_budget_bytes` meta key.
`outbox` / `dead_letter` are dropped too (their pre-v11 rows have no owner account and were quarantined anyway;
"clear everything" is the decision). A newer version still throws.

### `SqliteColdStore.cs` (constructor)

```csharp
public const int CurrentSchemaVersion = 12;
/// <summary>The schema version this open replaced, or null when the file was already current (or brand new).</summary>
public int? ResetFromSchema { get; }

public SqliteColdStore(string path, string account, string? spotifyLocale)
{
    _account = account;
    _spotifyLocale = ...;
    _conn = new SqliteConnection(...); _conn.Open();
    try
    {
        // foreign_keys stays OFF until the reset has run: DROP TABLE under enforced foreign keys fails on a
        // parent table (catalog_scope) that still has children, and the pragma is a no-op inside a transaction.
        ExecLocked("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;" +
            "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT);");
        int? previous = null;
        using (var version = _conn.CreateCommand())
        {
            version.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
            if (int.TryParse(version.ExecuteScalar() as string, out int schema)) previous = schema;
        }
        if (previous > CurrentSchemaVersion)
            throw new InvalidOperationException("The library database belongs to a newer Wavee version.");
        if (previous is { } old && old != CurrentSchemaVersion) { ResetForSchemaLocked(); ResetFromSchema = old; }
        if (previous != CurrentSchemaVersion)
            ExecLocked("INSERT OR REPLACE INTO meta(key,value) VALUES('schema_version','" + CurrentSchemaVersion + "');");
        ExecLocked("PRAGMA foreign_keys=ON;");
        ExecLocked(/* video_override + recent_surfaces CREATE TABLE IF NOT EXISTS, unchanged */);
        EnsureReplicaSchema();
        EnsureCatalogSchema();
        EnsureCatalogAccountingLocked();
        _read = ...; _read.Open();
    }
    catch { _conn.Dispose(); throw; }
}

// Children before parents. Every table any earlier schema (≤10 JSON cache, 11 facets, the interim migration
// bookkeeping) or the current one creates for catalog/replica state. Unknown tables are left alone.
static readonly string[] ResetTables =
[
    "catalog_relation_item", "catalog_search", "extension_cache", "catalog_resource", "catalog_scope",
    "replica_playlist_item", "replica_rootlist_entry", "replica_collection_item", "replica_recovery", "replica_state",
    "outbox", "dead_letter",
    "entity", "entities", "localized_entities", "localized_extension_cache", "artist_overview", "video_assoc",
    "entity_refs", "catalog_relation_recovery", "playlists", "playlist_items", "rootlist", "collection_items",
    "collection_rev", "replica_legacy_state", "replica_legacy_recovery",
    "catalog_migration_progress", "catalog_migration_recovery", "catalog_migration_pending",
    "catalog_migration_recovery_chunk", "catalog_migration_row_cursor",
];

void ResetForSchemaLocked()
{
    using var tx = _conn.BeginTransaction();
    foreach (var table in ResetTables) ExecLocked("DROP TABLE IF EXISTS " + table + ";", tx);
    ExecLocked("DELETE FROM meta WHERE key<>'" + MetaCacheBudget + "';", tx);
    ExecLocked("INSERT OR REPLACE INTO meta(key,value) VALUES('" + MetaVacuumPending + "','1');", tx);
    tx.Commit();
}
```

Remove from the store: `_catalogMigrationGate`, `_catalogMigration`, `InitializeCatalogAsync`, `MigrationProgress`
and `_migrationProgress`, `CatalogMigrationProgress`, the migration wait in `Dispose`, `ImportedScope`,
`MergeImportedOverview`, `PrepareImportedRecords` and any helper only the migration used (`ReplicaCommand` /
`ExecuteReplicaLocked` / `CatalogPayloadCodec.EncodeStrings|DecodeStrings` stay only if a non-migration caller
remains — grep). `EnsureCatalogAccountingLocked` must still produce `catalog_bytes` / `catalog_revision` for an empty
catalog (it runs after the reset).

### `SqliteReplicaPersistence.cs`

`EnsureReplicaSchema` loses the `replica_state`/`replica_recovery` rename, `MigrateReplicaBaselinesLocked`,
`AddReplicaColumnLocked` and `InitializeReplicaAsync`. `outbox` and `dead_letter` are created **once with their full
column set** (the reset drops them, and a v12 file already has every column):

```sql
CREATE TABLE IF NOT EXISTS outbox(id INTEGER PRIMARY KEY,type TEXT NOT NULL,entity_key TEXT NOT NULL,
  set_id TEXT,target_saved INTEGER,op BLOB,base_rev BLOB,attempts INTEGER NOT NULL DEFAULT 0,parent_folder TEXT,
  storage_account TEXT NOT NULL DEFAULT 'default',owner_account TEXT,intent_state INTEGER NOT NULL DEFAULT 0,
  acknowledged_revision BLOB,created_at_ms INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS dead_letter(id INTEGER PRIMARY KEY,type TEXT,entity_key TEXT,reason TEXT,created_at INTEGER,
  storage_account TEXT NOT NULL DEFAULT 'default',owner_account TEXT);
CREATE INDEX IF NOT EXISTS ix_outbox_scoped_owner ON outbox(storage_account,owner_account,intent_state,id);
```

`LoadAsync` no longer awaits anything before `Task.Run(LoadReplica)`. Same for `ReadAsync`, `ReadTransportAsync`,
the search reader and `CatalogCacheMaintenance` (every `await _cold.InitializeCatalogAsync(token)` goes).

### Deletions

`SqliteCatalogMigration.cs`, `.Batches.cs`, `.Documents.cs`, `.Progress.cs`, `SqliteReplicaMigration.cs`,
`CatalogMigrationModels.cs` (`EntityJson`), `PayloadCodec.DecodeBounded`, and `LegacyArtistOverview` +
`ArtistOverviewDoc` (+ `ArtistAlbumStub`, `ArtistTopTrack`, `ArtistTopTrackConverter` when nothing else references
them — if the whole file is legacy-only, delete `ArtistOverview.cs`). Fix the `Wavee.Core/Domain/Models.cs` comment
that names `ArtistOverviewDoc`.

### Tests (lane A)

- Delete `CatalogMigrationTests.cs`; move `CatalogTestDb` (drop its `legacy` branch and the
  `Entity`/`Extension` helpers) into a new `CatalogTestDb.cs`.
- `CatalogRetentionTests.cs`: drop the `InitializeCatalogAsync` awaits.
- `SqliteReplicaPersistenceTests.UnattributedLegacyRowsAreQuarantinedOutsideNormalReads` → replace with
  `AnOlderSchemaIsDroppedAndRebuilt`: seed schema 8 + `playlists`/`playlist_items`/`outbox` rows as today; after
  `Open()`: `LoadAsync(Alice)` is empty, `LastIntentId == 0`, `playlists` table no longer exists, `outbox` is
  empty, `schema_version == "12"`, `cold.ResetFromSchema == 8`.
- `VideoOverrideStoreTests.CurationStartupReadsExistingRowsWithoutWaitingForCatalogMigration` →
  `SchemaResetKeepsVideoOverrides`: same seed (schema 3); the row survives and `schema_version == "12"`.
- New `SqliteSchemaResetTests.cs`:
  1. a v11 file with rows in `catalog_scope`/`catalog_resource`/`replica_state`/`outbox`, plus `activity_log`,
     `recent_surfaces`, `video_override` rows and `cache_budget_bytes` → after open: catalog/replica tables exist and
     are empty, `outbox` empty, the three user tables keep their rows, `cache_budget_bytes` kept,
     `cache_vacuum_pending == "1"`, `ResetFromSchema == 11`;
  2. a brand-new file → `schema_version == "12"`, `ResetFromSchema == null`;
  3. reopening a v12 file keeps its rows and reports `ResetFromSchema == null`;
  4. `schema_version = 13` → the constructor throws.
- Delete `ArtistOverviewDocTests.cs` with its types.

---

## Lane B — the app side

- `WaveeApp.cs`: delete the `CatalogMigrationGate` wrap (and its comment). `_services.DataReady` stays (it still
  gates replica bootstrap + native sources).
- Delete `Features/Shell/CatalogMigrationDialog.cs`, `Features/Shell/CatalogMigrationPresentation.cs`,
  `Wavee.Tests/CatalogMigrationPresentationTests.cs`.
- `assets/loc/en-US.json`: remove the whole `catalogMigration` object (ko-KR / nl have no such keys). The
  `Strings.CatalogMigration.*` members are generated from it and disappear with the block.
- `App/Services.cs` `CreateReal`: fix the `open + migrate + open-time sweep` comment, and log the reset right after
  the store opens so the log explains the empty sidebar on that launch:

  ```csharp
  if (cold.ResetFromSchema is { } previous)
      WaveeLog.Instance.Info("catalog", $"library.db schema v{previous} → v{SqliteColdStore.CurrentSchemaVersion}: " +
          "catalog cache and library replicas cleared; they re-fetch from Spotify on this launch");
  ```
- `docs/plans/wavee/catalog-state-replacement-implementation.md`: rename the section to
  "Persistence v12 (schema reset)"; replace the migration paragraphs (shadow tables, cursors, recovery, the sign-in
  hang note, the dialog wireframe, the `CatalogMigrationProgress` paragraph) with the reset rule above and a pointer
  to this plan; checklist line → "v12 persistence (schema reset) and sole writer".
- `CHANGELOG.md`: one bullet in the unreleased section, in the file's existing voice: upgrading clears the local
  catalog cache and library replicas once; they re-sync from Spotify on the first launch. No issue number (not a
  tracked issue).
- `.claude/skills/wavee/*.md`: remove any mention of the catalog migration / `InitializeCatalogAsync`.

---

## Lane C — the playback catalog epoch

`Backend/PlaybackProjection.cs`:

```csharp
readonly long _queueOwner;                 // _catalogEpoch deleted
...
public Task ObserveTracksAsync(IReadOnlyList<Track> tracks, CancellationToken ct = default)
{
    if (_disposed || !_catalog.IsOwner(_queueOwner)) throw new OperationCanceledException("The playback owner changed.");
    return _catalog.SeedTracksAsync(tracks, _catalog.Epoch, ct);
}
public Task ObserveOwnerTrackAsync(Track track, CancellationToken ct = default)
{
    if (_disposed || !_catalog.IsOwner(_queueOwner)) throw new OperationCanceledException("The playback owner changed.");
    return _catalog.ObserveOwnerTrackAsync(track, _catalog.Epoch, ct);
}
```

The epoch a resolve started under is still enforced where it belongs: `LiveContextResolver` captures
`_projection.Epoch` before the network call and `MaterializeAsync` checks it. The projection outlives session
installs (it is built in `LiveConnect` before `LiveSessionHost` calls `Data.SetSessionAsync`), so a
construction-time epoch is always stale by the first click.

`Backend/PlaybackController.ExecutePlayAsync`: a failed play intent must never be silent again —

```csharp
async Task ExecutePlayAsync(PlayRequest request, string origin, CancellationToken ct)
{
    try { ...existing body... }
    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
    {
        _log.Warn($"play intent failed origin={origin} ctx={request.ContextUri} index={request.StartIndex}: " +
            $"{ex.GetType().Name}: {ex.Message}");
        throw;
    }
}
```

Test (beside `PlaybackCatalogTestHost`): build the host, install a new session on the repository
(`SetSessionAsync` with a different account/scope so the epoch advances), then `projection.ObserveTracksAsync([track])`
completes and `Queue.ReadTrack(track.Uri).Title` is the seeded title. A second test: the same call after
`ReleaseOwner` / a second `ActivateOwner` still throws "owner changed".

---

## Lane D — fetch throughput and the batch log

`Backend/Catalog/ResourceCoordinator.cs`:

- `WorkerCount = 4`.
- One always-on line per batch (category `catalog`, event `catalog.fetch.batch`), via `WaveeLog.Instance.Event`
  with fields: `provider`, `group`, `keys` (count), `subjects` (distinct count), `first` (first subject), `fetchMs`,
  `acceptMs`, `ready`, `absent`, `failed`, `superseded`, and `error` (the first failure message) — Info when every
  key is ready/absent, Warn otherwise. This is the line that finally shows what an album open costs.

`SpotifyLive/Catalog/SpotifyCatalogResourceProvider.cs`:

```csharp
public string BatchGroup(ResourceKey key) => SpotifyCatalogDecoder.ExtensionFor(key) is not null ? "extended-metadata"
    : key.Facet is FacetKind.AlbumDetail or FacetKind.AlbumVersions ? "album-envelope"
    : "envelope:" + key.Facet;
```

so every playlist header (or every artist overview, home section, …) in the queue travels in one worker turn.
Inside `FetchAsync`, album documents and envelopes run with bounded parallelism (`SemaphoreSlim(4)` +
`Task.WhenAll`; results land in a `ConcurrentDictionary<long, ResourceResponse>` or a per-request array — never a
shared `Dictionary` from several tasks). `FetchRawAsync` reads the transport cache for all keys with `Task.WhenAll`
instead of awaiting one key at a time. The extended-metadata path is unchanged (one POST per batch; the waste tests
count those).

Tests: `ResourceCoordinatorTests` — a batch-group test that four keys of one group with different subjects reach
`FetchAsync` in one call, and that keys of four different groups run on separate workers concurrently (gate the
fake provider on a `TaskCompletionSource` and assert `Running == 4`). Keep every existing assertion green.

---

## Verification (orchestrator)

1. `dotnet build Wavee.slnx` and `-c Release` clean (`TreatWarningsAsErrors`).
2. `dotnet test src/apps/Wavee.Tests/Wavee.Tests.csproj` green.
3. Launch: the user's `library.db` is v11 → the log shows the reset line, the sidebar re-syncs, an album opens with
   its rows within the batch lines' `fetchMs`, a track click logs `play resolved` and switches the track.
