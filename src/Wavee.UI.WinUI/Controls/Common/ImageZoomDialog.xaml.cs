using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.Controls.Common;

/// <summary>
/// Reusable full-window cover/image lightbox: the image is the content (stretched
/// edge-to-edge, zoom + pan), with floating close + "Save as…" controls over it —
/// no dialog chrome. Used by any cover surface (albums, shows/podcasts, etc.) via
/// <see cref="ShowAsync"/>. Hosted in a <see cref="Popup"/>.
/// </summary>
public sealed partial class ImageZoomDialog : UserControl
{
    private string? _imageUrl;
    private string _suggestedFileName = "cover";
    private Popup? _popup;
    private XamlRoot? _xamlRoot;
    private TaskCompletionSource<bool>? _closedTcs;

    public ImageZoomDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Opens the lightbox for <paramref name="imageUrl"/> (a Spotify image URI or a
    /// plain https URL). <paramref name="suggestedFileName"/> seeds the Save dialog
    /// (e.g. the album / show name). <paramref name="title"/> is accepted for API
    /// stability but not shown (the lightbox has no header). Returns a task that
    /// completes when the viewer is dismissed. No-ops when the URL can't be resolved.
    /// </summary>
    public static Task ShowAsync(XamlRoot? xamlRoot, string? imageUrl, string? title, string? suggestedFileName)
    {
        if (xamlRoot is null) return Task.CompletedTask;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl)) return Task.CompletedTask;

        var overlay = new ImageZoomDialog
        {
            _imageUrl = httpsUrl,
            _suggestedFileName = SanitizeFileName(suggestedFileName),
            _xamlRoot = xamlRoot,
        };
        // Match tooltip / focus chrome to the app theme (the surface itself is
        // intentionally dark, like any image viewer).
        if (xamlRoot.Content is FrameworkElement rootElement)
            overlay.RequestedTheme = rootElement.ActualTheme;
        // Full-resolution decode (no DecodePixelSize cap) so zoom reveals detail.
        overlay.FullImage.Source = new BitmapImage(new Uri(httpsUrl));

        var popup = new Popup
        {
            XamlRoot = xamlRoot,
            Child = overlay,
        };
        overlay._popup = popup;
        overlay.SizeToWindow();
        xamlRoot.Changed += overlay.OnXamlRootChanged;

        var tcs = new TaskCompletionSource<bool>();
        overlay._closedTcs = tcs;
        popup.IsOpen = true;
        overlay.OverlayRoot.Focus(FocusState.Programmatic);
        return tcs.Task;
    }

    private void SizeToWindow()
    {
        if (_xamlRoot is null) return;
        Width = _xamlRoot.Size.Width;
        Height = _xamlRoot.Size.Height;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToWindow();

    private void Close()
    {
        if (_xamlRoot is not null)
            _xamlRoot.Changed -= OnXamlRootChanged;
        if (_popup is not null)
            _popup.IsOpen = false;
        _closedTcs?.TrySetResult(true);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OverlayRoot_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Only the dimmed frame around the image dismisses — taps on the image
        // (or the floating buttons) bubble with a different OriginalSource.
        if (ReferenceEquals(e.OriginalSource, OverlayRoot))
            Close();
    }

    private void OverlayRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void ZoomScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the zoom content the size of the viewport so the Uniform image fits
        // at zoom factor 1 (a ScrollView otherwise measures it at natural pixels)
        // and stays centred; zoom then scales beyond fit.
        ImageHost.Width = ZoomScroller.ActualWidth;
        ImageHost.Height = ZoomScroller.ActualHeight;
    }

    private void ZoomScroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var target = ZoomScroller.ZoomFactor > 1.05f ? 1f : 2.5f;
        ZoomScroller.ZoomTo(target, null);
        e.Handled = true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveImageAsync();

    private async Task SaveImageAsync()
    {
        if (string.IsNullOrEmpty(_imageUrl)) return;

        var notifications = Ioc.Default.GetService<INotificationService>();
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = _suggestedFileName,
            };
            picker.FileTypeChoices.Add("JPEG image", new List<string> { ".jpg" });
            picker.FileTypeChoices.Add("PNG image", new List<string> { ".png" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, global::Wavee.UI.WinUI.MainWindow.Instance.WindowHandle);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var http = Ioc.Default.GetService<IHttpClientFactory>()?.CreateClient() ?? new HttpClient();
            var bytes = await http.GetByteArrayAsync(_imageUrl);

            var isJpeg = file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                      || file.FileType.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
            if (isJpeg)
            {
                // Spotify cover art is already JPEG — write through without re-compressing.
                await FileIO.WriteBytesAsync(file, bytes);
            }
            else
            {
                await ReencodeAsync(bytes, file, BitmapEncoder.PngEncoderId);
            }

            notifications?.Show($"Saved to {file.Name}", NotificationSeverity.Success, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            notifications?.Show($"Couldn't save image: {ex.Message}", NotificationSeverity.Error, TimeSpan.FromSeconds(5));
        }
    }

    private static async Task ReencodeAsync(byte[] sourceBytes, StorageFile file, Guid encoderId)
    {
        using var inStream = new InMemoryRandomAccessStream();
        await inStream.WriteAsync(sourceBytes.AsBuffer());
        inStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(inStream);
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        using var outStream = await file.OpenAsync(FileAccessMode.ReadWrite);
        outStream.Size = 0;
        var encoder = await BitmapEncoder.CreateAsync(encoderId, outStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            decoder.PixelWidth,
            decoder.PixelHeight,
            decoder.DpiX,
            decoder.DpiY,
            pixels.DetachPixelData());
        await encoder.FlushAsync();
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "cover";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "cover" : clean;
    }
}
