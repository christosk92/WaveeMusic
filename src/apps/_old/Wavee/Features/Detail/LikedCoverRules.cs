using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

/// <summary>Which treatment the Liked Songs collection cover renders. Persisted as an int via
/// <c>WaveeSettings.LikedCoverStyle</c>, so the VALUES ARE WIRE-STABLE: a new treatment appends, and an existing value
/// never re-means (a downgrade from a future build must still read its own stored int as the same look).
///
/// <para><see cref="Stock"/> is 0 because it is the DEGRADE TARGET, not the default: it is the bundled PNG that is
/// always renderable — offline, on a cold open, and on a library too small to feed a collage — so every ladder in
/// <see cref="LikedCoverRules"/> lands here. The shipped default is <see cref="Lens"/> (see
/// <c>WaveeSettings.LikedCoverStyle</c>); a fresh install still SEES Stock until it owns enough art, because
/// <see cref="LikedCoverRules.Effective"/> says so — automatically, with no setting write.</para></summary>
public enum LikedCoverStyle
{
    /// <summary>Today's bundled purple-heart PNG. The offline-safe anchor and every ladder's floor.</summary>
    Stock = 0,
    /// <summary>Blurred, tinted mosaic ground with a crisp copy revealed through a heart-shaped mask.</summary>
    Lens = 1,
    /// <summary>A tilted 6x6 wall of the collection under a slow ambient drift.</summary>
    Wall = 2,
    /// <summary>A 4x4 grid ordered by dominant hue, serpentine rows.</summary>
    Rainbow = 3,
    /// <summary>Three diagonal strips of covers scrolling in alternating directions over a palette ground.</summary>
    Marquee = 4,
    /// <summary>The newest like at 2x2 in a 3x3 grid, six follow.</summary>
    Feature = 5,
    /// <summary>A flat 3x3 grid of the nine newest likes.</summary>
    Mosaic = 6,
    /// <summary>A multi-radial gradient built from the extracted cover swatches — no art tiles at all.</summary>
    Tone = 7,
    /// <summary>The last five covers fanned, hub-card style, over a palette ground.</summary>
    Stack = 8,
}

/// <summary>What a liked cover of a given size actually paints — the output of <see cref="LikedCoverRules.Site"/>.
/// Purely a VIEW decision (no persistence, no wire), so unlike <see cref="LikedCoverStyle"/> these values are free to
/// be reordered.</summary>
public enum LikedCoverSite
{
    /// <summary>The full composed treatment on its 304 canvas.</summary>
    Treatment,
    /// <summary>Tone's gradient + heart at miniature scale — the one treatment that reads small.</summary>
    MiniTone,
    /// <summary>The app's ordinary flat 2x2 collection mosaic, exactly as a cover-less playlist shows.</summary>
    MiniMosaic,
    /// <summary>The bundled PNG — every ladder's floor.</summary>
    Stock,
}

/// <summary>The PURE rules behind the Liked Songs dynamic cover: which treatment actually renders, which artwork feeds
/// it, and how a grid is filled or ordered. Engine-free by construction (System + Wavee.Core + the generated loc
/// consts) so every decision is pinned by <c>LikedCoverRulesTests</c> without mounting a page, a store or a GPU.
///
/// <para>The whole point of this file is the HONESTY LADDER: a treatment is only ever composed from art the library
/// actually owns. Below a style's minimum the answer is <see cref="LikedCoverStyle.Stock"/> — the bundled PNG, i.e.
/// exactly what the app paints today — never a half-empty grid and never a grey hole.</para></summary>
public static class LikedCoverRules
{
    /// <summary>The picker's display order: the prototype's own order — <see cref="LikedCoverStyle.Lens"/>, the
    /// shipped default, first — with the degrade target last. Total over the enum, so every stored value has a slot
    /// the picker can check.</summary>
    public static readonly LikedCoverStyle[] PickerOrder =
    [
        LikedCoverStyle.Lens, LikedCoverStyle.Wall, LikedCoverStyle.Rainbow, LikedCoverStyle.Marquee,
        LikedCoverStyle.Feature, LikedCoverStyle.Mosaic, LikedCoverStyle.Tone, LikedCoverStyle.Stack,
        LikedCoverStyle.Stock,
    ];

    /// <summary>The persisted int as a style. A value this build does not define — a registry edit, a downgrade from a
    /// future version that shipped a tenth treatment — reads as <see cref="LikedCoverStyle.Stock"/> rather than
    /// throwing or rendering nothing; the picker then reflects the clamped value on its next read.</summary>
    public static LikedCoverStyle FromSetting(int value)
        => value is >= (int)LikedCoverStyle.Stock and <= (int)LikedCoverStyle.Stack
            ? (LikedCoverStyle)value
            : LikedCoverStyle.Stock;

    /// <summary>The style as its persisted int.</summary>
    public static int ToSetting(LikedCoverStyle style) => (int)style;

