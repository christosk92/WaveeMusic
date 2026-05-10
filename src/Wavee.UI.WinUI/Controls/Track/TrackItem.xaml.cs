using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Helpers.Playback;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.Styles;

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

    public static readonly DependencyProperty PlayNextCommandProperty =
        DependencyProperty.Register(nameof(PlayNextCommand), typeof(ICommand), typeof(TrackItem),
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

    public static readonly DependencyProperty AlbumColumnWidthProperty =
        DependencyProperty.Register(nameof(AlbumColumnWidth), typeof(double), typeof(TrackItem),
            new PropertyMetadata(180d, OnRowColumnWidthChanged));

    public double AlbumColumnWidth
    {
        get => (double)GetValue(AlbumColumnWidthProperty);
        set => SetValue(AlbumColumnWidthProperty, value);
    }

    public static readonly DependencyProperty DateAddedColumnWidthProperty =
        DependencyProperty.Register(nameof(DateAddedColumnWidth), typeof(double), typeof(TrackItem),
            new PropertyMetadata(120d, OnRowColumnWidthChanged));

    public double DateAddedColumnWidth
    {
        get => (double)GetValue(DateAddedColumnWidthProperty);
        set => SetValue(DateAddedColumnWidthProperty, value);
    }

    public static readonly DependencyProperty PlayCountColumnWidthProperty =
        DependencyProperty.Register(nameof(PlayCountColumnWidth), typeof(double), typeof(TrackItem),
            new PropertyMetadata(100d, OnRowColumnWidthChanged));

    public double PlayCountColumnWidth
    {
        get => (double)GetValue(PlayCountColumnWidthProperty);
        set => SetValue(PlayCountColumnWidthProperty, value);
    }

    public static readonly DependencyProperty ProgressColumnWidthProperty =
        DependencyProperty.Register(nameof(ProgressColumnWidth), typeof(double), typeof(TrackItem),
            new PropertyMetadata(150d, OnRowColumnWidthChanged));

    public double ProgressColumnWidth
    {
        get => (double)GetValue(ProgressColumnWidthProperty);
        set => SetValue(ProgressColumnWidthProperty, value);
    }

    public static readonly DependencyProperty AddedByColumnWidthProperty =
        DependencyProperty.Register(nameof(AddedByColumnWidth), typeof(double), typeof(TrackItem),
            new PropertyMetadata(140d, OnRowColumnWidthChanged));

    public double AddedByColumnWidth
    {
        get => (double)GetValue(AddedByColumnWidthProperty);
        set => SetValue(AddedByColumnWidthProperty, value);
    }

    public static readonly DependencyProperty DurationColumnWidthProperty =
        DependencyProperty.Register(nameof(DurationColumnWidth), typeof(double), typeof(TrackItem),
            new PropertyMetadata(60d, OnRowColumnWidthChanged));

    public double DurationColumnWidth
    {
        get => (double)GetValue(DurationColumnWidthProperty);
        set => SetValue(DurationColumnWidthProperty, value);
    }

    private static void OnRowColumnWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackItem item && item.Mode == TrackItemDisplayMode.Row && item._batchUpdateDepth == 0)
            item.ApplyRowColumnVisibility();
    }

    /// <summary>
    /// Depth counter for <see cref="BeginBatchUpdate"/>/<see cref="EndBatchUpdate"/>.
    /// While &gt; 0 the Show*/Width DP change handlers skip <see cref="ApplyRowColumnVisibility"/>;
    /// <see cref="EndBatchUpdate"/> flushes once at the end. Callers batch the 4 Show flags +
    /// 3 width DPs per virtualized row to turn 7 layout passes into 1.
    /// </summary>
    private int _batchUpdateDepth;

    public void BeginBatchUpdate() => _batchUpdateDepth++;

    public void EndBatchUpdate()
    {
        if (_batchUpdateDepth == 0) return;
        _batchUpdateDepth--;
        if (_batchUpdateDepth == 0 && Mode == TrackItemDisplayMode.Row)
            ApplyRowColumnVisibility();
    }

    public static readonly DependencyProperty ShowDateAddedProperty =
        DependencyProperty.Register(nameof(ShowDateAdded), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnColumnVisibilityChanged));

    public static readonly DependencyProperty DateAddedTextProperty =
        DependencyProperty.Register(nameof(DateAddedText), typeof(string), typeof(TrackItem),
            new PropertyMetadata(null, OnDateAddedTextChanged));

    public static readonly DependencyProperty ShowPlayCountProperty =
        DependencyProperty.Register(nameof(ShowPlayCount), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnColumnVisibilityChanged));

    public static readonly DependencyProperty PlayCountTextProperty =
        DependencyProperty.Register(nameof(PlayCountText), typeof(string), typeof(TrackItem),
            new PropertyMetadata(null, OnPlayCountTextChanged));

    public static readonly DependencyProperty ShowProgressProperty =
        DependencyProperty.Register(nameof(ShowProgress), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnColumnVisibilityChanged));

    public static readonly DependencyProperty ShowPopularityBadgeProperty =
        DependencyProperty.Register(nameof(ShowPopularityBadge), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnShowPopularityBadgeChanged));

    public static readonly DependencyProperty ShowAddedByColumnProperty =
        DependencyProperty.Register(nameof(ShowAddedByColumn), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnColumnVisibilityChanged));

    public static readonly DependencyProperty AddedByTextProperty =
        DependencyProperty.Register(nameof(AddedByText), typeof(string), typeof(TrackItem),
            new PropertyMetadata(null, OnAddedByTextChanged));

    public static readonly DependencyProperty AddedByAvatarUrlProperty =
        DependencyProperty.Register(nameof(AddedByAvatarUrl), typeof(string), typeof(TrackItem),
            new PropertyMetadata(null, OnAddedByAvatarUrlChanged));

    public static readonly DependencyProperty IsCompactRowProperty =
        DependencyProperty.Register(nameof(IsCompactRow), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnIsCompactRowChanged));

    public static readonly DependencyProperty RowDensityProperty =
        DependencyProperty.Register(nameof(RowDensity), typeof(int), typeof(TrackItem),
            new PropertyMetadata(2, OnRowDensityChanged));

    // XS → XL. Paddings shrink at XS so the row can actually hit its 32-px target;
    // default (M) matches the original Padding="8,8" from XAML so unchanged rows are
    // pixel-identical to before this DP existed.
    private static readonly Thickness[] RowDensityPaddings =
    {
        new Thickness(4, 2, 4, 2),
        new Thickness(6, 4, 6, 4),
        new Thickness(8, 6, 8, 6),
        new Thickness(10, 10, 10, 10),
        new Thickness(12, 14, 12, 14),
    };

    // Album art square size per step. 0 at XS means "hidden". Column width is
    // derived as artSize + 8 (right-side gap).
    private static readonly double[] RowDensityArtSizes =
    {
        0d,
        28d,
        34d,
        40d,
        48d,
    };

    public static readonly DependencyProperty PlaceholderColorHexProperty =
        DependencyProperty.Register(nameof(PlaceholderColorHex), typeof(string), typeof(TrackItem),
            new PropertyMetadata(null, (d, e) => ((TrackItem)d).ApplyPlaceholderColor(e.NewValue as string)));

    public static readonly DependencyProperty UseImageColorHintProperty =
        DependencyProperty.Register(nameof(UseImageColorHint), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(true, (d, _) => ((TrackItem)d).ResolveImageColorHint()));

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, (d, _) => ((TrackItem)d).UpdateSelectionVisualState()));

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

    public ICommand? PlayNextCommand
    {
        get => (ICommand?)GetValue(PlayNextCommandProperty);
        set => SetValue(PlayNextCommandProperty, value);
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

    public bool ShowPlayCount
    {
        get => (bool)GetValue(ShowPlayCountProperty);
        set => SetValue(ShowPlayCountProperty, value);
    }

    public bool ShowProgress
    {
        get => (bool)GetValue(ShowProgressProperty);
        set => SetValue(ShowProgressProperty, value);
    }

    public bool ShowPopularityBadge
    {
        get => (bool)GetValue(ShowPopularityBadgeProperty);
        set => SetValue(ShowPopularityBadgeProperty, value);
    }

    public bool ShowAddedByColumn
    {
        get => (bool)GetValue(ShowAddedByColumnProperty);
        set => SetValue(ShowAddedByColumnProperty, value);
    }

    public string? AddedByText
    {
        get => (string?)GetValue(AddedByTextProperty);
        set => SetValue(AddedByTextProperty, value);
    }

    public string? AddedByAvatarUrl
    {
        get => (string?)GetValue(AddedByAvatarUrlProperty);
        set => SetValue(AddedByAvatarUrlProperty, value);
    }

    public string? PlayCountText
    {
        get => (string?)GetValue(PlayCountTextProperty);
        set => SetValue(PlayCountTextProperty, value);
    }

    public bool IsCompactRow
    {
        get => (bool)GetValue(IsCompactRowProperty);
        set => SetValue(IsCompactRowProperty, value);
    }

    public int RowDensity
    {
        get => (int)GetValue(RowDensityProperty);
        set => SetValue(RowDensityProperty, value);
    }

    public string? PlaceholderColorHex
    {
        get => (string?)GetValue(PlaceholderColorHexProperty);
        set => SetValue(PlaceholderColorHexProperty, value);
    }

    public bool UseImageColorHint
    {
        get => (bool)GetValue(UseImageColorHintProperty);
        set => SetValue(UseImageColorHintProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    #endregion

    #region Events

    public event EventHandler<string>? ArtistClicked;
    public event EventHandler<string>? AlbumClicked;

    #endregion

    #region Internal State

    private bool _isHovered;
    private bool _isAlternateRow;
    private bool _useCardRow;
    private readonly ThemeColorService? _themeColors = Ioc.Default.GetService<ThemeColorService>();
    private readonly Data.Contracts.ITrackLikeService? _likeService = Ioc.Default.GetService<Data.Contracts.ITrackLikeService>();
    private readonly Microsoft.Extensions.Logging.ILogger? _logger = Ioc.Default.GetService<Microsoft.Extensions.Logging.ILogger<TrackItem>>();
    private readonly IPlaybackStateService? _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
    private readonly IMusicVideoMetadataService? _musicVideoMetadata = Ioc.Default.GetService<IMusicVideoMetadataService>();
    private readonly Wavee.UI.Services.ITrackColorHintService? _colorHintService = Ioc.Default.GetService<Wavee.UI.Services.ITrackColorHintService>();
    private static ISettingsService? _cachedSettingsService;
    private static ImageCacheService? _cachedImageCache;
    private bool _isThisTrackPlaying;
    private bool _isThisTrackPaused;
    private bool _isBuffering;
    private string? _boundCompactImageUrl;
    private string? _boundRowImageUrl;
    // URLs we currently hold a pin on in ImageCacheService. May lag _boundCompactImageUrl
    // briefly during a rebind; always kept in sync before the method returns.
    private string? _pinnedCompactUrl;
    private string? _pinnedRowUrl;
    // URL we've already retried once after ImageFailed. Prevents infinite retry loops
    // on a genuinely broken URL. Reset when the URL changes.
    private string? _retriedCompactUrl;
    private string? _retriedRowUrl;
    private ITrackItem? _observedTrack;
    private bool _isMessengerRegistered;
    private bool _isSaveStateSubscribed;

    // Guards against stale color-hint applies after a virtualized row is recycled.
    // Incremented on every ResolveImageColorHint invocation; an awaiting continuation
    // only applies its result if this counter hasn't advanced since it started.
    private int _colorHintVersion;

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

        // Row mode navigation links. Per-artist links inside RowArtistsHost are
        // wired in RebuildArtistsSubline (each link gets its own Click handler);
        // RowAlbumLink stays a single HyperlinkButton.
        RowAlbumLink.Click += OnAlbumLinkClick;

        // Transient CDN/network failures: retry once per URL with a fresh BitmapImage.
        CompactAlbumArt.ImageFailed += OnCompactAlbumArtFailed;
        RowAlbumArt.ImageFailed += OnRowAlbumArtFailed;
    }

    #region Mode Switching

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item.ApplyMode();
        item.ResetHoverVisualState();
        item.SyncLoadingStateFromTrack();
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
            ApplyRowDensityPadding();
            ApplyRowColumnVisibility();
        }

        RowPopularityBadge.Visibility = ShowPopularityBadge && Mode == TrackItemDisplayMode.Row
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    #endregion

    #region Track Changed

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item.ObserveTrack(item.IsLoaded ? e.NewValue as ITrackItem : null);
        item.ResetHoverVisualState();
        item.StopPendingBeam();
        item.SyncLoadingStateFromTrack();
        item.BindTrackData();
        item.ResolveImageColorHint();
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
            CompactLocalBadge.Visibility = track.IsLocal ? Visibility.Visible : Visibility.Collapsed;

            UpdateBadgePlacement();
            CompactHeartButton.IsLiked = GetTrackLikedState(track);
            CompactHeartButton.Visibility = Visibility.Visible;
            ApplyCompactAlbumArt(track.ImageSmallUrl ?? track.ImageUrl);
        }
        else
        {
            CompactTitle.Text = "";
            CompactSubtitle.Text = "";
            CompactDuration.Text = "";
            CompactLocalBadge.Visibility = Visibility.Collapsed;
            CompactHeartButton.Visibility = Visibility.Collapsed;
            UpdateBadgePlacement();
            ApplyCompactAlbumArt(null);
        }
    }

    private void BindRowData(ITrackItem? track)
    {
        if (track != null)
        {
            RowTitle.Text = track.Title ?? "";
            RowLocalBadge.Visibility = track.IsLocal ? Visibility.Visible : Visibility.Collapsed;
            RowDuration.Text = track.DurationFormatted ?? "";

            RowHeartButton.IsLiked = GetTrackLikedState(track);
            RowHeartButton.Visibility = Visibility.Visible;
            var artistName = track.ArtistName ?? "";
            RebuildArtistsSubline(track);
            // Hide the subline when ShowArtistColumn is off OR the artist name is blank
            // (e.g. local files, editorial placeholders).
            RowArtistsHost.Visibility = (ShowArtistColumn && !ShowProgress && !string.IsNullOrEmpty(artistName))
                ? Visibility.Visible
                : Visibility.Collapsed;
            // Must run after RowArtistsHost.Visibility is set — placement depends on it.
            UpdateBadgePlacement();
            RowAlbumLink.Content = track.AlbumName ?? "";
            RowAlbumLink.Tag = track.AlbumId;
            ApplyRowProgress(track);
            ApplyRowAlbumArt(track.ImageSmallUrl ?? track.ImageUrl);

            // Row index
            RowIndexText.Text = (track.OriginalIndex > 0)
                ? track.OriginalIndex.ToString()
                : RowIndex > 0 ? RowIndex.ToString() : "";

            ApplyChartStatus(track);
        }
        else
        {
            RowTitle.Text = "";
            RowLocalBadge.Visibility = Visibility.Collapsed;
            RowDuration.Text = "";
            RowArtistsHost.Children.Clear();
            RowAlbumLink.Content = "";
            ApplyRowProgress(null);
            UpdateBadgePlacement();
            ApplyRowAlbumArt(null);
            ApplyChartStatus(null);
        }
    }

    /// <summary>
    /// Renders the chart-status badge in the bottom of the Index slot for
    /// chart-format playlists (Top 50 etc.). Inert for any track whose
    /// <c>FormatAttributes</c> doesn't carry chart fields — the slot
    /// reverts to the centered position number.
    /// </summary>
    private void ApplyChartStatus(ITrackItem? track)
    {
        var info = (track as PlaylistTrackDto)?.Chart;
        if (info is null)
        {
            RowChartStatusContainer.Visibility = Visibility.Collapsed;
            RowIndexText.HorizontalAlignment = HorizontalAlignment.Center;
            RowIndexText.Margin = new Thickness(0);
            return;
        }
        RowChartStatusContainer.Visibility = Visibility.Visible;
        RowIndexText.HorizontalAlignment = HorizontalAlignment.Left;
        RowIndexText.Margin = new Thickness(6, 0, 0, 0);
        RowChartStatusGlyph.Visibility = Visibility.Visible;

        switch (info.Status)
        {
            case ChartStatus.Up:
                RowChartStatusGlyph.Glyph = FluentGlyphs.ChartUp;
                RowChartStatusGlyph.Foreground =
                    (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                RowChartStatusDelta.Foreground =
                    (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
                RowChartStatusDelta.Text = info.Delta is > 0
                    ? info.Delta!.Value.ToString()
                    : string.Empty;
                break;
            case ChartStatus.Down:
                RowChartStatusGlyph.Glyph = FluentGlyphs.ChartDown;
                RowChartStatusGlyph.Foreground =
                    (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                RowChartStatusDelta.Foreground =
                    (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                RowChartStatusDelta.Text = info.Delta is < 0
                    ? (-info.Delta!.Value).ToString()
                    : string.Empty;
                break;
            case ChartStatus.Equal:
                RowChartStatusGlyph.Glyph = FluentGlyphs.ChartEqual;
                RowChartStatusGlyph.Foreground =
                    (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
                RowChartStatusDelta.Text = string.Empty;
                break;
            case ChartStatus.New:
                RowChartStatusGlyph.Visibility = Visibility.Collapsed;
                RowChartStatusDelta.Foreground =
                    (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
                RowChartStatusDelta.Text =
                    AppLocalization.GetString("Playlist_Chart_New");
                break;
        }
        ToolTipService.SetToolTip(RowChartStatusContainer, BuildChartTooltip(info));
    }

    private static string BuildChartTooltip(ChartTrackInfo info) => info.Status switch
    {
        ChartStatus.Up    => AppLocalization.Format(
                                "Playlist_Chart_TooltipUp",
                                info.Delta, info.PreviousPosition),
        ChartStatus.Down  => AppLocalization.Format(
                                "Playlist_Chart_TooltipDown",
                                info.Delta is int d ? -d : 0, info.PreviousPosition),
        ChartStatus.Equal => AppLocalization.GetString("Playlist_Chart_TooltipEqual"),
        ChartStatus.New   => AppLocalization.GetString("Playlist_Chart_TooltipNew"),
        _                 => string.Empty,
    };

    private void ApplyRowProgress(ITrackItem? track)
    {
        var progress = Math.Clamp(track?.PlaybackProgress ?? 0d, 0d, 1d);
        RowProgressBar.Value = progress * 100d;
        var hasError = track?.HasPlaybackProgressError == true;
        var isPlayed = !hasError && progress >= 0.995d;
        var hasProgressBar = !hasError && !isPlayed && progress > 0.001d;
        RowProgressExplicit.Visibility = track?.IsExplicit == true ? Visibility.Visible : Visibility.Collapsed;
        RowPlayedIndicator.Visibility = isPlayed ? Visibility.Visible : Visibility.Collapsed;
        RowProgressBar.Visibility = hasProgressBar ? Visibility.Visible : Visibility.Collapsed;
        RowProgressText.Visibility = isPlayed ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(RowProgressText, hasProgressBar ? 2 : 1);
        Grid.SetColumnSpan(RowProgressText, hasProgressBar ? 1 : 2);
        RowProgressText.HorizontalAlignment = hasProgressBar ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        RowProgressText.Foreground = hasError
            ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var releaseText = track is LibraryEpisodeDto { ReleaseDate: DateTimeOffset releaseDate }
            ? releaseDate.LocalDateTime.ToString("MMM d, yyyy")
            : "";

        RowPlayedText.Text = string.IsNullOrEmpty(releaseText)
            ? "Played"
            : $"Played · {releaseText}";

        if (!string.IsNullOrEmpty(releaseText))
            RowPlayedText.Text = $"Played - {releaseText}";

        var progressText = string.IsNullOrWhiteSpace(track?.PlaybackProgressText)
            ? "Unplayed"
            : track.PlaybackProgressText;

        if (!string.IsNullOrEmpty(releaseText))
            progressText = $"{progressText} · {releaseText}";

        if (!string.IsNullOrEmpty(releaseText))
        {
            var baseProgressText = string.IsNullOrWhiteSpace(track?.PlaybackProgressText)
                ? "Unplayed"
                : track.PlaybackProgressText;
            progressText = $"{baseProgressText} - {releaseText}";
        }

        RowProgressText.Text = progressText;
    }

    private void ApplyCompactAlbumArt(string? imageUrl)
    {
        // Idempotent: same URL and the Image still has a Source → don't churn.
        // Without this, recycled containers, OnLoaded rebinds, and shimmer→loaded
        // transitions all force WinUI to drop its decoded bitmap and re-decode.
        if (imageUrl == _boundCompactImageUrl && CompactAlbumArt.Source != null)
        {
            CompactAlbumArt.Visibility = Visibility.Visible;
            CompactAlbumArt.Opacity = 1;
            return;
        }

        // Only clear the retry guard when the URL actually changes. A retry of the
        // same URL must keep its guard set so we don't loop on a permanently-broken
        // image.
        bool urlChanged = imageUrl != _boundCompactImageUrl;
        _boundCompactImageUrl = imageUrl;
        if (urlChanged) _retriedCompactUrl = null;
        CompactAlbumArt.Visibility = Visibility.Visible;
        CompactAlbumArt.Source = null;

        if (!string.IsNullOrEmpty(_pinnedCompactUrl))
        {
            _cachedImageCache?.Unpin(_pinnedCompactUrl, 48);
            _pinnedCompactUrl = null;
        }

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl))
            return;

        _cachedImageCache ??= Ioc.Default.GetService<ImageCacheService>();
        // Atomic pin-on-insert avoids the self-eviction race where the
        // just-added entry was the only unpinned candidate when the cache
        // was full of pinned visible cards.
        CompactAlbumArt.Source = _cachedImageCache?.GetOrCreate(httpsUrl, 48, pin: true);
        CompactAlbumArt.Opacity = 1;
        _pinnedCompactUrl = httpsUrl;
    }

    private void ApplyRowAlbumArt(string? imageUrl)
    {
        if (imageUrl == _boundRowImageUrl && RowAlbumArt.Source != null)
        {
            RowAlbumArt.Visibility = Visibility.Visible;
            RowAlbumArt.Opacity = 1;
            RowArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        bool urlChanged = imageUrl != _boundRowImageUrl;
        _boundRowImageUrl = imageUrl;
        if (urlChanged) _retriedRowUrl = null;
        RowAlbumArt.Source = null;

        if (!string.IsNullOrEmpty(_pinnedRowUrl))
        {
            _cachedImageCache?.Unpin(_pinnedRowUrl, 48);
            _pinnedRowUrl = null;
        }

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            RowAlbumArt.Visibility = Visibility.Collapsed;
            RowArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        // Placeholder stays visible behind the image; the image fades in on top
        // (FadeInOnLoad) and fully covers the icon once opaque.
        RowArtPlaceholder.Visibility = Visibility.Visible;
        _cachedImageCache ??= Ioc.Default.GetService<ImageCacheService>();
        // Atomic pin-on-insert (same race fix as the compact path above).
        RowAlbumArt.Source = _cachedImageCache?.GetOrCreate(httpsUrl, 48, pin: true);
        RowAlbumArt.Opacity = 1;
        _pinnedRowUrl = httpsUrl;
        RowAlbumArt.Visibility = Visibility.Visible;
    }

    private void OnCompactAlbumArtFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var url = _pinnedCompactUrl;
        if (string.IsNullOrEmpty(url) || url == _retriedCompactUrl) return;
        _retriedCompactUrl = url;

        // Drop the poisoned BitmapImage so the next GetOrCreate creates a fresh one
        // and does a new UriSource decode. Clearing Source trips the idempotent
        // guard in ApplyCompactAlbumArt so the rebind actually runs.
        _cachedImageCache?.Invalidate(url, 48);
        _cachedImageCache?.Unpin(url, 48);
        _pinnedCompactUrl = null;
        CompactAlbumArt.Source = null;
        DispatcherQueue?.TryEnqueue(() => ApplyCompactAlbumArt(_boundCompactImageUrl));
    }

    private void OnRowAlbumArtFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var url = _pinnedRowUrl;
        if (string.IsNullOrEmpty(url) || url == _retriedRowUrl) return;
        _retriedRowUrl = url;

        _cachedImageCache?.Invalidate(url, 48);
        _cachedImageCache?.Unpin(url, 48);
        _pinnedRowUrl = null;
        RowAlbumArt.Source = null;
        DispatcherQueue?.TryEnqueue(() => ApplyRowAlbumArt(_boundRowImageUrl));
    }

    private void ApplyPlaceholderColor(string? hex)
    {
        var fallback = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
        if (string.IsNullOrEmpty(hex))
        {
            if (CompactAlbumArtBorder != null) CompactAlbumArtBorder.Background = fallback;
            if (RowAlbumArtBorder != null) RowAlbumArtBorder.Background = fallback;
            return;
        }

        var color = ParseHexColor(hex);
        if (CompactAlbumArtBorder != null)
            CompactAlbumArtBorder.Background = new SolidColorBrush(color) { Opacity = 0.3 };
        if (RowAlbumArtBorder != null)
            RowAlbumArtBorder.Background = new SolidColorBrush(color) { Opacity = 0.3 };
    }

    /// <summary>
    /// When <see cref="UseImageColorHint"/> is true and no explicit
    /// <see cref="PlaceholderColorHex"/> was provided, resolves the per-track dominant
    /// color via <see cref="ITrackColorHintService"/> and applies it as the placeholder
    /// tint. Safe across virtualized-row recycling: each invocation bumps a version
    /// counter and the async continuation only applies its result if the row is still
    /// bound to the same track image.
    /// </summary>
    private void ResolveImageColorHint()
    {
        // Explicit PlaceholderColorHex wins — if a page set it (e.g. an album page
        // using a single album tint), don't override with a per-track hint.
        if (!string.IsNullOrEmpty(PlaceholderColorHex)) return;
        if (!UseImageColorHint) return;
        if (_colorHintService == null) return;

        var rawUrl = Track?.ImageUrl;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            ApplyPlaceholderColor(null);
            return;
        }

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(rawUrl);
        if (string.IsNullOrWhiteSpace(httpsUrl))
        {
            ApplyPlaceholderColor(null);
            return;
        }

        var version = System.Threading.Interlocked.Increment(ref _colorHintVersion);

        // Fast path: synchronous cache hit — apply inline, no async hop.
        if (_colorHintService.TryGet(httpsUrl, out var cachedHex))
        {
            ApplyPlaceholderColor(cachedHex);
            return;
        }

        // Apply neutral immediately so the row doesn't flash a stale previous color
        // while the background worker resolves this URL's color.
        ApplyPlaceholderColor(null);

        _ = ResolveImageColorHintAsync(httpsUrl, version);
    }

    private async Task ResolveImageColorHintAsync(string httpsUrl, int version)
    {
        try
        {
            var hex = await _colorHintService!.GetOrResolveAsync(httpsUrl).ConfigureAwait(true);
            // Row recycled to a different track while we were waiting: drop the result.
            if (_colorHintVersion != version) return;
            ApplyPlaceholderColor(hex);
        }
        catch (OperationCanceledException)
        {
            // Row was unloaded or cancelled — fine, nothing to do.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Color-hint resolution failed for {Url}", httpsUrl);
        }
    }

    private static Windows.UI.Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length switch
        {
            6 => Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16)),
            8 => Windows.UI.Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16)),
            _ => Windows.UI.Color.FromArgb(255, 128, 128, 128)
        };
    }

    private void RebindAlbumArtIfNeeded()
    {
        if (Mode == TrackItemDisplayMode.Compact)
        {
            if (!string.IsNullOrEmpty(_boundCompactImageUrl) && CompactAlbumArt.Source == null)
                ApplyCompactAlbumArt(_boundCompactImageUrl);
        }
        else
        {
            if (!string.IsNullOrEmpty(_boundRowImageUrl) && RowAlbumArt.Source == null)
                ApplyRowAlbumArt(_boundRowImageUrl);
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
        if (item.Mode == TrackItemDisplayMode.Row && item._batchUpdateDepth == 0)
            item.ApplyRowColumnVisibility();
    }

    private static void OnDateAddedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item.RowDateAdded.Text = (string?)e.NewValue ?? "";
    }

    private static void OnPlayCountTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item.RowPlayCount.Text = (string?)e.NewValue ?? "";
    }

    private static void OnShowPopularityBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item.RowPopularityBadge.Visibility = (bool)e.NewValue && item.Mode == TrackItemDisplayMode.Row
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void OnAddedByTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        var text = (string?)e.NewValue ?? "";
        item.RowAddedByText.Text = text;
        // Feed the same text to PersonPicture so it can derive initials when
        // the avatar URL is missing — without DisplayName, PersonPicture
        // falls back to a generic person glyph instead of the user's initial.
        item.RowAddedByAvatar.DisplayName = text;
        // Empty text → collapse the cell entirely so empty rows don't
        // reserve space for a placeholder avatar + label.
        item.RowAddedByCell.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static void OnAddedByAvatarUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        var url = (string?)e.NewValue;
        if (string.IsNullOrEmpty(url))
        {
            // Clear the photo so PersonPicture renders its initials / glyph fallback.
            item.RowAddedByAvatar.ProfilePicture = null;
            return;
        }

        // The resolver may return either a direct https URL or a Spotify
        // internal `spotify:image:{hex}` reference; route both through the
        // helper so PersonPicture always gets a loadable URI.
        var httpsUrl = Helpers.SpotifyImageHelper.ToHttpsUrl(url) ?? url;
        if (!Uri.TryCreate(httpsUrl, UriKind.Absolute, out var avatarUri))
        {
            item.RowAddedByAvatar.ProfilePicture = null;
            return;
        }
        item.RowAddedByAvatar.ProfilePicture = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(avatarUri)
        {
            DecodePixelWidth = 40
        };
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

    public void SetAlternatingBorder(bool isAlternate, bool useCardRow = false)
    {
        _isAlternateRow = isAlternate;
        _useCardRow = useCardRow;
        ApplyRowBackground();
    }

    private const int RowDurationColumnIndex = 9;
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
        var density = Math.Clamp(RowDensity, 0, RowDensityArtSizes.Length - 1);
        var artSize = RowDensityArtSizes[density];
        // XS (density 0) force-hides the album art regardless of ShowAlbumArt — the
        // whole point of XS is "image off + tight padding".
        var effectiveShowArt = ShowAlbumArt && artSize > 0;

        RowArtColDef.Width        = effectiveShowArt   ? new GridLength(artSize + 8)         : new GridLength(0);
        RowAlbumColDef.Width      = ShowAlbumColumn    ? new GridLength(AlbumColumnWidth)     : new GridLength(0);
        RowAddedByColDef.Width    = ShowAddedByColumn  ? new GridLength(AddedByColumnWidth)   : new GridLength(0);
        RowDateColDef.Width       = ShowDateAdded      ? new GridLength(DateAddedColumnWidth) : new GridLength(0);
        RowPlayCountColDef.Width  = ShowPlayCount      ? new GridLength(PlayCountColumnWidth)
            : new GridLength(0);
        RowDurationColDef.Width   = new GridLength(DurationColumnWidth);
        RowPlayCount.Visibility = ShowPlayCount ? Visibility.Visible : Visibility.Collapsed;
        RowProgressCell.Visibility = ShowProgress ? Visibility.Visible : Visibility.Collapsed;

        // Collapsing the column to Width=0 alone isn't enough: RowAlbumArtBorder
        // has a fixed Width/Height in XAML and would still render into the next
        // column. Toggle visibility and resize to match the current density step.
        if (RowAlbumArtBorder is not null)
        {
            RowAlbumArtBorder.Visibility = effectiveShowArt ? Visibility.Visible : Visibility.Collapsed;
            if (effectiveShowArt)
            {
                RowAlbumArtBorder.Width = artSize;
                RowAlbumArtBorder.Height = artSize;
            }
        }

        // Artist subline is hidden at XS too — single-line rows are how we hit the
        // 32-px target height.
        RowArtistsHost.Visibility = (ShowArtistColumn && density > 0 && !ShowProgress) ? Visibility.Visible : Visibility.Collapsed;

        // Keep the shimmer overlay's columns in sync so loading rows align with the
        // real row layout (and with the column headers above).
        ShimArtColDef.Width       = RowArtColDef.Width;
        ShimAlbumColDef.Width     = RowAlbumColDef.Width;
        ShimAddedByColDef.Width   = RowAddedByColDef.Width;
        ShimDateColDef.Width      = RowDateColDef.Width;
        ShimPlayCountColDef.Width = RowPlayCountColDef.Width;
        ShimDurationColDef.Width  = RowDurationColDef.Width;

        // Subline visibility just changed (artist link) — re-evaluate whether the
        // explicit/video badges should sit on the subline or inline beside the title.
        UpdateBadgePlacement();
    }

    private void ApplyRowDensityPadding()
    {
        var density = Math.Clamp(RowDensity, 0, RowDensityPaddings.Length - 1);
        RowRoot.Padding = RowDensityPaddings[density];
    }

    private static void OnRowDensityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode != TrackItemDisplayMode.Row) return;
        item.ApplyRowDensityPadding();
        if (item._batchUpdateDepth == 0)
            item.ApplyRowColumnVisibility();
    }

    #endregion

    #region Loading State

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item.ApplyLoadingVisualState((bool)e.NewValue);
        item.UpdatePendingBeam();
    }

    private void SyncLoadingStateFromTrack()
    {
        var loading = Track is { IsLoaded: false };
        if (IsLoading != loading)
            IsLoading = loading;
        else
            ApplyLoadingVisualState(loading);
    }

    private void ApplyLoadingVisualState(bool loading)
    {
        if (Mode == TrackItemDisplayMode.Compact)
        {
            CompactAlbumArt.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            CompactArtShimmer.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            CompactInfoPanel.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            CompactInfoShimmer.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            CompactDuration.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            RowContentGrid.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            RowShimmerOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    #endregion

    #region Hover

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;

        if (Mode == TrackItemDisplayMode.Compact)
        {
            ApplyCompactBackground();
        }
        else
        {
            ApplyRowBackground();
        }

        UpdateOverlayState();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ResetHoverVisualState();
        UpdateOverlayState();
    }

    private void ResetHoverVisualState()
    {
        _isHovered = false;

        if (Mode == TrackItemDisplayMode.Compact)
        {
            ApplyCompactBackground();
        }
        else
        {
            ApplyRowBackground();
        }
    }

    private void ApplyCompactBackground()
    {
        if (CompactBorder == null) return;

        if (IsSelected)
        {
            CompactBorder.Background = _isHovered
                ? (_themeColors?.GetBrush("ListViewItemBackgroundSelectedPointerOver")
                   ?? (Brush)Application.Current.Resources["ListViewItemBackgroundSelectedPointerOver"])
                : (_themeColors?.GetBrush("ListViewItemBackgroundSelected")
                   ?? (Brush)Application.Current.Resources["ListViewItemBackgroundSelected"]);
            CompactBorder.BorderBrush = _themeColors?.AccentFill
                ?? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            CompactBorder.BorderThickness = new Thickness(1.5);
        }
        else if (_isHovered)
        {
            CompactBorder.Background = _themeColors?.CardBackground
                ?? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            CompactBorder.BorderBrush = _themeColors?.GetBrush("CardStrokeColorDefaultBrush")
                ?? (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            CompactBorder.BorderThickness = new Thickness(1);
        }
        else
        {
            CompactBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            CompactBorder.BorderBrush = null;
            CompactBorder.BorderThickness = new Thickness(0);
        }
    }

    private void ApplyRowBackground()
    {
        if (RowRoot == null) return;

        bool nativePillShowing = IsSelected || _isHovered;

        if (!nativePillShowing && (_useCardRow || _isAlternateRow))
        {
            // CardBackground (Fluent card tint) gives visible alternating-row
            // striping in both light and dark. The boxed-in-light-mode look
            // users previously complained about was driven by the per-row
            // drop shadow — that's been removed, and the card fill alone
            // reads cleanly in both themes.
            RowRoot.Background = _useCardRow
                ? (_isAlternateRow
                    ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
                    : DefaultBackground)
                : _themeColors?.CardBackground
                  ?? (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
        }
        else
        {
            RowRoot.Background = DefaultBackground;
        }

        // BorderThickness is always 1 — only the BorderBrush colour changes
        // between visible (alternating-row card stroke) and invisible
        // (transparent). Toggling the THICKNESS instead would add / remove
        // 2 px from the row's outer bounds on hover, shifting every cell's
        // inner content by 1 px and producing the visible flicker the user
        // reported. Keep the geometry stable; only repaint.
        RowRoot.BorderThickness = new Thickness(1);
        if (!nativePillShowing && _isAlternateRow)
        {
            RowRoot.BorderBrush = _themeColors?.CardStroke
                ?? (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        }
        else
        {
            RowRoot.BorderBrush = TransparentBrush;
        }
    }

    // Cached transparent brush — reused across hover transitions so we don't
    // allocate a new SolidColorBrush on every PointerEntered / PointerExited.
    private static readonly Brush TransparentBrush =
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void UpdateSelectionVisualState()
    {
        if (CompactSelectionIndicator != null)
            CompactSelectionIndicator.Opacity = IsSelected ? 1 : 0;

        if (RowSelectionIndicator != null)
            RowSelectionIndicator.Opacity = IsSelected ? 1 : 0;

        if (Mode == TrackItemDisplayMode.Compact)
            ApplyCompactBackground();
        else
            ApplyRowBackground();
    }

    #endregion

    #region Playback State

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ObserveTrack(Track);
        RebindAlbumArtIfNeeded();
        RefreshPlaybackState();
        UpdateOverlayState();
        UpdateBadgePlacement();
        RefreshLikedState();

        // Subscribe to global state changes via WeakReferenceMessenger so a
        // missed Unloaded `-=` (or container recycle past Unloaded) doesn't
        // pin this TrackItem in the static event invocation list forever.
        if (!_isMessengerRegistered)
        {
            WeakReferenceMessenger.Default.Register<TrackItem, TrackStateRefreshMessage>(
                this, static (r, _) => r.OnPlaybackStateChanged());
            _isMessengerRegistered = true;
        }

        if (_likeService != null && !_isSaveStateSubscribed)
        {
            _likeService.SaveStateChanged += OnSaveStateChanged;
            _isSaveStateSubscribed = true;
        }
    }

    private void OnPlaybackStateChanged()
    {
        // Cheap pre-check on the calling thread: skip the dispatch when this
        // row's effective playback state can't have flipped. Across the four
        // events that PlaybackStateChanged fans (CurrentTrackId, IsPlaying,
        // IsBuffering, BufferingTrackId), only the previously-active row, the
        // newly-active row, and the buffering row need to update — every
        // other realized TrackItem is a no-op. At 500 visible rows that's a
        // ~1 ms-per-event drop to a handful of µs. The reads below are
        // lock-free statics + plain instance fields, safe on any thread.
        var track = Track;
        if (track == null) return;

        var trackId = track.Id;
        var isThisTrack = trackId == TrackStateBehavior.CurrentTrackId;
        var nowPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        var nowPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying;
        var nowBuffering = trackId == TrackStateBehavior.BufferingTrackId
                           && TrackStateBehavior.IsCurrentlyBuffering;

        if (nowPlaying == _isThisTrackPlaying
            && nowPaused == _isThisTrackPaused
            && nowBuffering == _isBuffering)
            return;

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

        var isLiked = GetTrackLikedState(track);
        CompactHeartButton.IsLiked = isLiked;
        RowHeartButton.IsLiked = isLiked;
        track.IsLiked = isLiked;
    }

    private bool GetTrackLikedState(ITrackItem track)
    {
        if (IsSpotifyEpisodeUri(track.Uri))
            return track.IsLiked;

        if (_likeService is null)
            return track.IsLiked;

        var uri = GetImmediateSaveTargetUri(track);
        if (!string.IsNullOrEmpty(uri))
            return _likeService.IsSaved(Data.Contracts.SavedItemType.Track, uri);

        if (IsCurrentPlaybackVideoTrack(track))
            _ = RefreshCurrentVideoLikedStateAsync(track);

        return false;
    }

    private async Task RefreshCurrentVideoLikedStateAsync(ITrackItem expectedTrack)
    {
        var uri = await PlaybackSaveTargetResolver
            .ResolveTrackUriAsync(_playbackStateService, _musicVideoMetadata)
            .ConfigureAwait(true);
        if (Track != expectedTrack || string.IsNullOrEmpty(uri) || _likeService is null)
            return;

        var isLiked = _likeService.IsSaved(Data.Contracts.SavedItemType.Track, uri);
        CompactHeartButton.IsLiked = isLiked;
        RowHeartButton.IsLiked = isLiked;
        expectedTrack.IsLiked = isLiked;
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
            StopPendingBeam();
            return;
        }

        var wasBuffering = _isBuffering;
        var isThisTrack = track.Id == TrackStateBehavior.CurrentTrackId;
        _isThisTrackPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        _isThisTrackPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying;
        _isBuffering = track.Id == TrackStateBehavior.BufferingTrackId
                       && TrackStateBehavior.IsCurrentlyBuffering;

        if (wasBuffering && !_isBuffering && isThisTrack)
            ResetHoverVisualState();

        // Title accent color. In Light mode, AccentTextFillColorPrimaryBrush
        // resolves to a saturated bright accent (red on default Windows accent),
        // which overpowers neighboring rows. Use the secondary variant in Light
        // for de-emphasis; Dark keeps primary so the active row still pops.
        var accentResource = ActualTheme == ElementTheme.Light
            ? "AccentTextFillColorSecondaryBrush"
            : "AccentTextFillColorPrimaryBrush";
        var accentBrush = _themeColors?.AccentText ?? (Brush)Application.Current.Resources[accentResource];
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
        if (Track == null)
        {
            StopPendingBeam();
            return;
        }

        if (Mode == TrackItemDisplayMode.Compact)
            UpdateCompactOverlay();
        else
            UpdateRowOverlay();

        UpdatePendingBeam();
    }

    private void UpdateCompactOverlay()
    {
        if (_isBuffering)
        {
            if (CompactPlayButton.Visibility == Visibility.Visible)
            {
                CompactPlayButton.Opacity = 0;
                CompactPlayButton.Visibility = Visibility.Collapsed;
            }

            CompactNowPlaying.Visibility = Visibility.Collapsed;
            SetCompactEqualizer(false, false);
            CompactBufferingRing.IsActive = true;
            CompactBufferingRing.Visibility = Visibility.Visible;
        }
        else if (_isHovered)
        {
            CompactNowPlaying.Visibility = Visibility.Collapsed;
            SetCompactEqualizer(false, false);
            CompactBufferingRing.IsActive = false;
            CompactBufferingRing.Visibility = Visibility.Collapsed;
            if (CompactPlayContent != null)
                CompactPlayContent.IsPlaying = _isThisTrackPlaying;

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

            if (_isThisTrackPlaying)
            {
                CompactBufferingRing.IsActive = false;
                CompactBufferingRing.Visibility = Visibility.Collapsed;
                CompactNowPlaying.Visibility = Visibility.Visible;
                CompactNowPlaying.Opacity = 1.0;
                SetCompactEqualizer(true, true);
            }
            else if (_isThisTrackPaused)
            {
                CompactBufferingRing.IsActive = false;
                CompactBufferingRing.Visibility = Visibility.Collapsed;
                CompactNowPlaying.Visibility = Visibility.Visible;
                CompactNowPlaying.Opacity = 0.7;
                SetCompactEqualizer(true, false);
            }
            else
            {
                SetCompactEqualizer(false, false);
                CompactBufferingRing.IsActive = false;
                CompactBufferingRing.Visibility = Visibility.Collapsed;
                CompactNowPlaying.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void UpdateRowOverlay()
    {
        if (_isBuffering)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            SetRowEqualizer(false, false);
            RowBufferingRing.IsActive = true;
            RowBufferingRing.Visibility = Visibility.Visible;
        }
        else if (_isHovered)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            SetRowEqualizer(false, false);
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Visible;
            if (RowPlayContent != null)
                RowPlayContent.IsPlaying = _isThisTrackPlaying;
        }
        else if (_isThisTrackPlaying)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
            SetRowEqualizer(true, true);
        }
        else if (_isThisTrackPaused)
        {
            RowIndexText.Visibility = Visibility.Collapsed;
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
            SetRowEqualizer(true, false);
        }
        else
        {
            RowIndexText.Visibility = Visibility.Visible;
            SetRowEqualizer(false, false);
            RowPlayButton.Visibility = Visibility.Collapsed;
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void SetCompactEqualizer(bool visible, bool active)
    {
        if (visible && CompactNowPlayingEqualizer == null)
            FindName("CompactNowPlayingEqualizer");
        if (CompactNowPlayingEqualizer == null) return;

        CompactNowPlayingEqualizer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        CompactNowPlayingEqualizer.IsActive = visible && active;
    }

    private void SetRowEqualizer(bool visible, bool active)
    {
        if (visible && RowNowPlayingEqualizer == null)
            FindName("RowNowPlayingEqualizer");
        if (RowNowPlayingEqualizer == null) return;

        RowNowPlayingEqualizer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RowNowPlayingEqualizer.IsActive = visible && active;
    }

    private void UpdatePendingBeam()
    {
        if (_isBuffering && !IsLoading)
            StartPendingBeam();
        else
            StopPendingBeam();
    }

    private void StartPendingBeam()
    {
        if (PlaybackPendingBeam == null)
            this.FindName("PlaybackPendingBeam");
        PlaybackPendingBeam?.Start();
    }

    private void StopPendingBeam()
    {
        PlaybackPendingBeam?.Stop();
    }

    private static async Task CollapseAfterDelay(UIElement element, int ms)
    {
        await Task.Delay(ms);
        if (element.Opacity <= 0.01)
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
            ExecutePlayCommandWithPending(track);
        }
    }

    private void OnHeartClicked()
        => _ = OnHeartClickedAsync();

    private async Task OnHeartClickedAsync()
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

        var uri = IsCurrentPlaybackVideoTrack(track)
            ? await PlaybackSaveTargetResolver
                .ResolveTrackUriAsync(_playbackStateService, _musicVideoMetadata)
                .ConfigureAwait(true)
            : GetImmediateSaveTargetUri(track);
        if (string.IsNullOrEmpty(uri))
            return;

        var wasLiked = _likeService.IsSaved(Data.Contracts.SavedItemType.Track, uri);
        _logger?.LogInformation("HeartButton: ToggleSave uri={Uri}, currentlyLiked={IsLiked}", uri, wasLiked);

        // Just tell the service - it updates the cache, fires SaveStateChanged,
        // and ALL hearts across the app react via OnSaveStateChanged.
        _likeService.ToggleSave(Data.Contracts.SavedItemType.Track, uri, wasLiked);
    }

    private string? GetImmediateSaveTargetUri(ITrackItem track)
    {
        if (IsCurrentPlaybackVideoTrack(track))
            return PlaybackSaveTargetResolver.GetTrackUri(_playbackStateService);

        if (IsSpotifyEpisodeUri(track.Uri))
            return null;

        if (!string.IsNullOrEmpty(track.Uri))
            return track.Uri;

        return string.IsNullOrEmpty(track.Id) ? null : $"spotify:track:{track.Id}";
    }

    private static bool IsSpotifyEpisodeUri(string? uri)
        => uri?.StartsWith("spotify:episode:", StringComparison.Ordinal) == true;

    private bool IsCurrentPlaybackVideoTrack(ITrackItem track)
    {
        if (_playbackStateService?.CurrentTrackIsVideo != true)
            return false;

        var currentTrackId = _playbackStateService.CurrentTrackId;
        if (string.IsNullOrEmpty(currentTrackId))
            return false;

        var currentTrackUri = currentTrackId.Contains(':', StringComparison.Ordinal)
            ? currentTrackId
            : $"spotify:track:{currentTrackId}";

        return string.Equals(track.Id, currentTrackId, StringComparison.Ordinal)
               || string.Equals(track.Uri, currentTrackUri, StringComparison.Ordinal);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ObserveTrack(null);
        ResetHoverVisualState();
        SetCompactEqualizer(false, false);
        SetRowEqualizer(false, false);
        StopPendingBeam();
        if (_isMessengerRegistered)
        {
            WeakReferenceMessenger.Default.Unregister<TrackStateRefreshMessage>(this);
            _isMessengerRegistered = false;
        }

        if (_likeService != null && _isSaveStateSubscribed)
        {
            _likeService.SaveStateChanged -= OnSaveStateChanged;
            _isSaveStateSubscribed = false;
        }

        // Release image cache pins so off-screen containers stop blocking eviction.
        if (!string.IsNullOrEmpty(_pinnedCompactUrl))
        {
            _cachedImageCache?.Unpin(_pinnedCompactUrl, 48);
            _pinnedCompactUrl = null;
        }
        if (!string.IsNullOrEmpty(_pinnedRowUrl))
        {
            _cachedImageCache?.Unpin(_pinnedRowUrl, 48);
            _pinnedRowUrl = null;
        }

        // Null the Image.Source on Unloaded — without this, the WinUI compositor
        // visual keeps the native decoded surface alive even after the BitmapImage
        // managed wrapper is unreferenced. With ItemsRepeater recycling thousands
        // of TrackItems across nav, those native surfaces add up to hundreds of
        // MB. The matching ApplyXxxAlbumArt path in OnLoaded re-fetches via the
        // ImageCacheService, which is decode-bucketed so the next render is cheap.
        if (RowAlbumArt != null) RowAlbumArt.Source = null;
        if (CompactAlbumArt != null) CompactAlbumArt.Source = null;
    }

    private void ObserveTrack(ITrackItem? track)
    {
        if (ReferenceEquals(_observedTrack, track)) return;

        if (_observedTrack != null)
            _observedTrack.PropertyChanged -= OnTrackItemPropertyChanged;

        _observedTrack = track;

        if (_observedTrack != null)
            _observedTrack.PropertyChanged += OnTrackItemPropertyChanged;
    }

    private void OnTrackItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _observedTrack)) return;

        if (DispatcherQueue?.HasThreadAccess == true)
        {
            ApplyObservedTrackChange(e.PropertyName);
            return;
        }

        var propertyName = e.PropertyName;
        DispatcherQueue?.TryEnqueue(() => ApplyObservedTrackChange(propertyName));
    }

    private void ApplyObservedTrackChange(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName) || IsTrackContentProperty(propertyName))
        {
            SyncLoadingStateFromTrack();
            BindTrackData();
            ResolveImageColorHint();
            RefreshPlaybackState();
            UpdateOverlayState();
            return;
        }

        if (propertyName == nameof(ITrackItem.HasVideo))
            UpdateBadgePlacement();
    }

    private static bool IsTrackContentProperty(string propertyName) => propertyName switch
    {
        nameof(ITrackItem.Id) => true,
        nameof(ITrackItem.Uri) => true,
        nameof(ITrackItem.Title) => true,
        nameof(ITrackItem.ArtistName) => true,
        nameof(ITrackItem.ArtistId) => true,
        nameof(ITrackItem.AlbumName) => true,
        nameof(ITrackItem.AlbumId) => true,
        nameof(ITrackItem.ImageUrl) => true,
        nameof(ITrackItem.Duration) => true,
        nameof(ITrackItem.DurationFormatted) => true,
        nameof(ITrackItem.OriginalIndex) => true,
        nameof(ITrackItem.IsLoaded) => true,
        nameof(ITrackItem.IsExplicit) => true,
        nameof(ITrackItem.IsLiked) => true,
        nameof(ITrackItem.IsLocal) => true,
        nameof(ITrackItem.HasVideo) => true,
        nameof(ITrackItem.PlaybackProgress) => true,
        nameof(ITrackItem.PlaybackProgressText) => true,
        nameof(ITrackItem.HasPlaybackProgressError) => true,
        "Data" => true,
        _ => false,
    };

    // Places the explicit + video badges in the right slot for the current row layout.
    // Row mode has two slots: the subline (alongside the artist link) and an inline
    // slot beside the title. When the subline is hidden (album page, XS density,
    // missing artist) the inline slot is used so badges don't float on an empty row.
    // Compact mode always has the artist subtitle, so badges always go on the subline.
    private void UpdateBadgePlacement()
    {
        var track = Track;
        var hasVideo = track?.HasVideo == true;
        var isExplicit = track?.IsExplicit == true;

        // Compact: subtitle is the artist text and is always present when bound.
        CompactExplicit.Visibility = isExplicit ? Visibility.Visible : Visibility.Collapsed;
        CompactVideoBadge.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
        var compactHasSubtitle = !string.IsNullOrWhiteSpace(track?.ArtistName);
        CompactVideoSeparator.Visibility = (hasVideo && compactHasSubtitle)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Row: subline is visible only when the artist link is. Separator depends on
        // the link's visibility, not just on whether ArtistName is set — that's the
        // fix for the orphan "·" on album rows where the link is collapsed but the
        // album artist name is non-empty.
        var sublineVisible = RowArtistsHost.Visibility == Visibility.Visible && !ShowProgress;
        if (sublineVisible)
        {
            RowExplicit.Visibility = isExplicit ? Visibility.Visible : Visibility.Collapsed;
            RowVideoBadge.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
            RowVideoSeparator.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
            RowExplicitInline.Visibility = Visibility.Collapsed;
            RowVideoBadgeInline.Visibility = Visibility.Collapsed;
        }
        else
        {
            RowExplicit.Visibility = Visibility.Collapsed;
            RowVideoBadge.Visibility = Visibility.Collapsed;
            RowVideoSeparator.Visibility = Visibility.Collapsed;
            RowExplicitInline.Visibility = isExplicit && !ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            RowVideoBadgeInline.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
        }
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
        if (track == null) return;
        ExecutePlayCommandWithPending(track);
    }

    private void ExecutePlayCommandWithPending(ITrackItem track)
    {
        if (PlayCommand?.CanExecute(track) != true) return;

        if (track.Id != TrackStateBehavior.CurrentTrackId)
        {
            _playbackStateService?.NotifyBuffering(track.Id);
            ResetHoverVisualState();
            _isThisTrackPlaying = false;
            _isThisTrackPaused = false;
            _isBuffering = true;
            UpdateOverlayState();
        }

        PlayCommand.Execute(track);
    }

    #endregion

    #region Navigation Links (Row mode)

    private void OnArtistLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton link && link.Tag is string artistId && !string.IsNullOrEmpty(artistId))
        {
            // Pull the visible artist name from the inner TextBlock so navigation
            // labels match what was clicked rather than the row's flattened
            // ArtistName string (which may comma-join multiple names).
            var displayName = (link.Content as TextBlock)?.Text
                ?? link.Content as string
                ?? "";
            ArtistClicked?.Invoke(this, artistId);
            NavigationHelpers.OpenArtist(artistId, displayName);
        }
    }

    /// <summary>
    /// Rebuild the per-artist hyperlink stack inside <see cref="RowArtistsHost"/>.
    /// Renders one HyperlinkButton per artist with comma separators between them
    /// when the track carries a rich <see cref="ITrackItem.Artists"/> list; falls
    /// back to a single link from <c>(ArtistName, ArtistId)</c> for legacy DTOs
    /// (LikedSongDto, PlaylistTrackDto, …) that haven't been upgraded yet.
    /// </summary>
    private void RebuildArtistsSubline(ITrackItem track)
    {
        RowArtistsHost.Children.Clear();

        var captionStyle = (Microsoft.UI.Xaml.Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var subduedBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var artists = track.Artists;
        if (artists == null || artists.Count == 0)
        {
            // Single-link fallback. Empty ArtistId is fine — OnArtistLinkClick
            // checks for empty before navigating, matching the legacy behaviour.
            var name = track.ArtistName ?? "";
            if (string.IsNullOrEmpty(name)) return;
            RowArtistsHost.Children.Add(BuildArtistLink(name, track.ArtistId ?? "", captionStyle, subduedBrush));
            return;
        }

        for (var i = 0; i < artists.Count; i++)
        {
            if (i > 0)
            {
                RowArtistsHost.Children.Add(new TextBlock
                {
                    Text = ", ",
                    Style = captionStyle,
                    Foreground = subduedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var a = artists[i];
            RowArtistsHost.Children.Add(BuildArtistLink(a.Name, a.Uri, captionStyle, subduedBrush));
        }
    }

    private HyperlinkButton BuildArtistLink(
        string name,
        string artistTag,
        Microsoft.UI.Xaml.Style captionStyle,
        Brush subduedBrush)
    {
        var link = new HyperlinkButton
        {
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = artistTag,
            Content = new TextBlock
            {
                Text = name,
                Style = captionStyle,
                Foreground = subduedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            }
        };
        link.Click += OnArtistLinkClick;
        return link;
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

        var ctx = new TrackMenuContext
        {
            PlayCommand = PlayCommand,
            PlayNextCommand = PlayNextCommand,
            AddToQueueCommand = AddToQueueCommand,
            RemoveCommand = RemoveCommand,
            RemoveLabel = RemoveCommandLabel
        };

        var items = TrackContextMenuBuilder.Build(track, ctx);
        ContextMenuHost.Show(this, items, position);
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

