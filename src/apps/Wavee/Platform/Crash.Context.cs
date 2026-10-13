using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee;

public static partial class Crash
{
    /// <summary>Parent-owned metadata, sent over stdin. TickCount64 has the same basis in both processes;
    /// an exit report can therefore measure parent uptime without starting the child's app or GPU services.</summary>
    public sealed record ParentContext(int Pid, long Sequence, long StartedTickMs, Summary Metadata,
        string ModuleName, string Framework, string Os, string BuildDate);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(ParentContext))]
    public sealed partial class ContextJson : JsonSerializerContext { }

    public static class ContextProtocol
    {
        public const int MaxLineChars = 16_384;

        public static string Format(ParentContext context) =>
            "C " + JsonSerializer.Serialize(context, ContextJson.Default.ParentContext);

        /// <summary>Both F and a validated context can establish the first frame; it can never become unseen again.</summary>
        public static bool FirstFrameSeen(bool previouslySeen, ParentContext? context) =>
            previouslySeen || context?.Metadata.BeforeFirstFrame == false;

        /// <summary>Invalid, out-of-order, foreign-parent or different-session context cannot replace the last good one.</summary>
        public static bool TryRead(string line, int parentPid, ParentContext? previous, out ParentContext? context)
        {
            context = previous;
            if (!line.StartsWith("C ", StringComparison.Ordinal) || line.Length > MaxLineChars) return false;
            try
            {
                var next = JsonSerializer.Deserialize(line.AsSpan(2), ContextJson.Default.ParentContext);
                if (next is null || next.Pid != parentPid || next.Pid <= 0 || next.Sequence <= 0
                    || next.StartedTickMs < 0 || next.Metadata is null || string.IsNullOrEmpty(next.Metadata.SessionId)
                    || next.Metadata.Rvas is null || next.Metadata.Version is null || next.Metadata.Quad is null
                    || next.Metadata.Commit is null || next.Metadata.Channel is null || next.Metadata.Arch is null
                    || next.Metadata.OsBuild is null || next.Metadata.Gpu is null || next.Metadata.GpuTier is null
                    || next.Metadata.Locale is null || next.Metadata.LastRoute is null || next.Metadata.DebugId is null
                    || next.ModuleName is null || next.Framework is null || next.Os is null || next.BuildDate is null) return false;
                if (previous is not null && (next.Sequence <= previous.Sequence
                    || next.StartedTickMs != previous.StartedTickMs
                    || next.Metadata.SessionId != previous.Metadata.SessionId)) return false;
                context = next;
                return true;
            }
            catch (JsonException) { return false; }
        }

        /// <summary>Bound memory while draining an oversized line; its next valid protocol line remains readable.</summary>
        public static string? ReadLine(TextReader reader, out bool oversized)
        {
            var line = new System.Text.StringBuilder();
            oversized = false;
            int c;
            while ((c = reader.Read()) >= 0 && c != '\n')
            {
                if (line.Length < MaxLineChars) line.Append((char)c);
                else oversized = true;
            }
            if (c < 0 && line.Length == 0 && !oversized) return null;
            if (line.Length > 0 && line[^1] == '\r') line.Length--;
            return line.ToString();
        }

        public static Summary BuildSummary(ParentContext? context, int parentPid, int reporterPid,
            Kind kind, int exitCode, string message, bool beforeFirstFrame, long nowTickMs, DateTimeOffset stamp)
        {
            // A missing handshake means unknown parent metadata. Never replace it with the child's process state.
            var metadata = context?.Metadata ?? new Summary("", "", kind, "", "", "", "", "", "", "",
                "", "", false, false, "", "", 0, true, "", "", "", [], 0, 0, "", 0, false, 0);
            return metadata with
            {
                ReportId = Guid.NewGuid().ToString("N"), Kind = kind,
                StampUtc = stamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                UptimeMs = context is null ? 0 : Math.Max(0, nowTickMs - context.StartedTickMs),
                BeforeFirstFrame = beforeFirstFrame, ExceptionType = kind.ToString(), ExceptionMessage = message,
                Rvas = [], ExitCode = exitCode, HasDump = false, DumpBytes = 0, ExceptionCode = 0,
                FaultModule = "", FaultOffset = 0, ParentPid = parentPid, ReporterPid = reporterPid,
                DumpLocalCreated = null, DumpIncluded = null, DumpOmissionReason = null,
            };
        }
    }

    /// <summary>Ordered, bounded shutdown checkpoints. Numeric IDs survive redaction; names are code-owned.</summary>
    public enum ShutdownPhase
    {
        ShellCleanup = 10, Tray = 11, SessionFlush = 12, OsPlayback = 13, Evidence = 14, AiLyrics = 15, ShellDone = 16,
        LoopReturned = 20, Library = 21, Sidebar = 22, Telemetry = 23, Audio = 24, AudioKeys = 25,
        Video = 26, Modules = 27, Store = 28, DiskCache = 29, Fetcher = 30, UpdateApply = 31,
        UpdateStop = 32, Diagnostics = 33, Platform = 34,
    }

    public static class CleanupDiagnostics
    {
        /// <summary>Unexpected cleanup errors still propagate on healthy exit. During exception unwinding the first
        /// failure remains authoritative, while the cleanup failure is retained as a secondary diagnostic.</summary>
        public static void Run(Exception? primaryFailure, Action cleanup, Action<Exception> reportSecondary)
        {
            try { cleanup(); }
            catch (Exception secondary) when (primaryFailure is not null)
            {
                try { reportSecondary(secondary); } catch { }
            }
        }
    }

    /// <summary>Filter before truncation. Continuations and structural drop markers follow their owning entry;
    /// a pid-looking token in an entry's message never changes ownership.</summary>
    public sealed class ParentTailFilter(int parentPid, string? sessionId = null)
    {
        bool _parentEntry;

        public bool Keep(string line)
        {
            if (line.StartsWith("seq=", StringComparison.Ordinal))
            {
                _parentEntry = false;
                string? sid = null;
                foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.StartsWith("pid=", StringComparison.Ordinal))
                    {
                        _parentEntry = int.TryParse(token.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                            && pid == parentPid && (string.IsNullOrEmpty(sessionId) || sid == sessionId);
                        break;
                    }
                    if (token.StartsWith("sid=", StringComparison.Ordinal)) sid = token[4..];
                    if (!token.StartsWith("seq=", StringComparison.Ordinal) && !token.StartsWith("tid=", StringComparison.Ordinal)
                        && !token.StartsWith("t=", StringComparison.Ordinal) && !token.StartsWith("sid=", StringComparison.Ordinal)) break;
                }
            }
            return _parentEntry;
        }
    }
}
