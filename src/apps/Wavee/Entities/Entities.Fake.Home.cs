// ── Entities/Entities.Fake.Home.cs ─────────────────────────────────────────────────────────────────────────────────
// SEED-SURFACES, owner P: the --fake fixtures behind Home (+ its facets, the Charts deck and the podium), Search,
// Browse and Recents (ch 31 §7.1 rows 34-42 and 45; WP-5.P contract §8)
//
// Role: CORE
// Owner: P
// Wave: 5
// Budget: 700 lines (UNVERIFIED — ch 31 §9.4 budgets SEED-SURFACES 500 for every owner together; this is owner P's
//   share of four routes' fixtures, reported)
// Spec: docs/plans/wavee/wavee-0.3-ui/31-fake-data.md §0 (rules 1-3, 8, 9, 13), §2 W1/W9/W14/W16, §7.1, §7.2, §7.3;
//   the WP-5.P contract §8 (what each route demands and what this file commits)
//
// THE SAME RULE AS Entities.Fake.cs (ch 31 §0.1). Everything below goes through the path a live answer takes: the
// Home document is the bundled capture `assets/spotify/home.json` decoded by the SAME fold the pathfinder provider
// calls (`Spotify.Decode.HomeFeed`, ch 31 §9.5(2) — "the offline fixture path"), and every synthetic row is staged and
// committed through `Entities.Commit`. Relations with no staged arm (a facet row's section list, the podium's ranking,
// a query's hits) are written with the table's own whole-relation write — the sanctioned shape Entities.Fake.cs already
// uses — never with a page-side shortcut. No page asks "am I fake"; this file never runs outside `SeedFake`.
//
// PURE IN (index, now0) (ch 31 §7.2). Every dated value is `now0` ± a constant (§2 W16's table), every fixture is a
// function of its index through `Wrap`, and nothing here reads a clock, a `Random` or `string.GetHashCode`. The one
// input that is not an argument is the bundled capture, which is a fixture file, not state.
//
// WHAT --fake CAN AND CANNOT SHOW HERE (reported, not faked): a search for any query other than "a" and a "Show all"
// page past what is committed stay unanswered — --fake registers no provider for synthetic subjects (App.cs), which is
// ch 31 §7.4 E's "the ranking is the server's" and the chapter's own boundary. Home's what's-new timeline is not seeded:
// `Notify.Rebuild` escalates to OS toasts, and a demo launch must not raise Windows banners.

using FluentGpu.Foundation;

namespace Wavee;

public static partial class Entities
{
    // ── fixture constants ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The playlists the Charts deck, the browse shelves and the search facet reach for, beyond the core seed's
    /// seven: editorial rows owned by <see cref="SpotifyOwnerUri"/>, the first ten of them charts.</summary>
    const int CatalogueCount = 40;

    const string SpotifyOwnerUri = "spotify:user:spotify";

    /// <summary>The one daylist card the bundled Home capture carries (ch 31 §2 W1): the seed pins its window, which the
    /// capture lacks, so the hero's flip clock has a target (W16: now0 + 4 h 37 m, created now0 − 3 h 23 m).</summary>
    const string DaylistUri = "spotify:playlist:37i9dQZF1EP6YuccBxUcC1";

    /// <summary>A show shelf the capture does not have, so the landing's podcast module and the Podcasts facet render
    /// (ch 11 W27).</summary>
    const string PodcastSectionUri = "spotify:section:wavee-seed-shows";

    static readonly string[] s_catalogueNames =
    [
        "Top 50 - Global", "Top 50 - USA", "Viral 50 - Global", "Top Songs - Global", "Top Songs - Netherlands",
        "Top 50 - Greece", "Top 50 - South Korea", "Viral 50 - USA", "Top Songs - USA", "Top Albums - Global",
        "Today's Top Hits", "RapCaviar", "Hot Hits NL", "All Out 2010s", "Rock Classics", "Chill Hits", "Peaceful Piano",
        "Deep Focus", "Mood Booster", "Songs to Sing in the Car", "Lo-Fi Beats", "Jazz Vibes", "Pop Rising",
        "Indie Pop", "Dance Rising", "Fresh Finds", "K-Pop Daebak", "Viva Latino", "Afro Hits", "Ambient Relaxation",
        "Coffee Table Jazz", "Brain Food", "Intense Studying", "Beast Mode", "Power Hour", "Morning Motivation",
        "Evening Acoustic", "Night Rider", "Sleep", "Rainy Day",
    ];

    /// <summary>Dark, graded-looking payload accents (the `extractedColors.colorDark` shape), so every card spine, Fold
    /// wash and hero leg the seed reaches has a real colour on frame one — never an invented app accent.</summary>
    static readonly uint[] s_darkAccents =
    [
        0xFF3B1F5C, 0xFF14485C, 0xFF5C2A14, 0xFF1F5C2E, 0xFF5C1438, 0xFF34405C,
        0xFF5C4E14, 0xFF14365C, 0xFF4A145C, 0xFF2E5C55, 0xFF5C1F1F, 0xFF1F2F5C,
    ];

    static string CatalogueUri(int i) => "spotify:playlist:hp" + Wrap(i, CatalogueCount);

    // ── the partial SeedFake calls ─────────────────────────────────────────────────────────────────────────────────

    static partial void SeedHomeSurfaces(long now0)
    {
        SeedCatalogue(now0);
        SeedHomeDocument(now0);
        SeedHomeMockupAll(now0);      // A6: mockup-mirroring content for "" (All) — must land before the facet split
        SeedHomeFacets();
        SeedHomeMockupFacets(now0);   // A6: Podcasts/Music-only mockup content + the Following subset/failure demo
        SeedChartsAndBrowse();
        SeedTopContent();
        SeedSearch();
        SeedRecents(now0);
    }

    // ── 1. the catalogue: the "Spotify" owner and forty editorial playlists ─────────────────────────────────────────

    static void SeedCatalogue(long now0)
    {
        var s = Staging.Rent();
        try
        {
            ref var owner = ref s.Users.RowFor(s.AddText(Utf8(SpotifyOwnerUri)), Authority.Seed, (uint)UserFields.Identity);
            owner.Name = s.AddText("Spotify"u8);

            for (int i = 0; i < CatalogueCount; i++)
            {
                ref var row = ref s.Playlists.RowFor(s.AddText(Utf8(CatalogueUri(i))), Authority.Seed,
                    (uint)(PlaylistFields.Identity | PlaylistFields.Format | PlaylistFields.Accent));
                row.Title = s.AddText(Utf8(s_catalogueNames[i]));
                row.Description = s.AddText(Utf8(i < 10
                    ? "Your weekly update of the most played tracks right now."
                    : "The essential tracks, all in one playlist."));
                row.Image = s.AddText(Utf8(Cover(i + 5)));
                row.OwnerUri = s.AddText(Utf8(SpotifyOwnerUri));
                row.TrackCount = 20;
                row.Format = (byte)(i < 10 ? PlaylistFormat.Chart : PlaylistFormat.Editorial);
                row.Accent = s_darkAccents[Wrap(i, s_darkAccents.Length)];
            }
            Commit(s);
        }
        finally { Staging.Return(s); }

        // Twenty rows each, so a card opened from Charts, Browse or Search lands on a table with rows to scroll.
        Span<int> targets = stackalloc int[20];
        Span<PlaylistTrackEdge> payload = stackalloc PlaylistTrackEdge[20];
        for (int p = 0; p < CatalogueCount; p++)
        {
            int start = Wrap(p * 17 + 5, TrackCount);
            for (int k = 0; k < targets.Length; k++)
            {
                targets[k] = ResolveSeedSlot(EntityKind.Track, TrackUri(start + k));
                payload[k] = new PlaylistTrackEdge(StringId.Empty, (int)(now0 - (p + k) * 3600L), Table.None, 0, 0, 0, 0);
            }
            int slot = ResolveSeedSlot(EntityKind.Playlist, CatalogueUri(p));
            if (slot != Table.None) Current.Edges.PlaylistTracks.ReplaceRun(slot, targets, payload);
        }
    }

    // ── 2. the Home document: the bundled capture, the daylist window, one show shelf ───────────────────────────────

    static void SeedHomeDocument(long now0)
    {
        byte[]? capture = ReadBundledCapture("home.json");

        var s = Staging.Rent();
        try
        {
            if (capture is not null) Spotify.Decode.HomeFeed(capture, "wavee:home"u8, s);

            // The daylist window (ch 31 §2 W16; A6 restated it to the mockup canvas's own countdown, Main.dc.html
            // "Next daylist in 01:35:05"). Full authority: the capture's own playlist answer may already speak for
            // the group, and this fixture exists precisely to override the window it lacks.
            ref var daylist = ref s.Playlists.RowFor(s.AddText(Utf8(DaylistUri)), Authority.Full, (uint)PlaylistFields.Daylist);
            daylist.DaylistExpiresAt = (int)(now0 + (1 * 3600 + 35 * 60 + 5));
            daylist.DaylistCreatedAt = (int)(now0 - (2 * 3600 + 24 * 60 + 55));

            Span<string> shows = new string[ShowCount];
            for (int i = 0; i < shows.Length; i++) shows[i] = ShowUri(i);
            StageSection(s, PodcastSectionUri, "Shows to try", SectionKind.HomeGeneric, chart: false, shows,
                total: shows.Length, nextOffset: SectionPaging.Complete);

            Commit(s);
        }
        finally { Staging.Return(s); }

        // The landing's section list = the capture's, then the show shelf. A capture that is missing (a stripped build)
        // still leaves an answered, renderable feed rather than a skeleton that only the 8 s force release ends.
        var home = HomeFeed();
        var existing = home.SectionSlots;
        var sections = new int[existing.Length + 1];
        existing.CopyTo(sections);
        sections[^1] = Section(PodcastSectionUri.AsSpan()).Slot;
        Current.Edges.HomeSection.ReplaceRun(home.Slot, sections, default);
        Current.Homes.Bump(home.Slot, (uint)HomeFields.All);
    }

