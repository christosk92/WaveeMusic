using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wavee;

// The Settings › Logs viewer's decision core — engine-free (System + WaveeLogEntry/WaveeLogField from IWaveeLog.cs
// only) so LogViewTests can pin every filter/group/format rule without standing up FluentGpu. LogsPanel.cs (the
// component) owns every signal and every Loc.Get call; this class owns none of either — CLAUDE.md's "no
// source-text tests" rule means the DECISION lives here, testable directly, rather than being re-derived by a
// test that greps the component's source.

/// <summary>The Segmented level filter — index-compatible with the four-item strip in LogsPanel's filter row.</summary>
public enum LogLevelBucket : byte { All = 0, InfoPlus = 1, Warnings = 2, Errors = 3 }

/// <summary>One rendered row: the log entry that anchors it (the FIRST of a repeated run when grouped) plus how many
/// adjacent identical entries it stands in for (1 = not repeated).</summary>
public readonly record struct LogViewRow(WaveeLogEntry Entry, int Repeat);

/// <summary>The filter/sort/cap state <see cref="LogView.Build"/> applies to one entry span. A plain record so
/// LogsPanel can build one from its signals each render and diff it for the remount Key (<see cref="LogView.RemountKey"/>).</summary>
public sealed record LogViewQuery(
    LogLevelBucket Level = LogLevelBucket.All,
    string? Category = null,
    string Search = "",
    bool NewestFirst = true,
    bool GroupRepeats = true,
    int Cap = LogView.PageRows);

/// <summary>The built view: the rows to render plus the counts the toolbar badges and the footer need. <see cref="Total"/>,
/// <see cref="WarningCount"/> and <see cref="ErrorCount"/> are computed over EVERY entry in the span passed to
/// <see cref="LogView.Build"/> — never the filtered/capped subset — so the badges and the footer's "of N" always
/// describe the whole session, independent of the search box or the level segments.</summary>
public sealed record LogViewResult(LogViewRow[] Rows, int Total, int WarningCount, int ErrorCount, bool Truncated)
{
    public int Shown => Rows.Length;
    public static readonly LogViewResult Empty = new([], 0, 0, 0, false);
}

static class LogView
{
    /// <summary>Default page size (the footer's "Load more" grows the query's Cap by this much) and the hard
    /// ceiling past which "Load more" stops offering (mirrors the old DiagnosticsPanel.MaxVisibleRows).</summary>
    public const int PageRows = 500, MaxRows = 2000;

    /// <summary>The runtime log-level dropdown items, Trace..Error — index == (int)WaveeLogLevel; Critical is
    /// never user-selectable (nothing in the app logs at Critical short of a crash handler, which bypasses this UI).</summary>
    public static readonly string[] LevelNames = ["Trace", "Debug", "Info", "Warning", "Error"];

