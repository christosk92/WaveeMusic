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

// F243: a rail splitter drag previews on `Ui.RailDragWidth` and only the release writes `Ui.RailWidth` (the layout width the
// page, the rail card and the docked PlayReady stream follow) and the persisted setting.
public class RailDragCommitTests
{
    [Theory]
    [InlineData(340f, 340f)]
    [InlineData(340f, 340.004f)]
    [InlineData(200f, 200f)]
    public void A_drag_that_ends_where_the_rail_was_commits_nothing(float committed, float dragged)
        => Assert.Null(Shell.FrameRules.RailDragCommit(dragged, committed));

    [Theory]
    [InlineData(340f, 412f, 412f)]
    [InlineData(340f, 120f, Shell.RailMinW)]
    [InlineData(340f, 900f, Shell.RailMaxW)]
    public void A_moved_drag_commits_its_width_through_the_one_rail_clamp(float committed, float dragged, float expected)
        => Assert.Equal(expected, Shell.FrameRules.RailDragCommit(dragged, committed));

    [Fact]
    public void A_drag_clamped_back_onto_the_committed_width_commits_nothing()
        => Assert.Null(Shell.FrameRules.RailDragCommit(50f, Shell.RailMinW));

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_non_finite_preview_never_reaches_the_layout(float dragged)
        => Assert.Null(Shell.FrameRules.RailDragCommit(dragged, 340f));
}

// F243 / F257 / F259: the ghost A/B's diagnostics. The tier a `[video.mount]` line names, and the spacing gate that keeps a
// swept resize from writing a docked fit / slot line per frame.
public class VideoGhostAbDiagnosticsTests
{
    [Fact]
    public void A_local_file_is_the_override_tier_and_a_url_is_clear()
    {
        Assert.Equal("override", Playback.Video.MountTier(Playback.Video.VideoSource.LocalFile(@"C:\clips\a.mp4")));
        Assert.Equal("clear", Playback.Video.MountTier(Playback.Video.VideoSource.Clear("https://example.test/a.mp4")));
        Assert.Equal("none", Playback.Video.MountTier(null));
    }

    [Fact]
    public void The_gate_passes_the_first_line_then_holds_a_sweep_and_reports_how_many()
    {
        var gate = new global::Wavee.Video.GeometryLogGate();
        Assert.True(gate.TryPass(1_000L, 250, out int held));
        Assert.Equal(0, held);
        Assert.False(gate.TryPass(1_010L, 250, out held));
        Assert.False(gate.TryPass(1_020L, 250, out held));
        Assert.False(gate.TryPass(1_249L, 250, out held));
        Assert.True(gate.TryPass(1_250L, 250, out held));
        Assert.Equal(3, held);
        Assert.False(gate.TryPass(1_300L, 250, out held));
        Assert.True(gate.TryPass(1_500L, 250, out held));
        Assert.Equal(1, held);
    }

    [Fact]
    public void The_trailing_flush_writes_the_settled_value_once_and_only_when_lines_were_held()
    {
        var gate = new global::Wavee.Video.GeometryLogGate();
        Assert.False(gate.TryFlush(900L, out int held));      // the timer's fire at mount: nothing pending
        Assert.Equal(0, held);
        Assert.True(gate.TryPass(1_000L, 250, out held));
        Assert.False(gate.TryFlush(1_010L, out held));        // a passed line leaves nothing to flush
        Assert.False(gate.TryPass(1_020L, 250, out held));
        Assert.False(gate.TryPass(1_040L, 250, out held));
        Assert.True(gate.TryFlush(1_290L, out held));         // the sweep stopped: the settled rect is written, 2 were held
        Assert.Equal(2, held);
        Assert.False(gate.TryFlush(1_300L, out held));        // and only once
        Assert.False(gate.TryPass(1_400L, 250, out held));    // the flush restarted the spacing window
        Assert.True(gate.TryFlush(1_650L, out held));
        Assert.Equal(1, held);
    }
}
