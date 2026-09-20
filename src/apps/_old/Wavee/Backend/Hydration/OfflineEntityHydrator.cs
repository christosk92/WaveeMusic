using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend.Hydration;

/// <summary>The LOGGED-OUT / no-transport hydrator (design §1.3): the offline inner of the Spotify source's
/// <see cref="SwitchableEntityHydrator"/>. It answers purely from the store — which is not a no-op, because a
/// <c>store.GetX</c> is what PROMOTES a cold row into the hot tier, so an offline open still paints everything the
/// cache holds. Nothing here networks, so nothing here can throw a transport error; a level we cannot reach is
/// <see cref="HydrationStatus.Unsupported"/> ("structurally impossible right now"), never <c>Failed</c>.</summary>
public sealed class OfflineEntityHydrator(IStore store) : IEntityHydrator
{
    readonly IStore _store = store;

    public HydrationLevel LevelOf(string uri) => LevelOf(EntityUri.Parse(uri));

    HydrationLevel LevelOf(in EntityUri e) => e.Kind switch
    {
        EntityKind.Track => HydrationLevels.Of(_store.GetTrack(e.Uri)),
        EntityKind.Episode => HydrationLevels.Of(_store.GetEpisode(e.Uri)),
        EntityKind.Album => HydrationLevels.Of(_store.GetAlbum(e.Uri)),
        EntityKind.Artist => HydrationLevels.Of(_store.GetArtist(e.Uri)),
        EntityKind.Playlist => HydrationLevels.Of(_store.GetPlaylist(e.Uri), _store.HasMembership(e.Uri)),
        EntityKind.Show => ShowLevel(e.Uri),
        // A resolved owner is a resident entity like any other (P4-C), so an offline read answers it — otherwise a
        // logged-out playlist byline would ask forever for a name the cache already holds.
        EntityKind.User => HydrationLevels.Of(_store.GetOwner(e.Uri)),
        // Collection/Prerelease/Concert/Unknown have no resident entity to measure offline. Collection membership
        // lives in the saved-sets plane and its rung is about its MEMBERS, which the ladder pages — not something an
        // offline read can complete.
        _ => HydrationLevel.None,
    };

    // A show's members ride the same ordered-membership plane a playlist uses (ShowHydration's Identity step calls
    // SetMembership(showUri, episodes)), so the Open/Full rungs are "how many members are resident at Episode.Open".
    // ONE body, shared with the live ladder — an offline rung that disagreed with the online one would make a page
    // shimmer forever after a reconnect.
    HydrationLevel ShowLevel(string showUri) => ShowHydration.LevelOf(_store, showUri);

    public Task<HydrationOutcome> EnsureAsync(string uri, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
    {
        var reached = LevelOf(uri);
        return Task.FromResult(new HydrationOutcome(reached,
            reached >= level ? HydrationStatus.Reached : HydrationStatus.Unsupported));
    }

    public Task<HydrationBatchOutcome> EnsureManyAsync(IReadOnlyList<string> uris, HydrationLevel level,
        HydrationOptions opts = default, CancellationToken ct = default)
    {
        var reached = new List<string>(uris.Count);
        List<string>? missing = null;
        for (int i = 0; i < uris.Count; i++)
        {
            if (LevelOf(uris[i]) >= level) reached.Add(uris[i]);
            else (missing ??= new List<string>()).Add(uris[i]);
        }
        return Task.FromResult(new HydrationBatchOutcome(reached,
            (IReadOnlyCollection<string>?)missing ?? System.Array.Empty<string>(),
            missing is null ? HydrationStatus.Reached : HydrationStatus.Unsupported));
    }

    // Traits are per-playable extension facets — they exist only behind the transport, so offline there is nothing to
    // ensure and nothing to report. (Traits never throw out of the façade even when live; see design §1.3.)
    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSurface surface, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task EnsureTraitsAsync(IReadOnlyList<string> uris, TraitSet traits, TraitSurface surface, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>No ledger offline: nothing is sealed, so there is nothing to unseal.</summary>
    public void Invalidate(string uri) { }
}
