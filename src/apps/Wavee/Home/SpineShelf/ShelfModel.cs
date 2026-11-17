// ── Home/SpineShelf/ShelfModel.cs — the Spine Shelf's values: its items (a "Because you like" card) and their preview songs ───────────
//
// Pure reads of the zone the chooser built (Zone.Clusters, one per baseline section, Spotify's order) and of the tables the lookup filled
// (the song's canvas in Edges.TrackCanvas, its clip and cover colour in Edges.TrackPreview). Nothing here draws. The shelf's paging rule
// (at most MaxItems per page, a longer list split evenly) lives here too, so the planner, the projection and every theme agree.

using Wavee;

namespace Wavee.HomeUi;

/// <summary>One preview song as a tile draws it. <see cref="Canvas"/> is the looping mp4 (null: the song has none and its tile shows its
/// cover on its own colour), <see cref="Still"/> the canvas's first-frame thumbnail, <see cref="Clip"/> the 30 s mp3, <see cref="Accent"/> the
/// cover's dark colour (ARGB, 0 unknown). <see cref="Artist"/> is "" until the track row's credits land.</summary>
public readonly record struct ShelfSong(HomeCard Card, string Uri, string Title, string Artist, string? Cover, string? Canvas, string? Still,
                                        string? Clip, uint Accent)
{
    public bool HasCanvas => Canvas is { Length: > 0 };

    /// <summary>A placeholder tile (the lookup has not answered for its card yet).</summary>
    public bool IsBlank => Uri.Length == 0;

    public static readonly ShelfSong Blank = new(default, "", "", "", null, null, null, null, 0);
}

