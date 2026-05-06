using System;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Wavee.UI.WinUI.Behaviors;

/// <summary>
/// Attached behavior for Image controls that provides:
/// - Hide on error (fallback to placeholder)
/// - Fade-in animation when image loads
/// - DecodePixelWidth/Height optimization for memory efficiency
/// </summary>
public static class ImageFallbackBehavior
{
    // ConditionalWeakTable to store callback tokens without preventing GC of Image controls
    private static readonly ConditionalWeakTable<Image, CallbackTokenHolder> _callbackTokens = new();

    // Helper class to hold the callback token
    private sealed class CallbackTokenHolder
    {
        public long Token { get; set; }
    }

    #region HideOnError Property

    public static readonly DependencyProperty HideOnErrorProperty =
        DependencyProperty.RegisterAttached(
            "HideOnError",
            typeof(bool),
            typeof(ImageFallbackBehavior),
            new PropertyMetadata(false, OnHideOnErrorChanged));

    public static bool GetHideOnError(DependencyObject obj) =>
        (bool)obj.GetValue(HideOnErrorProperty);

    public static void SetHideOnError(DependencyObject obj, bool value) =>
        obj.SetValue(HideOnErrorProperty, value);

    #endregion

    #region FadeInOnLoad Property

    public static readonly DependencyProperty FadeInOnLoadProperty =
        DependencyProperty.RegisterAttached(
            "FadeInOnLoad",
            typeof(bool),
            typeof(ImageFallbackBehavior),
            new PropertyMetadata(false, OnFadeInOnLoadChanged));

    public static bool GetFadeInOnLoad(DependencyObject obj) =>
        (bool)obj.GetValue(FadeInOnLoadProperty);

    public static void SetFadeInOnLoad(DependencyObject obj, bool value) =>
        obj.SetValue(FadeInOnLoadProperty, value);

    #endregion

    #region DecodePixelWidth Property

    public static readonly DependencyProperty DecodePixelWidthProperty =
        DependencyProperty.RegisterAttached(
            "DecodePixelWidth",
            typeof(int),
            typeof(ImageFallbackBehavior),
            new PropertyMetadata(0));

    public static int GetDecodePixelWidth(DependencyObject obj) =>
        (int)obj.GetValue(DecodePixelWidthProperty);

    public static void SetDecodePixelWidth(DependencyObject obj, int value) =>
        obj.SetValue(DecodePixelWidthProperty, value);

    #endregion

    #region DecodePixelHeight Property

    public static readonly DependencyProperty DecodePixelHeightProperty =
        DependencyProperty.RegisterAttached(
            "DecodePixelHeight",
            typeof(int),
            typeof(ImageFallbackBehavior),
            new PropertyMetadata(0));

    public static int GetDecodePixelHeight(DependencyObject obj) =>
        (int)obj.GetValue(DecodePixelHeightProperty);

    public static void SetDecodePixelHeight(DependencyObject obj, int value) =>
        obj.SetValue(DecodePixelHeightProperty, value);

    #endregion

    #region Property Changed Handlers

