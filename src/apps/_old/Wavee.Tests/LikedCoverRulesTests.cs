using System;
using System.Collections.Generic;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

/// <summary>The Liked Songs dynamic cover's PURE rules (<c>Features/Detail/LikedCoverRules.cs</c>, source-included
/// here because it is engine-free): which treatment survives contact with the user's actual library, which artwork
/// feeds it, and how a grid is filled or ordered.
///
/// <para>The rule worth pinning hardest is the DEGRADE LADDER. Everything about this feature is "made from your own
/// music", so the only honest answer when the library cannot feed a treatment is the bundled stock PNG — which is also
/// exactly what the app painted before the feature existed. A half-empty grid, a repeated cover reading as a bug, or a
/// throw on a hand-edited registry value are all failures of the same rule.</para></summary>
public class LikedCoverRulesTests
{
    static Track T(string? url, string albumUri = "", string id = "t")
        => new(id, "spotify:track:" + id, id,
            Array.Empty<ArtistRef>(), new AlbumRef("", albumUri, ""),
            180_000, false, url is null ? null : new Image(url));

    static Track NoAlbum(string url, string id = "t")
        => new(id, "spotify:track:" + id, id,
            Array.Empty<ArtistRef>(), null!,
            180_000, false, new Image(url));

    // ── FromSetting: the persisted int, clamped (E11) ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, LikedCoverStyle.Stock)]
    [InlineData(1, LikedCoverStyle.Lens)]
    [InlineData(2, LikedCoverStyle.Wall)]
    [InlineData(3, LikedCoverStyle.Rainbow)]
    [InlineData(4, LikedCoverStyle.Marquee)]
    [InlineData(5, LikedCoverStyle.Feature)]
    [InlineData(6, LikedCoverStyle.Mosaic)]
    [InlineData(7, LikedCoverStyle.Tone)]
    [InlineData(8, LikedCoverStyle.Stack)]
    public void EveryDefinedValue_ReadsBackAsItself(int stored, LikedCoverStyle expected)
    {
        Assert.Equal(expected, LikedCoverRules.FromSetting(stored));
        Assert.Equal(stored, LikedCoverRules.ToSetting(expected));
    }

