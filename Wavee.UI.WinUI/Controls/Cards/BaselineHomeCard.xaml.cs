using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI.Animations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;
using Microsoft.UI;
using Wavee.Playback.Contracts;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.Services;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Cards;

public sealed partial class BaselineHomeCard : UserControl
{
    private const double CardAspectRatio = 200d / 350d;

    private static BaselineHomeCard? s_activeCard;
    private static BaselineHomeCard? s_activeAudioCard;
    private static bool s_isPreviewAudioAutoPlayEnabled;

    private readonly ImageCacheService? _imageCache;
    private readonly PreviewAudioVisualizationCoordinator? _previewVisualizationCoordinator;
    private MediaPlayer? _canvasMediaPlayer;
    private MediaPlayer? _previewAudioPlayer;
    private HomeSectionItem? _subscribedItem;
    private bool _isPointerOver;
    private bool _isPreviewAudioPlaying;
    private bool _isApplyingResponsiveSize;
    private int _previewTrackIndex;
    private string? _previewVisualizationSessionId;
    private string? _previewVisualizationUrl;

    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(nameof(Item), typeof(HomeSectionItem), typeof(BaselineHomeCard),
            new PropertyMetadata(null, OnItemChanged));

    public HomeSectionItem? Item
    {
        get => (HomeSectionItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public BaselineHomeCard()
    {
        _imageCache = Ioc.Default.GetService<ImageCacheService>();
        _previewVisualizationCoordinator = Ioc.Default.GetService<PreviewAudioVisualizationCoordinator>();
        InitializeComponent();
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (BaselineHomeCard)d;
        card.SetSubscribedItem(e.OldValue as HomeSectionItem, e.NewValue as HomeSectionItem);
        card._previewTrackIndex = 0;
        card.StopHoverMedia();
        card.StopPreviewAudio();
        card.UpdateFromItem();
    }

    private void SetSubscribedItem(HomeSectionItem? oldItem, HomeSectionItem? newItem)
    {
        if (oldItem != null)
            oldItem.PropertyChanged -= Item_PropertyChanged;

        _subscribedItem = newItem;

        if (newItem != null)
            newItem.PropertyChanged += Item_PropertyChanged;
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateFromItem);
    }

    private void UpdateFromItem()
    {
        var item = Item;
        if (item == null)
            return;

        ClampPreviewTrackIndex(item);
        var activePreviewTrack = GetActivePreviewTrack(item);

        TitleText.Text = item.Title ?? "";
        SubtitleText.Text = item.Subtitle ?? "";
        TypeText.Text = item.ContentType switch
        {
            HomeContentType.Album => "Album",
            HomeContentType.Playlist => "Playlist",
            HomeContentType.Podcast => "Podcast",
            _ => "Made for you"
        };

        var previewTrackName = activePreviewTrack?.Name;
        PreviewEyebrowText.Visibility = string.IsNullOrWhiteSpace(previewTrackName)
            ? Visibility.Collapsed
            : Visibility.Visible;
        PreviewTrackText.Text = previewTrackName ?? "";
        PreviewTrackText.Visibility = string.IsNullOrWhiteSpace(previewTrackName)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl(item, activePreviewTrack));
        PreviewOverlayRoot.Visibility = _isPointerOver && hasPreviewAudio
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Visibility = _isPointerOver && hasPreviewAudio
                ? Visibility.Visible
                : Visibility.Collapsed;
            PreviewVisualizer.SetActive(_isPointerOver && hasPreviewAudio);
        }

        if (PreviewAudioButton != null)
        {
            PreviewAudioButton.Visibility = _isPointerOver && hasPreviewAudio
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var hasMultiplePreviewTracks = item.PreviewTracks.Count > 1;
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = _isPointerOver && hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = _isPointerOver && hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;

        if (_isPointerOver)
        {
            if (!hasPreviewAudio)
            {
                StopPreviewVisualization();
            }
            else if (!string.Equals(_previewVisualizationUrl, GetActiveAudioPreviewUrl(item, activePreviewTrack), StringComparison.Ordinal))
            {
                StartPreviewVisualization();
            }

            if (hasPreviewAudio && s_isPreviewAudioAutoPlayEnabled && !_isPreviewAudioPlaying)
                _ = StartPreviewAudioAsync();
        }

        LoadImages(
            GetActiveHeroImageUrl(item, activePreviewTrack),
            activePreviewTrack?.CoverArtUrl ?? item.ImageUrl);
        ApplyColor(activePreviewTrack?.ColorHex ?? item.HeroColorHex ?? item.ColorHex);
        UpdateLoadingState(item.IsBaselineLoading);
    }