    /// <summary>Filter → group → cap one entry span into rows, oldest-or-newest first per the query. Order of
    /// operations matters: level/category/search run per-entry (cheap), THEN adjacent repeats collapse (so a
    /// repeat run can't straddle a filtered-out entry), THEN the cap stops the walk — <see cref="LogViewResult.Truncated"/>
    /// is set the moment one more row would have been added past the cap, without scanning the rest of the span.</summary>
    public static LogViewResult Build(ReadOnlySpan<WaveeLogEntry> entries, LogViewQuery query)
    {
        if (entries.Length == 0) return LogViewResult.Empty;

        int warn = 0, err = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Level == WaveeLogLevel.Warning) warn++;
            else if (entries[i].Level >= WaveeLogLevel.Error) err++;
        }

        int cap = Math.Clamp(query.Cap, 1, MaxRows);
        var rows = new List<LogViewRow>(Math.Min(entries.Length, cap));
        int start = query.NewestFirst ? entries.Length - 1 : 0;
        int end = query.NewestFirst ? -1 : entries.Length;
        int step = query.NewestFirst ? -1 : 1;
        bool truncated = false;

        for (int i = start; i != end; i += step)
        {
            var e = entries[i];
            if (!PassesLevel(e.Level, query.Level)) continue;
            if (query.Category is { Length: > 0 } cat && !string.Equals(e.Category, cat, StringComparison.OrdinalIgnoreCase)) continue;
            if (query.Search.Length > 0 && !Matches(e, query.Search)) continue;

            if (query.GroupRepeats && rows.Count > 0 && IsRepeatOf(rows[^1].Entry, e))
            {
                rows[^1] = rows[^1] with { Repeat = rows[^1].Repeat + 1 };
                continue;
            }

            if (rows.Count >= cap) { truncated = true; break; }
            rows.Add(new LogViewRow(e, 1));
        }

        return new LogViewResult(rows.ToArray(), entries.Length, warn, err, truncated);
    }

    /// <summary>The Segmented level bucket gate (moved from DiagnosticsPanel.PassesLevel).</summary>
    public static bool PassesLevel(WaveeLogLevel level, LogLevelBucket bucket) => bucket switch
    {
        LogLevelBucket.InfoPlus => level >= WaveeLogLevel.Info,
        LogLevelBucket.Warnings => level >= WaveeLogLevel.Warning,
        LogLevelBucket.Errors => level >= WaveeLogLevel.Error,
        _ => true,
    };

    /// <summary>OrdinalIgnoreCase substring match over category, event id, message, operation id and every field's
    /// name/value (moved from DiagnosticsPanel.PassesSearch).</summary>
    public static bool Matches(in WaveeLogEntry e, string query)
    {
        if (e.Category.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.EventId.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.Message.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (e.OperationId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) return true;
        if (e.Fields is { } fields)
            for (int i = 0; i < fields.Length; i++)
                if (fields[i].Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || fields[i].Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    /// <summary>Two entries collapse into one grouped row only when level, category, event id AND message all match —
    /// two different messages at the same instant (or the same message at a different level) never merge.</summary>
    public static bool IsRepeatOf(in WaveeLogEntry a, in WaveeLogEntry b) =>
        a.Level == b.Level && a.Category == b.Category && a.EventId == b.EventId && a.Message == b.Message;

    /// <summary>Distinct categories across the span, case-insensitively, sorted — the category ComboBox's live item
    /// list (the old hard-coded s_categories array is gone; a category that stops appearing in the loaded entries
    /// stops appearing in the dropdown too).</summary>
    public static string[] Categories(ReadOnlySpan<WaveeLogEntry> entries)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Length; i++)
            if (entries[i].Category.Length > 0) set.Add(entries[i].Category);
        var result = new string[set.Count];
        set.CopyTo(result);
        return result;
    }

    /// <summary>The category ComboBox's selected index for a category name: 0 = "All categories" (also the fallback
    /// for null/empty or a name no longer present — the clamp a shrinking category list needs).</summary>
    public static int CategoryIndex(string[] categories, string? category)
    {
        if (string.IsNullOrEmpty(category)) return 0;
        for (int i = 0; i < categories.Length; i++)
            if (string.Equals(categories[i], category, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    /// <summary>One field per line ("name=value"), for the expanded row's Fields CodeBlock. Empty when there are no
    /// fields — the caller skips the CodeBlock entirely rather than showing an empty box.</summary>
    public static string FieldText(WaveeLogField[]? fields)
    {
        if (fields is not { Length: > 0 }) return "";
        var sb = new StringBuilder(fields.Length * 16);
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            fields[i].AppendTo(sb);
        }
        return sb.ToString();
    }

    /// <summary>"Copy visible" / "Export session" text — one line per row, "seq=N " + the entry's own Format(), with
    /// a "(repeated N×)" suffix for a grouped row (moved from DiagnosticsPanel.BuildCopyText).</summary>
    public static string CopyText(ReadOnlySpan<LogViewRow> rows)
    {
        var sb = new StringBuilder(rows.Length * 96);
        for (int i = 0; i < rows.Length; i++)
        {
            var e = rows[i].Entry;
            sb.Append("seq=").Append(e.Sequence.ToString(CultureInfo.InvariantCulture))
              .Append(' ').Append(e.Format());
            if (rows[i].Repeat > 1)
                sb.Append(" (repeated ").Append(rows[i].Repeat.ToString(CultureInfo.InvariantCulture)).Append("×)");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>"HH:mm:ss.fff" in the CALLER-SUPPLIED offset (not <c>ToLocalTime()</c> — that reads the host's TZ,
    /// which would make this untestable) — "—" for the timestamp-less lines older builds wrote before <c>t=</c>
    /// existed (<see cref="WaveeLogSessions"/>'s doc comment).</summary>
    public static string FormatTime(long unixMs, TimeSpan utcOffset) =>
        unixMs <= 0 ? "—" : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToOffset(utcOffset).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>A past session's ComboBox item label: "MMM d · HH:mm · N events" when the session carries a
    /// timestamp, else "pid P · N events" for the legacy sid-less files <see cref="WaveeLogSessions"/> still parses.</summary>
    public static string SessionLabel(long startUnixMs, int pid, int entryCount, TimeSpan utcOffset)
    {
        string head = startUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(startUnixMs).ToOffset(utcOffset).ToString("MMM d · HH:mm", CultureInfo.InvariantCulture)
            : "pid " + pid.ToString(CultureInfo.InvariantCulture);
        return head + " · " + entryCount.ToString(CultureInfo.InvariantCulture) + " events";
    }

    /// <summary>The live session's uptime tier: hours+minutes once an hour has passed, whole minutes under that,
    /// "just now" under one minute (moved from DiagnosticsPanel.LiveSessionSubtitle's tier ladder).</summary>
    public static string Uptime(TimeSpan up)
    {
        if (up.TotalHours >= 1) return ((int)up.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + up.Minutes.ToString(CultureInfo.InvariantCulture) + " m";
        if (up.TotalMinutes >= 1) return ((int)up.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        return "just now";
    }

    /// <summary>The expanded row's meta line: "#812 · lyrics.fetch · tid 4 · op 7f2a · 41 ms" — the event-id suffix,
    /// the operation id and the elapsed-ms clause are each omitted when the entry doesn't carry them, so a plain
    /// Log() call (no Event() extras) still reads as "#5 · app · tid 1" instead of dangling separators.</summary>
    public static string MetaLine(in WaveeLogEntry e)
    {
        var sb = new StringBuilder(64);
        sb.Append('#').Append(e.Sequence.ToString(CultureInfo.InvariantCulture));
        sb.Append(" · ").Append(e.Category);
        if (e.EventId.Length > 0) sb.Append('.').Append(e.EventId);
        sb.Append(" · tid ").Append(e.ThreadId.ToString(CultureInfo.InvariantCulture));
        if (e.OperationId is { Length: > 0 } op) sb.Append(" · op ").Append(op);
        if (e.ElapsedMs >= 0) sb.Append(" · ").Append(e.ElapsedMs.ToString(CultureInfo.InvariantCulture)).Append(" ms");
        return sb.ToString();
    }

    /// <summary>Locate the row standing in for a sequence number (e.g. the row a "bring into view" click named before
    /// a remount) — a grouped row's stored Entry is the first of its run, so this finds it by that anchor sequence.</summary>
    public static int IndexOfSequence(LogViewRow[] rows, long seq)
    {
        for (int i = 0; i < rows.Length; i++)
            if (rows[i].Entry.Sequence == seq) return i;
        return -1;
    }

    /// <summary>The ItemsView remount Key: LogsPanel's list is an autonomous component whose ItemCount/template
    /// freeze at first mount (see the fluentgpu skill's Key-remount rule), so the visible SET changing — a new
    /// session, a filter edit, the live tail growing — has to remount it. Bundles every input that can change the
    /// set plus the shown-count (a live-only signal: two builds with identical filters but different tail growth
    /// still need a fresh key) so ScrollKey (session+filters only, not shown) can still restore the offset across it.</summary>
    public static string RemountKey(int session, LogViewQuery query, int shown) =>
        session.ToString(CultureInfo.InvariantCulture)
        + ":L" + (int)query.Level
        + ":C" + (query.Category ?? "")
        + ":N" + (query.NewestFirst ? 1 : 0)
        + ":G" + (query.GroupRepeats ? 1 : 0)
        + ":Q" + query.Search   // the full text, not just its length — two different queries of equal length must
                                // never collide into the same key (they would show one's rows under the other's filter)
        + ":n" + shown.ToString(CultureInfo.InvariantCulture);
}
