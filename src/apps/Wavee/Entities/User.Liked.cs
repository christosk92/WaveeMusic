// ── Entities/User.Liked.cs ─────────────────────────────────────────────────────────────────────────────────────────
// LikedCoverRules + ContentFilterTags + the liked uri identity — the PURE half of the Liked Songs cover and chip bar
//
// Role: CORE
// Owner: O
// Wave: 5
// Budget: 500 lines
// Spec: ch 07 §8 (LikedCoverStyle, LikedCoverSite, LikedCoverRules, ContentFilterTags, ContentFilterChipSet), §9
//
// Ported VERBATIM from 0.2.9 `Features/Detail/LikedCoverRules.cs` and `Wavee.Core/Library/ContentFilterTags.cs`. Only
// the INPUT types changed: a liked row is a `Track` HANDLE now (tile url = `Controls.ArtUrl(track.ImageId)`, album key =
// `track.AlbumSlot`, url identity = the image id, tags = `Edges.TrackTags.Payload(slot)` read only when
// `Knows(TrackFields.Tags)` — "not fetched" is never "no descriptors"). Each rule also keeps a record-shaped overload so
// its 0.2.9 facts port without a scope.
//
// THE HONESTY LADDER is the whole point of the cover half: a treatment is only ever composed from art the library owns;
// below a style's floor the answer is Stock, the bundled PNG — never a half-empty grid and never a grey hole.

namespace Wavee;

// ── 1. the cover ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Which treatment the Liked Songs cover renders. PERSISTED as an int (<c>Platform.Keys.LikedCoverStyle</c>),
/// so the values are WIRE-STABLE: a new treatment appends, an existing value never re-means. <see cref="Stock"/> is 0
/// because it is the DEGRADE TARGET; the shipped default is <see cref="Lens"/>.</summary>
public enum LikedCoverStyle
{
    Stock = 0,
    Lens = 1,
    Wall = 2,
    Rainbow = 3,
    Marquee = 4,
    Feature = 5,
    Mosaic = 6,
    Tone = 7,
    Stack = 8,
}

/// <summary>What a liked cover of a given size actually paints — a pure VIEW decision, free to reorder.</summary>
public enum LikedCoverSite { Treatment, MiniTone, MiniMosaic, Stock }

/// <summary>One liked row as the tile rule reads it: its cover url and its album identity (null/"" = none).</summary>
public readonly record struct LikedTileInput(string? Url, string? AlbumKey);

/// <summary>The PURE rules behind the Liked Songs dynamic cover (ch 07 §8).</summary>
public static class LikedCoverRules
{
    /// <summary>The picker's display order — Lens first, the degrade target last. Total over the enum.</summary>
    public static readonly LikedCoverStyle[] PickerOrder =
    [
        LikedCoverStyle.Lens, LikedCoverStyle.Wall, LikedCoverStyle.Rainbow, LikedCoverStyle.Marquee,
        LikedCoverStyle.Feature, LikedCoverStyle.Mosaic, LikedCoverStyle.Tone, LikedCoverStyle.Stack,
        LikedCoverStyle.Stock,
    ];

    /// <summary>How many styles the enum defines — the count <c>Prefs.Appearance.LikedCover</c> clamps against.</summary>
    public const int StyleCount = 9;

    /// <summary>The persisted int as a style; a value this build does not define reads as Stock.</summary>
    public static LikedCoverStyle FromSetting(int value)
        => value is >= (int)LikedCoverStyle.Stock and <= (int)LikedCoverStyle.Stack ? (LikedCoverStyle)value : LikedCoverStyle.Stock;

    public static int ToSetting(LikedCoverStyle style) => (int)style;

    /// <summary>The loc key for a style's picker label, total over the enum.</summary>
    public static string NameKey(LikedCoverStyle style) => style switch
    {
        LikedCoverStyle.Lens => Strings.Detail.LikedCover.Style.Lens,
        LikedCoverStyle.Wall => Strings.Detail.LikedCover.Style.Wall,
        LikedCoverStyle.Rainbow => Strings.Detail.LikedCover.Style.Rainbow,
        LikedCoverStyle.Marquee => Strings.Detail.LikedCover.Style.Marquee,
        LikedCoverStyle.Feature => Strings.Detail.LikedCover.Style.Feature,
        LikedCoverStyle.Mosaic => Strings.Detail.LikedCover.Style.Mosaic,
        LikedCoverStyle.Tone => Strings.Detail.LikedCover.Style.Tone,
        LikedCoverStyle.Stack => Strings.Detail.LikedCover.Style.Stack,
        _ => Strings.Detail.LikedCover.Style.Stock,
    };

