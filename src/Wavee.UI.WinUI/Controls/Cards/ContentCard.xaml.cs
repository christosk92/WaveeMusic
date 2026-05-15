using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Windows.Foundation;
using Windows.UI;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Controls.Imaging;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Reusable content card with colored placeholder, fade-in image, title and subtitle.
/// Supports square (playlist/album) and circular (artist) image modes.
/// </summary>
public sealed partial class ContentCard : UserControl
{
    // â”€â”€ Dependency Properties â”€â”€

    /// <summary>
    /// Back-compat shim. The actual gate lives in
    /// <see cref="Wavee.UI.WinUI.Services.ImageLoadingSuspension"/> so the new
    /// <see cref="Wavee.UI.WinUI.Controls.Imaging.CompositionImage"/> control
    /// can observe it without taking a dependency on this card.
    /// </summary>
    public static bool IsImageLoadingSuspended
    {
        get => ImageLoadingSuspension.IsSuspended;
        set => ImageLoadingSuspension.IsSuspended = value;
    }

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnImageUrlChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnSubtitleChanged));

    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(nameof(Badge), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnBadgeChanged));

    public static readonly DependencyProperty PlaceholderColorHexProperty =
        DependencyProperty.Register(nameof(PlaceholderColorHex), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnPlaceholderColorChanged));

    public static readonly DependencyProperty PlaceholderGlyphProperty =
        DependencyProperty.Register(nameof(PlaceholderGlyph), typeof(string), typeof(ContentCard),
            new PropertyMetadata("\uE8D6", OnPlaceholderGlyphChanged));

    public static readonly DependencyProperty IsCircularImageProperty =
        DependencyProperty.Register(nameof(IsCircularImage), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsCircularChanged));

    public static readonly DependencyProperty CenterTextProperty =
        DependencyProperty.Register(nameof(CenterText), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnCenterTextChanged));

    public static readonly DependencyProperty ImageSizeProperty =
        DependencyProperty.Register(nameof(ImageSize), typeof(double), typeof(ContentCard),
            new PropertyMetadata(0.0)); // 0 = auto (fill width for square, 120 for circle)

    /// <summary>
    /// Controls the image-host aspect ratio. <see cref="CardAspectMode.Square"/> is the
    /// historical default and keeps every existing ContentCard call site unchanged.
    /// <see cref="CardAspectMode.Tall"/> is 2:3 portrait (TV/movie posters);
    /// <see cref="CardAspectMode.Wide"/> and <see cref="CardAspectMode.Backdrop"/> are
    /// 16:9 landscape (music videos / continue-watching hero rails).
    /// Mutually exclusive with <see cref="IsCircularImage"/> â€” setting both falls back
    /// to circular at runtime.
    /// </summary>
    public static readonly DependencyProperty AspectModeProperty =
        DependencyProperty.Register(nameof(AspectMode), typeof(CardAspectMode), typeof(ContentCard),
            new PropertyMetadata(CardAspectMode.Square, OnAspectModeChanged));

    public string? ImageUrl
    {
        get => (string?)GetValue(ImageUrlProperty);
        set => SetValue(ImageUrlProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>
    /// Optional short accent line rendered beneath the subtitle. When <c>null</c> or empty
    /// the badge row is collapsed and the card retains its original height. Used today to
    /// show "Played 3h ago" on the library grid when the user sorts by Recents.
    /// </summary>
    public string? Badge
    {
        get => (string?)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    public string? PlaceholderColorHex
    {
        get => (string?)GetValue(PlaceholderColorHexProperty);
        set => SetValue(PlaceholderColorHexProperty, value);
    }

    public string PlaceholderGlyph
    {
        get => (string)GetValue(PlaceholderGlyphProperty);
        set => SetValue(PlaceholderGlyphProperty, value);
    }

    public bool IsCircularImage
    {
        get => (bool)GetValue(IsCircularImageProperty);
        set => SetValue(IsCircularImageProperty, value);
    }

    public bool CenterText
    {
        get => (bool)GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    public double ImageSize
    {
        get => (double)GetValue(ImageSizeProperty);
        set => SetValue(ImageSizeProperty, value);
    }

    public CardAspectMode AspectMode
    {
        get => (CardAspectMode)GetValue(AspectModeProperty);
        set => SetValue(AspectModeProperty, value);
    }

    public static readonly DependencyProperty NavigationUriProperty =
        DependencyProperty.Register(nameof(NavigationUri), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnNavigationUriChanged));

    public static readonly DependencyProperty NavigationTitleProperty =
        DependencyProperty.Register(nameof(NavigationTitle), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null));

    public static readonly DependencyProperty NavigationTotalTracksProperty =
        DependencyProperty.Register(nameof(NavigationTotalTracks), typeof(int), typeof(ContentCard),
            new PropertyMetadata(0));

    /// <summary>
    /// Origin-known track count for album / playlist cards. Forwarded into the
    /// <c>ContentNavigationParameter</c> built by <see cref="HandleNavigation"/>
    /// so the destination page renders an exact-count skeleton via
    /// <c>TrackDataGrid.LoadingRowCount</c>. Default 0 means "unknown" and the
    /// destination falls back to its default skeleton row count.
    /// </summary>
    public int NavigationTotalTracks
    {
        get => (int)GetValue(NavigationTotalTracksProperty);
        set => SetValue(NavigationTotalTracksProperty, value);
    }

    /// <summary>
    /// Spotify URI to navigate to when clicked (e.g. "spotify:artist:xxx").
    /// When set, the card handles navigation internally (like ShortsPill).
    /// </summary>
    public string? NavigationUri
    {
        get => (string?)GetValue(NavigationUriProperty);
        set => SetValue(NavigationUriProperty, value);
    }

    /// <summary>
    /// Fallback title for the navigation tab header.
    /// </summary>
    public string? NavigationTitle
    {
        get => (string?)GetValue(NavigationTitleProperty);
        set => SetValue(NavigationTitleProperty, value);
    }

    public static readonly DependencyProperty IsExternalProperty =
        DependencyProperty.Register(nameof(IsExternal), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ShowPlaybackOverlayProperty =
        DependencyProperty.Register(nameof(ShowPlaybackOverlay), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(true, OnShowPlaybackOverlayChanged));

    public static readonly DependencyProperty UseConnectedAnimationProperty =
        DependencyProperty.Register(nameof(UseConnectedAnimation), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(true));

    /// <summary>
    /// Enables source-to-destination connected animation for same-tab navigation.
    /// Disable for cards hosted inside the destination page itself; reusing that
    /// page can invalidate the source subtree during the navigation event.
    /// </summary>
    public bool UseConnectedAnimation
    {
        get => (bool)GetValue(UseConnectedAnimationProperty);
        set => SetValue(UseConnectedAnimationProperty, value);
    }

    public static readonly DependencyProperty AutoNavigateOnTapProperty =
        DependencyProperty.Register(nameof(AutoNavigateOnTap), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(true));

    /// <summary>
    /// When true (the default), tapping the card auto-routes through
    /// <see cref="NavigateToUri"/> if <see cref="NavigationUri"/> is set.
    /// When false, tapping the card fires <c>CardClick</c> as if
    /// <see cref="NavigationUri"/> were null â€” but the URI is still used by
    /// the viewport-prefetch path and by the <see cref="SecondaryActionVisible"/>
    /// "Open album" button. Use this on cards that want prefetch + the
    /// secondary affordance but need a custom primary tap (e.g. artist-page
    /// discography cards that expand inline on tap).
    /// </summary>
    public bool AutoNavigateOnTap
    {
        get => (bool)GetValue(AutoNavigateOnTapProperty);
        set => SetValue(AutoNavigateOnTapProperty, value);
    }

    public static readonly DependencyProperty SecondaryActionVisibleProperty =
        DependencyProperty.Register(nameof(SecondaryActionVisible), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false));

    // OpenInNewWindow (E8A7) - reads as "go to detail page" without looking
    // like an external-link arrow (NavigateExternalInline). Sourced via
    // FluentGlyphs to keep PUA literals out of .cs (CLAUDE.md convention).
    public static readonly DependencyProperty SecondaryActionGlyphProperty =
        DependencyProperty.Register(nameof(SecondaryActionGlyph), typeof(string), typeof(ContentCard),
            new PropertyMetadata(Styles.FluentGlyphs.OpenInNewWindow));

    public static readonly DependencyProperty SecondaryActionTooltipProperty =
        DependencyProperty.Register(nameof(SecondaryActionTooltip), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null));

    /// <summary>
    /// Show a small accent-coloured overlay button at top-right of the cover
    /// image. Click navigates via <see cref="AlbumNavigationHelper.NavigateToAlbum"/>
    /// (using <see cref="NavigationUri"/> / <see cref="NavigationTotalTracks"/>
    /// / etc.) and consumes the routed event so a parent's tap handler does
    /// not also fire. Designed for surfaces whose primary tap does something
    /// other than navigate (e.g. the discography cards on Artist Page that
    /// expand a track preview inline) â€” the secondary button gives the user
    /// a discrete "Open full album page" route.
    /// </summary>
    public bool SecondaryActionVisible
    {
        get => (bool)GetValue(SecondaryActionVisibleProperty);
        set => SetValue(SecondaryActionVisibleProperty, value);
    }

    public string SecondaryActionGlyph
    {
        get => (string)GetValue(SecondaryActionGlyphProperty);
        set => SetValue(SecondaryActionGlyphProperty, value);
    }

    public string? SecondaryActionTooltip
    {
        get => (string?)GetValue(SecondaryActionTooltipProperty);
        set => SetValue(SecondaryActionTooltipProperty, value);
    }

    /// <summary>
    /// When true, the hover overlay shows an "open in browser" button (globe icon)
    /// instead of the play button, and the play / now-playing chrome is suppressed.
    /// Use for cards whose target is an external URL (e.g. merch shop links). Click
    /// on the overlay fires <see cref="ExternalActionRequested"/>; clicking the card
    /// body still fires <see cref="CardClick"/> as usual.
    /// </summary>
    public bool IsExternal
    {
        get => (bool)GetValue(IsExternalProperty);
        set => SetValue(IsExternalProperty, value);
    }

    /// <summary>
    /// Controls the play / now-playing hover chrome for non-playable cards that
    /// still use ContentCard's layout and click routing, such as cast members.
    /// </summary>
    public bool ShowPlaybackOverlay
    {
        get => (bool)GetValue(ShowPlaybackOverlayProperty);
        set => SetValue(ShowPlaybackOverlayProperty, value);
    }

    public static readonly DependencyProperty IsPassiveProperty =
        DependencyProperty.Register(nameof(IsPassive), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsPassiveChanged));

    /// <summary>
    /// When true, the internal Button is disabled for hit testing so clicks pass through
    /// to a parent ItemContainer for selection. Hover/press animations still work via
    /// the UserControl's own pointer handlers.
    /// </summary>
    public bool IsPassive
    {
        get => (bool)GetValue(IsPassiveProperty);
        set => SetValue(IsPassiveProperty, value);
    }

    public static readonly DependencyProperty IsLoadingProperty =
        DependencyProperty.Register(nameof(IsLoading), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsLoadingChanged));

    /// <summary>
    /// When true, shows shimmer placeholders instead of real content (ghost/loading state).
    /// </summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsPlayingChanged));

    public static readonly DependencyProperty IsContextPausedProperty =
        DependencyProperty.Register(nameof(IsContextPaused), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsContextPausedChanged));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsContextPaused
    {
        get => (bool)GetValue(IsContextPausedProperty);
        set => SetValue(IsContextPausedProperty, value);
    }

    // â”€â”€ Events â”€â”€

    public event EventHandler? CardClick;
    public event EventHandler? CardMiddleClick;
    public event EventHandler? CardHover;
    public event EventHandler? PlayRequested;
    public event EventHandler? ExternalActionRequested;
    public event TypedEventHandler<ContentCard, RightTappedRoutedEventArgs>? CardRightTapped;

    // â”€â”€ Constructor â”€â”€

    private bool _passiveHandlersAdded;

    // Store handler references so RemoveHandler can match the exact instances
    private PointerEventHandler? _passivePointerEntered;
    private PointerEventHandler? _passivePointerExited;
    private PointerEventHandler? _passivePointerPressed;
    private PointerEventHandler? _passivePointerReleased;
    private bool _isPointerOver;
    private bool _isPlaybackPending;
    private int _playbackPendingVersion;
    private bool _circleSizeHandlerAttached;
    private bool _hasEffectiveViewport;
    private bool _isInsideEffectiveViewport = true;

    // Album-metadata viewport prefetch (see Services.IAlbumPrefetcher). Single-
    // shot per realization â€” reset in OnUnloaded so container recycling re-fires
    // on the next viewport enter. The AlbumPrefetcher service does its own
    // dedupe across the whole session, so re-fires are cheap.
    private bool _albumPrefetchKicked;
    private bool _playlistPrefetchKicked;
    private const double AlbumPrefetchTriggerDistance = 500;
    private const string AlbumUriPrefix = "spotify:album:";
    private const string PlaylistUriPrefix = "spotify:playlist:";
    private const int CardImageDecodeSize = 200;
    private const double DefaultCardWidth = 160;
    private const double CardHorizontalPadding = 16;
    private const double CircleImageInset = 16;
    private const double MinimumImageSide = 60;
    private string? _currentImageCacheUrl;
    private string? _retryImageCacheUrl;
    private int _retryImageLoadCount;

    private readonly ThemeColorService? _themeColorService;
    private readonly NowPlayingHighlightService? _highlightService;

    public ContentCard()
    {
        _themeColorService = Ioc.Default.GetService<ThemeColorService>();
        _highlightService = Ioc.Default.GetService<NowPlayingHighlightService>();
        InitializeComponent();
        // Cards are always interactive (click navigates or opens). Set the hand cursor
        // once on construction â€” the system shows it on hover automatically as long as
        // the cursor stays assigned, no per-event toggling needed.
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // Backstop for ItemsRepeater recycle: OnUnloaded nulls the image
        // Source for memory; the Loaded handler is supposed to re-call
        // LoadImage on re-realization, but in some recycle paths (same item
        // reused in the same container) the binding does not re-trigger and
        // Loaded may fire before x:Bind has propagated the new DataContext.
        // EffectiveViewportChanged fires whenever this element's viewport in
        // an ancestor scroller changes â€” including the first measurement
        // after re-attach â€” and lets us reload the cached bitmap on demand.
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (IsPassive && !_passiveHandlersAdded)
        {
            _passiveHandlersAdded = true;

            // Register with handledEventsToo=true so hover/press animations still run
            // when a parent ItemContainer marks pointer events as handled (selection chrome).
            // CardButton itself stays hit-testable so the inner play-button overlay can
            // receive clicks; passive "don't navigate on card click" is enforced in
            // CardButton_Click by selecting the parent ItemContainer instead.
            _passivePointerEntered = new PointerEventHandler(Card_PointerEntered);
            _passivePointerExited = new PointerEventHandler(Card_PointerExited);
            _passivePointerPressed = new PointerEventHandler(Card_PointerPressed);
            _passivePointerReleased = new PointerEventHandler(Card_PointerReleased);

            AddHandler(PointerEnteredEvent, _passivePointerEntered, true);
            AddHandler(PointerExitedEvent, _passivePointerExited, true);
            AddHandler(PointerPressedEvent, _passivePointerPressed, true);
            AddHandler(PointerReleasedEvent, _passivePointerReleased, true);
        }

        // Subscribe to the shared NowPlayingHighlightService singleton instead of
        // registering directly with WeakReferenceMessenger. The service listens to
        // NowPlayingChangedMessage once at startup and broadcasts via a plain C# event
        // â€” avoiding ~310 per-card messenger Register calls during HomePage realization.
        if (_highlightService != null)
        {
            _highlightService.CurrentChanged += OnHighlightServiceChanged;
            // Apply the current snapshot immediately so newly-realized cards reflect playback state.
            var (contextUri, albumUri, playing) = _highlightService.Current;
            ApplyHighlight(contextUri, albumUri, playing);
        }
        ImageLoadingSuspension.Changed += OnImageLoadingSuspensionChanged;
        if (!_hasEffectiveViewport || _isInsideEffectiveViewport)
            LoadImage(ImageUrl);
        SyncInitialPlaybackState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ResetInteractionState(updatePlayingState: false);
        StopPendingBeam();

        // Re-arm album / playlist prefetch on re-realization (ItemsRepeater
        // recycle). The prefetcher's own dedup HashSet still prevents a
        // duplicate POST.
        _albumPrefetchKicked = false;
        _playlistPrefetchKicked = false;

        // Release the final CompositionRectangleClip on SquareImageContainer.
        // UpdateSquareImageClip swaps a fresh clip on every image load and
        // disposes the previous one â€” but the LAST one stays attached for
        // the card's lifetime. On unload, drop it so the GPU resource is
        // released promptly rather than waiting for full visual teardown.
        if (SquareImageContainer is not null)
        {
            try
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SquareImageContainer);
                var lingering = visual.Clip;
                visual.Clip = null;
                lingering?.Dispose();
            }
            catch
            {
                // Composition can already be torn down during window close.
            }
        }

        // Unsubscribe from the highlight service â€” strong event, explicit unsubscribe required.
        if (_highlightService != null)
            _highlightService.CurrentChanged -= OnHighlightServiceChanged;
        ImageLoadingSuspension.Changed -= OnImageLoadingSuspensionChanged;

        // Clean up SizeChanged subscription to prevent memory leaks
        if (CircleImageContainer != null && _circleSizeHandlerAttached)
        {
            CircleImageContainer.SizeChanged -= OnCircleContainerSizeChanged;
            _circleSizeHandlerAttached = false;
        }

        // EffectiveViewportChanged can report an empty/stale viewport while a
        // navigation-cached page is being detached. The next attach must take a
        // fresh viewport sample instead of trusting the old "outside viewport"
        // result and skipping reload.
        _hasEffectiveViewport = false;
        _isInsideEffectiveViewport = true;
        // CompositionImage.OnUnloaded handles its own pin release. No further
        // teardown needed here â€” the surface stays in the LRU until evicted.

        // Remove passive pointer handlers using the SAME instances that were added
        if (_passiveHandlersAdded)
        {
            if (_passivePointerEntered != null)
                RemoveHandler(PointerEnteredEvent, _passivePointerEntered);
            if (_passivePointerExited != null)
                RemoveHandler(PointerExitedEvent, _passivePointerExited);
            if (_passivePointerPressed != null)
                RemoveHandler(PointerPressedEvent, _passivePointerPressed);
            if (_passivePointerReleased != null)
                RemoveHandler(PointerReleasedEvent, _passivePointerReleased);

            _passivePointerEntered = null;
            _passivePointerExited = null;
            _passivePointerPressed = null;
            _passivePointerReleased = null;
            _passiveHandlersAdded = false;
        }
    }

    // â”€â”€ Now-playing self-management (via shared NowPlayingHighlightService) â”€â”€

    private void OnHighlightServiceChanged(string? contextUri, string? albumUri, bool playing)
        => ApplyHighlight(contextUri, albumUri, playing);

    private void ApplyHighlight(string? contextUri, string? albumUri, bool playing)
    {
        // Do the cheap string comparison BEFORE scheduling a dispatcher callback.
        // This avoids queuing 20-50 TryEnqueue calls when only 0-1 cards actually match.
        var navUri = NavigationUri; // read once â€” safe, DependencyProperty reads are thread-safe for strings
        // Match on context OR album URI â€” so an album card lights up whenever the
        // currently-playing track belongs to that album, not only when playback
        // was launched from the album itself.
        var isMatch = !string.IsNullOrEmpty(navUri)
            && ((!string.IsNullOrEmpty(contextUri)
                 && string.Equals(navUri, contextUri, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(albumUri)
                    && string.Equals(navUri, albumUri, StringComparison.OrdinalIgnoreCase)));

        // Only dispatch if state actually changed
        var wasPlaying = IsPlaying;
        var wasPaused = IsContextPaused;
        var newPlaying = isMatch && playing;
        var newPaused = isMatch && !playing;
        var shouldClearPending = _isPlaybackPending && (!isMatch || playing);
        if (newPlaying == wasPlaying && newPaused == wasPaused && !shouldClearPending) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPlaybackPending && isMatch && playing)
                _isPointerOver = false;

            IsPlaying = newPlaying;
            IsContextPaused = newPaused;
            if (_isPlaybackPending && (!isMatch || playing))
                SetPlaybackPending(false);
        });
    }

    private void SyncInitialPlaybackState()
    {
        if (_highlightService != null)
        {
            var (contextUri, albumUri, playing) = _highlightService.Current;
            ApplyHighlight(contextUri, albumUri, playing);
            return;
        }

        ApplyHighlightFromPlaybackStateService();
    }

    private void ApplyHighlightFromPlaybackStateService()
    {
        var ps = Ioc.Default.GetService<IPlaybackStateService>();
        if (ps == null) return;
        ApplyHighlight(ps.CurrentContext?.ContextUri, ps.CurrentAlbumId, ps.IsPlaying);
    }

    // â”€â”€ Property changed callbacks â”€â”€

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var url = e.NewValue as string;
        if (card.HasLoadedImageFor(url))
            return;

        if (IsImageLoadingSuspended)
        {
            if (!card.IsCurrentImageUrl(url))
                card.ReleaseImage();
            return;
        }

        if (card._hasEffectiveViewport && !card._isInsideEffectiveViewport)
        {
            if (!card.IsCurrentImageUrl(url))
                card.ReleaseImage();
            return;
        }

        card.LoadImage(url);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.TitleText.Text = e.NewValue as string ?? "";
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.SubtitleText.Text = e.NewValue as string ?? "";
    }

    private static void OnBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        if (card.BadgeText == null) return;
        var value = e.NewValue as string;
        card.BadgeText.Text = value ?? "";
        card.BadgeText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnPlaceholderColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.ApplyPlaceholderColor(e.NewValue as string);
    }

    private static void OnPlaceholderGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var glyph = e.NewValue as string ?? "\uE8D6";
        if (card.SquarePlaceholderIcon != null)
            card.SquarePlaceholderIcon.Glyph = glyph;
        // CirclePlaceholderIcon only exists after CircleImageContainer is realized;
        // EnsureCircleRealized re-applies this glyph when the subtree loads.
        if (card.CirclePlaceholderIcon != null)
            card.CirclePlaceholderIcon.Glyph = glyph;
    }

    private static void OnIsCircularChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.UpdateImageMode();
    }

    private static void OnAspectModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        // Recompute the image-host height under the new aspect ratio. The
        // SizeChanged path won't fire if the container's measured width is
        // unchanged, so push from here directly.
        if (card.SquareImageContainer != null)
        {
            var width = card.ImageSize > 0
                ? card.ImageSize
                : card.SquareImageContainer.ActualWidth;
            if (width > 0)
                card.SetSquareImageSide(width);
        }
    }

    private static void OnCenterTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var center = (bool)e.NewValue;
        card.TitleText.HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        card.SubtitleText.HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        card.TitleText.TextAlignment = center ? Microsoft.UI.Xaml.TextAlignment.Center : Microsoft.UI.Xaml.TextAlignment.Left;
        card.SubtitleText.TextAlignment = center ? Microsoft.UI.Xaml.TextAlignment.Center : Microsoft.UI.Xaml.TextAlignment.Left;
    }

    // â”€â”€ Image loading â”€â”€

    private void OnImageLoadingSuspensionChanged(bool suspended)
    {
        if (suspended || !IsLoaded)
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && !IsImageLoadingSuspended)
                ReloadImageIfNeeded(ignoreViewportGate: true);
        });
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        // Album metadata prefetch â€” fires once per realization when the card
        // is within AlbumPrefetchTriggerDistance px of the viewport. The
        // prefetcher itself dedupes URIs across the whole session, so the
        // single-shot guard here is just a hot-path optimisation. Runs
        // independently of the image-loading viewport check below; the
        // distance threshold is lenient (BringIntoViewDistance â‰¤ 500) so we
        // catch cards approaching the viewport, not just ones fully visible.
        if (!_albumPrefetchKicked || !_playlistPrefetchKicked)
        {
            var navUri = NavigationUri;
            if (!string.IsNullOrEmpty(navUri)
                && args.BringIntoViewDistanceX <= AlbumPrefetchTriggerDistance
                && args.BringIntoViewDistanceY <= AlbumPrefetchTriggerDistance)
            {
                if (!_albumPrefetchKicked && navUri.StartsWith(AlbumUriPrefix, StringComparison.Ordinal))
                {
                    _albumPrefetchKicked = true;
                    Ioc.Default.GetService<IAlbumPrefetcher>()?.EnqueueAlbumPrefetch(navUri);
                }
                else if (!_playlistPrefetchKicked && navUri.StartsWith(PlaylistUriPrefix, StringComparison.Ordinal))
                {
                    _playlistPrefetchKicked = true;
                    Ioc.Default.GetService<IPlaylistMetadataPrefetcher>()?.EnqueuePlaylistPrefetch(navUri);
                }
            }
        }

        if (!TryGetEffectiveViewportIntersection(sender, args.EffectiveViewport, out var isInsideEffectiveViewport))
        {
            // During page re-attach WinUI can raise this before layout has a
            // real size. That is an unknown viewport sample, not an offscreen
            // card; releasing here races the Loaded reload and leaves
            // placeholders behind.
            _hasEffectiveViewport = false;
            _isInsideEffectiveViewport = true;
            return;
        }

        _hasEffectiveViewport = true;
        _isInsideEffectiveViewport = isInsideEffectiveViewport;

        if (!_isInsideEffectiveViewport)
            return;

        // Cheap short-circuit: 99% of fires are scroll noise on already-loaded
        // cards. Only act when the image was nulled (by OnUnloaded) and we
        // have a URL to reload.
        if (string.IsNullOrEmpty(ImageUrl)) return;
        if (HasImage()) return;
        LoadImage(ImageUrl);
    }

    public void ReleaseImage()
    {
        _currentImageCacheUrl = null;

        if (SquareImage != null)
        {
            SquareImage.ImageUrl = null;
            // Reset to invisible â€” fade-in animation snaps from current
            // opacity, so leaving this at 1 caused a 1 â†’ 0 â†’ 0.85 flash on
            // the next ImageOpened. The XAML default is 0 too.
            SquareImage.Opacity = 0;
        }

        if (CircleImage != null)
            CircleImage.ImageUrl = null;

        if (SquarePlaceholderIcon != null)
            SquarePlaceholderIcon.Visibility = Visibility.Visible;
        if (CirclePlaceholderIcon != null)
            CirclePlaceholderIcon.Visibility = Visibility.Visible;
    }

    public void ReloadImageIfNeeded(bool ignoreViewportGate = false)
    {
        if (!ignoreViewportGate && _hasEffectiveViewport && !_isInsideEffectiveViewport)
            return;
        if (HasImage())
            return;

        if (ignoreViewportGate)
        {
            _hasEffectiveViewport = false;
            _isInsideEffectiveViewport = true;
        }

        LoadImage(ImageUrl);
    }

    private bool HasImage()
        => IsCircularImage
            ? !string.IsNullOrEmpty(CircleImage?.ImageUrl)
            : !string.IsNullOrEmpty(SquareImage?.ImageUrl);

    private bool HasLoadedImageFor(string? url)
        => HasImage() && IsCurrentImageUrl(url);

    private bool IsCurrentImageUrl(string? url)
    {
        var resolved = ResolveCardImageUrl(url);
        return !string.IsNullOrEmpty(resolved)
               && string.Equals(_currentImageCacheUrl, resolved, StringComparison.Ordinal);
    }

    private static bool TryGetEffectiveViewportIntersection(
        FrameworkElement element,
        Rect effectiveViewport,
        out bool intersects)
    {
        intersects = true;

        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        if (effectiveViewport.Width <= 0 || effectiveViewport.Height <= 0)
            return false;

        intersects = effectiveViewport.Right > 0
                     && effectiveViewport.Bottom > 0
                     && effectiveViewport.Left < element.ActualWidth
                     && effectiveViewport.Top < element.ActualHeight;
        return true;
    }

    private void LoadImage(string? url)
    {
        // Guard: template may not be applied yet
        if (SquareImage == null) return;
        if (IsImageLoadingSuspended) return;

        var resolvedImageUrl = ResolveCardImageUrl(url);
        if (string.IsNullOrEmpty(resolvedImageUrl))
        {
            ReleaseImage();
            return;
        }

        if (string.Equals(_currentImageCacheUrl, resolvedImageUrl, StringComparison.Ordinal) && HasImage())
        {
            HidePlaceholderForCurrentMode();
            return;
        }

        // Show placeholders â€” they sit on top of the image via z-order.
        SquarePlaceholderIcon.Visibility = Visibility.Visible;
        if (CirclePlaceholderIcon != null)
            CirclePlaceholderIcon.Visibility = Visibility.Visible;

        var httpsUrl = resolvedImageUrl;
        _currentImageCacheUrl = httpsUrl;
        if (!string.Equals(_retryImageCacheUrl, httpsUrl, StringComparison.Ordinal))
        {
            _retryImageCacheUrl = httpsUrl;
            _retryImageLoadCount = 0;
        }

        // CompositionImage handles pin/unpin and surface lifetime internally.
        // Setting ImageUrl kicks off the LoadedImageSurface fetch via the
        // shared ImageCacheService.
        if (IsCircularImage)
        {
            EnsureCircleRealized();
            CircleImage!.DecodePixelSize = CardImageDecodeSize;
            CircleImage.ImageUrl = httpsUrl;
            // Clear the square slot so a virtualized recycle doesn't leave the
            // last item's surface holding a pin in this card's other layer.
            SquareImage.ImageUrl = null;
            CirclePlaceholderIcon!.Visibility = Visibility.Collapsed;
        }
        else
        {
            SquareImage.DecodePixelSize = CardImageDecodeSize;
            SquareImage.ImageUrl = httpsUrl;
            if (CircleImage != null) CircleImage.ImageUrl = null;
            SquarePlaceholderIcon.Visibility = Visibility.Collapsed;
            // If the surface is already loaded (cache hit), snap to resting
            // opacity. CompositionImage's ImageOpened event still fires in
            // that case, but we'd otherwise pop from 0 â†’ 0.85 on the next
            // tick which looks like a delayed reveal on a cached hit.
            if (SquareImage.IsImageLoaded)
                SquareImage.Opacity = 0.85;
        }
    }

    private void HidePlaceholderForCurrentMode()
    {
        if (IsCircularImage)
        {
            if (CirclePlaceholderIcon != null)
                CirclePlaceholderIcon.Visibility = Visibility.Collapsed;
        }
        else if (SquarePlaceholderIcon != null)
        {
            SquarePlaceholderIcon.Visibility = Visibility.Collapsed;
        }
    }

    private static string? ResolveCardImageUrl(string? url)
    {
        var httpsUrl = Helpers.SpotifyImageHelper.ToHttpsUrl(url);
        if (!string.IsNullOrEmpty(httpsUrl))
            return httpsUrl;

        // Home cards need a cheap preview. Full playlist/sidebar surfaces still
        // use PlaylistMosaicService for composed 2x2 mosaics.
        return Helpers.SpotifyImageHelper.TryParseMosaicTileUrls(url, out var tileUrls) && tileUrls.Count > 0
            ? tileUrls[0]
            : null;
    }

    private void ApplyPlaceholderColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            SquareImageContainer.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
            return;
        }

        var color = ParseHexColor(hex);
        var brush = new SolidColorBrush(color) { Opacity = 0.3 };
        SquareImageContainer.Background = brush;

        // Only apply to circle placeholder if the circle subtree is realized;
        // EnsureCircleRealized re-applies this color when the subtree loads.
        if (CirclePlaceholder?.Fill is SolidColorBrush)
            CirclePlaceholder.Fill = new SolidColorBrush(color) { Opacity = 0.3 };
    }

    private void UpdateImageMode()
    {
        if (SquareImageContainer == null) return; // template not applied yet

        if (IsCircularImage)
        {
            EnsureCircleRealized();
            SquareImageContainer.Visibility = Visibility.Collapsed;
            CircleImageContainer!.Visibility = Visibility.Visible;
            // Size will be set dynamically based on card width via SizeChanged
            if (!_circleSizeHandlerAttached)
            {
                CircleImageContainer.SizeChanged += OnCircleContainerSizeChanged;
                _circleSizeHandlerAttached = true;
            }
            StabilizeImageSlotForMeasure(ActualWidth);
        }
        else
        {
            SquareImageContainer.Visibility = Visibility.Visible;
            // Only collapse the circle container if it was actually realized;
            // for square cards the x:Load-deferred subtree simply never exists.
            if (CircleImageContainer != null)
            {
                CircleImageContainer.Visibility = Visibility.Collapsed;
                if (_circleSizeHandlerAttached)
                {
                    CircleImageContainer.SizeChanged -= OnCircleContainerSizeChanged;
                    _circleSizeHandlerAttached = false;
                }
            }
            StabilizeImageSlotForMeasure(ActualWidth);
        }
    }

    private void OnCircleContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Make circle diameter = container width (minus a small margin)
        var size = ImageSize > 0
            ? ImageSize
            : Math.Max(MinimumImageSide, e.NewSize.Width - CircleImageInset);
        SetCircleImageSide(size);
    }

    private void SquareImageContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = ImageSize > 0 ? ImageSize : e.NewSize.Width;
        if (width <= 0) return;

        SetSquareImageSide(width);
    }

    /// <summary>
    /// Height multiplier for the current <see cref="AspectMode"/>:
    /// height = width Ã— ratio. Square = 1, Tall (2:3 portrait) = 1.5,
    /// Wide / Backdrop (16:9 landscape) = 0.5625.
    /// </summary>
    private double AspectHeightRatio() => AspectMode switch
    {
        CardAspectMode.Tall                                  => 1.5,
        CardAspectMode.Wide or CardAspectMode.Backdrop       => 9.0 / 16.0,
        _                                                    => 1.0,
    };

    /// <summary>
    /// Sets the image-host box height from a measured width, honoring the current
    /// <see cref="AspectMode"/>. Name kept for back-compat â€” historically only
    /// "Square" existed so the parameter was a single side. With aspect modes the
    /// height is derived from the width per ratio.
    /// </summary>
    private void SetSquareImageSide(double width)
    {
        if (SquareImageContainer == null || width <= 0)
            return;

        var height = width * AspectHeightRatio();

        if (double.IsNaN(SquareImageContainer.Height) || Math.Abs(SquareImageContainer.Height - height) > 0.5)
            SquareImageContainer.Height = height;

        UpdateSquareImageClip(width, height);
    }

    private void SetCircleImageSide(double side)
    {
        if (side <= 0 || CirclePlaceholder == null || CircleImage == null)
            return;

        if (CircleImageContainer != null
            && (double.IsNaN(CircleImageContainer.Height) || Math.Abs(CircleImageContainer.Height - side) > 0.5))
            CircleImageContainer.Height = side;

        CirclePlaceholder.Width = side;
        CirclePlaceholder.Height = side;
        CircleImage.Width = side;
        CircleImage.Height = side;
    }

    private void UpdateSquareImageClip(double width, double height)
    {
        // Grid.CornerRadius only clips background paint in WinUI 3. SquareImageContainer
        // is a Grid (not a Border), so its CornerRadius does not clip child UIElements.
        // CompositionRectangleClip set on the outermost visual (GetElementVisual returns the
        // handoff visual for Border but the outermost for Grid/UserControl) clips the image.
        // CreateRectangleClip is used instead of CreateGeometricClip(RoundedRectangleGeometry)
        // â€” the latter bleeds at sub-pixel edges (see AnimatedHeroBackground.UpdateClip).
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(SquareImageContainer);
        var compositor = visual.Compositor;
        var clip = compositor.CreateRectangleClip();
        clip.Right = (float)width;
        clip.Bottom = (float)height;
        clip.TopLeftRadius = new System.Numerics.Vector2(4f);
        clip.TopRightRadius = new System.Numerics.Vector2(4f);
        clip.BottomLeftRadius = new System.Numerics.Vector2(4f);
        clip.BottomRightRadius = new System.Numerics.Vector2(4f);
        // Assign the new clip BEFORE disposing the old one â€” disposing
        // an attached clip mid-composition can flash. WinUI keeps a
        // ref-count on attached clips, so disposing after the swap drops
        // the redundant managed wrapper without affecting the visual.
        var oldClip = visual.Clip;
        visual.Clip = clip;
        try { oldClip?.Dispose(); }
        catch { /* idempotent â€” already disposed by composition shutdown */ }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        StabilizeImageSlotForMeasure(availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private void StabilizeImageSlotForMeasure(double availableWidth)
    {
        if (SquareImageContainer == null)
            return;

        var cardWidth = ResolveMeasureWidth(availableWidth);
        var contentWidth = Math.Max(MinimumImageSide, cardWidth - CardHorizontalPadding);

        if (IsCircularImage)
        {
            EnsureCircleRealized();
            var side = ImageSize > 0
                ? ImageSize
                : Math.Max(MinimumImageSide, contentWidth - CircleImageInset);
            SetCircleImageSide(side);
        }
        else
        {
            var width = ImageSize > 0 ? ImageSize : contentWidth;
            var height = width * AspectHeightRatio();
            if (double.IsNaN(SquareImageContainer.Height) || Math.Abs(SquareImageContainer.Height - height) > 0.5)
                SquareImageContainer.Height = height;
        }
    }

    private double ResolveMeasureWidth(double availableWidth)
    {
        if (!double.IsNaN(availableWidth) && !double.IsInfinity(availableWidth) && availableWidth > 0)
            return availableWidth;

        if (ActualWidth > 0)
            return ActualWidth;

        return ImageSize > 0 ? ImageSize + CardHorizontalPadding : DefaultCardWidth;
    }

    private void SquareImage_ImageOpened(object? sender, EventArgs e)
    {
        SquarePlaceholderIcon.Visibility = Visibility.Collapsed;

        // Fade in using XAML framework layer (not composition â€” avoids layer multiply bugs).
        // No explicit `from` â€” let the animation pick up SquareImage's current
        // opacity. End at resting opacity (0.85), not 1.0 â€” hover handlers
        // manage the 0.85 â†” 1.0 toggle on their own.
        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Opacity(to: 0.85,
                     duration: TimeSpan.FromMilliseconds(250),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(SquareImage);
    }

    private void SquareImage_ImageFailed(object? sender, EventArgs e)
    {
        // CompositionImage already invalidated the cache entry before raising
        // ImageFailed. Reset our local state and put the placeholder back.
        var failedUrl = _currentImageCacheUrl;
        SquareImage.ImageUrl = null;
        SquarePlaceholderIcon.Visibility = Visibility.Visible;

        if (string.IsNullOrEmpty(failedUrl)
            || !IsLoaded
            || IsImageLoadingSuspended
            || _retryImageLoadCount >= 1)
        {
            return;
        }

        _retryImageLoadCount++;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded
                || IsImageLoadingSuspended
                || (_hasEffectiveViewport && !_isInsideEffectiveViewport)
                || HasImage()
                || !string.Equals(_currentImageCacheUrl, failedUrl, StringComparison.Ordinal))
            {
                return;
            }

            LoadImage(ImageUrl);
        });
    }

    // â”€â”€ Hover handling â”€â”€

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        CardHover?.Invoke(this, EventArgs.Empty);

        // Realize the overlay for the current shape before reading the named elements â€”
        // after x:Load="False" on the overlays, the backing fields start null until FindName.
        EnsurePlayOverlayRealized();
        UpdatePlayingState();

        var overlayBtn = GetActiveOverlayButton();
        if (overlayBtn != null)
        {
            overlayBtn.Visibility = Visibility.Visible;
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(150))
                .Start(overlayBtn);
        }

        // SecondaryAction "Open album" button — fades in alongside the play
        // overlay so the discography-card affordance is discoverable only on
        // hover. Gated on SecondaryActionVisible (default false) so cards
        // that don't opt in pay no animation cost.
        if (SecondaryActionVisible && SquareSecondaryActionButton != null)
        {
            SquareSecondaryActionButton.Visibility = Visibility.Visible;
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(150))
                .Start(SquareSecondaryActionButton);
        }

        // Scale up via composition with proper CenterPoint
        if (CardRoot != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Scale(from: System.Numerics.Vector3.One, to: new System.Numerics.Vector3(1.03f), duration: TimeSpan.FromMilliseconds(200))
                .Start(CardRoot);
        }

        // Image opacity: muted at rest (0.85), full on hover. Cheap snap; the
        // 1.03 scale animation above already carries the motion of the pop.
        if (SquareImage != null) SquareImage.Opacity = 1.0;
        if (CircleImage != null) CircleImage.Opacity = 1.0;
    }

    private async void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        var overlayBtn = GetActiveOverlayButton();
        if (overlayBtn != null && !_isPlaybackPending && !IsContextPaused)
        {
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(100))
                .Start(overlayBtn);

            // Collapse after fade-out to reset for next hover
            await System.Threading.Tasks.Task.Delay(120);
            if (!_isPointerOver && !_isPlaybackPending && !IsContextPaused)
                overlayBtn.Visibility = Visibility.Collapsed;
        }

        // Mirror the secondary "Open album" button fade-out. Same 100 ms
        // fade + 120 ms collapse delay as the play overlay above so both
        // affordances retreat together.
        if (SecondaryActionVisible && SquareSecondaryActionButton != null)
        {
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(100))
                .Start(SquareSecondaryActionButton);

            await System.Threading.Tasks.Task.Delay(120);
            if (!_isPointerOver)
                SquareSecondaryActionButton.Visibility = Visibility.Collapsed;
        }

        UpdatePlayingState();

        if (CardRoot != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Scale(from: new System.Numerics.Vector3(1.03f), to: System.Numerics.Vector3.One, duration: TimeSpan.FromMilliseconds(200))
                .Start(CardRoot);
        }

        // Restore the muted resting state for the image.
        if (SquareImage != null) SquareImage.Opacity = 0.85;
        if (CircleImage != null) CircleImage.Opacity = 0.85;
    }

    // â”€â”€ Press animation â”€â”€

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (CardRoot == null) return;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Scale(to: new System.Numerics.Vector3(0.96f), duration: TimeSpan.FromMilliseconds(100))
            .Start(CardRoot);
    }

    private void Card_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (CardRoot == null) return;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

        // Mouse releases return to hover scale; touch/pen taps have no hover state.
        var targetScale = _isPointerOver
            ? new System.Numerics.Vector3(1.03f)
            : System.Numerics.Vector3.One;
        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Scale(to: targetScale, duration: TimeSpan.FromMilliseconds(150))
            .Start(CardRoot);
    }

    // â”€â”€ Passive mode â”€â”€

    private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var loading = (bool)e.NewValue;
        if (card.ShimmerOverlay != null)
            card.ShimmerOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (card.ContentPanel != null)
            card.ContentPanel.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnIsPassiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Passive behaviour is enforced in CardButton_Click by redirecting to
        // parent ItemContainer selection. CardButton stays hit-testable so the
        // inner play-button overlay still receives clicks.
    }

    // â”€â”€ Playing state â”€â”€

    private static void OnShowPlaybackOverlayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((ContentCard)d).UpdatePlayingState();
    }

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.UpdatePlayingState();
    }

    private static void OnIsContextPausedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        if ((bool)e.NewValue)
            card.EnsurePlayOverlayRealized();
        card.UpdatePlayingState();
    }

    private static void OnNavigationUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.ResetPlaybackVisualStateForNewItem();
        card.SyncInitialPlaybackState();
    }

    private void UpdatePlayingState()
    {
        var isPlaying = IsPlaying;
        var isPaused = IsContextPaused;
        var isActiveContext = isPlaying || isPaused;
        var isPending = _isPlaybackPending;
        var showPlaybackChrome = ShowPlaybackOverlay && !IsExternal;

        var showSquarePlaying = showPlaybackChrome && (isPlaying || isPaused) && !isPending && !IsCircularImage;
        var showCirclePlaying = showPlaybackChrome && (isPlaying || isPaused) && !isPending && IsCircularImage;
        var showPlayButton = showPlaybackChrome && (_isPointerOver || isPaused || isPending);

        if (showSquarePlaying && SquarePlayingIndicator == null)
            this.FindName("SquarePlayingIndicator");
        if (showCirclePlaying)
            EnsureCircleRealized();
        if (showPlayButton)
            EnsurePlayOverlayRealized();

        // Null-guard every access â€” all overlays are x:Load-deferred, so any of them
        // may be null on a card that hasn't yet realized its subtree.
        if (SquarePlayingIndicator != null)
            SquarePlayingIndicator.Visibility = showSquarePlaying
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (SquarePlayingEqualizer != null)
            SquarePlayingEqualizer.IsActive = showSquarePlaying && isPlaying;

        if (CirclePlayingIndicator != null)
            CirclePlayingIndicator.Visibility = showCirclePlaying
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (CirclePlayingEqualizer != null)
            CirclePlayingEqualizer.IsActive = showCirclePlaying && isPlaying;

        if (IsExternal)
        {
            // External cards have no play / pending / paused notion â€” overlay
            // visibility is purely hover-driven, handled in Card_PointerEntered/Exited.
            // Keep the play button collapsed in case a card flipped from non-external
            // to external while realized.
            if (SquarePlayButton != null) SquarePlayButton.Visibility = Visibility.Collapsed;
            if (CirclePlayButton != null) CirclePlayButton.Visibility = Visibility.Collapsed;
        }
        else if (!ShowPlaybackOverlay)
        {
            if (SquarePlayButton != null) SquarePlayButton.Visibility = Visibility.Collapsed;
            if (CirclePlayButton != null) CirclePlayButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var playBtn = IsCircularImage ? CirclePlayButton : SquarePlayButton;
            var playAction = IsCircularImage ? CirclePlayAction : SquarePlayAction;
            if (playBtn != null && playAction != null)
            {
                playAction.IsPlaying = isPlaying;
                playAction.IsPending = isPending;

                playBtn.Visibility = (isPending || showPlayButton)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (playBtn.Visibility == Visibility.Visible)
                    playBtn.Opacity = 1;
            }
        }

        // Accent color on title when this is the active context
        if (isActiveContext)
            TitleText.Foreground = _themeColorService?.AccentText
                ?? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        else
            TitleText.ClearValue(TextBlock.ForegroundProperty);
    }

    // â”€â”€ Click handlers â”€â”€

    private void CardButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPlayButtonSource(e.OriginalSource))
            return;

        // Passive mode: the card lives inside an ItemsView/ItemContainer and a
        // click should select the item rather than navigate. Ctrl+click still
        // opens a new tab to preserve the "open in background" affordance.
        if (IsPassive)
        {
            if (!string.IsNullOrEmpty(NavigationUri) && Helpers.Navigation.NavigationHelpers.IsCtrlPressed())
            {
                ResetInteractionState();
                if (NavigateToUri(openInNewTab: true))
                    return;
            }

            SelectParentItemContainer();
            return;
        }

        // Self-navigation: if NavigationUri is set AND auto-routing is enabled,
        // navigate directly. The AutoNavigateOnTap gate lets surfaces opt out
        // of the auto-route while still benefiting from NavigationUri-driven
        // viewport prefetch + the SecondaryAction "Open album" button (e.g.
        // artist-page discography cards whose primary tap expands inline).
        if (AutoNavigateOnTap && !string.IsNullOrEmpty(NavigationUri))
        {
            var openInNewTab = Helpers.Navigation.NavigationHelpers.IsCtrlPressed();
            if (!openInNewTab && UseConnectedAnimation)
                PrepareConnectedAnimation();

            ResetInteractionState();
            if (NavigateToUri(openInNewTab))
                return;
        }

        CardClick?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Click handler for the optional SecondaryAction overlay button. Routes
    /// to AlbumPage with full prefetch + connected animation + count prefill
    /// via <see cref="AlbumNavigationHelper.NavigateToAlbum"/>. Marks the event
    /// handled so the underlying card Tapped / CardClick doesn't also fire â€”
    /// critical on surfaces whose primary tap triggers a different action
    /// (expand, select, etc.).
    /// </summary>
    private void SecondaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        var uri = NavigationUri;
        if (string.IsNullOrEmpty(uri)) return;
        Helpers.Navigation.AlbumNavigationHelper.NavigateToAlbum(
            uri,
            title: Title,
            subtitle: Subtitle,
            imageUrl: ImageUrl,
            totalTracks: NavigationTotalTracks > 0 ? NavigationTotalTracks : null,
            connectedAnimationSource: UseConnectedAnimation ? GetConnectedAnimationSource() : null);

        // RoutedEventArgs from Button.Click does not bubble to ancestor Tapped
        // handlers under the WinUI 3 input model â€” the Button consumes the
        // pointer before Tapped fires, so we don't need e.Handled = true here.
        // CardClick is only invoked from CardButton_Click (which never sees the
        // inner Button's click), so the expand path also stays untouched.
    }

    /// <summary>
    /// The cover-image visual to use as the connected-animation source.
    /// Square cards animate the SquareImageContainer; circle cards (artist
    /// avatars) animate the CircleImageContainer. Returns null if neither is
    /// available â€” caller's null-check skips the animation prep cleanly.
    /// </summary>
    private UIElement? GetConnectedAnimationSource()
    {
        if (IsCircularImage && CircleImageContainer is not null)
            return CircleImageContainer;
        return SquareImageContainer as UIElement;
    }

    private void SelectParentItemContainer()
    {
        DependencyObject? current = VisualTreeHelper.GetParent(this);
        while (current != null)
        {
            if (current is ItemContainer itemContainer)
            {
                itemContainer.IsSelected = true;
                return;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        PlayRequested?.Invoke(this, EventArgs.Empty);

        var playback = Ioc.Default.GetService<IPlaybackService>();
        if (playback == null) return;
        var playbackState = Ioc.Default.GetService<IPlaybackStateService>();

        try
        {
            var navUri = NavigationUri;
            if (IsPlaying)
            {
                await Task.Run(async () => await playback.PauseAsync());
            }
            else if (IsContextPaused)
            {
                SetPlaybackPending(true);
                playbackState?.NotifyBuffering(null);
                var result = await Task.Run(async () => await playback.ResumeAsync());
                if (!result.IsSuccess)
                {
                    SetPlaybackPending(false);
                    playbackState?.ClearBuffering();
                }
            }
            else if (!string.IsNullOrEmpty(navUri))
            {
                SetPlaybackPending(true);
                playbackState?.NotifyBuffering(null);
                var result = await Task.Run(async () => await playback.PlayContextAsync(navUri));
                if (!result.IsSuccess)
                {
                    SetPlaybackPending(false);
                    playbackState?.ClearBuffering();
                }
            }
        }
        catch(Exception x)
        {
            SetPlaybackPending(false);
            playbackState?.ClearBuffering();
            Debug.WriteLine(x.ToString());
            // Playback errors surface via IPlaybackService.Errors observable
        }
    }

    private bool IsPlayButtonSource(object? source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (ReferenceEquals(current, SquarePlayButton)
                || ReferenceEquals(current, CirclePlayButton)
                || ReferenceEquals(current, SquareExternalButton))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ExternalButton_Click(object sender, RoutedEventArgs e)
    {
        // Mirror the play button: the overlay is the explicit affordance, but the
        // semantic action (open URL) belongs to the consumer. ExternalActionRequested
        // is the precise hook; CardClick also fires so consumers wired only to
        // CardClick (most current Merch usages) keep working.
        ExternalActionRequested?.Invoke(this, EventArgs.Empty);
        CardClick?.Invoke(this, EventArgs.Empty);
    }

    private void CardButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
        {
            if (!string.IsNullOrEmpty(NavigationUri))
            {
                ResetInteractionState();
                if (NavigateToUri(openInNewTab: true))
                    return;
            }
            CardMiddleClick?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CardButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CardRightTapped?.Invoke(this, e);
        if (e.Handled)
            return;

        if (!string.IsNullOrEmpty(NavigationUri))
        {
            var items = Controls.ContextMenu.Builders.CardContextMenuBuilder.Build(new Controls.ContextMenu.Builders.CardMenuContext
            {
                Uri = NavigationUri!,
                Title = Title ?? string.Empty,
                Subtitle = Subtitle,
                ImageUrl = ImageUrl,
                OpenAction = openInNewTab =>
                {
                    ResetInteractionState();
                    NavigateToUri(openInNewTab);
                }
            });
            Controls.ContextMenu.ContextMenuHost.Show(this, items, e.GetPosition(this));
            e.Handled = true;
            return;
        }
    }

    // â”€â”€ Navigation â”€â”€

    private bool NavigateToUri(bool openInNewTab)
    {
        var uri = NavigationUri!;
        var parts = uri.Split(':');
        if (parts.Length < 3) return false;

        var type = parts[1];
        var title = NavigationTitle ?? Title ?? type;

        var param = new Data.Parameters.ContentNavigationParameter
        {
            Uri = uri,
            Title = title,
            Subtitle = SubtitleText?.Text,
            ImageUrl = ImageUrl,
            TotalTracks = NavigationTotalTracks > 0 ? NavigationTotalTracks : null
        };

        switch (type)
        {
            case "collection" when uri.Contains("your-episodes", StringComparison.OrdinalIgnoreCase):
                Helpers.Navigation.NavigationHelpers.OpenYourEpisodes(openInNewTab);
                return true;
            case "collection":
                Helpers.Navigation.NavigationHelpers.OpenLikedSongs(openInNewTab);
                return true;
            case "artist":
                Helpers.Navigation.NavigationHelpers.OpenArtist(param, title, openInNewTab);
                return true;
            case "album":
                OpenAlbumAfterClick(param, title, openInNewTab);
                return true;
            case "playlist":
                Helpers.Navigation.NavigationHelpers.OpenPlaylist(param, title, openInNewTab);
                return true;
            case "user" when uri.Contains(":collection", StringComparison.OrdinalIgnoreCase):
                Helpers.Navigation.NavigationHelpers.OpenLikedSongs(openInNewTab);
                return true;
            case "user":
                Helpers.Navigation.NavigationHelpers.OpenProfile(param, title, openInNewTab);
                return true;
            case "page":
            case "section":
            case "genre":
                Helpers.Navigation.NavigationHelpers.OpenBrowsePage(param, openInNewTab);
                return true;
            case "show":
                Helpers.Navigation.NavigationHelpers.OpenShowPage(param, openInNewTab);
                return true;
            case "episode":
                Helpers.Navigation.NavigationHelpers.OpenEpisodePage(
                    uri,
                    title,
                    ImageUrl,
                    openInNewTab: openInNewTab);
                return true;
        }

        return false;
    }

    private void OpenAlbumAfterClick(Data.Parameters.ContentNavigationParameter parameter, string title, bool openInNewTab)
    {
        if (!openInNewTab && DispatcherQueue is not null)
        {
            DispatcherQueue.TryEnqueue(() =>
                Helpers.Navigation.NavigationHelpers.OpenAlbum(parameter, title, openInNewTab: false));
            return;
        }

        Helpers.Navigation.NavigationHelpers.OpenAlbum(parameter, title, openInNewTab);
    }

    internal bool PrepareConnectedAnimation()
    {
        var uri = NavigationUri;
        if (string.IsNullOrEmpty(uri) || IsCircularImage)
            return false;

        // Don't snapshot before the GPU surface has finished loading â€” the
        // morph would start from an empty rect and pop into the cover mid-
        // flight. Cold-cache cards fall back to the standard shimmer crossfade
        // reveal, which looks correct.
        if (SquareImage is null || !SquareImage.IsImageLoaded)
            return false;

        var parts = uri.Split(':');
        if (parts.Length < 3)
            return false;

        var key = parts[1] switch
        {
            var type when type.Equals("album", StringComparison.OrdinalIgnoreCase)
                => Helpers.ConnectedAnimationHelper.AlbumArt,
            var type when type.Equals("playlist", StringComparison.OrdinalIgnoreCase)
                => Helpers.ConnectedAnimationHelper.PlaylistArt,
            var type when type.Equals("show", StringComparison.OrdinalIgnoreCase)
                => Helpers.ConnectedAnimationHelper.PodcastArt,
            var type when type.Equals("episode", StringComparison.OrdinalIgnoreCase)
                => Helpers.ConnectedAnimationHelper.PodcastEpisodeArt,
            _ => null
        };

        if (key is null)
            return false;

        Helpers.ConnectedAnimationHelper.PrepareAnimation(
            key,
            SquareImageContainer);
        return true;
    }

    // â”€â”€ Helpers â”€â”€

    private void EnsurePlayOverlayRealized()
    {
        if (IsExternal)
        {
            // External cards never need play / now-playing chrome â€” only the
            // "open in browser" overlay. Square is the only supported shape today.
            if (SquareExternalButton == null)
                this.FindName("SquareExternalButton");
            return;
        }

        if (!ShowPlaybackOverlay)
            return;

        if (IsCircularImage)
            EnsureCircleRealized();
        else if (SquarePlayButton == null)
            this.FindName("SquarePlayButton");
    }

    /// <summary>
    /// Returns the button used for the hover overlay in the card's current mode:
    /// the external (open-in-browser) button when <see cref="IsExternal"/> is true,
    /// otherwise the play button matching the image shape. May return null if the
    /// overlay subtree hasn't been realized yet (call <see cref="EnsurePlayOverlayRealized"/>
    /// first if you intend to act on the result).
    /// </summary>
    private Button? GetActiveOverlayButton()
        => IsExternal
            ? SquareExternalButton
            : !ShowPlaybackOverlay
                ? null
            : (IsCircularImage ? CirclePlayButton : SquarePlayButton);

    private void SetPlaybackPending(bool pending)
    {
        if (_isPlaybackPending == pending) return;

        _isPlaybackPending = pending;
        _playbackPendingVersion++;
        if (pending)
        {
            EnsurePlayOverlayRealized();
            StartPendingBeam();
            _ = ClearPlaybackPendingAfterTimeoutAsync(_playbackPendingVersion);
        }
        else
        {
            StopPendingBeam();
        }

        UpdatePlayingState();
    }

    private void ResetPlaybackVisualStateForNewItem()
    {
        _isPointerOver = false;
        _isPlaybackPending = false;
        _playbackPendingVersion++;
        StopPendingBeam();
        if (SquarePlayButton != null)
            SquarePlayButton.Visibility = Visibility.Collapsed;
        if (CirclePlayButton != null)
            CirclePlayButton.Visibility = Visibility.Collapsed;
        if (SquareExternalButton != null)
            SquareExternalButton.Visibility = Visibility.Collapsed;
        IsPlaying = false;
        IsContextPaused = false;
    }

    private void ResetInteractionState(bool updatePlayingState = true)
    {
        _isPointerOver = false;

        if (CardRoot != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.Scale = System.Numerics.Vector3.One;
        }

        if (SquareImage != null) SquareImage.Opacity = 0.85;
        if (CircleImage != null) CircleImage.Opacity = 0.85;

        if (!_isPlaybackPending)
            StopPendingBeam();

        if (!_isPlaybackPending)
        {
            if (SquarePlayButton != null)
            {
                SquarePlayButton.Opacity = 0;
                SquarePlayButton.Visibility = Visibility.Collapsed;
            }
            if (CirclePlayButton != null)
            {
                CirclePlayButton.Opacity = 0;
                CirclePlayButton.Visibility = Visibility.Collapsed;
            }
            if (SquareExternalButton != null)
            {
                SquareExternalButton.Opacity = 0;
                SquareExternalButton.Visibility = Visibility.Collapsed;
            }
        }

        if (updatePlayingState)
            UpdatePlayingState();
    }

    private void StartPendingBeam()
    {
        if (PendingBeam == null)
            this.FindName("PendingBeam");
        PendingBeam?.Start();
    }

    private void StopPendingBeam()
    {
        PendingBeam?.Stop();
    }

    private async Task ClearPlaybackPendingAfterTimeoutAsync(int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPlaybackPending && _playbackPendingVersion == version)
            {
                SetPlaybackPending(false);
                Ioc.Default.GetService<IPlaybackStateService>()?.ClearBuffering();
            }
        });
    }

    /// <summary>
    /// Realizes the <c>CircleImageContainer</c> subtree on demand. With <c>x:Load="False"</c>
    /// on the grid, all circle-mode named elements (<c>CirclePlaceholder</c>, <c>CirclePlaceholderIcon</c>,
    /// <c>CircleImage</c>, <c>CircleImageBrush</c>, <c>CirclePlayButton</c>, <c>CirclePlayingIndicator</c>, etc.)
    /// start null until <see cref="FrameworkElement.FindName"/> triggers the subtree load.
    /// Idempotent â€” returns early if the container is already realized.
    /// </summary>
    private void EnsureCircleRealized()
    {
        if (CircleImageContainer != null) return;
        this.FindName("CircleImageContainer");

        // Re-apply DP-sourced state that the DP callbacks skipped while the circle
        // subtree was null. For the common case of a square card (which never
        // realizes this subtree), none of this ever runs.
        if (CirclePlaceholderIcon != null)
        {
            var glyph = GetValue(PlaceholderGlyphProperty) as string ?? "\uE8D6";
            CirclePlaceholderIcon.Glyph = glyph;
        }
        if (CirclePlaceholder != null && GetValue(PlaceholderColorHexProperty) is string hex && !string.IsNullOrEmpty(hex))
        {
            var color = ParseHexColor(hex);
            CirclePlaceholder.Fill = new SolidColorBrush(color) { Opacity = 0.3 };
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
}
