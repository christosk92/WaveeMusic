// ── Playback/Playback.Refill.cs ──────────────────────────────────────────────────────────────────────────────────────
// When the deck asks for more rows: the context's next page, the first autoplay batch, or autoplay's next page. Pure
// rules over the queue's edges; the reducer's `CheckRefill` applies the verdict.
//
// Role: CORE
// Owner: G
// Wave: queue refill (2026-09-16)
// Budget: 150 lines
// Spec: docs/plans/wavee/queue-refill-and-visual-defects-2026-09-16-implementation.md §3 WS-B; gap register G-080, G-242
//
// A NAMED PARTIAL OF `Playback.cs`: no clock, no I/O, no allocation, UI thread only (C1). The ask fires when a run is
// three quarters consumed or three rows from its end, whichever comes first, so the answer lands well before the
// endgame; the endgame's own ask (`DoEndingSoon`) stays as the fallback for a run that got there anyway.

namespace Wavee;

public static partial class Playback
{
    /// <summary>What the deck should ask for now, if anything.</summary>
    public enum RefillKind : byte
    {
        None,
        /// <summary>The context's next page (<see cref="Effects.Page"/>).</summary>
        PageContext,
        /// <summary>The first autoplay batch, or a fresh batch after the last one ran out (<see cref="Effects.Autoplay"/>).</summary>
        AskAutoplay,
        /// <summary>The next page of the autoplay answer the host holds (<see cref="Effects.AutoplayPage"/>).</summary>
        PageAutoplay,
    }

    public static class Refill
    {
        /// <summary>A run is "nearly consumed" once <see cref="ConsumedNum"/>/<see cref="ConsumedDen"/> of it is behind
        /// the cursor, or at most <see cref="MinAhead"/> rows are ahead of it.</summary>
        public const int ConsumedNum = 3, ConsumedDen = 4, MinAhead = 3;

        /// <summary>Count one provider's run: every row of <paramref name="provider"/> is <paramref name="total"/>, those
        /// past <paramref name="cursor"/> are <paramref name="ahead"/>. User-queued rows never count, whatever their
        /// provider says; history rows count as consumed.</summary>
        public static void Run(ReadOnlySpan<QueueEdge> rows, int cursor, QueueProvider provider, out int total, out int ahead)
        {
            total = 0;
            ahead = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                QueueEdge row = rows[i];
                if (row.Bucket == (byte)QueueBucket.UserQueue || row.Provider == (byte)QueueProvider.Queue) continue;
                if (row.Provider != (byte)provider) continue;
                total++;
                if (i > cursor) ahead++;
            }
        }

        public static bool NearlyConsumed(int total, int ahead)
            => total > 0 && (ahead <= MinAhead || ahead * ConsumedDen < total * (ConsumedDen - ConsumedNum));

        /// <summary>The verdict for a deck at <paramref name="cursor"/>. Nothing while an ask is out, before the host has
        /// said whether the context pages (<paramref name="pagesKnown"/>), under repeat-track, or off the queue.</summary>
        public static RefillKind Decide(ReadOnlySpan<QueueEdge> rows, int cursor, AutoplayPhase phase, bool pagesKnown,
            bool morePages, bool autoplayPages, Spotify.Decode.RepeatMode repeat)
        {
            if (phase != AutoplayPhase.None || !pagesKnown || repeat == Spotify.Decode.RepeatMode.Track) return RefillKind.None;
            if ((uint)cursor >= (uint)rows.Length) return RefillKind.None;
            if (morePages)
            {
                Run(rows, cursor, QueueProvider.Context, out int total, out int ahead);
                return NearlyConsumed(total, ahead) ? RefillKind.PageContext : RefillKind.None;
            }
            Run(rows, cursor, QueueProvider.Autoplay, out int autoTotal, out int autoAhead);
            if (autoTotal == 0) return RefillKind.AskAutoplay;
            if (!NearlyConsumed(autoTotal, autoAhead)) return RefillKind.None;
            return autoplayPages ? RefillKind.PageAutoplay : RefillKind.AskAutoplay;
        }
    }

    /// <summary>Apply <see cref="Refill.Decide"/> to the deck: one ask at a time, marked <see cref="AutoplayPhase.Requested"/>
    /// until the host answers.</summary>
    static void CheckRefill(ref State s, ref Effects fx)
    {
        if (!s.RoutesLocal || !s.HasCurrent || s.Context.IsEmpty || s.Cursor.IsNone || Entities.Current is null) return;
        switch (Refill.Decide(Queue.Rows, s.Cursor.Index, s.Autoplay, s.PagesKnown, s.MorePages, s.AutoplayPages, s.Repeat))
        {
            case RefillKind.PageContext:
                s.Autoplay = AutoplayPhase.Requested;
                fx.Page = true;
                fx.PageContext = s.Context;
                break;
            case RefillKind.AskAutoplay:
                s.Autoplay = AutoplayPhase.Requested;
                fx.Autoplay = true;
                fx.AutoplayContext = s.Context;
                break;
            case RefillKind.PageAutoplay:
                s.Autoplay = AutoplayPhase.Requested;
                fx.AutoplayPage = true;
                fx.AutoplayPageContext = s.Context;
                break;
        }
    }
}
