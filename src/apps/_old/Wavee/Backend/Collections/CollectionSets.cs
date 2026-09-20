using System;
using System.Collections.Generic;
using Wavee.Core;

namespace Wavee.Backend.Collections;

// ── The single owner of the logical-set ↔ wire-set mapping ────────────────────────────────────────────────────────────
// The library exposes five LOGICAL sets (liked/albums/artists/shows/episodes) but the collection2v2 service speaks fewer,
// coarser WIRE sets. This class is the one place the mapping (both directions) lives; CollectionFetcher, the write mapper,
// and the push direct-apply all delegate here.
public static class CollectionSets
{
    /// <summary>Every wire set the inbound sync walks — the unit of a page walk, a sync token and a reconcile pass. One
    /// entry per server set, so "collection" is walked ONCE for liked + albums (they were two walks before, each sweeping
    /// the other's half of the same mixed snapshot).</summary>
    public static readonly string[] WireSets = { "collection", "artist", "show", "listenlater", "ylpin" };

    /// <summary>The <c>collection_rev</c> key a wire set's sync token is stored under. Tokens are keyed by WIRE set, not
    /// logical set: one walk of "collection" yields one token that covers both liked and albums, and a delta from it
    /// fans out to both. (Pre-v10 rows were keyed by logical set and are cleared by the v10 migration.)</summary>
    public static string RevisionKey(string wireSet) => wireSet;

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
        // Your-Library pins: playlists/albums/artists/shows + the Liked Songs collection, mixed. Confirmed from the
        // retired app's SpotifyLibraryService (§0.1) — an ordinary collection2v2 set, not a special protocol.
        "pins" => "ylpin",
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
    // so a push-triggered refetch walks that wire set once and fans the items back out to every logical set it carries
    // ("collection" → BOTH liked and albums, split by URI prefix). Unknown wire sets (ylpin, artistban, …) → empty.
    public static IReadOnlyList<string> LogicalSetsForWireSet(string wireSet) => wireSet switch
    {
        "collection" => LikedAndAlbums,
        "artist" => Artists,
        "show" => Shows,
        "listenlater" => Episodes,
        "ylpin" => Pins,
        _ => Array.Empty<string>(),
    };

    static readonly string[] LikedAndAlbums = { "liked", "albums" };
    static readonly string[] Artists = { "artists" };
    static readonly string[] Shows = { "shows" };
    static readonly string[] Episodes = { "episodes" };
    static readonly string[] Pins = { "pins" };

    /// <summary>Whether an item off the wire may be folded into a logical set at all. Every prefix-filtered set says yes
    /// to whatever its prefix admits; the prefix-less "pins" set — the one place a mixed, partly-opaque set can leak a
    /// non-uri into the store — admits only pinnable Spotify entity uris, a rootlist folder, and the Liked Songs
    /// collection (bare <c>spotify:collection</c> on this set, per <c>PinSyncRules.LikedWireUri</c>).</summary>
    public static bool AcceptsUri(string setId, string uri)
    {
        if (setId != "pins") return true;
        if (!uri.StartsWith("spotify:", StringComparison.Ordinal)) return false;
        if (EntityUri.IsLikedCollection(uri)) return true;
        if (EntityUri.FolderIdOf(uri).Length > 0) return true;
        return EntityUri.KindOf(uri) is EntityKind.Playlist or EntityKind.Album or EntityKind.Artist or EntityKind.Show;
    }

    /// <summary>Does a dealer PubSubUpdate for this wire set direct-apply (zero round-trip) or always re-fetch? ylpin
    /// pushes are opaque in practice (the retired client never decoded one), so they always take the delta path (§0.1).</summary>
    public static bool PushDirectApplies(string wireSet) => wireSet != "ylpin";

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
