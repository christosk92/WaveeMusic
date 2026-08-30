using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee;

// The two playback-derived first-party sources — wavee.queue and wavee.nowPlaying.
//
// NO SUBSCRIPTIONS HERE. Both read PlaybackBridge signals with Peek (never Value: a service is not a computation, so a
// read would subscribe nothing and a WRITE from the render thread is the bug this avoids). Their freshness comes from the
// binder's pump, which subscribes to QueueRevision + Identity and rebuilds — one observer for the whole sidebar instead of
// one per source.

/// <summary><c>wavee.queue</c> — what plays next. Excludes the currently playing item (that is <c>wavee.nowPlaying</c>'s
/// job) and is deduped by uri, so a repeated track cannot produce two rows with one reconciler key.</summary>
public sealed class SidebarQueueSource : SidebarDataSourceBase
{
    readonly PlaybackBridge? _playback;
    readonly List<Track> _scratch = new(32);

    public SidebarQueueSource(PlaybackBridge? playback) : base(SidebarContributions.Queue) => _playback = playback;

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Track;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;

    public override SidebarConfigSchema ConfigSchema { get; } = new(1,
    [
        new SidebarConfigField("maxItems", SidebarConfigFieldKind.Int, "sidebar.source.queue.maxItems",
            DefaultJson: "5", Min: 1, Max: 50),
    ]);

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        // An empty queue is EMPTY, not pending: playback either has a queue or it does not.
        SetHealthQuiet(SidebarSourceState.Ready);
        if (_playback is null) return 0;

        var queue = _playback.Queue.Peek();
        if (queue.Count == 0) return 0;
        string? current = _playback.CurrentTrack.Peek()?.Uri;

        _scratch.Clear();
        for (int i = 0; i < queue.Count; i++)
        {
            var t = queue[i].Track;
            if (t.Uri.Length == 0) continue;
            if (i == 0 && string.Equals(t.Uri, current, StringComparison.Ordinal)) continue;   // the now-playing head
            _scratch.Add(t);
        }
        return SidebarSourceMap.Tracks(_scratch, into,
            SidebarLibrarySource.Max(request, request.Config.Int("maxItems"), 5));
    }
}

/// <summary><c>wavee.nowPlaying</c> — the single current track (a one-row section: the spotlight, not a list). Empty while
/// nothing plays, which is the honest state — a "Now playing" section with a placeholder row would be a lie.</summary>
public sealed class SidebarNowPlayingSource : SidebarDataSourceBase
{
    readonly PlaybackBridge? _playback;

    public SidebarNowPlayingSource(PlaybackBridge? playback) : base(SidebarContributions.NowPlaying)
        => _playback = playback;

    public override SidebarSourceItemType ItemType => SidebarSourceItemType.Track;
    public override SidebarSourceSorts SupportedSorts => SidebarSourceSorts.SourceOrder;
    public override SidebarSourcePaging Paging => SidebarSourcePaging.None;

    public override int Fill(List<SidebarLibraryEntry> into, in SidebarSourceRequest request)
    {
        SetHealthQuiet(SidebarSourceState.Ready);
        var track = _playback?.CurrentTrack.Peek();
        if (track is null || track.Uri.Length == 0) return 0;
        into.Add(SidebarSourceMap.FromTrack(track, 0));
        return 1;
    }
}
