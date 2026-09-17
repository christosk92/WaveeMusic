// ── Entities/Entities.Fake.Library.cs ──────────────────────────────────────────────────────────────────────────────
// SEED-SURFACES for owner O — the playlist page (every header/notice/tuning/chart/daylist arm), Local Files, the Liked
// facts' descriptors and credits, and the curated chip set (`SeedLibrarySurfaces`, the WP-5.O contract §5)
//
// Role: CORE
// Owner: O (Wave 5, stream B of WP-5.O)
// Wave: 5
// Budget: ~650 lines
// Spec: ch 31 §0, §2 W7/W8/W11/W16, §3.1, §7.1 rows 1/3/6-8/10-14/19-25, §7.2, §7.3 · ch 06 §7 · ch 07 §7 · the WP-5.O
//   contract §5 (fixtures U1 · P0-P6 · X0-X5 · L0 · T0 · T1 · C0)
//
// THE SAME RULES AS `Entities.Fake.cs` (ch 31 §0.1-§0.3, §7.2): rows go through `Staging` + `Commit` at `Authority.Seed`,
// relations through the handle writers (`Playlist.ApplyMembership` / `Apply` / `SetNotice` / `SettleCreate` /
// `ApplyTuning` / `ApplyRecommendations`, `User.SetContentFilters`) or an edge table's whole-run write; every value is a
// pure function of a fixture index and `now0` (UNIX seconds); no `Random`, no clock, no `string.GetHashCode`; nothing
// publishes — `SeedFake` publishes once.
//
// THIS PARTIAL RUNS AFTER THE BASE SEED, THE ALBUM SEED (M) AND THE ARTIST SEED (N), BEFORE THE HOME SEED (P). Two
// relations can therefore already be written when it runs, and it never clobbers either:
//   · TrackArtists (T1) — written ONLY for a pool track whose run is still Unknown (M credits every pool track today, so
//     T1 is the fallback for a build without M's seed);
//   · TrackTags (T0) — an Unknown run gets the 0.2.9 descriptor cycle; a KNOWN one-descriptor run on 1-in-3 rows gains
//     the SECOND descriptor ("Chill", the seventh concept C0's curated set is evidenced by). The primary is never touched.
//
// THE CLOCK (reported): `Platform.Clock.FixedSeedEpoch` is in the past against the wall clock `Entities.Now` follows, so a
// `now0 + …` instant (the daylist rollover) reads expired at launch under the fixed epoch and live under `--live-clock`.
// The contract's offsets are kept verbatim; the album seed's "+730 d" dodge is not copied onto a 4-hour window.

