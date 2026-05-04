using System;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Vanara.PInvoke;
using Wavee.UI.WinUI.Services.Docking;
using Windows.Graphics;

namespace Wavee.UI.WinUI.Behaviors;

/// <summary>
/// Attached behavior — drag the host element outside <see cref="MainWindow"/> to
/// detach the associated panel into its own floating window. Threshold-based:
/// short drags within the main window are ignored, so click handlers on overlapped
/// elements (the sidebar player chevron, etc.) still work.
/// </summary>
/// <remarks>
/// Algorithm:
/// <list type="number">
///   <item>PointerPressed (left button) → record screen anchor, capture pointer.</item>
///   <item>PointerMoved → if the cursor is outside <see cref="MainWindow"/>'s rect
///   by more than <see cref="DetachMarginPx"/>, detach.</item>
///   <item>On detach: spawn the floating window via <see cref="IPanelDockingService"/>,
///   then post <c>WM_NCLBUTTONDOWN(HTCAPTION)</c> to the new window's HWND so Windows
///   continues the drag without the user releasing the mouse — the canonical VS / Chrome
///   tear-off trick.</item>
/// </list>
/// Per-element drag state lives in a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// keyed on the host element so the GC reclaims it with the control.
/// </remarks>
public static class DragToDetachBehavior
{
    private const double DetachMarginPx = 32.0;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private static readonly nint HTCAPTION = (nint)2;

    private static readonly ConditionalWeakTable<UIElement, DragState> _states = new();

    private sealed class DragState
    {
        public bool IsTracking;
        public Pointer? CapturedPointer;
    }

    /// <summary>
    /// Attached property — set in XAML to "Player" or "RightPanel" (the
    /// <see cref="DetachablePanel"/> enum names). Use string here so XAML can
    /// bind it inline (<c>behaviors:DragToDetachBehavior.Panel="Player"</c>)
    /// without an x:Static cast.
    /// </summary>
    public static readonly DependencyProperty PanelProperty = DependencyProperty.RegisterAttached(
        "Panel",
        typeof(string),
        typeof(DragToDetachBehavior),
        new PropertyMetadata(null, OnPanelChanged));

    public static string? GetPanel(DependencyObject obj) => (string?)obj.GetValue(PanelProperty);

    public static void SetPanel(DependencyObject obj, string? value) => obj.SetValue(PanelProperty, value);

    private static void OnPanelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;

        element.PointerPressed -= OnPointerPressed;
        element.PointerMoved -= OnPointerMoved;
        element.PointerReleased -= OnPointerReleased;
        element.PointerCaptureLost -= OnPointerCaptureLost;
        element.PointerCanceled -= OnPointerCaptureLost;

        if (TryParsePanel(e.NewValue, out _))
        {
            element.PointerPressed += OnPointerPressed;
            element.PointerMoved += OnPointerMoved;
            element.PointerReleased += OnPointerReleased;
            element.PointerCaptureLost += OnPointerCaptureLost;
            element.PointerCanceled += OnPointerCaptureLost;
        }
    }

    private static bool TryParsePanel(object? value, out DetachablePanel panel)
    {
        if (value is string s && Enum.TryParse(s, ignoreCase: true, out panel))
            return true;
        panel = default;
        return false;
    }

    private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element) return;
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse &&
            e.Pointer.PointerDeviceType != PointerDeviceType.Pen)
            return;

        var pt = e.GetCurrentPoint(element);
        if (!pt.Properties.IsLeftButtonPressed) return;

        var state = _states.GetValue(element, _ => new DragState());
        state.IsTracking = true;
        state.CapturedPointer = e.Pointer;
        element.CapturePointer(e.Pointer);
    }

    private static void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element) return;
        if (!_states.TryGetValue(element, out var state) || !state.IsTracking) return;
        if (!TryParsePanel(GetPanel(element), out var panel)) return;

        if (!User32.GetCursorPos(out var screenPt)) return;

        if (!IsCursorOutsideMainWindow(screenPt, (int)DetachMarginPx))
            return;

        // Threshold crossed — tear off.
        ResetState(element, state);

        var docking = Ioc.Default.GetService<IPanelDockingService>();
        if (docking == null) return;

        // Offset the spawn so the cursor lands on the new window's title bar
        // (~ centered horizontally on a small drag handle, ~16 px below the top edge).
        var spawnAt = new PointInt32(
            Math.Max(0, screenPt.X - 100),
            Math.Max(0, screenPt.Y - 16));

        docking.Detach(panel, spawnAt);

        // Hand off the live mouse-down to the new window so the drag continues
        // seamlessly — Windows enters its modal move loop on WM_NCLBUTTONDOWN.
        var appWindow = docking.GetWindowFor(panel);
        if (appWindow == null) return;

        try
        {
            var hwnd = Win32Interop.GetWindowFromWindowId(appWindow.Id);
            // Re-fetch cursor position in case it moved between Detach and here.
            User32.GetCursorPos(out var nowPt);
            var lparam = MakeLParam((short)nowPt.X, (short)nowPt.Y);
            User32.PostMessage((HWND)hwnd, WM_NCLBUTTONDOWN, HTCAPTION, lparam);
        }
        catch
        {
            // Fall back to the spawned window's default placement; user can drag from there.
        }
    }

    private static void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element) return;
        if (_states.TryGetValue(element, out var state)) ResetState(element, state);
    }

    private static void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not UIElement element) return;
        if (_states.TryGetValue(element, out var state)) ResetState(element, state);
    }

    private static void ResetState(UIElement element, DragState state)
    {
        state.IsTracking = false;
        if (state.CapturedPointer is { } p)
        {
            try { element.ReleasePointerCapture(p); } catch { /* best-effort */ }
            state.CapturedPointer = null;
        }
    }

    private static bool IsCursorOutsideMainWindow(POINT pt, int marginPx)
    {
        try
        {
            var main = MainWindow.Instance.AppWindow;
            if (main == null) return false;
            var pos = main.Position;
            var size = main.Size;
            return pt.X < pos.X - marginPx
                || pt.X > pos.X + size.Width + marginPx
                || pt.Y < pos.Y - marginPx
                || pt.Y > pos.Y + size.Height + marginPx;
        }
        catch
        {
            return false;
        }
    }

    private static nint MakeLParam(short low, short high) =>
        (nint)(((int)high << 16) | ((int)low & 0xFFFF));
}
