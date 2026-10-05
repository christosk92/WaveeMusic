// ── Platform/Log.Files.cs ──────────────────────────────────────────────────────────────────────────────────────────
// the app log's FILE rules, as pure classes: LogFileNames (the one naming authority), LogRetentionPolicy (age + bytes),
// FileQueuePolicy (the sink queue's overflow and the drop marker), TailAssembler (a crash tail = file lines + the
// still-queued lines), MemorySamplePolicy (when a mem.sample is a warning)
//
// Role: CORE (engine-free)
// Owner: G (privacy & diagnostics plan §2.3, G1/G3/G5/G6/G7)
// Spec: docs/plans/wavee/privacy-diagnostics-tab-implementation.md §2
//
// WHY THESE ARE CLASSES. Every rule below used to live inside `Platform.Host.cs`'s writer (a private roll name, a
// newest-seven-files prune, a marker written as a bare string) or as a second copy of a glob in the lister, the deleter
// and the Storage census — four places that had to agree and did not (a "Delete old logs" that deleted the file being
// written; a retention that kept ~1.3 days; a drop marker nothing could parse back). The writer (`Platform.Host.cs`),
// the lister (`WaveeLogSessions.ListPastSessions`), the deleter and the census (`Settings.Host.cs`), the crash tail
// (`Crash.Bundles.cs`, in BOTH the app and the crash-handler child) all call THIS file, and `LogFilesTests` pins it.

using System.Globalization;

namespace Wavee;

/// <summary>THE naming authority for the app log's files. A <c>basePath</c> is the CONFIGURED path
/// (<c>Log.BasePath</c>, <c>...\logs\wavee.log</c>); the files derived from it are:
/// <list type="bullet">
/// <item><c>wavee-yyyyMMdd.log</c> — the DAILY file, one per local calendar day; today's is the one being written.</item>
/// <item><c>wavee-yyyyMMdd-HHmmss.log</c> — a SIZE ROLL: the daily file renamed at the size limit, stamped with the
/// LOCAL time of the roll (<c>-2</c>, <c>-3</c>… appended when two rolls land in one second). The same shape is the
/// 0.2.9 single-file migration (<c>wavee.log</c> stamped with its last write).</item>
/// <item><c>wavee-yyyyMMdd-yyyyMMdd-HHmmss.log</c> — the LEGACY roll of 0.3 builds before 2026-10-02 (the active file's
/// local day, then a UTC stamp). The writer no longer makes it, but files already on disk are still app logs, so they
/// are listed, retained and deleted like any other.</item>
/// </list>
/// Ordinal order is chronological, and a size roll sorts BEFORE the day file it was cut from: <c>'-'</c> (0x2D) sorts
/// before <c>'.'</c> (0x2E), so <c>wavee-20261002-163142.log</c> &lt; <c>wavee-20261002.log</c>. That invariant is what
/// lets <see cref="WaveeLogSessions.Chronological"/> be a plain ordinal sort.</summary>
public static class LogFileNames
{
    const string DayFormat = "yyyyMMdd";
    const string StampFormat = "yyyyMMdd-HHmmss";

    static string Root(string basePath) => Path.GetFileNameWithoutExtension(basePath);

    static string Dir(string basePath) => Path.GetDirectoryName(basePath) ?? "";

    /// <summary>The daily file for <paramref name="localDay"/>, as a full path beside <paramref name="basePath"/>.</summary>
    public static string Dated(string basePath, DateOnly localDay)
        => Path.Combine(Dir(basePath),
            Root(basePath) + "-" + localDay.ToString(DayFormat, CultureInfo.InvariantCulture) + Path.GetExtension(basePath));

