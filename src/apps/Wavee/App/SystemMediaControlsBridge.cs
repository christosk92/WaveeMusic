using System;
using System.Runtime.Versioning;
using FluentGpu.WindowsApi.Media;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The System Media Transport Controls (SMTC) integration for Wavee: it mirrors the app's UNIFIED now-playing state
/// (the <see cref="PlaybackBridge"/> signals, which fold local-engine AND remote Connect playback into one truth) onto
/// the OS media surfaces — the Windows now-playing flyout, the lock screen, and the hardware media keys / headset
/// buttons — and routes the transport buttons the OS raises back into <see cref="IPlaybackPlayer"/> intents.
/// <para>
/// It reads the bridge's own UI-facing signals (never the engine <c>IMediaPlayer</c> directly): those signals are the
/// single source that is CORRECT whether Wavee is the active local player or a viewer of a remote Connect device, and
/// they are already marshalled onto the UI thread. This is why the SMTC source is the bridge and not the engine's
/// <c>NowPlaying</c> surface (which is dark whenever another Connect device owns playback).
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Activate"/> and the <c>OnStateChanged</c>/<c>OnPositionChanged</c> pushes are all
/// invoked on the UI thread that owns the window handle (from <see cref="PlaybackBridge"/>'s post-marshalled callbacks),
/// which is exactly what <see cref="SystemMediaControls"/> requires. The OS raises <c>ButtonPressed</c> on an arbitrary
/// worker thread, so the button dispatcher is set to the same UI-thread post delegate; the transport intent then runs
/// on the UI thread.
/// </para>
/// <para>
/// <b>Fail-soft.</b> If the platform refuses SMTC (older OS, interop failure) construction is swallowed and every push
/// becomes a no-op — the app keeps working with no OS media surface, never crashing.
/// </para>
/// </summary>
[SupportedOSPlatform("windows8.0")]
public sealed class SystemMediaControlsBridge : IDisposable
{
    readonly PlaybackBridge _bridge;
    readonly IPlaybackPlayer _player;
    readonly Action<Action> _post;

    SystemMediaControls? _smtc;
    bool _disposed;

    // Edge-dedupe so a state tick that didn't change what the OS shows makes no WinRT call.
    string? _lastUri = null;
    MediaPlaybackStatus _lastStatus = (MediaPlaybackStatus)(-1);
    bool _lastCanNext = true, _lastCanPrev = true;
    // The timeline's dedupe moved INTO the coalescer (it still carries the whole-second `_lastTimelineSec` rule as its
    // steady-state gate) so the burst latch and the per-second gate can't disagree. See SmtcTimelineCoalescer.
    SmtcTimelineCoalescer _timeline;
    readonly Action _flushTimeline;   // cached once: the per-tick path must not allocate a delegate
    // Has the OS timeline already been zeroed for the current live broadcast? One cross-process COM call per transition
    // into live, not one per position tick for the whole stream.
    bool _liveTimelineCleared;

    public SystemMediaControlsBridge(PlaybackBridge bridge, IPlaybackPlayer player, Action<Action> post)
    {
        _bridge = bridge;
        _player = player;
        _post = post;
        _flushTimeline = FlushTimeline;
    }

