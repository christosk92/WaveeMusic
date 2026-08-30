using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Lyrics;
using Wavee.Core;

namespace Wavee.Backend;

// ── Switchable playback facade — bind once, swap the backend live ─────────────────────────────────────────────────────
// The UI's PlaybackBridge captures its seams (player/state/devices) in its ctor and subscribes them once. To go from the
// in-memory fake to the live Connect backend WITHOUT rebuilding the UI, the bridge binds to these stable facades; on
// connect the composition root calls SetInner(live) and the facade re-points + re-emits, so every existing subscription
// keeps working against the new backend. (Better than WaveeMusic's DI-rebuild-on-login; the bridge never re-points.)

public sealed class SwitchableState : IPlaybackState, IDisposable
{
    readonly SimpleSubject<IPlaybackState> _changes = new();
    readonly SimpleSubject<long> _ticks = new();
    readonly object _gate = new();
    IPlaybackState _inner;
    IDisposable? _changeSub, _tickSub;

    public SwitchableState(IPlaybackState inner) { _inner = inner; Wire(inner); }

    public void SetInner(IPlaybackState inner)
    {
        lock (_gate)
        {
            _changeSub?.Dispose(); _tickSub?.Dispose();
            _inner = inner; Wire(inner);
        }
        _changes.OnNext(this);   // refresh every reader against the new backend
    }

    void Wire(IPlaybackState s)
    {
        _changeSub = s.Changes.Subscribe(Observers.From<IPlaybackState>(_ => _changes.OnNext(this)));
        _tickSub = s.PositionTicks.Subscribe(Observers.From<long>(ms => _ticks.OnNext(ms)));
    }

    IPlaybackState Cur { get { lock (_gate) return _inner; } }

    public Track? CurrentTrack => Cur.CurrentTrack;
    public string? ContextUri => Cur.ContextUri;
    public bool IsPlaying => Cur.IsPlaying;
    public bool IsBuffering => Cur.IsBuffering;
    // EVERY IPlaybackState member is forwarded, including the ones the interface gives a DEFAULT body. A default is
    // what a provider with no answer reports; a FACADE has no answers of its own, so inheriting one here silently
    // replaces the live projection's truth with "no". That is exactly what happened to live-ness: the projection said
    // IsLive, the bridge read this facade, and the LIVE pill never lit for any broadcast. Never add a member to
    // IPlaybackState without adding its forward here.
    public PlaybackRecoveryKind RecoveryKind => Cur.RecoveryKind;
    public long PositionMs => Cur.PositionMs;
    public long DurationMs => Cur.DurationMs;
    public double Volume => Cur.Volume;
    public bool IsShuffle => Cur.IsShuffle;
    public RepeatMode Repeat => Cur.Repeat;
    public IReadOnlyList<QueueEntry> Queue => Cur.Queue;
    public bool IsLoading => Cur.IsLoading;
    public string? Error => Cur.Error;
    public bool CanSkipNext => Cur.CanSkipNext;
    public bool CanSkipPrev => Cur.CanSkipPrev;
    public bool CanSeek => Cur.CanSeek;
    public bool IsLive => Cur.IsLive;
    public LiveWindow Live => Cur.Live;
    public int StreamBitrateKbps => Cur.StreamBitrateKbps;
    public string? StreamFormat => Cur.StreamFormat;
    public string? ActiveDeviceId => Cur.ActiveDeviceId;
    public IObservable<IPlaybackState> Changes => _changes;
    public IObservable<long> PositionTicks => _ticks;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }   // consumers use Changes

    public void Dispose() { _changeSub?.Dispose(); _tickSub?.Dispose(); }
}

public sealed class SwitchablePlayer : IPlaybackPlayer
{
    readonly SwitchableState _state;
    readonly object _gate = new();
    IPlaybackPlayer _inner;

    public SwitchablePlayer(IPlaybackPlayer inner) { _inner = inner; _state = new SwitchableState(inner.State); }

    public void SetInner(IPlaybackPlayer inner) { lock (_gate) _inner = inner; _state.SetInner(inner.State); }
    IPlaybackPlayer Cur { get { lock (_gate) return _inner; } }

