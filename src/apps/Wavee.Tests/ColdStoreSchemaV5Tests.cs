using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Core;
using EntityKind = Wavee.Backend.Metadata.EntityKind;   // the PERSISTED transport vocabulary (Wavee.Core.EntityKind is the routing one)
using Xunit;

namespace Wavee.Tests;

// ── schema v5: the one `entity` cache tier ───────────────────────────────────────────────────────────────────────────
// What is pinned here:
//   (a) the v4 → v5 migration: pin-reachable rows (localized wins a same-uri conflict) + their album/artist closure move
//       into `entity` with thin columns and fmt=1 payloads; orphans are dropped (that IS the one-time GC); an unparseable
//       payload survives verbatim as fmt=0; the legacy tables and dead indexes are gone; accounting + vacuum flag set,
//   (b) the fmt prefix round-trips both formats byte-for-byte,
//   (c) TouchEntities chunks past the SQLite parameter cap,
//   (d) recent_surfaces is LRU-capped at 50 on insert,
//   (e) cache_bytes tracks SUM(size) across upsert/replace/delete,
//   (f) the open-time extension sweep honours the +7d ETag-revalidation grace.
public class ColdStoreSchemaV5Tests
{
    const string Locale = "en";

    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-v5-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }

    static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        return c;
    }

    static void Exec(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(string path, string sql)
    {
        using var c = Open(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    static long Count(string path, string sql) => Convert.ToInt64(Scalar(path, sql) ?? 0L);

    static bool HasTable(string path, string table)
        => Count(path, $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{table}';") > 0;

    static bool HasIndex(string path, string index)
        => Count(path, $"SELECT count(*) FROM sqlite_master WHERE type='index' AND name='{index}';") > 0;

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    static byte[] Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => JsonSerializer.SerializeToUtf8Bytes(value, info);

    static Track PinnedTrack(string title) => new(
        "pinned", "spotify:track:pinned", title,
        [new ArtistRef("ca", "spotify:artist:closure", "Closure Artist"), new ArtistRef("cb", "spotify:artist:second", "Second")],
        new AlbumRef("cl", "spotify:album:closure", "Closure Album"),
        213_000, IsExplicit: true, new Image("https://cdn.example/track.jpg"));

    // The bare v4 SCHEMA — every table the v4 ctor created, both legacy entity generations, and version 4. No rows: the
    // fixtures below (and the focused regression tests at the bottom) add exactly the rows they are about.
    static void SeedV4Schema(string path)
    {
        Exec(path, """
            CREATE TABLE entities(uri TEXT PRIMARY KEY, kind INTEGER NOT NULL, payload BLOB NOT NULL);
            CREATE TABLE localized_entities(uri TEXT NOT NULL, locale TEXT NOT NULL, kind INTEGER NOT NULL,
                payload BLOB NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(uri, locale));
            CREATE INDEX ix_localized_entities_locale ON localized_entities(locale);
            CREATE INDEX ix_localized_entities_updated ON localized_entities(updated_at);
            CREATE TABLE localized_extension_cache(entity_uri TEXT NOT NULL, locale TEXT NOT NULL, extension_kind INTEGER NOT NULL,
                payload BLOB, etag TEXT, offline_ttl INTEGER NOT NULL DEFAULT 0, missing INTEGER NOT NULL DEFAULT 0,
                expires_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(entity_uri, locale, extension_kind));
            CREATE INDEX ix_localized_extension_locale ON localized_extension_cache(locale);
            CREATE INDEX ix_localized_extension_expiry ON localized_extension_cache(expires_at);
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE collection_items(account TEXT NOT NULL, set_id TEXT NOT NULL, item_uri TEXT NOT NULL,
                added_at INTEGER NOT NULL DEFAULT 0, position INTEGER, sync INTEGER NOT NULL, PRIMARY KEY(account, set_id, item_uri));
            CREATE TABLE playlists(uri TEXT PRIMARY KEY, base_rev BLOB);
            CREATE TABLE playlist_items(playlist_uri TEXT NOT NULL, position INTEGER NOT NULL, item_id TEXT,
                item_uri TEXT NOT NULL, added_by TEXT, added_at INTEGER, PRIMARY KEY(playlist_uri, position));
            CREATE TABLE rootlist(account TEXT NOT NULL, position INTEGER NOT NULL, kind INTEGER, uri TEXT, group_name TEXT, depth INTEGER, PRIMARY KEY(account, position));
            CREATE TABLE outbox(id INTEGER PRIMARY KEY, type TEXT NOT NULL, entity_key TEXT NOT NULL, set_id TEXT, target_saved INTEGER, op BLOB, base_rev BLOB, attempts INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE video_override(uri TEXT PRIMARY KEY, path TEXT NOT NULL, id TEXT NOT NULL,
                duration_ms INTEGER DEFAULT 0, size INTEGER DEFAULT 0, mtime INTEGER DEFAULT 0, added_at INTEGER DEFAULT 0);
            INSERT INTO meta(key,value) VALUES('schema_version','4');
            """);
    }

    // One legacy row, into either generation. `locale` null ⇒ the base `entities` table.
    static void Legacy(SqliteConnection c, string uri, string? locale, EntityKind kind, byte[] payload, long updated = 0)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = locale is null
            ? "INSERT INTO entities(uri,kind,payload) VALUES($u,$k,$p);"
            : "INSERT INTO localized_entities(uri,locale,kind,payload,updated_at) VALUES($u,$l,$k,$p,$t);";
        cmd.Parameters.AddWithValue("$u", uri);
        cmd.Parameters.AddWithValue("$k", (int)kind);
        cmd.Parameters.AddWithValue("$p", payload);
        if (locale is not null) { cmd.Parameters.AddWithValue("$l", locale); cmd.Parameters.AddWithValue("$t", updated); }
        cmd.ExecuteNonQuery();
    }

    // A v4 database, built by hand: the v4 schema plus the two legacy entity generations and the identity rows that make
    // a uri pin-reachable.
    static void SeedV4(string path)
    {
        SeedV4Schema(path);
        Exec(path, """
            -- identity: what makes a uri PIN-REACHABLE
            INSERT INTO collection_items VALUES('default','liked','spotify:track:pinned',10,NULL,0);
            INSERT INTO collection_items VALUES('default','liked','spotify:track:corrupt',11,NULL,0);
            INSERT INTO playlists(uri,base_rev) VALUES('spotify:playlist:p',NULL);
            INSERT INTO playlist_items VALUES('spotify:playlist:p',0,'i1','spotify:track:member',NULL,0);
            """);

        using var c = Open(path);
        void Legacy(string table, string uri, string? locale, EntityKind kind, byte[] payload, long updated = 0)
            => ColdStoreSchemaV5Tests.Legacy(c, uri, locale, kind, payload, updated);

        // Same uri in BOTH generations — the localized row must win (the precedence the v4 3-leg UNION gave).
        Legacy("entities", "spotify:track:pinned", null, EntityKind.Track, Json(PinnedTrack("Base Title"), EntityJson.Default.Track));
        Legacy("localized", "spotify:track:pinned", Locale, EntityKind.Track, Json(PinnedTrack("Localized Title"), EntityJson.Default.Track), 100);

        // A playlist-member track (pinned through playlist_items) + the playlist header itself (pinned through playlists).
        Legacy("localized", "spotify:track:member", Locale, EntityKind.Track,
            Json(new Track("m", "spotify:track:member", "Member", [], new AlbumRef("", "", ""), 1000, false, null), EntityJson.Default.Track), 100);
        Legacy("localized", "spotify:playlist:p", Locale, EntityKind.Playlist,
            Json(new Playlist("p", "spotify:playlist:p", "My Mix", null, "Me", new Image("https://cdn.example/pl.jpg"), 1), EntityJson.Default.Playlist), 100);

        // CLOSURE: neither is directly pin-reachable — they are reachable only as the pinned track's album/artist.
        Legacy("entities", "spotify:album:closure", null, EntityKind.Album,
            Json(new Album("cl", "spotify:album:closure", "Closure Album", new Image("https://cdn.example/al.jpg"),
                [new ArtistRef("ca", "spotify:artist:closure", "Closure Artist")], 2021, 12), EntityJson.Default.Album));
        Legacy("localized", "spotify:artist:closure", Locale, EntityKind.Artist, FatArtistJson(), 100);

        // A pinned row whose payload is NOT valid JSON — must survive verbatim, never be dropped.
        Legacy("localized", "spotify:track:corrupt", Locale, EntityKind.Track, CorruptPayload, 100);

        // ORPHANS: hydrated debris, pinned by nothing → not migrated (the intentional one-time GC).
        Legacy("entities", "spotify:track:orphan", null, EntityKind.Track,
            Json(new Track("o", "spotify:track:orphan", "Orphan", [], new AlbumRef("", "", ""), 1, false, null), EntityJson.Default.Track));
        Legacy("localized", "spotify:album:orphan", Locale, EntityKind.Album,
            Json(new Album("o", "spotify:album:orphan", "Orphan Album", null, [], 2000, 1), EntityJson.Default.Album), 100);
    }

    static readonly byte[] CorruptPayload = Encoding.UTF8.GetBytes("{not json at all,,,");

    static byte[] FatArtistJson()
    {
        var albums = new List<Album>();
        for (int i = 0; i < 40; i++)
            albums.Add(new Album("a" + i, "spotify:album:fat" + i, "Fat Album " + i, new Image("https://cdn.example/fat" + i + ".jpg"),
                [new ArtistRef("ca", "spotify:artist:closure", "Closure Artist")], 2000 + i, 10));
        return Json(new Artist("ca", "spotify:artist:closure", "Closure Artist", new Image("https://cdn.example/ar.jpg"),
            albums, MonthlyListeners: 1234, Followers: 5678, Bio: new string('b', 4000)), EntityJson.Default.Artist);
    }

    // ── (a) the v4 → v5 migration ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MigrationV4ToV5_MigratesPinReachableRowsWithThinColumns_DropsOrphansAndLegacyTables()
    {
        var path = TempDb();
        try
        {
            SeedV4(path);
            var expected = Json(PinnedTrack("Localized Title"), EntityJson.Default.Track);

            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            {
                // The localized generation wins the same-uri conflict, and the payload round-trips byte-for-byte.
                var pinned = cold.GetEntity("spotify:track:pinned");
                Assert.NotNull(pinned);
                Assert.Equal(EntityKind.Track, pinned!.Value.Kind);
                Assert.Equal(expected, pinned.Value.Payload);

                // Orphans were simply not migrated — that IS the one-time GC.
                Assert.Null(cold.GetEntity("spotify:track:orphan"));
                Assert.Null(cold.GetEntity("spotify:album:orphan"));

                // The pin closure came along even though neither is directly pin-reachable.
                Assert.NotNull(cold.GetEntity("spotify:album:closure"));
                Assert.NotNull(cold.GetEntity("spotify:artist:closure"));

                // A corrupt payload survives verbatim (never dropped, never fatal).
                var corrupt = cold.GetEntity("spotify:track:corrupt");
                Assert.NotNull(corrupt);
                Assert.Equal(CorruptPayload, corrupt!.Value.Payload);

                Assert.True(cold.GetCacheBytes() > 0);

                var uris = cold.LoadAllEntities().Select(e => e.Uri).OrderBy(u => u, StringComparer.Ordinal).ToArray();
                Assert.Equal(new[]
                {
                    "spotify:album:closure", "spotify:artist:closure", "spotify:playlist:p",
                    "spotify:track:corrupt", "spotify:track:member", "spotify:track:pinned",
                }, uris);
            }

            // Thin columns, straight out of SQL (list rendering never has to touch the payload).
            using (var c = Open(path))
            {
                using var q = c.CreateCommand();
                q.CommandText = "SELECT title,subtitle,image_url,duration_ms,flags,album_uri,fmt,size FROM entity WHERE uri='spotify:track:pinned';";
                using var r = q.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("Localized Title", r.GetString(0));
                Assert.Equal("Closure Artist, Second", r.GetString(1));
                Assert.Equal(PinnedTrack("x").Image!.Url, r.GetString(2));
                Assert.Equal(213_000, r.GetInt64(3));
                // FlagHasVideo is LEGACY-only now: has-video moved off the Track record onto the VideoAssociation
                // plane, so freshly serialized rows carry no "HasVideo" JSON property and the flag stays clear.
                Assert.Equal(EntityThinExtractor.FlagExplicit, r.GetInt64(4));
                Assert.Equal("spotify:album:closure", r.GetString(5));
                Assert.Equal(PayloadCodec.FmtZstd, r.GetInt32(6));
                Assert.True(r.GetInt64(7) > 0);
            }

            // The unparseable row keeps fmt=0 + null thin columns.
            Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:track:corrupt' AND fmt=0 AND title IS NULL;"));
            // The fat artist migrated WHOLE (the thin split is Wave B), compressed.
            Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity WHERE uri='spotify:artist:closure' AND fmt=1 AND title='Closure Artist';"));

            // Closure edges are recorded for the pinned track (album + both artists).
            Assert.Equal(3L, Count(path, "SELECT count(*) FROM entity_refs WHERE parent_uri='spotify:track:pinned';"));
            Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity_refs WHERE parent_uri='spotify:track:pinned' AND child_uri='spotify:album:closure';"));

            // Legacy generation + dead indexes are gone; the one index the TTL sweep needs is kept.
            Assert.False(HasTable(path, "entities"));
            Assert.False(HasTable(path, "localized_entities"));
            Assert.False(HasIndex(path, "ix_localized_entities_locale"));
            Assert.False(HasIndex(path, "ix_localized_entities_updated"));
            Assert.False(HasIndex(path, "ix_localized_extension_locale"));
            Assert.True(HasIndex(path, "ix_localized_extension_expiry"));
            Assert.True(HasIndex(path, "ix_entity_gc"));
            Assert.True(HasTable(path, "artist_overview"));
            Assert.True(HasTable(path, "recent_surfaces"));

            Assert.Equal("1", Scalar(path, "SELECT value FROM meta WHERE key='vacuum_pending';") as string);
            // v5 IS the cache-tier consolidation; the ladder then keeps walking into v6 (the `playlists.adopted_at` clock).
            Assert.Equal(SqliteColdStore.CurrentSchemaVersion.ToString(), Scalar(path, "SELECT value FROM meta WHERE key='schema_version';") as string);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void MigrationV4ToV5_IsIdempotent_AndReopenChangesNothing()
    {
        var path = TempDb();
        try
        {
            SeedV4(path);
            using (var _ = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale)) { }
            long rows = Count(path, "SELECT count(*) FROM entity;");
            long bytes = Count(path, "SELECT CAST(value AS INTEGER) FROM meta WHERE key='cache_bytes';");

            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            {
                Assert.Equal(rows, cold.LoadAllEntities().Count());
                Assert.Equal(bytes, cold.GetCacheBytes());
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void EnumeratePinnedUris_UnionsTheIdentityTablesPlusRecentSurfaces()
    {
        var path = TempDb();
        try
        {
            SeedV4(path);
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            cold.UpsertRecentSurface("spotify:album:recent", (int)EntityKind.Album, 500);

            var pins = cold.EnumeratePinnedUris().ToHashSet(StringComparer.Ordinal);
            Assert.Contains("spotify:track:pinned", pins);      // collection_items
            Assert.Contains("spotify:track:member", pins);      // playlist_items
            Assert.Contains("spotify:playlist:p", pins);        // playlists
            Assert.Contains("spotify:album:recent", pins);      // recent_surfaces
            Assert.DoesNotContain("spotify:track:orphan", pins);
        }
        finally { TryDelete(path); }
    }

    // ── the property casing the thin extractor depends on ────────────────────────────────────────────────────────────

    [Fact]
    public void EntityJson_SerializesDeclaredPascalCaseNames_AndTheThinExtractorReadsThem()
    {
        var track = PinnedTrack("Casing Probe");
        var bytes = Json(track, EntityJson.Default.Track);
        var text = Encoding.UTF8.GetString(bytes);
        // `EntityJson` sets no PropertyNamingPolicy → members serialize under their DECLARED names.
        foreach (var name in new[] { "\"Title\"", "\"Artists\"", "\"Album\"", "\"DurationMs\"", "\"IsExplicit\"", "\"Image\"", "\"Uri\"", "\"Name\"", "\"Url\"" })
            Assert.Contains(name, text);

        Assert.True(EntityThinExtractor.TryExtract(bytes, EntityKind.Track, out var thin));
        Assert.Equal(track.Title, thin.Title);
        Assert.Equal("Closure Artist, Second", thin.Subtitle);
        Assert.Equal(track.Image!.Url, thin.ImageUrl);
        Assert.Equal(track.DurationMs, thin.DurationMs);
        Assert.Equal(track.Album.Uri, thin.AlbumUri);
        Assert.Equal(EntityThinExtractor.FlagExplicit, thin.Flags);
        Assert.Equal(new[] { "spotify:album:closure", "spotify:artist:closure", "spotify:artist:second" }, thin.Refs!.ToArray());

        // LEGACY rows: Track.HasVideo is deleted (has-video lives on the VideoAssociation plane), but payloads
        // persisted before that still carry the property, and the extractor must keep reading it — the thin flag
        // column stays truthful for old rows without any migration.
        Assert.True(EntityThinExtractor.TryExtract(
            Encoding.UTF8.GetBytes("""{"Uri":"spotify:track:old","Title":"Old","IsExplicit":false,"HasVideo":true}"""),
            EntityKind.Track, out var legacyThin));
        Assert.Equal(EntityThinExtractor.FlagHasVideo, legacyThin.Flags);

        // Every other kind lands its own title/subtitle pair.
        Assert.True(EntityThinExtractor.TryExtract(
            Json(new Album("a", "spotify:album:a", "The Album", null, [new ArtistRef("x", "spotify:artist:x", "X")], 2020, 1), EntityJson.Default.Album),
            EntityKind.Album, out var album));
        Assert.Equal("The Album", album.Title);
        Assert.Equal("X", album.Subtitle);

        Assert.True(EntityThinExtractor.TryExtract(
            Json(new Playlist("p", "spotify:playlist:p", "Mix", null, "Owner", null, 0), EntityJson.Default.Playlist),
            EntityKind.Playlist, out var pl));
        Assert.Equal("Mix", pl.Title);
        Assert.Equal("Owner", pl.Subtitle);

        Assert.True(EntityThinExtractor.TryExtract(
            Json(new Show("s", "spotify:show:s", "My Show", "Acme", null), EntityJson.Default.Show), EntityKind.Show, out var show));
        Assert.Equal("My Show", show.Title);
        Assert.Equal("Acme", show.Subtitle);

        Assert.True(EntityThinExtractor.TryExtract(
            Json(new Episode("e", "spotify:episode:e", "Ep 1", "My Show", null, 5000, DateTimeOffset.UnixEpoch), EntityJson.Default.Episode),
            EntityKind.Episode, out var ep));
        Assert.Equal("Ep 1", ep.Title);
        Assert.Equal("My Show", ep.Subtitle);
        Assert.Equal(5000, ep.DurationMs);

        Assert.False(EntityThinExtractor.TryExtract(CorruptPayload, EntityKind.Track, out _));
    }

    // ── (b) the fmt prefix ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PayloadCodec_RoundTripsBothFormats()
    {
        var payload = Encoding.UTF8.GetBytes("{\"Title\":\"" + new string('x', 4000) + "\"}");

        var raw = PayloadCodec.Encode(payload, PayloadCodec.FmtRawJson);
        Assert.Equal(PayloadCodec.FmtRawJson, PayloadCodec.FormatOf(raw));
        Assert.Equal(payload.Length + 1, raw.Length);
        Assert.Equal(payload, PayloadCodec.Decode(raw));

        var zstd = PayloadCodec.Encode(payload, PayloadCodec.FmtZstd);
        Assert.Equal(PayloadCodec.FmtZstd, PayloadCodec.FormatOf(zstd));
        Assert.True(zstd.Length < payload.Length);          // a repetitive payload really does shrink
        Assert.Equal(payload, PayloadCodec.Decode(zstd));

        // Non-JSON / non-UTF8 bytes are just bytes to the codec.
        var binary = new byte[] { 0, 1, 2, 250, 251, 255 };
        Assert.Equal(binary, PayloadCodec.Decode(PayloadCodec.Encode(binary, PayloadCodec.FmtZstd)));
        Assert.Equal(binary, PayloadCodec.Decode(PayloadCodec.Encode(binary, PayloadCodec.FmtRawJson)));

        Assert.Empty(PayloadCodec.Decode(null));
        Assert.Empty(PayloadCodec.Decode(Array.Empty<byte>()));
        Assert.Empty(PayloadCodec.Decode(PayloadCodec.Encode(Array.Empty<byte>(), PayloadCodec.FmtZstd)));
    }

    [Fact]
    public void UpsertEntity_StoresCompressed_AndReadsBackTheRawJson()
    {
        var path = TempDb();
        try
        {
            var track = PinnedTrack("Round Trip");
            var json = Json(track, EntityJson.Default.Track);
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            {
                cold.UpsertEntity(track.Uri, EntityKind.Track, json);
                cold.Flush();
                Assert.Equal(json, cold.GetEntity(track.Uri)!.Value.Payload);
            }

            Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity WHERE fmt=1 AND title='Round Trip' AND duration_ms=213000;"));
            // The write path records the same pin-closure edges the migration does.
            Assert.Equal(3L, Count(path, "SELECT count(*) FROM entity_refs WHERE parent_uri='spotify:track:pinned';"));
        }
        finally { TryDelete(path); }
    }

    // ── (c) batched last-access ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TouchEntities_StampsEveryUri_ChunkingPastTheParameterCap()
    {
        var path = TempDb();
        try
        {
            const int n = 1500;   // > the 900-parameter chunk size, so this exercises the chunking loop
            var uris = new List<string>(n);
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            for (int i = 0; i < n; i++)
            {
                var uri = "spotify:track:t" + i;
                uris.Add(uri);
                cold.UpsertEntity(uri, EntityKind.Track,
                    Json(new Track("t" + i, uri, "T" + i, [], new AlbumRef("", "", ""), 1000, false, null), EntityJson.Default.Track));
            }
            cold.Flush();

            cold.TouchEntities(uris, 20_300);
            Assert.Equal(n, Count(path, "SELECT count(*) FROM entity WHERE last_access=20300;"));

            cold.TouchEntities(new[] { "spotify:track:t7" }, 20_400);
            Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity WHERE last_access=20400;"));
            Assert.Equal(n - 1, Count(path, "SELECT count(*) FROM entity WHERE last_access=20300;"));

            cold.TouchEntities(Array.Empty<string>(), 1);        // no-op, no throw
            cold.TouchEntities(new[] { "spotify:track:nope" }, 9);   // unknown uri, no throw
        }
        finally { TryDelete(path); }
    }

    // ── (d) recent surfaces ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RecentSurfaces_AreCappedAtFifty_NewestFirst()
    {
        var path = TempDb();
        try
        {
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            for (int i = 0; i < 60; i++) cold.UpsertRecentSurface("spotify:album:a" + i, (int)EntityKind.Album, 1000 + i);

            var rows = cold.LoadRecentSurfaces();
            Assert.Equal(50, rows.Count);
            Assert.Equal("spotify:album:a59", rows[0].Uri);          // newest first
            Assert.Equal(1059, rows[0].LastOpenedUnixSeconds);
            Assert.Equal((int)EntityKind.Album, rows[0].Kind);
            Assert.DoesNotContain(rows, r => r.Uri == "spotify:album:a0");   // the oldest ten fell out

            // Re-opening an old surface promotes it back in (uri is the PK, last_opened is the LRU key).
            cold.UpsertRecentSurface("spotify:album:a0", (int)EntityKind.Album, 9999);
            rows = cold.LoadRecentSurfaces();
            Assert.Equal(50, rows.Count);
            Assert.Equal("spotify:album:a0", rows[0].Uri);
        }
        finally { TryDelete(path); }
    }

    // ── (e) cache accounting ─────────────────────────────────────────────────────────────────────────────────────────

    static long StoredBytes(string path)
        => Count(path, "SELECT (SELECT IFNULL(SUM(size),0) FROM entity) + (SELECT IFNULL(SUM(size),0) FROM artist_overview) " +
                       "+ (SELECT IFNULL(SUM(length(payload)),0) FROM localized_extension_cache);");

    [Fact]
    public void CacheBytes_TracksTheCacheTier_AcrossUpsertReplaceAndDelete()
    {
        var path = TempDb();
        try
        {
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            {
                Assert.Equal(0, cold.GetCacheBytes());

                var small = new Track("t", "spotify:track:t", "T", [], new AlbumRef("", "", ""), 1000, false, null);
                cold.UpsertEntity(small.Uri, EntityKind.Track, Json(small, EntityJson.Default.Track));
                cold.Flush();
                long afterInsert = cold.GetCacheBytes();
                Assert.True(afterInsert > 0);
                Assert.Equal(StoredBytes(path), afterInsert);

                // A REPLACE must move the counter by the delta, not add the whole new row again.
                var big = small with { Title = new string('t', 8000) };
                cold.UpsertEntity(big.Uri, EntityKind.Track, Json(big, EntityJson.Default.Track));
                cold.Flush();
                Assert.Equal(StoredBytes(path), cold.GetCacheBytes());
                Assert.True(cold.GetCacheBytes() > afterInsert);
                Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity;"));   // still one row

                // artist_overview rides the same counter.
                cold.UpsertArtistOverview("spotify:artist:a", Locale, FatArtistJson(), 500);
                Assert.Equal(StoredBytes(path), cold.GetCacheBytes());
                var ov = cold.GetArtistOverview("spotify:artist:a");
                Assert.NotNull(ov);
                Assert.Equal(FatArtistJson(), ov!.Value.Payload);
                Assert.Equal(500, ov.Value.FetchedAtUnixSeconds);

                // …and a replace of the overview is a delta too.
                cold.UpsertArtistOverview("spotify:artist:a", Locale, Encoding.UTF8.GetBytes("{}"), 600);
                Assert.Equal(StoredBytes(path), cold.GetCacheBytes());
                Assert.Equal(1L, Count(path, "SELECT count(*) FROM artist_overview;"));

                // extension rows count too.
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                cold.UpsertExtension(new ColdExtension("spotify:album:x", 7, new byte[512], "etag", 300, false, now + 3600, now));
                cold.Flush();
                Assert.Equal(StoredBytes(path), cold.GetCacheBytes());
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ReplaceEntityRefs_ReplacesNotAppends()
    {
        var path = TempDb();
        try
        {
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            cold.ReplaceEntityRefs("spotify:track:t", new[] { "spotify:album:a", "spotify:artist:a", "" });
            Assert.Equal(2L, Count(path, "SELECT count(*) FROM entity_refs WHERE parent_uri='spotify:track:t';"));
            cold.ReplaceEntityRefs("spotify:track:t", new[] { "spotify:album:b" });
            Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity_refs WHERE parent_uri='spotify:track:t';"));
            Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity_refs WHERE child_uri='spotify:album:b';"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void VacuumPending_RunsOnceThenClears()
    {
        var path = TempDb();
        try
        {
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            Assert.Equal("1", Scalar(path, "SELECT value FROM meta WHERE key='vacuum_pending';") as string);
            Assert.True(cold.RunFullVacuumIfPending());
            Assert.Equal("0", Scalar(path, "SELECT value FROM meta WHERE key='vacuum_pending';") as string);
            Assert.False(cold.RunFullVacuumIfPending());
            Assert.Equal(2L, Convert.ToInt64(Scalar(path, "PRAGMA auto_vacuum;")));   // 2 = INCREMENTAL
            cold.RunIncrementalVacuum(200);                                            // no-throw slice
        }
        finally { TryDelete(path); }
    }

    // ── (f) the extension sweep ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtensionSweep_DropsRowsPastTheSevenDayGrace_AndKeepsRecentlyExpiredOnes()
    {
        var path = TempDb();
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long day = 24 * 60 * 60;
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            {
                cold.UpsertExtension(new ColdExtension("spotify:album:stale", 1, new byte[400], "e1", 300, false, now - 8 * day, now - 8 * day));
                cold.UpsertExtension(new ColdExtension("spotify:album:grace", 1, new byte[400], "e2", 300, false, now - 2 * day, now - 2 * day));
                cold.UpsertExtension(new ColdExtension("spotify:album:live", 1, new byte[400], "e3", 300, false, now + day, now));
                cold.Flush();
                Assert.Equal(3, cold.LoadAllExtensions().Count());
            }

            // The sweep is a TTL tier driven by the RECURRING GC pass, NOT by open (see SweepExpiredExtensionsNow) — an
            // open-time sweep put an unbounded delete on the startup path. Drive it the way the GC pass does; what this
            // case is about is the CUTOFF, and that is unchanged either way.
            using (var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale))
            {
                var (sweptRows, sweptBytes) = cold.SweepExpiredExtensionsNow();
                Assert.Equal(1, sweptRows);                                   // only the >+7d row
                Assert.True(sweptBytes > 0, "the sweep must report the payload bytes it freed");

                var kept = cold.LoadAllExtensions().Select(x => x.EntityUri).ToHashSet(StringComparer.Ordinal);
                Assert.Equal(2, kept.Count);
                Assert.DoesNotContain("spotify:album:stale", kept);          // > +7d past expiry
                Assert.Contains("spotify:album:grace", kept);                 // inside the ETag-revalidation grace
                Assert.Contains("spotify:album:live", kept);
                Assert.Equal(StoredBytes(path), cold.GetCacheBytes());        // the sweep kept the counter honest
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void LoadAllExtensions_HonoursTheLimit_NewestFirst()
    {
        var path = TempDb();
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            for (int i = 0; i < 10; i++)
                cold.UpsertExtension(new ColdExtension("spotify:album:e" + i, 1, new byte[16], null, 0, false, now + 3600, now + i));
            cold.Flush();

            var top = cold.LoadAllExtensions(3).ToList();
            Assert.Equal(3, top.Count);
            Assert.Equal("spotify:album:e9", top[0].EntityUri);   // newest first
            Assert.Equal(10, cold.LoadAllExtensions(0).Count());  // 0 = uncapped
            Assert.Equal(10, cold.LoadAllExtensions().Count());   // the 2048 default is well above this fixture
        }
        finally { TryDelete(path); }
    }

    // ── (g) shapes the REAL v4 library.db turned out to contain (Wave E migration smoke) ──────────────────────────────
    // Each of these was found by migrating the developer's own 168 MB schema-v4 file, and none of them was reachable from
    // the hand-built fixture above. They are pinned here so a later refactor cannot silently regress the real database.

    /// <summary>568 of the developer's 11 238 migrated tracks carry <c>"Title":""</c> — skeleton rows the legacy hydrator
    /// wrote from a membership response before the metadata fetch landed. They must MIGRATE (a pinned row is never
    /// dropped), leave <c>title</c> NULL rather than empty-string, and still be findable offline through the artist
    /// name — which is the only handle the user has on them.</summary>
    [Fact]
    public void Migration_KeepsALegacyTrackWithAnEmptyTitle_AndOfflineSearchStillFindsItByArtist()
    {
        var path = TempDb();
        try
        {
            SeedV4Schema(path);
            Exec(path, "INSERT INTO collection_items VALUES('default','liked','spotify:track:notitle',10,NULL,0);");
            var skeleton = new Track("nt", "spotify:track:notitle", "",
                [new ArtistRef("jk", "spotify:artist:jk", "Jukjae")], new AlbumRef("al", "spotify:album:al", "Some Album"),
                0, false, new Image("https://cdn.example/nt.jpg"));
            using (var c = Open(path))
                Legacy(c, "spotify:track:notitle", Locale, EntityKind.Track, Json(skeleton, EntityJson.Default.Track), 100);

            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            Assert.NotNull(cold.GetEntity("spotify:track:notitle"));
            // NULL, not "": NullIfEmpty is what keeps `title IS NOT NULL` a usable "has a display title" predicate.
            Assert.Equal(1L, Count(path,
                "SELECT count(*) FROM entity WHERE uri='spotify:track:notitle' AND title IS NULL AND subtitle='Jukjae' AND fmt=1;"));

            // It is in the SQL candidate corpus, and the offline track search reaches it through the artist leg.
            var candidates = cold.LoadLibraryCandidates(ColdCandidateScope.LibraryTracks);
            Assert.Contains(candidates.Tracks, r => r.Uri == "spotify:track:notitle" && r.Title is null && r.Subtitle == "Jukjae");

            using var store = new CachedStore(cold);
            store.WarmComplete.GetAwaiter().GetResult();
            Assert.Contains(Wavee.Backend.Library.LibraryTrackSearch.Search(store, "jukj", 50),
                            t => t.Uri == "spotify:track:notitle");
        }
        finally { TryDelete(path); }
    }

    /// <summary>16 886 of the developer's 25 511 migrated <c>entity_refs</c> edges point at an album/artist that has NO
    /// <c>entity</c> row — the legacy tables simply never cached that parent's metadata. Dangling edges are legitimate
    /// (the edge is a pin REASON, not a foreign key), so every consumer must tolerate them: the GC pin closure, the
    /// candidate corpus, and the sweeps.</summary>
    [Fact]
    public void DanglingEntityRefs_AreTolerated_ByThePinClosure_TheSweeps_AndTheCandidateCorpus()
    {
        var path = TempDb();
        try
        {
            SeedV4Schema(path);
            Exec(path, """
                INSERT INTO collection_items VALUES('default','liked','spotify:track:dangler',10,NULL,0);
                INSERT INTO collection_items VALUES('default','artists','spotify:artist:ghost',11,NULL,0);
                """);
            // The track migrates; its album and BOTH its artists have no legacy row at all, so the closure pass finds
            // nothing to migrate and the three edges are left dangling.
            var t = new Track("dg", "spotify:track:dangler", "Dangler",
                [new ArtistRef("gh", "spotify:artist:ghost", "Ghost"), new ArtistRef("g2", "spotify:artist:ghost2", "Ghost Two")],
                new AlbumRef("ga", "spotify:album:ghost", "Ghost Album"), 1000, false, null);
            using (var c = Open(path))
                Legacy(c, "spotify:track:dangler", Locale, EntityKind.Track, Json(t, EntityJson.Default.Track), 100);

            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity;"));
            Assert.Equal(3L, Count(path, "SELECT count(*) FROM entity_refs WHERE parent_uri='spotify:track:dangler';"));
            Assert.Equal(3L, Count(path,
                "SELECT count(*) FROM entity_refs r WHERE NOT EXISTS(SELECT 1 FROM entity e WHERE e.uri=r.child_uri);"));

            // The candidate corpus joins straight through them without producing phantom rows or throwing.
            Assert.Empty(cold.LoadLibraryCandidates(ColdCandidateScope.SavedArtists).Roots);
            Assert.Contains(cold.LoadLibraryCandidates(ColdCandidateScope.LibraryTracks).Tracks, r => r.Uri == "spotify:track:dangler");

            // A full GC pass: the dangling children join into the pin table (harmlessly — they have no row to protect),
            // and the pinned parent is never a victim even with everything stale and the budget at 1 byte.
            cold.GcBeginPass(null);
            Assert.True(cold.GcPinnedCount() >= 4);   // the track + its 3 dangling children
            long far = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86_400;
            Assert.Equal(0, cold.GcSweepUnpinnedEntities(far, far, System.Threading.CancellationToken.None).Rows);
            Assert.Equal(0, cold.GcEnforceBudget(1, far, System.Threading.CancellationToken.None).Rows);
            cold.GcEndPass();
            Assert.Equal(1L, Count(path, "SELECT count(*) FROM entity;"));
        }
        finally { TryDelete(path); }
    }

    /// <summary>The real migration moved 14 197 rows — eight keyset pages of <c>MigrationBatchRows</c>. The hand-built
    /// fixture above is six rows, so the <c>uri &gt; $last</c> paging loop (and the per-batch transaction boundary) was
    /// never exercised by a test at all. Seed past the batch size and assert nothing is skipped or double-counted.</summary>
    [Fact]
    public void Migration_PagesPastTheBatchSize_MigratingEveryPinReachableRow()
    {
        const int Rows = 2500;   // > SqliteColdStore's 2000-row MigrationBatchRows ⇒ at least two pages
        var path = TempDb();
        try
        {
            SeedV4Schema(path);
            using (var c = Open(path))
            using (var tx = c.BeginTransaction())
            {
                using var pin = c.CreateCommand();
                pin.Transaction = tx;
                pin.CommandText = "INSERT INTO collection_items VALUES('default','liked',$u,0,NULL,0);";
                var pu = pin.Parameters.Add("$u", SqliteType.Text);
                using var ins = c.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO localized_entities(uri,locale,kind,payload,updated_at) VALUES($u,$l,$k,$p,100);";
                var iu = ins.Parameters.Add("$u", SqliteType.Text);
                ins.Parameters.Add("$l", SqliteType.Text).Value = Locale;
                ins.Parameters.Add("$k", SqliteType.Integer).Value = (int)EntityKind.Track;
                var ip = ins.Parameters.Add("$p", SqliteType.Blob);
                for (int i = 0; i < Rows; i++)
                {
                    // Zero-padded so the keyset cursor's ORDINAL uri ordering is the same as the numeric one.
                    string uri = $"spotify:track:{i:D6}";
                    pu.Value = uri; pin.ExecuteNonQuery();
                    iu.Value = uri;
                    ip.Value = Json(new Track(i.ToString(), uri, "Track " + i, [], new AlbumRef("", "", ""), 1000, false, null),
                                    EntityJson.Default.Track);
                    ins.ExecuteNonQuery();
                }
                tx.Commit();
            }

            using var cold = new SqliteColdStore(path, SqliteColdStore.DefaultAccount, Locale);
            Assert.Equal((long)Rows, Count(path, "SELECT count(*) FROM entity;"));
            Assert.Equal((long)Rows, Count(path, "SELECT count(DISTINCT uri) FROM entity;"));
            Assert.Equal((long)Rows, Count(path, "SELECT count(*) FROM entity WHERE title IS NOT NULL AND fmt=1;"));
            // First, a mid-page boundary row, and last — the three places a keyset cursor bug shows up.
            foreach (var i in new[] { 0, 1999, 2000, Rows - 1 })
                Assert.NotNull(cold.GetEntity($"spotify:track:{i:D6}"));
            Assert.Equal(StoredBytes(path), cold.GetCacheBytes());
        }
        finally { TryDelete(path); }
    }
}
