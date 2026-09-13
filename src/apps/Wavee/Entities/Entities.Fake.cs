// ── Entities/Entities.Fake.cs ──────────────────────────────────────────────────────────────────────────────────────
// Entities.SeedFake() — SEED-CORE first cut (decision D17, gap G-015/G-181); ch 31 is its specification.
//
// Role: CORE
// Owner: Q (orchestrator first cut per D17 — "land the seed-core now, other probe arms stay S's")
// Wave: 5
// Budget: 1100 lines (this cut: SEED-CORE only — a rootlist with folders, the library's four sets, pins, 166 liked
//   tracks, 16 covers, everything the gap register's G-015 fix spec names verbatim). SEED-SURFACES (home, search,
//   browse, concerts, lyrics, video, the clock-anchored fixtures of ch 31 §7.3 W16 …) is later work and is not here.
// Spec: docs/plans/wavee/wavee-0.3-ui/31-fake-data.md — §0 non-negotiables, §7.2 determinism rule, §7.5 shape,
//   §8 pure rules, DATA GAPS 2/3/11 (already closed on disk: Authority.Seed, Table.AllocRun, EdgeTable.ReplaceRun,
//   Scope.PublishAll — see the "ALREADY LANDED" note below).
//
// THE ONE RULE (ch 31 §0.1): SeedFake writes staging columns and edges through the SAME commit path
// Spotify.Decode writes through, and then does nothing else. No `if (fake)` branch belongs in Entities/, Shell/ or a
// page — a surface never asks "am I fake". Every fixture is addressed by an ordinary `spotify:*` uri
// (`EntityUri.Parse` decides provider/kind exactly as it would for a live one; ch 31 §1.2 rule 2), and every
// generator here is a pure function of an index and `now0` — no `Random`, no `DateTime.Now`, no `string.GetHashCode`
// (ch 31 §7.2 rule A), and `Wrap` makes every index total for any `int` (rule B).
//
// ALREADY LANDED (so this file does not have to ask for them): `Authority.Seed` (Entities.cs, one rung below Wire),
// `Table.AllocRun` and `EdgeTable<T>.ReplaceRun`/`Replace` (both call out "the seed's … call" in their own doc
// comments), and `Scope.PublishAll()` ("ch 31 calls it at the end of the seed"). This file uses `ReplaceRun`/
// `Replace` directly for every relation below rather than the staged-edge `Relation` enum: `Relation.Pins` does not
// exist yet (`Edges.Staging.cs`, B1) and `User.Replace`/`User.ReplaceRootlist` are the sanctioned whole-relation
// write already used by the optimistic path — the same `EdgeTable.Replace` a live library-sync answer would call
// (D11). Rows (Tracks/Albums/Artists/Playlists/Shows/Users) still go through `Staging` + `Entities.Commit` exactly
// as a decoder would, so `SetText`'s ref-counting (defect 1) is never bypassed for row text.
//
// SCOPE OF THIS CUT (report: the rest is ch 31's SEED-SURFACES, deliberately not attempted here):
//   • Rootlist: 7 named playlists + a folder inside a folder (ch 31 §2 W13's shape).
//   • The library's four sets: SavedAlbums (13), FollowedArtists (12), SavedShows (8), Pins (5, playlists only —
//     `LibraryEdge` carries no kind bit yet, User.cs:227-234's own note; pinning only one kind sidesteps it).
//   • Liked: 166 tracks, AddedAt spread over ~332 days (ch 31 §2 W16's formula, so the week-spark/since/rediscover
//     surfaces have evidence once they exist).
//   • 16 bundled covers, interned once.
//   • AlbumTracks / PlaylistTracks memberships, so the album and playlist detail frames have rows to scroll.
// NOT in this cut (ch 31 §7.1's SEED-SURFACES rows: home, search, browse, concerts, recents, lyrics, video, the
// clock-anchored countdown/chart/tour fixtures of §7.3, `Sample`'s full W13 index set beyond what the customizer
// asks for today). `Clock.SeedEpoch` (Platform.cs) is still the one time input; nothing here reads a second clock.

