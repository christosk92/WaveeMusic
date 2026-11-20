// ── Home/ReleaseSpotlight/SpotlightModel.cs — the Release Spotlight's values: a followed artist's new release and what it says ─────────────
//
// Pure reads. The zone's clusters (one per release: its album card as Header, its preview songs as Rows, Spotify's order) become the spotlight's
// pages (SpotlightItem: what never changes for a release once its card and previews are on hand). What the two small lookups add later (the album:
// its release date, length and track count; the artist: their name and photo for the round avatar; the songs' durations) is SpotlightFacts, read
// live off the tables by the card (it re-renders only when a fact it shows changed). Both lookups are the entity paths the album and artist pages
// already use (Entities.Ensure over the rows, the AlbumTracks edge), cached like every other row. Nothing here draws.

using System.Globalization;
using System.Runtime.InteropServices;
using FluentGpu.Localization;
using Wavee;

namespace Wavee.HomeUi;

/// <summary>One page of the spotlight: a release (its album card), its cover and colour, and its preview songs (the baseline lookup's: the
/// canvas video, its still and the 30 s clip, in the lookup's order). <see cref="Key"/> is the album's uri (node keys, the preview's tile keys).</summary>
public sealed record SpotlightItem(string Key, HomeCard Card, string Title, string? Cover, uint Accent, ShelfSong[] Songs)
{
    /// <summary>The release's place in the spotlight, from 0.</summary>
    public int Ordinal { get; init; }

    /// <summary>A placeholder page (the answer is on its way): flat boxes at the card's size.</summary>
    public bool IsBlank => Card.IsBlank;

    /// <summary>The release's kind (Single, EP, Album, Compilation), null while unknown.</summary>
    public AlbumKind? Kind => Card.AlbumKind;

    /// <summary>A single draws no song list (its one song is the release); an EP, an album or a compilation lists its first songs. Known from
    /// the Home answer's albumType, so the list's rows stand (placeholders) before the preview songs land: nothing in the card moves when they do.</summary>
    public bool ListsSongs => Kind is AlbumKind.EP or AlbumKind.Album or AlbumKind.Compilation;

    /// <summary>The preview songs the lookup named (blank placeholders excluded).</summary>
    public int SongCount
    {
        get
        {
            int n = 0;
            foreach (var s in Songs) if (!s.IsBlank) n++;
            return n;
        }
    }

    /// <summary>The song the canvas tile shows at rest: the first with a canvas, else the first.</summary>
    public int FaceSong
    {
        get
        {
            for (int i = 0; i < Songs.Length; i++) if (Songs[i].HasCanvas) return i;
            return 0;
        }
    }

    /// <summary>Value equality over the songs too (a record compares an array by reference): a re-projection that changed nothing hands the
    /// spotlight an EQUAL item, so nothing re-renders.</summary>
    public bool Equals(SpotlightItem? o) => o is not null && Key == o.Key && Card == o.Card && Title == o.Title && Cover == o.Cover
        && Accent == o.Accent && Ordinal == o.Ordinal && Songs.AsSpan().SequenceEqual(o.Songs);

    public override int GetHashCode() => HashCode.Combine(Key, Title, Accent, Ordinal, Songs.Length);
}

/// <summary>What the lookups add to a release, read live off the tables: the eyebrow's words around the artist's name, the artist (name, uri,
/// photo), the meta line (type · release date · length) and each listed song's duration ("" while unknown).</summary>
public sealed record SpotlightFacts(string Lead, string Tail, string Artist, string ArtistUri, string? ArtistImage, string Meta, string[] Durations)
{
    public bool Equals(SpotlightFacts? o) => o is not null && Lead == o.Lead && Tail == o.Tail && Artist == o.Artist && ArtistUri == o.ArtistUri
        && ArtistImage == o.ArtistImage && Meta == o.Meta && Durations.AsSpan().SequenceEqual(o.Durations);

    public override int GetHashCode() => HashCode.Combine(Lead, Artist, Meta, Durations.Length);

    /// <summary>A placeholder page's facts: blank words at a typical length, so the loading skeleton derives their bars where the words land.</summary>
    public static readonly SpotlightFacts Blank = new(SpotlightModel.Hold(14), "", SpotlightModel.Hold(10), "", null, SpotlightModel.Hold(18), []);
}

