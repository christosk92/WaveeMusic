// ── Themes/Zune/Structure/Rules/ZuneLocalCollectionRules.cs — the numbers of Zune's "On this PC" collection page (#240 P2, board 21) ─────
//
// Data, not decisions: `ZuneLocalCollection.cs` reads them. Board: #240 board 21 "The collection in Zune" (720 x 900: a Small window): the
// full-bleed cover mosaic behind the head (115 tiles, half strength, fading into the ground), "collection" in the display voice, the song
// count in display numbers over the rest of the stats, then shelves of big covers with lowercase captions. The band draws the pivots and
// the page's verbs (row 2), so the page draws neither.

using Wavee.Theming.Contracts;
using Wavee.Themes.Common;

namespace Wavee.Themes.Zune;

/// <summary>Node keys and fixed geometry of Zune's collection page.</summary>
internal static class ZuneLocalCollectionRules
{
    public const string PageKey = "zlc-page", HeadKey = "zlc-head", ColumnKey = "zlc-column";

    /// <summary>The head: its top inset under the band, the gaps between the title, the count, the stats and the status line, its foot.</summary>
    /// <summary>The mosaic under the head (board 21): the covers at half strength under a veil of the shell ground, 35 % at the top, 62 % at the middle,
    /// the ground itself at the foot.</summary>
    public const float MosaicStrength = 0.5f, VeilTop = 0.35f, VeilMid = 0.62f;
    /// <summary>The mosaic's tile (board 21: 115), one size at every tier, so a crossing never changes the head's height through it.</summary>
    public const float MosaicTile = 115f;

    public const float HeadTop = 4f, CountGap = 4f, StatsGap = 4f, StatusGap = 6f, HeadBottom = 8f, CountWordGap = 14f, StatusDot = 8f;
    /// <summary>"edit home" at the state line's end: its hit height (the Compact target) and its gap from the state words.</summary>
    public const float VerbH = 40f, VerbGap = 18f;
}

/// <summary>Zune's records of the shared body (`CommonLocalCollectionLayout`): bigger covers than WinUI's at every tier (board 21: 140 at Small).</summary>
internal static class ZuneLocalCollectionTiers
{
    static CommonLocalCollectionLayout Std(WidthTier t) => new()
    {
        Tier = t, Card = 180f, CardGap = 16f, WallMin = 180f, WallGap = 16f, ArtistMin = 150f, DecadeMin = 240f,
        FileColumn = true, ActionsBeside = true, ChipH = CommonLocalCollectionRules.ChipH,
    };

    static readonly CommonLocalCollectionLayout[] s_records =
    [
        Std(WidthTier.Compact) with { Card = 140f, CardGap = 16f, WallMin = 140f, WallGap = 12f, ArtistMin = 104f, DecadeMin = 150f, FileColumn = false, ActionsBeside = false, ChipH = CommonTiers.CompactTarget },
        Std(WidthTier.Small) with { Card = 140f, CardGap = 16f, WallMin = 150f, ArtistMin = 120f, FileColumn = false },   // board 21
        Std(WidthTier.Standard),
        Std(WidthTier.Huge) with { Card = 220f, CardGap = 24f, WallMin = 220f, WallGap = 24f, ArtistMin = 180f, DecadeMin = 200f },
    ];

    public static CommonLocalCollectionLayout For(WidthTier content) => s_records[Math.Clamp((int)content, 0, s_records.Length - 1)];
}
