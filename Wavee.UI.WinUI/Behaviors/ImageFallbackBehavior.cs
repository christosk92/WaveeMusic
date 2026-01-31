using System;
using System.Runtime.CompilerServices;
using CommunityToolkit.WinUI.Animations;
using CommunityToolkit.WinUI.Animations.Expressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            // When source changes, prepare for fade by setting opacity to 0
            // Only do this if there's a new source (not when clearing)
            if (image.Source != null)
            {
                image.Opacity = 0;

                // Apply decode pixel size if set
                ApplyDecodePixelSize(image);

                // Safety: If the image is already loaded (cached), ImageOpened won't fire
                // Check if the BitmapImage has already loaded and restore opacity immediately
                if (image.Source is BitmapImage bitmapImage)
                {
                    // If PixelWidth > 0, the image is already loaded
                    if (bitmapImage.PixelWidth > 0)
                    {
                        AnimateFadeIn(image);
                    }
                }
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
            AnimateFadeIn(image);
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
        // Use Community Toolkit AnimationBuilder for smooth fade-in
        // Use FrameworkLayer.Xaml to match the image.Opacity = 0 we set earlier
        AnimationBuilder.Create()
            .Opacity(
                from: 0,
                to: 1,
                duration: TimeSpan.FromMilliseconds(200),
                layer: FrameworkLayer.Xaml)
            .Start(image);
    }

    #endregion

    #region DecodePixel Helper

    private static void ApplyDecodePixelSize(Image image)
    {
        if (image.Source is BitmapImage bitmapImage)
        {
            var width = GetDecodePixelWidth(image);
            var height = GetDecodePixelHeight(image);

            // Note: DecodePixelWidth/Height must be set before the image loads
            // Since binding sets the source before our callback fires, this may not
            // work for the first load. It will work for subsequent source changes.
            try
            {
                if (width > 0 && bitmapImage.DecodePixelWidth == 0)
                {
                    bitmapImage.DecodePixelWidth = width;
                }
                if (height > 0 && bitmapImage.DecodePixelHeight == 0)
                {
                    bitmapImage.DecodePixelHeight = height;
                }
            }
            catch
            {
                // DecodePixelWidth/Height can't be set after image started loading
            }
        }
    }

    #endregion
}