    /// <summary>The most tiles any treatment consumes (Rainbow's 4x4).</summary>
    public const int MaxTiles = 16;

    /// <summary>Distinct cover urls from the liked rows, newest-first, deduped by BOTH the album and the url, blank
    /// artwork skipped, capped at <paramref name="max"/> with an early stop.</summary>
    public static IReadOnlyList<string> Tiles(IReadOnlyList<LikedTileInput> rows, int max = MaxTiles)
    {
        if (rows is null || rows.Count == 0 || max <= 0) return Array.Empty<string>();
        var urls = new List<string>(Math.Min(max, rows.Count));
        var albums = new List<string>(Math.Min(max, rows.Count));
        for (int i = 0; i < rows.Count && urls.Count < max; i++)
        {
            string? url = rows[i].Url?.Trim();
            if (string.IsNullOrEmpty(url) || urls.Contains(url)) continue;
            string? album = rows[i].AlbumKey;
            bool keyed = !string.IsNullOrEmpty(album);
            if (keyed && albums.Contains(album!)) continue;
            urls.Add(url);
            if (keyed) albums.Add(album!);
        }
        return urls;
    }

    /// <summary>The same rule over liked HANDLES, written into <paramref name="into"/> (≤ <see cref="MaxTiles"/>):
    /// url = <c>Controls.ArtUrl(ImageId)</c>, album key = the album SLOT, url identity = the image id. Returns the count.
    /// UI thread. The url string is materialised only for an ACCEPTED tile, and the scan stops at the cap.</summary>
    public static int Tiles(ReadOnlySpan<Track> tracks, Span<string> into)
    {
        int max = Math.Min(into.Length, MaxTiles), n = 0, albumCount = 0;
        Span<int> images = stackalloc int[MaxTiles];
        Span<int> albums = stackalloc int[MaxTiles];
        for (int i = 0; i < tracks.Length && n < max; i++)
        {
            var t = tracks[i];
            if (!t.IsValid) continue;
            var image = t.ImageId;
            if (image.IsEmpty || Entities.Strings.Resolve(image).AsSpan().IsWhiteSpace()) continue;
            if (images[..n].Contains(image.Value)) continue;
            int album = t.AlbumSlot;
            if (album > Table.None && albums[..albumCount].Contains(album)) continue;
            if (Controls.ArtUrl(image) is not { Length: > 0 } url) continue;
            images[n] = image.Value;
            into[n++] = url;
            if (album > Table.None) albums[albumCount++] = album;
        }
        return n;
    }

    /// <summary>The DISTINCT-tile floor a style needs before it is honest to compose.</summary>
    public static int MinTiles(LikedCoverStyle style) => style switch
    {
        LikedCoverStyle.Lens => 4,
        LikedCoverStyle.Wall => 8,
        LikedCoverStyle.Rainbow => 8,
        LikedCoverStyle.Marquee => 6,
        LikedCoverStyle.Feature => 4,
        LikedCoverStyle.Mosaic => 4,
        LikedCoverStyle.Tone => 1,
        LikedCoverStyle.Stack => 3,
        _ => 0,
    };

    /// <summary>The style that actually renders: requested, unless the library cannot feed it (then Stock).</summary>
    public static LikedCoverStyle Effective(LikedCoverStyle requested, int distinctTiles)
        => distinctTiles < MinTiles(requested) ? LikedCoverStyle.Stock : requested;

    // ── identity: which uris ARE the liked collection ─────────────────────────────────────────────────────────────

    public const string CollectionUri = EntityUri.LikedCollection;

    /// <summary>The liked collection in ANY spelling (canonical, user-namespaced, facet-suffixed) — the parser's rule.</summary>
    public static bool IsLikedCollection(string? uri) => uri is { Length: > 0 } && EntityUri.IsLikedCollection(uri);

