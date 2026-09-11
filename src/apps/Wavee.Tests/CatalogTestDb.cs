using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Wavee.Tests;

sealed class CatalogTestDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wavee-catalog-test-" + Guid.NewGuid().ToString("N") + ".db");
    public void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var arg in args) command.Parameters.AddWithValue(arg.Name, arg.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    public object? Value(string sql)
    { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    public long Number(string sql) => Convert.ToInt64(Value(sql) ?? 0, System.Globalization.CultureInfo.InvariantCulture);
    public byte[] ReadBlobs(string sql)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader(); using var bytes = new MemoryStream();
        while (reader.Read()) bytes.Write(reader.GetFieldValue<byte[]>(0));
        return bytes.ToArray();
    }
    SqliteConnection Open()
    { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); connection.Open(); return connection; }
    public void Dispose()
    { foreach (var suffix in new[] { "", "-wal", "-shm" }) try { File.Delete(Path + suffix); } catch (IOException) { } }
}
