using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Stage I — the play-history telemetry projection reports each play-start (the Recently Played signal) with its context.
public class ConnectTelemetryTests
{
    sealed class RecSink : IPlaybackTelemetry
    {
        public readonly List<PlayReport> Plays = new();
        public void ReportPlay(in PlayReport r) => Plays.Add(r);
    }

    static Track T(string uri) => new(uri[(uri.LastIndexOf(':') + 1)..], uri, uri,
        Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    [Fact]
    public void ReportsEachPlayStart_WithContext_NotPauseOrEnd()
    {
        var sink = new RecSink();
        var proj = new TelemetryProjection(sink, () => "spotify:playlist:p");

        proj.OnEvent(new PlaybackEvent(EvKind.Started, T("spotify:track:a"), 0));
        proj.OnEvent(new PlaybackEvent(EvKind.TrackChanged, T("spotify:track:b"), 0));
        proj.OnEvent(new PlaybackEvent(EvKind.Paused, T("spotify:track:b"), 500));   // not a play-start → no report
        proj.OnEvent(new PlaybackEvent(EvKind.Ended, null, 1000));                    // no report

        Assert.Equal(2, proj.Reported);
        Assert.Equal(new[] { "spotify:track:a", "spotify:track:b" }, sink.Plays.ConvertAll(p => p.TrackUri));
        Assert.All(sink.Plays, p => Assert.Equal("spotify:playlist:p", p.ContextUri));
    }
}
