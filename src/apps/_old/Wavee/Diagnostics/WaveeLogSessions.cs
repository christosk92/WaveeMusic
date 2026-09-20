using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Wavee;

/// <summary>
/// Discovers and loads PAST app sessions from the rolling wavee.log file set — the in-memory ring only ever holds the
/// current run. A session opens at the "Wavee starting" startup line (with a sequence-number reset as the fallback,
/// since seq restarts at 1 each launch) and runs until the next one; a session may span a file roll, so the rolled
/// files + the live file are walked as one chronological stream. File lines carry a <c>seq= tid= [t=] </c> prefix —
/// <c>t=</c> (unix ms) was added later, so entries written by older builds parse with <c>UnixMs = 0</c> (shown
/// timestamp-less). Parsing is LOSSY by design: the formatted remainder becomes the Message verbatim (event id,
/// fields and exception text stay embedded in it), which is exactly what the log viewer renders anyway.
/// </summary>
static class WaveeLogSessions
{
    /// <summary>One past session: a [start, end) line range over the chronological file list.</summary>
    public sealed record Info(string[] Files, int FileStart, int LineStart, int FileEnd, int LineEnd,
                              long StartUnixMs, int Pid, int EntryCount, string SessionId);

    /// <summary>All completed sessions found on disk, NEWEST first. The trailing session belonging to
    /// <paramref name="currentPid"/> (this very run) is excluded — the live ring is the richer source for it.</summary>
    public static List<Info> ListPastSessions(string? liveFilePath, int currentPid)
    {
        var sessions = new List<Info>();
        try
        {
            if (liveFilePath is null) return sessions;
            string? dir = Path.GetDirectoryName(liveFilePath);
            if (dir is null || !Directory.Exists(dir)) return sessions;

            string root = Path.GetFileNameWithoutExtension(liveFilePath);
            string ext = Path.GetExtension(liveFilePath);
            string[] rolled = Directory.GetFiles(dir, root + "-*" + ext);
            Array.Sort(rolled, StringComparer.Ordinal);   // wavee-yyyyMMdd-HHmmss names sort chronologically
            var fileList = new List<string>(rolled.Length + 1);
            fileList.AddRange(rolled);
            if (File.Exists(liveFilePath)) fileList.Add(liveFilePath);
            if (fileList.Count == 0) return sessions;
            string[] files = fileList.ToArray();

            long prevSeq = long.MaxValue;
            string prevSid = "";
            bool open = false;
            int curFile = 0, curLine = 0, curPid = 0, curCount = 0;
            long curStart = 0;
            string curSid = "";

            void Close(int endFile, int endLine)
            {
                if (open) sessions.Add(new Info(files, curFile, curLine, endFile, endLine, curStart, curPid, curCount, curSid));
                open = false;
            }

            for (int fi = 0; fi < files.Length; fi++)
            {
                int li = 0;
                // One unreadable/mid-read-faulting file skips only that file: the walk continues to the next, and the
                // open-session state machine (prevSeq/prevSid/open + the current-session cursor) carries across the gap
                // unchanged — the boundary logic tolerates a partially-read file.
                try
                {
                    foreach (var line in ReadSharedLines(files[fi]))
                    {
                        if (TryParseLine(line, out var e, out bool isStart, out int pid, out string sid))
                        {
                            // A session opens when the run identity changes: sid differs from the previous line (new build),
                            // else the legacy heuristic (a "Wavee starting" marker or a seq reset) for sid-less lines.
                            bool boundary = sid.Length > 0
                                ? sid != prevSid
                                : isStart || e.Sequence < prevSeq;
                            if (boundary)
                            {
                                Close(fi, li);
                                open = true;
                                curFile = fi; curLine = li; curStart = e.UnixMs; curPid = pid; curCount = 0; curSid = sid;
                            }
                            prevSeq = e.Sequence;
                            prevSid = sid;
                            if (open) curCount++;
                        }
                        li++;
                    }
                }
                catch { continue; }
            }
            Close(files.Length - 1, int.MaxValue);

            // The trailing session is this very process (it appended its own startup line) — the ring shows it live.
            // Prefer the sid match (exact); fall back to pid for legacy sid-less files.
            if (sessions.Count > 0)
            {
                var last = sessions[^1];
                bool isCurrent = last.SessionId.Length > 0
                    ? last.SessionId == WaveeLog.SessionId
                    : last.Pid == currentPid;
                if (isCurrent) sessions.RemoveAt(sessions.Count - 1);
            }
            sessions.Reverse();   // newest first for the picker
        }
        catch { /* diagnostics must never throw into the UI */ }
        return sessions;
    }

    /// <summary>Parse one session's lines back into entries (ring-parity cap: the LAST <paramref name="maxEntries"/>).</summary>
    public static WaveeLogEntry[] LoadSession(Info info, int maxEntries = 4096)
    {
        var list = new List<WaveeLogEntry>(1024);
        try
        {
            for (int fi = info.FileStart; fi <= info.FileEnd && fi < info.Files.Length; fi++)
            {
                int from = fi == info.FileStart ? info.LineStart : 0;
                int to = fi == info.FileEnd ? info.LineEnd : int.MaxValue;
                int li = 0;
                try
                {
                    foreach (var line in ReadSharedLines(info.Files[fi]))
                    {
                        if (li >= to) break;
                        if (li >= from && TryParseLine(line, out var e, out _, out _, out _)) list.Add(e);
                        li++;
                    }
                }
                catch { continue; }
            }
        }
        catch { }
        if (list.Count > maxEntries) list.RemoveRange(0, list.Count - maxEntries);
        return list.ToArray();
    }

