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
        SeedHomeFacets();
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

            // The daylist window (ch 31 §2 W16). Full authority: the capture's own playlist answer may already speak for
            // the group, and this fixture exists precisely to override the window it lacks.
            ref var daylist = ref s.Playlists.RowFor(s.AddText(Utf8(DaylistUri)), Authority.Full, (uint)PlaylistFields.Daylist);
            daylist.DaylistExpiresAt = (int)(now0 + 4 * 3600 + 37 * 60);
            daylist.DaylistCreatedAt = (int)(now0 - (3 * 3600 + 23 * 60));

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

        // "Following" keeps the first two music sections that are real shelves (more than one card).
        var following = new List<int>(2);
        foreach (int slot in music)
        {
            if (following.Count == 2) break;
            if (new Section(slot).Cards > 1) following.Add(slot);
        }

        SeedFacet(home, "music-chip", music);
        SeedFacet(home, "music-following-chip", following);
        SeedFacet(home, "podcasts-chip", podcasts);
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

            // FlattenOne: ONE untitled shelf that pages (12 of 30) — the grid + the silent append preloader.
            var madeForYou = StageSection(s, "spotify:section:wavee-seed-mfy-1", "", SectionKind.BrowseShelf, false,
                CatalogueRange(0, 12), total: 30, nextOffset: 12);
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
        ref var row = ref s.Sections.RowFor(id, Authority.Seed, (uint)SectionFields.Identity);
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
        ref var row = ref s.Sections.RowFor(id, Authority.Seed, (uint)SectionFields.Identity);
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

    static readonly RecentsSeedRow[] s_recentsSeed =
    [
        new(EntityKind.Track, "spotify:track:tr3", 40 * 60, RecentsReason.Played, RecentsContentType.Music, 0, []),
        // ChildCount 5 over 3 members: the server truncates the member list and states the real count (ch 16 §7.2).
        new(EntityKind.Album, "spotify:album:al2", 5 * 3600, RecentsReason.Played, RecentsContentType.Music, 5, [26, 27, 28]),
        new(EntityKind.Playlist, "spotify:playlist:pl0", 26 * 3600, RecentsReason.Saved, RecentsContentType.Music, 3, [60, 61, 62]),
        new(EntityKind.Show, "spotify:show:sh1", 3 * 86_400, RecentsReason.Played, RecentsContentType.Podcasts, 0, []),
        new(EntityKind.Playlist, "spotify:playlist:pl2", 3 * 86_400 + 2 * 3600, RecentsReason.Played, RecentsContentType.Music, 4, [44, 45, 46, 47]),
        new(EntityKind.Artist, "spotify:artist:ar4", 9 * 86_400, RecentsReason.Played, RecentsContentType.Music, 0, []),
        new(EntityKind.Track, "spotify:track:tr40", 9 * 86_400 + 3 * 3600, RecentsReason.Played, RecentsContentType.Music, 0, []),
        new(EntityKind.Show, "spotify:show:sh5", 12 * 86_400, RecentsReason.Played, RecentsContentType.Podcasts, 0, []),
        new(EntityKind.Album, "spotify:album:al5", 40 * 86_400, RecentsReason.Played, RecentsContentType.Music, 10, [90, 91]),
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
