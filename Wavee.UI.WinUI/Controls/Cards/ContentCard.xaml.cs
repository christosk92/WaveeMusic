using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Windows.Foundation;
using Windows.UI;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Messages;
using Wavee.UI.WinUI.Services;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Reusable content card with colored placeholder, fade-in image, title and subtitle.
/// Supports square (playlist/album) and circular (artist) image modes.
/// </summary>
public sealed partial class ContentCard : UserControl
{
    // ── Dependency Properties ──

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

    public static readonly DependencyProperty NavigationUriProperty =
        DependencyProperty.Register(nameof(NavigationUri), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null, OnNavigationUriChanged));

    public static readonly DependencyProperty NavigationTitleProperty =
        DependencyProperty.Register(nameof(NavigationTitle), typeof(string), typeof(ContentCard),
            new PropertyMetadata(null));

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

    // ── Events ──

    public event EventHandler? CardClick;
    public event EventHandler? CardMiddleClick;
    public event EventHandler? CardHover;
    public event EventHandler? PlayRequested;
    public event EventHandler? ExternalActionRequested;
    public event TypedEventHandler<ContentCard, RightTappedRoutedEventArgs>? CardRightTapped;

    // ── Constructor ──

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

    private readonly ImageCacheService? _imageCache;
    private readonly ThemeColorService? _themeColorService;
    private readonly NowPlayingHighlightService? _highlightService;

    public ContentCard()
    {
        _imageCache = Ioc.Default.GetService<ImageCacheService>();
        _themeColorService = Ioc.Default.GetService<ThemeColorService>();
        _highlightService = Ioc.Default.GetService<NowPlayingHighlightService>();
        InitializeComponent();
        // Cards are always interactive (click navigates or opens). Set the hand cursor
        // once on construction — the system shows it on hover automatically as long as
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
        // an ancestor scroller changes — including the first measurement
        // after re-attach — and lets us reload the cached bitmap on demand.
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
        // — avoiding ~310 per-card messenger Register calls during HomePage realization.
        if (_highlightService != null)
        {
            _highlightService.CurrentChanged += OnHighlightServiceChanged;
            // Apply the current snapshot immediately so newly-realized cards reflect playback state.
            var (contextUri, albumUri, playing) = _highlightService.Current;
            ApplyHighlight(contextUri, albumUri, playing);
        }
        LoadImage(ImageUrl);
        SyncInitialPlaybackState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ResetInteractionState(updatePlayingState: false);
        StopPendingBeam();

        // Unsubscribe from the highlight service — strong event, explicit unsubscribe required.
        if (_highlightService != null)
            _highlightService.CurrentChanged -= OnHighlightServiceChanged;

        // Clean up SizeChanged subscription to prevent memory leaks
        if (CircleImageContainer != null && _circleSizeHandlerAttached)
        {
            CircleImageContainer.SizeChanged -= OnCircleContainerSizeChanged;
            _circleSizeHandlerAttached = false;
        }

        if (SquareImage != null)
            SquareImage.Source = null;
        if (CircleImageBrush != null)
            CircleImageBrush.ImageSource = null;

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

    // ── Now-playing self-management (via shared NowPlayingHighlightService) ──

    private void OnHighlightServiceChanged(string? contextUri, string? albumUri, bool playing)
        => ApplyHighlight(contextUri, albumUri, playing);

    private void ApplyHighlight(string? contextUri, string? albumUri, bool playing)
    {
        // Do the cheap string comparison BEFORE scheduling a dispatcher callback.
        // This avoids queuing 20-50 TryEnqueue calls when only 0-1 cards actually match.
        var navUri = NavigationUri; // read once — safe, DependencyProperty reads are thread-safe for strings
        // Match on context OR album URI — so an album card lights up whenever the
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

    // ── Property changed callbacks ──

    private static void OnImageUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var url = e.NewValue as string;
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

    private static void OnCenterTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var center = (bool)e.NewValue;
        card.TitleText.HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        card.SubtitleText.HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        card.TitleText.TextAlignment = center ? Microsoft.UI.Xaml.TextAlignment.Center : Microsoft.UI.Xaml.TextAlignment.Left;
        card.SubtitleText.TextAlignment = center ? Microsoft.UI.Xaml.TextAlignment.Center : Microsoft.UI.Xaml.TextAlignment.Left;
    }

    // ── Image loading ──

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        // Cheap short-circuit: 99% of fires are scroll noise on already-loaded
        // cards. Only act when the image was nulled (by OnUnloaded) and we
        // have a URL to reload.
        if (string.IsNullOrEmpty(ImageUrl)) return;
        var needsLoad = IsCircularImage
            ? CircleImageBrush?.ImageSource == null
            : SquareImage?.Source == null;
        if (!needsLoad) return;
        LoadImage(ImageUrl);
    }

    private void LoadImage(string? url)
    {
        // Guard: template may not be applied yet
        if (SquareImage == null) return;

        // Show placeholders — they sit on top of the image via z-order
        // Image stays Visible (Collapsed causes unload on scroll)
        SquarePlaceholderIcon.Visibility = Visibility.Visible;
        // Only touch circle placeholder if the circle subtree has been realized.
        if (CirclePlaceholderIcon != null)
            CirclePlaceholderIcon.Visibility = Visibility.Visible;

        // Clear any previous bitmap on every load. Without this, a recycled
        // ItemsRepeater container whose new item has no ImageUrl (e.g. Liked Songs,
        // or any item using a spotify:mosaic: URI that ToHttpsUrl can't resolve)
        // would keep showing the previous item's bitmap.
        SquareImage.Source = null;
        if (CircleImageBrush != null)
            CircleImageBrush.ImageSource = null;

        if (string.IsNullOrEmpty(url)) return;

        var httpsUrl = Helpers.SpotifyImageHelper.ToHttpsUrl(url);
        if (string.IsNullOrEmpty(httpsUrl)) return;

        // Use the shared LRU bitmap cache via DI
        var bitmap = _imageCache?.GetOrCreate(httpsUrl, 200) ?? new BitmapImage(new Uri(httpsUrl)) { DecodePixelWidth = 200, DecodePixelType = DecodePixelType.Logical };

        if (IsCircularImage)
        {
            EnsureCircleRealized();
            CircleImageBrush!.ImageSource = bitmap;
            // Hide placeholder — image renders via ImageBrush on the Ellipse
            CirclePlaceholderIcon!.Visibility = Visibility.Collapsed;
        }
        else
        {
            SquareImage.Source = bitmap;
            // Cached BitmapImage instances may already be opened, so ImageOpened
            // will not always fire again when a virtualized card is recycled.
            SquarePlaceholderIcon.Visibility = Visibility.Collapsed;
        }
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
        }
    }

    private void OnCircleContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Make circle diameter = container width (minus a small margin)
        var size = Math.Max(60, e.NewSize.Width - 16);
        if (ImageSize > 0) size = ImageSize;
        CirclePlaceholder.Width = size;
        CirclePlaceholder.Height = size;
        CircleImage.Width = size;
        CircleImage.Height = size;
    }

    private void SquareImageContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the image area square: height = width
        if (e.NewSize.Width > 0)
            SquareImageContainer.Height = e.NewSize.Width;
    }

    private void SquareImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        SquarePlaceholderIcon.Visibility = Visibility.Collapsed;

        // Fade in using XAML framework layer (not composition — avoids layer multiply bugs)
        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Opacity(from: 0, to: 1,
                     duration: TimeSpan.FromMilliseconds(250),
                     layer: CommunityToolkit.WinUI.Animations.FrameworkLayer.Xaml)
            .Start(SquareImage);
    }

    private void SquareImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SquareImage.Source = null;
        SquarePlaceholderIcon.Visibility = Visibility.Visible;
    }

    // ── Hover handling ──

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        CardHover?.Invoke(this, EventArgs.Empty);

        // Realize the overlay for the current shape before reading the named elements —
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

        // Scale up via composition with proper CenterPoint
        if (CardRoot != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Scale(from: System.Numerics.Vector3.One, to: new System.Numerics.Vector3(1.03f), duration: TimeSpan.FromMilliseconds(200))
                .Start(CardRoot);
        }

        if (CardShadow != null)
            CardShadow.Opacity = 0.25;
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

        UpdatePlayingState();

        if (CardRoot != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.CenterPoint = new System.Numerics.Vector3((float)CardRoot.ActualWidth / 2, (float)CardRoot.ActualHeight / 2, 0);

            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Scale(from: new System.Numerics.Vector3(1.03f), to: System.Numerics.Vector3.One, duration: TimeSpan.FromMilliseconds(200))
                .Start(CardRoot);
        }

        if (CardShadow != null)
            CardShadow.Opacity = 0;
    }

    // ── Press animation ──

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

    // ── Passive mode ──

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

    // ── Playing state ──

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

        var showSquarePlaying = (isPlaying || isPaused) && !isPending && !IsCircularImage;
        var showCirclePlaying = (isPlaying || isPaused) && !isPending && IsCircularImage;
        var showPlayButton = _isPointerOver || isPaused || isPending;

        if (showSquarePlaying && SquarePlayingIndicator == null)
            this.FindName("SquarePlayingIndicator");
        if (showCirclePlaying)
            EnsureCircleRealized();
        if (showPlayButton)
            EnsurePlayOverlayRealized();

        // Null-guard every access — all overlays are x:Load-deferred, so any of them
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
            // External cards have no play / pending / paused notion — overlay
            // visibility is purely hover-driven, handled in Card_PointerEntered/Exited.
            // Keep the play button collapsed in case a card flipped from non-external
            // to external while realized.
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

    // ── Click handlers ──

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

        // Self-navigation: if NavigationUri is set, navigate directly
        if (!string.IsNullOrEmpty(NavigationUri))
        {
            // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
            // PrepareConnectedAnimation();
            ResetInteractionState();
            if (NavigateToUri(Helpers.Navigation.NavigationHelpers.IsCtrlPressed()))
                return;
        }

        CardClick?.Invoke(this, EventArgs.Empty);
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
            return;
        }
        CardRightTapped?.Invoke(this, e);
    }

    // ── Navigation ──

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
            ImageUrl = ImageUrl
        };

        switch (type)
        {
            case "artist":
                Helpers.Navigation.NavigationHelpers.OpenArtist(param, title, openInNewTab);
                return true;
            case "album":
                Helpers.Navigation.NavigationHelpers.OpenAlbum(param, title, openInNewTab);
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
            case "show":
                Helpers.Navigation.NavigationHelpers.OpenShow(uri, title, openInNewTab);
                return true;
            case "episode":
                Helpers.Navigation.NavigationHelpers.PlayEpisode(uri);
                return true;
        }

        return false;
    }

    internal void PrepareConnectedAnimation()
    {
        // CONNECTED-ANIM (disabled): re-enable to restore source→destination morph
        // var uri = NavigationUri;
        // if (string.IsNullOrEmpty(uri)) return;
        //
        // var parts = uri.Split(':');
        // if (parts.Length < 2) return;
        //
        // var type = parts[1];
        // var imageElement = IsCircularImage
        //     ? (UIElement)CircleImageContainer
        //     : (UIElement)SquareImageContainer;
        //
        // var key = type switch
        // {
        //     "artist" => Helpers.ConnectedAnimationHelper.ArtistImage,
        //     "album" => Helpers.ConnectedAnimationHelper.AlbumArt,
        //     "playlist" => Helpers.ConnectedAnimationHelper.PlaylistArt,
        //     _ => null
        // };
        //
        // if (key != null)
        //     Helpers.ConnectedAnimationHelper.PrepareAnimation(key, imageElement);
    }

    // ── Helpers ──

    private void EnsurePlayOverlayRealized()
    {
        if (IsExternal)
        {
            // External cards never need play / now-playing chrome — only the
            // "open in browser" overlay. Square is the only supported shape today.
            if (SquareExternalButton == null)
                this.FindName("SquareExternalButton");
            return;
        }

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

        if (CardShadow != null)
            CardShadow.Opacity = 0;

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
    /// Idempotent — returns early if the container is already realized.
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
