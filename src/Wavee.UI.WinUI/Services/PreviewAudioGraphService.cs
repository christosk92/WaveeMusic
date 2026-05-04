using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Playback.Contracts;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Render;
using Wavee.UI.Services;
using WinRT;

namespace Wavee.UI.WinUI.Services;

public sealed class PreviewAudioGraphService : IPreviewAudioPlaybackEngine, IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly PreviewSpectrumAnalyzer _analyzer = new();

    private AudioGraph? _graph;
    private MediaSourceAudioInputNode? _sourceNode;
    private AudioDeviceOutputNode? _deviceOutputNode;
    private AudioFrameOutputNode? _frameOutputNode;
    private MediaPlayer? _fallbackPlayer;
    private Action<PreviewVisualizationFrame>? _onFrame;
    private Action? _onCompleted;
    private string? _sessionId;
    private long _frameSequence;
    private long _sessionVersion;
    private bool _hasLoggedFrameForSession;

    public string? CurrentSessionId { get; private set; }

    public PreviewAudioGraphService(ILogger<PreviewAudioGraphService>? logger = null)
    {
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    [Conditional("DEBUG")]
    private void TracePreview(string message)
    {
        Debug.WriteLine(
            $"[PreviewAudioGraphService] {message} | " +
            $"session='{CurrentSessionId ?? "<null>"}' graph={_graph != null} source={_sourceNode != null} " +
            $"device={_deviceOutputNode != null} fallback={_fallbackPlayer != null}");
    }

    public async Task<PreviewStartResult> StartAsync(
        string previewUrl,
        Action<PreviewVisualizationFrame> onFrame,
        Action onCompleted,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(previewUrl))
            return new PreviewStartResult(false, null);

        TracePreview($"StartAsync url='{previewUrl}'");
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StopCurrentSession_NoLock();

            var sessionId = Guid.NewGuid().ToString("N");
            ++_sessionVersion;

            _sessionId = sessionId;
            CurrentSessionId = sessionId;
            _onFrame = onFrame;
            _onCompleted = onCompleted;
            _frameSequence = 0;
            _hasLoggedFrameForSession = false;
            _analyzer.Reset();

            if (await TryStartAudioGraphSessionAsync(previewUrl, ct).ConfigureAwait(false))
            {
                TracePreview("StartAsync using AudioGraph");
                return new PreviewStartResult(true, CurrentSessionId);
            }

            StartFallbackSession(previewUrl);
            _logger?.LogDebug("Preview audio graph unavailable; running audio-only fallback without visualization for {SessionId}", sessionId);
            TracePreview("StartAsync using fallback player");
            return new PreviewStartResult(false, CurrentSessionId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        TracePreview("StopAsync");
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ++_sessionVersion;
            StopCurrentSession_NoLock();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> TryStartAudioGraphSessionAsync(string previewUrl, CancellationToken ct)
    {
        try
        {
            TracePreview("TryStartAudioGraphSessionAsync creating graph");
            var settings = new AudioGraphSettings(AudioRenderCategory.Media);
            var graphResult = await AudioGraph.CreateAsync(settings).AsTask(ct).ConfigureAwait(false);
            if (graphResult.Status != AudioGraphCreationStatus.Success || graphResult.Graph == null)
            {
                _logger?.LogDebug(
                    "Preview AudioGraph creation failed with status {Status} and error {Error}",
                    graphResult.Status,
                    graphResult.ExtendedError);
                return false;
            }

            var graph = graphResult.Graph;

            var outputResult = await graph.CreateDeviceOutputNodeAsync().AsTask(ct).ConfigureAwait(false);
            if (outputResult.Status != AudioDeviceNodeCreationStatus.Success || outputResult.DeviceOutputNode == null)
            {
                _logger?.LogDebug(
                    "Preview device output node creation failed with status {Status} and error {Error}",
                    outputResult.Status,
                    outputResult.ExtendedError);
                graph.Dispose();
                return false;
            }

            var mediaSource = MediaSource.CreateFromUri(new Uri(previewUrl));
            var sourceResult = await graph.CreateMediaSourceAudioInputNodeAsync(mediaSource).AsTask(ct).ConfigureAwait(false);
            if (sourceResult.Status != MediaSourceAudioInputNodeCreationStatus.Success || sourceResult.Node == null)
            {
                _logger?.LogDebug(
                    "Preview media source input node creation failed with status {Status} and error {Error}",
                    sourceResult.Status,
                    sourceResult.ExtendedError);
                outputResult.DeviceOutputNode.Dispose();
                graph.Dispose();
                return false;
            }

            var frameOutputNode = graph.CreateFrameOutputNode();
            sourceResult.Node.AddOutgoingConnection(outputResult.DeviceOutputNode);
            sourceResult.Node.AddOutgoingConnection(frameOutputNode);

            graph.QuantumStarted += OnGraphQuantumStarted;
            graph.UnrecoverableErrorOccurred += OnGraphUnrecoverableErrorOccurred;
            sourceResult.Node.MediaSourceCompleted += OnSourceNodeMediaSourceCompleted;

            _graph = graph;
            _deviceOutputNode = outputResult.DeviceOutputNode;
            _sourceNode = sourceResult.Node;
            _frameOutputNode = frameOutputNode;

            graph.Start();
            sourceResult.Node.Start();
            TracePreview("TryStartAudioGraphSessionAsync started graph and source");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Preview AudioGraph startup failed");
            StopCurrentSession_NoLock();
            return false;
        }
    }

    private void StartFallbackSession(string previewUrl)
    {
        try
        {
            if (_fallbackPlayer == null)
            {
                _fallbackPlayer = new MediaPlayer
                {
                    IsLoopingEnabled = false,
                    IsMuted = false
                };
                _fallbackPlayer.MediaOpened += OnFallbackPlayerMediaOpened;
                _fallbackPlayer.MediaFailed += OnFallbackPlayerMediaFailed;
                _fallbackPlayer.CurrentStateChanged += OnFallbackPlayerCurrentStateChanged;
            }

            _fallbackPlayer.MediaEnded -= OnFallbackPlayerMediaEnded;
            _fallbackPlayer.MediaEnded += OnFallbackPlayerMediaEnded;
            _fallbackPlayer.Source = MediaSource.CreateFromUri(new Uri(previewUrl));
            _fallbackPlayer.Play();
            TracePreview("StartFallbackSession play");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Preview fallback playback failed");
            StopCurrentSession_NoLock();
            throw;
        }
    }

    private void StopCurrentSession_NoLock()
    {
        TracePreview("StopCurrentSession_NoLock");
        if (_fallbackPlayer != null)
        {
            try
            {
                _fallbackPlayer.MediaEnded -= OnFallbackPlayerMediaEnded;
                _fallbackPlayer.Pause();
                _fallbackPlayer.Source = null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.LogDebug(ex, "Preview fallback player shutdown failed");
            }
        }

        if (_sourceNode != null)
            _sourceNode.MediaSourceCompleted -= OnSourceNodeMediaSourceCompleted;

        if (_graph != null)
        {
            _graph.QuantumStarted -= OnGraphQuantumStarted;
            _graph.UnrecoverableErrorOccurred -= OnGraphUnrecoverableErrorOccurred;
        }

        try
        {
            _sourceNode?.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Preview source node stop failed");
        }

        try
        {
            _graph?.Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Preview AudioGraph stop failed");
        }

        DisposeNode(ref _frameOutputNode);
        DisposeNode(ref _sourceNode);
        DisposeNode(ref _deviceOutputNode);
        DisposeNode(ref _graph);

        _analyzer.Reset();
        _onFrame = null;
        _onCompleted = null;
        _sessionId = null;
        CurrentSessionId = null;
        _frameSequence = 0;
        _hasLoggedFrameForSession = false;
    }

    private void OnGraphQuantumStarted(AudioGraph sender, object args)
    {
        AudioFrameOutputNode? frameOutputNode;
        AudioEncodingProperties? encodingProperties;
        string? sessionId;
        long sequence;

        lock (_stateGate)
        {
            if (!ReferenceEquals(sender, _graph) || _frameOutputNode == null || _onFrame == null || _sessionId == null)
                return;

            frameOutputNode = _frameOutputNode;
            encodingProperties = frameOutputNode.EncodingProperties;
            sessionId = _sessionId;
            sequence = ++_frameSequence;
        }

        try
        {
            using var frame = frameOutputNode.GetFrame();
            if (!TryAnalyzeFrame(frame, encodingProperties, out var amplitudes))
                return;

            if (!_hasLoggedFrameForSession)
            {
                _hasLoggedFrameForSession = true;
                TracePreview($"OnGraphQuantumStarted first frame amplitudes={amplitudes.Length}");
            }

            DispatchFrame(sessionId, amplitudes, completed: false, sequence);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "Preview frame processing failed");
        }
    }

    private void OnSourceNodeMediaSourceCompleted(MediaSourceAudioInputNode sender, object args)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (!ReferenceEquals(sender, _sourceNode))
                return;

            DispatchCompletedFrame();
            await NotifyCompletedAndStopAsync().ConfigureAwait(false);
        });
    }

    private void OnFallbackPlayerMediaEnded(MediaPlayer sender, object args)
    {
        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (!ReferenceEquals(sender, _fallbackPlayer))
                return;

            DispatchCompletedFrame();
            await NotifyCompletedAndStopAsync().ConfigureAwait(false);
        });
    }

    private void OnFallbackPlayerMediaOpened(MediaPlayer sender, object args)
    {
        TracePreview($"Fallback MediaOpened state={sender.CurrentState}");
    }

    private void OnFallbackPlayerMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        TracePreview($"Fallback MediaFailed error={args.Error} extended=0x{args.ExtendedErrorCode.HResult:x8}");
    }

    private void OnFallbackPlayerCurrentStateChanged(MediaPlayer sender, object args)
    {
        TracePreview($"Fallback CurrentStateChanged state={sender.CurrentState}");
    }

    private void OnGraphUnrecoverableErrorOccurred(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        _logger?.LogDebug(
            "Preview AudioGraph unrecoverable error: {Error}",
            args.Error);

        _dispatcherQueue.TryEnqueue(async () =>
        {
            if (!ReferenceEquals(sender, _graph))
                return;

            DispatchCompletedFrame();
            await NotifyCompletedAndStopAsync().ConfigureAwait(false);
        });
    }

    private async Task NotifyCompletedAndStopAsync()
    {
        Action? onCompleted;

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            onCompleted = _onCompleted;
            ++_sessionVersion;
            StopCurrentSession_NoLock();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        onCompleted?.Invoke();
    }

    private void DispatchCompletedFrame()
    {
        string? sessionId;
        long sequence;

        lock (_stateGate)
        {
            sessionId = _sessionId;
            sequence = ++_frameSequence;
        }

        if (sessionId == null)
            return;

        DispatchFrame(sessionId, Array.Empty<float>(), completed: true, sequence);
    }

    private void DispatchFrame(string sessionId, float[] amplitudes, bool completed, long sequence)
    {
        Action<PreviewVisualizationFrame>? onFrame;

        lock (_stateGate)
        {
            if (!string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
                return;

            onFrame = _onFrame;
        }

        if (onFrame == null)
            return;

        _dispatcherQueue.TryEnqueue(() => onFrame(new PreviewVisualizationFrame
        {
            SessionId = sessionId,
            Sequence = sequence,
            Completed = completed,
            Amplitudes = amplitudes
        }));
    }

    private bool TryAnalyzeFrame(AudioFrame frame, AudioEncodingProperties? encodingProperties, out float[] amplitudes)
    {
        amplitudes = [];

        if (encodingProperties == null)
            return false;

        var channelCount = Math.Max(1, (int)encodingProperties.ChannelCount);
        var sampleRate = Math.Max(1, (int)encodingProperties.SampleRate);

        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();

        unsafe
        {
            var access = reference.As<IMemoryBufferByteAccess>();
            access.GetBuffer(out var dataInBytes, out var capacityInBytes);
            if (dataInBytes == null || capacityInBytes < sizeof(float))
                return false;

            var floatCount = (int)capacityInBytes / sizeof(float);
            if (floatCount < channelCount)
                return false;

            var samples = new ReadOnlySpan<float>((float*)dataInBytes, floatCount);
            amplitudes = _analyzer.Process(samples, channelCount, sampleRate);
            return amplitudes.Length > 0;
        }
    }

    private static void DisposeNode<T>(ref T? disposable)
        where T : class, IDisposable
    {
        if (disposable == null)
            return;

        try
        {
            disposable.Dispose();
        }
        catch
        {
        }

        disposable = null;
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        if (_fallbackPlayer != null)
        {
            _fallbackPlayer.MediaOpened -= OnFallbackPlayerMediaOpened;
            _fallbackPlayer.MediaFailed -= OnFallbackPlayerMediaFailed;
            _fallbackPlayer.CurrentStateChanged -= OnFallbackPlayerCurrentStateChanged;
            _fallbackPlayer.Dispose();
        }
        _fallbackPlayer = null;
        _lifecycleGate.Dispose();
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* value, out uint capacity);
    }

    private sealed class PreviewSpectrumAnalyzer
    {
        private const int BarCount = 24;
        private const int FftLength = 2048;
        private const int FftMask = FftLength - 1;
        private const float MinFrequencyHz = 52f;
        private const float MaxFrequencyHz = 9800f;

        private static readonly float[] HannWindow = CreateHannWindow();

        private readonly float[] _history = new float[FftLength];
        private readonly float[] _real = new float[FftLength];
        private readonly float[] _imaginary = new float[FftLength];
        private readonly float[] _rawBands = new float[BarCount];
        private readonly float[] _smoothedBands = new float[BarCount];
        private readonly float[] _neighborBands = new float[BarCount];
        private readonly float[] _outputBands = new float[BarCount];

        private int _writeIndex;
        private int _sampleCount;
        private float _smoothedGain = 1f;
        private float _rollingPeak = 0.04f;

        public void Reset()
        {
            Array.Clear(_history);
            Array.Clear(_real);
            Array.Clear(_imaginary);
            Array.Clear(_rawBands);
            Array.Clear(_smoothedBands);
            Array.Clear(_neighborBands);
            Array.Clear(_outputBands);
            _writeIndex = 0;
            _sampleCount = 0;
            _smoothedGain = 1f;
            _rollingPeak = 0.04f;
        }

        public float[] Process(ReadOnlySpan<float> interleavedSamples, int channelCount, int sampleRate)
        {
            if (interleavedSamples.IsEmpty || channelCount <= 0)
                return [];

            for (int sampleIndex = 0; sampleIndex + channelCount <= interleavedSamples.Length; sampleIndex += channelCount)
            {
                float mono = 0f;
                for (int channel = 0; channel < channelCount; channel++)
                    mono += interleavedSamples[sampleIndex + channel];

                mono /= channelCount;
                _history[_writeIndex] = mono;
                _writeIndex = (_writeIndex + 1) & FftMask;
                if (_sampleCount < FftLength)
                    _sampleCount++;
            }

            if (_sampleCount < 256)
                return [];

            for (int i = 0; i < FftLength; i++)
            {
                var historyIndex = (_writeIndex + i) & FftMask;
                _real[i] = _history[historyIndex] * HannWindow[i];
                _imaginary[i] = 0f;
            }

            Transform(_real, _imaginary);

            var nyquist = Math.Max(1f, sampleRate / 2f);
            var maxFrequency = Math.Min(MaxFrequencyHz, nyquist * 0.92f);
            var minFrequency = Math.Min(MinFrequencyHz, maxFrequency * 0.5f);
            var maxBand = 0f;

            for (int bandIndex = 0; bandIndex < BarCount; bandIndex++)
            {
                var bandStart = bandIndex / (float)BarCount;
                var bandEnd = (bandIndex + 1) / (float)BarCount;
                var lowFrequency = LogLerp(minFrequency, maxFrequency, bandStart);
                var highFrequency = LogLerp(minFrequency, maxFrequency, bandEnd);
                var startBin = Math.Clamp((int)MathF.Floor(lowFrequency * FftLength / sampleRate), 1, (FftLength / 2) - 1);
                var endBin = Math.Clamp((int)MathF.Ceiling(highFrequency * FftLength / sampleRate), startBin + 1, FftLength / 2);

                var sum = 0f;
                var peak = 0f;
                for (int bin = startBin; bin < endBin; bin++)
                {
                    var magnitude = MathF.Sqrt((_real[bin] * _real[bin]) + (_imaginary[bin] * _imaginary[bin])) / FftLength;
                    var frequency = bin * sampleRate / (float)FftLength;
                    var compensated = magnitude * MathF.Pow(MathF.Max(frequency, 1f) / 220f, 0.24f);
                    sum += compensated * compensated;
                    peak = MathF.Max(peak, compensated);
                }

                var binCount = Math.Max(1, endBin - startBin);
                var rms = MathF.Sqrt(sum / binCount);
                var raw = (peak * 0.52f) + (rms * 0.48f);
                _rawBands[bandIndex] = raw;
                maxBand = MathF.Max(maxBand, raw);
            }

            var targetPeak = maxBand > 0.000001f ? maxBand : _rollingPeak;
            var peakSmoothing = targetPeak > _rollingPeak ? 0.22f : 0.06f;
            _rollingPeak += (targetPeak - _rollingPeak) * peakSmoothing;

            var targetGain = MathF.Min(8.8f, 0.31f / MathF.Max(_rollingPeak, 0.000001f));
            var gainSmoothing = targetGain > _smoothedGain ? 0.28f : 0.14f;
            _smoothedGain += (targetGain - _smoothedGain) * gainSmoothing;

            for (int bandIndex = 0; bandIndex < BarCount; bandIndex++)
            {
                var weighted = _rawBands[bandIndex] * _smoothedGain;
                var db = 20f * MathF.Log10(MathF.Max(weighted, 0.00025f));
                var normalized = Math.Clamp((db + 61f) / 30f, 0f, 1f);
                var highBandLift = 0.94f + ((bandIndex / (float)(BarCount - 1)) * 0.2f);
                normalized = Math.Clamp(normalized * highBandLift, 0f, 1f);
                var temporalSmoothing = normalized > _smoothedBands[bandIndex] ? 0.66f : 0.16f;
                _smoothedBands[bandIndex] += (normalized - _smoothedBands[bandIndex]) * temporalSmoothing;
            }

            for (int bandIndex = 0; bandIndex < BarCount; bandIndex++)
            {
                var left = bandIndex > 0 ? _smoothedBands[bandIndex - 1] : _smoothedBands[bandIndex];
                var center = _smoothedBands[bandIndex];
                var right = bandIndex + 1 < BarCount ? _smoothedBands[bandIndex + 1] : _smoothedBands[bandIndex];
                _neighborBands[bandIndex] = (left * 0.08f) + (center * 0.84f) + (right * 0.08f);
            }

            for (int bandIndex = 0; bandIndex < BarCount; bandIndex++)
            {
                var left = bandIndex > 0 ? _neighborBands[bandIndex - 1] : _neighborBands[bandIndex];
                var center = _neighborBands[bandIndex];
                var right = bandIndex + 1 < BarCount ? _neighborBands[bandIndex + 1] : _neighborBands[bandIndex];
                var shaped = (left * 0.04f) + (center * 0.92f) + (right * 0.04f);
                _outputBands[bandIndex] = Math.Clamp(shaped, 0f, 1f);
            }

            return (float[])_outputBands.Clone();
        }

        private static float[] CreateHannWindow()
        {
            var window = new float[FftLength];
            for (int i = 0; i < FftLength; i++)
                window[i] = 0.5f - (0.5f * MathF.Cos(2f * MathF.PI * i / (FftLength - 1)));

            return window;
        }

        private static float LogLerp(float start, float end, float amount)
        {
            start = MathF.Max(start, 1f);
            end = MathF.Max(end, start + 1f);
            return start * MathF.Pow(end / start, amount);
        }

        private static void Transform(float[] real, float[] imaginary)
        {
            var n = real.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;

                j ^= bit;
                if (i >= j)
                    continue;

                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }

            for (int length = 2; length <= n; length <<= 1)
            {
                var angle = -2f * MathF.PI / length;
                var wLengthReal = MathF.Cos(angle);
                var wLengthImaginary = MathF.Sin(angle);

                for (int i = 0; i < n; i += length)
                {
                    var wReal = 1f;
                    var wImaginary = 0f;
                    var halfLength = length >> 1;

                    for (int j = 0; j < halfLength; j++)
                    {
                        var evenIndex = i + j;
                        var oddIndex = evenIndex + halfLength;
                        var oddReal = (real[oddIndex] * wReal) - (imaginary[oddIndex] * wImaginary);
                        var oddImaginary = (real[oddIndex] * wImaginary) + (imaginary[oddIndex] * wReal);

                        real[oddIndex] = real[evenIndex] - oddReal;
                        imaginary[oddIndex] = imaginary[evenIndex] - oddImaginary;
                        real[evenIndex] += oddReal;
                        imaginary[evenIndex] += oddImaginary;

                        var nextWReal = (wReal * wLengthReal) - (wImaginary * wLengthImaginary);
                        wImaginary = (wReal * wLengthImaginary) + (wImaginary * wLengthReal);
                        wReal = nextWReal;
                    }
                }
            }
        }
    }
}
