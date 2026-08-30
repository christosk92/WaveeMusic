namespace Wavee.Sdk.Streams;

/// <summary>A half-open byte interval <c>[Start, End)</c>.</summary>
/// <param name="Start">First byte offset, inclusive.</param>
/// <param name="End">Last byte offset, exclusive.</param>
public readonly record struct ByteRange(long Start, long End);

/// <summary>
/// The set of byte ranges a <see cref="RangedHttpSource"/> has buffered. Thread-safe; ranges are kept sorted and
/// merged, so a contiguous prefix is always ONE entry and "how far ahead am I buffered" is a single lookup.
/// </summary>
public sealed class RangeSet
{
    readonly object _lock = new();
    readonly List<ByteRange> _ranges = new();

    /// <summary>True when every byte of <c>[start, end)</c> is present (an empty range is trivially present).</summary>
    public bool ContainsRange(long start, long end)
    {
        if (start >= end) return true;
        lock (_lock)
        {
            var idx = FindRangeContaining(start);
            return idx >= 0 && _ranges[idx].End >= end;
        }
    }

    /// <summary>The length of the contiguous run present from <paramref name="start"/>, or 0 when it is not present.</summary>
    public long ContainedLengthFrom(long start)
    {
        lock (_lock)
        {
            var idx = FindRangeContaining(start);
            return idx < 0 ? 0 : _ranges[idx].End - start;
        }
    }

    /// <summary>The sub-ranges of <c>[start, end)</c> that are NOT present, in ascending order.</summary>
    public List<ByteRange> GetGaps(long start, long end)
    {
        var gaps = new List<ByteRange>();
        if (start >= end) return gaps;
        lock (_lock)
        {
            var cur = start;
            foreach (var range in _ranges)
            {
                if (range.End <= cur) continue;
                if (range.Start >= end) break;
                if (range.Start > cur) gaps.Add(new ByteRange(cur, Math.Min(range.Start, end)));
                cur = Math.Max(cur, range.End);
                if (cur >= end) break;
            }
            if (cur < end) gaps.Add(new ByteRange(cur, end));
        }
        return gaps;
    }

    /// <summary>Record <c>[start, end)</c> as present, merging into any touching/overlapping neighbours.</summary>
    public void AddRange(long start, long end)
    {
        if (start >= end) return;
        lock (_lock)
        {
            var mergeStart = start;
            var mergeEnd = end;
            var first = -1;
            var last = -1;
            for (int i = 0; i < _ranges.Count; i++)
            {
                var r = _ranges[i];
                if (r.End >= mergeStart && r.Start <= mergeEnd)
                {
                    if (first < 0) first = i;
                    last = i;
                    mergeStart = Math.Min(mergeStart, r.Start);
                    mergeEnd = Math.Max(mergeEnd, r.End);
                }
            }

            var merged = new ByteRange(mergeStart, mergeEnd);
            if (first >= 0)
            {
                _ranges.RemoveRange(first, last - first + 1);
                _ranges.Insert(first, merged);
            }
            else
            {
                var insert = _ranges.FindIndex(r => r.Start > end);
                if (insert < 0) _ranges.Add(merged);
                else _ranges.Insert(insert, merged);
            }
        }
    }

    int FindRangeContaining(long position)
    {
        for (int i = 0; i < _ranges.Count; i++)
        {
            var r = _ranges[i];
            if (position >= r.Start && position < r.End) return i;
            if (r.Start > position) break;
        }
        return -1;
    }
}