    /// <summary>The size-roll name for a roll at <paramref name="localNow"/> (the caller passes <c>DateTime.Now</c>: LOCAL
    /// time, one date). <paramref name="collision"/> 0 is the plain name; 1 → <c>-2</c>, 2 → <c>-3</c> … — the writer
    /// moves with <c>overwrite: false</c> and bumps this when the name is taken, so a same-second roll never destroys one.</summary>
    public static string Rolled(string basePath, DateTime localNow, int collision = 0)
        => Path.Combine(Dir(basePath),
            Root(basePath) + "-" + localNow.ToString(StampFormat, CultureInfo.InvariantCulture)
            + (collision > 0 ? "-" + (collision + 1).ToString(CultureInfo.InvariantCulture) : "") + Path.GetExtension(basePath));

    /// <summary>The directory glob that finds every app log (a superset — filter the hits with <see cref="IsAppLog"/>).</summary>
    public static string Glob(string basePath) => Root(basePath) + "-*" + Path.GetExtension(basePath);

    /// <summary>True for exactly the three shapes above (the legacy double-dated roll included, on purpose), by FILE NAME
    /// — a full path is reduced to its name. A <c>.csv</c> capture, a <c>wavee-report-*.txt</c>, a crash txt, a
    /// <c>.log.bak</c> and a hand-copied <c>wavee-20261002 - Copy.log</c> are not app logs.</summary>
    public static bool IsAppLog(string fileName, string basePath)
    {
        string name = Path.GetFileName(fileName);
        string root = Root(basePath), ext = Path.GetExtension(basePath);
        if (name.Length <= root.Length + 1 + ext.Length) return false;
        if (!name.StartsWith(root, StringComparison.OrdinalIgnoreCase) || name[root.Length] != '-'
            || !name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;

        ReadOnlySpan<char> m = name.AsSpan(root.Length + 1, name.Length - root.Length - 1 - ext.Length);
        return m.Length switch
        {
            8 => IsDay(m),                                                                                   // yyyyMMdd
            15 => IsDay(m[..8]) && m[8] == '-' && IsDigits(m[9..]),                                          // yyyyMMdd-HHmmss
            24 => IsDay(m[..8]) && m[8] == '-' && IsDay(m[9..17]) && m[17] == '-' && IsDigits(m[18..]),      // legacy double-dated
            > 16 => IsDay(m[..8]) && m[8] == '-' && IsDigits(m[9..15]) && m[15] == '-' && IsDigits(m[16..]), // roll collision -n
            _ => false,
        };
    }

    /// <summary>True only for <paramref name="today"/>'s daily file — the one the writer appends to. A size roll, even
    /// today's, is finished and is not active.</summary>
    public static bool IsActive(string fileName, string basePath, DateOnly today)
        => string.Equals(Path.GetFileName(fileName), Path.GetFileName(Dated(basePath, today)), StringComparison.OrdinalIgnoreCase);

    /// <summary>The app logs among <paramref name="names"/> (names or paths; the strings come back untouched) — what the
    /// session lister walks and the Storage census counts.</summary>
    public static List<string> AppLogs(IEnumerable<string> names, string basePath)
    {
        var result = new List<string>();
        foreach (string n in names) if (IsAppLog(n, basePath)) result.Add(n);
        return result;
    }

    /// <summary>What "Delete old logs" deletes: every app log except the file being written right now.</summary>
    public static List<string> SweepTargets(IEnumerable<string> names, string basePath, DateOnly today)
    {
        var result = new List<string>();
        foreach (string n in names) if (IsAppLog(n, basePath) && !IsActive(n, basePath, today)) result.Add(n);
        return result;
    }

    /// <summary>The newest app log among <paramref name="names"/> by ordinal file name, or null when there is none.</summary>
    public static string? Newest(IEnumerable<string> names, string basePath)
    {
        string? best = null;
        string bestName = "";
        foreach (string n in names)
        {
            if (!IsAppLog(n, basePath)) continue;
            string name = Path.GetFileName(n);
            if (best is null || string.CompareOrdinal(name, bestName) > 0) { best = n; bestName = name; }
        }
        return best;
    }

    /// <summary>The file a crash bundle's tail reads: today's daily file when it exists, else the newest app log (a crash
    /// seconds after midnight, before the first line of the new day reached disk), else today's name anyway (the tail
    /// reader treats a missing file as an empty tail). <paramref name="existing"/> is the folder's file list.</summary>
    public static string TailPath(string basePath, DateOnly today, IEnumerable<string> existing)
    {
        string dated = Dated(basePath, today);
        string datedName = Path.GetFileName(dated);
        var list = existing as IReadOnlyCollection<string> ?? existing.ToList();
        foreach (string n in list)
            if (string.Equals(Path.GetFileName(n), datedName, StringComparison.OrdinalIgnoreCase)) return dated;
        return Newest(list, basePath) ?? dated;
    }

    static bool IsDigits(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    static bool IsDay(ReadOnlySpan<char> s)
        => s.Length == 8 && IsDigits(s) && DateOnly.TryParseExact(s, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

/// <summary>How long the app log's files are kept: nothing older than <c>maxDays</c>, and — oldest first — enough files
/// removed to bring the total under <c>maxBytes</c>. Replaces "the newest seven files", which at ~60 MB a day was ~1.3 days
/// of history (a long session ate half of it) while the comment promised a week. The file being written is never selected.</summary>
public static class LogRetentionPolicy
{
    public const int DefaultDays = 7;
    public const long DefaultBytes = 250L << 20;

    public readonly record struct Entry(string Path, long Bytes, DateTime LastWriteUtc);

    /// <summary>The paths to delete: first every file older than <paramref name="maxDays"/>, oldest first; then, while the
    /// remaining total (the active file's bytes included) is over <paramref name="maxBytes"/>, the oldest remaining file.
    /// <paramref name="activePath"/> — compared by file name — is never selected, however old or large.</summary>
    public static List<string> Select(IReadOnlyList<Entry> files, DateTime nowUtc, string? activePath,
        int maxDays = DefaultDays, long maxBytes = DefaultBytes)
    {
        var victims = new List<string>();
        DateTime cutoff = nowUtc - TimeSpan.FromDays(Math.Max(1, maxDays));
        var expired = new List<Entry>();
        var kept = new List<Entry>(files.Count);
        long total = 0;
        foreach (var f in files)
        {
            if (!IsActive(f.Path, activePath) && f.LastWriteUtc < cutoff) { expired.Add(f); continue; }
            kept.Add(f);
            total += f.Bytes;
        }
        expired.Sort(OldestFirst);
        foreach (var f in expired) victims.Add(f.Path);

        if (total > maxBytes)
        {
            kept.Sort(OldestFirst);
            foreach (var f in kept)
            {
                if (total <= maxBytes) break;
                if (IsActive(f.Path, activePath)) continue;
                victims.Add(f.Path);
                total -= f.Bytes;
            }
        }
        return victims;
    }

    static bool IsActive(string path, string? activePath)
        => !string.IsNullOrEmpty(activePath)
           && string.Equals(System.IO.Path.GetFileName(path), System.IO.Path.GetFileName(activePath), StringComparison.OrdinalIgnoreCase);

    static int OldestFirst(Entry a, Entry b)
    {
        int c = a.LastWriteUtc.CompareTo(b.LastWriteUtc);
        return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
    }
}

/// <summary>The file sink's queue rules. A bounded queue that drops its OLDEST lines when full, and says so with a real
/// log entry — one that goes through <see cref="Log.FormatFileLine"/>, so the marker carries <c>seq= tid= t= sid= pid=</c>
/// and <see cref="WaveeLogSessions.TryParseLine"/> reads it back (the bare "W [log] …" string it replaces was
/// unparseable, never reached the ring, and lost its count when the write failed).</summary>
public static class FileQueuePolicy
{
    /// <summary>How many of the oldest queued lines to drop to admit ONE more into a queue already holding
    /// <paramref name="queued"/> with room for <paramref name="cap"/>.</summary>
    public static int Admit(int queued, int cap) => queued >= Math.Max(1, cap) ? queued - Math.Max(1, cap) + 1 : 0;

    public static string DropMessage(int count) => "file sink dropped queued lines count=" + count.ToString(CultureInfo.InvariantCulture);

    /// <summary>The marker entry: category "log", Warning. <paramref name="unixMs"/> is the first line it precedes, so
    /// <c>t=</c> stays monotonic down the file.</summary>
    public static WaveeLogEntry DropMarker(long sequence, long unixMs, int threadId, int count)
        => new(sequence, unixMs, WaveeLogLevel.Warning, "log", "", DropMessage(count), null, threadId, -1, null, null);
}

/// <summary>A crash bundle's log tail = the last lines of the day's file, then any line still QUEUED for the file (the
/// sink writes on a pool thread, so the lines nearest the crash are exactly the ones not on disk yet).</summary>
public static class TailAssembler
{
    /// <summary>The file's lines first, then the pending lines the file does not already hold (a line is "already
    /// present" when its <c>sid= seq=</c> pair is — seq restarts per process, so the sid is part of the key), keeping the
    /// last <paramref name="max"/>. Continuation lines (stack traces carry no seq=) never dedupe and are kept as they are.</summary>
    public static string[] Merge(string[] fileTail, string[] pending, int max)
    {
        if (max <= 0) return [];
        if (pending.Length == 0) return fileTail.Length <= max ? fileTail : fileTail[^max..];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in fileTail) if (KeyOf(line) is { } key) seen.Add(key);

        var all = new List<string>(fileTail.Length + pending.Length);
        all.AddRange(fileTail);
        foreach (string line in pending)
        {
            if (KeyOf(line) is { } key && seen.Contains(key)) continue;
            all.Add(line);
        }
        int skip = Math.Max(0, all.Count - max);
        return all.GetRange(skip, all.Count - skip).ToArray();
    }

    /// <summary>"<c>sid</c>:<c>seq</c>" for a file line (<c>seq=17 tid=4 t=… sid=3f9c2a1b pid=… I [app] …</c>), or null for
    /// a continuation line / garbage. A sid-less (0.2.x) line keys on the seq alone.</summary>
    static string? KeyOf(string line)
    {
        if (!line.StartsWith("seq=", StringComparison.Ordinal)) return null;
        int at = 4;
        while (at < line.Length && char.IsAsciiDigit(line[at])) at++;
        if (at == 4) return null;
        string seq = line[4..at];
        int s = line.IndexOf(" sid=", at, StringComparison.Ordinal);
        if (s < 0) return ":" + seq;
        s += 5;
        int e = s;
        while (e < line.Length && line[e] != ' ') e++;
        return line[s..e] + ":" + seq;
    }
}

/// <summary>When a <c>mem.sample</c> is a warning. It used to be every sample once the working set passed 400 MB —
/// 2,232 warnings in one 10 MB roll and a Warnings badge that meant nothing. Now: Warning only on a NEW working-set
/// peak above the threshold (which includes the sample that first crosses it); every other sample is Info.</summary>
public static class MemorySamplePolicy
{
    /// <summary>400 MB: where a player's working set stops being unremarkable.</summary>
    public const long WarnBytes = 400L << 20;

    /// <summary>The periodic sampler's due rule, checked on every frame: nothing before <paramref name="checkMs"/> since the last sample;
    /// then one at <paramref name="steadyMs"/>, or at once when the working set moved by <paramref name="growthBytes"/> either way.</summary>
    public static bool PeriodicDue(double sinceLastMs, long workingSetDeltaBytes, double checkMs = 5000, double steadyMs = 30_000, long growthBytes = 64L << 20)
        => sinceLastMs >= checkMs && (sinceLastMs >= steadyMs || Math.Abs(workingSetDeltaBytes) >= growthBytes);

    public static WaveeLogLevel LevelFor(long workingSet, long previousPeak, long thresholdBytes)
        => workingSet > thresholdBytes && workingSet > previousPeak ? WaveeLogLevel.Warning : WaveeLogLevel.Info;
}
