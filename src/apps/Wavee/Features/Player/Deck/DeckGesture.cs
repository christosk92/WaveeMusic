using System;
using FluentGpu.Media;

namespace Wavee;

/// <summary>
/// <c>SeekBar</c>'s scrub gesture, lifted out of the bar so a deck's own grip can drive it — the record's headshell
/// and the iPod's click wheel are seek gestures that happen not to be a slider.
///
/// <para><b>The contract is the two bridge signals, not this class.</b> A drag writes
/// <c>PlaybackBridge.ScrubTargetMs</c> (pointer-owned position: non-null MEANS a drag is live) and a release writes
/// a committed <c>SeekTargetMs</c> through <c>CommitSeek</c>. Deck models react to those two facts and know nothing
/// about pointers — which is why the tonearm state machine is unit-testable without an input stack.</para>
///
/// <para><b>Audible previews are throttled, not per-move.</b> <see cref="SeekPreviewScheduler"/> coalesces at 100 ms
/// and <see cref="Drain"/> is pumped by <c>DeckClock</c>'s existing 30 Hz tick, so a drag adds no second timer.
/// Previews are local-pipeline only (<c>CanPreviewSeek</c>); a Connect device commits on release.</para>
///
/// <para>ONE per <c>DeckHost</c>: a face reads <c>host.Gesture</c>, so a deck with two grips (wheel + rail) cannot
/// end up with two competing gestures.</para>
/// </summary>
sealed class DeckGesture
{
    PlaybackBridge? _b;
    SeekPreviewScheduler _previews;

    // The gesture's identity, captured at Begin. If ANY of the three moves the drag is stale: the pointer is now
    // dragging a position on a track that is no longer playing, and committing it would seek the WRONG song.
    string? _trackUri;
    long _generation;
    string? _device;

    long _startMs;
    bool _active;
    bool _previewIssued;

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
        var transport = b.Transport.Peek();
        _trackUri = b.CurrentTrack.Peek()?.Uri;
        _generation = transport.ItemGeneration;
        _device = _generation > 0 ? null : b.ActiveDeviceId.Peek();
        _startMs = Clamp(b, startMs);
        _previews.Reset();
        _previewIssued = false;
        _active = true;
        Publish(b, _startMs);
    }

    /// <summary>The grip moved to <paramref name="ms"/>.</summary>
    public void Move(long ms)
    {
        if (_b is not { } b || !Current(b)) return;
        Publish(b, Clamp(b, ms));
    }

    /// <summary>Released at <paramref name="ms"/>: discard anything still queued and commit exactly one seek.</summary>
    public void Commit(long ms)
    {
        if (_b is not { } b) { Release(null); return; }
        if (!Current(b)) { Release(b); return; }
        long target = Clamp(b, ms);
        _previews.DiscardPending();
        b.CommitSeek(target);
        Release(b);
    }

    /// <summary>Aborted (Escape, a lost capture, the track changing under the finger). If audible previews already
    /// moved the playhead, put it back where the grip was taken — otherwise the cancel would silently be a seek.</summary>
    public void Cancel()
    {
        var b = _b;
        bool restore = b is not null && _previewIssued && Current(b);
        long original = _startMs;
        Release(b);
        if (restore && b is not null) b.CommitSeek(original);
    }

    /// <summary>Pumped by <c>DeckClock</c> each tick: hand the throttle its due sample, if any.</summary>
    public void Drain()
    {
        if (_b is not { } b || !Current(b)) return;
        if (!_previews.TryTake(Environment.TickCount64, out long target)) return;
        _previewIssued = true;
        b.PreviewSeek(target);
    }

    void Publish(PlaybackBridge b, long target)
    {
        b.ScrubTargetMs.Value = target;
        if (b.CanPreviewSeek) _previews.Queue(target);
        Drain();
    }

    void Release(PlaybackBridge? b)
    {
        _previews.Reset();
        _trackUri = null;
        _previewIssued = false;
        _active = false;
        if (b is not null) b.ScrubTargetMs.Value = null;
    }

    // Peek-only (these run from pointer handlers and the ticker, never from a render).
    static bool Enabled(PlaybackBridge b) => b.CurrentTrack.Peek() is not null && b.Error.Peek() is null && b.CanSeek.Peek();

    bool Current(PlaybackBridge b)
    {
        if (!_active || !Enabled(b)) return false;
        var transport = b.Transport.Peek();
        // A LOCAL session owns its own generation, so the device is irrelevant (and would flap); a remote one has no
        // generation, so the device id is the only identity there is. Same split as SeekBar's gesture guard.
        string? device = transport.ItemGeneration > 0 ? null : b.ActiveDeviceId.Peek();
        return string.Equals(_trackUri, b.CurrentTrack.Peek()?.Uri, StringComparison.Ordinal)
            && _generation == transport.ItemGeneration
            && string.Equals(_device, device, StringComparison.Ordinal);
    }

    // A deck draws a TRACK's medium: no DVR arm (a live broadcast has no groove to be a fraction of), so the clamp
    // is the plain 0..duration one. An unknown duration clamps at zero only.
    static long Clamp(PlaybackBridge b, long ms)
    {
        long dur = b.DurationMs.Peek();
        return dur > 0 ? Math.Clamp(ms, 0, dur) : Math.Max(0, ms);
    }
}