    /// <summary>The current inner player — the composition-time seam PlaybackBridge uses to reach the concrete
    /// PlaybackController (restore-snapshot wiring) after a fake→live swap without re-pointing the bridge.</summary>
    public IPlaybackPlayer Inner => Cur;

    public IPlaybackState State => _state;
    public Task PlayAsync(string contextUri, int startIndex = 0, CancellationToken ct = default) => Cur.PlayAsync(contextUri, startIndex, ct);
    public Task PlayContextTrackAsync(string contextUri, PlaybackContextTrack track, int fallbackIndex = 0, CancellationToken ct = default) => Cur.PlayContextTrackAsync(contextUri, track, fallbackIndex, ct);
    public Task PlayOrderedAsync(string contextUri, IReadOnlyList<PlaybackContextTrack> tracks, int startIndex = 0, CancellationToken ct = default) => Cur.PlayOrderedAsync(contextUri, tracks, startIndex, ct);
    public Task PlayTrackAsync(string trackUri, CancellationToken ct = default) => Cur.PlayTrackAsync(trackUri, ct);
    public Task PlayTrackAsync(Track track, CancellationToken ct = default) => Cur.PlayTrackAsync(track, ct);
    public Task PauseAsync(CancellationToken ct = default) => Cur.PauseAsync(ct);
    public Task ResumeAsync(CancellationToken ct = default) => Cur.ResumeAsync(ct);
    public Task NextAsync(CancellationToken ct = default) => Cur.NextAsync(ct);
    public Task PreviousAsync(CancellationToken ct = default) => Cur.PreviousAsync(ct);
    public Task SeekAsync(long positionMs, SeekMode mode, CancellationToken ct = default) => Cur.SeekAsync(positionMs, mode, ct);
    public Task SetVolumeAsync(double volume01, CancellationToken ct = default) => Cur.SetVolumeAsync(volume01, ct);
    public Task SetShuffleAsync(bool on, CancellationToken ct = default) => Cur.SetShuffleAsync(on, ct);
    public Task SetRepeatAsync(RepeatMode mode, CancellationToken ct = default) => Cur.SetRepeatAsync(mode, ct);
    public Task SkipToQueueItemAsync(QueueItemId id, CancellationToken ct = default) => Cur.SkipToQueueItemAsync(id, ct);
    public Task MoveQueueItemAsync(QueueItemId id, int newPos, CancellationToken ct = default) => Cur.MoveQueueItemAsync(id, newPos, ct);
    public Task RemoveQueueItemAsync(QueueItemId id, CancellationToken ct = default) => Cur.RemoveQueueItemAsync(id, ct);
    public Task ClearQueueAsync(CancellationToken ct = default) => Cur.ClearQueueAsync(ct);
    public Task ClearHistoryAsync(CancellationToken ct = default) => Cur.ClearHistoryAsync(ct);
    public Task EnqueueAsync(string trackUri, CancellationToken ct = default) => Cur.EnqueueAsync(trackUri, ct);
    public Task EnqueueAsync(Track track, CancellationToken ct = default) => Cur.EnqueueAsync(track, ct);
    public Task PlayNextAsync(IReadOnlyList<PlaybackContextTrack> tracks, CancellationToken ct = default) => Cur.PlayNextAsync(tracks, ct);
    // Forwarded explicitly: the interface's default would resolve HERE (front-insert/append) and never reach the inner
    // player's exact slot insert.
    public Task InsertIntoQueueAsync(IReadOnlyList<PlaybackContextTrack> tracks, int index, CancellationToken ct = default) => Cur.InsertIntoQueueAsync(tracks, index, ct);
    public Task<string?> StartRadioAsync(string seedUri, string? displayName = null, CancellationToken ct = default) => Cur.StartRadioAsync(seedUri, displayName, ct);
    // Forwarded explicitly for the same reason as InsertIntoQueueAsync above: the interface's default (true) would
    // resolve HERE and PowerBridge's suspend handler would always pause, even a viewer session, if this weren't wired.
    public bool ShouldPauseOnSuspend => Cur.ShouldPauseOnSuspend;
}

