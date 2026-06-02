using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// JPEG-encodes a square cover image for upload as a Spotify playlist cover.
///
/// Spotify's cover upload (image-upload.spotify.com → register-image) takes a JPEG payload no
/// larger than 256 KB. The reframe editor (<c>ImageReframeDialog</c>) composites the user's
/// crop / pad / re-center into a square BGRA8 buffer via Win2D; this helper encodes that buffer
/// to JPEG, stepping quality down until it fits the size cap.
/// </summary>
public static class PlaylistCoverHelper
{
    private const int MaxBytes = 256 * 1024;
    private static readonly uint[] QualitySteps = { 85, 75, 65, 55 };

    /// <summary>
    /// JPEG-encodes a square <paramref name="bgra8Pixels"/> buffer
    /// (<paramref name="side"/>×<paramref name="side"/>, BGRA8, tightly packed) stepping quality
    /// down until the result fits 256 KB. Throws <see cref="InvalidOperationException"/> if even
    /// the lowest quality is still too big (very unusual at 640×640).
    /// </summary>
    public static async Task<byte[]> EncodeSquareJpegAsync(byte[] bgra8Pixels, uint side)
    {
        ArgumentNullException.ThrowIfNull(bgra8Pixels);
        if (side == 0) throw new ArgumentOutOfRangeException(nameof(side));

        var expected = checked((long)side * side * 4);
        if (bgra8Pixels.Length < expected)
            throw new ArgumentException(
                $"Expected at least {expected} BGRA8 bytes for {side}×{side}, got {bgra8Pixels.Length}.",
                nameof(bgra8Pixels));

        foreach (var quality in QualitySteps)
        {
            var bytes = await EncodeAsync(bgra8Pixels, side, quality);
            if (bytes.Length <= MaxBytes)
                return bytes;
        }

        throw new InvalidOperationException(
            $"Cover image still exceeds {MaxBytes} bytes at JPEG quality {QualitySteps[^1]}; " +
            "pick a smaller / less detailed image.");
    }

    private static async Task<byte[]> EncodeAsync(byte[] bgra8Pixels, uint side, uint quality)
    {
        using var output = new InMemoryRandomAccessStream();

        var propertySet = new BitmapPropertySet
        {
            { "ImageQuality", new BitmapTypedValue(quality / 100.0, Windows.Foundation.PropertyType.Single) },
        };

        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, propertySet);
        // The encoder transforms / encodes the pixels we hand it here — without SetPixelData
        // (or a transcoding source) FlushAsync throws "no pixel data set or copied on the
        // encoder frame", which was the original cover-upload crash.
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            side, side,
            96, 96,
            bgra8Pixels);
        await encoder.FlushAsync();

        output.Seek(0);
        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
