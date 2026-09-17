// ── Entities/Entities.Fake.Album.cs ────────────────────────────────────────────────────────────────────────────────
// SEED-SURFACES for the album, prerelease, show, episode and track-drawer pages (`SeedAlbumSurfaces`)
//
// Role: CORE
// Owner: M (Wave 5, stream C of WP-5.M)
// Wave: 5
// Budget: 700 lines (ch 31 §9.4 gives the whole seed 1,100; this is the album/show share of SEED-SURFACES)
// Spec: ch 31 §0, §2 W3/W4/W12/W14/W16/W17, §3.1, §7.1 rows 3-5/15-18/31-32, §7.2, §7.3 · ch 05 §7, W11-W14, W19 ·
//   ch 09 §7, W7-W9 · the WP-5.M contract §4 · gap register G-260 (nothing a page demands stays unseeded), G-261 (album
//   titles of their own), G-262 (billed artists + the top track)
//
// THE SAME RULES AS `Entities.Fake.cs` (ch 31 §0.1-§0.3, §7.2): rows go through `Staging` + `Commit` at
// `Authority.Seed`, relations through the edge tables' whole-run writes, every value is a pure function of a fixture
// index and `now0`, no `Random`, no clock, no `string.GetHashCode`, and nothing publishes (`SeedFake` publishes once).
// This partial runs AFTER the base seed's commit, so its rows are a second answer at the same rung — `Table.Accepts`
// lets an equal authority through, which is what re-states the base albums' identity under their own titles.
//
// WHAT IT ADDS (and why each exists):
//   · every album al0-al12 (+ al13, al14): Release, Publishing, Availability and the PreReleaseLink group — the whole
//     `AlbumFields.All` — so the album page's demand never reaches the provider offline (G-260); billed artists; the
//     member tracks' `Album` column pointing at the album they are a member of (ch 31 §0.8's agreement rule — two base
//     ranges overlapped, al4/al5 and al10/al11, and a track can name only one album, so al5 and al11 move to free runs);
//     descending play counts so row 1 is the star (ch 31 §3.1's curve); `DeriveTopTrack` after the memberships (G-262).
//   · every seeded track: `Row | Audio | Tags | Video | Isrc` known (G-260), its credit run, one descriptor, and EMPTY
//     complete versions / waveform / credits runs unless it is one of the four drawer fixtures.
//   · al2 the rich album (ch 31 W3): 2 billed + 3 track-only artists (the face pile's +N), 2 other versions, 6 more-by
//     ("Show all 6"), 7 featured-on playlists, 6 similar albums, 6 merch rows (one with no price, one with no shop url).
//   · al3 the short release with a video: track 0 `HasVideo` + its video counterpart row; related artists for its rows.
//   · al4 the compilation: a different artist on every row, two discs.
//   · al13 the UPCOMING prerelease (`spotify:prerelease:pr13`), al14 the WATERFALL ("3 of 12", one dateless pending row).
//   · every show: its blurb, `8 + s % 5` episodes newest first, one in progress at a third; sh3's two degraded cards;
//     sh7 PARTIAL (the load-more pill); sh8 a new, unsaved, EMPTY show.
//
// THE CLOCK TRAP (reported as a gap): `Platform.Clock.FixedSeedEpoch` (1,788,000,000 = 2026-08-29) is already in the PAST
// against the wall clock `Shell.Host` publishes into `Entities.Now`, so ch 31 W16's `now0 + 9 d 4 h` countdown would be
// expired at launch. Every future instant here is `now0 + 730 d + 9 d 4 h` (+ a week per waterfall row): future for
// two years past the fixed epoch, and still `now0 ± a constant`.