    private void LoadImages(string? heroUrl, string? thumbUrl)
    {
        var heroHttpsUrl = SpotifyImageHelper.ToHttpsUrl(heroUrl);
        if (string.IsNullOrWhiteSpace(heroHttpsUrl))
        {
            HeroImage.Source = null;
        }
        else
        {
            BitmapImage? heroImage = _imageCache?.GetOrCreate(heroHttpsUrl);
            heroImage ??= new BitmapImage(new Uri(heroHttpsUrl)) { DecodePixelType = DecodePixelType.Logical };
            HeroImage.Source = heroImage;
        }

        var thumbHttpsUrl = SpotifyImageHelper.ToHttpsUrl(thumbUrl);
        if (string.IsNullOrWhiteSpace(thumbHttpsUrl))
        {
            CoverThumbImage.Source = null;
            CoverThumbPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        BitmapImage? thumbImage = _imageCache?.GetOrCreate(thumbHttpsUrl);
        thumbImage ??= new BitmapImage(new Uri(thumbHttpsUrl)) { DecodePixelType = DecodePixelType.Logical };
        CoverThumbImage.Source = thumbImage;
        CoverThumbPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void ApplyColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            ColorWash.Background = null;
            BottomPanel.ClearValue(Border.BackgroundProperty);
            return;
        }

        var color = ParseHexColor(hex);
        ColorWash.Background = new SolidColorBrush(color);
        BottomPanel.Background = new SolidColorBrush(Darken(color, 0.66));
    }

    private void UpdateLoadingState(bool isLoading)
    {
        if (isLoading)
            EnsureShimmerRealized();

        if (ShimmerOverlay != null)
            ShimmerOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

        TitleOverlay.Opacity = isLoading ? 0 : 1;
        BottomPanel.Opacity = isLoading ? 0 : 1;
        CoverThumbBorder.Opacity = isLoading ? 0 : 1;
        HeroImage.Opacity = isLoading ? 0.18 : 0.92;
    }

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;

        if (s_activeCard != null && !ReferenceEquals(s_activeCard, this))
            s_activeCard.StopHoverMedia();

