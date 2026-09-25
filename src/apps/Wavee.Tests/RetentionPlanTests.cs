// ── Wavee.Tests/RetentionPlanTests.cs — `RetentionPlan.IndicesToDelete` (§6.3) ──────────────────────────────────────
//
// A port of the old `Prune_DeletesOldestGzipFirst_ByAgeAndDirectoryCap` fixture data
// (`git show 99e398d7^:src/apps/_old/Wavee.Tests/DealerArchiveTests.cs:213-241`) against the new pure function —
// no disk, no DealerArchive type.

using System;
using System.Collections.Generic;
using Xunit;

namespace Wavee.Tests;

public class RetentionPlanTests
{
    [Fact]
    public void Deletes_oldest_first_by_age_cutoff_and_directory_cap()
    {
        var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        // index 0: too old (age > 90 days) — deleted on age alone
        // index 1: within retain window, but pushes the running total over the 100-byte cap — deleted
        // index 2: newest, kept
        var segments = new List<(DateTime WriteUtc, long Bytes)>
        {
            (new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), 80),
            (new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), 80),
            (new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc), 80),
        };

        var toDelete = RetentionPlan.IndicesToDelete(segments, now, retainDays: 90, maxDirectoryBytes: 100);

        Assert.Equal([0, 1], toDelete);
    }

    [Fact]
    public void Keeps_everything_when_under_both_the_age_cutoff_and_the_directory_cap()
    {
        var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        var segments = new List<(DateTime WriteUtc, long Bytes)>
        {
            (new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc), 10),
            (new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc), 10),
        };

        Assert.Empty(RetentionPlan.IndicesToDelete(segments, now, retainDays: 90, maxDirectoryBytes: 1000));
    }

    [Fact]
    public void An_ancient_segment_is_deleted_even_when_the_directory_is_well_under_the_cap()
    {
        var now = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        var segments = new List<(DateTime WriteUtc, long Bytes)>
        {
            (new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1),
        };

        Assert.Equal([0], RetentionPlan.IndicesToDelete(segments, now, retainDays: 90, maxDirectoryBytes: 1_000_000));
    }
}
