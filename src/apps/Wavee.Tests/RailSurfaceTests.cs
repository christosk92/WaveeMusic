// ── Wavee.Tests/RailSurfaceTests.cs — the rail's stage-B rules: the hero's width ladder and the friends feed ────────
//
// `Rail.Hero` is the W7b table (ch 21 §2): the art / deck side and the pinned block at the three rungs of the rail's
// live width. `Rail.Friends` is FriendsPanel's decision half: which surface, "listening now", the relative-time bucket
// and where a row goes. All pure; nothing here starts the engine.

using Wavee;
using Xunit;

using F = Wavee.Rail.Friends;
using H = Wavee.Rail.Hero;

namespace Wavee.Tests;

public class RailHeroTests
{
    [Theory]
    [InlineData(200f, 184f)]
    [InlineData(340f, 324f)]
    [InlineData(500f, 484f)]
    public void The_side_is_the_W7b_ladder(float railWidth, float side) => Assert.Equal(side, H.Side(railWidth));

    [Theory]
    [InlineData(200f, 236f)]
    [InlineData(340f, 376f)]
    [InlineData(500f, 536f)]
    public void The_pinned_block_is_art_top_plus_the_side(float railWidth, float block)
        => Assert.Equal(block, H.PinnedBlockH(railWidth));

    [Fact]
    public void Art_top_is_derived_from_the_strip() => Assert.Equal(H.Inset + H.HeaderRowH + H.Inset, H.ArtTop);

    [Fact]
    public void A_drag_remounts_at_most_once_per_quantum()
    {
        int changes = 0;
        float last = H.Side(200f);
        for (float w = 200f; w <= 500f; w += 1f)
        {
            float s = H.Side(w);
            Assert.Equal(0f, s % H.SideQuantum);
            if (s != last) { changes++; last = s; }
        }
        Assert.InRange(changes, 1, (int)((500f - 200f) / H.SideQuantum) + 1);
    }

    [Fact]
    public void A_degenerate_width_never_produces_a_non_positive_side() => Assert.True(H.Side(0f) >= 0f);
}

public class RailFriendsTests
{
    [Fact]
    public void Rows_win_over_every_state()
    {
        foreach (F.FeedState s in Enum.GetValues<F.FeedState>())
            Assert.Equal(F.Surface.Rows, F.SurfaceFor(3, s));
    }

    [Theory]
    [InlineData(F.FeedState.Offline, F.Surface.Offline)]
    [InlineData(F.FeedState.Error, F.Surface.Error)]
    [InlineData(F.FeedState.Idle, F.Surface.Skeleton)]
    [InlineData(F.FeedState.Loading, F.Surface.Skeleton)]
    [InlineData(F.FeedState.Ready, F.Surface.Empty)]
    public void With_no_rows_the_state_picks_the_surface(F.FeedState state, F.Surface surface)
        => Assert.Equal(surface, F.SurfaceFor(0, state));

    [Fact]
    public void An_unattached_host_reads_offline() => Assert.Equal(F.FeedState.Offline, F.State.Peek());

    [Fact]
    public void Live_is_two_minutes_inclusive_and_never_for_an_unknown_timestamp()
    {
        const long now = 10_000_000;
        Assert.True(F.IsLive(now, now - F.LiveWindowMs));
        Assert.False(F.IsLive(now, now - F.LiveWindowMs - 1));
        Assert.True(F.IsLive(now, now));
        Assert.False(F.IsLive(now, 0));
    }

    [Theory]
    [InlineData(0L, F.AgeUnit.Now, 0L)]
    [InlineData(59_999L, F.AgeUnit.Now, 0L)]
    [InlineData(-5_000L, F.AgeUnit.Now, 0L)]
    [InlineData(60_000L, F.AgeUnit.Minutes, 1L)]
    [InlineData(59L * 60_000L, F.AgeUnit.Minutes, 59L)]
    [InlineData(60L * 60_000L, F.AgeUnit.Hours, 1L)]
    [InlineData(23L * 3_600_000L, F.AgeUnit.Hours, 23L)]
    [InlineData(24L * 3_600_000L, F.AgeUnit.Days, 1L)]
    [InlineData(80L * 3_600_000L, F.AgeUnit.Days, 3L)]
    public void The_relative_time_buckets(long ageMs, F.AgeUnit unit, long count)
    {
        Assert.Equal(unit, F.Age(ageMs, out long n));
        Assert.Equal(count, n);
    }

    [Theory]
    [InlineData(5, 6, 7, F.Target.Context)]
    [InlineData(0, 6, 7, F.Target.Album)]
    [InlineData(0, 0, 7, F.Target.Artist)]
    [InlineData(0, 0, 0, F.Target.None)]
    public void A_row_routes_context_then_album_then_artist(int context, int album, int artist, F.Target target)
        => Assert.Equal(target, F.TargetOf(context, album, artist));
}
