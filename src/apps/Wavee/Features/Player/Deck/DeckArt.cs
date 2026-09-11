using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The parts every deck face draws the same way — cover art, discs, rings, bundled textures, and the two
/// cover-derived colours. It exists so twelve faces cannot each invent their own decode size, their own placeholder
/// or their own asset path, and so the "what is a circle here" answer is written once.
///
/// <para><b>Textures instead of gradients.</b> The renderer has no conic or repeating gradient, so record grooves,
/// wood grain, CD rainbow and marble ship as bundled alpha PNGs under <c>assets/deck/</c> and are TINTED at draw
/// time (<c>ImageEl.ColorOverlay</c>) rather than baked per theme. A missing file simply paints the placeholder,
/// which is why each face also carries a hairline-ring fallback.</para>
/// </summary>
static class DeckArt
{
    /// <summary>Degrees to radians — the faces' single most-used constant (<c>Affine2D.Rotation</c> takes radians,
    /// every deck angle is authored in degrees).</summary>
    public const float Deg2Rad = MathF.PI / 180f;

    /// <summary>Where the bundled deck textures live, next to the exe (<c>assets/**</c> is copied by Wavee.csproj).</summary>
    public const string AssetFolder = "deck";

    // Resolved absolute paths, memoized: a face rebuild (an option flip) must not re-Combine strings, and the
    // engine's image cache keys on the path so a stable string is also a stable cache key. UI thread only.
    static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal);

    /// <summary>The now-playing cover as a decoded square. <paramref name="size"/> is the LAYOUT size in DIP and
    /// also drives the decode, so a 60-DIP label is not decoded at 512.</summary>
    public static Element Cover(string? url, float size, CornerRadius4 corners, ColorF placeholder, string? blurHash)
        => new ImageEl
        {
            Source = url ?? "",
            Width = size,
            Height = size,
            Fit = ImageFit.Cover,
            DecodePx = DecodeFor(size),
            Corners = corners,
            Placeholder = placeholder,
            BlurHash = blurHash,
        };

    /// <summary>A filled disc of diameter <paramref name="d"/>.</summary>
    public static Element Circle(float d, ColorF fill) => new BoxEl
    {
        Width = d,
        Height = d,
        Shrink = 0f,
        Corners = Radii.Circle(d),
        Fill = fill,
    };

    /// <summary>A hollow ring: outer diameter <paramref name="d"/>, stroke <paramref name="w"/>. Hollow SDF, not a
    /// donut of two filled circles — so it composites over whatever is underneath it.</summary>
    public static Element Ring(float d, float w, ColorF c) => new BoxEl
    {
        Width = d,
        Height = d,
        Shrink = 0f,
        Corners = Radii.Circle(d),
        BorderWidth = w,
        BorderColor = c,
    };

    /// <summary>A bundled deck texture (grooves, wood grain, CD rainbow, marble, reel flange, Winamp title bar),
    /// tinted. Pass a transparent <paramref name="tint"/> to draw the PNG's own pixels.</summary>
    public static Element Texture(string assetFile, float w, float h, CornerRadius4 corners, ColorF tint) => new ImageEl
    {
        Source = AssetPath(assetFile),
        Width = w,
        Height = h,
        Fit = ImageFit.Cover,
        Corners = corners,
        ColorOverlay = tint,
        // Transparent, not the ImageEl default grey: a texture that has not decoded yet must show what is UNDER it
        // (the face's own fallback rings / fill), never a flat plate.
        Placeholder = ColorF.Transparent,
    };

    /// <summary>The absolute path of a bundled deck asset (memoized).</summary>
    public static string AssetPath(string assetFile)
    {
        if (Paths.TryGetValue(assetFile, out string? p)) return p;
        p = Path.Combine(AppContext.BaseDirectory, "assets", AssetFolder, assetFile);
        Paths[assetFile] = p;
        return p;
    }

    /// <summary>The track's cover url, normalized exactly as the hero tile normalizes it (so both hit the same
    /// decode + the same colour-plane entry). Null when the track carries no image.</summary>
    public static string? CoverUrl(Track track)
        => track.Image?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) : null;

    /// <summary>The hero's cover WASH as a bound channel — the tinted card ground a deck sits on, and the
    /// placeholder every cover slot shows while its art decodes. Bound, not a value: the grading lands
    /// asynchronously and must paint without re-rendering the face.</summary>
    public static Prop<ColorF> CoverWash(string? url) => Prop.Of(() => NowPlayingPanel.HeroWashColor(url));

    /// <summary>The cover's ACCENT as a bound channel — the album-colour vinyl, the Zune stripe, the WMP bars.
    /// Falls back to <paramref name="fallback"/> for greyscale art or before the grading lands.</summary>
    public static Prop<ColorF> CoverAccent(string? url, ColorF fallback) => Prop.Of(() =>
    {
        // Watch INSIDE the thunk: this subscribes the bind, not the face's render.
        if (url is { Length: > 0 }) _ = SpotifyLive.CoverColorPlane.Current.Watch(url).Value;
        var scheme = Surfaces.SchemeFor(url);
        return scheme is { } s ? WaveePalette.Accent(s) : fallback;
    });

    // Decode one step above the layout size (retina + the disc's own scale-up), capped where the source stops
    // helping. Matches the hero tile's 512-for-324 ratio.
    static float DecodeFor(float size)
    {
        float px = size * 1.5f;
        if (px <= 64f) return 64f;
        if (px <= 128f) return 128f;
        if (px <= 256f) return 256f;
        return 512f;
    }
}
