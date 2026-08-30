using System;
using System.Collections.Generic;

namespace Wavee.Core;

public enum ImageSourceQuality
{
    None = 0,
    Unresolved = 1,
    Usable = 2,
}

public static class ImageSource
{
    const string SpotifyImagePrefix = "spotify:image:";
    const string SpotifyImageCdnPrefix = "https://i.scdn.co/image/";

    public static string? Normalize(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith(SpotifyImagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = trimmed[SpotifyImagePrefix.Length..].Trim();
            return id.Length == 0 ? "" : SpotifyImageCdnPrefix + id;
        }

        return trimmed;
    }

    public static IReadOnlyList<string>? NormalizeAll(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return values;

        string[]? normalized = null;
        for (int i = 0; i < values.Count; i++)
        {
            var current = values[i];
            var next = Normalize(current) ?? "";
            if (normalized is not null) normalized[i] = next;
            else if (next != current)
            {
                normalized = new string[values.Count];
                for (int j = 0; j < i; j++) normalized[j] = values[j];
                normalized[i] = next;
            }
        }

        return normalized ?? values;
    }

    public static ImageSourceQuality Quality(Image? image)
    {
        if (image is null) return ImageSourceQuality.None;
        var url = Quality(image.Url);
        if (url == ImageSourceQuality.Usable) return url;
        var largest = Quality(image.LargestUrl);
        if (largest == ImageSourceQuality.Usable) return largest;

        var tiles = Quality(image.MosaicTiles);
        var best = largest > url ? largest : url;
        return tiles > best ? tiles : best;
    }

    public static bool IsUsable(Image? image) => Quality(image) == ImageSourceQuality.Usable;

    /// <summary>Pick the better of two artwork references for the SAME entity from two different sources.
    /// <see cref="Quality"/> (is there a resolvable URL at all?) decides first; a tie is then broken by known pixel
    /// area, and only then by <paramref name="primary"/>.
    ///
    /// <para>The area tie-break is what keeps a thin writer honest. A cluster/library echo upserts a track carrying a
    /// small 64px cover while the row already holds the resolved 640px one; both URLs are perfectly
    /// <see cref="ImageSourceQuality.Usable"/>, so resolvability alone would hand the win to whichever side happened to
    /// be <paramref name="primary"/> and let the thin write downgrade the art. Dimensions are optional, so this only
    /// engages when BOTH sides state them — an unknown size never outranks a known one, and equal (or unstated) sizes
    /// keep the historical prefer-primary behaviour.</para></summary>
    public static Image? ChooseBetter(Image? primary, Image? fallback)
    {
        if (primary is null) return fallback;
        if (fallback is null) return primary;
        var qp = Quality(primary);
        var qf = Quality(fallback);
        if (qp != qf) return qp > qf ? primary : fallback;
        long ap = Area(primary), af = Area(fallback);
        if (ap != af && ap > 0 && af > 0) return ap > af ? primary : fallback;
        return primary;
    }

    /// <summary>The stated pixel area, or 0 when either dimension is unknown or non-positive ("I don't know" — never a
    /// claim of smallness, which is why <see cref="ChooseBetter"/> requires both sides to be positive).</summary>
    static long Area(Image image) =>
        image.Width is int w && w > 0 && image.Height is int h && h > 0 ? (long)w * h : 0L;

    // ── Spotify image ids ───────────────────────────────────────────────────────────────────────────────────────
    // A Spotify image id is 40 hex chars whose FIRST 16 are a size/kind marker and whose last 24 identify the ARTWORK:
    //   ab67616d00004851<hash>  64px album      ab67616d00001e02<hash>  300px      ab67616d0000b273<hash>  640px
    //   ab6761610000f178<hash>  artist          ab67706c00006c11<hash>  playlist
    // (verified across 349 kind-179 payloads; CoverColorPlane keys its colour cache on the same tail). So two urls of the
    // same cover at different sizes are the SAME ART — a card payload's 300px hash and the detail payload's 640px hash —
    // and anything that latches "the cover already on screen" must compare this identity, not the url: comparing urls
    // is exactly what made the detail hero re-decode and fade the same picture back in when the full model landed.
    const int SpotifyImageIdLength = 40;
    const int SpotifyImageSizePrefixLength = 16;
    const string SpotifyImageUriPrefix = "spotify:image:";
    const string SpotifyImageCdnSegment = "/image/";

