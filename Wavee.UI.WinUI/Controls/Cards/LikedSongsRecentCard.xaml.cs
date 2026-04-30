using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Wavee.UI.WinUI.Helpers;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Controls.Cards;

/// <summary>
/// Recents card for the Liked Songs entity (<c>spotify:collection:tracks</c>)
/// when Spotify reports a "saved" event. The foreground purple-heart tile
/// stays in XAML; the three fanned recently-added album thumbnails are
/// rendered via WinUI Composition (one <see cref="SpriteVisual"/> per cover
/// inside a <see cref="ContainerVisual"/> attached to <c>ThumbnailHost</c>).
///
/// Composition over XAML for the thumbnails because:
///   1. Rotated XAML Borders shimmer on their edges (XAML compositor pixel
///      grid). SpriteVisual rasterizes once at the native size.
///   2. <see cref="DropShadow"/> per visual gives the depth Spotify's render
///      has; ThemeShadow on three rotated XAML Borders is heavy and awkward.
/// </summary>
public sealed partial class LikedSongsRecentCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(LikedSongsRecentCard),
            new PropertyMetadata(null, OnTitleChanged));

    public static readonly DependencyProperty AddedCountProperty =
        DependencyProperty.Register(nameof(AddedCount), typeof(int?), typeof(LikedSongsRecentCard),
            new PropertyMetadata(null, OnAddedCountChanged));

    public static readonly DependencyProperty Thumbnail1ImageUrlProperty =
        DependencyProperty.Register(nameof(Thumbnail1ImageUrl), typeof(string), typeof(LikedSongsRecentCard),
            new PropertyMetadata(null, (d, _) => ((LikedSongsRecentCard)d).RebuildThumbnails()));

    public static readonly DependencyProperty Thumbnail2ImageUrlProperty =
        DependencyProperty.Register(nameof(Thumbnail2ImageUrl), typeof(string), typeof(LikedSongsRecentCard),
            new PropertyMetadata(null, (d, _) => ((LikedSongsRecentCard)d).RebuildThumbnails()));

    public static readonly DependencyProperty Thumbnail3ImageUrlProperty =
        DependencyProperty.Register(nameof(Thumbnail3ImageUrl), typeof(string), typeof(LikedSongsRecentCard),
            new PropertyMetadata(null, (d, _) => ((LikedSongsRecentCard)d).RebuildThumbnails()));

    public static readonly DependencyProperty NavigationUriProperty =
        DependencyProperty.Register(nameof(NavigationUri), typeof(string), typeof(LikedSongsRecentCard),
            new PropertyMetadata(null));

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public int? AddedCount
    {
        get => (int?)GetValue(AddedCountProperty);
        set => SetValue(AddedCountProperty, value);
    }

    public string? Thumbnail1ImageUrl
    {
        get => (string?)GetValue(Thumbnail1ImageUrlProperty);
        set => SetValue(Thumbnail1ImageUrlProperty, value);
    }

    public string? Thumbnail2ImageUrl
    {
        get => (string?)GetValue(Thumbnail2ImageUrlProperty);
        set => SetValue(Thumbnail2ImageUrlProperty, value);
    }

    public string? Thumbnail3ImageUrl
    {
        get => (string?)GetValue(Thumbnail3ImageUrlProperty);
        set => SetValue(Thumbnail3ImageUrlProperty, value);
    }

    public string? NavigationUri
    {
        get => (string?)GetValue(NavigationUriProperty);
        set => SetValue(NavigationUriProperty, value);
    }

    // ── Composition state ───────────────────────────────────────────
    // Built lazily on first Loaded; rebuilt whenever a thumbnail URL DP
    // changes or the host size changes. Disposed on Unloaded.
    private Compositor? _compositor;
    private ContainerVisual? _thumbnailContainer;
    private Visual? _heartVisual;
    private readonly List<LoadedImageSurface> _surfaces = new(3);
    private readonly List<SpriteVisual> _sprites = new(3);
    private bool _isLoaded;
    private bool _isHovered;
    private float _hostWidth;

    // Per-slot fan layout — back-to-front. Each slot has a rest pose (matches
    // Spotify's tight stack where thumbnails barely peek behind the heart)
    // and a hover pose (the fan spreads out and the heart steps left so the
    // covers become fully visible). Composition animations interpolate the
    // rotation + offset between the two.
    //
    // Anchor: top-right of ThumbnailHost. Each Offset is relative to that
    // anchor (subtracted from hostWidth) so the layout is width-responsive.
    private readonly record struct SlotPose(float Size, float Rotation, Vector2 Offset);

    // Deliberate fan: identical size + consistent rotation step + consistent
    // translation step. Reads as a deck of cards gently splayed rather than a
    // random pile.
    //
    // Anchor: BOTTOM-LEFT of the host. Pose.Offset.X = pixels from the LEFT
    // edge; Pose.Offset.Y = pixels UP from the BOTTOM edge (positive = higher).
    // This puts the fan in the same lower band as the heart so they read as
    // one composition (not opposite corners with empty space between).
    //
    // Slot 1 = back-most of the deck; Slot 3 = front-most (rightmost, highest).
    private const float ThumbSize = 84f;

    // Anchored to the LEFT edge — fan hugs the left side of the card so it
    // shares the same vertical band as the heart and never extends past the
    // card's right edge into a neighbour's slot.
    private static readonly SlotPose[] RestLayout =
    [
        // Back of stack — at the left edge, partly behind heart
        new(ThumbSize,  -3f, new Vector2( 0f, 22f)),
        // Middle — small step right + up
        new(ThumbSize,   0f, new Vector2(14f, 30f)),
        // Front — rightmost of the cluster, highest
        new(ThumbSize,   3f, new Vector2(28f, 38f)),
    ];

    private static readonly SlotPose[] HoverLayout =
    [
        // Subtle spread — fan opens slightly to the right but the leftmost
        // slot stays anchored to the edge. Stays inside the card width.
        new(ThumbSize,  -7f, new Vector2( 0f, 24f)),
        new(ThumbSize,   1f, new Vector2(22f, 34f)),
        new(ThumbSize,   9f, new Vector2(46f, 46f)),
    ];

    // Heart tile hover pose: translate down + scale down so it gracefully
    // steps aside and lets the fanned covers take the spotlight.
    private const float HeartHoverTranslateX = -4f;
    private const float HeartHoverTranslateY = 8f;
    private const float HeartHoverScale = 0.75f;
    private static readonly TimeSpan HoverDuration = TimeSpan.FromMilliseconds(280);

    public LikedSongsRecentCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        EnsureCompositionGraph();
        RebuildThumbnails();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        DisposeCompositionGraph();
    }

    private void ImageArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the image area square — Recents card slot is sized by the
        // outer SingleRowLayout / ShelfScroller (width-driven). Without this
        // the row collapses to whatever the heart tile + text minimum is.
        if (e.NewSize.Width > 0 && Math.Abs(ImageArea.Height - e.NewSize.Width) > 0.5)
            ImageArea.Height = e.NewSize.Width;
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (LikedSongsRecentCard)d;
        if (card.TitleText != null)
            card.TitleText.Text = (string?)e.NewValue ?? "Liked Songs";
    }

    private static void OnAddedCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (LikedSongsRecentCard)d;
        var count = (int?)e.NewValue;
        if (card.SubtitleText != null)
            card.SubtitleText.Text = count.HasValue
                ? $"{count} songs added"
                : "Songs added";
        if (card.AddedCheckGlyph != null)
            card.AddedCheckGlyph.Visibility = Visibility.Visible;
    }

    // ── Composition graph ─────────────────────────────────────────────

    private void EnsureCompositionGraph()
    {
        if (_thumbnailContainer != null) return;

        var hostVisual = ElementCompositionPreview.GetElementVisual(ThumbnailHost);
        _compositor = hostVisual.Compositor;
        _thumbnailContainer = _compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(ThumbnailHost, _thumbnailContainer);

        // Pull the heart's backing Visual so we can animate its translation +
        // scale on hover. Translation (vs. Offset) is the right knob here —
        // it's a delta that stacks on top of the XAML-assigned layout position
        // instead of replacing it. Must be enabled per-element first.
        ElementCompositionPreview.SetIsTranslationEnabled(HeartTile, true);
        _heartVisual = ElementCompositionPreview.GetElementVisual(HeartTile);
        _heartVisual.CenterPoint = new Vector3(
            (float)HeartTile.Width / 2f,
            (float)HeartTile.Height / 2f,
            0f);
    }

    private void RebuildThumbnails()
    {
        if (!_isLoaded || _thumbnailContainer == null || _compositor == null)
            return;

        // Clear existing
        _thumbnailContainer.Children.RemoveAll();
        foreach (var sprite in _sprites)
            sprite.Dispose();
        _sprites.Clear();
        foreach (var surface in _surfaces)
            surface.Dispose();
        _surfaces.Clear();

        var urls = new[] { Thumbnail1ImageUrl, Thumbnail2ImageUrl, Thumbnail3ImageUrl };

        // The fan anchors at the top-right of ThumbnailHost — derive the
        // host width from a cached size or fall back to the layout slot.
        // CenterPoint = size/2 so rotation pivots on the visual's center.
        var hostWidth = (float)(ThumbnailHost.ActualWidth > 0 ? ThumbnailHost.ActualWidth : ImageArea.ActualWidth);
        if (hostWidth <= 0)
        {
            // Layout hasn't run yet — defer to next pass.
            ThumbnailHost.SizeChanged += DeferredHostSized;
            return;
        }

        _hostWidth = hostWidth;

        // Always render all 3 fan slots — even when the URL hasn't arrived
        // yet, the slot shows a placeholder colored sprite so the card never
        // reads as empty. As each LoadedImageSurface completes, the placeholder
        // brush is swapped to the image. Failed loads keep the placeholder.
        var placeholderBrush = _compositor.CreateColorBrush(
            Color.FromArgb(255, 0x2C, 0x2C, 0x32));

        for (var i = 0; i < RestLayout.Length; i++)
        {
            var url = urls[i];
            var pose = _isHovered ? HoverLayout[i] : RestLayout[i];
            var sprite = _compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(pose.Size);
            sprite.CenterPoint = new Vector3(pose.Size / 2f, pose.Size / 2f, 0f);
            sprite.RotationAngleInDegrees = pose.Rotation;
            sprite.Offset = ComputeOffset(pose);

            // Soft drop shadow per visual — depth that XAML couldn't easily do.
            var shadow = _compositor.CreateDropShadow();
            shadow.BlurRadius = 8f;
            shadow.Offset = new Vector3(0f, 2f, 0f);
            shadow.Color = Color.FromArgb(180, 0, 0, 0);
            sprite.Shadow = shadow;

            // Start with the placeholder brush so the slot shows immediately;
            // swap to a SurfaceBrush once the image actually finishes loading.
            sprite.Brush = placeholderBrush;

            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    var httpsUrl = SpotifyImageHelper.ToHttpsUrl(url) ?? url;
                    var surface = LoadedImageSurface.StartLoadFromUri(new Uri(httpsUrl), new Size(ThumbSize, ThumbSize));
                    _surfaces.Add(surface);
                    var spriteRef = sprite;
                    surface.LoadCompleted += (sender, args) =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_compositor == null || spriteRef == null) return;
                            if (args.Status == LoadedImageSourceLoadStatus.Success)
                            {
                                var imgBrush = _compositor.CreateSurfaceBrush();
                                imgBrush.Stretch = CompositionStretch.UniformToFill;
                                imgBrush.Surface = sender;
                                spriteRef.Brush = imgBrush;
                            }
                            // On non-Success, leave the placeholder brush in
                            // place — the slot still reads as a card-coloured
                            // square peeking out behind the heart tile.
                        });
                    };
                }
                catch
                {
                    // Bad URI — placeholder brush stays.
                }
            }

            _sprites.Add(sprite);
            _thumbnailContainer.Children.InsertAtBottom(sprite);
        }
    }

    /// <summary>
    /// Translates a pose's BOTTOM-LEFT-anchored coordinates into the
    /// TOP-LEFT-anchored Offset that <see cref="SpriteVisual.Offset"/>
    /// expects. Host is square (height = width), enforced by ImageArea_SizeChanged.
    /// Pose.Offset.X is from the left; Pose.Offset.Y is from the BOTTOM
    /// (positive = up), which keeps the fan composition reading natural.
    /// </summary>
    private Vector3 ComputeOffset(SlotPose pose)
        => new(pose.Offset.X, _hostWidth - pose.Size - pose.Offset.Y, 0f);

    private void DeferredHostSized(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0) return;
        ThumbnailHost.SizeChanged -= DeferredHostSized;
        RebuildThumbnails();
    }

    private void DisposeCompositionGraph()
    {
        if (_thumbnailContainer != null)
        {
            ElementCompositionPreview.SetElementChildVisual(ThumbnailHost, null);
            foreach (var sprite in _sprites)
                sprite.Dispose();
            _sprites.Clear();
            _thumbnailContainer.Dispose();
            _thumbnailContainer = null;
        }
        foreach (var surface in _surfaces)
            surface.Dispose();
        _surfaces.Clear();
        _compositor = null;
    }

    // ── Pointer + nav ─────────────────────────────────────────────────

    private void CardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isHovered) return;
        _isHovered = true;
        // Float the whole card above its row neighbours so the popping-out
        // thumbnails draw over the next card to the right (not under it).
        // Canvas.ZIndex on the UserControl is the standard knob ItemsRepeater
        // honors for sibling draw order.
        Canvas.SetZIndex(this, 1);
        AnimateToHover(true);
    }

    private void CardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isHovered) return;
        _isHovered = false;
        AnimateToHover(false);
        // Drop ZIndex back so the card returns to natural row order. Done
        // immediately rather than at end-of-animation; the hover-out transit
        // is short enough that flipping z mid-anim isn't perceptible.
        Canvas.SetZIndex(this, 0);
    }

    /// <summary>
    /// Animates each thumbnail sprite + the heart tile between the rest pose
    /// (heart front-and-center, thumbnails barely peeking) and the hover pose
    /// (heart slides + scales aside, thumbnails fan out and become visible).
    /// All running on the compositor thread, so smooth even when the UI thread
    /// is busy populating the rest of the home shelf.
    /// </summary>
    private void AnimateToHover(bool toHover)
    {
        if (_compositor == null) return;

        var ease = _compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0f), new Vector2(0f, 1f));

        // Thumbnails — animate offset + rotation per slot.
        for (var i = 0; i < _sprites.Count && i < RestLayout.Length; i++)
        {
            var sprite = _sprites[i];
            var pose = toHover ? HoverLayout[i] : RestLayout[i];
            var targetOffset = ComputeOffset(pose);

            var offsetAnim = _compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(1f, targetOffset, ease);
            offsetAnim.Duration = HoverDuration;
            sprite.StartAnimation(nameof(SpriteVisual.Offset), offsetAnim);

            var rotAnim = _compositor.CreateScalarKeyFrameAnimation();
            rotAnim.InsertKeyFrame(1f, pose.Rotation, ease);
            rotAnim.Duration = HoverDuration;
            sprite.StartAnimation(nameof(SpriteVisual.RotationAngleInDegrees), rotAnim);
        }

        // Heart — translate + scale. "Translation" is the additive delta that
        // sits alongside the XAML-assigned layout offset (enabled in
        // EnsureCompositionGraph). Naming gotcha: animations target the
        // string "Translation", but the property is exposed via expression-
        // accessible name only — there's no clr-side getter on Visual.
        if (_heartVisual != null)
        {
            var heartTranslate = _compositor.CreateVector3KeyFrameAnimation();
            heartTranslate.InsertKeyFrame(
                1f,
                toHover ? new Vector3(HeartHoverTranslateX, HeartHoverTranslateY, 0f) : Vector3.Zero,
                ease);
            heartTranslate.Duration = HoverDuration;
            _heartVisual.StartAnimation("Translation", heartTranslate);

            var heartScale = _compositor.CreateVector3KeyFrameAnimation();
            heartScale.InsertKeyFrame(
                1f,
                toHover ? new Vector3(HeartHoverScale, HeartHoverScale, 1f) : Vector3.One,
                ease);
            heartScale.Duration = HoverDuration;
            _heartVisual.StartAnimation(nameof(Visual.Scale), heartScale);
        }
    }

    private void CardRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        NavigationHelpers.OpenLikedSongs(openInNewTab);
        e.Handled = true;
    }
}
