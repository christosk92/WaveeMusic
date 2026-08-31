using System;
using System.Linq;

namespace Wavee.Core;

/// <summary>The in-memory, deterministic catalog that drives the whole skeleton with NO network. Cover art is BUNDLED
/// locally (app/Wavee/assets/covers, copied next to the exe) so cards show real art offline; <see cref="Cover"/>
/// resolves a deterministic file path per seed.</summary>
public static class FakeData
{
    /// <summary>How many cover images are bundled under assets/covers (cover00.jpg … coverNN.jpg).</summary>
    const int CoverCount = 16;
    static int Wrap(int i, int count) => ((i % count) + count) % count;
    static readonly (string Title, string Artist)[] Seed =
    [
        ("LAST GOODBYE", "sunkis"), ("mellow pop wistful saturday late night", "Spotify"), ("\uC6B0\uC6B8\uD574", "Christos"),
        ("Iced Americano", "Christos"), ("Dalkom Cafe", "Christos"), ("Nostalgia 2000s Mix", "Spotify"),
        ("Strobe", "deadmau5"), ("Innerbloom", "RÜFÜS DU SOL"), ("Breathe", "Télépopmusik"),
        ("Lebanese Blonde", "Thievery Corporation"), ("Undo", "Björk"), ("Genesis", "Grimes"),
        ("An Ending (Ascent)", "Brian Eno"), ("Svefn-g-englar", "Sigur Rós"), ("Intro", "The xx"),
        ("Weird Fishes", "Radiohead"),
    ];

