using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NVorbis;
using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Decoders;

/// <summary>
/// Vorbis audio decoder using NVorbis.
/// </summary>
/// <remarks>
/// Handles Spotify's OGG Vorbis files which have:
/// - A 0xa7 (167 byte) Spotify header to skip
/// - Normalization data at offset 144
/// - Standard OGG Vorbis data after the header
/// </remarks>
public sealed class VorbisDecoder : IAudioDecoder
{
    /// <summary>
    /// Spotify header size that must be skipped before OGG data.
    /// </summary>
    public const int SpotifyHeaderSize = 0xa7; // 167 bytes

    /// <summary>
    /// OGG magic bytes: "OggS"
    /// </summary>
    private static readonly byte[] OggMagic = "OggS"u8.ToArray();

    /// <summary>
    /// Size of decode buffer in samples per channel.
    /// </summary>
    private const int SamplesPerBuffer = 4096;

    private readonly ILogger? _logger;

    public string FormatName => "Vorbis";

    /// <summary>
    /// Creates a new VorbisDecoder.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public VorbisDecoder(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Determines if this decoder can decode the given stream.
    /// Checks for OGG magic bytes at the Spotify header offset.
    /// </summary>
    public bool CanDecode(Stream stream)
    {
        if (!stream.CanRead)
            return false;

        var startPosition = stream.CanSeek ? stream.Position : 0;

        try
        {
            // Seek to after Spotify header
            if (stream.CanSeek)
            {
                if (stream.Length < SpotifyHeaderSize + 4)
                    return false;

                stream.Position = SpotifyHeaderSize;
            }
            else
            {
                // For non-seekable streams, skip bytes
                var toSkip = SpotifyHeaderSize;
                var skipBuffer = ArrayPool<byte>.Shared.Rent(Math.Min(toSkip, 1024));
                try
                {
                    while (toSkip > 0)
                    {
                        var bytesRead = stream.Read(skipBuffer, 0, Math.Min(toSkip, skipBuffer.Length));
                        if (bytesRead == 0)
                            return false;
                        toSkip -= bytesRead;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(skipBuffer);
                }
            }

            // Read OGG magic bytes
            Span<byte> magic = stackalloc byte[4];
            var read = stream.Read(magic);
            if (read < 4)
                return false;

            return magic.SequenceEqual(OggMagic);
        }
        finally
        {
            // Reset stream position if possible
            if (stream.CanSeek)
            {
                stream.Position = startPosition;
            }
        }
    }

    /// <summary>
    /// Gets the PCM audio format this decoder will output.
    /// </summary>
    public async Task<AudioFormat> GetFormatAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Wrap stream to skip Spotify header
        await using var skipStream = new SkipStream(stream, SpotifyHeaderSize, leaveOpen: true);

        using var reader = new VorbisReader(skipStream, closeOnDispose: false);

        return new AudioFormat(
            SampleRate: reader.SampleRate,
            Channels: reader.Channels,
            BitsPerSample: 16 // We output 16-bit PCM
        );
    }

    /// <summary>
    /// Decodes the audio stream into PCM audio buffers.
    /// </summary>
    public async IAsyncEnumerable<AudioBuffer> DecodeAsync(
        Stream stream,
        long startPositionMs = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Create a stream that skips the Spotify header
        // Note: We don't dispose this because the caller owns the stream
        var skipStream = new SkipStream(stream, SpotifyHeaderSize, leaveOpen: true);

        VorbisReader? reader = null;
        try
        {
            reader = new VorbisReader(skipStream, closeOnDispose: false);

            // Seek to start position if specified
            if (startPositionMs > 0)
            {
                reader.TimePosition = TimeSpan.FromMilliseconds(startPositionMs);
                _logger?.LogDebug("Seeked to position: {PositionMs}ms", startPositionMs);
            }

            var format = new AudioFormat(reader.SampleRate, reader.Channels, 16);
            var floatBuffer = ArrayPool<float>.Shared.Rent(SamplesPerBuffer * reader.Channels);
            var pcmBuffer = ArrayPool<byte>.Shared.Rent(SamplesPerBuffer * reader.Channels * 2);

            try
            {
                _logger?.LogDebug(
                    "Starting Vorbis decode: {SampleRate}Hz, {Channels}ch, startPosition={StartPositionMs}ms",
                    reader.SampleRate, reader.Channels, startPositionMs);

                int samplesRead;
                while ((samplesRead = reader.ReadSamples(floatBuffer, 0, floatBuffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Convert float samples to 16-bit PCM
                    var pcmBytes = samplesRead * 2;
                    ConvertFloatToPcm16(floatBuffer.AsSpan(0, samplesRead), pcmBuffer.AsSpan(0, pcmBytes));

                    // Calculate position in milliseconds
                    var positionMs = (long)(reader.TimePosition.TotalMilliseconds);

                    // Copy to new buffer for the AudioBuffer (since we reuse the arrays)
                    var bufferCopy = new byte[pcmBytes];
                    pcmBuffer.AsSpan(0, pcmBytes).CopyTo(bufferCopy);

                    yield return new AudioBuffer(bufferCopy, positionMs);
                }

                _logger?.LogDebug("Vorbis decode complete");
            }
            finally
            {
                ArrayPool<float>.Shared.Return(floatBuffer);
                ArrayPool<byte>.Shared.Return(pcmBuffer);
            }
        }
        finally
        {
            reader?.Dispose();
            await skipStream.DisposeAsync();
        }
    }

    /// <summary>
    /// Reads normalization data from the stream.
    /// </summary>
    /// <param name="stream">Audio stream (should be at position 0).</param>
    /// <returns>Normalization data.</returns>
    public static NormalizationData ReadNormalizationData(Stream stream)
    {
        if (!stream.CanSeek)
            return NormalizationData.Default;

        var originalPosition = stream.Position;
        try
        {
            if (stream.Length < NormalizationData.FileOffset + NormalizationData.Size)
                return NormalizationData.Default;

            stream.Position = NormalizationData.FileOffset;

            Span<byte> data = stackalloc byte[NormalizationData.Size];
            var bytesRead = stream.Read(data);

            if (bytesRead < NormalizationData.Size)
                return NormalizationData.Default;

            return NormalizationData.Parse(data);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Converts float samples (-1.0 to 1.0) to 16-bit PCM.
    /// </summary>
    private static void ConvertFloatToPcm16(ReadOnlySpan<float> floatSamples, Span<byte> pcmOutput)
    {
        for (int i = 0; i < floatSamples.Length; i++)
        {
            // Clamp to [-1, 1] range
            var sample = Math.Clamp(floatSamples[i], -1f, 1f);

            // Convert to 16-bit signed integer
            var pcmSample = (short)(sample * 32767f);

            // Write as little-endian
            var offset = i * 2;
            pcmOutput[offset] = (byte)(pcmSample & 0xFF);
            pcmOutput[offset + 1] = (byte)((pcmSample >> 8) & 0xFF);
        }
    }
}
