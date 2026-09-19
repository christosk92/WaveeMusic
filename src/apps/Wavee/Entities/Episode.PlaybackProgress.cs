namespace Wavee;

/// <summary>A presentation snapshot. Persisted reader/filter progress stays separate from the moving player clock.</summary>
public readonly record struct EpisodePlaybackProgress(int PositionMs, int DurationMs, bool Known, bool Completed)
{
    public float Pct => Completed ? 1f : Known ? Episode.ReaderPct(PositionMs, DurationMs) : 0f;
    public int LeftMinutes => Episode.LeftMinutes(PositionMs, DurationMs);
    public string LeftLabel => Episode.LeftLabel(PositionMs, DurationMs);
}

public readonly partial struct Episode
{
    /// <summary>Use in leaf property binds only, never in the reader's list/filter projection. The current episode
    /// reads the same source-content clock as the bottom player (local or Connect, playing or paused); other rows
    /// never subscribe to clock ticks. Live playback overrides an old completed marker when replaying or seeking.
    /// Switching/recycling a row re-evaluates identity before reading the clock. No progress is written here.</summary>
    public static EpisodePlaybackProgress PlaybackProgressOf(Episode e)
    {
        if (!e.IsValid) return default;
        if (Playback.CurrentId.Value.Equals(e.Id) && Playback.PhaseSignal.Value != Playback.Phase.Idle)
        {
            int duration = Playback.DurationMs.Value;
            if (duration <= 0) duration = e.DurationMs;
            int position = Math.Max(0, Playback.PositionMs.Value);
            if (duration > 0) position = Math.Min(position, duration);
            return new(position, duration, true, Rules.Completed(position, duration));
        }
        return new(e.ProgressMs, e.DurationMs, e.Knows(EpisodeFields.Progress), e.Completed);
    }
}
