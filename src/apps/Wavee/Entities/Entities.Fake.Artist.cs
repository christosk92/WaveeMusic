// ── Entities/Entities.Fake.Artist.cs ────────────────────────────────────────────────────────────────────────────────
// SeedArtistSurfaces — the artist page and the discography, seeded (ch 31 SEED-SURFACES, the artist rows)
//
// Role: CORE (no engine types, no I/O, no clock read, no Random, no string.GetHashCode)
// Owner: N (stream N-B)
// Wave: 5
// Budget: ~900 lines (a new seed file, WP-5.N contract §0)
// Spec: ch 31 §0, §7.2 (determinism), §7.3 / W16 (the clock), §8 (Hash, Wrap, KindMatches, TopTracksOf, TourBannerFor);
//       ch 08 §7 (what the page reads); WP-5.N contract §8 (the artist matrix, the uri prefixes, the order)
//
// THE ORDER (contract §8): 1. N-C's `SeedConcertSurfaces(now0)` — concert rows, places, the feed, every artist schedule;
// 2. ONE staging for every artist's overview groups, chart, facets, appears-on, related and the six payload relations,
// committed through the ordinary path at `Authority.Seed`; 3. `Artist.DeriveTour(slot, now0 * 1000)` for every seeded
// artist, so the banner is deterministic on the fixed clock whatever the concert commit derived against the wall clock.
//
// THE SAME PATH THE DECODER TAKES: the chart is the SEED list (`Staging.EndPopular`), the payload relations are
// `ArtistExtraRun`s, the facets / related / appears-on are `EdgeRun`s — so `--fake` exercises the merge and the owned-text
// commit exactly as a live overview does, and nothing on the page asks "am I fake".
//
// TRUTHFUL KNOWN BITS: the core artists' IDENTITY is the core seed's (never restaged here); this file states the overview
// groups and the chart. Facet albums state `AlbumFields.Card & ~AlbumFields.Artists` because every OTHER card field is
// filled but no `AlbumArtists` run is staged for them (they are the page artist's own releases — nothing here reads a
// billed-artist line off a facet/appears-on card, and Card's own doc names only name/cover/year/date/count/kind) — the
// same "truthful known bits" discipline this comment already claims: were the bit left in, `Knows(AlbumFields.Card)`
// would silently overclaim a run this file never populates, mirroring bug C's album variant, ONE level down. Each facet
// album carries an `AlbumTracks` run whose length IS its `TrackCount` (the ch 31 §0.8 agreement rule). The chart's
// tracks state `TrackFields.Row` (+ Year) with a real `TrackArtists` run behind the Artists bit.
//
// ROW IDENTITY (contract §8): minted under this stream's own prefixes — `spotify:track:pt{i}n{k}` (chart k < 50, videos
// k ≥ 50), `spotify:album:dg{i}{a|s|c|p|u}{k}` (albums, singles, compilations, appears-on, the upcoming release),
// `spotify:artist:arnoimg` (the no-image artist, ch 08 W4b). Core rows (`tr*`, `pl*`, `ar*`) are READ as targets only.

