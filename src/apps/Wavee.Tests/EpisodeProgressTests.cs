// ── Wavee.Tests/EpisodeProgressTests.cs — podcast progress, for real (podcast plan §5.8, wave P2) ────────────────────
//
// `Entities/Episode.Progress.cs`'s decisions and its local write, pinned without a player, a session or a socket:
//
//   WHAT A MARK WRITES. Played ⇒ the whole duration (the completed marker while none is resident), unplayed ⇒ 0 — staged
//   at Local for the Progress group only, stamped with the caller's instant, so the hydrate can neither undo it nor
//   mistake this device's own revision for a newer one. What it SENDS herodotus is a Duration position — the duration or
//   0 — and nothing for a played mark whose duration is not resident.
//   WHERE A LOAD STARTS. An episode a Claim or an Advance starts "from the top" starts at its row's progress: in
//   progress ⇒ there; finished (THE completion rule, or the marker with no duration) ⇒ 0; unknown ⇒ 0. The facts drive
//   `EpisodeProgress.ResumeFromMs` directly, and `Playback.EpisodeStartOf` over real committed rows — the exact read the
//   reducer's load tail makes (PlaybackStepTests pins what the deck then publishes).
//   THE MIRROR. `Entities.MirrorEpisodeProgress` lands through the real commit (no store is open here, so its
//   write-behind declines and the staging goes back to the pool) and a stale Full wire answer cannot rewind it.

using Wavee;
using Xunit;

namespace Wavee.Tests;

public class EpisodeProgressTests
{
    [Theory]
    [InlineData(true, 1_800_000, 1_800_000)]
    [InlineData(true, 0, int.MaxValue)]            // the duration not resident yet: the completed marker
    [InlineData(false, 1_800_000, 0)]
    [InlineData(false, 0, 0)]
    public void A_mark_writes_the_whole_duration_or_nothing(bool played, int durationMs, int expected)
        => Assert.Equal(expected, EpisodeProgress.Marked(played, durationMs));

    /// <summary>What a mark SENDS herodotus (a Duration position; the official mark-played write is uncaptured, and marker
    /// 4 too thinly proven to write): played ⇒ the duration, which reads back completed by THE rule; unplayed ⇒ 0; and a
    /// played mark with no resident duration sends NOTHING — 0 would say "unplayed" to every other device.</summary>
    [Theory]
    [InlineData(true, 1_800_000, true, 1_800_000)]
    [InlineData(true, 0, false, 0)]
    [InlineData(true, -1, false, 0)]
    [InlineData(false, 1_800_000, true, 0)]
    [InlineData(false, 0, true, 0)]
    public void A_mark_sends_the_duration_or_zero_and_nothing_it_cannot_state(bool played, int durationMs, bool sends,
                                                                             int expectedPositionMs)
    {
        Assert.Equal(sends, EpisodeProgress.TryMarkedResumePoint(played, durationMs, out int positionMs));
        Assert.Equal(expectedPositionMs, positionMs);
        if (sends && played)
        {
            Assert.True(Episode.Rules.ProgressOf(Episode.Rules.ResumeArm.Position, positionMs, durationMs, out int readBack));
            Assert.True(Episode.Rules.Completed(readBack, durationMs));
        }
    }

    [Theory]
    [InlineData(true, 754_000, 1_800_000, 754_000)]         // in progress: resume there
    [InlineData(true, 754_000, 0, 754_000)]                 // no duration yet: a stated position still stands
    [InlineData(true, 1_780_000, 1_800_000, 1_780_000)]             // 20 s left — completed by the one rule (D-5): start over
    [InlineData(true, 1_800_000, 1_800_000, 0)]             // at the end
    [InlineData(true, 1_900_000, 1_800_000, 0)]             // past it
    [InlineData(true, int.MaxValue, 0, 0)]                  // the completed marker, duration unknown
    [InlineData(true, int.MaxValue, 1_800_000, 0)]          // the completed marker, duration known
    [InlineData(true, 0, 1_800_000, 0)]                     // never started
    [InlineData(false, 754_000, 1_800_000, 0)]              // progress unknown: no guess
    public void A_load_that_named_no_position_starts_at_the_rows_progress_unless_it_is_finished_or_unknown(
        bool knows, int progressMs, int durationMs, int expected)
        => Assert.Equal(expected, EpisodeProgress.ResumeFromMs(knows, progressMs, durationMs));

    [Fact]
    public void Unix_milliseconds_become_the_columns_seconds_clamped()
    {
        Assert.Equal(1_788_000_000, EpisodeProgress.UnixSeconds(1_788_000_000_999));
        Assert.Equal(0, EpisodeProgress.UnixSeconds(-5));
        Assert.Equal(int.MaxValue, EpisodeProgress.UnixSeconds(long.MaxValue));
    }

    [Fact]
    public void The_mirror_ticks_every_15_seconds()
        => Assert.Equal(15_000, EpisodeProgress.MirrorIntervalMs);
}

[Collection(EntitiesCollection.Name)]
public class EpisodeProgressRowTests
{
    const string EpisodeUri = "spotify:episode:0Q86acNRm6V9GYx55SXKwf";
    const long At = 1_788_000_500_000;

    static EntityId Id()
    {
        Assert.True(EntityId.TryParseGid(EpisodeUri.AsSpan(), out EntityId id));
        return id;
    }

