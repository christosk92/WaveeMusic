using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PortAudioSharp;
using Wavee.Connect.Playback.Abstractions;

namespace Wavee.Connect.Playback.Sinks;

/// <summary>
/// Cross-platform audio output using PortAudio.
/// Supports WASAPI (Windows), CoreAudio (macOS), ALSA (Linux).
/// </summary>
public sealed class PortAudioSink : IAudioSink
{
    private static bool _initialized;
    private static readonly object _initLock = new();

    private readonly ILogger? _logger;
    private readonly object _lock = new();

    private PortAudioSharp.Stream? _stream;
    private CircularAudioBuffer? _buffer;
    private AudioFormat? _format;

    private bool _disposed;
    private bool _isPlaying;
    private bool _isInitialized;
    private long _samplesWritten;
    private long _samplesPlayed;

    public string SinkName => "PortAudio";

    /// <summary>
    /// Creates a new PortAudioSink.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    public PortAudioSink(ILogger? logger = null)
    {
        _logger = logger;
        EnsurePortAudioInitialized();
    }

    private void EnsurePortAudioInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            PortAudioSharp.PortAudio.Initialize();
            _initialized = true;
            _logger?.LogDebug("PortAudio initialized");
        }
    }

    /// <inheritdoc />
    public Task InitializeAsync(AudioFormat format, int bufferSizeMs = 100, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            // Clean up any existing stream
            CleanupStream();

            _format = format;

            // Calculate buffer size in bytes (4x requested for safety margin)
            var bufferBytes = format.BytesPerSecond * bufferSizeMs * 4 / 1000;
            _buffer = new CircularAudioBuffer(bufferBytes);

            // Get default output device
            var deviceIndex = PortAudioSharp.PortAudio.DefaultOutputDevice;
            if (deviceIndex == PortAudioSharp.PortAudio.NoDevice)
            {
                throw new InvalidOperationException("No default audio output device found");
            }

            var deviceInfo = PortAudioSharp.PortAudio.GetDeviceInfo(deviceIndex);

            // Configure output parameters
            var outputParams = new StreamParameters
            {
                device = deviceIndex,
                channelCount = format.Channels,
                sampleFormat = format.BitsPerSample switch
                {
                    16 => SampleFormat.Int16,
                    24 => SampleFormat.Int24,
                    32 => SampleFormat.Float32,
                    _ => SampleFormat.Int16
                },
                suggestedLatency = deviceInfo.defaultLowOutputLatency
            };

            // Calculate frames per buffer
            var framesPerBuffer = (uint)(format.SampleRate * bufferSizeMs / 1000);

            // Create stream with callback
            _stream = new PortAudioSharp.Stream(
                inParams: null,  // No input
                outParams: outputParams,
                sampleRate: format.SampleRate,
                framesPerBuffer: framesPerBuffer,
                streamFlags: StreamFlags.NoFlag,
                callback: StreamCallback,
                userData: null);

            _isInitialized = true;
            _samplesWritten = 0;
            _samplesPlayed = 0;

            _logger?.LogDebug(
                "Initialized PortAudio sink: {SampleRate}Hz, {Channels}ch, {BitsPerSample}bit, buffer={BufferMs}ms",
                format.SampleRate, format.Channels, format.BitsPerSample, bufferSizeMs);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// PortAudio stream callback - pulls audio from the circular buffer.
    /// </summary>
    private StreamCallbackResult StreamCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (_buffer == null || _format == null || !_isPlaying)
        {
            // Output silence
            var silenceBytes = (int)(frameCount * _format!.BytesPerFrame);
            unsafe
            {
                var ptr = (byte*)output;
                for (int i = 0; i < silenceBytes; i++)
                    ptr[i] = 0;
            }
            return StreamCallbackResult.Continue;
        }

        var bytesNeeded = (int)(frameCount * _format.BytesPerFrame);

        // Read from circular buffer into unmanaged memory
        unsafe
        {
            var span = new Span<byte>((void*)output, bytesNeeded);
            var bytesRead = _buffer.Read(span);

            // If we didn't get enough data, pad with silence
            if (bytesRead < bytesNeeded)
            {
                span.Slice(bytesRead).Clear();
            }

            Interlocked.Add(ref _samplesPlayed, bytesRead);
        }

        return StreamCallbackResult.Continue;
    }

    /// <inheritdoc />
    public async Task WriteAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_buffer == null || !_isInitialized)
            throw new InvalidOperationException("Sink not initialized");

        // Auto-start playback if not playing and we have enough data
        lock (_lock)
        {
            if (!_isPlaying && _buffer.Available > _format!.BytesPerSecond / 10)
            {
                StartPlayback();
            }
        }

        // Write to circular buffer (blocks if buffer is full - provides backpressure)
        await _buffer.WriteAsync(audioData, cancellationToken);
        Interlocked.Add(ref _samplesWritten, audioData.Length);
    }

    private void StartPlayback()
    {
        StartPlaybackInternal();
    }

    /// <summary>
    /// Attempts to start playback and returns success status.
    /// </summary>
    /// <returns>True if playback started successfully, false otherwise.</returns>
    private bool StartPlaybackInternal()
    {
        if (_stream == null || _isPlaying)
            return false;

        try
        {
            _stream.Start();
            _isPlaying = true;
            _logger?.LogDebug("PortAudio playback started");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start PortAudio playback");
            _isPlaying = false;
            return false;
        }
    }

    /// <inheritdoc />
    public Task<AudioSinkStatus> GetStatusAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_buffer == null || _format == null)
            {
                return Task.FromResult(new AudioSinkStatus(0, 0, false));
            }

            var bufferedBytes = _buffer.Available;
            var bufferedMs = (int)_format.BytesToMilliseconds(bufferedBytes);
            var positionMs = _format.BytesToMilliseconds(Interlocked.Read(ref _samplesPlayed));

            return Task.FromResult(new AudioSinkStatus(positionMs, bufferedMs, _isPlaying));
        }
    }

    /// <inheritdoc />
    public Task PauseAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_stream != null && _isPlaying)
            {
                _stream.Stop();
                _isPlaying = false;
                _logger?.LogDebug("PortAudio playback paused");
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ResumeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_stream != null && !_isPlaying && _buffer?.Available > 0)
            {
                return Task.FromResult(StartPlaybackInternal());
            }
        }

        // Not in a state to resume (no stream or already playing)
        return Task.FromResult(_isPlaying);
    }

    /// <inheritdoc />
    public Task FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            _buffer?.Clear();
            _samplesWritten = 0;
            _samplesPlayed = 0;
            _logger?.LogDebug("PortAudio buffer flushed");
        }

        return Task.CompletedTask;
    }

    private void CleanupStream()
    {
        if (_stream != null)
        {
            if (_isPlaying)
            {
                try { _stream.Stop(); } catch { }
                _isPlaying = false;
            }

            _stream.Dispose();
            _stream = null;
        }

        _buffer = null;
        _isInitialized = false;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        lock (_lock)
        {
            CleanupStream();
            _disposed = true;
        }

        _logger?.LogDebug("PortAudio sink disposed");

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Thread-safe circular buffer for audio data with blocking write support.
/// </summary>
internal sealed class CircularAudioBuffer
{
    private readonly byte[] _buffer;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _spaceAvailable = new(true);
    private int _readPos;
    private int _writePos;
    private int _available;

    public CircularAudioBuffer(int capacity)
    {
        _buffer = new byte[capacity];
    }

    public int Capacity => _buffer.Length;
    public int FreeSpace
    {
        get { lock (_lock) return _buffer.Length - _available; }
    }
    public int Available
    {
        get { lock (_lock) return _available; }
    }

    /// <summary>
    /// Writes data to the buffer, blocking if necessary until space is available.
    /// </summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var remaining = data;

        while (remaining.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int written;
            lock (_lock)
            {
                var freeSpace = _buffer.Length - _available;
                if (freeSpace > 0)
                {
                    var toWrite = Math.Min(remaining.Length, freeSpace);
                    WriteInternal(remaining.Span[..toWrite]);
                    written = toWrite;
                }
                else
                {
                    written = 0;
                    _spaceAvailable.Reset();
                }
            }

            if (written > 0)
            {
                remaining = remaining[written..];
            }
            else
            {
                // Wait for space to become available (with timeout to allow cancellation checks)
                try
                {
                    _spaceAvailable.Wait(50, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }
    }

    private void WriteInternal(ReadOnlySpan<byte> data)
    {
        // Caller holds _lock
        var toWrite = Math.Min(data.Length, _buffer.Length - _available);
        if (toWrite == 0) return;

        var firstChunk = Math.Min(toWrite, _buffer.Length - _writePos);
        data[..firstChunk].CopyTo(_buffer.AsSpan(_writePos));

        if (toWrite > firstChunk)
        {
            data.Slice(firstChunk, toWrite - firstChunk).CopyTo(_buffer);
        }

        _writePos = (_writePos + toWrite) % _buffer.Length;
        _available += toWrite;
    }

    public int Read(Span<byte> destination)
    {
        lock (_lock)
        {
            var toRead = Math.Min(destination.Length, _available);
            if (toRead == 0) return 0;

            var firstChunk = Math.Min(toRead, _buffer.Length - _readPos);
            _buffer.AsSpan(_readPos, firstChunk).CopyTo(destination);

            if (toRead > firstChunk)
            {
                _buffer.AsSpan(0, toRead - firstChunk).CopyTo(destination[firstChunk..]);
            }

            _readPos = (_readPos + toRead) % _buffer.Length;
            _available -= toRead;

            // Signal that space is available for writers
            _spaceAvailable.Set();

            return toRead;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _readPos = 0;
            _writePos = 0;
            _available = 0;
            _spaceAvailable.Set();
        }
    }
}
