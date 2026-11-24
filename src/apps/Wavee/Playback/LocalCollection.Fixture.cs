// ── Playback/LocalCollection.Fixture.cs ────────────────────────────────────────────────────────────────────────────
// the "On this PC" page's inspector fixture: enough albums, years and formats to fill its shelves and its wall (#240 P2)
//
// Role: DIAGNOSTICS (UI thread, inspector only)
// Spec: 240-p2-contract.md P2-page row ("extend that fixture with enough fake albums/years/formats to fill the shelves and wall")
//
// `state kind=music-folders value=ready|scanning|offline` seeds P1b's small index (`LocalLibrary.InspectFixture`), then this adds a
// collection on top of it: 30 albums by 14 artists, 1972-2026, FLAC, MP3, AAC and Ogg, arriving today, this week, this month and earlier,
// in artist\album folders (some with disc subfolders and loose singles). "scanning" stops after the first third, as a scan that is
// still reading would. In memory only: no disk, no thread, no setting.

namespace Wavee;

public static class LocalCollectionFixture
{
    static bool s_seeded;

    /// <summary>The fixture's cover for an album it seeded (a file uri the inspector's fake decoder paints as a gradient), else null. A real
    /// library never reaches it: <see cref="Fill"/> runs only from the inspector's `music-folders` state, and `none` clears it.</summary>
    public static string? CoverOf(string artist, string album)
    {
        if (!Volatile.Read(ref s_seeded)) return null;
        foreach (var a in s_albums)
            if (a.Artist == artist && a.Title == album) return new Uri(Path.Combine(@"C:\Users\Listener\Music", a.Artist, a.Title, "cover.jpg")).AbsoluteUri;
        return null;
    }

    readonly record struct Album(string Artist, string Title, int Year, Spotify.Audio.Format Format, int Kbps, int Tracks, double DaysAgo, int Root);

    static readonly Album[] s_albums =
    [
        new("Coral Hours", "Lumen", 2026, Spotify.Audio.Format.Flac, 0, 10, 0.05, 1),
        new("Sable & Sun", "Copper Coast", 2011, Spotify.Audio.Format.Flac, 0, 9, 0.1, 1),
        new("Sable & Sun", "Low Tide Letters", 2020, Spotify.Audio.Format.Flac, 0, 11, 1.2, 1),
        new("Juniper Lane", "Soft Architecture", 1984, Spotify.Audio.Format.Mp3, 320, 12, 1.3, 1),
        new("Half Moon Radio", "Satellite Heart", 2016, Spotify.Audio.Format.Flac, 0, 10, 1.4, 1),
        new("Half Moon Radio", "Fieldnotes", 2015, Spotify.Audio.Format.Mp3, 256, 8, 1.5, 1),
        new("Juniper Lane", "Pool Lights", 1989, Spotify.Audio.Format.Mp3, 320, 10, 1.6, 1),
        new("Alex Rivers", "Blue Hour", 2022, Spotify.Audio.Format.Aac, 256, 11, 6, 1),
        new("Coral Hours", "Harbour Lights", 2024, Spotify.Audio.Format.Flac, 0, 11, 7, 1),
        new("The Lowlands", "Green Room Tapes", 1979, Spotify.Audio.Format.Flac, 0, 9, 8, 1),
        new("Coral Hours", "Wavee Sessions Vol. 2", 2025, Spotify.Audio.Format.Flac, 0, 12, 9, 1),
        new("Theo Ash", "Saltwater Hymns", 2003, Spotify.Audio.Format.Flac, 0, 10, 10, 1),
        new("Nova Kite", "Wavee Sessions Vol. 1", 2019, Spotify.Audio.Format.Mp3, 320, 12, 12, 1),
        new("Alex Rivers", "Glasshouse", 2018, Spotify.Audio.Format.Aac, 256, 10, 14, 1),
        new("Alex Rivers", "Quiet Machines", 2013, Spotify.Audio.Format.Mp3, 192, 9, 15, 1),
        new("Nova Kite", "Ferris Wheel Weather", 2021, Spotify.Audio.Format.Flac, 0, 10, 18, 1),
        new("Mia Solace", "Afterglow", 2017, Spotify.Audio.Format.OggVorbis320, 320, 8, 20, 1),
        new("The Lowlands", "Midnight Ferry", 1976, Spotify.Audio.Format.Flac, 0, 10, 40, 1),
        new("Odile Marchand", "Cinema Rain", 1972, Spotify.Audio.Format.Flac, 0, 9, 45, 1),
        new("The Vantage", "Polaroid Summer", 1998, Spotify.Audio.Format.Mp3, 320, 12, 60, 1),
        new("Kiyo Mori", "Velvet Transit", 2008, Spotify.Audio.Format.Aac, 256, 10, 70, 1),
        new("Mia Solace", "Paper Lanterns", 2010, Spotify.Audio.Format.Flac, 0, 11, 80, 1),
        new("Theo Ash", "Driftwood", 2006, Spotify.Audio.Format.Mp3, 320, 9, 90, 1),
        new("The Lowlands", "Static Bloom", 1993, Spotify.Audio.Format.Mp3, 256, 10, 100, 1),
        new("Juniper Lane", "Northern Static", 2019, Spotify.Audio.Format.Flac, 0, 10, 110, 2),
        new("The Vantage", "Signal Fires", 1995, Spotify.Audio.Format.Mp3, 192, 11, 120, 2),
        new("Odile Marchand", "Rue des Lilas", 1981, Spotify.Audio.Format.Flac, 0, 10, 130, 2),
        new("Kiyo Mori", "Neon Rivers", 2002, Spotify.Audio.Format.OggVorbis160, 160, 9, 140, 2),
        new("Rhea Moss", "Lantern Season", 1987, Spotify.Audio.Format.Flac, 0, 10, 150, 2),
        new("Otto Vale", "Glass Weather", 2012, Spotify.Audio.Format.Aac, 256, 9, 160, 2),
    ];

