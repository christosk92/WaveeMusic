using FluentAssertions;
using Wavee.Core.Audio.Download;
using Xunit;

namespace Wavee.Tests.Core.Audio.Download;

/// <summary>
/// Tests for RangeSet class.
/// Validates thread-safe byte range tracking for progressive download.
/// </summary>
public class RangeSetTests
{
    [Fact]
    public void AddRange_SingleRange_ContainsRange()
    {
        // ============================================================
        // WHY: Adding a single range should make it queryable.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();

        // Act
        rangeSet.AddRange(0, 100);

        // Assert
        rangeSet.Count.Should().Be(1);
        rangeSet.TotalBytes.Should().Be(100);
        rangeSet.Contains(0).Should().BeTrue();
        rangeSet.Contains(50).Should().BeTrue();
        rangeSet.Contains(99).Should().BeTrue();
        rangeSet.Contains(100).Should().BeFalse("End is exclusive");
    }

    [Fact]
    public void AddRange_OverlappingRanges_Merges()
    {
        // ============================================================
        // WHY: Overlapping ranges should be merged into one.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();

        // Act
        rangeSet.AddRange(0, 50);
        rangeSet.AddRange(25, 100); // Overlaps with first

        // Assert
        rangeSet.Count.Should().Be(1, "Overlapping ranges should merge");
        rangeSet.TotalBytes.Should().Be(100);
        rangeSet.Contains(0).Should().BeTrue();
        rangeSet.Contains(75).Should().BeTrue();
        rangeSet.Contains(99).Should().BeTrue();
    }

    [Fact]
    public void AddRange_AdjacentRanges_Merges()
    {
        // ============================================================
        // WHY: Adjacent ranges (touching at boundary) should merge.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();

        // Act
        rangeSet.AddRange(0, 50);
        rangeSet.AddRange(50, 100); // Adjacent to first

        // Assert
        rangeSet.Count.Should().Be(1, "Adjacent ranges should merge");
        rangeSet.TotalBytes.Should().Be(100);
    }

    [Fact]
    public void AddRange_NonOverlappingRanges_KeepsSeparate()
    {
        // ============================================================
        // WHY: Non-overlapping ranges should remain separate.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();

        // Act
        rangeSet.AddRange(0, 50);
        rangeSet.AddRange(100, 150); // Gap between ranges

        // Assert
        rangeSet.Count.Should().Be(2);
        rangeSet.TotalBytes.Should().Be(100); // 50 + 50
        rangeSet.Contains(50).Should().BeFalse("Gap should not be contained");
        rangeSet.Contains(75).Should().BeFalse("Gap should not be contained");
    }

    [Fact]
    public void AddRange_EmptyRange_IsIgnored()
    {
        // ============================================================
        // WHY: Empty or negative ranges should be ignored.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();

        // Act
        rangeSet.AddRange(50, 50); // Empty range
        rangeSet.AddRange(100, 90); // Negative range

        // Assert
        rangeSet.Count.Should().Be(0);
        rangeSet.TotalBytes.Should().Be(0);
    }

    [Fact]
    public void SubtractRange_MiddleOfRange_SplitsIntoTwo()
    {
        // ============================================================
        // WHY: Subtracting from the middle should split into two ranges.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 100);

        // Act
        rangeSet.SubtractRange(40, 60); // Remove middle 20 bytes

        // Assert
        rangeSet.Count.Should().Be(2);
        rangeSet.TotalBytes.Should().Be(80); // 40 + 40
        rangeSet.Contains(39).Should().BeTrue();
        rangeSet.Contains(40).Should().BeFalse("Subtracted");
        rangeSet.Contains(59).Should().BeFalse("Subtracted");
        rangeSet.Contains(60).Should().BeTrue();
    }

    [Fact]
    public void SubtractRange_StartOfRange_ShrinkRange()
    {
        // ============================================================
        // WHY: Subtracting from start should shrink the range.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 100);

        // Act
        rangeSet.SubtractRange(0, 30);

        // Assert
        rangeSet.Count.Should().Be(1);
        rangeSet.TotalBytes.Should().Be(70);
        rangeSet.Contains(0).Should().BeFalse();
        rangeSet.Contains(30).Should().BeTrue();
    }

    [Fact]
    public void SubtractRange_EndOfRange_ShrinkRange()
    {
        // ============================================================
        // WHY: Subtracting from end should shrink the range.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 100);

        // Act
        rangeSet.SubtractRange(70, 100);

        // Assert
        rangeSet.Count.Should().Be(1);
        rangeSet.TotalBytes.Should().Be(70);
        rangeSet.Contains(69).Should().BeTrue();
        rangeSet.Contains(70).Should().BeFalse();
    }

