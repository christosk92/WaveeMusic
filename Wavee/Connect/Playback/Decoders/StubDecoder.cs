using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Decoders;

/// <summary>
/// Stub decoder that generates silence (for testing without real decoder).
/// </summary>
public sealed class StubDecoder : IAudioDecoder
{
    public string FormatName => "Stub";

    public bool CanDecode(Stream stream)
    {
        // Accept any stream (fallback decoder)
        return true;
    }

    public Task<AudioFormat> GetFormatAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Return standard CD quality format
        return Task.FromResult(AudioFormat.CdQuality);
    }

    public async IAsyncEnumerable<AudioBuffer> DecodeAsync(
        Stream stream,
        long startPositionMs = 0,
        Action<string>? onMetadataReceived = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Generate 10 seconds of silence as a test
        var format = AudioFormat.CdQuality;
        var bufferSize = format.BytesPerSecond / 10; // 100ms chunks
        var silentBuffer = new byte[bufferSize];
        var durationMs = 10000; // 10 seconds
        var positionMs = startPositionMs; // Start from requested position

        while (positionMs < durationMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new AudioBuffer(silentBuffer, positionMs);

            positionMs += format.BytesToMilliseconds(bufferSize);
            await Task.Delay(10, cancellationToken); // Simulate decoding time
        }
    }
}
