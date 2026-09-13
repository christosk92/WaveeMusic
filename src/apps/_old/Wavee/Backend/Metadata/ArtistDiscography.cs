using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Metadata;

// ── Artist discography: assemble ─────────────────────────────────────────────────────────────────────────────────────
// The fold that turns resident AlbumV4 cards back into the Artist row's shelves. The FETCH half (the old EnsureAsync +
// DiscographyPrefetcher) is gone: ArtistHydration (Backend/Hydration) is the one place that decides which stubs to
// hydrate and at which rung (hydration-facade-plan.md §1.6). Store-only — no transport, no Pathfinder, no GraphQL.
public static class ArtistDiscography
{
    /// <summary>The on-open shelf slice; the full appears-on set is never bulk-hydrated. Public because the artist
    /// LADDER (Backend/Hydration/ArtistHydration.cs) caps its Rich stub batch by the same number — one constant,
    /// not two that can drift.</summary>
    public const int AppearsOnHydrateCap = 20;


    /// <summary>Upgrade stub cards to resident AlbumV4 cards, sorted DATE_DESC, tracklists STRIPPED (an Artist row must
    /// not embed hundreds of tracklists into its persisted JSON). Idempotent; the store merge (MergeAlbumCards) makes it
    /// clobber-safe.</summary>
    public static void Assemble(IStore store, string artistUri)
    {
        var artist = store.GetArtist(artistUri);
        if (artist?.TopAlbums is not { Count: > 0 } stubs) return;

        var cards = new List<Album>(stubs.Count);
        foreach (var s in stubs)
            cards.Add(store.GetAlbum(s.Uri) is { Name.Length: > 0 } full
                ? full with { Kind = s.Kind, Tracks = null, MoreByArtist = null, ArtistsDetailed = null, OtherVersions = null }
                : s);
        cards.Sort(static (a, b) => b.Year.CompareTo(a.Year));   // DATE_DESC (newest first) — matches the GraphQL facet order
        var next = artist with { TopAlbums = cards };

        if (artist.AppearsOn is { Count: > 0 } appears)
        {
            var ap = new List<Album>(appears.Count);
            foreach (var s in appears)
                ap.Add(store.GetAlbum(s.Uri) is { Name.Length: > 0 } full
                    ? full with { Tracks = null, MoreByArtist = null, ArtistsDetailed = null, OtherVersions = null } : s);
            next = next with { AppearsOn = ap };
        }
        store.UpsertArtist(next);
    }
}