    // ── 2b. mockup-mirroring content (A6, home-redesign wave 1) ────────────────────────────────────────────────────────
    //
    // Every uri/image below is real catalogue data pulled from the same captures the approved canvas mockup
    // (docs/plans/wavee/home-redesign/canvas/*.dc.html) was built from, so a --fake screenshot at 1440x900 is
    // comparable item-by-item against the boards. Staged the SAME way as every other fixture in this file (plain
    // `RowFor` writes through Staging + Commit, ch 31 §0.1's rule); edges that need a resolved SLOT (AlbumArtists)
    // are wired AFTER Commit, exactly like `SeedCatalogue`'s own PlaylistTracks wiring below. The daylist's seed
    // CHIPS and an episode's PlayedState/VideoThumbUrl/Explicit have no entity column — only a per-section CARD FACT
    // one (`Entities/Home.cs` §2b) — so `StageDaylistSection`/`StageEpisodeSection` write `Staging.CardFacts`/
    // `CardSeeds` directly instead of going through the plain `StageSection` helper.
    //
    // No account ids, no personal usernames: the greeting stays the un-personalized "Good afternoon" the bundled
    // capture already uses, and "Wavee Listener" (the fake user's own existing display name, `Entities.Fake.cs`'s
    // `SeedRows`) is the only name in play. Real artists get PLAUSIBLE, INVENTED release dates for the "From artists
    // you follow" shelf (owner instruction -- no real single/EP uri was captured for most of them): vaultboy's
    // single is the one real album uri the capture held ("I loved you too much to just laugh it off"); Troye Sivan/
    // Charlie Puth/Rich Brian/ROSE/Henry Moodie each get a minted `spotify:album:hm...` id, real artist name,
    // invented date.