public static class ReleaseSpotlightRules
{
    /// <summary>The most releases one spotlight pages through in the Following facet (the rest stay its release list); the All feed's fold
    /// gathers its whole run.</summary>
    public const int MaxPages = 8;

    /// <summary>The covers waiting on the right of the spotlight's foot: the next releases, at most this many.</summary>
    public const int NextCovers = 4;

    /// <summary>Resting on the card this long starts the preview (the canvas and the clip): a pointer that only crosses the card opens nothing.</summary>
    public const float SettleMs = 180f;

    /// <summary>The canvas still and the next song's still cross-fading when the pointer moves to another song.</summary>
    public const float FaceMs = SpineShelfRules.FadeMs;
}

public static class SpotlightModel
{
    /// <summary>The zone's releases, in Spotify's order: one per cluster with a card.</summary>
    public static SpotlightItem[] Of(Zone zone)
    {
        var clusters = zone.Clusters;
        if (clusters is not { Count: > 0 }) return [];
        var items = new List<SpotlightItem>(clusters.Count);
        foreach (var c in clusters)
        {
            if (c.Header is not { } h) continue;
            // Until the lookup answers: placeholders at the songs' rows. Once it has: exactly the songs it named.
            bool answered = c.Rows.Count > 0 || Home.PreviewsSettled(c.FooterSectionSlot);
            var songs = new ShelfSong[answered ? c.Rows.Count : (h.IsBlank ? 0 : 1)];
            for (int i = 0; i < songs.Length; i++) songs[i] = i < c.Rows.Count ? ShelfModel.SongOf(c.Rows[i]) : ShelfSong.Blank;
            items.Add(new SpotlightItem(h.IsBlank ? "blank:" + items.Count.ToString(CultureInfo.InvariantCulture) : h.Uri, h,
                h.IsBlank ? Hold(TitleHold) : h.Title, h.ImageUrl, h.Accent, songs) { Ordinal = items.Count });
        }
        return items.ToArray();
    }

    /// <summary>Invisible words of <paramref name="n"/> en spaces: a placeholder that holds a typical word run's width (the skeleton derives a
    /// bar of it), never drawn as anything on a loaded card.</summary>
    public static string Hold(int n) => n switch
    {
        TitleHold => s_title,
        SongHold => s_song,
        _ => new string(' ', Math.Max(1, n)),
    };

    /// <summary>A placeholder title's and a placeholder song's length.</summary>
    public const int TitleHold = 8, SongHold = 16;

    static readonly string s_title = new(' ', TitleHold), s_song = new(' ', SongHold);

    /// <summary>How many pages <paramref name="zone"/> holds (one per release with a card).</summary>
    public static int Count(Zone zone)
    {
        int n = 0;
        if (zone.Clusters is { } cs) foreach (var c in cs) if (c.Header is not null) n++;
        return n;
    }

    /// <summary>Ask for what the card shows beyond the Home answer: the album (release date, track count, its track list and their durations),
    /// its billed artist (name and photo) and the preview songs' rows (durations, the artist line). Cached rows answer at once; nothing is
    /// asked twice (Entities.Ensure skips what a row knows; the track list only while it is unanswered).</summary>
    public static void Prepare(SpotlightItem item)
    {
        if (item.IsBlank || item.Card.Target.Kind != EntityKind.Album) return;
        int slot = item.Card.Target.Slot;
        var scope = Entities.Current;
        Entities.Ensure(scope.Albums, [slot], (uint)AlbumFields.Card);
        if (scope.Edges.AlbumTracks.State(slot) == EdgeState.Unknown) Entities.EnsureEdge(FetchEdge.AlbumTracks, slot);
        var album = new Album(slot);
        var tracks = album.TrackSlots;
        if (tracks.Length > 0) Entities.Ensure(scope.Tracks, tracks, (uint)TrackFields.Duration);
        var billed = album.ArtistSlots;
        if (billed.Length > 0) Entities.Ensure(scope.Artists, billed[..1], (uint)ArtistFields.Identity);
        Span<int> songs = stackalloc int[ClusterFold.MaxRows];
        int n = 0;
        foreach (var s in item.Songs)
            if (!s.IsBlank && s.Card.Kind == HomeCardKind.Track && n < songs.Length) songs[n++] = s.Card.Target.Slot;
        if (n > 0) Entities.Ensure(scope.Tracks, songs[..n], (uint)(TrackFields.Duration | TrackFields.Artists));
    }

