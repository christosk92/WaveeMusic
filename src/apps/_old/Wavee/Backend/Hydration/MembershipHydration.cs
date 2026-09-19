using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── the fetchers' membership hydrate delegate, surfaced by the CALLER (design gap fix) ──────────────────────────────
// PlaylistFetcher and CollectionFetcher each take a plain hydrate delegate and know nothing about WHICH surface asked
// — deliberately, so one fetcher serves two callers with two different trait surfaces. Before this fix both callers
// built that delegate as a bare Identity ensure carrying TraitSurface.None, which is correct for a ladder's own
// identity/ref-repair sub-ask (see HydrationOptions.SubAsk) but wrong for a top-level "a membership diff/snapshot
// just adopted these rows" ask: TraitPolicy.For(None) is TraitSet.None by design (Prefetch/Context/None are
// genuinely identity-only waves), so every freshly-adopted row's PLAYS/BPM·KEY columns went forever unasked. Worse,
// the container ladder's own post-step trait pass (PlaylistHydration.ContinueAsync) plans against whatever
// membership is resident AT PLAN TIME, which races a background revalidate/diff and can miss exactly the rows a
// refresh is in the middle of adopting (a daylist rollover, a dealer diff).
//
// The fix: give the delegate the caller's real surface for BOTH the identity ensure (so the catalogue POST's
// client-feature-id and the hydration census attribute correctly) AND a companion trait ask for the SAME rows, fired
// the moment they are adopted rather than waiting for the container's own next open. EnsureTraitsAsync never throws
// (traits are optional polish) and is fired without awaiting it here on purpose: a page open that already blocks on
// the identity ensure above must not pay a second network round trip for the trait POST too.
public static class MembershipHydration
{
    public static Func<IReadOnlyList<string>, CancellationToken, Task> For(IEntityHydrator hydrator, TraitSurface surface)
        => (uris, ct) => HydrateAsync(hydrator, surface, uris, ct);

    static async Task HydrateAsync(IEntityHydrator hydrator, TraitSurface surface, IReadOnlyList<string> uris, CancellationToken ct)
    {
        await hydrator.EnsureManyAsync(uris, HydrationLevel.Identity, new HydrationOptions(Surface: surface), ct)
            .ConfigureAwait(false);
        _ = hydrator.EnsureTraitsAsync(uris, surface);
    }
}
