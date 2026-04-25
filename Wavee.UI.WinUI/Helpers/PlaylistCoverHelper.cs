using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Prepares a user-picked image for upload as a Spotify playlist cover.
///
/// Spotify's <c>PUT /v1/playlists/{id}/images</c> requires a base64-encoded JPEG
/// payload no larger than 256 KB. Source images come from a file picker so they
/// can be any reasonable format and size. This helper centre-crops to a square,
/// downscales to a sensible max, encodes JPEG, and steps quality down until the
/// payload fits the size cap.
/// </summary>
public static class PlaylistCoverHelper
{
    private const int MaxDimension = 640;
    private const int MaxBytes = 256 * 1024;
    private static readonly uint[] QualitySteps = { 85, 75, 65, 55 };

    /// <summary>
    /// Reads <paramref name="file"/>, centre-crops to a square, downscales to
    /// <see cref="MaxDimension"/>×<see cref="MaxDimension"/>, and JPEG-encodes
    /// it stepping quality down until the result fits 256 KB. Throws
    /// <see cref="InvalidOperationException"/> if even the lowest quality
    /// produces a file larger than 256 KB (very unusual at 640×640).
    /// </summary>
    public static async Task<byte[]> PrepareForUploadAsync(StorageFile file)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));

        using var sourceStream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(sourceStream);

        // Center-crop to square at the source resolution, then let the encoder
        // downscale via ScaledWidth/Height so we never decode pixels we throw
        // away.
        var srcW = decoder.PixelWidth;
        var srcH = decoder.PixelHeight;
        var sideSrc = Math.Min(srcW, srcH);
        var cropX = (srcW - sideSrc) / 2;
        var cropY = (srcH - sideSrc) / 2;

        var targetSide = (uint)Math.Min(sideSrc, MaxDimension);

        foreach (var quality in QualitySteps)
        {
            var bytes = await EncodeJpegAsync(decoder, cropX, cropY, sideSrc, sideSrc, targetSide, targetSide, quality);
            if (bytes.Length <= MaxBytes)
                return bytes;
        }

        throw new InvalidOperationException(
            $"Cover image still exceeds {MaxBytes} bytes at JPEG quality {QualitySteps[^1]}; " +
            "pick a smaller / less detailed image.");
    }

    private static async Task<byte[]> EncodeJpegAsync(
        BitmapDecoder decoder,
        uint cropX, uint cropY, uint cropW, uint cropH,
        uint scaledW, uint scaledH,
        uint quality)
    {
        using var output = new InMemoryRandomAccessStream();

        var propertySet = new Windows.Graphics.Imaging.BitmapPropertySet();
        propertySet.Add("ImageQuality",
            new Windows.Graphics.Imaging.BitmapTypedValue(quality / 100.0, Windows.Foundation.PropertyType.Single));

        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, propertySet);
        encoder.BitmapTransform.Bounds = new BitmapBounds
        {
            X = cropX, Y = cropY, Width = cropW, Height = cropH
        };
        encoder.BitmapTransform.ScaledWidth = scaledW;
        encoder.BitmapTransform.ScaledHeight = scaledH;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;

        // Re-decode pixel data is implicit when we set transform + flush.
        await encoder.FlushAsync();

        output.Seek(0);
        var bytes = new byte[output.Size];
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(bytes);
        }
        return bytes;
    }
}
