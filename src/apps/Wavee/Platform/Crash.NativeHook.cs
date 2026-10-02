// ── Platform/Crash.NativeHook.cs ───────────────────────────────────────────────────────────────────────────────────
// The native fault hooks, born as the WP-0 spike (docs/plans/wavee/crash-diagnostics-implementation.md §B.0, §F row
// "0 · Spike", §G rows Native AV / Hang / Boot). Measured 2026-09-25: the VEH is the hook that sees a foreign-code AV;
// the unhandled filter stays as the NativeAOT fallback. Both fire only for faults OUTSIDE Wavee.exe (`IsForeignFault`)
// and hand the first one to `Crash.Host.RequestDumpCore` through `OnForeignFault`.
//
// Role: PLATFORM (Win32 P/Invoke)
// Spec: crash-diagnostics-implementation.md §B.0, §G "Native AV" / "Hang" / "Boot loop";
//       crash-production-readiness-implementation.md (#165) "W3a" + appendix A3 (the fault-stack capture)
//
// CAPTURE FIRST, ALLOCATION-FREE (#165 W3a). A native bundle's only frames are the ones walked HERE, on the faulting
// thread, while its stack still exists. `CaptureFaultStack` runs before `LogFired` and before the dump request — both
// allocate (strings, the bundle writer, the pipe) on a heap that may be what just faulted. It records the exception
// address plus up to `NativeFrames.MaxCapture` return addresses from `RtlCaptureStackBackTrace` into a `NativeMemory`
// buffer allocated once by `Install`, whose warm-up call also binds the import (no export lookup — no loader lock —
// inside a hook). `FaultRvas()` projects the buffer onto Wavee.exe RVAs later, through the pure
// `NativeFrames.SelectAppRvas` (Crash.Native.cs). The faulting MODULE is not resolved in this process at all: the
// child reads it out of process (`Crash.Handler.TryReadFault`) while this thread stays parked on the dump reply.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Wavee;

public static partial class Crash
{
    /// <summary>The two native crash hooks (VEH + unhandled filter), the allocation-free fault-stack capture they run
    /// first, a deliberate foreign-code fault to trip them with, and the hang probe. Everything a hook does before
    /// <see cref="LogFired"/> is allocation-free — a handler running after a fault is not a place to allocate.</summary>
    public static partial class NativeHook
    {
        static int s_installed;
        static int s_reported;                  // 0/1: the dump hook ran once this process (VEH first, filter as fallback)
        static nint s_imageBase;
        static nuint s_imageSize;

        // The fault-stack capture (#165 W3a): written once, by the hook that wins s_reported, on the faulting thread —
        // and read back by that same thread inside OnForeignFault, so plain fields suffice.
        static unsafe nint* s_frames;           // NativeMemory, NativeFrames.MaxCapture slots; allocated by Install, never freed
        static int s_frameCount;
        static nint s_faultAddress;

        /// <summary>Fault codes the spike watches for: access violation, illegal instruction, in-page error, array
        /// bounds exceeded, integer divide-by-zero — the classes a native crash actually raises.</summary>
        const uint AccessViolation = 0xC0000005, IllegalInstruction = 0xC000001D, InPageError = 0xC0000006,
            ArrayBoundsExceeded = 0xC000008C, IntegerDivideByZero = 0xC0000094;

        /// <summary>A later package (the real crash host, WP-B) can request a dump from the filter once one lands —
        /// this spike only calls it, never implements it. Called from the FILTER only (the VEH runs too early / too
        /// often relative to the "is this actually unhandled" question the filter answers).</summary>
        public static Action<uint, nint>? OnForeignFault;