    /// <summary>Acquire the SMTC for <paramref name="hwnd"/> (the real top-level window handle — pass
    /// <c>FluentApp.WindowHandle</c>) and wire the transport buttons. UI-thread only. A zero handle or an unsupported
    /// platform leaves the bridge inert (no OS surface, no throw). Idempotent — a second call is a no-op.</summary>
    public void Activate(nint hwnd)
    {
        if (_smtc is not null || _disposed || hwnd == 0) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(8, 0)) return;
        try
        {
            var smtc = SystemMediaControls.GetForWindow(hwnd);
            smtc.ButtonDispatcher = _post;   // OS worker thread → UI thread (the same post the bridge marshals on)
            smtc.ButtonPressed += OnButton;
            smtc.PositionChangeRequested += OnSeek;   // lock-screen / flyout scrub-bar drag → SeekAsync (best-effort seam)
            smtc.SetEnabledButtons(play: true, pause: true, next: true, previous: true);
            smtc.PlaybackRate = 1.0;         // must be > 0; Spotify content is normal speed (spoken-word rate is not surfaced here)
            smtc.IsEnabled = true;
            _smtc = smtc;
            // Seed the OS surface from whatever is playing right now (the bridge was activated first).
            OnStateChanged();
            OnPositionChanged(_bridge.PositionMs.Peek());
        }
        catch (Exception)
        {
            // Platform refused SMTC (older OS / interop) — stay inert; the app is unaffected.
            _smtc = null;
        }
    }

    /// <summary>Push the current now-playing metadata + play status + transport availability to the OS. Called from
    /// <see cref="PlaybackBridge"/> whenever the unified state changes (already on the UI thread). Only differences from
    /// the last push touch the OS.</summary>
    public void OnStateChanged()
    {
        if (_smtc is not { } smtc || _disposed) return;

        var track = _bridge.CurrentTrack.Peek();
        string uri = track?.Uri ?? "";
        if (!string.Equals(uri, _lastUri, StringComparison.Ordinal))
        {
            _lastUri = uri;
            try
            {
                if (track is null)
                {
                    smtc.ClearDisplay();
                }
                else
                {
                    string artist = track.Artists.Count > 0 ? track.Artists[0].Name : "";
                    string? album = string.IsNullOrEmpty(track.Album.Name) ? null : track.Album.Name;
                    string? art = string.IsNullOrEmpty(track.Image?.Url) ? null : track.Image!.Url;
                    smtc.UpdateDisplay(track.Title ?? "", artist, album, art);
                }
            }
            catch (Exception) { /* a bad art URL / transient WinRT failure must never break playback */ }
        }

        var status = track is null ? MediaPlaybackStatus.Closed
            : _bridge.IsBuffering.Peek() ? MediaPlaybackStatus.Changing
            : _bridge.IsPlaying.Peek() ? MediaPlaybackStatus.Playing
            : MediaPlaybackStatus.Paused;
        if (status != _lastStatus)
        {
            _lastStatus = status;
            try { smtc.SetPlaybackStatus(status); } catch (Exception) { }
        }

        bool canNext = _bridge.CanSkipNext.Peek(), canPrev = _bridge.CanSkipPrev.Peek();
        if (canNext != _lastCanNext || canPrev != _lastCanPrev)
        {
            _lastCanNext = canNext;
            _lastCanPrev = canPrev;
            try { smtc.SetEnabledButtons(play: true, pause: true, next: canNext, previous: canPrev); } catch (Exception) { }
        }
    }

    /// <summary>Push the timeline (position + duration) so the Win11 flyout / lock-screen scrub bar tracks playback.
    /// Called from <see cref="PlaybackBridge"/> on each position tick (UI thread); throttled to whole-second changes so
    /// it stays the ~1 Hz cadence the OS expects. Seek-DRAG on the OS scrub bar arrives via
    /// <see cref="SystemMediaControls.PositionChangeRequested"/> → <see cref="OnSeek"/> (a best-effort seam).
    /// <para>
    /// COALESCED (newest wins): the tick itself only latches — the actual <c>UpdateTimeline</c> (a WinRT activation plus a
    /// CROSS-PROCESS COM RPC, ~1 ms) is deferred to one posted flush per burst. A frame that drains a BACKLOG of queued
    /// position ticks — each carrying a different whole second, so the per-second dedupe never fired — used to pay that
    /// RPC once per tick, synchronously on the UI thread. Now it pays it once, at the top of the next frame, with the
    /// newest position. Zero-alloc per tick: a struct latch and a cached flush delegate.
    /// </para></summary>
    public void OnPositionChanged(long positionMs)
    {
        if (_smtc is null || _disposed) return;
        if (_timeline.Push(positionMs)) _post(_flushTimeline);
    }

    // The one deferred timeline push per burst. Posted (never called inline), so it runs on the UI thread at the top of a
    // later frame — by which time the whole burst has been latched and only the newest position survives. TryTake always
    // clears the scheduled bit, so a bail-out here can never wedge the latch armed.
    void FlushTimeline()
    {
        var smtc = _smtc;
        long dur = smtc is null || _disposed ? 0 : _bridge.DurationMs.Peek();

        // A LIVE broadcast has no timeline to publish, and the OS has no way to render one that is honest — the Win11
        // flyout draws a fixed-length scrub bar or nothing. Leaving the PREVIOUS track's timeline up is the failure that
        // matters: the coalescer already declines to push at duration 0, so without this the flyout would keep showing
        // the last song's 3:47 with a thumb frozen mid-bar while a six-hour stream plays. Push one explicit zero
        // timeline on the transition into live and then stay quiet.
        if (dur <= 0 && _bridge.IsLive.Peek())
        {
            _timeline.TryTake(dur, out _);   // always consume the latch, so the scheduled-flush bit cannot wedge armed
            if (_liveTimelineCleared || smtc is null || _disposed) return;
            _liveTimelineCleared = true;
            try { smtc.UpdateTimeline(TimeSpan.Zero, TimeSpan.Zero); } catch (Exception) { }
            return;
        }
        _liveTimelineCleared = false;

        if (!_timeline.TryTake(dur, out long pos)) return;
        try { smtc!.UpdateTimeline(TimeSpan.FromMilliseconds(pos), TimeSpan.FromMilliseconds(dur)); } catch (Exception) { }
    }

    // The OS/user pressed a transport button. Routed to the UI thread by ButtonDispatcher, then translated into a player
    // intent (the same intents the on-screen transport buttons fire). Seek is not carried by SMTC ButtonPressed.
    void OnButton(MediaButton button)
    {
        if (_disposed) return;
        // A hardware media key, a headset AVRCP event, or another app poking the OS flyout lands here and moves playback
        // with NOTHING on screen and (until now) nothing in the log — indistinguishable from a spontaneous jump. One Info
        // line per button press (a human-rate event) makes an unexplained transport change attributable.
        WaveeLog.Instance.Info("playback", "smtc transport button: " + button);
        switch (button)
        {
            case MediaButton.Play: _ = _player.ResumeAsync(); break;
            case MediaButton.Pause: _ = _player.PauseAsync(); break;
            case MediaButton.Next: _ = _player.NextAsync(); break;
            case MediaButton.Previous: _ = _player.PreviousAsync(); break;
            case MediaButton.Stop: _ = _player.PauseAsync(); break;   // no Stop verb in the player — pause is the closest
            default: break;   // Unknown (record/ff/rewind/channel) — ignore
        }
    }

    // The user dragged the OS scrub bar (lock screen / SMTC flyout). Routed to the UI thread by the same dispatcher,
    // then translated into a seek intent. The requested position arrives in SECONDS; clamp to the known duration.
    void OnSeek(double seconds)
    {
        if (_disposed) return;
        long ms = (long)Math.Round(seconds * 1000.0);
        long dur = _bridge.DurationMs.Peek();
        ms = dur > 0 ? Math.Clamp(ms, 0, dur) : Math.Max(0, ms);
        // The OS scrub bar reports one position on release, not a scrub stream — a committed seek. Route through
        // CommitSeek (not a bare _player.SeekAsync) so the seek latch is armed here too — without it, a scrub from
        // the lock screen while paused snapped back to the pre-seek position until the next authoritative tick.
        _bridge.CommitSeek(ms);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var smtc = _smtc;
        _smtc = null;
        if (smtc is not null)
        {
            try { smtc.ButtonPressed -= OnButton; } catch (Exception) { }
            try { smtc.PositionChangeRequested -= OnSeek; } catch (Exception) { }
            try { smtc.IsEnabled = false; } catch (Exception) { }
            try { smtc.Dispose(); } catch (Exception) { }
        }
    }
}
