using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Converters;
using Wavee.UI.WinUI.ViewModels.Local;

namespace Wavee.UI.WinUI.Views.Local;

public sealed partial class LocalPersonDetailPage : Page
{
    public LocalPersonDetailViewModel ViewModel { get; }

    private static readonly SpotifyImageConverter ImageConverter = new();

    public LocalPersonDetailPage()
    {
        ViewModel = Ioc.Default.GetService<LocalPersonDetailViewModel>() ?? new LocalPersonDetailViewModel();
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.ShowsInLibrary.CollectionChanged += (_, _) => SyncShelves();
        ViewModel.MoviesInLibrary.CollectionChanged += (_, _) => SyncShelves();
    }

    /// <summary>
    /// Navigation parameter is either an <see cref="int"/> (TMDB person id) or a
    /// <see cref="LocalPersonNavigationParameter"/> carrying a seed name + image
    /// so the hero renders something while the TMDB fetch resolves.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        switch (e.Parameter)
        {
            case LocalPersonNavigationParameter p:
                ViewModel.Prefill(p.SeedName, p.SeedImageUri);
                ApplySeedHero(p.SeedName, p.SeedImageUri);
                await ViewModel.LoadAsync(p.PersonId);
                break;
            case int personId:
                await ViewModel.LoadAsync(personId);
                break;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.Person))
        {
            SyncPersonHero();
            SyncShelves();
        }
        else if (e.PropertyName == nameof(ViewModel.SeedName) || e.PropertyName == nameof(ViewModel.SeedImageUri))
        {
            ApplySeedHero(ViewModel.SeedName, ViewModel.SeedImageUri);
        }
    }

    private void ApplySeedHero(string? name, string? imageUri)
    {
        // Render the seed values BEFORE the TMDB fetch resolves so the user
        // never sees a blank hero on navigate. Person-detail rebinds replace
        // these values once Person is populated.
        if (HeroNameText is not null && string.IsNullOrEmpty(HeroNameText.Text))
            HeroNameText.Text = name ?? string.Empty;
        if (HeroPortraitBrush is not null && HeroPortraitBrush.ImageSource is null
            && !string.IsNullOrEmpty(imageUri))
        {
            HeroPortraitBrush.ImageSource = ResolveImage(imageUri);
        }
    }

    private void SyncPersonHero()
    {
        if (ViewModel.Person is not { } person) return;

        HeroNameText.Text = person.Name;

        if (!string.IsNullOrWhiteSpace(person.ProfileImageUrl))
        {
            HeroPortraitBrush.ImageSource = new BitmapImage(new Uri(person.ProfileImageUrl));
        }
        else if (HeroPortraitBrush.ImageSource is null && !string.IsNullOrEmpty(ViewModel.SeedImageUri))
        {
            HeroPortraitBrush.ImageSource = ResolveImage(ViewModel.SeedImageUri);
        }

        // Meta line: known-for · birthday · place — skip empty segments.
        var parts = new System.Collections.Generic.List<string>(3);
        if (!string.IsNullOrWhiteSpace(person.KnownForDepartment)) parts.Add(person.KnownForDepartment!);
        if (!string.IsNullOrWhiteSpace(person.Birthday)) parts.Add(person.Birthday!);
        if (!string.IsNullOrWhiteSpace(person.PlaceOfBirth)) parts.Add(person.PlaceOfBirth!);
        HeroMetaText.Text = string.Join(" · ", parts);

        if (!string.IsNullOrWhiteSpace(person.Biography))
        {
            BiographyText.Text = person.Biography;
            BiographySection.Visibility = Visibility.Visible;
        }
        else
        {
            BiographySection.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncShelves()
    {
        ShowsSection.Visibility = ViewModel.ShowsInLibrary.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoviesSection.Visibility = ViewModel.MoviesInLibrary.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = (ViewModel.ShowsInLibrary.Count == 0 && ViewModel.MoviesInLibrary.Count == 0)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static ImageSource? ResolveImage(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        return ImageConverter.Convert(uri, typeof(ImageSource), "320", string.Empty) as ImageSource;
    }

    private void ShowCard_CardClick(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string showId)
            Frame.Navigate(typeof(LocalShowDetailPage), showId);
    }

    private void MovieCard_CardClick(object? sender, EventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string trackUri)
            Frame.Navigate(typeof(LocalMovieDetailPage), trackUri);
    }

    public static string FormatShowMeta(int seasonCount, int episodeCount)
        => $"{seasonCount} {(seasonCount == 1 ? "season" : "seasons")} · {episodeCount} {(episodeCount == 1 ? "episode" : "episodes")}";

    public static string FormatYear(int? year) =>
        year is { } y && y > 0 ? y.ToString() : string.Empty;
}

/// <summary>
/// Navigation parameter for <see cref="LocalPersonDetailPage"/>. Carries the
/// TMDB person id plus optional seed display values from the source cast row
/// so the hero renders something before the TMDB person fetch resolves.
/// </summary>
public sealed record LocalPersonNavigationParameter(
    int PersonId,
    string? SeedName,
    string? SeedImageUri);
