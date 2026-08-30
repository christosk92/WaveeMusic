using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

// SelectionModel.cs (source-included from src/FluentGpu.Controls) opens `using FluentGpu.Foundation;` — it uses no type
// from it, but the namespace must EXIST for the using to compile without referencing the FluentGpu.Engine assembly
// (the same reason VirtualCollectionSignalShim exists for FluentGpu.Signals). This anchor declares it.
namespace FluentGpu.Foundation
{
    internal static class FoundationShimAnchor { }
}

namespace Wavee.Tests.Actions
{
    /// <summary>Track factory + shared fakes for the Actions tests.</summary>
    internal static class T
    {
        public static Track Mk(string id, string? uriOverride = null, int artists = 1, string albumUri = "spotify:album:al1") =>
            new(id,
                uriOverride ?? ("spotify:track:" + id),
                "Track " + id,
                MkArtists(artists),
                new AlbumRef("al1", albumUri, "Album One"),
                180_000, false, null,
                ContextUid: "uid-" + id);

        static IReadOnlyList<ArtistRef> MkArtists(int n)
        {
            var a = new ArtistRef[n];
            for (int i = 0; i < n; i++) a[i] = new ArtistRef("ar" + i, "spotify:artist:ar" + i, "Artist " + i);
            return a;
        }
    }

    internal sealed class NeverObservable<TItem> : IObservable<TItem>
    {
        public static readonly NeverObservable<TItem> Instance = new();
        sealed class Nop : IDisposable { public static readonly Nop I = new(); public void Dispose() { } }
        public IDisposable Subscribe(IObserver<TItem> observer) => Nop.I;
    }

    /// <summary>A recording playback fake: the player IS its own state (the UnsupportedPlaybackPlayer shape).
    /// <see cref="ActiveDeviceId"/> settable — the queue verbs' remote-device gate under test.</summary>
    internal sealed class RecordingPlayer : IPlaybackPlayer, IPlaybackState
    {
        public readonly List<IReadOnlyList<PlaybackContextTrack>> PlayNextCalls = new();
        public readonly List<(int Index, IReadOnlyList<PlaybackContextTrack> Tracks)> InsertCalls = new();
        public readonly List<string> Enqueued = new();
        public readonly List<string> PlayedTracks = new();
        public readonly List<QueueItemId> Removed = new();
        public readonly List<string> RadioSeeds = new();
        /// <summary>What <see cref="StartRadioAsync"/> returns (the radio playlist uri, or null = no radio) — set per test.</summary>
        public string? RadioResult = null;

        public string? ActiveDeviceId { get; set; }

        // ── IPlaybackPlayer ────────────────────────────────────────────────────────────────────────────────────────
        public Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default) => Task.CompletedTask;
        public Task PlayContextTrackAsync(string contextUri, PlaybackContextTrack track, int fallbackIndex = 0, CancellationToken ct = default) => Task.CompletedTask;
        public Task PlayOrderedAsync(string contextUri, IReadOnlyList<PlaybackContextTrack> tracks, int startIndex = 0, CancellationToken ct = default) => Task.CompletedTask;
        public Task PlayTrackAsync(string trackUri, CancellationToken ct = default) { PlayedTracks.Add(trackUri); return Task.CompletedTask; }
        public Task PlayTrackAsync(Track track, CancellationToken ct = default) { PlayedTracks.Add(track.Uri); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task NextAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PreviousAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SeekAsync(long positionMs, SeekMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetVolumeAsync(double volume01, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetShuffleAsync(bool on, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task SkipToQueueItemAsync(QueueItemId id, CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveQueueItemAsync(QueueItemId id, int newPos, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveQueueItemAsync(QueueItemId id, CancellationToken ct = default) { Removed.Add(id); return Task.CompletedTask; }
        public Task ClearQueueAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearHistoryAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task EnqueueAsync(string trackUri, CancellationToken ct = default) { Enqueued.Add(trackUri); return Task.CompletedTask; }
        public Task EnqueueAsync(Track track, CancellationToken ct = default) { Enqueued.Add(track.Uri); return Task.CompletedTask; }
        public Task PlayNextAsync(IReadOnlyList<PlaybackContextTrack> tracks, CancellationToken ct = default) { PlayNextCalls.Add(tracks); return Task.CompletedTask; }
        public Task InsertIntoQueueAsync(IReadOnlyList<PlaybackContextTrack> tracks, int index, CancellationToken ct = default)
        { InsertCalls.Add((index, tracks)); return Task.CompletedTask; }
        public Task<string?> StartRadioAsync(string seedUri, string? displayName = null, CancellationToken ct = default) { RadioSeeds.Add(seedUri); return Task.FromResult(RadioResult); }
        public IPlaybackState State => this;

        // ── IPlaybackState ─────────────────────────────────────────────────────────────────────────────────────────
        public Track? CurrentTrack => null;
        public string? ContextUri => null;
        public bool IsPlaying => false;
        public bool IsBuffering => false;
        public long PositionMs => 0;
        public long DurationMs => 0;
        public double Volume => 1.0;
        public bool IsShuffle => false;
        public RepeatMode Repeat => RepeatMode.Off;
        public IReadOnlyList<QueueEntry> Queue => Array.Empty<QueueEntry>();
        public IObservable<IPlaybackState> Changes => NeverObservable<IPlaybackState>.Instance;
        public IObservable<long> PositionTicks => NeverObservable<long>.Instance;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
}