        /// <summary>Registers both candidates. Idempotent; safe to call once from <c>Shell.Host.InstallCrashNet</c>.
        /// Captures the main module's image range ONCE, here, so the hot fault path never touches
        /// <see cref="Process"/> again.</summary>
        public static unsafe void Install()
        {
            if (Interlocked.Exchange(ref s_installed, 1) != 0) return;
            CaptureImageRange();
            s_frames = (nint*)NativeMemory.AllocZeroed((nuint)NativeFrames.MaxCapture, (nuint)sizeof(nint));
            // Warm-up: binds the ntdll import NOW, so the first real capture inside a hook never resolves an export
            // (GetProcAddress under the loader lock) on a thread that just faulted. Its one frame is overwritten later.
            _ = RtlCaptureStackBackTrace(0, 1, s_frames, null);

            delegate* unmanaged[Stdcall]<EXCEPTION_POINTERS*, int> filter = &Filter;
            SetUnhandledExceptionFilter((nint)filter);

            delegate* unmanaged[Stdcall]<EXCEPTION_POINTERS*, int> veh = &Veh;
            AddVectoredExceptionHandler(0 /* last — try every other handler's VEH first */, (nint)veh);
        }

        static void CaptureImageRange()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                if (proc.MainModule is { } main)
                {
                    s_imageBase = main.BaseAddress;
                    s_imageSize = unchecked((nuint)(long)main.ModuleMemorySize);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                Log.Warn("crash", "crash.native.hook could not read the main module's image range", ex);
            }
        }

        /// <summary><c>SetUnhandledExceptionFilter</c>'s candidate. Runs only once nothing else claimed the exception,
        /// so a fault it recognizes as foreign is genuinely about to terminate the process either way — it reports,
        /// offers the dump hook, and hands the OS its own default handling.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        static unsafe int Filter(EXCEPTION_POINTERS* info)
        {
            if (IsForeignFault(info, out uint code))
            {
                bool first = Interlocked.Exchange(ref s_reported, 1) == 0;
                if (first) CaptureFaultStack(info);   // FIRST: allocation-free, before LogFired / the dump request allocate
                LogFired("filter", code);
                if (first)
                    try { OnForeignFault?.Invoke(code, (nint)info); } catch (Exception) { }
                return ExceptionExecuteHandler;
            }
            return ExceptionContinueSearch;
        }

        /// <summary><c>AddVectoredExceptionHandler</c>'s candidate. Runs BEFORE structured exception handling walks the
        /// stack (and may run more than once per fault, e.g. a first-chance guard page probe) — it only ever reports
        /// and always defers, never claims the exception.</summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        static unsafe int Veh(EXCEPTION_POINTERS* info)
        {
            if (IsForeignFault(info, out uint code))
            {
                // Measured 2026-09-25 (spike WP-0, CoreCLR run): the VEH is the ONLY hook that sees a foreign-code AV —
                // the runtime turns it into a fatal AccessViolationException before any unhandled-exception filter runs.
                // So the dump is requested HERE, while the process is still alive and EXCEPTION_POINTERS is valid; the
                // filter path above stays as the NativeAOT fallback. Once per process: the filter may still run after.
                bool first = Interlocked.Exchange(ref s_reported, 1) == 0;
                if (first) CaptureFaultStack(info);   // FIRST: allocation-free, before LogFired / the dump request allocate
                LogFired("veh", code);
                if (first)
                    try { OnForeignFault?.Invoke(code, (nint)info); } catch (Exception) { }
            }
            return ExceptionContinueSearch;
        }

        /// <summary>The whole capture: two field writes and one ntdll walk into the preallocated buffer — no managed
        /// allocation, no lock, no export lookup (<see cref="Install"/>'s warm-up bound the import). Called only after
        /// <see cref="IsForeignFault"/> validated <paramref name="info"/> and its exception record.</summary>
        static unsafe void CaptureFaultStack(EXCEPTION_POINTERS* info)
        {
            s_faultAddress = ((EXCEPTION_RECORD*)info->ExceptionRecord)->ExceptionAddress;
            nint* frames = s_frames;
            s_frameCount = frames != null ? RtlCaptureStackBackTrace(0, (uint)NativeFrames.MaxCapture, frames, null) : 0;
        }

        /// <summary>The faulting instruction's address as the hook captured it (0 before any native fault) — the
        /// native headline's "at 0x…" (it used to print the EXCEPTION_POINTERS address instead).</summary>
        public static nint FaultAddress => s_faultAddress;

