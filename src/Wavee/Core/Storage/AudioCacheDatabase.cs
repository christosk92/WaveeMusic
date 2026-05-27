using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Wavee.Core.Storage.Abstractions;

namespace Wavee.Core.Storage;

/// <summary>
/// SQLite store for the audio resolver's hot-path caches (<c>head_data</c>
/// and <c>cdn_cache</c>). Lives in its own file (<c>audio_cache.db</c>) with
/// its own write semaphore so playback writes never queue behind library
/// or playlist sync.
///
/// On first construction, if the legacy <see cref="MetadataDatabase"/> still
/// contains <c>head_data</c> or <c>cdn_cache</c> rows, they are copied into
/// this file and the source tables are dropped. This is run synchronously
/// from <see cref="MigrateFromMetadataAsync"/> — call it once at startup
/// before any audio playback begins.
/// </summary>
public sealed class AudioCacheDatabase : IAudioCacheDatabase
{
    private static readonly TimeSpan WriteLockSlowWaitThreshold = TimeSpan.FromMilliseconds(250);

    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _writeLockStateGate = new();
    private string? _writeLockOwner;
    private long _writeLockOwnerId;
    private DateTimeOffset _writeLockAcquiredAt;
    private int _writeLockOwnerThreadId;
    private long _writeLockSequence;
    private bool _disposed;

    public AudioCacheDatabase(string databasePath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _databasePath = databasePath;
        _logger = logger;

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ConnectionString;

        EnsureSchema();

        _logger?.LogInformation("AudioCacheDatabase initialized at {Path}", databasePath);
    }

