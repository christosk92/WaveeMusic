// ── Playback/Playback.Endgame.cs ────────────────────────────────────────────────────────────────────────────────────
// The endgame's per-tick decision (G-112): pure, no FluentGpu/session/reducer surface — everything `Tick` needs to
// know about whether to nudge the reducer for more, log the arm diagnostic, or stand down, extracted so it is
// unit-testable without a session, a device or a reducer.
//
// Role: CORE
// Owner: H
// Wave: 3
// Budget: 80 lines
//
// A NAMED PARTIAL OF `Playback.Audio`: replaces the once-per-load `s_gaplessArmed`/`s_endingSoonSent` latches — an
// unprepared endgame that got nothing back the first time now keeps nudging every ~3 s instead of asking once and
// going silent for the rest of the track.

namespace Wavee;

public static partial class Playback
{
    public static partial class Audio
    {
        /// <summary>What the endgame should do THIS tick.</summary>
        public enum EndgameAction : byte
        {
            /// <summary>Nothing to do — outside the window, or already asked/committed recently enough.</summary>
            Wait,
            /// <summary>Nudge the reducer for the next row (<see cref="PostEndingSoon"/>): inside the ask window, with
            /// nothing prepared and nothing in flight, and the re-ask interval has elapsed.</summary>
            Ask,
            /// <summary>A hand-off is due now — <see cref="EndgamePlan.HandOff"/> says which kind.</summary>
            Commit,
        }

        /// <summary>The endgame's per-tick verdict (G-112), replacing the once-per-load `s_gaplessArmed`/
        /// `s_endingSoonSent` latches that asked the reducer once and never again: an unprepared endgame (the
        /// diagnosed bug — a re-seed failure left nothing to prepare, and the pump never asked twice) now keeps
        /// nudging every <paramref name="reaskIntervalMs"/> for as long as the ask window stays open with nothing
        /// prepared and nothing in flight. PURE — no I/O, no engine/session types; <see cref="Tick"/> is the only
        /// caller, and it owns the once-per-track latches (<c>s_armLogged</c> / <c>s_lastEndgameAskMs</c> /
        /// <c>s_endgameAskCount</c>) this reads and advances.</summary>
        public readonly record struct EndgamePlan(EndgameAction Action, HandOff HandOff, bool LogArm, long RemainMs)
        {
            /// <param name="positionMs">The active track's position, in ms.</param>
            /// <param name="durationMs">The active track's duration, in ms — the catalogue figure `Tick` already used
            /// for the arm/ask windows; the commit's own exact-frame boundary stays the commit block's job.</param>
            /// <param name="fadeMs">The effective crossfade length (0 = gapless — rule 3).</param>
            /// <param name="prepared">The next row's prepared slot is ready (<c>IsReady</c>).</param>
            /// <param name="overlapAllowed">The prepared slot may overlap the outgoing voice (<c>s_prepOverlap</c>).</param>
            /// <param name="handOffInFlight">A crossfade or a gapless join is already committing.</param>
            /// <param name="armLogged">The `[gapless] arm` diagnostic already fired for this track.</param>
            /// <param name="lastAskMs">When the endgame last asked for this track, or -1 if it never has.</param>
            /// <param name="nowMs">The current tick's clock (<c>Playback.Host.FrameNowMs</c>).</param>
            /// <param name="reaskIntervalMs">How long to wait between nudges while nothing resolves (default ~3 s).</param>
            public static EndgamePlan Decide(long positionMs, long durationMs, int fadeMs, bool prepared,
                bool overlapAllowed, bool handOffInFlight, bool armLogged, long lastAskMs, long nowMs,
                long reaskIntervalMs = 3_000)
            {
                if (durationMs <= 0) return new EndgamePlan(EndgameAction.Wait, HandOff.None, false, 0);

                long remainMs = durationMs - positionMs;

                // A hand-off is already due: never ask on top of it. The real commit still runs off `Tick`'s own
                // `HandOffAt` call against the voice's exact-frame boundary — this is only a "don't bother asking,
                // it's about to join" signal against the coarser catalogue duration.
                // The arm line is owed once per track even on the tick that commits: with a fade ≥ 2 s the arm lead
                // and the crossfade window open on the SAME tick, and the healthy `reason=0` line is the one a log
                // reader counts hand-offs by.
                bool logArm = !handOffInFlight && !armLogged && remainMs <= ArmLeadMs(fadeMs);

                HandOff handoff = HandOffAt(positionMs, durationMs, fadeMs, prepared, overlapAllowed, handOffInFlight);
                if (handoff != HandOff.None) return new EndgamePlan(EndgameAction.Commit, handoff, logArm, remainMs);

                bool inAskWindow = !handOffInFlight && remainMs <= EndingSoonMs(fadeMs, durationMs);
                bool intervalElapsed = lastAskMs < 0 || nowMs - lastAskMs >= reaskIntervalMs;
                bool ask = inAskWindow && !prepared && intervalElapsed;

                return new EndgamePlan(ask ? EndgameAction.Ask : EndgameAction.Wait, HandOff.None, logArm, remainMs);
            }
        }
    }
}
