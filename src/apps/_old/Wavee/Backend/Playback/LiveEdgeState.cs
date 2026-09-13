namespace Wavee.Backend.Playback;

/// <summary>
/// The one fact every live surface asks — <b>am I at the live edge, or am I behind it?</b> — decided ONCE, with
/// hysteresis, instead of re-derived from a raw threshold at each of the three places that need it.
///
/// <para><b>Why a state machine and not a comparison.</b> A live playable does not sit ON the edge; it sits a few
/// seconds inside it, and that distance BREATHES. Media Foundation republishes the seekable window several times a
/// second, the live edge advances in bursts as segments land, and a healthy HLS playhead naturally rides 5–8 s behind
/// the window's end. A plain <c>behind &gt; threshold</c> comparison against any threshold in that band therefore
/// flips on almost every report: the observed defect was the player bar's right slot FLICKERING between the LIVE mark
/// and "GO LIVE −0:06" a few times a second, dragging the whole seek row's layout with it.</para>
///
/// <para><b>The three rules.</b> They are asymmetric on purpose — the cost of the two mistakes is not the same.</para>
/// <list type="bullet">
/// <item>A playable is AT EDGE while <see cref="EnterBehindMs"/> or less behind. 15 s is comfortably past the widest
/// natural ride, so ordinary breathing never leaves the state.</item>
/// <item>It becomes BEHIND only after <see cref="ConfirmReports"/> CONSECUTIVE reports past that line. One report is a
/// window that jumped; two in a row is a playhead that actually fell back (a stall, a rewind, a paused DVR).</item>
/// <item>It returns to AT EDGE at <see cref="ReturnToEdgeMs"/> or less — a lower line than it left by, so the two
/// thresholds cannot chatter across each other. Between the two lines the state simply HOLDS whatever it is.</item>
/// </list>
///
/// <para><b>Engine-free and pure by design</b> — no signals, no player, no engine types, one value in and one value
/// out — so the enter/exit hysteresis and the two-report confirmation are decided by unit tests instead of by a live
/// stream nobody can replay. <c>Wavee.Tests</c> source-includes <c>Backend/**</c>.</para>
/// </summary>
/// <param name="IsBehind">The decided state: true = BEHIND the edge (offer the way back), false = AT the edge.</param>
/// <param name="PendingReports">How many consecutive reports have already been seen past <see cref="EnterBehindMs"/>
/// while still AT EDGE. Bookkeeping for the confirmation rule; zero in every settled state.</param>
public readonly record struct LiveEdgeState(bool IsBehind, int PendingReports)
{
    /// <summary>Past this much behind the edge, a playable is a CANDIDATE for BEHIND (confirmed by the next report).
    /// Wide enough that a healthy HLS ride — 5–8 s inside the window's end — never crosses it.</summary>
    public const long EnterBehindMs = 15_000L;

    /// <summary>At or under this much behind, a BEHIND playable is back AT the edge. Strictly below
    /// <see cref="EnterBehindMs"/>: the gap between the two IS the hysteresis, and it is what makes the state stable
    /// under a window that republishes several times a second.</summary>
    public const long ReturnToEdgeMs = 5_000L;

    /// <summary>How many CONSECUTIVE reports past <see cref="EnterBehindMs"/> confirm the fall back. One report can be
    /// a window that jumped forward on a segment boundary; two in a row is a playhead.</summary>
    public const int ConfirmReports = 2;

    /// <summary>The settled AT-EDGE state — the honest start for every playable, and where a
    /// <c>PlaybackBridge.GoLive</c> puts the machine outright (the user asked to be at the edge; the next window
    /// report must not be able to answer "still behind" from a position the seek has already left).</summary>
    public static LiveEdgeState AtEdge => default;

    /// <summary>The settled BEHIND state.</summary>
    public static LiveEdgeState Behind => new(true, 0);

    /// <summary>Fold one window report into the state.</summary>
    /// <param name="previous">The state the last report left behind.</param>
    /// <param name="behindMs">How far behind the live edge the playhead is now, in ms (never negative).</param>
    /// <param name="hasWindow">Is there a REWINDABLE window to be behind IN? A station with nothing to rewind can
    /// never be behind — there is no way back and nothing to go back to — so it settles AT EDGE unconditionally, and
    /// so does every non-live playable.</param>
    public static LiveEdgeState Next(LiveEdgeState previous, long behindMs, bool hasWindow)
    {
        // Nothing to be behind IN (a station, or not live at all): AT EDGE, and the confirmation counter resets, so a
        // variant switch can never carry a half-confirmed fall into the next playable.
        if (!hasWindow) return AtEdge;

        if (previous.IsBehind)
            // Already behind: leave only at the LOWER line. Between the two lines the state holds.
            return behindMs <= ReturnToEdgeMs ? AtEdge : Behind;

        // At the edge: anything inside the wide line is ordinary breathing, and it clears the counter — the rule is
        // CONSECUTIVE reports, so one report back inside means the fall did not happen.
        if (behindMs <= EnterBehindMs) return AtEdge;

        int pending = previous.PendingReports + 1;
        return pending >= ConfirmReports ? Behind : new LiveEdgeState(false, pending);
    }
}