public sealed class SwitchableDevices : IConnectDevices, IDisposable
{
    readonly SimpleSubject<IReadOnlyList<PlaybackDevice>> _changed = new(Array.Empty<PlaybackDevice>());
    readonly object _gate = new();
    IConnectDevices _inner;
    IDisposable? _sub;

    public SwitchableDevices(IConnectDevices inner) { _inner = inner; Wire(inner); }

    public void SetInner(IConnectDevices inner)
    {
        lock (_gate) { _sub?.Dispose(); _inner = inner; Wire(inner); }
        _changed.OnNext(inner.Devices);
    }

    void Wire(IConnectDevices d) => _sub = d.DevicesChanged.Subscribe(Observers.From<IReadOnlyList<PlaybackDevice>>(x => _changed.OnNext(x)));
    IConnectDevices Cur { get { lock (_gate) return _inner; } }

    public IReadOnlyList<PlaybackDevice> Devices => Cur.Devices;
    public IObservable<IReadOnlyList<PlaybackDevice>> DevicesChanged => _changed;
    public Task TransferAsync(string deviceId, CancellationToken ct = default) => Cur.TransferAsync(deviceId, ct);

    public void Dispose() => _sub?.Dispose();
}

public sealed class SwitchableSession : ISpotifySession, IDisposable
{
    readonly SimpleSubject<AuthStatus> _status;
    readonly object _gate = new();
    ISpotifySession _inner;
    IDisposable? _sub;

    public SwitchableSession(ISpotifySession inner) { _inner = inner; _status = new SimpleSubject<AuthStatus>(inner.Status); Wire(inner); }

    public void SetInner(ISpotifySession inner)
    {
        lock (_gate) { _sub?.Dispose(); _inner = inner; Wire(inner); }
        _status.OnNext(inner.Status);   // refresh the auth chip / user against the live session
    }

    void Wire(ISpotifySession s) => _sub = s.StatusChanged.Subscribe(Observers.From<AuthStatus>(st => _status.OnNext(st)));
    ISpotifySession Cur { get { lock (_gate) return _inner; } }

    public AuthStatus Status => Cur.Status;
    public WaveeUser? CurrentUser => Cur.CurrentUser;
    public IObservable<AuthStatus> StatusChanged => _status;
    public Task<bool> ConnectAsync(CancellationToken ct = default) => Cur.ConnectAsync(ct);
    public Task LogoutAsync(CancellationToken ct = default) => Cur.LogoutAsync(ct);
    public void Dispose() => _sub?.Dispose();
}

/// <summary>An ISpotifySession reflecting a completed live login (Authenticated + the real account).</summary>
public sealed class LiveSpotifySession : ISpotifySession
{
    readonly SimpleSubject<AuthStatus> _status = new(AuthStatus.Authenticated);
    public LiveSpotifySession(string account, bool isPremium) : this(account, account, null, isPremium) { }
    public LiveSpotifySession(string account, string displayName, string? avatarUrl, bool isPremium, string? email = null)
        => CurrentUser = new WaveeUser(account, string.IsNullOrWhiteSpace(displayName) ? account : displayName, avatarUrl, isPremium, email);
    public AuthStatus Status { get; private set; } = AuthStatus.Authenticated;
    public WaveeUser? CurrentUser { get; private set; }
    public IObservable<AuthStatus> StatusChanged => _status;
    public Task<bool> ConnectAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task LogoutAsync(CancellationToken ct = default) { Status = AuthStatus.LoggedOut; CurrentUser = null; _status.OnNext(AuthStatus.LoggedOut); return Task.CompletedTask; }
}

