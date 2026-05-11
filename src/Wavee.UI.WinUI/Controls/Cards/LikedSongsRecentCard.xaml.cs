using System;
using System.Collections.Generic;
using System.Linq;
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
using Wavee.UI.WinUI.Styles;

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

    public static readonly DependencyProperty AddedItemNounProperty =
        DependencyProperty.Register(nameof(AddedItemNoun), typeof(string), typeof(LikedSongsRecentCard),
            new PropertyMetadata("song", OnAddedItemNounChanged));

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
            new PropertyMetadata(null, (d, _) => ((LikedSongsRecentCard)d).ApplyTileVariant()));

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

    public string AddedItemNoun
    {
        get => (string)GetValue(AddedItemNounProperty);
        set => SetValue(AddedItemNounProperty, value);
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
    // All dimensions are now expressed as RATIOS of host width and resolved
    // at runtime — the card is consumed by SingleRowLayout which sizes its
    // children by available row width, so a fixed-pixel composition stayed
    // tiny in the bottom-left on any card wider than the original ~140 px
    // target (the visual bug the user flagged).
    private readonly record struct SlotPose(float Size, float Rotation, Vector2 Offset);
    private readonly record struct SlotPoseRatio(float Rotation, float OffsetXRatio, float OffsetYRatio);

    // Each thumbnail's edge length is this fraction of the host width.
    // 0.60 matches the Spotify reference where the deck dominates the
    // image well with the heart anchoring the bottom-left corner.
    private const float ThumbSizeRatio = 0.60f;

    // Heart tile edge length as a fraction of host width. Originally a fixed
    // 56 px — on a ~140 px host that's 40%; on a 250 px host it shrank to 22%
    // and looked tiny. Keep it at ~40% always.
    private const float HeartSizeRatio = 0.40f;

    // Offsets expressed as fractions of host width. Anchored BOTTOM-LEFT, so
    // OffsetX = pixels from left, OffsetY = pixels UP from bottom.
    private static readonly SlotPoseRatio[] RestLayoutRatios =
    [
        // Back of stack — at the left edge, partly behind heart
        new(-3f, 0.00f, 0.16f),
        // Middle — small step right + up
        new( 0f, 0.10f, 0.21f),
        // Front — rightmost of the cluster, highest
        new( 3f, 0.20f, 0.27f),
    ];

    private static readonly SlotPoseRatio[] HoverLayoutRatios =
    [
        // Subtle spread — fan opens slightly to the right but the leftmost
        // slot stays anchored to the edge. Stays inside the card width.
        new(-7f, 0.00f, 0.17f),
        new( 1f, 0.16f, 0.24f),
        new( 9f, 0.33f, 0.33f),
    ];

    // Heart tile hover pose: translate down + scale down so it gracefully
    // steps aside and lets the fanned covers take the spotlight. Translation
    // also a fraction of host so the step-aside doesn't look weak on big cards.
    private const float HeartHoverTranslateXRatio = -0.03f;
    private const float HeartHoverTranslateYRatio =  0.06f;
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
        ApplyTileVariant();
        RebuildThumbnails();
    }

    /// <summary>
    /// Repaints the foreground tile (gradient + glyph) based on the current
    /// <see cref="NavigationUri"/>. Liked Songs (<c>spotify:collection:tracks</c>)
    /// keeps the purple gradient + heart it always had; the
    /// <c>spotify:collection:your-episodes</c> pseudo-collection swaps to
    /// Spotify's saved-episodes green identity with a headphones glyph so the
    /// two recents-saved cards aren't visually indistinguishable. Called from
    /// both <see cref="OnLoaded"/> (first realisation) and the
    /// <see cref="NavigationUri"/> DP changed callback (ItemsRepeater
    /// recycling — same control instance, new item).
    /// </summary>
    private void ApplyTileVariant()
    {
        // Defensive: DP can fire before InitializeComponent has populated
        // the named XAML elements. ApplyTileVariant runs again from OnLoaded.
        if (TileGradientStop1 == null || TileGradientStop2 == null
            || TileGradientStop3 == null || TileGlyph == null)
            return;

        var isYourEpisodes = NavigationUri?.Contains("your-episodes",
            StringComparison.OrdinalIgnoreCase) == true;

        if (isYourEpisodes)
        {
            TileGradientStop1.Color = Color.FromArgb(0xFF, 0x1F, 0x8B, 0x47);
            TileGradientStop2.Color = Color.FromArgb(0xFF, 0x0F, 0x5A, 0x2D);
            TileGradientStop3.Color = Color.FromArgb(0xFF, 0x06, 0x2E, 0x18);
            TileGlyph.Glyph = FluentGlyphs.DeviceHeadphones;
        }
        else
        {
            TileGradientStop1.Color = Color.FromArgb(0xFF, 0x50, 0x38, 0xA0);
            TileGradientStop2.Color = Color.FromArgb(0xFF, 0x7B, 0x5F, 0xCC);
            TileGradientStop3.Color = Color.FromArgb(0xFF, 0x9C, 0xC5, 0xC5);
            TileGlyph.Glyph = FluentGlyphs.HeartFilled;
        }
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
        if (e.NewSize.Width <= 0) return;
        if (Math.Abs(ImageArea.Height - e.NewSize.Width) > 0.5)
            ImageArea.Height = e.NewSize.Width;

        // Rebuild the composition when the host width changes so the fan +
        // heart sizing scales with the card — without this they stay at
        // their first-realised pixel size and look tiny on wider cards.
        if (Math.Abs((float)e.NewSize.Width - _hostWidth) > 0.5f)
            RebuildThumbnails();
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
        card.UpdateSubtitleText();
        if (card.AddedCheckGlyph != null)
            card.AddedCheckGlyph.Visibility = Visibility.Visible;
        card.RebuildThumbnails();
    }

    private static void OnAddedItemNounChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (LikedSongsRecentCard)d;
        card.UpdateSubtitleText();
    }

    private void UpdateSubtitleText()
    {
        if (SubtitleText == null)
            return;

        var noun = string.IsNullOrWhiteSpace(AddedItemNoun) ? "song" : AddedItemNoun.Trim();
        var count = AddedCount;
        if (count.HasValue)
        {
            var suffix = count.Value == 1 ? string.Empty : "s";
            SubtitleText.Text = $"{count.Value} {noun}{suffix} added";
        }
        else
        {
            SubtitleText.Text = $"{char.ToUpperInvariant(noun[0])}{noun[1..]}s added";
        }
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

        // Resize the heart tile to match the new host width. Margin tracks
        // host width too (~3%) so it doesn't look glued to the edge on a
        // small card or floating on a wide one.
        var heartSize = hostWidth * HeartSizeRatio;
        var heartMargin = hostWidth * 0.03f;
        HeartTile.Width = heartSize;
        HeartTile.Height = heartSize;
        HeartTile.Margin = new Thickness(heartMargin, 0, 0, heartMargin);
        if (_heartVisual != null)
            _heartVisual.CenterPoint = new Vector3(heartSize / 2f, heartSize / 2f, 0f);
        // Glyph scales with the tile so the heart icon stays visually
        // proportional instead of shrinking to a dot on a big card.
        TileGlyph.FontSize = heartSize * 0.4;

        // Render only the number of slots Spotify says were recently added,
        // capped at the three thumbnails the home response can carry AND at
        // the number of URLs we actually have. This avoids:
        //   • a three-card fan for a one-episode save event (AddedCount cap)
        //   • empty placeholder squares when the URI list is missing from
        //     group_metadata (observed for spotify:collection:your-episodes —
        //     Spotify omits field 2 episode URIs there, only ships the count)
        // When the resolver later fills Thumbnail{1..3}ImageUrl, RebuildThumbnails
        // re-runs and draws the slots that now have URLs.
        var placeholderBrush = _compositor.CreateColorBrush(
            Color.FromArgb(255, 0x2C, 0x2C, 0x32));

        var nonEmptyUrlCount = urls.Count(static url => !string.IsNullOrWhiteSpace(url));
        var slotCount = Math.Clamp(
            Math.Min(AddedCount ?? nonEmptyUrlCount, nonEmptyUrlCount),
            0, RestLayoutRatios.Length);
        for (var i = 0; i < slotCount; i++)
        {
            var url = urls[i];
            var pose = ResolvePose(_isHovered ? HoverLayoutRatios[i] : RestLayoutRatios[i]);
            var sprite = _compositor.CreateSpriteVisual();
            sprite.Size = new Vector2(pose.Size);
            sprite.CenterPoint = new Vector3(pose.Size / 2f, pose.Size / 2f, 0f);
            sprite.RotationAngleInDegrees = pose.Rotation;
            sprite.Offset = ComputeOffset(pose);

            // Soft drop shadow per visual — depth that XAML couldn't easily do.
            // Blur scales with host so the depth reads consistent across sizes.
            var shadow = _compositor.CreateDropShadow();
            shadow.BlurRadius = Math.Max(6f, hostWidth * 0.05f);
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
                    var surface = LoadedImageSurface.StartLoadFromUri(new Uri(httpsUrl), new Size(pose.Size, pose.Size));
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

    /// <summary>Resolve a ratio-based pose into absolute pixel values
    /// against the current host width.</summary>
    private SlotPose ResolvePose(SlotPoseRatio ratio)
        => new(
            Size: _hostWidth * ThumbSizeRatio,
            Rotation: ratio.Rotation,
            Offset: new Vector2(_hostWidth * ratio.OffsetXRatio, _hostWidth * ratio.OffsetYRatio));

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
        var poseSet = toHover ? HoverLayoutRatios : RestLayoutRatios;
        for (var i = 0; i < _sprites.Count && i < poseSet.Length; i++)
        {
            var sprite = _sprites[i];
            var pose = ResolvePose(poseSet[i]);
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
        // Translation deltas scale with host so the step-aside reads the
        // same on a 140 px card and a 280 px card.
        if (_heartVisual != null)
        {
            var heartTranslate = _compositor.CreateVector3KeyFrameAnimation();
            heartTranslate.InsertKeyFrame(
                1f,
                toHover
                    ? new Vector3(_hostWidth * HeartHoverTranslateXRatio, _hostWidth * HeartHoverTranslateYRatio, 0f)
                    : Vector3.Zero,
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
        if (NavigationUri?.Contains("your-episodes", StringComparison.OrdinalIgnoreCase) == true)
            NavigationHelpers.OpenYourEpisodes(openInNewTab);
        else
            NavigationHelpers.OpenLikedSongs(openInNewTab);
        e.Handled = true;
    }
}
