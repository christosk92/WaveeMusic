using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.Controls.Lyrics.Models;
using Wavee.Controls.Lyrics.Models.Lyrics;
using Wavee.UI.Contracts;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Controls.ContextMenu;
using Wavee.UI.WinUI.Controls.ContextMenu.Builders;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.DTOs;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.UI;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls.RightPanel.Details;

/// <summary>
/// Hosts the entire Details tab subtree for <see cref="RightPanelView"/> —
/// artist / podcast header card, output device picker, lyrics snippet, AI
/// meaning, biography, podcast description / chapters / comments, credits,
/// concerts, related videos — plus the Details-tab visual chrome:
/// <see cref="DetailsCanvasImage"/> (canvas video / blurred album art),
/// <see cref="CanvasLyricsOverlay"/> (composition-backed big lyrics text), and
/// <see cref="DetailsCanvasSyncBadge"/>.
/// </summary>
/// <remarks>
/// <para>
/// The parent panel's <see cref="Wavee.Controls.Lyrics.Controls.NowPlayingCanvas"/>
/// element stays in <see cref="RightPanelView"/> because it spans both columns
/// of the panel's root grid and is shared with the Lyrics tab. Everything
/// Details-specific lives here.
/// </para>
/// <para>
/// Lifetime: <see cref="InitializeDetails"/> + <see cref="TeardownDetails"/>
/// are called by the parent on Loaded / Unloaded after the host's deferred
/// subtree materialises (the parent's <c>EnsureDetailsTreeLoaded</c> path).
/// VM subscriptions (<see cref="TrackDetailsViewModel"/>, <see cref="LyricsViewModel"/>,
/// playback state) live entirely inside this host so the parent doesn't have
/// to fan out details-specific change events.
/// </para>
/// <para>
/// State flow: tab activation / panel visibility flip via DPs
/// (<see cref="IsDetailsTabActive"/>, <see cref="IsPanelVisible"/>); the host
/// raises <see cref="BackgroundChromeInvalidated"/> whenever its background mode
/// or canvas-media visibility changes so the parent can re-evaluate panel-wide
/// overlay composition.
/// </para>
/// </remarks>
public sealed partial class DetailsTabHost : UserControl
{
    // ── Constants ──
    private const float AlbumArtBlurAmount = 20f;
    private const float AlbumArtSaturationAmount = 0.88f;
    private const float CanvasSaturationAmount = 0.82f;
    private const int PodcastChapterPreviewCount = 4;

    // Details lyrics snippet sync timing
    private const double DetailsSnippetTickMs = 250;
    private const double CursorBlinkPeriodMs = 520;
    private const byte PastLyricAlpha = 118;
    private const byte HeldLyricAlpha = 188;
    private const byte ActiveLyricAlpha = 238;
    private const byte CursorLyricAlpha = 220;
    private const float DetailsOverlayFontSize = 28f;
    private const float DetailsOverlayMinHeight = 48f;
    private const float DetailsCursorWidth = 2.5f;
    private const float DetailsCursorOffsetX = 5f;
    private const float DetailsCursorTopInset = 3f;
    private const float MinVisibleCursorRegionWidth = 0.75f;

    private const int CreditsCollapsedMaxPeople = 4;

    // ── Services / VMs (resolved via IoC) ──
    private readonly TrackDetailsViewModel? _detailsVm;
    private readonly LyricsViewModel? _lyricsVm;
    private readonly LyricsAiService? _lyricsAiService;
    private readonly AiCapabilities? _aiCapabilities;
    private readonly ISettingsService? _settingsService;
    private readonly INotificationService? _notificationService;

    private bool _initialized;
    private bool _detailsSubscribed;
    private bool _detailsHadData;
    private bool _detailsWheelHandlerRegistered;

    // Podcast chapter timeline
    private DispatcherQueueTimer? _podcastChapterTimelineTimer;
    private bool _showAllPodcastChapters;
    private int _podcastChapterPreviewStart = -1;
    private string? _podcastChapterEpisodeUri;

    // Details lyrics snippet (TextBlock + in-canvas big-text overlay)
    private DispatcherQueueTimer? _detailsSnippetTimer;
    private long _lastDetailsSnippetUpdateTickMs = -1;
    private int _lastSnippetLineIndex = -1;
    private bool _canvasLyricsActive;
    private bool _detailsLyricsRenderSubscribed;
    private readonly List<CompositionObject> _detailsLyricsCompositionObjects = [];
    private ContainerVisual? _detailsLyricsContainerVisual;
    private SpriteVisual? _detailsLyricsTextVisual;
    private SpriteVisual? _detailsLyricsCursorVisual;
    private CompositionSurfaceBrush? _detailsLyricsTextBrush;
    private CompositionColorBrush? _detailsLyricsCursorBrush;
    private CompositionGraphicsDevice? _detailsLyricsGraphicsDevice;
    private CompositionDrawingSurface? _detailsLyricsDrawingSurface;
    private CanvasDevice? _detailsLyricsCanvasDevice;
    private CanvasTextLayout? _detailsLyricsTextLayout;
    private CanvasTextLayoutRegion[]? _detailsLyricsCharacterRegions;
    private string? _detailsLyricsLayoutText;
    private float _detailsLyricsLayoutWidth;
    private float _detailsLyricsLayoutHeight;

    // AI meaning state
    private CancellationTokenSource? _detailsAiMeaningCts;
    private string? _detailsAiMeaningTrackUri;
    private bool _detailsAiMeaningBusy;
    private IReadOnlyDictionary<int, LyricsAiCitation> _detailsAiCitationById =
        new Dictionary<int, LyricsAiCitation>();

    // Credits collapse
    private bool _creditsExpanded;

    // Background (None / BlurredAlbumArt / Canvas)
    private DetailsBackgroundMode _activeBackgroundMode;
    private Windows.Media.Playback.MediaPlayer? _canvasMediaPlayer;
    private string? _currentCanvasUrl;
    private string? _currentAlbumArtUrl;
    private CanvasDevice? _canvasDevice;
    private CanvasRenderTarget? _canvasFrameTarget;
    private CanvasImageSource? _canvasImageSource;
    private CanvasImageSource? _blurredAlbumArtImageSource;
    private int _detailsBackgroundGeneration;
    private readonly object _canvasFrameRenderGate = new();
    private bool _canvasFrameRenderQueued;
    private bool _canvasFramePending;
    private long _lastCanvasFrameRenderTimestamp;
    private static readonly long CanvasFrameMinIntervalTicks = Stopwatch.Frequency / 30;
    private int _blurredAlbumArtRenderWidth;
    private int _blurredAlbumArtRenderHeight;

    public DetailsTabHost()
    {
        InitializeComponent();
        _detailsVm = Ioc.Default.GetService<TrackDetailsViewModel>();
        _lyricsVm = Ioc.Default.GetService<LyricsViewModel>();
        _lyricsAiService = Ioc.Default.GetService<LyricsAiService>();
        _aiCapabilities = Ioc.Default.GetService<AiCapabilities>();
        _settingsService = Ioc.Default.GetService<ISettingsService>();
        _notificationService = Ioc.Default.GetService<INotificationService>();
    }

    // ── Dependency Properties ──

    /// <summary>
    /// True when the parent's Details tab is selected. Drives canvas play/pause,
    /// timer activity, AI meaning visibility, and the big-text overlay loop.
    /// </summary>
    public bool IsDetailsTabActive
    {
        get => (bool)GetValue(IsDetailsTabActiveProperty);
        set => SetValue(IsDetailsTabActiveProperty, value);
    }
    public static readonly DependencyProperty IsDetailsTabActiveProperty =
        DependencyProperty.Register(
            nameof(IsDetailsTabActive),
            typeof(bool),
            typeof(DetailsTabHost),
            new PropertyMetadata(false, OnIsDetailsTabActiveChanged));

    /// <summary>
    /// Mirrors the parent panel's open/closed visibility. Combined with
    /// <see cref="IsDetailsTabActive"/> to gate timers and render loops.
    /// </summary>
    public bool IsPanelVisible
    {
        get => (bool)GetValue(IsPanelVisibleProperty);
        set => SetValue(IsPanelVisibleProperty, value);
    }
    public static readonly DependencyProperty IsPanelVisibleProperty =
        DependencyProperty.Register(
            nameof(IsPanelVisible),
            typeof(bool),
            typeof(DetailsTabHost),
            new PropertyMetadata(false, OnIsPanelVisibleChanged));

    /// <summary>
    /// When the host (the floating fullscreen player) supplies its own chrome,
    /// the Details canvas image must stay hidden so it doesn't compete with
    /// the host's surface.
    /// </summary>
    public bool IsEmbeddedChromeTransparent
    {
        get => (bool)GetValue(IsEmbeddedChromeTransparentProperty);
        set => SetValue(IsEmbeddedChromeTransparentProperty, value);
    }
    public static readonly DependencyProperty IsEmbeddedChromeTransparentProperty =
        DependencyProperty.Register(
            nameof(IsEmbeddedChromeTransparent),
            typeof(bool),
            typeof(DetailsTabHost),
            new PropertyMetadata(false, OnIsEmbeddedChromeTransparentChanged));

    /// <summary>
    /// Current background mode that the host is rendering — read by the parent
    /// when it computes panel-wide overlay state.
    /// </summary>
    public DetailsBackgroundMode ActiveBackgroundMode => _activeBackgroundMode;

    /// <summary>
    /// Whether the canvas video <see cref="Image"/> is currently visible.
    /// Read by the parent when it decides whether to show the shared canvas
    /// chrome overlay.
    /// </summary>
    public bool IsCanvasMediaVisible => DetailsCanvasImage.Visibility == Visibility.Visible;

    /// <summary>
    /// Raised when anything that affects the parent's panel-wide background
    /// chrome changes — the parent listens and re-applies its overlay state.
    /// </summary>
    public event EventHandler? BackgroundChromeInvalidated;

    /// <summary>
    /// Raised when the user taps the lyrics snippet — the parent switches the
    /// active tab to Lyrics.
    /// </summary>
    public event EventHandler? LyricsSnippetTapped;

    /// <summary>
    /// Raised when the user picks a background mode from the more-options
    /// menu — the parent re-extracts the background tint to match.
    /// </summary>
    public event EventHandler? BackgroundModeChanged;

