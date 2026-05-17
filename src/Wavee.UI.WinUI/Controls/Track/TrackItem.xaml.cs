using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
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
using Wavee.UI.Helpers;
using Wavee.UI.Services;
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
    private const int OptimisticPlayPendingTimeoutMs = 8000;

    #region Dependency Properties

    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(nameof(Track), typeof(ITrackItem), typeof(TrackItem),
            new PropertyMetadata(null, OnTrackChanged));

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(TrackItemDisplayMode), typeof(TrackItem),
            new PropertyMetadata(TrackItemDisplayMode.Row, OnModeChanged));

    // Read-only mirror DPs that drive x:Load on each mode's whole subtree.
    // Synced from OnModeChanged. Defaults match Mode's default (Row) so playlist /
    // album / liked-songs surfaces — the common case — realize RowRoot
    // directly with no Compact→Row flash during template instantiation.
    // Compact-mode callers (e.g. artist Top Tracks, search results) must set
    // Mode="Compact" explicitly.
    public static readonly DependencyProperty IsCompactModeProperty =
        DependencyProperty.Register(nameof(IsCompactMode), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsRowModeProperty =
        DependencyProperty.Register(nameof(IsRowMode), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(true));

    public bool IsCompactMode
    {
        get => (bool)GetValue(IsCompactModeProperty);
        private set => SetValue(IsCompactModeProperty, value);
    }

    public bool IsRowMode
    {
        get => (bool)GetValue(IsRowModeProperty);
        private set => SetValue(IsRowModeProperty, value);
    }

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

    public static readonly DependencyProperty PreserveImageOnUnloadProperty =
        DependencyProperty.Register(nameof(PreserveImageOnUnload), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false));

    public bool PreserveImageOnUnload
    {
        get => (bool)GetValue(PreserveImageOnUnloadProperty);
        set => SetValue(PreserveImageOnUnloadProperty, value);
    }

    public static readonly DependencyProperty ShowArtistColumnProperty =
        DependencyProperty.Register(nameof(ShowArtistColumn), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(true, OnColumnVisibilityChanged));

    public static readonly DependencyProperty ShowAlbumColumnProperty =
        DependencyProperty.Register(nameof(ShowAlbumColumn), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(true, OnColumnVisibilityChanged));

    public static readonly DependencyProperty TitleColumnMaxWidthProperty =
        DependencyProperty.Register(nameof(TitleColumnMaxWidth), typeof(double), typeof(TrackItem),
            new PropertyMetadata(640d, OnRowColumnWidthChanged));

    public double TitleColumnMaxWidth
    {
        get => (double)GetValue(TitleColumnMaxWidthProperty);
        set => SetValue(TitleColumnMaxWidthProperty, value);
    }

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
            new PropertyMetadata(false, OnShowPlayCountChanged));

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

    // Opt-in hover-tint for Row mode. When set, ApplyRowBackground paints this
    // brush on hover (instead of leaving the row transparent). Used by raw
    // ItemsRepeater hosts (ArtistPage top-tracks) that don't get the
    // TrackDataGrid alternating/card striping but still want a hover
    // affordance to match playlist track rows.
    public static readonly DependencyProperty RowHoverBackgroundBrushProperty =
        DependencyProperty.Register(nameof(RowHoverBackgroundBrush), typeof(Brush), typeof(TrackItem),
            new PropertyMetadata(null, OnRowHoverBackgroundBrushChanged));

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

    public Brush? RowHoverBackgroundBrush
    {
        get => (Brush?)GetValue(RowHoverBackgroundBrushProperty);
        set => SetValue(RowHoverBackgroundBrushProperty, value);
    }

    private static void OnRowHoverBackgroundBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TrackItem item) item.ApplyRowBackground();
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
    public event EventHandler? TrackChanged;

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
    private bool _isThisTrackPlaying;
    private bool _isThisTrackPaused;
    private bool _isBuffering;
    private CancellationTokenSource? _localBufferingTimeoutCts;
    private string? _localBufferingTimeoutTrackId;
    private string? _boundCompactImageUrl;
    private string? _boundRowImageUrl;
    // URL we've already retried once after ImageFailed. Prevents infinite retry loops
    // on a genuinely broken URL. Reset when the URL changes.
    private string? _retriedCompactUrl;
    private string? _retriedRowUrl;
    private ITrackItem? _observedTrack;
    private bool _isMessengerRegistered;
    private bool _isSaveStateSubscribed;
    private string? _rowArtistsSignature;
    private string? _boundColorHintUrl;

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

        // Default DP value for Mode is Row, so x:Load realizes RowRoot for
        // this instance. InitializeComponent evaluates x:Bind for x:Load, but
        // realization can be deferred to the next layout pass — force it now
        // so WireRowHandlers' element references are non-null.
        if (RowRoot is null) FindName(nameof(RowRoot));
        WireRowHandlers();

        // Tap-to-play (respects TrackClickBehavior setting)
        Tapped += OnTapped;
        DoubleTapped += OnDoubleTapped;

        // Context menu
        RightTapped += OnRightTapped;
        Holding += OnHolding;

        // CompactAlbumArt / RowAlbumArt ImageFailed subscriptions happen
        // lazily in EnsureCompactAlbumArtRealized / EnsureRowAlbumArtRealized.
        // Both controls are inside x:Load-deferred subtrees, so the named
        // fields are null until the first time the active mode is shown.
    }

    // ── Mode-aware event wiring ────────────────────────────────────────────
    // CompactBorder and RowRoot are x:Load-deferred on Mode, so cross-mode
    // event subscription from the constructor would NRE on the inactive side.
    // Wire the active mode's handlers when its subtree is realized: the
    // constructor wires the default-Row side, and OnModeChanged wires the
    // other side when Mode flips. Idempotent via the _xWired flags; the
    // flags are cleared in OnModeChanged when the corresponding subtree is
    // unloaded so a future re-realization re-attaches handlers to the new
    // element instances.
    private bool _compactHandlersWired;
    private bool _rowHandlersWired;

    private void WireCompactHandlers()
    {
        if (_compactHandlersWired) return;
        if (CompactHeartButton is null || CompactPlayButton is null) return;
        CompactHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnHeartClicked);
        CompactPlayButton.Click += OnPlayButtonClick;
        _compactHandlersWired = true;
    }

    private void WireRowHandlers()
    {
        if (_rowHandlersWired) return;
        if (RowHeartButton is null || RowPlayButton is null || RowAlbumLink is null) return;
        RowHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnHeartClicked);
        RowPlayButton.Click += OnPlayButtonClick;
        RowAlbumLink.Click += OnAlbumLinkClick;
        _rowHandlersWired = true;
    }

    // ── Lazy realize: inactive-mode CompositionImage subtree ──

    private bool _compactAlbumArtSubscribed;
    private bool _rowAlbumArtSubscribed;

    private void EnsureCompactAlbumArtRealized()
    {
        // CompactAlbumArt lives inside CompactBorder, which is x:Load-deferred
        // behind IsCompactMode. Force-realize the parent first so the child's
        // name lookup resolves — FindName on a child of a deferred parent
        // does not transitively realize the parent.
        if (CompactBorder is null) FindName(nameof(CompactBorder));
        if (CompactAlbumArt is null) FindName(nameof(CompactAlbumArt));
        if (!_compactAlbumArtSubscribed && CompactAlbumArt is not null)
        {
            CompactAlbumArt.ImageFailed += OnCompactAlbumArtFailed;
            _compactAlbumArtSubscribed = true;
        }
    }

    private void EnsureRowAlbumArtRealized()
    {
        if (RowRoot is null) FindName(nameof(RowRoot));
        if (RowAlbumArt is null) FindName(nameof(RowAlbumArt));
        if (!_rowAlbumArtSubscribed && RowAlbumArt is not null)
        {
            RowAlbumArt.ImageFailed += OnRowAlbumArtFailed;
            _rowAlbumArtSubscribed = true;
        }
    }

    #region Mode Switching

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // Drive x:Load on the inactive mode's whole subtree. Setting these
        // BEFORE ApplyMode/BindTrackData ensures the right subtree is realized
        // by the time we try to set its ImageUrl.
        var compact = item.Mode == TrackItemDisplayMode.Compact;

        // Reset the wired flag for the side we're leaving — x:Load unloads
        // that subtree and a future Mode flip back will create fresh element
        // instances that need fresh handlers.
        if (compact) item._rowHandlersWired = false;
        else item._compactHandlersWired = false;

        item.IsCompactMode = compact;
        item.IsRowMode = !compact;

        // x:Load binding propagates on the next layout pass, but the imperative
        // calls below (BindTrackData, ApplyLoadingVisualState, etc.) need the
        // named fields populated NOW. Force the active subtree to realize
        // synchronously via FindName — this both wires up the generated x:Name
        // fields and triggers x:Load loading of the deferred element.
        if (compact)
        {
            if (item.CompactBorder is null) item.FindName(nameof(CompactBorder));
        }
        else
        {
            if (item.RowRoot is null) item.FindName(nameof(RowRoot));
        }

        // Wire the newly-active mode's event handlers. Each Wire* method is
        // idempotent, so re-firing for the same mode is a no-op.
        if (compact) item.WireCompactHandlers();
        else item.WireRowHandlers();

        item.ApplyMode();
        item.ResetHoverVisualState();
        item.SyncLoadingStateFromTrack();
        item.BindTrackData();
        item.UpdateOverlayState();
    }

    private void ApplyMode()
    {
        // CompactBorder / RowRoot Visibility is set declaratively in XAML and
        // gated by x:Load on IsCompactMode / IsRowMode — only the active mode's
        // subtree exists, so the inactive side is null. Don't toggle Visibility
        // here; let x:Load handle realization.
        if (Mode != TrackItemDisplayMode.Compact)
        {
            ApplyRowDensityPadding();
            ApplyRowColumnVisibility();
        }

        // RowPopularityBadge lives inside RowRoot, which is null when Mode==Compact.
        if (RowPopularityBadge is not null)
        {
            RowPopularityBadge.Visibility = ShowPopularityBadge && Mode == TrackItemDisplayMode.Row
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
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
        // Refresh the add-to-playlist + affordance against the new track —
        // recycled rows can be re-pointed at a track that's already in the
        // pending set, so we need to repaint the glyph (+ vs check).
        item.UpdateAddToPlaylistAffordance();
        item.TrackChanged?.Invoke(item, EventArgs.Empty);
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
            CompactDuration.Text = track.DurationFormatted ?? "";
            CompactLocalBadge.Visibility = track.IsLocal ? Visibility.Visible : Visibility.Collapsed;

            UpdateBadgePlacement();
            CompactHeartButton.IsLiked = GetTrackLikedState(track);
            CompactHeartButton.Visibility = Visibility.Visible;
            ApplyCompactAlbumArt(track.ImageSmallUrl ?? track.ImageUrl);
            UpdateCompactSubtitleText();
        }
        else
        {
            CompactTitle.Text = "";
            CompactDuration.Text = "";
            CompactLocalBadge.Visibility = Visibility.Collapsed;
            CompactHeartButton.Visibility = Visibility.Collapsed;
            UpdateBadgePlacement();
            if (!PreserveImageOnUnload)
                ApplyCompactAlbumArt(null);
            UpdateCompactSubtitleText();
        }
    }

    private void UpdateCompactSubtitleText()
    {
        if (CompactSubtitle == null)
            return;

        var artist = Track?.ArtistName ?? "";
        var playCount = ShowPlayCount ? PlayCountText : null;

        if (string.IsNullOrWhiteSpace(playCount))
        {
            CompactSubtitle.Text = artist;
            return;
        }

        var playCountText = playCount.Contains("play", StringComparison.OrdinalIgnoreCase)
            ? playCount
            : $"{playCount} plays";
        CompactSubtitle.Text = string.IsNullOrWhiteSpace(artist)
            ? playCountText
            : $"{artist} · {playCountText}";
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
            _rowArtistsSignature = null;
            RowArtistsHost.Children.Clear();
            RowAlbumLink.Content = "";
            ApplyRowProgress(null);
            UpdateBadgePlacement();
            if (!PreserveImageOnUnload)
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
        // Diagnostic: trace null-URL invocations on already-loaded rows
        // (the broken-tile pattern). Filter on "[TrackItem.compactArt]".
        if (imageUrl is null && !string.IsNullOrEmpty(_boundCompactImageUrl))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TrackItem.compactArt] NULL URL passed while previously bound={_boundCompactImageUrl}; "
                + $"trackId={Track?.Id ?? "(null)"} title={Track?.Title ?? "(null)"} "
                + $"trackHasImg={Track?.ImageSmallUrl ?? Track?.ImageUrl ?? "(null)"} "
                + $"stack={new System.Diagnostics.StackTrace(1, false).ToString().Split('\n').FirstOrDefault()?.Trim()}");
        }

        // Lazy-realize the compact-mode CompositionImage on first use.
        EnsureCompactAlbumArtRealized();
        if (CompactAlbumArt is null) return;

        if (imageUrl == _boundCompactImageUrl &&
            !string.IsNullOrEmpty(CompactAlbumArt.ImageUrl) &&
            CompactAlbumArt.Visibility == Visibility.Visible)
        {
            CompactAlbumArt.Visibility = Visibility.Visible;
            CompactAlbumArt.Opacity = 1;
            return;
        }

        // Null URL on an already-painted row is a TRANSIENT state during
        // lazy-track-item property updates or x:Bind Update() flushes — the
        // actual data still has an image. Clearing here drops the cache pin,
        // the surface can get evicted, and the row stays blank forever even
        // after a real URL re-arrives (the late re-set would race with another
        // recycle and could miss). Keep the existing image; the next real URL
        // overwrites it cleanly.
        if (string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(CompactAlbumArt.ImageUrl))
        {
            return;
        }

        bool urlChanged = imageUrl != _boundCompactImageUrl;
        _boundCompactImageUrl = imageUrl;
        if (urlChanged) _retriedCompactUrl = null;
        CompactAlbumArt.Visibility = Visibility.Visible;

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            CompactAlbumArt.ImageUrl = null;
            return;
        }

        // CompositionImage handles pin/unpin and the LRU race internally.
        CompactAlbumArt.ImageUrl = httpsUrl;
        CompactAlbumArt.Opacity = 1;
    }

    private void ApplyRowAlbumArt(string? imageUrl)
    {
        EnsureRowAlbumArtRealized();
        if (RowAlbumArt is null) return;

        // Same transient-null protection as ApplyCompactAlbumArt above.
        if (string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(RowAlbumArt.ImageUrl))
        {
            return;
        }

        if (imageUrl == _boundRowImageUrl &&
            !string.IsNullOrEmpty(RowAlbumArt.ImageUrl) &&
            RowAlbumArt.Visibility == Visibility.Visible)
        {
            RowAlbumArt.Visibility = Visibility.Visible;
            RowAlbumArt.Opacity = 1;
            RowArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        bool urlChanged = imageUrl != _boundRowImageUrl;
        _boundRowImageUrl = imageUrl;
        if (urlChanged) _retriedRowUrl = null;

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            RowAlbumArt.ImageUrl = null;
            RowAlbumArt.Visibility = Visibility.Collapsed;
            RowArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        // Placeholder stays visible behind the image; CompositionImage fades
        // its own placeholder out as the surface loads.
        RowArtPlaceholder.Visibility = Visibility.Visible;
        RowAlbumArt.ImageUrl = httpsUrl;
        RowAlbumArt.Opacity = 1;
        RowAlbumArt.Visibility = Visibility.Visible;
    }

    private void OnCompactAlbumArtFailed(object? sender, EventArgs e)
    {
        var url = _boundCompactImageUrl;
        if (string.IsNullOrEmpty(url)) return;
        var alreadyRetried = url == _retriedCompactUrl;
        _retriedCompactUrl = url;

        CompactAlbumArt.ImageUrl = null;
        CompactAlbumArt.Visibility = Visibility.Visible;
        CompactAlbumArt.Opacity = 1;
        if (alreadyRetried) return;

        // CompositionImage already invalidated the cache entry. Re-set the URL
        // to trigger a fresh GetOrCreate.
        DispatcherQueue?.TryEnqueue(() => ApplyCompactAlbumArt(_boundCompactImageUrl));
    }

    private void OnRowAlbumArtFailed(object? sender, EventArgs e)
    {
        var url = _boundRowImageUrl;
        if (string.IsNullOrEmpty(url)) return;
        var alreadyRetried = url == _retriedRowUrl;
        _retriedRowUrl = url;

        RowAlbumArt.ImageUrl = null;
        RowAlbumArt.Visibility = Visibility.Visible;
        RowAlbumArt.Opacity = 1;
        if (alreadyRetried) return;

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
            _boundColorHintUrl = null;
            ApplyPlaceholderColor(null);
            return;
        }

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(rawUrl);
        if (string.IsNullOrWhiteSpace(httpsUrl))
        {
            _boundColorHintUrl = null;
            ApplyPlaceholderColor(null);
            return;
        }

        if (string.Equals(_boundColorHintUrl, httpsUrl, StringComparison.Ordinal))
            return;
        _boundColorHintUrl = httpsUrl;

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
            if (!string.IsNullOrEmpty(_boundCompactImageUrl) && string.IsNullOrEmpty(CompactAlbumArt?.ImageUrl))
                ApplyCompactAlbumArt(_boundCompactImageUrl);
        }
        else
        {
            if (!string.IsNullOrEmpty(_boundRowImageUrl) && string.IsNullOrEmpty(RowAlbumArt?.ImageUrl))
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

    private static void OnShowPlayCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        if (item.Mode == TrackItemDisplayMode.Row && item._batchUpdateDepth == 0)
            item.ApplyRowColumnVisibility();
        item.UpdateCompactSubtitleText();
    }

    private static void OnDateAddedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // RowDateAdded lives inside RowRoot, which is x:Load-deferred. Skip
        // when Mode is Compact; BindRowData repopulates this when the row
        // realizes if needed.
        if (item.RowDateAdded is not null)
            item.RowDateAdded.Text = (string?)e.NewValue ?? "";
    }

    private static void OnPlayCountTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // RowPlayCount lives inside the x:Load-deferred RowRoot subtree.
        if (item.RowPlayCount is not null)
            item.RowPlayCount.Text = (string?)e.NewValue ?? "";
        item.UpdateCompactSubtitleText();
    }

    private static void OnShowPopularityBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // RowPopularityBadge lives inside the x:Load-deferred RowRoot subtree.
        if (item.RowPopularityBadge is null) return;
        item.RowPopularityBadge.Visibility = (bool)e.NewValue && item.Mode == TrackItemDisplayMode.Row
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void OnAddedByTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        // RowAddedByText / RowAddedByAvatar / RowAddedByCell live inside the
        // x:Load-deferred RowRoot subtree — skip when Compact-mode hosts set
        // the DP before / without ever realizing the Row subtree.
        if (item.RowAddedByText is null) return;
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
        // RowAddedByAvatar lives inside the x:Load-deferred RowRoot subtree.
        if (item.RowAddedByAvatar is null) return;
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
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url) ?? url;
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
        RowTitleColDef.MaxWidth   = ResolveColumnMaxWidth(TitleColumnMaxWidth, RowTitleColDef.MinWidth);
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
        ShimTitleColDef.MaxWidth  = RowTitleColDef.MaxWidth;
        ShimAlbumColDef.Width     = RowAlbumColDef.Width;
        ShimAddedByColDef.Width   = RowAddedByColDef.Width;
        ShimDateColDef.Width      = RowDateColDef.Width;
        ShimPlayCountColDef.Width = RowPlayCountColDef.Width;
        ShimDurationColDef.Width  = RowDurationColDef.Width;

        // Subline visibility just changed (artist link) — re-evaluate whether the
        // explicit/video badges should sit on the subline or inline beside the title.
        UpdateBadgePlacement();
    }

    private static double ResolveColumnMaxWidth(double value, double minWidth)
        => double.IsNaN(value) || double.IsInfinity(value)
            ? double.PositiveInfinity
            : Math.Max(minWidth, value);

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
            // CompactAlbumArt is x:Load-deferred behind IsCompactMode. When
            // the LazyTrackItem fires IsLoading before x:Bind has propagated
            // the realize, the named field can still be null on the very
            // first state apply. Force-realize, then guard.
            EnsureCompactAlbumArtRealized();
            if (CompactAlbumArt != null)
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
            CompactBorder.Background = TransparentBrush;
            CompactBorder.BorderBrush = TransparentBrush;
            CompactBorder.BorderThickness = new Thickness(1);
        }
    }

    private void ApplyRowBackground()
    {
        if (RowRoot == null) return;

        bool nativePillShowing = IsSelected || _isHovered;

        // Opt-in hover-tint: paint the configured hover brush and short-circuit.
        // Border collapses to invisible so the hover slab reads as a single block.
        if (_isHovered && !IsSelected && RowHoverBackgroundBrush is not null)
        {
            RowRoot.Background = RowHoverBackgroundBrush;
            RowRoot.BorderThickness = new Thickness(1);
            RowRoot.BorderBrush = TransparentBrush;
            return;
        }

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
        // Virtualized/recycled rows can be unloaded while LazyTrackItem.Data is
        // patched with late metadata such as artist top-track cover art. Since
        // we stop observing on Unloaded, force a full bind from the current
        // Track on Loaded instead of only restoring the previously-bound image
        // URL. This keeps recycled rows from staying on placeholders until a
        // resize or layout refresh prepares them again.
        //
        // Clear the bound-image cache before rebinding so the Apply*AlbumArt
        // dedup ("imageUrl == _bound*ImageUrl") doesn't short-circuit the
        // re-apply. With PreserveImageOnUnload=true (artist top tracks,
        // recommended songs), the previous track's URL was still in the
        // cache from before Unload — recycled rows that drew a different
        // (or no) image during the unload window would skip the re-push
        // and stay on the wrong art. CompositionImage's own surface cache
        // handles the actual dedup, so re-pushing an identical URL is free.
        _boundCompactImageUrl = null;
        _boundRowImageUrl = null;
        RebindObservedTrack();
        RepinVisibleAlbumArt();
        UpdateBadgePlacement();
        RefreshLikedState();

        // Re-sync playback state from the global tracker. Without this, a row
        // that was unloaded while showing a buffering ring (e.g. user clicked
        // play, scrolled away, scrolled back) would keep _isBuffering = true
        // until the next global PropertyChanged broadcast — which only fires
        // on state transitions, so the ring could remain stuck across many
        // rows after rapid plays in the artist top-tracks grid.
        RefreshPlaybackState();
        UpdateOverlayState();

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

        HookAddToPlaylistSession();
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
        // CompactHeartButton / RowHeartButton are inside x:Load-deferred subtrees,
        // so the inactive mode's reference is null. Update whichever is realized.
        if (CompactHeartButton is not null) CompactHeartButton.IsLiked = isLiked;
        if (RowHeartButton is not null) RowHeartButton.IsLiked = isLiked;
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
        if (CompactHeartButton is not null) CompactHeartButton.IsLiked = isLiked;
        if (RowHeartButton is not null) RowHeartButton.IsLiked = isLiked;
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
            CancelLocalBufferingTimeout();
            StopPendingBeam();
            return;
        }

        var wasBuffering = _isBuffering;
        var isThisTrack = track.Id == TrackStateBehavior.CurrentTrackId;
        _isThisTrackPlaying = isThisTrack && TrackStateBehavior.IsCurrentlyPlaying;
        _isThisTrackPaused = isThisTrack && !TrackStateBehavior.IsCurrentlyPlaying;
        _isBuffering = track.Id == TrackStateBehavior.BufferingTrackId
                       && TrackStateBehavior.IsCurrentlyBuffering;

        if (!_isBuffering)
            CancelLocalBufferingTimeout();

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
            CompactPlayButton.Opacity = 0;
            CompactPlayButton.Visibility = Visibility.Collapsed;
            CompactPlayButton.IsHitTestVisible = false;

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

            CompactPlayButton.Visibility = Visibility.Visible;
            CompactPlayButton.IsHitTestVisible = true;
            if (CompactPlayButton.Opacity < 0.99)
            {
                AnimationBuilder.Create()
                    .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(100))
                    .Start(CompactPlayButton);
            }
        }
        else
        {
            CompactPlayButton.IsHitTestVisible = false;
            if (CompactPlayButton.Visibility == Visibility.Visible && CompactPlayButton.Opacity > 0.01)
            {
                AnimationBuilder.Create()
                    .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(85))
                    .Start(CompactPlayButton);
                _ = CollapseCompactPlayButtonAfterDelayAsync(90);
            }
            else
            {
                CompactPlayButton.Opacity = 0;
                CompactPlayButton.Visibility = Visibility.Collapsed;
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

    private async Task CollapseCompactPlayButtonAfterDelayAsync(int delayMs)
    {
        await Task.Delay(delayMs);
        if (!_isHovered && !_isBuffering && CompactPlayButton.Opacity <= 0.05)
        {
            CompactPlayButton.Opacity = 0;
            CompactPlayButton.Visibility = Visibility.Collapsed;
            CompactPlayButton.IsHitTestVisible = false;
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

    private void StartLocalBufferingTimeout(string trackId)
    {
        CancelLocalBufferingTimeout();

        var cts = new CancellationTokenSource();
        _localBufferingTimeoutCts = cts;
        _localBufferingTimeoutTrackId = trackId;
        _ = ClearLocalBufferingAfterTimeoutAsync(trackId, cts.Token);
    }

    private async Task ClearLocalBufferingAfterTimeoutAsync(string trackId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(OptimisticPlayPendingTimeoutMs, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(() =>
        {
            if (ct.IsCancellationRequested
                || !_isBuffering
                || !string.Equals(_localBufferingTimeoutTrackId, trackId, StringComparison.Ordinal)
                || !string.Equals(Track?.Id, trackId, StringComparison.Ordinal))
            {
                return;
            }

            if (TrackStateBehavior.IsCurrentlyBuffering
                && string.Equals(TrackStateBehavior.BufferingTrackId, trackId, StringComparison.Ordinal))
            {
                return;
            }

            CancelLocalBufferingTimeout();
            _isBuffering = false;
            UpdateOverlayState();
        });
    }

    private void CancelLocalBufferingTimeout()
    {
        var cts = _localBufferingTimeoutCts;
        _localBufferingTimeoutCts = null;
        _localBufferingTimeoutTrackId = null;

        if (cts is null)
            return;

        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
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

        return string.IsNullOrEmpty(track.Id)
            ? null
            : SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, track.Id);
    }

    private static bool IsSpotifyEpisodeUri(string? uri)
        => SpotifyUriHelper.IsKind(uri, SpotifyEntityKind.Episode);

    private bool IsCurrentPlaybackVideoTrack(ITrackItem track)
    {
        if (_playbackStateService?.CurrentTrackIsVideo != true)
            return false;

        var currentTrackId = _playbackStateService.CurrentTrackId;
        if (string.IsNullOrEmpty(currentTrackId))
            return false;

        var currentTrackUri = currentTrackId.Contains(':', StringComparison.Ordinal)
            ? currentTrackId
            : SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, currentTrackId);

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

        // Drop stale playback state so a recycled container can't paint a
        // buffering / now-playing visual on the next track it's bound to
        // before the bind path has had a chance to re-evaluate.
        _isThisTrackPlaying = false;
        _isThisTrackPaused = false;
        _isBuffering = false;
        CancelLocalBufferingTimeout();
        if (CompactBufferingRing is not null)
        {
            CompactBufferingRing.IsActive = false;
            CompactBufferingRing.Visibility = Visibility.Collapsed;
        }
        if (RowBufferingRing is not null)
        {
            RowBufferingRing.IsActive = false;
            RowBufferingRing.Visibility = Visibility.Collapsed;
        }
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

        UnhookAddToPlaylistSession();

        if (PreserveImageOnUnload)
            return;

        // CompositionImage releases its own pin on Unloaded — don't clear
        // ImageUrl here. The same-DataContext scroll-back path was the
        // reason this used to set Source = null in the BitmapImage era;
        // with surfaces, leaving ImageUrl intact lets the inner Composition
        // visual repaint immediately on re-attach since the cache still
        // holds the surface for any URL that was visible recently.
    }

    private void RepinVisibleAlbumArt()
    {
        // CompositionImage manages its own pin lifecycle via OnLoaded/OnUnloaded,
        // so this method is a no-op in the surface-backed pipeline.
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
        if (string.IsNullOrEmpty(propertyName) || propertyName == "Data")
        {
            RebindObservedTrack();
            return;
        }

        var track = Track;
        switch (propertyName)
        {
            case nameof(ITrackItem.IsLoaded):
                SyncLoadingStateFromTrack();
                UpdateOverlayState();
                return;

            case nameof(ITrackItem.IsLiked):
                if (track is not null)
                {
                    var likedState = GetTrackLikedState(track);
                    if (CompactHeartButton is not null) CompactHeartButton.IsLiked = likedState;
                    if (RowHeartButton is not null) RowHeartButton.IsLiked = likedState;
                }
                return;

            case nameof(ITrackItem.HasVideo):
            case nameof(ITrackItem.IsExplicit):
                UpdateBadgePlacement();
                return;

            case nameof(ITrackItem.PlaybackProgress):
            case nameof(ITrackItem.PlaybackProgressText):
            case nameof(ITrackItem.HasPlaybackProgressError):
                if (Mode == TrackItemDisplayMode.Row)
                    ApplyRowProgress(track);
                return;

            case nameof(ITrackItem.ImageUrl):
            case nameof(ITrackItem.ImageSmallUrl):
                if (Mode == TrackItemDisplayMode.Compact)
                    ApplyCompactAlbumArt(track?.ImageSmallUrl ?? track?.ImageUrl);
                else
                    ApplyRowAlbumArt(track?.ImageSmallUrl ?? track?.ImageUrl);
                ResolveImageColorHint();
                return;

            case nameof(ITrackItem.Title):
                if (Mode == TrackItemDisplayMode.Compact)
                    CompactTitle.Text = track?.Title ?? "";
                else
                    RowTitle.Text = track?.Title ?? "";
                UpdateOverlayState();
                return;

            case nameof(ITrackItem.ArtistName):
            case nameof(ITrackItem.ArtistId):
            case nameof(ITrackItem.Artists):
                if (Mode == TrackItemDisplayMode.Compact)
                {
                    CompactSubtitle.Text = track?.ArtistName ?? "";
                }
                else if (track is not null)
                {
                    var artistName = track.ArtistName ?? "";
                    RebuildArtistsSubline(track);
                    RowArtistsHost.Visibility = (ShowArtistColumn && !ShowProgress && !string.IsNullOrEmpty(artistName))
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    UpdateBadgePlacement();
                }
                return;

            case nameof(ITrackItem.AlbumName):
            case nameof(ITrackItem.AlbumId):
                if (Mode == TrackItemDisplayMode.Row)
                {
                    RowAlbumLink.Content = track?.AlbumName ?? "";
                    RowAlbumLink.Tag = track?.AlbumId;
                }
                return;

            case nameof(ITrackItem.Duration):
            case nameof(ITrackItem.DurationFormatted):
                if (Mode == TrackItemDisplayMode.Compact)
                    CompactDuration.Text = track?.DurationFormatted ?? "";
                else
                    RowDuration.Text = track?.DurationFormatted ?? "";
                return;

            case nameof(ITrackItem.OriginalIndex):
                if (Mode == TrackItemDisplayMode.Row)
                {
                    RowIndexText.Text = track?.OriginalIndex > 0
                        ? track.OriginalIndex.ToString()
                        : RowIndex > 0 ? RowIndex.ToString() : "";
                }
                return;

            case nameof(ITrackItem.IsLocal):
                if (Mode == TrackItemDisplayMode.Compact)
                    CompactLocalBadge.Visibility = track?.IsLocal == true ? Visibility.Visible : Visibility.Collapsed;
                else
                    RowLocalBadge.Visibility = track?.IsLocal == true ? Visibility.Visible : Visibility.Collapsed;
                return;
        }

        if (IsTrackContentProperty(propertyName))
            RebindObservedTrack();
    }

    private void RebindObservedTrack()
    {
        SyncLoadingStateFromTrack();
        BindTrackData();
        ResolveImageColorHint();
        RefreshPlaybackState();
        UpdateOverlayState();
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
        nameof(ITrackItem.ImageSmallUrl) => true,
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
        nameof(ITrackItem.Artists) => true,
        "Data" => true,
        _ => false,
    };

    // Places the explicit + video badges in the right slot for the current row layout.
    // Row mode has two slots: the subline (alongside the artist link) and an inline
    // slot beside the title. When the subline is hidden (album page, XS density,
    // missing artist) the inline slot is used so badges don't float on an empty row.
    // Compact mode always has the artist subtitle, so badges always go on the subline.
    //
    // CompactBorder and RowRoot are x:Load-deferred behind IsCompactMode / IsRowMode,
    // so the inactive mode's named fields are null. Branch on Mode and only touch
    // the realized subtree's elements.
    private void UpdateBadgePlacement()
    {
        var track = Track;
        var hasVideo = track?.HasVideo == true;
        var isExplicit = track?.IsExplicit == true;

        if (Mode == TrackItemDisplayMode.Compact)
        {
            // Compact: subtitle is the artist text and is always present when bound.
            CompactExplicit.Visibility = isExplicit ? Visibility.Visible : Visibility.Collapsed;
            CompactVideoBadge.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
            var compactHasSubtitle = !string.IsNullOrWhiteSpace(track?.ArtistName);
            CompactVideoSeparator.Visibility = (hasVideo && compactHasSubtitle)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else
        {
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
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        // Don't handle taps on interactive elements (buttons, links)
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
            return;
        if (IsCtrlOrShiftDown())
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
        if (IsCtrlOrShiftDown())
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
            StartLocalBufferingTimeout(track.Id);
            UpdateOverlayState();
        }

        PlayCommand.Execute(track);
    }

    private static bool IsCtrlOrShiftDown()
    {
        try
        {
            var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            const Windows.UI.Core.CoreVirtualKeyStates down = Windows.UI.Core.CoreVirtualKeyStates.Down;
            return (ctrlState & down) == down || (shiftState & down) == down;
        }
        catch
        {
            return false;
        }
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
        var signature = BuildArtistsSignature(track);
        if (string.Equals(signature, _rowArtistsSignature, StringComparison.Ordinal))
            return;

        _rowArtistsSignature = signature;
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

    private static string BuildArtistsSignature(ITrackItem track)
    {
        var artists = track.Artists;
        if (artists == null || artists.Count == 0)
            return $"{track.ArtistName}|{track.ArtistId}";

        var sb = new StringBuilder(artists.Count * 32);
        for (var i = 0; i < artists.Count; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(artists[i].Name).Append('@').Append(artists[i].Uri);
        }
        return sb.ToString();
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