    /// <summary>The loc key for a style's picker label. Here rather than in the picker so the enum and its wording
    /// cannot drift apart, and so the mapping is total — every defined value has a name.</summary>
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

    /// <summary>The most tiles any treatment consumes (Rainbow's 4x4). Every call site asks for this one number so the
    /// tile list is computed once per cover and shared by the full-size treatment and the picker's miniatures.</summary>
    public const int MaxTiles = 16;

    /// <summary>Distinct cover urls from the liked tracks, newest-first (the input order — <c>LibraryStore.Liked</c>
    /// is already newest-first).
    ///
    /// <para>Deduped by BOTH the album uri and the url itself: <c>StoreLibrarySource.TilesFromTracks</c> dedupes by
    /// album uri alone, which is right for a 2x2 but lets two different releases sharing one rendition (a single and
    /// its parent album, a re-issue) put the same picture twice into a 3x3 — where it reads as a bug. A track whose
    /// artwork is missing or blank is skipped entirely rather than contributing an empty cell.</para></summary>
    public static IReadOnlyList<string> Tiles(IReadOnlyList<Track> tracks, int max = MaxTiles)
    {
        if (tracks is null || tracks.Count == 0 || max <= 0) return Array.Empty<string>();

        var urls = new List<string>(Math.Min(max, tracks.Count));
        var seenUrl = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? seenAlbum = null;
        for (int i = 0; i < tracks.Count && urls.Count < max; i++)
        {
            string? url = tracks[i].Image?.Url;
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (seenUrl.Contains(url)) continue;

            string? albumUri = tracks[i].Album?.Uri;
            bool keyed = !string.IsNullOrEmpty(albumUri);
            if (keyed && (seenAlbum ??= new HashSet<string>(StringComparer.Ordinal)).Contains(albumUri!)) continue;

            seenUrl.Add(url);
            if (keyed) seenAlbum!.Add(albumUri!);
            urls.Add(url);
        }
        return urls;
    }

