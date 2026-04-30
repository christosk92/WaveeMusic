using System.Reactive.Subjects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Library.Local;

public sealed class LocalLikeService : ILocalLikeService, IDisposable
{
    private readonly string _connectionString;
    private readonly Subject<(string TrackUri, bool Liked)> _changes = new();
    private readonly ILogger? _logger;

    public LocalLikeService(string databasePath, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var b = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        _connectionString = b.ConnectionString;
        _logger = logger;
    }

    public IObservable<(string TrackUri, bool Liked)> Changes => _changes;

    public Task<bool> IsLikedAsync(string trackUri, CancellationToken ct = default)
    {
        if (!LocalUri.IsTrack(trackUri)) return Task.FromResult(false);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_locally_liked FROM entities WHERE uri = $u LIMIT 1;";
        cmd.Parameters.AddWithValue("$u", trackUri);
        var result = cmd.ExecuteScalar();
        bool liked = result is not null and not DBNull && Convert.ToInt32(result) != 0;
        return Task.FromResult(liked);
    }

    public Task SetLikedAsync(string trackUri, bool liked, CancellationToken ct = default)
    {
        if (!LocalUri.IsTrack(trackUri))
            throw new ArgumentException("Only wavee:local:track: URIs are supported.", nameof(trackUri));

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE entities SET is_locally_liked = $v WHERE uri = $u;";
        cmd.Parameters.AddWithValue("$v", liked ? 1 : 0);
        cmd.Parameters.AddWithValue("$u", trackUri);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
            _logger?.LogDebug("Like toggle for unindexed track {Uri} ignored", trackUri);
        else
            _changes.OnNext((trackUri, liked));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetLikedTrackUrisAsync(CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT uri FROM entities
            WHERE source_type = 1 AND entity_type = 1 AND is_locally_liked = 1
            ORDER BY updated_at DESC;
            """;
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return Task.FromResult<IReadOnlyList<string>>(list);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public void Dispose() => _changes.Dispose();
}
