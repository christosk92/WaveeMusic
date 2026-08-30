using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using System.Runtime.Versioning;
using Wavee;
using Wavee.Backend.Audio;
using Wavee.Backend.Persistence;

namespace Wavee.SpotifyLive.Audio;

/// <summary>DPAPI-wrapped PlayPlay license payloads keyed by fileId — survives app restarts.</summary>
public sealed class LicenseKeyDiskCache : IDisposable
{
    readonly SqliteConnection _conn;
    readonly ICredentialProtector _protector;
    readonly object _gate = new();
    readonly int _maxEntries;
    readonly TimeSpan _maxAge;

    public LicenseKeyDiskCache(string dbPath, ICredentialProtector protector, int maxEntries = 4096, TimeSpan? maxAge = null)
    {
        _protector = protector;
        _maxEntries = maxEntries;
        _maxAge = maxAge ?? TimeSpan.FromDays(30);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        _conn.Open();
        using var c = _conn.CreateCommand();
        c.CommandText = "CREATE TABLE IF NOT EXISTS audio_license(fileId TEXT PRIMARY KEY, blob BLOB NOT NULL, saved_at INTEGER NOT NULL);";
        c.ExecuteNonQuery();
    }

    public static string DefaultDbPath() => Path.Combine(CacheRoot(), "audiokeys.db");

    public static string CacheRoot()
    {
        try { return FluentGpu.WindowsApi.Storage.AppDataStore.ForUnpackaged("Wavee", "Wavee").CacheFolder; }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "Cache");
        }
    }

    public static LicenseKeyDiskCache? FromSettings(IAppSettings settings)
    {
        if (!settings.Get(WaveeSettings.AudioKeyCacheEnabled)) return null;
        return new LicenseKeyDiskCache(DefaultDbPath(), CreateProtector());
    }

    static ICredentialProtector CreateProtector()
    {
#if WAVEE_TESTS
        return new NoOpProtector();
#else
        if (OperatingSystem.IsWindows())
        {
            try { return new DpapiProtector(); }
            catch { }
        }
        return new NoOpProtector();
#endif
    }

    public bool TryLoad(string fileIdHex, out PlayPlayLicenseResult result)
    {
        result = default;
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT blob, saved_at FROM audio_license WHERE fileId=$id;";
            c.Parameters.AddWithValue("$id", fileIdHex);
            using var r = c.ExecuteReader();
            if (!r.Read()) return false;
            var blob = (byte[])r.GetValue(0);
            long savedAt = r.GetInt64(1);
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - savedAt > (long)_maxAge.TotalSeconds)
            {
                Invalidate(fileIdHex);
                return false;
            }
            if (!TryDecode(blob, out result)) return false;
            Touch(fileIdHex);
            return result.Reason == AudioKeyFailureReason.None && !result.Key.IsEmpty;
        }
    }

    public void Save(string fileIdHex, in PlayPlayLicenseResult lic)
    {
        if (lic.Reason != AudioKeyFailureReason.None || lic.Key.IsEmpty) return;
        var plain = Encode(lic);
        var blob = _protector.Protect(plain);
        var wrapped = System.Text.Encoding.UTF8.GetBytes(_protector.Scheme + ":" + Convert.ToBase64String(blob));
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "INSERT OR REPLACE INTO audio_license(fileId, blob, saved_at) VALUES($id, $b, $t);";
            c.Parameters.AddWithValue("$id", fileIdHex);
            c.Parameters.AddWithValue("$b", wrapped);
            c.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            c.ExecuteNonQuery();
            TrimIfNeeded();
        }
    }

    public void Invalidate(string fileIdHex)
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM audio_license WHERE fileId=$id;";
            c.Parameters.AddWithValue("$id", fileIdHex);
            c.ExecuteNonQuery();
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "DELETE FROM audio_license;";
            c.ExecuteNonQuery();
        }
    }

    public (int Count, long Bytes) Stats()
    {
        lock (_gate)
        {
            using var c = _conn.CreateCommand();
            c.CommandText = "SELECT COUNT(*), COALESCE(SUM(LENGTH(blob)),0) FROM audio_license;";
            using var r = c.ExecuteReader();
            r.Read();
            return (r.GetInt32(0), r.GetInt64(1));
        }
    }

    void Touch(string fileIdHex)
    {
        using var c = _conn.CreateCommand();
        c.CommandText = "UPDATE audio_license SET saved_at=$t WHERE fileId=$id;";
        c.Parameters.AddWithValue("$id", fileIdHex);
        c.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        c.ExecuteNonQuery();
    }

    void TrimIfNeeded()
    {
        using var count = _conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM audio_license;";
        var n = (long)count.ExecuteScalar()!;
        if (n <= _maxEntries) return;
        long drop = n - (long)(_maxEntries * 0.9);
        using var del = _conn.CreateCommand();
        del.CommandText = "DELETE FROM audio_license WHERE fileId IN (SELECT fileId FROM audio_license ORDER BY saved_at ASC LIMIT $n);";
        del.Parameters.AddWithValue("$n", drop);
        del.ExecuteNonQuery();
    }

    bool TryDecode(byte[] wrapped, out PlayPlayLicenseResult result)
    {
        result = default;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(wrapped);
            int idx = text.IndexOf(':');
            if (idx < 0 || text[..idx] != _protector.Scheme) return false;
            var plain = _protector.Unprotect(Convert.FromBase64String(text[(idx + 1)..]));
            return TryParsePlain(plain, out result);
        }
        catch { return false; }
    }

    static byte[] Encode(in PlayPlayLicenseResult lic)
    {
        var key = lic.Key.Span;
        var aux = lic.Auxiliary.Span;
        var raw = lic.RawBody.Span;
        var req = lic.RequestBody.Span;
        var buf = new byte[16 + key.Length + aux.Length + raw.Length + req.Length];
        int o = 0;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), key.Length); o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), aux.Length); o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), raw.Length); o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), req.Length); o += 4;
        key.CopyTo(buf.AsSpan(o)); o += key.Length;
        aux.CopyTo(buf.AsSpan(o)); o += aux.Length;
        raw.CopyTo(buf.AsSpan(o)); o += raw.Length;
        req.CopyTo(buf.AsSpan(o));
        return buf;
    }

    static bool TryParsePlain(ReadOnlySpan<byte> plain, out PlayPlayLicenseResult result)
    {
        result = default;
        if (plain.Length < 16) return false;
        int kl = BinaryPrimitives.ReadInt32LittleEndian(plain);
        int al = BinaryPrimitives.ReadInt32LittleEndian(plain[4..]);
        int rl = BinaryPrimitives.ReadInt32LittleEndian(plain[8..]);
        int ql = BinaryPrimitives.ReadInt32LittleEndian(plain[12..]);
        int need = 16 + kl + al + rl + ql;
        if (kl < 0 || al < 0 || rl < 0 || ql < 0 || plain.Length < need) return false;
        int o = 16;
        var key = plain.Slice(o, kl); o += kl;
        var aux = plain.Slice(o, al); o += al;
        var raw = plain.Slice(o, rl); o += rl;
        var req = plain.Slice(o, ql);
        result = new PlayPlayLicenseResult(key.ToArray(), aux.ToArray(), AudioKeyFailureReason.None, "",
            raw.ToArray(), req.ToArray());
        return true;
    }

    public void Dispose() => _conn.Dispose();
}
