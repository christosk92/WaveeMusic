using Xunit;

namespace Wavee.Tests;

public sealed class AudiobookOrderTests
{
    static int[] Normalize(int[] provider, EpisodeKind[] kinds, out int trailer, out int trailerCount)
    {
        var reader = new int[provider.Length];
        int count = AudiobookOrder.Normalize(provider, kinds, reader, out trailer, out trailerCount);
        return reader[..count];
    }

    [Fact]
    public void Captured_native_chapters_and_trailing_sample_map_to_reader_and_back_without_numbers()
    {
        // themanagerpath.saz raw/399 native playlist positions: Track1..18, five-minute trailer.
        // raw/401 EpisodeV4 identifies the sample as type87=1; number89 is absent on all items.
        int[] native = Enumerable.Range(1, 19).ToArray();
        var kinds = new EpisodeKind[19]; kinds[18] = EpisodeKind.Trailer;
        int[] reader = Normalize(native, kinds, out int trailer, out int trailers);
        Assert.Equal(Enumerable.Range(1, 18).Reverse(), reader);
        Assert.Equal(1, reader[^1]);
        Assert.Equal(19, trailer); Assert.Equal(1, trailers);
        Assert.Equal(Enumerable.Range(1, 19), native); // normalization cannot rewrite playback's membership

        var pcts = new float[18];
        var view = new int[18];
        int n = ShowReaderRules.View(pcts, Episode.Rules.Status.All, oldest: true, [], view);
        Assert.Equal(Enumerable.Range(1, 18), view[..n].Select(index => reader[index]));
        int reverse = ShowReaderRules.View(pcts, Episode.Rules.Status.All, oldest: false, [], view);
        Assert.Equal(Enumerable.Range(1, 18).Reverse(), view[..reverse].Select(index => reader[index]));
    }

    [Fact]
    public void Serial_listen_next_starts_at_chapter_one_and_continues_forward_after_progress()
    {
        int[] reader = Normalize([1, 2, 3, 4, 99], [EpisodeKind.Full, EpisodeKind.Full,
            EpisodeKind.Full, EpisodeKind.Full, EpisodeKind.Trailer], out _, out _);
        var pcts = new float[4];
        int[] next = new int[ListenNext.UpNextMax];
        var picked = ListenNext.Pick(pcts, ConsumptionOrder.Sequential, -1, next);
        Assert.Equal(-1, picked.Resume);
        Assert.Equal(new[] { 1, 2, 3 }, next[..picked.Count].Select(index => reader[index]));
        pcts[3] = 1; // chapter1 completed
        pcts[2] = .4f; // chapter2 in progress
        picked = ListenNext.Pick(pcts, ConsumptionOrder.Sequential, -1, next);
        Assert.Equal(2, reader[picked.Resume]);
        Assert.Equal(new[] { 3, 4 }, next[..picked.Count].Select(index => reader[index]));
    }

    [Fact]
    public void Hydration_can_identify_samples_without_losing_unknown_chapters_or_reordering_them()
    {
        Assert.Equal(new[] { 99, 2, 1 }, Normalize([1, 2, 99], [], out int unknown, out int unknownCount));
        Assert.Equal(0, unknown); Assert.Equal(0, unknownCount);
        Assert.Equal(new[] { 2, 1 }, Normalize([1, 2, 99], [EpisodeKind.Full, EpisodeKind.Full, EpisodeKind.Trailer],
            out int trailer, out int count));
        Assert.Equal(99, trailer); Assert.Equal(1, count);
    }

    [Fact]
    public void Every_trailer_is_excluded_from_counts_and_the_first_remains_the_sample_action()
    {
        var reader = Normalize([90, 1, 2, 99], [EpisodeKind.Trailer, EpisodeKind.Full, EpisodeKind.Full, EpisodeKind.Trailer],
            out int trailer, out int count);
        Assert.Equal(new[] { 2, 1 }, reader); Assert.Equal(90, trailer); Assert.Equal(2, count);
        Assert.Empty(Normalize([90], [EpisodeKind.Trailer], out trailer, out count));
        Assert.Equal(1, count);
        Assert.Empty(Normalize([], [], out trailer, out count)); Assert.Equal(0, count);
    }
}
