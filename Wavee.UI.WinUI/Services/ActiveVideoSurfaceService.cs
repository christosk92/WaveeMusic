using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Media.Playback;

namespace Wavee.UI.WinUI.Services;

/// <inheritdoc cref="IActiveVideoSurfaceService"/>
public sealed class ActiveVideoSurfaceService : IActiveVideoSurfaceService
{
    private readonly List<IVideoSurfaceProvider> _providers = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly DispatcherQueue? _dispatcher;
    private readonly ILogger<ActiveVideoSurfaceService>? _logger;
    private IMediaSurfaceConsumer? _currentOwner;
    private MediaPlayer? _activeSurface;
    private FrameworkElement? _activeElementSurface;
    private bool _activeSurfaceIsLoading;
    private bool _activeSurfaceHasFirstFrame;
    private bool _activeSurfaceIsBuffering;

    public ActiveVideoSurfaceService(ILogger<ActiveVideoSurfaceService>? logger = null)
    {
        // Capture the UI dispatcher at ctor time — providers fire their
        // SurfaceChanges from background threads (media decoder threads,
        // network completion, etc.) but consumers must be touched on UI.
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _logger = logger;
    }

    public IVideoSurfaceProvider? ActiveProvider { get; private set; }
    public MediaPlayer? ActiveSurface => _activeSurface;
    public FrameworkElement? ActiveElementSurface => _activeElementSurface;
    public bool HasActiveSurface => _activeSurface is not null || _activeElementSurface is not null;
    public bool IsActiveSurfaceLoading => _activeSurfaceIsLoading;
    public bool HasActiveFirstFrame => _activeSurfaceHasFirstFrame;
    public bool IsActiveSurfaceBuffering => _activeSurfaceIsBuffering;
    public IMediaSurfaceConsumer? CurrentOwner => _currentOwner;
    public string? ActiveKind => ActiveProvider?.Kind;

    public event EventHandler<MediaPlayer?>? ActiveSurfaceChanged;
    public event EventHandler? SurfaceOwnershipChanged;

    public void RegisterProvider(IVideoSurfaceProvider provider)
    {
        if (_providers.Contains(provider)) return;
        _providers.Add(provider);
        // Subscribe to its surface changes — when it goes active/inactive,
        // re-evaluate which provider should be ActiveProvider.
        var sub = provider.SurfaceChanges.Subscribe(_ => OnProviderSurfaceChanged(provider));
        _subscriptions.Add(sub);

        // If the provider is already active at registration time, recompute now.
        if (provider.IsActive) OnProviderSurfaceChanged(provider);
    }

    public void AcquireSurface(IMediaSurfaceConsumer consumer)
    {
        // Detach the previous owner first so the MediaPlayer never has two
        // bound MediaPlayerElements at the same instant — that's what makes
        // handoff glitch-free.
        var ownerChanged = !ReferenceEquals(_currentOwner, consumer);
        if (ownerChanged && _currentOwner is not null)
        {
            try { _currentOwner.DetachSurface(); }
            catch (Exception ex) { _logger?.LogDebug(ex, "DetachSurface threw on previous owner"); }
        }
        _currentOwner = consumer;
        var surface = ActiveSurface;
        if (surface is not null)
        {
            try { consumer.AttachSurface(surface); }
            catch (Exception ex) { _logger?.LogDebug(ex, "AttachSurface threw"); }
            if (ownerChanged)
                SurfaceOwnershipChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var elementSurface = ActiveElementSurface;
        if (elementSurface is not null)
        {
            try { consumer.AttachElementSurface(elementSurface); }
            catch (Exception ex) { _logger?.LogDebug(ex, "AttachElementSurface threw"); }
        }

        if (ownerChanged)
            SurfaceOwnershipChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReleaseSurface(IMediaSurfaceConsumer consumer)
    {
        if (!ReferenceEquals(_currentOwner, consumer)) return;
        try { consumer.DetachSurface(); }
        catch (Exception ex) { _logger?.LogDebug(ex, "DetachSurface threw on release"); }
        _currentOwner = null;
        SurfaceOwnershipChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsOwnedBy(IMediaSurfaceConsumer consumer)
        => ReferenceEquals(_currentOwner, consumer);

    private void OnProviderSurfaceChanged(IVideoSurfaceProvider provider)
    {
        // Marshal to UI thread — events come from media-decoder/network
        // threads, but ActiveProvider is read by XAML bindings and the
        // current-owner attach/detach calls touch UIElements.
        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnProviderSurfaceChanged(provider));
            return;
        }

        // Pick the first provider that's IsActive — there's no contention
        // expected today (one engine plays at a time), and a future
        // multi-source case can refine the priority here.
        IVideoSurfaceProvider? newActive = null;
        foreach (var p in _providers)
        {
            if (p.IsActive) { newActive = p; break; }
        }

        var oldSurface = _activeSurface;
        var oldElementSurface = _activeElementSurface;
        var oldIsLoading = _activeSurfaceIsLoading;
        var oldHasFirstFrame = _activeSurfaceHasFirstFrame;
        var oldIsBuffering = _activeSurfaceIsBuffering;
        ActiveProvider = newActive;
        var newSurface = newActive?.Surface;
        var newElementSurface = newActive?.ElementSurface;
        _activeSurface = newSurface;
        _activeElementSurface = newElementSurface;
        _activeSurfaceIsLoading = newActive?.IsSurfaceLoading == true;
        _activeSurfaceHasFirstFrame = newActive?.HasFirstFrame == true;
        _activeSurfaceIsBuffering = newActive?.IsSurfaceBuffering == true;
        var surfaceChanged = !ReferenceEquals(oldSurface, newSurface)
            || !ReferenceEquals(oldElementSurface, newElementSurface);
        var readinessChanged = oldIsLoading != _activeSurfaceIsLoading
            || oldHasFirstFrame != _activeSurfaceHasFirstFrame
            || oldIsBuffering != _activeSurfaceIsBuffering;

        if (!surfaceChanged && !readinessChanged) return;

        // Push the new surface to the current owner (if any). Detach first
        // when the surface is going away, attach when it appears.
        if (surfaceChanged && _currentOwner is not null)
        {
            if (oldSurface is not null || oldElementSurface is not null)
            {
                try { _currentOwner.DetachSurface(); }
                catch (Exception ex) { _logger?.LogDebug(ex, "DetachSurface threw on surface flip"); }
            }
            if (newSurface is not null)
            {
                try { _currentOwner.AttachSurface(newSurface); }
                catch (Exception ex) { _logger?.LogDebug(ex, "AttachSurface threw on surface flip"); }
            }
            else if (newElementSurface is not null)
            {
                try { _currentOwner.AttachElementSurface(newElementSurface); }
                catch (Exception ex) { _logger?.LogDebug(ex, "AttachElementSurface threw on surface flip"); }
            }
        }

        ActiveSurfaceChanged?.Invoke(this, newSurface);
    }
}