/// <summary>Stable lyrics facade (docs/lyrics-aggregator-reranker-plan.md §11): the UI binds <c>svc.Lyrics</c> once; on
/// live login the composition root swaps the fake provider for the real <c>AggregatingLyricsProvider</c> via SetInner,
/// without rebuilding the app tree. Mirrors <see cref="SwitchablePlayer"/>.</summary>
public sealed class SwitchableLyrics : IUpgradingLyricsProvider, ILyricsRefetch
{
    readonly object _gate = new();
    readonly Dictionary<string, LyricsDocument> _cache = new();
    readonly Dictionary<string, Task<LyricsDocument?>> _inflight = new();
    readonly SimpleEvent<LyricsDocument> _upgrades = new();
    ILyricsProvider _inner;
    IDisposable? _upgradeSub;
    public SwitchableLyrics(ILyricsProvider inner) { _inner = inner; WireUpgrades(inner); }
    public IObservable<LyricsDocument> LyricsUpgraded => _upgrades;
    public void SetInner(ILyricsProvider inner)
    {
        lock (_gate)
        {
            _upgradeSub?.Dispose();
            _inner = inner;
            _cache.Clear();
            _inflight.Clear();
            WireUpgrades(inner);
        }
    }

    void WireUpgrades(ILyricsProvider provider)
    {
        _upgradeSub = provider is IUpgradingLyricsProvider up
            ? up.LyricsUpgraded.Subscribe(Observers.From<LyricsDocument>(doc => OnInnerUpgrade(provider, doc)))
            : null;
    }

    void OnInnerUpgrade(ILyricsProvider provider, LyricsDocument doc)
    {
        bool forward;
        lock (_gate)
        {
            if (!ReferenceEquals(_inner, provider)) return;
            if (_cache.TryGetValue(doc.TrackId, out var current) && !IsRicher(doc, current)) return;
            _cache[doc.TrackId] = doc;
            forward = true;
        }
        if (forward) _upgrades.OnNext(doc);
    }

    static bool IsRicher(LyricsDocument next, LyricsDocument current)
    {
        int nr = Richness(next), cr = Richness(current);
        if (nr != cr) return nr > cr;
        if (nr < 3) return false;
        return SyllableCount(next) > SyllableCount(current);
    }

    static int Richness(LyricsDocument doc)
    {
        if (doc.Lines.Any(l => l.IsWordByWord && l.Syllables.Count > 0)) return 3;
        return doc.Sync switch
        {
            LyricsSyncKind.Syllable => 3,
            LyricsSyncKind.Line => 2,
            LyricsSyncKind.Unsynced => 1,
            _ => 0,
        };
    }

    static int SyllableCount(LyricsDocument doc)
    {
        int n = 0;
        foreach (var l in doc.Lines) n += l.Syllables.Count;
        return n;
    }

    public Task<LyricsDocument?> GetLyricsAsync(string trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return Task.FromResult<LyricsDocument?>(null);

        Task<LyricsDocument?> task;
        lock (_gate)
        {
            if (_cache.TryGetValue(trackId, out var cached))
                return Task.FromResult<LyricsDocument?>(cached);

            if (_inflight.TryGetValue(trackId, out var existing))
            {
                task = existing;
            }
            else
            {
                var cur = _inner;
                task = FetchAndCacheAsync(trackId, cur);
                _inflight[trackId] = task;
            }
        }

        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    /// <summary>Forget this track HERE too, then delegate. This facade keeps its own winner cache, so clearing only the
    /// aggregator's would leave the stale document sitting one layer above and the re-fetch would look like a no-op.</summary>
    public Task<LyricsDocument?> RefetchAsync(string trackId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackId)) return Task.FromResult<LyricsDocument?>(null);
        ILyricsProvider inner;
        lock (_gate)
        {
            _cache.Remove(trackId);
            _inflight.Remove(trackId);
            inner = _inner;
        }
        return inner is ILyricsRefetch refetchable
            ? refetchable.RefetchAsync(trackId, ct)
            : inner.GetLyricsAsync(trackId, ct);
    }

    async Task<LyricsDocument?> FetchAndCacheAsync(string trackId, ILyricsProvider provider)
    {
        try
        {
            var doc = await provider.GetLyricsAsync(trackId, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_inner, provider))
                {
                    if (doc is not null)
                    {
                        if (!_cache.TryGetValue(trackId, out var current) || IsRicher(doc, current))
                            _cache[trackId] = doc;
                    }
                    _inflight.Remove(trackId);
                }
            }

            return doc;
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inner, provider))
                    _inflight.Remove(trackId);
            }

            throw;
        }
    }
}
