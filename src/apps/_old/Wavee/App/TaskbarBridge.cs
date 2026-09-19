using System;
using System.IO;
using System.Runtime.Versioning;
using FluentGpu;
using FluentGpu.WindowsApi.Shell;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// Taskbar-button mirror of the unified now-playing state: determinate progress, a pause overlay while audio is
/// paused, and a three-button thumbnail toolbar (prev / play-pause / next). Playing uses the progress bar alone —
/// a play glyph on top of the app icon is redundant with the fill. Sibling of <see cref="SystemMediaControlsBridge"/> —
/// same owner (<see cref="PlaybackBridge"/>), same UI-thread + HWND contract, same fail-soft / edge-dedupe discipline.
/// <para>
/// Progress is coalesced through <see cref="SmtcTimelineCoalescer"/> so a backlog of position ticks pays one
/// <c>ITaskbarList3.SetProgressValue</c> (~1 Hz). Overlay path strings and tooltips are resolved once at
/// <see cref="Activate"/>; the steady-state tick allocates nothing.
/// </para>
/// </summary>
[SupportedOSPlatform("windows6.1")]
public sealed class TaskbarBridge : IDisposable
{
    const int IdPrev = 1, IdPlayPause = 2, IdNext = 3;

    readonly PlaybackBridge _bridge;
    readonly IPlaybackPlayer _player;
    readonly Action<Action> _post;

    nint _hwnd;
    bool _active, _disposed, _thumbsAdded;

    // Overlay / progress / thumb edge-dedupe — only a real change touches the shell.
    OverlayKind _lastOverlay = OverlayKind.Unset;
    ProgressKind _lastProgress = ProgressKind.Unset;
    bool _lastCanPrev, _lastCanNext, _lastPlaying, _lastHasTrack;
    bool _haveThumbState;

    SmtcTimelineCoalescer _timeline;
    readonly Action _flushTimeline;

    // Resolved once; null = file missing (tooltips still work, glyphs are skipped).
    string? _prevIco, _playIco, _pauseIco, _nextIco;

    public TaskbarBridge(PlaybackBridge bridge, IPlaybackPlayer player, Action<Action> post)
    {
        _bridge = bridge;
        _player = player;
        _post = post;
        _flushTimeline = FlushProgress;
    }

    /// <summary>Wire the thumbnail toolbar on <paramref name="hwnd"/> (<c>FluentApp.WindowHandle</c>) and subscribe
    /// the shell click / explorer-restart events. UI-thread only. A zero handle leaves the bridge inert. Idempotent.</summary>
    public void Activate(nint hwnd)
    {
        if (_active || _disposed || hwnd == 0) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        _hwnd = hwnd;
        _prevIco = ResolveIcon("prev.ico");
        _playIco = ResolveIcon("play.ico");
        _pauseIco = ResolveIcon("pause.ico");
        _nextIco = ResolveIcon("next.ico");
        try
        {
            FluentApp.ThumbButtonClicked += OnThumbClick;
            FluentApp.TaskbarButtonCreated += OnTaskbarCreated;
            ApplyThumbs(forceAdd: true);
            _active = true;
            OnStateChanged();
            OnPositionChanged(_bridge.PositionMs.Peek());
        }
        catch (Exception)
        {
            // Shell refused the toolbar — stay inert; playback is unaffected.
            _active = false;
            _hwnd = 0;
        }
    }

    /// <summary>Push overlay + progress mode + thumb enablement. Called from <see cref="PlaybackBridge"/> on the UI
    /// thread. Only differences from the last push touch the shell — no string alloc on a no-op tick.</summary>
    public void OnStateChanged()
    {
        if (!_active || _disposed || _hwnd == 0) return;

        var track = _bridge.CurrentTrack.Peek();
        bool hasTrack = track is not null;
        bool playing = hasTrack && _bridge.IsPlaying.Peek();
        bool canPrev = _bridge.CanSkipPrev.Peek();
        bool canNext = _bridge.CanSkipNext.Peek();

        // Playing: no overlay — the determinate progress bar is the playing cue. Paused-with-a-track: pause glyph.
        var overlay = hasTrack && !playing ? OverlayKind.Pause : OverlayKind.None;
        if (overlay != _lastOverlay)
        {
            _lastOverlay = overlay;
            try
            {
                if (overlay == OverlayKind.None)
                    TaskbarManager.SetOverlayIcon(_hwnd, null, "");
                else if (_pauseIco is not null)
                    TaskbarManager.SetOverlayIcon(_hwnd, _pauseIco, "Paused");
                else
                    TaskbarManager.SetOverlayIcon(_hwnd, null, "");
            }
            catch (Exception) { /* a missing/unloadable .ico must never break playback */ }
        }

        var progress = !hasTrack ? ProgressKind.Idle : playing ? ProgressKind.Playing : ProgressKind.Paused;
        if (progress != _lastProgress)
        {
            _lastProgress = progress;
            try
            {
                if (progress == ProgressKind.Idle)
                    TaskbarManager.ClearProgress(_hwnd);
                else
                    TaskbarManager.SetProgressState(_hwnd, progress == ProgressKind.Playing
                        ? TaskbarProgressState.Normal
                        : TaskbarProgressState.Paused);
            }
            catch (Exception) { }
        }

        if (!_haveThumbState || canPrev != _lastCanPrev || canNext != _lastCanNext
            || playing != _lastPlaying || hasTrack != _lastHasTrack)
        {
            _lastCanPrev = canPrev;
            _lastCanNext = canNext;
            _lastPlaying = playing;
            _lastHasTrack = hasTrack;
            _haveThumbState = true;
            ApplyThumbs(forceAdd: false);
        }
    }

