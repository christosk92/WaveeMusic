using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class ConcertViewModel : ObservableObject
{
    private readonly IConcertService _concertService;
    private readonly ILocationService _locationService;
    private readonly ILogger? _logger;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string? _errorMessage;

    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _dateFormatted;
    [ObservableProperty] private string? _dayOfWeek;
    [ObservableProperty] private string? _dayOfWeekUpper;
    [ObservableProperty] private string? _dateMonth;
    [ObservableProperty] private string? _dateDay;
    [ObservableProperty] private string? _dateYear;
    [ObservableProperty] private string? _artistNamesJoined;
    [ObservableProperty] private string? _venue;
    [ObservableProperty] private string? _city;
    [ObservableProperty] private string? _country;
    [ObservableProperty] private string? _headerImageUrl;
    [ObservableProperty] private string? _showTime;
    [ObservableProperty] private string? _fullLocation;
    [ObservableProperty] private bool _isFestival;
    [ObservableProperty] private bool _isSaved;

    [ObservableProperty] private ObservableCollection<ConcertArtistVm> _artists = [];
    [ObservableProperty] private ObservableCollection<ConcertOfferVm> _offers = [];
    [ObservableProperty] private ObservableCollection<ConcertRelatedVm> _relatedConcerts = [];
    [ObservableProperty] private ObservableCollection<string> _genres = [];
    [ObservableProperty] private ObservableCollection<ConcertFeaturedPlaylistVm> _featuredPlaylists = [];
    [ObservableProperty] private string? _userLocationName;

    // Multi-artist hero: Store-style "1 feature + up to 3 supporting" mosaic.
    // Null slots collapse their tile; the overall panel hides when there's only 1 artist.
    [ObservableProperty] private ConcertArtistVm? _supportingArtist1;
    [ObservableProperty] private ConcertArtistVm? _supportingArtist2;
    [ObservableProperty] private ConcertArtistVm? _supportingArtist3;
    [ObservableProperty] private bool _hasSupportingArtists;
    [ObservableProperty] private bool _hasOneSupporting;
    [ObservableProperty] private bool _hasTwoSupporting;
    [ObservableProperty] private bool _hasThreeSupporting;

    // ── Theme-aware palette (from the headliner artist) ──
    // Raw palette from the API (null when not provided). A cached flag lets us refresh
    // the brushes when the host reports a theme change (see ApplyTheme).
    private ConcertArtistPalette? _headlinerPalette;
    private bool _isDarkTheme;

    /// <summary>Subtle page-wash brush tinted toward the headliner's color. Transparent if no palette.</summary>
    [ObservableProperty] private Brush? _paletteBackdropBrush;
    /// <summary>Gradient brush used on the StoreHero feature tile's dark-to-transparent overlay,
    /// tinted by the palette. Falls back to a pure black gradient if no palette.</summary>
    [ObservableProperty] private Brush? _paletteHeroGradientBrush;
    /// <summary>Accent pill bg (date pill in the StoreHero). Falls back to system accent.</summary>
    [ObservableProperty] private Brush? _paletteAccentPillBrush;
    /// <summary>Text color on the accent pill. White when palette is present.</summary>
    [ObservableProperty] private Brush? _paletteAccentPillForegroundBrush;

    public bool HasOffers => Offers.Count > 0;
    public bool HasRelatedConcerts => RelatedConcerts.Count > 0;
    public bool HasGenres => Genres.Count > 0;
    public bool HasFeaturedPlaylists => FeaturedPlaylists.Count > 0;

    public ConcertViewModel(IConcertService concertService, ILocationService locationService, ILogger<ConcertViewModel>? logger = null)
    {
        _concertService = concertService;
        _locationService = locationService;
        _logger = logger;
    }

    // ── Location (same pattern as ArtistViewModel) ──

    public async Task<List<LocationSearchResult>> SearchLocationsAsync(string query, CancellationToken ct = default)
        => await _locationService.SearchAsync(query, ct);

    public async Task SaveLocationAsync(string geonameId, string? cityName)
    {
        await _locationService.SaveByGeonameIdAsync(geonameId, cityName);
        UserLocationName = cityName ?? _locationService.CurrentCity;
    }

    public async Task<LocationSearchResult?> ResolveCurrentLocationAsync()
    {
        try
        {
            var geolocator = new Windows.Devices.Geolocation.Geolocator();
            var position = await geolocator.GetGeopositionAsync();
            return await _locationService.SearchByCoordinatesAsync(
                position.Coordinate.Point.Position.Latitude,
                position.Coordinate.Point.Position.Longitude);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to resolve current location");
            return null;
        }
    }

    // ── Theme-aware palette application ──

    /// <summary>
    /// Rebuilds palette brushes using the right tier for the current app theme:
    /// dark → HigherContrast (saturated deep), light → MinContrast (pastel).
    /// Called by the view on load and whenever ActualThemeChanged fires.
    /// Safe to call before the palette is available; brushes fall back to neutral defaults.
    /// </summary>
    public void ApplyTheme(bool isDark)
    {
        _isDarkTheme = isDark;

        // Pick a tier that's saturated-dark enough to let white text sit over an image:
        //   Dark theme  → HigherContrast (deepest, blends with the dark app chrome).
        //   Light theme → HighContrast   (still saturated, but a step brighter so the
        //                                  subtle page-wash isn't inky on a light app).
        // MinContrast (pastel) is intentionally avoided for overlays — not dark enough
        // for white-on-image readability.
        var tier = _headlinerPalette is null
            ? null
            : (isDark
                ? (_headlinerPalette.HigherContrast ?? _headlinerPalette.HighContrast)
                : (_headlinerPalette.HighContrast ?? _headlinerPalette.HigherContrast));

        if (tier == null)
        {
            // No palette from the API — leave brushes null so XAML falls back to ThemeResource defaults.
            PaletteBackdropBrush = null;
            PaletteHeroGradientBrush = null;
            PaletteAccentPillBrush = null;
            PaletteAccentPillForegroundBrush = null;
            return;
        }

        var bg = Color.FromArgb(255, tier.BackgroundR, tier.BackgroundG, tier.BackgroundB);
        var bgTint = Color.FromArgb(255, tier.BackgroundTintedR, tier.BackgroundTintedG, tier.BackgroundTintedB);
        var accent = Color.FromArgb(255, tier.TextAccentR, tier.TextAccentG, tier.TextAccentB);

        // Light mode: blend palette colors toward white before applying alpha so
        // a dark-cover headliner doesn't drag the page dark. Dark mode unchanged.
        var heroBg     = isDark ? bg     : TintColorHelper.LightTint(bg);
        var heroBgTint = isDark ? bgTint : TintColorHelper.LightTint(bgTint);
        var washColor  = isDark ? bg     : TintColorHelper.LightTint(bg);

        // Page-wide subtle wash — the palette bg at low alpha over the app bg.
        PaletteBackdropBrush = new SolidColorBrush(Color.FromArgb(
            (byte)(isDark ? 60 : 38), washColor.R, washColor.G, washColor.B));

        // Hero feature-tile overlay gradient: palette-tinted ink on the left → transparent.
        // Left uses the TintedBase (deeper) so text on top stays readable.
        var (a0, a1, a2, a3) = isDark ? (240, 176, 80, 0) : (140, 100, 50, 0);
        var heroGrad = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 0),
        };
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a0, heroBgTint.R, heroBgTint.G, heroBgTint.B), Offset = 0.0 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a1, heroBg.R,     heroBg.G,     heroBg.B),     Offset = 0.35 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a2, heroBg.R,     heroBg.G,     heroBg.B),     Offset = 0.65 });
        heroGrad.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)a3, heroBg.R,     heroBg.G,     heroBg.B),     Offset = 1.0 });
        PaletteHeroGradientBrush = heroGrad;

        // Accent pill inside the hero: palette's bright accent as fill, white text on it.
        PaletteAccentPillBrush = new SolidColorBrush(accent);
        var accentLuma = (accent.R * 299 + accent.G * 587 + accent.B * 114) / 1000;
        // Dark accent → white text; very bright accent → dark text. Threshold 160 avoids white-on-yellow.
        PaletteAccentPillForegroundBrush = new SolidColorBrush(
            accentLuma > 160 ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255));
    }

    [RelayCommand]
    private async Task LoadAsync(string? concertUri)
    {
        if (string.IsNullOrEmpty(concertUri) || IsLoading) return;
        IsLoading = true;
        HasError = false;

        try
        {
            var detail = await _concertService.GetDetailAsync(concertUri);

            Title = detail.Title;
            Venue = detail.Venue;
            City = detail.City;
            Country = detail.Country;
            IsFestival = detail.IsFestival;
            IsSaved = detail.IsSaved;
            ShowTime = detail.ShowTime;
            FullLocation = detail.FullLocation;

            if (detail.Date != default)
            {
                DateFormatted = detail.Date.ToString("MMM d, yyyy").ToUpperInvariant();
                DayOfWeek = detail.Date.ToString("dddd");
                DayOfWeekUpper = detail.Date.ToString("ddd").ToUpperInvariant();
                DateMonth = detail.Date.ToString("MMM").ToUpperInvariant();
                DateDay = detail.Date.Day.ToString();
                DateYear = detail.Date.Year.ToString();
            }

            ArtistNamesJoined = string.Join(", ", detail.Artists
                .Select(a => a.Name)
                .Where(n => !string.IsNullOrEmpty(n)));

            // Supporting artists feed the Store-style multi-artist hero mosaic.
            // Slots beyond the first 3 supporting artists fall to the Lineup section below.
            var supporting = Artists.Skip(1).Take(3).ToList();
            SupportingArtist1 = supporting.Count > 0 ? supporting[0] : null;
            SupportingArtist2 = supporting.Count > 1 ? supporting[1] : null;
            SupportingArtist3 = supporting.Count > 2 ? supporting[2] : null;
            HasSupportingArtists = supporting.Count > 0;
            HasOneSupporting = supporting.Count == 1;
            HasTwoSupporting = supporting.Count == 2;
            HasThreeSupporting = supporting.Count >= 3;

            // Adopt the headliner's palette (if any) and rebuild the theme-aware brushes.
            _headlinerPalette = detail.Artists.FirstOrDefault()?.Palette;
            ApplyTheme(_isDarkTheme);

            // Use first artist's header image as hero background
            HeaderImageUrl = detail.Artists.Count > 0 ? detail.Artists[0].HeaderImageUrl : null;

            Artists.Clear();
            foreach (var a in detail.Artists)
            {
                Artists.Add(new ConcertArtistVm
                {
                    Uri = a.Uri,
                    Name = a.Name,
                    AvatarUrl = a.AvatarUrl,
                    HeaderImageUrl = a.HeaderImageUrl,
                    UpcomingConcertCount = a.UpcomingConcertCount,
                    PopularAlbums = a.PopularAlbums.Select(pa => new ConcertAlbumVm
                    {
                        Name = pa.Name,
                        Uri = pa.Uri,
                        CoverArtUrl = pa.CoverArtUrl,
                        ArtistName = pa.ArtistName
                    }).ToList()
                });
            }

            Offers.Clear();
            foreach (var o in detail.Offers)
            {
                Offers.Add(new ConcertOfferVm
                {
                    ProviderName = o.ProviderName,
                    ProviderImageUrl = o.ProviderImageUrl,
                    Url = o.Url,
                    SaleType = o.SaleType
                });
            }

            RelatedConcerts.Clear();
            foreach (var r in detail.RelatedConcerts)
            {
                RelatedConcerts.Add(new ConcertRelatedVm
                {
                    Title = r.Title,
                    Uri = r.Uri,
                    City = r.City,
                    Venue = r.Venue,
                    ArtistName = r.ArtistName,
                    ArtistAvatarUrl = r.ArtistAvatarUrl,
                    DateFormatted = r.Date != default ? r.Date.ToString("MMM d").ToUpperInvariant() : ""
                });
            }

            Genres.Clear();
            foreach (var g in detail.Genres)
                Genres.Add(g);

            FeaturedPlaylists.Clear();
            foreach (var p in detail.FeaturedPlaylists)
            {
                FeaturedPlaylists.Add(new ConcertFeaturedPlaylistVm
                {
                    Uri = p.Uri,
                    Name = p.Name,
                    Description = p.Description,
                    ImageUrl = p.ImageUrl,
                    OwnerName = p.OwnerName,
                });
            }

            // User location
            UserLocationName = _locationService.CurrentCity;

            OnPropertyChanged(nameof(HasOffers));
            OnPropertyChanged(nameof(HasRelatedConcerts));
            OnPropertyChanged(nameof(HasGenres));
            OnPropertyChanged(nameof(HasFeaturedPlaylists));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load concert {Uri}", concertUri);
            HasError = true;
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public sealed class ConcertArtistVm
{
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? AvatarUrl { get; init; }
    public string? HeaderImageUrl { get; init; }
    public int UpcomingConcertCount { get; init; }
    public List<ConcertAlbumVm> PopularAlbums { get; init; } = [];
}

public sealed class ConcertAlbumVm
{
    public string? Name { get; init; }
    public string? Uri { get; init; }
    public string? CoverArtUrl { get; init; }
    public string? ArtistName { get; init; }
}

public sealed class ConcertOfferVm
{
    public string? ProviderName { get; init; }
    public string? ProviderImageUrl { get; init; }
    public string? Url { get; init; }
    public string? SaleType { get; init; }
}

public sealed class ConcertRelatedVm
{
    public string? Title { get; init; }
    public string? Uri { get; init; }
    public string? City { get; init; }
    public string? Venue { get; init; }
    public string? ArtistName { get; init; }
    public string? ArtistAvatarUrl { get; init; }
    public string? DateFormatted { get; init; }
}

public sealed class ConcertFeaturedPlaylistVm
{
    public string? Uri { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? OwnerName { get; init; }
}