    /// <summary>Read the raw log lines for a past session (same file walk as <see cref="LoadSession"/>).</summary>
    public static List<string> ReadSessionRawLines(Info info)
    {
        var lines = new List<string>(Math.Max(info.EntryCount, 16));
        try
        {
            for (int fi = info.FileStart; fi <= info.FileEnd && fi < info.Files.Length; fi++)
            {
                int from = fi == info.FileStart ? info.LineStart : 0;
                int to = fi == info.FileEnd ? info.LineEnd : int.MaxValue;
                int li = 0;
                try
                {
                    foreach (var line in ReadSharedLines(info.Files[fi]))
                    {
                        if (li >= to) break;
                        if (li >= from) lines.Add(line);
                        li++;
                    }
                }
                catch { continue; }
            }
        }
        catch { }
        return lines;
    }

    /// <summary>Write a past session's raw log lines to disk.</summary>
    public static void ExportSessionToFile(Info info, string path) =>
        File.WriteAllLines(path, ReadSessionRawLines(info));

    // "[ISO-UTC ]seq=N tid=M [t=U] L [category] rest" → a lossy WaveeLogEntry (rest becomes Message verbatim).
    // Older builds prefixed every line with "yyyy-MM-dd HH:mm:ss.fffZ "; current builds carry t= (unix ms) instead —
    // both shapes yield a timestamp, and a line with neither parses with UnixMs = 0.
    static bool TryParseLine(string line, out WaveeLogEntry entry, out bool isStartMarker, out int pid, out string sid)
    {
        entry = default;
        isStartMarker = false;
        pid = 0;
        sid = "";

        int at = 0;
        long isoMs = 0;
        if (!line.StartsWith("seq=", StringComparison.Ordinal))
        {
            int idx = line.IndexOf(" seq=", StringComparison.Ordinal);
            if (idx <= 0 || idx > 33 || !char.IsAsciiDigit(line[0])) return false;
            if (!DateTimeOffset.TryParse(line[..idx], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)) return false;
            isoMs = ts.ToUnixTimeMilliseconds();
            at = idx + 1;
        }
        if (!Expect(line, ref at, "seq=") || !ReadLong(line, ref at, out long seq)) return false;
        if (!Expect(line, ref at, " tid=") || !ReadLong(line, ref at, out long tid)) return false;
        long unixMs = isoMs;
        if (Expect(line, ref at, " t=") && !ReadLong(line, ref at, out unixMs)) return false;
        // Optional sid= (8 hex) + pid= tokens (added later); absent in older files.
        if (Expect(line, ref at, " sid=")) sid = ReadToken(line, ref at);
        if (Expect(line, ref at, " pid=") && ReadLong(line, ref at, out long pidTok)) pid = (int)Math.Min(pidTok, int.MaxValue);
        if (at + 3 >= line.Length || line[at] != ' ') return false;

        WaveeLogLevel level;
        switch (line[at + 1])
        {
            case 'T': level = WaveeLogLevel.Trace; break;
            case 'D': level = WaveeLogLevel.Debug; break;
            case 'I': level = WaveeLogLevel.Info; break;
            case 'W': level = WaveeLogLevel.Warning; break;
            case 'E': level = WaveeLogLevel.Error; break;
            case 'C': level = WaveeLogLevel.Critical; break;
            default: return false;
        }
        if (line[at + 2] != ' ' || line[at + 3] != '[') return false;

        int catStart = at + 4;
        int catEnd = line.IndexOf(']', catStart);
        if (catEnd < 0) return false;
        string category = line[catStart..catEnd];
        int restAt = catEnd + 1;
        if (restAt < line.Length && line[restAt] == ' ') restAt++;
        string rest = restAt < line.Length ? line[restAt..] : "";

        if (category == "app" && rest.StartsWith("startup - Wavee starting", StringComparison.Ordinal))
        {
            isStartMarker = true;
            if (pid == 0)   // no pid= prefix token (legacy line) → recover it from the message body
            {
                int p = rest.IndexOf(" pid=", StringComparison.Ordinal);
                if (p >= 0)
                {
                    long v = 0;
                    for (int q = p + 5; q < rest.Length && char.IsAsciiDigit(rest[q]); q++) v = v * 10 + (rest[q] - '0');
                    pid = (int)Math.Min(v, int.MaxValue);
                }
            }
        }

        entry = new WaveeLogEntry(seq, unixMs, level, category, "", rest, null, (int)tid, -1, null, null);
        return true;
    }

    // Read a (possibly still-being-written) log file line-by-line with a share mode matching WaveeLog's persistent append
    // sink (FileShare.ReadWrite | Delete). File.ReadLines opens with the default FileShare.Read, which throws a sharing
    // violation against the LIVE file the writer holds open — the fault that discarded the whole session list. Like
    // File.ReadLines this is lazy (the open happens on the first MoveNext, INSIDE the caller's foreach), so every caller
    // wraps the enumeration in try/catch to cover both the open and the read loop.
    static IEnumerable<string> ReadSharedLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    // Match a literal at `at`: advance past it and return true, or leave `at` untouched and return false.
    static bool Expect(string s, ref int at, string token)
    {
        if (at + token.Length > s.Length) return false;
        for (int i = 0; i < token.Length; i++)
            if (s[at + i] != token[i]) return false;
        at += token.Length;
        return true;
    }

    // Read a run of non-space chars from `at` (used for the sid= hex token). Advances past it.
    static string ReadToken(string s, ref int at)
    {
        int start = at;
        while (at < s.Length && s[at] != ' ') at++;
        return s[start..at];
    }

    static bool ReadLong(string s, ref int at, out long value)
    {
        value = 0;
        int start = at;
        while (at < s.Length && char.IsAsciiDigit(s[at]))
        {
            value = value * 10 + (s[at] - '0');
            at++;
        }
        return at > start;
    }
}
