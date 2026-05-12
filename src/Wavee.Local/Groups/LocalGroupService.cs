using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Wavee.Local.Models;

namespace Wavee.Local.Groups;

/// <summary>
/// Manages user-defined collections (and auto-generated show/album groups) in
/// <c>local_groups</c> + <c>local_group_members</c>. Backed by the shared
/// SQLite DB.
/// </summary>
public sealed class LocalGroupService
{
    private readonly string _connectionString;
    private readonly ILogger? _logger;

    public LocalGroupService(string connectionString, ILogger? logger = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
    }

    public Task<IReadOnlyList<LocalCollection>> ListAsync(string? kindFilter = null, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.id, g.name, g.kind, g.poster_hash, g.created_at, g.user_created,
                   (SELECT COUNT(*) FROM local_group_members m WHERE m.group_id = g.id) AS items
            FROM local_groups g
            WHERE ($k IS NULL OR g.kind = $k)
            ORDER BY g.created_at DESC;
            """;
        cmd.Parameters.AddWithValue("$k", (object?)kindFilter ?? DBNull.Value);
        var list = new List<LocalCollection>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var posterHash = r.IsDBNull(3) ? null : r.GetString(3);
            list.Add(new LocalCollection(
                Id: r.GetString(0),
                Name: r.GetString(1),
                Kind: r.GetString(2),
                PosterArtworkUri: string.IsNullOrEmpty(posterHash) ? null : LocalArtworkCache.UriScheme + posterHash,
                CreatedAt: r.GetInt64(4),
                UserCreated: r.GetInt32(5) != 0,
                ItemCount: r.GetInt32(6)));
        }
        return Task.FromResult<IReadOnlyList<LocalCollection>>(list);
    }

    public Task<string> CreateAsync(string name, string kind = "collection", CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_groups (id, name, kind, created_at, user_created)
            VALUES ($id, $n, $k, $now, 1);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
        return Task.FromResult(id);
    }

    public Task AddMemberAsync(string groupId, string filePath, int sortOrder = 0, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_group_members (group_id, local_file_path, sort_order)
            VALUES ($g, $p, $o)
            ON CONFLICT(group_id, local_file_path) DO UPDATE SET sort_order = $o;
            """;
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.Parameters.AddWithValue("$o", sortOrder);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RemoveMemberAsync(string groupId, string filePath, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_group_members WHERE group_id = $g AND local_file_path = $p;";
        cmd.Parameters.AddWithValue("$g", groupId);
        cmd.Parameters.AddWithValue("$p", filePath);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string groupId, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM local_groups WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", groupId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task RenameAsync(string groupId, string newName, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE local_groups SET name = $n WHERE id = $id;";
        cmd.Parameters.AddWithValue("$n", newName);
        cmd.Parameters.AddWithValue("$id", groupId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    /// <summary>Returns the file paths in a collection in sort_order.</summary>
    public Task<IReadOnlyList<string>> GetMemberPathsAsync(string groupId, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT local_file_path FROM local_group_members WHERE group_id = $g ORDER BY sort_order, local_file_path;";
        cmd.Parameters.AddWithValue("$g", groupId);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return Task.FromResult<IReadOnlyList<string>>(list);
    }

    /// <summary>Single-collection metadata fetch by id.</summary>
    public Task<LocalCollection?> GetAsync(string groupId, CancellationToken ct = default)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.id, g.name, g.kind, g.poster_hash, g.created_at, g.user_created,
                   (SELECT COUNT(*) FROM local_group_members m WHERE m.group_id = g.id)
            FROM local_groups g WHERE g.id = $id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", groupId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Task.FromResult<LocalCollection?>(null);
        var posterHash = r.IsDBNull(3) ? null : r.GetString(3);
        return Task.FromResult<LocalCollection?>(new LocalCollection(
            Id: r.GetString(0),
            Name: r.GetString(1),
            Kind: r.GetString(2),
            PosterArtworkUri: string.IsNullOrEmpty(posterHash) ? null : LocalArtworkCache.UriScheme + posterHash,
            CreatedAt: r.GetInt64(4),
            UserCreated: r.GetInt32(5) != 0,
            ItemCount: r.GetInt32(6)));
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }
}
