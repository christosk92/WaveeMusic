using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Wavee.AudioIpc;
using Wavee.Playback.Contracts;
using Wavee.UI.WinUI.Helpers.Application;

namespace Wavee.UI.WinUI.Services;

public sealed class PreviewAudioVisualizationCoordinator : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    private AudioProcessManager? _manager;
    private AudioPipelineProxy? _proxy;
    private IDisposable? _frameSubscription;
    private ActivePreviewSession? _activeSession;

    public PreviewAudioVisualizationCoordinator(ILogger<PreviewAudioVisualizationCoordinator>? logger = null)
    {
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public string? Activate(string? previewUrl, Action<PreviewVisualizationFrame> onFrame)
    {
        if (string.IsNullOrWhiteSpace(previewUrl))
            return null;

        ActivePreviewSession? previousSession;
        ActivePreviewSession nextSession;
        AudioPipelineProxy? proxy;

        lock (_gate)
        {
            EnsureAttached_NoLock();

            previousSession = _activeSession;
            nextSession = new ActivePreviewSession(Guid.NewGuid().ToString("N"), previewUrl, onFrame);
            _activeSession = nextSession;
            proxy = _proxy;
        }

        if (previousSession != null && proxy != null)
            _ = StopPreviewAnalysisAsync(proxy, previousSession.SessionId);

        if (proxy != null)
            _ = StartPreviewAnalysisAsync(proxy, nextSession);

        return nextSession.SessionId;
    }

    public void Deactivate(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        AudioPipelineProxy? proxy;

        lock (_gate)
        {
            if (!string.Equals(_activeSession?.SessionId, sessionId, StringComparison.Ordinal))
                return;

            EnsureAttached_NoLock();
            proxy = _proxy;
            _activeSession = null;
        }

        if (proxy != null)
            _ = StopPreviewAnalysisAsync(proxy, sessionId);
    }

    private void EnsureAttached_NoLock()
    {
        var currentManager = AppLifecycleHelper.AudioProcessManager;
        if (!ReferenceEquals(_manager, currentManager))
        {
            if (_manager != null)
                _manager.ProxyRestarted -= OnProxyRestarted;

            _manager = currentManager;

            if (_manager != null)
                _manager.ProxyRestarted += OnProxyRestarted;
        }

        AttachProxy_NoLock(_manager?.Proxy);
    }

    private void AttachProxy_NoLock(AudioPipelineProxy? proxy)
    {
        if (ReferenceEquals(_proxy, proxy))
            return;

        _frameSubscription?.Dispose();
        _frameSubscription = null;
        _proxy = proxy;

        if (_proxy != null)
            _frameSubscription = _proxy.PreviewVisualizationFrames.Subscribe(OnPreviewFrame);
    }

    private void OnProxyRestarted(AudioPipelineProxy proxy)
    {
        ActivePreviewSession? activeSession;

        lock (_gate)
        {
            AttachProxy_NoLock(proxy);
            activeSession = _activeSession;
        }

        if (activeSession != null)
            _ = StartPreviewAnalysisAsync(proxy, activeSession);
    }

    private void OnPreviewFrame(PreviewVisualizationFrame frame)
    {
        Action<PreviewVisualizationFrame>? handler = null;

        lock (_gate)
        {
            if (string.Equals(_activeSession?.SessionId, frame.SessionId, StringComparison.Ordinal))
                handler = _activeSession.OnFrame;
        }

        if (handler == null)
            return;

        _dispatcherQueue.TryEnqueue(() => handler(frame));
    }

    private async Task StartPreviewAnalysisAsync(AudioPipelineProxy proxy, ActivePreviewSession session)
    {
        try
        {
            await proxy.StartPreviewAnalysisAsync(session.SessionId, session.PreviewUrl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to start preview analysis for {SessionId}", session.SessionId);
        }
    }

    private async Task StopPreviewAnalysisAsync(AudioPipelineProxy proxy, string sessionId)
    {
        try
        {
            await proxy.StopPreviewAnalysisAsync(sessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to stop preview analysis for {SessionId}", sessionId);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_manager != null)
                _manager.ProxyRestarted -= OnProxyRestarted;

            _frameSubscription?.Dispose();
            _frameSubscription = null;
            _proxy = null;
            _manager = null;
            _activeSession = null;
        }
    }

    private sealed record ActivePreviewSession(
        string SessionId,
        string PreviewUrl,
        Action<PreviewVisualizationFrame> OnFrame);
}