    private static void OnIsDetailsTabActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DetailsTabHost host)
            host.HandleTabActivationChanged();
    }

    private static void OnIsPanelVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DetailsTabHost host)
            host.HandlePanelVisibilityChanged();
    }

    private static void OnIsEmbeddedChromeTransparentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DetailsTabHost host)
            host.ApplyEmbeddedChromeMask();
    }

    // ── Lifecycle ──

    /// <summary>
    /// Wire VM subscriptions, register the wheel handler, and start the podcast
    /// chapter / snippet timers. Idempotent; the parent calls this after
    /// materialising the deferred host subtree.
    /// </summary>
    public void InitializeDetails()
    {
        if (_initialized) return;
        _initialized = true;
        System.Diagnostics.Debug.WriteLine("[mem] DetailsTabHost.InitializeDetails");

        if (_lyricsVm != null)
        {
            _lyricsVm.PlaybackState.PropertyChanged += OnPlaybackStateChanged;
            _lyricsVm.PropertyChanged += OnLyricsVmPropertyChanged;
        }

        if (_detailsVm != null && !_detailsSubscribed)
        {
            _detailsVm.PropertyChanged += OnDetailsVmPropertyChanged;
            _detailsSubscribed = true;
        }

        EnsurePodcastChapterTimelineTimer();
        EnsureDetailsSnippetTimer();
        RegisterDetailsWheelHandler();

        ApplyEmbeddedChromeMask();
        UpdatePanelBackgroundState();
    }

    /// <summary>
    /// Cancel all subscriptions, timers, in-flight requests, and dispose
    /// composition resources. Safe to call multiple times.
    /// </summary>
    public void TeardownDetails()
    {
        System.Diagnostics.Debug.WriteLine("[mem] DetailsTabHost.TeardownDetails");

        UnregisterDetailsWheelHandler();
        TeardownPodcastChapterTimelineTimer();
        TeardownDetailsSnippetTimer();

        if (_lyricsVm != null)
        {
            _lyricsVm.PlaybackState.PropertyChanged -= OnPlaybackStateChanged;
            _lyricsVm.PropertyChanged -= OnLyricsVmPropertyChanged;
        }

        if (_detailsVm != null && _detailsSubscribed)
        {
            _detailsVm.PropertyChanged -= OnDetailsVmPropertyChanged;
            _detailsSubscribed = false;
        }

        TeardownCanvasBackground();
        TeardownBlurredAlbumArt();
        TeardownDetailsLyricsComposition();
        TeardownDetailsLyricsSnippet();
        CancelDetailsAiMeaningRequest();

        _canvasDevice?.Dispose();
        _canvasDevice = null;
        _initialized = false;
    }

    // ── DP-driven state transitions ──

    private void HandleTabActivationChanged()
    {
        if (!_initialized) return;

        UpdatePanelBackgroundState();
        UpdateBackgroundMediaVisibility();
        ApplyCanvasLayout();

        if (!IsDetailsTabActive)
        {
            DetachDetailsLyricsRenderLoop();
            CanvasLyricsOverlay.Visibility = Visibility.Collapsed;
            _canvasLyricsActive = false;
            ClearCanvasLyricOverlay();
        }
        else
        {
            UpdateDetailsLyricsUpdateMode();
        }

        UpdateDetailsSnippetTimerState();
        UpdatePodcastChapterTimelineTimerState();

        // Kick a load when newly active — mirrors the previous parent behaviour
        // where switching to Details triggered LoadAndBindDetailsAsync.
        if (IsDetailsTabActive && IsPanelVisible && _detailsVm != null)
            _ = LoadAndBindDetailsAsync();
    }

    private void HandlePanelVisibilityChanged()
    {
        if (!_initialized) return;

        UpdateDetailsSnippetTimerState();
        UpdatePodcastChapterTimelineTimerState();
        UpdateDetailsLyricsUpdateMode();
    }

    private void ApplyEmbeddedChromeMask()
    {
        if (DetailsCanvasImage != null && IsEmbeddedChromeTransparent)
            DetailsCanvasImage.Visibility = Visibility.Collapsed;
    }

    // ── VM subscriptions ──

    private void OnLyricsVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsViewModel.CurrentLyrics)
                           or nameof(LyricsViewModel.CurrentSongInfo)
                           or nameof(LyricsViewModel.HasLyrics)
                           or nameof(LyricsViewModel.IsLoading))
        {
            if (IsDetailsTabActive && _detailsVm?.HasData == true)
                RefreshDetailsLyrics();
        }
    }

    private void OnPlaybackStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPlaybackStateService.IsPlaying))
        {
            UpdatePodcastChapterTimelineTimerState();
            UpdateDetailsSnippetTimerState();
            UpdateDetailsLyricsUpdateMode();
        }
        else if (e.PropertyName is nameof(IPlaybackStateService.Position))
        {
            UpdatePodcastChapterTimelineProgress();
        }

        if (e.PropertyName is nameof(IPlaybackStateService.CurrentTrackId)
                           or nameof(IPlaybackStateService.CurrentArtistId))
        {
            _lastDetailsSnippetUpdateTickMs = -1;
            _lastSnippetLineIndex = -1;
            // Track changes arrive AFTER CurrentAlbumArt updates (see PlaybackStateService),
            // so the in-flight color extraction kicked off by the AlbumArt change is still
            // racing here. Don't reset tint state — that would cancel the extraction and
            // strand the panel on the fallback color. Just clear the canvas treatment.
            ApplyDetailsBackground(null, false);
            UpdatePodcastChapterTimelineTimerState();
            BackgroundChromeInvalidated?.Invoke(this, EventArgs.Empty);
        }

        // Album-art changes feed the parent's tint extraction — surface via event.
        if (e.PropertyName is nameof(IPlaybackStateService.CurrentAlbumArtLarge)
                           or nameof(IPlaybackStateService.CurrentAlbumArt))
        {
            BackgroundChromeInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDetailsVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdatePanelBackgroundState();

            if (IsDetailsTabActive)
                ApplyDetailsState();
        });
    }

    // ── Details data load + apply ──

    private async Task LoadAndBindDetailsAsync()
    {
        if (_detailsVm == null) return;

        if (!_detailsSubscribed)
        {
            _detailsVm.PropertyChanged += OnDetailsVmPropertyChanged;
            _detailsSubscribed = true;
        }

        await _detailsVm.LoadDetailsAsync();
        ApplyDetailsState();
    }

    private void ApplyDetailsState()
    {
        if (_detailsVm == null) return;

        DetailsLoadingShimmer.Visibility = _detailsVm.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        DetailsErrorText.Text = _detailsVm.ErrorMessage ?? "";
        DetailsErrorText.Visibility = !string.IsNullOrEmpty(_detailsVm.ErrorMessage)
            ? Visibility.Visible : Visibility.Collapsed;

        var hasData = _detailsVm.HasData;

        // Background (none / blurred album art / canvas) — update immediately, no delay
        UpdatePanelBackgroundState();

        if (hasData)
        {
            // If we already had data showing (track change), animate the transition
            if (_detailsHadData)
                _ = AnimateDetailsContentChangeAsync();
            else
                _ = AnimateDetailsContentInAsync();
        }
        else
        {
            UpdateDetailsContent();
        }

        _detailsHadData = hasData;
    }

    /// <summary>
    /// Crossfade: fade out → update → fade in + slide up.
    /// </summary>
    private async Task AnimateDetailsContentChangeAsync()
    {
        // Fade out
        await AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(150),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseIn)
            .StartAsync(DetailsContent);

        // Update content while invisible
        UpdateDetailsContent();

        // Fade in with slide up
        await AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(250),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .Translation(Axis.Y, from: 12, to: 0, duration: TimeSpan.FromMilliseconds(250),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .StartAsync(DetailsContent);
    }

    /// <summary>
    /// Initial appear: fade in + slide up from below.
    /// </summary>
    private async Task AnimateDetailsContentInAsync()
    {
        UpdateDetailsContent();
        DetailsContent.ChangeView(null, 0, null, true);

        // Start hidden
        DetailsContent.Opacity = 0;

        // Animate in
        await AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(300),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .Translation(Axis.Y, from: 20, to: 0, duration: TimeSpan.FromMilliseconds(300),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .StartAsync(DetailsContent);
    }

    /// <summary>
    /// Synchronously applies details VM data to XAML elements.
    /// </summary>
    private void UpdateDetailsContent()
    {
        if (_detailsVm == null) return;

        DetailsContent.DataContext = _detailsVm;
        var hasData = _detailsVm.HasData;
        var isPodcast = hasData && _detailsVm.IsPodcastEpisode;

        if (isPodcast)
        {
            UpdatePodcastDetailsContent();
            return;
        }

        HidePodcastDetailsContent();

        // Artist header
        DetailsArtistHeaderCard.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        if (hasData)
        {
            DetailsArtistName.Text = _detailsVm.ArtistName ?? "";
            DetailsVerifiedIcon.Visibility = _detailsVm.IsVerified ? Visibility.Visible : Visibility.Collapsed;
            DetailsArtistStats.Text = $"{_detailsVm.Followers} followers · {_detailsVm.MonthlyListeners} monthly listeners";

            if (!string.IsNullOrEmpty(_detailsVm.ArtistAvatarUrl))
            {
                DetailsArtistAvatar.ProfilePicture = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri(_detailsVm.ArtistAvatarUrl));
            }
        }

        // Bio card (includes record label)
        var hasBio = hasData && !string.IsNullOrEmpty(_detailsVm.BiographyText);
        var hasLabel = hasData && !string.IsNullOrEmpty(_detailsVm.RecordLabel);
        DetailsBio.Visibility = (hasBio || hasLabel) ? Visibility.Visible : Visibility.Collapsed;
        if (hasBio)
        {
            DetailsBioText.Text = _detailsVm.BiographyText!;
            DetailsBioText.Visibility = Visibility.Visible;
            DetailsBioText.MaxLines = _detailsVm.IsBioExpanded ? 0 : 3;
            DetailsBioToggle.Content = _detailsVm.IsBioExpanded ? "Show less" : "Show more";
            // Toggle visibility is driven by IsTextTrimmedChanged event
            DetailsBioToggle.Visibility = _detailsVm.IsBioExpanded
                ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            DetailsBioText.Visibility = Visibility.Collapsed;
            DetailsBioToggle.Visibility = Visibility.Collapsed;
        }

        DetailsRecordLabel.Visibility = hasLabel ? Visibility.Visible : Visibility.Collapsed;
        DetailsRecordLabel.Text = hasLabel ? $"© {_detailsVm.RecordLabel}" : "";

        // Lyrics snippet (mini live canvas)
        var showLyricsSnippet = hasData && _lyricsVm?.HasLyrics == true && _lyricsVm.CurrentLyrics != null;
        DetailsLyricsSnippet.Visibility = showLyricsSnippet ? Visibility.Visible : Visibility.Collapsed;
        if (showLyricsSnippet)
            SetupDetailsLyricsSnippet();
        else
            TeardownDetailsLyricsSnippet();

        // Credits (collapsed by default — show first group only)
        SyncDetailsAiMeaningForCurrentTrack(showLyricsSnippet);

        _creditsExpanded = false;
        var hasCredits = hasData && _detailsVm.CreditGroups.Count > 0;
        DetailsCreditsSection.Visibility = hasCredits ? Visibility.Visible : Visibility.Collapsed;
        if (hasCredits)
            ApplyCreditsCollapse();

        // Concerts
        DetailsConcertsSection.Visibility = _detailsVm.HasConcerts
            ? Visibility.Visible : Visibility.Collapsed;
        DetailsConcertsList.ItemsSource = _detailsVm.Concerts;

        // Related Videos
        DetailsRelatedVideosSection.Visibility = _detailsVm.HasRelatedVideos
            ? Visibility.Visible : Visibility.Collapsed;
        DetailsRelatedVideosList.ItemsSource = _detailsVm.RelatedVideos;
        UpdateDetailsCanvasSyncBadge();
    }

    private void UpdatePodcastDetailsContent()
    {
        if (_detailsVm == null) return;

        DetailsArtistHeaderCard.Visibility = Visibility.Collapsed;
        DetailsLyricsSnippet.Visibility = Visibility.Collapsed;
        TeardownDetailsLyricsSnippet();
        DetailsAiMeaningSection.Visibility = Visibility.Collapsed;
        CancelDetailsAiMeaningRequest();
        ResetDetailsAiMeaningUi(clearText: true);
        DetailsBio.Visibility = Visibility.Collapsed;
        DetailsCreditsSection.Visibility = Visibility.Collapsed;
        DetailsConcertsSection.Visibility = Visibility.Collapsed;
        DetailsRelatedVideosSection.Visibility = Visibility.Collapsed;

        DetailsPodcastHeaderCard.Visibility = Visibility.Visible;
        DetailsPodcastTitle.Text = _detailsVm.PodcastEpisodeTitle;
        DetailsPodcastShowLine.Text = _detailsVm.PodcastShowName;
        DetailsPodcastMetaLine.Text = _detailsVm.PodcastEpisodeMetadata;

        DetailsPodcastArtwork.Source = string.IsNullOrWhiteSpace(_detailsVm.PodcastEpisodeImageUrl)
            ? null
            : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_detailsVm.PodcastEpisodeImageUrl));

        DetailsPodcastDescriptionSection.Visibility = _detailsVm.HasPodcastDescription
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailsPodcastDescriptionText.Text = _detailsVm.PodcastEpisodeDescription ?? "";
        DetailsPodcastTranscriptChip.Visibility = _detailsVm.HasPodcastTranscript
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailsPodcastTranscriptText.Text = _detailsVm.PodcastTranscriptLabel;

        DetailsPodcastChaptersSection.Visibility = _detailsVm.HasPodcastChapters
            ? Visibility.Visible
            : Visibility.Collapsed;

        var episodeUri = _detailsVm.PodcastEpisodeDetail?.Uri;
        if (!string.Equals(_podcastChapterEpisodeUri, episodeUri, StringComparison.Ordinal))
        {
            _podcastChapterEpisodeUri = episodeUri;
            _showAllPodcastChapters = false;
            _podcastChapterPreviewStart = -1;
        }

        UpdatePodcastChapterTimelineProgress();
        UpdatePodcastChaptersSource(force: true);
        UpdatePodcastChapterTimelineTimerState();

        DetailsPodcastCommentsSection.Visibility = Visibility.Visible;
        DetailsPodcastComments.HeaderText = _detailsVm.PodcastCommentsCountLabel;
        DetailsPodcastComments.Comments = _detailsVm.PodcastComments;
        DetailsPodcastComments.HasNoComments = !_detailsVm.HasPodcastComments;
        DetailsPodcastComments.HasMoreComments = _detailsVm.HasMorePodcastComments;
        DetailsPodcastComments.StatusText = _detailsVm.HasPodcastCommentStatus
            ? _detailsVm.PodcastCommentStatus
            : null;

        UpdateDetailsCanvasSyncBadge();
    }

    private void HidePodcastDetailsContent()
    {
        DetailsPodcastHeaderCard.Visibility = Visibility.Collapsed;
        DetailsPodcastArtwork.Source = null;
        DetailsPodcastDescriptionSection.Visibility = Visibility.Collapsed;
        DetailsPodcastChaptersSection.Visibility = Visibility.Collapsed;
        DetailsPodcastCommentsSection.Visibility = Visibility.Collapsed;
        DetailsPodcastChaptersList.ItemsSource = null;
        _podcastChapterPreviewStart = -1;
        UpdatePodcastChapterTimelineTimerState();
        DetailsPodcastComments.Comments = null;
    }

    private void RefreshDetailsLyrics()
    {
        var showLyricsSnippet = _lyricsVm?.HasLyrics == true && _lyricsVm.CurrentLyrics != null;
        DetailsLyricsSnippet.Visibility = showLyricsSnippet ? Visibility.Visible : Visibility.Collapsed;
        if (showLyricsSnippet)
            SetupDetailsLyricsSnippet();
        else
            TeardownDetailsLyricsSnippet();

        SyncDetailsAiMeaningForCurrentTrack(showLyricsSnippet);
        UpdateCanvasLyricsVisibility();
    }

    // ── Comments / podcast ──

    // Reactions popup — uses the shared PodcastCommentReactionsDialog helper
    // so the right panel renders the same dialog as the library episode detail.
    private async void DetailsPodcastComments_ShowReactionsRequested(
        Wavee.UI.WinUI.Controls.Comments.CommentsList sender,
        PodcastCommentViewModel comment)
    {
        if (XamlRoot is null) return;
        await PodcastCommentReactionsDialog.ShowAsync(
            XamlRoot,
            (token, reaction) => comment.GetReactionsAsync(token, reaction));
    }

    private async void DetailsPodcastComments_ShowReplyReactionsRequested(
        Wavee.UI.WinUI.Controls.Comments.CommentsList sender,
        PodcastReplyViewModel reply)
    {
        // Compact mode hides replies so this is unlikely to fire, but route to
        // the same dialog if it ever does.
        if (XamlRoot is null) return;
        await PodcastCommentReactionsDialog.ShowAsync(
            XamlRoot,
            (token, reaction) => reply.GetReactionsAsync(token, reaction));
    }

    private void DetailsPodcastChapter_Click(object sender, RoutedEventArgs e)
    {
        if (_detailsVm is null || sender is not FrameworkElement { Tag: EpisodeChapterVm chapter })
            return;

        AnimatePodcastChapterRow((FrameworkElement)sender, 0.992f, 90);
        if (_detailsVm.SeekPodcastChapterCommand.CanExecute(chapter))
            _detailsVm.SeekPodcastChapterCommand.Execute(chapter);
    }

    private void DetailsPodcastChaptersToggle_Click(object sender, RoutedEventArgs e)
    {
        _showAllPodcastChapters = !_showAllPodcastChapters;
        _podcastChapterPreviewStart = -1;
        UpdatePodcastChaptersSource(force: true);
    }

    private void DetailsPodcastChapter_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Hand));
        AnimatePodcastChapterRow(element, 1.012f, 160);
    }

    private void DetailsPodcastChapter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        element.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
        AnimatePodcastChapterRow(element, 1f, 150);
    }

    private void DetailsPodcastChapter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimatePodcastChapterRow(element, 0.992f, 80);
    }

    private void DetailsPodcastChapter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimatePodcastChapterRow(element, 1.012f, 120);
    }

    private void DetailsPodcastChapter_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            AnimatePodcastChapterRow(element, 1f, 120);
    }

    private void UpdatePodcastChapterTimelineProgress()
    {
        if (_detailsVm?.IsPodcastEpisode == true)
        {
            _detailsVm.UpdatePodcastChapterTimeline(GetPodcastTimelinePositionMs());
            UpdatePodcastChaptersSource();
        }
    }

    private double GetPodcastTimelinePositionMs()
    {
        if (_lyricsVm is not null)
            return _lyricsVm.GetInterpolatedPosition().TotalMilliseconds;

        return _detailsVm?.PlaybackState.Position ?? 0;
    }

    private void UpdatePodcastChaptersSource(bool force = false)
    {
        if (_detailsVm is null || DetailsPodcastChaptersList is null)
            return;

        var chapters = _detailsVm.PodcastChapters;
        var count = chapters.Count;
        var hasOverflow = count > PodcastChapterPreviewCount;

        DetailsPodcastChaptersToggleButton.Visibility = hasOverflow
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailsPodcastChaptersToggleButton.Content = _showAllPodcastChapters
            ? "Show less"
            : $"Show all {count}";

        if (!hasOverflow || _showAllPodcastChapters)
        {
            if (force || !ReferenceEquals(DetailsPodcastChaptersList.ItemsSource, chapters))
                DetailsPodcastChaptersList.ItemsSource = chapters;

            _podcastChapterPreviewStart = 0;
            return;
        }

        var activeIndex = -1;
        for (var i = 0; i < count; i++)
        {
            if (chapters[i].IsActive)
            {
                activeIndex = i;
                break;
            }
        }

        if (activeIndex < 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (!chapters[i].IsCompleted)
                {
                    activeIndex = i;
                    break;
                }
            }
        }

        if (activeIndex < 0)
            activeIndex = 0;

        var start = Math.Clamp(activeIndex - 1, 0, Math.Max(0, count - PodcastChapterPreviewCount));
        if (!force && start == _podcastChapterPreviewStart)
            return;

        _podcastChapterPreviewStart = start;
        DetailsPodcastChaptersList.ItemsSource = chapters
            .Skip(start)
            .Take(PodcastChapterPreviewCount)
            .ToList();
    }

    private bool ShouldRunPodcastChapterTimelineTimer()
    {
        return IsDetailsTabActive
            && IsPanelVisible
            && _detailsVm?.IsPodcastEpisode == true
            && _detailsVm.HasPodcastChapters
            && _detailsVm.PlaybackState.IsPlaying;
    }

    private void EnsurePodcastChapterTimelineTimer()
    {
        if (_podcastChapterTimelineTimer is not null)
            return;

        _podcastChapterTimelineTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _podcastChapterTimelineTimer.Interval = TimeSpan.FromMilliseconds(250);
        _podcastChapterTimelineTimer.Tick += OnPodcastChapterTimelineTimerTick;
    }

    private void OnPodcastChapterTimelineTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!ShouldRunPodcastChapterTimelineTimer())
        {
            sender.Stop();
            return;
        }

        UpdatePodcastChapterTimelineProgress();
    }

    private void UpdatePodcastChapterTimelineTimerState()
    {
        EnsurePodcastChapterTimelineTimer();
        if (ShouldRunPodcastChapterTimelineTimer())
            _podcastChapterTimelineTimer?.Start();
        else
            _podcastChapterTimelineTimer?.Stop();
    }

    private void TeardownPodcastChapterTimelineTimer()
    {
        if (_podcastChapterTimelineTimer is null)
            return;

        _podcastChapterTimelineTimer.Stop();
        _podcastChapterTimelineTimer.Tick -= OnPodcastChapterTimelineTimerTick;
        _podcastChapterTimelineTimer = null;
    }

    // ── Details lyrics snippet shared timer ──
    // Drives the TextBlock-based snippet at low frequency when the canvas
    // overlay render loop isn't running.

    private void EnsureDetailsSnippetTimer()
    {
        if (_detailsSnippetTimer is not null)
            return;

        _detailsSnippetTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _detailsSnippetTimer.Interval = TimeSpan.FromMilliseconds(DetailsSnippetTickMs);
        _detailsSnippetTimer.Tick += OnDetailsSnippetTimerTick;
    }

    private void TeardownDetailsSnippetTimer()
    {
        if (_detailsSnippetTimer is null)
            return;

        _detailsSnippetTimer.Stop();
        _detailsSnippetTimer.Tick -= OnDetailsSnippetTimerTick;
        _detailsSnippetTimer = null;
    }

    private void OnDetailsSnippetTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_lyricsVm == null || !ShouldRunDetailsLyricsSharedTimer())
        {
            sender.Stop();
            return;
        }

        var tickMs = Environment.TickCount64;
        if (_lastDetailsSnippetUpdateTickMs >= 0
            && tickMs - _lastDetailsSnippetUpdateTickMs < DetailsSnippetTickMs)
        {
            return;
        }

        _lastDetailsSnippetUpdateTickMs = tickMs;
        UpdateLyricsSnippetText();
    }

    private void UpdateDetailsSnippetTimerState()
    {
        EnsureDetailsSnippetTimer();
        if (ShouldRunDetailsLyricsSharedTimer())
            _detailsSnippetTimer?.Start();
        else
        {
            _detailsSnippetTimer?.Stop();
            _lastDetailsSnippetUpdateTickMs = -1;
        }
    }

    private static void AnimatePodcastChapterRow(FrameworkElement element, float scale, double durationMs)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(
            (float)Math.Max(0, element.ActualWidth / 2),
            (float)Math.Max(0, element.ActualHeight / 2),
            0);

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), easing);
        visual.StartAnimation(nameof(Visual.Scale), animation);
    }

    // ── Canvas sync badge ──

    private void UpdateDetailsCanvasSyncBadge()
    {
        if (DetailsCanvasSyncBadge == null)
            return;

        var show = IsDetailsTabActive
                   && _detailsVm?.HasData == true
                   && _detailsVm.HasPendingCanvasUpdate;

        DetailsCanvasSyncBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DetailsCanvasSyncBadge_Click(object sender, RoutedEventArgs e)
        => await ReviewPendingCanvasAsync();

    private async Task ReviewPendingCanvasAsync()
    {
        if (_detailsVm == null
            || !_detailsVm.HasPendingCanvasUpdate
            || XamlRoot == null)
        {
            return;
        }

        var result = await CanvasSyncReviewDialog.ShowAsync(
            XamlRoot,
            _detailsVm.CanvasUrl,
            _detailsVm.PendingCanvasUrl);

        switch (result)
        {
            case CanvasSyncReviewResult.UseNew:
                await _detailsVm.AcceptPendingCanvasUpdateAsync();
                break;

            case CanvasSyncReviewResult.KeepCurrent:
                await _detailsVm.RejectPendingCanvasUpdateAsync();
                break;
        }

        RefreshDetailsCanvasUi();
    }

    private async Task PickManualCanvasFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".webm");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".m4v");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary;
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);

            var file = await picker.PickSingleFileAsync();
            if (file == null || _detailsVm == null)
                return;

            await _detailsVm.ImportManualCanvasFileAsync(file.Path);
            _notificationService?.Show("Custom canvas applied.", NotificationSeverity.Success, TimeSpan.FromSeconds(3));
            RefreshDetailsCanvasUi();
        }
        catch (Exception ex)
        {
            _notificationService?.Show(ex.Message, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    private async Task PromptForManualCanvasUrlAsync()
    {
        if (XamlRoot == null || _detailsVm == null)
            return;

        var textBox = new TextBox
        {
            PlaceholderText = "https://example.com/canvas.mp4",
            Text = _detailsVm.IsManualCanvasOverride
                && Uri.TryCreate(_detailsVm.CanvasUrl, UriKind.Absolute, out var existingUri)
                && !existingUri.IsFile
                    ? existingUri.AbsoluteUri
                    : string.Empty,
            MinWidth = 420,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Set canvas URL",
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Paste a direct video URL to use as the Details canvas for this track.",
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                    textBox
                }
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        try
        {
            await _detailsVm.SetManualCanvasUrlAsync(textBox.Text);
            _notificationService?.Show("Custom canvas applied.", NotificationSeverity.Success, TimeSpan.FromSeconds(3));
            RefreshDetailsCanvasUi();
        }
        catch (Exception ex)
        {
            _notificationService?.Show(ex.Message, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    private async Task ResetCanvasToUpstreamAsync()
    {
        if (_detailsVm == null)
            return;

        try
        {
            await _detailsVm.ResetCanvasToUpstreamAsync();
            _notificationService?.Show("Canvas reset to Spotify.", NotificationSeverity.Success, TimeSpan.FromSeconds(3));
            RefreshDetailsCanvasUi();
        }
        catch (Exception ex)
        {
            _notificationService?.Show(ex.Message, NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    private void RefreshDetailsCanvasUi()
    {
        UpdatePanelBackgroundState();
        if (IsDetailsTabActive)
            ApplyDetailsState();
    }

    private IReadOnlyList<ContextMenuItemModel>? BuildDetailsCanvasMenuItems()
    {
        if (_detailsVm?.HasData != true) return null;

        var canvasLabel = _detailsVm.IsManualCanvasOverride ? "Custom canvas" : "Canvas";
        var canvasMenuGlyph = Wavee.UI.WinUI.Styles.FluentGlyphs.Canvas;
        var children = new List<ContextMenuItemModel>
        {
            new()
            {
                Text = "Choose file...",
                Invoke = async () => await PickManualCanvasFileAsync()
            },
            new()
            {
                Text = "Set URL...",
                Invoke = async () => await PromptForManualCanvasUrlAsync()
            }
        };

        if (_detailsVm.IsManualCanvasOverride)
        {
            children.Add(ContextMenuItemModel.Separator);
            children.Add(new ContextMenuItemModel
            {
                Text = "Reset to Spotify",
                Invoke = async () => await ResetCanvasToUpstreamAsync()
            });
        }

        if (_detailsVm.HasPendingCanvasUpdate)
        {
            children.Add(ContextMenuItemModel.Separator);
            children.Add(new ContextMenuItemModel
            {
                Text = "Review Spotify update...",
                Invoke = async () => await ReviewPendingCanvasAsync()
            });
        }

        return new[]
        {
            new ContextMenuItemModel
            {
                Text = canvasLabel,
                Glyph = canvasMenuGlyph,
                Items = children
            }
        };
    }

    // ── Lyrics snippet interaction ──

    private void DetailsLyricsSnippet_Tapped(object sender, TappedRoutedEventArgs e)
    {
        LyricsSnippetTapped?.Invoke(this, EventArgs.Empty);
    }

    private void DetailsLyricsSnippet_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ((FrameworkElement)sender).Opacity = 0.8;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private void DetailsLyricsSnippet_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ((FrameworkElement)sender).Opacity = 1.0;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    // ── AI lyrics meaning ──

    private void SyncDetailsAiMeaningForCurrentTrack(bool hasLyrics)
    {
        var canShow = hasLyrics
                      && _lyricsAiService != null
                      && _aiCapabilities?.IsLyricsSummarizeEnabled == true
                      && _lyricsVm?.CurrentLyrics != null;

        DetailsAiMeaningSection.Visibility = canShow ? Visibility.Visible : Visibility.Collapsed;
        if (!canShow)
        {
            CancelDetailsAiMeaningRequest();
            ResetDetailsAiMeaningUi(clearText: true);
            _detailsAiMeaningTrackUri = null;
            return;
        }

        var trackUri = GetCurrentTrackUriForAi();
        if (!string.Equals(_detailsAiMeaningTrackUri, trackUri, StringComparison.Ordinal))
        {
            CancelDetailsAiMeaningRequest();
            _detailsAiMeaningTrackUri = trackUri;
            ResetDetailsAiMeaningUi(clearText: true);
        }

        if (_lyricsAiService!.TryGetCachedLyricsMeaning(trackUri, out var cached))
            ApplyDetailsAiMeaningResult(cached);
        else if (!_detailsAiMeaningBusy)
            DetailsAiMeaningButton.Content = "Generate";

        DetailsAiMeaningButton.IsEnabled = !_detailsAiMeaningBusy;
    }

    private async void DetailsAiMeaningButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lyricsAiService == null
            || _aiCapabilities?.IsLyricsSummarizeEnabled != true
            || _lyricsVm?.CurrentLyrics == null)
        {
            return;
        }

        var fullText = _lyricsVm.CurrentLyrics.WrappedOriginalText;
        if (string.IsNullOrWhiteSpace(fullText))
        {
            ApplyDetailsAiMeaningResult(LyricsAiResult.Empty);
            return;
        }

        var trackUri = GetCurrentTrackUriForAi();
        _detailsAiMeaningTrackUri = trackUri;

        CancelDetailsAiMeaningRequest();
        var cts = _detailsAiMeaningCts = new CancellationTokenSource();
        SetDetailsAiMeaningBusy(true);

        try
        {
            var result = await _lyricsAiService.GetLyricsMeaningAsync(
                trackUri,
                fullText,
                deltaProgress: null,
                ct: cts.Token,
                trackTitle: _lyricsVm.CurrentSongInfo?.Title,
                artistName: _lyricsVm.CurrentSongInfo?.Artist);

            if (cts.IsCancellationRequested)
                return;

            ApplyDetailsAiMeaningResult(result);
        }
        catch (OperationCanceledException)
        {
            // Details card was hidden or the track changed.
        }
        finally
        {
            if (ReferenceEquals(_detailsAiMeaningCts, cts))
            {
                _detailsAiMeaningCts = null;
                SetDetailsAiMeaningBusy(false);
            }

            cts.Dispose();
        }
    }

    private void ApplyDetailsAiMeaningResult(LyricsAiResult result)
    {
        DetailsAiMeaningText.Visibility = Visibility.Visible;
        DetailsAiMeaningButton.Content = result.Kind == LyricsAiResultKind.Ok ? "Ready" : "Retry";

        var text = result.Kind switch
        {
            LyricsAiResultKind.Ok => result.Text,
            LyricsAiResultKind.Filtered => "The on-device safety filter blocked this lyrics meaning.",
            LyricsAiResultKind.Empty => "No lyrics are available to interpret.",
            LyricsAiResultKind.Unavailable => "On-device AI is not available right now.",
            LyricsAiResultKind.Error => "Something went wrong asking the on-device model.",
            _ => string.Empty,
        };

        if (result.HasCitations)
            RenderDetailsAiMeaningWithCitations(result);
        else
            RenderDetailsAiMeaningPlainText(text);
    }

    private void RenderDetailsAiMeaningPlainText(string text)
    {
        _detailsAiCitationById = new Dictionary<int, LyricsAiCitation>();
        DetailsAiMeaningText.Blocks.Clear();

        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = text });
        DetailsAiMeaningText.Blocks.Add(paragraph);
    }

    private void RenderDetailsAiMeaningWithCitations(LyricsAiResult result)
    {
        DetailsAiMeaningText.Blocks.Clear();
        _detailsAiCitationById = result.Citations!.ToDictionary(static citation => citation.Id);

        var paragraph = new Paragraph();
        var lastChar = '\0';
        foreach (var segment in result.Segments!)
        {
            if (string.IsNullOrEmpty(segment.Text))
                continue;

            // The model often emits self-contained segments that end and start
            // mid-sentence with no whitespace at the boundary, so insert a
            // single space Run between segments when the previous text ended
            // with a non-whitespace character and this one starts with one.
            if (lastChar != '\0'
                && !char.IsWhiteSpace(lastChar)
                && !char.IsWhiteSpace(segment.Text[0]))
            {
                paragraph.Inlines.Add(new Run { Text = " " });
            }

            if (segment.CitationId > 0
                && _detailsAiCitationById.TryGetValue(segment.CitationId, out var citation))
            {
                var cited = citation;
                var hyperlink = new Hyperlink();
                hyperlink.Inlines.Add(new Run { Text = segment.Text });
                hyperlink.Click += (_, _) => ShowDetailsAiCitationFlyout(cited);
                ToolTipService.SetToolTip(hyperlink, FormatCitationTooltip(cited));
                paragraph.Inlines.Add(hyperlink);
            }
            else
            {
                paragraph.Inlines.Add(new Run { Text = segment.Text });
            }

            lastChar = segment.Text[^1];
        }

        if (paragraph.Inlines.Count == 0)
            paragraph.Inlines.Add(new Run { Text = result.Text });

        DetailsAiMeaningText.Blocks.Add(paragraph);
    }

    private void ShowDetailsAiCitationFlyout(LyricsAiCitation citation)
    {
        if (XamlRoot is null)
            return;

        var stack = new StackPanel
        {
            Spacing = 6,
            MaxWidth = 260,
        };
        stack.Children.Add(new TextBlock
        {
            Text = FormatCitationLineRange(citation),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = citation.Summary,
            FontSize = 12,
            TextWrapping = TextWrapping.WrapWholeWords,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var flyout = new Flyout
        {
            Content = stack,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft,
            XamlRoot = XamlRoot,
        };
        flyout.ShowAt(DetailsAiMeaningText);
    }

    private static string FormatCitationTooltip(LyricsAiCitation citation)
        => FormatCitationLineRange(citation) + ": " + citation.Summary;

    private static string FormatCitationLineRange(LyricsAiCitation citation)
    {
        return citation.StartLine == citation.EndLine
            ? $"Line {citation.StartLine}"
            : $"Lines {citation.StartLine}-{citation.EndLine}";
    }

    private void SetDetailsAiMeaningBusy(bool isBusy)
    {
        _detailsAiMeaningBusy = isBusy;
        DetailsAiMeaningProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        DetailsAiMeaningButton.IsEnabled = !isBusy;
        if (isBusy)
            DetailsAiMeaningButton.Content = "Generating";
    }

    private void ResetDetailsAiMeaningUi(bool clearText)
    {
        _detailsAiMeaningBusy = false;
        DetailsAiMeaningProgress.Visibility = Visibility.Collapsed;
        DetailsAiMeaningButton.IsEnabled = true;
        DetailsAiMeaningButton.Content = "Generate";
        if (clearText)
        {
            DetailsAiMeaningText.Blocks.Clear();
            DetailsAiMeaningText.Visibility = Visibility.Collapsed;
            _detailsAiCitationById = new Dictionary<int, LyricsAiCitation>();
        }
    }

    private void CancelDetailsAiMeaningRequest()
    {
        try
        {
            _detailsAiMeaningCts?.Cancel();
        }
        catch
        {
            // Already disposed; harmless.
        }
        finally
        {
            _detailsAiMeaningCts?.Dispose();
            _detailsAiMeaningCts = null;
            _detailsAiMeaningBusy = false;
        }
    }

    private string GetCurrentTrackUriForAi()
    {
        if (!string.IsNullOrWhiteSpace(_detailsVm?.CurrentTrackUri))
            return _detailsVm.CurrentTrackUri!;

        return BuildTrackUri(_lyricsVm?.PlaybackState.CurrentTrackId);
    }

    private static string BuildTrackUri(string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, "unknown");

        var trimmed = trackId.Trim();
        return SpotifyUriHelper.IsKind(trimmed, SpotifyEntityKind.Track)
            ? trimmed
            : SpotifyUriHelper.ToUri(SpotifyEntityKind.Track, trimmed);
    }

    // ── Lyrics snippet (TextBlock + canvas overlay) ──

    private readonly record struct CanvasLyricPresentation(
        int PastCharCount,
        int HeldStartIndex,
        int HeldCharCount,
        int ActiveStartIndex,
        int ActiveVisibleCharCount,
        int CursorCharIndex,
        bool ShowCursor,
        float CursorAdvance,
        float CursorOpacity,
        byte PastAlpha,
        byte HeldAlpha,
        byte ActiveAlpha);

    private void SetupDetailsLyricsSnippet()
    {
        if (_lyricsVm == null) return;

        // Update immediately
        UpdateCanvasLyricsVisibility();
        UpdateLyricsSnippetText();
        _lastDetailsSnippetUpdateTickMs = -1;
        UpdateDetailsLyricsUpdateMode();
    }

    private void TeardownDetailsLyricsSnippet()
    {
        _lastDetailsSnippetUpdateTickMs = -1;
        _lastSnippetLineIndex = -1;
        DetachDetailsLyricsRenderLoop();
        ClearCanvasLyricOverlay();
        CanvasLyricsOverlay.Visibility = Visibility.Collapsed;
        _canvasLyricsActive = false;
    }

    private void UpdateCanvasLyricsVisibility()
    {
        var show = IsDetailsTabActive
                   && _activeBackgroundMode == DetailsBackgroundMode.Canvas
                   && _lyricsVm?.HasLyrics == true;
        _canvasLyricsActive = show;
        CanvasLyricsOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            EnsureDetailsLyricsComposition();
            UpdateDetailsLyricsPresentation(renderCanvasOverlay: true);
        }
        else
        {
            ClearCanvasLyricOverlay();
        }

        UpdateDetailsLyricsUpdateMode();
    }

    private void UpdateLyricsSnippetText()
    {
        UpdateDetailsLyricsPresentation(renderCanvasOverlay: _canvasLyricsActive && !ShouldRunDetailsLyricsRenderLoop());
    }

    private void UpdateDetailsLyricsPresentation(bool renderCanvasOverlay)
    {
        if (_lyricsVm?.CurrentLyrics?.LyricsLines is not { Count: > 0 } lines)
        {
            ClearCanvasLyricOverlay();
            return;
        }

        var posMs = _lyricsVm.GetInterpolatedPosition().TotalMilliseconds;
        var currentIdx = FindCurrentLyricLineIndex(lines, posMs);
        if (currentIdx < 0) currentIdx = 0;

        var currentLine = lines[currentIdx];
        var next = currentIdx < lines.Count - 1 ? lines[currentIdx + 1].PrimaryText : "";

        // Update card snippet (only on line change)
        if (currentIdx != _lastSnippetLineIndex)
        {
            _lastSnippetLineIndex = currentIdx;

            var prev = currentIdx > 0 ? lines[currentIdx - 1].PrimaryText : "";
            DetailsLyricsPrev.Text = prev;
            DetailsLyricsPrev.Visibility = string.IsNullOrWhiteSpace(prev)
                ? Visibility.Collapsed : Visibility.Visible;
            DetailsLyricsCurrent.Text = currentLine.PrimaryText;
            DetailsLyricsNext.Text = next;
            DetailsLyricsNext.Visibility = string.IsNullOrWhiteSpace(next)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        if (renderCanvasOverlay && _canvasLyricsActive)
            RenderCanvasLyricSurface(currentLine, BuildCanvasLyricPresentation(currentLine, posMs));
        else if (_canvasLyricsActive)
        {
            return;
        }
        else
            ClearCanvasLyricOverlay();
    }

    private void AttachDetailsLyricsRenderLoop()
    {
        if (_detailsLyricsRenderSubscribed)
            return;

        CompositionTarget.Rendering += OnDetailsLyricsCompositionRendering;
        _detailsLyricsRenderSubscribed = true;
    }

    private void DetachDetailsLyricsRenderLoop()
    {
        if (!_detailsLyricsRenderSubscribed)
            return;

        CompositionTarget.Rendering -= OnDetailsLyricsCompositionRendering;
        _detailsLyricsRenderSubscribed = false;
    }

    private void OnDetailsLyricsCompositionRendering(object? sender, object args)
    {
        if (!_canvasLyricsActive || !IsDetailsTabActive)
            return;

        UpdateDetailsLyricsPresentation(renderCanvasOverlay: true);
    }

    private void UpdateDetailsLyricsUpdateMode()
    {
        if (_lyricsVm?.HasLyrics != true || _lyricsVm.CurrentLyrics == null || !IsDetailsTabActive)
        {
            DetachDetailsLyricsRenderLoop();
            _lastDetailsSnippetUpdateTickMs = -1;
            UpdateDetailsSnippetTimerState();
            return;
        }

        var shouldRunRenderLoop = ShouldRunDetailsLyricsRenderLoop();
        if (shouldRunRenderLoop)
        {
            AttachDetailsLyricsRenderLoop();
            _lastDetailsSnippetUpdateTickMs = -1;
            UpdateDetailsSnippetTimerState();
            return;
        }

        DetachDetailsLyricsRenderLoop();
        UpdateDetailsSnippetTimerState();
    }

    private bool ShouldRunDetailsLyricsRenderLoop()
    {
        return _canvasLyricsActive
            && IsDetailsTabActive
            && _lyricsVm?.PlaybackState.IsPlaying == true;
    }

    private bool ShouldRunDetailsLyricsSharedTimer()
    {
        return IsDetailsTabActive
            && IsPanelVisible
            && _lyricsVm?.HasLyrics == true
            && _lyricsVm.CurrentLyrics != null
            && !ShouldRunDetailsLyricsRenderLoop();
    }

    private int FindCurrentLyricLineIndex(IReadOnlyList<LyricsLine> lines, double posMs)
    {
        if (lines.Count == 0)
            return -1;

        if (_lastSnippetLineIndex >= 0 && _lastSnippetLineIndex < lines.Count)
        {
            var index = _lastSnippetLineIndex;
            while (index + 1 < lines.Count && lines[index + 1].StartMs <= posMs)
                index++;

            while (index >= 0 && lines[index].StartMs > posMs)
                index--;

            return index;
        }

        var low = 0;
        var high = lines.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (lines[mid].StartMs <= posMs)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private CanvasLyricPresentation BuildCanvasLyricPresentation(
        LyricsLine line, double posMs)
    {
        var syllables = line.PrimarySyllables;
        if (!line.IsPrimaryHasRealSyllableInfo || syllables.Count == 0)
        {
            var fallbackText = line.PrimaryText ?? "";
            return new CanvasLyricPresentation(
                PastCharCount: 0,
                HeldStartIndex: 0,
                HeldCharCount: fallbackText.Length,
                ActiveStartIndex: -1,
                ActiveVisibleCharCount: 0,
                CursorCharIndex: Math.Max(-1, fallbackText.Length - 1),
                ShowCursor: false,
                CursorAdvance: 1f,
                CursorOpacity: 0f,
                PastAlpha: PastLyricAlpha,
                HeldAlpha: (byte)Math.Round(HeldLyricAlpha * 0.72),
                ActiveAlpha: ActiveLyricAlpha);
        }

        var activeIndex = -1;
        var lastStartedIndex = -1;
        for (var i = 0; i < syllables.Count; i++)
        {
            var syllable = syllables[i];
            if (syllable.StartMs > posMs)
                break;

            lastStartedIndex = i;
            if (syllable.EndMs == null || posMs < syllable.EndMs.Value)
                activeIndex = i;
        }

        var heldIndex = activeIndex < 0 ? lastStartedIndex : -1;
        if (activeIndex < 0 && heldIndex < 0)
        {
            return default;
        }

        var pastCharCount = 0;
        var heldStartIndex = -1;
        var heldCharCount = 0;
        var activeStartIndex = -1;
        var activeVisibleCharCount = 0;
        var cursorCharIndex = -1;
        var cursorAdvance = 1f;

        if (activeIndex >= 0)
        {
            var activeSyllable = syllables[activeIndex];
            pastCharCount = Math.Max(0, activeSyllable.StartIndex);
            activeStartIndex = activeSyllable.StartIndex;
            activeVisibleCharCount = GetActiveSyllableCharCount(activeSyllable, posMs);
            cursorCharIndex = activeVisibleCharCount > 0
                ? activeStartIndex + activeVisibleCharCount - 1
                : Math.Max(-1, activeStartIndex - 1);
            cursorAdvance = GetActiveSyllableCursorAdvance(activeSyllable, posMs, activeVisibleCharCount);
        }
        else if (heldIndex >= 0)
        {
            var heldSyllable = syllables[heldIndex];
            pastCharCount = Math.Max(0, heldSyllable.StartIndex);
            heldStartIndex = heldSyllable.StartIndex;
            heldCharCount = heldSyllable.Length;
            cursorCharIndex = heldCharCount > 0
                ? heldStartIndex + heldCharCount - 1
                : Math.Max(-1, heldStartIndex - 1);
            cursorAdvance = 1f;
        }

        return new CanvasLyricPresentation(
            PastCharCount: pastCharCount,
            HeldStartIndex: heldStartIndex,
            HeldCharCount: heldCharCount,
            ActiveStartIndex: activeStartIndex,
            ActiveVisibleCharCount: activeVisibleCharCount,
            CursorCharIndex: cursorCharIndex,
            ShowCursor: activeIndex >= 0,
            CursorAdvance: cursorAdvance,
            CursorOpacity: activeIndex >= 0 ? GetCursorBlinkOpacity(posMs) : 0f,
            PastAlpha: PastLyricAlpha,
            HeldAlpha: HeldLyricAlpha,
            ActiveAlpha: (byte)Math.Round(ActiveLyricAlpha * GetActiveSyllableIntensity(
                activeIndex >= 0 ? syllables[activeIndex] : syllables[Math.Max(0, heldIndex)],
                posMs)));
    }

    private void RenderCanvasLyricSurface(LyricsLine line, CanvasLyricPresentation presentation)
    {
        if (CanvasLyricLineHost == null || !IsLoaded)
            return;

        var text = (line.PrimaryText ?? string.Empty).ToUpperInvariant();
        if (text.Length == 0)
        {
            ClearCanvasLyricOverlay();
            return;
        }

        EnsureDetailsLyricsComposition();

        var availableWidth = (float)Math.Max(
            120,
            CanvasLyricLineHost.ActualWidth > 1
                ? CanvasLyricLineHost.ActualWidth
                : Math.Max(120, ActualWidth - 40));
        EnsureDetailsLyricsTextLayout(text, availableWidth);
        if (_detailsLyricsTextLayout == null)
            return;

        var targetHeight = Math.Max(DetailsOverlayMinHeight, _detailsLyricsLayoutHeight);
        if (Math.Abs(CanvasLyricLineHost.Height - targetHeight) > 0.5)
            CanvasLyricLineHost.Height = targetHeight;

        EnsureDetailsLyricsDrawingSurface(
            Math.Max(1, (int)Math.Ceiling(availableWidth)),
            Math.Max(1, (int)Math.Ceiling(targetHeight)));
        UpdateDetailsLyricsCompositionSize();

        if (_detailsLyricsDrawingSurface == null)
            return;

        using (var ds = CanvasComposition.CreateDrawingSession(_detailsLyricsDrawingSurface))
        {
            ds.Clear(Colors.Transparent);

            _detailsLyricsTextLayout.SetColor(0, text.Length, Colors.Transparent);

            if (presentation.PastCharCount > 0)
            {
                _detailsLyricsTextLayout.SetColor(
                    0,
                    Math.Min(presentation.PastCharCount, text.Length),
                    Windows.UI.Color.FromArgb(presentation.PastAlpha, 255, 255, 255));
            }

            if (presentation.HeldStartIndex >= 0 && presentation.HeldCharCount > 0)
            {
                _detailsLyricsTextLayout.SetColor(
                    presentation.HeldStartIndex,
                    Math.Min(presentation.HeldCharCount, text.Length - presentation.HeldStartIndex),
                    Windows.UI.Color.FromArgb(presentation.HeldAlpha, 255, 255, 255));
            }

            if (presentation.ActiveStartIndex >= 0 && presentation.ActiveVisibleCharCount > 0)
            {
                _detailsLyricsTextLayout.SetColor(
                    presentation.ActiveStartIndex,
                    Math.Min(presentation.ActiveVisibleCharCount, text.Length - presentation.ActiveStartIndex),
                    Windows.UI.Color.FromArgb(presentation.ActiveAlpha, 255, 255, 255));
            }

            ds.DrawTextLayout(_detailsLyricsTextLayout, Vector2.Zero, Colors.Transparent);
        }

        UpdateDetailsLyricsCursor(presentation);
        if (_detailsLyricsTextVisual != null)
            _detailsLyricsTextVisual.Opacity = 1f;
    }

    private void EnsureDetailsLyricsComposition()
    {
        if (CanvasLyricLineHost == null)
            return;

        var hostVisual = ElementCompositionPreview.GetElementVisual(CanvasLyricLineHost);
        var compositor = hostVisual.Compositor;

        if (_detailsLyricsContainerVisual != null && _detailsLyricsGraphicsDevice != null)
        {
            UpdateDetailsLyricsCompositionSize();
            return;
        }

        _detailsLyricsCanvasDevice ??= new CanvasDevice();
        _detailsLyricsGraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _detailsLyricsCanvasDevice);

        _detailsLyricsContainerVisual = TrackDetailsLyricsCompositionObject(compositor.CreateContainerVisual());

        _detailsLyricsTextBrush = TrackDetailsLyricsCompositionObject(compositor.CreateSurfaceBrush());
        _detailsLyricsTextBrush.Stretch = CompositionStretch.None;

        _detailsLyricsTextVisual = TrackDetailsLyricsCompositionObject(compositor.CreateSpriteVisual());
        _detailsLyricsTextVisual.Brush = _detailsLyricsTextBrush;

        _detailsLyricsCursorBrush = TrackDetailsLyricsCompositionObject(
            compositor.CreateColorBrush(Windows.UI.Color.FromArgb(CursorLyricAlpha, 255, 255, 255)));
        _detailsLyricsCursorVisual = TrackDetailsLyricsCompositionObject(compositor.CreateSpriteVisual());
        _detailsLyricsCursorVisual.Brush = _detailsLyricsCursorBrush;
        _detailsLyricsCursorVisual.Opacity = 0f;

        _detailsLyricsContainerVisual.Children.InsertAtBottom(_detailsLyricsTextVisual);
        _detailsLyricsContainerVisual.Children.InsertAtTop(_detailsLyricsCursorVisual);

        ElementCompositionPreview.SetElementChildVisual(CanvasLyricLineHost, _detailsLyricsContainerVisual);
        UpdateDetailsLyricsCompositionSize();
    }

    private void EnsureDetailsLyricsTextLayout(string text, float maxWidth)
    {
        if (_detailsLyricsCanvasDevice == null)
            return;

        if (_detailsLyricsTextLayout != null
            && string.Equals(_detailsLyricsLayoutText, text, StringComparison.Ordinal)
            && Math.Abs(_detailsLyricsLayoutWidth - maxWidth) < 1f)
        {
            return;
        }

        _detailsLyricsTextLayout?.Dispose();

        var format = new CanvasTextFormat
        {
            FontSize = DetailsOverlayFontSize,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = 900 },
            HorizontalAlignment = CanvasHorizontalAlignment.Left,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            WordWrapping = CanvasWordWrapping.Wrap
        };

        _detailsLyricsTextLayout = new CanvasTextLayout(
            _detailsLyricsCanvasDevice,
            text,
            format,
            maxWidth,
            2000f)
        {
            Options = CanvasDrawTextOptions.NoPixelSnap
        };
        _detailsLyricsCharacterRegions = _detailsLyricsTextLayout.GetCharacterRegions(0, text.Length);
        _detailsLyricsLayoutText = text;
        _detailsLyricsLayoutWidth = maxWidth;
        _detailsLyricsLayoutHeight = Math.Max((float)_detailsLyricsTextLayout.LayoutBounds.Height, DetailsOverlayMinHeight);
    }

    private void EnsureDetailsLyricsDrawingSurface(int width, int height)
    {
        if (_detailsLyricsGraphicsDevice == null)
            return;

        var targetSize = new Size(width, height);
        if (_detailsLyricsDrawingSurface == null)
        {
            _detailsLyricsDrawingSurface = _detailsLyricsGraphicsDevice.CreateDrawingSurface(
                targetSize,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                DirectXAlphaMode.Premultiplied);
            if (_detailsLyricsTextBrush != null)
                _detailsLyricsTextBrush.Surface = _detailsLyricsDrawingSurface;
            return;
        }

        CanvasComposition.Resize(_detailsLyricsDrawingSurface, targetSize);
    }

    private void UpdateDetailsLyricsCompositionSize()
    {
        if (CanvasLyricLineHost == null || _detailsLyricsContainerVisual == null)
            return;

        var width = Math.Max(1f, (float)(CanvasLyricLineHost.ActualWidth > 1 ? CanvasLyricLineHost.ActualWidth : _detailsLyricsLayoutWidth));
        var height = Math.Max(DetailsOverlayMinHeight, (float)CanvasLyricLineHost.Height);
        var size = new Vector2(width, height);

        _detailsLyricsContainerVisual.Size = size;
        if (_detailsLyricsTextVisual != null)
            _detailsLyricsTextVisual.Size = size;
    }

    private void ClearCanvasLyricOverlay()
    {
        if (_detailsLyricsCursorVisual != null)
            _detailsLyricsCursorVisual.Opacity = 0f;
        if (_detailsLyricsTextVisual != null)
            _detailsLyricsTextVisual.Opacity = 0f;
        if (_detailsLyricsDrawingSurface != null)
        {
            using var ds = CanvasComposition.CreateDrawingSession(_detailsLyricsDrawingSurface);
            ds.Clear(Colors.Transparent);
        }
    }

    private void UpdateDetailsLyricsCursor(CanvasLyricPresentation presentation)
    {
        if (_detailsLyricsCursorVisual == null || _detailsLyricsCharacterRegions == null)
            return;

        if (!presentation.ShowCursor
            || presentation.CursorCharIndex < 0
            || presentation.CursorCharIndex >= _detailsLyricsCharacterRegions.Length)
        {
            _detailsLyricsCursorVisual.Opacity = 0f;
            return;
        }

        var resolvedIndex = ResolveVisibleCursorRegionIndex(presentation.CursorCharIndex);
        if (resolvedIndex < 0)
        {
            _detailsLyricsCursorVisual.Opacity = 0f;
            return;
        }

        var region = _detailsLyricsCharacterRegions[resolvedIndex];
        var bounds = region.LayoutBounds;
        var cursorAdvance = Math.Clamp(presentation.CursorAdvance, 0f, 1f);
        _detailsLyricsCursorVisual.Offset = new Vector3(
            (float)(bounds.X + (bounds.Width * cursorAdvance) + DetailsCursorOffsetX),
            (float)(bounds.Y + DetailsCursorTopInset),
            0f);
        _detailsLyricsCursorVisual.Size = new Vector2(
            DetailsCursorWidth,
            (float)Math.Max(14f, bounds.Height - (DetailsCursorTopInset * 2)));
        _detailsLyricsCursorVisual.Opacity = presentation.CursorOpacity;
    }

    private int ResolveVisibleCursorRegionIndex(int requestedIndex)
    {
        if (_detailsLyricsCharacterRegions == null
            || string.IsNullOrEmpty(_detailsLyricsLayoutText)
            || requestedIndex < 0
            || requestedIndex >= _detailsLyricsCharacterRegions.Length)
        {
            return -1;
        }

        if (IsUsableCursorRegion(requestedIndex))
            return requestedIndex;

        for (var i = requestedIndex - 1; i >= 0; i--)
        {
            if (IsUsableCursorRegion(i))
                return i;
        }

        for (var i = requestedIndex + 1; i < _detailsLyricsCharacterRegions.Length; i++)
        {
            if (IsUsableCursorRegion(i))
                return i;
        }

        return -1;
    }

    private bool IsUsableCursorRegion(int index)
    {
        if (_detailsLyricsCharacterRegions == null
            || string.IsNullOrEmpty(_detailsLyricsLayoutText)
            || index < 0
            || index >= _detailsLyricsCharacterRegions.Length
            || index >= _detailsLyricsLayoutText.Length)
        {
            return false;
        }

        if (char.IsWhiteSpace(_detailsLyricsLayoutText[index]))
            return false;

        var bounds = _detailsLyricsCharacterRegions[index].LayoutBounds;
        return bounds.Width >= MinVisibleCursorRegionWidth && bounds.Height > 0;
    }

    private T TrackDetailsLyricsCompositionObject<T>(T compositionObject) where T : CompositionObject
    {
        _detailsLyricsCompositionObjects.Add(compositionObject);
        return compositionObject;
    }

    private void TeardownDetailsLyricsComposition()
    {
        DetachDetailsLyricsRenderLoop();

        if (CanvasLyricLineHost != null)
            ElementCompositionPreview.SetElementChildVisual(CanvasLyricLineHost, null);

        _detailsLyricsTextLayout?.Dispose();
        _detailsLyricsTextLayout = null;
        _detailsLyricsCharacterRegions = null;
        _detailsLyricsLayoutText = null;
        _detailsLyricsLayoutWidth = 0;
        _detailsLyricsLayoutHeight = 0;
        _detailsLyricsDrawingSurface = null;
        _detailsLyricsGraphicsDevice = null;

        for (int i = _detailsLyricsCompositionObjects.Count - 1; i >= 0; i--)
            _detailsLyricsCompositionObjects[i].Dispose();
        _detailsLyricsCompositionObjects.Clear();

        _detailsLyricsContainerVisual = null;
        _detailsLyricsTextVisual = null;
        _detailsLyricsCursorVisual = null;
        _detailsLyricsTextBrush = null;
        _detailsLyricsCursorBrush = null;

        _detailsLyricsCanvasDevice?.Dispose();
        _detailsLyricsCanvasDevice = null;
    }

    private void CanvasLyricLineHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_canvasLyricsActive)
            return;

        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1
            && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 1)
        {
            return;
        }

        _detailsLyricsTextLayout?.Dispose();
        _detailsLyricsTextLayout = null;
        _detailsLyricsCharacterRegions = null;
        _detailsLyricsLayoutText = null;
        _detailsLyricsLayoutWidth = 0;
        _detailsLyricsLayoutHeight = 0;
        UpdateDetailsLyricsPresentation(renderCanvasOverlay: _canvasLyricsActive);
    }

    private static int GetActiveSyllableCharCount(BaseLyrics syllable, double posMs)
    {
        var text = syllable.Text ?? "";
        if (text.Length == 0)
            return 0;

        var durationMs = Math.Max(60, syllable.DurationMs > 0 ? syllable.DurationMs : 220);
        var progress = Math.Clamp((posMs - syllable.StartMs) / durationMs, 0, 1);
        var eased = 1.0 - Math.Pow(1.0 - progress, 1.7);
        return Math.Clamp((int)Math.Ceiling(text.Length * eased), 1, text.Length);
    }

    private static double GetActiveSyllableIntensity(BaseLyrics syllable, double posMs)
    {
        var durationMs = Math.Max(60, syllable.DurationMs > 0 ? syllable.DurationMs : 220);
        var progress = Math.Clamp((posMs - syllable.StartMs) / durationMs, 0, 1);
        return 0.55 + 0.45 * (1.0 - Math.Pow(1.0 - progress, 2.0));
    }

    private static float GetActiveSyllableCursorAdvance(BaseLyrics syllable, double posMs, int visibleCharCount)
    {
        var text = syllable.Text ?? "";
        if (text.Length == 0 || visibleCharCount <= 0)
            return 1f;

        var durationMs = Math.Max(60, syllable.DurationMs > 0 ? syllable.DurationMs : 220);
        var linearProgress = Math.Clamp((posMs - syllable.StartMs) / durationMs, 0, 1);
        var charProgress = linearProgress * text.Length;
        var visibleStart = Math.Max(0, visibleCharCount - 1);
        var cursorAdvance = charProgress - visibleStart;
        return (float)Math.Clamp(cursorAdvance, 0.15, 1.0);
    }

    private static float GetCursorBlinkOpacity(double posMs)
    {
        var phase = (posMs % CursorBlinkPeriodMs) / CursorBlinkPeriodMs;
        var pulse = 0.5 + (0.5 * Math.Sin(phase * Math.PI * 2));
        return (float)(0.25 + (0.75 * pulse));
    }

    // ── Credits collapse/expand ──

    private void ApplyCreditsCollapse()
    {
        if (_detailsVm == null) return;
        var allGroups = _detailsVm.CreditGroups;
        var totalPeople = allGroups.Sum(g => g.Contributors?.Count ?? 0);

        if (_creditsExpanded || totalPeople <= CreditsCollapsedMaxPeople)
        {
            DetailsCreditGroups.ItemsSource = allGroups;
            DetailsCreditsToggle.Visibility = totalPeople > CreditsCollapsedMaxPeople
                ? Visibility.Visible : Visibility.Collapsed;
            DetailsCreditsToggle.Content = "Show less";
        }
        else
        {
            // Take groups until we hit the max people limit
            var collapsed = new List<CreditGroupVm>();
            var count = 0;
            foreach (var group in allGroups)
            {
                var contributors = group.Contributors ?? [];
                if (count + contributors.Count > CreditsCollapsedMaxPeople && collapsed.Count > 0)
                    break;
                collapsed.Add(group);
                count += contributors.Count;
                if (count >= CreditsCollapsedMaxPeople) break;
            }
            DetailsCreditGroups.ItemsSource = collapsed;
            var hidden = totalPeople - count;
            DetailsCreditsToggle.Visibility = Visibility.Visible;
            DetailsCreditsToggle.Content = $"View all credits (+{hidden} more)";
        }
    }

    private async void DetailsCreditsToggle_Click(object sender, RoutedEventArgs e)
    {
        _creditsExpanded = !_creditsExpanded;

        // Animate: fade out → update → fade in
        await AnimationBuilder.Create()
            .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(120),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseIn)
            .StartAsync(DetailsCreditGroups);

        ApplyCreditsCollapse();

        await AnimationBuilder.Create()
            .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(200),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .Translation(Axis.Y, from: _creditsExpanded ? 8 : -8, to: 0,
                duration: TimeSpan.FromMilliseconds(200),
                easingType: EasingType.Sine, easingMode: EasingMode.EaseOut)
            .StartAsync(DetailsCreditGroups);
    }

    private void DetailsMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailsVm?.PlaybackState is not { } ps) return;
        if (string.IsNullOrEmpty(ps.CurrentTrackId)) return;

        var adapter = new NowPlayingTrackAdapter(ps);
        var ctx = new TrackMenuContext
        {
            ShowCreditsAction = () =>
            {
                if (DetailsCreditsSection.Visibility == Visibility.Visible)
                {
                    var transform = DetailsCreditsSection.TransformToVisual(DetailsContent);
                    var position = transform.TransformPoint(new Point(0, 0));
                    DetailsContent.ChangeView(null, DetailsContent.VerticalOffset + position.Y, null, false);
                }
            },
            SetBackgroundModeAction = (mode) =>
            {
                var modeStr = mode switch
                {
                    DetailsBackgroundMode.None => "None",
                    DetailsBackgroundMode.BlurredAlbumArt => "BlurredAlbumArt",
                    DetailsBackgroundMode.Canvas => "Canvas",
                    _ => "Canvas"
                };
                _settingsService?.Update(s => s.DetailsBackgroundMode = modeStr);
                _ = _settingsService?.SaveAsync();
                UpdatePanelBackgroundState();
                BackgroundModeChanged?.Invoke(this, EventArgs.Empty);
            },
            HasCanvas = _detailsVm?.HasCanvas ?? false,
            CurrentBackgroundMode = _activeBackgroundMode,
            ExtraItems = BuildDetailsCanvasMenuItems()
        };

        var items = TrackContextMenuBuilder.Build(adapter, ctx);
        ContextMenuHost.Show((FrameworkElement)sender, items);
    }

    private void DetailsBioText_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        // Show "Show more" only when text is actually truncated
        if (_detailsVm != null && !_detailsVm.IsBioExpanded)
        {
            DetailsBioToggle.Visibility = sender.IsTextTrimmed
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DetailsBioToggle_Click(object sender, RoutedEventArgs e)
    {
        _detailsVm?.ToggleBioExpandedCommand.Execute(null);
        if (_detailsVm != null)
        {
            DetailsBioText.MaxLines = _detailsVm.IsBioExpanded ? 0 : 3;
            DetailsBioToggle.Content = _detailsVm.IsBioExpanded ? "Show less" : "Show more";
            DetailsBioToggle.Visibility = Visibility.Visible;
        }
    }

    // ── Details background (None / Blurred Album Art / Canvas video) ──
    // Canvas: MediaPlayer in frame server mode → Win2D blur → CanvasImageSource → Image.
    // Blurred Album Art: Load album art bitmap → heavy Win2D blur → CanvasImageSource → Image.
    // No SwapChainPanel = acrylic works on top.

    private DetailsBackgroundMode GetSettingsBackgroundMode()
    {
        var raw = _settingsService?.Settings.DetailsBackgroundMode ?? "Canvas";
        return raw switch
        {
            "None" => DetailsBackgroundMode.None,
            "BlurredAlbumArt" => DetailsBackgroundMode.BlurredAlbumArt,
            "Canvas" => DetailsBackgroundMode.Canvas,
            _ => DetailsBackgroundMode.Canvas
        };
    }

    private void UpdatePanelBackgroundState()
    {
        var hasDetailsData = _detailsVm?.HasData == true;
        ApplyDetailsBackground(
            hasDetailsData ? _detailsVm?.CanvasUrl : null,
            hasDetailsData && _detailsVm?.HasCanvas == true);
        UpdateDetailsCanvasSyncBadge();
    }

    private void ApplyDetailsBackground(string? canvasUrl, bool hasCanvas)
    {
        // Right panel background is intentionally simple:
        // only show canvas media on the Details tab when a canvas source exists.
        var mode = IsDetailsTabActive && hasCanvas
            ? DetailsBackgroundMode.Canvas
            : DetailsBackgroundMode.None;

        _activeBackgroundMode = mode;
        UpdateCanvasLyricsVisibility();

        switch (mode)
        {
            case DetailsBackgroundMode.None:
                TeardownCanvasBackground();
                TeardownBlurredAlbumArt();
                break;

            case DetailsBackgroundMode.Canvas:
                TeardownBlurredAlbumArt();
                SetupCanvasBackground(canvasUrl);
                break;
        }

        UpdateBackgroundMediaVisibility();
        ApplyCanvasLayout();
        BackgroundChromeInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateBackgroundMediaVisibility()
    {
        if (DetailsCanvasImage == null)
            return;

        var hasMedia = HasResolvedBackgroundSource();
        var showMedia = IsDetailsTabActive
                        && _activeBackgroundMode == DetailsBackgroundMode.Canvas
                        && hasMedia
                        && !IsEmbeddedChromeTransparent;

        DetailsCanvasImage.Visibility = showMedia ? Visibility.Visible : Visibility.Collapsed;

        if (_canvasMediaPlayer != null)
        {
            if (showMedia)
                _canvasMediaPlayer.Play();
            else
                _canvasMediaPlayer.Pause();
        }

        BackgroundChromeInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private bool HasResolvedBackgroundSource()
    {
        return _activeBackgroundMode switch
        {
            DetailsBackgroundMode.None => false,
            DetailsBackgroundMode.Canvas => _canvasImageSource != null && _canvasMediaPlayer != null,
            _ => false
        };
    }

    // ── Blurred album art ──

    private async void SetupBlurredAlbumArt()
    {
        var generation = ++_detailsBackgroundGeneration;
        var albumArt = SpotifyImageHelper.ToHttpsUrl(
            _lyricsVm?.PlaybackState.CurrentAlbumArtLarge
            ?? _lyricsVm?.PlaybackState.CurrentAlbumArt);

        if (string.IsNullOrEmpty(albumArt))
        {
            TeardownBlurredAlbumArt();
            return;
        }

        if (albumArt == _currentAlbumArtUrl
            && _blurredAlbumArtImageSource != null)
        {
            UpdateBackgroundMediaVisibility();
            return;
        }

        _currentAlbumArtUrl = albumArt;
        _canvasDevice ??= new CanvasDevice();

        try
        {
            using var bitmap = await CanvasBitmap.LoadAsync(
                _canvasDevice, new Uri(albumArt));

            // Render at panel size (half res for perf)
            var w = Math.Max(1, (int)ActualWidth / 2);
            var h = Math.Max(1, (int)ActualHeight / 2);

            var imageSource = new CanvasImageSource(_canvasDevice, w, h, 96);
            try
            {
                using (var ds = imageSource.CreateDrawingSession(Colors.Transparent))
                {
                    // Scale bitmap to fill the target rect
                    var scaleX = (float)w / bitmap.SizeInPixels.Width;
                    var scaleY = (float)h / bitmap.SizeInPixels.Height;
                    var scale = Math.Max(scaleX, scaleY);

                    var scaledW = bitmap.SizeInPixels.Width * scale;
                    var scaledH = bitmap.SizeInPixels.Height * scale;
                    var offsetX = (w - scaledW) / 2f;
                    var offsetY = (h - scaledH) / 2f;

                    using var scaled = new ScaleEffect
                    {
                        Source = bitmap,
                        Scale = new Vector2(scale, scale),
                        CenterPoint = Vector2.Zero
                    };

                    using var blur = new GaussianBlurEffect
                    {
                        Source = scaled,
                        BlurAmount = AlbumArtBlurAmount,
                        BorderMode = EffectBorderMode.Hard
                    };

                    using var saturation = new SaturationEffect
                    {
                        Source = blur,
                        Saturation = AlbumArtSaturationAmount
                    };

                    ds.DrawImage(saturation, new Vector2(offsetX, offsetY));
                }

                // Verify we're still in blurred album art mode and same URL
                if (_activeBackgroundMode != DetailsBackgroundMode.BlurredAlbumArt
                    || _currentAlbumArtUrl != albumArt
                    || generation != _detailsBackgroundGeneration)
                {
                    DisposeCanvasImageSource(imageSource);
                    return;
                }

                ReplaceBlurredAlbumArtSource(imageSource);
                DetailsCanvasImage.Source = imageSource;
                _blurredAlbumArtRenderWidth = w;
                _blurredAlbumArtRenderHeight = h;
                UpdateBackgroundMediaVisibility();
            }
            catch
            {
                DisposeCanvasImageSource(imageSource);
                throw;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RightPanel] SetupBlurredAlbumArt failed: {ex.Message}");
            UpdateBackgroundMediaVisibility();
        }
    }

    private void TeardownBlurredAlbumArt()
    {
        _detailsBackgroundGeneration++;
        _currentAlbumArtUrl = null;
        _blurredAlbumArtRenderWidth = 0;
        _blurredAlbumArtRenderHeight = 0;
        if (ReferenceEquals(DetailsCanvasImage.Source, _blurredAlbumArtImageSource))
            DetailsCanvasImage.Source = null;
        DisposeCanvasImageSource(ref _blurredAlbumArtImageSource);
        UpdateBackgroundMediaVisibility();
    }

    private void ReplaceBlurredAlbumArtSource(CanvasImageSource imageSource)
    {
        if (!ReferenceEquals(_blurredAlbumArtImageSource, imageSource))
        {
            if (ReferenceEquals(DetailsCanvasImage.Source, _blurredAlbumArtImageSource))
                DetailsCanvasImage.Source = null;
            DisposeCanvasImageSource(ref _blurredAlbumArtImageSource);
            _blurredAlbumArtImageSource = imageSource;
        }
    }

    // ── Canvas layout (push content to bottom so video is visible) ──

    /// <summary>
    /// How much vertical space to reserve below the canvas for the "always-visible"
    /// bottom cards (artist header + output device card, with their 16px spacing).
    /// Falls back to a sensible default when the cards haven't measured yet.
    /// </summary>
    private double GetCanvasBottomReservedHeight()
    {
        const double StackPanelSpacing = 16;
        const double Padding = 12;

        double artistHeight = DetailsArtistHeaderCard?.Visibility == Visibility.Visible
            ? (DetailsArtistHeaderCard.ActualHeight > 0 ? DetailsArtistHeaderCard.ActualHeight : 84)
            : 0;

        double deviceHeight = DetailsOutputDeviceCard?.Visibility == Visibility.Visible
            ? (DetailsOutputDeviceCard.ActualHeight > 0 ? DetailsOutputDeviceCard.ActualHeight : 68)
            : 0;

        if (artistHeight == 0 && deviceHeight == 0) return 120;

        double spacing = (artistHeight > 0 && deviceHeight > 0) ? StackPanelSpacing : 0;
        return artistHeight + deviceHeight + spacing + Padding;
    }

    private void ApplyCanvasLayout(bool animate = true)
    {
        if (DetailsCanvasSpacer == null) return;

        var isCanvas = _activeBackgroundMode == DetailsBackgroundMode.Canvas
                       && IsDetailsTabActive;

        var reservedBelow = GetCanvasBottomReservedHeight();
        var targetHeight = isCanvas
            ? Math.Max(0, DetailsContent.ActualHeight - reservedBelow)
            : 0d;

        if (!animate || !IsLoaded)
        {
            DetailsCanvasSpacer.Height = targetHeight;
            return;
        }

        var current = DetailsCanvasSpacer.Height;
        if (Math.Abs(current - targetHeight) < 1) return;

        var storyboard = new Storyboard();
        var da = new DoubleAnimation
        {
            From = current,
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(400),
            EasingFunction = new CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(da, DetailsCanvasSpacer);
        Storyboard.SetTargetProperty(da, "Height");
        storyboard.Children.Add(da);
        storyboard.Begin();

        if (isCanvas)
            DetailsContent.ChangeView(null, 0, null, false);
    }

    private void DetailsContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_activeBackgroundMode == DetailsBackgroundMode.Canvas
            && IsDetailsTabActive
            && DetailsCanvasSpacer != null)
        {
            DetailsCanvasSpacer.Height = Math.Max(0, DetailsContent.ActualHeight - GetCanvasBottomReservedHeight());
        }
    }

    /// <summary>
    /// Invoked on the first wheel-scroll down while canvas is visible. Collapses the
    /// canvas spacer by exactly the height of the output device card (plus a small
    /// padding), so the card slides up from behind the canvas and is anchored at the
    /// top of the content area. The canvas itself remains visible above it.
    /// </summary>
    private void AnchorOutputDeviceCardOnScroll()
    {
        if (DetailsCanvasSpacer == null || DetailsOutputDeviceCard == null) return;

        // Card hasn't measured yet → fall back to a sensible default.
        var cardHeight = DetailsOutputDeviceCard.ActualHeight > 0
            ? DetailsOutputDeviceCard.ActualHeight
            : 68;

        // 16 accounts for the StackPanel.Spacing between cards.
        var collapseBy = cardHeight + 16;
        var current = DetailsCanvasSpacer.Height;
        var target = Math.Max(0, current - collapseBy);
        if (Math.Abs(current - target) < 1) return;

        var storyboard = new Storyboard();
        var da = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = TimeSpan.FromMilliseconds(320),
            EasingFunction = new CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(da, DetailsCanvasSpacer);
        Storyboard.SetTargetProperty(da, "Height");
        storyboard.Children.Add(da);
        storyboard.Begin();
    }

    // ── Card-by-card paging for Details ──

    private void RegisterDetailsWheelHandler()
    {
        if (_detailsWheelHandlerRegistered) return;
        DetailsContent.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler(DetailsContent_PointerWheelChanged), true);
        _detailsWheelHandlerRegistered = true;
    }

    private void UnregisterDetailsWheelHandler()
    {
        if (!_detailsWheelHandlerRegistered) return;
        DetailsContent.RemoveHandler(PointerWheelChangedEvent,
            new PointerEventHandler(DetailsContent_PointerWheelChanged));
        _detailsWheelHandlerRegistered = false;
    }

    private FrameworkElement[] GetVisibleDetailsCards()
    {
        var all = new FrameworkElement[]
        {
            DetailsArtistHeaderCard, DetailsOutputDeviceCard, DetailsLyricsSnippet, DetailsAiMeaningSection, DetailsBio,
            DetailsCreditsSection, DetailsConcertsSection, DetailsRelatedVideosSection
        };
        return all.Where(c => c != null && c.Visibility == Visibility.Visible).ToArray();
    }

    private void DetailsContent_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsDetailsTabActive) return;

        var props = e.GetCurrentPoint(DetailsContent).Properties;
        var delta = props.MouseWheelDelta;
        if (delta == 0) return;

        // When a canvas is visible, the DetailsCanvasSpacer pushes all cards below the fold
        // (by ~ DetailsContent.ActualHeight - 120). On the first scroll down from rest, collapse
        // enough of that spacer to reveal the output device card so it "hooks" onto the
        // canvas area rather than staying hidden. Subsequent scrolls fall through to the
        // normal card-paging logic below.
        if (delta < 0
            && _activeBackgroundMode == DetailsBackgroundMode.Canvas
            && DetailsCanvasSpacer != null
            && DetailsOutputDeviceCard != null
            && DetailsOutputDeviceCard.Visibility == Visibility.Visible
            && DetailsContent.VerticalOffset < 1
            && DetailsCanvasSpacer.Height > 0)
        {
            AnchorOutputDeviceCardOnScroll();
            e.Handled = true;
            return;
        }

        var cards = GetVisibleDetailsCards();
        if (cards.Length == 0) return;

        var content = (UIElement)DetailsContent.Content;
        var currentOffset = DetailsContent.VerticalOffset;

        if (delta < 0) // scroll down
        {
            // Find the first card whose bottom edge is below the current viewport top
            // and scroll past it (to the next card's top)
            for (int i = 0; i < cards.Length; i++)
            {
                var transform = cards[i].TransformToVisual(content);
                var cardY = transform.TransformPoint(new Point(0, 0)).Y;
                var cardBottom = cardY + cards[i].ActualHeight;

                // This card's bottom is still below viewport top — scroll past it
                if (cardBottom > currentOffset + 1)
                {
                    DetailsContent.ChangeView(null, cardBottom, null, false);
                    break;
                }
            }
        }
        else // scroll up
        {
            // Find the last card whose top edge is above the current viewport top
            // and scroll to its top
            for (int i = cards.Length - 1; i >= 0; i--)
            {
                var transform = cards[i].TransformToVisual(content);
                var cardY = transform.TransformPoint(new Point(0, 0)).Y;

                if (cardY < currentOffset - 1)
                {
                    DetailsContent.ChangeView(null, cardY, null, false);
                    break;
                }
            }

            // If we're above all cards, scroll to top
            if (cards.Length > 0)
            {
                var firstTransform = cards[0].TransformToVisual(content);
                var firstY = firstTransform.TransformPoint(new Point(0, 0)).Y;
                if (currentOffset <= firstY + 1)
                    DetailsContent.ChangeView(null, 0, null, false);
            }
        }

        e.Handled = true;
    }

    // ── Canvas video ──

    private void SetupCanvasBackground(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            TeardownCanvasBackground();
            return;
        }

        if (url == _currentCanvasUrl && _canvasMediaPlayer != null) return;

        TeardownCanvasBackground();
        _currentCanvasUrl = url;
        ResetCanvasFrameScheduling();

        _canvasDevice ??= new CanvasDevice();

        _canvasMediaPlayer = new Windows.Media.Playback.MediaPlayer
        {
            IsLoopingEnabled = true,
            IsMuted = true,
            IsVideoFrameServerEnabled = true // Frame server mode — no swap chain
        };
        _canvasMediaPlayer.VideoFrameAvailable += OnCanvasVideoFrameAvailable;
        _canvasMediaPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(url));

        UpdateBackgroundMediaVisibility();
        _canvasMediaPlayer.Play();
    }

    private void OnCanvasVideoFrameAvailable(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastCanvasFrameRenderTimestamp);
        if (last != 0 && now - last < CanvasFrameMinIntervalTicks)
            return;

        Interlocked.Exchange(ref _lastCanvasFrameRenderTimestamp, now);
        QueueCanvasFrameRender();
    }

    private void TeardownCanvasBackground()
    {
        _detailsBackgroundGeneration++;
        ResetCanvasFrameScheduling();
        if (_canvasMediaPlayer != null)
        {
            _canvasMediaPlayer.VideoFrameAvailable -= OnCanvasVideoFrameAvailable;
            _canvasMediaPlayer.Pause();
            _canvasMediaPlayer.Source = null;
            _canvasMediaPlayer.Dispose();
            _canvasMediaPlayer = null;
        }

        if (ReferenceEquals(DetailsCanvasImage.Source, _canvasImageSource))
            DetailsCanvasImage.Source = null;
        DisposeCanvasImageSource(ref _canvasImageSource);
        _canvasFrameTarget?.Dispose();
        _canvasFrameTarget = null;
        Interlocked.Exchange(ref _lastCanvasFrameRenderTimestamp, 0);
        // Keep _canvasDevice alive for reuse

        _currentCanvasUrl = null;
        UpdateBackgroundMediaVisibility();
    }

    private void QueueCanvasFrameRender()
    {
        var shouldQueue = false;

        lock (_canvasFrameRenderGate)
        {
            _canvasFramePending = true;
            if (!_canvasFrameRenderQueued)
            {
                _canvasFrameRenderQueued = true;
                shouldQueue = true;
            }
        }

        if (!shouldQueue)
            return;

        if (!DispatcherQueue.TryEnqueue(ProcessCanvasFrameRender))
            ResetCanvasFrameScheduling();
    }

    private void ProcessCanvasFrameRender()
    {
        var requeue = false;

        try
        {
            lock (_canvasFrameRenderGate)
            {
                _canvasFramePending = false;
            }

            RenderCanvasFrame();
        }
        finally
        {
            lock (_canvasFrameRenderGate)
            {
                if (_canvasFramePending)
                {
                    requeue = true;
                }
                else
                {
                    _canvasFrameRenderQueued = false;
                }
            }

            if (requeue && !DispatcherQueue.TryEnqueue(ProcessCanvasFrameRender))
                ResetCanvasFrameScheduling();
        }
    }

    private void RenderCanvasFrame()
    {
        if (_canvasMediaPlayer == null || _canvasDevice == null)
            return;

        var w = Math.Max(1, (int)ActualWidth);
        var h = Math.Max(1, (int)ActualHeight);
        if (w <= 0 || h <= 0)
            return;

        var naturalW = (int)(_canvasMediaPlayer.PlaybackSession?.NaturalVideoWidth ?? 0u);
        var naturalH = (int)(_canvasMediaPlayer.PlaybackSession?.NaturalVideoHeight ?? 0u);
        var sourceW = Math.Max(1, naturalW > 0 ? naturalW : w);
        var sourceH = Math.Max(1, naturalH > 0 ? naturalH : h);

        try
        {
            // Hold at most one UI-thread render task at a time and drop intermediate
            // frames when the dispatcher is behind, instead of queueing unbounded work.
            if (_canvasFrameTarget == null || _canvasFrameTarget.SizeInPixels.Width != sourceW || _canvasFrameTarget.SizeInPixels.Height != sourceH)
            {
                _canvasFrameTarget?.Dispose();
                _canvasFrameTarget = new CanvasRenderTarget(_canvasDevice, sourceW, sourceH, 96);
            }

            _canvasMediaPlayer.CopyFrameToVideoSurface(_canvasFrameTarget);

            if (_canvasImageSource == null || _canvasImageSource.SizeInPixels.Width != w || _canvasImageSource.SizeInPixels.Height != h)
            {
                if (ReferenceEquals(DetailsCanvasImage.Source, _canvasImageSource))
                    DetailsCanvasImage.Source = null;
                DisposeCanvasImageSource(ref _canvasImageSource);
                _canvasImageSource = new CanvasImageSource(_canvasDevice, w, h, 96);
                DetailsCanvasImage.Source = _canvasImageSource;
            }

            using var ds = _canvasImageSource.CreateDrawingSession(Colors.Transparent);
            using var saturation = new SaturationEffect
            {
                Source = _canvasFrameTarget,
                Saturation = CanvasSaturationAmount
            };

            var scaleX = (float)w / sourceW;
            var scaleY = (float)h / sourceH;
            var scale = Math.Max(scaleX, scaleY);
            var offsetX = (w - (sourceW * scale)) / 2f;
            var offsetY = (h - (sourceH * scale)) / 2f;

            using var scaled = new ScaleEffect
            {
                Source = saturation,
                Scale = new Vector2(scale, scale),
                CenterPoint = Vector2.Zero
            };

            ds.DrawImage(scaled, new Vector2(offsetX, offsetY));
            UpdateBackgroundMediaVisibility();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ResetCanvasFrameScheduling();

            _canvasDevice?.Dispose();
            _canvasDevice = null;
            _canvasFrameTarget?.Dispose();
            _canvasFrameTarget = null;
            if (ReferenceEquals(DetailsCanvasImage.Source, _canvasImageSource))
                DetailsCanvasImage.Source = null;
            DisposeCanvasImageSource(ref _canvasImageSource);

            var url = _currentCanvasUrl;
            _currentCanvasUrl = null;
            if (!string.IsNullOrEmpty(url))
                SetupCanvasBackground(url);
        }
    }

    private void ResetCanvasFrameScheduling()
    {
        lock (_canvasFrameRenderGate)
        {
            _canvasFramePending = false;
            _canvasFrameRenderQueued = false;
        }

        Interlocked.Exchange(ref _lastCanvasFrameRenderTimestamp, 0);
    }

    private static void DisposeCanvasImageSource(CanvasImageSource? source)
    {
        // CanvasImageSource is not IDisposable in this Win2D projection.
        // Callers detach Image.Source before dropping the reference.
    }

    private static void DisposeCanvasImageSource(ref CanvasImageSource? source)
    {
        DisposeCanvasImageSource(source);
        source = null;
    }
}
