// ── Platform/Crash.Trail.cs ────────────────────────────────────────────────────────────────────────────────────────
// What a crash report says about the minutes BEFORE the crash, the native code the threads were in, and the machine.
// The 2026-11-07 crash round could solve four of eight issues only because a log tail happened to hold the right line:
// "minimized for hours, the trim ran, restored, crashed" and "switched GPU in Settings, the driver faulted" were both
// invisible in the reports themselves. Three parts:
//
//   1. THE EVENT TRAIL. The engine's `FluentGpu.Foundation.CrashTrail` ring (~256 entries, allocation-free) records the
//      engine's own events (park/restore, device lost/recovered, GPU switch, cold trims); the app adds navigations (the
//      route NAME, never its argument), track start/skip (no ids), memory trims, entering and leaving the private key
//      path, shutdown phases (`AppTrail`). Codes and numbers only, so the trail is not personal by construction.
//   2. NATIVE REGIONS. The same block holds a per-thread label set around risky native calls (`CrashTrail.Enter`):
//      d3d12.close, d3d12.recovery, pipeline.compile with the pipeline's name, wic.decode, and the app's key path. The
//      native hook names the faulting thread's region; every report lists the regions threads were in.
//   3. THE MACHINE. GPU driver version, a virtual-machine flag (SMBIOS strings from a fixed list), and the FILE NAMES of
//      loaded modules that are neither Windows' nor Wavee's (overlays and injectors: RivaTuner, Discord, Steam, recorders).
//
// WHY A FILE MAPPING. An exit-code report (a fail-fast, a stack overflow) is written by the handler child after the
// parent is gone and ran no hook. So the parent moves the trail block onto `crash\trail-<pid>.bin` (delete-on-close,
// mapped; `Share`), and the child opens the same file at start-up (`OpenForParent`), the way `Crash.FatalText` keeps the
// runtime's stderr: the file exists exactly while one of them is alive, and the child reads the parent's last writes
// after it died (the pages belong to the file, not to the dead process).

using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using FluentGpu.Foundation;

namespace Wavee;

public static partial class Crash
{
    /// <summary>The app's trail events (<see cref="CrashTrail.AppBase"/> and up). The values are the wire between the parent
    /// and the handler child: never renumber one.</summary>
    public enum AppTrail : ushort
    {
        /// <summary>A route was shown; text = the route's NAME (never its argument).</summary>
        Navigate = 1000,
        /// <summary>A track load began: A = playable kind, B = start position ms.</summary>
        TrackStart = 1001,
        /// <summary>A skip input: A = +1 next, -1 previous.</summary>
        TrackSkip = 1002,
        /// <summary>The memory governor trimmed: A = pressure level, B = bytes freed.</summary>
        MemoryTrim = 1003,
        /// <summary>The private key path was entered (A = 1 key, 2 body decryptor).</summary>
        KeyPathEnter = 1004,
        /// <summary>…and left: A = outcome (0 ok, 1 no key, 2 threw, 3 cancelled), B = ms inside.</summary>
        KeyPathLeave = 1005,
        /// <summary>A shutdown checkpoint: A = <see cref="Crash.ShutdownPhase"/>.</summary>
        ShutdownPhase = 1006,
        /// <summary>The key-path breaker is open this launch: A = recent key-path faults.</summary>
        KeyPathBreakerOpen = 1007,
        /// <summary>A native fault inside the key path was recorded for the breaker.</summary>
        KeyPathFault = 1008,
        /// <summary>The crash handler is up: A = 1 when the trail is shared with it.</summary>
        CrashHandler = 1009,
    }

    /// <summary>The app's native region codes (beside the engine's <see cref="NativeRegion"/>).</summary>
    public static class AppRegion
    {
        /// <summary>The private key path's derivation (the seam's <c>KeyDeriver</c> call).</summary>
        public const ushort KeyPath = 1000;
        /// <summary>The private key path's native body decryptor.</summary>
        public const ushort KeyPathDecrypt = 1001;

        public static bool IsKeyPath(ushort kind) => kind is KeyPath or KeyPathDecrypt;
    }

    public static partial class Trail
    {
        /// <summary>How many trail entries a report prints (the ring keeps <see cref="CrashTrail.Capacity"/>).</summary>
        public const int ReportEntries = 200;

        public static void Note(AppTrail e, long a = 0, long b = 0) => CrashTrail.Record((ushort)e, a, b, default);

        public static void Note(AppTrail e, long a, long b, ReadOnlySpan<char> text) => CrashTrail.Record((ushort)e, a, b, text);

        // ── sharing the block with the handler child ────────────────────────────────────────────────────────────────

        static FileStream? s_file;              // rooted for the process's life, like the mapping and the view below
        static MemoryMappedFile? s_map;
        static MemoryMappedViewAccessor? s_view;

