using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.RightPanel;

public sealed partial class RightPanelView : UserControl
{
    private const double MinPanelWidth = 200;
    private const double MaxPanelWidth = 500;

    private bool _draggingResizer;
    private double _preManipulationWidth;

    // Lyrics state
    private readonly LyricsViewModel _lyricsVm;

    public RightPanelView()
    {
        _lyricsVm = Ioc.Default.GetRequiredService<LyricsViewModel>();

        InitializeComponent();
        Visibility = Visibility.Collapsed;
        Width = PanelWidth;

        Loaded += RightPanelView_Loaded;
        Unloaded += RightPanelView_Unloaded;
    }

    private void RightPanelView_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateContentVisibility();

        // Wire up the Win2D lyrics canvas
        LyricsCanvas?.SetViewModel(_lyricsVm);

        _lyricsVm.PropertyChanged += OnLyricsVmPropertyChanged;

        // Apply initial background color if lyrics are already loaded
        UpdateGradientBackground(_lyricsVm.BackgroundColor);
    }

    private void RightPanelView_Unloaded(object sender, RoutedEventArgs e)
    {
        _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
        _lyricsVm.IsVisible = false;

        LyricsCanvas?.Dispose();
    }

    private void UpdateContentVisibility()
    {
        if (QueueContent == null) return;

        QueueContent.Visibility = SelectedMode == RightPanelMode.Queue ? Visibility.Visible : Visibility.Collapsed;
        LyricsContent.Visibility = SelectedMode == RightPanelMode.Lyrics ? Visibility.Visible : Visibility.Collapsed;
        FriendsContent.Visibility = SelectedMode == RightPanelMode.FriendsActivity ? Visibility.Visible : Visibility.Collapsed;

        QueueTab.IsChecked = SelectedMode == RightPanelMode.Queue;
        LyricsTab.IsChecked = SelectedMode == RightPanelMode.Lyrics;
        FriendsTab.IsChecked = SelectedMode == RightPanelMode.FriendsActivity;

        _lyricsVm.IsVisible = SelectedMode == RightPanelMode.Lyrics && IsOpen;

        // Pause/resume the Win2D canvas based on visibility
        var isLyricsVisible = SelectedMode == RightPanelMode.Lyrics && IsOpen;
        LyricsCanvas?.SetPaused(!isLyricsVisible || !_lyricsVm.HasLyrics);
    }

    // ── Gradient background ──

    private void OnLyricsVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.BackgroundColor):
                DispatcherQueue.TryEnqueue(() => UpdateGradientBackground(_lyricsVm.BackgroundColor));
                break;
            case nameof(LyricsViewModel.HasLyrics):
                DispatcherQueue.TryEnqueue(() =>
                {
                    var isLyricsVisible = SelectedMode == RightPanelMode.Lyrics && IsOpen;
                    LyricsCanvas?.SetPaused(!isLyricsVisible || !_lyricsVm.HasLyrics);
                });
                break;
        }
    }

    private void UpdateGradientBackground(Windows.UI.Color baseColor)
    {
        if (LyricsBackground == null || GradientTop == null || GradientBottom == null) return;

        var topColor = Windows.UI.Color.FromArgb(65, baseColor.R, baseColor.G, baseColor.B);
        var bottomColor = Windows.UI.Color.FromArgb(130,
            (byte)(baseColor.R / 2),
            (byte)(baseColor.G / 2),
            (byte)(baseColor.B / 2));

        var storyboard = new Storyboard();

        var topAnim = new ColorAnimation
        {
            To = topColor,
            Duration = TimeSpan.FromMilliseconds(600),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(topAnim, GradientTop);
        Storyboard.SetTargetProperty(topAnim, "Color");
        storyboard.Children.Add(topAnim);

        var bottomAnim = new ColorAnimation
        {
            To = bottomColor,
            Duration = TimeSpan.FromMilliseconds(600),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(bottomAnim, GradientBottom);
        Storyboard.SetTargetProperty(bottomAnim, "Color");
        storyboard.Children.Add(bottomAnim);

        storyboard.Begin();

        if (LyricsBackground.Opacity < 0.01)
        {
            var fadeIn = new Storyboard();
            var opacityAnim = new DoubleAnimation
            {
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(600),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(opacityAnim, LyricsBackground);
            Storyboard.SetTargetProperty(opacityAnim, "Opacity");
            fadeIn.Children.Add(opacityAnim);
            fadeIn.Begin();
        }
    }

    // ── Tab header clicks ──

    private void QueueTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.Queue;

    private void LyricsTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.Lyrics;

    private void FriendsTab_Click(object sender, RoutedEventArgs e)
        => SelectedMode = RightPanelMode.FriendsActivity;

    // ── Resize gripper ──

    private void Resizer_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        _draggingResizer = true;
        _preManipulationWidth = PanelWidth;
        VisualStateManager.GoToState(this, "ResizerPressed", true);
        e.Handled = true;
    }

    private void Resizer_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var newWidth = _preManipulationWidth - e.Cumulative.Translation.X;
        newWidth = System.Math.Clamp(newWidth, MinPanelWidth, MaxPanelWidth);
        PanelWidth = newWidth;
        e.Handled = true;
    }

    private void Resizer_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        _draggingResizer = false;
        VisualStateManager.GoToState(this, "ResizerNormal", true);
        e.Handled = true;
    }

    private void Resizer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var resizer = (FrameworkElement)sender;
        resizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
        VisualStateManager.GoToState(this, "ResizerPointerOver", true);
        e.Handled = true;
    }

    private void Resizer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingResizer) return;

        var resizer = (FrameworkElement)sender;
        resizer.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
        VisualStateManager.GoToState(this, "ResizerNormal", true);
        e.Handled = true;
    }

    private void Resizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        IsOpen = !IsOpen;
        e.Handled = true;
    }
}
