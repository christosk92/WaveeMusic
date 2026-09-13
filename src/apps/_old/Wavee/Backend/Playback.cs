using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Core;

namespace Wavee.Backend;

// ── ENGINE ④ — Playback (pure QueueCore → reducer → event log → projections) ─────────────────────────────────────────

/// <summary>QueueCore — the pure queue algorithm (3 buckets, dual cursor, shuffle, repeat, advance ladder). No I/O.
/// The user-queue drains first WITHOUT advancing the context cursor; the ladder terminates at EndOfContext (autoplay is
/// an async request the reducer surfaces, never network I/O inside this pure core).</summary>
public sealed class QueueCore
{
    readonly List<QueuedTrack> _history = new();
    readonly List<QueuedTrack> _context = new();      // the PLAY order (shuffled or natural)
    readonly List<QueuedTrack> _original = new();     // the natural order — kept so shuffle can be turned back OFF
    readonly List<QueuedTrack> _userQueue = new();    // q# — drains before the context
    int _cursor = -1;
    int _seedState = 0x5DEECE66 & 0x7fffffff;
    QueuedTrack? _cur;

    public string? ContextUri { get; private set; }
    public bool Shuffle { get; private set; }
    public RepeatMode Repeat { get; private set; }
    public int CursorIndex => _cursor;
    public int RemainingInContext => _cursor < 0 ? 0 : Math.Max(0, _context.Count - _cursor - 1);
    // The public surface stays Track-shaped (so the reducer/scaffold/tests are unchanged); the Spotify context uid rides
    // alongside via CurrentUid + QueueEntry.Uid + the QueuedTrack SetContext/EnqueueUser overloads the Connect path uses.
    public Track? Current => _cur?.Track;
    public string CurrentUid => _cur is { } c ? c.Uid : "";

