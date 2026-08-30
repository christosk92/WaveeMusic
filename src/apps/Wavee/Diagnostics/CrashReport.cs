using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The one-file crash artifact: header (version / build stamp / arch / pid / framework / OS / module base), the
/// exception, the frame RVAs, and the tail of the app log.
/// Written from the app-loop catch and the process-wide unhandled handler; the path is stashed in
/// <c>WaveeSettings.PendingCrashReport</c> so the next launch can offer it.
/// <para>
/// A shipped build is NativeAOT with <c>StackTraceSupport=false</c>, so its frames print as
/// <c>Wavee!&lt;BaseAddress&gt;+0x7b1fc6</c> — an offset from the module base and nothing else. The header therefore
/// carries everything a later symbolication needs: the commit + quad (to pick the right
/// <c>Wavee-&lt;quad&gt;-&lt;rid&gt;-symbols.zip</c> off the release) and the module base + size, and the
/// "Frames (RVA)" section repeats each offset one per line so it can be fed to <c>ln Wavee+0x…</c> against the
/// shipped <c>Wavee.pdb</c> (see <c>docs/guide/releasing-wavee.md</c> § Symbolicating a crash report).
/// </para>
/// </summary>
static class CrashReport
{
    /// <summary>How many reports survive a <see cref="Prune"/>. Enough to see a pattern across a few days of a
    /// recurring crash, small enough that a crash loop cannot quietly fill the log folder.</summary>
    public const int DefaultKeep = 10;

    /// <summary>The reports folder — the SAME lowercase <c>logs</c> directory Program.cs configures the app log into, so
    /// "open the report folder" lands on the logs the report quotes rather than a sibling folder Windows created for
    /// a differently-cased path on a case-sensitive volume.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wavee", "logs");

