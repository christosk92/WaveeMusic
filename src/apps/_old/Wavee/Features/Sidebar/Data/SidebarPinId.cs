using System;
using Wavee.Core;

namespace Wavee;

// The STABLE pin identity scheme (F.5.4). Engine-free (System only), source-included by src/apps/Wavee.Tests.
//
// The pin id IS the nav route key for every navigable kind. That single choice buys three things at once:
//   * a pinned row renders its label/glyph through ShellNav.Dest(id, name) with no extra plumbing (which is why the
//     "show:" arm in ShellNav is a prerequisite — F.6),
//   * the recency join against HistoryStore is an identity lookup on Route.Name (F.7.6), and
//   * a pin survives a library refresh, because it never depends on a list index.

/// <summary>One pinned sidebar item. <see cref="Id"/> is the STABLE identity (F.5.4) and is also the nav route key for
/// every kind except <see cref="SidebarEntryKind.Folder"/>. <see cref="Name"/>/<see cref="Uri"/> are a display CACHE so a
/// pinned row paints instantly offline before the library resolves; they are refreshed by the projection and are never
/// the source of truth.
///
/// <para><see cref="Kind"/> is <see cref="SidebarEntryKind"/> — the SAME vocabulary the projection uses (there is no
/// more separate "persisted pin kind" enum; the old <c>SidebarPinKind</c> and the two lossy mappings between it and
/// <see cref="SidebarEntryKind"/> — <c>KindOfPin</c>/<c>KindOfEntry</c> — are deleted). The wire freezes the OLD
/// numbering in <c>SidebarLayoutWire</c>'s frozen legacy table instead of duplicating the domain type; see
/// <c>SidebarLayoutDoc.cs</c>.</para></summary>
public sealed record SidebarPin(string Id, SidebarEntryKind Kind, string Uri, string Name, long AddedAtMs)
{
    /// <summary>§3.0 consumer alias — the pin store keys on <see cref="Id"/>.</summary>
    public string Key => Id;

    /// <summary>The nav route this pin opens; "" for a folder (it expands in place, it never navigates).</summary>
    public string RouteKey => SidebarPinId.RouteOf(Id) ?? "";
}

public static class SidebarPinId
{
    // ── prefixes (one place, so a parse and a build can never disagree) ──
    public const string PlaylistPrefix = "pl:";
    public const string AlbumPrefix = "album:";
    public const string ArtistPrefix = "artist:";
    public const string ShowPrefix = "show:";
    public const string FolderPrefix = "folder:";

    /// <summary>The pre-seeded app routes shown by pickers. Dynamic route families (see
    /// <see cref="PinnableRoutePrefixes"/>) are also pinnable when reached; their instances only exist at runtime, so
    /// they are recognised by prefix rather than enumerated here.</summary>
    /// <remarks>"recents" is the full recently-played page. It is offered by the CUSTOMIZER only and is deliberately NOT
    /// in <c>SidebarCustomLayout.DefaultTopBar</c> — a destination a user may add, not one the shell mandates.</remarks>
    public static readonly string[] PinnableRoutes =
        ["home", "search", "albums", "artists", "liked", "podcasts", "local", "history", "recents"];

    /// <summary>Real, durable pages that <see cref="FromRoute"/> accepts but that the curated picker deliberately does
    /// NOT seed — the concerts hub is reachable from an artist page, and offering it in the pin picker alongside Home
    /// and Search would advertise a discovery surface most users never open. Pinnable when REACHED, not suggested.</summary>
    public static readonly string[] AlsoPinnableRoutes = ["concerts"];

    /// <summary>The dynamic route FAMILIES a pin may address. Every entry is a durable destination: the entity kinds
    /// (whose ids double as pin ids), plus the app's own generated pages — a pre-release album, a Home section drill-in,
    /// a browse category, a discography facet, an artist's concert schedule.
    ///
    /// <para>Spelled as LITERALS for the same reason <c>ShellNav</c> spells its route keys that way: this file is
    /// source-included by <c>Wavee.Tests</c>, which cannot see the engine-bound files that own the constants
    /// (<c>HomeSectionRoutes.Prefix</c>, <c>BrowseRoutes.Prefix</c>, <c>DiscographyRoute</c>,
    /// <c>ConcertRoutes.ArtistSchedulePrefix</c>). Adding a route family means adding it here too — that is the whole
    /// point of a closed list: an UNRECOGNISED key is refused, so a third-party entity scheme or a typo can never
    /// become a pin that renders as the "Your Library" fallback.</para></summary>
    public static readonly string[] PinnableRoutePrefixes =
    [
        PlaylistPrefix, AlbumPrefix, ArtistPrefix, ShowPrefix, FolderPrefix,
        "prerelease:", "home-section:", "browse:", "disco:", "artist-concerts:",
        // A page a playback MODULE describes (`module:wavee:module:<id>:<b64(entityId)>`). Durable in exactly the way
        // this list requires: the id is the module's own stable entity id, not a session handle, so a pinned YouTube
        // channel or radio station still opens the same page tomorrow. The pin wears the route's fallback title and
        // image like any other entity route.
        "module:",
    ];

