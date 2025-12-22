using System.Buffers;
using System.Runtime.CompilerServices;
using ManagedBass;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Decoders;

/// <summary>
/// Audio decoder using ManagedBass (BASS library).
/// Handles MP3, FLAC, WAV, and other formats supported by BASS.
/// </summary>
public sealed class BassDecoder : IAudioDecoder
{
    private static bool _bassInitialized;
    private static readonly object _initLock = new();

    /// <summary>
    /// Size of decode buffer in samples per channel.
    /// </summary>
    private const int SamplesPerBuffer = 4096;

    private readonly ILogger? _logger;

    /// <inheritdoc/>
    public string FormatName => "Bass";

    /// <summary>
    /// Creates a new BassDecoder.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public BassDecoder(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool CanDecode(Stream stream)
    {
        if (!stream.CanRead)
            return false;

        var startPosition = stream.CanSeek ? stream.Position : 0;

        try
        {
            // Read first 12 bytes for format detection
            Span<byte> header = stackalloc byte[12];
            var bytesRead = stream.Read(header);

            if (bytesRead < 4)
                return false;

            return IsSupported(header[..bytesRead]);
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = startPosition;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<AudioFormat> GetFormatAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        EnsureBassInitialized();

        // Copy stream to memory for BASS
        var data = await ReadStreamToMemoryAsync(stream, cancellationToken);

        // Create BASS stream from memory
        var handle = Bass.CreateStream(data, 0, data.Length, BassFlags.Decode | BassFlags.Float);

        if (handle == 0)
        {
            var error = Bass.LastError;
            throw new InvalidOperationException($"BASS failed to create stream: {error}");
        }

        try
        {
            var info = Bass.ChannelGetInfo(handle);
            return new AudioFormat(info.Frequency, info.Channels, 16); // Output as 16-bit PCM
        }
        finally
        {
            Bass.StreamFree(handle);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AudioBuffer> DecodeAsync(
        Stream stream,
        long startPositionMs = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureBassInitialized();

        // Copy stream to memory for BASS
        var data = await ReadStreamToMemoryAsync(stream, cancellationToken);

        // Create BASS decode stream from memory
        var handle = Bass.CreateStream(data, 0, data.Length, BassFlags.Decode | BassFlags.Float);

        if (handle == 0)
        {
            var error = Bass.LastError;
            throw new InvalidOperationException($"BASS failed to create stream: {error}");
        }

        try
        {
            var info = Bass.ChannelGetInfo(handle);

            // Seek to start position if specified
            if (startPositionMs > 0)
            {
                var bytePos = Bass.ChannelSeconds2Bytes(handle, startPositionMs / 1000.0);
                if (!Bass.ChannelSetPosition(handle, bytePos))
                {
                    _logger?.LogWarning("Failed to seek to {PositionMs}ms: {Error}", startPositionMs, Bass.LastError);
                }
                else
                {
                    _logger?.LogDebug("Seeked to position: {PositionMs}ms", startPositionMs);
                }
            }

            // Buffer for float samples from BASS
            var floatBuffer = ArrayPool<float>.Shared.Rent(SamplesPerBuffer * info.Channels);
            // Buffer for 16-bit PCM output
            var pcmBuffer = ArrayPool<byte>.Shared.Rent(SamplesPerBuffer * info.Channels * 2);

            try
            {
                _logger?.LogDebug(
                    "Starting BASS decode: {SampleRate}Hz, {Channels}ch, startPosition={StartPositionMs}ms",
                    info.Frequency, info.Channels, startPositionMs);

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Read float samples from BASS
                    // BASS_DATA_FLOAT = read as 32-bit floating point
                    var bytesToRead = floatBuffer.Length * sizeof(float);
                    var bytesRead = Bass.ChannelGetData(handle, floatBuffer, bytesToRead | (int)DataFlags.Float);

                    if (bytesRead <= 0)
                    {
                        // End of stream or error
                        if (bytesRead < 0)
                        {
                            var error = Bass.LastError;
                            if (error != Errors.Ended)
                            {
                                _logger?.LogWarning("BASS decode error: {Error}", error);
                            }
                        }
                        break;
                    }

                    var samplesRead = bytesRead / sizeof(float);

                    // Convert float samples to 16-bit PCM
                    var pcmBytes = samplesRead * 2;
                    ConvertFloatToPcm16(floatBuffer.AsSpan(0, samplesRead), pcmBuffer.AsSpan(0, pcmBytes));

                    // Get current position in milliseconds
                    var posBytes = Bass.ChannelGetPosition(handle);
                    var posMs = (long)(Bass.ChannelBytes2Seconds(handle, posBytes) * 1000);

                    // Copy to new buffer for the AudioBuffer (since we reuse the arrays)
                    var bufferCopy = new byte[pcmBytes];
                    pcmBuffer.AsSpan(0, pcmBytes).CopyTo(bufferCopy);

                    yield return new AudioBuffer(bufferCopy, posMs);
                }

                _logger?.LogDebug("BASS decode complete");
            }
            finally
            {
                ArrayPool<float>.Shared.Return(floatBuffer);
                ArrayPool<byte>.Shared.Return(pcmBuffer);
            }
        }
        finally
        {
            Bass.StreamFree(handle);
        }
    }

    /// <summary>
    /// Checks if the header bytes indicate a supported format.
    /// </summary>
    private static bool IsSupported(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
            return false;

        // MP3 with ID3v2 header
        if (header.Length >= 3 && header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            return true;

        // MP3 sync word (frame header)
        if (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
            return true;

        // FLAC magic: "fLaC"
        if (header[0] == 'f' && header[1] == 'L' && header[2] == 'a' && header[3] == 'C')
            return true;

        // WAV/RIFF magic: "RIFF"
        if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F')
            return true;

        // AIFF magic: "FORM"
        if (header[0] == 'F' && header[1] == 'O' && header[2] == 'R' && header[3] == 'M')
            return true;

        // M4A/AAC (ISO Base Media File Format) - check bytes 4-7 for 'ftyp'
        // First 4 bytes are atom size, bytes 4-7 are the type marker
        if (header.Length >= 8 &&
            header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p')
            return true;

        // Note: OGG is handled by VorbisDecoder, so we skip it here
        // to avoid conflicts. If you want BASS to handle OGG too,
        // uncomment the following:
        // if (header[0] == 'O' && header[1] == 'g' && header[2] == 'g' && header[3] == 'S')
        //     return true;

        return false;
    }

    /// <summary>
    /// Reads the entire stream into a byte array.
    /// </summary>
    private static async Task<byte[]> ReadStreamToMemoryAsync(Stream stream, CancellationToken ct)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var segment))
        {
            return segment.Array ?? ms.ToArray();
        }

        // Reset to beginning if possible
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, ct);
        return memoryStream.ToArray();
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

    /// <summary>
    /// Ensures BASS library is initialized.
    /// </summary>
    private static void EnsureBassInitialized()
    {
        if (_bassInitialized)
            return;

        lock (_initLock)
        {
            if (_bassInitialized)
                return;

            // Initialize BASS with no sound device (decode only)
            // Device 0 = "no sound" for decoding purposes
            if (!Bass.Init(0))
            {
                var error = Bass.LastError;
                // BASS_ERROR_ALREADY is OK - already initialized
                if (error != Errors.Already)
                {
                    throw new InvalidOperationException($"Failed to initialize BASS: {error}");
                }
            }

            _bassInitialized = true;
        }
    }
}