        /// <summary><c>crash\trail-&lt;pid&gt;.bin</c> under the log folder.</summary>
        public static string PathFor(string logFolder, int pid)
            => Path.Combine(Files.Root(logFolder), "trail-" + pid.ToString(CultureInfo.InvariantCulture) + ".bin");

        /// <summary>Parent, once, before the handler child is spawned: moves the trail onto a mapped, delete-on-close file the
        /// child can read after this process is gone. Returns what it did, for the caller's log line. Never throws.</summary>
        public static unsafe string Share(string logFolder)
        {
            try
            {
                Directory.CreateDirectory(Files.Root(logFolder));
                var file = new FileStream(PathFor(logFolder, Environment.ProcessId), FileMode.Create, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.DeleteOnClose);
                file.SetLength(CrashTrail.TotalBytes);
                var map = MemoryMappedFile.CreateFromFile(file, null, CrashTrail.TotalBytes, MemoryMappedFileAccess.ReadWrite,
                    HandleInheritability.None, leaveOpen: true);
                var view = map.CreateViewAccessor(0, CrashTrail.TotalBytes, MemoryMappedFileAccess.ReadWrite);
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);   // never released: the trail writes through it until exit
                if (!CrashTrail.Attach(p + view.PointerOffset, CrashTrail.TotalBytes)) return "failed:attach";
                s_file = file; s_map = map; s_view = view;
                return "shared";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return "failed:" + ex.GetType().Name;
            }
        }

        /// <summary>Child, at start-up: opens the parent's trail file so it outlives the parent. Null when there is none.</summary>
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