    /// <summary>Seed the context with uid-carrying tracks (the Connect / context-resolve path).</summary>
    public void SetContext(string uri, IReadOnlyList<QueuedTrack> tracks, int startIndex)
    {
        _original.Clear();
        foreach (var t in tracks) _original.Add(WithProvider(t, "context"));
        _userQueue.Clear();
        _history.Clear();
        ContextUri = uri;
        _context.Clear();
        _context.AddRange(_original);
        _cursor = _context.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _context.Count - 1);
        _cur = _cursor >= 0 ? _context[_cursor] : null;
        if (Shuffle) ReshuffleAnchoringCurrent();   // a context set while shuffle is ON is shuffled (was previously ignored)
    }

    /// <summary>uid-less convenience (synthetic single tracks, the scaffold/reducer) — wraps each Track with uid "".</summary>
    public void SetContext(string uri, IEnumerable<Track> tracks, int startIndex) => SetContext(uri, Wrap(tracks), startIndex);

    public void EnqueueUser(QueuedTrack t) => _userQueue.Add(WithProvider(t, "queue"));
    public void EnqueueUser(Track t) => _userQueue.Add(new QueuedTrack(t, "", "queue"));

    /// <summary>Head-insert ahead of the user queue (a "play next" — plays before already-queued items; cursor unmoved).</summary>
    public void EnqueueNext(QueuedTrack t) => _userQueue.Insert(0, WithProvider(t, "queue"));

    /// <summary>Batch-append to the user queue (add_to_queue of many).</summary>
    public void EnqueueRange(IEnumerable<QueuedTrack> tracks) { foreach (var t in tracks) _userQueue.Add(WithProvider(t, "queue")); }

    /// <summary>Replace the user queue wholesale (set_queue's next_tracks). prev_tracks are advisory — our "prev" is the
    /// resident context history (the cursor), so they're ignored.</summary>
    public void ReplaceNextUp(IReadOnlyList<QueuedTrack> next)
    {
        _userQueue.Clear();
        foreach (var t in next) _userQueue.Add(WithProvider(t, "queue"));
    }

    public void RelabelContext(string uri) => ContextUri = uri;

    public void AppendToContext(IReadOnlyList<QueuedTrack> tracks, string provider)
    {
        if (tracks.Count == 0) return;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = WithProvider(tracks[i], provider);
            _original.Add(t);
            _context.Add(t);
        }
    }

    /// <summary>Remove an upcoming entry by its Snapshot id: a user-queue item (q#) or an upcoming context track (c#).
    /// Returns false if the id doesn't resolve to a removable upcoming entry.</summary>
    public bool Remove(string entryId)
    {
        if (TryIndex(entryId, 'q', out int qi) && qi < _userQueue.Count) { _userQueue.RemoveAt(qi); return true; }
        if (TryIndex(entryId, 'c', out int ci) && ci > _cursor && ci < _context.Count)
        {
            var removed = _context[ci];
            _context.RemoveAt(ci);
            _original.RemoveAll(x => ReferenceEquals(x.Track, removed.Track));   // keep shuffle-restore consistent
            return true;
        }
        return false;
    }

    /// <summary>Reorder within the user queue (the bucket the queue panel reorders). toIndex is clamped.</summary>
    public bool Move(string entryId, int toIndex)
    {
        if (!TryIndex(entryId, 'q', out int from) || from >= _userQueue.Count) return false;
        var t = _userQueue[from];
        _userQueue.RemoveAt(from);
        _userQueue.Insert(Math.Clamp(toIndex, 0, _userQueue.Count), t);
        return true;
    }

    static bool TryIndex(string id, char prefix, out int i)
    { i = -1; return id.Length > 1 && id[0] == prefix && int.TryParse(id.AsSpan(1), out i) && i >= 0; }

    public Track? Next()
    {
        if (Repeat == RepeatMode.Track && _cur is not null) return _cur?.Track;          // repeat-one holds
        RememberCurrent();
        if (_userQueue.Count > 0)
        {
            while (_userQueue.Count > 0)
            {
                var entry = _userQueue[0];
                _userQueue.RemoveAt(0);
                _cur = entry;
                if (entry.RowKind == QueueRowKind.Playable) return entry.Track;
            }
            _cur = null;
            return null;
        }
        while (_cursor + 1 < _context.Count)
        {
            _cursor++;
            var entry = _context[_cursor];
            _cur = entry;
            if (entry.RowKind == QueueRowKind.Playable) return entry.Track;
        }
        if (Repeat == RepeatMode.Context && _context.Count > 0)
        {
            for (_cursor = 0; _cursor < _context.Count; _cursor++)
            {
                var entry = _context[_cursor];
                _cur = entry;
                if (entry.RowKind == QueueRowKind.Playable) return entry.Track;
            }
        }
        _cur = null;
        return null;   // EndOfContext — the reducer decides whether to request autoplay
    }

    public Track? Prev()
    {
        if (_cursor - 1 >= 0) { _cursor--; _cur = _context[_cursor]; }
        return _cur?.Track;
    }

    public Track? PeekNext()
    {
        if (_userQueue.Count > 0) return _userQueue[0].Track;
        int next = _cursor + 1;
        return next >= 0 && next < _context.Count ? _context[next].Track : null;
    }

    public void SetRepeat(RepeatMode m) => Repeat = m;

    public void SetShuffle(bool on)
    {
        if (on == Shuffle) return;
        Shuffle = on;
        if (on) ReshuffleAnchoringCurrent();
        else RestoreOriginalOrder();
    }

    static List<QueuedTrack> Wrap(IEnumerable<Track> tracks)
    {
        var list = tracks is ICollection<Track> c ? new List<QueuedTrack>(c.Count) : new List<QueuedTrack>();
        foreach (var t in tracks) list.Add(new QueuedTrack(t, ""));
        return list;
    }

    // Fisher–Yates over the NATURAL order, anchoring Current at logical 0 (deterministic LCG — no Random, replayable).
    // Anchor identity is the wrapped Track reference (each context slot holds a distinct Track instance).
    static QueuedTrack WithProvider(QueuedTrack track, string provider) =>
        track.Provider == provider ? track : track with { Provider = provider };

    void RememberCurrent()
    {
        if (_cur is not { } cur) return;
        if (_history.Count == 0 || !ReferenceEquals(_history[^1].Track, cur.Track))
            _history.Add(cur);
        if (_history.Count > 32) _history.RemoveRange(0, _history.Count - 32);
    }

    public IReadOnlyList<string> RecentUris(int max)
    {
        if (max <= 0) return Array.Empty<string>();
        var list = new List<string>(max);
        if (_cur is { } cur && !string.IsNullOrEmpty(cur.Track.Uri)) list.Add(cur.Track.Uri);
        for (int i = _history.Count - 1; i >= 0 && list.Count < max; i--)
            if (!string.IsNullOrEmpty(_history[i].Track.Uri)) list.Add(_history[i].Track.Uri);
        return list;
    }

    void ReshuffleAnchoringCurrent()
    {
        if (_original.Count <= 1 || _cur is not { } cur) return;
        var rest = new List<QueuedTrack>(_original.Count);
        foreach (var t in _original) if (!ReferenceEquals(t.Track, cur.Track)) rest.Add(t);
        for (int i = rest.Count - 1; i > 0; i--)
        {
            int j = NextSeed(i);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        _context.Clear();
        _context.Add(cur);
        _context.AddRange(rest);
        _cursor = 0;
    }

    // Turn shuffle OFF: restore the natural order and resume from Current's natural position.
    void RestoreOriginalOrder()
    {
        _context.Clear();
        _context.AddRange(_original);
        _cursor = _cur is { } cur ? _context.FindIndex(t => ReferenceEquals(t.Track, cur.Track)) : -1;
        if (_cursor < 0 && _context.Count > 0) _cursor = 0;
    }

    int NextSeed(int maxInclusive)
    {
        _seedState = unchecked(_seedState * 1103515245 + 12345) & 0x7fffffff;
        return _seedState % (maxInclusive + 1);
    }

    // The up-next the queue panel renders: now-playing, then the user queue (q#, drains first), then the CONTEXT next-up
    // (the upcoming context tracks after the cursor, in play order — capped so a 10k-track context doesn't realize a 10k list).
    public IReadOnlyList<QueueEntry> Snapshot()
    {
        const int NextUpCap = 50;
        var list = new List<QueueEntry>();
        if (_cur is { } cur)
            list.Add(new QueueEntry(QueueItemId.None, "now", cur.Track, QueueBucket.NowPlaying, QueueProviderExtensions.FromWire(cur.Provider), cur.Provider == "autoplay", cur.Uid, cur.Metadata));
        int i = 0;
        foreach (var t in _userQueue)
            list.Add(new QueueEntry(QueueItemId.None, $"q{i++}", t.Track, QueueBucket.UserQueue, QueueProviderExtensions.FromWire(t.Provider), t.Provider == "autoplay", t.Uid, t.Metadata));
        for (int c = _cursor + 1; c < _context.Count && list.Count <= NextUpCap; c++)
        {
            var t = _context[c];
            list.Add(new QueueEntry(QueueItemId.None, $"c{c}", t.Track, QueueBucket.NextUp, QueueProviderExtensions.FromWire(t.Provider), t.Provider == "autoplay", t.Uid, t.Metadata));
        }
        int firstHistory = Math.Max(0, _history.Count - 16);
        for (int h = firstHistory; h < _history.Count; h++)
        {
            var t = _history[h];
            list.Add(new QueueEntry(QueueItemId.None, $"h{h}", t.Track, QueueBucket.History, QueueProviderExtensions.FromWire(t.Provider), t.Provider == "autoplay", t.Uid, t.Metadata));
        }
        return list;
    }

}