using System.Globalization;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Entities
{
    static partial void SeedAlbumSurfaces(long now0) => AlbumSeed.Run(now0);

    /// <summary>The album/show seed, namespaced so its helpers cannot collide with the other owners' seed partials.
    /// Inside it `Album`, `Track`, `Show` … are Entities' FACTORY METHODS, so the handle types are spelled
    /// <c>global::Wavee.*</c>.</summary>
    static class AlbumSeed
    {
        // ── fixture constants (the WP-5.M contract §4; ch 31 §3.1) ──────────────────────────────────────────────────

        /// <summary>al0-al12 are the base seed's; al13 is the prerelease, al14 the waterfall.</summary>
        const int Albums = 15, PreReleaseAlbum = 13, WaterfallAlbum = 14, RichAlbum = 2, ShortAlbum = 3, CompilationAlbum = 4;
        const int Shows = 9, PartialShow = 7, EmptyShow = 8, DegradedShow = 3;
        const long Day = 86_400;
        /// <summary>The countdown lead: 730 d + ch 31 W16's 9 d 4 h (the clock trap in the header).</summary>
        const long Lead = 739 * Day + 4 * 3600;
        const string PreReleaseUri = "spotify:prerelease:pr13";
        const int Waveform = Spotify.Decode.WaveformColumns;

        /// <summary>G-261: album titles of their own, disjoint from the playlist, folder, track and show vocabularies.</summary>
        static readonly string[] s_albumTitles =
        [
            "Paper Lanterns", "Glasshouse", "Northern Static", "Two Summers", "Wavee Sessions Vol. 1", "Low Tide Letters",
            "Satellite Heart", "Evening Radio", "The Long Way Round", "Salt & Honey", "Wavee Sessions Vol. 2",
            "Harbour Lights", "Blue Hour", "Coming Up Roses", "Slow Reveal",
        ];

        static readonly string[] s_labels = ["Wavee Records", "Half Moon Recordings", "Coral Sound", "Dry Season Music"];
        static readonly string[] s_descriptors = ["Pop", "Dance", "Indie", "Hip Hop", "Rock", "Electronic"];   // FakeData.cs:63
        static readonly string[] s_episodeTitles =                                                                 // FakeData.cs:380
        [
            "The Build Trap", "Latency, Honestly", "On Craft", "Tape Loops", "The Quiet Release", "Edge Cases",
            "First Principles", "After Hours", "The Long Game", "Cold Start", "Postmortem", "Signal Lost",
        ];

        /// <summary>The track drawer's four fixtures, as (album, row).</summary>
        static bool IsDrawerRow(int album, int row) => (album == RichAlbum && row <= 2) || (album == ShortAlbum && row == 0);

        // ── identities ───────────────────────────────────────────────────────────────────────────────────────────────

        static string AlbumUri(int a) => "spotify:album:al" + a.ToString(CultureInfo.InvariantCulture);
        static string ArtistUri(int i) => "spotify:artist:ar" + Wrap(i, ArtistCount).ToString(CultureInfo.InvariantCulture);
        static string ShowUri(int s) => "spotify:show:sh" + s.ToString(CultureInfo.InvariantCulture);
        static string EpisodeUri(int s, int i) => ShowUri(s).Replace(":show:", ":episode:", StringComparison.Ordinal)
                                                  + "e" + i.ToString(CultureInfo.InvariantCulture);

        static int TracksIn(int a) => a < AlbumCount ? AlbumShape(a).Tracks : a == PreReleaseAlbum ? 10 : 12;
        static AlbumKind KindOf(int a) => a < AlbumCount ? AlbumShape(a).Kind : AlbumKind.Album;

        /// <summary>Where album <paramref name="a"/>'s run starts in the 166-track pool: the base seed's <c>a·13</c>,
        /// except the two albums whose runs overlapped a neighbour's (al5 in al4's, al11 in al10's) — they move to free
        /// runs, so every pool track is a member of at most ONE album and its <c>Album</c> column can say which.</summary>
        static int PoolStart(int a) => a switch { 5 => 79, 11 => 119, _ => Wrap(a * 13, TrackCount) };

        /// <summary>The uri of album <paramref name="a"/>'s row <paramref name="k"/>: a pool track for the base albums,
        /// an own row for the prerelease and the waterfall (their rows are not out, and the pool is the Liked set).</summary>
        static string MemberUri(int a, int k) => a < AlbumCount
            ? TrackUri(Wrap(PoolStart(a) + k, TrackCount))
            : "spotify:track:al" + a.ToString(CultureInfo.InvariantCulture) + "t" + k.ToString(CultureInfo.InvariantCulture);

        static string VideoUri(int pool) => TrackUri(pool) + "v";
        static string AltUri(int pool) => TrackUri(pool) + "a";

        static int SlotOf(Table table, string uri) => table.Slot(uri.AsSpan());

        // ── the dated and derived values (pure over the index and now0) ─────────────────────────────────────────────

        /// <summary>ch 31 §3.1's album curve: descending from ~60 M, so row 1 is the star on every album.</summary>
        static uint Plays(int a, int k) => (uint)(60_000_000 / (k + 1) + ((a * 10 * 131 % 37) + 1) * 250_000);

        static ushort YearOf(int a, long now0) => a < AlbumCount ? (ushort)(2008 + a % 17) : (ushort)ReleaseOf(a, now0).Year;

        static DateTime ReleaseOf(int a, long now0)
            => a == PreReleaseAlbum ? DateTimeOffset.FromUnixTimeSeconds(now0 + Lead).UtcDateTime
             : a == WaterfallAlbum ? DateTimeOffset.FromUnixTimeSeconds(now0 - 20 * Day).UtcDateTime
             : new DateTime(2008 + a % 17, 1 + a * 5 % 12, 1 + a * 7 % 28);

        /// <summary>0 year · 1 month · 2 day: al0 states only a year, al1 a month, the rest a day.</summary>
        static byte PrecisionOf(int a) => a switch { 0 => 0, 1 => 1, _ => 2 };

        /// <summary>A waterfall row's release instant: rows 1-3 are out (0), 4-11 land a week apart, row 12 is pending
        /// with NO instant (ch 05 parity 56). The prerelease's rows all land with the album.</summary>
        static int AvailableAt(int a, int k, long now0)
            => a == PreReleaseAlbum ? (int)(now0 + Lead)
             : a == WaterfallAlbum && k is >= 3 and <= 10 ? (int)(now0 + Lead + (k - 3) * 7 * Day)
             : 0;

        static bool Pending(int a, int k) => a == PreReleaseAlbum || (a == WaterfallAlbum && k >= 3);

        /// <summary>The billed artists (AlbumArtists), as artist fixture indices.</summary>
        static int Billed(int a, Span<int> into)
        {
            switch (a)
            {
                case RichAlbum: into[0] = 2; into[1] = 5; return 2;
                case CompilationAlbum: into[0] = 3; return 1;
                case PreReleaseAlbum: into[0] = 1; return 1;
                case WaterfallAlbum: into[0] = 4; return 1;
                default: into[0] = Wrap(a, ArtistCount); return 1;
            }
        }

        /// <summary>A member row's credited artists: the compilation credits the pool track's own artist; al2 credits its
        /// two billed artists on alternating rows plus three track-only contributors (rows 2, 4, 6 — the face pile's +3);
        /// every other album credits its lead.</summary>
        static int Credited(int a, int k, Span<int> into)
        {
            if (a == CompilationAlbum) { into[0] = Wrap(PoolStart(a) + k, ArtistCount); return 1; }
            int n = Billed(a, into);
            if (a != RichAlbum) return 1;
            n = k % 2 == 0 ? 2 : 1;                                   // ar5 on the even rows
            if (k is 1 or 3 or 5) into[n++] = 7 + (k - 1) / 2;       // ar7, ar8, ar9: track-only contributors
            return n;
        }

        static string CreditLine(ReadOnlySpan<int> artists)
        {
            string line = "";
            for (int i = 0; i < artists.Length; i++) line += (i == 0 ? "" : ", ") + s_artistNames[Wrap(artists[i], ArtistCount)];
            return line;
        }

        // ── the seed ─────────────────────────────────────────────────────────────────────────────────────────────────

        public static void Run(long now0)
        {
            var s = Staging.Rent();
            try
            {
                StageAlbums(s, now0);
                StageTracks(s, now0);
                StageShows(s, now0);
                Commit(s);
            }
            finally
            {
                Staging.Return(s);
            }

            LandAlbums();
            LandTracks();
            LandShows();

            // G-262: the star is derived on the model AFTER the memberships, never scanned per render.
            for (int a = 0; a < Albums; a++) global::Wavee.Album.DeriveTopTrack(SlotOf(Current.Albums, AlbumUri(a)));
        }

        static TextRef Text(Staging s, string value) => value.Length == 0 ? default : s.AddText(Utf8(value));

        static void StageAlbums(Staging s, long now0)
        {
            for (int a = 0; a < Albums; a++)
            {
                ref var row = ref s.Albums.RowFor(Text(s, AlbumUri(a)), Authority.Seed,
                    (uint)(AlbumFields.Identity | AlbumFields.Release | AlbumFields.Publishing | AlbumFields.Availability
                         | AlbumFields.PreReleaseLink));
                ushort year = YearOf(a, now0);
                string label = s_labels[Wrap(a, s_labels.Length)];
                var release = ReleaseOf(a, now0);
                byte precision = PrecisionOf(a);

                // identity: the base seed's cover, year, kind and count, under the album's OWN title (G-261)
                row.Title = Text(s, s_albumTitles[a]);
                row.Image = Text(s, Cover(a));
                row.Year = year;
                row.TrackCount = TracksIn(a);
                row.Kind = (byte)KindOf(a);

                // release: the ISO string at the provider's precision; a year alone parses to no instant (ch 05 §8)
                row.ReleaseDateIso = Text(s, release.ToString(precision switch { 0 => "yyyy", 1 => "yyyy-MM", _ => "yyyy-MM-dd" },
                                                              CultureInfo.InvariantCulture));
                row.DatePrecision = precision;
                row.ReleaseAt = precision == 0 ? 0
                              : a == PreReleaseAlbum ? (int)(now0 + Lead)
                              : Spotify.Decode.Seconds(release.Year, release.Month, precision == 1 ? 1 : release.Day);

                // publishing: the whole record (the panel appears once, complete)
                string y = year.ToString(CultureInfo.InvariantCulture);
                row.Label = Text(s, label);
                row.Copyright = Text(s, "© " + y + " " + label);
                row.Courtesy = Text(s, "℗ " + y + " " + label);
                row.ShareUrl = Text(s, "https://open.spotify.com/album/al" + a.ToString(CultureInfo.InvariantCulture));
                row.DiscCount = (byte)(a == CompilationAlbum ? 2 : 1);

                // availability + the kind-138 link: the prerelease's window and pairing; answered EMPTY everywhere else
                row.Flags = a == PreReleaseAlbum ? (uint)AlbumFlags.PreRelease : 0;
                row.PreReleaseEnd = a == PreReleaseAlbum ? (int)(now0 + Lead) : 0;
                row.PreReleaseUri = a == PreReleaseAlbum ? Text(s, PreReleaseUri) : default;
            }
        }

        static void StageTracks(Staging s, long now0)
        {
            Span<int> artists = stackalloc int[4];

            // the pool: every row gains its descriptor, video verdict and isrc; album members re-state their identity
            // (the album link, the credit line, the album's play curve)
            Span<int> albumOf = stackalloc int[TrackCount];
            Span<int> rowOf = stackalloc int[TrackCount];
            albumOf.Fill(-1);
            for (int a = 0; a < AlbumCount; a++)
                for (int k = 0; k < TracksIn(a); k++)
                {
                    int pool = Wrap(PoolStart(a) + k, TrackCount);
                    albumOf[pool] = a;
                    rowOf[pool] = k;
                }

            for (int i = 0; i < TrackCount; i++)
            {
                int a = albumOf[i], k = rowOf[i];
                ref var row = ref s.Tracks.RowFor(Text(s, TrackUri(i)), Authority.Seed,
                    (uint)(TrackFields.Tags | TrackFields.Video | TrackFields.Isrc));
                row.Isrc = Text(s, Isrc(i));
                if (a < 0) continue;

                int n = Credited(a, k, artists);
                row.Known |= (uint)(TrackFields.Identity | TrackFields.PlayCount);
                row.Title = Text(s, s_titles[Wrap(i, s_titles.Length)]);
                row.ArtistLine = Text(s, CreditLine(artists[..n]));
                row.Image = Text(s, Cover(i));
                row.AlbumUri = Text(s, AlbumUri(a));
                row.DurationMs = 138_000 + (i * 37 % 150) * 1000;
                row.PlayCount = Plays(a, k);
                row.Flags = i % 6 == 0 ? (uint)TrackFlags.Explicit : 0;

                if (!IsDrawerRow(a, k)) continue;
                row.Known |= (uint)TrackFields.Files;
                if (a == RichAlbum && k == 0) row.Flags |= (uint)TrackFlags.Lossless;
                if (k == 0)
                {
                    // the two drawer rows with a music video: the flag, the counterpart and its 16:9 still
                    row.Flags |= (uint)TrackFlags.HasVideo;
                    row.VideoUri = Text(s, VideoUri(i));
                    row.VideoImage = Text(s, Cover(i + 8));
                }
            }

            // the counterparts the drawer fixtures point at: al2 row 1's video + acoustic, al2 row 2's acoustic, al3 row 1's video
            StageCounterpart(s, now0, PoolStart(RichAlbum), RichAlbum, 0, video: true);
            StageCounterpart(s, now0, PoolStart(RichAlbum), RichAlbum, 0, video: false);
            StageCounterpart(s, now0, PoolStart(RichAlbum) + 1, RichAlbum, 1, video: false);
            StageCounterpart(s, now0, PoolStart(ShortAlbum), ShortAlbum, 0, video: true);

            // the prerelease's and the waterfall's own rows
            for (int a = PreReleaseAlbum; a <= WaterfallAlbum; a++)
                for (int k = 0; k < TracksIn(a); k++)
                {
                    int seed = 1000 + a * 20 + k;
                    int n = Credited(a, k, artists);
                    bool pending = Pending(a, k);
                    ref var row = ref s.Tracks.RowFor(Text(s, MemberUri(a, k)), Authority.Seed,
                        (uint)(TrackFields.Row | TrackFields.Year | TrackFields.Audio | TrackFields.Tags | TrackFields.Video
                             | TrackFields.Isrc));
                    row.Title = Text(s, s_titles[Wrap(seed, s_titles.Length)]);
                    row.ArtistLine = Text(s, CreditLine(artists[..n]));
                    row.Image = Text(s, Cover(a));
                    row.AlbumUri = Text(s, AlbumUri(a));
                    row.DurationMs = 138_000 + (seed * 37 % 150) * 1000;
                    row.PlayCount = pending ? 0 : Plays(a, k);             // a row that is not out has no plays: the dash
                    row.Flags = pending ? (uint)TrackFlags.Unavailable : 0;
                    row.AvailableAt = AvailableAt(a, k, now0);
                    row.Year = YearOf(a, now0);
                    Audio(ref row, seed);
                    row.Isrc = Text(s, Isrc(seed));
                }
        }

        static void StageCounterpart(Staging s, long now0, int pool, int a, int k, bool video)
        {
            ref var row = ref s.Tracks.RowFor(Text(s, video ? VideoUri(pool) : AltUri(pool)), Authority.Seed,
                (uint)(TrackFields.Row | TrackFields.Year | TrackFields.Audio | TrackFields.Tags | TrackFields.Video
                     | TrackFields.Isrc));
            Span<int> artists = stackalloc int[4];
            int n = Credited(a, k, artists);
            row.Title = Text(s, s_titles[Wrap(pool, s_titles.Length)] + (video ? " (Official Video)" : " (Acoustic)"));
            row.ArtistLine = Text(s, CreditLine(artists[..n]));
            row.Image = Text(s, Cover(pool + 8));
            row.AlbumUri = Text(s, AlbumUri(a));
            row.DurationMs = 138_000 + (pool * 37 % 150) * 1000 + (video ? 12_000 : -9_000);
            row.PlayCount = Plays(a, k) / 7;
            row.Year = YearOf(a, now0);
            Audio(ref row, pool + (video ? 500 : 700));
            row.Isrc = Text(s, Isrc(pool + (video ? 500 : 700)));
        }

        static void Audio(ref StagedTrack row, int seed)
        {
            row.Tempo = (ushort)((70 + seed % 90) * 10);
            row.Camelot = (byte)(1 + Wrap(seed, 12));
            row.CamelotColor = s_camelotColors[Wrap(seed, 12)];
        }

        /// <summary>A plausible, deterministic 12-character ISRC (CC XXX YY NNNNN).</summary>
        static string Isrc(int seed)
            => "QZWV1" + (8 + seed % 17).ToString("D2", CultureInfo.InvariantCulture)
               + Wrap(seed, 100_000).ToString("D5", CultureInfo.InvariantCulture);

        static void StageShows(Staging s, long now0)
        {
            for (int sh = 0; sh < Shows; sh++)
            {
                int n = EpisodesIn(sh);
                string name = sh < ShowCount ? s_showNames[sh] : "The Empty Room";
                ref var show = ref s.Shows.RowFor(Text(s, ShowUri(sh)), Authority.Seed,
                    (uint)(sh < ShowCount ? ShowFields.About : ShowFields.All));
                // FakeData.cs:405's blurb; the base seed's identity stands for sh0-sh7, sh8 is new
                show.Description = Text(s, name + " — conversations, deep dives and field recordings from Wavee Podcasts. New episodes weekly.");
                show.EpisodesAsked = n;                                 // the cursor: everything resident was asked
                if (sh == EmptyShow)
                {
                    show.Title = Text(s, name);
                    show.Image = Text(s, Cover(10));
                    show.Publisher = Text(s, "Wavee Podcasts");
                }

                int total = TotalEpisodes(sh);
                for (int i = 0; i < n; i++)
                {
                    int duration = (22 + (sh * 7 + i * 13) % 50) * 60_000;      // FakeData.cs:413
                    ref var ep = ref s.Episodes.RowFor(Text(s, EpisodeUri(sh, i)), Authority.Seed, (uint)EpisodeFields.All);
                    ep.Title = Text(s, "#" + (total - i).ToString(CultureInfo.InvariantCulture) + " · "
                                     + s_episodeTitles[(sh * 5 + i) % s_episodeTitles.Length]);
                    // ch 09 parity 62: sh3 carries one card with no description and one with no art — known-and-empty
                    ep.Image = sh == DegradedShow && i == 5 ? default : Text(s, Cover(sh + 2));
                    ep.Description = sh == DegradedShow && i == 4 ? default
                        : Text(s, "In this episode of " + name + ": notes, tangents and a few hard-won lessons.");
                    ep.DurationMs = duration;
                    ep.PublishedAt = (int)(now0 - (i * 7 * Day + sh * Day));
                    ep.ProgressMs = i == 1 ? duration / 3 : 0;          // exactly one "continue listening" card
                    ep.ShowUri = Text(s, ShowUri(sh));
                }
            }
        }

        /// <summary>Resident episodes: <c>8 + s % 5</c> (FakeData.cs:408); none on the empty show.</summary>
        static int EpisodesIn(int sh) => sh == EmptyShow ? 0 : 8 + sh % 5;

        /// <summary>The membership's stated total: sh7 has twelve more than it holds (the load-more pill).</summary>
        static int TotalEpisodes(int sh) => EpisodesIn(sh) + (sh == PartialShow ? 12 : 0);

        // ── the relations ────────────────────────────────────────────────────────────────────────────────────────────

        static void LandAlbums()
        {
            var e = Current.Edges;
            Span<int> targets = stackalloc int[32];
            Span<int> artists = stackalloc int[4];
            Span<AlbumTrackEdge> numbers = stackalloc AlbumTrackEdge[32];

            for (int a = 0; a < Albums; a++)
            {
                int album = SlotOf(Current.Albums, AlbumUri(a));
                int count = TracksIn(a);
                for (int k = 0; k < count; k++)
                {
                    targets[k] = SlotOf(Current.Tracks, MemberUri(a, k));
                    numbers[k] = a == CompilationAlbum
                        ? new AlbumTrackEdge((byte)(k < 9 ? 1 : 2), (ushort)(k < 9 ? k + 1 : k - 8))
                        : new AlbumTrackEdge(1, (ushort)(k + 1));
                }
                e.AlbumTracks.ReplaceRun(album, targets[..count], numbers[..count]);

                int billed = Billed(a, artists);
                for (int b = 0; b < billed; b++) targets[b] = SlotOf(Current.Artists, ArtistUri(artists[b]));
                e.AlbumArtists.ReplaceRun(album, targets[..billed], default);

                // The trailing band's relations: al2 carries every section; every other album answers each EMPTY, so
                // its section is absent from the first frame rather than a skeleton waiting on a provider.
                bool rich = a == RichAlbum;
                AlbumRun(e.AlbumVersions, album, rich ? s_richVersions : Array.Empty<int>(), targets);
                AlbumRun(e.AlbumMoreBy, album, rich ? s_richMoreBy : Array.Empty<int>(), targets);
                AlbumRun(e.AlbumSimilar, album, rich ? s_richSimilar : Array.Empty<int>(), targets);
                int playlists = rich ? PlaylistCount : 0;
                for (int p = 0; p < playlists; p++) targets[p] = SlotOf(Current.Playlists, PlaylistUri(p));
                e.AlbumRecommendations.ReplaceRun(album, targets[..playlists], default);
                e.AlbumFeaturedOn.ReplaceRun(album, targets[..playlists], default);
                int listings = rich ? SeedMerch(targets) : 0;
                e.AlbumMerch.ReplaceRun(album, targets[..listings], default);
            }

            static void AlbumRun(EdgeTable<NoEdge> relation, int album, int[] indices, Span<int> targets)
            {
                for (int i = 0; i < indices.Length; i++) targets[i] = SlotOf(Current.Albums, AlbumUri(indices[i]));
                relation.ReplaceRun(album, targets[..indices.Length], default);
            }
        }

        // al2's trailing relations, as album fixture indices (the contract's counts: 2 versions, 6 more-by, 6 similar).
        static readonly int[] s_richVersions = [8, 5];
        static readonly int[] s_richMoreBy = [0, 1, 3, 4, 6, 7];
        static readonly int[] s_richSimilar = [9, 10, 11, 12, 1, 6];
        static readonly string[] s_merchNames = ["Northern Static Tee", "Northern Static Vinyl LP", "Tour Poster", "Enamel Pin", "Hoodie", "Cassette"];
        static readonly string[] s_merchPrices = ["$25.00", "$34.99", "", "$9.00", "$60.00", "$12.00"];

        /// <summary>al2's six merch listings (ch 05 W12): row 3 has no price ("Buy"), row 4 no shop url (an inert listing).
        /// Merch strings are OWNED by the listing (Edges.cs header), so each is retained.</summary>
        static int SeedMerch(Span<int> into)
        {
            var merch = Current.Edges.Merch;
            int first = merch.AllocRun(s_merchNames.Length);
            for (int j = 0; j < s_merchNames.Length; j++)
            {
                ref var m = ref merch.Row[first + j];
                RetainText(ref m.Name, Intern(Utf8(s_merchNames[j])));
                if (s_merchPrices[j].Length > 0) RetainText(ref m.Price, Intern(Utf8(s_merchPrices[j])));
                RetainText(ref m.ImageId, Intern(Utf8(Cover(20 + j))));
                if (j != 3) RetainText(ref m.ShopUrl, Intern(Utf8("https://shop.wavee.app/al2/" + j.ToString(CultureInfo.InvariantCulture))));
                into[j] = first + j;
            }
            return s_merchNames.Length;
        }

        static void LandTracks()
        {
            var e = Current.Edges;
            Span<int> artists = stackalloc int[4];
            Span<int> targets = stackalloc int[8];
            Span<int> zeros = stackalloc int[Waveform];
            Span<StringId> tags = stackalloc StringId[2];

            // the pool: credits, descriptor, and the drawer runs
            for (int a = 0; a < AlbumCount; a++)
                for (int k = 0; k < TracksIn(a); k++)
                {
                    int pool = Wrap(PoolStart(a) + k, TrackCount);
                    int slot = SlotOf(Current.Tracks, TrackUri(pool));
                    Artists(slot, artists[..Credited(a, k, artists)], targets);
                    Tags(slot, pool, IsDrawerRow(a, k), zeros, tags);
                    if (IsDrawerRow(a, k)) Drawer(slot, pool, a, k, artists, targets, zeros);
                    else Traits(slot);
                    if (a == ShortAlbum) RelatedArtists(slot, targets);
                }

            for (int i = 0; i < TrackCount; i++)
            {
                int slot = SlotOf(Current.Tracks, TrackUri(i));
                if (e.TrackArtists.State(slot) != EdgeState.Unknown) continue;   // a member: done above
                artists[0] = Wrap(i, ArtistCount);                               // the base seed's own credit line
                Artists(slot, artists[..1], targets);
                Tags(slot, i, false, zeros, tags);
                Traits(slot);
            }

            // the prerelease's and the waterfall's own rows, and the four counterparts
            for (int a = PreReleaseAlbum; a <= WaterfallAlbum; a++)
                for (int k = 0; k < TracksIn(a); k++)
                    Plain(SlotOf(Current.Tracks, MemberUri(a, k)), a, k, 1000 + a * 20 + k);
            Plain(SlotOf(Current.Tracks, VideoUri(PoolStart(RichAlbum))), RichAlbum, 0, PoolStart(RichAlbum) + 500);
            Plain(SlotOf(Current.Tracks, AltUri(PoolStart(RichAlbum))), RichAlbum, 0, PoolStart(RichAlbum) + 700);
            Plain(SlotOf(Current.Tracks, AltUri(PoolStart(RichAlbum) + 1)), RichAlbum, 1, PoolStart(RichAlbum) + 701);
            Plain(SlotOf(Current.Tracks, VideoUri(PoolStart(ShortAlbum))), ShortAlbum, 0, PoolStart(ShortAlbum) + 500);

            static void Plain(int slot, int a, int k, int seed)
            {
                Span<int> credited = stackalloc int[4];
                Span<int> scratch = stackalloc int[4];
                Artists(slot, credited[..Credited(a, k, credited)], scratch);
                Span<int> none = stackalloc int[Waveform];
                Span<StringId> one = stackalloc StringId[2];
                Tags(slot, seed, false, none, one);
                Traits(slot);
            }
        }

        static void Artists(int track, ReadOnlySpan<int> artists, Span<int> targets)
        {
            for (int i = 0; i < artists.Length; i++) targets[i] = SlotOf(Current.Artists, ArtistUri(artists[i]));
            Current.Edges.TrackArtists.ReplaceRun(track, targets[..artists.Length], default);
        }

        /// <summary>One descriptor (FakeData.cs:60's `Tags[0]`), two on a drawer row. The run is the Tags group's data;
        /// its KNOWN bit rode the staged row.</summary>
        static void Tags(int track, int seed, bool drawer, Span<int> zeros, Span<StringId> tags)
        {
            tags[0] = Intern(Utf8(s_descriptors[Wrap(seed, s_descriptors.Length)]));
            int n = 1;
            if (drawer) tags[n++] = Intern(Utf8(s_descriptors[Wrap(seed + 2, s_descriptors.Length)]));
            Current.Edges.TrackTags.Replace(track, zeros[..n], tags[..n], EdgeState.Complete, n);
        }

        /// <summary>A plain row's drawer relations, answered EMPTY: no versions, no waveform, no credits.</summary>
        static void Traits(int track)
        {
            var e = Current.Edges;
            e.TrackVersions.ReplaceRun(track, default, default);
            e.TrackWaveform.ReplaceRun(track, default, default);
            e.ReleaseCreditText(track);
            e.TrackCredits.ReplaceRun(track, default, default);
        }

        static void RelatedArtists(int track, Span<int> targets)
        {
            for (int i = 0; i < 6; i++) targets[i] = SlotOf(Current.Artists, ArtistUri(6 + i));
            Current.Edges.TrackRelatedArtists.ReplaceRun(track, targets[..6], default);
        }

        /// <summary>One drawer fixture (ch 01 W14-W15, W27; ch 05 W14): the format ladder, the waveform, grouped credits
        /// (linked and unlinked) and the versions (a video counterpart and/or an alternate audio).</summary>
        static void Drawer(int track, int pool, int a, int k, Span<int> artists, Span<int> targets, Span<int> zeros)
        {
            var e = Current.Edges;

            // formats: Ogg 320/160/96 on every fixture, AAC on three, FLAC 16 on al2's first row
            Span<FormatEdge> ladder = stackalloc FormatEdge[5];
            int rungs = 0;
            ladder[rungs++] = new FormatEdge(2, 320);
            ladder[rungs++] = new FormatEdge(1, 160);
            ladder[rungs++] = new FormatEdge(0, 96);
            if (!(a == RichAlbum && k == 2)) ladder[rungs++] = new FormatEdge(9, 48);
            if (a == RichAlbum && k == 0) ladder[rungs++] = new FormatEdge(16, 1411);
            e.TrackFormats.Replace(track, zeros[..rungs], ladder[..rungs], EdgeState.Complete, rungs);

            // waveform: 220 magnitudes, a deterministic triangle envelope
            Span<byte> columns = stackalloc byte[Waveform];
            for (int c = 0; c < Waveform; c++)
            {
                int phase = (c * 3 + pool) % 64;
                columns[c] = (byte)(60 + (phase < 32 ? phase : 63 - phase) * 6);
            }
            e.TrackWaveform.Replace(track, zeros, columns, EdgeState.Complete, Waveform);

            // credits: performers (linked), songwriters (one linked, one not), a producer with no artist page
            int n = Credited(a, k, artists);
            Span<CreditEdge> credits = stackalloc CreditEdge[8];
            int rows = 0;
            for (int i = 0; i < n && rows < 4; i++)
            {
                targets[rows] = SlotOf(Current.Artists, ArtistUri(artists[i]));
                credits[rows++] = Credit(s_artistNames[Wrap(artists[i], ArtistCount)], i == 0 ? "Main Artist" : "Featured Artist", "Performers");
            }
            targets[rows] = SlotOf(Current.Artists, ArtistUri(artists[0]));
            credits[rows++] = Credit(s_artistNames[Wrap(artists[0], ArtistCount)], "Composer", "Songwriters");
            targets[rows] = Table.None;
            credits[rows++] = Credit("Mara Ellison", "Lyricist", "Songwriters");
            targets[rows] = Table.None;
            credits[rows++] = Credit("Theo Vance", "Producer", "Producers");
            e.ReleaseCreditText(track);
            e.TrackCredits.Replace(track, targets[..rows], credits[..rows], EdgeState.Complete, rows);

            // versions: the self row is the drawer's own; the edge names the OTHER renditions
            Span<VersionEdge> kinds = stackalloc VersionEdge[2];
            int versions = 0;
            if (k == 0)
            {
                targets[versions] = SlotOf(Current.Tracks, VideoUri(pool));
                kinds[versions++] = new VersionEdge(TrackVersionKind.Video);
            }
            if (a == RichAlbum && k <= 1)
            {
                targets[versions] = SlotOf(Current.Tracks, AltUri(pool));
                kinds[versions++] = new VersionEdge(TrackVersionKind.Audio);
            }
            e.TrackVersions.Replace(track, targets[..versions], kinds[..versions], EdgeState.Complete, versions);

            static CreditEdge Credit(string name, string role, string group)
                => new(Retained(Intern(Utf8(name))), Retained(Intern(Utf8(role))), Retained(Intern(Utf8(group))));
        }

        static void LandShows()
        {
            Span<int> targets = stackalloc int[16];
            for (int sh = 0; sh < Shows; sh++)
            {
                int n = EpisodesIn(sh), total = TotalEpisodes(sh);
                for (int i = 0; i < n; i++) targets[i] = SlotOf(Current.Episodes, EpisodeUri(sh, i));
                int show = SlotOf(Current.Shows, ShowUri(sh));
                Current.Edges.ShowEpisodes.Replace(show, targets[..n], default,
                    total > n ? EdgeState.Partial : EdgeState.Complete, total);
            }
        }
    }
}
