// ── Entities/Store.Repair.cs — CORE + SHELL: the one-time purge of rows the staging-arena aliasing corrupted ─────────
//
// 2026-09-25: two owners of one `Staging` (a second `Return` put the instance in the pool twice) decoded into ONE text
// arena from two api threads, and the write-behind persisted what the commit read: covers like
// "unnikhttps://i.scdn.co/image/ab67616d…" (every one exactly the right LENGTH at the wrong OFFSET), album uris like
// "79aOne Spring Night OSTjulyanamendes", share urls holding a copyright, release dates holding a label. The lease in
// `Staging` stops new ones; this file removes the ones already on disk, ONCE per file.
//
//   1. CORE — `CorruptTextRules`: what each persisted text column must LOOK like, as a pure function over (table,
//      column, value). Only columns with a real shape are judged — a cover url, a catalog uri, a share url, an iso date,
//      an isrc — plus the plain-text columns the corruption hit (titles, names, copyright, label), which are judged only
//      for carrying a fragment of one of those shapes (a url, a uri, an image host). A user-written playlist title or
//      description is never judged: a url in it is legitimate.
//   2. SHELL — `Store.RepairCorruptText`: at open, a file with no `repair.staging-arena` mark is scanned column by column
//      through the rules, every row with a column that fails is DELETED (the row goes; the provider answers it again on
//      the next ensure — a cache miss, never a runtime filter), and the mark is written in the same transaction. A file
//      created by this build is marked on its first open with nothing to scan.
//
// Why a delete and not a schema bump: the fingerprint names the file, so a bump would throw away the whole cache — the
// library, the journal of unsent intents, 40k clean rows — to reach ~1% bad ones.

using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Wavee;

/// <summary>The shape a persisted text column must have (<see cref="CorruptTextRules"/>).</summary>
public enum TextShape : byte
{
    /// <summary>Not judged.</summary>
    None,
    /// <summary>Free text the corruption reached (a title, a name, a copyright): judged only for carrying a url, a
    /// catalog uri or an image host inside it.</summary>
    Text,
    /// <summary>An image url: <c>https://i.scdn.co/image/</c> + 40 hex, or another https url with no second scheme in it.</summary>
    ImageUrl,
    /// <summary><c>spotify:album:</c> + 22 base62.</summary>
    AlbumUri,
    /// <summary><c>spotify:track:</c> + 22 base62.</summary>
    TrackUri,
    /// <summary>A track or an episode uri (a music video's counterpart).</summary>
    TrackOrEpisodeUri,
    /// <summary><c>spotify:show:</c> + 22 base62.</summary>
    ShowUri,
    /// <summary><c>spotify:user:</c> + a non-empty name without whitespace or a second uri.</summary>
    UserUri,
    /// <summary><c>https://open.spotify.com/…</c> with no whitespace and no second scheme.</summary>
    ShareUrl,
    /// <summary><c>yyyy</c>, <c>yyyy-mm</c>, <c>yyyy-mm-dd</c>, or <c>yyyy-mm-ddThh:mm:ss[.f]Z</c>.</summary>
    DateIso,
    /// <summary>An isrc: 12-15 of <c>[A-Za-z0-9-]</c>.</summary>
    Isrc,
}

/// <summary>PURE. What a persisted text value must look like, per (table, column) — the rules the one-time repair
/// (<see cref="Store"/>'s <c>RepairCorruptText</c>) deletes rows by. Null and empty always fit: "the answer said nothing".</summary>
public static class CorruptTextRules
{
    /// <summary>The persisted columns this repair judges, and how. Everything else is <see cref="TextShape.None"/>.</summary>
    public static TextShape ShapeOf(string table, string column) => (table, column) switch
    {
        ("track", "title") => TextShape.Text,
        ("track", "image") or ("track", "video_image") => TextShape.ImageUrl,
        ("track", "album_uri") => TextShape.AlbumUri,
        ("track", "canonical_uri") => TextShape.TrackUri,
        ("track", "video_uri") => TextShape.TrackOrEpisodeUri,
        ("track", "isrc") => TextShape.Isrc,
        ("album", "title") or ("album", "label") or ("album", "copyright") or ("album", "courtesy") => TextShape.Text,
        ("album", "image") => TextShape.ImageUrl,
        ("album", "release_date_iso") => TextShape.DateIso,
        ("album", "share_url") => TextShape.ShareUrl,
        ("artist", "name") => TextShape.Text,
        ("artist", "image") or ("artist", "header") => TextShape.ImageUrl,
        ("playlist", "image") => TextShape.ImageUrl,
        ("playlist", "share_url") => TextShape.ShareUrl,
        ("playlist", "owner_uri") => TextShape.UserUri,
        ("show", "title") or ("show", "publisher") => TextShape.Text,
        ("show", "image") => TextShape.ImageUrl,
        ("episode", "title") => TextShape.Text,
        ("episode", "image") => TextShape.ImageUrl,
        ("episode", "show_uri") => TextShape.ShowUri,
        ("user", "image") => TextShape.ImageUrl,
        ("concert", "image") => TextShape.ImageUrl,
        _ => TextShape.None,
    };

