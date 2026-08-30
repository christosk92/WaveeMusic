using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Channels = System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Wavee.Core;

namespace Wavee.Backend.Persistence;

// The durable (SQLite) activity store — its OWN connection to the existing library.db (NOT SqliteColdStore's): WAL +
// busy_timeout so the two connections coexist, and a single-writer write-behind queue so appends/updates never block the
// UI thread. Reads (LoadRecent) are synchronous under the connection lock (called once at startup). Owns only the
// activity_log table + its index — it never touches SqliteColdStore's schema-version key.
public sealed class SqliteActivityStore : IActivityStore, IDisposable
{
    readonly SqliteConnection _conn;
    readonly object _connLock = new();
    readonly Channels.Channel<Action> _queue = Channels.Channel.CreateUnbounded<Action>(new Channels.UnboundedChannelOptions { SingleReader = true });
    readonly Task _writer;

    public SqliteActivityStore(string path)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;");
        Exec("CREATE TABLE IF NOT EXISTS activity_log(" +
             "id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, target_uri TEXT NOT NULL, target_name TEXT, " +
             "payload TEXT, created_at INTEGER NOT NULL, status INTEGER NOT NULL DEFAULT 0, read INTEGER NOT NULL DEFAULT 0);");
        Exec("CREATE INDEX IF NOT EXISTS ix_activity_created ON activity_log(created_at DESC);");
        _writer = Task.Run(WriteLoopAsync);
    }

    // ── writes (enqueued; executed by the single writer task off the UI thread) ─────────────────────────────────────────
    public void Append(ActivityEntry entry) => Enqueue(() => InsertLocked(entry));
    public void SetStatus(long id, ActivityStatus status) => Enqueue(() => ExecLocked(
        "UPDATE activity_log SET status=$s WHERE id=$id;", ("$s", (int)status), ("$id", id)));
    public void MarkAllRead() => Enqueue(() => ExecLocked("UPDATE activity_log SET read=1 WHERE read=0;"));
    public void Clear() => Enqueue(() => ExecLocked("DELETE FROM activity_log;"));

    public void Prune(int maxCount, long maxAgeMs) => Enqueue(() =>
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - maxAgeMs;
        ExecLocked("DELETE FROM activity_log WHERE created_at < $c;", ("$c", cutoff));
        ExecLocked("DELETE FROM activity_log WHERE id NOT IN " +
                   "(SELECT id FROM activity_log ORDER BY created_at DESC, id DESC LIMIT $m);", ("$m", maxCount));
    });

    // ── read (synchronous — startup load; tests call Flush first) ───────────────────────────────────────────────────────
    public IReadOnlyList<ActivityEntry> LoadRecent(int limit)
    {
        var list = new List<ActivityEntry>(Math.Min(limit, 256));
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT id, kind, target_uri, target_name, payload, created_at, status, read " +
                            "FROM activity_log ORDER BY created_at DESC, id DESC LIMIT $limit;";
            c.Parameters.AddWithValue("$limit", limit);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                ActivityPayload? payload = null;
                if (!r.IsDBNull(4) && r.GetString(4) is { Length: > 0 } json)
                {
                    try { payload = JsonSerializer.Deserialize(json, ActivityJsonCtx.Default.ActivityPayload); }
                    catch { }
                }
                list.Add(new ActivityEntry(
                    r.GetInt64(0), (ActivityKind)r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                    payload, r.GetInt64(5), (ActivityStatus)r.GetInt32(6), r.GetInt32(7) != 0));
            }
        }
        return list;
    }

    /// <summary>Block until every enqueued write has committed. Used by tests + <see cref="Dispose"/>.</summary>
    public void Flush()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_queue.Writer.TryWrite(() => done.TrySetResult())) done.Task.Wait();
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { }
        lock (_connLock) _conn.Dispose();
    }

    // ── internals ───────────────────────────────────────────────────────────────────────────────────────────────────────
    async Task WriteLoopAsync()
    {
        await foreach (var op in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { op(); } catch { /* a single failed write must not stop the loop */ }
        }
    }

    void Enqueue(Action op) => _queue.Writer.TryWrite(op);

    void InsertLocked(ActivityEntry e)
    {
        string? payload = e.Payload is null ? null : JsonSerializer.Serialize(e.Payload, ActivityJsonCtx.Default.ActivityPayload);
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT OR REPLACE INTO activity_log(id, kind, target_uri, target_name, payload, created_at, status, read) " +
                            "VALUES($id,$k,$u,$n,$p,$t,$s,$r);";
            c.Parameters.AddWithValue("$id", e.Id);
            c.Parameters.AddWithValue("$k", (int)e.Kind);
            c.Parameters.AddWithValue("$u", e.TargetUri);
            c.Parameters.AddWithValue("$n", (object?)e.TargetName ?? DBNull.Value);
            c.Parameters.AddWithValue("$p", (object?)payload ?? DBNull.Value);
            c.Parameters.AddWithValue("$t", e.TimestampMs);
            c.Parameters.AddWithValue("$s", (int)e.Status);
            c.Parameters.AddWithValue("$r", e.Read ? 1 : 0);
            c.ExecuteNonQuery();
        }
    }

    void Exec(string sql)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = sql;
            c.ExecuteNonQuery();
        }
    }

    void ExecLocked(string sql, params (string, object)[] ps)
    {
        lock (_connLock)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = sql;
            foreach (var (name, val) in ps) c.Parameters.AddWithValue(name, val);
            c.ExecuteNonQuery();
        }
    }
}
