// ── Entities/Entities.Fake.LocalLink.cs — the fake catalogue's answer for "Your files and Spotify" (a named partial of Entities.Fake.cs) ──────────
//
// The owner's own library is one untagged file, "Nickelback - Savin' Me.mp3" (4:12). Its name splits to "Savin' Me" by Nickelback, and the matcher
// asks the catalogue's track search for that title (LocalLink.Match.cs). Under --fake the search answers from these rows, so the whole flow ("Find on
// Spotify…", the candidates, Pick, the linked row, the Spotify album's "Versions and formats") can be shown without an account:
//   nb0  "All the Right Reasons" (2005), five songs, "Savin' Me" at 3:39 the fourth: the album version, 33 s shorter than the owner's file;
//   nb1  "The Best of Nickelback Volume 1" (2013), three songs, a "Savin' Me" at 4:12: the length the owner's file has.
// Every row is seeded whole (identity, year, audio, tags, ISRC), as the album seed's own rows are, so nothing is fetched for them.

namespace Wavee;

public static partial class Entities
{
    /// <summary>The fake Nickelback artist and albums (file header).</summary>
    public const string FakeLinkArtistUri = "spotify:artist:nb0", FakeLinkAlbumUri = "spotify:album:nb0", FakeLinkBestOfUri = "spotify:album:nb1";

    static readonly (string Title, int Ms)[][] s_linkAlbums =
    [
        [("Follow You Home", 260_000), ("Fight for All the Wrong Reasons", 224_000), ("Photograph", 259_000), ("Savin' Me", 219_000), ("Animals", 186_000)],
        [("How You Remind Me", 223_000), ("Savin' Me", 252_000), ("Rockstar", 255_000)],
    ];

    static readonly string[] s_linkAlbumTitles = ["All the Right Reasons", "The Best of Nickelback Volume 1"];
    static readonly ushort[] s_linkAlbumYears = [2005, 2013];

    static string LinkAlbumUri(int a) => a == 0 ? FakeLinkAlbumUri : FakeLinkBestOfUri;

    static string LinkTrackUri(int a, int k) => "spotify:track:nb" + a.ToString(System.Globalization.CultureInfo.InvariantCulture) + "n"
                                                + k.ToString(System.Globalization.CultureInfo.InvariantCulture);

    static partial void SeedLocalLinkSurfaces(long now0)
    {
        var s = Staging.Rent();
        try
        {
            ref var artist = ref s.Artists.RowFor(s.AddText(Utf8(FakeLinkArtistUri)), Authority.Seed, (uint)ArtistFields.Identity);
            artist.Name = s.AddText(Utf8("Nickelback"));
            artist.Image = s.AddText(Utf8(Cover(9)));
            for (int a = 0; a < s_linkAlbums.Length; a++)
            {
                ref var album = ref s.Albums.RowFor(s.AddText(Utf8(LinkAlbumUri(a))), Authority.Seed, (uint)AlbumFields.Identity);
                album.Title = s.AddText(Utf8(s_linkAlbumTitles[a]));
                album.Image = s.AddText(Utf8(Cover(11 + a)));
                album.Year = s_linkAlbumYears[a];
                album.TrackCount = s_linkAlbums[a].Length;
                album.Kind = (byte)(a == 0 ? AlbumKind.Album : AlbumKind.Compilation);
                for (int k = 0; k < s_linkAlbums[a].Length; k++)
                {
                    var (title, ms) = s_linkAlbums[a][k];
                    ref var track = ref s.Tracks.RowFor(s.AddText(Utf8(LinkTrackUri(a, k))), Authority.Seed,
                        (uint)(TrackFields.Row | TrackFields.Year | TrackFields.Audio | TrackFields.Tags | TrackFields.Video | TrackFields.Isrc));
                    track.Title = s.AddText(Utf8(title));
                    track.ArtistLine = s.AddText(Utf8("Nickelback"));
                    track.Image = s.AddText(Utf8(Cover(11 + a)));
                    track.AlbumUri = s.AddText(Utf8(LinkAlbumUri(a)));
                    track.DurationMs = ms;
                    track.PlayCount = (uint)(90_000_000 - a * 40_000_000 - k * 6_000_000);
                    track.Year = s_linkAlbumYears[a];
                    track.Isrc = s.AddText(Utf8("USRR" + (5 + a * 8).ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + "0" + (4100 + k).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
            Commit(s);
        }
        finally
        {
            Staging.Return(s);
        }

        var e = Current.Edges;
        Span<int> one = stackalloc int[1];
        Span<int> targets = stackalloc int[8];
        Span<AlbumTrackEdge> numbers = stackalloc AlbumTrackEdge[8];
        int artistSlot = ResolveSeedSlot(EntityKind.Artist, FakeLinkArtistUri);
        one[0] = artistSlot;
        for (int a = 0; a < s_linkAlbums.Length; a++)
        {
            int albumSlot = ResolveSeedSlot(EntityKind.Album, LinkAlbumUri(a));
            if (albumSlot == Table.None) continue;
            e.AlbumArtists.ReplaceRun(albumSlot, one, default);
            int count = s_linkAlbums[a].Length;
            for (int k = 0; k < count; k++)
            {
                targets[k] = ResolveSeedSlot(EntityKind.Track, LinkTrackUri(a, k));
                numbers[k] = new AlbumTrackEdge(1, (ushort)(k + 1));
                e.TrackArtists.ReplaceRun(targets[k], one, default);
            }
            e.AlbumTracks.ReplaceRun(albumSlot, targets[..count], numbers[..count]);
            global::Wavee.Album.DeriveTopTrack(albumSlot);
        }
    }
}
