using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Models;
using Wavee.UI.WinUI.Floating;
using Windows.Graphics;

namespace Wavee.UI.WinUI.Services.Docking;

/// <summary>
/// Concrete <see cref="IPanelDockingService"/>. Detaches the sidebar player and
/// the right panel into <see cref="PlayerFloatingWindow"/> /
/// <see cref="RightPanelFloatingWindow"/>; mirrors all state into
/// <see cref="ShellLayoutState"/> via <see cref="IShellSessionService.UpdateLayout"/>
/// so geometry survives restarts.
/// </summary>
internal sealed partial class PanelDockingService : ObservableObject, IPanelDockingService
{
    private readonly IShellSessionService _shellSession;
    private readonly DispatcherQueue? _uiDispatcher;
    private readonly ILogger<PanelDockingService>? _logger;

    private PlayerFloatingWindow? _playerWindow;
    private RightPanelFloatingWindow? _rightPanelWindow;

    [ObservableProperty]
    private bool _isPlayerDetached;

    [ObservableProperty]
    private bool _isRightPanelDetached;

    public PanelDockingService(
        IShellSessionService shellSession,
        ILogger<PanelDockingService>? logger = null)
    {
        _shellSession = shellSession;
        _uiDispatcher = DispatcherQueue.GetForCurrentThread();
        _logger = logger;
    }

    public AppWindow? GetWindowFor(DetachablePanel panel) => panel switch
    {
        DetachablePanel.Player => _playerWindow?.AppWindow,
        DetachablePanel.RightPanel => _rightPanelWindow?.AppWindow,
        _ => null
    };

    public void Detach(DetachablePanel panel, PointInt32? spawnAt = null)
    {
        switch (panel)
        {
            case DetachablePanel.Player:
                if (_playerWindow != null) { Activate(_playerWindow); return; }
                _playerWindow = CreatePlayerWindow(spawnAt);
                IsPlayerDetached = true;
                PersistDetached(panel, true);
                Activate(_playerWindow);
                break;

            case DetachablePanel.RightPanel:
                if (_rightPanelWindow != null) { Activate(_rightPanelWindow); return; }
                _rightPanelWindow = CreateRightPanelWindow(spawnAt);
                IsRightPanelDetached = true;
                PersistDetached(panel, true);
                Activate(_rightPanelWindow);
                break;
        }
    }

    public void Dock(DetachablePanel panel)
    {
        switch (panel)
        {
            case DetachablePanel.Player:
                if (_playerWindow == null) return;
                var pw = _playerWindow;
                _playerWindow = null;
                IsPlayerDetached = false;
                PersistDetached(panel, false);
                pw.RequestClose();
                break;

            case DetachablePanel.RightPanel:
                if (_rightPanelWindow == null) return;
                var rw = _rightPanelWindow;
                _rightPanelWindow = null;
                IsRightPanelDetached = false;
                PersistDetached(panel, false);
                rw.RequestClose();
                break;
        }
    }

    public void HandleFloatingClose(DetachablePanel panel) => Dock(panel);

    public void NotifyFloatingGeometryChanged(DetachablePanel panel)
    {
        var window = panel switch
        {
            DetachablePanel.Player => (Microsoft.UI.Xaml.Window?)_playerWindow,
            DetachablePanel.RightPanel => _rightPanelWindow,
            _ => null
        };
        if (window?.AppWindow is not { } appWindow) return;

        var pos = appWindow.Position;
        var size = appWindow.Size;
        _shellSession.UpdateLayout(s =>
        {
            switch (panel)
            {
                case DetachablePanel.Player:
                    s.PlayerWindowX = pos.X;
                    s.PlayerWindowY = pos.Y;
                    s.PlayerWindowWidth = size.Width;
                    s.PlayerWindowHeight = size.Height;
                    break;
                case DetachablePanel.RightPanel:
                    s.RightPanelWindowX = pos.X;
                    s.RightPanelWindowY = pos.Y;
                    s.RightPanelWindowWidth = size.Width;
                    s.RightPanelWindowHeight = size.Height;
                    break;
            }
        });
    }