    /// <summary>The albums the board draws as "Hi-Res 24/96"; the fixture's other FLAC albums are CD quality (16/44.1).</summary>
    static readonly string[] s_hiRes = ["Lumen", "Copper Coast", "Low Tide Letters", "Satellite Heart", "Saltwater Hymns", "Northern Static"];

    static readonly string[] s_words =
    [
        "Tidewater", "Lighthouse Keeper", "Afterglow", "Sunset Boulevard", "Rewind", "Paper Planes", "Undertow", "Northern Lights", "Slow Bloom",
        "Ferry Song", "Static", "Halcyon", "Low Sun", "Small Hours", "Ember", "Weathervane", "Glasswork", "Long Way Home", "Motel Pool",
        "Satellites", "Driftwood", "Blue Cassette", "Overpass", "Morning Radio",
    ];

    /// <summary>Add the collection to the seeded fixture index (after <see cref="LocalLibrary.InspectFixture"/>).</summary>
    public static void Fill(string value)
    {
        // "listing" is a first scan before anything is indexed: nothing to add.
        Volatile.Write(ref s_seeded, value is not ("none" or "listing"));
        if (value is "none" or "listing") return;
        var index = LocalLibrary.Index;
        long now = DateTime.UtcNow.Ticks;
        long id = 1000;
        int count = value == "scanning" ? s_albums.Length / 3 : s_albums.Length;
        uint[] volumes = [0, 0x1234_ABCD, 0x0BAD_F00D];
        for (int a = 0; a < count; a++)
        {
            var al = s_albums[a];
            string folder = al.Artist + "\\" + al.Title + " (" + al.Year + ")";
            bool discs = al.Tracks >= 12;   // the long albums keep their discs in CD1 / CD2
            long firstSeen = now - (long)(al.DaysAgo * TimeSpan.TicksPerDay);
            // The files' own times are years older than when Wavee first saw them (copied in from a backup): "Recently added" must
            // order by first-seen, never by the file time.
            long written = new DateTime(Math.Min(al.Year + 1, 2025), 3, 1, 0, 0, 0, DateTimeKind.Utc).Ticks + a * TimeSpan.TicksPerHour;
            bool hiRes = Array.IndexOf(s_hiRes, al.Title) >= 0;
            int rate = hiRes ? 96_000 : 44_100;
            int bits = al.Format == Spotify.Audio.Format.Flac ? (hiRes ? 24 : 16) : 0;
            var batches = new Dictionary<string, LocalScanBatch>(StringComparer.Ordinal);
            for (int t = 0; t < al.Tracks; t++)
            {
                int disc = discs ? (t < al.Tracks / 2 ? 1 : 2) : 1;
                string sub = discs ? folder + "\\CD" + disc : folder;
                if (!batches.TryGetValue(sub, out var batch))
                {
                    batch = new LocalScanBatch(al.Root, sub) { Folder = new LocalFolder(al.Root, sub, (UInt128)(5000 + id), (ulong)id, discs ? [] : ["cover.jpg"], true) };
                    batches[sub] = batch;
                }
                string title = s_words[(a * 7 + t * 3) % s_words.Length];
                if (t % 5 == 4) title += " (Live)";
                string ext = al.Format switch
                {
                    Spotify.Audio.Format.Flac => ".flac", Spotify.Audio.Format.Mp3 => ".mp3", Spotify.Audio.Format.Aac => ".m4a", _ => ".ogg",
                };
                string name = (t + 1).ToString("00") + " " + title + ext;
                int ms = 150_000 + (a * 37 + t * 53) % 180 * 1000;
                var tags = new LocalTagSet(LocalKind.Audio, title, [al.Artist], al.Title, al.Artist, discs ? t % (al.Tracks / 2) + 1 : t + 1, disc, al.Year, ms, false,
                    al.Kbps > 0 ? al.Kbps : (hiRes ? 2_900 : 900) + t * 7, LocalCover.None, 0, 0, true, rate, bits, 2);
                long size = al.Kbps > 0 ? (long)al.Kbps * 125 * ms / 1000 : 30_000_000L + t * 1000;
                batch.Upserts.Add(new LocalEntry(id, al.Root, sub + "\\" + name, volumes[al.Root], (UInt128)(9000 + id), size, written, written, 0x20, al.Format, tags, title,
                    LocalState.Present, now, true, "", firstSeen));
                id++;
            }
            foreach (var b in batches.Values) index.Apply(b);
        }
        // Loose singles in one folder: a folder of files with mixed tags.
        if (value != "scanning")
        {
            var singles = new LocalScanBatch(1, "Singles") { Folder = new LocalFolder(1, "Singles", 4999, 0x5151, [], true) };
            string[] titles = ["Harbour Lights (radio edit)", "Sunset Boulevard (acoustic)", "Rewind (live at Paradiso)", "Afterglow (demo)", "Tidewater", "Lighthouse Keeper"];
            for (int i = 0; i < titles.Length; i++)
            {
                var f = i % 3 == 0 ? Spotify.Audio.Format.Flac : i % 3 == 1 ? Spotify.Audio.Format.Mp3 : Spotify.Audio.Format.Aac;
                var tags = new LocalTagSet(LocalKind.Audio, titles[i], ["Coral Hours"], titles[i] + " (single)", "Coral Hours", 1, 1, 2023 + i % 3, 180_000 + i * 21_000, false,
                    f == Spotify.Audio.Format.Flac ? 950 : 320, LocalCover.None, 0, 0, true, 44_100, f == Spotify.Audio.Format.Flac ? 16 : 0, 2);
                singles.Upserts.Add(new LocalEntry(id++, 1, "Singles\\" + titles[i] + (f == Spotify.Audio.Format.Flac ? ".flac" : f == Spotify.Audio.Format.Mp3 ? ".mp3" : ".m4a"),
                    0x1234_ABCD, (UInt128)(9000 + id), 8_000_000, now - TimeSpan.TicksPerDay * 200, now, 0x20, f, tags, titles[i], LocalState.Present, now, true, "",
                    now - TimeSpan.TicksPerDay * 200));
            }
            index.Apply(singles);
        }
        LocalLibrary.Epoch.Value++;
    }
}
