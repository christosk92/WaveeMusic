// scroll-input-probe — raw scroll-input measurement OUTSIDE the engine (docs/plans/wavee/scroll-feel-investigation-2026-09-10.md).
//
// One plain Win32 window that records, on one QPC clock, every stage a two-finger touchpad pan or a wheel notch
// passes through BEFORE it reaches FluentGpu — so an engine-side "sluggish / blocked" report can be split into
// "the OS delivered it late / not at all" versus "the engine did":
//
//   hid       WM_INPUT reports from the precision-touchpad HID collection (usage page 0x0D, usage 0x05): the
//             digitizer's own cadence, contact count and the first contact's X/Y (parsed with HidP_*).
//   rawmouse  WM_INPUT wheel reports from a real mouse (usage page 0x01, usage 0x02).
//   pwheel    WM_POINTERWHEEL / WM_POINTERHWHEEL as the window sees them (pointer type says touchpad vs mouse).
//   mwheel    WM_MOUSEWHEEL / WM_MOUSEHWHEEL that still reach the window.
//   dm.*      A DirectManipulation viewport set up EXACTLY like the engine's producer (window-rect viewport, no
//             SetContentRect, INTERACTION|TX|TY|SCALING — no OS inertia, MANUALUPDATE, one Update per compositor
//             tick): hit-tests, status edges, content deltas.
//   tick      DCompositionWaitForCompositorClock ticks from a waiter thread — the engine's production clock — with
//             the measured period next to the WINDOW's monitor refresh (EnumDisplaySettings), so a mixed-refresh
//             desktop (120 Hz laptop panel + a 50 Hz DisplayLink monitor) is visible as a number.
//   blocked   windows where two fingers are on the pad and NOTHING (no pwheel, no dm.content) was delivered.
//
// The window also paints a stripe pattern scrolled by the accumulated deltas, so the raw OS path can be FELT
// on the same monitor — a control experiment against the app.
//
// CoreCLR + classic [ComImport] interop on purpose (same posture as the engine's ops/tools/dm-probe): a throwaway
// measurement tool, not engine code. Usage:
//   dotnet run --project ops/tools/scroll-input-probe -- [--no-dm] [--dm-auto] [--out <dir>] [--no-mip]
// ESC or closing the window ends the session and writes <dir>\scroll-input-probe-<stamp>.csv + .summary.txt
// (default dir: %LOCALAPPDATA%\Wavee\logs).

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ScrollInputProbe;

internal static class Program
{
    // ── options ──
    private static bool _dm = true, _dmAuto, _mip = true;
    // --engine-pump: pump DM's Update exactly like Win32DirectManipulation does (one per tick only while RUNNING or
    // within 120 ms of a hit-test; otherwise one idle drain every 250 ms). --dm-inertia: configure TRANSLATION_INERTIA
    // (the WinUI/XAML shape) instead of the engine's no-inertia primary config.
    private static bool _enginePump, _dmInertia;
    // --content-rect: give the viewport a 1,000,000 px content rect centred under the viewport (WinUI/XAML give DM real
    // content extents); the engine and the default probe use NO content rect (content == viewport, zero runway).
    private static bool _contentRect;
    private const float ContentRectSize = 1_000_000f;
    private static long _lastUpdateQpc;
    private static string _outDir;
    private static int _autoExitSeconds;   // --seconds N: end the session by itself (automation / smoke test)

    private static readonly long QpcFreq = Stopwatch.Frequency;
    private static readonly long T0 = Stopwatch.GetTimestamp();
    private static StreamWriter _csv, _txt;
    private static readonly object _logGate = new();
    private static IntPtr _hwnd;
    private static int _clientW = 900, _clientH = 700;

    // ── DirectManipulation ──
    private static IDirectManipulationManager _mgr;
    private static IDirectManipulationUpdateManager _upd;
    private static IDirectManipulationViewport _vp;
    private static IDirectManipulationContent _content;
    private static ProbeSink _sink;
    private static uint _cookie;
    private static int _dmStatus = 5;       // READY
    private static float _lastTx, _lastTy;  // content-space baseline (p = -t/s, the engine's convention)
    private static bool _haveBaseline;
    private static long _lastHitQpc, _runningEnteredQpc, _lastContentQpc;
    private static int _vpW = 1000, _vpH = 1000;
    private static double _tickDy;          // dm dy delivered since the previous tick (wherever the callback ran)
    private static bool _tickHadUpdate, _inUpdate;
    private static int _cContentOutsideUpdate;
    // INERTIA-while-fingers-down episodes: DM reports INERTIA although no inertia is configured; the engine treats that
    // edge as the gesture's end (ScrollEnd + a fling seeded from history) and forwards nothing until RUNNING returns.
    private static long _inertiaOpenQpc;
    private static int _inertiaHid, _inertiaTwoFingerHid, _cInertiaEpisodes, _cInertiaEpisodesFingersDown;
    private static double _inertiaYTravel, _inertiaY0 = -1, _inertiaTotalMs, _inertiaFingersDownTotalMs;
    private static readonly Stat InertiaEpisodeMs = new(), InertiaFingersDownMs = new();

    // ── compositor clock ──
    private static Thread _clockThread;
    private static volatile bool _quit;
    private static long _lastTickQpc;
    private const uint WM_APP_TICK = 0x8001;

    // ── stats ──
    private static readonly Stat TickPeriod = new(), HidGap = new(), PWheelPadGap = new(), PWheelMouseGap = new(), MWheelGap = new(),
        DmDeltaAbs = new(), DmRunningTickGap = new(), HitToRunning = new(), RunningToFirstDelta = new(), LiftToReady = new(),
        HidPerRunningTick = new();
    private static int _cHid, _cHidTwoFinger, _cRawMouseWheel, _cRawMouseMove, _cPWheelPad, _cPWheelMouse, _cPWheelOther, _cMWheel,
        _cHit, _cSetContactFail, _cContent, _cTicks, _cTicksRunning, _cTicksRunningZero, _cPtrUpdate, _cBlocked;
    private static int _cPWheelPadDetented, _cPWheelPadHiRes, _cPWheelMouseDetented, _cPWheelMouseHiRes;
    private static double _blockedTotalMs, _worstRunningGapMs;
    private static readonly Dictionary<string, int> _statusEdges = new();
    private static readonly int[] _contactHist = new int[8];
    private static bool _hidParsed;         // HidP parsing worked at least once

    // ── touchpad contact state (from HID) ──
    private static int _lastContacts = -1;
    private static long _lastHidQpc, _lastTwoFingerQpc, _twoFingerSinceQpc, _lastAnyContactQpc, _liftQpc;
    private static int _hidSinceLastTick;
    private static long _lastDeliveryQpc;   // last pwheel-from-touchpad or dm.content with |dy|>0
    private static long _blockedOpenQpc;    // 0 = no blocked window open
    private static int _blockedHid, _blockedPWheel;
    private const double BlockedThresholdMs = 150;

    // ── visible scroller ──
    private static double _offset;          // accumulated DIP-ish delta (dm dy + wheel)
    private static int _dpi = 96;

    // ── last-source gaps ──
    private static long _lastPWheelPadQpc, _lastPWheelMouseQpc, _lastMWheelQpc, _lastRawMouseWheelQpc;

