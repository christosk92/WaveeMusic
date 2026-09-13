using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

// ── the collection ladder (design §2.3) ──────────────────────────────────────────────────────────────────────────────
// A collection (Liked, saved albums/artists/shows/episodes) has no entity of its own: the SET is a local plane that the
// library sync owns, and "hydrating" it means naming its MEMBERS. So Identity is free — the set is always there — and
// Open is "every saved uri is at least named", paged 300 at a time. This is today's PagedHydrateAsync + libSrc's
// HydrateMembers + libSrc's DetectVideos hooks, addressed by uri and reduced to one ladder.
public sealed class CollectionHydration : IKindHydration
{
    const int Page = Metadata.MetadataChunking.MaxEntitiesPerRequest;

    readonly IStore _store;
    readonly TraitPolicy _policy;

    public CollectionHydration(IStore store, TraitPolicy policy)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public EntityKind Kind => EntityKind.Collection;

    /// <summary>THE collection uri → saved-set id map. Mirrors the inference <c>StoreLibrarySource.SetForUri</c> makes
    /// from an ITEM's kind, but keyed on the SET's own uri: <c>spotify:collection:tracks</c> and the user-namespaced
    /// <c>spotify:user:&lt;u&gt;:collection</c> are both Liked; <c>spotify:collection:{albums|artists|shows|episodes}</c>
    /// name themselves. Anything else has no set and no ladder.</summary>
    public static string? SetOf(in EntityUri uri) => uri.Id switch
    {
        "tracks" or "collection" => "liked",
        "albums" => "albums",
        "artists" => "artists",
        "shows" => "shows",
        "episodes" => "episodes",
        _ => null,
    };

    public HydrationLevel LevelOf(string uri)
    {
        var e = EntityUri.Parse(uri);
        if (SetOf(e) is not { } set) return HydrationLevel.None;
        var saved = _store.SavedUris(set);
        // Identity is unconditional: the saved-set plane is local, so "which uris are in this collection" is always
        // answerable. Open means every member can at least be NAMED — the scan exits on the first one that cannot, so
        // the cold case (the one that has work to do) is cheap and only the warm case pays the full pass.
        for (int i = 0; i < saved.Count; i++)
            if (MemberLevel(saved[i]) < HydrationLevel.Identity) return HydrationLevel.Identity;
        return HydrationLevel.Full;   // Open ≡ Rich ≡ Full for a collection — return the highest so any ask terminates
    }

    HydrationLevel MemberLevel(string uri) => EntityUri.KindOf(uri) switch
    {
        EntityKind.Track => HydrationLevels.Of(_store.GetTrack(uri)),
        EntityKind.Episode => HydrationLevels.Of(_store.GetEpisode(uri)),
        EntityKind.Album => HydrationLevels.Of(_store.GetAlbum(uri)),
        EntityKind.Artist => HydrationLevels.Of(_store.GetArtist(uri)),
        EntityKind.Show => HydrationLevels.Of(_store.GetShow(uri), _store.HasMembership(uri), 0, 0),
        _ => HydrationLevel.Full,   // a member no ladder owns cannot hold the collection back
    };

    /// <summary>A collection has no catalogue kind of its own (<c>CatalogKindOf</c> answers UnknownExtension for it),
    /// so step 0 skips it entirely and there is nothing to fuse.</summary>
    public void ExtraCatalogKinds(in EntityUri uri, HydrationLevel level, List<(string Uri, int Kind)> into) { }

    public async Task ContinueAsync(IReadOnlyList<EntityUri> uris, HydrationLevel level, HydrationOptions opts,
                                    HydrationContext ctx, CancellationToken ct)
    {
        if (level < HydrationLevel.Open) return;   // Identity is the local set — already true before we were called

        for (int i = 0; i < uris.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (SetOf(uris[i]) is not { } set) continue;
            var saved = _store.SavedUris(set);
            if (saved.Count == 0) continue;

            // Pages run SEQUENTIALLY and blocking INSIDE this ladder rather than being re-enqueued one job apiece:
            // OpenPolicy already puts the whole collection open on the pump, so this is the background work — splitting
            // a 10k library into 34 more pump jobs would only fight itself for the pump's two slots.
            var members = new List<string>(saved.Count);
            for (int m = 0; m < saved.Count; m++) if (saved[m] is { Length: > 0 } uri) members.Add(uri);
            for (int start = 0; start < members.Count; start += Page)
            {
                var page = members.GetRange(start, Math.Min(Page, members.Count - start));
                await ctx.Hydrator.EnsureManyAsync(page, HydrationLevel.Identity,
                    new HydrationOptions(Surface: TraitSurface.LikedSongs, Priority: opts.Priority), ct).ConfigureAwait(false);
            }

            // Row traits for the playable sets only — the row bundle decorates a track/episode ROW, and a saved album
            // or artist has no such row to decorate.
            if (set is not ("liked" or "episodes")) continue;
            var traits = _policy.For(TraitSurface.LikedSongs);
            if (traits == TraitSet.None) continue;
            ctx.Pump.Enqueue(opts.Priority - 1,
                pumpCt => ctx.Hydrator.EnsureTraitsAsync(members, traits, TraitSurface.LikedSongs, pumpCt));
        }
    }
}