    const string ScdnImage = "https://i.scdn.co/image/";
    const string ShareHost = "https://open.spotify.com/";

    /// <summary>Does <paramref name="value"/> have the shape <paramref name="shape"/> promises? Null/empty fit.</summary>
    public static bool Fits(TextShape shape, ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return true;
        return shape switch
        {
            TextShape.None => true,
            TextShape.Text => !CarriesFragment(value),
            TextShape.ImageUrl => IsImageUrl(value),
            TextShape.AlbumUri => IsCatalogUri(value, "spotify:album:"),
            TextShape.TrackUri => IsCatalogUri(value, "spotify:track:"),
            TextShape.TrackOrEpisodeUri => IsCatalogUri(value, "spotify:track:") || IsCatalogUri(value, "spotify:episode:"),
            TextShape.ShowUri => IsCatalogUri(value, "spotify:show:"),
            TextShape.UserUri => IsUserUri(value),
            TextShape.ShareUrl => value.StartsWith(ShareHost, StringComparison.Ordinal) && IsOneUrl(value),
            TextShape.DateIso => IsDateIso(value),
            TextShape.Isrc => IsIsrc(value),
            _ => true,
        };
    }

    /// <summary>A cover url. The i.scdn.co form is exact (40 lowercase hex — every one of the 2026-09-25 corrupt covers
    /// was the right length at the wrong offset, so a prefix test alone would have kept "ps://i.scdn.co/image/ab6…");
    /// any other https host (mosaic, image-cdn, pickasso, charts) must at least be ONE url.</summary>
    public static bool IsImageUrl(ReadOnlySpan<char> v)
    {
        if (v.StartsWith(ScdnImage, StringComparison.Ordinal))
        {
            ReadOnlySpan<char> id = v[ScdnImage.Length..];
            if (id.Length != 40) return false;
            foreach (char c in id) if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)) return false;
            return true;
        }
        return v.StartsWith("https://", StringComparison.Ordinal) && IsOneUrl(v);
    }

    /// <summary><paramref name="prefix"/> + exactly 22 base62.</summary>
    public static bool IsCatalogUri(ReadOnlySpan<char> v, string prefix)
    {
        if (v.Length != prefix.Length + 22 || !v.StartsWith(prefix, StringComparison.Ordinal)) return false;
        foreach (char c in v[prefix.Length..]) if (!char.IsAsciiLetterOrDigit(c)) return false;
        return true;
    }

    static bool IsUserUri(ReadOnlySpan<char> v)
    {
        const string Prefix = "spotify:user:";
        if (v.Length <= Prefix.Length || !v.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        ReadOnlySpan<char> name = v[Prefix.Length..];
        foreach (char c in name) if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        return !CarriesFragment(name);
    }

    /// <summary>A url with nothing glued to it: no whitespace, no control character, and no second scheme after its own.</summary>
    static bool IsOneUrl(ReadOnlySpan<char> v)
    {
        foreach (char c in v) if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        ReadOnlySpan<char> rest = v[8..];
        return rest.IndexOf("http", StringComparison.Ordinal) < 0 && rest.IndexOf("spotify:", StringComparison.Ordinal) < 0;
    }

    static bool IsDateIso(ReadOnlySpan<char> v)
    {
        // yyyy[-mm[-dd[Thh:mm:ss[.f+]Z]]]
        if (v.Length < 4 || !AllDigits(v[..4])) return false;
        if (v.Length == 4) return true;
        if (v.Length < 7 || v[4] != '-' || !AllDigits(v.Slice(5, 2))) return false;
        if (v.Length == 7) return true;
        if (v.Length < 10 || v[7] != '-' || !AllDigits(v.Slice(8, 2))) return false;
        if (v.Length == 10) return true;
        if (v.Length < 20 || v[10] != 'T' || v[^1] != 'Z') return false;
        ReadOnlySpan<char> t = v[11..^1];                    // hh:mm:ss[.f+]
        if (!AllDigits(t[..2]) || t[2] != ':' || !AllDigits(t.Slice(3, 2)) || t[5] != ':' || !AllDigits(t.Slice(6, 2))) return false;
        if (t.Length == 8) return true;
        return t[8] == '.' && t.Length > 9 && AllDigits(t[9..]);
    }

    static bool IsIsrc(ReadOnlySpan<char> v)
    {
        if (v.Length is < 12 or > 15) return false;
        foreach (char c in v) if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        return true;
    }

    /// <summary>Free text carrying a piece of a shaped value — the signature of another row's bytes read at an offset:
    /// a url scheme, a catalog uri, the image or share host.</summary>
    static bool CarriesFragment(ReadOnlySpan<char> v)
        => v.IndexOf("https://", StringComparison.Ordinal) >= 0
        || v.IndexOf("http://", StringComparison.Ordinal) >= 0
        || v.IndexOf("scdn.co", StringComparison.Ordinal) >= 0
        || v.IndexOf("open.spotify.com", StringComparison.Ordinal) >= 0
        || v.IndexOf("spotify:album:", StringComparison.Ordinal) >= 0
        || v.IndexOf("spotify:track:", StringComparison.Ordinal) >= 0
        || v.IndexOf("spotify:artist:", StringComparison.Ordinal) >= 0
        || v.IndexOf("spotify:playlist:", StringComparison.Ordinal) >= 0
        || v.IndexOf("spotify:show:", StringComparison.Ordinal) >= 0
        || v.IndexOf("spotify:episode:", StringComparison.Ordinal) >= 0;

    static bool AllDigits(ReadOnlySpan<char> v)
    {
        foreach (char c in v) if (!char.IsAsciiDigit(c)) return false;
        return true;
    }
}

