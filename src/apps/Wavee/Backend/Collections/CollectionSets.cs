using System;
using System.Collections.Generic;

namespace Wavee.Backend.Collections;

// ── The single owner of the logical-set ↔ wire-set mapping ────────────────────────────────────────────────────────────
// The library exposes five LOGICAL sets (liked/albums/artists/shows/episodes) but the collection2v2 service speaks fewer,
// coarser WIRE sets. This class is the one place the mapping (both directions) lives; CollectionFetcher, the write mapper,
// and the push direct-apply all delegate here.
public static class CollectionSets
{
    // logical UI set_id → the wire collection set name (the only place the mapping lives). Confirmed against the reference
    // (Wavee SpotifyLibraryService): the real wire sets are "collection" (tracks AND albums, mixed), "artist", "show", and
    // "listenlater" (saved episodes) — all singular; there is no "albums"/"artists"/"shows"/"episodes" set. Sending those
    // names is the other half of the /paging 400 (InvalidArgument on the set string).
    public static string WireSet(string setId) => setId switch
    {
        "liked" => "collection",
        "albums" => "collection",   // no "albums" wire set — albums ride inside "collection"; split out by URI prefix below
        "artists" => "artist",
        "shows" => "show",
        "episodes" => "listenlater",
        // A PRE-SAVE. The capture (pre-release.saz) proves the write is POST /collection/v2/write against
        // spotify:prerelease:{id} but never shows the `set` string, so "collection" is INFERRED from observable
        // behaviour — a pre-saved record turns into a saved album the moment it drops, and albums ride "collection".
        // If the inference is wrong the gateway 400s: SetReplayStrategy.Replay returns false, the op backs off and then
        // dead-letters + rolls the heart back (Backend/Mutation.cs). Visible, reversible, never corrupting.
        // Inbound sync for this set is deliberately NOT wired (see LogicalSetsForWireSet) until a live capture confirms
        // the set string.
        "prerelease" => "collection",
        _ => setId,
    };

    // Sets that share the "collection" wire set are disambiguated client-side by entity-URI prefix; null = keep everything.
    public static string? UriPrefix(string setId) => setId switch
    {
        "liked" => "spotify:track:",
        "albums" => "spotify:album:",
        "prerelease" => "spotify:prerelease:",
        _ => null,
    };

    // The inverse of WireSet: the logical sets that ride a given WIRE set. A dealer push / write reply names the WIRE set,
    // so a push-triggered refetch must fan back out to every logical set the wire set carries ("collection" → BOTH liked
    // and albums — each has its own sync token, both deltas are cheap). Unknown wire sets (ylpin, artistban, …) → empty.
    public static IReadOnlyList<string> LogicalSetsForWireSet(string wireSet) => wireSet switch
    {
        "collection" => LikedAndAlbums,
        "artist" => Artists,
        "show" => Shows,
        "listenlater" => Episodes,
        _ => Array.Empty<string>(),
    };

    static readonly string[] LikedAndAlbums = { "liked", "albums" };
    static readonly string[] Artists = { "artists" };
    static readonly string[] Shows = { "shows" };
    static readonly string[] Episodes = { "episodes" };

    // The specific logical set for ONE item off a wire-set push: the first logical set of the wire set whose URI prefix the
    // item matches (a prefix-less set matches anything). null = the item isn't attributable to a known logical set.
    public static string? LogicalSetForItem(string wireSet, string uri)
    {
        foreach (var set in LogicalSetsForWireSet(wireSet))
        {
            var prefix = UriPrefix(set);
            if (prefix is null || uri.StartsWith(prefix, StringComparison.Ordinal)) return set;
        }
        return null;
    }
}
