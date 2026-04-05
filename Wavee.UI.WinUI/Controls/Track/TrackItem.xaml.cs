using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Track;

/// <summary>
/// Display mode for TrackItem.
/// </summary>
public enum TrackItemDisplayMode
{
    /// <summary>Compact card for grids (artist top tracks, search results). 56px height.</summary>
    Compact,
    /// <summary>Table row for list views (playlists, albums, liked songs). Multi-column.</summary>
    Row
}

/// <summary>
/// Unified track display control with consistent behavior across all contexts.
/// Supports Compact mode (grid cells) and Row mode (table lists).
/// Handles hover play button, now-playing indicator, click-to-play, and context menu.
/// </summary>
public sealed partial class TrackItem : UserControl
{
    #region Dependency Properties

    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(nameof(Track), typeof(ITrackItem), typeof(TrackItem),
            new PropertyMetadata(null, OnTrackChanged));

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(TrackItemDisplayMode), typeof(TrackItem),
            new PropertyMetadata(TrackItemDisplayMode.Compact, OnModeChanged));

    public static readonly DependencyProperty PlayCommandProperty =
        DependencyProperty.Register(nameof(PlayCommand), typeof(ICommand), typeof(TrackItem),
            new PropertyMetadata(null));

    public static readonly DependencyProperty AddToQueueCommandProperty =
        DependencyProperty.Register(nameof(AddToQueueCommand), typeof(ICommand), typeof(TrackItem),
            new PropertyMetadata(null));

    public static readonly DependencyProperty RemoveCommandProperty =
        DependencyProperty.Register(nameof(RemoveCommand), typeof(ICommand), typeof(TrackItem),
            new PropertyMetadata(null));

    public static readonly DependencyProperty RemoveCommandLabelProperty =
        DependencyProperty.Register(nameof(RemoveCommandLabel), typeof(string), typeof(TrackItem),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnIsLoadingChanged));

    public static readonly DependencyProperty RowIndexProperty =
        DependencyProperty.Register(nameof(RowIndex), typeof(int), typeof(TrackItem),
            new PropertyMetadata(0, OnRowIndexChanged));

    public static readonly DependencyProperty ShowAlbumArtProperty =
        DependencyProperty.Register(nameof(ShowAlbumArt), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(true, OnColumnVisibilityChanged));

    public static readonly DependencyProperty ShowArtistColumnProperty =
        DependencyProperty.Register(nameof(ShowArtistColumn), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(true, OnColumnVisibilityChanged));

    public static readonly DependencyProperty ShowAlbumColumnProperty =
        DependencyProperty.Register(nameof(ShowAlbumColumn), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(true, OnColumnVisibilityChanged));

    public static readonly DependencyProperty ShowDateAddedProperty =
        DependencyProperty.Register(nameof(ShowDateAdded), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnColumnVisibilityChanged));

    public static readonly DependencyProperty DateAddedTextProperty =
        DependencyProperty.Register(nameof(DateAddedText), typeof(string), typeof(TrackItem),
            new PropertyMetadata(null, OnDateAddedTextChanged));

    public static readonly DependencyProperty IsCompactRowProperty =
        DependencyProperty.Register(nameof(IsCompactRow), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnIsCompactRowChanged));

    public ITrackItem? Track
    {
        get => (ITrackItem?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public TrackItemDisplayMode Mode
    {
        get => (TrackItemDisplayMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public ICommand? PlayCommand
    {
        get => (ICommand?)GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }

    public ICommand? AddToQueueCommand
    {
        get => (ICommand?)GetValue(AddToQueueCommandProperty);
        set => SetValue(AddToQueueCommandProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => (ICommand?)GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    public string? RemoveCommandLabel
    {
        get => (string?)GetValue(RemoveCommandLabelProperty);
        set => SetValue(RemoveCommandLabelProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public int RowIndex
    {
        get => (int)GetValue(RowIndexProperty);
        set => SetValue(RowIndexProperty, value);
    }

    public bool ShowAlbumArt
    {
        get => (bool)GetValue(ShowAlbumArtProperty);
        set => SetValue(ShowAlbumArtProperty, value);
    }

    public bool ShowArtistColumn
    {
        get => (bool)GetValue(ShowArtistColumnProperty);
        set => SetValue(ShowArtistColumnProperty, value);
    }

    public bool ShowAlbumColumn
    {
        get => (bool)GetValue(ShowAlbumColumnProperty);
        set => SetValue(ShowAlbumColumnProperty, value);
    }

    public bool ShowDateAdded
    {
        get => (bool)GetValue(ShowDateAddedProperty);
        set => SetValue(ShowDateAddedProperty, value);
    }

    public string? DateAddedText
    {
        get => (string?)GetValue(DateAddedTextProperty);
        set => SetValue(DateAddedTextProperty, value);
    }

    public bool IsCompactRow
    {
        get => (bool)GetValue(IsCompactRowProperty);
        set => SetValue(IsCompactRowProperty, value);
    }

    #endregion

    #region Events

    public event EventHandler<string>? ArtistClicked;
    public event EventHandler<string>? AlbumClicked;

    #endregion

    #region Internal State

    private bool _isHovered;
    private bool _isAlternateRow;
    private readonly ThemeColorService? _themeColors = Ioc.Default.GetService<ThemeColorService>();
    private readonly Data.Contracts.ITrackLikeService? _likeService = Ioc.Default.GetService<Data.Contracts.ITrackLikeService>();
    private readonly Microsoft.Extensions.Logging.ILogger? _logger = Ioc.Default.GetService<Microsoft.Extensions.Logging.ILogger<TrackItem>>();
    private readonly IPlaybackStateService? _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
    private static ISettingsService? _cachedSettingsService;
    private bool _isThisTrackPlaying;
    private bool _isThisTrackPaused;
    private bool _isBuffering;

    #endregion

    public TrackItem()
    {
        InitializeComponent();

        // Hover tracking
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;

        // Ensure playback bridge is initialized (idempotent)
        TrackStateBehavior.EnsurePlaybackSubscription();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Heart button click handling
        CompactHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnHeartClicked);
        RowHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnHeartClicked);

        // Play button clicks (both modes)
        CompactPlayButton.Click += OnPlayButtonClick;
        RowPlayButton.Click += OnPlayButtonClick;

        // Tap-to-play (respects TrackClickBehavior setting)
        Tapped += OnTapped;
        DoubleTapped += OnDoubleTapped;

        // Context menu
        RightTapped += OnRightTapped;
        Holding += OnHolding;

        // Row mode navigation links
        RowArtistLink.Click += OnArtistLinkClick;
        RowAlbumLink.Click += OnAlbumLinkClick;
    }

    #region Mode Switching

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item.ApplyMode();
        item.BindTrackData();
        item.UpdateOverlayState();
    }

    private void ApplyMode()
    {
        if (Mode == TrackItemDisplayMode.Compact)
        {
            CompactBorder.Visibility = Visibility.Visible;
            RowRoot.Visibility = Visibility.Collapsed;
        }
        else
        {
            CompactBorder.Visibility = Visibility.Collapsed;
            RowRoot.Visibility = Visibility.Visible;
            ApplyRowColumnVisibility();
        }
    }

    #endregion

    #region Track Changed

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item._isHovered = false;
        item.BindTrackData();
        item.RefreshPlaybackState();
        item.UpdateOverlayState();
    }

    private void BindTrackData()
    {
        var track = Track;

        if (Mode == TrackItemDisplayMode.Compact)
            BindCompactData(track);
        else
            BindRowData(track);
    }

    private void BindCompactData(ITrackItem? track)
    {
        if (track != null)
        {
            CompactTitle.Text = track.Title ?? "";
            CompactSubtitle.Text = track.ArtistName ?? "";
            CompactDuration.Text = track.DurationFormatted ?? "";
            CompactExplicit.Visibility = track.IsExplicit ? Visibility.Visible : Visibility.Collapsed;

            // Video indicator: "PlayCount · [icon] Music Video"
            var hasVideo = track.HasVideo;
            CompactVideoSeparator.Visibility = hasVideo && !string.IsNullOrEmpty(track.ArtistName) ? Visibility.Visible : Visibility.Collapsed;
            CompactVideoIcon.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
            CompactVideoLabel.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
            CompactHeartButton.IsLiked = _likeService?.IsSaved(Data.Contracts.SavedItemType.Track, track.Id) ?? track.IsLiked;
            CompactHeartButton.Visibility = Visibility.Visible;

            var imageUrl = track.ImageUrl;
            if (!string.IsNullOrEmpty(imageUrl))
            {
                var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
                if (!string.IsNullOrEmpty(httpsUrl))
                {
                    var cache = Ioc.Default.GetService<ImageCacheService>();
                    CompactAlbumArt.Source = cache?.GetOrCreate(httpsUrl, 48);
                }
                else
                {
                    CompactAlbumArt.Source = null;
                }
            }
            else
            {
                CompactAlbumArt.Source = null;
            }
        }
        else
        {
            CompactTitle.Text = "";
            CompactSubtitle.Text = "";
            CompactDuration.Text = "";
            CompactExplicit.Visibility = Visibility.Collapsed;
            CompactVideoSeparator.Visibility = Visibility.Collapsed;
            CompactVideoIcon.Visibility = Visibility.Collapsed;
            CompactVideoLabel.Visibility = Visibility.Collapsed;
            CompactHeartButton.Visibility = Visibility.Collapsed;
            CompactAlbumArt.Source = null;
        }
    }

    private void BindRowData(ITrackItem? track)
    {
        if (track != null)
        {
            RowTitle.Text = track.Title ?? "";
            RowExplicit.Visibility = track.IsExplicit ? Visibility.Visible : Visibility.Collapsed;
            RowDuration.Text = track.DurationFormatted ?? "";

            RowHeartButton.IsLiked = _likeService?.IsSaved(Data.Contracts.SavedItemType.Track, track.Id) ?? track.IsLiked;
            RowHeartButton.Visibility = Visibility.Visible;
            RowArtistLink.Content = track.ArtistName ?? "";
            RowArtistLink.Tag = track.ArtistId;
            RowAlbumLink.Content = track.AlbumName ?? "";
            RowAlbumLink.Tag = track.AlbumId;

            // Album art
            var imageUrl = track.ImageUrl;
            if (!string.IsNullOrEmpty(imageUrl))
            {
                var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
                if (!string.IsNullOrEmpty(httpsUrl))
                {
                    var cache = Ioc.Default.GetService<ImageCacheService>();
                    RowAlbumArt.Source = cache?.GetOrCreate(httpsUrl, 48);
                    RowAlbumArt.Visibility = Visibility.Visible;
                    RowArtPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    RowAlbumArt.Source = null;
                    RowAlbumArt.Visibility = Visibility.Collapsed;
                    RowArtPlaceholder.Visibility = Visibility.Visible;
                }
            }
            else
            {
                RowAlbumArt.Source = null;
                RowAlbumArt.Visibility = Visibility.Collapsed;
                RowArtPlaceholder.Visibility = Visibility.Visible;
            }

            // Row index
            RowIndexText.Text = (track.OriginalIndex > 0)
                ? track.OriginalIndex.ToString()
                : RowIndex > 0 ? RowIndex.ToString() : "";
        }
        else
        {
            RowTitle.Text = "";
            RowExplicit.Visibility = Visibility.Collapsed;
            RowDuration.Text = "";
            RowArtistLink.Content = "";
            RowAlbumLink.Content = "";
            RowAlbumArt.Source = null;
            RowAlbumArt.Visibility = Visibility.Collapsed;
            RowArtPlaceholder.Visibility = Visibility.Visible;
        }
    }

    // Expose RowIndex TextBlock for external access (used by the internal name)
    // RowIndexText is the x:Name from XAML - no alias needed

    #endregion

    #region Row Properties Changed

    private static void OnRowIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode == TrackItemDisplayMode.Row)
        {
            var track = item.Track;
            var idx = (int)e.NewValue;
            item.RowIndexText.Text = (track?.OriginalIndex > 0)
                ? track.OriginalIndex.ToString()
                : idx > 0 ? idx.ToString() : "";
        }
    }

    private static void OnColumnVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode == TrackItemDisplayMode.Row)
            item.ApplyRowColumnVisibility();
    }

    private static void OnDateAddedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item.RowDateAdded.Text = (string?)e.NewValue ?? "";
    }

    private static void OnIsCompactRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode == TrackItemDisplayMode.Row)
        {
            var compact = (bool)e.NewValue;
            item.RowRoot.Padding = compact ? new Thickness(4, 4, 4, 4) : new Thickness(8, 8, 8, 8);
            item.RowIndexColDef.Width = compact ? new GridLength(30) : new GridLength(40);
        }
    }

    /// <summary>
    /// Applies alternating row styling: border + tinted background on odd rows.
    /// </summary>
    private static readonly Brush DefaultBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public void SetAlternatingBorder(bool isAlternate)
    {
        _isAlternateRow = isAlternate;
        if (isAlternate)
        {
            RowRoot.BorderBrush = _themeColors?.GetBrush("CardStrokeColorDefaultBrush")
                ?? (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            RowRoot.BorderThickness = new Thickness(1);
            RowRoot.Background = _themeColors?.CardBackground
                ?? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        }
        else
        {
            RowRoot.BorderBrush = null;
            RowRoot.BorderThickness = new Thickness(0);
            RowRoot.Background = DefaultBackground;
        }
    }

    private const int RowDurationColumnIndex = 7;
    private readonly List<ColumnDefinition> _customColDefs = [];
    private readonly List<UIElement> _customColElements = [];

    /// <summary>
    /// Populates custom column values (e.g. Plays) by inserting TextBlocks into the row grid.
    /// Called from TrackListView.ContainerContentChanging with pre-computed values.
    /// </summary>
    public void SetCustomColumnValues(string[] values, IList<TrackList.TrackListColumnDefinition> columns)
    {
        // Clear previous custom columns
        foreach (var el in _customColElements)
            RowContentGrid.Children.Remove(el);
        _customColElements.Clear();

        foreach (var cd in _customColDefs)
            RowContentGrid.ColumnDefinitions.Remove(cd);
        _customColDefs.Clear();

        // Reset duration column to base position
        Grid.SetColumn(RowDuration, RowDurationColumnIndex);

        for (int i = 0; i < values.Length; i++)
        {
            // Insert column definition before Duration
            var colDef = new ColumnDefinition { Width = columns[i].Width };
            RowContentGrid.ColumnDefinitions.Insert(RowDurationColumnIndex + i, colDef);
            _customColDefs.Add(colDef);

            var tb = new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = values[i],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = _themeColors?.TextSecondary ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = columns[i].TextAlignment,
            };
            Grid.SetColumn(tb, RowDurationColumnIndex + i);
            RowContentGrid.Children.Add(tb);
            _customColElements.Add(tb);
        }

        // Shift duration column right
        Grid.SetColumn(RowDuration, RowDurationColumnIndex + values.Length);
    }

    private void ApplyRowColumnVisibility()
    {
        RowArtColDef.Width = ShowAlbumArt ? new GridLength(42) : new GridLength(0);
        RowArtistColDef.Width = ShowArtistColumn ? new GridLength(180) : new GridLength(0);
        RowAlbumColDef.Width = ShowAlbumColumn ? new GridLength(180) : new GridLength(0);
        RowDateColDef.Width = ShowDateAdded ? new GridLength(120) : new GridLength(0);
    }

    #endregion

    #region Loading State

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        var loading = (bool)e.NewValue;

        if (item.Mode == TrackItemDisplayMode.Compact)
        {
            item.CompactAlbumArt.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            item.CompactArtShimmer.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            item.CompactInfoPanel.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            item.CompactInfoShimmer.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            item.CompactDuration.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            item.RowContentGrid.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            item.RowShimmerOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    #endregion

    #region Hover

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;

        if (Mode == TrackItemDisplayMode.Compact)
        {
            CompactBorder.Background = _themeColors?.CardBackground
                ?? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            CompactBorder.BorderBrush = _themeColors?.GetBrush("CardStrokeColorDefaultBrush")
                ?? (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            CompactBorder.BorderThickness = new Thickness(1);
        }
        else
        {
            RowRoot.Background = _themeColors?.GetBrush("SubtleFillColorTertiaryBrush")
                ?? (Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"];
        }

        UpdateOverlayState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;

        if (Mode == TrackItemDisplayMode.Compact)
        {
            CompactBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            CompactBorder.BorderBrush = null;
            CompactBorder.BorderThickness = new Thickness(0);
        }
        else
        {
            RowRoot.Background = _isAlternateRow
                ? (_themeColors?.CardBackground ?? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"])
                : DefaultBackground;
        }

        UpdateOverlayState();
    }

    #endregion

    #region Playback State

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshPlaybackState();
        UpdateOverlayState();
        RefreshLikedState();

        // Subscribe to global state changes for reactive updates
        TrackStateBehavior.PlaybackStateChanged += OnPlaybackStateChanged;
        if (_likeService != null)
            _likeService.SaveStateChanged += OnSaveStateChanged;
    }

    private void OnPlaybackStateChanged()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            RefreshPlaybackState();
            UpdateOverlayState();
        });
    }

    private void OnSaveStateChanged()
    {
        DispatcherQueue?.TryEnqueue(RefreshLikedState);
    }

    /// <summary>
    /// Refresh heart button state from the in-memory cache.
    /// </summary>
    public void RefreshLikedState()
    {
        var track = Track;
        if (track == null || _likeService == null) return;

        var isLiked = _likeService.IsSaved(Data.Contracts.SavedItemType.Track, track.Id);
        CompactHeartButton.IsLiked = isLiked;
        RowHeartButton.IsLiked = isLiked;
        track.IsLiked = isLiked;
    }

    /// <summary>
    /// Refresh playback state from TrackStateBehavior. Can be called externally
    /// by TrackListView for optimized per-row updates.
    /// </summary>
    public void RefreshPlaybackState()
    {
        var track = Track;
        if (track == null)
        {
            _isThisTrackPlaying = false;
            _isThisTrackPaused = false;
            _isBuffering = false;
            return;
        }

        var isThisTrack = track.Id == TrackStateBehavior.CurrentTrackId;
        _isThisTrackPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        _isThisTrackPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying;
        _isBuffering = track.Id == TrackStateBehavior.BufferingTrackId
                       && TrackStateBehavior.IsCurrentlyBuffering;

        // Title accent color
        var accentBrush = _themeColors?.AccentText ?? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        var normalBrush = _themeColors?.TextPrimary ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        if (Mode == TrackItemDisplayMode.Compact)
        {
            CompactTitle.Foreground = (isThisTrack || _isBuffering) ? accentBrush : normalBrush;
        }
        else
        {
            RowTitle.Foreground = (isThisTrack || _isBuffering) ? accentBrush : normalBrush;
        }
    }

    private void UpdateOverlayState()
    {
        if (Track == null) return;

        if (Mode == TrackItemDisplayMode.Compact)
            UpdateCompactOverlay();
        else
            UpdateRowOverlay();
    }

    private void UpdateCompactOverlay()
    {
        if (_isHovered)
        {
            CompactNowPlaying.Visibility = Visibility.Collapsed;
            CompactBufferingRing.IsActive = false;
            CompactBufferingRing.Visibility = Visibility.Collapsed;
            CompactPlayIcon.Glyph = _isThisTrackPlaying ? "\uE769" : "\uE768";

            if (CompactPlayButton.Visibility == Visibility.Collapsed)
            {
                CompactPlayButton.Opacity = 0;
                CompactPlayButton.Visibility = Visibility.Visible;
                CompactPlayButton.UpdateLayout();
                AnimationBuilder.Create()
                    .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(150))
                    .Start(CompactPlayButton);
            }
        }
        else
        {
            if (CompactPlayButton.Visibility == Visibility.Visible)
            {
                AnimationBuilder.Create()
                    .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(100))
                    .Start(CompactPlayButton);
                _ = CollapseAfterDelay(CompactPlayButton, 120);
            }

            if (_isBuffering)
            {
                CompactNowPlaying.Visibility = Visibility.Collapsed;
                CompactBufferingRing.IsActive = true;
                CompactBufferingRing.Visibility = Visibility.Visible;
            }
            else if (_isThisTrackPlaying)
            {
                CompactBufferingRing.IsActive = false;
                CompactBufferingRing.Visibility = Visibility.Collapsed;
                CompactNowPlaying.Visibility = Visibility.Visible;
                CompactNowPlaying.Opacity = 1.0;
                CompactNowPlayingIcon.Glyph = "\uE995";
            }
            else if (_isThisTrackPaused)
            {
                CompactBufferingRing.IsActive = false;
                CompactBufferingRing.Visibility = Visibility.Collapsed;
                CompactNowPlaying.Visibility = Visibility.Visible;
                CompactNowPlaying.Opacity = 0.5;
                CompactNowPlayingIcon.Glyph = "\uE995";
            }
            else
            {
                CompactBufferingRing.IsActive = false;
                CompactBufferingRing.Visibility = Visibility.Collapsed;
                CompactNowPlaying.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void UpdateRowOverlay()
    {
        if (_isHovered)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowNowPlayingIcon.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Visible;
            RowPlayIcon.Glyph = _isThisTrackPlaying ? "\uE769" : "\uE768";
        }
        else if (_isBuffering)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowNowPlayingIcon.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = true;
            RowBufferingRing.Visibility = Visibility.Visible;
        }
        else if (_isThisTrackPlaying)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
            RowNowPlayingIcon.Visibility = Visibility.Visible;
            RowNowPlayingIcon.Glyph = "\uE995";
        }
        else if (_isThisTrackPaused)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
            RowNowPlayingIcon.Visibility = Visibility.Visible;
            RowNowPlayingIcon.Glyph = "\uE769";
        }
        else
        {
            RowIndexText.Visibility = Visibility.Visible;
            RowNowPlayingIcon.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
        }
    }

    private static async Task CollapseAfterDelay(UIElement element, int ms)
    {
        await Task.Delay(ms);
        element.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region Click / Play

    private void OnPlayButtonClick(object sender, RoutedEventArgs e)
    {
        var track = Track;
        if (track == null) return;

        if (track.Id == TrackStateBehavior.CurrentTrackId)
        {
            _playbackStateService?.PlayPause();
        }
        else
        {
            if (PlayCommand?.CanExecute(track) == true)
                PlayCommand.Execute(track);
        }
    }

    private void OnHeartClicked()
    {
        var track = Track;
        if (track == null)
        {
            _logger?.LogWarning("HeartButton clicked but no track is bound");
            return;
        }
        if (_likeService == null)
        {
            _logger?.LogWarning("HeartButton clicked but ITrackLikeService is not available");
            return;
        }

        var uri = track.Uri;
        if (string.IsNullOrEmpty(uri)) uri = $"spotify:track:{track.Id}";

        _logger?.LogInformation("HeartButton: ToggleSave uri={Uri}, currentlyLiked={IsLiked}", uri, track.IsLiked);

        // Just tell the service — it updates the SourceCache, fires SaveStateChanged,
        // and ALL hearts across the app react via OnSaveStateChanged.
        _likeService.ToggleSave(Data.Contracts.SavedItemType.Track, uri, track.IsLiked);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        TrackStateBehavior.PlaybackStateChanged -= OnPlaybackStateChanged;
        if (_likeService != null)
            _likeService.SaveStateChanged -= OnSaveStateChanged;
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't handle taps on interactive elements (buttons, links)
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;

        var settings = TryGetSettings();
        if (settings?.Settings.TrackClickBehavior != "SingleTap") return;

        HandleTrackPlay();
        e.Handled = true;
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Don't handle double-taps on interactive elements
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;

        var settings = TryGetSettings();
        if (settings?.Settings.TrackClickBehavior == "SingleTap") return;

        HandleTrackPlay();
        e.Handled = true;
    }

    private void HandleTrackPlay()
    {
        var track = Track;
        if (track == null || PlayCommand?.CanExecute(track) != true) return;
        PlayCommand.Execute(track);
    }

    #endregion

    #region Navigation Links (Row mode)

    private void OnArtistLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton link && link.Tag is string artistId && !string.IsNullOrEmpty(artistId))
        {
            ArtistClicked?.Invoke(this, artistId);
            NavigationHelpers.OpenArtist(artistId, link.Content as string ?? "");
        }
    }

    private void OnAlbumLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton link && link.Tag is string albumId && !string.IsNullOrEmpty(albumId))
        {
            AlbumClicked?.Invoke(this, albumId);
            var param = new Data.Parameters.ContentNavigationParameter
            {
                Uri = albumId,
                Title = link.Content as string ?? "",
                ImageUrl = Track?.ImageUrl
            };
            NavigationHelpers.OpenAlbum(param, param.Title);
        }
    }

    #endregion

    #region Context Menu

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var track = Track;
        if (track == null) return;

        ShowContextMenu(e.GetPosition(this));
        e.Handled = true;
    }

    private void OnHolding(object sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState != Microsoft.UI.Input.HoldingState.Started) return;

        var track = Track;
        if (track == null) return;

        ShowContextMenu(e.GetPosition(this));
        e.Handled = true;
    }

    private void ShowContextMenu(Windows.Foundation.Point position)
    {
        var track = Track;
        if (track == null) return;

        var options = new TrackContextMenuOptions
        {
            PlayCommand = PlayCommand,
            AddToQueueCommand = AddToQueueCommand,
            RemoveCommand = RemoveCommand,
            RemoveLabel = RemoveCommandLabel
        };

        var menu = TrackContextMenu.Create(track, options);
        menu.ShowAt(this, position);
    }

    #endregion

    #region Cleanup

    // Unsubscribe handled inline: Unloaded event removes save-state listener
    // to prevent leaks when TrackItem is recycled by ItemsRepeater/ListView.

    #endregion

    #region Helpers

    /// <summary>
    /// Checks if a visual tree element is an interactive control (button, link)
    /// that should not trigger row-level tap-to-play.
    /// </summary>
    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Button or HyperlinkButton) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private static ISettingsService? TryGetSettings()
    {
        if (_cachedSettingsService != null) return _cachedSettingsService;
        try { return _cachedSettingsService = Ioc.Default.GetService<ISettingsService>(); }
        catch (Exception ex) { Debug.WriteLine($"Failed to resolve ISettingsService: {ex.Message}"); return null; }
    }

    #endregion
}

