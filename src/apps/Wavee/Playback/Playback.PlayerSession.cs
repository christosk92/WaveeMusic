// ── Playback/Playback.PlayerSession.cs ──────────────────────────────────────────────────────────────────────────────
// The player session: the id put-state names as `player_state.session_id` and a stream report names as
// RawCoreStream.player_session_id (f59) — one value, the one current when the stream STARTED.
//
// Role: CORE (pure — no clock, no I/O, no RNG of its own: the caller hands in the mint)
//
// The official client's lifetime, read off its captures (the 2026-10 player-session forensics): a new local play, even
// in the same context, mints a new id; a skip, a track end, an autoplay row, a pause, a seek and a queue edit KEEP it;
// a remote play that carries `options.session_id` ADOPTS it (the controller's later update_context quotes it), one
// that carries none mints; a transfer mints. The id is 22 base62 characters of a random 128-bit value.

namespace Wavee;

public static partial class Playback
{
    /// <summary>Why a row's audio began, as far as the player session is concerned.</summary>
    public enum SessionCause : byte
    {
        /// <summary>The next row of the same listening: a skip, a natural end, a gapless join, an autoplay row, a restore.</summary>
        Advance,
        /// <summary>The listener chose something here (a row, a play button), even the context already playing.</summary>
        LocalPlay,
        /// <summary>A controller's <c>play</c> command; it may offer the session id to use.</summary>
        RemotePlay,
        /// <summary>Playback moved to this device (a transfer, a takeover of the mirrored row).</summary>
        Transfer,
    }

    public static class PlayerSession
    {
        /// <summary>The session after a row began. <paramref name="current"/> is the one before (zero = none yet),
        /// <paramref name="offered"/> the id a remote play carried (zero = none, or not a 128-bit base62 id, which is
        /// minted over rather than published in a spelling the wire cannot hold).</summary>
        public static UInt128 NextSession(UInt128 current, SessionCause cause, UInt128 offered, Func<UInt128> mint) => cause switch
        {
            SessionCause.Advance => current != UInt128.Zero ? current : mint(),
            SessionCause.RemotePlay when offered != UInt128.Zero => offered,
            _ => mint(),
        };

        /// <summary>The cause a Step's input maps to: only a Play moves the session (the claim names who asked); every
        /// other load (an advance, a hand-off, a restore, a reload) keeps it. A takeover of the mirrored row is a transfer.</summary>
        public static SessionCause CauseOf(bool isPlay, ClaimCause claim, bool takeover)
        {
            if (!isPlay) return SessionCause.Advance;
            if (takeover) return SessionCause.Transfer;
            return claim switch
            {
                ClaimCause.UserPlay => SessionCause.LocalPlay,
                ClaimCause.InboundPlay => SessionCause.RemotePlay,
                ClaimCause.InboundTransfer or ClaimCause.NobodyTransferToSelf => SessionCause.Transfer,
                _ => SessionCause.Advance,
            };
        }

        /// <summary>The text a stream report carries: the session the stream started in, spelled as put-state spells it;
        /// "" for video (the official client sends none) and when no session was current.</summary>
        public static string TelemetryText(UInt128 atStart, bool video)
            => video || atStart == UInt128.Zero ? "" : string.Create(Base62.GidChars, atStart, static (span, value) => Base62.Encode(value, span));
    }
}
