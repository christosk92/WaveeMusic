using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.WinUI.Converters;
using Wavee.UI.WinUI.ViewModels.Local;

namespace Wavee.UI.WinUI.Controls.Local;

/// <summary>
/// Visual shell for the Netflix-style "Up Next" card on
/// <see cref="Views.VideoPlayerPage"/>. Hosts the poster, S/E label,
/// title, countdown ring, "Watch now" button, and dismiss × — all wired
/// through to <see cref="UpNextEpisodeOverlayViewModel"/>.
///
/// <para>Visibility itself is driven by the VM's
/// <see cref="UpNextEpisodeOverlayViewModel.IsVisible"/> property; this
/// control listens to that change to start the slide-in / fade-out
/// storyboards rather than letting XAML toggle <c>Visibility</c>
/// instantly. Cleaner motion, same end-state.</para>
/// </summary>
public sealed partial class UpNextEpisodeOverlay : UserControl
{
    private static readonly SpotifyImageConverter ImageConverter = new();

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(UpNextEpisodeOverlayViewModel),
            typeof(UpNextEpisodeOverlay),
            new PropertyMetadata(null, OnViewModelChanged));

    public UpNextEpisodeOverlayViewModel? ViewModel
    {
        get => (UpNextEpisodeOverlayViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private Storyboard? _slideInStoryboard;
    private Storyboard? _fadeOutStoryboard;
    private bool _animationsCurrentlyVisible;

    public UpNextEpisodeOverlay()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UpNextEpisodeOverlay self) return;
        if (e.OldValue is UpNextEpisodeOverlayViewModel oldVm)
            oldVm.PropertyChanged -= self.OnViewModelPropertyChanged;
        if (e.NewValue is UpNextEpisodeOverlayViewModel newVm)
        {
            newVm.PropertyChanged += self.OnViewModelPropertyChanged;
            self.SyncFromViewModel(newVm);
        }
        else
        {
            self.Root.Visibility = Visibility.Collapsed;
            self._animationsCurrentlyVisible = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        switch (e.PropertyName)
        {
            case nameof(UpNextEpisodeOverlayViewModel.IsVisible):
                AnimateVisibility(vm.IsVisible);
                break;
            case nameof(UpNextEpisodeOverlayViewModel.NextEpisodePosterUri):
                ApplyPoster(vm.NextEpisodePosterUri);
                break;
        }
    }

    private void SyncFromViewModel(UpNextEpisodeOverlayViewModel vm)
    {
        ApplyPoster(vm.NextEpisodePosterUri);
        // Snap to current state without animating — we may be re-binding
        // to a VM that's already mid-flow.
        if (vm.IsVisible)
        {
            Root.Opacity = 1;
            Root.Visibility = Visibility.Visible;
            RootTranslate.X = 0;
            _animationsCurrentlyVisible = true;
        }
        else
        {
            Root.Visibility = Visibility.Collapsed;
            _animationsCurrentlyVisible = false;
        }
    }

    private void ApplyPoster(string? posterUri)
    {
        if (string.IsNullOrEmpty(posterUri))
        {
            PosterBrush.ImageSource = null;
            return;
        }
        try
        {
            var src = ImageConverter.Convert(posterUri, typeof(ImageSource), "480", string.Empty) as ImageSource;
            PosterBrush.ImageSource = src;
        }
        catch
        {
            PosterBrush.ImageSource = null;
        }
    }

    private void AnimateVisibility(bool visible)
    {
        if (visible == _animationsCurrentlyVisible && Root.Visibility == (visible ? Visibility.Visible : Visibility.Collapsed))
            return;
        _animationsCurrentlyVisible = visible;

        _slideInStoryboard?.Stop();
        _fadeOutStoryboard?.Stop();

        if (visible)
        {
            Root.Visibility = Visibility.Visible;
            RootTranslate.X = 80;
            Root.Opacity = 0;

            var sb = new Storyboard();

            var slide = new DoubleAnimation
            {
                From = 80,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(slide, RootTranslate);
            Storyboard.SetTargetProperty(slide, "X");
            sb.Children.Add(slide);

            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, Root);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);

            _slideInStoryboard = sb;
            sb.Begin();
        }
        else
        {
            var sb = new Storyboard();
            var fade = new DoubleAnimation
            {
                From = Root.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(150),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, Root);
            Storyboard.SetTargetProperty(fade, "Opacity");
            sb.Children.Add(fade);
            sb.Completed += (_, __) =>
            {
                if (!_animationsCurrentlyVisible)
                    Root.Visibility = Visibility.Collapsed;
            };
            _fadeOutStoryboard = sb;
            sb.Begin();
        }
    }

    private void WatchNowButton_Click(object sender, RoutedEventArgs e) => ViewModel?.WatchNow();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => ViewModel?.Cancel();

    private void CardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
        => ViewModel?.NotifyPointerEnteredCard();

    private void CardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
        => ViewModel?.NotifyPointerExitedCard();
}