    /// <summary>The full image id of a cover reference — the segment after <c>/image/</c> of a CDN url, or the token
    /// after <c>spotify:image:</c> — as a SLICE of the input (no allocation; safe on a render path). Any other url shape
    /// yields its last path segment; empty in ⇒ empty out.</summary>
    public static ReadOnlySpan<char> ImageIdSpan(ReadOnlySpan<char> url)
    {
        url = url.Trim();
        if (url.IsEmpty) return default;
        int q = url.IndexOf('?');
        if (q >= 0) url = url[..q];
        if (url.StartsWith(SpotifyImageUriPrefix, StringComparison.OrdinalIgnoreCase))
            return url[SpotifyImageUriPrefix.Length..].Trim();
        int img = url.LastIndexOf(SpotifyImageCdnSegment, StringComparison.OrdinalIgnoreCase);
        if (img >= 0) return url[(img + SpotifyImageCdnSegment.Length)..];
        int slash = url.LastIndexOf('/');
        return slash >= 0 && slash + 1 < url.Length ? url[(slash + 1)..] : url;
    }

    /// <summary>The size-independent ARTWORK identity of an image id: the 24-char tail of a 40-char Spotify id; ids of
    /// any other shape key on themselves.</summary>
    public static ReadOnlySpan<char> ArtIdentityOf(ReadOnlySpan<char> imageId)
        => imageId.Length == SpotifyImageIdLength ? imageId[SpotifyImageSizePrefixLength..] : imageId;

    /// <summary>The artwork identity of a cover url (see <see cref="ArtIdentityOf"/>): every pre-sized rendition of one
    /// cover shares it. Empty for a null/blank url.</summary>
    public static ReadOnlySpan<char> ArtIdentity(string? url) => ArtIdentityOf(ImageIdSpan(url));

    /// <summary>The identity url <see cref="SameArt"/> compares: the image's own <see cref="Image.Url"/> when it has
    /// one, else its first mosaic tile — the same reduction <c>Surfaces.Artwork</c> applies before painting a 1-3-tile
    /// mosaic as a single cover (<c>image = new Image(tiles[0])</c>). Without this a cover-less playlist (Url == "",
    /// tiles only) can never match a single-cover rendition of the SAME first tile — e.g. a nav-preview card that
    /// already reduced the mosaic to its lead tile vs. the full model's still-mosaic Image — and the latch always lost.</summary>
    static string ReducedUrl(Image? image)
    {
        if (image is null) return "";
        if (Normalize(image.Url) is { Length: > 0 } u) return u;
        return image.MosaicTiles is { Count: > 0 } tiles ? Normalize(tiles[0]) ?? "" : "";
    }

