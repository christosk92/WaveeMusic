using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Wavee.UI.WinUI.Data.Contracts;

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
    [ObservableProperty] private string? _userLocationName;

    public bool HasOffers => Offers.Count > 0;
    public bool HasRelatedConcerts => RelatedConcerts.Count > 0;
    public bool HasGenres => Genres.Count > 0;

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
            }

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

            // User location
            UserLocationName = _locationService.CurrentCity;

            OnPropertyChanged(nameof(HasOffers));
            OnPropertyChanged(nameof(HasRelatedConcerts));
            OnPropertyChanged(nameof(HasGenres));
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