using FluentGpu.Foundation;

namespace Wavee;

public static partial class Entities
{
    // ── fixture constants (ch 31 §3.1) ──────────────────────────────────────────────────────────────────────────────

    const int CoverCount = 16;
    const int TrackCount = 166;                 // ch 31 §8 assertion 5: 166 liked, pinned literal
    const int AlbumCount = 13;
    const int ArtistCount = 12;
    const int PlaylistCount = 7;                 // "7 named playlists" (§8 assertion 5)
    const int ShowCount = 8;

    static readonly string[] s_titles =
    [
        "Midnight Drive", "Iced Americano", "Dalkom Cafe", "Nostalgia 2000s Mix", "Strobe", "Weird Fishes",
        "Late Bloom", "Rewind", "Paper Planes (Wavee Mix)", "Slow Static", "Afterglow", "Windows Down",
        "Quiet Hours", "Neon Rain", "Better Days", "Halfway Home",
    ];

    static readonly string[] s_artistNames =
    [
        "Christos", "Alex Rivers", "Mia Solace", "The Wavee Collective", "Nova Kite", "Sable & Sun",
        "Half Moon Radio", "Kimmuseum", "Dry Season", "The Longtime", "Iris Overdrive", "Coral Hours",
    ];

    static readonly string[] s_playlistNames =
    [
        "mellow pop wistful saturday", "My Playlist #6", "우울해", "Late Night", "Iced Americano",
        "Nostalgia 2000s Mix", "Henry Moodie Mix",
    ];

    static readonly string[] s_folderNames = ["Cafe & chill", "Late night"];

    static readonly string[] s_showNames =
    [
        "The Wavee Debrief", "Coffee & Code", "Half Moon Radio Hour", "Slow Static Sessions",
        "The Longtime Podcast", "Neon Rain Diaries", "Quiet Hours", "Afterglow Weekly",
    ];

    // Twelve Camelot-wheel accent colours, ARGB, value-for-value a plausible swatch ladder (ch 31 §8's
    // `CamelotColors`; this cut does not port 0.2.9's literal table, only its shape).
    static readonly uint[] s_camelotColors =
    [
        0xFFE05252, 0xFFE08A52, 0xFFE0C452, 0xFFC4E052, 0xFF8AE052, 0xFF52E06B,
        0xFF52E0B0, 0xFF52C4E0, 0xFF528AE0, 0xFF6B52E0, 0xFFB052E0, 0xFFE052C4,
    ];

    /// <summary>Totality for any <c>int</c> (ch 31 §7.2 rule B): a uri-derived index can be huge or negative, and every
    /// table lookup below wraps through this rather than trusting its caller.</summary>
    public static int Wrap(int i, int n) => n <= 0 ? 0 : ((i % n) + n) % n;

    /// <summary>One of the 16 bundled covers, as an ABSOLUTE path next to the exe (the same convention
    /// <see cref="WaveeFonts"/>/<see cref="WaveeIcons"/> use) — so a caller never has to guess the process's working
    /// directory. Interning a path is not I/O (ch 31 §1.2's "no I/O except the bundled asset paths it interns").</summary>
    static string Cover(int i)
        => Path.Combine(AppContext.BaseDirectory, "assets", "covers", "cover" + Wrap(i, CoverCount).ToString("D2") + ".jpg");

