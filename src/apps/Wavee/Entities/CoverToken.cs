// ── Entities/CoverToken.cs — the cover TOKENS a wire may hand over instead of a url (profile pages plan, D2) ──────────
//
// Role: CORE (pure). The one place that turns `spotify:image:<id>` and `spotify:mosaic:<id>:<id>:…` into what an image
// column holds and what a surface paints. The profile view's public playlists carry both (research §5): a cover-less
// playlist is a MOSAIC token and the CDN never serves one — the client composes a 2×2 of 300-px album tiles itself.

namespace Wavee;

/// <summary>The cover TOKENS a wire may hand over instead of a url, and the one place that turns them into what an image
/// column holds and a surface paints: <c>spotify:image:&lt;file id&gt;</c> and <c>spotify:mosaic:&lt;id&gt;:&lt;id&gt;:…</c> (a
/// cover-less playlist — the client composes a 2×2 of 300-px tiles; the CDN never serves one). PURE, allocation-free,
/// thread-safe: the decoder calls the UTF-8 half on an api thread, Controls the char half on the UI thread.
/// <para>Decision: a mosaic is STORED VERBATIM in the playlist's Image column; <see cref="Controls.ArtUrl"/> paints its
/// lead tile wherever one cover is drawn and <see cref="Controls.MosaicTiles"/> hands a 2×2 all four.</para></summary>
public static class CoverToken
{
    public const string ImagePrefix = "spotify:image:", MosaicPrefix = "spotify:mosaic:";
    /// <summary>Room for <c>https://i.scdn.co/image/</c> + any image id a token carries.</summary>
    public const int MaxImageUrl = 128;
    /// <summary>The length of a Spotify image id: 40 hex characters, the first 16 naming family and size.</summary>
    public const int ImageIdLength = 40;

    // The album family's 16-char prefix at 300 px (research §5): `ab67616d0000b273` is 640, `…00001e02` 300, `…00004851` 64.
    const string AlbumFamily = "ab67616d", Album300 = "ab67616d00001e02";

    public static bool IsImageToken(ReadOnlySpan<byte> wire) => wire.StartsWith("spotify:image:"u8);

    /// <summary>A <c>spotify:image:&lt;id&gt;</c> token → its CDN url (UTF-8) in <paramref name="into"/>; 0 when not a token,
    /// empty, or it does not fit.</summary>
    public static int ImageTokenUrl(ReadOnlySpan<byte> wire, Span<byte> into)
    {
        ReadOnlySpan<byte> prefix = "spotify:image:"u8, cdn = "https://i.scdn.co/image/"u8;
        if (!wire.StartsWith(prefix)) return 0;
        var id = wire[prefix.Length..];
        if (id.IsEmpty || cdn.Length + id.Length > into.Length) return 0;
        cdn.CopyTo(into);
        id.CopyTo(into[cdn.Length..]);
        return cdn.Length + id.Length;
    }

    /// <summary>The ONE file a value paints as a single cover: a <c>spotify:image:</c> token's id, a mosaic's LEAD tile, a
    /// bare file id itself; empty for any other <c>spotify:</c> token. A slice of the input.</summary>
    public static ReadOnlySpan<char> FileIdOf(ReadOnlySpan<char> value)
    {
        if (value.StartsWith(ImagePrefix, StringComparison.Ordinal)) return value[ImagePrefix.Length..];
        if (value.StartsWith(MosaicPrefix, StringComparison.Ordinal))
        {
            var rest = value[MosaicPrefix.Length..];
            int colon = rest.IndexOf(':');
            return colon < 0 ? rest : rest[..colon];
        }
        return value.StartsWith("spotify:", StringComparison.Ordinal) ? default : value;
    }

    public static bool IsMosaic(ReadOnlySpan<char> value) => value.StartsWith(MosaicPrefix, StringComparison.Ordinal);

    /// <summary>A mosaic token's tile ids as ranges into <paramref name="value"/>, wire order, empty segments skipped, at
    /// most <c>into.Length</c>.</summary>
    public static int MosaicTiles(ReadOnlySpan<char> value, Span<Range> into)
    {
        if (!IsMosaic(value)) return 0;
        int n = 0, start = MosaicPrefix.Length;
        while (start < value.Length && n < into.Length)
        {
            int rel = value[start..].IndexOf(':');
            int end = rel < 0 ? value.Length : start + rel;
            if (end > start) into[n++] = new Range(start, end);
            if (rel < 0) break;
            start = end + 1;
        }
        return n;
    }

    /// <summary>The id a 2×2 mosaic paints for ONE of its parts. The official client composes 300-px album tiles
    /// (research §5), so a part that is a 40-hex <c>ab67616d…</c> album image id — whatever size it names — becomes the
    /// 300-px tile (<c>ab67616d00001e02</c> + the id's last 24 characters); anything else (an artist or user image, a
    /// part of another length, a non-hex id) passes through unchanged. PURE and allocation-free: the answer is
    /// <paramref name="part"/> itself or a slice of <paramref name="scratch"/> (which must hold
    /// <see cref="ImageIdLength"/> characters, else the part passes through).</summary>
    public static ReadOnlySpan<char> MosaicTileId(ReadOnlySpan<char> part, Span<char> scratch)
    {
        if (part.Length != ImageIdLength || scratch.Length < ImageIdLength
            || !part.StartsWith(AlbumFamily, StringComparison.Ordinal)) return part;
        for (int i = 0; i < part.Length; i++)
            if (!char.IsAsciiHexDigit(part[i])) return part;
        if (part.StartsWith(Album300, StringComparison.Ordinal)) return part;           // already the 300-px tile
        Album300.CopyTo(scratch);
        part[Album300.Length..].CopyTo(scratch[Album300.Length..]);
        return scratch[..ImageIdLength];
    }
}