        /// <summary>Child: the parent's trail as it stands (alive or dead). Null when unreadable.</summary>
        public static CrashTrail.Snapshot? ReadForParent(FileStream? file)
        {
            if (file is null) return null;
            try
            {
                var buffer = new byte[CrashTrail.TotalBytes];
                int read = RandomAccess.Read(file.SafeFileHandle, buffer, 0);
                return read == buffer.Length ? CrashTrail.Decode(buffer) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { return null; }
        }

        /// <summary>This process's trail.</summary>
        public static CrashTrail.Snapshot? Live()
        {
            try { return CrashTrail.Decode(CrashTrail.Block); } catch (Exception) { return null; }
        }

        // ── names ───────────────────────────────────────────────────────────────────────────────────────────────────

        public static string RegionName(ushort kind) => kind switch
        {
            (ushort)NativeRegion.D3D12Close => "d3d12.close",
            (ushort)NativeRegion.DeviceRecovery => "d3d12.recovery",
            (ushort)NativeRegion.PipelineCompile => "pipeline.compile",
            (ushort)NativeRegion.WicDecode => "wic.decode",
            AppRegion.KeyPath => "keypath",
            AppRegion.KeyPathDecrypt => "keypath.decrypt",
            _ => "region" + kind.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>A region as a report line: <c>pipeline.compile "glyphs" for 12 ms</c>.</summary>
        public static string Describe(CrashTrail.Region r, long nowTickMs)
        {
            var sb = new StringBuilder(64).Append(RegionName(r.Kind));
            if (r.Text.Length > 0) sb.Append(" \"").Append(r.Text).Append('"');
            sb.Append(" for ").Append(Math.Max(0, nowTickMs - r.SinceTickMs).ToString(CultureInfo.InvariantCulture)).Append(" ms");
            return sb.ToString();
        }

        // DXGI's vendor and device ids are 32-bit (a Qualcomm adapter reports 0x4D4F4351, "QCOM"), so neither half is masked.
        static string Adapter(long id) => id == 0 ? "auto"
            : ((uint)(id >> 32)).ToString("X4", CultureInfo.InvariantCulture) + ":" + ((uint)id).ToString("X4", CultureInfo.InvariantCulture);

        static string Park(long park) => park switch { 1 => "cover", 2 => "minimized", _ => "none" };

        /// <summary>One entry as text: the event's name and its arguments, named.</summary>
        public static string Describe(CrashTrail.Entry e)
        {
            string I(long v) => v.ToString(CultureInfo.InvariantCulture);
            return e.Code switch
            {
                (ushort)TrailEvent.WindowParked => "window.parked park=" + Park(e.A) + (e.B != 0 ? " popout" : ""),
                (ushort)TrailEvent.WindowRestored => "window.restored" + (e.B != 0 ? " popout" : ""),
                (ushort)TrailEvent.HiddenShallow => "hidden.shallow park=" + Park(e.A) + " released=" + I(e.B),
                (ushort)TrailEvent.HiddenDeep => "hidden.deep park=" + Park(e.A) + " parked=" + I(e.B),
                (ushort)TrailEvent.HiddenRestore => "hidden.restore from=" + (e.A == 2 ? "deep" : e.A == 1 ? "shallow" : I(e.A)) + " parked=" + I(e.B),
                (ushort)TrailEvent.DeviceLost => "device.lost reason=0x" + ((uint)e.A).ToString("X8", CultureInfo.InvariantCulture) + " recoveries=" + I(e.B) + " path=" + e.Text,
                (ushort)TrailEvent.DeviceRecovered => "device.recovered recovery=" + I(e.A),
                (ushort)TrailEvent.DeviceRecoverFailed => "device.recover-failed recovery=" + I(e.A),
                (ushort)TrailEvent.AdapterSwitch => "gpu.switch from=" + Adapter(e.A) + " to=" + Adapter(e.B),
                (ushort)TrailEvent.AdapterSelected => "gpu.device adapter=" + Adapter(e.A) + " software=" + I(e.B) + " mode=" + e.Text,
                (ushort)TrailEvent.ColdTrim => "cold.trim" + ((e.A & 1) != 0 ? " pixels" : "") + ((e.A & 2) != 0 ? " scene slots=" + I(e.B) : "") + ((e.A & 4) != 0 ? " snapshots" : ""),
                (ushort)AppTrail.Navigate => "nav route=" + e.Text,
                (ushort)AppTrail.TrackStart => "track.start kind=" + I(e.A) + " fromMs=" + I(e.B),
                (ushort)AppTrail.TrackSkip => e.A < 0 ? "track.skip previous" : "track.skip next",
                (ushort)AppTrail.MemoryTrim => "memory.trim level=" + I(e.A) + " freedBytes=" + I(e.B),
                (ushort)AppTrail.KeyPathEnter => "keypath.enter" + (e.A == 2 ? " decrypt" : ""),
                (ushort)AppTrail.KeyPathLeave => "keypath.leave outcome=" + (e.A switch { 0 => "ok", 1 => "nokey", 2 => "threw", 3 => "cancelled", _ => I(e.A) }) + " ms=" + I(e.B),
                (ushort)AppTrail.ShutdownPhase => "shutdown phase=" + (Enum.IsDefined((ShutdownPhase)(int)e.A) ? ((ShutdownPhase)(int)e.A).ToString() : I(e.A)),
                (ushort)AppTrail.KeyPathBreakerOpen => "keypath.breaker open faults=" + I(e.A),
                (ushort)AppTrail.KeyPathFault => "keypath.fault recorded",
                (ushort)AppTrail.CrashHandler => "crash.handler shared=" + I(e.A),
                _ => "event" + e.Code.ToString(CultureInfo.InvariantCulture) + " a=" + I(e.A) + " b=" + I(e.B) + (e.Text.Length > 0 ? " " + e.Text : ""),
            };
        }

        // ── the report sections ─────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The faulting thread's region (when <paramref name="faultThreadId"/> is in one), the other threads' regions,
        /// then the trail, oldest first, timed in seconds before <paramref name="nowTickMs"/>. Writes nothing for a null
        /// snapshot. Returns the faulting thread's region name ("" when none) for the summary.</summary>
        public static string AppendSections(StringBuilder sb, CrashTrail.Snapshot? snap, long nowTickMs, uint faultThreadId, string faultLabel = "faulting thread")
        {
            if (snap is null) return "";
            string faultRegion = "";
            if (snap.Regions.Length > 0)
            {
                sb.Append("\nNative regions\n--------------\n");
                foreach (var r in snap.Regions)
                {
                    bool faulting = faultThreadId != 0 && r.ThreadId == faultThreadId;
                    if (faulting) faultRegion = RegionName(r.Kind) + (r.Text.Length > 0 ? ":" + r.Text : "");
                    sb.Append(faulting ? faultLabel : "thread").Append(' ').Append(r.ThreadId.ToString(CultureInfo.InvariantCulture))
                      .Append(": ").Append(Describe(r, nowTickMs)).Append('\n');
                }
            }
            if (faultThreadId != 0 && faultRegion.Length == 0)
                sb.Append(snap.Regions.Length > 0 ? "" : "\n").Append("region=none (the ").Append(faultLabel).Append(" was in no labelled native call)\n");
            var entries = snap.Entries;
            int start = Math.Max(0, entries.Length - ReportEntries);
            sb.Append("\nEvent trail (").Append((entries.Length - start).ToString(CultureInfo.InvariantCulture))
              .Append(" events, oldest first; seconds before this report)\n-----------\n");
            for (int i = start; i < entries.Length; i++)
            {
                double ago = (entries[i].TickMs - nowTickMs) / 1000.0;
                sb.Append(ago.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(10)).Append("  ").Append(Describe(entries[i])).Append('\n');
            }
            return faultRegion;
        }
    }

    // ── the machine ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Machine context for every report: the GPU driver version (from the engine), a VM flag and the third-party
    /// module names.</summary>
    public static partial class Machine
    {
        /// <summary>At most this many third-party module names ride in a report.</summary>
        public const int MaxModules = 40;

        // SMBIOS manufacturer / product strings of the common hypervisors (matched case-insensitively). Only the token from
        // THIS list is ever reported, never the firmware text itself.
        static readonly string[] s_vmTokens =
        [
            "VMware", "VirtualBox", "innotek", "QEMU", "KVM", "Virtual Machine", "Hyper-V", "Parallels", "Xen", "Bochs",
            "Amazon EC2", "Google Compute Engine", "OpenStack",
        ];

        static (bool Vm, string Hint)? s_vm;

        /// <summary>Whether the firmware says this is a virtual machine, and which known hypervisor token matched. Cached.</summary>
        public static unsafe (bool Vm, string Hint) VirtualMachine()
        {
            if (s_vm is { } cached) return cached;
            (bool, string) result = (false, "");
            try
            {
                const uint Rsmb = 0x52534D42;   // 'RSMB'
                uint size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
                if (size > 0 && size < 1 << 20)
                {
                    byte[] table = new byte[size];
                    fixed (byte* p = table) size = GetSystemFirmwareTable(Rsmb, 0, p, size);
                    string text = Encoding.ASCII.GetString(table, 0, (int)Math.Min(size, (uint)table.Length));
                    foreach (string token in s_vmTokens)
                        if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) { result = (true, token); break; }
                }
            }
            catch (Exception) { }
            s_vm = result;
            return result;
        }

        /// <summary>The file names (never paths) of the modules in <paramref name="paths"/> that are neither Windows' (under
        /// <paramref name="windowsDir"/>, or a Microsoft framework package), the .NET runtime's, nor Wavee's own (under
        /// <paramref name="appDir"/>): overlays, hooks and injectors. Sorted, distinct, sanitized to
        /// <c>[A-Za-z0-9._-]</c> (a name that is not is dropped), at most <see cref="MaxModules"/>. PURE.</summary>
        public static string[] ThirdParty(IEnumerable<string> paths, string appDir, string windowsDir)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            string app = WithSlash(appDir), win = WithSlash(windowsDir);
            foreach (string raw in paths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string path = raw.StartsWith(@"\\?\", StringComparison.Ordinal) ? raw[4..] : raw;
                if (app.Length > 1 && path.StartsWith(app, StringComparison.OrdinalIgnoreCase)) continue;
                if (win.Length > 1 && path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains(@"\WindowsApps\Microsoft.", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Contains(@"\dotnet\shared\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\dotnet\host\", StringComparison.OrdinalIgnoreCase)) continue;   // the .NET runtime (a framework-dependent Debug build)
                string name = Path.GetFileName(path);
                if (name.Length is 0 or > 64 || !IsSafeName(name)) continue;
                names.Add(name);
                if (names.Count >= MaxModules) break;
            }
            return [.. names];
        }

        static string WithSlash(string dir) => string.IsNullOrEmpty(dir) ? "" : dir.EndsWith('\\') ? dir : dir + "\\";

        static bool IsSafeName(string name)
        {
            foreach (char c in name)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
            return true;
        }

        /// <summary>This process's third-party modules.</summary>
        public static string[] ThirdPartyOfThisProcess()
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                var paths = new List<string>();
                foreach (System.Diagnostics.ProcessModule m in proc.Modules) paths.Add(m.FileName ?? "");
                return ThirdParty(paths, AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            }
            catch (Exception) { return []; }
        }

        /// <summary>The report.txt "Machine" section.</summary>
        public static void AppendSection(StringBuilder sb, Summary s)
        {
            sb.Append("\nMachine\n-------\n")
              // "gpuTier", "driver version": the upload redactor reads a bare "tier=" as an account fact, and a dotted quad as
              // an IPv4 address unless "version=" precedes it.
              .Append("gpu=").Append(s.Gpu.Length > 0 ? s.Gpu : "unknown").Append(" gpuTier=").Append(s.GpuTier)
              .Append(" softwareAdapter=").Append(s.SoftwareAdapter ? '1' : '0').Append('\n')
              .Append("driver version=").Append(string.IsNullOrEmpty(s.GpuDriver) ? "unknown" : s.GpuDriver).Append('\n')
              .Append("virtualMachine=").Append(s.VirtualMachine == true ? '1' : '0');
            if (!string.IsNullOrEmpty(s.VmHint)) sb.Append(" vmHint=").Append(s.VmHint);
            sb.Append('\n').Append("thirdPartyModules=");
            if (s.ThirdPartyModules is { Length: > 0 } mods) sb.AppendJoin(", ", mods); else sb.Append("none");
            sb.Append('\n');
        }

        [LibraryImport("kernel32.dll")]
        private static unsafe partial uint GetSystemFirmwareTable(uint provider, uint tableId, byte* buffer, uint size);
    }
}
