// ── Platform/Crash.Native.cs ───────────────────────────────────────────────────────────────────────────────────────
// The pure half of native-crash identity (#165, docs/plans/wavee/crash-production-readiness-implementation.md "W3a" +
// appendix A3): which captured frames become a native bundle's `rvas`, which loaded module a fault address falls in,
// and how that module's name is normalized before it reaches `summary.json`. Engine-free and process-free — the two
// callers own the I/O: `Crash.NativeHook` captures the raw frames allocation-free inside the VEH, and the CHILD
// (`Crash.Handler.TryReadFault`) reads the parent's exception record and module list over ReadProcessMemory /
// K32EnumProcessModulesEx. Every rule here is a table in `Wavee.Tests/CrashNativeTests.cs`.
//
// Role: CORE (pure)
// Owner: W3a
// Spec: crash-production-readiness-implementation.md "W3a · App native capture + bundle core", appendix A3
//
// WHY START AT THE EXCEPTION ADDRESS. `RtlCaptureStackBackTrace` runs INSIDE the vectored handler, so the walk begins
// with the handler's own frames (CaptureFaultStack/Veh — Wavee.exe), then ntdll's dispatcher (RtlpCallVectoredHandlers,
// RtlDispatchException, KiUserExceptionDispatcher), and only then — the unwinder steps through
// KiUserExceptionDispatcher's machine frame on x64 and ARM64 — the faulting frame itself, whose PC IS the exception
// address. Everything before that frame is "how the crash was reported", never "where it happened", so selection
// starts there. When the walk never produced that exact address (x64 emulated on ARM64, a truncated unwind) the
// fallback skips the LEADING run of in-image frames — the handler's own — and the in-image filter drops the dispatcher.

namespace Wavee;

public static partial class Crash
{
    /// <summary>Projects a raw native back trace onto Wavee.exe RVAs — the same identity the managed path's
    /// <c>Report.ParseRvas</c> yields, so the Worker symbolicates and groups both kinds through one map.</summary>
    public static class NativeFrames
    {
        /// <summary><see cref="MaxCapture"/>: the VEH's capture buffer (frames, <c>RtlCaptureStackBackTrace</c>'s
        /// documented skip+count ceiling is 63). <see cref="MaxRvas"/>: how many app frames a bundle keeps (the Worker
        /// caps <c>rvas</c> at 64).</summary>
        public const int MaxCapture = 62, MaxRvas = 32;

        /// <summary>The in-image frames of <paramref name="frames"/> (innermost first) as RVAs from
        /// <paramref name="imageBase"/>, starting AT the frame whose address equals <paramref name="exceptionAddress"/>
        /// (inclusive — a fault inside the image is its own first frame). When no frame equals it, the leading run of
        /// in-image frames (the handler's own) is skipped instead. Foreign frames never contribute; at most
        /// <paramref name="max"/> are kept. An empty image range or no app frame yields an empty array.</summary>
        public static long[] SelectAppRvas(ReadOnlySpan<nint> frames, nint exceptionAddress, nint imageBase, nuint imageSize, int max = MaxRvas)
        {
            if (max <= 0 || imageBase == 0 || imageSize == 0 || frames.IsEmpty) return [];
            int start = exceptionAddress != 0 ? frames.IndexOf(exceptionAddress) : -1;
            if (start < 0)
            {
                start = 0;
                while (start < frames.Length && InImage(frames[start], imageBase, imageSize)) start++;
            }
            var list = new List<long>(Math.Min(max, frames.Length - start));
            for (int i = start; i < frames.Length && list.Count < max; i++)
                if (InImage(frames[i], imageBase, imageSize)) list.Add((long)(frames[i] - imageBase));
            return list.ToArray();
        }

        static bool InImage(nint address, nint imageBase, nuint imageSize) =>
            address >= imageBase && (nuint)(address - imageBase) < imageSize;
    }

    /// <summary>The faulting module of a native crash: which loaded module's range holds the exception address, and
    /// the normalized name that goes into <see cref="Summary.FaultModule"/> (the Worker's native grouping key is
    /// <c>code | module | top app frames</c>, falling back to <c>module + offset</c>).</summary>
    public static class FaultModule
    {
        /// <summary>The longest name <see cref="Normalize"/> keeps — the Worker's <c>^[a-z0-9][a-z0-9._-]{0,63}$</c>.</summary>
        public const int MaxNameLength = 64;

        /// <summary>The index of the module whose <c>[Base, Base + Size)</c> holds <paramref name="address"/>, or -1. A
        /// module whose information could not be read (Base 0 / Size 0) never matches.</summary>
        public static int Find(ReadOnlySpan<(nint Base, uint Size)> modules, nint address)
        {
            for (int i = 0; i < modules.Length; i++)
            {
                var (b, size) = modules[i];
                if (size != 0 && address >= b && (nuint)(address - b) < size) return i;
            }
            return -1;
        }

        /// <summary>Base name only (anything up to the last <c>\</c> or <c>/</c> is dropped), lower-case, and then it must
        /// match the Worker's sanitizer exactly — an ASCII letter/digit first, then <c>[a-z0-9._-]</c>, at most
        /// <see cref="MaxNameLength"/> characters — or the answer is <c>""</c>. Never partially cleaned: a name the
        /// Worker would reject is not one this side invents a spelling for.</summary>
        public static string Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            ReadOnlySpan<char> name = raw.AsSpan().Trim();
            int slash = name.LastIndexOfAny('\\', '/');
            if (slash >= 0) name = name[(slash + 1)..];
            if (name.IsEmpty || name.Length > MaxNameLength) return "";
            Span<char> lower = stackalloc char[MaxNameLength];
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c is >= 'A' and <= 'Z') c = (char)(c + ('a' - 'A'));
                bool alnum = c is >= 'a' and <= 'z' or >= '0' and <= '9';
                if (!alnum && (i == 0 || c is not ('.' or '_' or '-'))) return "";
                lower[i] = c;
            }
            return new string(lower[..name.Length]);
        }
    }
}