/// <summary>One shelf item: a baseline section's card (the playlist it offers), why it is here, and its preview songs.
/// <see cref="Key"/> is the card's uri: the item's identity (node keys, the remembered open item).</summary>
public sealed record ShelfItem(string Key, HomeCard Header, string Title, string Over, HeaderEntity? Reason, string Meta, uint Accent,
                               ShelfSong[] Songs)
{
    /// <summary>The lookup answered and named no preview song for this card: the open card shows the playlist's own cover instead of
    /// placeholders that would never fill.</summary>
    public bool NoSongs { get; init; }

    /// <summary>The item's place in the whole shelf (across its pages), from 0.</summary>
    public int Ordinal { get; init; }

    /// <summary>The number on the spine ("01"): its place in the whole shelf, so a second page goes on counting.</summary>
    public string Number => (Ordinal + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Value equality over the songs too (a record compares an array by reference): a re-projection that changed nothing in an item
    /// hands the shelf an EQUAL item, so nothing re-renders (every Home data commit re-projects the board).</summary>
    public bool Equals(ShelfItem? o) => o is not null && Key == o.Key && Header == o.Header && Title == o.Title && Over == o.Over
        && Equals(Reason, o.Reason) && Meta == o.Meta && Accent == o.Accent && NoSongs == o.NoSongs && Ordinal == o.Ordinal
        && Songs.AsSpan().SequenceEqual(o.Songs);

    public override int GetHashCode() => HashCode.Combine(Key, Title, Accent, Ordinal, Songs.Length);
}

public static class SpineShelfRules
{
    /// <summary>The most items one page of the shelf holds: the open card keeps room for five song tiles beside seven spines at the
    /// narrowest row form (Small). A longer list pages (the chapter's pager), split evenly: 19 → 7 + 6 + 6.</summary>
    public const int MaxItems = 8;

    /// <summary>The song tiles of an open card: every preview the lookup answers.</summary>
    public const int Songs = ClusterFold.MaxRows;

    /// <summary>How many pages <paramref name="n"/> items take.</summary>
    public static int Pages(int n) => n <= 0 ? 0 : (n + MaxItems - 1) / MaxItems;

    /// <summary>The size of page <paramref name="page"/> of <paramref name="pages"/> over <paramref name="n"/> items (the first pages take the remainder).</summary>
    public static int PageSize(int n, int pages, int page) => pages <= 0 ? 0 : n / pages + (page < n % pages ? 1 : 0);

    /// <summary>The first item of page <paramref name="page"/>.</summary>
    public static int PageStart(int n, int pages, int page)
    {
        int start = 0;
        for (int p = 0; p < page; p++) start += PageSize(n, pages, p);
        return start;
    }

    // ── motion (one curve for the whole shelf: the row's length is constant only when every item rides the same e(t)) ──
    /// <summary>Open and close: the concept's 520 ms on cubic-bezier(.22, .9, .24, 1).</summary>
    public const float OpenMs = 520f;
    /// <summary>A song tile widening and narrowing: one critically damped spring for every tile and every picture in them (TileResponse
    /// seconds). A spring, not the item's tween: the pointer retargets tiles mid-motion, and a tween is re-aimed over the time IT has left,
    /// which differs per tile, so a quick sweep opened gaps of up to 24 DIP between tiles (and overlaps) and a tile caught near its end
    /// finished its whole change in a frame or two. Springs with one stiffness keep their state on a retarget and add up linearly: the
    /// widths' sum stays the row's span, every gap stays TileGap and every picture stays centred, on every frame of any retarget.</summary>
    public const float TileResponse = 0.30f;
    /// <summary>The longest a tile's widening takes to come to rest: the fallback that opens a tile's media when no width row runs (reduced
    /// motion); with a row the media opens on its rest (WhenSettled).</summary>
    public const float TileMs = 520f;
    /// <summary>One frame: the media of a tile opens this long after its widening has come to rest.</summary>
    public const float RestFrameMs = 17f;
    /// <summary>How long a tile the pointer left stays live while the pointer crosses a gap toward the next tile (a few frames).</summary>
    public const float LeaveGraceMs = 90f;
    /// <summary>The still fading out over the first frame of its canvas video, and back in over the video before it is released.</summary>
    public const float SwapMs = 160f;

    /// <summary>The spine/face cross-fade.</summary>
    public const float FadeMs = 240f;
    /// <summary>Resting on a spine this long opens it (the meter fills over it).</summary>
    public const float DwellMs = 700f;
    /// <summary>A clip's length: the progress line fills over it.</summary>
    public const float ClipMs = 30_000f;
    /// <summary>A live tile's share of the tile row against 1 for each other tile.</summary>
    public const float LiveShare = 1.7f;
    /// <summary>The faked blur under a cover (a song without a canvas): the cover decoded this many pixels across, upscaled by the sampler,
    /// and darkened by this much.</summary>
    public const float BlurDecodePx = 12f, BlurDim = 0.35f;

    /// <summary>A canvas still's decode size: the largest thumbnail Spotify sends (288 x 512), as the canvas host decodes its poster.</summary>
    public const float StillDecodePx = 512f;
    /// <summary>A song tile's cover (a song without a canvas) takes at most this share of the tile's width (the concept's min(70 %, …)).</summary>
    public const float CoverShare = 0.7f;
    /// <summary>The clip's volume against the user's own.</summary>
    public const float ClipVolume = 0.4f;

    public static readonly FluentGpu.Foundation.EasingSpec Curve = FluentGpu.Foundation.EasingSpec.CubicBezier(0.22f, 0.9f, 0.24f, 1f);
}

public static class ShelfModel
{
    /// <summary>The zone's items, in Spotify's order: one per cluster with a card.</summary>
    public static ShelfItem[] Of(Zone zone)
    {
        var clusters = zone.Clusters;
        if (clusters is not { Count: > 0 }) return [];
        var items = new List<ShelfItem>(clusters.Count);
        foreach (var c in clusters)
        {
            if (c.Header is not { } h) continue;
            // Until the lookup answers: five flat placeholders at the tiles' size. Once it has: exactly the songs it named (a short answer's tiles
            // share the row), or none (the cover tile).
            bool answered = c.Rows.Count > 0 || Answered(c.FooterSectionSlot) || LookupDone();
            var songs = new ShelfSong[answered ? Math.Max(1, c.Rows.Count) : SpineShelfRules.Songs];
            for (int i = 0; i < songs.Length; i++) songs[i] = i < c.Rows.Count ? SongOf(c.Rows[i]) : ShelfSong.Blank;
            bool none = c.Rows.Count == 0 && answered;
            items.Add(new ShelfItem(h.Uri, h, h.Title, c.Over, c.Reason, SectionCopy.PreviewFacts(in h), h.Accent, songs) { NoSongs = none, Ordinal = items.Count });
        }
        return items.ToArray();
    }

    /// <summary>The lookup closed this section's preview list (its card was answered).</summary>
    static bool Answered(int sectionSlot)
        => sectionSlot > Table.None && Entities.Current.Edges.SectionPreviewTracks.State(sectionSlot) == EdgeState.Complete;

    /// <summary>A facet's whole lookup has concluded: answered (a card it did not name has no preview songs to wait for) or failed (none
    /// will come), so no tile waits on placeholders that never fill; the cover tile stands instead.</summary>
    static bool LookupDone()
    {
        var marker = Entities.Current.Edges.HomePreviews;
        for (int p = 0; p < marker.ParentCount; p++) if (marker.State(p) == EdgeState.Complete || marker.IsFailed(p)) return true;
        return false;
    }

    /// <summary>A preview song's tile values, read live off the tables (allocates the resolved strings: a render-time read).</summary>
    public static ShelfSong SongOf(HomeCard c)
    {
        int slot = c.Target.Slot;
        string? canvas = null, still = null, clip = null;
        uint accent = 0;
        if (c.Kind == HomeCardKind.Track && Entities.TryCanvas(slot, out var cv))
        {
            canvas = Entities.Strings.Resolve(cv.Url);
            still = cv.Poster.IsEmpty ? null : Entities.Strings.Resolve(cv.Poster);
        }
        if (c.Kind == HomeCardKind.Track && Entities.TryPreview(slot, out var pv))
        {
            clip = pv.AudioUrl.IsEmpty ? null : Entities.Strings.Resolve(pv.AudioUrl);
            accent = pv.Accent;
        }
        return new ShelfSong(c, c.Uri, c.Title, HomeCards.PlainText(c.Subtitle), c.ImageUrl, canvas, still, clip, accent);
    }
}
