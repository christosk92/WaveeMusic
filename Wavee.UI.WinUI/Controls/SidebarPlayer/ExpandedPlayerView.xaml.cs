using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.ViewModels;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.SidebarPlayer;

/// <summary>
/// Floating-player "now playing" expanded layout — Apple-Music iPad style.
/// 2-column layout: <see cref="ExpandedNowPlayingLayout"/> on the left,
/// <see cref="Controls.RightPanel.RightPanelView"/> on the right with
/// <see cref="Controls.RightPanel.RightPanelView.IsTabHeaderVisible"/> off.
/// Right column has 3 states (None / Lyrics / Queue) toggled by the
/// bottom-right buttons; mode flips drive the inner panel's
/// <see cref="Controls.RightPanel.RightPanelView.SelectedMode"/>.
///
/// An atmospheric backdrop bleeds the album-art palette across the window
/// so the layout doesn't sit on bare Mica.
/// </summary>
public sealed partial class ExpandedPlayerView : UserControl
{
    private readonly PlayerBarViewModel _viewModel;

    public ExpandedPlayerView()
    {
        _viewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += OnActualThemeChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>Right-column content state. Default: <see cref="ExpandedPlayerContentMode.Lyrics"/>.</summary>
    public ExpandedPlayerContentMode Mode
    {
        get => (ExpandedPlayerContentMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode),
        typeof(ExpandedPlayerContentMode),
        typeof(ExpandedPlayerView),
        new PropertyMetadata(ExpandedPlayerContentMode.Lyrics, OnModeChanged));

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ExpandedPlayerView view) view.ApplyMode();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ContentHost.MaxWidth = double.PositiveInfinity;
        ApplyMode();
        SyncContentHostWidth();
        ApplyAmbientTint(_viewModel.AlbumArtColor);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ActualThemeChanged -= OnActualThemeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        => SyncContentHostWidth();

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => ApplyAmbientTint(_viewModel.AlbumArtColor);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.AlbumArtColor))
            ApplyAmbientTint(_viewModel.AlbumArtColor);
    }

    /// <summary>
    /// Drives <see cref="Controls.RightPanel.RightPanelView.PanelWidth"/> from
    /// the right column's actual width — the panel hard-sets its own
    /// <c>Width</c> from <c>PanelWidth</c>, so we feed the live value as the
    /// window resizes.
    /// </summary>
    private void SyncContentHostWidth()
    {
        if (Mode == ExpandedPlayerContentMode.None) return;
        var w = RightColumnContainer.ActualWidth;
        if (w <= 0) return;
        if (Math.Abs(ContentHost.PanelWidth - w) > 0.5)
            ContentHost.PanelWidth = w;
    }

    private void ApplyMode()
    {
        var mode = Mode;
        var rightVisible = mode != ExpandedPlayerContentMode.None;

        // Right column collapses to 0 width when both panes are off so the
        // player layout grows naturally to fill the wider left column.
        RightColumnDef.Width = rightVisible
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        RightColumnContainer.Visibility = rightVisible ? Visibility.Visible : Visibility.Collapsed;

        if (rightVisible)
        {
            ContentHost.SelectedMode = mode == ExpandedPlayerContentMode.Queue
                ? RightPanelMode.Queue
                : RightPanelMode.Lyrics;
            SyncContentHostWidth();
        }

        LyricsToggleButton.IsChecked = mode == ExpandedPlayerContentMode.Lyrics;
        QueueToggleButton.IsChecked = mode == ExpandedPlayerContentMode.Queue;
    }

    private void LyricsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        Mode = Mode == ExpandedPlayerContentMode.Lyrics
            ? ExpandedPlayerContentMode.None
            : ExpandedPlayerContentMode.Lyrics;
    }

    private void QueueToggleButton_Click(object sender, RoutedEventArgs e)
    {
        Mode = Mode == ExpandedPlayerContentMode.Queue
            ? ExpandedPlayerContentMode.None
            : ExpandedPlayerContentMode.Queue;
    }

    // ── Ambient backdrop tint ────────────────────────────────────────────
    //
    // Builds a vertical gradient from the album-art dominant color, fading
    // to transparent so Mica reads through near the title bar and bottom.
    // Light mode uses a softer, blended tint so foreground text stays
    // readable; dark mode keeps a saturated upper band for atmosphere.

    private void ApplyAmbientTint(string? hexColor)
    {
        if (string.IsNullOrEmpty(hexColor) || hexColor!.TrimStart('#').Length != 6)
        {
            var transparent = Color.FromArgb(0, 0, 0, 0);
            AmbientStopTop.Color = transparent;
            AmbientStopUpperMid.Color = transparent;
            AmbientStopLowerMid.Color = transparent;
            AmbientStopBottom.Color = transparent;
            return;
        }

        try
        {
            var hex = hexColor.TrimStart('#');
            var r = Convert.ToByte(hex[..2], 16);
            var g = Convert.ToByte(hex[2..4], 16);
            var b = Convert.ToByte(hex[4..6], 16);

            byte tr = r, tg = g, tb = b;
            byte topAlpha;
            byte midAlpha;
            byte lowerMidAlpha;

            if (ActualTheme == ElementTheme.Dark)
            {
                // Dark: saturated palette in upper half, fades to ~transparent at bottom.
                topAlpha = 150;
                midAlpha = 110;
                lowerMidAlpha = 50;
            }
            else
            {
                // Light: pre-blend toward white so text stays legible.
                const float blend = 0.55f;
                tr = (byte)(r * (1 - blend) + 255 * blend);
                tg = (byte)(g * (1 - blend) + 255 * blend);
                tb = (byte)(b * (1 - blend) + 255 * blend);
                topAlpha = 220;
                midAlpha = 170;
                lowerMidAlpha = 80;
            }

            AmbientStopTop.Color = Color.FromArgb(topAlpha, tr, tg, tb);
            AmbientStopUpperMid.Color = Color.FromArgb(midAlpha, tr, tg, tb);
            AmbientStopLowerMid.Color = Color.FromArgb(lowerMidAlpha, tr, tg, tb);
            AmbientStopBottom.Color = Color.FromArgb(0, tr, tg, tb);
        }
        catch
        {
            // Keep prior tint on parse failure.
        }
    }
}
