// ── Playback/Playback.EndHold.cs ───────────────────────────────────────────────────────────────────────────────────
// THE END HOLD: the full screen's "Up next in 5" between a music video's natural end and the advance
//
// Role: CORE (the reducer's half: pure, clock-free) + the two host verbs and the stage's offer
// Spec: the plan "video full screen", Part 3, step 3.4 (owner decision 2026-11-10: "Countdown, video only")
//
// ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// WHEN. A video's natural end (`DoEnded`) normally advances in the same drain: a hard cut to the next row. The ONE guarded
// branch (`HoldsEnd`) takes a hold instead when ALL of these hold at that moment: the stage shows the picture in this window
// (Video mode with a video face, offered only by a structure that states `StagePolicy.VideoStage`: `OfferEndHold`), the setting
// "Count down before the next video" is on, the deck row played AS VIDEO here (`State.Kind`), playback routes locally (no remote
// device owns it), the deck is healthy (no fault, not parked), repeat-one is off (it replays the same row: there is no "next"), and
// a next row is in the queue NOW, after the deck's (`NaturalNext`: the queue, an autoplay row already appended; a wrap under repeat-context
// goes round to rows the card's queue no longer lists, so it takes today's path). An
// autoplay continuation that is not resolved yet takes today's path (`EndOfContext` waits for it and advances the moment it lands):
// there is nothing to show on a card, and the hold never holds on nothing. Everything else (an audio track, a video docked or in the
// mini player, a video under the audio stage, a skip, the last row) never reaches the branch.
//
// THE STATE ("ended, waiting"). The deck stays on the ended row, PAUSED at its end (the pump has stopped there; the host is paused so a
// player that would loop or report cannot move it), the play registration still open (the advance closes it with the row's duration, as
// it would have). `Hold` is Counting while the card counts down and Waiting once the countdown was cancelled (the card is gone). The
// transport, the OS card, a controller and the media keys all see an ordinary paused row at its end. Each action, deliberately:
//   Play / Space / the media key's play / a controller's resume   → the advance now ("play now"): the only thing to play is the next row
//   Next (the bar, a key, a controller's skip without a named row)  → the advance now, as the natural end (the row DID end; no skip report)
//   the card's Play now · the countdown elapsing                    → the advance now (`ReleaseEndHold`, stamped: a stale timer is a no-op)
//   the card's Cancel · the stage closing · leaving Video mode       → Counting → Waiting: no advance; Play later starts the next row
//   Pause · shuffle · repeat · a scrub's press · sleep · stop · a transfer → Counting → Waiting, then the action runs as usual
//   Previous · a seek · a row played from anywhere                   → the hold ends and the action runs as on any paused row
//   a remote device taking playback · any new load · a fault         → the hold ends (the invariant after every Step)
// The pump's own reports for the held load are dropped: the stream is over, and a late Started/Resumed must not un-pause the deck.
// The countdown's clock is the stage's (one timer, `Stage.SurfaceCore`), armed from `State.HoldAtMs` on the frame clock, so a re-render
// or a remount re-arms it for what is left, never from the top; the app shutting down takes it with it (the hold is never persisted).

using RepeatMode = Wavee.Spotify.Decode.RepeatMode;

namespace Wavee;

public static partial class Playback
{
    /// <summary>Where the end hold stands (<see cref="State.Hold"/>).</summary>
    public enum EndHold : byte
    {
        /// <summary>No hold: every end advances at once.</summary>
        None,
        /// <summary>A video ended in full screen; the card counts down and the advance runs when it elapses.</summary>
        Counting,
        /// <summary>The countdown was cancelled: the deck rests on the ended row's end; Play or Next starts the next row.</summary>
        Waiting,
    }

    /// <summary>How long the card counts down before the next video plays.</summary>
    public const int EndHoldMs = 5_000;

    /// <summary>The hold as the shell reads it: the phase and the frame-clock stamp the countdown started at (0: none).</summary>
    public readonly record struct EndHoldFacts(EndHold Phase, long AtMs)
    {
        /// <summary>What is left of the countdown at <paramref name="nowMs"/> (frame clock), 0 when it is not counting.</summary>
        public int RemainingMs(long nowMs) => Phase == EndHold.Counting ? (int)Math.Clamp(AtMs + EndHoldMs - nowMs, 0L, EndHoldMs) : 0;
    }

    /// <summary>The hold, published once per drain (<see cref="Publish"/>).</summary>
    public static readonly FluentGpu.Signals.Signal<EndHoldFacts> EndHoldNow = new(default) { DebugName = "playback.endHold" };

    /// <summary>The stage shows the picture in this window (Video mode, a video face) in a structure that draws the card: the shell's half of
    /// the hold's condition, written by the stage. UI thread (the reducer reads it in the drain, on the same thread).</summary>
    static bool s_endHoldStage;

