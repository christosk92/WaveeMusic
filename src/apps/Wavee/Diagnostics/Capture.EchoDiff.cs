// ── Wavee/Diagnostics/Capture.EchoDiff.cs — the queue echo diff and the frame-ignored reason, pure (§6.4) ──────────

using System;

namespace Wavee;

public enum QueueEchoVerdict : byte { Exact, PositionShifted, RowMissing, RowsAdded }

/// <summary>PURE: compare what our OWN optimistic mutation recorded (a row id + the position we placed it at)
/// against what the server's echoed queue revision says landed. No string diffing — row ids and positions, the
/// same values already captured on the `QueueMutation` and `ConnectStatePutResponse` records (§2.5).</summary>
public static class QueueEchoDiff
{
    /// <summary>Gap-filled verdict split (the plan leaves the four cases' exact boundary to the implementer):
    /// absent → <see cref="QueueEchoVerdict.RowMissing"/>; same position → <see cref="QueueEchoVerdict.Exact"/>;
    /// echoed EARLIER than we placed it → <see cref="QueueEchoVerdict.PositionShifted"/> (something ahead of it
    /// that we expected was removed/reordered); echoed LATER → <see cref="QueueEchoVerdict.RowsAdded"/> (rows
    /// landed ahead of it server-side that our optimistic splice did not know about).</summary>
    public static QueueEchoVerdict Compare(long ourRowId, int ourPosition, ReadOnlySpan<long> echoedRowIds)
    {
        int echoedPosition = echoedRowIds.IndexOf(ourRowId);
        return echoedPosition switch
        {
            < 0 => QueueEchoVerdict.RowMissing,
            _ when echoedPosition == ourPosition => QueueEchoVerdict.Exact,
            _ when echoedPosition < ourPosition => QueueEchoVerdict.PositionShifted,
            _ => QueueEchoVerdict.RowsAdded,
        };
    }
}

/// <summary>PURE: why a frame was dropped without acting on it — the `FrameIgnored` record's `reason`, an enum,
/// never a formatted string. Mirrors `Ownership.Fold`'s own F0 stale-guard and the "everything else" fall-through
/// at `Spotify.Connect.cs:247-255` (§3.3, §6.4).</summary>
public enum CaptureIgnoreReason { Unclassified, StaleServerTime, NotOurClaim, TruncatedPayload }