/// <summary>What one repair pass did: rows deleted per table, and how long it took.</summary>
public readonly record struct RepairResult(bool Ran, int Scanned, int Deleted, string PerTable, long Ms);

public static partial class Store
{
    /// <summary>The meta key that says this file has been through the staging-arena repair.</summary>
    public const string StagingArenaRepairKey = "repair.staging-arena";

    /// <summary>SHELL, ONE-TIME. Delete every row of a registered kind whose judged text columns do not fit
    /// <see cref="CorruptTextRules"/>, then mark the file (<see cref="StagingArenaRepairKey"/>) in the same transaction;
    /// a marked file is left alone. Runs at open on the write connection, before the store thread exists. Always-on
    /// <c>store.repair</c> line with the counts. Public so a fact can run it against a fixture file.</summary>
    public static RepairResult RepairCorruptText(SqliteConnection c)
    {
        long started = Stopwatch.GetTimestamp();
        using (var probe = c.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM meta WHERE key=$k;";
            probe.Parameters.AddWithValue("$k", StagingArenaRepairKey);
            if (probe.ExecuteScalar() is long n && n > 0) return new RepairResult(false, 0, 0, "", 0);
        }

        int scanned = 0, deleted = 0;
        var perTable = new System.Text.StringBuilder();
        using SqliteTransaction tx = c.BeginTransaction();
        for (int k = 0; k < s_shapes.Length; k++)
        {
            if (s_shapes[k] is not { } sql) continue;
            KindShape shape = sql.Shape;
            var judged = new List<(string Name, TextShape Shape)>();
            foreach (StoreColumn col in shape.Columns)
            {
                TextShape rule = CorruptTextRules.ShapeOf(shape.Table, col.Name);
                if (rule != TextShape.None && col.Type == StoreType.Text) judged.Add((col.Name, rule));
            }
            if (judged.Count == 0) continue;

            var bad = new List<(long Scope, string Uri)>();
            using (var select = c.CreateCommand())
            {
                select.Transaction = tx;
                var sb = new System.Text.StringBuilder("SELECT scope_id,uri");
                foreach (var (name, _) in judged) sb.Append(',').Append(name);
                sb.Append(" FROM ").Append(shape.Table).Append(';');
                select.CommandText = sb.ToString();
                using SqliteDataReader r = select.ExecuteReader();
                while (r.Read())
                {
                    scanned++;
                    for (int i = 0; i < judged.Count; i++)
                    {
                        if (r.IsDBNull(2 + i)) continue;
                        if (CorruptTextRules.Fits(judged[i].Shape, r.GetString(2 + i).AsSpan())) continue;
                        bad.Add((r.GetInt64(0), r.GetString(1)));
                        break;
                    }
                }
            }
            if (bad.Count == 0) continue;
            using (var delete = c.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM " + shape.Table + " WHERE scope_id=$s AND uri=$u;";
                SqliteParameter ps = delete.Parameters.Add("$s", SqliteType.Integer);
                SqliteParameter pu = delete.Parameters.Add("$u", SqliteType.Text);
                foreach (var (scope, uri) in bad)
                {
                    ps.Value = scope;
                    pu.Value = uri;
                    deleted += delete.ExecuteNonQuery();
                }
            }
            if (perTable.Length > 0) perTable.Append(',');
            perTable.Append(shape.Table).Append(':').Append(bad.Count);
        }
        using (var mark = c.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES($k,$v);";
            mark.Parameters.AddWithValue("$k", StagingArenaRepairKey);
            mark.Parameters.AddWithValue("$v", "deleted=" + deleted.ToString(System.Globalization.CultureInfo.InvariantCulture));
            mark.ExecuteNonQuery();
        }
        tx.Commit();

        long ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        string tables = perTable.Length == 0 ? "none" : perTable.ToString();
        Log.Event(deleted > 0 ? WaveeLogLevel.Warning : WaveeLogLevel.Info, "store", "store.repair",
            "one-time purge of rows the staging-arena aliasing corrupted (they are fetched again)", null, -1, null,
            WaveeLogField.Of("scanned", scanned), WaveeLogField.Of("deleted", deleted),
            WaveeLogField.Of("tables", tables), WaveeLogField.Of("ms", ms));
        return new RepairResult(true, scanned, deleted, tables, ms);
    }
}
