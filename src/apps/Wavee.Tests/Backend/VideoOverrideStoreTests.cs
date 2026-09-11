using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Catalog;
using Wavee.Backend.Persistence;
using Xunit;

namespace Wavee.Tests;

public sealed class VideoOverrideStoreTests
{
    [Fact]
    public async Task DurableAttachReplaceDurationAndRemoveSurviveRestart()
    {
        using var database = new Database();
        using (var cold = database.Open())
        {
            await using var queue = new DataCommitQueue();
            var service = new VideoOverrideService(queue, cold, await cold.ReadVideoOverridesAsync());
            await service.AttachAsync("spotify:track:a", @"C:\videos\a.mp4");
            await service.AttachAsync("spotify:episode:e", @"C:\videos\e.mp4");
            await service.NoteDurationAsync("spotify:track:a", 3210);
        }
        using (var cold = database.Open())
        {
            await using var queue = new DataCommitQueue();
            var service = new VideoOverrideService(queue, cold, await cold.ReadVideoOverridesAsync());
            Assert.Equal(2, service.Count);
            Assert.True(service.TryGetActive("spotify:track:a", out var prior));
            Assert.Equal(3210, prior.DurationMs);
            Assert.StartsWith("local:video:", prior.SourceKey, StringComparison.Ordinal);
            await service.AttachAsync("spotify:track:a", @"D:\other.mp4");
            Assert.True(await service.RemoveAsync("spotify:episode:e"));
            Assert.False(await service.RemoveAsync("spotify:episode:e"));
        }
        using (var cold = database.Open())
        {
            var row = Assert.Single(await cold.ReadVideoOverridesAsync());
            Assert.Equal(@"D:\other.mp4", row.Path);
            Assert.Equal(0, row.DurationMs);
        }
    }

    [Fact]
    public async Task FailedAttachOrRemoveDoesNotChangeTheWarmViewOrNotify()
    {
        await using var queue = new DataCommitQueue();
        var persistence = new ControllablePersistence();
        var service = new VideoOverrideService(queue, persistence, []);
        int changes = 0; service.OnChanged = (_, _) => changes++;
        var original = await service.AttachAsync("spotify:track:a", @"C:\videos\a.mp4");
        persistence.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => service.AttachAsync("spotify:track:a", @"D:\other.mp4"));
        await Assert.ThrowsAsync<IOException>(() => service.RemoveAsync("spotify:track:a"));
        Assert.True(service.TryGetActive("spotify:track:a", out var visible));
        Assert.Equal(original, visible);
        Assert.Equal(original, Assert.Single(await persistence.LoadAsync()));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task PendingDiskWriteDoesNotPublishAnAttachmentBeforeDurability()
    {
        await using var queue = new DataCommitQueue();
        var persistence = new ControllablePersistence { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new VideoOverrideService(queue, persistence, []);
        bool notified = false; service.OnChanged = (_, _) => notified = true;
        var pending = service.AttachAsync("spotify:track:a", @"C:\videos\a.mp4");
        await persistence.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Has("spotify:track:a")); Assert.False(notified);
        persistence.Gate.SetResult();
        await pending;
        Assert.True(service.Has("spotify:track:a")); Assert.True(notified);
    }

    [Fact]
    public async Task LateDurationAfterRemovalCannotResurrectTheAttachment()
    {
        await using var queue = new DataCommitQueue();
        var persistence = new MemoryVideoOverridePersistence();
        var service = new VideoOverrideService(queue, persistence, []);
        await service.AttachAsync("spotify:track:a", @"C:\videos\a.mp4");
        var removed = service.RemoveAsync("spotify:track:a");
        var duration = service.NoteDurationAsync("spotify:track:a", 1000);
        await Task.WhenAll(removed, duration);
        Assert.False(service.Has("spotify:track:a"));
        Assert.Empty(await persistence.LoadAsync());
    }

    [Fact]
    public async Task SchemaResetKeepsVideoOverrides()
    {
        using var database = new Database();
        database.Execute("CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT); INSERT INTO meta VALUES('schema_version','3');" +
            "CREATE TABLE video_override(uri TEXT PRIMARY KEY,path TEXT NOT NULL,id TEXT NOT NULL,duration_ms INTEGER,size INTEGER,mtime INTEGER,added_at INTEGER);" +
            "INSERT INTO video_override VALUES('spotify:track:kept','C:\\videos\\keep.mp4','stable',123,456,789,1000);");
        using var cold = database.Open();
        var row = Assert.Single(await cold.ReadVideoOverridesAsync());
        Assert.Equal("spotify:track:kept", row.Uri); Assert.Equal("stable", row.Id); Assert.Equal(123, row.DurationMs);
        Assert.Equal("12", database.Scalar("SELECT value FROM meta WHERE key='schema_version';"));
    }

    sealed class ControllablePersistence : IVideoOverridePersistence
    {
        readonly MemoryVideoOverridePersistence _inner = new();
        public bool Fail;
        public TaskCompletionSource? Gate;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<VideoOverride>> LoadAsync(CancellationToken ct = default) => _inner.LoadAsync(ct);
        public async ValueTask WriteAsync(VideoOverride row, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            if (Fail) throw new IOException("durability failed");
            await _inner.WriteAsync(row, ct);
        }
        public ValueTask DeleteAsync(string uri, CancellationToken ct = default)
            => Fail ? ValueTask.FromException(new IOException("durability failed")) : _inner.DeleteAsync(uri, ct);
    }

    sealed class Database : IDisposable
    {
        readonly string _path = Path.Combine(Path.GetTempPath(), "wavee-curation-" + Guid.NewGuid().ToString("N") + ".db");
        public SqliteColdStore Open() => new(_path);
        public void Execute(string sql) { using var c = Connect(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        public object? Scalar(string sql) { using var c = Connect(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
        SqliteConnection Connect() { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString()); c.Open(); return c; }
        public void Dispose() { foreach (var p in new[] { _path, _path + "-wal", _path + "-shm" }) File.Delete(p); }
    }
}
