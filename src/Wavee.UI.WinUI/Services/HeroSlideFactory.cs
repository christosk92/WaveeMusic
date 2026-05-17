using System;
using System.Windows.Input;
using Klankhuis.Hero.Controls;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Styles;
using Wavee.UI.WinUI.ViewModels;
using Windows.UI;
using Wavee.UI.Helpers;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// Constructs <see cref="HeroCarouselItem"/> slides from <see cref="HomeSectionItem"/>
/// data. Promoted out of <c>HomeHeroAdapter</c> so any VM that drives a
/// hero carousel (Home, Browse, future Search/Genre destinations) builds
/// slides through the same code — keeps Tag wiring, accent fallback, and
/// Spotify image URL resolution in one place.
/// </summary>
public static class HeroSlideFactory
{
    /// <summary>Klankhuis's default accent — used when an item has no
    /// extracted colour and no caller-supplied override.</summary>
    public static readonly Color FallbackAccent = Color.FromArgb(255, 0x60, 0xCD, 0xFF);

    /// <summary>
    /// Build one slide. Selection rules (which items to include, in what
    /// order) stay per-VM; this only handles the per-slide construction.
    /// </summary>
    /// <param name="overrideAccent">When non-null, used directly as the
    /// slide's <see cref="HeroCarouselItem.Accent"/> regardless of the
    /// item's own colour. Useful for browse pages that want every slide
    /// tinted with the page's header hex.</param>
    public static HeroCarouselItem BuildSlide(
        HomeSectionItem item,
        string eyebrow,
        string primaryCta,
        string secondaryCta,
        ICommand? primaryCommand,
        ICommand? secondaryCommand,
        Color? overrideAccent = null,
        bool useImageAsBackground = true)
    {
        var isRecentlySaved = item.IsRecentlySaved;
        return new HeroCarouselItem
        {
            Eyebrow = eyebrow,
            Title = item.Title ?? string.Empty,
            Tagline = item.Subtitle ?? string.Empty,
            TaglineIconGlyph = isRecentlySaved ? FluentGlyphs.CheckMark : string.Empty,
            TaglineIconColor = Color.FromArgb(255, 0x1E, 0xD7, 0x60),
            ImageUri = TryMakeImageUri(item.ImageUrl),
            Accent = overrideAccent ?? ParseAccentOrFallback(item.ColorHex),
            PrimaryCtaText = primaryCta,
            PrimaryCtaCommand = primaryCommand,
            PrimaryCtaCommandParameter = item,
            SecondaryCtaText = secondaryCta,
            SecondaryCtaCommand = secondaryCommand,
            SecondaryCtaCommandParameter = item,
            Tag = item,
            UseImageAsBackground = useImageAsBackground
        };
    }

    /// <summary>
    /// Parse a hex colour with the Klankhuis fallback when missing or invalid.
    /// Public so side-rail / sub-page tile builders can reuse the same logic.
    /// </summary>
    public static Color ParseAccentOrFallback(string? hex)
    {
        if (TintColorHelper.TryParseHex(hex, out var c))
            return c;
        return FallbackAccent;
    }

    /// <summary>
    /// Resolve a Spotify image identifier (raw <c>https://</c> CDN URL OR
    /// <c>spotify:image:...</c> / <c>spotify:mosaic:...</c>) to an absolute
    /// HTTPS <see cref="Uri"/> that <c>LoadedImageSurface</c> can fetch.
    /// Returns null when the input has no resolvable form.
    /// </summary>
    public static Uri? TryMakeImageUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // The Klankhuis SideCard / HeroCarousel use LoadedImageSurface, which
        // only fetches http(s). spotify:image:* parses as a valid Uri but
        // the surface fetch silently fails — route through SpotifyImageHelper
        // for the same conversion the rest of the app uses.
        var resolved = SpotifyImageHelper.ToHttpsUrl(raw);
        if (string.IsNullOrEmpty(resolved)) return null;
        return Uri.TryCreate(resolved, UriKind.Absolute, out var uri) ? uri : null;
    }
}
