// ── Wavee/Diagnostics/Capture.Rotation.cs — segment rotation and retention, pure (§6.2, §6.3) ──────────────────────
//
// A straight port of the old DealerArchive file's DECISION, not its I/O (§1 item 6): the shell (unit 2) is the
// only thing that touches Directory/File; these two classes only ever see already-computed sizes/ages/dates.

using System;
using System.Collections.Generic;

namespace Wavee;

/// <summary>PURE: given the segment's open date, the clock's current date, its current byte length and the size
/// cap, what should happen. §6.2.</summary>
public static class SegmentRotation
{
    public enum Verdict { Keep, RollForDayChange, RollForSize }

    public static Verdict Decide(DateTime openDate, DateTime now, long currentBytes, long maxLiveBytes)
        => now.Date != openDate.Date ? Verdict.RollForDayChange
         : currentBytes >= maxLiveBytes ? Verdict.RollForSize
         : Verdict.Keep;
}

/// <summary>PURE: which already-rolled (gzipped) segments to delete, given their ages, sizes, the retain-days
/// cutoff and the directory byte cap — oldest first, exactly the old file's `Prune` ordering (§6.3).</summary>
public static class RetentionPlan
{
    /// <summary>Returns the indices (into `segments`, as passed) to delete. A segment older than `retainDays` is
    /// always deleted regardless of the byte cap; among the survivors, newest-first accumulation against
    /// `maxDirectoryBytes` marks anything past the cap for deletion too — oldest offender first.</summary>
    public static IReadOnlyList<int> IndicesToDelete(
        IReadOnlyList<(DateTime WriteUtc, long Bytes)> segments, DateTime nowUtc, int retainDays, long maxDirectoryBytes)
    {
        var toDelete = new List<int>();
        var survivors = new List<int>(segments.Count);

        for (int i = 0; i < segments.Count; i++)
        {
            double ageDays = (nowUtc - segments[i].WriteUtc).TotalDays;
            if (ageDays > retainDays) toDelete.Add(i);
            else survivors.Add(i);
        }

        // Newest-first accumulation against the directory cap: keep survivors while the running total stays
        // within budget; anything after the cap is exceeded is deleted too, oldest-of-the-survivors first.
        survivors.Sort((a, b) => segments[b].WriteUtc.CompareTo(segments[a].WriteUtc)); // newest → oldest
        long running = 0;
        var overCap = new List<int>();
        foreach (int i in survivors)
        {
            running += segments[i].Bytes;
            if (running > maxDirectoryBytes) overCap.Add(i);
        }

        toDelete.AddRange(overCap);
        toDelete.Sort((a, b) => segments[a].WriteUtc.CompareTo(segments[b].WriteUtc)); // oldest first, as documented
        return toDelete;
    }
}