    /// <summary>The minimum number of DISTINCT tiles a style needs before it is honest to compose. Below it the style
    /// degrades to <see cref="LikedCoverStyle.Stock"/>; at or above it <see cref="FillCells"/> cycles the tiles to
    /// fill the remaining cells.
    ///
    /// <para>These are the "is this recognisably a collage of MY music" floors, not the geometric cell counts: a 6x6
    /// Wall cycling eight covers still reads as a wall, a 6x6 Wall cycling two reads as wallpaper.</para></summary>
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
        _ => 0,      // Stock composes nothing and therefore needs nothing
    };

    /// <summary>The style that actually renders. Requested, unless the library cannot feed it — an empty or still-
    /// loading liked list yields zero tiles, which is Stock, which is byte-identical to what the app paints today, so
    /// the cold first frame never flashes.</summary>
    public static LikedCoverStyle Effective(LikedCoverStyle requested, int distinctTiles)
        => distinctTiles < MinTiles(requested) ? LikedCoverStyle.Stock : requested;

    // ── identity: which uris ARE the liked collection ─────────────────────────────────────────────────────────────

    /// <summary>The canonical Liked Songs uri. THE parser owns the string (<see cref="EntityUri.LikedCollection"/>) —
    /// it also has to recognise the other spellings — and this is the cover feature's name for it.</summary>
    public const string CollectionUri = EntityUri.LikedCollection;

    /// <summary>Is this uri the Liked Songs collection, in ANY of its spellings — the canonical form, the
    /// user-namespaced <c>spotify:user:&lt;u&gt;:collection</c> that Home and recents section items carry, or the
    /// facet-suffixed variant? Delegates to <see cref="EntityUri.IsLikedCollection"/> (hydration-facade-design.md
    /// §1.1: one parse, never a private prefix ladder) and is re-exposed here so the cover's own rules read as one
    /// vocabulary.</summary>
    public static bool IsLikedCollection(string? uri) => EntityUri.IsLikedCollection(uri);

    /// <summary>Every liked spelling folded to <see cref="CollectionUri"/>; anything else passed through untouched.
    /// A card hands this to the artwork, the nav dispatcher and the now-playing match so all three agree on one
    /// string.</summary>
    public static string Canonical(string? uri) => EntityUri.CanonicalLiked(uri);

    // ── the site ladder: what a cover of a given SIZE actually paints ─────────────────────────────────────────────

    /// <summary>The cells a flat collection mosaic fills — and therefore the tile floor below which even the mosaic
    /// cannot be composed honestly.</summary>
    public const int MosaicCells = 4;

    /// <summary>How close two edges must be to count as one square. Half a DIP: a responsive card width can arrive as
    /// 149.9997 and must still read as square.</summary>
    public const float SquareEpsilon = 0.5f;

    /// <summary>Is this slot square (and therefore a treatment's natural frame)?</summary>
    public static bool IsSquare(float width, float height) => MathF.Abs(width - height) < SquareEpsilon;

    /// <summary>The square edge a NON-square slot composes the cover at. COVER-fit — the longer edge, centred and
    /// clipped — because that is what <c>Surfaces.Artwork</c> already does to every other cover in a letterbox slot,
    /// and a picked treatment letterboxed inside grey bands would be the one artwork on the page that does not fill
    /// its frame.</summary>
    public static float FitSide(float width, float height) => MathF.Max(width, height);

    /// <summary>What a cover of <paramref name="size"/> DIP actually paints, given the style that survived
    /// <see cref="Effective"/> and the art on hand. ONE ladder, so the rail hero, a 150-DIP shelf tile and a 40-DIP
    /// sidebar row cannot disagree about where the floors are.
    ///
    /// <para><paramref name="treatmentMinSize"/> is injected rather than read from a const here: the floor is a
    /// COMPOSITION fact (it is where the badge stops being legible), so <c>LikedCoverTreatments.BadgeMinSize</c> owns
    /// it and this file stays engine-free. A non-finite size (an unmeasured slot) takes the full treatment rather than
    /// collapsing — the comparison is written so NaN falls through.</para></summary>
    public static LikedCoverSite Site(LikedCoverStyle effective, float size, int distinctTiles, float treatmentMinSize)
    {
        if (effective == LikedCoverStyle.Stock) return LikedCoverSite.Stock;
        if (!(size < treatmentMinSize)) return LikedCoverSite.Treatment;
        // Below the floor a 6x6 wall is unreadable specks. Tone carries no art at all and reads perfectly small, so it
        // keeps its own gradient; everything else falls to the app's ordinary 2x2 collection mosaic, and to the stock
        // PNG only when even four distinct covers are not there yet.
        if (effective == LikedCoverStyle.Tone) return LikedCoverSite.MiniTone;
        return distinctTiles >= MosaicCells ? LikedCoverSite.MiniMosaic : LikedCoverSite.Stock;
    }

    /// <summary>Fill an N-cell grid from however many tiles exist by cycling them in order. Deterministic, so a
    /// re-render of an unchanged library produces the identical cell list and nothing re-decodes.</summary>
    public static string[] FillCells(IReadOnlyList<string> tiles, int cells)
    {
        if (cells <= 0 || tiles is null || tiles.Count == 0) return Array.Empty<string>();
        var filled = new string[cells];
        for (int i = 0; i < cells; i++) filled[i] = tiles[i % tiles.Count];
        return filled;
    }

    /// <summary>Wall's scatter: which tile lands in cell <paramref name="cell"/> of the 6x6 grid.
    ///
    /// <para>The prototype's <c>(i*5 + i%7) % count</c>. The step between neighbouring cells is +6 (or -1 across each
    /// seventh cell), so at Wall's minimum of eight distinct tiles no two adjacent cells ever repeat and the grid
    /// never resolves into visible stripes.</para></summary>
    public static int WallCellIndex(int cell, int tileCount)
    {
        if (tileCount <= 0) return 0;
        if (cell < 0) cell = 0;
        return (cell * 5 + cell % 7) % tileCount;
    }

    /// <summary>Rainbow's grid width. The serpentine below is authored against it.</summary>
    public const int RainbowColumns = 4;

    /// <summary>Rainbow's cell order: tile indices sorted by hue ascending, with every second row reversed so the ramp
    /// runs continuously boustrophedon instead of snapping back at each row break.
    ///
    /// <para>A null hue means "not graded yet, or genuinely achromatic" — those go LAST, in their original order, so a
    /// grading landing later moves a tile INTO the ramp rather than reshuffling everything. The result is always a
    /// permutation of <c>0..hues.Count-1</c>.</para></summary>
    public static int[] RainbowOrder(IReadOnlyList<float?> hues)
    {
        int n = hues?.Count ?? 0;
        if (n == 0) return Array.Empty<int>();

        var order = new int[n];
        var graded = new List<int>(n);
        for (int i = 0; i < n; i++) if (hues![i] is not null) graded.Add(i);
        // Tie-broken by the original index, which makes an unstable List.Sort behave like a stable one — two covers
        // graded to the same hue must not swap places between renders.
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
            while (lo < hi)
            {
                (order[lo], order[hi]) = (order[hi], order[lo]);
                lo++; hi--;
            }
        }
        return order;
    }

    /// <summary>Hue in degrees <c>[0,360)</c> of an opaque ARGB swatch, or null when the swatch carries no chroma at
    /// all (black, white, any grey) and therefore has no place on a hue ramp.
    ///
    /// <para>Plain uint math on purpose: these rules stay engine-free, and <c>WaveePalette</c>'s ColorF cannot cross
    /// into the test assembly.</para></summary>
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

    /// <summary>The url the detail page's tone plane keys off for a non-Stock liked cover — the newest tile, i.e. the
    /// cover the treatment leads with. Null when there is no art, which is also the "keep today's toneless liked page"
    /// answer.</summary>
    public static string? ToneAnchorUrl(IReadOnlyList<string> tiles)
        => tiles is { Count: > 0 } ? tiles[0] : null;
}
