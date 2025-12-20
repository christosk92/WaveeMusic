using System.Collections;

namespace Wavee.Core.Audio.Download;

/// <summary>
/// Tracks non-overlapping byte ranges for progressive download.
/// Thread-safe for concurrent reads and writes.
/// </summary>
/// <remarks>
/// Used to track which portions of an audio file have been downloaded.
/// Supports efficient queries for:
/// - Checking if a position is downloaded
/// - Finding how many contiguous bytes are available from a position
/// - Adding new downloaded ranges (with automatic merging)
/// - Finding gaps that need to be fetched
/// </remarks>
public sealed class RangeSet : IEnumerable<ByteRange>
{
    private readonly object _lock = new();
    private readonly List<ByteRange> _ranges = new();

    /// <summary>
    /// Gets the total number of bytes covered by all ranges.
    /// </summary>
    public long TotalBytes
    {
        get
        {
            lock (_lock)
            {
                return _ranges.Sum(r => r.Length);
            }
        }
    }

    /// <summary>
    /// Gets the number of distinct ranges.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _ranges.Count;
            }
        }
    }

    /// <summary>
    /// Adds a range to the set, merging with adjacent/overlapping ranges.
    /// </summary>
    /// <param name="range">The range to add.</param>
    public void AddRange(ByteRange range)
    {
        if (range.Length <= 0)
            return;

        lock (_lock)
        {
            AddRangeInternal(range);
        }
    }

    /// <summary>
    /// Adds a range specified by start and end positions.
    /// </summary>
    /// <param name="start">Start position (inclusive).</param>
    /// <param name="end">End position (exclusive).</param>
    public void AddRange(long start, long end)
    {
        AddRange(new ByteRange(start, end));
    }

    /// <summary>
    /// Removes a range from the set, splitting existing ranges if needed.
    /// </summary>
    /// <param name="range">The range to remove.</param>
    public void SubtractRange(ByteRange range)
    {
        if (range.Length <= 0)
            return;

        lock (_lock)
        {
            SubtractRangeInternal(range);
        }
    }

    /// <summary>
    /// Removes a range specified by start and end positions.
    /// </summary>
    /// <param name="start">Start position (inclusive).</param>
    /// <param name="end">End position (exclusive).</param>
    public void SubtractRange(long start, long end)
    {
        SubtractRange(new ByteRange(start, end));
    }

    /// <summary>
    /// Checks if a position is contained within any range.
    /// </summary>
    /// <param name="position">The position to check.</param>
    /// <returns>True if the position is in a downloaded range.</returns>
    public bool Contains(long position)
    {
        lock (_lock)
        {
            return FindRangeContaining(position) >= 0;
        }
    }

    /// <summary>
    /// Checks if a range is fully contained within the downloaded ranges.
    /// </summary>
    /// <param name="start">Start position (inclusive).</param>
    /// <param name="end">End position (exclusive).</param>
    /// <returns>True if the entire range is downloaded.</returns>
    public bool ContainsRange(long start, long end)
    {
        if (start >= end)
            return true;

        lock (_lock)
        {
            var idx = FindRangeContaining(start);
            if (idx < 0)
                return false;

            // Check if this single range covers the entire requested range
            return _ranges[idx].End >= end;
        }
    }

    /// <summary>
    /// Gets the number of contiguous bytes available from a starting position.
    /// </summary>
    /// <param name="start">The starting position.</param>
    /// <returns>Number of contiguous bytes available, or 0 if position is not in any range.</returns>
    public long ContainedLengthFrom(long start)
    {
        lock (_lock)
        {
            var idx = FindRangeContaining(start);
            if (idx < 0)
                return 0;

            return _ranges[idx].End - start;
        }
    }

    /// <summary>
    /// Finds the first gap (missing range) starting from a position.
    /// </summary>
    /// <param name="start">The starting position.</param>
    /// <param name="maxEnd">Maximum end position to consider.</param>
    /// <returns>The first missing range, or null if everything up to maxEnd is downloaded.</returns>
    public ByteRange? FindFirstGap(long start, long maxEnd)
    {
        if (start >= maxEnd)
            return null;

        lock (_lock)
        {
            // Find which range contains or is after the start position
            var idx = FindRangeContainingOrAfter(start);

            if (idx < 0 || idx >= _ranges.Count)
            {
                // No ranges at or after start, everything is a gap
                return new ByteRange(start, maxEnd);
            }

            var range = _ranges[idx];

            if (range.Start <= start)
            {
                // Start is within a range, gap starts after this range
                if (range.End >= maxEnd)
                    return null; // No gap

                // Gap starts at end of this range
                var gapStart = range.End;
                var gapEnd = idx + 1 < _ranges.Count
                    ? Math.Min(_ranges[idx + 1].Start, maxEnd)
                    : maxEnd;

                return gapStart < gapEnd ? new ByteRange(gapStart, gapEnd) : null;
            }
            else
            {
                // Start is before this range, gap is from start to range.Start
                var gapEnd = Math.Min(range.Start, maxEnd);
                return new ByteRange(start, gapEnd);
            }
        }
    }

    /// <summary>
    /// Gets all gaps (missing ranges) within a specified range.
    /// </summary>
    /// <param name="start">Start of the range to check.</param>
    /// <param name="end">End of the range to check.</param>
    /// <returns>List of missing ranges.</returns>
    public List<ByteRange> GetGaps(long start, long end)
    {
        var gaps = new List<ByteRange>();
        if (start >= end)
            return gaps;

        lock (_lock)
        {
            var currentPos = start;

            foreach (var range in _ranges)
            {
                if (range.End <= currentPos)
                    continue; // Range is before our position

                if (range.Start >= end)
                    break; // Range is after our region

                if (range.Start > currentPos)
                {
                    // Gap from currentPos to range.Start
                    gaps.Add(new ByteRange(currentPos, Math.Min(range.Start, end)));
                }

                currentPos = Math.Max(currentPos, range.End);
                if (currentPos >= end)
                    break;
            }

            // Final gap if we haven't reached the end
            if (currentPos < end)
            {
                gaps.Add(new ByteRange(currentPos, end));
            }
        }

        return gaps;
    }

    /// <summary>
    /// Clears all ranges.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _ranges.Clear();
        }
    }

    /// <summary>
    /// Gets a snapshot of all ranges.
    /// </summary>
    public ByteRange[] ToArray()
    {
        lock (_lock)
        {
            return _ranges.ToArray();
        }
    }

    public IEnumerator<ByteRange> GetEnumerator()
    {
        ByteRange[] snapshot;
        lock (_lock)
        {
            snapshot = _ranges.ToArray();
        }
        return ((IEnumerable<ByteRange>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    #region Private Methods

    private void AddRangeInternal(ByteRange newRange)
    {
        if (_ranges.Count == 0)
        {
            _ranges.Add(newRange);
            return;
        }

        // Find insertion point and merge overlapping/adjacent ranges
        var mergeStart = newRange.Start;
        var mergeEnd = newRange.End;
        var firstOverlap = -1;
        var lastOverlap = -1;

        for (int i = 0; i < _ranges.Count; i++)
        {
            var existing = _ranges[i];

            // Check if ranges overlap or are adjacent
            if (existing.End >= mergeStart && existing.Start <= mergeEnd)
            {
                if (firstOverlap < 0)
                    firstOverlap = i;
                lastOverlap = i;

                mergeStart = Math.Min(mergeStart, existing.Start);
                mergeEnd = Math.Max(mergeEnd, existing.End);
            }
        }

        if (firstOverlap >= 0)
        {
            // Remove overlapping ranges and insert merged range
            _ranges.RemoveRange(firstOverlap, lastOverlap - firstOverlap + 1);
            _ranges.Insert(firstOverlap, new ByteRange(mergeStart, mergeEnd));
        }
        else
        {
            // No overlap, find insertion point to maintain sorted order
            var insertIdx = _ranges.FindIndex(r => r.Start > newRange.End);
            if (insertIdx < 0)
                _ranges.Add(newRange);
            else
                _ranges.Insert(insertIdx, newRange);
        }
    }

    private void SubtractRangeInternal(ByteRange subtract)
    {
        var newRanges = new List<ByteRange>();

        foreach (var existing in _ranges)
        {
            if (existing.End <= subtract.Start || existing.Start >= subtract.End)
            {
                // No overlap, keep as-is
                newRanges.Add(existing);
            }
            else
            {
                // Overlap - split or shrink
                if (existing.Start < subtract.Start)
                {
                    // Left portion remains
                    newRanges.Add(new ByteRange(existing.Start, subtract.Start));
                }
                if (existing.End > subtract.End)
                {
                    // Right portion remains
                    newRanges.Add(new ByteRange(subtract.End, existing.End));
                }
            }
        }

        _ranges.Clear();
        _ranges.AddRange(newRanges);
    }

    private int FindRangeContaining(long position)
    {
        for (int i = 0; i < _ranges.Count; i++)
        {
            var range = _ranges[i];
            if (position >= range.Start && position < range.End)
                return i;
            if (range.Start > position)
                break; // Ranges are sorted, no need to continue
        }
        return -1;
    }

    private int FindRangeContainingOrAfter(long position)
    {
        for (int i = 0; i < _ranges.Count; i++)
        {
            if (_ranges[i].End > position)
                return i;
        }
        return -1;
    }

    #endregion
}

/// <summary>
/// Represents a byte range [Start, End).
/// </summary>
/// <param name="Start">Start position (inclusive).</param>
/// <param name="End">End position (exclusive).</param>
public readonly record struct ByteRange(long Start, long End)
{
    /// <summary>
    /// Length of the range in bytes.
    /// </summary>
    public long Length => End - Start;

    /// <summary>
    /// Checks if this range overlaps with another.
    /// </summary>
    public bool Overlaps(ByteRange other) =>
        Start < other.End && End > other.Start;

    /// <summary>
    /// Checks if this range is adjacent to another (touching but not overlapping).
    /// </summary>
    public bool IsAdjacentTo(ByteRange other) =>
        End == other.Start || Start == other.End;

    /// <summary>
    /// Checks if this range contains a position.
    /// </summary>
    public bool Contains(long position) =>
        position >= Start && position < End;

    public override string ToString() => $"[{Start}, {End})";
}