    public string DatabasePath => _databasePath;

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            """;
        pragma.ExecuteNonQuery();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS head_data (
                file_id    TEXT PRIMARY KEY NOT NULL,
                data       BLOB NOT NULL,
                cached_at  INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS cdn_cache (
                file_id    TEXT PRIMARY KEY NOT NULL,
                url        TEXT NOT NULL,
                expiry_ms  INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One-shot startup migration: if the legacy metadata database still
    /// contains <c>head_data</c> or <c>cdn_cache</c> tables, copy any rows
    /// into this file and drop the source tables. Idempotent — when the
    /// source tables are absent this is a no-op.
    /// </summary>
    public async Task MigrateFromMetadataAsync(string legacyMetadataDbPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyMetadataDbPath);

        if (!File.Exists(legacyMetadataDbPath))
            return;

        var legacyConnString = new SqliteConnectionStringBuilder
        {
            DataSource = legacyMetadataDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ConnectionString;

        await using var legacy = new SqliteConnection(legacyConnString);
        await legacy.OpenAsync(ct);

        var hasHead = await TableExistsAsync(legacy, "head_data", ct);
        var hasCdn = await TableExistsAsync(legacy, "cdn_cache", ct);
        if (!hasHead && !hasCdn)
            return;

        using var __lease = await AcquireWriteLockAsync(nameof(MigrateFromMetadataAsync), 0, ct);

        await using var dest = new SqliteConnection(_connectionString);
        await dest.OpenAsync(ct);

        var copiedHead = 0;
        var copiedCdn = 0;

        if (hasHead)
        {
            using var read = legacy.CreateCommand();
            read.CommandText = "SELECT file_id, data, cached_at FROM head_data";
            await using var reader = await read.ExecuteReaderAsync(ct);

            using var tx = (SqliteTransaction)await dest.BeginTransactionAsync(ct);
            using var insert = dest.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR REPLACE INTO head_data (file_id, data, cached_at)
                VALUES (@id, @data, @cached);
                """;
            var idParam = insert.Parameters.Add("@id", SqliteType.Text);
            var dataParam = insert.Parameters.Add("@data", SqliteType.Blob);
            var cachedParam = insert.Parameters.Add("@cached", SqliteType.Integer);
            while (await reader.ReadAsync(ct))
            {
                idParam.Value = reader.GetString(0);
                dataParam.Value = (byte[])reader.GetValue(1);
                cachedParam.Value = reader.GetInt64(2);
                await insert.ExecuteNonQueryAsync(ct);
                copiedHead++;
            }
            await reader.CloseAsync();
            await tx.CommitAsync(ct);
        }

        if (hasCdn)
        {
            using var read = legacy.CreateCommand();
            read.CommandText = "SELECT file_id, url, expiry_ms FROM cdn_cache";
            await using var reader = await read.ExecuteReaderAsync(ct);

            using var tx = (SqliteTransaction)await dest.BeginTransactionAsync(ct);
            using var insert = dest.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT OR REPLACE INTO cdn_cache (file_id, url, expiry_ms)
                VALUES (@id, @url, @expiry);
                """;
            var idParam = insert.Parameters.Add("@id", SqliteType.Text);
            var urlParam = insert.Parameters.Add("@url", SqliteType.Text);
            var expiryParam = insert.Parameters.Add("@expiry", SqliteType.Integer);
            while (await reader.ReadAsync(ct))
            {
                idParam.Value = reader.GetString(0);
                urlParam.Value = reader.GetString(1);
                expiryParam.Value = reader.GetInt64(2);
                await insert.ExecuteNonQueryAsync(ct);
                copiedCdn++;
            }
            await reader.CloseAsync();
            await tx.CommitAsync(ct);
        }

        // Drop legacy source tables — they're owned by this database now.
        using (var drop = legacy.CreateCommand())
        {
            drop.CommandText = """
                DROP TABLE IF EXISTS head_data;
                DROP TABLE IF EXISTS cdn_cache;
                """;
            await drop.ExecuteNonQueryAsync(ct);
        }

        _logger?.LogInformation(
            "AudioCacheDatabase: migrated {Head} head_data + {Cdn} cdn_cache row(s) from legacy metadata.db",
            copiedHead, copiedCdn);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", tableName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    public async Task<byte[]?> GetPersistedHeadDataAsync(string fileIdHex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT data FROM head_data WHERE file_id = @file_id";
        cmd.Parameters.AddWithValue("@file_id", fileIdHex);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
            return (byte[])reader.GetValue(0);
        return null;
    }

    public async Task SetPersistedHeadDataAsync(string fileIdHex, byte[] headData, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var __lease = await AcquireWriteLockAsync(nameof(SetPersistedHeadDataAsync), 1, ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO head_data (file_id, data, cached_at)
            VALUES (@file_id, @data, @cached)
            """;
        cmd.Parameters.AddWithValue("@file_id", fileIdHex);
        cmd.Parameters.AddWithValue("@data", headData);
        cmd.Parameters.AddWithValue("@cached", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string Url, DateTimeOffset Expiry)?> GetPersistedCdnUrlAsync(string fileIdHex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string url;
        long expiryMs;
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(ct);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT url, expiry_ms FROM cdn_cache WHERE file_id = @file_id";
            cmd.Parameters.AddWithValue("@file_id", fileIdHex);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) return null;

            url = reader.GetString(0);
            expiryMs = reader.GetInt64(1);
        }

        var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expiryMs);
        if (expiry <= DateTimeOffset.UtcNow)
        {
            try
            {
                _ = DeletePersistedCdnUrlAsync(fileIdHex, CancellationToken.None);
            }
            catch { }
            return null;
        }
        return (url, expiry);
    }

    public async Task SetPersistedCdnUrlAsync(string fileIdHex, string url, DateTimeOffset expiry, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var __lease = await AcquireWriteLockAsync(nameof(SetPersistedCdnUrlAsync), 1, ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO cdn_cache (file_id, url, expiry_ms)
            VALUES (@file_id, @url, @expiry_ms)
            """;
        cmd.Parameters.AddWithValue("@file_id", fileIdHex);
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@expiry_ms", expiry.ToUnixTimeMilliseconds());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task DeletePersistedCdnUrlAsync(string fileIdHex, CancellationToken ct)
    {
        using var __lease = await AcquireWriteLockAsync(nameof(DeletePersistedCdnUrlAsync), 1, ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM cdn_cache WHERE file_id = @file_id";
        cmd.Parameters.AddWithValue("@file_id", fileIdHex);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Tracked write-lock — mirrors MetadataDatabase's pattern so this
    //    file's contention events name their holder in the log. ──

    private async Task<WriteLockLease> AcquireWriteLockAsync(string operation, int itemCount, CancellationToken cancellationToken)
    {
        var waitStarted = Stopwatch.GetTimestamp();
        if (!await _writeLock.WaitAsync(WriteLockSlowWaitThreshold, cancellationToken).ConfigureAwait(false))
        {
            var snapshot = GetWriteLockOwnerSnapshot();
            _logger?.LogWarning(
                "AudioCacheDatabase write lock wait: waiter={Operation} items={ItemCount} waitingMs>{ThresholdMs:F0} owner={Owner} ownerId={OwnerId} ownerThread={OwnerThread} heldMs={HeldMs:F0}",
                operation,
                itemCount,
                WriteLockSlowWaitThreshold.TotalMilliseconds,
                snapshot.Owner ?? "<unknown>",
                snapshot.OwnerId,
                snapshot.OwnerThreadId,
                snapshot.Held?.TotalMilliseconds ?? -1);

            try
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        var waitElapsed = Stopwatch.GetElapsedTime(waitStarted);
        var ownerId = Interlocked.Increment(ref _writeLockSequence);
        lock (_writeLockStateGate)
        {
            _writeLockOwner = itemCount > 0 ? $"{operation}({itemCount})" : operation;
            _writeLockOwnerId = ownerId;
            _writeLockAcquiredAt = DateTimeOffset.UtcNow;
            _writeLockOwnerThreadId = Environment.CurrentManagedThreadId;
        }

        if (waitElapsed >= WriteLockSlowWaitThreshold)
        {
            _logger?.LogWarning(
                "AudioCacheDatabase write lock acquired after slow wait: owner={Owner} ownerId={OwnerId} waitMs={WaitMs:F0}",
                itemCount > 0 ? $"{operation}({itemCount})" : operation,
                ownerId,
                waitElapsed.TotalMilliseconds);
        }

        return new WriteLockLease(this, ownerId, itemCount > 0 ? $"{operation}({itemCount})" : operation);
    }

    private (string? Owner, long OwnerId, int OwnerThreadId, TimeSpan? Held) GetWriteLockOwnerSnapshot()
    {
        lock (_writeLockStateGate)
        {
            var held = _writeLockOwner is null
                ? (TimeSpan?)null
                : DateTimeOffset.UtcNow - _writeLockAcquiredAt;
            return (_writeLockOwner, _writeLockOwnerId, _writeLockOwnerThreadId, held);
        }
    }

    private void ReleaseWriteLock(long ownerId, string owner)
    {
        TimeSpan held;
        lock (_writeLockStateGate)
        {
            held = _writeLockOwnerId == ownerId
                ? DateTimeOffset.UtcNow - _writeLockAcquiredAt
                : TimeSpan.Zero;

            if (_writeLockOwnerId == ownerId)
            {
                _writeLockOwner = null;
                _writeLockOwnerId = 0;
                _writeLockOwnerThreadId = 0;
                _writeLockAcquiredAt = default;
            }
        }

        if (held >= WriteLockSlowWaitThreshold)
        {
            _logger?.LogWarning(
                "AudioCacheDatabase write lock held for {HeldMs:F0}ms by {Owner} ownerId={OwnerId}",
                held.TotalMilliseconds,
                owner,
                ownerId);
        }

        _writeLock.Release();
    }

    private readonly struct WriteLockLease : IDisposable
    {
        private readonly AudioCacheDatabase _owner;
        private readonly long _ownerId;
        private readonly string _operation;

        public WriteLockLease(AudioCacheDatabase owner, long ownerId, string operation)
        {
            _owner = owner;
            _ownerId = ownerId;
            _operation = operation;
        }

        public void Dispose() => _owner.ReleaseWriteLock(_ownerId, _operation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
        _logger?.LogDebug("AudioCacheDatabase disposed");
        await Task.CompletedTask;
    }
}
