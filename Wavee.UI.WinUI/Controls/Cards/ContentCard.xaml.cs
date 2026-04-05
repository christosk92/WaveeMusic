using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.Mvvm.DependencyInjection;
using Windows.Foundation;
using Windows.UI;
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
            new PropertyMetadata(null));

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

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(ContentCard),
            new PropertyMetadata(false, OnIsPlayingChanged));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    // ── Events ──

    public event EventHandler? CardClick;
    public event EventHandler? CardMiddleClick;
    public event EventHandler? CardHover;
    public event EventHandler? PlayRequested;
    public event TypedEventHandler<ContentCard, RightTappedRoutedEventArgs>? CardRightTapped;

    // ── Constructor ──

    private bool _passiveHandlersAdded;

    // Store handler references so RemoveHandler can match the exact instances
    private PointerEventHandler? _passivePointerEntered;
    private PointerEventHandler? _passivePointerExited;
    private PointerEventHandler? _passivePointerPressed;
    private PointerEventHandler? _passivePointerReleased;

    private readonly ImageCacheService? _imageCache;
    private readonly ThemeColorService? _themeColorService;

    public ContentCard()
    {
        _imageCache = Ioc.Default.GetService<ImageCacheService>();
        _themeColorService = Ioc.Default.GetService<ThemeColorService>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (IsPassive && !_passiveHandlersAdded)
        {
            _passiveHandlersAdded = true;

            // Apply passive state now that the control is in the visual tree
            if (CardButton != null)
                CardButton.IsHitTestVisible = false;

            // Register with handledEventsToo=true so we get pointer events
            // even when a parent ItemContainer marks them as handled.
            // Store references so RemoveHandler can match the exact instances.
            _passivePointerEntered = new PointerEventHandler(Card_PointerEntered);
            _passivePointerExited = new PointerEventHandler(Card_PointerExited);
            _passivePointerPressed = new PointerEventHandler(Card_PointerPressed);
            _passivePointerReleased = new PointerEventHandler(Card_PointerReleased);

            AddHandler(PointerEnteredEvent, _passivePointerEntered, true);
            AddHandler(PointerExitedEvent, _passivePointerExited, true);
            AddHandler(PointerPressedEvent, _passivePointerPressed, true);
            AddHandler(PointerReleasedEvent, _passivePointerReleased, true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Clean up SizeChanged subscription to prevent memory leaks
        if (CircleImageContainer != null)
            CircleImageContainer.SizeChanged -= OnCircleContainerSizeChanged;

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

    private static void OnPlaceholderColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.ApplyPlaceholderColor(e.NewValue as string);
    }

    private static void OnPlaceholderGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var glyph = e.NewValue as string ?? "\uE8D6";
        card.SquarePlaceholderIcon.Glyph = glyph;
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

    private void LoadImage(string? url)
    {
        // Guard: template may not be applied yet
        if (SquareImage == null) return;

        // Show placeholders — they sit on top of the image via z-order
        // Image stays Visible (Collapsed causes unload on scroll)
        SquarePlaceholderIcon.Visibility = Visibility.Visible;
        CirclePlaceholderIcon.Visibility = Visibility.Visible;

        if (string.IsNullOrEmpty(url)) return;

        var httpsUrl = Helpers.SpotifyImageHelper.ToHttpsUrl(url);
        if (string.IsNullOrEmpty(httpsUrl)) return;

        // Use the shared LRU bitmap cache via DI
        var bitmap = _imageCache?.GetOrCreate(httpsUrl, 200) ?? new BitmapImage(new Uri(httpsUrl)) { DecodePixelWidth = 200, DecodePixelType = DecodePixelType.Logical };

        if (IsCircularImage)
        {
            CircleImageBrush.ImageSource = bitmap;
            // Hide placeholder — image renders via ImageBrush on the Ellipse
            CirclePlaceholderIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            SquareImage.Source = bitmap;
            // ImageOpened on the Image control will handle fade-in
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

        // Also apply to circle placeholder
        if (CirclePlaceholder.Fill is SolidColorBrush)
            CirclePlaceholder.Fill = new SolidColorBrush(color) { Opacity = 0.3 };
    }

    private void UpdateImageMode()
    {
        if (SquareImageContainer == null) return; // template not applied yet

        if (IsCircularImage)
        {
            SquareImageContainer.Visibility = Visibility.Collapsed;
            CircleImageContainer.Visibility = Visibility.Visible;
            // Size will be set dynamically based on card width via SizeChanged
            CircleImageContainer.SizeChanged += OnCircleContainerSizeChanged;
        }
        else
        {
            SquareImageContainer.Visibility = Visibility.Visible;
            CircleImageContainer.Visibility = Visibility.Collapsed;
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

    // ── Hover handling ──

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        CardHover?.Invoke(this, EventArgs.Empty);

        var playBtn = IsCircularImage ? CirclePlayButton : SquarePlayButton;
        if (playBtn != null)
        {
            playBtn.Visibility = Visibility.Visible;
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(from: 0, to: 1, duration: TimeSpan.FromMilliseconds(150))
                .Start(playBtn);
        }

        // Scale up via composition with proper CenterPoint
        if (CardBorder != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardBorder);
            visual.CenterPoint = new System.Numerics.Vector3((float)CardBorder.ActualWidth / 2, (float)CardBorder.ActualHeight / 2, 0);

            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Scale(from: System.Numerics.Vector3.One, to: new System.Numerics.Vector3(1.03f), duration: TimeSpan.FromMilliseconds(200))
                .Start(CardBorder);
        }

        if (CardShadow != null)
            CardShadow.Opacity = 0.25;
    }

    private async void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        var playBtn = IsCircularImage ? CirclePlayButton : SquarePlayButton;
        if (playBtn != null)
        {
            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(100))
                .Start(playBtn);

            // Collapse after fade-out to reset for next hover
            await System.Threading.Tasks.Task.Delay(120);
            playBtn.Visibility = Visibility.Collapsed;
        }

        if (CardBorder != null)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardBorder);
            visual.CenterPoint = new System.Numerics.Vector3((float)CardBorder.ActualWidth / 2, (float)CardBorder.ActualHeight / 2, 0);

            CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
                .Scale(from: new System.Numerics.Vector3(1.03f), to: System.Numerics.Vector3.One, duration: TimeSpan.FromMilliseconds(200))
                .Start(CardBorder);
        }

        if (CardShadow != null)
            CardShadow.Opacity = 0;
    }

    // ── Press animation ──

    private void Card_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (CardBorder == null) return;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardBorder);
        visual.CenterPoint = new System.Numerics.Vector3((float)CardBorder.ActualWidth / 2, (float)CardBorder.ActualHeight / 2, 0);

        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Scale(to: new System.Numerics.Vector3(0.96f), duration: TimeSpan.FromMilliseconds(100))
            .Start(CardBorder);
    }

    private void Card_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (CardBorder == null) return;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(CardBorder);
        visual.CenterPoint = new System.Numerics.Vector3((float)CardBorder.ActualWidth / 2, (float)CardBorder.ActualHeight / 2, 0);

        // Scale back to hover state (1.03) since pointer is still over the card
        CommunityToolkit.WinUI.Animations.AnimationBuilder.Create()
            .Scale(to: new System.Numerics.Vector3(1.03f), duration: TimeSpan.FromMilliseconds(150))
            .Start(CardBorder);
    }

    // ── Passive mode ──

    private static void OnIsPassiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        var passive = (bool)e.NewValue;
        if (card.CardButton != null)
            card.CardButton.IsHitTestVisible = !passive;
    }

    // ── Playing state ──

    private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (ContentCard)d;
        card.UpdatePlayingState();
    }

    private void UpdatePlayingState()
    {
        if (SquarePlayingIndicator == null) return;

        var isPlaying = IsPlaying;
        SquarePlayingIndicator.Visibility = isPlaying && !IsCircularImage ? Visibility.Visible : Visibility.Collapsed;
        CirclePlayingIndicator.Visibility = isPlaying && IsCircularImage ? Visibility.Visible : Visibility.Collapsed;

        // Accent color on title when playing
        if (isPlaying)
            TitleText.Foreground = _themeColorService?.AccentText
                ?? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        else
            TitleText.ClearValue(TextBlock.ForegroundProperty);
    }

    // ── Click handlers ──

    private void CardButton_Click(object sender, RoutedEventArgs e)
    {
        // Self-navigation: if NavigationUri is set, navigate directly
        if (!string.IsNullOrEmpty(NavigationUri))
        {
            PrepareConnectedAnimation();
            NavigateToUri(Helpers.Navigation.NavigationHelpers.IsCtrlPressed());
            return;
        }

        CardClick?.Invoke(this, EventArgs.Empty);
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        PlayRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CardButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
        {
            if (!string.IsNullOrEmpty(NavigationUri))
            {
                NavigateToUri(openInNewTab: true);
                return;
            }
            CardMiddleClick?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CardButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(NavigationUri))
        {
            var menu = new MenuFlyout();
            var openNewTab = new MenuFlyoutItem
            {
                Text = "Open in new tab",
                Icon = new SymbolIcon(Symbol.OpenWith)
            };
            openNewTab.Click += (_, _) => NavigateToUri(openInNewTab: true);
            menu.Items.Add(openNewTab);
            menu.ShowAt(this, e.GetPosition(this));
            return;
        }
        CardRightTapped?.Invoke(this, e);
    }

    // ── Navigation ──

    private void NavigateToUri(bool openInNewTab)
    {
        var uri = NavigationUri!;
        var parts = uri.Split(':');
        if (parts.Length < 3) return;

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
                break;
            case "album":
                Helpers.Navigation.NavigationHelpers.OpenAlbum(param, title, openInNewTab);
                break;
            case "playlist":
                Helpers.Navigation.NavigationHelpers.OpenPlaylist(param, title, openInNewTab);
                break;
        }
    }

    internal void PrepareConnectedAnimation()
    {
        var uri = NavigationUri;
        if (string.IsNullOrEmpty(uri)) return;

        var parts = uri.Split(':');
        if (parts.Length < 2) return;

        var type = parts[1];
        var imageElement = IsCircularImage
            ? (UIElement)CircleImageContainer
            : (UIElement)SquareImageContainer;

        var key = type switch
        {
            "artist" => Helpers.ConnectedAnimationHelper.ArtistImage,
            "album" => Helpers.ConnectedAnimationHelper.AlbumArt,
            "playlist" => Helpers.ConnectedAnimationHelper.PlaylistArt,
            _ => null
        };

        if (key != null)
            Helpers.ConnectedAnimationHelper.PrepareAnimation(key, imageElement);
    }

    // ── Helpers ──

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
