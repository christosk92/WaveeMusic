using System.IO;
using System.Text.Json;
using static Wavee.Core.SpotifyExportMapper;

namespace Wavee.Core;

/// <summary>Loads the bundled Spotify GraphQL exports once and holds them as clean domain data (the JSON documents are
/// parsed and discarded — no JsonElement is retained). Backs <see cref="SpotifyExportSource"/>. Missing/!valid files
/// degrade to empty so the app still runs (docs/plans/wavee/architecture.md §4.4).</summary>
public sealed class SpotifyExport
{
    readonly List<PlaylistSummary> _summaries = new();
    readonly Dictionary<string, Playlist> _headers = new();        // uri → header (Tracks empty)
    readonly Dictionary<string, Playlist> _fullPlaylists = new();  // uri → header + REAL tracks (the Iced detail)
    readonly Dictionary<string, HomeCard> _cards = new();          // uri → card (for enriching opened album/artist/playlist)
    readonly Dictionary<string, Artist> _artists = new();          // uri → full magazine artist (from artist-*.json)

    public IReadOnlyList<PlaylistSummary> LibraryPlaylists => _summaries;
    public int LikedCount { get; private set; }
    public HomeContribution Home { get; private set; } = new(System.Array.Empty<HomeGroup>());

    public bool TryGetFullPlaylist(string uri, out Playlist p) => _fullPlaylists.TryGetValue(uri, out p!);
    public bool TryGetHeader(string uri, out Playlist p) => _headers.TryGetValue(uri, out p!);
    public bool TryGetCard(string uri, out HomeCard c) => _cards.TryGetValue(uri, out c!);
    public bool TryGetArtist(string uri, out Artist a) => _artists.TryGetValue(uri, out a!);
    public IReadOnlyCollection<Artist> Artists => _artists.Values;

    public static SpotifyExport Load(string? dir = null)
        => new(dir ?? Path.Combine(AppContext.BaseDirectory, "assets", "spotify"));

    SpotifyExport(string dir)
    {
        LoadLibrary(Path.Combine(dir, "playlists.json"));
        LoadIced(Path.Combine(dir, "icedamericano.json"));
        LoadHome(Path.Combine(dir, "home.json"));
        LoadArtists(dir);
    }

    // Every artist-*.json holds a full `data.artistUnion` overview (the magazine page), keyed by the artist uri.
    void LoadArtists(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.GetFiles(dir, "artist-*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var au = Dig(doc.RootElement, "data", "artistUnion");
                if (au.ValueKind != JsonValueKind.Object) continue;
                var artist = MapArtist(au);
                if (artist.Uri.Length > 0) _artists[artist.Uri] = artist;
            }
            catch { /* malformed export → skip */ }
        }
    }

    void LoadLibrary(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var items = Dig(doc.RootElement, "data", "me", "libraryV3", "items");
            if (items.ValueKind != JsonValueKind.Array) return;
            foreach (var it in items.EnumerateArray())
            {
                var data = Dig(it, "item", "data");
                var tn = Str(data, "__typename");
                if (tn == "PseudoPlaylist") { LikedCount = (int)Long(data, "count"); continue; }
                if (tn != "Playlist") continue;                       // skip Folder
                var uri = Str(data, "uri") ?? Str(Dig(it, "item"), "_uri");
                if (uri is null) continue;
                if (Str(data, "format") == "listen-later") continue;  // "Your Episodes" pseudo
                int count = SynthCount(uri);
                var header = MapPlaylistHeader(data, count);
                _headers[uri] = header;
                _summaries.Add(new PlaylistSummary(header.Uri, header.Name, header.OwnerName, count, header.Cover));
            }
        }
        catch { /* malformed export → skip */ }
    }

    void LoadIced(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var p = Dig(doc.RootElement, "data", "playlistV2");
            var uri = Str(p, "uri");
            if (uri is null) return;
            var tracks = new List<Track>();
            var items = Dig(p, "content", "items");
            if (items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray())
                    if (MapTrack(it) is { } t) tracks.Add(t);
            var full = MapPlaylistHeader(p, tracks.Count, tracks);
            _fullPlaylists[uri] = full;
            _headers[uri] = full;
            for (int i = 0; i < _summaries.Count; i++)
                if (_summaries[i].Uri == full.Uri)
                    _summaries[i] = _summaries[i] with { TrackCount = tracks.Count, Cover = _summaries[i].Cover ?? full.Cover };
        }
        catch { /* skip */ }
    }

    void LoadHome(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var home = Dig(doc.RootElement, "data", "home");
            var sections = Dig(home, "sectionContainer", "sections", "items");
            if (sections.ValueKind == JsonValueKind.Array)
                foreach (var sec in sections.EnumerateArray())
                {
                    var its = Dig(sec, "sectionItems", "items");
                    if (its.ValueKind != JsonValueKind.Array) continue;
                    foreach (var it in its.EnumerateArray())
                        if (CardFromEntity(Dig(it, "content", "data")) is { } c) _cards[c.Uri] = c;
                }
            Home = SpotifyHomeComposer.Compose(home, _summaries);
        }
        catch { /* skip */ }
    }
}
