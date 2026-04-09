using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Wavee.AudioHost.Audio.Abstractions;
using Wavee.AudioHost.Audio.Decoders;
using Wavee.AudioHost.Audio.Streaming;
using Wavee.Playback.Contracts;

namespace Wavee.AudioHost;

internal sealed class PreviewAnalysisService : IAsyncDisposable
{
    private const int BucketCount = 12;
    private const double MinFrameIntervalMs = 50;

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
        long lastFrameTimestamp = 0;
        var smoothed = new float[BucketCount];

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

                    var now = Environment.TickCount64;
                    if (lastFrameTimestamp != 0 && now - lastFrameTimestamp < MinFrameIntervalMs)
                        continue;

                    if (!TryCreateFrame(buffer, format, smoothed, out var amplitudes))
                        continue;

                    lastFrameTimestamp = now;
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
        if (frameCount <= 0)
            return false;

        Span<float> peak = stackalloc float[BucketCount];
        Span<float> sumSquares = stackalloc float[BucketCount];
        Span<int> counts = stackalloc int[BucketCount];

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var offset = frameIndex * bytesPerFrame;
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
            var magnitude = MathF.Abs(mixed);
            var bucket = Math.Min(BucketCount - 1, (frameIndex * BucketCount) / frameCount);

            if (magnitude > peak[bucket])
                peak[bucket] = magnitude;

            sumSquares[bucket] += magnitude * magnitude;
            counts[bucket]++;
        }

        amplitudes = new float[BucketCount];
        for (int i = 0; i < BucketCount; i++)
        {
            var rms = counts[i] > 0 ? MathF.Sqrt(sumSquares[i] / counts[i]) : 0f;
            var envelope = (peak[i] * 0.62f) + (rms * 0.38f);
            var normalized = Math.Clamp(MathF.Pow(envelope * 2.75f, 0.78f), 0f, 1f);
            smoothed[i] += (normalized - smoothed[i]) * 0.42f;
            amplitudes[i] = smoothed[i];
        }

        return true;
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