    /// <summary>A hand-edited registry value, a downgrade from a build that shipped a tenth treatment, or a garbage
    /// write: the answer is Stock, never an exception and never an unrendered enum value.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-42)]
    [InlineData(9)]
    [InlineData(99)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UnknownOrOutOfRangeValues_ClampToStock(int stored)
        => Assert.Equal(LikedCoverStyle.Stock, LikedCoverRules.FromSetting(stored));

    /// <summary>The enum's ints ARE the wire. If a value ever moves, every install that stored it silently changes
    /// look — so the mapping is pinned as a whole, not just per case.</summary>
    [Fact]
    public void TheWireValuesAreStable()
    {
        foreach (var style in Enum.GetValues<LikedCoverStyle>())
            Assert.Equal(style, LikedCoverRules.FromSetting((int)style));

        Assert.Equal(0, (int)LikedCoverStyle.Stock);
        Assert.Equal(8, (int)LikedCoverStyle.Stack);
        Assert.Equal(9, Enum.GetValues<LikedCoverStyle>().Length);
    }

    // ── MinTiles + Effective: the degrade ladder (E1/E3/E9/E22) ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(LikedCoverStyle.Stock, 0)]
    [InlineData(LikedCoverStyle.Lens, 4)]
    [InlineData(LikedCoverStyle.Wall, 8)]
    [InlineData(LikedCoverStyle.Rainbow, 8)]
    [InlineData(LikedCoverStyle.Marquee, 6)]
    [InlineData(LikedCoverStyle.Feature, 4)]
    [InlineData(LikedCoverStyle.Mosaic, 4)]
    [InlineData(LikedCoverStyle.Tone, 1)]
    [InlineData(LikedCoverStyle.Stack, 3)]
    public void TheMinTilesLadderIsPinned(LikedCoverStyle style, int expected)
        => Assert.Equal(expected, LikedCoverRules.MinTiles(style));

    /// <summary>One tile below its minimum a treatment is not composable; at its minimum it is. Both sides of every
    /// rung, because the rung is the whole contract.</summary>
    [Theory]
    [InlineData(LikedCoverStyle.Lens)]
    [InlineData(LikedCoverStyle.Wall)]
    [InlineData(LikedCoverStyle.Rainbow)]
    [InlineData(LikedCoverStyle.Marquee)]
    [InlineData(LikedCoverStyle.Feature)]
    [InlineData(LikedCoverStyle.Mosaic)]
    [InlineData(LikedCoverStyle.Tone)]
    [InlineData(LikedCoverStyle.Stack)]
    public void EachStyleDegradesJustBelowItsMinimumAndSurvivesAtIt(LikedCoverStyle style)
    {
        int min = LikedCoverRules.MinTiles(style);
        Assert.Equal(LikedCoverStyle.Stock, LikedCoverRules.Effective(style, min - 1));
        Assert.Equal(style, LikedCoverRules.Effective(style, min));
        Assert.Equal(style, LikedCoverRules.Effective(style, min + 100));
    }

    /// <summary>An empty library — or a liked list still loading, which yields the same zero tiles — paints the stock
    /// PNG for every requested style. That is what makes the cold first frame identical to the pre-feature app.</summary>
    [Fact]
    public void AnEmptyLibraryIsStockWhateverWasRequested()
    {
        foreach (var style in Enum.GetValues<LikedCoverStyle>())
            Assert.Equal(LikedCoverStyle.Stock, LikedCoverRules.Effective(style, 0));
    }

    /// <summary>Lens is the SHIPPED DEFAULT, so its rung is the one a fresh install actually walks: under four
    /// distinct covers it paints the stock PNG — the pre-feature look, with no setting written — and at four it
    /// composes, by itself, the moment the library gets there (E3/E21). Its own no-fallback rule is pinned alongside:
    /// there is no tile count at which a stored Lens renders as some OTHER treatment.</summary>
    [Theory]
    [InlineData(0, LikedCoverStyle.Stock)]
    [InlineData(3, LikedCoverStyle.Stock)]
    [InlineData(4, LikedCoverStyle.Lens)]
    [InlineData(16, LikedCoverStyle.Lens)]
    [InlineData(4000, LikedCoverStyle.Lens)]
    public void LensComposesAtItsFloorAndDegradesOnlyToStock(int tiles, LikedCoverStyle expected)
        => Assert.Equal(expected, LikedCoverRules.Effective(LikedCoverStyle.Lens, tiles));

    /// <summary>The picker offers every style the enum defines, once each, and LEADS with the default — so a fresh
    /// install opens the flyout on its own checked card rather than on someone else's.</summary>
    [Fact]
    public void ThePickerOrderLeadsWithLensAndIsTotalAndDistinct()
    {
        var order = LikedCoverRules.PickerOrder;
        Assert.Equal(LikedCoverStyle.Lens, order[0]);
        Assert.Equal(LikedCoverStyle.Stock, order[^1]);
        Assert.Equal(order.Length, new HashSet<LikedCoverStyle>(order).Count);
        Assert.Equal(Enum.GetValues<LikedCoverStyle>().Length, order.Length);
    }

    // ── Tiles: which artwork feeds a treatment (E13) ────────────────────────────────────────────────────────────────

    [Fact]
    public void TilesKeepTheNewestFirstInputOrder()
    {
        var tiles = LikedCoverRules.Tiles([T("u1", "al:1"), T("u2", "al:2"), T("u3", "al:3")]);
        Assert.Equal(["u1", "u2", "u3"], tiles);
    }

    /// <summary>Two tracks off the same release contribute ONE tile — the 2x2 rule Wavee already uses for cover-less
    /// playlists.</summary>
    [Fact]
    public void TilesDedupeByAlbumUri()
    {
        var tiles = LikedCoverRules.Tiles([T("u1", "al:1"), T("u2", "al:1"), T("u3", "al:2")]);
        Assert.Equal(["u1", "u3"], tiles);
    }

    /// <summary>And two DIFFERENT releases that happen to share one rendition — a single and its parent album, a
    /// re-issue — also contribute one tile. Album-uri dedupe alone lets that pair through, where the same picture twice
    /// in a 3x3 reads as a rendering bug.</summary>
    [Fact]
    public void TilesAlsoDedupeByUrlAcrossDifferentAlbums()
    {
        var tiles = LikedCoverRules.Tiles([T("same", "al:1"), T("same", "al:2"), T("other", "al:3")]);
        Assert.Equal(["same", "other"], tiles);
    }

    /// <summary>A duplicated url must not burn its album's slot: the NEXT track off that album still gets to
    /// contribute its own distinct artwork.</summary>
    [Fact]
    public void ADuplicateUrlDoesNotConsumeItsAlbumSlot()
    {
        var tiles = LikedCoverRules.Tiles([T("a", "al:1"), T("a", "al:2"), T("b", "al:2")]);
        Assert.Equal(["a", "b"], tiles);
    }

    [Fact]
    public void TracksWithoutUsableArtworkAreSkippedEntirely()
    {
        var tiles = LikedCoverRules.Tiles(
        [
            T(null, "al:1"),        // no Image at all
            T("", "al:2"),          // an Image whose url is blank
            T("   ", "al:3"),       // whitespace, which Image normalization trims to blank
            T("real", "al:4"),
        ]);
        Assert.Equal(["real"], tiles);
    }

    /// <summary>A track with no album reference at all still contributes, deduped on its url alone.</summary>
    [Fact]
    public void AMissingAlbumRefFallsBackToUrlDedupe()
    {
        var tiles = LikedCoverRules.Tiles([NoAlbum("a"), NoAlbum("a"), NoAlbum("b")]);
        Assert.Equal(["a", "b"], tiles);
    }

    /// <summary>An album-less track carries an EMPTY album uri, which must not be treated as one shared album that
    /// swallows every one of them.</summary>
    [Fact]
    public void AnEmptyAlbumUriIsNotAnAlbumKey()
    {
        var tiles = LikedCoverRules.Tiles([T("a"), T("b"), T("c")]);
        Assert.Equal(["a", "b", "c"], tiles);
    }

    [Fact]
    public void TilesAreCappedAndStopScanningOnceFull()
    {
        var tracks = new List<Track>();
        for (int i = 0; i < 200; i++) tracks.Add(T("u" + i, "al:" + i, "t" + i));

        Assert.Equal(LikedCoverRules.MaxTiles, LikedCoverRules.Tiles(tracks).Count);
        Assert.Equal(16, LikedCoverRules.MaxTiles);
        Assert.Equal(4, LikedCoverRules.Tiles(tracks, 4).Count);
        Assert.Empty(LikedCoverRules.Tiles(tracks, 0));
    }

    [Fact]
    public void TilesOfNothingIsNothing()
    {
        Assert.Empty(LikedCoverRules.Tiles(Array.Empty<Track>()));
        Assert.Empty(LikedCoverRules.Tiles(null!));
    }

    [Fact]
    public void ToneAnchorIsTheNewestTileOrNothing()
    {
        Assert.Equal("first", LikedCoverRules.ToneAnchorUrl(["first", "second"]));
        Assert.Null(LikedCoverRules.ToneAnchorUrl(Array.Empty<string>()));
    }

    // ── FillCells ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FillCellsCyclesTheTilesInOrder()
        => Assert.Equal(["a", "b", "c", "d", "a", "b", "c", "d", "a"], LikedCoverRules.FillCells(["a", "b", "c", "d"], 9));

    [Fact]
    public void FillCellsIsExactWhenTheTilesAlreadyFit()
        => Assert.Equal(["a", "b", "c"], LikedCoverRules.FillCells(["a", "b", "c"], 3));

    /// <summary>Determinism is load-bearing: a re-render of an unchanged library must produce the identical cell list
    /// so the url-keyed mosaic cells never re-decode.</summary>
    [Fact]
    public void FillCellsIsDeterministic()
    {
        var tiles = new[] { "a", "b", "c", "d", "e" };
        Assert.Equal(LikedCoverRules.FillCells(tiles, 16), LikedCoverRules.FillCells(tiles, 16));
    }

    [Fact]
    public void FillCellsDegradesToNothingRatherThanEmptyCells()
    {
        Assert.Empty(LikedCoverRules.FillCells(Array.Empty<string>(), 9));
        Assert.Empty(LikedCoverRules.FillCells(null!, 9));
        Assert.Empty(LikedCoverRules.FillCells(["a"], 0));
        Assert.Empty(LikedCoverRules.FillCells(["a"], -3));
    }

    // ── WallCellIndex ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(1, 8, 6)]     // 1*5 + 1 = 6
    [InlineData(2, 8, 4)]     // 2*5 + 2 = 12 -> 4
    [InlineData(6, 8, 4)]     // 6*5 + 6 = 36 -> 4
    [InlineData(7, 8, 3)]     // 7*5 + 0 = 35 -> 3
    [InlineData(35, 16, 15)]  // the last cell of the 6x6 grid: 35*5 + 0 = 175 -> 15
    public void WallScatterIsPinned(int cell, int tileCount, int expected)
        => Assert.Equal(expected, LikedCoverRules.WallCellIndex(cell, tileCount));

    /// <summary>The whole point of the scatter: at Wall's minimum of eight distinct tiles (and above) no two
    /// neighbouring cells of the 6x6 grid ever show the same cover, so the wall never resolves into stripes.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void NoTwoAdjacentWallCellsShareATile(int tileCount)
    {
        const int Columns = 6, Cells = 36;
        for (int cell = 0; cell < Cells; cell++)
        {
            if (cell % Columns == Columns - 1) continue;    // the row wraps here; those two cells are not neighbours
            int here = LikedCoverRules.WallCellIndex(cell, tileCount);
            Assert.NotEqual(here, LikedCoverRules.WallCellIndex(cell + 1, tileCount));
        }
    }

    /// <summary>Every cell must land ON a tile — an out-of-range index would index past the tile list and throw at
    /// paint time.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    public void WallScatterAlwaysIndexesAnExistingTile(int tileCount)
    {
        for (int cell = 0; cell < 36; cell++)
        {
            int i = LikedCoverRules.WallCellIndex(cell, tileCount);
            Assert.InRange(i, 0, tileCount - 1);
        }
    }

    [Fact]
    public void WallScatterSurvivesDegenerateInput()
    {
        Assert.Equal(0, LikedCoverRules.WallCellIndex(3, 0));
        Assert.Equal(0, LikedCoverRules.WallCellIndex(3, -1));
        Assert.Equal(0, LikedCoverRules.WallCellIndex(-5, 8));
    }

    // ── RainbowOrder ────────────────────────────────────────────────────────────────────────────────────────────────

    static float?[] Hues(params float?[] h) => h;

    [Fact]
    public void RainbowSortsByHueAscendingWithinTheFirstRow()
    {
        var order = LikedCoverRules.RainbowOrder(Hues(300f, 10f, 200f, 120f));
        Assert.Equal([1, 3, 2, 0], order);
    }

    /// <summary>Every second row runs right-to-left, so the ramp is continuous across the row break instead of
    /// snapping from violet back to red.</summary>
    [Fact]
    public void RainbowReversesEverySecondRow()
    {
        // Eight already-ascending hues: row 0 keeps 0..3, row 1 becomes 7,6,5,4.
        var order = LikedCoverRules.RainbowOrder(Hues(0f, 40f, 80f, 120f, 160f, 200f, 240f, 280f));
        Assert.Equal([0, 1, 2, 3, 7, 6, 5, 4], order);
    }

    [Fact]
    public void RainbowSerpentineHandlesAPartialTrailingRow()
    {
        // Six ascending hues: row 0 keeps 0..3, the two-cell row 1 becomes 5,4.
        var order = LikedCoverRules.RainbowOrder(Hues(0f, 40f, 80f, 120f, 160f, 200f));
        Assert.Equal([0, 1, 2, 3, 5, 4], order);
    }

    /// <summary>An ungraded cover has no hue and no place on the ramp: it goes last, keeping its original relative
    /// order, so a grading landing later moves ONE tile into the ramp rather than reshuffling the grid.</summary>
    [Fact]
    public void UngradedTilesGoLastInTheirOriginalOrder()
    {
        // Rows before the serpentine: [1, 3, 0, 2] then [4, 5]; row 1 reverses to [5, 4].
        var order = LikedCoverRules.RainbowOrder(Hues(200f, 10f, null, 100f, null, null));
        Assert.Equal([1, 3, 0, 2, 5, 4], order);
    }

    [Fact]
    public void EqualHuesKeepTheirOriginalOrder()
        => Assert.Equal([0, 1, 2, 3], LikedCoverRules.RainbowOrder(Hues(90f, 90f, 90f, 90f)));

    /// <summary>Whatever the input, the answer is a permutation — every tile is placed exactly once, so no cell is
    /// blank and no cover is drawn twice.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(16)]
    public void RainbowOrderIsAlwaysAPermutation(int n)
    {
        var hues = new float?[n];
        for (int i = 0; i < n; i++) hues[i] = i % 3 == 0 ? null : (n - i) * 17f % 360f;

        var order = LikedCoverRules.RainbowOrder(hues);
        Assert.Equal(n, order.Length);
        var seen = new HashSet<int>(order);
        Assert.Equal(n, seen.Count);
        for (int i = 0; i < n; i++) Assert.Contains(i, seen);
    }

    [Fact]
    public void RainbowOrderOfNothingIsNothing()
    {
        Assert.Empty(LikedCoverRules.RainbowOrder(Array.Empty<float?>()));
        Assert.Empty(LikedCoverRules.RainbowOrder(null!));
    }

    // ── HueOf ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0xFFFF0000u, 0f)]
    [InlineData(0xFFFFFF00u, 60f)]
    [InlineData(0xFF00FF00u, 120f)]
    [InlineData(0xFF00FFFFu, 180f)]
    [InlineData(0xFF0000FFu, 240f)]
    [InlineData(0xFFFF00FFu, 300f)]
    public void HueOfThePrimariesIsExact(uint argb, float expected)
    {
        var hue = LikedCoverRules.HueOf(argb);
        Assert.NotNull(hue);
        Assert.InRange(hue!.Value, expected - 0.01f, expected + 0.01f);
    }

    /// <summary>A swatch with no chroma has no hue — saying "red-ish" about a grey is exactly the kind of invented
    /// answer the ramp must not contain.</summary>
    [Theory]
    [InlineData(0xFF000000u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0xFF808080u)]
    [InlineData(0u)]
    public void AchromaticSwatchesHaveNoHue(uint argb)
        => Assert.Null(LikedCoverRules.HueOf(argb));

    /// <summary>The alpha byte is not part of the hue, and the answer always lands inside the ramp's domain.</summary>
    [Fact]
    public void HueIgnoresAlphaAndStaysInRange()
    {
        Assert.Equal(LikedCoverRules.HueOf(0xFF3B82F6u), LikedCoverRules.HueOf(0x113B82F6u));

        for (uint r = 0; r <= 255; r += 17)
            for (uint g = 0; g <= 255; g += 51)
                for (uint b = 0; b <= 255; b += 51)
                    if (LikedCoverRules.HueOf(0xFF000000u | (r << 16) | (g << 8) | b) is { } h)
                        Assert.InRange(h, 0f, 359.9999f);
    }

    // ── Style names ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every treatment has its own label. A shared or empty key means a picker card with no name, or two
    /// cards claiming to be the same thing.</summary>
    [Fact]
    public void EveryStyleHasItsOwnNameKey()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var style in Enum.GetValues<LikedCoverStyle>())
        {
            string key = LikedCoverRules.NameKey(style);
            Assert.False(string.IsNullOrWhiteSpace(key));
            Assert.True(keys.Add(key), $"{style} reuses the loc key {key}");
        }
    }

    // -- Identity: which uris ARE the liked collection ----------------------------------------------------------------

    /// <summary>THE bug this predicate exists for: Spotify's recents feed spells Liked Songs more than one way, and a
    /// string compare against the canonical form alone answered NO for the very cards the dynamic cover exists to
    /// dress -- so a 150-DIP recents tile kept painting the stock purple heart (served, to add insult, from
    /// <c>misc.scdn.co/liked-songs/liked-songs-300.png</c>) while the detail page showed the chosen treatment.
    ///
    /// <para>The negative rows matter as much: the SIBLING collections are separate surfaces, and dressing
    /// <c>spotify:collection:albums</c> in the liked cover would be a different, louder bug.</para></summary>
    [Theory]
    // the canonical spelling
    [InlineData("spotify:collection:tracks", true)]
    // the user-namespaced spelling Home / recents section items carry (measured in assets/spotify/home.json)
    [InlineData("spotify:user:@:collection", true)]
    [InlineData("spotify:user:abc123:collection", true)]
    [InlineData("spotify:user:abc123:collection:tracks", true)]
    // sibling collections -- real, separate surfaces
    [InlineData("spotify:collection:albums", false)]
    [InlineData("spotify:collection:artists", false)]
    [InlineData("spotify:collection:shows", false)]
    [InlineData("spotify:collection:episodes", false)]
    [InlineData("spotify:user:abc123:collection:albums", false)]
    // not a collection at all
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", false)]
    [InlineData("spotify:album:1", false)]
    [InlineData("spotify:user:abc123", false)]
    [InlineData("wavee:playlist:local-1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLikedCollection_AcceptsEverySpelling_AndNothingElse(string? uri, bool expected)
        => Assert.Equal(expected, LikedCoverRules.IsLikedCollection(uri));

    /// <summary>Canonical folds every liked spelling to ONE string so the artwork, the nav dispatcher and the
    /// now-playing match cannot disagree about which entity a card stands for. Anything else passes through
    /// untouched -- canonicalization is not a place to normalize other uris.</summary>
    [Theory]
    [InlineData("spotify:collection:tracks", "spotify:collection:tracks")]
    [InlineData("spotify:user:@:collection", "spotify:collection:tracks")]
    [InlineData("spotify:user:abc123:collection:tracks", "spotify:collection:tracks")]
    [InlineData("spotify:collection:albums", "spotify:collection:albums")]
    [InlineData("spotify:playlist:x", "spotify:playlist:x")]
    [InlineData(null, "")]
    public void Canonical_FoldsLikedAndPassesEverythingElse(string? uri, string expected)
        => Assert.Equal(expected, LikedCoverRules.Canonical(uri));

    // -- The site ladder: what a cover of size s actually paints ------------------------------------------------------

    /// <summary><c>LikedCoverTreatments.BadgeMinSize</c>, restated because that file is engine-bound and cannot enter
    /// this assembly. It is INJECTED into <c>Site</c> for exactly that reason -- the composition owns the floor, the
    /// rules own the ladder -- so this constant is a test fixture, not a second definition.</summary>
    const float Floor = 140f;

    [Theory]
    // Stock is Stock at every size: it composes nothing, so there is nothing to collapse.
    [InlineData(LikedCoverStyle.Stock, 304f, 16, LikedCoverSite.Stock)]
    [InlineData(LikedCoverStyle.Stock, 20f, 16, LikedCoverSite.Stock)]
    // At or above the floor every art-backed style paints its full treatment.
    [InlineData(LikedCoverStyle.Wall, 304f, 16, LikedCoverSite.Treatment)]
    [InlineData(LikedCoverStyle.Wall, 140f, 8, LikedCoverSite.Treatment)]
    [InlineData(LikedCoverStyle.Lens, 172f, 8, LikedCoverSite.Treatment)]
    // The recents rail's own range straddles the floor: cardW 148..188 minus 2*Pad(8) is 132..172.
    [InlineData(LikedCoverStyle.Wall, 172f, 16, LikedCoverSite.Treatment)]
    [InlineData(LikedCoverStyle.Wall, 132f, 16, LikedCoverSite.MiniMosaic)]
    // Below the floor: the app's ordinary 2x2 collection mosaic ...
    [InlineData(LikedCoverStyle.Wall, 48f, 4, LikedCoverSite.MiniMosaic)]
    [InlineData(LikedCoverStyle.Rainbow, 38f, 16, LikedCoverSite.MiniMosaic)]
    // ... unless even four distinct covers are not there yet, which is Stock.
    [InlineData(LikedCoverStyle.Wall, 48f, 3, LikedCoverSite.Stock)]
    [InlineData(LikedCoverStyle.Wall, 48f, 0, LikedCoverSite.Stock)]
    // Tone carries no art at all and reads perfectly small, so it keeps its own gradient below the floor.
    [InlineData(LikedCoverStyle.Tone, 20f, 1, LikedCoverSite.MiniTone)]
    [InlineData(LikedCoverStyle.Tone, 20f, 0, LikedCoverSite.MiniTone)]
    [InlineData(LikedCoverStyle.Tone, 304f, 1, LikedCoverSite.Treatment)]
    public void Site_LaddersBySizeAndArtOnHand(LikedCoverStyle effective, float size, int tiles, LikedCoverSite expected)
        => Assert.Equal(expected, LikedCoverRules.Site(effective, size, tiles, Floor));

    /// <summary>An unmeasured slot (a responsive cell before its first bounds report) must take the FULL treatment
    /// rather than silently collapsing to a mosaic -- the comparison in <c>Site</c> is written so NaN falls
    /// through.</summary>
    [Fact]
    public void Site_NonFiniteSize_TakesTheTreatment()
        => Assert.Equal(LikedCoverSite.Treatment,
                        LikedCoverRules.Site(LikedCoverStyle.Wall, float.NaN, 16, Floor));

    /// <summary>Every style below its own <c>MinTiles</c> has already become Stock by the time <c>Site</c> is asked,
    /// so the two ladders compose: Effective decides WHETHER, Site decides WHAT.</summary>
    [Fact]
    public void Site_ComposesWithEffective_AtEverySize()
    {
        float[] sizes = [20f, 38f, 56f, 132f, 140f, 172f, 304f];
        foreach (var style in LikedCoverRules.PickerOrder)
            for (int tiles = 0; tiles <= LikedCoverRules.MaxTiles; tiles++)
            {
                var effective = LikedCoverRules.Effective(style, tiles);
                foreach (float size in sizes)
                {
                    var site = LikedCoverRules.Site(effective, size, tiles, Floor);
                    if (effective == LikedCoverStyle.Stock)
                        Assert.Equal(LikedCoverSite.Stock, site);
                    // A mosaic is only ever offered when there is enough art to fill its four cells.
                    if (site == LikedCoverSite.MiniMosaic)
                        Assert.True(tiles >= LikedCoverRules.MosaicCells, $"{style} @{size} with {tiles} tiles");
                    // A full treatment is only ever offered at or above the floor.
                    if (site == LikedCoverSite.Treatment)
                        Assert.True(size >= Floor, $"{style} @{size}");
                }
            }
    }

    // -- Aspect: what a NON-square slot does --------------------------------------------------------------------------

    /// <summary>Half a DIP of tolerance, because a responsive card width arrives as 149.9997 and must still read as
    /// square.</summary>
    [Theory]
    [InlineData(150f, 150f, true)]
    [InlineData(149.9997f, 150f, true)]
    [InlineData(150f, 149.6f, true)]
    [InlineData(56f, 32f, false)]
    [InlineData(300f, 260f, false)]
    public void IsSquare_ToleratesSubDipDrift(float w, float h, bool expected)
        => Assert.Equal(expected, LikedCoverRules.IsSquare(w, h));

    /// <summary>A letterbox slot COVER-fits the square treatment (compose at the longer edge, centre-crop) rather than
    /// letterboxing it inside bands: that is what <c>Surfaces.Artwork</c> already does to every other cover in a
    /// non-square frame, and the liked collection must not be the one card whose art does not fill its slot.</summary>
    [Theory]
    [InlineData(56f, 32f, 56f)]
    [InlineData(300f, 260f, 300f)]
    [InlineData(32f, 56f, 56f)]
    [InlineData(150f, 150f, 150f)]
    public void FitSide_TakesTheLongerEdge(float w, float h, float expected)
        => Assert.Equal(expected, LikedCoverRules.FitSide(w, h));
}