    // ── the seed ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The demo catalog, as columns. Called once, from the app's boot sequence, only when
    /// <c>Platform.Args.Fake</c> — see App.cs's exact call site in the batch report; this file does not call itself.
    /// Every value is a pure function of (its fixture index, <paramref name="now0"/>): the same input produces the
    /// same tables on every launch and every process (ch 31 §7.2).</summary>
    public static void SeedFake(long now0)
    {
        var staging = Staging.Rent();
        try
        {
            SeedRows(staging, now0);
            Commit(staging);
        }
        finally
        {
            Staging.Return(staging);
        }

        // Resolve the account's own row and wire it as MeSlot BEFORE any library relation is written: every relation
        // below hangs off Users[me] (User.cs's "the library is edges", G6). `Entities.Current.MeSlot` is a plain
        // public field (Entities.cs) — no B1 hook is needed to set it from here.
        int meSlot = ResolveSeedSlot(EntityKind.User, FakeAccount);
        Current.MeSlot = meSlot;
        if (meSlot != Table.None)
        {
            var me = new User(meSlot);
            SeedLibrary(me, now0);
            SeedRootlist(me, now0);
        }

        SeedMemberships();

        // ONE publication (ch 31 §7.5 step 5 / §1.2): every table this seed touched bumps its Changed signal exactly
        // once, here — never per row and never per relation.
        Current.PublishAll();
    }

    /// <summary>THE fake account's raw identifier — deliberately NOT a <c>spotify:user:</c> uri. <c>Entities.cs</c>'s
    /// own <c>ResolveMe</c> resolves <c>Scope.MeSlot</c> from <c>scope.Key.Account</c> treated as literal row TEXT
    /// (<c>scope.Users.Slot(scope.Key.Account.AsSpan())</c>), so the account's own row must be staged under exactly
    /// this bare string for that lookup to ever find it — the same convention a real login's username takes.
    /// <c>Platform.cs</c>'s <c>--fake</c> arm must set <c>CatalogScope.Account</c> to this same literal (reported).</summary>
    public const string FakeAccount = "wavee-listener";

    static string UserUri(int i) => i switch
    {
        0 => FakeAccount,
        1 => "spotify:user:christos",
        2 => "spotify:user:alex-rivers",
        _ => "spotify:user:mia-solace",
    };

    static string TrackUri(int i) => "spotify:track:tr" + Wrap(i, TrackCount);
    static string AlbumUri(int i) => "spotify:album:al" + Wrap(i, AlbumCount);
    static string ArtistUri(int i) => "spotify:artist:ar" + Wrap(i, ArtistCount);
    static string PlaylistUri(int i) => "spotify:playlist:pl" + Wrap(i, PlaylistCount);
    static string ShowUri(int i) => "spotify:show:sh" + Wrap(i, ShowCount);

    /// <summary>The 6-cycle every fake album's release shape follows (ch 31 §2 W4): every artist therefore owns one
    /// album of each of the four <see cref="AlbumKind"/>s across its six slots.</summary>
    static (AlbumKind Kind, int Tracks) AlbumShape(int i) => Wrap(i, 6) switch
    {
        0 => (AlbumKind.Single, 1),
        1 => (AlbumKind.EP, 5),
        2 => (AlbumKind.Album, 12),
        3 => (AlbumKind.Single, 2),
        4 => (AlbumKind.Compilation, 18),
        _ => (AlbumKind.Album, 10),
    };

