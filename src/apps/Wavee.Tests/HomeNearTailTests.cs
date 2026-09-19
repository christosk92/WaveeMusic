// ── Wavee.Tests/HomeNearTailTests.cs — the infinite-scroll grammar's pure half (Wave 5, owner P) ────────────────────
//
// 0.2.9 `HomeSectionAppendPreloader.NearTailWatch` (:46-54): the scroll-geometry projection key (offset floored to 24 px,
// XOR the content height floored to 48 px — so an append's OWN growth re-evaluates nearness) and the 1.5-viewport test.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class HomeNearTailTests
{
    [Fact]
    public void The_key_buckets_the_offset_by_24_and_the_content_by_48()
    {
        Assert.Equal(0L, HomeNearTail.Project(0f, 800f, 0f));
        Assert.Equal(HomeNearTail.Project(0f, 800f, 0f), HomeNearTail.Project(23.9f, 800f, 47.9f));
        Assert.Equal(1L << 20, HomeNearTail.Project(24f, 800f, 0f));
        Assert.Equal(1L, HomeNearTail.Project(0f, 800f, 48f));
        Assert.Equal((5L << 20) ^ 7L, HomeNearTail.Project(120f, 800f, 336f));
    }

    [Fact]
    public void Scrolling_within_one_bucket_does_not_move_the_key_but_content_growth_does()
    {
        long before = HomeNearTail.Project(1000f, 800f, 4000f);
        Assert.Equal(before, HomeNearTail.Project(1005f, 800f, 4010f));   // 1000/24 and 1005/24 floor to 41; 4000/48, 4010/48 to 83
        // An append grew the content: the same offset must re-evaluate.
        Assert.NotEqual(before, HomeNearTail.Project(1000f, 800f, 4800f));
    }

    [Fact]
    public void The_viewport_is_not_part_of_the_key()
        => Assert.Equal(HomeNearTail.Project(500f, 400f, 3000f), HomeNearTail.Project(500f, 900f, 3000f));

    [Fact]
    public void Near_is_within_one_and_a_half_viewports_of_the_end()
    {
        // content 3000, viewport 800 → near once offset + 800 ≥ 3000 − 1200.
        Assert.True(HomeNearTail.IsNear(1000f, 800f, 3000f));
        Assert.False(HomeNearTail.IsNear(999f, 800f, 3000f));
        Assert.True(HomeNearTail.IsNear(2200f, 800f, 3000f));
    }

    [Fact]
    public void Content_shorter_than_the_viewport_is_always_near()
        => Assert.True(HomeNearTail.IsNear(0f, 800f, 600f));
}
