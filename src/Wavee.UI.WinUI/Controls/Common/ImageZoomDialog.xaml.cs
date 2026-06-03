using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Wavee.UI.Helpers;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.Controls.Common;

/// <summary>
/// Reusable full-cover viewer: shows an image in a zoom/pan-able overlay dialog
/// with an "export (Save as…)" action. Used by any cover surface — albums,
/// playlists, podcasts/shows, etc. — via <see cref="ShowAsync"/>.
/// </summary>
public sealed partial class ImageZoomDialog : ContentDialog
{
    private string? _imageUrl;
    private string _suggestedFileName = "cover";

    public ImageZoomDialog()
    {
        InitializeComponent();
        PrimaryButtonClick += OnSavePrimaryClick;
    }

    /// <summary>
    /// Opens the viewer for <paramref name="imageUrl"/> (a Spotify image URI or a
    /// plain https URL). <paramref name="title"/> is shown in the dialog header and
    /// <paramref name="suggestedFileName"/> seeds the Save dialog (e.g. the album /
    /// playlist / show name). No-ops when the image URL can't be resolved.
    /// </summary>
    public static async Task ShowAsync(XamlRoot? xamlRoot, string? imageUrl, string? title, string? suggestedFileName)
    {
        if (xamlRoot is null) return;
        var httpsUrl = SpotifyImageHelper.ToHttpsUrl(imageUrl);
        if (string.IsNullOrEmpty(httpsUrl)) return;

        var dialog = new ImageZoomDialog
        {
            XamlRoot = xamlRoot,
            Title = string.IsNullOrWhiteSpace(title) ? "Cover art" : title,
            _imageUrl = httpsUrl,
            _suggestedFileName = SanitizeFileName(suggestedFileName),
        };
        // Match the dialog chrome (background / title / buttons) to the app's
        // current light/dark theme — robust even when the app applies a
        // per-element RequestedTheme override rather than an app-wide one.
        if (xamlRoot.Content is FrameworkElement rootElement)
            dialog.RequestedTheme = rootElement.ActualTheme;
        // Full-resolution decode (no DecodePixelSize cap) so zoom reveals detail.
        dialog.FullImage.Source = new BitmapImage(new Uri(httpsUrl));

        // Scale the square viewer to the window so it "expands" responsively.
        var size = xamlRoot.Size;
        var side = Math.Clamp(Math.Min(size.Width, size.Height) * 0.78, 320, 760);
        dialog.Viewer.Width = side;
        dialog.Viewer.Height = side;
        // Size the image to the viewport so it FITS at zoom factor 1 (a ScrollViewer
        // otherwise measures an unconstrained Uniform image at its natural pixels);
        // zoom then scales beyond fit. Uniform letterboxes non-square art cleanly.
        dialog.FullImage.Width = side;
        dialog.FullImage.Height = side;

        await dialog.ShowAsync();
    }

    private void ZoomScroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // ScrollView zooms programmatically via ZoomTo (no ChangeView). Null
        // centerPoint zooms about the viewport centre.
        var target = ZoomScroller.ZoomFactor > 1.05f ? 1f : 2.5f;
        ZoomScroller.ZoomTo(target, null);
        e.Handled = true;
    }

    private async void OnSavePrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the viewer open after exporting so the user can keep browsing the art.
        var deferral = args.GetDeferral();
        args.Cancel = true;
        try
        {
            await SaveImageAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

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