        s_activeCard = this;
        EnsureHoverChromeRealized();
        ApplyHoverState();
    }

    private void ApplyHoverState()
    {
        if (HoverChrome != null)
            HoverChrome.Visibility = Visibility.Visible;

        var hasPreviewAudio = !string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl());

        if (PreviewAudioButton != null)
            PreviewAudioButton.Visibility = hasPreviewAudio ? Visibility.Visible : Visibility.Collapsed;

        if (PreviewOverlayRoot != null)
            PreviewOverlayRoot.Visibility = hasPreviewAudio ? Visibility.Visible : Visibility.Collapsed;

        var hasMultiplePreviewTracks = (Item?.PreviewTracks.Count ?? 0) > 1;
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = hasMultiplePreviewTracks ? Visibility.Visible : Visibility.Collapsed;

        if (hasPreviewAudio)
            StartPreviewVisualization();

        if (hasPreviewAudio && s_isPreviewAudioAutoPlayEnabled && !_isPreviewAudioPlaying)
            _ = StartPreviewAudioAsync();

        if (HoverChrome != null)
        {
            AnimationBuilder.Create()
                .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(140))
                .Start(HoverChrome);
        }

        Canvas.SetZIndex(this, 10);
        var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
        visual.CenterPoint = new System.Numerics.Vector3(
            (float)(CardRoot.ActualWidth / 2),
            (float)(CardRoot.ActualHeight / 2),
            0);

        AnimationBuilder.Create()
            .Scale(from: System.Numerics.Vector3.One, to: new System.Numerics.Vector3(1.025f), duration: TimeSpan.FromMilliseconds(180))
            .Start(CardRoot);

        StartCanvasPreview();
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        StopHoverMedia();
    }

    private void StopHoverMedia()
    {
        _isPointerOver = false;
        StopCanvasPreview();
        StopPreviewVisualization();
        StopPreviewAudio();

        if (PreviewAudioButton != null)
            PreviewAudioButton.Visibility = Visibility.Collapsed;
        if (PreviewOverlayRoot != null)
            PreviewOverlayRoot.Visibility = Visibility.Collapsed;
        if (PreviousPreviewTrackButton != null)
            PreviousPreviewTrackButton.Visibility = Visibility.Collapsed;
        if (NextPreviewTrackButton != null)
            NextPreviewTrackButton.Visibility = Visibility.Collapsed;
        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Reset();
            PreviewVisualizer.SetActive(false);
            PreviewVisualizer.Visibility = Visibility.Collapsed;
        }

        if (IsLoaded)
        {
            if (HoverChrome != null)
            {
                AnimationBuilder.Create()
                    .Opacity(to: 0, duration: TimeSpan.FromMilliseconds(120))
                    .Start(HoverChrome);
            }

            var visual = ElementCompositionPreview.GetElementVisual(CardRoot);
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)(CardRoot.ActualWidth / 2),
                (float)(CardRoot.ActualHeight / 2),
                0);

            AnimationBuilder.Create()
                .Scale(from: new System.Numerics.Vector3(1.025f), to: System.Numerics.Vector3.One, duration: TimeSpan.FromMilliseconds(160))
                .Start(CardRoot);

            _ = CollapseHoverChromeAsync();
            Canvas.SetZIndex(this, 0);
        }

        if (ReferenceEquals(s_activeCard, this))
            s_activeCard = null;
    }

    private async Task CollapseHoverChromeAsync()
    {
        await Task.Delay(150);
        if (!_isPointerOver && IsLoaded && HoverChrome != null)
            HoverChrome.Visibility = Visibility.Collapsed;
    }

    private void StartCanvasPreview()
    {
        var canvasUrl = GetActiveCanvasUrl();
        if (string.IsNullOrWhiteSpace(canvasUrl))
        {
            StopCanvasPreview();
            return;
        }

        try
        {
            EnsureCanvasPlayerRealized();
            if (CanvasPlayer == null)
                return;

            _canvasMediaPlayer ??= new MediaPlayer
            {
                IsLoopingEnabled = true,
                IsMuted = true
            };

            CanvasPlayer.Stretch = Stretch.UniformToFill;
            CanvasPlayer.SetMediaPlayer(_canvasMediaPlayer);
            CanvasPlayer.Visibility = Visibility.Visible;
            CanvasPlayer.Opacity = 0;

            _canvasMediaPlayer.Source = MediaSource.CreateFromUri(new Uri(canvasUrl));
            _canvasMediaPlayer.Play();

            AnimationBuilder.Create()
                .Opacity(to: 1, duration: TimeSpan.FromMilliseconds(400),
                    delay: TimeSpan.FromMilliseconds(250),
                    easingMode: Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut)
                .Start(CanvasPlayer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Canvas preview failed: {ex.Message}");
            StopCanvasPreview();
        }
    }

    private void StopCanvasPreview()
    {
        if (_canvasMediaPlayer == null)
        {
            if (CanvasPlayer != null)
            {
                CanvasPlayer.Visibility = Visibility.Collapsed;
                CanvasPlayer.Opacity = 0;
            }
            return;
        }

        _canvasMediaPlayer.Pause();
        _canvasMediaPlayer.Source = null;
        if (CanvasPlayer != null)
        {
            CanvasPlayer.Visibility = Visibility.Collapsed;
            CanvasPlayer.Opacity = 0;
        }
    }

    private void StartPreviewVisualization()
    {
        var previewUrl = GetActiveAudioPreviewUrl();
        if (!_isPointerOver || string.IsNullOrWhiteSpace(previewUrl))
            return;

        StopPreviewVisualization();

        EnsurePreviewVisualizerRealized();
        if (PreviewVisualizer != null)
        {
            PreviewVisualizer.Visibility = Visibility.Visible;
            PreviewVisualizer.Reset();
            PreviewVisualizer.SetActive(true);
        }

        _previewVisualizationUrl = previewUrl;
        _previewVisualizationSessionId = _previewVisualizationCoordinator?.Activate(previewUrl, OnPreviewVisualizationFrame);
    }

    private void StopPreviewVisualization()
    {
        var sessionId = _previewVisualizationSessionId;
        _previewVisualizationSessionId = null;
        _previewVisualizationUrl = null;

        if (!string.IsNullOrWhiteSpace(sessionId))
            _previewVisualizationCoordinator?.Deactivate(sessionId);
    }

    private void OnPreviewVisualizationFrame(PreviewVisualizationFrame frame)
    {
        if (!_isPointerOver || PreviewVisualizer == null)
            return;

        if (frame.Completed)
        {
            PreviewVisualizer.Reset();
            return;
        }

        PreviewVisualizer.PushLevels(frame.Amplitudes);
    }

    private void PreviewAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPreviewAudioPlaying)
        {
            s_isPreviewAudioAutoPlayEnabled = false;
            StopPreviewAudio();
            return;
        }

        s_isPreviewAudioAutoPlayEnabled = true;
        _ = StartPreviewAudioAsync();
    }

    private Task StartPreviewAudioAsync()
    {
        var previewUrl = GetActiveAudioPreviewUrl();
        if (string.IsNullOrWhiteSpace(previewUrl))
            return Task.CompletedTask;

        if (s_activeAudioCard != null && !ReferenceEquals(s_activeAudioCard, this))
            s_activeAudioCard.StopPreviewAudio();

        StopPreviewAudio();

        try
        {
            _previewAudioPlayer ??= new MediaPlayer
            {
                IsLoopingEnabled = false,
                IsMuted = false
            };

            _previewAudioPlayer.MediaEnded -= PreviewAudioPlayer_MediaEnded;
            _previewAudioPlayer.MediaEnded += PreviewAudioPlayer_MediaEnded;
            _previewAudioPlayer.Source = MediaSource.CreateFromUri(new Uri(previewUrl));
            _previewAudioPlayer.Play();

            _isPreviewAudioPlaying = true;
            s_activeAudioCard = this;

            if (PreviewAudioIcon != null)
                PreviewAudioIcon.Glyph = "\uE15D";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"[BaselineHomeCard] Audio preview failed: {ex.Message}");
            StopPreviewAudio();
        }

        return Task.CompletedTask;
    }

    private void StopPreviewAudio()
    {
        if (_previewAudioPlayer != null)
        {
            try
            {
                _previewAudioPlayer.MediaEnded -= PreviewAudioPlayer_MediaEnded;
                _previewAudioPlayer.Pause();
                _previewAudioPlayer.Source = null;
            }
            catch
            {
            }
        }

        _isPreviewAudioPlaying = false;
        if (ReferenceEquals(s_activeAudioCard, this))
            s_activeAudioCard = null;
        if (PreviewAudioIcon != null)
            PreviewAudioIcon.Glyph = "\uE198";
    }

    private void PreviewAudioPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(StopPreviewAudio);
    }

    private void PreviousPreviewTrackButton_Click(object sender, RoutedEventArgs e)
    {
        ChangePreviewTrack(-1);
    }

    private void NextPreviewTrackButton_Click(object sender, RoutedEventArgs e)
    {
        ChangePreviewTrack(1);
    }

    private void CardRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsPreviewButtonSource(e.OriginalSource))
            return;

        if (Item != null)
            HomeViewModel.NavigateToItem(Item, NavigationHelpers.IsCtrlPressed());
    }

    private void CardRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsPreviewButtonSource(e.OriginalSource))
            return;

        if (e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed && Item != null)
        {
            HomeViewModel.NavigateToItem(Item, openInNewTab: true);
            e.Handled = true;
        }
    }

    private void CardRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Item == null)
            return;

        var menu = new MenuFlyout();
        var openNewTab = new MenuFlyoutItem
        {
            Text = "Open in new tab",
            Icon = new SymbolIcon(Symbol.OpenWith)
        };
        openNewTab.Click += (_, _) => HomeViewModel.NavigateToItem(Item, openInNewTab: true);
        menu.Items.Add(openNewTab);
        menu.ShowAt(this, e.GetPosition(this));
        e.Handled = true;
    }

    private bool IsPreviewButtonSource(object? source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (ReferenceEquals(current, PreviewAudioButton) ||
                ReferenceEquals(current, PreviousPreviewTrackButton) ||
                ReferenceEquals(current, NextPreviewTrackButton))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        SetSubscribedItem(_subscribedItem, null);

        _isPointerOver = false;
        StopCanvasPreview();
        StopPreviewVisualization();
        StopPreviewAudio();

        if (ReferenceEquals(s_activeCard, this))
            s_activeCard = null;

        _canvasMediaPlayer?.Dispose();
        _canvasMediaPlayer = null;

        _previewAudioPlayer?.Dispose();
        _previewAudioPlayer = null;
    }

    private void BaselineHomeCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isApplyingResponsiveSize)
            return;

        var width = e.NewSize.Width;
        if (width <= 0)
            return;

        var targetHeight = width / CardAspectRatio;

        if (!double.IsNaN(Height) && Math.Abs(Height - targetHeight) < 1)
            return;

        _isApplyingResponsiveSize = true;
        Height = targetHeight;
        DispatcherQueue.TryEnqueue(() => _isApplyingResponsiveSize = false);
    }

    private void EnsureHoverChromeRealized()
    {
        if (HoverChrome != null)
            return;
        FindName("HoverChrome");
        UpdateLayout();
    }

    private void EnsureCanvasPlayerRealized()
    {
        if (CanvasPlayer != null)
            return;
        FindName("CanvasPlayer");
        UpdateLayout();
    }

    private void EnsureShimmerRealized()
    {
        if (ShimmerOverlay != null)
            return;
        FindName("ShimmerOverlay");
    }

    private void EnsurePreviewVisualizerRealized()
    {
        if (PreviewVisualizer != null)
            return;
    }

    private void ChangePreviewTrack(int delta)
    {
        var item = Item;
        if (item == null || item.PreviewTracks.Count <= 1)
            return;

        var wasPreviewAudioPlaying = _isPreviewAudioPlaying;
        _previewTrackIndex = (_previewTrackIndex + delta + item.PreviewTracks.Count) % item.PreviewTracks.Count;

        StopCanvasPreview();
        StopPreviewVisualization();
        StopPreviewAudio();

        UpdateFromItem();

        if (_isPointerOver)
        {
            StartCanvasPreview();
            if (!string.IsNullOrWhiteSpace(GetActiveAudioPreviewUrl(item, GetActivePreviewTrack(item))))
                StartPreviewVisualization();
        }

        if (wasPreviewAudioPlaying)
            _ = StartPreviewAudioAsync();
    }

    private void ClampPreviewTrackIndex(HomeSectionItem item)
    {
        if (item.PreviewTracks.Count == 0)
        {
            _previewTrackIndex = 0;
            return;
        }

        _previewTrackIndex = Math.Clamp(_previewTrackIndex, 0, item.PreviewTracks.Count - 1);
    }

    private HomeBaselinePreviewTrack? GetActivePreviewTrack(HomeSectionItem? item = null)
    {
        item ??= Item;
        if (item == null || item.PreviewTracks.Count == 0)
            return null;

        ClampPreviewTrackIndex(item);
        return item.PreviewTracks[_previewTrackIndex];
    }

    private string? GetActiveAudioPreviewUrl(HomeSectionItem? item = null, HomeBaselinePreviewTrack? track = null)
    {
        item ??= Item;
        track ??= GetActivePreviewTrack(item);
        return track?.AudioPreviewUrl ?? item?.AudioPreviewUrl;
    }

    private string? GetActiveCanvasUrl(HomeSectionItem? item = null, HomeBaselinePreviewTrack? track = null)
    {
        item ??= Item;
        track ??= GetActivePreviewTrack(item);
        return track?.CanvasUrl ?? item?.CanvasUrl;
    }

    private string? GetActiveHeroImageUrl(HomeSectionItem? item = null, HomeBaselinePreviewTrack? track = null)
    {
        item ??= Item;
        track ??= GetActivePreviewTrack(item);
        return track?.CanvasThumbnailUrl
            ?? track?.CoverArtUrl
            ?? item?.HeroImageUrl
            ?? item?.CanvasThumbnailUrl
            ?? item?.ImageUrl;
    }

    private static Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length switch
        {
            6 => Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16)),
            8 => Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16)),
            _ => Colors.Black
        };
    }

    private static Color Darken(Color color, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            color.A,
            (byte)Math.Clamp(color.R * amount, 0, 255),
            (byte)Math.Clamp(color.G * amount, 0, 255),
            (byte)Math.Clamp(color.B * amount, 0, 255));
    }
}