    static void SeedRows(Staging s, long now0)
    {
        // ── users: me + three owners ────────────────────────────────────────────────────────────────────────────────
        for (int i = 0; i < 4; i++)
        {
            ref var row = ref s.Users.RowFor(s.AddText(Utf8(UserUri(i))), Authority.Seed, (uint)UserFields.Identity);
            row.Name = s.AddText(Utf8(i == 0 ? "Wavee Listener" : s_artistNames[i - 1]));
            row.Image = s.AddText(Utf8(Cover(i)));
        }

        // ── tracks: the 166-row liked pool, reused (via Wrap) as album and playlist membership ─────────────────────
        for (int i = 0; i < TrackCount; i++)
        {
            ref var row = ref s.Tracks.RowFor(s.AddText(Utf8(TrackUri(i))), Authority.Seed,
                (uint)(TrackFields.Identity | TrackFields.PlayCount | TrackFields.Year | TrackFields.Availability
                     | TrackFields.Audio));
            row.Title = s.AddText(Utf8(s_titles[Wrap(i, s_titles.Length)]));
            row.ArtistLine = s.AddText(Utf8(s_artistNames[Wrap(i, s_artistNames.Length)]));
            row.Image = s.AddText(Utf8(Cover(i)));
            row.AlbumUri = s.AddText(Utf8(AlbumUri(i % AlbumCount)));
            row.DurationMs = 138_000 + (i * 37 % 150) * 1000;
            row.Year = (ushort)(2008 + i % 17);
            row.Tempo = (ushort)((70 + i % 90) * 10);
            row.Camelot = (byte)(1 + Wrap(i, 12));
            row.CamelotColor = s_camelotColors[Wrap(i, 12)];
            row.PlayCount = (uint)(50_000 + i * 977 % 2_000_000);
            row.Flags = i % 6 == 0 ? (uint)TrackFlags.Explicit : 0;
        }

        // ── albums: the four AlbumKinds, cycling every six ─────────────────────────────────────────────────────────
        for (int i = 0; i < AlbumCount; i++)
        {
            var shape = AlbumShape(i);
            ref var row = ref s.Albums.RowFor(s.AddText(Utf8(AlbumUri(i))), Authority.Seed, (uint)AlbumFields.Identity);
            row.Title = s.AddText(Utf8(s_titles[Wrap(i, s_titles.Length)]));
            row.Image = s.AddText(Utf8(Cover(i)));
            row.Year = (ushort)(2008 + i % 17);
            row.TrackCount = shape.Tracks;
            row.Kind = (byte)shape.Kind;
        }

        // ── artists: identity only (name, avatar) — enough for FollowedArtists and a credit line ──────────────────
        for (int i = 0; i < ArtistCount; i++)
        {
            ref var row = ref s.Artists.RowFor(s.AddText(Utf8(ArtistUri(i))), Authority.Seed, (uint)ArtistFields.Identity);
            row.Name = s.AddText(Utf8(s_artistNames[i]));
            row.Image = s.AddText(Utf8(Cover(i + 3)));
        }

        // ── playlists: the rootlist's seven named rows ─────────────────────────────────────────────────────────────
        for (int i = 0; i < PlaylistCount; i++)
        {
            ref var row = ref s.Playlists.RowFor(s.AddText(Utf8(PlaylistUri(i))), Authority.Seed,
                (uint)(PlaylistFields.Identity | PlaylistFields.Capabilities));
            row.Title = s.AddText(Utf8(s_playlistNames[i]));
            row.Image = s.AddText(Utf8(Cover(i + 7)));
            int ownerIndex = i == 0 ? 0 : 1 + Wrap(i, 3);           // the first row is the account's own playlist
            row.OwnerUri = s.AddText(Utf8(UserUri(ownerIndex)));
            row.TrackCount = PlaylistTrackCount(i);
            row.Caps = (byte)(ownerIndex == 0 ? (PlaylistCaps.CanView | PlaylistCaps.CanEditItems
                                                | PlaylistCaps.CanEditMetadata | PlaylistCaps.IsOwner)
                                              : PlaylistCaps.CanView);
        }

        // ── shows: identity only, backing SavedShows ───────────────────────────────────────────────────────────────
        for (int i = 0; i < ShowCount; i++)
        {
            ref var row = ref s.Shows.RowFor(s.AddText(Utf8(ShowUri(i))), Authority.Seed, (uint)ShowFields.Identity);
            row.Title = s.AddText(Utf8(s_showNames[i]));
            row.Image = s.AddText(Utf8(Cover(i + 2)));
            row.Publisher = s.AddText(Utf8("Wavee Podcasts"));
        }
    }