    /// <summary>The release's facts as the tables hold them now (allocates the strings: a render-time read, once per change).</summary>
    public static SpotlightFacts FactsOf(SpotlightItem item, long nowMs)
    {
        if (item.IsBlank) return SpotlightFacts.Blank;
        var c = item.Card;
        string artist = HomeCards.PlainText(c.Subtitle), artistUri = "";
        string? image = null;
        var album = new Album(c.Target.Slot);
        bool isAlbum = c.Target.Kind == EntityKind.Album && album.IsValid;
        if (isAlbum)
        {
            var billed = album.ArtistSlots;
            if (billed.Length > 0)
            {
                var a = new Artist(billed[0]);
                if (a.IsValid)
                {
                    if (!a.NameId.IsEmpty) artist = a.Name;
                    artistUri = a.Uri.Text;
                    image = a.ImageId.IsEmpty ? null : Entities.Strings.Resolve(a.ImageId);
                }
            }
        }
        var (lead, tail) = Eyebrow(item, c.IsPreRelease);

        // type · release date · length
        var parts = new List<string>(3);
        string type = ReleaseListRules.TypeLabel(in c, upcomingFirst: false);
        if (type.Length > 0) parts.Add(type);
        string when = ReleaseListRules.DateLabel(in c, nowMs, TimeZoneInfo.Local, CultureInfo.CurrentCulture);
        if (when.Length > 0) parts.Add(when);
        if (isAlbum && Length(album, item) is { Length: > 0 } length) parts.Add(length);
        string meta = parts.Count == 0 ? " " : string.Join(" · ", parts);

        var durations = new string[item.Songs.Length];
        for (int i = 0; i < durations.Length; i++)
        {
            var s = item.Songs[i];
            int ms = !s.IsBlank && s.Card.Kind == HomeCardKind.Track ? new Track(s.Card.Target.Slot).DurationMs : 0;
            durations[i] = ms > 0 ? Track.Format.TrackTime(ms) : "";
        }
        return new SpotlightFacts(lead, tail, artist.Length > 0 ? artist : " ", artistUri, image, meta, durations);
    }

    /// <summary>The release's length: a single's one song ("3:12"); an EP's or an album's songs and minutes ("4 songs · 13 min"), the songs
    /// alone while any duration is unknown; "" while the album row has not answered.</summary>
    static string Length(Album album, SpotlightItem item)
    {
        var tracks = album.TrackSlots;
        bool complete = Entities.Current.Edges.AlbumTracks.State(album.Slot) == EdgeState.Complete && tracks.Length > 0;
        long total = 0;
        bool known = complete;
        if (complete)
            foreach (int t in tracks)
            {
                int ms = new Track(t).DurationMs;
                if (ms <= 0) { known = false; break; }
                total += ms;
            }
        int count = complete ? tracks.Length : album.Knows(AlbumFields.TrackCount) ? album.TrackCount : 0;
        if (item.Kind == AlbumKind.Single || count == 1) return known ? Track.Format.TrackTime(total) : "";
        if (count <= 0) return "";
        return Detail.Text.AlbumMeta(count, total, known) ?? "";
    }

    /// <summary>The eyebrow's words before and after the artist's name ("New single from " · "", "" · "의 새 싱글"): the localized pattern split at
    /// its {artist}, so the name can be a link in every language's order.</summary>
    public static (string Lead, string Tail) Eyebrow(SpotlightItem item, bool upcoming)
    {
        const string mark = "\u0001";
        string text = upcoming ? Strings.Home.Spotlight.Upcoming(mark) : item.Kind switch
        {
            AlbumKind.Single => Strings.Home.Spotlight.NewSingle(mark),
            AlbumKind.EP => Strings.Home.Spotlight.NewEp(mark),
            AlbumKind.Album => Strings.Home.Spotlight.NewAlbum(mark),
            _ => Strings.Home.Spotlight.NewRelease(mark),
        };
        int at = text.IndexOf(mark, StringComparison.Ordinal);
        return at < 0 ? (text, "") : (text[..at], text[(at + mark.Length)..]);
    }
}
