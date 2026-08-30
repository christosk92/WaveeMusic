using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── the show ladder (design §2.3) ────────────────────────────────────────────────────────────────────────────────────
// A show is the one container whose members ARRIVE WITH IT: ShowV4's episode[] is projected as header + ordered
// membership in step 0. So the ladder above Identity is pure paging — the first 300 episodes to Open (the page the
// user is looking at), the rest on the pump — plus the recent-surface pin that stops the cache GC from purging the
// membership we just paid for.
public sealed class ShowHydration : IKindHydration
{
    /// <summary>One page = the transport's per-POST entity ceiling, so a page is exactly one request.</summary>
    const int Page = HydrationLevels.ShowOpenPage;

    readonly IStore _store;
    readonly TraitPolicy _policy;

    public ShowHydration(IStore store, TraitPolicy policy)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public EntityKind Kind => EntityKind.Show;

    public HydrationLevel LevelOf(string uri) => LevelOf(_store, uri);

    /// <summary>THE show rung, shared with <see cref="OfflineEntityHydrator"/> so "how far up is this show?" has one
    /// body: the rung is a function of the header, the baseline, and how many members from the HEAD of the membership
    /// list are resident at Episode.Open.</summary>
    public static HydrationLevel LevelOf(IStore store, string showUri)
    {
        bool has = store.HasMembership(showUri);
        var members = has ? store.Membership(showUri) : Array.Empty<PlaylistMember>();
        // TWO counts, because the two rungs ask different questions: Open is about the HEAD (the page the show renders,
        // which is what this ladder awaits) and Full is about the whole list. One combined count let a tail page that
        // happened to land first — a Liked-Episodes sweep, a playlist carrying this show's episodes — carry the head
        // threshold on its own, so the show reported paintable with holes at the top that were then never fetched.
        int head = 0, total = 0;
        int page = members.Count < Page ? members.Count : Page;
        for (int i = 0; i < members.Count; i++)
        {
            if (HydrationLevels.Of(store.GetEpisode(members[i].ItemUri)) < HydrationLevel.Open) continue;
            total++;
            if (i < page) head++;
        }
        return HydrationLevels.Of(store.GetShow(showUri), has, head, members.Count, total);
    }

    /// <summary>Nothing extra: ShowV4 carries the header and the episode list in one payload.</summary>
    public void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into) { }

    public async Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts,
                                    HydrationContext ctx, CancellationToken ct)
    {
        if (level < HydrationLevel.Open) return;   // Identity is step 0 (header + membership) and nothing more

        for (int i = 0; i < uris.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string showUri = uris[i].Uri;
            var members = _store.Membership(showUri);
            if (members.Count == 0) continue;

            var head = Slice(members, 0, Page);
            if (head.Count > 0)
            {
                // AWAITED: the first page IS the show page's primary content. One EpisodeV4 POST for the whole page.
                await ctx.Hydrator.EnsureManyAsync(head, HydrationLevel.Open,
                    new HydrationOptions(Surface: TraitSurface.ShowOpen, Priority: opts.Priority), ct).ConfigureAwait(false);

                var traits = _policy.For(TraitSurface.ShowOpen);
                if (traits != TraitSet.None)
                    ctx.Pump.Enqueue(opts.Priority - 1,
                        pumpCt => ctx.Hydrator.EnsureTraitsAsync(head, traits, TraitSurface.ShowOpen, pumpCt));
            }

            // Opening a show is a `recent_surfaces` pin reason: without it the cache GC is free to purge the membership
            // this ladder just wrote, which is the bug that made a revisited show re-page from nothing.
            _store.RecordRecentSurface(showUri, (int)Metadata.EntityKind.Show);

            if (level < HydrationLevel.Full || members.Count <= Page) continue;

            // Full = every member at Episode.Open. Paged on the pump: nothing on screen waits for episode 301.
            var tail = new List<PlaylistMember>(members.Count - Page);
            for (int m = Page; m < members.Count; m++) tail.Add(members[m]);
            for (int start = 0; start < tail.Count; start += Page)
            {
                var page = Slice(tail, start, Page);
                if (page.Count == 0) continue;
                ctx.Pump.Enqueue(opts.Priority - 1, pumpCt => ctx.Hydrator.EnsureManyAsync(
                    page, HydrationLevel.Open,
                    new HydrationOptions(Surface: TraitSurface.ShowOpen, Priority: opts.Priority - 1), pumpCt));
            }
        }
    }

    static List<string> Slice(IReadOnlyList<PlaylistMember> members, int start, int count)
    {
        var list = new List<string>(Math.Min(count, Math.Max(0, members.Count - start)));
        for (int i = start; i < members.Count && list.Count < count; i++)
            if (members[i].ItemUri is { Length: > 0 } uri) list.Add(uri);
        return list;
    }
}
