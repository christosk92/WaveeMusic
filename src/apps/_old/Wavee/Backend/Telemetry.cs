using System;

namespace Wavee.Backend;

// ── Stage I — play-history telemetry (the Recently Played / play-count signal) ────────────────────────────────────────
// A projection over the playback event log that reports each track-play to a sink. The proto-free seam + accumulation live
// here (unit-tested); the live sink (SpotifyLive/Gabo/RawCoreStreamProjection + GaboBatcher) posts gabo-receiver events.
// Per the decision to include telemetry in v1: this is the play-START signal (the core "this track was played" event);
// the richer gabo envelopes (played-duration segments, content-integrity, audio-session) layer on top in the live sink.

public readonly record struct PlayReport(string TrackUri, string? ContextUri, long AtMs);

public interface IPlaybackTelemetry
{
    void ReportPlay(in PlayReport report);
}

public sealed class NullTelemetry : IPlaybackTelemetry
{
    public void ReportPlay(in PlayReport report) { }
}

/// <summary>Reports a play to the sink on each Started / TrackChanged (the seed of Recently Played). Supersedes the
/// counting-only HistoryProjection by carrying the track + context to the sink.</summary>
public sealed class TelemetryProjection : IPlaybackProjection
{
    readonly IPlaybackTelemetry _sink;
    readonly Func<string?> _contextUri;

    /// <summary>Count of reports emitted (test visibility).</summary>
    public int Reported { get; private set; }

    public TelemetryProjection(IPlaybackTelemetry sink, Func<string?>? contextUri = null)
    {
        _sink = sink;
        _contextUri = contextUri ?? (() => null);
    }

    public void OnEvent(in PlaybackEvent e)
    {
        if (e.Kind is EvKind.Started or EvKind.TrackChanged && e.Track is { } t)
        {
            Reported++;
            _sink.ReportPlay(new PlayReport(t.Uri, _contextUri(), e.AtMs));
        }
    }
}