    public static string Write(Exception ex, string? logPath)
    {
        string dir = DefaultDirectory;
        Directory.CreateDirectory(dir);
        string stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(dir, $"crash-report-{stamp}.txt");

        var sb = new StringBuilder(32 * 1024);
        sb.AppendLine("Wavee crash report");
        sb.AppendLine("=================");
        Describe(ex, sb);

        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
        {
            sb.AppendLine();
            sb.AppendLine("wavee.log tail");
            sb.AppendLine("--------------");
            foreach (var line in TailLines(logPath, maxLines: 600))
                sb.AppendLine(line);
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Prune(dir);
        return path;
    }

    /// <summary>The report minus the banner and the log tail — header, exception, frame RVAs — as one string, for a
    /// handler that logs rather than writes a file (the unobserved-task path) so its frames carry the same build stamp
    /// and module base and are just as resolvable later.</summary>
    public static string Describe(Exception ex)
    {
        var sb = new StringBuilder(4 * 1024);
        Describe(ex, sb);
        return sb.ToString();
    }

    static void Describe(Exception ex, StringBuilder sb)
    {
        // version FIRST after the banner: the only question that is always asked of a crash report is "which build?",
        // and a report pasted into an issue is usually truncated after the first few lines. The build stamp lines
        // right after it are what pick the symbols zip; the module line is what turns its offsets into addresses.
        sb.AppendLine("version=" + InformationalVersion());
        var build = BuildStamp();
        sb.AppendLine("commit=" + Or(build?.Commit, "unknown"));
        sb.AppendLine("buildDate=" + Or(build?.BuildDate, "unknown"));
        sb.AppendLine("channel=" + Or(build?.Channel, "unknown"));
        sb.AppendLine("quad=" + Or(build?.Quad, "unknown"));
        sb.AppendLine("arch=" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
        sb.AppendLine("timeLocal=" + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("framework=" + RuntimeInformation.FrameworkDescription);
        sb.AppendLine("os=" + RuntimeInformation.OSDescription);
        sb.AppendLine(MainModuleLine());
        sb.AppendLine();
        sb.AppendLine("Exception");
        sb.AppendLine("---------");
        string exceptionText = ex.ToString();
        sb.AppendLine(exceptionText);

        // The whole ToString(), not ex.StackTrace: an inner exception's frames (an async continuation's real fault)
        // are only in the former, and the section is meant to be pasted into a symbol lookup as-is.
        IReadOnlyList<long> rvas = CrashReportFrames.ParseRvas(exceptionText);
        if (rvas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Frames (RVA)");
            sb.AppendLine("------------");
            sb.AppendLine("# offsets from the module base above, in stack-trace order (innermost first);");
            sb.AppendLine("# resolve each with `ln Wavee+0x<rva>` against the release's Wavee-<quad>-<rid>-symbols.zip");
            foreach (long rva in rvas)
                sb.AppendLine("0x" + rva.ToString("x", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Delete the oldest <c>crash-report-*.txt</c> beyond the newest <paramref name="keep"/>. Ordering is by
    /// FILE NAME, not mtime: the name carries a sortable <c>yyyyMMdd-HHmmss</c> stamp, which stays correct across a copy
    /// or a restore that resets timestamps. Best-effort — a locked or vanished file never fails a crash write.</summary>
    public static void Prune(string dir, int keep = DefaultKeep)
    {
        if (keep < 0) keep = 0;
        string[] files;
        try
        {
            if (!Directory.Exists(dir)) return;
            files = Directory.GetFiles(dir, "crash-report-*.txt", SearchOption.TopDirectoryOnly);
        }
        catch { return; }
        if (files.Length <= keep) return;

        Array.Sort(files, static (a, b) =>
            string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));   // newest name first
        for (int i = keep; i < files.Length; i++)
        {
            try { File.Delete(files[i]); }
            catch { }
        }
    }

    static string Or(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    static string InformationalVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(CrashReport).Assembly;
            if (asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } v)
                return v;
            return asm.GetName().Version?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    /// <summary>The parsed build stamp (commit / build date / channel / quad). <see cref="AppVersion.Info"/> is a
    /// static initializer that has long since run by the time anything can crash, but the report writer must survive
    /// even a failure in there — it is the last thing that runs in a dying process.</summary>
    static WaveeVersionInfo? BuildStamp()
    {
        try { return AppVersion.Info; }
        catch { return null; }
    }

    /// <summary><c>module=&lt;path&gt; base=0x… size=0x…</c> for the main module. The base is what
    /// <c>Wavee!&lt;BaseAddress&gt;+0x…</c> frames are relative to, so a report is symbolicatable even from a machine
    /// that is gone. Best-effort: the process API can refuse (a hardened session, a dying process), and a missing line
    /// must never cost the rest of the report.</summary>
    static string MainModuleLine()
    {
        string path = "unknown";
        try { path = Environment.ProcessPath ?? path; }
        catch { }
        try
        {
            using var proc = Process.GetCurrentProcess();
            var main = proc.MainModule;
            if (main is not null)
            {
                if (!string.IsNullOrEmpty(main.FileName)) path = main.FileName;
                return "module=" + path
                     + " base=0x" + main.BaseAddress.ToInt64().ToString("x", CultureInfo.InvariantCulture)
                     + " size=0x" + main.ModuleMemorySize.ToString("x", CultureInfo.InvariantCulture);
            }
        }
        catch { }
        return "module=" + path + " base=unknown size=unknown";
    }

    static IEnumerable<string> TailLines(string path, int maxLines)
    {
        var q = new Queue<string>(Math.Max(16, maxLines));
        foreach (var line in File.ReadLines(path))
        {
            if (q.Count == maxLines) q.Dequeue();
            q.Enqueue(line);
        }
        return q;
    }
}

/// <summary>
/// The pure half of the crash report's frame section: pulls the <c>+0x…</c> offsets out of a NativeAOT stack trace
/// whose frames print as <c>at Wavee!&lt;BaseAddress&gt;+0x7b1fc6</c> (a shipped build has
/// <c>StackTraceSupport=false</c>, so that is ALL a frame says). Text in, numbers out — no reflection, no regex
/// (hand-scanned so it is NativeAOT-trivial and allocation-light), unit-tested in <c>CrashReportFramesTests</c>.
/// </summary>
static class CrashReportFrames
{
    /// <summary>The frame marker: a module name, a bang, the literal <c>&lt;BaseAddress&gt;</c>, a plus and a hex offset.</summary>
    const string Marker = "!<BaseAddress>+0x";

    /// <summary>Every <c>&lt;module&gt;!&lt;BaseAddress&gt;+0x&lt;hex&gt;</c> offset in <paramref name="stackTrace"/>, in
    /// order of appearance (innermost frame first, duplicates kept — a recursive crash repeats a frame on purpose).
    /// Frames of any other shape (a JIT build's <c>at Type.Method() in file:line</c>, the async boundary
    /// <c>--- End of stack trace from previous location ---</c>) contribute nothing. Never throws; null or empty
    /// input is an empty list.</summary>
    public static IReadOnlyList<long> ParseRvas(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace)) return Array.Empty<long>();
        List<long>? found = null;
        int at = 0;
        while ((at = stackTrace.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0)
        {
            int start = at + Marker.Length;
            int end = start;
            while (end < stackTrace.Length && IsHex(stackTrace[end])) end++;
            at = end;
            // A hex run that long is not an RVA (it would overflow); an empty run is a marker with no offset at all.
            int digits = end - start;
            if (digits == 0 || digits > 15) continue;
            if (!long.TryParse(stackTrace.AsSpan(start, digits), NumberStyles.AllowHexSpecifier,
                               CultureInfo.InvariantCulture, out long rva))
                continue;
            (found ??= new List<long>(16)).Add(rva);
        }
        return found ?? (IReadOnlyList<long>)Array.Empty<long>();
    }

    static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