    /// <summary>True when both images show the SAME ARTWORK — <see cref="SameSource"/>, or two Spotify image ids that
    /// differ only in their size marker (a card-size and a hero-size rendition of one cover), comparing the REDUCED
    /// identity (<see cref="ReducedUrl"/>) so a mosaic and a single-tile rendition of its own lead tile also match.
    /// Null/empty pairs are not the same art. This is the identity a "keep what is already on screen" latch must use.</summary>
    public static bool SameArt(Image? a, Image? b)
    {
        if (a is null || b is null) return false;
        if (SameSource(a, b)) return true;
        var ua = ReducedUrl(a);
        var ub = ReducedUrl(b);
        if (ua.Length == 0 || ub.Length == 0) return false;
        var ia = ImageIdSpan(ua);
        var ib = ImageIdSpan(ub);
        // Only a full 40-char id carries a size prefix; anything else was already settled by SameSource above.
        if (ia.Length != SpotifyImageIdLength || ib.Length != SpotifyImageIdLength) return false;
        return ArtIdentityOf(ia).Equals(ArtIdentityOf(ib), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when both images resolve to the same cover identity (normalized URL, or matching mosaic tiles
    /// when neither has a URL). Null/empty pairs are not "the same source". Size-agnostic identity is
    /// <see cref="SameArt"/>.</summary>
    public static bool SameSource(Image? a, Image? b)
    {
        if (a is null || b is null) return false;
        var ua = Normalize(a.Url) ?? "";
        var ub = Normalize(b.Url) ?? "";
        if (ua.Length > 0 && ub.Length > 0)
            return string.Equals(ua, ub, StringComparison.OrdinalIgnoreCase);
        if (ua.Length > 0 || ub.Length > 0) return false;
        return MosaicEquals(a.MosaicTiles, b.MosaicTiles);
    }

    /// <summary>The cover-handoff latch: <paramref name="visible"/> is the cover already on screen, <paramref name="incoming"/>
    /// the one a fresher payload just brought.
    /// <list type="bullet">
    /// <item>SAME ART (any size — <see cref="SameArt"/>): keep <paramref name="visible"/> so the hero never re-decodes and
    /// re-fades the picture it is already showing (a new url is a new <c>ImageCache</c> key ⇒ Pending ⇒ placeholder ⇒
    /// crossfade); merge the richer metadata (blurhash, dimensions, the largest rendition) onto it.</item>
    /// <item>DIFFERENT ART: <paramref name="incoming"/> wins — a playlist whose cover was just edited, or a daylist that
    /// rolled over, must show its new cover.</item>
    /// <item>Only one side usable: that side.</item>
    /// </list>
    /// Pure and order-safe, so it can run at every point a model is published (initial load AND live refresh).</summary>
    public static Image? PreferVisible(Image? incoming, Image? visible)
    {
        bool inOk = IsUsable(incoming);
        bool visOk = IsUsable(visible);
        if (!inOk) return visOk ? visible : incoming ?? visible;
        if (!visOk) return incoming;
        return SameArt(incoming, visible) ? EnrichVisible(visible!, incoming!) : incoming;
    }

    /// <summary>Resolve the normal or largest known source for a rendering context.</summary>
    public static string? UrlFor(Image? image, bool preferLargest)
    {
        if (image is null) return null;
        string? value = preferLargest && !string.IsNullOrWhiteSpace(image.LargestUrl)
            ? image.LargestUrl
            : image.Url;
        return Normalize(value);
    }

    public static ImageSourceQuality Quality(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return ImageSourceQuality.None;
        return IsUnresolvedProviderToken(source.Trim()) ? ImageSourceQuality.Unresolved : ImageSourceQuality.Usable;
    }

    static Image EnrichVisible(Image visible, Image incoming)
    {
        bool needBlur = string.IsNullOrEmpty(visible.BlurHash) && !string.IsNullOrEmpty(incoming.BlurHash);
        bool needW = visible.Width is null && incoming.Width is not null;
        bool needH = visible.Height is null && incoming.Height is not null;
        string? incomingLargest = incoming.LargestUrl ?? incoming.Url;
        bool needLargest = string.IsNullOrEmpty(visible.LargestUrl) && !string.IsNullOrEmpty(incomingLargest);
        if (!needBlur && !needW && !needH && !needLargest) return visible;
        return visible with
        {
            BlurHash = needBlur ? incoming.BlurHash : visible.BlurHash,
            Width = needW ? incoming.Width : visible.Width,
            Height = needH ? incoming.Height : visible.Height,
            LargestUrl = needLargest ? incomingLargest : visible.LargestUrl,
        };
    }

    static bool MosaicEquals(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var ua = Normalize(a[i]) ?? "";
            var ub = Normalize(b[i]) ?? "";
            if (!string.Equals(ua, ub, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return a.Count > 0;
    }

    static ImageSourceQuality Quality(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0) return ImageSourceQuality.None;
        var best = ImageSourceQuality.None;
        for (int i = 0; i < values.Count && best != ImageSourceQuality.Usable; i++)
        {
            var q = Quality(values[i]);
            if (q > best) best = q;
        }

        return best;
    }

    static bool IsUnresolvedProviderToken(string source)
        => source.StartsWith(SpotifyImagePrefix, StringComparison.OrdinalIgnoreCase);
}
