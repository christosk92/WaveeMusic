// ── Wavee.Tests/StoreRepairTests.cs — the one-time purge of rows the staging-arena aliasing corrupted ─────────────────
//
// The rules are pure (CorruptTextRules) and pinned here on the very values the 2026-09-25 verify profile held; the
// repair is pinned against a real sqlite file: corrupt rows go, clean rows are byte-for-byte untouched, the file is
// marked, and a marked file is never scanned again.

using Microsoft.Data.Sqlite;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public class StoreRepairTests : IDisposable
{
    // ── the rules (pure) ─────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://i.scdn.co/image/ab67616d0000b27321456c115d2f0f44f4630a63", true)]
    [InlineData("https://mosaic.scdn.co/640/ab67616d00001e02a1b2ab67616d00001e02c3d4", true)]
    [InlineData("https://image-cdn-fa.spotifycdn.com/image/ab67706c0000da84aa", true)]
    [InlineData("https://charts-images.scdn.co/assets/locale_en/regional/daily/region_global_default.jpg", true)]
    // the verify profile's corrupt covers: right length, wrong offset
    [InlineData("unnikhttps://i.scdn.co/image/ab67616d0000b2730d47dce466c76c73f3b", false)]
    [InlineData("tps://i.scdn.co/image/ab67616d0000b273ffffffffffffffffffffffffRi", false)]
    [InlineData("image/ab67616d0000b27321456c115d2f0f44f4630a6388risingRich Brian", false)]
    [InlineData("/charts-images.scdn.co/assets/locale_en/regional/daily/region_ar", false)]
    [InlineData("https://i.scdn.co/image/ab67616d0000b273f04f5a05400", false)]
    [InlineData("89California (feat. Warren Hue) [Acoustic Live Version]88risingR", false)]
    [InlineData("https://i.scdn.co/image/ab67616d0000485170342d75a6ea41c287c4a9a3htt", false)]
    public void A_cover_is_one_whole_url(string value, bool fits)
        => Assert.Equal(fits, CorruptTextRules.Fits(TextShape.ImageUrl, value));

    [Theory]
    [InlineData("spotify:album:0ncsn2LIyaN4kvISb50joI", true)]
    [InlineData("79aOne Spring Night OSTjulyanamendes", false)]
    [InlineData("https://mosaic.scdn.co/640/ab67616d0", false)]
    [InlineData("spotify:album:2DWidcIfxmV4ZOJCVwOYjMsp", false)]
    [InlineData("spotify:track:0ncsn2LIyaN4kvISb50joI", false)]
    public void An_album_uri_is_the_album_kind_and_22_base62(string value, bool fits)
        => Assert.Equal(fits, CorruptTextRules.Fits(TextShape.AlbumUri, value));

    [Theory]
    [InlineData("2008-05-02T00:00:00Z", true)]
    [InlineData("2024-01-01", true)]
    [InlineData("2024-01", true)]
    [InlineData("1999", true)]
    [InlineData("2020-03-04T05:06:07.123Z", true)]
    [InlineData("ecords© 2022 Re", false)]
    [InlineData("2024-01-0188rising", false)]
    public void A_release_date_is_iso(string value, bool fits)
        => Assert.Equal(fits, CorruptTextRules.Fits(TextShape.DateIso, value));

    [Theory]
    [InlineData("https://open.spotify.com/album/0mDeN57X1YtJHfXNdYlJbw", true)]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc", true)]
    [InlineData("AL℗ 2023 BABY GRAVY under exclusive license to IMPERIALhttps://i.scdn.co/imag", false)]
    [InlineData(".scdn.co/image/ab67616d0000b2731376b4b16f4bfcba02dc571bI met you when I was 18.", false)]
    [InlineData("https://open.spotify.com/album/0mDeN57X1YtJHfXNdYlJbwhttps://open", false)]
    public void A_share_url_is_one_open_spotify_url(string value, bool fits)
        => Assert.Equal(fits, CorruptTextRules.Fits(TextShape.ShareUrl, value));

    [Theory]
    [InlineData("De Stad Amsterdam", true)]
    [InlineData("AC/DC", true)]
    [InlineData("℗ 2018 SpinninRecords.com", true)]
    [InlineData("James Arthur (Deluxe)James ArthurSyco Music(P) 2013 Simco Limitedhttps://i.scdn.co/image/ab67", false)]
    [InlineData("ify:album:3ROg8rapFYVq5IJmOwDHlqspotify:album:4EnR7CwhjFNQll6dOWduyl", false)]
    [InlineData("0e18df1732097746a44506ee2024-09-20T00:00:00ZNYUhttps://open.spot", false)]
    public void Free_text_is_judged_only_for_a_shaped_fragment_inside_it(string value, bool fits)
        => Assert.Equal(fits, CorruptTextRules.Fits(TextShape.Text, value));

    [Fact]
    public void Null_and_empty_always_fit_and_user_written_text_is_never_judged()
    {
        foreach (TextShape shape in Enum.GetValues<TextShape>()) Assert.True(CorruptTextRules.Fits(shape, ""));
        Assert.Equal(TextShape.None, CorruptTextRules.ShapeOf("playlist", "title"));
        Assert.Equal(TextShape.None, CorruptTextRules.ShapeOf("playlist", "description"));
        Assert.Equal(TextShape.None, CorruptTextRules.ShapeOf("episode", "description"));
        Assert.Equal(TextShape.ImageUrl, CorruptTextRules.ShapeOf("track", "image"));
        Assert.Equal(TextShape.AlbumUri, CorruptTextRules.ShapeOf("track", "album_uri"));
        Assert.Equal(TextShape.ShareUrl, CorruptTextRules.ShapeOf("album", "share_url"));
        Assert.Equal(TextShape.DateIso, CorruptTextRules.ShapeOf("album", "release_date_iso"));
        Assert.Equal(TextShape.Text, CorruptTextRules.ShapeOf("album", "copyright"));
    }

    // ── the migration, on a real file ────────────────────────────────────────────────────────────────────────────────

    readonly string _db = Path.Combine(Path.GetTempPath(), "wavee-repair-" + Guid.NewGuid().ToString("n") + ".db");

    public StoreRepairTests()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Post = static a => a();
        Store.Register(new TrackShape());
        Store.Register(new AlbumShape());
    }

    public void Dispose()
    {
        Fetch.Reset();
        Store.Shutdown();
        Store.Use(null);
        Store.Post = static a => a();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_db + suffix); } catch (IOException) { }
    }

    void Open()
    {
        Store.Use(_db);
        Entities.Boot(CatalogScope.Fake());
        Store.Flush();
        Store.Shutdown();
    }

    SqliteConnection Direct()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _db, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    void Sql(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Direct();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    List<string> Rows(string table)
    {
        using var c = Direct();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM " + table + " ORDER BY uri;";
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
        {
            var cells = new string[r.FieldCount];
            for (int i = 0; i < cells.Length; i++) cells[i] = r.IsDBNull(i) ? "∅" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            rows.Add(string.Join("|", cells));
        }
        return rows;
    }

    void Track(string uri, string title, string? image, string? album)
        => Sql("INSERT INTO track(scope_id,uri,title,image,album_uri,duration_ms,known,fetched_at,touched) VALUES(7,$u,$t,$i,$a,1000,3,5,6);",
               ("$u", uri), ("$t", title), ("$i", image), ("$a", album));

    void Album(string uri, string title, string? date, string? share, string? copyright)
        => Sql("INSERT INTO album(scope_id,uri,title,release_date_iso,share_url,copyright,known,fetched_at,touched) VALUES(7,$u,$t,$d,$s,$c,3,5,6);",
               ("$u", uri), ("$t", title), ("$d", date), ("$s", share), ("$c", copyright));

    [Fact]
    public void The_repair_deletes_every_corrupt_row_once_and_leaves_clean_rows_untouched()
    {
        Open();                                                       // a file this build created — and marked
        Sql("DELETE FROM meta WHERE key=$k;", ("$k", Store.StagingArenaRepairKey));   // … as an older build's would be

        const string Good = "https://i.scdn.co/image/ab67616d0000b27321456c115d2f0f44f4630a63";
        Track("spotify:track:0000000000000000000001", "Nightrider", Good, "spotify:album:0ncsn2LIyaN4kvISb50joI");
        Track("spotify:track:0000000000000000000002", "No cover yet", null, null);
        Track("spotify:track:0000000000000000000003", "De Stad Amsterdam",
              "unnikhttps://i.scdn.co/image/ab67616d0000b2730d47dce466c76c73f3b", "spotify:album:4JSTsGRrrd5yBnNtzPsnlO");
        Track("spotify:track:0000000000000000000004", "Kiss", Good, "79aOne Spring Night OSTjulyanamendes");
        Track("spotify:track:0000000000000000000005", "s LLC(P) 2021 https://i.scdn.co", Good, "spotify:album:0ncsn2LIyaN4kvISb50joI");
        Album("spotify:album:0000000000000000000001", "Nightrider", "2024-01-01", "https://open.spotify.com/album/0000000000000000000001", "℗ 2024 88rising");
        Album("spotify:album:0000000000000000000002", "Old", "2008-05-02T00:00:00Z", null, null);
        Album("spotify:album:0000000000000000000003", "Bad date", "ecords© 2022 Re", null, null);
        Album("spotify:album:0000000000000000000004", "Bad share", "2024-01-01",
              "AL℗ 2023 BABY GRAVY under exclusive license to IMPERIALhttps://i.scdn.co/imag", null);
        Album("spotify:album:0000000000000000000005", "Bad copyright", "2024-01-01", null,
              "James Arthur (Deluxe)James ArthurSyco Music(P) 2013 Simco Limitedhttps://i.scdn.co/image/ab67");
        List<string> tracksBefore = Rows("track"), albumsBefore = Rows("album");

        Open();                                                       // the repair runs at open

        List<string> tracks = Rows("track"), albums = Rows("album");
        Assert.Equal(new[] { tracksBefore[0], tracksBefore[1] }, tracks);          // clean rows: byte-for-byte
        Assert.Equal(new[] { albumsBefore[0], albumsBefore[1] }, albums);
        using (var c = Direct())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM meta WHERE key='" + Store.StagingArenaRepairKey + "';";
            Assert.Equal("deleted=6", cmd.ExecuteScalar() as string);
        }

        // ONCE: a marked file is never scanned again (a row written after the repair is the lease's job, not this one's)
        Track("spotify:track:0000000000000000000006", "Later", "unnik-not-a-url", null);
        Open();
        Assert.Equal(3, Rows("track").Count);
    }
}