    [STAThread]
    private static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-dm": _dm = false; break;
                case "--dm-auto": _dmAuto = true; break;
                case "--no-mip": _mip = false; break;
                case "--engine-pump": _enginePump = true; break;
                case "--dm-inertia": _dmInertia = true; break;
                case "--content-rect": _contentRect = true; break;
                case "--out" when i + 1 < args.Length: _outDir = args[++i]; break;
                case "--seconds" when i + 1 < args.Length: _autoExitSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: Console.WriteLine("usage: scroll-input-probe [--no-dm] [--dm-auto] [--no-mip] [--engine-pump] [--dm-inertia] [--content-rect] [--out <dir>] [--seconds N]"); return 2;
            }
        }
        _outDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "logs");
        Directory.CreateDirectory(_outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string csvPath = Path.Combine(_outDir, $"scroll-input-probe-{stamp}.csv");
        string txtPath = Path.Combine(_outDir, $"scroll-input-probe-{stamp}.summary.txt");
        _csv = new StreamWriter(csvPath, false, new UTF8Encoding(false)) { AutoFlush = false };
        _txt = new StreamWriter(txtPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        _csv.WriteLine("t_ms,qpc,kind,a,b,c,d,note");

        Native.SetProcessDpiAwarenessContext((IntPtr)(-4));   // per-monitor v2, like the app
        Native.CoInitializeEx(IntPtr.Zero, Native.COINIT_APARTMENTTHREADED);
        Info($"os={Environment.OSVersion} arch={RuntimeInformation.ProcessArchitecture} dm={_dm} dmAuto={_dmAuto} mip={_mip} enginePump={_enginePump} dmInertia={_dmInertia} qpcFreq={QpcFreq}");
        if (_mip)
        {
            bool ok = Native.EnableMouseInPointer(true);
            Info($"EnableMouseInPointer(true) -> {ok}");
        }
        CreateProbeWindow();
        LogMonitor("start");
        LogDwmTiming();
        LogRawInputDevices();
        RegisterRawInput();
        if (_dm)
        {
            int hr = SetUpDManip();
            if (hr < 0) { Info($"DM setup FAILED hr=0x{hr:X8} — continuing without DM"); _dm = false; }
        }
        StartClockThread();

        Console.WriteLine();
        Console.WriteLine("READY. Put the probe window on the monitor you use Wavee on, then:");
        Console.WriteLine("  1) two-finger pan up/down slowly, then fast; flick and lift; hold two fingers still for 2 s then continue");
        Console.WriteLine("  2) a few mouse-wheel notches if a mouse is attached");
        Console.WriteLine("  3) repeat on the other monitor (drag the window there)");
        Console.WriteLine("ESC in the probe window (or close it) ends the session.");
        Console.WriteLine($"csv: {csvPath}");
        RunLoop();

        _quit = true;
        TearDown();
        Summary();
        _csv.Flush(); _csv.Dispose();
        _txt.Dispose();
        Console.WriteLine($"csv:     {csvPath}");
        Console.WriteLine($"summary: {txtPath}");
        return 0;
    }

    // ───────────────────────────── logging ─────────────────────────────

    private static double Ms(long qpc) => (qpc - T0) * 1000.0 / QpcFreq;
    private static double MsBetween(long a, long b) => (b - a) * 1000.0 / QpcFreq;

    private static void Row(long qpc, string kind, double a = 0, double b = 0, double c = 0, double d = 0, string note = null)
    {
        lock (_logGate)
        {
            _csv.Write(Ms(qpc).ToString("F3", CultureInfo.InvariantCulture)); _csv.Write(',');
            _csv.Write(qpc); _csv.Write(',');
            _csv.Write(kind); _csv.Write(',');
            _csv.Write(a.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
            _csv.Write(b.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
            _csv.Write(c.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
            _csv.Write(d.ToString("R", CultureInfo.InvariantCulture)); _csv.Write(',');
            if (note != null) _csv.Write(note.Replace(',', ';'));
            _csv.WriteLine();
        }
    }

    private static void Info(string s)
    {
        long q = Stopwatch.GetTimestamp();
        string line = $"[{Ms(q),10:F1}] {s}";
        Console.WriteLine(line);
        lock (_logGate) { _txt.WriteLine(line); }
        Row(q, "info", note: s);
    }

    // ───────────────────────────── window ─────────────────────────────

    private static Native.WndProc _wndProcKeepAlive;

    private static void CreateProbeWindow()
    {
        _wndProcKeepAlive = WndProc;
        var wc = new Native.WNDCLASSW
        {
            style = 0x0003, // CS_HREDRAW|CS_VREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
            hInstance = Native.GetModuleHandleW(null),
            lpszClassName = "ScrollInputProbeWnd",
            hbrBackground = IntPtr.Zero,
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)32512),
        };
        Native.RegisterClassW(ref wc);
        _hwnd = Native.CreateWindowExW(0, "ScrollInputProbeWnd", $"scroll-input-probe [{(_contentRect ? "CONTENT-RECT" : "no content rect")}{(_enginePump ? " engine-pump" : "")}{(_dmInertia ? " dm-inertia" : "")}] — two-finger pan here (ESC ends)",
            Native.WS_OVERLAPPEDWINDOW | Native.WS_VISIBLE, 200, 200, _clientW + 16, _clientH + 60,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Native.GetClientRect(_hwnd, out var r);
        _clientW = r.right - r.left; _clientH = r.bottom - r.top;
        _dpi = (int)Native.GetDpiForWindow(_hwnd);
        Info($"hwnd=0x{_hwnd:X} client={_clientW}x{_clientH} dpi={_dpi}");
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        long now = Stopwatch.GetTimestamp();
        switch (msg)
        {
            case Native.WM_INPUT:
                OnRawInput(lParam, now);
                return Native.DefWindowProcW(hWnd, msg, wParam, lParam);   // required for WM_INPUT cleanup

            case Native.DM_POINTERHITTEST:
            {
                uint pointerId = (uint)(wParam.ToInt64() & 0xFFFF);
                uint ptype = 0; Native.GetPointerType(pointerId, out ptype);
                _cHit++;
                _lastHitQpc = now;
                int hr = -1, st = -1;
                if (_dm && _vp != null && ptype == Native.PT_TOUCHPAD)
                {
                    hr = _vp.SetContact(pointerId);
                    if (hr < 0) _cSetContactFail++;
                    _vp.GetStatus(out st);
                }
                Row(now, "dm.hit", pointerId, ptype, hr, st, WhereIsTheCursor());
                if (_dm && hr >= 0) return IntPtr.Zero;
                return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
            }

            case Native.WM_POINTERWHEEL:
            case Native.WM_POINTERHWHEEL:
            {
                short delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                uint pid = (uint)(wParam.ToInt64() & 0xFFFF);
                uint ptype = 0; Native.GetPointerType(pid, out ptype);
                bool horizontal = msg == Native.WM_POINTERHWHEEL;
                double gap = 0;
                if (ptype == Native.PT_TOUCHPAD)
                {
                    _cPWheelPad++;
                    if (delta % 120 == 0) _cPWheelPadDetented++; else _cPWheelPadHiRes++;
                    if (_lastPWheelPadQpc != 0) { gap = MsBetween(_lastPWheelPadQpc, now); if (gap < 200) PWheelPadGap.Add(gap); }
                    _lastPWheelPadQpc = now;
                    _lastDeliveryQpc = now;
                    if (_blockedOpenQpc != 0) _blockedPWheel++;
                }
                else if (ptype == Native.PT_MOUSE)
                {
                    _cPWheelMouse++;
                    if (delta % 120 == 0) _cPWheelMouseDetented++; else _cPWheelMouseHiRes++;
                    if (_lastPWheelMouseQpc != 0) { gap = MsBetween(_lastPWheelMouseQpc, now); if (gap < 500) PWheelMouseGap.Add(gap); }
                    _lastPWheelMouseQpc = now;
                }
                else _cPWheelOther++;
                Row(now, "pwheel", delta, ptype, gap, horizontal ? 1 : 0);
                if (!horizontal) { _offset += -delta * 0.11 * (_dpi / 96.0); Native.InvalidateRect(hWnd, IntPtr.Zero, false); }
                return IntPtr.Zero;
            }

            case Native.WM_MOUSEWHEEL:
            case Native.WM_MOUSEHWHEEL:
            {
                short delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                _cMWheel++;
                double gap = 0;
                if (_lastMWheelQpc != 0) { gap = MsBetween(_lastMWheelQpc, now); if (gap < 500) MWheelGap.Add(gap); }
                _lastMWheelQpc = now;
                Row(now, "mwheel", delta, msg == Native.WM_MOUSEHWHEEL ? 1 : 0, gap);
                if (msg == Native.WM_MOUSEWHEEL) { _offset += -delta * 0.11 * (_dpi / 96.0); Native.InvalidateRect(hWnd, IntPtr.Zero, false); }
                return IntPtr.Zero;
            }

            case Native.WM_POINTERUPDATE:
            case Native.WM_POINTERDOWN:
            case Native.WM_POINTERUP:
                _cPtrUpdate++;
                if (_blockedOpenQpc != 0) _blockedPtrMsgs++;
                return Native.DefWindowProcW(hWnd, msg, wParam, lParam);

            case WM_APP_TICK:
                OnTick(now);
                return IntPtr.Zero;

            case Native.WM_PAINT:
                Paint(hWnd);
                return IntPtr.Zero;

            case Native.WM_ERASEBKGND:
                return (IntPtr)1;

            case Native.WM_SIZE:
                Native.GetClientRect(hWnd, out var r);
                _clientW = Math.Max(1, r.right - r.left); _clientH = Math.Max(1, r.bottom - r.top);
                if (_dm && _vp != null && _dmStatus == 5)
                {
                    var rect = new Native.RECT { left = 0, top = 0, right = _clientW, bottom = _clientH };
                    _vp.SetViewportRect(ref rect); _vpW = _clientW; _vpH = _clientH;
                }
                return IntPtr.Zero;

            case Native.WM_DPICHANGED:
                _dpi = (int)(wParam.ToInt64() & 0xFFFF);
                goto case Native.WM_DISPLAYCHANGE;
            case Native.WM_EXITSIZEMOVE:
            case Native.WM_DISPLAYCHANGE:
                LogMonitor(msg == Native.WM_DPICHANGED ? "dpi-changed" : msg == Native.WM_EXITSIZEMOVE ? "moved" : "display-changed");
                return Native.DefWindowProcW(hWnd, msg, wParam, lParam);

            case Native.WM_KEYDOWN when wParam.ToInt64() == 0x1B:
                Native.DestroyWindow(hWnd);
                return IntPtr.Zero;
            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static string _lastMonitorDev = "";
    private static void LogMonitor(string why)
    {
        IntPtr mon = Native.MonitorFromWindow(_hwnd, 2);
        var mi = new Native.MONITORINFOEXW(); mi.cbSize = Marshal.SizeOf<Native.MONITORINFOEXW>();
        Native.GetMonitorInfoW(mon, ref mi);
        var dm = new Native.DEVMODEW(); dm.dmSize = (ushort)Marshal.SizeOf<Native.DEVMODEW>();
        double hz = Native.EnumDisplaySettingsW(mi.szDevice, -1, ref dm) ? dm.dmDisplayFrequency : 0;
        IntPtr pmon = Native.MonitorFromWindow(IntPtr.Zero, 1);
        var pmi = new Native.MONITORINFOEXW(); pmi.cbSize = Marshal.SizeOf<Native.MONITORINFOEXW>();
        Native.GetMonitorInfoW(pmon, ref pmi);
        var pdm = new Native.DEVMODEW(); pdm.dmSize = (ushort)Marshal.SizeOf<Native.DEVMODEW>();
        double phz = Native.EnumDisplaySettingsW(pmi.szDevice, -1, ref pdm) ? pdm.dmDisplayFrequency : 0;
        _dpi = (int)Native.GetDpiForWindow(_hwnd);
        string note = $"{why}: window on {mi.szDevice} {dm.dmPelsWidth}x{dm.dmPelsHeight}@{hz}Hz dpi={_dpi}; primary {pmi.szDevice} @{phz}Hz{(mon == pmon ? " (same)" : " (DIFFERENT monitor)")}";
        Row(Stopwatch.GetTimestamp(), "monitor", hz, phz, _dpi, mon == pmon ? 1 : 0, note);
        Info(note);
        _lastMonitorDev = mi.szDevice;
        _windowHz = hz; _primaryHz = phz;
    }
    private static double _windowHz, _primaryHz;

    private static void LogDwmTiming()
    {
        var ti = new Native.DWM_TIMING_INFO(); ti.cbSize = (uint)Marshal.SizeOf<Native.DWM_TIMING_INFO>();
        int hr = Native.DwmGetCompositionTimingInfo(IntPtr.Zero, ref ti);
        if (hr >= 0)
            Info($"DwmGetCompositionTimingInfo: rateRefresh={ti.rateRefresh.uiNumerator}/{ti.rateRefresh.uiDenominator} qpcRefreshPeriod={ti.qpcRefreshPeriod * 1000.0 / QpcFreq:F3}ms rateCompose={ti.rateCompose.uiNumerator}/{ti.rateCompose.uiDenominator}");
        else Info($"DwmGetCompositionTimingInfo FAILED hr=0x{hr:X8}");
    }

    // ───────────────────────────── raw input ─────────────────────────────

    private static void LogRawInputDevices()
    {
        uint n = 0; uint sz = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICELIST>();
        Native.GetRawInputDeviceList(IntPtr.Zero, ref n, sz);
        if (n == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)(n * sz));
        try
        {
            uint got = Native.GetRawInputDeviceList(buf, ref n, sz);
            for (int i = 0; i < got; i++)
            {
                var d = Marshal.PtrToStructure<Native.RAWINPUTDEVICELIST>(buf + i * (int)sz);
                var info = new Native.RID_DEVICE_INFO(); info.cbSize = (uint)Marshal.SizeOf<Native.RID_DEVICE_INFO>();
                uint isz = info.cbSize;
                Native.GetRawInputDeviceInfoW(d.hDevice, Native.RIDI_DEVICEINFO, ref info, ref isz);
                string desc = d.dwType switch
                {
                    0 => $"MOUSE buttons={info.mouse.dwNumberOfButtons} sampleRate={info.mouse.dwSampleRate}",
                    1 => "KEYBOARD",
                    _ => $"HID vid=0x{info.hid.dwVendorId:X4} pid=0x{info.hid.dwProductId:X4} usagePage=0x{info.hid.usUsagePage:X2} usage=0x{info.hid.usUsage:X2}"
                };
                if (d.dwType == 1) continue;
                if (d.dwType == 2 && !(info.hid.usUsagePage == 0x0D || info.hid.usUsagePage == 0x01)) continue;
                Info($"rawinput device 0x{d.hDevice:X}: {desc}");
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void RegisterRawInput()
    {
        var rid = new Native.RAWINPUTDEVICE[]
        {
            new() { usUsagePage = 0x0D, usUsage = 0x05, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd },   // precision touchpad
            new() { usUsagePage = 0x0D, usUsage = 0x04, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd },   // touch screen (cadence only)
            new() { usUsagePage = 0x01, usUsage = 0x02, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd },   // mouse
        };
        bool ok = Native.RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
        Info($"RegisterRawInputDevices(touchpad 0D/05, touch 0D/04, mouse 01/02, INPUTSINK) -> {ok} gle={Marshal.GetLastWin32Error()}");
    }

    private static readonly Dictionary<IntPtr, HidDevice> _hidDevices = new();
    private static byte[] _rawBuf = new byte[4096];

    private sealed class HidDevice
    {
        public IntPtr Preparsed;
        public ushort UsagePage, Usage;
        public int FirstContactLink = -1;   // link collection of the first finger
        public bool Broken;
    }

    private static unsafe void OnRawInput(IntPtr hRawInput, long now)
    {
        uint size = 0;
        uint headerSize = (uint)sizeof(Native.RAWINPUTHEADER);
        Native.GetRawInputData(hRawInput, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        if (_rawBuf.Length < size) _rawBuf = new byte[size * 2];
        fixed (byte* p = _rawBuf)
        {
            if (Native.GetRawInputData(hRawInput, Native.RID_INPUT, (IntPtr)p, ref size, headerSize) == unchecked((uint)-1)) return;
            var header = (Native.RAWINPUTHEADER*)p;
            byte* body = p + headerSize;
            if (header->dwType == 0)   // RIM_TYPEMOUSE
            {
                var m = (Native.RAWMOUSE*)body;
                ushort buttonFlags = (ushort)(m->ulButtons & 0xFFFF);
                short wheel = unchecked((short)((m->ulButtons >> 16) & 0xFFFF));
                if ((buttonFlags & (Native.RI_MOUSE_WHEEL | Native.RI_MOUSE_HWHEEL)) != 0)
                {
                    _cRawMouseWheel++;
                    double gap = _lastRawMouseWheelQpc != 0 ? MsBetween(_lastRawMouseWheelQpc, now) : 0;
                    _lastRawMouseWheelQpc = now;
                    Row(now, "rawmouse", wheel, buttonFlags, gap, (buttonFlags & Native.RI_MOUSE_HWHEEL) != 0 ? 1 : 0, $"dev=0x{header->hDevice:X}");
                }
                else if (m->lLastX != 0 || m->lLastY != 0) _cRawMouseMove++;
                return;
            }
            if (header->dwType != 2) return;   // RIM_TYPEHID
            var hid = (Native.RAWHID*)body;
            int reportLen = (int)hid->dwSizeHid;
            int count = (int)hid->dwCount;
            byte* report = body + 8;
            var dev = GetHidDevice(header->hDevice);
            for (int i = 0; i < count; i++, report += reportLen)
            {
                int contacts = -1, x = -1, y = -1, tip = -1;
                if (dev != null && !dev.Broken && dev.UsagePage == 0x0D && dev.Usage == 0x05)
                    ParseTouchpadReport(dev, report, reportLen, out contacts, out x, out y, out tip);
                RecordHid(now, reportLen, contacts, x, y, tip, dev?.Usage ?? 0);
            }
        }
    }

    private static unsafe HidDevice GetHidDevice(IntPtr hDevice)
    {
        if (_hidDevices.TryGetValue(hDevice, out var d)) return d;
        d = new HidDevice();
        var info = new Native.RID_DEVICE_INFO(); info.cbSize = (uint)Marshal.SizeOf<Native.RID_DEVICE_INFO>();
        uint isz = info.cbSize;
        if (Native.GetRawInputDeviceInfoW(hDevice, Native.RIDI_DEVICEINFO, ref info, ref isz) != unchecked((uint)-1))
        { d.UsagePage = info.hid.usUsagePage; d.Usage = info.hid.usUsage; }
        uint psz = 0;
        Native.GetRawInputDeviceInfoW(hDevice, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref psz);
        if (psz > 0)
        {
            d.Preparsed = Marshal.AllocHGlobal((int)psz);
            if (Native.GetRawInputDeviceInfoW(hDevice, Native.RIDI_PREPARSEDDATA, d.Preparsed, ref psz) == unchecked((uint)-1))
            { Marshal.FreeHGlobal(d.Preparsed); d.Preparsed = IntPtr.Zero; d.Broken = true; }
        }
        else d.Broken = true;
        Info($"hid device 0x{hDevice:X} usagePage=0x{d.UsagePage:X2} usage=0x{d.Usage:X2} preparsed={(d.Preparsed != IntPtr.Zero ? psz.ToString() : "none")}");
        _hidDevices[hDevice] = d;
        return d;
    }

    private static unsafe void ParseTouchpadReport(HidDevice dev, byte* report, int len, out int contacts, out int x, out int y, out int tip)
    {
        contacts = -1; x = -1; y = -1; tip = -1;
        try
        {
            uint v;
            // Contact Count (Digitizer 0x54) lives in the top-level collection.
            if (Native.HidP_GetUsageValue(0, 0x0D, 0, 0x54, out v, dev.Preparsed, (IntPtr)report, (uint)len) == Native.HIDP_STATUS_SUCCESS)
                contacts = (int)v;
            // The first finger's X/Y: find the link collection once (contact collections are usually 1..N).
            if (dev.FirstContactLink < 0)
            {
                for (ushort link = 1; link <= 16; link++)
                {
                    if (Native.HidP_GetUsageValue(0, 0x01, link, 0x30, out v, dev.Preparsed, (IntPtr)report, (uint)len) == Native.HIDP_STATUS_SUCCESS)
                    { dev.FirstContactLink = link; break; }
                }
                if (dev.FirstContactLink < 0) dev.FirstContactLink = 0;
            }
            if (dev.FirstContactLink > 0)
            {
                ushort link = (ushort)dev.FirstContactLink;
                if (Native.HidP_GetUsageValue(0, 0x01, link, 0x30, out v, dev.Preparsed, (IntPtr)report, (uint)len) == Native.HIDP_STATUS_SUCCESS) x = (int)v;
                if (Native.HidP_GetUsageValue(0, 0x01, link, 0x31, out v, dev.Preparsed, (IntPtr)report, (uint)len) == Native.HIDP_STATUS_SUCCESS) y = (int)v;
                ushort* usages = stackalloc ushort[16];
                uint ulen = 16;
                if (Native.HidP_GetUsages(0, 0x0D, link, (IntPtr)usages, ref ulen, dev.Preparsed, (IntPtr)report, (uint)len) == Native.HIDP_STATUS_SUCCESS)
                {
                    tip = 0;
                    for (int i = 0; i < ulen; i++) if (usages[i] == 0x42) tip = 1;
                }
            }
            if (contacts >= 0) _hidParsed = true;
        }
        catch (Exception ex)
        {
            dev.Broken = true;
            Info($"HidP parse failed, contact parsing disabled: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static void RecordHid(long now, int reportLen, int contacts, int x, int y, int tip, ushort usage)
    {
        _cHid++;
        _hidSinceLastTick++;
        double gap = 0;
        if (_lastHidQpc != 0) { gap = MsBetween(_lastHidQpc, now); if (gap < 100) HidGap.Add(gap); }
        _lastHidQpc = now;
        if (contacts >= 0)
        {
            _contactHist[Math.Min(contacts, 7)]++;
            bool down = contacts >= 1 && tip != 0;   // a tip=0 report with contacts>=1 is the lift report itself
            int effective = down ? contacts : 0;
            if (effective >= 1) _lastAnyContactQpc = now;
            if (effective >= 2)
            {
                _cHidTwoFinger++;
                if (_lastContacts < 2) _twoFingerSinceQpc = now;
                _lastTwoFingerQpc = now;
                if (_blockedOpenQpc != 0) _blockedHid++;
                if (_inertiaOpenQpc != 0) _inertiaTwoFingerHid++;
            }
            else if (_lastContacts >= 1) _liftQpc = now;
            if (_inertiaOpenQpc != 0)
            {
                _inertiaHid++;
                if (y >= 0) { if (_inertiaY0 < 0) _inertiaY0 = y; _inertiaYTravel = Math.Max(_inertiaYTravel, Math.Abs(y - _inertiaY0)); }
            }
            _lastContacts = effective;
        }
        Row(now, "hid", gap, reportLen, contacts, x, $"y={y};tip={tip};usage=0x{usage:X2}");
    }

    // ───────────────────────────── DirectManipulation ─────────────────────────────

    private static void CenterContent()
    {
        float c = ContentRectSize / 2f;
        _vp.ZoomToRect(c - _vpW / 2f, c - _vpH / 2f, c + _vpW / 2f, c + _vpH / 2f, false);
    }

    private static int SetUpDManip()
    {
        try
        {
            var t = Type.GetTypeFromCLSID(new Guid("54E211B6-3650-4F75-8334-FA359598E1C5"));
            _mgr = (IDirectManipulationManager)Activator.CreateInstance(t);
        }
        catch (Exception ex) { Info($"CoCreate DirectManipulationManager FAILED: {ex.Message}"); return -1; }
        Guid iidUpd = typeof(IDirectManipulationUpdateManager).GUID;
        int hr = _mgr.GetUpdateManager(ref iidUpd, out _upd); if (hr < 0) return hr;
        Guid iidVp = typeof(IDirectManipulationViewport).GUID;
        hr = _mgr.CreateViewport(IntPtr.Zero, _hwnd, ref iidVp, out _vp); if (hr < 0) return hr;
        Native.GetClientRect(_hwnd, out var rect);
        _vpW = rect.right - rect.left; _vpH = rect.bottom - rect.top;
        hr = _vp.SetViewportRect(ref rect); if (hr < 0) return hr;
        int cfg = 0x1 | 0x2 | 0x4 | 0x20;   // INTERACTION|TRANSLATION_X|TRANSLATION_Y|SCALING — the engine's primary config, no inertia
        if (_dmInertia) cfg |= 0x10;        // + TRANSLATION_INERTIA (the XAML/Chromium shape)
        hr = _vp.AddConfiguration(cfg); if (hr < 0) return hr;
        hr = _vp.ActivateConfiguration(cfg); if (hr < 0) return hr;
        if (!_dmAuto) { hr = _vp.SetViewportOptions(0x2); if (hr < 0) return hr; }   // MANUALUPDATE like the engine
        _sink = new ProbeSink();
        hr = _vp.AddEventHandler(_hwnd, _sink, out _cookie); if (hr < 0) return hr;
        Guid iidContent = typeof(IDirectManipulationContent).GUID;
        hr = _vp.GetPrimaryContent(ref iidContent, out _content); if (hr < 0) return hr;
        if (_contentRect)
        {
            var cr = new Native.RECT { left = 0, top = 0, right = (int)ContentRectSize, bottom = (int)ContentRectSize };
            hr = _content.SetContentRect(ref cr); if (hr < 0) { Info($"SetContentRect FAILED hr=0x{hr:X8}"); return hr; }
        }
        hr = _vp.Enable(); if (hr < 0) return hr;
        hr = _mgr.Activate(_hwnd); if (hr < 0) return hr;
        if (_contentRect) CenterContent();
        Info($"DM ready: viewport={_vpW}x{_vpH} cfg=0x{cfg:X} manualUpdate={!_dmAuto} inertiaConfigured={_dmInertia} enginePump={_enginePump} contentRect={(_contentRect ? ContentRectSize.ToString("F0") + "px centred" : "none (== viewport)")}");
        return 0;
    }

    internal sealed class ProbeSink : IDirectManipulationViewportEventHandler
    {
        private readonly float[] _m = new float[6];

        public int OnViewportStatusChanged(IDirectManipulationViewport viewport, int current, int previous)
        {
            long now = Stopwatch.GetTimestamp();
            string edge = $"{StatusName(previous)}->{StatusName(current)}";
            _statusEdges.TryGetValue(edge, out int n); _statusEdges[edge] = n + 1;
            double sinceHit = _lastHitQpc != 0 ? MsBetween(_lastHitQpc, now) : -1;
            if (current == 3)
            {
                _runningEnteredQpc = now;
                _lastContentQpc = 0;
                if ((previous == 5 || previous == 1) && _lastHitQpc != 0 && sinceHit < 2000) HitToRunning.Add(sinceHit);
                _haveBaseline = false;
                if (_inertiaOpenQpc != 0) CloseInertiaEpisode(now, "RUNNING");
            }
            if (current == 4 && previous == 3)
            {
                _inertiaOpenQpc = now; _inertiaHid = 0; _inertiaTwoFingerHid = 0; _inertiaYTravel = 0; _inertiaY0 = -1;
            }
            if (current == 5 && _inertiaOpenQpc != 0) CloseInertiaEpisode(now, "READY");
            if (previous == 3 && current == 5)
            {
                if (_liftQpc != 0 && now > _liftQpc && MsBetween(_liftQpc, now) < 2000) LiftToReady.Add(MsBetween(_liftQpc, now));
            }
            if (current == 5) { _haveBaseline = false; ResetViewport(); }
            _dmStatus = current;
            Row(now, "dm.status", current, previous, sinceHit, 0, edge);
            return 0;
        }

        public int OnViewportUpdated(IDirectManipulationViewport viewport) => 0;

        public int OnContentUpdated(IDirectManipulationViewport viewport, IDirectManipulationContent content)
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (content.GetContentTransform(_m, 6) < 0) return 0;
                float scale = _m[0], tx = _m[4], ty = _m[5];
                float invS = scale > 0.001f ? 1f / scale : 1f;
                float px = -tx * invS, py = -ty * invS;
                bool owns = _dmStatus == 3 || (_dmInertia && _dmStatus == 4);
                if (!owns || !_haveBaseline) { _lastTx = px; _lastTy = py; _haveBaseline = true; return 0; }
                float dx = px - _lastTx, dy = py - _lastTy;
                _lastTx = px; _lastTy = py;
                if (MathF.Abs(scale - 1f) > 0.01f) return 0;
                if (MathF.Abs(dx) < 0.01f && MathF.Abs(dy) < 0.01f) return 0;
                _cContent++;
                if (_lastContentQpc == 0 && _runningEnteredQpc != 0) RunningToFirstDelta.Add(MsBetween(_runningEnteredQpc, now));
                _lastContentQpc = now;
                _lastDeliveryQpc = now;
                DmDeltaAbs.Add(MathF.Abs(dy));
                _tickDy += dy;
                if (!_inUpdate) _cContentOutsideUpdate++;
                _offset += dy / (_dpi / 96.0);
                Row(now, "dm.content", dx, dy, scale, _dmStatus, _inUpdate ? "inUpdate" : "viaMessageLoop");
                Native.InvalidateRect(_hwnd, IntPtr.Zero, false);
            }
            catch (Exception ex) { Info($"sink EX: {ex.GetType().Name} {ex.Message}"); }
            return 0;
        }
    }

    private static void ResetViewport()
    {
        if (_vp == null || _content == null) return;
        var m = new float[6];
        if (_content.GetContentTransform(m, 6) >= 0 && MathF.Abs(m[0] - 1f) <= 1e-4f && MathF.Abs(m[4]) <= 0.5f && MathF.Abs(m[5]) <= 0.5f) return;
        if (_contentRect) CenterContent(); else _vp.ZoomToRect(0f, 0f, _vpW, _vpH, false);
    }

    internal static string StatusName(int s) => s switch
    {
        0 => "BUILDING", 1 => "ENABLED", 2 => "DISABLED", 3 => "RUNNING", 4 => "INERTIA", 5 => "READY", 6 => "SUSPENDED", _ => $"?{s}"
    };

    // ───────────────────────────── compositor clock + tick ─────────────────────────────

    private static void StartClockThread()
    {
        _clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "compositor-clock" };
        _clockThread.Start();
    }

    private static void ClockLoop()
    {
        bool available = true;
        int fast = 0;
        while (!_quit)
        {
            long t0 = Stopwatch.GetTimestamp();
            uint r;
            try { r = Native.DCompositionWaitForCompositorClock(0, IntPtr.Zero, 100); }
            catch (Exception ex) { Info($"DCompositionWaitForCompositorClock unavailable: {ex.Message}"); available = false; break; }
            if (r == 0x102) continue;                 // WAIT_TIMEOUT
            if (r == 0xFFFFFFFF) { Info("DCompositionWaitForCompositorClock WAIT_FAILED"); Thread.Sleep(50); continue; }
            long now = Stopwatch.GetTimestamp();
            if (now - t0 < QpcFreq / 1000) { if (++fast > 16) { Info("compositor clock returns without waiting (remote session?) — falling back to 8ms sleep"); available = false; break; } }
            else fast = 0;
            Native.PostMessageW(_hwnd, WM_APP_TICK, (IntPtr)1, (IntPtr)(now - T0));
        }
        while (!_quit && !available)
        {
            Thread.Sleep(8);
            Native.PostMessageW(_hwnd, WM_APP_TICK, IntPtr.Zero, (IntPtr)(Stopwatch.GetTimestamp() - T0));
        }
    }

    private static long _lastStatusPrintQpc;

    private static void OnTick(long now)
    {
        _cTicks++;
        double period = _lastTickQpc != 0 ? MsBetween(_lastTickQpc, now) : 0;
        if (period > 0 && period < 100) TickPeriod.Add(period);
        _lastTickQpc = now;

        // _tickDy accumulates from wherever DM's content callback runs (inside Update or via the message loop) and is
        // reset AFTER this tick's row, so the row reads "dy delivered since the previous tick".
        _tickHadUpdate = false;
        bool live = _dmStatus == 3;
        if (_dm && !_dmAuto && _upd != null)
        {
            bool pump = true;
            if (_enginePump)
            {
                // Win32DirectManipulation: UpdateFrame only while Live (RUNNING, or a SetContact pending for < 120 ms);
                // otherwise Win32Platform.PumpInto issues UpdateIdle at most every 250 ms.
                bool liveLikeEngine = _dmStatus == 3 || (_lastHitQpc != 0 && MsBetween(_lastHitQpc, now) < 120 && _dmStatus != 5);
                pump = liveLikeEngine || _lastUpdateQpc == 0 || MsBetween(_lastUpdateQpc, now) >= 250;
            }
            if (pump)
            {
                _inUpdate = true;
                _upd.Update(IntPtr.Zero);   // ONE Update per compositor tick, exactly like Win32DirectManipulation.UpdateFrame
                _inUpdate = false;
                _tickHadUpdate = true;
                _lastUpdateQpc = now;
            }
        }
        if (live)
        {
            _cTicksRunning++;
            HidPerRunningTick.Add(_hidSinceLastTick);
            if (Math.Abs(_tickDy) < 0.01) _cTicksRunningZero++;
            if (_lastContentQpc != 0)
            {
                double gapMs = MsBetween(_lastContentQpc, now);
                if (Math.Abs(_tickDy) >= 0.01) { DmRunningTickGap.Add(gapMs); }
                if (gapMs > _worstRunningGapMs) _worstRunningGapMs = gapMs;
            }
        }
        _hidSinceLastTick = 0;
        Row(now, "tick", period, _tickHadUpdate ? 1 : 0, _tickDy, _dmStatus);
        _tickDy = 0;

        // Blocked-window detection: two fingers on the pad (HID says so) for > threshold, and nothing delivered.
        if (_hidParsed)
        {
            bool twoFingersNow = _lastTwoFingerQpc != 0 && MsBetween(_lastTwoFingerQpc, now) < 60;
            if (twoFingersNow)
            {
                double held = MsBetween(_twoFingerSinceQpc, now);
                double sinceDelivery = _lastDeliveryQpc != 0 ? MsBetween(_lastDeliveryQpc, now) : held;
                if (held > BlockedThresholdMs && sinceDelivery > BlockedThresholdMs && _blockedOpenQpc == 0)
                {
                    _blockedOpenQpc = now - (long)(BlockedThresholdMs * QpcFreq / 1000);
                    _blockedHid = 0; _blockedPWheel = 0; _blockedPtrMsgs = 0;
                    Info($"BLOCKED window opened (two fingers down, no delivery for {sinceDelivery:F0} ms; dm={StatusName(_dmStatus)}; {WhereIsTheCursor()})");
                }
                else if (_blockedOpenQpc != 0 && sinceDelivery < 30) CloseBlocked(now, "delivery resumed");
            }
            else if (_blockedOpenQpc != 0) CloseBlocked(now, "fingers lifted");
        }

        if (_autoExitSeconds > 0 && Ms(now) > _autoExitSeconds * 1000.0) { Native.DestroyWindow(_hwnd); return; }
        if (_lastStatusPrintQpc == 0 || MsBetween(_lastStatusPrintQpc, now) > 1000)
        {
            _lastStatusPrintQpc = now;
            Console.Write($"\r ticks={_cTicks} ({(TickPeriod.Count > 0 ? 1000.0 / TickPeriod.Mean : 0):F1}Hz) hid={_cHid} 2f={_cHidTwoFinger} pwheel pad/mouse={_cPWheelPad}/{_cPWheelMouse} mwheel={_cMWheel} dm hit={_cHit} content={_cContent} status={StatusName(_dmStatus)} blocked={_cBlocked}   ");
            lock (_logGate) { _csv.Flush(); }
        }
    }

    private static void CloseInertiaEpisode(long now, string endedBy)
    {
        double dur = MsBetween(_inertiaOpenQpc, now);
        bool fingersDown = _inertiaTwoFingerHid >= 3;   // >= 3 two-finger reports (~25 ms) inside the episode
        _cInertiaEpisodes++; _inertiaTotalMs += dur; InertiaEpisodeMs.Add(dur);
        if (fingersDown) { _cInertiaEpisodesFingersDown++; _inertiaFingersDownTotalMs += dur; InertiaFingersDownMs.Add(dur); }
        Row(now, "dm.inertia", dur, _inertiaHid, _inertiaTwoFingerHid, _inertiaYTravel, $"endedBy={endedBy};fingersDown={fingersDown}");
        _inertiaOpenQpc = 0;
    }

    /// <summary>Cursor position, whether the window under it is ours, and whether we are the foreground window —
    /// a PTP pan is routed to the window under the cursor, so a "blocked" stroke with the cursor elsewhere is not
    /// an input-stack failure at all.</summary>
    private static string WhereIsTheCursor()
    {
        Native.GetCursorPos(out var pt);
        IntPtr under = Native.WindowFromPoint(pt);
        IntPtr root = Native.GetAncestor(under, 2 /* GA_ROOT */);
        bool overUs = under == _hwnd || root == _hwnd;
        bool fg = Native.GetForegroundWindow() == _hwnd;
        return $"cursor=({pt.x},{pt.y}) overProbe={overUs} foreground={fg}";
    }

    private static int _blockedPtrMsgs;

    private static void CloseBlocked(long now, string why)
    {
        double dur = MsBetween(_blockedOpenQpc, now);
        _cBlocked++; _blockedTotalMs += dur;
        Row(now, "blocked", dur, _blockedHid, _blockedPWheel, _dmStatus, $"{why};ptrMsgs={_blockedPtrMsgs}");
        Info($"BLOCKED window closed after {dur:F0} ms ({why}; hid={_blockedHid} pwheel={_blockedPWheel} ptrMsgs={_blockedPtrMsgs} dm={StatusName(_dmStatus)})");
        _blockedOpenQpc = 0;
    }

    // ───────────────────────────── paint ─────────────────────────────

    private static void Paint(IntPtr hWnd)
    {
        var ps = new Native.PAINTSTRUCT();
        IntPtr hdc = Native.BeginPaint(hWnd, ref ps);
        try
        {
            IntPtr mem = Native.CreateCompatibleDC(hdc);
            IntPtr bmp = Native.CreateCompatibleBitmap(hdc, _clientW, _clientH);
            IntPtr old = Native.SelectObject(mem, bmp);
            var full = new Native.RECT { left = 0, top = 0, right = _clientW, bottom = _clientH };
            IntPtr bg = Native.CreateSolidBrush(0x00202020);
            Native.FillRect(mem, ref full, bg);
            Native.DeleteObject(bg);
            int pitch = 48;
            int off = (int)Math.Floor(((_offset % pitch) + pitch) % pitch);
            IntPtr stripe = Native.CreateSolidBrush(0x00E0C080);
            IntPtr stripe2 = Native.CreateSolidBrush(0x0080C0E0);
            for (int y = -pitch - off, i = 0; y < _clientH + pitch; y += pitch, i++)
            {
                var r = new Native.RECT { left = 0, top = y, right = _clientW, bottom = y + pitch / 3 };
                Native.FillRect(mem, ref r, (i & 1) == 0 ? stripe : stripe2);
            }
            Native.DeleteObject(stripe); Native.DeleteObject(stripe2);
            string text = $"offset={_offset:F1}  dm={StatusName(_dmStatus)}  tick={(TickPeriod.Count > 0 ? 1000.0 / TickPeriod.Mean : 0):F1}Hz  window={_windowHz}Hz primary={_primaryHz}Hz  hid={_cHid} pwheel={_cPWheelPad + _cPWheelMouse} dm.content={_cContent} blocked={_cBlocked}";
            Native.SetBkMode(mem, 1);
            Native.SetTextColor(mem, 0x00FFFFFF);
            Native.TextOutW(mem, 12, 12, text, text.Length);
            Native.BitBlt(hdc, 0, 0, _clientW, _clientH, mem, 0, 0, 0x00CC0020);
            Native.SelectObject(mem, old);
            Native.DeleteObject(bmp);
            Native.DeleteDC(mem);
        }
        finally { Native.EndPaint(hWnd, ref ps); }
    }

    // ───────────────────────────── loop / teardown / summary ─────────────────────────────

    private static void RunLoop()
    {
        while (true)
        {
            while (Native.PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1))
            {
                if (m.message == Native.WM_QUIT) return;
                if (_dm && _mgr != null)
                {
                    _mgr.ProcessInput(ref m, out bool handled);   // per-message ProcessInput BEFORE dispatch, like the engine
                    if (handled) continue;
                }
                Native.TranslateMessage(ref m);
                Native.DispatchMessageW(ref m);
            }
            Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 50, 0x04FF, 0x0004 /*MWMO_INPUTAVAILABLE*/);
        }
    }

    private static void TearDown()
    {
        try
        {
            if (_vp != null) { _vp.Stop(); _vp.RemoveEventHandler(_cookie); _vp.Disable(); _vp.Abandon(); }
            _mgr?.Deactivate(_hwnd);
        }
        catch (Exception ex) { Info($"teardown: {ex.Message}"); }
    }

    private static void Summary()
    {
        void L(string s) { Console.WriteLine(s); _txt.WriteLine(s); }
        L("");
        L("================ SUMMARY ================");
        L($"session {Ms(Stopwatch.GetTimestamp()) / 1000:F1} s   dm={_dm} dmAuto={_dmAuto} mip={_mip} enginePump={_enginePump} dmInertia={_dmInertia}   last monitor={_lastMonitorDev} window={_windowHz}Hz primary={_primaryHz}Hz");
        L($"compositor tick: n={_cTicks} {TickPeriod.Describe("ms")}  => {(TickPeriod.Count > 0 ? 1000.0 / TickPeriod.Mean : 0):F1} Hz measured vs window monitor {_windowHz} Hz");
        if (_windowHz > 0 && TickPeriod.Count > 0 && Math.Abs(1000.0 / TickPeriod.Mean - _windowHz) > 2)
            L($"  !! MISMATCH: the production clock ticks at {1000.0 / TickPeriod.Mean:F1} Hz but this window's monitor shows {_windowHz} Hz — every frame the app produces on that clock is NOT one displayed frame (judder/duplication)");
        L($"touchpad HID reports: n={_cHid} twoFinger={_cHidTwoFinger} parsed={_hidParsed} gap {HidGap.Describe("ms")} => {(HidGap.Count > 0 ? 1000.0 / HidGap.Mean : 0):F0} Hz digitizer");
        if (_hidParsed) L($"  contact-count histogram: {string.Join(" ", _contactHist.Select((n, i) => $"{i}:{n}"))}");
        L($"raw mouse: wheel={_cRawMouseWheel} moves={_cRawMouseMove}");
        L($"WM_POINTERWHEEL touchpad: n={_cPWheelPad} detented(×120)={_cPWheelPadDetented} hiRes={_cPWheelPadHiRes} gap {PWheelPadGap.Describe("ms")}");
        L($"WM_POINTERWHEEL mouse:    n={_cPWheelMouse} detented(×120)={_cPWheelMouseDetented} hiRes={_cPWheelMouseHiRes} gap {PWheelMouseGap.Describe("ms")}");
        L($"WM_POINTERWHEEL other:    n={_cPWheelOther}    WM_MOUSEWHEEL: n={_cMWheel} gap {MWheelGap.Describe("ms")}    pointer update/down/up msgs={_cPtrUpdate}");
        if (_dm)
        {
            L($"DM: hitTests={_cHit} setContactFail={_cSetContactFail} contentDeltas={_cContent} edges: {string.Join(", ", _statusEdges.Select(kv => $"{kv.Key}×{kv.Value}"))}");
            L($"  hit-test -> RUNNING {HitToRunning.Describe("ms")}");
            L($"  RUNNING -> first delta {RunningToFirstDelta.Describe("ms")}");
            L($"  finger lift (HID contacts 0) -> READY {LiftToReady.Describe("ms")}");
            L($"  ticks while RUNNING={_cTicksRunning} of which ZERO-delta={_cTicksRunningZero} ({(_cTicksRunning > 0 ? 100.0 * _cTicksRunningZero / _cTicksRunning : 0):F0}%)  worst gap between deltas while RUNNING={_worstRunningGapMs:F1} ms");
            L($"  |dy| per delivered tick {DmDeltaAbs.Describe("px")}   delta-to-delta gap {DmRunningTickGap.Describe("ms")}   HID reports per RUNNING tick {HidPerRunningTick.Describe("")}");
            L($"  content callbacks outside Update (delivered through the message loop): {_cContentOutsideUpdate} of {_cContent}");
            L($"  INERTIA episodes (no inertia configured): n={_cInertiaEpisodes} total={_inertiaTotalMs:F0} ms {InertiaEpisodeMs.Describe("ms")}");
            L($"    with two fingers still on the pad: n={_cInertiaEpisodesFingersDown} total={_inertiaFingersDownTotalMs:F0} ms {InertiaFingersDownMs.Describe("ms")} (each is a gesture the engine ENDS while the fingers are down)");
        }
        L($"BLOCKED windows (two fingers down, nothing delivered > {BlockedThresholdMs} ms): n={_cBlocked} total={_blockedTotalMs:F0} ms{(_hidParsed ? "" : "  (contact parsing unavailable — detection disabled)")}");
        string verdict;
        if (_cHid == 0 && _cPWheelPad == 0 && _cContent == 0) verdict = "NO TOUCHPAD INPUT SEEN — nothing was measured";
        else if (_cInertiaEpisodesFingersDown > 0) verdict = $"DM reported INERTIA {_cInertiaEpisodesFingersDown}x while two fingers were still on the pad (no inertia configured): the engine ends the gesture on that edge and forwards nothing until RUNNING returns";
        else if (_cBlocked > 0) verdict = $"OS/DM delivery had {_cBlocked} blocked window(s): the touchpad was down and nothing reached the window";
        else if (_cTicksRunning > 0 && _cTicksRunningZero > _cTicksRunning / 3) verdict = "DM delivered deltas on fewer than 2/3 of the ticks while RUNNING — the OS side is starving the frame clock";
        else if (_windowHz > 0 && TickPeriod.Count > 0 && Math.Abs(1000.0 / TickPeriod.Mean - _windowHz) > 2) verdict = "input delivery is fine; the production clock does not match this monitor's refresh (display-side judder)";
        else verdict = "input delivery is clean at this window — any remaining sluggishness is inside the app";
        L($"VERDICT: {verdict}");
    }

    internal sealed class Stat
    {
        private readonly List<double> _v = new();
        public int Count => _v.Count;
        public double Mean => _v.Count == 0 ? 0 : _v.Average();
        public void Add(double x) => _v.Add(x);
        public double P(double q)
        {
            if (_v.Count == 0) return 0;
            var s = _v.OrderBy(x => x).ToArray();
            return s[Math.Min(s.Length - 1, (int)Math.Round(q * (s.Length - 1)))];
        }
        public string Describe(string unit) => _v.Count == 0 ? "n=0"
            : $"n={_v.Count} mean={Mean:F2}{unit} p50={P(0.5):F2} p95={P(0.95):F2} max={_v.Max():F2}";
    }
}

// ---------------- COM interop (vtable order verified against directmanipulation.h 10.0.26100.0; copied from fluent-gpu/ops/tools/dm-probe) ----------------

[ComImport, Guid("FBF5D3B4-70C7-4163-9322-5A6F660D6FBC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationManager
{
    [PreserveSig] int Activate(IntPtr hwnd);
    [PreserveSig] int Deactivate(IntPtr hwnd);
    [PreserveSig] int RegisterHitTestTarget(IntPtr hwnd, IntPtr hitTestHwnd, int hitTestType);
    [PreserveSig] int ProcessInput(ref Native.MSG msg, [MarshalAs(UnmanagedType.Bool)] out bool handled);
    [PreserveSig] int GetUpdateManager(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDirectManipulationUpdateManager obj);
    [PreserveSig] int CreateViewport(IntPtr frameInfo, IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDirectManipulationViewport obj);
    [PreserveSig] int CreateContent(IntPtr frameInfo, ref Guid clsid, ref Guid riid, out IntPtr obj);
}

[ComImport, Guid("28B85A3D-60A0-48BD-9BA1-5CE8D9EA3A6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationViewport
{
    [PreserveSig] int Enable();
    [PreserveSig] int Disable();
    [PreserveSig] int SetContact(uint pointerId);
    [PreserveSig] int ReleaseContact(uint pointerId);
    [PreserveSig] int ReleaseAllContacts();
    [PreserveSig] int GetStatus(out int status);
    [PreserveSig] int GetTag(ref Guid riid, out IntPtr obj, out uint id);
    [PreserveSig] int SetTag(IntPtr obj, uint id);
    [PreserveSig] int GetViewportRect(out Native.RECT rect);
    [PreserveSig] int SetViewportRect(ref Native.RECT rect);
    [PreserveSig] int ZoomToRect(float left, float top, float right, float bottom, [MarshalAs(UnmanagedType.Bool)] bool animate);
    [PreserveSig] int SetViewportTransform([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
    [PreserveSig] int SyncDisplayTransform([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
    [PreserveSig] int GetPrimaryContent(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDirectManipulationContent content);
    [PreserveSig] int AddContent(IntPtr content);
    [PreserveSig] int RemoveContent(IntPtr content);
    [PreserveSig] int SetViewportOptions(int options);
    [PreserveSig] int AddConfiguration(int configuration);
    [PreserveSig] int RemoveConfiguration(int configuration);
    [PreserveSig] int ActivateConfiguration(int configuration);
    [PreserveSig] int SetManualGesture(int gesture);
    [PreserveSig] int SetChaining(int enabledTypes);
    [PreserveSig] int AddEventHandler(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IDirectManipulationViewportEventHandler handler, out uint cookie);
    [PreserveSig] int RemoveEventHandler(uint cookie);
    [PreserveSig] int SetInputMode(int mode);
    [PreserveSig] int SetUpdateMode(int mode);
    [PreserveSig] int Stop();
    [PreserveSig] int Abandon();
}

[ComImport, Guid("952121DA-D69F-45F9-B0F9-F23944321A6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationViewportEventHandler
{
    [PreserveSig] int OnViewportStatusChanged([MarshalAs(UnmanagedType.Interface)] IDirectManipulationViewport viewport, int current, int previous);
    [PreserveSig] int OnViewportUpdated([MarshalAs(UnmanagedType.Interface)] IDirectManipulationViewport viewport);
    [PreserveSig] int OnContentUpdated([MarshalAs(UnmanagedType.Interface)] IDirectManipulationViewport viewport, [MarshalAs(UnmanagedType.Interface)] IDirectManipulationContent content);
}

[ComImport, Guid("B89962CB-3D89-442B-BB58-5098FA0F9F16"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationContent
{
    [PreserveSig] int GetContentRect(out Native.RECT rect);
    [PreserveSig] int SetContentRect(ref Native.RECT rect);
    [PreserveSig] int GetViewport(ref Guid riid, out IntPtr viewport);
    [PreserveSig] int GetTag(ref Guid riid, out IntPtr obj, out uint id);
    [PreserveSig] int SetTag(IntPtr obj, uint id);
    [PreserveSig] int GetOutputTransform([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
    [PreserveSig] int GetContentTransform([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
    [PreserveSig] int SyncContentTransform([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] float[] matrix, uint pointCount);
}

[ComImport, Guid("B0AE62FD-BE34-46E7-9CAA-D361FACBB9CC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectManipulationUpdateManager
{
    [PreserveSig] int RegisterWaitHandleCallback(IntPtr handle, IntPtr eventHandler, out uint cookie);
    [PreserveSig] int UnregisterWaitHandleCallback(uint cookie);
    [PreserveSig] int Update(IntPtr frameInfo);
}

internal static class Native
{
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_VISIBLE = 0x10000000;
    public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_KEYDOWN = 0x0100, WM_QUIT = 0x0012,
        WM_DISPLAYCHANGE = 0x007E, WM_INPUT = 0x00FF, WM_EXITSIZEMOVE = 0x0232, WM_DPICHANGED = 0x02E0;
    public const uint WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    public const uint WM_POINTERUPDATE = 0x0245, WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;
    public const uint WM_POINTERWHEEL = 0x024E, WM_POINTERHWHEEL = 0x024F;
    public const uint DM_POINTERHITTEST = 0x0250;
    public const uint COINIT_APARTMENTTHREADED = 0x2;
    public const uint PT_TOUCH = 2, PT_PEN = 3, PT_MOUSE = 4, PT_TOUCHPAD = 5;
    public const uint RIDEV_INPUTSINK = 0x00000100, RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICEINFO = 0x2000000b;
    public const ushort RI_MOUSE_WHEEL = 0x0400, RI_MOUSE_HWHEEL = 0x0800;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWMOUSE { public ushort usFlags; private ushort _pad; public uint ulButtons, ulRawButtons; public int lLastX, lLastY; public uint ulExtraInformation; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWHID { public uint dwSizeHid, dwCount; }
    [StructLayout(LayoutKind.Sequential)] public struct RID_DEVICE_INFO_MOUSE { public uint dwId, dwNumberOfButtons, dwSampleRate; public int fHasHorizontalWheel; }
    [StructLayout(LayoutKind.Sequential)] public struct RID_DEVICE_INFO_HID { public uint dwVendorId, dwProductId, dwVersionNumber; public ushort usUsagePage, usUsage; }
    [StructLayout(LayoutKind.Explicit)]
    public struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public RID_DEVICE_INFO_MOUSE mouse;
        [FieldOffset(8)] public RID_DEVICE_INFO_HID hid;
        [FieldOffset(8)] private ulong _keyboardPad0; [FieldOffset(16)] private ulong _keyboardPad1; [FieldOffset(24)] private ulong _keyboardPad2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public uint dmFields;
        public int dmPositionX, dmPositionY; public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels; public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency,
            dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [StructLayout(LayoutKind.Sequential)] public struct UNSIGNED_RATIO { public uint uiNumerator, uiDenominator; }
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_TIMING_INFO
    {
        public uint cbSize; public UNSIGNED_RATIO rateRefresh; public ulong qpcRefreshPeriod; public UNSIGNED_RATIO rateCompose; public ulong qpcVBlank; public ulong cRefresh;
        public uint cDXRefresh; public ulong qpcCompose, cFrame; public uint cDXPresent; public ulong cRefreshFrame, cFrameSubmitted; public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed; public uint cDXPresentConfirmed; public ulong cRefreshConfirmed; public uint cDXRefreshConfirmed; public ulong cFramesLate;
        public uint cFramesOutstanding; public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed, cFrameComplete, qpcFrameComplete, cFramePending,
            qpcFramePending, cFramesDisplayed, cFramesComplete, cFramesPending, cFramesAvailable, cFramesDropped, cFramesMissed, cRefreshNextDisplayed,
            cRefreshNextPresented, cRefreshesDisplayed, cRefreshesPresented, cRefreshStarted, cPixelsReceived, cPixelsDrawn, cBuffersEmpty;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("ole32")] public static extern int CoInitializeEx(IntPtr r, uint flags);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnableMouseInPointer([MarshalAs(UnmanagedType.Bool)] bool enable);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern void PostQuitMessage(int code);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetPointerType(uint id, out uint type);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32")] public static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint ms, uint wakeMask, uint flags);
    [DllImport("user32")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("user32")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFOEXW mi);
    [DllImport("user32", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODEW dm);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool InvalidateRect(IntPtr h, IntPtr r, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [DllImport("user32")] public static extern IntPtr BeginPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32")] public static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);
    [DllImport("user32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint num, uint size);
    [DllImport("user32")] public static extern uint GetRawInputDeviceList(IntPtr list, ref uint num, uint size);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint GetRawInputDeviceInfoW(IntPtr device, uint cmd, ref RID_DEVICE_INFO data, ref uint size);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern uint GetRawInputDeviceInfoW(IntPtr device, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32")] public static extern uint GetRawInputData(IntPtr raw, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("hid")] public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsed, IntPtr report, uint reportLen);
    [DllImport("hid")] public static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, IntPtr usageList, ref uint usageLength, IntPtr preparsed, IntPtr report, uint reportLen);
    [DllImport("dwmapi")] public static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO ti);
    [DllImport("dcomp")] public static extern uint DCompositionWaitForCompositorClock(uint count, IntPtr handles, uint timeoutMs);
    [DllImport("gdi32")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32")] public static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32")] public static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32")] public static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool TextOutW(IntPtr hdc, int x, int y, string s, int len);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
}