    /// <summary>The row as a show page would have it: identity (a 30-minute duration) and, when given, a wire position.</summary>
    static Episode Seed(int? wireProgressMs)
    {
        TestScope.Fresh();
        Staging s = Staging.Rent();
        uint known = (uint)EpisodeFields.Identity | (wireProgressMs is null ? 0u : (uint)EpisodeFields.Progress);
        ref StagedEpisode row = ref s.Episodes.RowFor(Id(), Authority.Full, known);
        row.DurationMs = 1_800_000;
        row.ProgressMs = wireProgressMs ?? 0;
        row.PlayedAt = 1_788_000_000;
        TestScope.CommitAndPublish(s);
        return Entities.Episode(Id());
    }

    [Theory]
    [InlineData(true, 1_800_000)]
    [InlineData(false, 0)]
    public void A_mark_stages_the_Progress_group_alone_at_Local_with_the_callers_instant(bool played, int expected)
    {
        Episode e = Seed(wireProgressMs: 100_000);
        Staging s = Staging.Rent();
        try
        {
            Entities.StageLocalProgress(s, e.Id, EpisodeProgress.Marked(played, e.DurationMs), At);

            ref StagedEpisode row = ref s.Episodes[0];
            Assert.Equal(e.Id, row.Id.Packed);
            Assert.Equal(Authority.Local, row.Authority);
            Assert.Equal((uint)EpisodeFields.Progress, row.Known);
            Assert.Equal(expected, row.ProgressMs);
            Assert.Equal(1_788_000_500, row.PlayedAt);
        }
        finally { Staging.Return(s); }
    }

    [Theory]
    [InlineData(true, Episode.Rules.Status.Played)]
    [InlineData(false, Episode.Rules.Status.Unplayed)]
    public void A_mark_lands_over_a_wire_position_and_reads_as_the_filter_expects(bool played, Episode.Rules.Status status)
    {
        Episode e = Seed(wireProgressMs: 900_000);
        Staging s = Staging.Rent();
        Entities.StageLocalProgress(s, e.Id, EpisodeProgress.Marked(played, e.DurationMs), At);
        TestScope.CommitAndPublish(s);

        Assert.True(Episode.Rules.Matches(status, Episode.Rules.PctOf(e)));
        Assert.Equal(1_788_000_500, e.PlayedAt);
    }

    [Fact]
    public void The_players_mirror_lands_at_Local_and_a_stale_wire_answer_cannot_rewind_it()
    {
        Episode e = Seed(wireProgressMs: 100_000);

        Entities.MirrorEpisodeProgress(e.Id, 1_200_000, At);
        Assert.Equal(1_200_000, e.ProgressMs);

        Staging wire = Staging.Rent();
        ref StagedEpisode stale = ref wire.Episodes.RowFor(e.Id, Authority.Full, (uint)EpisodeFields.Progress);
        stale.ProgressMs = 300_000;
        stale.PlayedAt = 1_788_000_400;
        TestScope.CommitAndPublish(wire);

        Assert.Equal(1_200_000, e.ProgressMs);
        Assert.Equal(1_788_000_500, e.PlayedAt);
    }

    [Fact]
    public void The_mirror_ignores_an_id_that_is_not_an_episode()
    {
        Episode e = Seed(wireProgressMs: 100_000);
        Assert.True(EntityId.TryParseGid("spotify:track:0Q86acNRm6V9GYx55SXKwf".AsSpan(), out EntityId track));

        Entities.MirrorEpisodeProgress(track, 1_200_000, At);

        Assert.Equal(100_000, e.ProgressMs);
        Assert.False(Entities.Current.Tracks.TryGetSlot(track, out _));
    }

    // ── the load path's start, over committed rows ──

    static int StartOf(Episode e) => Playback.EpisodeStartOf(e.Id);

    [Fact]
    public void An_in_progress_episode_loads_at_its_position()
        => Assert.Equal(754_000, StartOf(Seed(wireProgressMs: 754_000)));

    [Fact]
    public void A_finished_episode_loads_from_the_start()
    {
        Episode e = Seed(wireProgressMs: 100_000);
        Entities.MirrorEpisodeProgress(e.Id, EpisodeProgress.Marked(played: true, e.DurationMs), At);

        Assert.Equal(0, StartOf(e));
    }

    [Fact]
    public void An_episode_whose_progress_is_unknown_loads_from_the_start()
    {
        Episode e = Seed(wireProgressMs: null);

        Assert.False(e.Knows(EpisodeFields.Progress));
        Assert.Equal(0, StartOf(e));
    }

    [Fact]
    public void Anything_but_a_resident_episode_loads_from_the_start()
    {
        Seed(wireProgressMs: 754_000);
        Assert.True(EntityId.TryParseGid("spotify:track:0Q86acNRm6V9GYx55SXKwf".AsSpan(), out EntityId track));
        Assert.True(EntityId.TryParseGid("spotify:episode:4rOoJ6Egrf8K2IrywzwOMk".AsSpan(), out EntityId stranger));

        Assert.Equal(0, Playback.EpisodeStartOf(track));        // same gid, another kind
        Assert.Equal(0, Playback.EpisodeStartOf(stranger));     // an episode this scope holds no row for
        Assert.Equal(0, Playback.EpisodeStartOf(default));
    }
}
