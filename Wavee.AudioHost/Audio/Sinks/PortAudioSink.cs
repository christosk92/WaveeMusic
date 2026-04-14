using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PortAudioSharp;
using Wavee.AudioHost.Audio.Abstractions;
using Wavee.AudioHost.Audio.Processors;
using Wavee.Playback.Contracts;

namespace Wavee.AudioHost.Audio.Sinks;

/// <summary>
/// Cross-platform audio output using PortAudio.
/// Supports WASAPI (Windows), CoreAudio (macOS), ALSA (Linux).
/// </summary>
public sealed class PortAudioSink : IAudioSink, IDeviceSelectableSink
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

    // Current PortAudio device index (mutated by explicit user switches via SwitchToDeviceAsync)
    private int _currentDeviceIndex;

    // Optional final-stage processor applied after the circular buffer is read,
    // so user volume changes are not delayed by already-buffered PCM.
    private VolumeProcessor? _realtimeVolumeProcessor;

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

    public void SetRealtimeVolumeProcessor(VolumeProcessor? processor)
    {
        lock (_lock)
        {
            _realtimeVolumeProcessor = processor;
            var format = _format;
            if (processor != null && format != null)
                processor.InitializeAsync(format).GetAwaiter().GetResult();
        }
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
            _realtimeVolumeProcessor?.InitializeAsync(format, cancellationToken).GetAwaiter().GetResult();

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

            var volumeProcessor = _realtimeVolumeProcessor;
            if (bytesRead > 0 && volumeProcessor is { IsEnabled: true })
                volumeProcessor.ProcessInPlace(span[..bytesRead]);

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

        // Auto-start playback once we've buffered ~2s of audio.
        // The extra headroom absorbs GC pauses and I/O contention during the
        // concurrent track download burst that happens at track start.
        lock (_lock)
        {
            if (!_isPlaying && _buffer.Available > _format!.BytesPerSecond * 2)
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
    // DEVICE CHANGE DETECTION & SELECTION
    // ================================================================

    /// <summary>
    /// Core device-switch routine used by explicit user-initiated <see cref="SwitchToDeviceAsync"/> calls.
    /// Must be called outside the _lock (this method acquires it).
    /// </summary>
    private void SwitchToDeviceInternal(int newDeviceIndex)
    {
        lock (_lock)
        {
            if (_disposed || _format == null)
                return;

            var wasPlaying = _isPlaying;
            var format = _format;
            string? oldName = null, newName = null;
            try { oldName = PortAudioSharp.PortAudio.GetDeviceInfo(_currentDeviceIndex).name; } catch { }

            _logger?.LogInformation(
                "[PortAudio] Switching output device: {OldName} (idx={OldIdx}) → idx={NewIdx}",
                oldName, _currentDeviceIndex, newDeviceIndex);

            // Save remaining buffer data so playback resumes seamlessly
            byte[]? savedData = null;
            int savedLength = 0;
            if (_buffer != null && _buffer.Available > 0)
            {
                savedData = new byte[_buffer.Available];
                savedLength = _buffer.Read(savedData);
                _logger?.LogDebug("[PortAudio] Saved {Bytes} bytes of buffered PCM for device switch", savedLength);
            }

            // Stop current stream
            _isPlaying = false;
            if (_stream != null)
            {
                _logger?.LogDebug("[PortAudio] Aborting existing stream on {OldName}", oldName);
                try { _stream.Abort(); } catch { }
                _stream.Dispose();
                _stream = null;
            }

            // Create new stream on the new device
            _currentDeviceIndex = newDeviceIndex;
            var deviceInfo = PortAudioSharp.PortAudio.GetDeviceInfo(newDeviceIndex);
            newName = deviceInfo.name;

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

            _logger?.LogInformation(
                "[PortAudio] Device switch complete: {NewName} (idx={NewIdx}), wasPlaying={WasPlaying}, restoredBytes={RestoredBytes}",
                newName, newDeviceIndex, wasPlaying, savedLength);
        }
    }

    // ================================================================
    // IDeviceSelectableSink
    // ================================================================

    /// <inheritdoc />
    public string? CurrentDeviceName
    {
        get
        {
            if (_disposed || !_isInitialized)
                return null;
            try
            {
                return PortAudioSharp.PortAudio.GetDeviceInfo(_currentDeviceIndex).name;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioOutputDeviceDto> EnumerateOutputDevices()
    {
        // Returns whatever PortAudio has cached since the last Pa_Initialize. This is
        // the "cheap" path — no audio gap. To pick up newly-plugged devices, callers
        // must first invoke RefreshPortAudioDeviceList() (exposed via the IPC command
        // "refresh_audio_devices" so the UI can request an explicit rescan on demand).
        EnsurePortAudioInitialized();

        int wasapiIndex = FindWasapiHostApiIndex();
        _logger?.LogDebug("[PortAudio] EnumerateOutputDevices: wasapiHostApi={WasapiIdx}, totalDevices={Total}",
            wasapiIndex, PortAudioSharp.PortAudio.DeviceCount);

        var defaultIndex = PortAudioSharp.PortAudio.DefaultOutputDevice;
        var count = PortAudioSharp.PortAudio.DeviceCount;
        var list = new List<AudioOutputDeviceDto>(count);
        for (int i = 0; i < count; i++)
        {
            DeviceInfo info;
            try
            {
                info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
            }
            catch
            {
                continue;
            }

            if (info.maxOutputChannels <= 0)
                continue;

            // Filter to WASAPI host API only — PortAudio otherwise surfaces the same
            // physical endpoint multiple times (MME, DirectSound, WDM-KS variants) plus
            // meta devices like "Microsoft Sound Mapper" and "Primary Sound Driver" that
            // don't match what the user sees in Windows Sound settings.
            if (wasapiIndex >= 0)
            {
                int hostApiIndex = TryGetDeviceHostApi(i);
                if (hostApiIndex != wasapiIndex) continue;
            }
            else
            {
                // Fallback: name-based filter if we couldn't detect WASAPI index
                var name = info.name ?? string.Empty;
                if (name.Contains("Sound Mapper", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Primary Sound Driver", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("()")
                    || name.Contains("\\System32\\drivers\\", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var dto = new AudioOutputDeviceDto
            {
                DeviceIndex = i,
                Name = info.name ?? $"Device {i}",
                IsDefault = i == defaultIndex
            };
            _logger?.LogDebug("[PortAudio] Enumerated device: {Name} (idx={Idx}, isDefault={IsDefault})",
                dto.Name, dto.DeviceIndex, dto.IsDefault);
            list.Add(dto);
        }
        _logger?.LogDebug("[PortAudio] EnumerateOutputDevices result: {Count} WASAPI devices", list.Count);
        return list;
    }

    /// <summary>
    /// Terminates and re-initializes PortAudio so it enumerates the live device set
    /// (necessary for PortAudio to pick up newly-plugged devices like Bluetooth headphones).
    ///
    /// If a stream is currently open, this saves its buffered PCM + format, tears the
    /// stream down, re-inits PortAudio, re-resolves the current device by name (indexes
    /// change across a re-init), and re-opens the stream on the matching device. The
    /// buffered PCM is restored so there's only a brief gap (~50-100ms) in playback.
    /// </summary>
    private void RefreshPortAudioDeviceList()
    {
        lock (_lock)
        {
            if (_disposed || !_initialized)
                return;

            // Snapshot stream state (if any) before tearing down.
            var hadStream = _stream != null;
            var savedFormat = _format;
            var savedDeviceName = hadStream ? CurrentDeviceName : null;
            var wasPlaying = hadStream && _isPlaying;
            byte[]? savedData = null;
            int savedLen = 0;

            _logger?.LogInformation(
                "[PortAudio] RefreshDeviceList: hadStream={HadStream}, currentDevice={Device}, wasPlaying={WasPlaying}",
                hadStream, savedDeviceName, wasPlaying);

            if (hadStream)
            {
                if (_buffer != null && _buffer.Available > 0)
                {
                    savedData = new byte[_buffer.Available];
                    savedLen = _buffer.Read(savedData);
                    _logger?.LogDebug("[PortAudio] Saved {Bytes} bytes of buffered PCM before Pa_Terminate", savedLen);
                }

                _isPlaying = false;
                try { _stream!.Abort(); } catch { }
                try { _stream!.Dispose(); } catch { }
                _stream = null;
            }

            // Cycle PortAudio so it re-scans the system audio devices.
            _logger?.LogDebug("[PortAudio] Calling Pa_Terminate...");
            try { PortAudioSharp.PortAudio.Terminate(); } catch { }
            _logger?.LogDebug("[PortAudio] Calling Pa_Initialize...");
            try
            {
                PortAudioSharp.PortAudio.Initialize();
                _initialized = true;
                _logger?.LogDebug("[PortAudio] Pa_Initialize OK — device count={Count}", PortAudioSharp.PortAudio.DeviceCount);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PortAudio] Pa_Initialize failed");
                _initialized = false;
                return;
            }

            if (!hadStream || savedFormat == null)
            {
                _logger?.LogDebug("[PortAudio] No stream to reopen after refresh (hadStream={HadStream})", hadStream);
                return;
            }

            // Find the new index of the device we were playing on (indexes change on re-init).
            var newIndex = FindDeviceIndexByName(savedDeviceName)
                           ?? PortAudioSharp.PortAudio.DefaultOutputDevice;
            if (newIndex == PortAudioSharp.PortAudio.NoDevice)
            {
                _logger?.LogWarning("[PortAudio] No output device available after Pa re-init; cannot reopen stream");
                return;
            }

            string? reopenName = null;
            try { reopenName = PortAudioSharp.PortAudio.GetDeviceInfo(newIndex).name; } catch { }
            _logger?.LogInformation(
                "[PortAudio] Reopening stream on {DeviceName} (idx={Idx}), wasPlaying={WasPlaying}",
                reopenName, newIndex, wasPlaying);

            try
            {
                ReopenStreamOnDevice(newIndex, savedFormat, savedData, savedLen, wasPlaying);
                _logger?.LogInformation("[PortAudio] Stream reopened successfully on {DeviceName}", reopenName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PortAudio] Failed to reopen stream after device refresh");
            }
        }
    }

    /// <summary>
    /// Best-effort lookup of a PortAudio device index by its friendly name.
    /// </summary>
    private static int? FindDeviceIndexByName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        try
        {
            var count = PortAudioSharp.PortAudio.DeviceCount;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var info = PortAudioSharp.PortAudio.GetDeviceInfo(i);
                    if (info.maxOutputChannels > 0 &&
                        string.Equals(info.name, name, StringComparison.Ordinal))
                        return i;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Creates a new PortAudio stream on the given device index, reusing the provided
    /// format and restoring any previously-buffered PCM so playback continues seamlessly.
    /// </summary>
    private void ReopenStreamOnDevice(
        int deviceIndex,
        AudioFormat format,
        byte[]? savedData,
        int savedLen,
        bool wasPlaying)
    {
        _currentDeviceIndex = deviceIndex;
        var deviceInfo = PortAudioSharp.PortAudio.GetDeviceInfo(deviceIndex);
        _logger?.LogDebug(
            "[PortAudio] ReopenStreamOnDevice: {DeviceName} (idx={Idx}), {Rate}Hz {Channels}ch {Bits}bit, savedBytes={SavedBytes}, wasPlaying={WasPlaying}",
            deviceInfo.name, deviceIndex, format.SampleRate, format.Channels, format.BitsPerSample, savedLen, wasPlaying);

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

        if (savedData != null && savedLen > 0)
            _buffer.WriteImmediate(savedData.AsSpan(0, savedLen));

        if (wasPlaying)
            StartPlaybackInternal();
    }

    // ── Native PortAudio host-API interop ──
    //
    // PortAudioSharp2 only wraps device-level APIs. For host-API filtering we P/Invoke
    // the native portaudio.dll directly. The enum values match the PortAudio C header
    // (paWASAPI = 13 on recent builds).

    private const int PaHostApiTypeWasapi = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct PaHostApiInfoNative
    {
        public int structVersion;
        public int type;
        public IntPtr name; // const char*
        public int deviceCount;
        public int defaultInputDevice;
        public int defaultOutputDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaDeviceInfoNative
    {
        public int structVersion;
        public IntPtr name;
        public int hostApi;
        public int maxInputChannels;
        public int maxOutputChannels;
        public double defaultLowInputLatency;
        public double defaultLowOutputLatency;
        public double defaultHighInputLatency;
        public double defaultHighOutputLatency;
        public double defaultSampleRate;
    }

    [DllImport("portaudio", EntryPoint = "Pa_GetHostApiCount")]
    private static extern int Pa_GetHostApiCount();

    [DllImport("portaudio", EntryPoint = "Pa_GetHostApiInfo")]
    private static extern IntPtr Pa_GetHostApiInfo(int hostApi);

    [DllImport("portaudio", EntryPoint = "Pa_GetDeviceInfo")]
    private static extern IntPtr Pa_GetDeviceInfo(int device);

    private static int FindWasapiHostApiIndex()
    {
        try
        {
            int count = Pa_GetHostApiCount();
            for (int i = 0; i < count; i++)
            {
                var ptr = Pa_GetHostApiInfo(i);
                if (ptr == IntPtr.Zero) continue;
                var info = Marshal.PtrToStructure<PaHostApiInfoNative>(ptr);
                if (info.type == PaHostApiTypeWasapi) return i;
            }
        }
        catch
        {
        }
        return -1;
    }

    private static int TryGetDeviceHostApi(int deviceIndex)
    {
        try
        {
            var ptr = Pa_GetDeviceInfo(deviceIndex);
            if (ptr == IntPtr.Zero) return -1;
            var info = Marshal.PtrToStructure<PaDeviceInfoNative>(ptr);
            return info.hostApi;
        }
        catch
        {
            return -1;
        }
    }

    /// <inheritdoc />
    public void RefreshDeviceList()
    {
        RefreshPortAudioDeviceList();
    }

    /// <inheritdoc />
    public Task SwitchToDefaultDeviceAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                // Re-init PortAudio so it discovers newly-plugged devices (Bluetooth, USB DAC).
                // Then reopen the stream on whatever Pa_GetDefaultOutputDevice() returns now.
                RefreshPortAudioDeviceListAndFollowDefault();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SwitchToDefaultDeviceAsync failed");
                throw;
            }
        }, ct);
    }

    /// <summary>
    /// Variant of <see cref="RefreshPortAudioDeviceList"/> that re-opens the output stream
    /// on the system default device rather than trying to reattach to the old device by name.
    /// Used when Windows signals that the system default output changed.
    /// </summary>
    private void RefreshPortAudioDeviceListAndFollowDefault()
    {
        lock (_lock)
        {
            if (_disposed || !_initialized)
                return;

            var hadStream = _stream != null;
            var savedFormat = _format;
            var savedDeviceName = hadStream ? CurrentDeviceName : null;
            var wasPlaying = hadStream && _isPlaying;
            byte[]? savedData = null;
            int savedLen = 0;

            _logger?.LogInformation(
                "[PortAudio] FollowDefault: Windows default output changed — hadStream={HadStream}, currentDevice={Device}, wasPlaying={WasPlaying}",
                hadStream, savedDeviceName, wasPlaying);

            if (hadStream)
            {
                if (_buffer != null && _buffer.Available > 0)
                {
                    savedData = new byte[_buffer.Available];
                    savedLen = _buffer.Read(savedData);
                    _logger?.LogDebug("[PortAudio] Saved {Bytes} bytes of buffered PCM before Pa_Terminate", savedLen);
                }

                _isPlaying = false;
                _logger?.LogDebug("[PortAudio] Aborting stream on {OldDevice} before Pa_Terminate", savedDeviceName);
                try { _stream!.Abort(); } catch { }
                try { _stream!.Dispose(); } catch { }
                _stream = null;
            }

            // Cycle PortAudio so it re-scans the system audio devices.
            _logger?.LogDebug("[PortAudio] Calling Pa_Terminate...");
            try { PortAudioSharp.PortAudio.Terminate(); } catch { }
            _logger?.LogDebug("[PortAudio] Calling Pa_Initialize...");
            try
            {
                PortAudioSharp.PortAudio.Initialize();
                _initialized = true;
                _logger?.LogDebug("[PortAudio] Pa_Initialize OK — device count={Count}", PortAudioSharp.PortAudio.DeviceCount);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PortAudio] Pa_Initialize failed — cannot follow new default device");
                _initialized = false;
                return;
            }

            if (!hadStream || savedFormat == null)
            {
                _logger?.LogDebug("[PortAudio] No stream to reopen after FollowDefault (hadStream={HadStream})", hadStream);
                return;
            }

            // Follow the new system default (not the old device).
            var newIndex = PortAudioSharp.PortAudio.DefaultOutputDevice;
            if (newIndex == PortAudioSharp.PortAudio.NoDevice)
            {
                _logger?.LogWarning("[PortAudio] Pa_GetDefaultOutputDevice returned NoDevice after re-init; cannot follow new default");
                return;
            }

            string? newName = null;
            try { newName = PortAudioSharp.PortAudio.GetDeviceInfo(newIndex).name; } catch { }
            _logger?.LogInformation(
                "[PortAudio] Following new Windows default: {NewName} (idx={NewIdx}), wasPlaying={WasPlaying}",
                newName, newIndex, wasPlaying);

            try
            {
                ReopenStreamOnDevice(newIndex, savedFormat, savedData, savedLen, wasPlaying);
                _logger?.LogInformation("[PortAudio] Stream successfully reopened on new default device: {NewName}", newName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PortAudio] Failed to reopen stream on new default device {NewName}", newName);
            }
        }
    }

    /// <inheritdoc />
    public Task SwitchToDeviceAsync(int deviceIndex, CancellationToken ct = default)
    {
        if (deviceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(deviceIndex));

        if (deviceIndex == _currentDeviceIndex)
            return Task.CompletedTask;

        // PortAudio device operations must be serialized; do the switch on a worker so we
        // don't block the caller (IPC command thread).
        return Task.Run(() =>
        {
            try
            {
                SwitchToDeviceInternal(deviceIndex);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to switch to device index {DeviceIndex}", deviceIndex);
                throw;
            }
        }, ct);
    }

    private void CleanupStream()
    {
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
