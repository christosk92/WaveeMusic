// ── Platform/Crash.cs ──────────────────────────────────────────────────────────────────────────────────────────────
// The crash & diagnostics pipeline's CORE (WP-A): the on-disk bundle shapes (Summary/SendRecord, source-generated
// JSON), the pure path arithmetic every writer/reader/pruner shares (Files), the PE debug-directory reader
// (PeDebugId), the hang / consent / recovery decision tables, and the random per-install id. Engine-free — no D3D, no
// window, no process — so every rule here is a fact in `Wavee.Tests/CrashCoreTests.cs`, never an eyeball check.
//
// Role: CORE
// Owner: WP-A
// Wave: crash-diagnostics (post-0.3; independent of the migration waves — nothing here renders)
// Budget: n/a (new file)
// Spec: docs/plans/wavee/crash-diagnostics-implementation.md §B.2, §B.3, §B.7, §B.8, §F ("A · Core"), §I
//
// `public static partial class Crash` is shared with the other work packages' files (`Crash.Handler.cs`,
// `Crash.Host.cs`, `Crash.Scrub.cs`, `Crash.Upload.cs`): this file owns only the pieces §I lists under "WP-A". Nothing
// here opens a pipe, spawns a process or touches a `Signal<T>` — that is the host's job (`Crash.Host.cs`, WP-B).
//
// WHY A STRUCTURAL DEBUG-DIRECTORY READER, NOT A LIBRARY. NativeAOT ships with `StackTraceSupport=false`, so a crash's
// only durable identity is its module + RVA and the PE's own CodeView debug id (the same "GUID-age" a symbol server
// keys on) — `PeDebugId.TryRead` walks the PE header by hand (DOS stub → COFF → optional header → data directory 6 →
// section → IMAGE_DEBUG_DIRECTORY → RSDS) with `BinaryPrimitives` over bounded reads, no unsafe code, and NEVER throws:
// a malformed or truncated stream (a partial download, a stripped exe) degrades to `false`, never an exception the
// caller has to guard.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Wavee;

public static partial class Crash
{
    // ── 1. the bundle shapes (wire + JSON) ──────────────────────────────────────────────────────────────────────────

    /// <summary>How a bundle came to exist.</summary>
    public enum Kind : byte { Managed, Native, Hang, ExitCode, UncleanExit }

    /// <summary>The persisted reporting mode (Settings ▸ Privacy &amp; diagnostics ▸ Crash reports).</summary>
    public enum Reporting : byte { Off = 0, Ask = 1, Auto = 2 }

    /// <summary>A bundle's upload state, as `send.json` records it.</summary>
    public enum SendState : byte { NotSent, Queued, Sent, Failed }

    /// <summary><c>summary.json</c> — the machine-readable half of a bundle. Every field is scrubber-safe EXCEPT
    /// <see cref="ExceptionMessage"/> and <see cref="LastRoute"/> (the scrubber, WP-C, rewrites those before a bundle
    /// ever leaves the outbox); the local copy on disk keeps them as captured because the in-app prompt and the
    /// Reports list show the real thing first.
    /// <para>The three trailing native-fault fields (#165 W3a) are optional constructor parameters ON PURPOSE: a
    /// <c>summary.json</c> written before they existed has no such properties, and the source-generated reader then
    /// takes these defaults instead of failing the bundle. <see cref="ExceptionCode"/> is the NTSTATUS the hook saw
    /// (0 for every non-native kind; a JSON number on the wire); <see cref="FaultModule"/> is the normalized base name
    /// of the module the fault address fell in (<see cref="Crash.FaultModule.Normalize"/>; "" when unresolved) and
    /// <see cref="FaultOffset"/> the address's offset inside it — both filled by the CHILD after the dump
    /// (<c>Crash.Handler</c>, via <see cref="Bundles.UpdateSummary"/>).</para></summary>
    public sealed record Summary(
        string ReportId, string InstallId, Kind Kind, string StampUtc,
        string Version, string Quad, string Commit, string Channel, string Arch, string OsBuild,
        string Gpu, string GpuTier, bool SoftwareAdapter, bool Packaged, string Locale,
        string SessionId, long UptimeMs, bool BeforeFirstFrame, string LastRoute,
        string ExceptionType, string ExceptionMessage, long[] Rvas, long ModuleBase, long ModuleSize,
        string DebugId,               // RSDS GUID-age from the PE debug directory (PeDebugId.TryRead)
        int ExitCode, bool HasDump, long DumpBytes,
        uint ExceptionCode = 0, string FaultModule = "", long FaultOffset = 0);