    /// <summary>Real pages that are never pins. The first three are tooling/editor surfaces (a pinned "Settings" row is
    /// chrome, not a destination); <c>playback-diagnostics</c> is a report reached from a dialog.</summary>
    static readonly string[] UnpinnableRoutes =
        ["settings", "api-console", "sidebar-customize", "home-customize", "playback-diagnostics"];

    /// <summary>One dated event. Its page is real and navigable, but a concert happens and is then over, so a pin would
    /// decay into a dead row — the durable destinations are the hub ("concerts") and an artist's schedule
    /// ("artist-concerts:"), both of which <see cref="FromRoute"/> accepts.</summary>
    const string EventRoutePrefix = "concert:";

    /// <summary>Liked Songs is a ROUTE pin, not a playlist pin — matching <c>ActionRules.RouteFor</c>'s existing special
    /// case, so a pin made from the detail page and a pin made from a sidebar row are the SAME pin.</summary>
    public const string LikedSongsUri = EntityUri.LikedCollection;

    /// <summary>The one pin identity a store / menu / drop must use. Accepts a pin id, a bare entity uri, or a route
    /// key and returns the canonical id — so a card drop that carried <c>spotify:playlist:…</c> and a menu that looks
    /// up <c>pl:spotify:playlist:…</c> resolve to the SAME pin. Null = not pinnable.</summary>
    public static string? Canonical(string? idOrUri)
    {
        if (string.IsNullOrEmpty(idOrUri)) return null;
        if (KindOf(idOrUri) != SidebarEntryKind.AppRoute) return idOrUri;   // already a prefixed pin id
        if (idOrUri.StartsWith("spotify:", StringComparison.Ordinal)
            || idOrUri.StartsWith("wavee:", StringComparison.Ordinal))
            return FromUri(idOrUri);                                   // an entity uri never becomes a route pin
        return FromRoute(idOrUri);
    }

    /// <summary>The legacy raw-uri form a card/hero drop used to persist as <c>SidebarPin.Id</c> (the payload's uri,
    /// not the pin id). Empty when the id is a route or folder. Used so <c>IsPinned</c>/<c>Unpin</c> still find those
    /// rows until <c>LoadFrom</c> migrates them.</summary>
    public static string LegacyUriAlias(string? pinId)
    {
        if (string.IsNullOrEmpty(pinId)) return "";
        string uri = UriOf(pinId);
        return uri.Length > 0 && !string.Equals(uri, pinId, StringComparison.Ordinal) ? uri : "";
    }

    /// <summary>uri → pin id. Null = not pinnable. Tracks and episodes are NEVER pinnable (locked decision 4) and that is
    /// enforced HERE, in one function, rather than per menu.</summary>
    public static string? FromUri(string? uri) => uri switch
    {
        null or "" => null,
        // Every liked spelling collapses to the ONE route pin — a pin made from a recents card that carried
        // `spotify:user:<u>:collection` must be the same pin as one made from the detail page.
        var u when EntityUri.IsLikedCollection(u) => "liked",                           // a ROUTE pin
        // Kind comes from the ONE parser (hydration-facade-design.md §1.1). Playlists are pinnable from either provider
        // (spotify AND the session-local `wavee:playlist:*`); album/artist/show stay Spotify-only, as the schemes were.
        var u when EntityUri.KindOf(u) == EntityKind.Playlist => PlaylistPrefix + u,
        var u when EntityUri.Parse(u) is { IsSpotify: true, Kind: EntityKind.Album } => AlbumPrefix + u,
        var u when EntityUri.Parse(u) is { IsSpotify: true, Kind: EntityKind.Artist } => ArtistPrefix + u,
        var u when EntityUri.Parse(u) is { IsSpotify: true, Kind: EntityKind.Show } => ShowPrefix + u,
        _ => null,                                                                      // tracks, episodes, everything else
    };

    /// <summary>Route key → pin id, and the app's one route RECOGNISER. Every durable application destination is stable
    /// enough to pin — the curated <see cref="PinnableRoutes"/> picker set, <see cref="AlsoPinnableRoutes"/>, and the
    /// dynamic <see cref="PinnableRoutePrefixes"/> families (entity routes keep their existing prefixed identities, so
    /// pins made from page chrome, tabs, cards and sidebar rows converge on one record). Refused: tooling/editor
    /// surfaces, one dated event, and anything UNRECOGNISED.
    ///
    /// <para>That last clause is load-bearing and was once missing: callers use this as a recogniser, not just as a
    /// policy filter. <c>WaveeActionTargets.Resolve</c> asks "is this stored key a route?" before falling through to its
    /// bare-uri arm, and <c>SidebarPaneSlot</c>/<c>SidebarDestination</c> gate on it. A version that returned every
    /// non-empty string made those questions unanswerable — a third-party entity uri came back as a route pin with an
    /// empty entity uri, and any typo became a pin that painted as the "Your Library" fallback.</para></summary>
    public static string? FromRoute(string? routeKey)
    {
        if (string.IsNullOrWhiteSpace(routeKey)) return null;

        for (int i = 0; i < UnpinnableRoutes.Length; i++)
            if (string.Equals(UnpinnableRoutes[i], routeKey, StringComparison.Ordinal)) return null;

        // Checked BEFORE the prefix families: "concert:" would otherwise be claimed by nothing, but an explicit refusal
        // documents the event/hub split at the point a reader looks for it.
        if (routeKey.StartsWith(EventRoutePrefix, StringComparison.Ordinal)) return null;

        for (int i = 0; i < PinnableRoutePrefixes.Length; i++)
            if (routeKey.StartsWith(PinnableRoutePrefixes[i], StringComparison.Ordinal)) return routeKey;

        for (int i = 0; i < PinnableRoutes.Length; i++)
            if (string.Equals(PinnableRoutes[i], routeKey, StringComparison.Ordinal)) return routeKey;

        for (int i = 0; i < AlsoPinnableRoutes.Length; i++)
            if (string.Equals(AlsoPinnableRoutes[i], routeKey, StringComparison.Ordinal)) return routeKey;

        return null;
    }