    /// <summary>Latch a position tick. The Win32 progress put is deferred to one posted flush per burst (same coalescer
    /// as SMTC). Zero-alloc: a struct latch and a cached flush delegate.</summary>
    public void OnPositionChanged(long positionMs)
    {
        if (!_active || _disposed || _hwnd == 0) return;
        if (_lastProgress == ProgressKind.Idle) return;
        if (_timeline.Push(positionMs)) _post(_flushTimeline);
    }

    void FlushProgress()
    {
        if (!_active || _disposed || _hwnd == 0) return;
        long dur = _bridge.DurationMs.Peek();
        if (!_timeline.TryTake(dur, out long pos)) return;
        try { TaskbarManager.SetProgress(_hwnd, (ulong)pos, (ulong)dur); } catch (Exception) { }
    }

    void ApplyThumbs(bool forceAdd)
    {
        if (_hwnd == 0) return;
        bool playing = _lastPlaying;
        bool hasTrack = _lastHasTrack;
        var prev = new ThumbButton(IdPrev, _prevIco, "Previous", Enabled: _lastCanPrev);
        var mid = new ThumbButton(IdPlayPause, playing ? _pauseIco : _playIco,
            playing ? "Pause" : "Play", Enabled: hasTrack);
        var next = new ThumbButton(IdNext, _nextIco, "Next", Enabled: _lastCanNext);
        try
        {
            if (forceAdd || !_thumbsAdded)
            {
                TaskbarManager.SetThumbButtons(_hwnd, prev, mid, next);
                _thumbsAdded = true;
            }
            else
            {
                TaskbarManager.UpdateThumbButton(_hwnd, prev);
                TaskbarManager.UpdateThumbButton(_hwnd, mid);
                TaskbarManager.UpdateThumbButton(_hwnd, next);
            }
        }
        catch (Exception)
        {
            // Bad icon path / add-too-early — retry without glyphs so tooltips still land.
            try
            {
                TaskbarManager.SetThumbButtons(_hwnd,
                    new ThumbButton(IdPrev, null, "Previous", Enabled: _lastCanPrev),
                    new ThumbButton(IdPlayPause, null, playing ? "Pause" : "Play", Enabled: hasTrack),
                    new ThumbButton(IdNext, null, "Next", Enabled: _lastCanNext));
                _thumbsAdded = true;
            }
            catch (Exception) { }
        }
    }

    void OnThumbClick(int id)
    {
        if (_disposed) return;
        switch (id)
        {
            case IdPrev: _ = _player.PreviousAsync(); break;
            case IdPlayPause:
                if (_bridge.IsPlaying.Peek()) _ = _player.PauseAsync();
                else _ = _player.ResumeAsync();
                break;
            case IdNext: _ = _player.NextAsync(); break;
        }
    }

    void OnTaskbarCreated()
    {
        if (_disposed || _hwnd == 0) return;
        try
        {
            TaskbarManager.NotifyTaskbarButtonCreated(_hwnd);
            _thumbsAdded = false;
            ApplyThumbs(forceAdd: true);
        }
        catch (Exception) { }
    }

    static string? ResolveIcon(string fileName)
    {
        try
        {
            string rel = Path.Combine("assets", "taskbar", fileName);
            string a = Path.Combine(AppContext.BaseDirectory, rel);
            if (File.Exists(a)) return a;
        }
        catch (Exception) { }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { FluentApp.ThumbButtonClicked -= OnThumbClick; } catch (Exception) { }
        try { FluentApp.TaskbarButtonCreated -= OnTaskbarCreated; } catch (Exception) { }
        if (_hwnd != 0)
        {
            try { TaskbarManager.SetOverlayIcon(_hwnd, null, ""); } catch (Exception) { }
            try { TaskbarManager.ClearProgress(_hwnd); } catch (Exception) { }
        }
        _hwnd = 0;
        _active = false;
    }

    enum OverlayKind : byte { Unset, None, Pause }
    enum ProgressKind : byte { Unset, Idle, Playing, Paused }
}
