using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.Models;
using Wavee.UI.Services;
using Wavee.UI.WinUI.Behaviors.Track;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Controls.Track.Behaviors;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;
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
///
/// Split across several partials for source clarity (every partial still
/// participates in the same compiled class — virtualized rows pay no extra
/// realize / recycle cost):
///   * <c>TrackItem.xaml.cs</c>           — DPs, ctor, lifecycle, track binding
///   * <c>TrackItem.ModeAndLoading.cs</c> — Compact/Row switching + loading shimmer
///   * <c>TrackItem.Hover.cs</c>          — pointer-enter reveal + selection backgrounds
///   * <c>TrackItem.Playback.cs</c>       — now-playing / buffering overlay state machine
///   * <c>TrackItem.Click.cs</c>          — tap-to-play, heart, artist / album links
///   * <c>TrackItem.AddToPlaylist.cs</c>  — app-wide "+ / check" affordance
/// </summary>
public sealed partial class TrackItem : UserControl
{
    private const int OptimisticPlayPendingTimeoutMs = 8000;

    [System.Diagnostics.Conditional("WAVEE_COMPACT_TRACK_DIAGNOSTICS")]
    private static void CompactDiag(string message) => System.Diagnostics.Debug.WriteLine(message);

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

    /// <summary>
    /// When true the host track list is in multi-select mode: this row shows a
    /// persistent checkbox, tap-to-play is suppressed (tap toggles selection),
    /// and the index / play-button / equalizer cell content is hidden in favour
    /// of the checkbox. Pushed onto every realized row by <c>TrackDataGrid</c>.
    /// </summary>
    public static readonly DependencyProperty IsSelectionModeProperty =
        DependencyProperty.Register(nameof(IsSelectionMode), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false, OnIsSelectionModeChanged));

    private static void OnIsSelectionModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (TrackItem)d;
        item._selectionPointerHandled = false;
        item.UpdateSelectionAffordance();
        item.UpdateOverlayState();
    }

    /// <summary>
    /// When true this row's host supports selection mode, so the right-click
    /// menu offers a "Select" entry. Set by <c>TrackDataGrid</c>; left false on
    /// surfaces (search cards, etc.) that don't host multi-select.
    /// </summary>
    public static readonly DependencyProperty SupportsSelectionModeProperty =
        DependencyProperty.Register(nameof(SupportsSelectionMode), typeof(bool), typeof(TrackItem),
            new PropertyMetadata(false));

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

    public bool IsSelectionMode
    {
        get => (bool)GetValue(IsSelectionModeProperty);
        set => SetValue(IsSelectionModeProperty, value);
    }

    public bool SupportsSelectionMode
    {
        get => (bool)GetValue(SupportsSelectionModeProperty);
        set => SetValue(SupportsSelectionModeProperty, value);
    }

    #endregion

    #region Events

    public event EventHandler<string>? ArtistClicked;
    public event EventHandler<string>? AlbumClicked;
    public event EventHandler? TrackChanged;

    /// <summary>Raised when the row's checkbox / tap toggles selection while in
    /// selection mode. The bool is the desired selected state. The host
    /// (<c>TrackDataGrid</c>) owns the selection — it acts on this request.</summary>
    public event EventHandler<bool>? SelectionToggleRequested;

    /// <summary>Raised when the user clicks the hover checkbox (or the "Select"
    /// context-menu item) on a row that is not yet in selection mode. The host
    /// enters selection mode and selects this row.</summary>
    public event EventHandler? EnterSelectionRequested;

    #endregion

    #region Internal State

    private bool _isHovered;
    private bool _isAlternateRow;
    private bool _useCardRow;
    private readonly ThemeColorService? _themeColors = Ioc.Default.GetService<ThemeColorService>();
    private readonly ITrackLikeService? _likeService = Ioc.Default.GetService<ITrackLikeService>();
    private readonly IContentFilterService? _contentFilter = Ioc.Default.GetService<IContentFilterService>();
    private readonly ILogger? _logger = Ioc.Default.GetService<ILogger<TrackItem>>();
    private readonly IPlaybackStateService? _playbackStateService = Ioc.Default.GetService<IPlaybackStateService>();
    private readonly IMusicVideoMetadataService? _musicVideoMetadata = Ioc.Default.GetService<IMusicVideoMetadataService>();
    private static ISettingsService? _cachedSettingsService;
    private bool _isThisTrackPlaying;
    private bool _isThisTrackPaused;
    private bool _isBuffering;
    private CancellationTokenSource? _localBufferingTimeoutCts;
    private string? _localBufferingTimeoutTrackId;
    private string? _boundCompactImageUrl;
    private string? _boundRowImageUrl;
    private string? _lastNonEmptyCompactImageUrl;
    private string? _lastNonEmptyRowImageUrl;
    private ITrackItem? _observedTrack;
    private bool _isMessengerRegistered;
    private bool _isSaveStateSubscribed;
    private bool _isContentFilterSubscribed;
    private string? _rowArtistsSignature;

    #endregion

    public TrackItem()
    {
        InitializeComponent();

        // Hover tracking
        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;

        // Ensure playback bridge is initialized (idempotent)
        TrackStateBehavior.EnsurePlaybackSubscription();
        ActualThemeChanged += OnActualThemeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Default DP value for Mode is Row, so x:Load realizes RowRoot for
        // this instance. InitializeComponent evaluates x:Bind for x:Load, but
        // realization can be deferred to the next layout pass — force it now
        // so WireRowHandlers' element references are non-null.
        if (RowRoot is null) FindName(nameof(RowRoot));
        WireRowHandlers();

        // Selection-mode row clicks need to run before ItemsView mutates the
        // native selection set, otherwise the tap handler sees the post-click
        // IsSelected state and toggles the wrong way.
        AddHandler(
            UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(OnSelectionPointerPressed),
            handledEventsToo: true);

        // Tap-to-play (respects TrackClickBehavior setting)
        Tapped += OnTapped;
        DoubleTapped += OnDoubleTapped;

        // Context menu. Register with handledEventsToo so compact-mode child
        // buttons (hover play, heart, trailing more) cannot accidentally make
        // right-click/hold feel dead depending on the exact hit target.
        AddHandler(
            UIElement.RightTappedEvent,
            new Microsoft.UI.Xaml.Input.RightTappedEventHandler(OnRightTapped),
            handledEventsToo: true);
        AddHandler(
            UIElement.HoldingEvent,
            new Microsoft.UI.Xaml.Input.HoldingEventHandler(OnHolding),
            handledEventsToo: true);

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
        if (CompactMoreButton is not null)
            CompactMoreButton.Click += OnCompactMoreButtonClick;
        _compactHandlersWired = true;
    }

    private void WireRowHandlers()
    {
        if (_rowHandlersWired) return;
        if (RowHeartButton is null || RowPlayButton is null || RowAlbumLink is null) return;
        RowHeartButton.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(OnHeartClicked);
        RowPlayButton.Click += OnPlayButtonClick;
        RowAlbumLink.Click += OnAlbumLinkClick;
        if (RowMoreButton is not null)
            RowMoreButton.Click += OnRowMoreButtonClick;
        if (RowSelectCheckBox is not null)
        {
            RowSelectCheckBox.Checked += RowSelectCheckBox_Toggled;
            RowSelectCheckBox.Unchecked += RowSelectCheckBox_Toggled;
            // Mark the tap handled so it never reaches the ItemContainer, which
            // would otherwise run its native Extended-mode select-replace and
            // wipe the rest of the multi-selection.
            RowSelectCheckBox.Tapped += RowSelectCheckBox_Tapped;
        }
        _rowHandlersWired = true;
    }

    // ── Lazy realize: inactive-mode CompositionImage subtree ──
    // Subscription latch is per-element in TrackImageRetryBehavior; these
    // booleans guard against re-wiring within the same realized subtree.

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
            // Single-retry-per-URL semantics live in the behavior; the
            // callback re-enters this control's Apply* path so cache invalidation
            // and dedup re-run.
            TrackImageRetryBehavior.Attach(CompactAlbumArt, failedUrl =>
            {
                _boundCompactImageUrl = null;
                ApplyCompactAlbumArt(_lastNonEmptyCompactImageUrl ?? failedUrl);
            });
            _compactAlbumArtSubscribed = true;
        }
    }

    private void EnsureRowAlbumArtRealized()
    {
        if (RowRoot is null) FindName(nameof(RowRoot));
        if (RowAlbumArt is null) FindName(nameof(RowAlbumArt));
        if (!_rowAlbumArtSubscribed && RowAlbumArt is not null)
        {
            TrackImageRetryBehavior.Attach(RowAlbumArt, failedUrl =>
            {
                _boundRowImageUrl = null;
                ApplyRowAlbumArt(_lastNonEmptyRowImageUrl ?? failedUrl);
            });
            _rowAlbumArtSubscribed = true;
        }
    }

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
        item.UpdateSelectionAffordance();
        item.RefreshHiddenState();
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
            var artUrl = track.ImageSmallUrl ?? track.ImageUrl;
            CompactDiag(
                $"[CompactDiag] BindCompactData id={track.Id} title={track.Title ?? "(null)"} "
                + $"smallUrl={(track.ImageSmallUrl ?? "(null)")} url={(track.ImageUrl ?? "(null)")} "
                + $"isLoadedFE={IsLoaded} attached={(CompactAlbumArt is null ? "(null-art)" : CompactAlbumArt.IsLoaded.ToString())} "
                + $"boundUrl={_boundCompactImageUrl ?? "(null)"} compactImageDp={(CompactAlbumArt?.ImageUrl ?? "(null)")}");
            ApplyCompactAlbumArt(artUrl);
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
        CompactSubtitle.Text = artist;

        if (CompactPlayCount == null)
            return;

        var playCount = ShowPlayCount ? PlayCountText : null;
        if (string.IsNullOrWhiteSpace(playCount))
        {
            CompactPlayCount.Text = "";
            CompactPlayCount.Visibility = Visibility.Collapsed;
            return;
        }

        CompactPlayCount.Text = playCount.Contains("play", StringComparison.OrdinalIgnoreCase)
            ? playCount
            : $"{playCount} plays";
        CompactPlayCount.Visibility = Visibility.Visible;
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
                RowChartStatusGlyph.Foreground = ResolveTrackBrush("SystemFillColorSuccessBrush");
                RowChartStatusDelta.Foreground = ResolveTrackBrush("SystemFillColorSuccessBrush");
                RowChartStatusDelta.Text = info.Delta is > 0
                    ? info.Delta!.Value.ToString()
                    : string.Empty;
                break;
            case ChartStatus.Down:
                RowChartStatusGlyph.Glyph = FluentGlyphs.ChartDown;
                RowChartStatusGlyph.Foreground = ResolveTrackBrush("SystemFillColorCriticalBrush");
                RowChartStatusDelta.Foreground = ResolveTrackBrush("SystemFillColorCriticalBrush");
                RowChartStatusDelta.Text = info.Delta is < 0
                    ? (-info.Delta!.Value).ToString()
                    : string.Empty;
                break;
            case ChartStatus.Equal:
                RowChartStatusGlyph.Glyph = FluentGlyphs.ChartEqual;
                RowChartStatusGlyph.Foreground = ResolveTrackBrush("TextFillColorTertiaryBrush");
                RowChartStatusDelta.Text = string.Empty;
                break;
            case ChartStatus.New:
                RowChartStatusGlyph.Visibility = Visibility.Collapsed;
                RowChartStatusDelta.Foreground = ResolveTrackBrush("AccentTextFillColorPrimaryBrush");
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
            ? ResolveTrackBrush("SystemFillColorCriticalBrush")
            : ResolveTrackBrush("TextFillColorSecondaryBrush");
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
        if (string.IsNullOrEmpty(imageUrl) && PreserveImageOnUnload)
            imageUrl = _lastNonEmptyCompactImageUrl;

        // Diagnostic: trace null-URL invocations on already-loaded rows
        // (the broken-tile pattern). Filter on "[TrackItem.compactArt]".
        if (imageUrl is null && !string.IsNullOrEmpty(_boundCompactImageUrl))
        {
            CompactDiag(
                $"[TrackItem.compactArt] NULL URL passed while previously bound={_boundCompactImageUrl}; "
                + $"trackId={Track?.Id ?? "(null)"} title={Track?.Title ?? "(null)"} "
                + $"trackHasImg={Track?.ImageSmallUrl ?? Track?.ImageUrl ?? "(null)"} "
                + $"stack={new System.Diagnostics.StackTrace(1, false).ToString().Split('\n').FirstOrDefault()?.Trim()}");
        }

        // Lazy-realize the compact-mode CompositionImage on first use.
        EnsureCompactAlbumArtRealized();
        CompactDiag(
            $"[CompactDiag] ApplyCompactAlbumArt enter url={imageUrl ?? "(null)"} "
            + $"boundUrl={_boundCompactImageUrl ?? "(null)"} "
            + $"compactArtNull={CompactAlbumArt is null} "
            + $"compactArtImageUrl={(CompactAlbumArt?.ImageUrl ?? "(null)")} "
            + $"compactArtFEIsLoaded={(CompactAlbumArt?.IsLoaded.ToString() ?? "(null)")} "
            + $"compactArtVisibility={(CompactAlbumArt?.Visibility.ToString() ?? "(null)")}");
        if (CompactAlbumArt is null) return;

        if (imageUrl == _boundCompactImageUrl &&
            !string.IsNullOrEmpty(CompactAlbumArt.ImageUrl) &&
            CompactAlbumArt.Visibility == Visibility.Visible)
        {
            CompactDiag($"[CompactDiag] ApplyCompactAlbumArt dedup-hit url={imageUrl ?? "(null)"}");
            CompactAlbumArt.Visibility = Visibility.Visible;
            CompactAlbumArt.Opacity = 1;
            CompactAlbumArt.RefreshCurrentImage();
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
        if (urlChanged) TrackImageRetryBehavior.Reset(CompactAlbumArt);
        CompactAlbumArt.Visibility = Visibility.Visible;

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            if (!PreserveImageOnUnload)
                CompactAlbumArt.ImageUrl = null;
            return;
        }

        _lastNonEmptyCompactImageUrl = imageUrl;

        // CompositionImage handles pin/unpin and the LRU race internally.
        var existingImageUrl = CompactAlbumArt.ImageUrl;
        CompactAlbumArt.ImageUrl = httpsUrl;
        CompactAlbumArt.Opacity = 1;
        if (string.Equals(existingImageUrl, httpsUrl, StringComparison.Ordinal))
            CompactAlbumArt.RefreshCurrentImage();
    }

    private void ApplyRowAlbumArt(string? imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl) && PreserveImageOnUnload)
            imageUrl = _lastNonEmptyRowImageUrl;

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
            RowAlbumArt.RefreshCurrentImage();
            RowArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        bool urlChanged = imageUrl != _boundRowImageUrl;
        _boundRowImageUrl = imageUrl;
        if (urlChanged) TrackImageRetryBehavior.Reset(RowAlbumArt);

        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl))
        {
            if (!PreserveImageOnUnload)
                RowAlbumArt.ImageUrl = null;
            RowAlbumArt.Visibility = Visibility.Collapsed;
            RowArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        _lastNonEmptyRowImageUrl = imageUrl;

        // Placeholder stays visible behind the image; CompositionImage fades
        // its own placeholder out as the surface loads.
        RowArtPlaceholder.Visibility = Visibility.Visible;
        var existingImageUrl = RowAlbumArt.ImageUrl;
        RowAlbumArt.ImageUrl = httpsUrl;
        RowAlbumArt.Opacity = 1;
        RowAlbumArt.Visibility = Visibility.Visible;
        if (string.Equals(existingImageUrl, httpsUrl, StringComparison.Ordinal))
            RowAlbumArt.RefreshCurrentImage();
    }

    private void ApplyPlaceholderColor(string? hex)
    {
        var fallback = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
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
    /// color via <see cref="Wavee.UI.Services.ITrackColorHintService"/> and applies it
    /// as the placeholder tint. Safe across virtualized-row recycling: the behavior
    /// holds a per-instance version counter and only the latest async continuation
    /// gets to paint.
    /// </summary>
    private void ResolveImageColorHint()
    {
        TrackColorHintBehavior.Resolve(
            this,
            Track?.ImageUrl,
            PlaceholderColorHex,
            UseImageColorHint,
            ApplyPlaceholderColor);
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

    // Expose RowIndex TextBlock for external access (used by the internal name)
    // RowIndexText is the x:Name from XAML - no alias needed

    #endregion

    #region Loaded / Unloaded — wiring + per-row playback rehydration

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ObserveTrack(Track);
        // Preserve the bound-image-URL cache across recycle: the dedup inside
        // ApplyCompactAlbumArt / ApplyRowAlbumArt now short-circuits the
        // Composition push when the recycled row's Track has the same image
        // as before. This is the difference between buttery scroll and a
        // visible per-row image re-flash on every fast scroll.
        //
        // Late-metadata patches (artist top-track cover-art enrichment) still
        // land correctly: they fire Track.PropertyChanged, which routes
        // through OnTrackItemPropertyChanged (subscribed in ObserveTrack) and
        // re-runs the per-property bind. The previous belt-and-braces
        // "always re-push on Loaded" was overlapping with that path.
        CompactDiag(
            $"[CompactDiag] TrackItem.OnLoaded mode={Mode} trackId={Track?.Id ?? "(null)"} "
            + $"boundUrl={_boundCompactImageUrl ?? "(null)"} "
            + $"compactArtNull={CompactAlbumArt is null} "
            + $"compactArtImageUrl={(CompactAlbumArt?.ImageUrl ?? "(null)")} "
            + $"compactArtIsLoaded={(CompactAlbumArt is null ? "(null)" : CompactAlbumArt.IsLoaded.ToString())} "
            + $"compactArtIsImageLoaded={(CompactAlbumArt?.IsImageLoaded.ToString() ?? "(null)")}");

        // Compact mode (artist top tracks) lives in a NonVirtualizingLayout
        // and pushes ApplyCompactAlbumArt during OnTrackChanged — BEFORE the
        // x:Load-deferred CompactAlbumArt is actually attached to the live
        // tree. CompositionImage.TryLoadCurrent bails at that point
        // (!_isAttached) and the load is supposed to retry from
        // CompactAlbumArt.OnLoaded. Clear the bound-URL latch here so the
        // RebindObservedTrack call below pushes a fresh URL through the
        // dedup check rather than short-circuiting — covers the case where
        // CompactAlbumArt's Loaded never fires or fires before the URL is
        // assigned. Row mode keeps the cache intact because the recycle
        // perf win there is real (playlist scrolling).
        if (Mode == TrackItemDisplayMode.Compact)
            _boundCompactImageUrl = null;
        RebindObservedTrack();
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

        if (_contentFilter != null && !_isContentFilterSubscribed)
        {
            _contentFilter.FilterChanged += OnContentFilterChanged;
            _isContentFilterSubscribed = true;
        }

        RefreshHiddenState();

        HookAddToPlaylistSession();
    }

    private void OnSaveStateChanged()
    {
        DispatcherQueue?.TryEnqueue(RefreshLikedState);
    }

    private void OnContentFilterChanged()
    {
        DispatcherQueue?.TryEnqueue(RefreshHiddenState);
    }

    /// <summary>
    /// Dims the whole row to 0.45 opacity when the bound track is on the
    /// user's hidden (Spotify <c>ban</c>) list. The orchestrator still
    /// refuses to play hidden tracks; this is the cheap visual feedback so
    /// the user knows the row IS the hidden one (not that something else is
    /// silently dropping it).
    /// </summary>
    public void RefreshHiddenState()
    {
        var track = Track;
        if (track is null || _contentFilter is null || string.IsNullOrEmpty(track.Uri))
        {
            Opacity = 1.0;
            return;
        }
        Opacity = _contentFilter.IsTrackHidden(track.Uri) ? 0.45 : 1.0;
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
            return _likeService.IsSaved(SavedItemType.Track, uri);

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

        var isLiked = _likeService.IsSaved(SavedItemType.Track, uri);
        if (CompactHeartButton is not null) CompactHeartButton.IsLiked = isLiked;
        if (RowHeartButton is not null) RowHeartButton.IsLiked = isLiked;
        expectedTrack.IsLiked = isLiked;
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

        if (_contentFilter != null && _isContentFilterSubscribed)
        {
            _contentFilter.FilterChanged -= OnContentFilterChanged;
            _isContentFilterSubscribed = false;
        }

        UnhookAddToPlaylistSession();

        // Reset the color-hint latch so a recycled row can't paint a stale
        // hex from a previous track's in-flight async continuation.
        TrackColorHintBehavior.Reset(this);

        if (PreserveImageOnUnload)
            return;

        // CompositionImage releases its own pin on Unloaded — don't clear
        // ImageUrl here. The same-DataContext scroll-back path was the
        // reason this used to set Source = null in the BitmapImage era;
        // with surfaces, leaving ImageUrl intact lets the inner Composition
        // visual repaint immediately on re-attach since the cache still
        // holds the surface for any URL that was visible recently.
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

    #endregion

    #region Context Menu

    private void OnRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        // On touch, a press-and-hold raises Holding (→ OnHolding shows the
        // menu) AND then RightTapped on release. Skip the touch RightTapped so
        // the menu opens exactly once. Mouse / pen still route through here.
        if (e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            return;

        var track = Track;
        if (track == null) return;

        ShowContextMenu(e.GetPosition(this));
        e.Handled = true;
    }

    private void OnHolding(object sender, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs e)
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
            RemoveLabel = RemoveCommandLabel,
            // Offer "Select" only on grid-hosted rows that aren't already in
            // selection mode (the multi-selection menu takes over from there).
            EnterSelectionAction = SupportsSelectionMode && !IsSelectionMode
                ? () => EnterSelectionRequested?.Invoke(this, EventArgs.Empty)
                : null
        };

        var items = TrackContextMenuBuilder.Build(track, ctx);
        ContextMenuHost.Show(this, items, position);
    }

    #endregion

    #region Cleanup

    // Unsubscribe handled inline in OnUnloaded: messenger, save-state listener,
    // add-to-playlist session subscription. TrackImageRetryBehavior and
    // TrackColorHintBehavior keep their per-element state in a
    // ConditionalWeakTable so it's collected with the control automatically.

    #endregion
}