    /// <summary>Every liked spelling folded to <see cref="CollectionUri"/>; anything else passed through ("" for null).</summary>
    public static string Canonical(string? uri) => IsLikedCollection(uri) ? CollectionUri : uri ?? "";

    // ── the site ladder ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The cells a flat collection mosaic fills — the floor below which even the mosaic is dishonest.</summary>
    public const int MosaicCells = 4;

    /// <summary>Half a DIP: a responsive width arriving as 149.9997 still reads as square.</summary>
    public const float SquareEpsilon = 0.5f;

    public static bool IsSquare(float width, float height) => MathF.Abs(width - height) < SquareEpsilon;

    /// <summary>A non-square slot COVER-fits the square treatment at its longer edge.</summary>
    public static float FitSide(float width, float height) => MathF.Max(width, height);

    /// <summary>What a cover of <paramref name="size"/> DIP paints. The floor is INJECTED (the composition owns it); a
    /// non-finite size takes the full treatment (the comparison is written so NaN falls through).</summary>
    public static LikedCoverSite Site(LikedCoverStyle effective, float size, int distinctTiles, float treatmentMinSize)
    {
        if (effective == LikedCoverStyle.Stock) return LikedCoverSite.Stock;
        if (!(size < treatmentMinSize)) return LikedCoverSite.Treatment;
        if (effective == LikedCoverStyle.Tone) return LikedCoverSite.MiniTone;
        return distinctTiles >= MosaicCells ? LikedCoverSite.MiniMosaic : LikedCoverSite.Stock;
    }

    /// <summary>Fill an N-cell grid by cycling the tiles in order. Deterministic.</summary>
    public static string[] FillCells(IReadOnlyList<string> tiles, int cells)
    {
        if (cells <= 0 || tiles is null || tiles.Count == 0) return Array.Empty<string>();
        var filled = new string[cells];
        for (int i = 0; i < cells; i++) filled[i] = tiles[i % tiles.Count];
        return filled;
    }

    /// <summary>Wall's scatter: <c>(i·5 + i%7) % count</c> — no adjacent repeat at ≥ 8 distinct tiles.</summary>
    public static int WallCellIndex(int cell, int tileCount)
    {
        if (tileCount <= 0) return 0;
        if (cell < 0) cell = 0;
        return (cell * 5 + cell % 7) % tileCount;
    }

    public const int RainbowColumns = 4;

    /// <summary>Rainbow's order: graded tiles by hue ascending (ties by index), ungraded last in original order, every
    /// odd row reversed (serpentine). Always a permutation.</summary>
    public static int[] RainbowOrder(IReadOnlyList<float?> hues)
    {
        int n = hues?.Count ?? 0;
        if (n == 0) return Array.Empty<int>();
        var order = new int[n];
        var graded = new List<int>(n);
        for (int i = 0; i < n; i++) if (hues![i] is not null) graded.Add(i);
        graded.Sort((a, b) =>
        {
            int c = hues![a]!.Value.CompareTo(hues[b]!.Value);
            return c != 0 ? c : a.CompareTo(b);
        });
        int w = 0;
        for (int i = 0; i < graded.Count; i++) order[w++] = graded[i];
        for (int i = 0; i < n; i++) if (hues![i] is null) order[w++] = i;
        for (int row = 1; row * RainbowColumns < n; row += 2)
        {
            int lo = row * RainbowColumns, hi = Math.Min(lo + RainbowColumns, n) - 1;
            while (lo < hi) { (order[lo], order[hi]) = (order[hi], order[lo]); lo++; hi--; }
        }
        return order;
    }

    /// <summary>Hue in degrees [0,360) of an ARGB swatch, or null for an achromatic one. Plain uint math.</summary>
    public static float? HueOf(uint argb)
    {
        float r = ((argb >> 16) & 0xFFu) / 255f;
        float g = ((argb >> 8) & 0xFFu) / 255f;
        float b = (argb & 0xFFu) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        if (d <= 0f) return null;
        float h = max == r ? (g - b) / d % 6f
                : max == g ? (b - r) / d + 2f
                :            (r - g) / d + 4f;
        h *= 60f;
        if (h < 0f) h += 360f;
        if (h >= 360f) h -= 360f;
        return h;
    }