    static int PlaylistTrackCount(int i) => i switch
    {
        0 => 50, 1 => 4, 2 => 22, 3 => 50, 4 => 15, 5 => 30, _ => 50,
    };

    static void SeedMemberships()
    {
        Span<int> targets = stackalloc int[64];
        Span<AlbumTrackEdge> albumPayload = stackalloc AlbumTrackEdge[64];
        Span<PlaylistTrackEdge> playlistPayload = stackalloc PlaylistTrackEdge[64];

        // AlbumTracks: exactly the row count each album's AlbumShape declared, so the header's track count and the
        // tracklist's row count always agree (ch 31 §0.8's "two fixtures must agree with each other" rule).
        for (int a = 0; a < AlbumCount; a++)
        {
            int count = Math.Min(AlbumShape(a).Tracks, targets.Length);
            int start = Wrap(a * 13, TrackCount);
            for (int k = 0; k < count; k++)
            {
                int t = Wrap(start + k, TrackCount);
                targets[k] = ResolveSeedSlot(EntityKind.Track, TrackUri(t));
                albumPayload[k] = new AlbumTrackEdge(1, (ushort)(k + 1));
            }
            int albumSlot = ResolveSeedSlot(EntityKind.Album, AlbumUri(a));
            if (albumSlot == Table.None) continue;
            Current.Edges.AlbumTracks.ReplaceRun(albumSlot, targets[..count], albumPayload[..count]);
        }

        // PlaylistTracks: a deterministic, wrapping slice of the 166-track pool per playlist.
        for (int p = 0; p < PlaylistCount; p++)
        {
            int count = Math.Min(PlaylistTrackCount(p), targets.Length);
            int start = Wrap(p * 23, TrackCount);
            for (int k = 0; k < count; k++)
            {
                int t = Wrap(start + k, TrackCount);
                targets[k] = ResolveSeedSlot(EntityKind.Track, TrackUri(t));
                playlistPayload[k] = new PlaylistTrackEdge(StringId.Empty, 0, Table.None, 0, 0, 0, 0);
            }
            int playlistSlot = ResolveSeedSlot(EntityKind.Playlist, PlaylistUri(p));
            if (playlistSlot == Table.None) continue;
            Current.Edges.PlaylistTracks.ReplaceRun(playlistSlot, targets[..count], playlistPayload[..count]);
        }
    }

