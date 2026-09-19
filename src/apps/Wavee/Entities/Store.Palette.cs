// ── Entities/Store.Palette.cs — SHELL (owner C; plan §WS-C) ────────────────────────────────────────────────────────
//
// The palette's disk half. One more table in the same file, but not one of the per-kind `KindShape` tables Ddl()
// generates in its loop: a cover's colours have no scope (Palette.cs's file header — "process-wide, not per Scope"),
// so this table has no `scope_id` and never binds `$s`. Everything else about it is the same store: one write
// connection, one background thread, upserts in a transaction, a cold read posted back through `Store.Post`.
//
// THE FLUSH IS ARMED, NOT POLLED. `Palette.SetDark/SetGraded/SetNegative` call `MarkPersist`, which calls the partial
// hook `Palette.PalettePersistArm()` — implemented here as `ArmPaletteFlush()` — the moment a row changes. The frame
// tick (`Shell.Host.cs`, beside `Palette.Tick()`) calls `FlushPalette()` every frame; it is two field reads when
// nothing is dirty, same shape as `Palette.Tick()` itself.

using Microsoft.Data.Sqlite;

namespace Wavee;

/// <summary><see cref="Palette.PalettePersistArm"/>'s implementation: a dirty row arms the store's flush. Erased
/// with no store file, exactly like <see cref="Palette.PalettePump"/> in <c>Palette.Host.cs</c>.</summary>
public static partial class Palette
{
    static partial void PalettePersistArm() => Store.ArmPaletteFlush();
}

public static partial class Store
{
    /// <summary>Rows written since the last flush, or a batch bigger than one flush drained. Volatile: it is only
    /// ever set on the UI thread, but read the same way <c>s_open</c> is.</summary>
    static volatile bool s_paletteArmed;

    /// <summary>Rows per flush — the same shape as every other batch API here (P4): one number for "how much is one
    /// unit of work", so a burst of gradings from a cold scroll cannot turn into one giant transaction.</summary>
    const int PaletteFlushCap = 256;

    /// <summary><c>Palette.PalettePersistArm()</c>'s implementation. Idempotent: the dirty rows themselves live in
    /// <see cref="Palette"/>'s own list, so arming twice before a flush costs nothing more than a bool already true.</summary>
    public static void ArmPaletteFlush() => s_paletteArmed = true;

    /// <summary>The frame tick's other half. Drains up to <see cref="PaletteFlushCap"/> dirty rows, keeps only the
    /// ones worth a disk write, and hands them to the store thread as ONE upsert transaction. Re-arms itself when the
    /// drain left more rows dirty than this batch took.</summary>
    public static void FlushPalette()
    {
        if (!s_paletteArmed || !s_open) return;
        s_paletteArmed = false;

        var buf = new Palette.PaletteRowSnapshot[PaletteFlushCap];
        int n = Palette.DrainDirty(buf);
        if (n == 0) return;
        if (Palette.DirtyCount > 0) ArmPaletteFlush();   // more than one batch's worth: come back next tick

        var rows = new List<Palette.PaletteRowSnapshot>(n);
        for (int i = 0; i < n; i++)
            if (PalettePersistence.IsWorthPersisting(buf[i].Known)) rows.Add(buf[i]);
        if (rows.Count == 0) return;

        Enqueue(() => WritePaletteCore(rows));
    }

    /// <summary>STORE THREAD. One transaction, one upsert per dirty row — the whole write side of the palette cache.
    /// <c>known</c> is masked to <see cref="PalettePersistence.PersistedBits"/> before it is bound, so a stray
    /// <see cref="PaletteBits.Queued"/> (there should never be one here — <see cref="FlushPalette"/> already filtered
    /// to answered rows) can never reach disk.</summary>
    static void WritePaletteCore(List<Palette.PaletteRowSnapshot> rows)
    {
        Db? db = s_db;
        if (db is null) return;
        Span<byte> darkBuf = stackalloc byte[PalettePersistence.SchemeBytes];
        Span<byte> lightBuf = stackalloc byte[PalettePersistence.SchemeBytes];
        int written = 0;
        try
        {
            lock (db.WriteLock)
            {
                using SqliteTransaction tx = db.Write.BeginTransaction();
                using var cmd = db.Write.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO palette(key,known,ts,dark,light) VALUES($k,$n,$t,$d,$l) " +
                    "ON CONFLICT(key) DO UPDATE SET known=excluded.known, ts=excluded.ts, dark=excluded.dark, light=excluded.light;";
                var pKey = new SqliteParameter("$k", "");
                var pKnown = new SqliteParameter("$n", 0L);
                var pTs = new SqliteParameter("$t", 0L);
                var pDark = new SqliteParameter("$d", DBNull.Value);
                var pLight = new SqliteParameter("$l", DBNull.Value);
                cmd.Parameters.Add(pKey);
                cmd.Parameters.Add(pKnown);
                cmd.Parameters.Add(pTs);
                cmd.Parameters.Add(pDark);
                cmd.Parameters.Add(pLight);

                for (int i = 0; i < rows.Count; i++)
                {
                    Palette.PaletteRowSnapshot row = rows[i];
                    if (row.Key.Length == 0) continue;

                    pKey.Value = row.Key;
                    pKnown.Value = (long)PalettePersistence.RestoreMask(row.Known);
                    pTs.Value = row.TsUnix;
                    if (row.Dark.IsEmpty) pDark.Value = DBNull.Value;
                    else { Scheme dark = row.Dark; PalettePersistence.Encode(in dark, darkBuf); pDark.Value = darkBuf.ToArray(); }
                    if (row.Light.IsEmpty) pLight.Value = DBNull.Value;
                    else { Scheme light = row.Light; PalettePersistence.Encode(in light, lightBuf); pLight.Value = lightBuf.ToArray(); }
                    cmd.ExecuteNonQuery();
                    written++;
                }
                tx.Commit();
            }
            s_writes++;
            s_writeRows += written;
        }
        catch (Exception ex) { s_faults++; Fault("palette.write", ex); }
    }

    /// <summary>STORE THREAD, enqueued FIRST in <see cref="Boot"/> — before <see cref="Warm"/>, because the palette
    /// has no scope to wait for. Reads every row still within the longest possible TTL, narrows each one with
    /// <see cref="PalettePersistence.FreshOnLoad"/>, and posts the survivors to <see cref="Palette.Restore"/>.</summary>
    static void WarmPaletteCore()
    {
        Db? db = s_db;
        if (db is null) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long cutoff = PalettePersistence.LoadCutoffUnix(now);
        var loaded = new List<Palette.PaletteRowSnapshot>();
        try
        {
            using var cmd = db.Read.CreateCommand();
            cmd.CommandText = "SELECT key,known,ts,dark,light FROM palette WHERE ts >= $c;";
            cmd.Parameters.Add(new SqliteParameter("$c", cutoff));
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                uint known = (uint)r.GetInt64(1);
                long ts = r.GetInt64(2);
                if (!PalettePersistence.FreshOnLoad(known, ts, now)) continue;
                Scheme dark = r.IsDBNull(3) ? default : PalettePersistence.Decode(r.GetFieldValue<byte[]>(3));
                Scheme light = r.IsDBNull(4) ? default : PalettePersistence.Decode(r.GetFieldValue<byte[]>(4));
                loaded.Add(new Palette.PaletteRowSnapshot(key, known, ts, dark, light));
            }
        }
        catch (Exception ex) { s_faults++; Fault("palette.warm", ex); return; }

        if (loaded.Count == 0) return;
        Post(() => Palette.Restore(loaded));
    }
}