    /// <summary>The stage offers (or stops offering) the hold. Withdrawing it while the card counts cancels the countdown (the stage closed,
    /// Video mode left, the audio face took the picture's place): the deck stays at the ended row's end.</summary>
    public static void OfferEndHold(bool on)
    {
        s_endHoldStage = on;
        if (!on && s_state.Hold == EndHold.Counting) Post(Input.EndHoldCancel(FrameNowMs()));
    }

    /// <summary>The countdown elapsed, or the card's Play now: run the advance the natural end deferred. <paramref name="atMs"/> is the hold's
    /// stamp (<see cref="EndHoldFacts.AtMs"/>): a release for a hold that has already ended is dropped.</summary>
    public static void ReleaseEndHold(long atMs) => Post(Input.EndHoldRelease(atMs, FrameNowMs()));

    /// <summary>The card's Cancel: the countdown stops, the deck rests on the ended row's end.</summary>
    public static void CancelEndHold() => Post(Input.EndHoldCancel(FrameNowMs()));

    /// <summary>The card's setting: "Count down before the next video" (on by default).</summary>
    static bool EndHoldSettingOn() => Platform.Settings.Get(Platform.Keys.VideoEndCountdown);

    // ── the reducer's half ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>THE guarded branch's test: does this natural end hold for the card? Read in <see cref="DoEnded"/> only.</summary>
    static bool HoldsEnd(in State s)
    {
        if (s.Hold != EndHold.None || s.Kind != PlayableKind.Video || !s.RoutesLocal || !s.HasCurrent || s.Error != Fault.None || s.Parked
            || s.Repeat == RepeatMode.Track || !s_endHoldStage || !EndHoldSettingOn())
            return false;
        // a next row AFTER the deck's (a wrap under repeat-context goes round to rows the card's queue no longer lists: today's path)
        return !NaturalNext(in s, out QueueCursor next).IsNone && next.Index > s.Cursor.Index;
    }

    /// <summary>Take the hold: the deck rests PAUSED at the ended row's end, the registration stays open for the advance to close.</summary>
    static void TakeHold(ref State s, in Input i, ref Effects fx)
    {
        s.Hold = EndHold.Counting;
        s.HoldAtMs = Math.Max(1L, i.NowMs);
        s.HoldLoadEpoch = s.LoadEpoch;
        s.PosMs = s.DurationMs > 0 ? s.DurationMs : s.Position(i.NowMs);
        s.PosQpc = i.NowMs;
        s.Phase = Phase.Paused;
        s.Buffering = false;
        Bump(ref s);
        fx.PauseHost = true;                             // the stream is over; a player that would loop or report stays where it ended
        fx.TransportEpoch = s.Epoch;
        Announce(ref s, ref fx, PublishReason.PlayerStateChanged);
        fx.Smtc = true;
        fx.SmtcEpoch = s.Epoch;
    }

    /// <summary>The deferred advance: exactly the natural end's (<c>Advance(TrackDone)</c>).</summary>
    static void ReleaseHold(ref State s, in Input i, ref Effects fx)
    {
        s.Hold = EndHold.None;
        Advance(ref s, in i, ref fx, forward: true, PlayReason.TrackDone);
    }

    /// <summary><see cref="InputKind.EndHold"/>: a cancel (IntArg 0) stops the countdown; a release (IntArg 1) for THIS hold advances.</summary>
    static void DoEndHold(ref State s, in Input i, ref Effects fx)
    {
        if (s.Hold != EndHold.Counting) return;
        if (i.IntArg == 0) { s.Hold = EndHold.Waiting; return; }
        if (i.LongArg != s.HoldAtMs) return;             // a timer for a hold that has ended
        ReleaseHold(ref s, in i, ref fx);
    }

    /// <summary>Before the Step, while a hold is up: the held load's pump reports are dropped (true), and the actions that cancel the
    /// countdown before running as usual stop it.</summary>
    static bool HoldSwallows(ref State s, in Input i)
    {
        switch (i.Kind)
        {
            case InputKind.AudioSignal or InputKind.Ended or InputKind.Duration or InputKind.Format or InputKind.Recovery
                or InputKind.PrepareLost or InputKind.LiveReport:
                return i.Epoch == s.HoldLoadEpoch;
            case InputKind.SetShuffle or InputKind.SetRepeat or InputKind.Suspend or InputKind.Stop or InputKind.Release
                or InputKind.Transfer or InputKind.ScrubBegin or InputKind.SetSpeed:
                if (s.Hold == EndHold.Counting) s.Hold = EndHold.Waiting;
                return false;
            default:
                return false;
        }
    }

    /// <summary>After every Step, while a hold is up: it ends with the row it held (a load, a stop, a lost ownership, a fault).</summary>
    static void HoldInvariant(ref State s)
    {
        if (s.LoadEpoch != s.HoldLoadEpoch || !s.RoutesLocal || !s.HasCurrent || s.Parked || s.Error != Fault.None || s.Phase != Phase.Paused)
            s.Hold = EndHold.None;
    }
}