    static void SeedLibrary(User me, long now0)
    {
        // Liked: 166 tracks, newest first, AddedAt spread over ~332 days (ch 31 §2 W16's formula) so the week-spark
        // and "since" surfaces have real evidence once they exist.
        Span<int> likedSlots = new int[TrackCount];
        Span<LibraryEdge> likedPayload = new LibraryEdge[TrackCount];
        for (int i = 0; i < TrackCount; i++)
        {
            likedSlots[i] = ResolveSeedSlot(EntityKind.Track, TrackUri(i));
            long addedAt = now0 - (i * 2L * 86_400 + i % 5 * 86_400);
            likedPayload[i] = new LibraryEdge((int)addedAt, 0);
        }
        me.Replace(LibraryEdgeKind.Liked, likedSlots, likedPayload);

        // SavedAlbums (13), FollowedArtists (12), SavedShows (8) — the library's other three sets.
        Span<int> albumSlots = new int[AlbumCount];
        Span<LibraryEdge> albumPayload = new LibraryEdge[AlbumCount];
        for (int i = 0; i < AlbumCount; i++)
        {
            albumSlots[i] = ResolveSeedSlot(EntityKind.Album, AlbumUri(i));
            albumPayload[i] = new LibraryEdge((int)(now0 - i * 3L * 86_400), 0);
        }
        me.Replace(LibraryEdgeKind.SavedAlbums, albumSlots, albumPayload);

        Span<int> artistSlots = new int[ArtistCount];
        Span<LibraryEdge> artistPayload = new LibraryEdge[ArtistCount];
        for (int i = 0; i < ArtistCount; i++)
        {
            artistSlots[i] = ResolveSeedSlot(EntityKind.Artist, ArtistUri(i));
            artistPayload[i] = new LibraryEdge((int)(now0 - i * 5L * 86_400), 0);
        }
        me.Replace(LibraryEdgeKind.FollowedArtists, artistSlots, artistPayload);

        Span<int> showSlots = new int[ShowCount];
        Span<LibraryEdge> showPayload = new LibraryEdge[ShowCount];
        for (int i = 0; i < ShowCount; i++)
        {
            showSlots[i] = ResolveSeedSlot(EntityKind.Show, ShowUri(i));
            showPayload[i] = new LibraryEdge((int)(now0 - i * 7L * 86_400), 0);
        }
        me.Replace(LibraryEdgeKind.SavedShows, showSlots, showPayload);

        // Pins (5): playlists only. `LibraryEdge` carries no kind bit (User.cs:227-234's own note — a pin's target
        // slot is ambiguous across tables until that lands), so this cut pins one kind rather than guess a shape a
        // later batch would have to unpick.
        Span<int> pinTargets = stackalloc int[5];
        Span<LibraryEdge> pinPayload = stackalloc LibraryEdge[5];
        int[] pinnedPlaylists = [0, 1, 2, 4, 6];
        for (int i = 0; i < pinnedPlaylists.Length; i++)
        {
            pinTargets[i] = ResolveSeedSlot(EntityKind.Playlist, PlaylistUri(pinnedPlaylists[i]));
            pinPayload[i] = new LibraryEdge((int)(now0 - i * 86_400), 0);
        }
        me.Replace(LibraryEdgeKind.Pins, pinTargets, pinPayload);
    }

    /// <summary>The rootlist: seven playlists with a folder inside a folder (ch 31 §2 W13's shape) —
    /// <c>pl0, pl1, [Cafe &amp; chill: pl2, [Late night: pl3], pl4], pl5, pl6</c>.</summary>
    static void SeedRootlist(User me, long now0)
    {
        // Arrays, not stackalloc spans: the local functions below capture them, and a ref local cannot be captured.
        // The seed runs once per fake boot, so the two small arrays are not on any hot path.
        var targets = new int[11];
        var payload = new RootlistEdge[11];
        int n = 0;

        Item(0, 0);
        Item(1, 0);
        FolderStart(s_folderNames[0], 0);
        Item(2, 1);
        FolderStart(s_folderNames[1], 1);
        Item(3, 2);
        FolderEnd(1);
        Item(4, 1);
        FolderEnd(0);
        Item(5, 0);
        Item(6, 0);

        me.ReplaceRootlist(targets.AsSpan(0, n), payload.AsSpan(0, n));

        void Item(int playlistIndex, int depth)
        {
            targets[n] = ResolveSeedSlot(EntityKind.Playlist, PlaylistUri(playlistIndex));
            payload[n] = new RootlistEdge((ushort)n, (byte)depth, (byte)RootlistKind.Item, StringId.Empty,
                (int)(now0 - playlistIndex * 86_400));
            n++;
        }

        void FolderStart(string name, int depth)
        {
            StringId folderName = default;
            RetainText(ref folderName, Intern(Utf8(name)));
            targets[n] = Table.None;
            payload[n] = new RootlistEdge((ushort)n, (byte)depth, (byte)RootlistKind.FolderStart, folderName, 0);
            n++;
        }

        void FolderEnd(int depth)
        {
            targets[n] = Table.None;
            payload[n] = new RootlistEdge((ushort)n, (byte)depth, (byte)RootlistKind.FolderEnd, StringId.Empty, 0);
            n++;
        }
    }

