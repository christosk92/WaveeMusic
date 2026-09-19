using FluentGpu.Signals;
using Wavee;
using Xunit;

namespace Wavee.Tests;

[Collection(EntitiesCollection.Name)]
public sealed class EpisodePlaybackProgressTests : IDisposable
{
    readonly EntityId _id = Playback.CurrentId.Peek();
    readonly Playback.Phase _phase = Playback.PhaseSignal.Peek();
    readonly int _position = Playback.PositionMs.Peek(), _duration = Playback.DurationMs.Peek();

    public EpisodePlaybackProgressTests() => TestScope.Fresh();

    public void Dispose()
    {
        Playback.CurrentId.Value = _id;
        Playback.PhaseSignal.Value = _phase;
        Playback.PositionMs.Value = _position;
        Playback.DurationMs.Value = _duration;
    }

    static Episode Seed(string uri, int progress = 360_000, int duration = 10_200_000)
    {
        Assert.True(EntityId.TryParseGid(uri.AsSpan(), out EntityId id));
        var staging = Staging.Rent();
        ref var row = ref staging.Episodes.RowFor(id, Authority.Full,
            (uint)(EpisodeFields.Identity | EpisodeFields.Progress));
        row.ProgressMs = progress;
        row.DurationMs = duration;
        TestScope.CommitAndPublish(staging);
        return Entities.Episode(id);
    }

    static void OnDeck(Episode e, int position, int duration, Playback.Phase phase = Playback.Phase.Playing)
    {
        Playback.CurrentId.Value = e.Id;
        Playback.PositionMs.Value = position;
        Playback.DurationMs.Value = duration;
        Playback.PhaseSignal.Value = phase;
    }

    [Theory]
    [InlineData(Playback.Phase.Playing)]
    [InlineData(Playback.Phase.Paused)]
    public void Seek_updates_remaining_and_fill_from_player_clock_without_mutating_resume_rows(Playback.Phase phase)
    {
        var e = Seed("spotify:episode:0Q86acNRm6V9GYx55SXKwf");
        OnDeck(e, 1_702_000, 10_200_000, phase);
        uint version = e.Version;
        var live = Episode.PlaybackProgressOf(e);
        Assert.Equal(1_702_000, live.PositionMs);
        Assert.Equal(142, live.LeftMinutes);
        Assert.Equal(Episode.ReaderPct(1_702_000, 10_200_000), live.Pct);
        Assert.Equal(360_000, e.ProgressMs);
        Assert.Equal(version, e.Version);
        Assert.Equal(Episode.ReaderPct(360_000, 10_200_000), Episode.ReaderPctOf(e));
    }

    [Fact]
    public void Only_current_row_leaves_subscribe_to_position_and_recycling_drops_old_clock_subscription()
    {
        var a = Seed("spotify:episode:0Q86acNRm6V9GYx55SXKwf");
        var b = Seed("spotify:episode:1Lhoz2bVz2uvyVm3Dwpg06");
        OnDeck(a, 1_702_000, 10_200_000);
        var runtime = new ReactiveRuntime();
        var bound = new Signal<Episode>(a);
        int activeReads = 0, inactiveReads = 0, shapeReads = 0;
        using var active = new Memo<EpisodePlaybackProgress>(runtime, () =>
        { activeReads++; return Episode.PlaybackProgressOf(bound.Value); });
        using var inactive = new Memo<EpisodePlaybackProgress>(runtime, () =>
        { inactiveReads++; return Episode.PlaybackProgressOf(b); });
        using var shape = new Memo<float>(runtime, () =>
        { shapeReads++; return Episode.ReaderPctOf(a); });
        Playback.PositionMs.Value = 1_762_000;
        Assert.Equal(1_762_000, active.Value.PositionMs);
        Assert.Equal(360_000, inactive.Value.PositionMs);
        _ = shape.Value;
        Assert.Equal(2, activeReads);
        Assert.Equal(1, inactiveReads);
        Assert.Equal(1, shapeReads);
        bound.Value = b;
        Assert.Equal(360_000, active.Value.PositionMs);
        Playback.PositionMs.Value = 1_822_000;
        Assert.Equal(360_000, active.Value.PositionMs);
        Assert.Equal(3, activeReads);
        Playback.CurrentId.Value = b.Id;
        Assert.Equal(1_822_000, active.Value.PositionMs);
        Assert.Equal(1_822_000, inactive.Value.PositionMs);
    }

    [Fact]
    public void Replaying_completed_episode_uses_actual_position_then_returns_to_persisted_state_when_idle()
    {
        var e = Seed("spotify:episode:0Q86acNRm6V9GYx55SXKwf", int.MaxValue);
        OnDeck(e, 120_000, 10_200_000);
        var live = Episode.PlaybackProgressOf(e);
        Assert.False(live.Completed);
        Assert.InRange(live.Pct, 0.01f, 0.02f);
        Assert.True(e.Completed);
        Playback.PhaseSignal.Value = Playback.Phase.Idle;
        Assert.Equal(1f, Episode.PlaybackProgressOf(e).Pct);
    }

    [Fact]
    public void Playback_duration_wins_with_catalog_fallback_and_live_position_is_clamped()
    {
        var e = Seed("spotify:episode:0Q86acNRm6V9GYx55SXKwf", duration: 600_000);
        OnDeck(e, 800_000, 900_000);
        Assert.Equal(900_000, Episode.PlaybackProgressOf(e).DurationMs);
        Assert.Equal(800_000, Episode.PlaybackProgressOf(e).PositionMs);
        Playback.DurationMs.Value = 0;
        Assert.Equal(600_000, Episode.PlaybackProgressOf(e).PositionMs);
        Assert.Equal(600_000, Episode.PlaybackProgressOf(e).DurationMs);
        Playback.PositionMs.Value = -20;
        Assert.Equal(0, Episode.PlaybackProgressOf(e).PositionMs);
        Assert.Equal(default, Episode.PlaybackProgressOf(default));
    }
}