    public Task RehydrateAsync()
    {
        // Defer to next dispatcher pass so the main window has finished its first
        // paint before we start spawning floats — avoids a brief Z-order pop.
        var tcs = new TaskCompletionSource();
        var dispatch = _uiDispatcher ?? DispatcherQueue.GetForCurrentThread();
        dispatch.TryEnqueue(() =>
        {
            try
            {
                var layout = _shellSession.GetLayoutSnapshot();
                if (layout.PlayerWindowDetached)
                {
                    // Player windows always reopen in expanded focus mode.
                    _shellSession.UpdateLayout(s => s.PlayerWindowExpanded = true);
                    var size = new SizeInt32(
                        (int)layout.PlayerWindowExpandedWidth,
                        (int)layout.PlayerWindowExpandedHeight);
                    var spawn = ClampToVisibleMonitor(
                        new PointInt32(
                            (int)layout.PlayerWindowExpandedX,
                            (int)layout.PlayerWindowExpandedY),
                        size);
                    _playerWindow = CreatePlayerWindow(spawn, size);
                    IsPlayerDetached = true;
                    Activate(_playerWindow);
                }
                if (layout.RightPanelWindowDetached)
                {
                    var spawn = ClampToVisibleMonitor(
                        new PointInt32((int)layout.RightPanelWindowX, (int)layout.RightPanelWindowY),
                        new SizeInt32((int)layout.RightPanelWindowWidth, (int)layout.RightPanelWindowHeight));
                    _rightPanelWindow = CreateRightPanelWindow(spawn, new SizeInt32(
                        (int)layout.RightPanelWindowWidth, (int)layout.RightPanelWindowHeight));
                    IsRightPanelDetached = true;
                    Activate(_rightPanelWindow);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Panel docking rehydrate failed");
            }
            finally
            {
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    // ── Internals ──────────────────────────────────────────────────────────

    private void PersistDetached(DetachablePanel panel, bool detached)
    {
        _shellSession.UpdateLayout(s =>
        {
            switch (panel)
            {
                case DetachablePanel.Player: s.PlayerWindowDetached = detached; break;
                case DetachablePanel.RightPanel: s.RightPanelWindowDetached = detached; break;
            }
        });
    }

    private PlayerFloatingWindow CreatePlayerWindow(PointInt32? spawnAt, SizeInt32? size = null)
    {
        _shellSession.UpdateLayout(s => s.PlayerWindowExpanded = true);
        var layout = _shellSession.GetLayoutSnapshot();
        var preferredSize = size ?? new SizeInt32(
            (int)layout.PlayerWindowExpandedWidth,
            (int)layout.PlayerWindowExpandedHeight);
        var window = new PlayerFloatingWindow();
        ApplyInitialPlacement(window, spawnAt, preferredSize);
        return window;
    }

    private RightPanelFloatingWindow CreateRightPanelWindow(PointInt32? spawnAt, SizeInt32? size = null)
    {
        var layout = _shellSession.GetLayoutSnapshot();
        var preferredSize = size ?? new SizeInt32(
            (int)layout.RightPanelWindowWidth,
            (int)layout.RightPanelWindowHeight);
        var window = new RightPanelFloatingWindow();
        ApplyInitialPlacement(window, spawnAt, preferredSize);
        return window;
    }

    private static void ApplyInitialPlacement(
        Microsoft.UI.Xaml.Window window,
        PointInt32? spawnAt,
        SizeInt32 size)
    {
        var appWindow = window.AppWindow;
        if (appWindow == null) return;

        appWindow.Resize(size);

        if (spawnAt is { } pos)
        {
            appWindow.Move(ClampToVisibleMonitor(pos, size));
        }
        else
        {
            // Center on the main window's monitor.
            try
            {
                var main = MainWindow.Instance.AppWindow;
                if (main != null)
                {
                    var mainPos = main.Position;
                    var mainSize = main.Size;
                    appWindow.Move(new PointInt32(
                        mainPos.X + (mainSize.Width - size.Width) / 2,
                        mainPos.Y + (mainSize.Height - size.Height) / 2));
                }
            }
            catch
            {
                // Ignore — leave the OS-default position.
            }
        }
    }

    /// <summary>
    /// Returns the input point if it lies on a visible monitor; otherwise the
    /// top-left of the nearest visible <see cref="DisplayArea"/> with the requested
    /// window centered inside it. Guards against monitors that disappeared between
    /// app sessions (laptop undocked, projector unplugged, etc.).
    /// </summary>
    private static PointInt32 ClampToVisibleMonitor(PointInt32 pos, SizeInt32 size)
    {
        try
        {
            var area = DisplayArea.GetFromPoint(pos, DisplayAreaFallback.Nearest);
            var work = area.WorkArea;
            // If the *center* of the window would be inside the work area, leave it.
            var centerX = pos.X + size.Width / 2;
            var centerY = pos.Y + size.Height / 2;
            var insideX = centerX >= work.X && centerX <= work.X + work.Width;
            var insideY = centerY >= work.Y && centerY <= work.Y + work.Height;
            if (insideX && insideY) return pos;

            // Otherwise center inside that nearest area.
            var clampedX = work.X + Math.Max(0, (work.Width - size.Width) / 2);
            var clampedY = work.Y + Math.Max(0, (work.Height - size.Height) / 2);
            return new PointInt32(clampedX, clampedY);
        }
        catch
        {
            return pos;
        }
    }

    private static void Activate(Microsoft.UI.Xaml.Window window)
    {
        try { window.Activate(); }
        catch { /* window already activated or in teardown */ }
    }
}