    static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    /// <summary>Resolve an already-committed uri to its slot — the same factories <see cref="Track(EntityUri)"/> etc.
    /// use for a deep link or a route, kept in one place so every membership builder above spells the resolve the
    /// same way. Allocates nothing new: the row was already committed by <see cref="SeedRows"/> under this exact
    /// uri text, so the factory's own "allocate if unseen" path never fires here.</summary>
    static int ResolveSeedSlot(EntityKind kind, string uri)
    {
        var id = EntityUri.Parse(uri.AsSpan());
        return kind switch
        {
            EntityKind.Track => Track(id).Slot,
            EntityKind.Album => Album(id).Slot,
            EntityKind.Artist => Artist(id).Slot,
            EntityKind.Playlist => Playlist(id).Slot,
            EntityKind.Show => Show(id).Slot,
            EntityKind.User => User(id).Slot,
            _ => Table.None,
        };
    }

    // ── the customizer's sample (ch 31 §7.2 rules C/D; G-181) ───────────────────────────────────────────────────────

    /// <summary>One miniature fixture: what <see cref="Sidebar.MiniaturePlaylist"/>/<see cref="Sidebar.MiniatureArtist"/>
    /// read to preview a TEMPLATE, not this account. Pure over the seed's own generator arithmetic — it never reads
    /// <c>Entities.Current</c> (ch 31 §7.2 rule D), so it answers identically whether or not <see cref="SeedFake"/> has
    /// ever run: on the real backend it still previews the same template the fake one renders for real (rule C: the
    /// customizer's nine hand-indexed call sites — playlists 1, 2, 5, 7, 8, 10, 12, 14 and artist 3 — must always name
    /// the same fixture).</summary>
    public static FakeSample Sample(EntityKind kind, int index) => kind switch
    {
        EntityKind.Artist => new FakeSample(
            InternNoRetain(s_artistNames[Wrap(index, s_artistNames.Length)]),
            InternNoRetain(Cover(index + 3)), 0, false),
        _ => new FakeSample(
            InternNoRetain(s_playlistNames[Wrap(index, s_playlistNames.Length)]),
            InternNoRetain(Cover(index + 7)), PlaylistTrackCount(Wrap(index, PlaylistCount)), false),
    };

    /// <summary>A read-only preview string. Sample is read far more than it changes and every string here is one of the
    /// small closed vocabularies above, so it is fine for two calls with the same index to intern the same text twice
    /// (the interner de-duplicates) — nothing here RETAINS the id into a column, so nothing has to release it either.</summary>
    static StringId InternNoRetain(string s) => Intern(Utf8(s));

    /// <summary>The shortcut badges' TEMPLATE counts (ch 31 §2 W13's "shortcut badges → LibraryStats()"), keyed by the
    /// same route key the sidebar's own shortcut items carry ("liked"/"albums"/"artists"/"podcasts"). Deliberately
    /// consistent with this file's seeded counts (166/13/12/8) rather than porting 0.2.9's mismatched 7-vs-8 stat
    /// (ch 31 §0.10c — the gap register calls that a defect to fix, not a fixture to keep) — a template preview and
    /// the seeded reality should never quietly disagree about a count nothing else distinguishes. Pure: like
    /// <see cref="Sample"/>, it never reads <c>Entities.Current</c> (rule D), so it answers the same on every
    /// backend. <c>null</c> for a key this cut has no badge fixture for.</summary>
    public static int? SampleShortcutCount(string routeKey) => routeKey switch
    {
        "liked" => TrackCount,
        "albums" => AlbumCount,
        "artists" => ArtistCount,
        "podcasts" => ShowCount,
        _ => null,
    };
}

/// <summary>One sample of the fake catalog's template — what the sidebar customizer's miniature previews.
/// <see cref="Count"/> is a track/member count where the kind has one (a playlist); <see cref="Circular"/> is
/// reserved for a future artist-avatar-in-a-ring preview and is always false in this cut.</summary>
public readonly record struct FakeSample(StringId Title, StringId Image, int Count, bool Circular);