    [Fact]
    public void SubtractRange_EntireRange_RemovesCompletely()
    {
        // ============================================================
        // WHY: Subtracting entire range should remove it completely.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 100);

        // Act
        rangeSet.SubtractRange(0, 100);

        // Assert
        rangeSet.Count.Should().Be(0);
        rangeSet.TotalBytes.Should().Be(0);
    }

    [Fact]
    public void Contains_PositionInRange_ReturnsTrue()
    {
        // ============================================================
        // WHY: Position within range should return true.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(50, 150);

        // Assert
        rangeSet.Contains(50).Should().BeTrue("Start is inclusive");
        rangeSet.Contains(100).Should().BeTrue("Middle");
        rangeSet.Contains(149).Should().BeTrue("End-1");
    }

    [Fact]
    public void Contains_PositionOutsideRange_ReturnsFalse()
    {
        // ============================================================
        // WHY: Position outside range should return false.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(50, 150);

        // Assert
        rangeSet.Contains(49).Should().BeFalse("Before range");
        rangeSet.Contains(150).Should().BeFalse("End is exclusive");
        rangeSet.Contains(200).Should().BeFalse("After range");
    }

    [Fact]
    public void ContainedLengthFrom_PartialRange_ReturnsCorrectLength()
    {
        // ============================================================
        // WHY: Should return bytes available from start position.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 100);

        // Assert
        rangeSet.ContainedLengthFrom(0).Should().Be(100);
        rangeSet.ContainedLengthFrom(50).Should().Be(50);
        rangeSet.ContainedLengthFrom(99).Should().Be(1);
    }

    [Fact]
    public void ContainedLengthFrom_PositionNotInRange_ReturnsZero()
    {
        // ============================================================
        // WHY: If position is not in any range, return 0.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(50, 100);

        // Assert
        rangeSet.ContainedLengthFrom(0).Should().Be(0, "Before range");
        rangeSet.ContainedLengthFrom(100).Should().Be(0, "After range");
    }

    [Fact]
    public void ContainsRange_FullyContained_ReturnsTrue()
    {
        // ============================================================
        // WHY: Should return true if entire requested range is contained.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 100);

        // Assert
        rangeSet.ContainsRange(10, 50).Should().BeTrue();
        rangeSet.ContainsRange(0, 100).Should().BeTrue();
    }

    [Fact]
    public void ContainsRange_PartiallyContained_ReturnsFalse()
    {
        // ============================================================
        // WHY: Should return false if only part is contained.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(50, 100);

        // Assert
        rangeSet.ContainsRange(0, 75).Should().BeFalse("Starts before range");
        rangeSet.ContainsRange(75, 150).Should().BeFalse("Extends past range");
    }

    [Fact]
    public void GetGaps_WithGaps_ReturnsGaps()
    {
        // ============================================================
        // WHY: Should identify missing ranges between downloaded parts.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 50);
        rangeSet.AddRange(100, 150);

        // Act
        var gaps = rangeSet.GetGaps(0, 200);

        // Assert
        gaps.Should().HaveCount(2);
        gaps[0].Start.Should().Be(50);
        gaps[0].End.Should().Be(100);
        gaps[1].Start.Should().Be(150);
        gaps[1].End.Should().Be(200);
    }

    [Fact]
    public void GetGaps_NoGaps_ReturnsEmpty()
    {
        // ============================================================
        // WHY: If fully downloaded, no gaps should be returned.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 100);

        // Act
        var gaps = rangeSet.GetGaps(0, 100);

        // Assert
        gaps.Should().BeEmpty();
    }

    [Fact]
    public void FindFirstGap_WithGap_ReturnsFirstGap()
    {
        // ============================================================
        // WHY: Should find the first missing range from a position.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 50);
        rangeSet.AddRange(100, 150);

        // Act
        var gap = rangeSet.FindFirstGap(0, 200);

        // Assert
        gap.Should().NotBeNull();
        gap!.Value.Start.Should().Be(50);
        gap.Value.End.Should().Be(100);
    }

    [Fact]
    public void FindFirstGap_NoDownloadedData_ReturnsEntireRange()
    {
        // ============================================================
        // WHY: Empty RangeSet means everything is a gap.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();

        // Act
        var gap = rangeSet.FindFirstGap(0, 1000);

        // Assert
        gap.Should().NotBeNull();
        gap!.Value.Start.Should().Be(0);
        gap.Value.End.Should().Be(1000);
    }

    [Fact]
    public void Clear_RemovesAllRanges()
    {
        // ============================================================
        // WHY: Clear should remove all ranges.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 100);
        rangeSet.AddRange(200, 300);

        // Act
        rangeSet.Clear();

        // Assert
        rangeSet.Count.Should().Be(0);
        rangeSet.TotalBytes.Should().Be(0);
    }

