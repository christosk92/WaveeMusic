using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;

namespace Wavee.UI.WinUI.Controls.Track;

/// <summary>
/// Compact track cell for grid layouts (Apple Music style).
/// Shows album art + title + subtitle, with shimmer state when loading.
/// Reusable across artist top tracks, search results, etc.
/// </summary>
public sealed partial class TrackCell : UserControl
{
    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(nameof(Track), typeof(ITrackItem), typeof(TrackCell),
            new PropertyMetadata(null, OnTrackChanged));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(TrackCell),
            new PropertyMetadata(false, OnIsLoadingChanged));

    public ITrackItem? Track
    {
        get => (ITrackItem?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public TrackCell()
    {
        InitializeComponent();
        PointerEntered += (s, e) => VisualStateManager.GoToState(this, "PointerOver", true);
        PointerExited += (s, e) => VisualStateManager.GoToState(this, "Normal", true);
    }

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cell = (TrackCell)d;
        var track = e.NewValue as ITrackItem;

        // Forward to TrackBehavior so double-tap play works
        TrackBehavior.SetTrack(cell, track);

        if (track != null)
        {
            cell.TitleText.Text = track.Title ?? "";
            cell.SubtitleText.Text = track.ArtistName ?? "";
            cell.DurationText.Text = track.DurationFormatted ?? "";
            cell.ExplicitBadge.Visibility = track.IsExplicit ? Visibility.Visible : Visibility.Collapsed;

            // Load album art
            var imageUrl = track.ImageUrl;
            if (!string.IsNullOrEmpty(imageUrl))
            {
                var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
                if (!string.IsNullOrEmpty(httpsUrl))
                    cell.AlbumArt.Source = new BitmapImage(new Uri(httpsUrl));
            }
            else
            {
                cell.AlbumArt.Source = null;
            }
        }
        else
        {
            cell.TitleText.Text = "";
            cell.SubtitleText.Text = "";
            cell.DurationText.Text = "";
            cell.AlbumArt.Source = null;
        }
    }

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cell = (TrackCell)d;
        var loading = (bool)e.NewValue;

        // Toggle between shimmer and content
        cell.AlbumArt.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        cell.ArtShimmer.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        cell.InfoPanel.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        cell.InfoShimmer.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        cell.DurationText.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
    }
}