    /// <summary>The page tone's key for a composed cover — the newest tile — or null with no art.</summary>
    public static string? ToneAnchorUrl(IReadOnlyList<string> tiles) => tiles is { Count: > 0 } ? tiles[0] : null;
}

// ── 2. the content-filter chips (ch 07 §8, ContentFilterTags verbatim) ───────────────────────────────────────────────

/// <summary>One CURATED chip as the service sends it: the label and the join token.</summary>
public readonly record struct ContentFilterChip(string Title, string Token);

/// <summary>An ordered chip set plus the split point between chips the tracks in view carry and chips with no local
/// evidence. Evidence is a PREFIX: evidenced entries always come first.</summary>
public readonly record struct ContentFilterChipSet(IReadOnlyList<string> Titles, int EvidencedCount)
{
    public static readonly ContentFilterChipSet Empty = new(Array.Empty<string>(), 0);

    public int Count => Titles.Count;

    /// <summary>True when at least one track in view carries this chip, i.e. selecting it yields rows.</summary>
    public bool IsEvidenced(int index) => index < EvidencedCount;
}

/// <summary>A derived chip with how many tracks in view carry it.</summary>
public readonly record struct TagCount(string Title, int Count);

/// <summary>Derives the Liked Songs chip set from the rows' own descriptors, and orders the curated set by evidence.</summary>
public static class ContentFilterTags
{
    /// <summary>Chips shown before the tail is hidden.</summary>
    public const int MaxChips = 10;

    /// <summary>THE evidence floor for descriptor-derived UI (the facts blend gates on the same number).</summary>
    public const int MinTrackCount = 3;

    /// <summary>The chip set, most-common first — <see cref="DeriveCounted(ReadOnlySpan{Track})"/> without the numbers.</summary>
    public static IReadOnlyList<string> Derive(ReadOnlySpan<Track> tracks) => Titles(DeriveCounted(tracks));

    /// <inheritdoc cref="Derive(ReadOnlySpan{Track})"/>
    public static IReadOnlyList<string> Derive(IReadOnlyList<IReadOnlyList<string>?> tagLists) => Titles(DeriveCounted(tagLists));

