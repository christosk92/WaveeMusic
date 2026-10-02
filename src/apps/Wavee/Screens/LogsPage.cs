// ── Screens/LogsPage.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the Logs page's pure half: the level strip's labels (counts live INSIDE them), the thousands rule, the session picker's
// status caption, and the two rules a session re-list needs (which index a selection lands on, whether "this session" is
// the live ring)
//
// Role: CORE
// Owner: S
// Wave: 0.3 privacy-diagnostics-tab (docs/plans/wavee/privacy-diagnostics-tab-implementation.md §3.2, §5.2)
//
// ENGINE-FREE. The page (`LogsPage.UI.cs`) lays these out and binds them; every decision lives here with a fact in
// `Wavee.Tests/LogsPageRulesTests.cs`. `LogView` / `LogCapturePolicy` (the filter, the grouping, the capture levels) stay
// where they were — `Diagnostics.cs` / `Platform.Settings.cs`.

using System.Globalization;

namespace Wavee;

/// <summary>Settings › Privacy &amp; diagnostics › Log viewer opens this: route <c>logs</c>, a page of its own.</summary>
public static partial class LogsPage
{
    public static class Rules
    {
        /// <summary>What the status caption beside the session picker says. <see cref="None"/> = no caption at all.</summary>
        public enum PickerCaptionKind : byte { None, Reading, NoneOnDisk, Failed }

        /// <summary>The Segmented strip's four labels: All · Info+ · Warnings · Errors, the last two carrying their counts
        /// ("Warnings · 368") only when there is something to count. The strip re-pushes its items every render, so the
        /// labels follow the live ring with no badges and no remount.</summary>
        public static string[] LevelLabels(string all, string info, string warnings, string errors, int warningCount, int errorCount)
            => [all, info, WithCount(warnings, warningCount), WithCount(errors, errorCount)];

        /// <summary>"Warnings · 1,234" for a positive count; the bare label for zero (nothing to count, nothing to read).</summary>
        public static string WithCount(string label, int count) => count > 0 ? label + " · " + Thousands(count) : label;

        /// <summary>"1,234" in every locale: a diagnostics surface whose numbers get pasted into bug reports reads the
        /// same everywhere, so the grouping separator is the invariant one.</summary>
        public static string Thousands(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>Session 0 is the live ring; every other index is a past run read from the log files.</summary>
        public static bool IsLive(int session) => session == 0;

        /// <summary>The picker's status line. A failed walk beats everything (the list is untrustworthy); a walk in flight
        /// reads as "reading"; a finished walk that found nothing says so (and why: retention); otherwise no caption.</summary>
        public static PickerCaptionKind PickerCaption(bool busy, int count, bool failed)
            => failed ? PickerCaptionKind.Failed
             : busy ? PickerCaptionKind.Reading
             : count <= 0 ? PickerCaptionKind.NoneOnDisk
             : PickerCaptionKind.None;

        /// <summary>The combo index a selection lands on after the session list was re-read. Indices shift whenever a past
        /// run appears or is pruned, so the selection follows the session's KEY (<c>WaveeLogSessions.KeyOf</c>): index
        /// 1 + its position in <paramref name="keys"/>, or 0 (the live session) when nothing was selected or the session
        /// is gone.</summary>
        public static int SelectionAfterRefresh(string? selectedKey, IReadOnlyList<string> keys)
        {
            if (string.IsNullOrEmpty(selectedKey)) return 0;
            for (int i = 0; i < keys.Count; i++)
                if (string.Equals(keys[i], selectedKey, StringComparison.Ordinal)) return i + 1;
            return 0;
        }
    }
}