// ── IAudioEngine — in-process decode/decrypt/output behind a swappable seam; STUBBED now (silent + stub decrypt) ───────
public interface IAudioEngine
{
    void Load(Track t);
    void Play();
    void Pause();
    void Stop();
    void Seek(long ms);
    long PositionMs { get; }
    bool IsPlaying { get; }
}

/// <summary>The one audio function the plan says to build now: a stub decrypt. Real engine = AES-128-CTR with the AudioKey;
/// here a passthrough so the pipeline shape exists and the reducer/projections are exercisable.</summary>
public static class StubCrypto
{
    public static ReadOnlySpan<byte> Decrypt(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> key) => encrypted;
}

public sealed class StubAudioEngine : IAudioEngine
{
    public Track? LoadedTrack { get; private set; }
    public string LastCmd { get; private set; } = "";
    public long PositionMs { get; private set; }
    public bool IsPlaying { get; private set; }

    public void Load(Track t) { LoadedTrack = t; PositionMs = 0; LastCmd = "load"; }
    public void Play() { IsPlaying = true; LastCmd = "play"; }
    public void Pause() { IsPlaying = false; LastCmd = "pause"; }
    public void Stop() { IsPlaying = false; LastCmd = "stop"; }
    public void Seek(long ms) { PositionMs = ms; LastCmd = "seek"; }
}

