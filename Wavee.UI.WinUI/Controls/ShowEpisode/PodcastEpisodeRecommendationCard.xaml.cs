using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Controls.ShowEpisode;

public sealed partial class PodcastEpisodeRecommendationCard : UserControl
{
    public static readonly DependencyProperty RecommendationProperty =
        DependencyProperty.Register(
            nameof(Recommendation),
            typeof(PodcastEpisodeRecommendationDto),
            typeof(PodcastEpisodeRecommendationCard),
            new PropertyMetadata(null, OnRecommendationChanged));

    public PodcastEpisodeRecommendationDto? Recommendation
    {
        get => (PodcastEpisodeRecommendationDto?)GetValue(RecommendationProperty);
        set => SetValue(RecommendationProperty, value);
    }

    public PodcastEpisodeRecommendationCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnRecommendationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PodcastEpisodeRecommendationCard card)
            card.ApplyRecommendation(e.NewValue as PodcastEpisodeRecommendationDto);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyRecommendation(Recommendation);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CoverImage.Source = null;
    }

    private void ApplyRecommendation(PodcastEpisodeRecommendationDto? recommendation)
    {
        if (recommendation is null)
        {
            TitleText.Text = "";
            DurationText.Text = "";
            SubtitleText.Text = "";
            ApplyCover(null);
            return;
        }

        TitleText.Text = recommendation.Title;
        DurationText.Text = recommendation.DurationFormatted;
        SubtitleText.Text = string.IsNullOrWhiteSpace(recommendation.ShowAndDateText)
            ? recommendation.Subtitle
            : recommendation.ShowAndDateText;
        ApplyCover(recommendation.ImageUrl);
    }

    private void ApplyCover(string? imageUrl)
    {
        CoverImage.Source = null;
        CoverImage.Visibility = Visibility.Collapsed;
        CoverPlaceholderIcon.Visibility = Visibility.Visible;

        if (string.IsNullOrWhiteSpace(imageUrl))
            return;

        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            return;

        CoverImage.Visibility = Visibility.Visible;
        CoverPlaceholderIcon.Visibility = Visibility.Collapsed;
        CoverImage.Source = new BitmapImage(uri);
    }

    private void CardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        HoverFill.Opacity = 1;
        PlayButton.Opacity = 1;
        ActionCluster.Opacity = 1;
    }

    private void CardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HoverFill.Opacity = 0;
        PlayButton.Opacity = 0;
        ActionCluster.Opacity = 0;
    }

    private void CardRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (Recommendation is null) return;
        if (e.OriginalSource is FrameworkElement fe && IsInsidePlayButton(fe)) return;

        ConnectedAnimationHelper.PrepareAnimation(ConnectedAnimationHelper.PodcastEpisodeArt, CoverContainer);
        NavigationHelpers.OpenEpisode(
            Recommendation.Uri,
            Recommendation.Title,
            Recommendation.ImageUrl,
            showTitle: Recommendation.ShowName,
            openInNewTab: NavigationHelpers.IsCtrlPressed());
        e.Handled = true;
    }

    private bool IsInsidePlayButton(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, PlayButton)) return true;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (Recommendation is not null)
            NavigationHelpers.PlayEpisode(Recommendation.Uri);
    }
}
