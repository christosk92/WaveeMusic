// ── Platform/Crash.FatalText.cs ────────────────────────────────────────────────────────────────────────────────────
// The runtime's LAST WORDS, kept for the exit-code report. A fail-fast ends the process without running any hook Wavee
// owns: `RaiseFailFastException` skips the VEH, the unhandled filter and `AppDomain.UnhandledException`, so the child
// only ever saw an exit code (issue c14d1271627f: 14 reports of 0xC0000602, no frames, no dump). Before it raises, the
// runtime does say WHY — on STD_ERROR_HANDLE, which in a Store / Start-menu launch is null (or NUL), so it went nowhere:
//   - NativeAOT's own `RhFailFast` (exit 0xC0000602) prints "Fatal error. Invalid Program: attempted to call a
//     UnmanagedCallersOnly method from managed code." (a reverse P/Invoke — a VEH, a COM or WinRT callback — entered
//     on a thread already in managed code) or ".NET runtime failed to initialize."; its other fail-fast sites are
//     silent, so an EMPTY section on a 0xC0000602 report is itself a finding;
//   - a stack overflow (exit 0xC00000FD) prints "Process is terminating due to StackOverflowException.";
//   - the managed fail-fast (`Environment.FailFast`, an unhandled exception whose handler never ran; exit 0xC0000409)
//     prints "Process terminated." with the message, the exception and its `Wavee!<BaseAddress>+0x…` frames.
//
// So the parent points an UNUSED stderr at `crash\fatal-<pid>.txt` (`Capture`, from `Crash.Host.Install`) and the child
// reads it back when the parent exits non-zero (`ReadForParent`, from `Crash.Handler.OnParentExited`): the text goes
// into report.txt (the upload scrubber redacts paths and identities there like everywhere else in it) and its frames
// into `rvas`, which the Worker symbolicates. A stderr someone already listens to (a console, a pipe, `2> file`, the
// headless inspector) is left alone.
//
// NO STALE FILES: the parent opens the file delete-on-close and the child opens it at start-up with share-delete, so it
// exists exactly while one of the two is alive: a clean exit, a crash and a dead child all leave nothing behind.
// The handle is never closed by Wavee: the runtime writes to it at the very end, from whatever thread is dying.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Wavee;

public static partial class Crash
{
    /// <summary>The parent's stderr redirect (<see cref="Capture"/>) and the child's read-back (<see cref="ReadForParent"/>)
    /// of the runtime's fatal-error text. Never throws.</summary>
    public static partial class FatalText
    {
        /// <summary>How much of the file the child keeps: the tail, where the last fatal text is.</summary>
        public const int MaxBytes = 16 * 1024;

        /// <summary>At most this many frames ride into <c>summary.rvas</c> (the Worker keeps 64).</summary>
        public const int MaxRvas = 32;

        const int StdErrorHandle = -12;
        const uint FileTypeDisk = 1, FileTypeChar = 2, FileTypePipe = 3;   // GetFileType; 0 = unknown (no usable handle)

        static FileStream? s_sink;   // rooted for the process's life: the runtime writes through its handle at the end

        /// <summary><c>crash\fatal-&lt;pid&gt;.txt</c> under the log folder.</summary>
        public static string PathFor(string logFolder, int pid)
            => Path.Combine(Files.Root(logFolder), "fatal-" + pid.ToString(CultureInfo.InvariantCulture) + ".txt");

        /// <summary>True when a stderr handle of this file type is already read by someone (a console, a pipe, a
        /// redirect to a file): <see cref="Capture"/> must leave it alone. A character device that is not a console is
        /// the NUL device (a launcher's "no output"), which nobody reads.</summary>
        public static bool IsListened(uint fileType, bool isConsole)
            => fileType is FileTypeDisk or FileTypePipe || (fileType == FileTypeChar && isConsole);

