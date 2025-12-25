using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ManagedBass;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Playback.Abstractions;
using Wavee.Connect.Playback.Sources;

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

    /// <summary>
    /// Maximum retries for reconnecting live streams.
    /// </summary>
    private const int MaxRetries = 3;

    /// <summary>
    /// Default sample rate for streaming sources when format detection isn't possible.
    /// </summary>
    private const int DefaultStreamingSampleRate = 44100;

    /// <summary>
    /// Default channel count for streaming sources.
    /// </summary>
    private const int DefaultStreamingChannels = 2;

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

        // For non-seekable streams (HTTP radio), we can't read ahead without consuming data.
        // Return a default format - actual format will be determined during decode.
        if (!stream.CanSeek)
        {
            _logger?.LogDebug("Non-seekable stream detected, using default format for streaming");
            return new AudioFormat(DefaultStreamingSampleRate, DefaultStreamingChannels, 16);
        }

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
        Action<string>? onMetadataReceived = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureBassInitialized();

        int handle;
        byte[]? memoryData = null;
        int syncHandle = 0;
        string? streamUrl = null; // Track URL for potential reconnection

        // Debug: log stream type
        _logger?.LogDebug("DecodeAsync received stream type: {Type}", stream.GetType().FullName);

        // Check if this is a URL-aware stream with HTTP URL - use native BASS URL streaming
        // Need to unwrap PrefixedStream to find the UrlAwareStream inside
        var urlStream = FindUrlAwareStream(stream);
        if (urlStream != null &&
            (urlStream.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             urlStream.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            streamUrl = urlStream.Url; // Store for reconnection
            _logger?.LogDebug("Using BASS native URL streaming for: {Url}", streamUrl);
            handle = Bass.CreateStream(streamUrl, 0, BassFlags.Decode | BassFlags.Float, null);

            // Set up ICY metadata sync if callback provided
            if (handle != 0 && onMetadataReceived != null)
            {
                syncHandle = Bass.ChannelSetSync(handle, SyncFlags.MetadataReceived, 0,
                    (h, ch, data, user) =>
                    {
                        try
                        {
                            var tagsPtr = Bass.ChannelGetTags(h, TagType.META);
                            if (tagsPtr != IntPtr.Zero)
                            {
                                var tags = Marshal.PtrToStringAnsi(tagsPtr);
                                if (!string.IsNullOrEmpty(tags))
                                {
                                    var title = ExtractStreamTitle(tags);
                                    if (!string.IsNullOrEmpty(title))
                                    {
                                        _logger?.LogDebug("ICY metadata received: {Title}", title);
                                        onMetadataReceived(title);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Error processing ICY metadata");
                        }
                    }, IntPtr.Zero);

                if (syncHandle == 0)
                {
                    _logger?.LogDebug("Failed to set up ICY metadata sync: {Error}", Bass.LastError);
                }
                else
                {
                    _logger?.LogDebug("ICY metadata sync set up successfully");
                }
            }
        }
        else
        {
            // Load into memory for seekable streams
            memoryData = await ReadStreamToMemoryAsync(stream, cancellationToken);
            handle = Bass.CreateStream(memoryData, 0, memoryData.Length, BassFlags.Decode | BassFlags.Float);
        }

        if (handle == 0)
        {
            var error = Bass.LastError;
            throw new InvalidOperationException($"BASS failed to create stream: {error}");
        }

        try
        {
            var info = Bass.ChannelGetInfo(handle);

            // Seek to start position if specified (only for seekable streams)
            if (startPositionMs > 0 && stream.CanSeek)
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

            // Track elapsed time for streaming (since position isn't reliable)
            var isStreaming = !stream.CanSeek;
            var isLiveStream = isStreaming && streamUrl != null; // Live HTTP stream that can be reconnected
            long streamingPositionMs = 0;
            var bytesPerMs = (info.Frequency * info.Channels * sizeof(float)) / 1000.0;

            try
            {
                _logger?.LogDebug(
                    "Starting BASS decode: {SampleRate}Hz, {Channels}ch, startPosition={StartPositionMs}ms, streaming={IsStreaming}",
                    info.Frequency, info.Channels, startPositionMs, isStreaming);

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Read float samples from BASS
                    // BASS_DATA_FLOAT = read as 32-bit floating point
                    var bytesToRead = floatBuffer.Length * sizeof(float);
                    var bytesRead = Bass.ChannelGetData(handle, floatBuffer, bytesToRead | (int)DataFlags.Float);

                    if (bytesRead <= 0)
                    {
                        var error = bytesRead < 0 ? Bass.LastError : Errors.Ended;

                        // For live streams, attempt reconnection instead of stopping
                        if (isLiveStream && streamUrl != null)
                        {
                            var reconnected = false;
                            for (int retry = 0; retry < MaxRetries && !cancellationToken.IsCancellationRequested; retry++)
                            {
                                var delay = TimeSpan.FromSeconds(Math.Pow(2, retry)); // 1s, 2s, 4s
                                _logger?.LogWarning(
                                    "Live stream interrupted ({Error}), reconnecting in {Delay}s (attempt {Attempt}/{Max})",
                                    error, delay.TotalSeconds, retry + 1, MaxRetries);

                                await Task.Delay(delay, cancellationToken);

                                // Free old handle and create new one
                                Bass.StreamFree(handle);
                                handle = Bass.CreateStream(streamUrl, 0, BassFlags.Decode | BassFlags.Float, null);

                                if (handle != 0)
                                {
                                    _logger?.LogInformation("Live stream reconnected successfully");
                                    reconnected = true;
                                    break;
                                }

                                _logger?.LogWarning("Reconnection attempt {Attempt} failed: {Error}", retry + 1, Bass.LastError);
                            }

                            if (!reconnected)
                            {
                                _logger?.LogError("Failed to reconnect live stream after {Max} attempts", MaxRetries);
                                break;
                            }

                            continue; // Resume decode loop with new handle
                        }

                        // Finite stream - existing behavior
                        if (error != Errors.Ended)
                        {
                            _logger?.LogWarning("BASS decode error: {Error}", error);
                        }
                        break;
                    }

                    var samplesRead = bytesRead / sizeof(float);

                    // Convert float samples to 16-bit PCM
                    var pcmBytes = samplesRead * 2;
                    ConvertFloatToPcm16(floatBuffer.AsSpan(0, samplesRead), pcmBuffer.AsSpan(0, pcmBytes));

                    // Get current position in milliseconds
                    long posMs;
                    if (isStreaming)
                    {
                        // For streaming, calculate from bytes decoded
                        posMs = streamingPositionMs;
                        streamingPositionMs += (long)(bytesRead / bytesPerMs);
                    }
                    else
                    {
                        var posBytes = Bass.ChannelGetPosition(handle);
                        posMs = (long)(Bass.ChannelBytes2Seconds(handle, posBytes) * 1000);
                    }

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
    /// Recursively unwraps streams to find a UrlAwareStream.
    /// </summary>
    private static UrlAwareStream? FindUrlAwareStream(Stream stream)
    {
        if (stream is UrlAwareStream urlStream)
            return urlStream;
        if (stream is PrefixedStream prefixed)
            return FindUrlAwareStream(prefixed.InnerStream);
        return null;
    }

    /// <summary>
    /// Extracts the StreamTitle from ICY metadata string.
    /// </summary>
    /// <param name="metadata">ICY metadata string (e.g., "StreamTitle='Artist - Song';").</param>
    /// <returns>The stream title, or null if not found.</returns>
    private static string? ExtractStreamTitle(string metadata)
    {
        // Format: StreamTitle='Artist - Song';StreamUrl='...';
        const string prefix = "StreamTitle='";
        var startIndex = metadata.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            return null;

        startIndex += prefix.Length;
        var endIndex = metadata.IndexOf("';", startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
            endIndex = metadata.IndexOf('\'', startIndex);
        if (endIndex < 0)
            return null;

        return metadata[startIndex..endIndex];
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
