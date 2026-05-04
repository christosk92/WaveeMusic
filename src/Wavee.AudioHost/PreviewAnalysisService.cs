using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wavee.AudioHost.Audio.Abstractions;
using Wavee.AudioHost.Audio.Decoders;
using Wavee.AudioHost.Audio.Streaming;
using Wavee.Playback.Contracts;

namespace Wavee.AudioHost;

internal sealed class PreviewAnalysisService : IAsyncDisposable
{
    private const int BucketCount = 28;
    private const int FftLength = 2048;
    private const double MinFrameIntervalMs = 50;
    private const float MinFrequencyHz = 45f;
    private const float MaxFrequencyHz = 16000f;

    private static readonly float[] HannWindow = CreateHannWindow();

    private readonly BassDecoder _decoder;
    private readonly Func<PreviewVisualizationFrame, CancellationToken, Task> _sendFrameAsync;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private string? _sessionId;

    public PreviewAnalysisService(
        BassDecoder decoder,
        Func<PreviewVisualizationFrame, CancellationToken, Task> sendFrameAsync,
        ILogger logger)
    {
        _decoder = decoder;
        _sendFrameAsync = sendFrameAsync;
        _logger = logger;
    }

    public async Task StartAsync(StartPreviewAnalysisCommand command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command.SessionId) || string.IsNullOrWhiteSpace(command.PreviewUrl))
            return;

        Task? previousTask;
        CancellationTokenSource? previousCts;
        string? previousSessionId;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            previousTask = _sessionTask;
            previousCts = _sessionCts;
            previousSessionId = _sessionId;
            _sessionTask = null;
            _sessionCts = null;
            _sessionId = null;
        }
        finally
        {
            _gate.Release();
        }

        await StopPriorSessionAsync(previousTask, previousCts, previousSessionId).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _sessionCts = linkedCts;
            _sessionId = command.SessionId;
            _sessionTask = Task.Run(
                () => RunSessionAsync(command.SessionId, command.PreviewUrl, linkedCts.Token),
                CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(string sessionId, CancellationToken ct)
    {
        Task? task = null;
        CancellationTokenSource? cts = null;
        string? activeSessionId = null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
                return;

            task = _sessionTask;
            cts = _sessionCts;
            activeSessionId = _sessionId;
            _sessionTask = null;
            _sessionCts = null;
            _sessionId = null;
        }
        finally
        {
            _gate.Release();
        }

        await StopPriorSessionAsync(task, cts, activeSessionId).ConfigureAwait(false);
    }

    private async Task RunSessionAsync(string sessionId, string previewUrl, CancellationToken ct)
    {
        long sequence = 0;
        long lastFramePositionMs = -1;
        var smoothed = new float[BucketCount];
        var playbackClock = Stopwatch.StartNew();

        try
        {
            using var formatStream = new UrlAwareStream(Stream.Null, previewUrl);
            var format = await _decoder.GetFormatAsync(formatStream, ct).ConfigureAwait(false);
            using var decodeStream = new UrlAwareStream(Stream.Null, previewUrl);

            await foreach (var buffer in _decoder.DecodeAsync(decodeStream, cancellationToken: ct).ConfigureAwait(false))
            {
                try
                {
                    if (ct.IsCancellationRequested)
                        break;

                    if (lastFramePositionMs >= 0 &&
                        buffer.PositionMs - lastFramePositionMs < MinFrameIntervalMs)
                        continue;

                    if (!TryCreateFrame(buffer, format, smoothed, out var amplitudes))
                        continue;

                    var delayMs = buffer.PositionMs - playbackClock.ElapsedMilliseconds;
                    if (delayMs > 1)
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);

                    lastFramePositionMs = buffer.PositionMs;
                    sequence++;

                    await _sendFrameAsync(new PreviewVisualizationFrame
                    {
                        SessionId = sessionId,
                        Sequence = sequence,
                        Amplitudes = amplitudes,
                        Completed = false
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    buffer.Return();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview analysis failed for {SessionId}", sessionId);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    await _sendFrameAsync(new PreviewVisualizationFrame
                    {
                        SessionId = sessionId,
                        Sequence = sequence + 1,
                        Amplitudes = [],
                        Completed = true
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to send preview completion frame for {SessionId}", sessionId);
                }
            }
        }
    }

    private async Task StopPriorSessionAsync(Task? task, CancellationTokenSource? cts, string? sessionId)
    {
        if (cts == null)
            return;

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        if (task != null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Preview analysis session shutdown failed for {SessionId}", sessionId);
            }
        }

        cts.Dispose();

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            try
            {
                await _sendFrameAsync(new PreviewVisualizationFrame
                {
                    SessionId = sessionId,
                    Sequence = 0,
                    Amplitudes = [],
                    Completed = true
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send preview stop frame for {SessionId}", sessionId);
            }
        }
    }

    private static bool TryCreateFrame(
        AudioBuffer buffer,
        AudioFormat format,
        float[] smoothed,
        out float[] amplitudes)
    {
        amplitudes = [];

        var pcm = buffer.Data.Span;
        var bytesPerFrame = Math.Max(format.BytesPerFrame, sizeof(short));
        var frameCount = pcm.Length / bytesPerFrame;
        if (frameCount < 16)
            return false;

        var real = new float[FftLength];
        var imaginary = new float[FftLength];
        var sourceFrameOffset = Math.Max(0, frameCount - FftLength);
        var framesToCopy = Math.Min(frameCount, FftLength);
        var fftOffset = FftLength - framesToCopy;

        for (int frameIndex = 0; frameIndex < framesToCopy; frameIndex++)
        {
            var sourceFrameIndex = sourceFrameOffset + frameIndex;
            var offset = sourceFrameIndex * bytesPerFrame;
            float mixed = 0;

            for (int channel = 0; channel < format.Channels; channel++)
            {
                var sampleOffset = offset + (channel * sizeof(short));
                if (sampleOffset + sizeof(short) > pcm.Length)
                    break;

                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(sampleOffset, sizeof(short)));
                mixed += sample / 32768f;
            }

            mixed /= Math.Max(format.Channels, 1);
            real[fftOffset + frameIndex] = mixed * HannWindow[fftOffset + frameIndex];
        }

        Transform(real, imaginary);

        amplitudes = new float[BucketCount];
        var rawBands = new float[BucketCount];
        var sampleRate = Math.Max(1, format.SampleRate);
        var nyquist = Math.Max(1f, sampleRate / 2f);
        var maxFrequency = Math.Min(MaxFrequencyHz, nyquist * 0.92f);
        var minFrequency = Math.Min(MinFrequencyHz, maxFrequency * 0.5f);
        var maxBand = 0f;

        for (int i = 0; i < BucketCount; i++)
        {
            var bandStart = i / (float)BucketCount;
            var bandEnd = (i + 1) / (float)BucketCount;
            var lowFrequency = LogLerp(minFrequency, maxFrequency, bandStart);
            var highFrequency = LogLerp(minFrequency, maxFrequency, bandEnd);
            var startBin = Math.Clamp((int)MathF.Floor(lowFrequency * FftLength / sampleRate), 1, (FftLength / 2) - 1);
            var endBin = Math.Clamp((int)MathF.Ceiling(highFrequency * FftLength / sampleRate), startBin + 1, FftLength / 2);

            var sum = 0f;
            var peak = 0f;
            for (int bin = startBin; bin < endBin; bin++)
            {
                var magnitude = MathF.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin])) / FftLength;
                var frequency = bin * sampleRate / (float)FftLength;
                var compensated = magnitude * MathF.Pow(MathF.Max(frequency, 1f) / 250f, 0.18f);
                sum += compensated * compensated;
                peak = MathF.Max(peak, compensated);
            }

            var binCount = Math.Max(1, endBin - startBin);
            var rms = MathF.Sqrt(sum / binCount);
            var raw = (peak * 0.72f) + (rms * 0.28f);
            rawBands[i] = raw;
            maxBand = MathF.Max(maxBand, raw);
        }

        var gain = maxBand > 0.000001f
            ? MathF.Min(18f, 0.9f / maxBand)
            : 1f;

        for (int i = 0; i < BucketCount; i++)
        {
            var normalized = Math.Clamp(MathF.Pow(rawBands[i] * gain, 0.58f), 0f, 1f);
            var smoothing = normalized > smoothed[i] ? 0.56f : 0.22f;
            smoothed[i] += (normalized - smoothed[i]) * smoothing;
            amplitudes[i] = Math.Clamp(smoothed[i], 0f, 1f);
        }

        return true;
    }

    private static float[] CreateHannWindow()
    {
        var window = new float[FftLength];
        for (int i = 0; i < window.Length; i++)
            window[i] = 0.5f - (0.5f * MathF.Cos(2f * MathF.PI * i / (window.Length - 1)));

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

    public async ValueTask DisposeAsync()
    {
        Task? task;
        CancellationTokenSource? cts;
        string? sessionId;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            task = _sessionTask;
            cts = _sessionCts;
            sessionId = _sessionId;
            _sessionTask = null;
            _sessionCts = null;
            _sessionId = null;
        }
        finally
        {
            _gate.Release();
        }

        await StopPriorSessionAsync(task, cts, sessionId).ConfigureAwait(false);
        _gate.Dispose();
    }
}