    /// <summary>The chip set with the carrier counts kept: every descriptor a track carries counts (a chip is a membership
    /// question), casing variants collapse, ≥ <see cref="MinTrackCount"/> carriers, count desc then name, capped.</summary>
    public static IReadOnlyList<TagCount> DeriveCounted(ReadOnlySpan<Track> tracks)
    {
        if (tracks.IsEmpty) return Array.Empty<TagCount>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tracks.Length; i++)
        {
            if (!tracks[i].IsValid || !tracks[i].Knows(TrackFields.Tags)) continue;   // not fetched ≠ no descriptors
            var tags = tracks[i].Tags;
            for (int t = 0; t < tags.Length; t++) Count(counts, Entities.Strings.Resolve(tags[t]));
        }
        return Finish(counts);
    }

    /// <inheritdoc cref="DeriveCounted(ReadOnlySpan{Track})"/>
    public static IReadOnlyList<TagCount> DeriveCounted(IReadOnlyList<IReadOnlyList<string>?> tagLists)
    {
        if (tagLists.Count == 0) return Array.Empty<TagCount>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tagLists.Count; i++)
        {
            var tags = tagLists[i];
            if (tags is null) continue;
            for (int t = 0; t < tags.Count; t++) Count(counts, tags[t]);
        }
        return Finish(counts);
    }

    /// <summary>The server's curated chips, KEPT whole, evidenced ones first (server order within each half), with the
    /// boundary reported so the bar can render the tail disabled.</summary>
    public static ContentFilterChipSet OrderByEvidence(IReadOnlyList<ContentFilterChip> serverChips, ReadOnlySpan<Track> tracks)
        => serverChips.Count == 0 ? ContentFilterChipSet.Empty : Order(serverChips, Present(tracks));

    /// <inheritdoc cref="OrderByEvidence(IReadOnlyList{ContentFilterChip},ReadOnlySpan{Track})"/>
    public static ContentFilterChipSet OrderByEvidence(IReadOnlyList<ContentFilterChip> serverChips, IReadOnlyList<IReadOnlyList<string>?> tagLists)
        => serverChips.Count == 0 ? ContentFilterChipSet.Empty : Order(serverChips, Present(tagLists));

    /// <summary>The OLDER reconcile: drops un-evidenced chips. Superseded by OrderByEvidence; exported, not the live path.</summary>
    public static IReadOnlyList<string> Reconcile(IReadOnlyList<ContentFilterChip> serverChips, ReadOnlySpan<Track> tracks)
        => serverChips.Count == 0 || tracks.IsEmpty ? Array.Empty<string>() : Kept(serverChips, Present(tracks));

    /// <inheritdoc cref="Reconcile(IReadOnlyList{ContentFilterChip},ReadOnlySpan{Track})"/>
    public static IReadOnlyList<string> Reconcile(IReadOnlyList<ContentFilterChip> serverChips, IReadOnlyList<IReadOnlyList<string>?> tagLists)
        => serverChips.Count == 0 || tagLists.Count == 0 ? Array.Empty<string>() : Kept(serverChips, Present(tagLists));

    static void Count(Dictionary<string, int> counts, string tag)
    {
        if (tag.Length == 0) return;
        counts.TryGetValue(tag, out int n);
        counts[tag] = n + 1;
    }

    static IReadOnlyList<TagCount> Finish(Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return Array.Empty<TagCount>();
        var ordered = new List<KeyValuePair<string, int>>(counts.Count);
        foreach (var kv in counts)
            if (kv.Value >= MinTrackCount) ordered.Add(kv);
        if (ordered.Count == 0) return Array.Empty<TagCount>();
        // Count descending, then name, so an enrichment pass cannot visibly shuffle the bar.
        ordered.Sort(static (a, b) =>
        {
            int c = b.Value.CompareTo(a.Value);
            return c != 0 ? c : string.Compare(a.Key, b.Key, StringComparison.CurrentCultureIgnoreCase);
        });
        int take = Math.Min(MaxChips, ordered.Count);
        var result = new TagCount[take];
        for (int i = 0; i < take; i++) result[i] = new TagCount(ordered[i].Key, ordered[i].Value);
        return result;
    }

    static IReadOnlyList<string> Titles(IReadOnlyList<TagCount> counted)
    {
        if (counted.Count == 0) return Array.Empty<string>();
        var titles = new string[counted.Count];
        for (int i = 0; i < counted.Count; i++) titles[i] = counted[i].Title;
        return titles;
    }

    static HashSet<string> Present(ReadOnlySpan<Track> tracks)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tracks.Length; i++)
        {
            if (!tracks[i].IsValid || !tracks[i].Knows(TrackFields.Tags)) continue;
            var tags = tracks[i].Tags;
            for (int t = 0; t < tags.Length; t++) present.Add(Entities.Strings.Resolve(tags[t]));
        }
        return present;
    }

    static HashSet<string> Present(IReadOnlyList<IReadOnlyList<string>?> tagLists)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < tagLists.Count; i++)
        {
            var tags = tagLists[i];
            if (tags is null) continue;
            for (int t = 0; t < tags.Count; t++) present.Add(tags[t]);
        }
        return present;
    }

    static ContentFilterChipSet Order(IReadOnlyList<ContentFilterChip> serverChips, HashSet<string> present)
    {
        var evidenced = new List<string>(serverChips.Count);
        var rest = new List<string>(serverChips.Count);
        foreach (var chip in serverChips)
            (present.Contains(chip.Token) || present.Contains(chip.Title) ? evidenced : rest).Add(chip.Title);
        int evidencedCount = evidenced.Count;
        evidenced.AddRange(rest);
        return new ContentFilterChipSet(evidenced, evidencedCount);
    }

    static IReadOnlyList<string> Kept(IReadOnlyList<ContentFilterChip> serverChips, HashSet<string> present)
    {
        if (present.Count == 0) return Array.Empty<string>();
        List<string>? kept = null;
        foreach (var chip in serverChips)
            if (present.Contains(chip.Token) || present.Contains(chip.Title))
                (kept ??= new List<string>(serverChips.Count)).Add(chip.Title);
        return (IReadOnlyList<string>?)kept ?? Array.Empty<string>();
    }
}
