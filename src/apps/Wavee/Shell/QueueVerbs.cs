// ── Shell/QueueVerbs.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the queue verbs the player bar owns today (themes plan C2, X6): DROP PLAY NEXT. The rest of the queue's verbs arrive with
// the queue model (C7), which extends this class.
//
// Role: CORE
// Spec: ch 19 #87, G-211
//
// The bar's centre cluster is a drop target: dropping a track, album or playlist there is PLAY NEXT, a front-insert and
// never an immediate playback change. The target's look and its accept/caption policy are view; the deposit is this.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using FluentGpu.Controls;
using FluentGpu.Localization;

namespace Wavee;

public static partial class Shell
{
    public static class QueueVerbs
    {
        /// <summary>What a play-next drop inserts, as DATA. <see cref="Order"/> is the indices into the dropped tracks in the
        /// order they are handed to the queue seam: the FIRST <see cref="PlayerBarRules.MaxPlayNextDrop"/> tracks, LAST
        /// first (each is front-inserted, so the dropped order is the play order), skipping any that are not valid.
        /// <see cref="Truncated"/> says whether the cap actually cut anything, i.e. whether the toast reads "the first N"
        /// rather than just "N".</summary>
        public readonly record struct PlayNextPlan(int[] Order, bool Truncated)
        {
            /// <summary>How many tracks the drop inserts (the toast's number).</summary>
            public int Count => Order.Length;
        }

        /// <summary>The plan for a drop of <paramref name="valid"/>.Length tracks, <c>valid[i]</c> saying whether track
        /// <c>i</c> resolved to a real row.</summary>
        public static PlayNextPlan PlanPlayNext(ReadOnlySpan<bool> valid)
        {
            int cap = PlayerBarRules.DropInsertCount(valid.Length);
            var order = new List<int>(cap);
            for (int i = cap - 1; i >= 0; i--)
            {
                if (!valid[i]) continue;
                order.Add(i);
            }
            return new PlayNextPlan(order.ToArray(), PlayerBarRules.DropWasTruncated(valid.Length));
        }

        /// <summary>Fire-and-forget entry for a drop handler: resolve the payload's tracks and deposit them.</summary>
        public static void DropPlayNext(DragPayload payload) => _ = DropPlayNextAsync(payload);

        /// <summary>The bar's one deposit: resolve the payload's tracks (cold: after the drop, never during the drag) and
        /// FRONT-insert them, last first, so the dropped order is the play order. Said out loud with a success toast.</summary>
        public static async Task DropPlayNextAsync(DragPayload payload)
        {
            Track[] tracks;
            try { tracks = await payload.ResolveTracksAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("playerbar", "play-next drop could not resolve its tracks", ex); return; }
            if (tracks.Length == 0) return;
            Playback.ToUi(() =>
            {
                // G-211: nothing must answer with silence: a drop with no queue seam behind it still tells the user why,
                // instead of the drag simply evaporating.
                if (Actions.Services.PlayNext is not { } playNext)
                {
                    Notify.Say(Loc.Get(Strings.Detail.QueueUnavailable), InfoBarSeverity.Warning);
                    return;
                }
                // The batch cap: take the FIRST MaxPlayNextDrop tracks of the drop (in drop order), still front-inserted
                // last-first so the kept prefix plays in the order it was dropped.
                var valid = new bool[tracks.Length];
                for (int i = 0; i < valid.Length; i++) valid[i] = tracks[i].IsValid;
                var plan = PlanPlayNext(valid);
                foreach (int i in plan.Order) playNext(tracks[i].Uri);
                if (plan.Count == 0) return;
                Notify.Say(plan.Truncated
                        ? Strings.Detail.AddedFirstToQueue(Strings.Detail.SongCount(plan.Count))
                        : Strings.Detail.AddedToQueue(Strings.Detail.SongCount(plan.Count)),
                    InfoBarSeverity.Success);
            });
        }
    }
}