    static void SeedHomeMockupAll(long now0)
    {
        var s = Staging.Rent();
        var albumArtistLinks = new List<(string Album, string Artist)>();
        try
        {
            // -- the daylist (its own Spotlight-role band, so it wins the Daylist zone ahead of anything the bundled
            // capture also carries) --
            var daylistId = MockPlaylist(s, DaylistUri, "scream teen pop friday morning",
                "https://daylist.spotifycdn.com/playlist-covers-mix/en/morning_default.jpg", 50, PlaylistFormat.Daylist);
            StageDaylistSection(s, "spotify:section:wavee-mockup-daylist", daylistId,
                ["scream", "teen pop", "belter", "garage band", "international", "hollywood"]);

            // -- Made for you --
            var dw = MockPlaylist(s, "spotify:playlist:37i9dQZEVXcKDbGa6CckPI", "Discover Weekly",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/dw/cover/en", 30, PlaylistFormat.DiscoverWeekly,
                header: "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/dw/cover/en",
                description: "Your shortcut to hidden gems, deep cuts, and future faves, updated every Monday.");
            var dm1 = MockPlaylist(s, "spotify:playlist:37i9dQZF1E38wY9VFrwrWy", "Daily Mix 1",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/daily/1/ab6761610000e5ebe9134cc9371327f8238b1840/en", 50, PlaylistFormat.DailyMix,
                description: "IVE, Red Velvet, IU and more");
            var dm2 = MockPlaylist(s, "spotify:playlist:37i9dQZF1E37Yqr2urjnJt", "Daily Mix 2",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/daily/2/ab6761610000e5ebaadc18cac8d48124357c38e6/en-GB", 50, PlaylistFormat.DailyMix,
                description: "Lady Gaga, Maroon 5, Taylor Swift and more");
            var dm3 = MockPlaylist(s, "spotify:playlist:37i9dQZF1E39B2ECSfJBmK", "Daily Mix 3",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/daily/3/ab6761610000e5eb8f66ec26f9ae0a3238df995e/en-GB", 50, PlaylistFormat.DailyMix,
                description: "Dynamicduo, LEE MU JIN, Jung Seung Hwan and more");
            var dm4 = MockPlaylist(s, "spotify:playlist:37i9dQZF1E35HMZkgUHOT0", "Daily Mix 4",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/daily/4/ab6761610000e5ebeb112262ba2e60bf90053a02/en-GB", 50, PlaylistFormat.DailyMix,
                description: "Henry Moodie, vaultboy, elijah woods and more");
            var dm5 = MockPlaylist(s, "spotify:playlist:37i9dQZF1E35L9PKtiQN8O", "Daily Mix 5",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/daily/5/ab6761610000e5ebb1c707a1f3c469b08644797d/en-GB", 50, PlaylistFormat.DailyMix,
                description: "Michalis Hatzigiannis, Giorgos Sabanis, Antonis Remos and more");
            var dm6 = MockPlaylist(s, "spotify:playlist:37i9dQZF1E37Twn3WxoAWE", "Daily Mix 6",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/daily/6/ab6761610000e5eb1787f6596afa03df2d44af87/en-GB", 50, PlaylistFormat.DailyMix,
                description: "YOON MIRAE, 10CM, Isaac Hong and more");
            StageSectionIds(s, "spotify:section:wavee-mockup-made-for-you", "Made for you", SectionKind.HomeGeneric,
                [dw, dm1, dm3, dm4, dm5, dm6], total: 6, nextOffset: SectionPaging.Complete);

            // -- "It's New Music Friday!" -- wide editorial, header images >= 2/3 (WideEditorial's own rule) --
            var releaseRadar = MockPlaylist(s, "spotify:playlist:37i9dQZEVXbp4UblnlhiEI", "Release Radar",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000bada/dt/v1/img/release-radar-v4/7pbDxGE6nQSZVfiFdq9lOL/en", 200, PlaylistFormat.ReleaseRadar,
                header: "https://pickasso.spotifycdn.com/image/ab67c0de0000bada/dt/v1/img/release-radar-v4/7pbDxGE6nQSZVfiFdq9lOL/en",
                description: "Catch all the latest music from artists you follow, plus new singles picked for you.");
            var freshPop = MockPlaylist(s, "spotify:playlist:37i9dQZF1DX2fMaj5GfMh3", "Fresh Pop",
                "https://i.scdn.co/image/ab67706f0000000227e488f3d54063030295b6c7", 97, PlaylistFormat.Editorial,
                header: "https://i.scdn.co/image/ab67706f0000000227e488f3d54063030295b6c7", description: "De nieuwste pop songs.");
            var newKpop = MockPlaylist(s, "spotify:playlist:37i9dQZF1DXe5W6diBL5N4", "All New K-Pop (국내 최신 가요)",
                "https://i.scdn.co/image/ab67706f000000022f459c0307327e063ff07880", 114, PlaylistFormat.Editorial,
                header: "https://i.scdn.co/image/ab67706f000000022f459c0307327e063ff07880", description: "Enjoy the freshest K-Pop new releases.");
            var dancePop = MockPlaylist(s, "spotify:playlist:37i9dQZF1DWWOGXILUAh53", "New Dance Pop",
                "https://i.scdn.co/image/ab67706f000000026447a3b0a68af686ca85fa4f", 100, PlaylistFormat.Editorial,
                header: "https://i.scdn.co/image/ab67706f000000026447a3b0a68af686ca85fa4f", description: "The hottest new pop with all the drops.");
            var newAlt = MockPlaylist(s, "spotify:playlist:37i9dQZF1DX82GYcclJ3Ug", "the new alt",
                "https://i.scdn.co/image/ab67706f000000026480c0fa2998b372b6870e7c", 160, PlaylistFormat.Editorial,
                header: "https://i.scdn.co/image/ab67706f000000026480c0fa2998b372b6870e7c", description: "No rules. No boundaries. The best new alternative tracks.");
            var indieUpdate = MockPlaylist(s, "spotify:playlist:37i9dQZF1DXbBKF9yWSvWR", "Indie Update",
                "https://i.scdn.co/image/ab67706f00000002a50716f031faef1d67ca771f", 100, PlaylistFormat.Editorial,
                header: "https://i.scdn.co/image/ab67706f00000002a50716f031faef1d67ca771f", description: "Wekelijkse update met de beste Indie releases.");
            StageSectionIds(s, "spotify:section:wavee-mockup-nmf", "It's New Music Friday!", SectionKind.HomeGeneric,
                [releaseRadar, freshPop, newKpop, dancePop, newAlt, indieUpdate], total: 6, nextOffset: SectionPaging.Complete);

            // -- "From artists you follow" (server-sections-only releases shelf, >=75% albums / >=60% in-window) --
            string vaultboyAlbumUri = "spotify:album:7eZqfrilfQeDlMI4YmqZ9y", vaultboyArtistUri = "spotify:artist:hmVaultboy";
            string troyeAlbumUri = "spotify:album:hmTroyeSivanNewSingle", troyeArtistUri = "spotify:artist:hmTroyeSivan";
            string puthAlbumUri = "spotify:album:hmCharliePuthNewEp", puthArtistUri = "spotify:artist:hmCharliePuth";
            string richBrianAlbumUri = "spotify:album:hmRichBrianNewSingle", richBrianArtistUri = "spotify:artist:2IDLDx25HU1nQMKde4n61a";
            string roseAlbumUri = "spotify:album:hmRoseUpcoming", roseArtistUri = "spotify:artist:hmRose";
            string henryAlbumUri = "spotify:album:hmHenryMoodieNewSingle", henryArtistUri = "spotify:artist:hmHenryMoodie";

            var vaultboy = MockAlbum(s, vaultboyAlbumUri, "I loved you too much to just laugh it off",
                "https://i.scdn.co/image/ab67616d00001e02a28a0b1cc269fa15e243eb77", (int)now0, AlbumKind.Single);
            var troye = MockAlbum(s, troyeAlbumUri, "Slip", Cover(31), (int)now0, AlbumKind.Single);
            var puth = MockAlbum(s, puthAlbumUri, "Charlie", Cover(32), (int)(now0 - 2 * 86_400), AlbumKind.EP);
            var richBrianRelease = MockAlbum(s, richBrianAlbumUri, "New Wave", Cover(33), (int)(now0 - 1 * 86_400), AlbumKind.Single);
            var rose = MockAlbum(s, roseAlbumUri, "rosie (Pre-Save)", "https://i.scdn.co/image/ab67616d00001e027be65ce6672ed7e5aac6e966",
                (int)(now0 + 8 * 86_400), AlbumKind.Album);
            var henryMoodieRelease = MockAlbum(s, henryAlbumUri, "Better Now", Cover(34), (int)(now0 - 4 * 86_400), AlbumKind.Single);

            MockArtist(s, vaultboyArtistUri, "vaultboy", Cover(39));
            MockArtist(s, troyeArtistUri, "Troye Sivan", Cover(35));
            MockArtist(s, puthArtistUri, "Charlie Puth", Cover(36));
            MockArtist(s, richBrianArtistUri, "Rich Brian", "https://i.scdn.co/image/ab67616100005174c13929d20a265e45fadadbc9");
            var roseArtist = MockArtist(s, roseArtistUri, "ROSÉ", Cover(37));
            MockArtist(s, henryArtistUri, "Henry Moodie", Cover(38));

            albumArtistLinks.Add((vaultboyAlbumUri, vaultboyArtistUri));
            albumArtistLinks.Add((troyeAlbumUri, troyeArtistUri));
            albumArtistLinks.Add((puthAlbumUri, puthArtistUri));
            albumArtistLinks.Add((richBrianAlbumUri, richBrianArtistUri));
            albumArtistLinks.Add((roseAlbumUri, roseArtistUri));
            albumArtistLinks.Add((henryAlbumUri, henryArtistUri));

            StageSectionIds(s, "spotify:section:wavee-mockup-releases", "From artists you follow", SectionKind.HomeGeneric,
                [vaultboy, troye, puth, richBrianRelease, rose, henryMoodieRelease], total: 6, nextOffset: SectionPaging.Complete);

            // -- "Because you like..." baseline clusters (ClusterFold, owned by A1, groups these at render time) --
            var troyeRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4ourTrQDXAVJ", "Troye Sivan Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/3WGpXCj9YhhfX11TToZcXP/en", 50, PlaylistFormat.Radio);
            var popTerjineun = MockPlaylist(s, "spotify:playlist:37i9dQZF1DX8WYwNpUjJBr", "Pop 터지는 팝콘 한 입",
                "https://i.scdn.co/image/ab67706f000000024b2cb319c9b6d646fdf27e8c", 50, PlaylistFormat.Editorial);
            var emailsAlbum = MockAlbum(s, "spotify:album:2g4aJTa5ejGpp0O0GKzWAQ", "emails i can't send fwd:",
                "https://i.scdn.co/image/ab67616d00001e020f45623be014a592a5815827", (int)(now0 - 200 * 86_400));
            StageSectionIds(s, "spotify:section:wavee-mockup-cluster-troye", "More like Troye Sivan", SectionKind.HomeBaseline,
                [troyeRadio, popTerjineun, emailsAlbum], total: 3, nextOffset: SectionPaging.Complete);

            var roseRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4vmmujp8nGFY", "ROSÉ Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/3eVa5w3URK5duf6eyVDbu9/en", 50, PlaylistFormat.Radio);
            var romanticizing = MockPlaylist(s, "spotify:playlist:37i9dQZF1DWWAv76uXmtxB", "romanticizing life",
                "https://i.scdn.co/image/ab67706f00000002939028c1e5124638af3ecaf1", 60, PlaylistFormat.Editorial);
            var rosieAlbumUri = "spotify:album:7kFyd5oyJdVX2pIi6P4iHE";
            var rosieAlbum = MockAlbum(s, rosieAlbumUri, "rosie",
                "https://i.scdn.co/image/ab67616d00001e027be65ce6672ed7e5aac6e966", (int)(now0 - 150 * 86_400));
            albumArtistLinks.Add((rosieAlbumUri, roseArtistUri));
            StageSectionIds(s, "spotify:section:wavee-mockup-cluster-rose", "More like ROSÉ", SectionKind.HomeBaseline,
                [roseRadio, romanticizing, rosieAlbum], total: 3, nextOffset: SectionPaging.Complete);

            var sadHour = MockPlaylist(s, "spotify:playlist:37i9dQZF1DWSqBruwoIXkA", "sad hour",
                "https://i.scdn.co/image/ab67706f000000021ec2ef7e5cc1221d4b45354e", 150, PlaylistFormat.Editorial);
            var thisIsHenry = MockPlaylist(s, "spotify:playlist:37i9dQZF1DZ06evO4j9SIr", "This Is Henry Moodie",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/thisisv3/7hr9W3IjXcm3UlLY7guLk5/nl", 50, PlaylistFormat.Editorial);
            StageSectionIds(s, "spotify:section:wavee-mockup-cluster-henry", "For fans of Henry Moodie", SectionKind.HomeBaseline,
                [sadHour, thisIsHenry, dm4], total: 3, nextOffset: SectionPaging.Complete);

            var thisIsSavage = MockPlaylist(s, "spotify:playlist:37i9dQZF1DZ06evO2drKmY", "This Is Savage Garden",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/thisisv3/3NRFinRTEqUCfaTTZmk8ek/en-GB", 50, PlaylistFormat.Editorial);
            var guiltyPleasures = MockPlaylist(s, "spotify:playlist:37i9dQZF1DX4pUKG1kS0Ac", "Guilty Pleasures",
                "https://i.scdn.co/image/ab67706f000000027773630113d067b9590817f4", 150, PlaylistFormat.Editorial);
            StageSectionIds(s, "spotify:section:wavee-mockup-cluster-savage", "For fans of Savage Garden", SectionKind.HomeBaseline,
                [thisIsSavage, guiltyPleasures], total: 2, nextOffset: SectionPaging.Complete);

            // -- RecentGrid support: WLUWD and 90's Nederlandstalig are two of the real uris `s_recentsSeed` (above)
            // points at, staged here (no section membership needed) purely so the RecentGrid renders a real title/
            // cover instead of a blank thin row -- Kris Kross Amsterdam/Rich Brian/Arash/Arcane already resolve
            // through this method's own Artist/Album rows below. --
            MockAlbum(s, "spotify:album:2SxoeF005n621Jca66RRdu", "WLUWD",
                "https://i.scdn.co/image/ab67616d00001e02cd8620c0c1a987ca8ef78152", (int)(now0 - 26 * 3600));
            MockPlaylist(s, "spotify:playlist:37i9dQZF1DXaQpIUzyByme", "90's Nederlandstalig",
                "https://i.scdn.co/image/ab67706f000000026617bd505871a9a04eb9decb", 70, PlaylistFormat.Editorial);
            MockArtist(s, "spotify:artist:4LcUpNlXFEleaLlelmkv2R", "Kris Kross Amsterdam",
                "https://i.scdn.co/image/ab6761610000517473a7c008c663a8177c9f6af4");

            // -- Jump back in (the first mixed-kind Generic section, per ZonePlanner's own rule) --
            var arcane = MockAlbum(s, "spotify:album:2x6LWti2bjYS6AllSomoV7",
                "Arcane League of Legends: Season 2 (Soundtrack from the Animated Series)",
                "https://i.scdn.co/image/ab67616d00001e024aaf2413384c1efcc38353c6", (int)(now0 - 40 * 86_400));
            var chillMix = MockPlaylist(s, "spotify:playlist:37i9dQZF1EVHGWrwldPRtj", "Chill Mix",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/topic/chill/2tIP7SsRs7vjIcLrU85W8J/en-GB", 50, PlaylistFormat.TopicMix);
            var millennium = MockPlaylist(s, "spotify:playlist:37i9dQZF1DWUoY6Ih7vsxr", "Millennium K-Pop",
                "https://i.scdn.co/image/ab67706f0000000286eee426895c21c627722618", 100, PlaylistFormat.Editorial);
            var sleepMusic = MockPlaylist(s, "spotify:playlist:5FI8rn340FgsOB7B8Ic0DZ", "Sleep Music for Deep Sleeping",
                "https://image-cdn-ak.spotifycdn.com/image/ab67706c0000da841011a2539b708c1a7aecfa4f", 432, PlaylistFormat.Editorial);
            var philCollins = MockArtist(s, "spotify:artist:4lxfqrEsLX6N1N4OCSkILp", "Phil Collins",
                "https://i.scdn.co/image/ab67616100005174e1b4af6dd9cad1b03d03218d");
            var imagineDragons = MockArtist(s, "spotify:artist:53XhwfbYqKCa1cC15pYq2q", "Imagine Dragons",
                "https://i.scdn.co/image/ab67616100005174ab47d8dae2b24f5afe7f9d38");
            var arash = MockArtist(s, "spotify:artist:7hQmAXAzWI6D350VTgkKTG", "Arash",
                "https://i.scdn.co/image/ab676161000051748a1890a7ec43f20b51a8e9e5");
            var qTop1500 = MockPlaylist(s, "spotify:playlist:2pnt79m93NytfAj2lByLlQ", "Q-top 1500 | editie 2025 | Qmusic",
                "https://image-cdn-ak.spotifycdn.com/image/ab67706c0000d72c92eebc4b40c2570334556ace", 1494, PlaylistFormat.Editorial);
            var mix2020s = MockPlaylist(s, "spotify:playlist:37i9dQZF1EQnsJ0xmvpihE", "2020s Mix",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/topic/twenty_twenties/7qmpXeNz2ojlMl2EEfkeLs/en", 50, PlaylistFormat.TopicMix);
            var thatSummer = MockAlbum(s, "spotify:album:6aFIFxsveiaKU30g2wiItW", "That Summer",
                "https://i.scdn.co/image/ab67616d00001e0289ec044397d7342e9e23fb06", (int)(now0 - 60 * 86_400), AlbumKind.Single);
            StageSectionIds(s, "spotify:section:wavee-mockup-jump-back-in", "Jump back in", SectionKind.HomeGeneric,
                [arcane, chillMix, rosieAlbum, millennium, sleepMusic, philCollins, imagineDragons, arash, qTop1500, dm2, mix2020s, thatSummer],
                total: 12, nextOffset: SectionPaging.Complete);

            // -- Radio & mixes (>=50% "radio" format cards -> the Radio role) --
            var savageRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4AgGLUsynqyF", "Savage Garden Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/3NRFinRTEqUCfaTTZmk8ek/en", 50, PlaylistFormat.Radio,
                header: "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/3NRFinRTEqUCfaTTZmk8ek/en");
            var physicalRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E8MGOuMJICudG", "Physical Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/track/3AzjcOeAmA57TIOr9zF1ZW/en", 50, PlaylistFormat.Radio);
            var oneDayRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E8DjkIvPCsKYV", "One Day (feat. Helena) - Radio Edit Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/track/0jkjiF0f9c7EpJRUJ47bvi/en", 50, PlaylistFormat.Radio);
            var vaultboyRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4y3o8BDiBs4n", "vaultboy Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/0K87f3owemzI8NUCoEIXOB/en", 50, PlaylistFormat.Radio);
            var rexRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4Cin6Wku9m4T", "Rex Orange County Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/7pbDxGE6nQSZVfiFdq9lOL/en", 50, PlaylistFormat.Radio);
            var damianoRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4x2U7TuxADyl", "Damiano David Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/7AaGbSgUxJFuZ49VvclNH6/en", 50, PlaylistFormat.Radio);
            var duaRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4yMj4BGCYzsQ", "Dua Lipa Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/6M2wZ9GZgrQXHCFfjv46we/en-GB", 50, PlaylistFormat.Radio);
            var coldplayRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4FhRRLh0mmxG", "Coldplay Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/4gzpq5DPGxSnKTe4SA8HAU/en-GB", 50, PlaylistFormat.Radio);
            var teddyRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4jVwymj8azOX", "Teddy Swims Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/33qOK5uJ8AR2xuQQAhHump/en-GB", 50, PlaylistFormat.Radio);
            var fleetwoodRadio = MockPlaylist(s, "spotify:playlist:37i9dQZF1E4tZxYcBZfDn9", "Fleetwood Mac Radio",
                "https://pickasso.spotifycdn.com/image/ab67c0de0000deef/dt/v1/img/radio/artist/08GQAI4eElDnROBrJRGE0X/en-GB", 50, PlaylistFormat.Radio);
            StageSectionIds(s, "spotify:section:wavee-mockup-radio", "Radio & mixes", SectionKind.HomeGeneric,
                [savageRadio, physicalRadio, oneDayRadio, vaultboyRadio, rexRadio, damianoRadio, duaRadio, coldplayRadio, teddyRadio, fleetwoodRadio],
                total: 10, nextOffset: SectionPaging.Complete);

            Commit(s);
        }
        finally { Staging.Return(s); }

        // Album -> artist credit lines (HomeCard.Subtitle's Album arm reads the AlbumArtists EDGE, which only a
        // resolved slot can carry -- wired here, after Commit, exactly like SeedCatalogue's own PlaylistTracks wiring).
        foreach (var (albumUri, artistUri) in albumArtistLinks)
        {
            int albumSlot = ResolveSeedSlot(EntityKind.Album, albumUri);
            int artistSlot = ResolveSeedSlot(EntityKind.Artist, artistUri);
            if (albumSlot != Table.None && artistSlot != Table.None)
                Current.Edges.AlbumArtists.ReplaceRun(albumSlot, [artistSlot], default);
        }

        // Prepend every mockup band ahead of the bundled capture's own sections (canvas order: Daylist, Made for you,
        // New Music Friday, Releases, clusters, Jump back in, Radio, THEN whatever the capture answered).
        string[] mockupUris =
        [
            "wavee-mockup-daylist", "wavee-mockup-made-for-you", "wavee-mockup-nmf", "wavee-mockup-releases",
            "wavee-mockup-cluster-troye", "wavee-mockup-cluster-rose", "wavee-mockup-cluster-henry",
            "wavee-mockup-cluster-savage", "wavee-mockup-jump-back-in", "wavee-mockup-radio",
        ];
        var home = HomeFeed();
        var existing = home.SectionSlots.ToArray();
        var combined = new int[mockupUris.Length + existing.Length];
        for (int i = 0; i < mockupUris.Length; i++) combined[i] = Section(("spotify:section:" + mockupUris[i]).AsSpan()).Slot;
        existing.CopyTo(combined, mockupUris.Length);
        Current.Edges.HomeSection.ReplaceRun(home.Slot, combined, default);
        Current.Homes.Bump(home.Slot, (uint)HomeFields.All);
    }

    // The Mock* rows stage at Full authority: the bundled capture already answers some of these uris (the daylist
    // decodes as plain "daylist"), and the mockup dataset must read exactly like the canvas.
    static StagedId MockPlaylist(Staging s, string uri, string title, string img, int trackCount,
        PlaylistFormat format = PlaylistFormat.Other, string? header = null, string? description = null)
    {
        var id = new StagedId(s.AddText(Utf8(uri)));
        ref var row = ref s.Playlists.RowFor(id, Authority.Full, (uint)(PlaylistFields.Identity | PlaylistFields.Format));
        row.Title = s.AddText(Utf8(title));
        row.Image = s.AddText(Utf8(img));
        row.TrackCount = trackCount;
        row.Format = (byte)format;
        if (header is not null) row.HeaderImage = s.AddText(Utf8(header));
        if (description is not null) row.Description = s.AddText(Utf8(description));
        return id;
    }

    static StagedId MockAlbum(Staging s, string uri, string title, string img, int releaseAtSeconds, AlbumKind kind = AlbumKind.Album)
    {
        var id = new StagedId(s.AddText(Utf8(uri)));
        ref var row = ref s.Albums.RowFor(id, Authority.Full,
            (uint)(AlbumFields.Title | AlbumFields.Image | AlbumFields.Year | AlbumFields.Kind | AlbumFields.Release));
        row.Title = s.AddText(Utf8(title));
        row.Image = s.AddText(Utf8(img));
        row.Kind = (byte)kind;
        row.ReleaseAt = releaseAtSeconds;
        row.Year = (ushort)Math.Clamp(DateTimeOffset.FromUnixTimeSeconds(releaseAtSeconds).Year, 1, 9999);
        return id;
    }

    static StagedId MockArtist(Staging s, string uri, string name, string img)
    {
        var id = new StagedId(s.AddText(Utf8(uri)));
        ref var row = ref s.Artists.RowFor(id, Authority.Full, (uint)ArtistFields.Identity);
        row.Name = s.AddText(Utf8(name));
        row.Image = s.AddText(Utf8(img));
        return id;
    }

    static StagedId MockShow(Staging s, string uri, string title, string img, string publisher)
    {
        var id = new StagedId(s.AddText(Utf8(uri)));
        ref var row = ref s.Shows.RowFor(id, Authority.Full, (uint)(ShowFields.Title | ShowFields.Image | ShowFields.Publisher));
        row.Title = s.AddText(Utf8(title));
        row.Image = s.AddText(Utf8(img));
        row.Publisher = s.AddText(Utf8(publisher));
        return id;
    }

    static StagedId MockEpisode(Staging s, string uri, string title, string img, StagedId showId, int durationMs, int publishedAtSeconds)
    {
        var id = new StagedId(s.AddText(Utf8(uri)));
        ref var row = ref s.Episodes.RowFor(id, Authority.Full, (uint)EpisodeFields.Identity);
        row.Title = s.AddText(Utf8(title));
        row.Image = s.AddText(Utf8(img));
        row.ShowUri = showId;
        row.DurationMs = durationMs;
        row.PublishedAt = publishedAtSeconds;
        return id;
    }

    /// <summary>What a seeded band answers: its identity AND its whole card list — the seed IS the offline catalogue's
    /// whole section, so the drill page (which demands <see cref="SectionFields.Whole"/>) reads it at once and asks no
    /// provider (there is none behind the fake scope).</summary>
    const uint SeededSection = (uint)(SectionFields.Identity | SectionFields.Whole);

    /// <summary>The same shape as `StageSection` (Charts/Browse's own helper, above) but for cards ALREADY staged this
    /// batch as `StagedId` values -- every mockup card is a real entity `Mock*` already minted, so re-encoding its uri
    /// as text a second time (what `StageSection`'s `string[]` overload does) would just waste arena bytes.</summary>
    static StagedId StageSectionIds(Staging s, string sectionUri, string title, SectionKind kind,
        ReadOnlySpan<StagedId> cardIds, int total, int nextOffset)
    {
        var id = new StagedId(s.AddText(Utf8(sectionUri)));
        ref var row = ref s.Sections.RowFor(id, Authority.Seed, SeededSection);
        row.Title = title.Length == 0 ? default : s.AddText(Utf8(title));
        row.Kind = (byte)kind;
        row.Total = total;
        row.Raw = cardIds.Length;
        row.Cards = cardIds.Length;
        row.NextOffset = nextOffset;

        int mark = s.Edges.PendingMark;
        foreach (var cardId in cardIds) s.Edges.Push().Target = cardId;
        s.Edges.Close(Relation.SectionCards, in id, mark);
        return id;
    }

    /// <summary>The daylist's own section: one card, whose seed CHIPS (section 2b's `StagedCardFact.SeedStart`/
    /// `SeedCount`) a plain `StageSectionIds` call has no way to carry.</summary>
    static void StageDaylistSection(Staging s, string sectionUri, StagedId daylistId, string[] tags)
    {
        var id = new StagedId(s.AddText(Utf8(sectionUri)));
        ref var row = ref s.Sections.RowFor(id, Authority.Seed, SeededSection);
        row.Kind = (byte)SectionKind.HomeSpotlight;
        row.Total = 1;
        row.Raw = 1;
        row.Cards = 1;
        row.NextOffset = SectionPaging.Complete;

        int mark = s.Edges.PendingMark;
        s.Edges.Push().Target = daylistId;
        s.Edges.Close(Relation.SectionCards, in id, mark);

        int factStart = s.CardFacts.Count;
        ref var f = ref s.CardFacts.Add();
        f.Target = daylistId;
        f.SeedStart = s.CardSeeds.Count;
        foreach (var tag in tags) s.CardSeeds.Add() = s.AddText(Utf8(tag));
        f.SeedCount = tags.Length;
        row.HasFacts = true;
        row.FactStart = factStart;
        row.FactCount = 1;
    }

    /// <summary>One episode card's fact-slab payload the podcasts capture always carries but a plain entity row cannot
    /// (section 2b -- `PlayedState`/`VideoThumbUrl` have no entity column, only a fact one).</summary>
    readonly record struct EpisodeCardSeed(StagedId Id, byte PlayedState, int ResumeMs, string? VideoThumb, bool Explicit);

    static StagedId StageEpisodeSection(Staging s, string sectionUri, string title, SectionKind kind, EpisodeCardSeed[] cards)
    {
        var id = new StagedId(s.AddText(Utf8(sectionUri)));
        ref var row = ref s.Sections.RowFor(id, Authority.Seed, SeededSection);
        row.Title = s.AddText(Utf8(title));
        row.Kind = (byte)kind;
        row.Total = cards.Length;
        row.Raw = cards.Length;
        row.Cards = cards.Length;
        row.NextOffset = SectionPaging.Complete;

        int mark = s.Edges.PendingMark;
        foreach (var c in cards) s.Edges.Push().Target = c.Id;
        s.Edges.Close(Relation.SectionCards, in id, mark);

        int factStart = s.CardFacts.Count;
        foreach (var c in cards)
        {
            ref var f = ref s.CardFacts.Add();
            f.Target = c.Id;
            f.SeedStart = -1;
            f.PlayedState = c.PlayedState;
            f.ResumeMs = c.ResumeMs;
            if (c.VideoThumb is not null) { f.VideoThumbUrl = s.AddText(Utf8(c.VideoThumb)); f.Flags |= (byte)HomeCardFlags.HasVideo; }
            if (c.Explicit) f.Flags |= (byte)HomeCardFlags.Explicit;
        }
        row.HasFacts = true;
        row.FactStart = factStart;
        row.FactCount = cards.Length;
        return id;
    }

    /// <summary>The Podcasts-facet mockup content (real public show/episode names, `podcasts.json`'s field shapes --
    /// section 4.3) and the Music facet's own "More like Avril Lavigne" cluster (Music.dc.html only -- it never
    /// appears on All). Runs after `SeedHomeFacets` so the two facets already exist to splice into.</summary>
    static void SeedHomeMockupFacets(long now0)
    {
        var s = Staging.Rent();
        try
        {
            // -- Podcasts: shows (Podcasts.dc.html "Your shows") --
            var patrickBoyle = MockShow(s, "spotify:show:hmPatrickBoyle", "Patrick Boyle On Finance", Cover(40), "Patrick Boyle");
            var braveTech = MockShow(s, "spotify:show:hmBraveTechnologist", "The Brave Technologist", Cover(41), "Brave");
            var solvedMark = MockShow(s, "spotify:show:hmSolvedMarkManson", "SOLVED with Mark Manson", Cover(42), "Mark Manson");
            var huberman = MockShow(s, "spotify:show:hmHubermanLab", "Huberman Lab", Cover(43), "Scicomm Media");
            var coldfusion = MockShow(s, "spotify:show:hmColdFusion", "ColdFusion", Cover(44), "ColdFusion");
            var heyTablo = MockShow(s, "spotify:show:hmHeyTablo", "Hey Tablo", Cover(45), "Team Epikase");
            var checkupMike = MockShow(s, "spotify:show:hmCheckupDoctorMike", "The Checkup with Doctor Mike", Cover(46), "DM Operations Inc.");
            StageSectionIds(s, "spotify:section:wavee-mockup-your-shows", "Your shows", SectionKind.HomeGeneric,
                [patrickBoyle, braveTech, solvedMark, huberman, coldfusion, heyTablo, checkupMike], total: 7, nextOffset: SectionPaging.Complete);

            // -- New episodes (video/explicit variety, exercises A6's decode facts end to end) --
            var epFund = MockEpisode(s, "spotify:episode:hmEpFundStructure", "The Fund Structure That Could Replace the ETF",
                Cover(47), patrickBoyle, 27 * 60_000, (int)(now0 - 2 * 86_400));
            var epHotTake = MockEpisode(s, "spotify:episode:hmEpHotTake", "Hot Take: A Great Movie Trilogy Must Have ONE FLOP (Hey Tablo Ep.39)",
                Cover(48), heyTablo, 48 * 60_000, (int)(now0 - 3 * 86_400));
            var epDoomsday = MockEpisode(s, "spotify:episode:hmEpDoomsdayCult", "The Doomsday Cult Inside OpenAI",
                Cover(49), patrickBoyle, 31 * 60_000, (int)(now0 - 5 * 86_400));
            var epEssentials = MockEpisode(s, "spotify:episode:hmEpEssentialsFitness", "Essentials: How to Assess & Improve All Aspects of Your Fitness",
                Cover(50), huberman, 35 * 60_000, (int)(now0 - 8 * 86_400));
            var epJohnPark = MockEpisode(s, "spotify:episode:hmEpJohnPark", "John Park is like Tukutz but way SMARTER (Hey Tablo Ep. 38)",
                Cover(51), heyTablo, 61 * 60_000, (int)(now0 - 10 * 86_400));
            StageEpisodeSection(s, "spotify:section:wavee-mockup-new-episodes", "New episodes", SectionKind.HomeGeneric,
            [
                new(epFund, PlayedState: 1, ResumeMs: 0, VideoThumb: null, Explicit: false),
                new(epHotTake, PlayedState: 1, ResumeMs: 0,
                    VideoThumb: "https://image-cdn-fa.spotifycdn.com/image/video-thumb-hot-take-1280.jpg", Explicit: true),
                new(epDoomsday, PlayedState: 1, ResumeMs: 0, VideoThumb: null, Explicit: false),
                new(epEssentials, PlayedState: 1, ResumeMs: 0, VideoThumb: null, Explicit: true),
                new(epJohnPark, PlayedState: 1, ResumeMs: 0,
                    VideoThumb: "https://image-cdn-fa.spotifycdn.com/image/video-thumb-john-park-1280.jpg", Explicit: true),
            ]);

            // -- Continue listening (one IN_PROGRESS episode: 44 min left of ~1 h 30) --
            var epContinue = MockEpisode(s, "spotify:episode:hmEpScottBessent", "Scott Bessent Is at War With Prices — and Prices Are Winning!",
                Cover(52), patrickBoyle, 90 * 60_000, (int)(now0 - 1 * 86_400));
            StageEpisodeSection(s, "spotify:section:wavee-mockup-continue", "Continue listening", SectionKind.HomeGeneric,
                [new(epContinue, PlayedState: 2, ResumeMs: (90 - 44) * 60_000, VideoThumb: null, Explicit: false)]);

            // -- Episodes you might like (baseline-style; both explicit) --
            var epAnxiety = MockEpisode(s, "spotify:episode:hmEpOvercomeAnxiety", "How to Overcome Anxiety, Solved",
                Cover(53), solvedMark, 130 * 60_000, (int)(now0 - 140 * 86_400));
            var epChangeLife = MockEpisode(s, "spotify:episode:hmEpChangeLife", "How to Change Your Life, Solved",
                Cover(54), solvedMark, 298 * 60_000, (int)(now0 - 110 * 86_400));
            StageEpisodeSection(s, "spotify:section:wavee-mockup-episodes-you-might-like", "Episodes you might like", SectionKind.HomeBaseline,
            [
                new(epAnxiety, PlayedState: 3, ResumeMs: 0, VideoThumb: null, Explicit: true),
                new(epChangeLife, PlayedState: 1, ResumeMs: 0, VideoThumb: null, Explicit: true),
            ]);

            // -- Music facet only: "More like Avril Lavigne" (Music.dc.html; never appears on All) --
            var avrilRadio = MockPlaylist(s, "spotify:playlist:hmAvrilLavigneRadio", "Avril Lavigne Radio", Cover(55), 50, PlaylistFormat.Radio);
            var popRock00s = MockPlaylist(s, "spotify:playlist:hm00sPopRock", "00s Pop Rock", Cover(56), 75, PlaylistFormat.Editorial,
                description: "The essential pop rock songs from the 2000s.");
            var songsRocked = MockPlaylist(s, "spotify:playlist:hmSongsWeRockedOutTo", "Songs We Rocked Out To", Cover(57), 80, PlaylistFormat.Editorial,
                description: "Walk down memory lane with the biggest rock anthems.");
            var allOut2000s = MockPlaylist(s, "spotify:playlist:hmAllOut2000s", "All Out 2000s", Cover(58), 150, PlaylistFormat.Editorial,
                description: "The biggest songs of the 2000s.");
            string breakawayUri = "spotify:album:hmBreakaway", kellyClarksonUri = "spotify:artist:hmKellyClarkson";
            var breakaway = MockAlbum(s, breakawayUri, "Breakaway", Cover(59), (int)(now0 - 8000 * 86_400));
            var kellyClarkson = MockArtist(s, kellyClarksonUri, "Kelly Clarkson", Cover(60));
            StageSectionIds(s, "spotify:section:wavee-mockup-cluster-avril", "More like Avril Lavigne", SectionKind.HomeBaseline,
                [avrilRadio, popRock00s, songsRocked, allOut2000s, breakaway, kellyClarkson], total: 6, nextOffset: SectionPaging.Complete);

            Commit(s);

            int albumSlot = ResolveSeedSlot(EntityKind.Album, breakawayUri);
            int artistSlot = ResolveSeedSlot(EntityKind.Artist, kellyClarksonUri);
            if (albumSlot != Table.None && artistSlot != Table.None)
                Current.Edges.AlbumArtists.ReplaceRun(albumSlot, [artistSlot], default);
        }
        finally { Staging.Return(s); }

        // -- splice: Podcasts gets the rich content ahead of the placeholder "Shows to try" shelf --
        SpliceFacet("podcasts-chip",
            ["wavee-mockup-your-shows", "wavee-mockup-new-episodes", "wavee-mockup-continue", "wavee-mockup-episodes-you-might-like"],
            prepend: true);

        // podcasts-following-chip narrows to a P1-P3 subset: the first three of "Your shows" only.
        var following = HomeFeed("podcasts-following-chip".AsSpan());
        Current.Edges.HomeSection.ReplaceRun(following.Slot,
            [Section("spotify:section:wavee-mockup-your-shows".AsSpan()).Slot], default);
        Current.Homes.Bump(following.Slot, (uint)HomeFields.All);
        // The section itself already carries only 7 shows (P1-P7), not a P1-P3 card-level trim -- a true per-facet
        // slice of the SectionCards edge is a follow-up for whichever wave owns the Following projection; recorded
        // here rather than silently fabricated, since SectionCards has no per-facet view today.

        SpliceFacet("music-chip", ["wavee-mockup-cluster-avril"], prepend: false);
    }

    /// <summary>Prepend or append named sections (by their `spotify:section:` suffix) onto a facet's list.</summary>
    static void SpliceFacet(string facetId, string[] sectionSuffixes, bool prepend)
    {
        var facet = HomeFeed(facetId.AsSpan());
        var existing = facet.SectionSlots.ToArray();
        var added = new int[sectionSuffixes.Length];
        for (int i = 0; i < sectionSuffixes.Length; i++) added[i] = Section(("spotify:section:" + sectionSuffixes[i]).AsSpan()).Slot;

        var combined = new int[existing.Length + added.Length];
        if (prepend) { added.CopyTo(combined, 0); existing.CopyTo(combined, added.Length); }
        else { existing.CopyTo(combined, 0); added.CopyTo(combined, existing.Length); }
        Current.Edges.HomeSection.ReplaceRun(facet.Slot, combined, default);
        Current.Homes.Bump(facet.Slot, (uint)HomeFields.All);
    }

    /// <summary>Per-facet fetch latency and failure, for --fake's demo of the switch's dim+bar and the InfoBar
    /// (06-facet-design.md section 2.6) -- DATA, not an environment switch, per the owner's "no env-var switches for
    /// behaviour" rule (CLAUDE.md). A6 encodes the table; consuming it (delaying/failing a facet's fake fetch) is
    /// Wave 4's `Home.Host.cs` fake-provider arm, owned by C1 -- this file only names the numbers.</summary>
    public static class FakeHomeFacetTiming
    {
        /// <summary>The canvas's own "busy" demo duration (06-facet-design.md section 2.6's timeline table is
        /// qualitative; ~1200 ms is long enough for a screenshot to catch the dim + indeterminate bar mid-flight).</summary>
        public const int SwitchLatencyMs = 1200;

        /// <summary>Facet ids whose fake fetch should end in FAILURE (InfoBar + revert) rather than success. Matches
        /// `Entities.HomeFeed("music-following-chip")` never being seeded above -- the two are meant to agree.</summary>
        public static readonly IReadOnlySet<string> Failing = new HashSet<string> { "music-following-chip" };
    }

    // ── F15 (home-redesign-remediation.md §3.1) — the consuming half of FakeHomeFacetTiming, moved out of
    // Home/Screen.UI.cs and into this file (called from `Home.EnsureFeed`, Home.cs): every --fake facet is already
    // seeded synchronously at boot (SeedHomeFacets), so `Home.EnsureFeed` has nothing to actually fetch — without
    // this, a switch's `LiveAttemptConcluded` would read true on the very next frame and the dim + bar the 06-facet
    // design calls for would never show. This holds the row's own `Inflight` stamp for `SwitchLatencyMs` (the same
    // signal a live in-flight fetch would clear), then — for a facet named in `Failing` — clears its `Sections`
    // known bit so the attempt concludes with nothing (a real switch failure: InfoBar + revert); every other facet
    // just re-bumps its version so the effect waiting on `Home.LiveAttemptConcluded` re-runs.
    static readonly Dictionary<int, System.Threading.Timer> s_fakeSwitchTimers = new();

    public static void SimulateFacetFetch(Home h)
    {
        if (!h.IsValid) return;
        var scope = Current;
        var t = scope.Homes;
        int slot = h.Slot;
        if (t.Inflight[slot] != 0) return;   // already simulating this row's switch

        uint stamp = unchecked((uint)Environment.TickCount64) | 1u;
        t.Inflight[slot] = stamp;

        string facetId = Entities.Strings.Resolve(h.FacetId);
        bool fail = facetId.Length > 0 && FakeHomeFacetTiming.Failing.Contains(facetId);

        if (s_fakeSwitchTimers.TryGetValue(slot, out var stale)) stale.Dispose();
        s_fakeSwitchTimers[slot] = new System.Threading.Timer(
            _ => Spotify.Post(() => ConcludeFakeFetch(scope, slot, stamp, fail)),
            null, FakeHomeFacetTiming.SwitchLatencyMs, System.Threading.Timeout.Infinite);
    }

    static void ConcludeFakeFetch(Scope scope, int slot, uint stamp, bool fail)
    {
        s_fakeSwitchTimers.Remove(slot);
        if (!ReferenceEquals(scope, Current)) return;    // the scope was replaced (sign-out, a fresh test) meanwhile
        var t = scope.Homes;
        if ((uint)slot >= (uint)t.Count || t.Inflight[slot] != stamp) return;
        t.Inflight[slot] = 0;
        // A failed attempt is ASKED-but-not-KNOWN — exactly what a live fetch that errored leaves — so
        // LiveAttemptConcluded reads true and the switch fails (InfoBar + revert) instead of loading forever.
        if (fail) { t.Known[slot] &= ~(uint)HomeFields.Sections; t.Asked[slot] |= (uint)HomeFields.Sections; }
        // Bump ORs its group into Known — a failure bumps with no group, or it would re-set the bit it just cleared.
        t.Bump(slot, fail ? 0u : (uint)HomeFields.All);
    }

    // ── 3. the facet documents (a facet is a different DOCUMENT, ch 10 §0.7) ────────────────────────────────────────

    static void SeedHomeFacets()
    {
        var home = HomeFeed();
        var all = home.SectionSlots.ToArray();
        var music = new List<int>(all.Length);
        var podcasts = new List<int>(2);
        foreach (int slot in all)
        {
            if (IsShowSection(slot)) podcasts.Add(slot);
            else music.Add(slot);
        }

        SeedFacet(home, "music-chip", music);
        // music-following-chip is the demo's FAILS case (06-facet-design.md, A6 dataset spec): deliberately never
        // seeded, so `Entities.HomeFeed("music-following-chip")` comes back unanswered (`Knows(HomeFields.All)` false)
        // — the closest a static seed can get to a live fetch failure. `FakeHomeFacetTiming.Failing` names it as
        // DATA for the future fake-provider arm (owned by C1/Home.Host.cs) that turns this into a real InfoBar + Retry.
        SeedFacet(home, "podcasts-chip", podcasts);
        // podcasts-following-chip is narrowed to a P1-P3 subset in SeedHomeMockupFacets, once the richer Podcasts
        // mockup content exists; seed the pre-mockup shelf here so the facet is still answered if that step is skipped.
        SeedFacet(home, "podcasts-following-chip", podcasts);
        SeedFacet(home, "audiobooks-chip", []);            // answered and empty: ch 10 W10's real empty state
    }

    static bool IsShowSection(int sectionSlot)
    {
        var kinds = Current.Edges.SectionCards.Payload(sectionSlot);
        if (kinds.IsEmpty) return false;
        foreach (var k in kinds) if (k.Kind is not (EntityKind.Show or EntityKind.Episode)) return false;
        return true;
    }

    static void SeedFacet(Home home, string facetId, List<int> sections)
    {
        var facet = HomeFeed(facetId.AsSpan());
        var t = Current.Homes;
        t.SetText(ref t.Greeting, facet.Slot, home.GreetingId);

        // Copied out before the write: SetChips grows the slabs the source spans point into.
        var ids = home.ChipIds.ToArray();
        var labels = home.ChipLabels.ToArray();
        var parents = home.ChipParents.ToArray();
        t.SetChips(facet.Slot, ids, labels, parents);

        Current.Edges.HomeSection.ReplaceRun(facet.Slot, sections.ToArray(), default);
        t.Bump(facet.Slot, (uint)HomeFields.All);
    }

    // ── 4. the Charts deck, the Browse directory and its category pages ─────────────────────────────────────────────

    /// <summary>The directory's tiles: (uri, title). The Top / For you / Genres / Mood &amp; activity rows are
    /// <c>BrowseTaxonomy</c>'s map ENTRY FOR ENTRY (0.2.9 <c>BrowseTaxonomy.cs:41-107</c>) so every band renders at its
    /// real size; three unmapped uris exercise the More band.</summary>
    static readonly (string Uri, string Title)[] s_browseTiles =
    [
        ("spotify:page:0JQ5DAqbMKFSi39LMRT0Cy", "Music"), ("spotify:page:0JQ5DArNBzkmxXHCqFLx2J", "Podcasts"),
        ("spotify:page:0JQ5DAqbMKFETqK4t8f1n3", "Audiobooks"), ("spotify:concerts", "Live Events"),
        ("spotify:page:0JQ5DAtOnAEpjOgUKwXyxj", "Discover"), ("spotify:page:0JQ5DAqbMKFPw634sFwguI", "EQUAL"),
        ("spotify:page:0JQ5DAqbMKFImHYGo3eTSg", "Fresh Finds"), ("spotify:page:0JQ5DAqbMKFGnsSfvg90Wo", "GLOW"),
        ("spotify:page:0JQ5DAt0tbjZptfcdMSKl3", "Made For You"), ("spotify:page:0JQ5DAqbMKFz6FAsUtgAab", "New Releases"),
        ("spotify:page:0JQ5DAqbMKFOOxftoKZxod", "RADAR"), ("spotify:page:0JQ5DAqbMKFDBgllo2cUIN", "Spotify Singles"),
        ("spotify:page:0JQ5DAqbMKFRKBHIxJ5hMm", "Tastemakers"), ("spotify:page:0JQ5DAqbMKFQIL0AXnG5AK", "Trending"),
        ("spotify:page:0JQ5DAqbMKFNQ0fGp4byGU", "Afro"), ("spotify:page:0JQ5DAqbMKFFtlLYUHv8bT", "Alternative"),
        ("spotify:page:0JQ5DAqbMKFLjmiZRss79w", "Ambient"), ("spotify:page:0JQ5DAqbMKFQ1UFISXj59F", "Arab"),
        ("spotify:page:0JQ5DAqbMKFQiK2EHwyjcU", "Blues"), ("spotify:page:0JQ5DAqbMKFObNLOHydSW8", "Caribbean"),
        ("spotify:page:0JQ5DAqbMKFPrEiAOxgac3", "Classical"), ("spotify:page:0JQ5DAqbMKFKLfwjuJMoNC", "Country"),
        ("spotify:page:0JQ5DAqbMKFHOzuVTgTizF", "Dance/Electronic"), ("spotify:page:0JQ5DAqbMKFCLroFGPFVr5", "Dutch music"),
        ("spotify:page:0JQ5DAqbMKFy78wprEpAjl", "Folk & Acoustic"), ("spotify:page:0JQ5DAqbMKFFsW9N8maB6z", "Funk & Disco"),
        ("spotify:page:0JQ5DAqbMKFQ00XGBls6ym", "Hip-Hop"), ("spotify:page:0JQ5DAqbMKFCWjUTdzaG0e", "Indie"),
        ("spotify:page:0JQ5DAqbMKFAJ5xb0fwo9m", "Jazz"), ("spotify:page:0JQ5DAqbMKFGvOw3O4nLAf", "K-pop"),
        ("spotify:page:0JQ5DAqbMKFxXaXKP7zcDp", "Latin"), ("spotify:page:0JQ5DAqbMKFDkd668ypn6O", "Metal"),
        ("spotify:page:0JQ5DAqbMKFEC4WFtoNRpw", "Pop"), ("spotify:page:0JQ5DAqbMKFAjfauKLOZiv", "Punk"),
        ("spotify:page:0JQ5DAqbMKFEZPnFQSFB1T", "R&B"), ("spotify:page:0JQ5DAqbMKFJKoGyUMo2hE", "Reggae"),
        ("spotify:page:0JQ5DAqbMKFDXXwE9BDJAr", "Rock"), ("spotify:page:0JQ5DAqbMKFIpEuaCnimBj", "Soul"),
        ("spotify:page:0JQ5DAqbMKFSCjnQr8QZ3O", "Songwriters"),
        ("spotify:page:0JQ5DAqbMKFx0uLQR2okcc", "At Home"), ("spotify:page:0JQ5DAqbMKFFzDl7qN9Apr", "Chill"),
        ("spotify:page:0JQ5DAqbMKFRY5ok2pxXJ0", "Cooking & Dining"), ("spotify:page:0JQ5DAqbMKFJ6dHNHTv6Mx", "Fitness"),
        ("spotify:page:0JQ5DAqbMKFCbimwdOYlsl", "Focus"), ("spotify:page:0JQ5DAqbMKFIRybaNTYXXy", "In the car"),
        ("spotify:page:0JQ5DAqbMKFAUsdyVjCQuL", "Love"), ("spotify:page:0JQ5DAqbMKFzHmL4tf05da", "Mood"),
        ("spotify:page:0JQ5DAqbMKFI3pNLtYMD9S", "Nature & Noise"), ("spotify:page:0JQ5DAqbMKFA6SOHvT3gck", "Party"),
        ("spotify:page:0JQ5DAqbMKFCuoRTxhYWow", "Sleep"), ("spotify:page:0JQ5DAqbMKFAQy4HL4XU2D", "Travel"),
        ("spotify:page:0JQ5DAqbMKFLb2EqgLtpjC", "Wellness"), ("spotify:page:0JQ5DAqbMKFAXlCG6QvYQ4", "Workout Music"),
        ("spotify:page:0JQ5DAudkNjCgYMM0TZXDw", "Charts"), ("spotify:page:0JQ5DAB3zgCauRwnvdEQjJ", "Podcast Charts"),
        ("spotify:page:wavee-seed-anime", "Anime"), ("spotify:page:wavee-seed-gaming", "Gaming"),
        ("spotify:page:wavee-seed-kids", "Kids & Family"),
    ];

    /// <summary>Tile colours: the browse category ladder's shape (opaque, saturated mid tones), cycled by index.</summary>
    static readonly uint[] s_tileColors =
    [
        0xFFE8115B, 0xFF1E3264, 0xFF8D67AB, 0xFF148A08, 0xFFBA5D07, 0xFF503750, 0xFF0D73EC, 0xFFE91429,
        0xFF477D95, 0xFFAF2896, 0xFF006450, 0xFF8C1932, 0xFF27856A, 0xFFE13300, 0xFF7358FF, 0xFF1E3264,
    ];

    const string PopPage = "spotify:page:0JQ5DAqbMKFEC4WFtoNRpw";           // Shelves
    const string MadeForYouPage = "spotify:page:0JQ5DAt0tbjZptfcdMSKl3";    // FlattenOne
    const string NewReleasesPage = "spotify:page:0JQ5DAqbMKFz6FAsUtgAab";   // FlattenTwoConcat
    const string FocusPage = "spotify:page:0JQ5DAqbMKFCbimwdOYlsl";         // FlattenTwoStacked

    static void SeedChartsAndBrowse()
    {
        var s = Staging.Rent();
        try
        {
            // ── the five chart sections (ch 11 W24; Featured first — the fail-loud slot) ──
            var featured = StageSection(s, ChartSections.Featured, "Featured Charts", SectionKind.BrowseShelf, true,
                CatalogueRange(0, 4), total: 4, nextOffset: SectionPaging.Complete);
            // Weekly carries a real cursor (10 of 74): the Charts walk and "Show all" arm on it (ch 12 W6).
            var weekly = StageSection(s, ChartSections.Weekly, "Weekly Song Charts", SectionKind.BrowseShelf, true,
                CatalogueRange(4, 10), total: 74, nextOffset: 10);
            var daily = StageSection(s, ChartSections.Daily, "Daily Song Charts", SectionKind.BrowseShelf, true,
                CatalogueRange(14, 8), total: 8, nextOffset: SectionPaging.Complete);
            var nowAvailable = StageSection(s, ChartSections.NowAvailable, "Now available", SectionKind.BrowseShelf, true,
                CatalogueRange(22, 4), total: 4, nextOffset: SectionPaging.Complete);
            var showsChart = new string[6];
            for (int i = 0; i < showsChart.Length; i++) showsChart[i] = ShowUri(i);
            var podcastChart = StageSection(s, ChartSections.Podcast, "", SectionKind.BrowseShelf, true,
                showsChart, total: 6, nextOffset: SectionPaging.Complete);

            // ── the directory: every tile, one run, in wire order ──
            var directory = new StagedId(s.AddText(Utf8(Browse.DirectoryUri)));
            s.Browses.RowFor(directory, Authority.Seed, (uint)BrowseFields.All);
            int tilesStart = s.BrowseChildren.Count;
            for (int i = 0; i < s_browseTiles.Length; i++)
            {
                var (uri, title) = s_browseTiles[i];
                var id = new StagedId(s.AddText(Utf8(uri)));
                ref var tile = ref s.Browses.RowFor(id, Authority.Seed, (uint)BrowseFields.Identity);
                tile.Title = s.AddText(Utf8(title));
                tile.Image = s.AddText(Utf8(Cover(i + 11)));
                tile.Color = s_tileColors[Wrap(i, s_tileColors.Length)];
                tile.Flags = uri == "spotify:concerts" ? (uint)BrowseFlags.ClientFeature : 0;
                s.BrowseChildren.Add() = id;
            }
            BrowseRun(s, in directory, BrowseRelation.Directory, tilesStart, total: s_browseTiles.Length);

            // ── the two chart PAGES own the chart sections (a Charts tile opens real shelves) ──
            SeedPage(s, ChartPages.Charts, 0xFF1F3F5C, [featured, weekly, daily, nowAvailable]);
            SeedPage(s, ChartPages.PodcastCharts, 0xFF3F1F5C, [podcastChart]);

            // ── one category page per BrowsePageLayout mode (ch 13 W19/W20; ch 31 §7.1 row 42) ──
            // Shelves: two titled shelves and a trailing related-categories block.
            var popPlaylists = StageSection(s, "spotify:section:wavee-seed-pop-1", "Popular Pop playlists",
                SectionKind.BrowseShelf, false, CatalogueRange(22, 6), total: 6, nextOffset: SectionPaging.Complete);
            var popAlbums = StageSection(s, "spotify:section:wavee-seed-pop-2", "Pop albums",
                SectionKind.BrowseShelf, false, AlbumRange(0, 6), total: 6, nextOffset: SectionPaging.Complete);
            var popRelated = StageTileSection(s, "spotify:section:wavee-seed-pop-3", "Explore more genres",
                SectionKind.BrowseRelated, tileFrom: 26, count: 6);
            SeedPage(s, PopPage, 0xFF8C1932, [popPlaylists, popAlbums, popRelated]);

            // FlattenOne: the page RETURNED one section, so the page IS that section's whole grid (the single-section
            // rule, RCA 2026-09-30) — seeded WHOLE, exactly as the query layer's walk lands it: the server's end reached.
            var madeForYou = StageSection(s, "spotify:section:wavee-seed-mfy-1", "", SectionKind.BrowseShelf, false,
                CatalogueRange(0, 12), total: 12, nextOffset: SectionPaging.Complete);
            SeedPage(s, MadeForYouPage, 0xFF14485C, [madeForYou]);

            // FlattenTwoConcat: two untitled shelves, neither with more.
            var newA = StageSection(s, "spotify:section:wavee-seed-new-1", "", SectionKind.BrowseShelf, false,
                AlbumRange(0, 7), total: 7, nextOffset: SectionPaging.Complete);
            var newB = StageSection(s, "spotify:section:wavee-seed-new-2", "", SectionKind.BrowseShelf, false,
                AlbumRange(7, 6), total: 6, nextOffset: SectionPaging.Complete);
            SeedPage(s, NewReleasesPage, 0xFF5C4E14, [newA, newB]);

            // FlattenTwoStacked: two untitled shelves, the first with more.
            var focusA = StageSection(s, "spotify:section:wavee-seed-focus-1", "", SectionKind.BrowseShelf, false,
                CatalogueRange(12, 8), total: 20, nextOffset: 8);
            var focusB = StageSection(s, "spotify:section:wavee-seed-focus-2", "", SectionKind.BrowseShelf, false,
                CatalogueRange(20, 6), total: 6, nextOffset: SectionPaging.Complete);
            SeedPage(s, FocusPage, 0xFF1F5C2E, [focusA, focusB]);

            Commit(s);
        }
        finally { Staging.Return(s); }
    }

    static string[] CatalogueRange(int start, int count)
    {
        var uris = new string[count];
        for (int i = 0; i < count; i++) uris[i] = CatalogueUri(start + i);
        return uris;
    }

    static string[] AlbumRange(int start, int count)
    {
        var uris = new string[count];
        for (int i = 0; i < count; i++) uris[i] = AlbumUri(start + i);
        return uris;
    }

    /// <summary>One band with entity cards: the section row (identity + ledger) and its <c>SectionCards</c> run, exactly
    /// as the browse and home folds stage one.</summary>
    static StagedId StageSection(Staging s, string uri, string title, SectionKind kind, bool chart,
        ReadOnlySpan<string> cardUris, int total, int nextOffset)
    {
        var id = new StagedId(s.AddText(Utf8(uri)));
        ref var row = ref s.Sections.RowFor(id, Authority.Seed, SeededSection);
        row.Title = title.Length == 0 ? default : s.AddText(Utf8(title));
        row.Kind = (byte)kind;
        row.Flags = chart ? (byte)SectionFlags.Chart : (byte)0;
        row.Total = total;
        row.Raw = cardUris.Length;
        row.Cards = cardUris.Length;
        row.NextOffset = nextOffset;

        int mark = s.Edges.PendingMark;
        for (int i = 0; i < cardUris.Length; i++) s.Edges.Push().Target = new StagedId(s.AddText(Utf8(cardUris[i])));
        s.Edges.Close(Relation.SectionCards, in id, mark);
        return id;
    }

    /// <summary>One band of category tiles (a grid or a related block): the section row and its
    /// <see cref="BrowseRelation.SectionCategories"/> run over tiles the directory already stages.</summary>
    static StagedId StageTileSection(Staging s, string uri, string title, SectionKind kind, int tileFrom, int count)
    {
        var id = new StagedId(s.AddText(Utf8(uri)));
        ref var row = ref s.Sections.RowFor(id, Authority.Seed, SeededSection);
        row.Title = s.AddText(Utf8(title));
        row.Kind = (byte)kind;
        row.Total = count;
        row.Raw = count;
        row.Cards = count;
        row.NextOffset = SectionPaging.Complete;

        int start = s.BrowseChildren.Count;
        for (int i = 0; i < count; i++)
            s.BrowseChildren.Add() = new StagedId(s.AddText(Utf8(s_browseTiles[Wrap(tileFrom + i, s_browseTiles.Length)].Uri)));
        BrowseRun(s, in id, BrowseRelation.SectionCategories, start, total: count);
        return id;
    }

    /// <summary>A category page: its page group (accent + section ledger, one page of sections, complete) and the run
    /// that lists its bands. The tile identity (title, colour) comes from the directory's own row for the same uri.</summary>
    static void SeedPage(Staging s, string pageUri, uint accent, ReadOnlySpan<StagedId> sections)
    {
        var page = new StagedId(s.AddText(Utf8(pageUri)));
        ref var row = ref s.Browses.RowFor(page, Authority.Seed, (uint)BrowseFields.Page);
        row.Accent = accent;
        row.TotalSections = sections.Length;
        row.NextSectionOffset = SectionPaging.Complete;

        int start = s.BrowseChildren.Count;
        for (int i = 0; i < sections.Length; i++) s.BrowseChildren.Add() = sections[i];
        BrowseRun(s, in page, BrowseRelation.PageSections, start, total: sections.Length);
    }

    static void BrowseRun(Staging s, in StagedId parent, BrowseRelation relation, int start, int total)
    {
        ref var run = ref s.BrowseRuns.Add();
        run.Parent = parent;
        run.Relation = relation;
        run.Start = start;
        run.Length = s.BrowseChildren.Count - start;
        run.Offset = -1;                                   // a whole Replace: the relation settles Complete
        run.Total = total;
    }

    // ── 5. the podium: the account's top artists and tracks, ranked (ch 11 W13) ────────────────────────────────────

    static void SeedTopContent()
    {
        int me = Current.MeSlot;
        if (me == Table.None) return;
        Span<int> artists = stackalloc int[10];
        Span<int> tracks = stackalloc int[10];
        for (int i = 0; i < 10; i++)
        {
            artists[i] = ResolveSeedSlot(EntityKind.Artist, ArtistUri(i));
            tracks[i] = ResolveSeedSlot(EntityKind.Track, TrackUri(i * 11));
        }
        Current.Edges.UserTopArtists.ReplaceRun(me, artists, default);
        Current.Edges.UserTopTracks.ReplaceRun(me, tracks, default);
    }

    // ── 6. search "a" (ch 13 W1-W10; ch 31 §2 W9, §7.1 row 41) ──────────────────────────────────────────────────────

    /// <summary>The query the gate probes. Any other query stays unanswered under --fake (file header).</summary>
    public const string FakeSearchQuery = "a";

    static readonly string[] s_relatedQueries = ["a day to remember", "arctic monkeys", "adele", "a-ha", "ariana grande"];

    static void SeedSearch()
    {
        ReadOnlySpan<char> q = FakeSearchQuery.AsSpan();
        var all = Search(q);

        const int trackHits = 30, artistHits = ArtistCount, albumHits = AlbumCount, playlistHits = 20,
                  showHits = ShowCount, profileHits = 4, genreHits = 8;

        // The chip strip, in the SERVER's rank order, each total agreeing with the list the facet row holds (ch 31 §0.8).
        all.SetChips(
            [SearchFacet.Tracks, SearchFacet.Artists, SearchFacet.Albums, SearchFacet.Playlists, SearchFacet.Podcasts,
             SearchFacet.Profiles, SearchFacet.Genres],
            [trackHits, artistHits, albumHits, playlistHits, showHits, profileHits, genreHits]);

        // The All facet: a ranked mix over five kinds, the first hit the Top Result.
        Span<EntityRef> mixed =
        [
            new(EntityKind.Artist, ResolveSeedSlot(EntityKind.Artist, ArtistUri(3))),
            new(EntityKind.Track, ResolveSeedSlot(EntityKind.Track, TrackUri(1))),
            new(EntityKind.Album, ResolveSeedSlot(EntityKind.Album, AlbumUri(2))),
            new(EntityKind.Playlist, ResolveSeedSlot(EntityKind.Playlist, CatalogueUri(10))),
            new(EntityKind.Show, ResolveSeedSlot(EntityKind.Show, ShowUri(1))),
            new(EntityKind.Track, ResolveSeedSlot(EntityKind.Track, TrackUri(5))),
            new(EntityKind.Album, ResolveSeedSlot(EntityKind.Album, AlbumUri(4))),
            new(EntityKind.Artist, ResolveSeedSlot(EntityKind.Artist, ArtistUri(0))),
            new(EntityKind.Playlist, ResolveSeedSlot(EntityKind.Playlist, PlaylistUri(1))),
            new(EntityKind.Track, ResolveSeedSlot(EntityKind.Track, TrackUri(8))),
        ];
        all.ApplyResults(0, mixed, mixed.Length);

        SeedFacetHits(q, SearchFacet.Tracks, EntityKind.Track, trackHits, static i => TrackUri(i * 5));
        SeedFacetHits(q, SearchFacet.Artists, EntityKind.Artist, artistHits, static i => ArtistUri(i));
        SeedFacetHits(q, SearchFacet.Albums, EntityKind.Album, albumHits, static i => AlbumUri(i));
        SeedFacetHits(q, SearchFacet.Playlists, EntityKind.Playlist, playlistHits, static i => CatalogueUri(10 + i));
        SeedFacetHits(q, SearchFacet.Podcasts, EntityKind.Show, showHits, static i => ShowUri(i));
        SeedFacetHits(q, SearchFacet.Profiles, EntityKind.User, profileHits, static i => i < 3 ? UserUri(i + 1) : SpotifyOwnerUri);

        // Genres: browse NODES (a genre uri and its page uri are one row, Browse.cs), the directory's own Genres band.
        Span<int> genres = stackalloc int[genreHits];
        for (int i = 0; i < genres.Length; i++) genres[i] = BrowseNode(s_browseTiles[14 + i * 3].Uri.AsSpan()).Slot;
        Current.Edges.SearchGenres.ReplaceRun(all.Slot, genres, default);

        // Related searches: payload-only (targets unused).
        Span<int> none = stackalloc int[s_relatedQueries.Length];
        Span<SearchRelatedEdge> related = new SearchRelatedEdge[s_relatedQueries.Length];
        for (int i = 0; i < related.Length; i++)
        {
            none[i] = Table.None;
            StringId text = default;
            RetainText(ref text, Intern(Utf8(s_relatedQueries[i])));
            related[i] = new SearchRelatedEdge(text);
        }
        Current.Edges.SearchRelated.ReplaceRun(all.Slot, none, related);

        Current.Searches.Bump(all.Slot, (uint)(SearchFields.Results | SearchFields.Genres | SearchFields.Related));
    }

    static void SeedFacetHits(ReadOnlySpan<char> query, SearchFacet facet, EntityKind kind, int count, Func<int, string> uriOf)
    {
        var row = Search(query, facet);
        var slots = new int[count];
        for (int i = 0; i < count; i++) slots[i] = ResolveSeedSlot(kind, uriOf(i));
        row.ApplyResults(0, slots, kind, count);
        Current.Searches.Bump(row.Slot, (uint)SearchFields.Results);
    }

    // ── 7. recents (ch 16 W1-W16; ch 31 §2 W16, §7.1 row 45) ────────────────────────────────────────────────────────

    /// <summary>One recents fixture row: what it points at, when (seconds before now0), why, which axis, and the plays a
    /// group collapsed (member track indices, each a further 5 minutes back).</summary>
    readonly record struct RecentsSeedRow(EntityKind Kind, string Uri, long SecondsAgo, RecentsReason Reason,
        RecentsContentType Axis, int ChildCount, int[] Members);

    // Rows 0, 1, 2, 5 and 8 point at the SAME real catalogue uris `SeedHomeMockupAll` stages for the RecentGrid/
    // Jump-back-in zones (Kris Kross Amsterdam, WLUWD, 90's Nederlandstalig, Arash, Arcane League of Legends: Season
    // 2) — the mockup canvas board
    // (docs/plans/wavee/home-redesign/canvas/Main.dc.html) shows exactly these four in its RecentGrid, so a screenshot
    // is comparable card-for-card even though the day-offsets below (kept from the pre-mockup fixture, to avoid moving
    // every downstream day-bucket assertion in EntitiesFakeHomeTests) don't reproduce the canvas's own Wed/Tue/Mon
    // captions verbatim — the fixed seed epoch (Saturday) isn't the capture's Friday, so exact weekday text can't
    // match regardless of which offsets are chosen.
    static readonly RecentsSeedRow[] s_recentsSeed =
    [
        new(EntityKind.Artist, "spotify:artist:4LcUpNlXFEleaLlelmkv2R", 40 * 60, RecentsReason.Played, RecentsContentType.Music, 0, []),
        // ChildCount 5 over 3 members: the server truncates the member list and states the real count (ch 16 §7.2).
        new(EntityKind.Album, "spotify:album:2SxoeF005n621Jca66RRdu", 5 * 3600, RecentsReason.Played, RecentsContentType.Music, 5, [26, 27, 28]),
        new(EntityKind.Playlist, "spotify:playlist:37i9dQZF1DXaQpIUzyByme", 26 * 3600, RecentsReason.Saved, RecentsContentType.Music, 3, [60, 61, 62]),
        new(EntityKind.Show, "spotify:show:sh1", 3 * 86_400, RecentsReason.Played, RecentsContentType.Podcasts, 0, []),
        new(EntityKind.Playlist, "spotify:playlist:pl2", 3 * 86_400 + 2 * 3600, RecentsReason.Played, RecentsContentType.Music, 4, [44, 45, 46, 47]),
        new(EntityKind.Artist, "spotify:artist:7hQmAXAzWI6D350VTgkKTG", 9 * 86_400, RecentsReason.Played, RecentsContentType.Music, 0, []),
        new(EntityKind.Track, "spotify:track:tr40", 9 * 86_400 + 3 * 3600, RecentsReason.Played, RecentsContentType.Music, 0, []),
        new(EntityKind.Show, "spotify:show:sh5", 12 * 86_400, RecentsReason.Played, RecentsContentType.Podcasts, 0, []),
        new(EntityKind.Album, "spotify:album:2x6LWti2bjYS6AllSomoV7", 40 * 86_400, RecentsReason.Played, RecentsContentType.Music, 10, [90, 91]),
        new(EntityKind.Track, "spotify:track:tr77", 41 * 86_400, RecentsReason.Played, RecentsContentType.Music, 0, []),
    ];

    static void SeedRecents(long now0)
    {
        var s = Staging.Rent();
        try
        {
            int rowStart = s.RecentsRows.Count, memberStart = s.RecentsMembers.Count;
            for (int i = 0; i < s_recentsSeed.Length; i++)
            {
                var seed = s_recentsSeed[i];
                long playedAtMs = (now0 - seed.SecondsAgo) * 1000L;
                int membersAt = s.RecentsMembers.Count - memberStart;
                for (int m = 0; m < seed.Members.Length; m++)
                {
                    ref var member = ref s.RecentsMembers.Add();
                    member.Id = new StagedId(s.AddText(Utf8(TrackUri(seed.Members[m]))));
                    member.ItemId = s.AddText(Utf8(ItemIdOf(i * 16 + m + 1)));
                    member.PlayedAtMs = playedAtMs - m * 5L * 60_000;
                }

                ref var row = ref s.RecentsRows.Add();
                row.Id = new StagedId(s.AddText(Utf8(seed.Uri)));
                row.ItemId = s.AddText(Utf8(ItemIdOf(i * 16)));
                row.PlayedAtMs = playedAtMs;
                row.ChildCount = seed.ChildCount;
                row.MembersStart = seed.Members.Length > 0 ? membersAt : 0;
                row.MembersLen = seed.Members.Length;
                row.Reason = (byte)seed.Reason;
                row.ContentType = (byte)seed.Axis;
                row.Kind = (byte)(seed.Members.Length > 0 ? RecentsRowKind.Group : RecentsRowKind.Single);
            }

            ref var page = ref s.RecentsPages.Add();
            page.Parent = new StagedId(s.AddText(Utf8(FakeAccount)));
            page.Revision = s.AddText("0a1b2c"u8);
            page.RowStart = rowStart;
            page.RowCount = s.RecentsRows.Count - rowStart;
            page.MemberStart = memberStart;
            page.MemberCount = s.RecentsMembers.Count - memberStart;

            Commit(s);
        }
        finally { Staging.Return(s); }
    }

    /// <summary>A stable lowercase-hex <c>item_id</c> per fixture index (uris repeat in a real list; the id never does).</summary>
    static string ItemIdOf(int i) => "5eed" + ((uint)Wrap(i, 1 << 20)).ToString("x8");

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A bundled capture next to the exe (<c>assets/spotify/</c>, copied by Wavee.csproj's Content glob), or null
    /// when a stripped build does not carry it. A fixture file, read once, never state (file header).</summary>
    static byte[]? ReadBundledCapture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "spotify", name);
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