        /// <summary>The captured fault stack as Wavee.exe RVAs, innermost first (<see cref="NativeFrames.SelectAppRvas"/>):
        /// a native bundle's <c>summary.json</c> <c>rvas</c> and its report's "Frames (RVA)" section. Allocates — call it
        /// from the dump request, never from inside a hook. Empty before any native fault.</summary>
        public static unsafe long[] FaultRvas() =>
            NativeFrames.SelectAppRvas(new ReadOnlySpan<nint>(s_frames, s_frames != null ? s_frameCount : 0),
                s_faultAddress, s_imageBase, s_imageSize);

        static unsafe bool IsForeignFault(EXCEPTION_POINTERS* info, out uint code)
        {
            code = 0;
            if (info == null || s_imageBase == 0) return false;
            var record = (EXCEPTION_RECORD*)info->ExceptionRecord;
            if (record == null) return false;
            code = record->ExceptionCode;
            if (code is not (AccessViolation or IllegalInstruction or InPageError or ArrayBoundsExceeded or IntegerDivideByZero)) return false;
            nint addr = record->ExceptionAddress;
            nint lo = s_imageBase, hi = unchecked(s_imageBase + (nint)s_imageSize);
            return addr < lo || addr >= hi;
        }

        /// <summary>The one allocation in either hook: <c>string.Concat</c> of constants plus the code's hex text.</summary>
        static void LogFired(string via, uint code)
        {
            Log.Critical("crash", string.Concat("crash.native.hook fired=", via, " code=0x", code.ToString("x8", CultureInfo.InvariantCulture)));
            Log.Flush();
        }

        // ── the probe arms ──────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>`--crash-probe native`: faults INSIDE foreign code (ntdll), not managed code — a managed
        /// <c>*(int*)0</c> becomes an NRE under NativeAOT and would prove nothing about either hook above. A null
        /// destination with a nonzero length is a guaranteed access violation raised from inside
        /// <c>RtlFillMemory</c>.</summary>
        public static void Fault()
        {
            Log.Warn("crash", "crash.probe native: faulting in ntdll");
            Log.Flush();
            RtlFillMemory(0, 16, 0);
        }

        /// <summary>`--crash-probe hang`: posts a 45 s sleep onto the UI thread via <paramref name="post"/> (the
        /// report chrome's <c>UsePost()</c>) — this IS the hang, not a simulation of one.</summary>
        public static void Hang(Action<Action> post)
        {
            Log.Warn("crash", "crash.probe hang: sleeping 45 s on the UI thread");
            Log.Flush();
            post(static () => Thread.Sleep(45_000));
        }

        // ── Win32 ───────────────────────────────────────────────────────────────────────────────────────────────────

        const int ExceptionExecuteHandler = 1, ExceptionContinueSearch = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct EXCEPTION_POINTERS
        {
            public nint ExceptionRecord;
            public nint ContextRecord;
        }

        /// <summary>Only the fields the spike reads; the full native struct also carries
        /// <c>NumberParameters</c>/<c>ExceptionInformation[15]</c> after this, which nothing here touches.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct EXCEPTION_RECORD
        {
            public uint ExceptionCode;
            public uint ExceptionFlags;
            public nint ExceptionRecordNext;
            public nint ExceptionAddress;
        }

        [LibraryImport("kernel32.dll")]
        private static partial nint SetUnhandledExceptionFilter(nint lpTopLevelExceptionFilter);

        [LibraryImport("kernel32.dll")]
        private static partial nint AddVectoredExceptionHandler(uint first, nint handler);

        [LibraryImport("ntdll.dll")]
        private static partial void RtlFillMemory(nint destination, nuint length, byte fill);

        /// <summary><c>USHORT RtlCaptureStackBackTrace(ULONG FramesToSkip, ULONG FramesToCapture, PVOID* BackTrace,
        /// PULONG BackTraceHash)</c> — blittable, so the generated stub is a direct call (no marshaller, no allocation).</summary>
        [LibraryImport("ntdll.dll")]
        private static unsafe partial ushort RtlCaptureStackBackTrace(uint framesToSkip, uint framesToCapture, nint* backTrace, uint* backTraceHash);
    }
}