    public static bool IsPinnableRoute(string? routeKey) => FromRoute(routeKey) is not null;

    /// <summary>A rootlist group id → its pin id. Folders are pinnable (locked decision 4) even though they never navigate.</summary>
    public static string ForFolder(string folderId) => FolderPrefix + folderId;

    /// <summary>Prefix dispatch; no known prefix ⇒ <see cref="SidebarEntryKind.AppRoute"/> (the bare-route form).</summary>
    public static SidebarEntryKind KindOf(string? pinId) =>
        pinId is null ? SidebarEntryKind.AppRoute
        : pinId.StartsWith(PlaylistPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Playlist
        : pinId.StartsWith(AlbumPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Album
        : pinId.StartsWith(ArtistPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Artist
        : pinId.StartsWith(ShowPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Show
        : pinId.StartsWith(FolderPrefix, StringComparison.Ordinal) ? SidebarEntryKind.Folder
        : SidebarEntryKind.AppRoute;

    /// <summary>The nav route a pin opens. Null for <see cref="SidebarEntryKind.Folder"/> (a folder expands in place; it
    /// never navigates) — every other kind's id IS its route key.</summary>
    public static string? RouteOf(string pinId) => KindOf(pinId) == SidebarEntryKind.Folder ? null : pinId;

    /// <summary>The entity uri behind a pin id ("" for a route or folder pin). The inverse of <see cref="FromUri"/> for
    /// the prefixed kinds — used when a pinned row needs a play/share target and the display cache is stale.</summary>
    public static string UriOf(string pinId) => KindOf(pinId) switch
    {
        SidebarEntryKind.Playlist => pinId.Substring(PlaylistPrefix.Length),
        SidebarEntryKind.Album => pinId.Substring(AlbumPrefix.Length),
        SidebarEntryKind.Artist => pinId.Substring(ArtistPrefix.Length),
        SidebarEntryKind.Show => pinId.Substring(ShowPrefix.Length),
        SidebarEntryKind.AppRoute when string.Equals(pinId, "liked", StringComparison.Ordinal) => LikedSongsUri,
        _ => "",
    };

    /// <summary>The rootlist group id behind a folder pin ("" when the pin is not a folder).</summary>
    public static string FolderIdOf(string pinId) =>
        KindOf(pinId) == SidebarEntryKind.Folder ? pinId.Substring(FolderPrefix.Length) : "";

    /// <summary>Whether a <see cref="SidebarEntryKind"/> can ever back a pin. <see cref="SidebarEntryKind.Track"/> is the
    /// ONE refusal (locked decision 4) — every other kind is either a real navigable library entity or the bare-route
    /// family, and the id scheme above already knows how to address both. Call this at every pin-CREATION boundary
    /// (never infer pinnability from whether a mapping happens to fall through to a default) so an unpinnable kind is
    /// rejected with a clear "no" instead of silently becoming a route pin.</summary>
    public static bool IsPinnable(SidebarEntryKind kind) => kind != SidebarEntryKind.Track;

    /// <summary>An action target → its pin id (null = not pinnable). Deriving from the target's URI rather than its
    /// <c>TargetKind</c> is deliberate: it keeps <c>ActionTarget</c>/<c>TargetKind</c>/<c>ActionRules</c> untouched, and a
    /// track/queue/now-playing target carries a track uri, which <see cref="FromUri"/> already refuses.</summary>
    public static string? FromTarget(in ActionTarget t) => FromUri(t.Uri);

    /// <summary>The pin id for a projected entry — the entry Id already IS the pin id (F.7.1), so this is the identity
    /// with the not-pinnable kinds screened out: internal routes and TRACK rows (queue / now playing / artist top
    /// tracks) are never pinnable.</summary>
    public static string? FromEntry(in SidebarLibraryEntry e) => e.Kind switch
    {
        SidebarEntryKind.AppRoute => FromRoute(e.Id),
        SidebarEntryKind.Track => null,
        _ => e.Id,
    };
}