    [Fact]
    public void ToArray_ReturnsSnapshotOfRanges()
    {
        // ============================================================
        // WHY: ToArray should return a copy of all ranges.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 50);
        rangeSet.AddRange(100, 150);

        // Act
        var ranges = rangeSet.ToArray();

        // Assert
        ranges.Should().HaveCount(2);
        ranges[0].Should().Be(new ByteRange(0, 50));
        ranges[1].Should().Be(new ByteRange(100, 150));
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentAdditions_NoDataLoss()
    {
        // ============================================================
        // WHY: RangeSet must be thread-safe for concurrent access.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        var tasks = new List<Task>();

        // Act - add 100 non-overlapping ranges concurrently
        for (int i = 0; i < 100; i++)
        {
            var start = i * 100;
            var end = start + 50;
            tasks.Add(Task.Run(() => rangeSet.AddRange(start, end)));
        }

        await Task.WhenAll(tasks);

        // Assert
        rangeSet.Count.Should().Be(100, "All 100 non-overlapping ranges should be added");
        rangeSet.TotalBytes.Should().Be(5000, "50 bytes x 100 ranges = 5000 bytes");
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentReadsAndWrites_NoExceptions()
    {
        // ============================================================
        // WHY: Concurrent reads and writes should not cause exceptions.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var tasks = new List<Task>();

        // Act - concurrent writes
        for (int i = 0; i < 10; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100 && !cts.IsCancellationRequested; j++)
                {
                    rangeSet.AddRange(idx * 1000 + j * 10, idx * 1000 + j * 10 + 5);
                }
            }));
        }

        // Concurrent reads
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100 && !cts.IsCancellationRequested; j++)
                {
                    _ = rangeSet.Contains(j * 10);
                    _ = rangeSet.TotalBytes;
                    _ = rangeSet.Count;
                }
            }));
        }

        // Assert - should complete without exceptions
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void Enumeration_ReturnsAllRanges()
    {
        // ============================================================
        // WHY: Enumerating should return all ranges.
        // ============================================================

        // Arrange
        var rangeSet = new RangeSet();
        rangeSet.AddRange(0, 50);
        rangeSet.AddRange(100, 150);

        // Act
        var ranges = rangeSet.ToList();

        // Assert
        ranges.Should().HaveCount(2);
        ranges.Should().Contain(new ByteRange(0, 50));
        ranges.Should().Contain(new ByteRange(100, 150));
    }
}

/// <summary>
/// Tests for ByteRange record struct.
/// </summary>
public class ByteRangeTests
{
    [Fact]
    public void Length_ReturnsCorrectValue()
    {
        // Arrange
        var range = new ByteRange(50, 150);

        // Assert
        range.Length.Should().Be(100);
    }

    [Fact]
    public void Contains_PositionInRange_ReturnsTrue()
    {
        // Arrange
        var range = new ByteRange(50, 150);

        // Assert
        range.Contains(50).Should().BeTrue("Start is inclusive");
        range.Contains(100).Should().BeTrue("Middle");
        range.Contains(149).Should().BeTrue("End-1");
        range.Contains(150).Should().BeFalse("End is exclusive");
    }

    [Fact]
    public void Overlaps_OverlappingRanges_ReturnsTrue()
    {
        // Arrange
        var range1 = new ByteRange(0, 100);
        var range2 = new ByteRange(50, 150);

        // Assert
        range1.Overlaps(range2).Should().BeTrue();
        range2.Overlaps(range1).Should().BeTrue();
    }

    [Fact]
    public void Overlaps_NonOverlappingRanges_ReturnsFalse()
    {
        // Arrange
        var range1 = new ByteRange(0, 50);
        var range2 = new ByteRange(100, 150);

        // Assert
        range1.Overlaps(range2).Should().BeFalse();
    }

    [Fact]
    public void Overlaps_AdjacentRanges_ReturnsFalse()
    {
        // Arrange
        var range1 = new ByteRange(0, 50);
        var range2 = new ByteRange(50, 100);

        // Assert
        range1.Overlaps(range2).Should().BeFalse("Adjacent ranges don't overlap");
    }

    [Fact]
    public void IsAdjacentTo_AdjacentRanges_ReturnsTrue()
    {
        // Arrange
        var range1 = new ByteRange(0, 50);
        var range2 = new ByteRange(50, 100);

        // Assert
        range1.IsAdjacentTo(range2).Should().BeTrue();
        range2.IsAdjacentTo(range1).Should().BeTrue();
    }

    [Fact]
    public void ToString_ReturnsExpectedFormat()
    {
        // Arrange
        var range = new ByteRange(0, 100);

        // Assert
        range.ToString().Should().Be("[0, 100)");
    }
}