using System.Globalization;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Entities
{
    static partial void SeedLibrarySurfaces(long now0) => LibrarySeed.Run(now0);

    /// <summary>Owner O's seed, namespaced so its helpers cannot collide with the other owners' seed partials. Inside it
    /// <c>Track</c>, <c>Playlist</c>, <c>User</c> … are Entities' FACTORY METHODS, so the handle types are spelled
    /// <c>global::Wavee.*</c>.</summary>
    internal static class LibrarySeed
    {
        // ── fixture constants (the WP-5.O contract §5) ─────────────────────────────────────────────────────────────

        public const string SpotifyUserUri = "spotify:user:spotify";
        public const string LocalUserUri = "spotify:user:local";
        public const int ExtraCount = 6;             // plx0-plx5
        public const int LocalCount = 14;            // ch 31 §3.1 "local tracks"
        public const int RecommendationCount = 24;   // X0
        public const int ChartNewEntries = 7;        // P4
        public const long ChartAgeSeconds = 7200;    // P4: "Updated 2 hours ago"
        public const int SavesP0 = 18_700_000;
        public const uint DaylistAccent = 0xFF6B3FA0;
        const long Hour = 3600;

        /// <summary>The chart delta bytes as <c>Spotify.Decode.ChartStatus</c> writes them.</summary>
        public const byte ChartEqual = 1, ChartUp = 2, ChartDown = 3, ChartNew = 4;

        public static string ExtraUri(int k) => "spotify:playlist:plx" + Wrap(k, ExtraCount).ToString(CultureInfo.InvariantCulture);
        public static string LocalTrackUri(int i) => "wavee:local:track:" + Wrap(i, LocalCount).ToString(CultureInfo.InvariantCulture);
        static string EpisodeRowUri(int k) => "spotify:track:plxep" + Wrap(k, 3).ToString(CultureInfo.InvariantCulture);

        static readonly string[] s_extraTitles = ["My Playlist #7", "Cafe Mosaic", "Deleted Mix", "Private Share", "My Playlist #8", "Mixed Bag"];

        /// <summary>0.2.9 <c>FakeData.LocalSeed</c> (:344-351), titles only — a local row has no album and no credit here.</summary>
        static readonly string[] s_localTitles =
        [
            "Sunset Boulevard", "Paper Planes", "Northern Lights", "Coastline", "Old Cassette", "Rainy Window", "First Snow",
            "Long Drive Home", "Attic Tapes", "Quiet Hours", "Garden Path", "Backroads", "Harbor Lights", "Morning Pages",
        ];

        /// <summary>0.2.9 <c>FakeData.Descriptors</c> (:63), primary-first, cycling.</summary>
        public static readonly string[] Descriptors = ["Pop", "Dance", "Indie", "Hip Hop", "Rock", "Electronic"];
        public const string SecondDescriptor = "Chill";

        /// <summary>C0: eight curated chips in SERVER order — seven evidenced by T0's descriptors, "Jazz" by nothing, placed
        /// mid-set so the evidence ordering has something to move.</summary>
        public static readonly string[] ChipTitles = ["Pop", "Dance", "Jazz", "Indie", "Hip Hop", "Chill", "Rock", "Electronic"];
        public static readonly string[] ChipTokens = ["pop", "dance", "jazz", "indie", "hip-hop", "chill", "rock", "electronic"];

        static readonly string[] s_episodeTitles = ["The Build Trap", "Latency, Honestly", "On Craft"];   // FakeData.cs:380

        // ch 06 W24's session-control vocabulary (0.2.9 PlaylistSignalsTests' identifiers).
        const string TuneDiscovery = "session_control_display$mix$more_discovery";
        const string TuneSoftPop = "session_control_display$mix$soft_pop:nl_genre";
        const string TuneEnergy = "session_control_display$mix$more_energy";
        const string TuneReset = "session-control-reset";

        const string P0Description =
            "Soft vocals, warm guitars and a few surprises for a slow Saturday morning. Start with "
            + "<a href=\"spotify:artist:ar1\">Alex Rivers</a> and let the rest drift in while the coffee is still hot — "
            + "updated whenever something new sounds like a window seat on a rainy afternoon.";

        /// <summary>Owner caps of the account's own playlists.</summary>
        const byte OwnerCaps = (byte)(PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.CanEditMetadata | PlaylistCaps.IsOwner);

        public static void Run(long now0)
        {
            var s = Staging.Rent();
            try
            {
                StageUsers(s);
                StageCorePlaylists(s, now0);
                StageExtras(s);
                StageLocal(s);
                StageTrackKnowns(s);
                Commit(s);
            }
            finally
            {
                Staging.Return(s);
            }

            LandCoreMemberships(now0);
            LandTuning();
            LandExtras(now0);
            LandLocal();
            LandTags();
            LandCredits();
            LandContentFilters();
        }

        static TextRef Text(Staging s, string value) => value.Length == 0 ? default : s.AddText(Utf8(value));

        // ══ 1. ROWS ════════════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>U1: the editorial owner and the local owner.</summary>
        static void StageUsers(Staging s)
        {
            ref var spotify = ref s.Users.RowFor(Text(s, SpotifyUserUri), Authority.Seed, (uint)UserFields.Identity);
            spotify.Name = Text(s, "Spotify");
            ref var local = ref s.Users.RowFor(Text(s, LocalUserUri), Authority.Seed, (uint)UserFields.Identity);
            local.Name = Text(s, "On this device");
        }

        /// <summary>Re-states one base playlist's Identity group at the same rung (every Identity column is written, so the
        /// base values are restated verbatim where the fixture does not change them).</summary>
        static ref StagedPlaylist Restate(Staging s, int i, PlaylistFields groups, string ownerUri)
        {
            ref var row = ref s.Playlists.RowFor(Text(s, PlaylistUri(i)), Authority.Seed, (uint)(PlaylistFields.Identity | groups));
            row.Title = Text(s, s_playlistNames[Wrap(i, PlaylistCount)]);
            row.Image = Text(s, Cover(i + 7));
            row.OwnerUri = Text(s, ownerUri);
            row.TrackCount = PlaylistTrackCount(i);
            return ref row;
        }

        /// <summary>P0-P6: the seven base playlists, each one playlist-page arm.</summary>
        static void StageCorePlaylists(Staging s, long now0)
        {
            // P0 — the account's own: long description with a link, public, a compact save count.
            ref var p0 = ref Restate(s, 0, PlaylistFields.Capabilities | PlaylistFields.Visibility | PlaylistFields.Saves, UserUri(0));
            p0.Description = Text(s, P0Description);
            p0.Caps = OwnerCaps;
            p0.PermissionRevision = Text(s, "5eed0000");
            p0.Flags = (uint)PlaylistFlags.Public;
            p0.FlagsMask = (uint)PlaylistFlags.Public;
            p0.Saves = SavesP0;

            // P1 — collaborative, owned by alex; the account may edit rows.
            ref var p1 = ref Restate(s, 1, PlaylistFields.Capabilities, UserUri(2));
            p1.Caps = (byte)(PlaylistCaps.CanView | PlaylistCaps.CanEditItems | PlaylistCaps.IsCollaborative);

            // P2 — a followed editorial list: no dates, no adders.
            ref var p2 = ref Restate(s, 2, PlaylistFields.Capabilities | PlaylistFields.Format, SpotifyUserUri);
            p2.Caps = (byte)PlaylistCaps.CanView;
            p2.Format = (byte)PlaylistFormat.Editorial;

            // P3 — the daylist: the rollover window and its payload accent.
            ref var p3 = ref Restate(s, 3, PlaylistFields.Capabilities | PlaylistFields.Format | PlaylistFields.Daylist | PlaylistFields.Accent, SpotifyUserUri);
            p3.Caps = (byte)PlaylistCaps.CanView;
            p3.Format = (byte)PlaylistFormat.Daylist;
            p3.DaylistExpiresAt = (int)(now0 + 4 * Hour + 37 * 60);
            p3.DaylistCreatedAt = (int)(now0 - 3 * Hour - 23 * 60);
            p3.Accent = DaylistAccent;

            // P4 — the chart: the caption facts (the deltas ride the membership).
            ref var p4 = ref Restate(s, 4, PlaylistFields.Capabilities | PlaylistFields.Format | PlaylistFields.Chart, SpotifyUserUri);
            p4.Caps = (byte)PlaylistCaps.CanView;
            p4.Format = (byte)PlaylistFormat.Chart;
            p4.ChartNewEntries = ChartNewEntries;
            p4.ChartUpdatedAt = (int)(now0 - ChartAgeSeconds);
            p4.ChartRankType = Text(s, "weekly");

            // P5 / P6 — the two tunable mixes (their options land through ApplyTuning below).
            ref var p5 = ref Restate(s, 5, PlaylistFields.Capabilities | PlaylistFields.Format, SpotifyUserUri);
            p5.Caps = (byte)PlaylistCaps.CanView;
            p5.Format = (byte)PlaylistFormat.DailyMix;
            ref var p6 = ref Restate(s, 6, PlaylistFields.Capabilities | PlaylistFields.Format, SpotifyUserUri);
            p6.Caps = (byte)PlaylistCaps.CanView;
            p6.Format = (byte)PlaylistFormat.InspiredByMix;
        }

        static int ExtraRows(int k) => k switch { 0 => 0, 1 => 12, 2 => 10, 3 => 8, 4 => 0, _ => 20 };

        /// <summary>X0-X5: the arms no base playlist reaches — reachable by route (<c>pl:spotify:playlist:plxN</c>), not in
        /// the sidebar tree (the base rootlist's seven items are a pinned count).</summary>
        static void StageExtras(Staging s)
        {
            for (int k = 0; k < ExtraCount; k++)
            {
                uint groups = (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities);
                if (k is 0 or 1 or 4 or 5) groups |= (uint)PlaylistFields.Visibility;
                ref var row = ref s.Playlists.RowFor(Text(s, ExtraUri(k)), Authority.Seed, groups);
                row.Title = Text(s, s_extraTitles[k]);
                row.TrackCount = ExtraRows(k);
                switch (k)
                {
                    case 0:   // X0 — owned, editable, EMPTY: the recommendations arm
                    case 4:   // X4 — owned, the create that failed
                        row.OwnerUri = Text(s, UserUri(0));
                        row.Caps = OwnerCaps;
                        break;
                    case 1:   // X1 — owned, Image KNOWN-EMPTY: the mosaic of its 12 rows' album covers
                        row.OwnerUri = Text(s, UserUri(0));
                        row.Caps = OwnerCaps;
                        break;
                    case 2:   // X2 — followed, deleted by its owner (a latching tombstone)
                        row.Image = Text(s, Cover(3));
                        row.OwnerUri = Text(s, UserUri(3));
                        row.Caps = (byte)PlaylistCaps.CanView;
                        row.Flags = (uint)PlaylistFlags.DeletedByOwner;
                        row.FlagsMask = (uint)PlaylistFlags.DeletedByOwner;
                        break;
                    case 3:   // X3 — capabilities KNOWN and CanView false: access revoked
                        row.Image = Text(s, Cover(5));
                        row.OwnerUri = Text(s, UserUri(1));
                        row.Caps = 0;
                        break;
                    default:  // X5 — owned, mixed tracks and episodes
                        row.Image = Text(s, Cover(6));
                        row.OwnerUri = Text(s, UserUri(0));
                        row.Caps = OwnerCaps;
                        break;
                }
                if ((groups & (uint)PlaylistFields.Visibility) != 0)
                {
                    row.Flags |= (uint)PlaylistFlags.Public;
                    row.FlagsMask |= (uint)PlaylistFlags.Public;
                }
            }

            // X5's three episodes, rendered as tracks (the Podcast bit; ch 04 DATA GAPS "Episodes inside a playlist").
            for (int k = 0; k < 3; k++)
            {
                ref var ep = ref s.Tracks.RowFor(Text(s, EpisodeRowUri(k)), Authority.Seed,
                    (uint)(TrackFields.Identity | TrackFields.Availability));
                ep.Title = Text(s, s_episodeTitles[k]);
                ep.ArtistLine = Text(s, "The Wavee Debrief");
                ep.Image = Text(s, Cover(k + 2));
                ep.DurationMs = (22 + (k * 13) % 50) * 60_000;
                ep.Flags = (uint)TrackFlags.Podcast;
            }
        }

        /// <summary>L0: the imported-files playlist and its fourteen local rows (ch 31 W11).</summary>
        static void StageLocal(Staging s)
        {
            ref var header = ref s.Playlists.RowFor(Text(s, global::Wavee.Playlist.LocalFilesUri), Authority.Seed,
                (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities));
            header.Title = Text(s, "Local Files");
            header.Description = Text(s, "Music imported from this computer.");
            header.OwnerUri = Text(s, LocalUserUri);
            header.TrackCount = LocalCount;
            header.Caps = OwnerCaps;

            for (int i = 0; i < LocalCount; i++)
            {
                ref var row = ref s.Tracks.RowFor(Text(s, LocalTrackUri(i)), Authority.Seed,
                    (uint)(TrackFields.Identity | TrackFields.Availability));
                row.Title = Text(s, s_localTitles[i]);
                row.DurationMs = 150_000 + (i * 41 % 140) * 1000;
                // Availability stated, not defaulted: a playable-only filter must not hide the local library (FakeData.cs:364).
                row.Flags = (uint)TrackFlags.Local;
            }
        }

        /// <summary>T0's Known half: the Tags group on every pool track (the runs land in <see cref="LandTags"/>).</summary>
        static void StageTrackKnowns(Staging s)
        {
            for (int i = 0; i < TrackCount; i++)
                s.Tracks.RowFor(Text(s, TrackUri(i)), Authority.Seed, (uint)TrackFields.Tags);
        }

        // ══ 2. RELATIONS ═══════════════════════════════════════════════════════════════════════════════════════════════

        static int SlotOf(EntityKind kind, string uri) => ResolveSeedSlot(kind, uri);

        /// <summary>A membership's 16-lowercase-hex item id, a pure function of (list, row). Interned without an AddRef —
        /// the same lifetime the staged PlaylistTracks commit gives an item id.</summary>
        static StringId ItemIdFor(int list, int row)
            => Intern(Utf8((0x5EED_0000_0000_0000UL | ((ulong)(uint)list << 20) | (uint)row).ToString("x16", CultureInfo.InvariantCulture)));

        /// <summary>Land a COMPLETE membership with its commit-time facts folded in one pass (Playlist.cs item 3).</summary>
        static void Land(global::Wavee.Playlist p, ReadOnlySpan<int> targets, ReadOnlySpan<PlaylistTrackEdge> edges)
        {
            var facts = Fold(targets, edges);
            p.ApplyMembership(targets, edges, in facts);
        }

        static PlaylistFacts Fold(ReadOnlySpan<int> targets, ReadOnlySpan<PlaylistTrackEdge> edges)
        {
            var facts = new PlaylistFacts();
            for (int i = 0; i < targets.Length; i++)
            {
                var t = new global::Wavee.Track(targets[i]);
                var e = i < edges.Length ? edges[i] : default;
                facts.Add(e.AddedBy, e.AddedAt, t.DurationMs, t.IsPodcast, t.HasVideo);
            }
            return facts;
        }

        /// <summary>P0 / P1 / P4 rewrite their base runs with the payload their arm needs; P2 / P3 / P5 / P6 keep their
        /// runs and gain the commit-time facts (the meta line's duration, the column-existence flags).</summary>
        static void LandCoreMemberships(long now0)
        {
            int me = Current.MeSlot;
            Span<int> targets = stackalloc int[64];
            Span<PlaylistTrackEdge> edges = stackalloc PlaylistTrackEdge[64];
            Span<int> adders = stackalloc int[4];
            for (int u = 0; u < adders.Length; u++) adders[u] = SlotOf(EntityKind.User, UserUri(u));

            for (int i = 0; i < PlaylistCount; i++)
            {
                var p = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, PlaylistUri(i)));
                if (!p.IsValid) continue;
                // Copied out first: a run is never held across its own rewrite (the Edges.cs span rule).
                int n = Math.Min(p.TrackSlots.Length, targets.Length);
                p.TrackSlots[..n].CopyTo(targets);

                switch (i)
                {
                    case 0:   // P0: item ids, a stamp every 7 hours, added by the account
                        for (int k = 0; k < n; k++)
                            edges[k] = new PlaylistTrackEdge(ItemIdFor(0, k), (int)(now0 - k * 7 * Hour), me, 0, 0, 0, 0);
                        Land(p, targets[..n], edges[..n]);
                        break;
                    case 1:   // P1: four adders cycling me / christos / alex / mia, a stamp on every row
                        for (int k = 0; k < n; k++)
                            edges[k] = new PlaylistTrackEdge(ItemIdFor(1, k), (int)(now0 - k * 26 * Hour), adders[Wrap(k, adders.Length)], 0, 0, 0, 0);
                        Land(p, targets[..n], edges[..n]);
                        break;
                    case 4:   // P4: NEW at row 1, UP 2-4, EQUAL 5-8, DOWN 9-12, EQUAL after
                        for (int k = 0; k < n; k++)
                        {
                            ushort pos = (ushort)(k + 1);
                            var (status, prev) = k switch
                            {
                                0 => (ChartNew, (ushort)0),
                                <= 3 => (ChartUp, (ushort)(pos + 2)),
                                <= 7 => (ChartEqual, pos),
                                <= 11 => (ChartDown, (ushort)(pos - 2)),
                                _ => (ChartEqual, pos),
                            };
                            edges[k] = new PlaylistTrackEdge(ItemIdFor(4, k), 0, Table.None, status, pos, prev, 0);
                        }
                        Land(p, targets[..n], edges[..n]);
                        break;
                    default:  // P2 / P3 / P5 / P6: the base run stands; fold its facts
                        var facts = Fold(targets[..n], p.TrackEdges[..Math.Min(n, p.TrackEdges.Length)]);
                        if (facts.Rows > 0) p.Apply(in facts);
                        break;
                }
            }
        }

        /// <summary>P5 untuned (three choices + a reset: the menu hides Reset), P6 tuned to its second choice (Reset + the
        /// "Current" check). Revision 0 = "no membership revision known" — the seed has no wire revision to bind to.</summary>
        static void LandTuning()
        {
            Span<TuningEdge> options = stackalloc TuningEdge[4];
            options[0] = new TuningEdge(Intern(Utf8(TuneDiscovery)), Intern(Utf8("More discovery tracks")), (byte)TuningOptionKind.Choice);
            options[1] = new TuningEdge(Intern(Utf8(TuneSoftPop)), Intern(Utf8("Make it more soft pop")), (byte)TuningOptionKind.Choice);
            options[2] = new TuningEdge(Intern(Utf8(TuneEnergy)), Intern(Utf8("More energy")), (byte)TuningOptionKind.Choice);
            options[3] = new TuningEdge(Intern(Utf8(TuneReset)), StringId.Empty, (byte)TuningOptionKind.Reset);

            var p5 = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, PlaylistUri(5)));
            if (p5.IsValid) p5.ApplyTuning(options, StringId.Empty, 0u);
            var p6 = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, PlaylistUri(6)));
            if (p6.IsValid) p6.ApplyTuning(options, options[1].Identifier, 0u);
        }

        static void LandExtras(long now0)
        {
            int me = Current.MeSlot;
            Span<int> targets = stackalloc int[24];
            Span<PlaylistTrackEdge> edges = stackalloc PlaylistTrackEdge[24];

            // X0 — Complete-EMPTY and 24 recommendations from the pool.
            var x0 = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, ExtraUri(0)));
            if (x0.IsValid)
            {
                Land(x0, default, default);
                for (int k = 0; k < RecommendationCount; k++) targets[k] = SlotOf(EntityKind.Track, TrackUri(100 + k));
                x0.ApplyRecommendations(targets[..RecommendationCount]);
            }

            // X1 — twelve pool tracks from twelve distinct albums behind a known-empty cover. Picked by their committed
            // album, not by consecutive index: the album seed (owner M) lays tracks in album RUNS, so neighbours share one.
            var x1 = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, ExtraUri(1)));
            if (x1.IsValid)
            {
                Span<int> albumsSeen = stackalloc int[12];
                int picked = 0;
                for (int i = 0; picked < 12 && i < TrackCount; i++)
                {
                    int slot = SlotOf(EntityKind.Track, TrackUri(60 + i));
                    int album = slot > 0 ? new global::Wavee.Track(slot).Album.Slot : 0;
                    if (album <= 0 || albumsSeen[..picked].Contains(album)) continue;
                    albumsSeen[picked] = album;
                    targets[picked] = slot;
                    edges[picked] = new PlaylistTrackEdge(ItemIdFor(101, picked), (int)(now0 - picked * 3 * Hour), me, 0, 0, 0, 0);
                    picked++;
                }
                Land(x1, targets[..picked], edges[..picked]);
            }

            // X2 — ten rows and the Deleted notice; X3 — eight rows and AccessRevoked.
            LandPlain(ExtraUri(2), 80, 10, DetailNotice.Deleted, targets, edges);
            LandPlain(ExtraUri(3), 90, 8, DetailNotice.AccessRevoked, targets, edges);

            // X4 — Complete-empty, and the optimistic create the server refused (terminal).
            var x4 = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, ExtraUri(4)));
            if (x4.IsValid)
            {
                Land(x4, default, default);
                x4.SettleCreate(false);
            }

            // X5 — twenty rows, three of them episodes (rows 5, 12 and 18), a stamp on every row.
            var x5 = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, ExtraUri(5)));
            if (x5.IsValid)
            {
                int pool = 120, episode = 0;
                for (int k = 0; k < 20; k++)
                {
                    targets[k] = k is 4 or 11 or 17
                        ? SlotOf(EntityKind.Track, EpisodeRowUri(episode++))
                        : SlotOf(EntityKind.Track, TrackUri(pool++));
                    edges[k] = new PlaylistTrackEdge(ItemIdFor(105, k), (int)(now0 - k * 5 * Hour), me, 0, 0, 0, 0);
                }
                Land(x5, targets[..20], edges[..20]);
            }
        }

        static void LandPlain(string uri, int poolStart, int count, DetailNotice notice, Span<int> targets, Span<PlaylistTrackEdge> edges)
        {
            var p = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, uri));
            if (!p.IsValid) return;
            for (int k = 0; k < count; k++)
            {
                targets[k] = SlotOf(EntityKind.Track, TrackUri(poolStart + k));
                edges[k] = default;
            }
            Land(p, targets[..count], edges[..count]);
            p.SetNotice(notice);
        }

        /// <summary>L0's membership: the fourteen local rows, Complete (there is no remote to ask).</summary>
        static void LandLocal()
        {
            var p = new global::Wavee.Playlist(SlotOf(EntityKind.Playlist, global::Wavee.Playlist.LocalFilesUri));
            if (!p.IsValid) return;
            Span<int> targets = stackalloc int[LocalCount];
            Span<PlaylistTrackEdge> edges = stackalloc PlaylistTrackEdge[LocalCount];
            for (int i = 0; i < LocalCount; i++)
            {
                targets[i] = SlotOf(EntityKind.Track, LocalTrackUri(i));
                edges[i] = default;
            }
            Land(p, targets, edges);
        }

        /// <summary>T0's runs. The payload IS the tag id (display name, [0] = primary); targets are unused.</summary>
        static void LandTags()
        {
            var relation = Current.Edges.TrackTags;
            Span<int> zeros = stackalloc int[2];
            Span<StringId> tags = stackalloc StringId[2];
            StringId second = Intern(Utf8(SecondDescriptor));
            for (int i = 0; i < TrackCount; i++)
            {
                int slot = SlotOf(EntityKind.Track, TrackUri(i));
                if (slot == Table.None) continue;
                bool carriesSecond = Wrap(i, 3) == 0;
                if (relation.State(slot) == EdgeState.Unknown)
                {
                    tags[0] = Intern(Utf8(Descriptors[Wrap(i, Descriptors.Length)]));
                    int n = 1;
                    if (carriesSecond) tags[n++] = second;
                    relation.Replace(slot, zeros[..n], tags[..n], EdgeState.Complete, n);
                    continue;
                }
                // Another seed answered first: extend a one-descriptor run, never replace a primary.
                var existing = relation.Payload(slot);
                if (!carriesSecond || existing.Length != 1 || existing[0] == second) continue;
                tags[0] = existing[0];
                tags[1] = second;
                relation.Replace(slot, zeros, tags, EdgeState.Complete, 2);
            }
        }

        /// <summary>T1: the credited artist (the base seed's credit line) and, on 1-in-4 rows, a second one — ONLY for a
        /// pool track whose credit run nobody has answered.</summary>
        static void LandCredits()
        {
            var relation = Current.Edges.TrackArtists;
            Span<int> targets = stackalloc int[2];
            for (int i = 0; i < TrackCount; i++)
            {
                int slot = SlotOf(EntityKind.Track, TrackUri(i));
                if (slot == Table.None || relation.State(slot) != EdgeState.Unknown) continue;
                int first = Wrap(i, ArtistCount);
                targets[0] = SlotOf(EntityKind.Artist, ArtistUri(first));
                int n = 1;
                if (Wrap(i, 4) == 0)
                {
                    int other = Wrap(i * 5 + 3, ArtistCount);
                    if (other == first) other = Wrap(other + 1, ArtistCount);
                    targets[n++] = SlotOf(EntityKind.Artist, ArtistUri(other));
                }
                relation.ReplaceRun(slot, targets[..n], default);
            }
        }

        /// <summary>C0: the account's curated chip set, whole, with its Known bit.</summary>
        static void LandContentFilters()
        {
            var me = new global::Wavee.User(Current.MeSlot);
            if (me.IsValid) me.SetContentFilters(ChipTitles, ChipTokens);
        }
    }
}
