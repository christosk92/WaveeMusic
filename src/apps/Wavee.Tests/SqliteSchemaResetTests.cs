using System;
using Wavee.Backend.Persistence;
using Xunit;

namespace Wavee.Tests;

// Schema v12: an older `library.db` is dropped and rebuilt from the network rather than migrated in place. These
// tests pin the reset rule directly (constructor + ResetTables), independent of any single caller's schema.
public sealed class SqliteSchemaResetTests
{
    [Fact]
    public void AnOlderSchemaDropsCatalogAndReplicaStateButKeepsUserOwnedTablesAndTheBudget()
    {
        using var db = new CatalogTestDb();
        db.Exec("CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);" +
            "INSERT INTO meta VALUES('schema_version','11');" +
            "INSERT INTO meta VALUES('cache_budget_bytes','12345');" +
            "INSERT INTO meta VALUES('catalog_bytes','999');" +
            "CREATE TABLE catalog_scope(scope_id INTEGER PRIMARY KEY,provider TEXT);" +
            "INSERT INTO catalog_scope VALUES(1,'spotify');" +
            "CREATE TABLE catalog_resource(scope_id INTEGER,subject TEXT);" +
            "INSERT INTO catalog_resource VALUES(1,'spotify:track:a');" +
            "CREATE TABLE replica_state(storage_account TEXT,owner_account TEXT,kind TEXT,id TEXT);" +
            "INSERT INTO replica_state VALUES('default','alice','playlist','p');" +
            "CREATE TABLE outbox(id INTEGER PRIMARY KEY,type TEXT,entity_key TEXT);" +
            "INSERT INTO outbox VALUES(1,'set','spotify:track:a');" +
            "CREATE TABLE activity_log(id INTEGER PRIMARY KEY,kind INTEGER NOT NULL,target_uri TEXT NOT NULL,target_name TEXT," +
            "payload TEXT,created_at INTEGER NOT NULL,status INTEGER NOT NULL DEFAULT 0,read INTEGER NOT NULL DEFAULT 0);" +
            "INSERT INTO activity_log(kind,target_uri,created_at) VALUES(1,'spotify:track:a',1000);" +
            "CREATE TABLE recent_surfaces(uri TEXT PRIMARY KEY,kind INTEGER,last_opened INTEGER);" +
            "INSERT INTO recent_surfaces VALUES('spotify:album:a',3,1000);" +
            "CREATE TABLE video_override(uri TEXT PRIMARY KEY,path TEXT NOT NULL,id TEXT NOT NULL," +
            "duration_ms INTEGER DEFAULT 0,size INTEGER DEFAULT 0,mtime INTEGER DEFAULT 0,added_at INTEGER DEFAULT 0);" +
            "INSERT INTO video_override VALUES('spotify:track:a','C:\\v.mp4','v',0,0,0,0);");

        using var cold = new SqliteColdStore(db.Path);

        Assert.Equal(11, cold.ResetFromSchema);
        Assert.Equal("12", db.Value("SELECT value FROM meta WHERE key='schema_version';"));
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_scope;"));
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM catalog_resource;"));
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM replica_state;"));
        Assert.Equal(0, db.Number("SELECT COUNT(*) FROM outbox;"));
        Assert.Equal(1, db.Number("SELECT COUNT(*) FROM activity_log;"));
        Assert.Equal(1, db.Number("SELECT COUNT(*) FROM recent_surfaces;"));
        Assert.Equal(1, db.Number("SELECT COUNT(*) FROM video_override;"));
        Assert.Equal("12345", db.Value("SELECT value FROM meta WHERE key='cache_budget_bytes';"));
        Assert.Equal("1", db.Value("SELECT value FROM meta WHERE key='cache_vacuum_pending';"));
        // EnsureCatalogAccountingLocked runs after the reset and re-seeds the byte counter for the now-empty catalog.
        Assert.Equal("0", db.Value("SELECT value FROM meta WHERE key='catalog_bytes';"));
    }

    [Fact]
    public void ABrandNewFileStartsAtTheCurrentSchemaWithoutAReset()
    {
        using var db = new CatalogTestDb();
        using var cold = new SqliteColdStore(db.Path);
        Assert.Null(cold.ResetFromSchema);
        Assert.Equal("12", db.Value("SELECT value FROM meta WHERE key='schema_version';"));
    }

    [Fact]
    public void ReopeningACurrentSchemaFileKeepsItsRowsAndReportsNoReset()
    {
        using var db = new CatalogTestDb();
        using (var first = new SqliteColdStore(db.Path)) Assert.Null(first.ResetFromSchema);
        db.Exec("INSERT INTO recent_surfaces VALUES('spotify:album:a',3,1000);");

        using var reopened = new SqliteColdStore(db.Path);

        Assert.Null(reopened.ResetFromSchema);
        Assert.Equal(1, db.Number("SELECT COUNT(*) FROM recent_surfaces;"));
    }

    [Fact]
    public void ANewerSchemaThrows()
    {
        using var db = new CatalogTestDb();
        db.Exec("CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT); INSERT INTO meta VALUES('schema_version','13');");
        Assert.Throws<InvalidOperationException>(() => new SqliteColdStore(db.Path));
    }
}
