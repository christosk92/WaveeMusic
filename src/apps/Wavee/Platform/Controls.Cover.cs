// ── Platform/Controls.Cover.cs — the 2×2 half of the cover tokens (profile pages plan, D2) ────────────────────────────
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Controls
{
    const int MosaicCacheSize = 256;
    static readonly MosaicEntry?[] s_mosaics = new MosaicEntry?[MosaicCacheSize];
    sealed class MosaicEntry(string source, string[] tiles) { public readonly string Source = source; public readonly string[] Tiles = tiles; }

    /// <summary>The FOUR tile urls of a <c>spotify:mosaic:</c> cover, for <see cref="Mosaic"/>; empty for any other image
    /// (and for a 1-3-tile mosaic — Mosaic's own rule: paint <see cref="ArtUrl"/>, the lead tile, as one cover). Each
    /// tile goes through <see cref="CoverToken.MosaicTileId"/> (the official client composes 300-px album tiles).
    /// Cached direct-mapped on the id like ArtUrl, so a re-render allocates nothing.</summary>
    public static ReadOnlySpan<string> MosaicTiles(StringId image)
    {
        if (image.IsEmpty) return default;
        string id = Entities.Strings.Resolve(image);
        if (!CoverToken.IsMosaic(id)) return default;
        int slot = image.Value & (MosaicCacheSize - 1);
        var hit = s_mosaics[slot];
        if (hit is not null && ReferenceEquals(hit.Source, id)) return hit.Tiles;
        Span<Range> ranges = stackalloc Range[4];
        if (CoverToken.MosaicTiles(id, ranges) < 4) return default;
        Span<char> scratch = stackalloc char[CoverToken.ImageIdLength];
        var tiles = new string[4];
        for (int i = 0; i < 4; i++)
            tiles[i] = string.Concat(CdnPrefix.AsSpan(), CoverToken.MosaicTileId(id.AsSpan()[ranges[i]], scratch));
        s_mosaics[slot] = new MosaicEntry(id, tiles);
        return tiles;
    }
}