    private static void OnHideOnErrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Image image)
        {
            if ((bool)e.NewValue)
            {
                image.ImageFailed += Image_ImageFailed;
                image.ImageOpened += Image_ImageOpened;
            }
            else
            {
                image.ImageFailed -= Image_ImageFailed;
                image.ImageOpened -= Image_ImageOpened;
            }
        }
    }

    private static void OnFadeInOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Image image)
        {
            if ((bool)e.NewValue)
            {
                // Subscribe to events
                image.ImageOpened += Image_FadeIn;
                image.ImageFailed += Image_ResetOpacity;

                // Listen for source changes to prepare for fade-in
                // Store the callback token so we can unregister it later
                var token = image.RegisterPropertyChangedCallback(Image.SourceProperty, OnSourceChangedForFade);
                _callbackTokens.AddOrUpdate(image, new CallbackTokenHolder { Token = token });
            }
            else
            {
                image.ImageOpened -= Image_FadeIn;
                image.ImageFailed -= Image_ResetOpacity;

                // Unregister the property changed callback to prevent memory leaks
                if (_callbackTokens.TryGetValue(image, out var holder))
                {
                    image.UnregisterPropertyChangedCallback(Image.SourceProperty, holder.Token);
                    _callbackTokens.Remove(image);
                }

                // Reset opacity to ensure image is visible
                image.Opacity = 1;
            }
        }
    }

    private static void OnSourceChangedForFade(DependencyObject sender, DependencyProperty dp)
    {
        if (sender is Image image && GetFadeInOnLoad(image))
        {
            // Restore visibility in case a previous load failure hid the image
            // (critical for virtualized lists where containers are recycled)
            image.Visibility = Visibility.Visible;

            // When source changes, prepare for fade by setting opacity to 0
            // Only do this if there's a new source (not when clearing)
            if (image.Source is BitmapImage bmp)
            {
                image.Opacity = 0;
                ApplyDecodePixelSize(image);
                // If the BitmapImage is already fully decoded (cache hit), WinUI will NOT
                // re-fire ImageOpened on this Image control — so Image_FadeIn never runs
                // and the image stays at Opacity=0 forever. Detect this and dispatch the
                // fade manually on the next tick (dispatched, not synchronous, to avoid
                // reentering the XAML property system from inside a DP-changed callback).
                if (bmp.PixelWidth > 0)
                    image.DispatcherQueue?.TryEnqueue(() => AnimateFadeIn(image));
                // For in-flight downloads, ImageOpened fires normally → Image_FadeIn handles it.
            }
            else if (image.Source != null)
            {
                // Non-BitmapImage source — just show it
                image.Opacity = 1;
            }
            else
            {
                // No source, make sure it's visible (for placeholder to show through)
                image.Opacity = 1;
            }
        }
    }

    #endregion

    #region Event Handlers

    private static void Image_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            // Hide the image so the fallback (FontIcon) can show
            image.Visibility = Visibility.Collapsed;
        }
    }

    private static void Image_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            // Ensure image is visible when loaded successfully
            image.Visibility = Visibility.Visible;
        }
    }

    private static void Image_FadeIn(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            // Defer to the next dispatcher tick so any in-flight visual tree mutations
            // (the right panel rebuilding its chrome on track change is the worst case)
            // settle before we touch the Composition Visual.
            image.DispatcherQueue?.TryEnqueue(() => AnimateFadeIn(image));
        }
    }

    private static void Image_ResetOpacity(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image)
        {
            // Reset opacity on failure so placeholder shows correctly
            image.Opacity = 1;
        }
    }

    #endregion

    #region Animation

    private static void AnimateFadeIn(Image image)
    {
        // Element must be attached to a XamlRoot before we touch its Composition Visual.
        // If it isn't yet, hook Loaded once and retry from there.
        if (!image.IsLoaded || image.XamlRoot is null)
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                image.Loaded -= OnLoaded;
                AnimateFadeIn(image);
            }
            image.Loaded += OnLoaded;
            return;
        }

        // Reset the XAML Opacity (set to 0 in OnSourceChangedForFade). The visible fade
        // is driven by animating the underlying Composition Visual.Opacity directly.
        image.Opacity = 1;

        // Hand-rolled Composition animation rather than CommunityToolkit AnimationBuilder.
        // AnimationBuilder.Start() unconditionally calls
        // ElementCompositionPreview.SetIsTranslationEnabled(target, true) as part of its
        // setup, even for an Opacity-only animation. On WinAppSDK 2.0-preview2 / ARM64,
        // that call fail-fasts dcompi.dll when the compositor is busy with concurrent
        // work (e.g. the right panel's gradient/Win2D chrome rebuilding on track change).
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(image);
            var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0f, 0f);
            animation.InsertKeyFrame(1f, 1f);
            animation.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Opacity", animation);
        }
        catch
        {
            // Image is already at Opacity=1 above, so it stays visible without the fade.
        }
    }

    #endregion

    #region DecodePixel Helper

    private static void ApplyDecodePixelSize(Image image)
    {
        if (image.Source is not BitmapImage bitmapImage)
            return;

        var width = GetDecodePixelWidth(image);
        var height = GetDecodePixelHeight(image);
        if (width <= 0 && height <= 0)
            return;

        // The binding sets BitmapImage.UriSource before this callback runs, so by
        // the time we get here, decoding may already be in flight at the source
        // resolution and any later DecodePixelWidth assignment is silently ignored.
        // Detect that case and rebuild the BitmapImage with DecodePixelWidth set
        // FIRST. Reassigning Image.Source re-enters OnSourceChangedForFade, but on
        // the second pass DecodePixelWidth > 0 and this branch is skipped — bounded
        // to one recursion level.
        if (bitmapImage.UriSource is { } uri && bitmapImage.DecodePixelWidth == 0 && bitmapImage.DecodePixelHeight == 0)
        {
            var fresh = new BitmapImage();
            if (width > 0) fresh.DecodePixelWidth = width;
            if (height > 0) fresh.DecodePixelHeight = height;
            fresh.UriSource = uri;
            image.Source = fresh;
            return;
        }

        try
        {
            if (width > 0 && bitmapImage.DecodePixelWidth == 0)
                bitmapImage.DecodePixelWidth = width;
            if (height > 0 && bitmapImage.DecodePixelHeight == 0)
                bitmapImage.DecodePixelHeight = height;
        }
        catch
        {
            // DecodePixelWidth/Height can't be set after image started loading.
        }
    }

    #endregion
}