        /// <summary>Parent, once, before the handler child is spawned: points an unused STD_ERROR_HANDLE at
        /// <see cref="PathFor"/> for this process. Returns what it did, for the caller's log line.</summary>
        public static string Capture(string logFolder)
        {
            try
            {
                nint current = GetStdHandle(StdErrorHandle);
                uint type = current is 0 or -1 ? 0 : GetFileType(current);
                bool console = type == FileTypeChar && GetConsoleMode(current, out _);
                if (IsListened(type, console)) return "kept:" + (type switch { FileTypeDisk => "file", FileTypePipe => "pipe", _ => "console" });
                string path = PathFor(logFolder, Environment.ProcessId);
                Directory.CreateDirectory(Files.Root(logFolder));
                var sink = new FileStream(path, FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.DeleteOnClose);
                if (!SetStdHandle(StdErrorHandle, sink.SafeFileHandle.DangerousGetHandle()))
                {
                    int err = Marshal.GetLastPInvokeError();
                    sink.Dispose();
                    return "failed:" + err.ToString(CultureInfo.InvariantCulture);
                }
                s_sink = sink;
                // Managed writers too (a boot exception after this point, an engine `--fg` trace): Console.Error was
                // bound to the null handle on first use. The file pointer follows these writes, so the runtime's own
                // last words still land after them.
                Console.SetError(new StreamWriter(sink, new UTF8Encoding(false), 256) { AutoFlush = true });
                return type == FileTypeChar ? "captured:nul" : "captured";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return "failed:" + ex.GetType().Name;
            }
        }

        /// <summary>Child, at start-up: opens the parent's file (if it captured one) so it outlives the parent's own
        /// handle. Null when there is none.</summary>
        public static FileStream? OpenForParent(string logFolder, int parentPid)
        {
            try
            {
                return new FileStream(PathFor(logFolder, parentPid), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>Child, after the parent exited: the last <see cref="MaxBytes"/> of what the runtime wrote, trimmed;
        /// "" when nothing was written. <paramref name="truncated"/> says the file was longer.</summary>
        public static string ReadForParent(FileStream? file, out bool truncated)
        {
            truncated = false;
            if (file is null) return "";
            try
            {
                long length = file.Length;
                if (length <= 0) return "";
                long start = Math.Max(0, length - MaxBytes);
                truncated = start > 0;
                var buffer = new byte[(int)(length - start)];
                int read = RandomAccess.Read(file.SafeFileHandle, buffer, start);
                return Normalize(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                return "";
            }
        }

        /// <summary>The text as report.txt carries it: CRLF folded to LF, NULs dropped, blank edges trimmed.</summary>
        public static string Normalize(string raw)
            => raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\0", "", StringComparison.Ordinal).Trim();

        /// <summary>The report.txt section for <paramref name="text"/> (nothing when it is empty).</summary>
        public static void AppendSection(StringBuilder sb, string text, bool truncated)
        {
            if (text.Length == 0) return;
            sb.Append("\nstderr (the process's last output; a fail-fast writes its reason here").Append(truncated ? ", last 16 KB" : "").Append("):\n")
              .Append(text).Append('\n');
        }

        /// <summary>The NativeAOT frames of the LAST fatal trace in <paramref name="text"/> (from its last "Process
        /// terminated." or "Unhandled exception." on; earlier output may hold other traces), innermost first, at most
        /// <see cref="MaxRvas"/>.</summary>
        public static long[] Rvas(string text)
        {
            int at = Math.Max(text.LastIndexOf("Process terminated.", StringComparison.Ordinal),
                text.LastIndexOf("Unhandled exception.", StringComparison.Ordinal));
            var all = Report.ParseRvas(at > 0 ? text[at..] : text);
            return all.Count <= MaxRvas ? all.ToArray() : all.GetRange(0, MaxRvas).ToArray();
        }

        [LibraryImport("kernel32.dll")]
        private static partial nint GetStdHandle(int nStdHandle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetStdHandle(int nStdHandle, nint hHandle);

        [LibraryImport("kernel32.dll")]
        private static partial uint GetFileType(nint hFile);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);
    }
}