using System.Globalization;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Entities
{
    static partial void SeedArtistSurfaces(long now0) => ArtistSeed.Run(now0);

    /// <summary>The artist seed, as a nested class so its helpers cannot collide with the other surfaces' seeds in this
    /// partial class (AlbumSeed / LibrarySeed precedent).</summary>
    static class ArtistSeed
    {
        const long Day = 86_400;

        /// <summary>The artists this seed dresses: the twelve core artists, then <see cref="NoImageUri"/> at index 12.</summary>
        public const int SeededArtists = ArtistCount + 1;

        /// <summary>ch 08 W4b: an artist with no header and no avatar.</summary>
        public const string NoImageUri = "spotify:artist:arnoimg";
        const string NoImageName = "The Unpictured";

        /// <summary>The artist whose page carries everything (ch 31 §10 item 19-20).</summary>
        public const int Showcase = 3;

        /// <summary>Facet sizes (contract §8): the showcase's singles pass 300, so the grid genuinely virtualizes and the
        /// era bands group; everyone else gets 2 / 3 / 1.</summary>
        public static int FacetCount(int artist, DiscoFacet facet) => artist == Showcase
            ? facet switch { DiscoFacet.Singles => 320, DiscoFacet.Compilations => 12, _ => 64 }
            : facet switch { DiscoFacet.Singles => 3, DiscoFacet.Compilations => 1, _ => 2 };

        public static int ChartCount(int artist) => artist == Showcase ? 10 : 5;
        public static bool HasPick(int i) => i != 2 && (i % 3 != 1 || i == Showcase);
        public static bool HasUpcoming(int i) => i is Showcase or 7;
        public static bool HasVideos(int i) => i % 5 != 2 || i == Showcase;
        public static bool HasPlaylists(int i) => i % 2 == 0 || i == Showcase;
        public static bool HasMerch(int i) => i % 3 != 1 || i == Showcase;
        public static bool HasCities(int i) => i % 2 == 1 || i == Showcase;
        public static bool HasGallery(int i) => i % 3 != 2 || i == Showcase;
        public static bool HasAppearsOn(int i) => i % 2 == 1 || i == Showcase;
        public static bool IsVerified(int i) => i % 5 != 0;
        public static bool HasWorldRank(int i) => i % 7 != 0;

        /// <summary>The upcoming release's end instant: <c>now0 + 9 d 4 h</c> (ch 31 W16).</summary>
        public static long UpcomingAt(long now0) => now0 + 9 * Day + 4 * 3600;

        static readonly string[] s_merchNames = ["Tour Tee", "Vinyl LP", "Hoodie", "Cap", "Poster", "Enamel Pin"];
        static readonly string[] s_cities = ["São Paulo", "London", "Mexico City", "Sydney", "Quezon City", "Jakarta", "Istanbul"];
        static readonly string[] s_countries = ["BR", "GB", "MX", "AU", "PH", "ID", "TR"];
        static readonly string[] s_playlistSubtitles = ["Spotify · official", "Spotify · featured", "Discovered on", "Made for you"];

        /// <summary>ch 31 §8's deterministic string hash (SpotifyExportMapper.cs:1024): <c>h = 17; h = h·31 + c; h &amp;
        /// 0x7fffffff</c>. It replaces 0.2.9's per-process <c>string.GetHashCode()</c> (§7.2 rule A).</summary>
        public static int Hash(string s)
        {
            int h = 17;
            unchecked { foreach (char c in s) h = h * 31 + c; }
            return h & 0x7fffffff;
        }

        public static string NameOf(int i) => i < ArtistCount ? s_artistNames[Wrap(i, ArtistCount)] : NoImageName;
        public static string ArtistUriOf(int i) => i < ArtistCount ? ArtistUri(i) : NoImageUri;

        static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);

        public static string ChartTrackUri(int artist, int k) => "spotify:track:pt" + Inv(artist) + "n" + Inv(k);
        public static string VideoTrackUri(int artist, int k) => ChartTrackUri(artist, 50 + k);

        public static string ReleaseUri(int artist, DiscoFacet facet, int k)
            => "spotify:album:dg" + Inv(artist) + facet switch { DiscoFacet.Singles => "s", DiscoFacet.Compilations => "c", _ => "a" } + Inv(k);

        public static string AppearsOnUri(int artist, int k) => "spotify:album:dg" + Inv(artist) + "p" + Inv(k);
        public static string UpcomingUri(int artist) => "spotify:album:dg" + Inv(artist) + "u0";

        /// <summary>Album or single, alternating by artist, so ch 08's latest-release banner shows BOTH arms (ch 31 21a).</summary>
        public static string LatestUri(int artist)
            => ReleaseUri(artist, artist % 2 == 0 ? DiscoFacet.Albums : DiscoFacet.Singles, 0);

        // ── a release's shape: pure functions of (artist, facet, k) ──────────────────────────────────────────────────

        /// <summary>The facet's kind for release k — Singles alternates Single and EP — coerced through the ONE filter so
        /// the seed's split and a live facet's cannot disagree (ch 31 §8 KindMatches).</summary>
        public static AlbumKind KindOf(DiscoFacet facet, int k)
        {
            var kind = facet switch
            {
                DiscoFacet.Singles => k % 4 == 3 ? AlbumKind.EP : AlbumKind.Single,
                DiscoFacet.Compilations => AlbumKind.Compilation,
                _ => AlbumKind.Album,
            };
            return ArtistCatalog.KindMatches(kind, facet) ? kind : ArtistCatalog.CanonicalKind(facet, TracksOf(facet, k));
        }

        public static int TracksOf(DiscoFacet facet, int k) => facet switch
        {
            DiscoFacet.Singles => k % 4 == 3 ? 5 : 1 + k % 3,
            DiscoFacet.Compilations => 14 + k % 6,
            _ => 8 + (k * 5) % 9,
        };

        /// <summary>DATE_DESC over a span of years: the showcase's albums reach back 40 years, its singles 36, its
        /// compilations 33 — three decades or more, so the era bands group. A fixed calendar (not now0): a release date
        /// is history, not a countdown.</summary>
        public static DateOnly ReleaseDate(int artist, DiscoFacet facet, int k)
        {
            int count = FacetCount(artist, facet);
            int years = artist == Showcase ? facet switch { DiscoFacet.Singles => 36, DiscoFacet.Compilations => 33, _ => 40 } : 6;
            int daysBack = count <= 1 ? 0 : (int)((long)k * years * 365 / count);
            return new DateOnly(2025, 12, 1).AddDays(-daysBack - artist * 3);
        }

        /// <summary>Day precision, with every ninth release known only to the year (the card's YEAR arm).</summary>
        static byte PrecisionOf(int k) => k % 9 == 8 ? (byte)0 : (byte)2;

        // ── the run ─────────────────────────────────────────────────────────────────────────────────────────────────

        public static void Run(long now0)
        {
            SeedConcertSurfaces(now0);                          // N-C first (contract §8.1)

            var staging = Staging.Rent();
            try
            {
                staging.Authority = Authority.Seed;
                for (int i = 0; i < SeededArtists; i++) StageArtist(staging, i, now0);
                Commit(staging);
            }
            finally
            {
                Staging.Return(staging);
            }

            // LAST: the banner on the fixed seed clock (contract §8.3).
            for (int i = 0; i < SeededArtists; i++)
                global::Wavee.Artist.DeriveTour(ResolveSeedSlot(EntityKind.Artist, ArtistUriOf(i)), now0 * 1000);   // `Artist` alone is Entities' factory
        }

        static TextRef Text(Staging s, string value) => value.Length == 0 ? default : s.AddText(Utf8(value));
        static StagedId Id(Staging s, string uri) => new(s.AddText(Utf8(uri)));

        static void StageArtist(Staging s, int i, long now0)
        {
            string name = NameOf(i);
            string uri = ArtistUriOf(i);
            int h = Hash(name);
            var artist = Id(s, uri);

            // Facets, chart, shelves FIRST: each run is closed before the artist row is appended, so no two relations
            // share a slice, and the row's own text follows its lists in the arena (order is free at commit).
            for (int f = 0; f < 3; f++) StageFacet(s, i, (DiscoFacet)f, in artist);
            StageChart(s, i, name, h, in artist);
            StageRelated(s, i, in artist);
            StageAppearsOn(s, i, in artist);
            StagePlaylists(s, i, in artist);
            StageVideos(s, i, name, in artist);
            StageMerch(s, i, h, in artist);
            StageCities(s, i, h, in artist);
            StageGallery(s, i, h, in artist);
            StageLinks(s, name, in artist);

            uint known = (uint)(ArtistFields.Header | ArtistFields.Stats | ArtistFields.Bio | ArtistFields.Pick
                              | ArtistFields.PreRelease | ArtistFields.Latest | ArtistFields.Tour | ArtistFields.Chart);
            if (i >= ArtistCount) known |= (uint)ArtistFields.Identity;   // arnoimg is this seed's own row
            ref var row = ref s.Artists.RowFor(artist, Authority.Seed, known);
            if (i >= ArtistCount) row.Name = Text(s, name);                 // and it has NO image (W4b)

            uint monthly = (uint)(850_000L + (h % 32) * 940_000L);
            row.Monthly = monthly;
            row.Followers = monthly / 2 + (uint)(h % 11) * 130_000u;
            row.WorldRank = HasWorldRank(i) ? (ushort)(1 + h % 500) : (ushort)0;
            if (IsVerified(i)) row.Flags |= (uint)ArtistFlags.Verified;
            if (i < ArtistCount)
            {
                row.Header = Text(s, Cover(i + 3));                         // the avatar's own picture, as 0.2.9 did
                row.HeaderAccent = 0;
            }

            string bio = "<p>" + name + " is an artist whose sound moves between intimate, late-night textures and wide, "
                       + "festival-scale moments. With " + monthly.ToString("N0", CultureInfo.InvariantCulture)
                       + " monthly listeners, the catalogue spans early EPs, breakout singles and the records that defined "
                       + "the run — built for headphones at 2am and crowds at midnight alike.</p>";
            row.Bio = Text(s, bio);
            Span<byte> lead = stackalloc byte[512];
            int n = ArtistText.Lead(Utf8(bio), lead);
            row.BioLead = n > 0 ? s.AddText(lead[..n]) : default;

            row.LatestUri = Id(s, LatestUri(i));
            if (HasPick(i))
            {
                string pickUri = LatestUri(i);
                var kind = i % 2 == 0 ? AlbumKind.Album : KindOf(DiscoFacet.Singles, 0);
                row.PickUri = Text(s, pickUri);
                row.PickItemUri = Text(s, pickUri);
                row.PickItemKind = (byte)EntityKind.Album;
                row.PickTitle = Text(s, ReleaseTitle(i, i % 2 == 0 ? DiscoFacet.Albums : DiscoFacet.Singles, 0));
                row.PickSubtitle = Text(s, (kind == AlbumKind.Album ? "Album" : "Single") + " · "
                                           + ReleaseDate(i, DiscoFacet.Albums, 0).Year.ToString(CultureInfo.InvariantCulture));
                row.PickComment = Text(s, "Out now — give it a listen.");
                row.PickCover = Text(s, Cover(i * 5));
                row.PickBackground = i == Showcase ? Text(s, Cover(i + 9)) : default;
            }
            if (HasUpcoming(i))
            {
                row.UpcomingUri = Text(s, UpcomingUri(i));
                row.UpcomingName = Text(s, s_titles[Wrap(i * 5 + 11, s_titles.Length)] + " (Deluxe)");
                row.UpcomingCover = Text(s, Cover(i + 13));
                row.UpcomingType = Text(s, "ALBUM");
                row.UpcomingReleaseAt = (int)UpcomingAt(now0);
                row.Flags |= (uint)ArtistFlags.Upcoming;
            }
        }

        public static string ReleaseTitle(int artist, DiscoFacet facet, int k)
            => s_titles[Wrap(artist * 7 + (int)facet * 5 + k * 3, s_titles.Length)]
               + (k >= s_titles.Length ? " " + Inv(k / s_titles.Length + 1) : "");

        /// <summary>One facet: the releases as card-complete album rows, each with its tracklist (core tracks as targets,
        /// run length == TrackCount), and the facet as ONE Complete run in DATE_DESC order.</summary>
        static void StageFacet(Staging s, int artist, DiscoFacet facet, in StagedId parent)
        {
            int count = FacetCount(artist, facet);
            Span<StagedId> ids = count <= 512 ? stackalloc StagedId[count] : new StagedId[count];
            for (int k = 0; k < count; k++)
            {
                var id = Id(s, ReleaseUri(artist, facet, k));
                ids[k] = id;
                var kind = KindOf(facet, k);
                int tracks = TracksOf(facet, k);
                var date = ReleaseDate(artist, facet, k);
                byte precision = PrecisionOf(k);

                // `& ~Artists`: no `AlbumArtists` run is staged for a facet card — see the file header's TRUTHFUL
                // KNOWN BITS note. Card now composes through Identity, which carries the bit since it was added for
                // bug C's album variant; a facet card must not claim it un-backed.
                ref var row = ref s.Albums.RowFor(id, Authority.Seed, (uint)AlbumFields.Card & ~(uint)AlbumFields.Artists);
                row.Title = Text(s, ReleaseTitle(artist, facet, k));
                row.Image = Text(s, Cover(artist * 5 + (int)facet * 3 + k));
                row.Year = (ushort)date.Year;
                row.TrackCount = tracks;
                row.Kind = (byte)kind;
                row.ReleaseDateIso = Text(s, date.ToString(precision == 0 ? "yyyy'-01-01'" : "yyyy-MM-dd", CultureInfo.InvariantCulture));
                row.DatePrecision = precision;
                row.ReleaseAt = Spotify.Decode.Seconds(date.Year, precision == 0 ? 1 : date.Month, precision == 0 ? 1 : date.Day);

                var run = s.Run(Relation.AlbumTracks);
                for (int t = 0; t < tracks; t++)
                {
                    int slot = ResolveSeedSlot(EntityKind.Track, TrackUri(artist * 17 + (int)facet * 29 + k * 11 + t));
                    ref var edge = ref run.Add(new StagedId(Current.Tracks.Id[slot]));
                    edge.B0 = 1;
                    edge.U0 = (ushort)(t + 1);
                }
                run.End(in id, EdgeState.Complete, tracks);
            }

            var facetRun = s.Run(facet switch
            {
                DiscoFacet.Singles => Relation.ArtistSingles,
                DiscoFacet.Compilations => Relation.ArtistCompilations,
                _ => Relation.ArtistAlbums,
            });
            for (int k = 0; k < count; k++) facetRun.Add(in ids[k]);
            facetRun.EndEvenIfEmpty(in parent, count);
        }

        /// <summary>The chart SEED (contract §8: the showcase 10 rows — two columns and a pager — everyone else 5): minted
        /// tracks with play counts, every third crediting a second core artist ("feat."), ordered by
        /// <see cref="ArtistPopularTracks.TopByPlays"/> — the ranking rule the chart and the artist context share.</summary>
        static void StageChart(Staging s, int artist, string name, int h, in StagedId parent)
        {
            int count = ChartCount(artist);
            Span<uint> plays = stackalloc uint[count];
            Span<int> titleKeys = stackalloc int[count];
            Span<int> order = stackalloc int[count];
            for (int k = 0; k < count; k++)
            {
                plays[k] = (uint)(2_400_000_000L / (k + 1) + (h % 1000) * 1000L);
                titleKeys[k] = Wrap(artist * 3 + k, s_titles.Length);
            }
            int ranked = ArtistPopularTracks.TopByPlays(plays, titleKeys, count, order);

            int mark = s.PopularMark;
            for (int r = 0; r < ranked; r++)
            {
                int k = order[r];
                var id = Id(s, ChartTrackUri(artist, k));
                int feat = k % 3 == 1 ? Wrap(artist + 1 + k, ArtistCount) : -1;
                if (feat == artist) feat = Wrap(feat + 1, ArtistCount);
                StageTrack(s, id, artist, name, k, titleKeys[k], plays[k], feat);
                s.PopularTracks.Add() = id;
            }
            s.EndPopular(in parent, mark, extension: false);
        }

        /// <summary>A minted track the chart or the video shelf targets: <c>TrackFields.Row</c> + Year, with its
        /// <c>TrackArtists</c> run (the page artist, then the featured one) behind the Artists bit.</summary>
        static void StageTrack(Staging s, in StagedId id, int artist, string name, int k, int title, uint plays, int feat)
        {
            string line = feat >= 0 ? name + ", " + s_artistNames[feat] : name;
            ref var row = ref s.Tracks.RowFor(id, Authority.Seed,
                (uint)(TrackFields.Row | TrackFields.Year));
            row.Title = Text(s, s_titles[Wrap(title, s_titles.Length)]);
            row.ArtistLine = Text(s, line);
            row.Image = Text(s, Cover(artist * 2 + k));
            row.AlbumUri = Id(s, ReleaseUri(artist, DiscoFacet.Albums, 0));
            row.DurationMs = 150_000 + (k * 37 % 120) * 1000;
            row.Year = (ushort)(2012 + Wrap(k, 14));
            row.PlayCount = plays;
            if (k % 4 == 1) row.Flags |= (uint)TrackFlags.Explicit;

            var credits = s.Run(Relation.TrackArtists);
            credits.Add(Id(s, ArtistUriOf(artist)));
            if (feat >= 0) credits.Add(Id(s, ArtistUri(feat)));
            credits.End(in id);
        }

        /// <summary>"Fans also like": six core artists, never the page artist (contract §8: related targets core rows).</summary>
        static void StageRelated(Staging s, int artist, in StagedId parent)
        {
            var run = s.Run(Relation.ArtistRelated);
            for (int k = 1, added = 0; added < 6 && k <= ArtistCount; k++)
            {
                int other = Wrap(artist + k, ArtistCount);
                if (other == artist) continue;
                run.Add(Id(s, ArtistUri(other)));
                added++;
            }
            run.EndEvenIfEmpty(in parent);
        }

        /// <summary>Appears on: six compilations under the stream's own prefix, card-complete (no drawer opens from a
        /// shelf, so no tracklist) — or a Complete-and-EMPTY run where the matrix says none.</summary>
        static void StageAppearsOn(Staging s, int artist, in StagedId parent)
        {
            var run = s.Run(Relation.ArtistAppearsOn);
            if (HasAppearsOn(artist))
                for (int k = 0; k < 6; k++)
                {
                    var id = Id(s, AppearsOnUri(artist, k));
                    var date = new DateOnly(2024, 6, 1).AddDays(-k * 211 - artist * 7);
                    // Same exclusion as `StageFacet` above: an "appears on" card names no billed artist either.
                    ref var row = ref s.Albums.RowFor(id, Authority.Seed, (uint)AlbumFields.Card & ~(uint)AlbumFields.Artists);
                    row.Title = Text(s, "Best of " + Inv(date.Year));
                    row.Image = Text(s, Cover(artist + k + 40));
                    row.Year = (ushort)date.Year;
                    row.TrackCount = 20 + k;
                    row.Kind = (byte)AlbumKind.Compilation;
                    row.ReleaseDateIso = Text(s, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    row.DatePrecision = 2;
                    row.ReleaseAt = Spotify.Decode.Seconds(date.Year, date.Month, date.Day);
                    run.Add(in id).B0 = (byte)AlbumKind.Compilation;
                }
            run.EndEvenIfEmpty(in parent);
        }

        static void StagePlaylists(Staging s, int artist, in StagedId parent)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Playlists);
            if (HasPlaylists(artist))
                for (int k = 0; k < 4; k++)
                {
                    ref var member = ref run.Add();
                    member.Target = new StagedId(Current.Playlists.Id[ResolveSeedSlot(EntityKind.Playlist, PlaylistUri(artist + k))]);
                    member.T0 = Text(s, s_playlistSubtitles[k]);
                }
            run.End(in parent);
        }

        /// <summary>Four music videos: minted tracks (Row-known, so a card has its title) with a 16:9-shelf still and a
        /// duration on the edge.</summary>
        static void StageVideos(Staging s, int artist, string name, in StagedId parent)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Videos);
            if (HasVideos(artist))
                for (int k = 0; k < 4; k++)
                {
                    var id = Id(s, VideoTrackUri(artist, k));
                    // A real count behind the PlayCount bit (ch 31 §8 assertion 7: no Known bit over a default value).
                    StageTrack(s, id, artist, name, 50 + k, artist * 5 + k, (uint)(3_000_000 + k * 250_000), -1);
                    ref var member = ref run.Add();
                    member.Target = id;
                    member.T0 = Text(s, Cover(artist + k + 20));
                    member.I0 = 200_000 + k * 13_000;
                }
            run.End(in parent);
        }

        static void StageMerch(Staging s, int artist, int h, in StagedId parent)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Merch);
            if (HasMerch(artist))
                for (int k = 0, n = 3 + h % 4; k < n; k++)
                {
                    ref var member = ref run.Add();
                    member.T0 = Text(s, s_merchNames[Wrap(h + k, s_merchNames.Length)]);
                    member.T1 = Text(s, "$" + Inv(19 + k * 5) + ".00");
                    member.T2 = Text(s, Cover(artist + k + 5));
                }
            run.End(in parent);
        }

        static void StageCities(Staging s, int artist, int h, in StagedId parent)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Cities);
            if (HasCities(artist))
            {
                long monthly = 850_000L + (h % 32) * 940_000L;
                for (int k = 0; k < 5; k++)
                {
                    int c = Wrap(h + k, s_cities.Length);
                    ref var member = ref run.Add();
                    member.T0 = Text(s, s_cities[c]);
                    member.T1 = Text(s, s_countries[c]);
                    member.U0 = (uint)Math.Max(1L, monthly / 30 / (k + 1));
                }
            }
            run.End(in parent);
        }

        static void StageGallery(Staging s, int artist, int h, in StagedId parent)
        {
            var run = s.RunArtistExtra(ArtistExtraKind.Gallery);
            if (HasGallery(artist))
                for (int k = 0, n = 4 + h % 5; k < n; k++) run.Add().T0 = Text(s, Cover(artist + k + 3));   // n ≤ 8 < 16: distinct
            run.End(in parent);
        }

        /// <summary>Instagram, Twitter, Wikipedia for every artist (contract §8: links always).</summary>
        static void StageLinks(Staging s, string name, in StagedId parent)
        {
            Span<char> buffer = stackalloc char[name.Length];
            int n = 0;
            foreach (char c in name) if (char.IsAsciiLetterOrDigit(c)) buffer[n++] = char.ToLowerInvariant(c);
            string slug = n == 0 ? "artist" : new string(buffer[..n]);
            var run = s.RunArtistExtra(ArtistExtraKind.Links);
            Link(ref run, s, "Instagram", "https://instagram.com/" + slug, ArtistCatalog.LinkKind.Instagram);
            Link(ref run, s, "Twitter", "https://twitter.com/" + slug, ArtistCatalog.LinkKind.Twitter);
            Link(ref run, s, "Wikipedia", "https://en.wikipedia.org/wiki/" + slug, ArtistCatalog.LinkKind.Wikipedia);
            run.End(in parent);

            static void Link(ref ArtistExtraRun run, Staging s, string title, string url, ArtistCatalog.LinkKind kind)
            {
                ref var member = ref run.Add();
                member.T0 = Text(s, title);
                member.T1 = Text(s, url);
                member.B0 = (byte)kind;
            }
        }
    }
}