    public static Image Cover(int i, int px = 640)
    {
        int n = Wrap(i, CoverCount);   // deterministic, handles any seed sign
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "covers", $"cover{n:D2}.jpg");
        return new Image(path, px, px);
    }

    static ArtistRef ArtistRef(int i)
    {
        int n = Wrap(i, Seed.Length);
        var s = Seed[n];
        return new ArtistRef($"ar{n}", $"spotify:artist:ar{n}", s.Artist);
    }

    public static Track Track(int i)
    {
        var s = Seed[Wrap(i, Seed.Length)];
        var album = new AlbumRef($"al{i}", $"spotify:album:al{i}", s.Title);
        long dur = 138_000 + (i * 37 % 150) * 1000L;       // 2:18 – ~4:50
        // No has-video here: it is not a property of the row. The fake backend has no association plane, so a fake
        // track honestly reports "no video" — which is also what the real app shows before a detect pass lands.
        //
        // Facts data, deterministic per index, so the detail rail's years / tempo / blend facts (and the perf bench that
        // opens a fake playlist) run their full path offline: a release year over 17 years, a tempo in three humps
        // (~96 / 128 / 172 bpm — what real kind-222 payloads look like across a mixed list), a Camelot slot with the
        // wire's per-slot colour, and one primary descriptor.
        int hump = i % 3;
        double bpm = (hump == 0 ? 96 : hump == 1 ? 128 : 172) + (i % 7) - 3;
        // Wrap, don't raw-mod: a URI-hashed index (IndexFromUri) can be huge, and the Album/track offset
        // multiplications (i*6, i*10) can overflow Int32 to NEGATIVE — a negative `i % 12` would make slot 0 or less
        // and index CamelotKeys/Colors[-1] (the crash on ArtistPage's pending-shape path). A fake generator must be
        // total for any int, so every array index below wraps into range.
        int slot = 1 + Wrap(i, 12);
        string ring = i % 5 < 3 ? "B" : "A";
        return new Track($"tr{i}", $"spotify:track:tr{i}", s.Title, [ArtistRef(i)], album, dur, i % 6 == 0, Cover(i, 64),
            TempoBpm: bpm, MusicalKey: CamelotKeys[slot - 1], CamelotCode: slot + ring, CamelotColor: CamelotColors[slot - 1],
            Tags: [Descriptors[Wrap(i, Descriptors.Length)]], Year: 2008 + i % 17);
    }

    static readonly string[] Descriptors = ["Pop", "Dance", "Indie", "Hip Hop", "Rock", "Electronic"];
    // The Camelot B ring's key names (1B = B … 12B = E) and one opaque colour per slot (the wire's own hue family).
    static readonly string[] CamelotKeys = ["B", "F#", "Db", "Ab", "Eb", "Bb", "F", "C", "G", "D", "A", "E"];
    static readonly uint[] CamelotColors =
    [
        0xFF05ECCBu, 0xFF14D7F0u, 0xFF56D9F8u, 0xFF8FB8FFu, 0xFFB8A0FFu, 0xFFE08CFFu,
        0xFFFF80B4u, 0xFFFF8C8Cu, 0xFFFFA860u, 0xFFF5CE4Eu, 0xFFC8E45Au, 0xFF7EE787u,
    ];

    public static Track[] Tracks(int count, int offset = 0)
    {
        var a = new Track[count];
        for (int i = 0; i < count; i++) a[i] = Track(offset + i);
        return a;
    }

    // A deterministic release shape per index, so the catalog spans all four kinds (and the More-by/home shelves show a mix).
    static (AlbumKind Kind, int Count) AlbumShape(int i) => (i % 6) switch
    {
        0 => (AlbumKind.Single, 1),
        1 => (AlbumKind.EP, 5),
        2 => (AlbumKind.Album, 12),
        3 => (AlbumKind.Single, 2),
        4 => (AlbumKind.Compilation, 18),
        _ => (AlbumKind.Album, 10),
    };

    static Track[] AlbumTracks(int count, int offset, ArtistRef artist, bool various)
    {
        var a = new Track[count];
        for (int i = 0; i < count; i++)
        {
            var t = Track(offset + i);
            long plays = 60_000_000L / (i + 1) + ((offset * 131 % 37) + 1) * 250_000L;   // descending → track 1 is the "hit"
            // A compilation is various-artists (each track keeps its own artist); a single/EP/album is one artist.
            a[i] = t with { PlayCount = plays, Artists = various ? t.Artists : [artist] };
        }
        return a;
    }

    public static Album Album(int i)
    {
        var s = Seed[Wrap(i, Seed.Length)];
        var (kind, count) = AlbumShape(i);
        var artist = ArtistRef(i);
        var tracks = AlbumTracks(count, i * 10, artist, kind == AlbumKind.Compilation);
        return new Album($"al{i}", $"spotify:album:al{i}", s.Title, Cover(i, 300), [artist], 2014 + (i % 11), tracks.Length, tracks, kind);
    }

    // A LARGE synthetic discography per artist so the artist-page grid genuinely virtualizes (hundreds of singles like the
    // real Spotify shape). Deterministic per (artist, kind, index); returns the window [offset, offset+limit) + facet total.
    public static DiscographyPage Discography(string artistUri, DiscographyKind kind, int offset, int limit)
    {
        int seed = ((IndexFromUri(artistUri) % Seed.Length) + Seed.Length) % Seed.Length;
        int total = kind switch
        {
            DiscographyKind.Albums => 60 + (seed * 7) % 40,
            DiscographyKind.Singles => 380 + (seed * 53) % 320,
            _ => 110 + (seed * 17) % 120,
        };
        offset = Math.Clamp(offset, 0, total);
        int n = Math.Clamp(limit, 0, total - offset);
        var items = new Album[n];
        for (int i = 0; i < n; i++) items[i] = DiscographyAlbum(seed, kind, offset + i);
        return new DiscographyPage(items, total);
    }

    static Album DiscographyAlbum(int artistSeed, DiscographyKind kind, int idx)
    {
        int g = (artistSeed * 131 + idx * 31) & 0x7fffffff;
        var s = Seed[g % Seed.Length];
        var artist = ArtistRef(artistSeed);
        var k = kind switch { DiscographyKind.Singles => AlbumKind.Single, DiscographyKind.Compilations => AlbumKind.Compilation, _ => AlbumKind.Album };
        int trackCount = kind == DiscographyKind.Singles ? 1 + g % 3 : 6 + g % 18;
        var tracks = AlbumTracks(trackCount, idx * 10, artist, k == AlbumKind.Compilation);
        string id = $"disc{artistSeed}_{(int)kind}_{idx}";
        return new Album(id, $"spotify:album:{id}", s.Title, Cover(g, 300), [artist], 2000 + g % 26, tracks.Length, tracks, k);
    }

    public static Artist Artist(int i)
    {
        var s = Seed[Wrap(i, Seed.Length)];
        var albums = new Album[6];
        for (int k = 0; k < albums.Length; k++) albums[k] = Album(i * 6 + k);
        int h = Math.Abs(s.Artist.GetHashCode());
        long monthly = 850_000L + (h % 32) * 940_000L;          // ~0.85M – ~30M monthly listeners
        long followers = monthly / 2 + (h % 11) * 130_000L;
        bool verified = h % 5 != 0;
        int rank = h % 7 == 0 ? 0 : 1 + h % 500;                // some artists have no world rank
        // The full magazine facets, synthesized deterministically (each gated so artists differ). The real Spotify
        // export overrides these for exported artists; here every fake artist still gets a rich page (square art).
        int n = Wrap(i, Seed.Length);
        // Latest = newest-by-year album; popular = first few distinct cards (offline stand-in for Pathfinder facets).
        Album? latest = albums.Length > 0
            ? albums.OrderByDescending(a => a.Year).ThenBy(a => a.Name, StringComparer.Ordinal).First()
            : null;
        var popular = albums.Take(4).ToArray();
        return new Artist($"ar{n}", $"spotify:artist:ar{n}", s.Artist, Cover(i, 300), albums,
            monthly, followers, ArtistBio(s.Artist, monthly), verified,
            WorldRank: rank, HeaderImage: Cover(i, 640), TopTracks: null,
            AppearsOn: null, Pinned: SynthPinned(h, albums), Extras: SynthExtras(i, s.Artist, h, monthly, albums),
            LatestRelease: latest, PopularReleases: popular.Length > 0 ? popular : null);
    }

    // ── synthesized "magazine" facets (fallback for artists with no real Spotify export) ─────────────────────────
    /// <summary>The "On tour now" banner copy derived from a concert list (mirrors WaveeMusic's tour-banner formatter:
    /// 1 date → a show, 2–3 → dates, ≥4 with the next within a week → on-tour-now, else an upcoming tour).</summary>
    public static TourBanner? TourBannerFor(string name, IReadOnlyList<Concert>? concerts)
    {
        if (concerts is not { Count: > 0 }) return null;
        int n = concerts.Count;
        var next = concerts[0];
        foreach (var c in concerts) if (c.Date < next.Date) next = c;
        bool soon = (next.Date - DateTimeOffset.Now).TotalDays <= 7 && next.Date >= DateTimeOffset.Now;
        string eyebrow = n == 1 ? "UPCOMING SHOW" : n <= 3 ? "UPCOMING DATES" : soon ? "ON TOUR NOW" : "UPCOMING TOUR";
        string when = next.Date.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
        return new TourBanner(eyebrow, $"{name} — on tour", $"Next: {when} · {next.Venue} · {next.City} · {n} dates total", soon);
    }

    static ArtistExtras SynthExtras(int i, string name, int h, long monthly, Album[] albums)
    {
        var concerts = h % 4 != 0 ? SynthConcerts(name, h) : null;
        return new ArtistExtras(
            Concerts: concerts,
            Merch: h % 3 != 1 ? SynthMerch(i, h) : null,
            Playlists: h % 2 == 0 ? SynthPlaylists(i, name) : null,
            MusicVideos: h % 5 != 2 ? SynthVideos(albums) : null,
            TopCities: h % 2 == 1 ? SynthCities(h, monthly) : null,
            ExternalLinks: SynthLinks(name),
            Gallery: h % 3 != 2 ? SynthGallery(i, h) : null,
            Related: null,                                       // the page falls back to the cached artists pool
            Tour: TourBannerFor(name, concerts));
    }

    static readonly (string City, string Venue)[] CityVenues =
    [
        ("Berlin", "Mercedes-Benz Arena"), ("London", "The O2"), ("Amsterdam", "Ziggo Dome"),
        ("Paris", "Accor Arena"), ("Milano", "Mediolanum Forum"), ("Madrid", "WiZink Center"),
        ("New York", "Madison Square Garden"), ("Tokyo", "Nippon Budokan"), ("São Paulo", "Allianz Parque"),
        ("Sydney", "Qudos Bank Arena"),
    ];

    static IReadOnlyList<Concert> SynthConcerts(string name, int h)
    {
        int n = 3 + h % 8;
        var now = DateTimeOffset.Now;
        var list = new List<Concert>(n);
        for (int k = 0; k < n; k++)
        {
            var (city, venue) = CityVenues[(h + k) % CityVenues.Length];
            list.Add(new Concert($"wavee:concert:{h}:{k}", name, venue, city, now.AddDays(4 + k * 9 + h % 5), IsFestival: k == 2));
        }
        return list;
    }

    static readonly string[] MerchNames = ["Tour Tee", "Vinyl LP", "Hoodie", "Cap", "Poster", "Enamel Pin"];
    static IReadOnlyList<MerchItem> SynthMerch(int i, int h)
    {
        int n = 3 + h % 4;
        var list = new List<MerchItem>(n);
        for (int k = 0; k < n; k++)
            list.Add(new MerchItem(MerchNames[(h + k) % MerchNames.Length], "$" + (19 + k * 5) + ".00",
                "Official merchandise.", Cover(i + k + 5, 300), null));
        return list;
    }

    static IReadOnlyList<PlaylistRef> SynthPlaylists(int i, string name)
    {
        string[] names = [$"This Is {name}", $"{name} Radio", $"{name} Mix", "Top Pop"];
        string[] subs = ["Spotify · official", "Spotify · featured", "Discovered on", "Made for you"];
        var list = new List<PlaylistRef>(4);
        for (int k = 0; k < 4; k++)
            list.Add(new PlaylistRef($"spotify:playlist:pl{(i + k) % 8}", names[k], Cover(i + k + 100, 300), subs[k]));
        return list;
    }

    static IReadOnlyList<MusicVideo> SynthVideos(Album[] albums)
    {
        var list = new List<MusicVideo>(4);
        foreach (var al in albums)
        {
            if (al.Tracks is not { Count: > 0 } ts) continue;
            var t = ts[0];
            list.Add(new MusicVideo(t.Uri, t.Title, al.Cover, t.DurationMs, t.IsExplicit));
            if (list.Count >= 4) break;
        }
        return list;
    }

    static readonly string[] Cities = ["São Paulo", "London", "Mexico City", "Sydney", "Quezon City", "Jakarta", "Istanbul"];
    static IReadOnlyList<TopCity> SynthCities(int h, long monthly)
    {
        var list = new List<TopCity>(5);
        for (int k = 0; k < 5; k++)
            list.Add(new TopCity(Cities[(h + k) % Cities.Length], null, Math.Max(1L, monthly / 30 / (k + 1))));
        return list;
    }

    static IReadOnlyList<ExternalLink> SynthLinks(string name)
    {
        string slug = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (slug.Length == 0) slug = "artist";
        return
        [
            new ExternalLink("Instagram", "https://instagram.com/" + slug, ExternalLinkKind.Instagram),
            new ExternalLink("Twitter", "https://twitter.com/" + slug, ExternalLinkKind.Twitter),
            new ExternalLink("Wikipedia", "https://en.wikipedia.org/wiki/" + slug, ExternalLinkKind.Wikipedia),
        ];
    }

    static IReadOnlyList<Image> SynthGallery(int i, int h)
    {
        int n = 4 + h % 5;
        var list = new List<Image>(n);
        for (int k = 0; k < n; k++) list.Add(Cover(i + k + 3, 480));
        return list;
    }

    static PinnedItem? SynthPinned(int h, Album[] albums)
    {
        if (h % 3 == 1 || albums.Length == 0) return null;
        var al = albums[0];
        string kind = al.Kind switch { AlbumKind.Single => "Single", AlbumKind.EP => "EP", AlbumKind.Compilation => "Compilation", _ => "Album" };
        return new PinnedItem("Pinned", al.Name, $"{kind} · {al.Year}", "Out now — give it a listen.", al.Cover, al.Uri);
    }

    static string ArtistBio(string name, long monthly) =>
        $"{name} is an artist whose sound moves between intimate, late-night textures and wide, festival-scale moments. " +
        $"With {monthly:N0} monthly listeners, the catalogue spans early EPs, breakout singles and the records that " +
        $"defined the run — built for headphones at 2am and crowds at midnight alike.";

    /// <summary>An artist's "Popular" list: the highest-played, title-deduped tracks across their discography — the SAME
    /// ordered set the artist context plays (see <see cref="ContextTracks"/>), so the artist page and playback agree.</summary>
    public static IReadOnlyList<Track> TopTracksOf(Artist a, int count = 5)
    {
        if (a.TopAlbums is not { Count: > 0 } albums) return Array.Empty<Track>();
        var all = new List<Track>();
        foreach (var al in albums) if (al.Tracks is { } ts) all.AddRange(ts);
        all.Sort((x, y) => y.PlayCount.CompareTo(x.PlayCount));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var top = new List<Track>(count);
        foreach (var t in all) { if (seen.Add(t.Title)) { top.Add(t); if (top.Count >= count) break; } }
        return top;
    }

    public static Playlist Playlist(int i, int trackCount = 40)
    {
        var s = Seed[Wrap(i, Seed.Length)];
        // Match the (name, owner, count) the home/sidebar PlaylistSummary advertises for THIS uri, so the detail page's
        // pre-loaded header (cover/title/owner/count from the clicked card) does NOT get swapped for a different
        // playlist when the full model loads. (Real backends are consistent for a uri; the fake catalog must be too.)
        string? name = null; string? owner = null; int count = trackCount;
        foreach (var p in PlaylistSeed) if (p.Seed == i) { name = p.Name; owner = p.Owner; count = p.Count; break; }
        name ??= $"{s.Title} Mix";
        owner ??= s.Artist == "Spotify" ? "Spotify" : "Christos";

        // Editorial/made-for-you playlists (a "Spotify" owner) carry NO per-track added-at/added-by — the detail page
        // then hides those columns. A user playlist stamps a descending "added at" (newest first); some are
        // collaborative (≥2 contributors), which is what makes the page reveal the Added-By column.
        bool curated = owner == "Spotify";
        var tracks = Tracks(count, i * 100);
        if (!curated)
        {
            bool collab = i % 3 == 1;
            string[] people = collab ? ["Christos", "Alex", "Mia"] : ["Christos"];
            var now = DateTimeOffset.Now;
            var stamped = new Track[tracks.Length];
            for (int t = 0; t < tracks.Length; t++)
                stamped[t] = tracks[t] with { AddedAt = now.AddDays(-(t * 2L + (i % 5))), AddedBy = people[t % people.Length] };
            tracks = stamped;
        }
        return new Playlist($"pl{i}", $"spotify:playlist:pl{i}", name, $"The best of {s.Artist} and friends.",
            owner, Cover(i + 100, 300), tracks.Length, tracks);
    }

    /// <summary>The big list (Liked Songs) — generated on demand so 50k stays cheap.</summary>
    public static Track[] LikedSongs(int count = 5000) => Tracks(count, 1000);

    // ── local files (the peer source: docs/plans/wavee/architecture.md "Local files") ────────────────────────────────────────────
    // Synthetic "imported" tracks — distinct names from the streamed catalog, TrackOrigin.Local, Source="local", and
    // wavee:local:* uris. The LocalSource serves these; ContextTracks resolves a local context so they actually play.
    static readonly (string Title, string Artist)[] LocalSeed =
    [
        ("Sunset Boulevard", "Marble Sounds"), ("Paper Planes", "Hollow Coves"), ("Northern Lights", "Aurora Fields"),
        ("Coastline", "Sea Glass"), ("Old Cassette", "Lo-Fi Attic"), ("Rainy Window", "Kettle & Keys"),
        ("First Snow", "Pinecone"), ("Long Drive Home", "Headlights"), ("Attic Tapes", "Dust & Vinyl"),
        ("Quiet Hours", "Night Shift"), ("Garden Path", "Greenhouse"), ("Backroads", "Gravel & Pine"),
        ("Harbor Lights", "Lantern"), ("Morning Pages", "Inkwell"),
    ];

    static Track[]? _localCache;
    public static IReadOnlyList<Track> LocalTracks()
    {
        if (_localCache is not null) return _localCache;
        var list = new Track[LocalSeed.Length];
        for (int i = 0; i < LocalSeed.Length; i++)
        {
            var (title, artist) = LocalSeed[i];
            var ar = new ArtistRef("localar" + i, "wavee:local:artist:" + i, artist);
            var al = new AlbumRef("localal" + i, "wavee:local:album:" + i, title);
            long dur = 150_000 + (i * 41 % 140) * 1000L;
            // Availability stated, not defaulted: it is nullable ("no verdict received") and a synthetic local file is
            // playable by construction, so leaving it unknown would let a playable-only filter hide the fake library.
            list[i] = new Track("localtr" + i, "wavee:local:track:" + i, title, new[] { ar }, al, dur, false,
                Cover(700 + i, 64), Origin: TrackOrigin.Local,
                Availability: Availability.Playable, Source: "local");
        }
        return _localCache = list;
    }

    // ── podcasts (the Podcasts facet: synthesized, since the export has none — docs/plans/wavee/architecture.md §9) ──────────────
    static readonly (string Name, string Publisher)[] ShowSeed =
    [
        ("Signals & Noise", "Wavee Studios"), ("The Long Take", "Reel Talk Media"), ("Night Coding", "Indie Dev FM"),
        ("Coffee & Code", "Brewed Bytes"), ("Synth History", "Analog Archives"), ("Field Notes", "Wander Audio"),
        ("Deep Focus Talks", "Mindful Media"), ("Release Notes", "Shipping It"),
    ];
    static readonly string[] EpTitles =
    [
        "The Build Trap", "Latency, Honestly", "On Craft", "Tape Loops", "The Quiet Release", "Edge Cases",
        "First Principles", "After Hours", "The Long Game", "Cold Start", "Postmortem", "Signal Lost",
    ];

    public static IReadOnlyList<Show> Shows()
    {
        var list = new Show[ShowSeed.Length];
        for (int i = 0; i < ShowSeed.Length; i++)
        {
            var (name, pub) = ShowSeed[i];
            list[i] = new Show("show" + i, "wavee:show:" + i, name, pub, Cover(800 + i, 300), ShowBlurb(name, pub));
        }
        return list;
    }

    public static Show? Show(string uri)
    {
        int idx = ((IndexFromUri(uri) % ShowSeed.Length) + ShowSeed.Length) % ShowSeed.Length;
        var (name, pub) = ShowSeed[idx];
        return new Show("show" + idx, uri, name, pub, Cover(800 + idx, 300), ShowBlurb(name, pub), ShowEpisodes(idx, name));
    }

    static string ShowBlurb(string name, string pub) => $"{name} — conversations, deep dives and field recordings from {pub}. New episodes weekly.";

    static IReadOnlyList<Episode> ShowEpisodes(int showIdx, string showName)
    {
        int n = 8 + showIdx % 5;
        var eps = new Episode[n];
        var now = DateTimeOffset.Now;
        for (int i = 0; i < n; i++)
        {
            long dur = (22 + (showIdx * 7 + i * 13) % 50) * 60_000L;     // 22–72 min
            long prog = i == 1 ? dur / 3 : 0;                            // one "continue listening" episode
            string title = $"#{n - i} · {EpTitles[(showIdx * 5 + i) % EpTitles.Length]}";
            eps[i] = new Episode("ep" + showIdx + "_" + i, $"wavee:episode:{showIdx}:{i}", title, showName,
                Cover(800 + showIdx, 300), dur, now.AddDays(-(i * 7L + showIdx)),
                $"In this episode of {showName}: notes, tangents and a few hard-won lessons.", prog);
        }
        return eps;
    }

    static IReadOnlyList<Track> EpisodesAsTracks(IReadOnlyList<Episode> eps)
    {
        var list = new Track[eps.Count];
        for (int i = 0; i < eps.Count; i++)
        {
            var e = eps[i];
            var ar = new ArtistRef("show", "wavee:show", e.ShowName);
            list[i] = new Track(e.Id, e.Uri, e.Title, new[] { ar }, new AlbumRef("show", "wavee:show", e.ShowName),
                e.DurationMs, false, e.Image, Source: "podcast");
        }
        return list;
    }

    /// <summary>The trailing numeric id of a <c>spotify:kind:xx{n}</c> uri (the same parse <see cref="FakeSource"/>
    /// uses to resolve a synthetic detail page) — so a context resolves to the IDENTICAL deterministic track list.</summary>
    public static int IndexFromUri(string uri)
    {
        int colon = uri.LastIndexOf(':');
        var tail = colon >= 0 ? uri[(colon + 1)..] : uri;
        int i = 0;
        foreach (char c in tail) if (char.IsDigit(c)) i = i * 10 + (c - '0');
        return i;
    }

    /// <summary>Resolve a <c>spotify:track:*</c> uri back to its synthetic track (the inverse of <see cref="Track"/>), so
    /// playing a single track (search, a row) loads the SAME track its card showed. Null for a non-track uri.</summary>
    public static Track? TrackByUri(string uri)
    {
        if (uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return Track(IndexFromUri(uri));
        if (uri.StartsWith("wavee:local:track:", StringComparison.Ordinal))
        {
            var lt = LocalTracks(); int i = IndexFromUri(uri);
            return (uint)i < (uint)lt.Count ? lt[i] : null;
        }
        return null;
    }

    /// <summary>Resolve a context uri (album / playlist / liked-songs) to ITS ordered track list — the same catalog the
    /// detail page loads, so playing a context at a start index plays the ACTUAL track that row shows. Empty for an
    /// unknown context.</summary>
    public static IReadOnlyList<Track> ContextTracks(string? contextUri)
    {
        if (string.IsNullOrEmpty(contextUri)) return Array.Empty<Track>();
        if (contextUri == "spotify:collection:tracks") return LikedSongs(161);
        if (contextUri.StartsWith("spotify:album:", StringComparison.Ordinal)) return Album(IndexFromUri(contextUri)).Tracks ?? Array.Empty<Track>();
        if (contextUri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) return Playlist(IndexFromUri(contextUri)).Tracks ?? Array.Empty<Track>();
        if (contextUri.StartsWith("spotify:artist:", StringComparison.Ordinal)) return TopTracksOf(Artist(IndexFromUri(contextUri)));
        if (contextUri.StartsWith("wavee:local:", StringComparison.Ordinal) || contextUri.StartsWith("local:", StringComparison.Ordinal)) return LocalTracks();
        if (contextUri.StartsWith("wavee:show:", StringComparison.Ordinal) && Show(contextUri) is { Episodes: { } eps }) return EpisodesAsTracks(eps);
        return Array.Empty<Track>();
    }

    // ── Sidebar IA: the "Your Library" counts + the (folder-capable) playlist tree ───────────────────────────────────
    /// <summary>The Your-Library badge counts shown in the sidebar.</summary>
    public static LibraryStats LibraryStats() => new(Albums: 13, Artists: 12, LikedSongs: 161, Podcasts: 7);

    // (Name, Owner, TrackCount, Seed) — Seed is the FakeData index, so the URI `spotify:playlist:pl{Seed}` round-trips
    // through GetPlaylistAsync (IndexFromUri reads the trailing digits). Seeds are distinct → no URI collisions.
    static readonly (string Name, string Owner, int Count, int Seed)[] PlaylistSeed =
    [
        ("mellow pop wistful saturday late night", "Spotify", 50, 1),
        ("My Playlist #6", "Christos", 4, 6),
        ("우울해", "Christos", 22, 2),
        ("Iced Americano", "Christos", 15, 3),
        ("Dalkom Cafe", "Christos", 50, 4),
        ("Nostalgia 2000s Mix", "Spotify", 30, 5),
        ("Henry Moodie Mix", "Spotify", 50, 7),
    ];

    static PlaylistSummary PlaylistSummary(int k)
    {
        var (name, owner, count, seed) = PlaylistSeed[k];
        return new PlaylistSummary($"spotify:playlist:pl{seed}", name, owner, count, Cover(seed + 100, 300));
    }

    /// <summary>The sidebar playlist tree: loose playlists + a collapsible folder that itself contains a folder — the
    /// demo backend deliberately exercises the RECURSION (folders hold nodes, not summaries), so a mode that only
    /// handles one level shows up immediately against the fakes.</summary>
    public static IReadOnlyList<PlaylistNode> PlaylistTree() =>
    [
        new PlaylistLeaf(PlaylistSummary(0)),
        new PlaylistLeaf(PlaylistSummary(1)),
        // Ids are ROOTLIST GROUP ids (bare, unprefixed) — the sidebar's pin id adds the "folder:" prefix itself.
        new PlaylistFolder("cafe", "Cafe & chill",
        [
            new PlaylistLeaf(PlaylistSummary(2)),
            new PlaylistFolder("latenight", "Late night",
            [
                new PlaylistLeaf(PlaylistSummary(4)),
            ]),
            new PlaylistLeaf(PlaylistSummary(3)),
        ]),
        new PlaylistLeaf(PlaylistSummary(5)),
        new PlaylistLeaf(PlaylistSummary(6)),
    ];

    /// <summary>Flattened playlist summaries (folders expanded at EVERY depth) — used to seed the "+ New playlist"
    /// growth list. Delegates to the one flatten helper so it can never drift from the tree's nesting rules.</summary>
    public static IReadOnlyList<PlaylistSummary> UserPlaylists() => SidebarTree.Flatten(PlaylistTree());

    public static LibraryItem[] Library()
    {
        var items = new List<LibraryItem>();
        items.Add(new LibraryItem("spotify:collection:tracks", "Liked Songs", "Playlist · 5,000 songs", Cover(900, 80), LibraryItemKind.Playlist));
        for (int i = 0; i < 8; i++) { var p = Playlist(i); items.Add(new LibraryItem(p.Uri, p.Name, $"Playlist · {p.TrackCount} songs", p.Cover, LibraryItemKind.Playlist)); }
        for (int i = 0; i < 6; i++) { var a = Album(i + 20); items.Add(new LibraryItem(a.Uri, a.Name, $"Album · {a.Artists[0].Name}", a.Cover, LibraryItemKind.Album)); }
        for (int i = 0; i < 4; i++) { var ar = Artist(i + 30); items.Add(new LibraryItem(ar.Uri, ar.Name, "Artist", ar.Image, LibraryItemKind.Artist)); }
        return items.ToArray();
    }

    public static SearchResults Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new SearchResults([], [], [], []);
        var tracks = Tracks(8, query.Length * 3);
        var albums = new[] { Album(query.Length), Album(query.Length + 1), Album(query.Length + 2) };
        var artists = new[] { Artist(query.Length), Artist(query.Length + 1) };
        var playlists = new[] { Playlist(query.Length), Playlist(query.Length + 1) };
        return new SearchResults(tracks, albums, artists, playlists);
    }

    public static LyricsDocument Lyrics(string trackId)
    {
        // A full-length (40-line) word-synced lyric: the synced rail shows ~11 lines and depth-of-field-blurs the rest,
        // so a realistic doc must overflow the viewport (scrollable) and span enough lines to exercise the per-line blur.
        string[] phrases =
        [
            "Driving in the city lights", "Watching as the world goes by", "Every heartbeat keeps the time",
            "Holding on, we're almost there", "Neon rivers in the rain", "Chasing all the echoes down the lane",
            "We were younger, we were free", "Dreaming of the way to be", "Stars are falling one by one",
            "Whispering the night's not done", "Carry me across the line", "Tell me that you'll still be mine",
        ];
        var doc = new List<LyricLine>();
        long t = 0;
        for (int i = 0; i < 40; i++)
        {
            string line = phrases[i % phrases.Length];
            var words = line.Split(' ');
            var syl = new List<LyricSyllable>();
            long w = t;
            foreach (var word in words) { syl.Add(new LyricSyllable(w, w + 400, word + " ")); w += 450; }
            doc.Add(new LyricLine(t, line, syl, EndMs: w, IsWordByWord: true));
            t += 3600;
        }
        return new LyricsDocument(trackId, IsSynced: true, doc, Sync: LyricsSyncKind.Syllable);
    }

    public static QueueEntry[] DefaultQueue()
    {
        var q = new List<QueueEntry>();
        q.Add(new QueueEntry(QueueItemId.None, "q0", Track(0), QueueBucket.NowPlaying, QueueProvider.Context, false));
        for (int i = 1; i <= 3; i++) q.Add(new QueueEntry(QueueItemId.None, "q" + i, Track(i), QueueBucket.UserQueue, QueueProvider.Queue, false));
        for (int i = 4; i <= 11; i++) q.Add(new QueueEntry(QueueItemId.None, "q" + i, Track(i), QueueBucket.NextUp, i > 8 ? QueueProvider.Autoplay : QueueProvider.Context, i > 8));
        return q.ToArray();
    }

    // Representative PENDING seeds for the skeleton-loading boundary: Skel.Region renders content(seed) and DERIVES the
    // shimmer from it, so these give the home/search resources a real-shaped (blank) value to lay out WHILE loading — the
    // page itself says nothing about skeletons. Content is blank; the deriver turns each title/cover into a shimmer bar.
    //
    // This MUST track the composer's module order and counts (SpotifyHomeComposer.Compose): the shimmer IS this tree, so
    // a seed that disagrees with the loaded layout turns the reveal into a jump-cut. It only needs to cover the rows a
    // cold viewport can actually show — the tail realizes after content has landed and never shimmers.
    // Cards carry a null Meta on purpose, except the hero: that is the case every ordinary module renderer must survive,
    // while HeroBlank supplies the title/tags/meta/action lanes that determine the hero's actual minimum height.
    public static readonly HomeFeed HomeSeed = new("", new HomeGroup[]
    {
        // One entry per module kind the composer can emit, at the count the module SHOWS. Counts and order are
        // load-bearing: they are the silhouette from which the Home shimmer is derived.
        new(HomeGroupKind.Hero, " ", [HeroBlank()], Uri: "wavee:skeleton:section:hero", TotalCount: 1),
        new(HomeGroupKind.WeeklyPair, null, WeeklyBlanks()),
        new(HomeGroupKind.QuickGrid, " ", BlankCards("quick", 8, HomeCardKind.Playlist)),
        new(HomeGroupKind.Recents, " ", BlankCards("recents", 8, HomeCardKind.Album)),
        new(HomeGroupKind.MixBand, " ", BlankCards("mix", 6, HomeCardKind.Playlist)),
        new(HomeGroupKind.ChipCards, " ", BlankCards("chips", 6, HomeCardKind.Playlist)),
        new(HomeGroupKind.RadioDial, " ", BlankCards("radio", 12, HomeCardKind.Playlist)),
        new(HomeGroupKind.QueueList, " ", BlankCards("queue", 6, HomeCardKind.Episode)),
        new(HomeGroupKind.RatedShelf, " ", BlankCards("books", 6, HomeCardKind.Audiobook)),
        new(HomeGroupKind.Featured, " ", BlankCards("featured", 4, HomeCardKind.Playlist)),
        new(HomeGroupKind.PodcastShelf, " ", BlankCards("podcasts", 6, HomeCardKind.Podcast), Uri: "wavee:skeleton:section:podcasts", TotalCount: 6),
        new(HomeGroupKind.Topic, " ", BlankCards("topic", 7, HomeCardKind.Playlist), Uri: "wavee:skeleton:section:topic", TotalCount: 20),
        new(HomeGroupKind.SectionEntry, " ", BlankCards("section", 7, HomeCardKind.Playlist), Uri: "wavee:skeleton:section:mixed", TotalCount: 20),
        new(HomeGroupKind.DiscoverFeed, " ", BlankCards("discover", 12, HomeCardKind.Playlist)),
    }, Sections:
    [
        new("wavee:skeleton:section:topic", " ", " ", BlankCards("topic-section", 7, HomeCardKind.Playlist), 20, 7),
        new("wavee:skeleton:section:mixed", " ", null, BlankCards("mixed-section", 7, HomeCardKind.Playlist), 20, 7),
        new("wavee:skeleton:section:podcasts", " ", null, BlankCards("podcast-section", 6, HomeCardKind.Podcast), 6, 6),
    ]);
    public static readonly SearchResults SearchSeed = Search("skeleton");

    static HomeCard HeroBlank() => new(
        "wavee:skeleton:hero:0", "Afternoon focus rotation", "A representative hero description", null,
        HomeCardKind.Playlist,
        Meta: new HomeCardMeta(Format: "daylist", TrackCount: 50,
            Seeds: ["focus", "indie", "chill"], OwnerName: "Wavee"));

    static HomeCard[] BlankCards(string group, int n, HomeCardKind kind)
    {
        var cards = new HomeCard[n];
        for (int i = 0; i < n; i++) cards[i] = new HomeCard("wavee:skeleton:" + group + ":" + i, "", "", null, kind);
        return cards;
    }

    static HomeCard[] WeeklyBlanks() =>
    [
        new("wavee:skeleton:weekly:discover", "", "", null, HomeCardKind.Playlist,
            Meta: new HomeCardMeta(Format: "discover-weekly")),
        new("wavee:skeleton:weekly:radar", "", "", null, HomeCardKind.Playlist,
            Meta: new HomeCardMeta(Format: "release-radar")),
    ];
}
