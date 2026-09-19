using System;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>
/// <c>SeekBar</c>'s scrub gesture, lifted out of the bar so a deck's own grip can drive it — the record's headshell
/// and the iPod's click wheel are seek gestures that happen not to be a slider.
///
/// <para><b>The contract is the two bridge signals, not this class.</b> A drag writes
/// <c>PlaybackBridge.ScrubTargetMs</c> (pointer-owned position: non-null MEANS a drag is live) and a release writes
/// a committed <c>SeekTargetMs</c> and routes through <c>PlaybackBridge.CommitSeek</c> (the ONE commit path: it arms
/// the bridge's latch and publishes the position optimistically). Deck models react to those two facts and know
/// nothing about pointers — which is why the tonearm state machine is unit-testable without an input stack.</para>
///
/// <para><b>No audible preview while dragging.</b> The 0.2.9 bridge exposes only the committed (accurate) seek, so a
/// drag moves the medium visually and the audio follows on release — exactly the seek bar's behaviour here.</para>
///
/// <para>ONE per <c>DeckHost</c>: a face reads <c>host.Gesture</c>, so a deck with two grips (wheel + rail) cannot
/// end up with two competing gestures.</para>
/// </summary>
sealed class DeckGesture
{
    PlaybackBridge? _b;

    // The gesture's identity, captured at Begin. If either moves the drag is stale: the pointer is now dragging a
    // position on a track that is no longer playing, and committing it would seek the WRONG song.
    string? _trackUri;
    string? _device;

    long _startMs;
    bool _active;

    /// <summary>A drag is live right now.</summary>
    public bool Active => _active;

    /// <summary>Wire the bridge (the host does this every render, before it builds the face). Until then every verb
    /// is inert, which is what a deck rendered without a bridge needs.</summary>
    public void Attach(PlaybackBridge bridge) => _b = bridge;

    /// <summary>Grip taken at <paramref name="startMs"/>. Captures the gesture identity and immediately publishes
    /// the pointer-owned position, so the medium follows the finger from the first frame.</summary>
    public void Begin(long startMs)
    {
        if (_b is not { } b || !Enabled(b)) return;
        _trackUri = b.CurrentTrack.Peek()?.Uri;
        _device = b.ActiveDeviceId.Peek();
        _startMs = Clamp(b, startMs);
        _active = true;
        b.ScrubTargetMs.Value = _startMs;
    }

    /// <summary>The grip moved to <paramref name="ms"/>.</summary>
    public void Move(long ms)
    {
        if (_b is not { } b || !Current(b)) return;
        b.ScrubTargetMs.Value = Clamp(b, ms);
    }

    /// <summary>Released at <paramref name="ms"/>: commit exactly one seek.</summary>
    public void Commit(long ms)
    {
        if (_b is not { } b) { Release(null); return; }
        if (!Current(b)) { Release(b); return; }
        long target = Clamp(b, ms);
        b.SeekTargetMs.Value = target;   // the deck keeps the arm on the target until the reported position lands
        b.CommitSeek(target);
        Release(b);
    }

    /// <summary>Aborted (Escape, a lost capture, the track changing under the finger). Nothing audible happened while
    /// dragging, so there is nothing to put back: the scrub position simply goes away.</summary>
    public void Cancel() => Release(_b);

    /// <summary>Pumped by <c>DeckClock</c> each tick. Kept for the clock's contract; there is no preview throttle to
    /// drain on this bridge.</summary>
    public void Drain() { }

    void Release(PlaybackBridge? b)
    {
        _trackUri = null;
        _device = null;
        _active = false;
        if (b is not null) b.ScrubTargetMs.Value = null;
    }

    // Peek-only (these run from pointer handlers and the ticker, never from a render).
    static bool Enabled(PlaybackBridge b) => b.CurrentTrack.Peek() is not null && b.Error.Peek() is null && b.CanSeek.Peek();

    bool Current(PlaybackBridge b)
        => _active && Enabled(b)
           && string.Equals(_trackUri, b.CurrentTrack.Peek()?.Uri, StringComparison.Ordinal)
           && string.Equals(_device, b.ActiveDeviceId.Peek(), StringComparison.Ordinal);

    // A deck draws a TRACK's medium: no DVR arm (a live broadcast has no groove to be a fraction of), so the clamp
    // is the plain 0..duration one. An unknown duration clamps at zero only.
    static long Clamp(PlaybackBridge b, long ms)
    {
        long dur = b.DurationMs.Peek();
        return dur > 0 ? Math.Clamp(ms, 0, dur) : Math.Max(0, ms);
    }
}
