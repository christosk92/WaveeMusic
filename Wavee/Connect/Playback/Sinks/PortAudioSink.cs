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
    private volatile bool _isPlaying; // volatile: read by native PortAudio callback thread
    private bool _isInitialized;
    private long _samplesWritten;
    private long _samplesPlayed;

    // Playback position tracking (based on bytes actually played through speakers)
    private long _basePositionMs;
    private long _bytesPlayedSinceBase;
    private double _bytesPerMs; // cached for callback performance

    // Device change monitoring
    private int _currentDeviceIndex;
    private Timer? _deviceCheckTimer;

    // Real-time volume applied in the callback (instant, no buffer delay)
    private volatile float _callbackVolume = 1.0f;

    // Underrun detection (logged from callback thread)
    private long _underrunCount;

    // Seek muting: output silence between flush and first write to prevent
    // partial-buffer clicks/pops during seek transitions
    private volatile bool _seekMute;
    private int _seekUnmuteThresholdBytes;

    // Deferred logging flags: set in callback (zero-alloc), logged from managed thread
    private volatile bool _callbackUnderflowFlag;
    private volatile bool _bufferUnderrunFlag;
    private volatile int _lastUnderrunBytesRead;
    private volatile int _lastUnderrunBytesNeeded;
    private long _lastUnderflowLogAtMs;
    private long _lastBufferUnderrunLogAtMs;

    private const long UnderflowLogIntervalMs = 2000;
    // Slightly larger callback quantum reduces risk of steady-playback underflows
    // caused by scheduler jitter / brief GC pauses.
    private const int CallbackPeriodMs = 80;

    public string SinkName => "PortAudio";

    /// <summary>
    /// Sets the playback volume applied directly in the audio callback.
    /// Takes effect within ~50ms (next callback), bypassing the circular buffer delay.
    /// </summary>
    public float CallbackVolume
    {
        get => _callbackVolume;
        set => _callbackVolume = Math.Clamp(value, 0f, 1.0f);
    }

    /// <inheritdoc />
    public long PlaybackPositionMs
    {
        get
        {
            var bytesPlayed = Interlocked.Read(ref _bytesPlayedSinceBase);
            return _basePositionMs + (long)(bytesPlayed / _bytesPerMs);
        }
    }

    /// <summary>
    /// Total number of callback underflow events seen by PortAudio.
    /// </summary>
    public long UnderrunCount => Volatile.Read(ref _underrunCount);

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
            _bytesPerMs = format.BytesPerSecond / 1000.0;

            // Calculate buffer size in bytes (2x requested for safety margin)
            var bufferBytes = format.BytesPerSecond * bufferSizeMs * 2 / 1000;
            _buffer = new CircularAudioBuffer(bufferBytes);

            // Get default output device
            var deviceIndex = PortAudioSharp.PortAudio.DefaultOutputDevice;
            if (deviceIndex == PortAudioSharp.PortAudio.NoDevice)
            {
                throw new InvalidOperationException("No default audio output device found");
            }

            _currentDeviceIndex = deviceIndex;
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
                suggestedLatency = Math.Max(deviceInfo.defaultHighOutputLatency, 0.3)
            };

            // Callback period trades control latency for playback robustness.
            // Using 80ms reduces sporadic steady-playback underflows on some systems.
            var framesPerBuffer = (uint)(format.SampleRate * CallbackPeriodMs / 1000);
            // Require at least ~2 callback chunks before unmuting after a flush/seek.
            // This reduces partial callback fills (and audible hiccups) right after seeks.
            _seekUnmuteThresholdBytes = Math.Max(
                format.BytesPerFrame,
                (int)(framesPerBuffer * format.BytesPerFrame * 2));

            // Create stream with callback
            _stream = new PortAudioSharp.Stream(
                inParams: null,  // No input
                outParams: outputParams,
                sampleRate: format.SampleRate,
                framesPerBuffer: framesPerBuffer,
                streamFlags: StreamFlags.PrimeOutputBuffersUsingStreamCallback,
                callback: StreamCallback,
                userData: null);

            _isInitialized = true;
            _samplesWritten = 0;
            _samplesPlayed = 0;
            _basePositionMs = 0;
            Interlocked.Exchange(ref _bytesPlayedSinceBase, 0);

            // Start device change monitoring (check every 2 seconds)
            _deviceCheckTimer = new Timer(CheckDeviceChange, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            _logger?.LogDebug(
                "Initialized PortAudio sink: {SampleRate}Hz, {Channels}ch, {BitsPerSample}bit, buffer={BufferMs}ms, device={DeviceIndex}",
                format.SampleRate, format.Channels, format.BitsPerSample, bufferSizeMs, deviceIndex);
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
        // Flag PortAudio-reported underflow for deferred logging (zero-alloc in callback)
        if ((statusFlags & StreamCallbackFlags.OutputUnderflow) != 0)
        {
            Interlocked.Increment(ref _underrunCount);
            _callbackUnderflowFlag = true;
        }

        // Capture references locally to avoid TOCTOU race with CleanupStream/InitializeAsync.
        // These are reference-type fields that could be set to null by another thread
        // between our null-check and the actual dereference.
        var buffer = _buffer;
        var format = _format;

        if (_seekMute || buffer == null || format == null || !_isPlaying)
        {
            // Output silence. Use format if available, otherwise fall back to a safe estimate.
            var bytesPerFrame = format?.BytesPerFrame ?? 4; // fallback: 2ch * 16bit = 4
            var silenceBytes = (int)(frameCount * bytesPerFrame);
            unsafe
            {
                new Span<byte>((void*)output, silenceBytes).Clear();
            }
            return StreamCallbackResult.Continue;
        }

        var bytesNeeded = (int)(frameCount * format.BytesPerFrame);

        // Read from circular buffer into unmanaged memory
        unsafe
        {
            var span = new Span<byte>((void*)output, bytesNeeded);
            var bytesRead = buffer.Read(span);

            // If we didn't get enough data, pad with silence
            if (bytesRead < bytesNeeded)
            {
                span.Slice(bytesRead).Clear();
                _bufferUnderrunFlag = true;
                _lastUnderrunBytesRead = bytesRead;
                _lastUnderrunBytesNeeded = bytesNeeded;
            }

            // Apply real-time volume scaling directly in the callback.
            // This bypasses the circular buffer delay — volume changes take effect
            // on the very next callback (~50ms) instead of after 8s of buffered audio.
            var vol = _callbackVolume;
            if (bytesRead > 0 && Math.Abs(vol - 1.0f) > 0.0001f && format.BitsPerSample == 16)
            {
                var samples = bytesRead / 2;
                var ptr = (short*)output;
                for (int i = 0; i < samples; i++)
                {
                    ptr[i] = (short)Math.Clamp((int)(ptr[i] * vol), short.MinValue, short.MaxValue);
                }
            }

            // Track playback position from bytes actually sent to the speaker
            if (bytesRead > 0)
            {
                Interlocked.Add(ref _bytesPlayedSinceBase, bytesRead);
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

        // Auto-start playback once we've buffered ~1s of audio
        lock (_lock)
        {
            if (!_isPlaying && _buffer.Available > _format!.BytesPerSecond)
            {
                StartPlayback();
            }
        }

        // Deferred logging from callback flags (zero-alloc in callback thread)
        if (_callbackUnderflowFlag)
        {
            var nowMs = Environment.TickCount64;
            if (nowMs - Volatile.Read(ref _lastUnderflowLogAtMs) >= UnderflowLogIntervalMs)
            {
                _callbackUnderflowFlag = false;
                Volatile.Write(ref _lastUnderflowLogAtMs, nowMs);
                _logger?.LogWarning("PortAudio output underflow detected (total: {Count})",
                    Volatile.Read(ref _underrunCount));
            }
        }
        if (_bufferUnderrunFlag)
        {
            var nowMs = Environment.TickCount64;
            if (nowMs - Volatile.Read(ref _lastBufferUnderrunLogAtMs) >= UnderflowLogIntervalMs)
            {
                _bufferUnderrunFlag = false;
                Volatile.Write(ref _lastBufferUnderrunLogAtMs, nowMs);
                _logger?.LogWarning(
                    "PortAudio buffer underrun: got {BytesRead}/{BytesNeeded} bytes",
                    _lastUnderrunBytesRead, _lastUnderrunBytesNeeded);
            }
        }

        // Write to circular buffer (blocks if buffer is full - provides backpressure)
        await _buffer.WriteAsync(audioData, cancellationToken);

        // After a seek flush, unmute only after we have enough buffered audio to satisfy
        // at least one callback comfortably, avoiding immediate partial callback fills.
        if (_seekMute && _buffer.Available >= _seekUnmuteThresholdBytes)
        {
            _seekMute = false;
        }

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
            // Set flag BEFORE Start() so the priming callback (triggered by
            // PrimeOutputBuffersUsingStreamCallback) reads real audio from the
            // circular buffer instead of outputting silence.  Without this, the
            // first real callback after Start() returns sees an empty PortAudio
            // internal buffer and reports OutputUnderflow.
            _isPlaying = true;
            _stream.Start();
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

            return Task.FromResult(new AudioSinkStatus(PlaybackPositionMs, bufferedMs, _isPlaying));
        }
    }

    /// <inheritdoc />
    public Task PauseAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Pure flag flip — callback outputs silence on next invocation (~5ms).
        // Stream stays running so resume is instant (no device restart).
        _isPlaying = false;
        _logger?.LogDebug("PortAudio paused (flag flip)");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ResumeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Pure flag flip — callback immediately starts outputting buffered audio.
        // Stream never stopped, so there's zero startup latency.
        if (_stream != null)
        {
            _isPlaying = true;
            _logger?.LogDebug("PortAudio resumed (flag flip)");
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task FlushAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            _seekMute = true; // mute callback until first post-flush write
            _buffer?.Clear();
            _samplesWritten = 0;
            _samplesPlayed = 0;
            Interlocked.Exchange(ref _bytesPlayedSinceBase, 0);
            _logger?.LogDebug("PortAudio buffer flushed");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetBasePosition(long positionMs)
    {
        _basePositionMs = positionMs;
        Interlocked.Exchange(ref _bytesPlayedSinceBase, 0);
        _logger?.LogDebug("PortAudio base position set to {PositionMs}ms", positionMs);
    }

    /// <inheritdoc />
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var buffer = _buffer;
        if (buffer == null)
            return;

        _logger?.LogDebug("Draining audio sink buffer...");

        // Wait for the buffer to empty (audio callback is draining it)
        // Safety timeout: 10 seconds max to avoid hanging forever
        var timeout = TimeSpan.FromSeconds(10);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (buffer.Available > 0 && sw.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(50, cancellationToken);
        }

        if (sw.Elapsed >= timeout)
        {
            _logger?.LogWarning("Drain timed out after {Elapsed}ms with {Remaining} bytes remaining",
                sw.ElapsedMilliseconds, buffer.Available);
        }
        else
        {
            _logger?.LogDebug("Drain completed in {Elapsed}ms", sw.ElapsedMilliseconds);
        }
    }

    // ================================================================
    // DEVICE CHANGE DETECTION
    // ================================================================

    /// <summary>
    /// Periodically checks if the default output device has changed (e.g., Bluetooth connected).
    /// If so, reinitializes the PortAudio stream on the new device.
    /// </summary>
    private void CheckDeviceChange(object? state)
    {
        if (_disposed || !_isInitialized || _format == null)
            return;

        try
        {
            var newDeviceIndex = PortAudioSharp.PortAudio.DefaultOutputDevice;
            if (newDeviceIndex == _currentDeviceIndex || newDeviceIndex == PortAudioSharp.PortAudio.NoDevice)
                return;

            _logger?.LogInformation(
                "Audio output device changed: {OldDevice} -> {NewDevice}, reinitializing...",
                _currentDeviceIndex, newDeviceIndex);

            lock (_lock)
            {
                if (_disposed || _format == null)
                    return;

                var wasPlaying = _isPlaying;
                var format = _format;

                // Save remaining buffer data
                byte[]? savedData = null;
                int savedLength = 0;
                if (_buffer != null && _buffer.Available > 0)
                {
                    savedData = new byte[_buffer.Available];
                    savedLength = _buffer.Read(savedData);
                }

                // Stop current stream
                _isPlaying = false;
                if (_stream != null)
                {
                    try { _stream.Abort(); } catch { }
                    _stream.Dispose();
                    _stream = null;
                }

                // Create new stream on the new device
                _currentDeviceIndex = newDeviceIndex;
                var deviceInfo = PortAudioSharp.PortAudio.GetDeviceInfo(newDeviceIndex);

                var outputParams = new StreamParameters
                {
                    device = newDeviceIndex,
                    channelCount = format.Channels,
                    sampleFormat = format.BitsPerSample switch
                    {
                        16 => SampleFormat.Int16,
                        24 => SampleFormat.Int24,
                        32 => SampleFormat.Float32,
                        _ => SampleFormat.Int16
                    },
                    suggestedLatency = Math.Max(deviceInfo.defaultHighOutputLatency, 0.3)
                };

                var framesPerBuffer = (uint)(format.SampleRate * CallbackPeriodMs / 1000);
                _seekUnmuteThresholdBytes = Math.Max(
                    format.BytesPerFrame,
                    (int)(framesPerBuffer * format.BytesPerFrame * 2));

                var bufferCapacity = _buffer?.Capacity ?? (format.BytesPerSecond * 2000 * 2 / 1000);
                _buffer = new CircularAudioBuffer(bufferCapacity);

                _stream = new PortAudioSharp.Stream(
                    inParams: null,
                    outParams: outputParams,
                    sampleRate: format.SampleRate,
                    framesPerBuffer: framesPerBuffer,
                    streamFlags: StreamFlags.PrimeOutputBuffersUsingStreamCallback,
                    callback: StreamCallback,
                    userData: null);

                // Restore saved buffer data
                if (savedData != null && savedLength > 0)
                {
                    _buffer.WriteImmediate(savedData.AsSpan(0, savedLength));
                }

                // Restart playback if it was playing
                if (wasPlaying)
                {
                    StartPlaybackInternal();
                }

                _logger?.LogInformation("Audio output device switched successfully to device {DeviceIndex}", newDeviceIndex);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to switch audio output device");
        }
    }

    private void CleanupStream()
    {
        // Stop device monitoring
        _deviceCheckTimer?.Dispose();
        _deviceCheckTimer = null;

        if (_stream != null)
        {
            if (_isPlaying)
            {
                try { _stream.Abort(); } catch { }
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
    private readonly object _writeLock = new();
    private readonly ManualResetEventSlim _spaceAvailable = new(true);
    private volatile int _readPos;
    private int _writePos;   // only touched under _writeLock
    private int _available;  // updated via Interlocked from reader, under _writeLock from writer

    public CircularAudioBuffer(int capacity)
    {
        _buffer = new byte[capacity];
    }

    public int Capacity => _buffer.Length;
    public int FreeSpace => _buffer.Length - Volatile.Read(ref _available);
    public int Available => Volatile.Read(ref _available);

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
            lock (_writeLock)
            {
                var freeSpace = _buffer.Length - Volatile.Read(ref _available);
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

    /// <summary>
    /// Synchronously writes data to the buffer without blocking.
    /// Used for restoring buffer data during device switches.
    /// </summary>
    public void WriteImmediate(ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            WriteInternal(data);
        }
    }

    private void WriteInternal(ReadOnlySpan<byte> data)
    {
        // Caller holds _writeLock
        var avail = Volatile.Read(ref _available);
        var toWrite = Math.Min(data.Length, _buffer.Length - avail);
        if (toWrite == 0) return;

        var firstChunk = Math.Min(toWrite, _buffer.Length - _writePos);
        data[..firstChunk].CopyTo(_buffer.AsSpan(_writePos));

        if (toWrite > firstChunk)
        {
            data.Slice(firstChunk, toWrite - firstChunk).CopyTo(_buffer);
        }

        _writePos = (_writePos + toWrite) % _buffer.Length;
        Interlocked.Add(ref _available, toWrite);
    }

    /// <summary>
    /// Lock-free read for the PortAudio callback thread.
    /// Safe because: single reader (callback), _readPos only modified here,
    /// _available decremented atomically. Writer only touches _writePos and increments _available.
    /// </summary>
    public int Read(Span<byte> destination)
    {
        var avail = Volatile.Read(ref _available);
        var toRead = Math.Min(destination.Length, avail);
        if (toRead == 0) return 0;

        var readPos = _readPos;
        var firstChunk = Math.Min(toRead, _buffer.Length - readPos);
        _buffer.AsSpan(readPos, firstChunk).CopyTo(destination);

        if (toRead > firstChunk)
        {
            _buffer.AsSpan(0, toRead - firstChunk).CopyTo(destination[firstChunk..]);
        }

        _readPos = (readPos + toRead) % _buffer.Length;
        Interlocked.Add(ref _available, -toRead);

        // Signal that space is available for writers
        _spaceAvailable.Set();

        return toRead;
    }

    public void Clear()
    {
        lock (_writeLock)
        {
            _readPos = 0;
            _writePos = 0;
            Volatile.Write(ref _available, 0);
            _spaceAvailable.Set();
        }
    }
}
