using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace Wavee.Controls.HeroCarousel;

/// <summary>
/// Tracks active direct-manipulation contacts that WinUI's wheel events do not expose.
/// </summary>
public sealed class DirectManipulationContactTracker : IDisposable
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_POINTERUPDATE = 0x0245;
    private const uint WM_POINTERDOWN = 0x0246;
    private const uint WM_POINTERUP = 0x0247;
    private const uint WM_POINTERLEAVE = 0x024A;
    private const uint WM_POINTERCAPTURECHANGED = 0x024C;
    private const int MaxIgnoredPointerDiagnostics = 40;

    private readonly HashSet<uint> _touchPointerIds = [];
    private readonly HashSet<uint> _touchpadPointerIds = [];
    private readonly Dictionary<IntPtr, IntPtr> _previousWndProcs = [];
    private readonly HashSet<IntPtr> _touchpadHwnds = [];
    private WndProcCallback? _wndProc;
    private IntPtr _rootHwnd;
    private int _ignoredPointerDiagnosticsCount;
    private bool _touchpadThreadRegistered;

    public static DirectManipulationContactTracker Shared { get; } = new();

    public bool HasActiveContact => HasActiveTouchContact || HasActiveTouchpadContact;

    public bool HasActiveTouchContact => _touchPointerIds.Count > 0;

    public bool HasActiveTouchpadContact => _touchpadPointerIds.Count > 0;

    public bool IsTouchpadTrackingAvailable { get; private set; }

    public string Diagnostics =>
        $"active={HasActiveContact} touch={_touchPointerIds.Count} touchpad={_touchpadPointerIds.Count} " +
        $"touchpadApi={IsTouchpadTrackingAvailable} threadApi={_touchpadThreadRegistered} " +
        $"hwnds={_previousWndProcs.Count} root=0x{_rootHwnd.ToInt64():X}";

    public string CurrentInputMessageSourceDiagnostics
    {
        get
        {
            if (!GetCurrentInputMessageSource(out InputMessageSource source))
            {
                return $"inputSource=unavailable error={Marshal.GetLastWin32Error()}";
            }

            return $"inputSource={source.DeviceType}/{source.OriginId}";
        }
    }

    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        Attach(WindowNative.GetWindowHandle(window));
    }

    public void Attach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _rootHwnd)
        {
            return;
        }

        Detach();

        _rootHwnd = hwnd;
        _touchpadThreadRegistered = TryRegisterTouchpadCapableThread(true);
        Log($"attach-thread registerTouchpad={_touchpadThreadRegistered} lastError={Marshal.GetLastWin32Error()}");

        _wndProc = WndProc;
        AttachHwnd(hwnd, "root");
        RefreshWindowTree();

        Log($"attach-summary {Diagnostics}");
    }

    public void RefreshWindowTree()
    {
        if (_rootHwnd == IntPtr.Zero)
        {
            return;
        }

        EnumChildWindows(_rootHwnd, (childHwnd, _) =>
        {
            AttachHwnd(childHwnd, "child");

            return true;
        }, IntPtr.Zero);

        Log($"refresh-window-tree {Diagnostics}");
    }

    public void Detach()
    {
        if (_touchpadThreadRegistered)
        {
            bool unregistered = TryRegisterTouchpadCapableThread(false);
            Log($"detach-thread unregisterTouchpad={unregistered} lastError={Marshal.GetLastWin32Error()}");
        }

        foreach (KeyValuePair<IntPtr, IntPtr> entry in _previousWndProcs)
        {
            SetWindowLongPtr(entry.Key, GWLP_WNDPROC, entry.Value);

            if (_touchpadHwnds.Contains(entry.Key))
            {
                TryRegisterTouchpadCapableWindow(entry.Key, false);
            }
        }

        _rootHwnd = IntPtr.Zero;
        _previousWndProcs.Clear();
        _touchpadHwnds.Clear();
        _wndProc = null;
        _touchpadThreadRegistered = false;
        IsTouchpadTrackingAvailable = false;
        Reset();
        Log("detach");
    }

    public void NotifyPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsDirectManipulationPointer(e))
        {
            bool added = _touchPointerIds.Add(e.Pointer.PointerId);

            if (added)
            {
                Log($"touch-down id={e.Pointer.PointerId} type={e.Pointer.PointerDeviceType} {Diagnostics}");
            }
        }
    }

    public void NotifyPointerReleased(PointerRoutedEventArgs e)
    {
        bool removed = _touchPointerIds.Remove(e.Pointer.PointerId);

        if (removed)
        {
            Log($"touch-up id={e.Pointer.PointerId} type={e.Pointer.PointerDeviceType} {Diagnostics}");
        }
    }

    public void NotifyPointerCanceled(PointerRoutedEventArgs e)
    {
        bool removed = _touchPointerIds.Remove(e.Pointer.PointerId);

        if (removed)
        {
            Log($"touch-cancel id={e.Pointer.PointerId} type={e.Pointer.PointerDeviceType} {Diagnostics}");
        }
    }

    public void Reset()
    {
        _touchPointerIds.Clear();
        _touchpadPointerIds.Clear();
    }

    public void Dispose()
    {
        Detach();
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (IsPointerMessage(message))
        {
            ProcessTouchpadPointerMessage(message, wParam);
        }

        return _previousWndProcs.TryGetValue(hwnd, out IntPtr previousWndProc)
            ? CallWindowProc(previousWndProc, hwnd, message, wParam, lParam)
            : DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ProcessTouchpadPointerMessage(uint message, UIntPtr wParam)
    {
        uint pointerId = (uint)(wParam.ToUInt64() & 0xFFFF);
        bool isEndingMessage = message is WM_POINTERUP or WM_POINTERLEAVE or WM_POINTERCAPTURECHANGED;

        if (!GetPointerInfo(pointerId, out PointerInfo info))
        {
            int error = Marshal.GetLastWin32Error();

            if (isEndingMessage)
            {
                bool removed = _touchpadPointerIds.Remove(pointerId);

                if (removed)
                {
                    Log($"touchpad-remove-after-getinfo-failed message={FormatPointerMessage(message)} id={pointerId} error={error} {Diagnostics}");
                }
            }
            else if (_ignoredPointerDiagnosticsCount < MaxIgnoredPointerDiagnostics)
            {
                _ignoredPointerDiagnosticsCount++;
                Log($"pointer-getinfo-failed message={FormatPointerMessage(message)} id={pointerId} error={error} {Diagnostics}");
            }

            return;
        }

        if (info.PointerType != PointerInputType.Touchpad)
        {
            if (_ignoredPointerDiagnosticsCount < MaxIgnoredPointerDiagnostics)
            {
                _ignoredPointerDiagnosticsCount++;
                Log($"pointer-ignored message={FormatPointerMessage(message)} id={info.PointerId} " +
                    $"type={info.PointerType} flags={info.PointerFlags} {Diagnostics}");
            }

            return;
        }

        bool isContactActive =
            !isEndingMessage &&
            (info.PointerFlags & PointerFlags.InContact) != 0 &&
            (info.PointerFlags & (PointerFlags.Up | PointerFlags.Canceled)) == 0;

        if (isContactActive)
        {
            bool added = _touchpadPointerIds.Add(info.PointerId);

            if (added)
            {
                Log($"touchpad-active message={FormatPointerMessage(message)} id={info.PointerId} flags={info.PointerFlags} {Diagnostics}");
            }
        }
        else
        {
            bool removed = _touchpadPointerIds.Remove(info.PointerId);

            if (removed || isEndingMessage)
            {
                Log($"touchpad-inactive message={FormatPointerMessage(message)} id={info.PointerId} flags={info.PointerFlags} removed={removed} {Diagnostics}");
            }
        }
    }

    private static bool IsPointerMessage(uint message)
    {
        return message is WM_POINTERDOWN or WM_POINTERUPDATE or WM_POINTERUP or WM_POINTERLEAVE or WM_POINTERCAPTURECHANGED;
    }

    private static bool IsDirectManipulationPointer(PointerRoutedEventArgs e)
    {
        PointerDeviceType deviceType = e.Pointer.PointerDeviceType;

        return deviceType is PointerDeviceType.Touch or PointerDeviceType.Pen;
    }

    private static bool TryRegisterTouchpadCapableWindow(IntPtr hwnd, bool enabled)
    {
        try
        {
            return RegisterTouchpadCapableWindow(hwnd, enabled);
        }
        catch (EntryPointNotFoundException)
        {
            Log($"register-touchpad-entrypoint-missing enabled={enabled} hwnd=0x{hwnd.ToInt64():X}");
            return false;
        }
    }

    private static bool TryRegisterTouchpadCapableThread(bool enabled)
    {
        try
        {
            return RegisterTouchpadCapableThread(enabled);
        }
        catch (EntryPointNotFoundException)
        {
            Log($"register-touchpad-thread-entrypoint-missing enabled={enabled}");
            return false;
        }
    }

    private static string FormatPointerMessage(uint message)
    {
        return message switch
        {
            WM_POINTERUPDATE => "WM_POINTERUPDATE",
            WM_POINTERDOWN => "WM_POINTERDOWN",
            WM_POINTERUP => "WM_POINTERUP",
            WM_POINTERLEAVE => "WM_POINTERLEAVE",
            WM_POINTERCAPTURECHANGED => "WM_POINTERCAPTURECHANGED",
            _ => $"0x{message:X}"
        };
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"HC_CONTACT {message}");
    }

    private void AttachHwnd(IntPtr hwnd, string role)
    {
        if (hwnd == IntPtr.Zero || _previousWndProcs.ContainsKey(hwnd) || _wndProc is null)
        {
            return;
        }

        bool touchpadRegistered = TryRegisterTouchpadCapableWindow(hwnd, true);
        int registerError = Marshal.GetLastWin32Error();

        IntPtr previousWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProc));
        int subclassError = Marshal.GetLastWin32Error();

        if (previousWndProc == IntPtr.Zero)
        {
            if (touchpadRegistered)
            {
                TryRegisterTouchpadCapableWindow(hwnd, false);
            }

            Log($"attach-hwnd-failed role={role} hwnd=0x{hwnd.ToInt64():X} registerTouchpad={touchpadRegistered} " +
                $"registerLastError={registerError} subclassLastError={subclassError}");
            return;
        }

        _previousWndProcs.Add(hwnd, previousWndProc);

        if (touchpadRegistered)
        {
            _touchpadHwnds.Add(hwnd);
            IsTouchpadTrackingAvailable = true;
        }

        Log($"attach-hwnd role={role} hwnd=0x{hwnd.ToInt64():X} registerTouchpad={touchpadRegistered} " +
            $"registerLastError={registerError} previousWndProc=0x{previousWndProc.ToInt64():X} subclassLastError={subclassError}");
    }

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, index, value)
            : SetWindowLongPtr32(hwnd, index, value);
    }

    [DllImport("user32.dll", EntryPoint = "#2689", SetLastError = true)]
    private static extern bool RegisterTouchpadCapableWindow(IntPtr hwnd, bool touchpadCapable);

    [DllImport("user32.dll", EntryPoint = "#2688", SetLastError = true)]
    private static extern bool RegisterTouchpadCapableThread(bool touchpadCapable);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCurrentInputMessageSource(out InputMessageSource inputMessageSource);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetPointerInfo(uint pointerId, out PointerInfo pointerInfo);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr previousWndProc, IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc callback, IntPtr lParam);

    private delegate IntPtr WndProcCallback(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct InputMessageSource
    {
        public InputMessageDeviceType DeviceType;
        public InputMessageOriginId OriginId;
    }

    [Flags]
    private enum InputMessageDeviceType : uint
    {
        Unavailable = 0x00000000,
        Keyboard = 0x00000001,
        Mouse = 0x00000002,
        Touch = 0x00000004,
        Pen = 0x00000008,
        Touchpad = 0x00000010
    }

    [Flags]
    private enum InputMessageOriginId : uint
    {
        Unavailable = 0x00000000,
        Hardware = 0x00000001,
        Injected = 0x00000002,
        System = 0x00000004
    }

    private enum PointerInputType : uint
    {
        Pointer = 1,
        Touch = 2,
        Pen = 3,
        Mouse = 4,
        Touchpad = 5
    }

    [Flags]
    private enum PointerFlags : uint
    {
        InContact = 0x00000004,
        Canceled = 0x00008000,
        Up = 0x00040000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public PointerInputType PointerType;
        public uint PointerId;
        public uint FrameId;
        public PointerFlags PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public Point PtPixelLocation;
        public Point PtHimetricLocation;
        public Point PtPixelLocationRaw;
        public Point PtHimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