    /// <summary><c>send.json</c> beside <c>summary.json</c> — the per-bundle send state. Written by the uploader
    /// (WP-C); read by <c>Crash.Host.Bundles()</c> (WP-B) so the Reports list (WP-E) never has to ask the network.</summary>
    public sealed record SendRecord(SendState State, string? SentAtUtc, string? Error, int Attempts, bool DumpIncluded);

    /// <summary>The one AOT-safe (reflection-free) JSON contract every bundle read/write goes through. camelCase on
    /// the wire — matching the Worker's `POST /v1/report` multipart <c>summary</c> part (§I, "Worker ingest contract")
    /// — and indented so a hand-opened <c>summary.json</c>/<c>send.json</c> is legible during the manual `cdb` runbook.</summary>
    // UseStringEnumConverter: `kind` travels as "Managed"/"Native"/"Hang"/"ExitCode"/"UncleanExit" — the Worker
    // validates and fingerprints on the string form (ops/crash/worker/src/validate.ts), never on the ordinal.
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(Summary))]
    [JsonSerializable(typeof(SendRecord))]
    public sealed partial class CrashJson : JsonSerializerContext
    {
    }

    /// <summary>One on-disk bundle as the UI sees it: the summary, its send state and the paths a reader/opener needs,
    /// computed once rather than re-joined at every call site.</summary>
    public sealed record BundleInfo(string Dir, Summary Summary, DateTime StampLocal, SendRecord Send, bool HasDump, long DumpBytes)
    {
        public string ReportTxt => Path.Combine(Dir, Files.ReportName);
        public string TailTxt => Path.Combine(Dir, Files.TailName);
        public string? DumpPath => HasDump ? Path.Combine(Dir, Files.DumpName) : null;
    }

    // ── 2. Files — pure path arithmetic; replaces the old Diagnostics.CrashFiles for the new bundle shape ────────────

    /// <summary>Every name and every piece of path arithmetic a bundle writer, reader or pruner needs. Nothing here
    /// touches disk — <c>Crash.Host.cs</c> (WP-B) is the only caller that does.</summary>
    public static class Files
    {
        public const string Folder = "crash", Outbox = "outbox";
        public const string SummaryName = "summary.json", ReportName = "report.txt", TailName = "log-tail.txt",
            DumpName = "minidump.dmp", SendName = "send.json", HandlerLog = "handler.log";

        /// <summary>Keep at most this many bundles regardless of size (a crash loop must not fill the disk).</summary>
        public const int KeepBundles = 10;
        /// <summary>...and never let the kept set exceed this many bytes either (a single fat dump must not starve the
        /// other nine).</summary>
        public const long MaxFolderBytes = 200L << 20;

        const string StampFormat = "yyyyMMdd-HHmmss-fff";

        /// <summary><c>%LOCALAPPDATA%\Wavee\logs\crash</c> (packaged: the LocalCache equivalent) — the parent of every
        /// bundle folder and of <see cref="Outbox"/>.</summary>
        public static string Root(string logFolder) => Path.Combine(logFolder, Folder);

        /// <summary>The queued-for-upload folder, inside <see cref="Root"/>.</summary>
        public static string OutboxDir(string logFolder) => Path.Combine(Root(logFolder), Outbox);

        /// <summary>A bundle folder's name for a local instant: <c>yyyyMMdd-HHmmss-fff-&lt;kind lower&gt;</c>. The
        /// millisecond stamp keeps two crashes a second apart from colliding; the kind suffix is what the Reports list
        /// and the recovery dialog read back without opening <c>summary.json</c> first.</summary>
        public static string BundleName(DateTimeOffset local, Kind kind) =>
            local.ToString(StampFormat, CultureInfo.InvariantCulture) + "-" + KindToken(kind);

        /// <summary>The inverse of <see cref="BundleName"/>. Any other shape — a stray folder, a truncated stamp, an
        /// unknown kind token — fails rather than being guessed at.</summary>
        public static bool TryParse(string folderName, out DateTime stamp, out Kind kind)
        {
            stamp = default;
            kind = default;
            if (string.IsNullOrEmpty(folderName) || folderName.Length <= StampFormat.Length + 1
                || folderName[StampFormat.Length] != '-') return false;
            if (!DateTime.TryParseExact(folderName.AsSpan(0, StampFormat.Length), StampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsedStamp)) return false;
            if (!TryParseKindToken(folderName[(StampFormat.Length + 1)..], out var parsedKind)) return false;
            stamp = parsedStamp;
            kind = parsedKind;
            return true;
        }

        /// <summary>Newest bundle NAME first (the stamp sorts ordinally because it is fixed-width and comes first) —
        /// the order the Reports list shows and the pruner below assumes.</summary>
        public static int NewestFirst(string a, string b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a));

        /// <summary>Which of <paramref name="newestFirst"/> to delete: every bundle beyond <paramref name="keep"/>
        /// (regardless of size), then — among the ones kept by count — the OLDEST first, until what remains is at
        /// most <paramref name="maxBytes"/>. <paramref name="newestFirst"/> must already be sorted newest first (e.g.
        /// by <see cref="NewestFirst"/> over the folder names).</summary>
        public static IReadOnlyList<string> Prune(IReadOnlyList<(string Path, long Bytes)> newestFirst, int keep, long maxBytes)
        {
            var toDelete = new List<string>();
            int n = newestFirst.Count;
            int kept = Math.Clamp(keep, 0, n);

            for (int i = kept; i < n; i++) toDelete.Add(newestFirst[i].Path);

            long total = 0;
            for (int i = 0; i < kept; i++) total += newestFirst[i].Bytes;
            for (int i = kept - 1; i >= 0 && total > maxBytes; i--)
            {
                total -= newestFirst[i].Bytes;
                toDelete.Add(newestFirst[i].Path);
            }
            return toDelete;
        }

        static string KindToken(Kind kind) => kind switch
        {
            Kind.Managed => "managed",
            Kind.Native => "native",
            Kind.Hang => "hang",
            Kind.ExitCode => "exitcode",
            Kind.UncleanExit => "uncleanexit",
            _ => "unknown",
        };

        static bool TryParseKindToken(string token, out Kind kind)
        {
            switch (token)
            {
                case "managed": kind = Kind.Managed; return true;
                case "native": kind = Kind.Native; return true;
                case "hang": kind = Kind.Hang; return true;
                case "exitcode": kind = Kind.ExitCode; return true;
                case "uncleanexit": kind = Kind.UncleanExit; return true;
                default: kind = default; return false;
            }
        }
    }

    // ── 3. PeDebugId — the PE debug directory, read by hand (B.2) ──────────────────────────────────────────────────

    /// <summary>Reads a PE's CodeView (RSDS) debug id straight from its own header — the "GUID-age" every symbol
    /// server, and the WP-G <c>.symmap</c>, already key on. No P/Invoke, no library, no unsafe code: a bounded,
    /// hand-rolled walk of DOS stub → COFF → optional header → data directory 6 (debug) → section →
    /// <c>IMAGE_DEBUG_DIRECTORY</c> → the CodeView entry.</summary>
    public static class PeDebugId
    {
        const uint RsdsSignature = 0x53445352;  // 'R''S''D''S', little-endian read of the 4 ASCII bytes
        const int DebugDirEntrySize = 28;        // sizeof(IMAGE_DEBUG_DIRECTORY)
        const int SectionHeaderSize = 40;        // sizeof(IMAGE_SECTION_HEADER)
        const int DebugDataDirectoryIndex = 6;    // IMAGE_DIRECTORY_ENTRY_DEBUG

        /// <summary>True and the RSDS debug id (upper-case GUID, no braces, "-" + decimal age — e.g.
        /// <c>7E2C1B4A-...-9F0E-1</c>) + the embedded pdb path on success. False on anything malformed or truncated;
        /// this NEVER throws — <paramref name="pe"/> may be an attacker- or corruption-controlled byte stream.</summary>
        public static bool TryRead(Stream pe, out string debugId, out string pdbName)
        {
            debugId = "";
            pdbName = "";
            try
            {
                return TryReadCore(pe, out debugId, out pdbName);
            }
            catch (Exception)
            {
                debugId = "";
                pdbName = "";
                return false;
            }
        }

        static bool TryReadCore(Stream pe, out string debugId, out string pdbName)
        {
            debugId = "";
            pdbName = "";
            if (pe is null || !pe.CanRead) return false;
            if (pe.CanSeek) pe.Seek(0, SeekOrigin.Begin);

            Span<byte> dos = stackalloc byte[64];
            if (!ReadExact(pe, dos)) return false;
            if (dos[0] != (byte)'M' || dos[1] != (byte)'Z') return false;
            int lfanew = BinaryPrimitives.ReadInt32LittleEndian(dos.Slice(0x3C, 4));
            if (lfanew < 0 || !SeekTo(pe, lfanew)) return false;

            Span<byte> peSig = stackalloc byte[4];
            if (!ReadExact(pe, peSig)) return false;
            if (peSig[0] != (byte)'P' || peSig[1] != (byte)'E' || peSig[2] != 0 || peSig[3] != 0) return false;

            Span<byte> coff = stackalloc byte[20];
            if (!ReadExact(pe, coff)) return false;
            ushort numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(coff.Slice(2, 2));
            ushort sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(coff.Slice(16, 2));
            if (sizeOfOptionalHeader < 2 || sizeOfOptionalHeader > 512 || numberOfSections == 0 || numberOfSections > 256) return false;

            byte[] opt = new byte[sizeOfOptionalHeader];
            if (!ReadExact(pe, opt)) return false;
            ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(opt.AsSpan(0, 2));
            bool pe32Plus = magic == 0x20B;
            if (!pe32Plus && magic != 0x10B) return false;

            // From SectionAlignment onward PE32 and PE32+ share offsets (PE32's extra 4-byte BaseOfData exactly
            // compensates its 4-byte-narrower ImageBase); only NumberOfRvaAndSizes/DataDirectory's start differs.
            int numRvaOff = pe32Plus ? 108 : 92;
            int dataDirOff = pe32Plus ? 112 : 96;
            if (opt.Length < numRvaOff + 4) return false;
            uint numberOfRvaAndSizes = BinaryPrimitives.ReadUInt32LittleEndian(opt.AsSpan(numRvaOff, 4));
            if (numberOfRvaAndSizes <= DebugDataDirectoryIndex) return false;

            int debugEntryOff = dataDirOff + DebugDataDirectoryIndex * 8;
            if (opt.Length < debugEntryOff + 8) return false;
            uint debugRva = BinaryPrimitives.ReadUInt32LittleEndian(opt.AsSpan(debugEntryOff, 4));
            uint debugSize = BinaryPrimitives.ReadUInt32LittleEndian(opt.AsSpan(debugEntryOff + 4, 4));
            if (debugRva == 0 || debugSize == 0) return false;

            long? debugFileOffset = null;
            Span<byte> section = stackalloc byte[SectionHeaderSize];
            for (int i = 0; i < numberOfSections; i++)
            {
                if (!ReadExact(pe, section)) return false;
                uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(8, 4));
                uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(12, 4));
                uint sizeOfRawData = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(16, 4));
                uint pointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(20, 4));
                uint span = virtualSize != 0 ? virtualSize : sizeOfRawData;
                if (debugFileOffset is null && debugRva >= virtualAddress && span != 0 && debugRva < virtualAddress + span)
                    debugFileOffset = (long)pointerToRawData + (debugRva - virtualAddress);
            }
            if (debugFileOffset is not { } fileOff || !SeekTo(pe, fileOff)) return false;

            int entryCount = (int)(debugSize / DebugDirEntrySize);
            if (entryCount <= 0 || entryCount > 128) return false;

            // Read every IMAGE_DEBUG_DIRECTORY entry SEQUENTIALLY first (they are contiguous in the file); only THEN
            // seek out to a candidate's CodeView payload — seeking mid-scan would desync the sequential read of the
            // remaining entries on a stream that cannot seek backward.
            const int MinCodeViewRsdsSize = 4 + 16 + 4 + 1;   // signature + guid + age + at least a NUL
            var candidates = new List<uint>(entryCount);      // PointerToRawData of every plausible CODEVIEW entry
            var sizes = new List<uint>(entryCount);
            Span<byte> entry = stackalloc byte[DebugDirEntrySize];
            for (int i = 0; i < entryCount; i++)
            {
                if (!ReadExact(pe, entry)) return false;
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4));
                uint sizeOfData = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(16, 4));
                uint pointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(24, 4));
                if (type != 2 /* IMAGE_DEBUG_TYPE_CODEVIEW */ || sizeOfData < MinCodeViewRsdsSize || sizeOfData > 4096
                    || pointerToRawData == 0) continue;
                candidates.Add(pointerToRawData);
                sizes.Add(sizeOfData);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                if (!SeekTo(pe, candidates[i])) continue;
                byte[] cv = new byte[sizes[i]];
                if (!ReadExact(pe, cv)) continue;
                if (BinaryPrimitives.ReadUInt32LittleEndian(cv.AsSpan(0, 4)) != RsdsSignature) continue;

                var guid = new Guid(cv.AsSpan(4, 16));
                uint age = BinaryPrimitives.ReadUInt32LittleEndian(cv.AsSpan(20, 4));
                int nameStart = 24, nameEnd = nameStart;
                while (nameEnd < cv.Length && cv[nameEnd] != 0) nameEnd++;

                debugId = guid.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "-" + age.ToString(CultureInfo.InvariantCulture);
                pdbName = Encoding.UTF8.GetString(cv, nameStart, nameEnd - nameStart);
                return true;
            }
            return false;
        }

        static bool ReadExact(Stream s, Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = s.Read(buffer[total..]);
                if (n <= 0) return false;
                total += n;
            }
            return true;
        }

        /// <summary>Seeks forward (or absolutely, on a seekable stream) without ever throwing on a truncated/non-seekable
        /// source — a backward seek on a non-seekable stream simply fails.</summary>
        static bool SeekTo(Stream s, long offset)
        {
            if (offset < 0) return false;
            if (s.CanSeek) { s.Seek(offset, SeekOrigin.Begin); return true; }
            if (offset < s.Position) return false;
            Span<byte> scratch = stackalloc byte[256];
            long toSkip = offset - s.Position;
            while (toSkip > 0)
            {
                int chunk = (int)Math.Min(toSkip, scratch.Length);
                int n = s.Read(scratch[..chunk]);
                if (n <= 0) return false;
                toSkip -= n;
            }
            return true;
        }
    }

    // ── 4. the hang watchdog's verdict (B.2 / B.7) ──────────────────────────────────────────────────────────────────

    /// <summary>Hang verdict; the child (WP-B) feeds it its own unbiased clock (`QueryUnbiasedInterruptTime`, immune to
    /// sleep/suspend) plus the parent's last posted beat. Pure so the whole matrix — window missing, debugger, modal,
    /// exiting, a suspend-epoch edge, too short a gap — is one fact table instead of five live repros.</summary>
    public static class HangRules
    {
        public const int NoBeatMs = 20_000, HungWindowMs = 10_000, RecoveredAfterMs = 60_000;

        /// <param name="hungWindowMs10s">Whether `IsHungAppWindow(hwnd)` has read true for at least
        /// <see cref="HungWindowMs"/> continuously — the child's own debounce, handed in already evaluated so this
        /// method stays a pure boolean combination with no clock of its own beyond the two ticks given.</param>
        public static bool IsHung(long nowUnbiasedTicks, long lastBeatUnbiasedTicks, bool hasWindow, bool hungWindowMs10s,
            bool debuggerAttached, bool modalPump, bool exiting, int suspendEpochAtBeat, int suspendEpochNow)
            => hasWindow && !debuggerAttached && !modalPump && !exiting && suspendEpochAtBeat == suspendEpochNow
               && nowUnbiasedTicks - lastBeatUnbiasedTicks >= NoBeatMs * 10_000L && hungWindowMs10s;
    }

    /// <summary>Which inherited runtime-diagnostics settings the crash-handler child must NOT inherit. `dotnet-trace
    /// collect -- Wavee.exe` (and `dotnet-counters`/`dotnet-monitor`) launch the app with
    /// <c>DOTNET_DiagnosticPorts=...,suspend</c>, which makes the runtime of EVERY process that inherits it park at
    /// startup in <c>ds_server_pause_for_diagnostics_monitor</c> until that tool resumes it. The tool attaches to the
    /// parent only, so the child froze before managed code ran, never logged <c>handler.start</c>, never watched the
    /// parent, and outlived it for hours (the "orphaned crash handler"). The handler needs no diagnostics port at all.</summary>
    public static class HandlerEnvRules
    {
        /// <summary>Suffixes of the <c>DOTNET_</c>/<c>COMPlus_</c> variables that suspend or expose the runtime.</summary>
        static readonly string[] s_scrubbed = ["DiagnosticPorts", "DefaultDiagnosticPortSuspend"];

        /// <summary>True for a variable the child must not inherit (case-insensitive, like Windows env names).</summary>
        public static bool ShouldScrub(string name)
        {
            foreach (string prefix in (ReadOnlySpan<string>)["DOTNET_", "COMPlus_"])
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = name[prefix.Length..];
                foreach (string s in s_scrubbed)
                    if (rest.Equals(s, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Name/value the child's runtime is forced to: no diagnostics IPC at all.</summary>
        public const string DisableName = "DOTNET_EnableDiagnostics", DisableValue = "0";

        /// <summary>Rewrites a child's environment in place: scrubs <see cref="ShouldScrub"/> names, disables diagnostics.</summary>
        public static void Apply(IDictionary<string, string?> env)
        {
            foreach (string key in env.Keys.ToArray())
                if (ShouldScrub(key)) env.Remove(key);
            env[DisableName] = DisableValue;
        }
    }

    // ── 5. consent (B.2) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What happens to ONE bundle at launch (or right after a live hang report), given the persisted mode.</summary>
    public static class ConsentPolicy
    {
        public enum Action : byte { Nothing, Prompt, UploadSilently, Toast }

        /// <summary>An <see cref="Kind.UncleanExit"/> bundle (a Task-Manager/OS kill, no evidence) never prompts and
        /// never uploads, in every mode. <see cref="Reporting.Off"/> still shows the LOCAL prompt with a manual Send —
        /// consent Off means "never send without asking", not "never let the user see what happened".</summary>
        public static Action Decide(Reporting mode, Kind kind, bool online)
            => kind == Kind.UncleanExit ? Action.Nothing
             : mode == Reporting.Auto ? (online ? Action.UploadSilently : Action.Toast)
             : Action.Prompt;

        /// <summary>Whether a dump may leave the machine with this send. A manual Send always shows what it sends
        /// first, so it is allowed regardless of mode; otherwise the mode must not be Off, the user must have ticked
        /// "include a memory snapshot", AND the bundle must not be a hang (hang dumps are always ask-first, §B.7).</summary>
        public static bool DumpAllowed(Reporting mode, Kind kind, bool includeDump, bool manualSend)
            => manualSend || (mode != Reporting.Off && includeDump && kind != Kind.Hang);

        static readonly Reporting[] s_canSend = [Reporting.Off, Reporting.Ask, Reporting.Auto];
        static readonly Reporting[] s_cannotSend = [Reporting.Off, Reporting.Ask];

        /// <summary>The modes a consent surface (the setup wizard's card, Settings' mode combo) offers, in display
        /// order. <paramref name="configured"/> is whether THIS build can send at all (<c>Uploader.Configured</c>: ingest
        /// URL, key and quad all stamped); a build that can't send never offers Automatic — nobody opts into an upload
        /// that can never happen (#165 W3a). Off/Ask stay: the local prompt, Copy and the Reports list work without a
        /// service.</summary>
        public static IReadOnlyList<Reporting> Offered(bool configured) => configured ? s_canSend : s_cannotSend;

        /// <summary>The mode the launch decision (<see cref="Decide"/>) actually runs under: a persisted Automatic on a
        /// build that can't send degrades to Ask (the prompt, with its local Copy/Send states) instead of silently doing
        /// nothing; every other mode — and Automatic on a build that can send — is itself.</summary>
        public static Reporting Effective(Reporting mode, bool configured)
            => mode == Reporting.Auto && !configured ? Reporting.Ask : mode;
    }

    // ── 6. recovery (B.2 / B.8) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>When a launch drops into engine-free recovery mode (TaskDialog) instead of the normal boot path.</summary>
    public static class RecoveryPolicy
    {
        public const int BootFailuresForRecovery = 2;

        public static bool Enter(bool recoverySwitch, int consecutiveBootFailures, bool bootThrew)
            => recoverySwitch || bootThrew || consecutiveBootFailures >= BootFailuresForRecovery;

        /// <param name="versionChanged">The previous process was killed by an update deployment — never counts toward
        /// the loop (a version bump killing every open window is not evidence of a boot-time crash).</param>
        public static int NextBootFailures(RunOutcome previous, bool previousReachedFirstFrame, bool versionChanged, int current)
            => previous == RunOutcome.Unclean && !previousReachedFirstFrame && !versionChanged ? current + 1 : 0;
    }

    // ── 7. the per-install id (B.5) ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The random install id sent with every report — never the device id, never anything account-linked.
    /// Generated once and persisted; a factory reset wipes the settings store, so a reset machine is a new install.</summary>
    public static class InstallId
    {
        /// <summary>Read-only: "" until <see cref="Ensure"/> ran (boot, UI thread). The crash path calls THIS, never Ensure.</summary>
        public static string Peek(IAppSettings s)
        {
            try { return s.Get(Platform.Keys.CrashInstallId); } catch { return ""; }
        }

        public static string Ensure(IAppSettings s)
        {
            string id = s.Get(Platform.Keys.CrashInstallId);
            if (id.Length > 0) return id;
            id = Guid.NewGuid().ToString("N");
            s.Set(Platform.Keys.CrashInstallId, id);
            return id;
        }
    }
}
