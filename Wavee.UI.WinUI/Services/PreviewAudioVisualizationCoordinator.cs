using System;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.Controls.Lyrics.Helper;
using Wavee.Playback.Contracts;

namespace Wavee.UI.WinUI.Services;

public sealed class PreviewAudioVisualizationCoordinator : IDisposable
{
    private const int VisualizerBarCount = 24;
    private const int LoopbackBarCount = VisualizerBarCount * 2;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    private SpectrumAnalyzer? _loopbackAnalyzer;
    private DispatcherQueueTimer? _loopbackTimer;
    private ActivePreviewSession? _activeSession;
    private long _loopbackSequence;

    public PreviewAudioVisualizationCoordinator(ILogger<PreviewAudioVisualizationCoordinator>? logger = null)
    {
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public string? Activate(string? previewUrl, Action<PreviewVisualizationFrame> onFrame)
    {
        if (string.IsNullOrWhiteSpace(previewUrl))
            return null;

        lock (_gate)
            _activeSession = null;

        StopLoopbackCapture();

        if (!TryStartLoopbackCapture())
            return null;

        var session = new ActivePreviewSession(Guid.NewGuid().ToString("N"), previewUrl, onFrame);
        lock (_gate)
        {
            _activeSession = session;
            _loopbackSequence = 0;
        }

        return session.SessionId;
    }

    public void Deactivate(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        lock (_gate)
        {
            if (!string.Equals(_activeSession?.SessionId, sessionId, StringComparison.Ordinal))
                return;

            _activeSession = null;
        }

        StopLoopbackCapture();
    }

    private bool TryStartLoopbackCapture()
    {
        try
        {
            _loopbackAnalyzer ??= new SpectrumAnalyzer
            {
                BarCount = LoopbackBarCount,
                Sensitivity = 100,
                SmoothingFactor = 0.82f
            };

            if (!_loopbackAnalyzer.IsCapturing)
                _loopbackAnalyzer.StartCapture();

            if (!_loopbackAnalyzer.IsCapturing)
                return false;

            _loopbackTimer ??= _dispatcherQueue.CreateTimer();
            _loopbackTimer.Interval = TimeSpan.FromMilliseconds(33);
            _loopbackTimer.Tick -= OnLoopbackTimerTick;
            _loopbackTimer.Tick += OnLoopbackTimerTick;
            _loopbackTimer.Start();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to start preview loopback visualization");
            StopLoopbackCapture();
            return false;
        }
    }

    private void StopLoopbackCapture()
    {
        if (_loopbackTimer != null)
        {
            _loopbackTimer.Stop();
            _loopbackTimer.Tick -= OnLoopbackTimerTick;
            _loopbackTimer = null;
        }

        try
        {
            if (_loopbackAnalyzer?.IsCapturing == true)
                _loopbackAnalyzer.StopCapture();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to stop preview loopback visualization");
        }
    }

    private void OnLoopbackTimerTick(DispatcherQueueTimer sender, object args)
    {
        ActivePreviewSession? activeSession;
        lock (_gate)
            activeSession = _activeSession;

        if (activeSession == null || _loopbackAnalyzer == null)
            return;

        _loopbackAnalyzer.UpdateSmoothSpectrum();
        var spectrum = _loopbackAnalyzer.SmoothSpectrum;
        if (spectrum == null || spectrum.Length == 0)
            return;

        var amplitudes = FoldMirroredSpectrum(spectrum, VisualizerBarCount);
        var frame = new PreviewVisualizationFrame
        {
            SessionId = activeSession.SessionId,
            Sequence = ++_loopbackSequence,
            Amplitudes = amplitudes,
            Completed = false
        };

        _dispatcherQueue.TryEnqueue(() => activeSession.OnFrame(frame));
    }

    private static float[] FoldMirroredSpectrum(float[] spectrum, int outputCount)
    {
        var amplitudes = new float[outputCount];
        if (spectrum.Length == 0)
            return amplitudes;

        var center = spectrum.Length / 2;
        if (center <= 0)
        {
            for (int i = 0; i < amplitudes.Length; i++)
                amplitudes[i] = Math.Clamp(spectrum[Math.Min(i, spectrum.Length - 1)], 0f, 1f);

            return amplitudes;
        }

        for (int i = 0; i < amplitudes.Length; i++)
        {
            var amount = amplitudes.Length == 1 ? 0f : i / (float)(amplitudes.Length - 1);
            var sourceOffset = amount * (center - 1);
            var lowIndex = Math.Clamp(center - 1 - (int)MathF.Round(sourceOffset), 0, spectrum.Length - 1);
            var highIndex = Math.Clamp(center + (int)MathF.Round(sourceOffset), 0, spectrum.Length - 1);
            amplitudes[i] = Math.Clamp((spectrum[lowIndex] + spectrum[highIndex]) * 0.5f, 0f, 1f);
        }

        return amplitudes;
    }

    public void Dispose()
    {
        StopLoopbackCapture();

        lock (_gate)
        {
            _loopbackAnalyzer?.Dispose();
            _loopbackAnalyzer = null;
            _activeSession = null;
        }
    }

    private sealed record ActivePreviewSession(
        string SessionId,
        string PreviewUrl,
        Action<PreviewVisualizationFrame> OnFrame);
}