// ── Event log → projections ──────────────────────────────────────────────────────────────────────────────────────────
public enum EvKind { Started, Paused, Resumed, TrackChanged, Ended, Seeked, OptionsChanged, VolumeChanged, QueueChanged, BecameInactive }

public readonly record struct PlaybackEvent(
    EvKind Kind,
    Track? Track,
    long AtMs,
    PlaybackIds? Ids = null,
    string ReasonStart = "",
    string ReasonEnd = "",
    string SourceStart = "",
    byte[]? MediaId = null,
    int SelectedBitrateKbps = 0,
    string AudioFormatName = "",
    long DurationMs = 0,
    byte[]? FileId = null,
    string Provider = "context",
    bool IsPremium = true,
    /// <summary>Seek target in ms when <see cref="Kind"/> is <see cref="EvKind.Seeked"/>; <see cref="AtMs"/> is the
    /// pre-seek playhead used to close the outgoing segment.</summary>
    long SeekToMs = -1);

public interface IPlaybackProjection { void OnEvent(in PlaybackEvent e); }

/// <summary>The reducer: intents drive the pure QueueCore + the IAudioEngine, and emit a PlaybackEvent log that
/// independent projections consume (history/gabo/PutState/now-playing). The seam IPlaybackState is one such projection.</summary>
public sealed class PlaybackReducer
{
    readonly QueueCore _queue = new();
    readonly IAudioEngine _audio;
    readonly List<IPlaybackProjection> _projections = new();

    public PlaybackReducer(IAudioEngine audio) => _audio = audio;

    public bool IsPlaying { get; private set; }
    public Track? Current => _queue.Current;
    public string? ContextUri => _queue.ContextUri;
    public IReadOnlyList<QueueEntry> Queue => _queue.Snapshot();

    public void Subscribe(IPlaybackProjection p) => _projections.Add(p);

    void Emit(EvKind k)
    {
        var e = new PlaybackEvent(k, _queue.Current, _audio.PositionMs);
        foreach (var p in _projections) p.OnEvent(in e);
    }

    public void Play(string ctxUri, IEnumerable<Track> tracks, int start = 0)
    {
        _queue.SetContext(ctxUri, tracks, start);
        if (_queue.Current != null) { _audio.Load(_queue.Current); _audio.Play(); IsPlaying = true; Emit(EvKind.Started); }
    }

    public void Pause() { _audio.Pause(); IsPlaying = false; Emit(EvKind.Paused); }
    public void Resume() { _audio.Play(); IsPlaying = true; Emit(EvKind.Resumed); }

    public void Next()
    {
        var t = _queue.Next();
        if (t != null) { _audio.Load(t); _audio.Play(); IsPlaying = true; Emit(EvKind.TrackChanged); }
        else { _audio.Stop(); IsPlaying = false; Emit(EvKind.Ended); }
    }

    public void EnqueueUser(Track t) => _queue.EnqueueUser(t);
    public void SetShuffle(bool on) => _queue.SetShuffle(on);
    public void SetRepeat(RepeatMode m) => _queue.SetRepeat(m);
}

/// <summary>A projection: the play-history counter (the real one appends play_history + feeds PlayCount/recently-played/gabo).</summary>
public sealed class HistoryProjection : IPlaybackProjection
{
    public int Plays { get; private set; }
    public Track? LastStarted { get; private set; }

    public void OnEvent(in PlaybackEvent e)
    {
        if (e.Kind is EvKind.Started or EvKind.TrackChanged)
        {
            Plays++;
            LastStarted = e.Track;
        }
    }
}
