namespace Wavee.Core;

// ── THE uri parser (docs/plans/wavee/hydration-facade-design.md §1.1) ────────────────────────────────────────────────
// Before this type the app carried six copies of "IdOf", two "KindFor"s, and ~40 hand-rolled StartsWith("spotify:track:")
// gates — which is why episodes (playables in every other respect) were silently dropped by seven services. There is now
// ONE parse: Provider decides ROUTING (which source owns the uri), Kind decides the LADDER (how it hydrates).

/// <summary>What an entity uri addresses. Ordered so the six kinds the metadata transport can fetch come first; the
/// trailing four are routing-only (a page opens them, no V4 hydrates them).</summary>
public enum EntityKind : byte
{
    Unknown, Track, Episode, Album, Artist, Playlist, Show, User,
    /// <summary><c>spotify:collection:tracks</c> (Liked), <c>spotify:user:&lt;u&gt;:collection</c>,
    /// <c>spotify:collection:{albums|artists|shows|episodes}</c>.</summary>
    Collection,
    /// <summary><c>spotify:prerelease:&lt;id&gt;</c> — an unreleased album behind extension kind 138.</summary>
    Prerelease,
    /// <summary><c>spotify:concert:&lt;id&gt;</c> — a live event, served by the return-only concerts service.</summary>
    Concert,
}

/// <summary>The stable provider ids <see cref="EntityUri.Provider"/> reports. A source's <c>Owns</c> compares against
/// these instead of re-deriving a prefix test, so adding a namespace is a one-line change HERE, not a sweep.</summary>
public static class EntityProviders
{
    public const string Spotify = "spotify";
    /// <summary><c>local:*</c> and <c>wavee:local:*</c> — the imported-files library (LocalSource).</summary>
    public const string Local = "local";
    /// <summary><c>wavee:playlist:*</c> — session-created user playlists (UserPlaylistSource).</summary>
    public const string User = "user";
    /// <summary><c>wavee:show:*</c> / <c>wavee:episode:*</c> — the synthetic podcast source.</summary>
    public const string WaveePodcast = "wavee-podcast";
    /// <summary><c>fake:*</c> and the bare legacy ids FakeData mints — the synthetic fallback catalog.</summary>
    public const string Fake = "fake";
    /// <summary>Nobody owns it. Routing answers <c>NotOwnedEntityHydrator</c>; the ladder answers <c>Unsupported</c>.</summary>
    public const string None = "";
}

/// <summary>The ONE parsed entity uri. <see cref="Provider"/> answers "who owns this?" (SourceRegistry routing),
/// <see cref="Kind"/> answers "which hydration ladder?", <see cref="Id"/> is THE trailing-segment id.
/// <para>Allocation: <see cref="KindOf"/> and the provider walk are a pure span scan — no allocation at all, which
/// matters because routing runs per entity at 10k+ scale. <see cref="Parse"/> additionally materializes
/// <see cref="Id"/>, which is exactly ONE small substring per call (vs the ~4 objects <c>String.Split</c> costs).
/// A caller that only needs to route MUST use <see cref="KindOf"/>.</para></summary>
public readonly record struct EntityUri(string Uri, string Provider, EntityKind Kind, string Id)
{
    /// <summary>Parse a uri into (provider, kind, id). Anything unrecognized is <c>("", Unknown, "")</c> — never a
    /// guess, so an unowned uri can't accidentally be addressed at a transport that would 404 on it.</summary>
    public static EntityUri Parse(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return new EntityUri("", EntityProviders.None, EntityKind.Unknown, "");
        var provider = ProviderOf(uri.AsSpan(), out var kind);
        return provider.Length == 0
            ? new EntityUri(uri, EntityProviders.None, EntityKind.Unknown, "")
            : new EntityUri(uri, provider, kind, IdOf(uri));
    }

    /// <summary>The canonical Liked Songs uri — the one spelling routes, pin ids, now-playing matches and the
    /// collection cover are all written with.</summary>
    public const string LikedCollection = "spotify:collection:tracks";

    /// <summary>Is this uri the Liked Songs collection, in ANY of the spellings the wire uses? THE predicate — the
    /// canonical form, the user-namespaced <c>spotify:user:&lt;u&gt;:collection</c> that Home and recents section items
    /// carry, and the facet-suffixed <c>spotify:user:&lt;u&gt;:collection:tracks</c>.
    ///
    /// <para>Sibling collections must answer NO: <c>spotify:collection:{albums|artists|shows|episodes}</c> are separate
    /// surfaces. So the tail after <c>:collection</c> has to be absent or literally <c>tracks</c>.</para>
    ///
    /// <para>Here rather than at the call sites because the app had FOUR copies of
    /// <c>uri == "spotify:collection:tracks"</c>, all of them blind to the user-namespaced form — which is how a
    /// recents card could carry the collection and be classified as an ordinary playlist.</para></summary>
    public static bool IsLikedCollection(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        if (string.Equals(uri, LikedCollection, StringComparison.Ordinal)) return true;
        if (KindOf(uri) != EntityKind.Collection) return false;
        // LAST occurrence, not the first: the segment we want is the trailing one, and a username that happens to
        // start with "collection" (`spotify:user:collectionX:collection`) would otherwise match the wrong span.
        int at = uri.LastIndexOf(":collection", StringComparison.Ordinal);
        if (at < 0) return false;
        var tail = uri.AsSpan(at + ":collection".Length);
        return tail.Length == 0 || tail.SequenceEqual(":tracks");
    }

    /// <summary>Every liked spelling folded to <see cref="LikedCollection"/>; anything else passed through untouched.
    /// A producer hands this to the card so the artwork, the nav dispatcher and the now-playing match all compare one
    /// string.</summary>
    public static string CanonicalLiked(string? uri)
        => IsLikedCollection(uri) ? LikedCollection : uri ?? "";

    /// <summary>The kind alone — the allocation-free routing primitive.</summary>
    public static EntityKind KindOf(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return EntityKind.Unknown;
        _ = ProviderOf(uri.AsSpan(), out var kind);
        return kind;
    }

    /// <summary>THE IdOf: the trailing segment after the last <c>':'</c> (<c>spotify:user:x:playlist:y</c> → <c>"y"</c>;
    /// a colon-less token is its own id). <c>""</c> for an empty uri or a trailing colon.</summary>
    public static string IdOf(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        int colon = uri.LastIndexOf(':');
        return colon < 0 ? uri : colon + 1 >= uri.Length ? "" : uri[(colon + 1)..];
    }

    /// <summary>A playable row: it has a duration, a play context and a queue identity.</summary>
    public bool IsPlayable => Kind is EntityKind.Track or EntityKind.Episode;

    /// <summary>A surface that OPENS to a list of playables (the ladders that page members).</summary>
    public bool IsContainer => Kind is EntityKind.Album or EntityKind.Playlist or EntityKind.Show
                                    or EntityKind.Collection or EntityKind.Artist;

    public bool IsSpotify => Provider == EntityProviders.Spotify;

    // ── the span walk ────────────────────────────────────────────────────────────────────────────────────────────────
    // One pass, constant-span switches (the C# compiler lowers `span switch { "track" => … }` to a length+char test —
    // no allocation, no String.Split). Order matters only where one prefix contains another (wavee:local: before local:
    // is not needed — they are disjoint at the first char — but wavee:* IS multiplexed by its second segment).
    static string ProviderOf(ReadOnlySpan<char> s, out EntityKind kind)
    {
        if (s.StartsWith("spotify:"))
        {
            kind = SpotifyKind(s["spotify:".Length..]);
            return EntityProviders.Spotify;
        }
        if (s.StartsWith("wavee:"))
        {
            var rest = s["wavee:".Length..];
            if (rest.StartsWith("local:")) { kind = LocalKind(rest["local:".Length..]); return EntityProviders.Local; }
            if (rest.StartsWith("playlist:")) { kind = EntityKind.Playlist; return EntityProviders.User; }
            if (rest.StartsWith("show:")) { kind = EntityKind.Show; return EntityProviders.WaveePodcast; }
            if (rest.StartsWith("episode:")) { kind = EntityKind.Episode; return EntityProviders.WaveePodcast; }
            kind = EntityKind.Unknown;
            return EntityProviders.None;   // wavee:skeleton:*, wavee:media:*, … — routed by their own owners, not here
        }
        if (s.StartsWith("local:")) { kind = LocalKind(s["local:".Length..]); return EntityProviders.Local; }
        if (s.StartsWith("fake:")) { kind = CatalogKind(Head(s["fake:".Length..])); return EntityProviders.Fake; }
        if (LegacyFakeKind(s) is { } legacy) { kind = legacy; return EntityProviders.Fake; }
        kind = EntityKind.Unknown;
        return EntityProviders.None;
    }

    /// <summary>The segment up to the next <c>':'</c> (the whole span when there is none).</summary>
    static ReadOnlySpan<char> Head(ReadOnlySpan<char> s)
    {
        int colon = s.IndexOf(':');
        return colon < 0 ? s : s[..colon];
    }

    // spotify:<type>[:…]. `user` is the one multiplexed head: spotify:user:<u>:playlist:<id> is a PLAYLIST and
    // spotify:user:<u>:collection[:…] is a COLLECTION — both were Unknown before, which is why a user-namespaced
    // playlist never got its 205 header.
    static EntityKind SpotifyKind(ReadOnlySpan<char> rest)
    {
        var head = Head(rest);
        if (!head.SequenceEqual("user")) return CatalogKind(head);

        var tail = rest[head.Length..];                               // ":<u>:playlist:<id>" | ":<u>" | ""
        if (tail.Length == 0) return EntityKind.User;                 // "spotify:user" — degenerate, still a user
        var user = Head(tail[1..]);                                   // "<u>" (skip the ':')
        var afterUser = tail[(1 + user.Length)..];                    // ":playlist:<id>" | ":collection[…]" | ""
        if (afterUser.Length == 0) return EntityKind.User;            // spotify:user:<id>
        return Head(afterUser[1..]) switch
        {
            "playlist" => EntityKind.Playlist,
            "collection" => EntityKind.Collection,
            _ => EntityKind.User,                                     // any other tail is still a user surface
        };
    }

    // wavee:local:file:<b64url(path)> is a PLAYABLE (LocalFileMediaProvider decodes it at play time), so it maps to
    // Track like every other local playable — a "file" kind would just be a Track the queue could not carry.
    static EntityKind LocalKind(ReadOnlySpan<char> rest)
    {
        var head = Head(rest);
        return head.SequenceEqual("file") ? EntityKind.Track : CatalogKind(head);
    }

    static EntityKind CatalogKind(ReadOnlySpan<char> type) => type switch   // constant-span patterns — no allocation
    {
        "track" => EntityKind.Track,
        "episode" => EntityKind.Episode,
        "album" => EntityKind.Album,
        "artist" => EntityKind.Artist,
        "playlist" => EntityKind.Playlist,
        "show" => EntityKind.Show,
        "user" => EntityKind.User,
        "collection" => EntityKind.Collection,
        "prerelease" => EntityKind.Prerelease,
        "concert" => EntityKind.Concert,
        _ => EntityKind.Unknown,
    };

    // The bare ids FakeData mints (`tr7`, `al7`, `pl7`, `ar7`) are Ids, not uris — but they reached uri-shaped call
    // sites often enough to be worth claiming for the fake source rather than silently answering Unknown. A colon
    // anywhere disqualifies the token, so no real uri can fall in here.
    static EntityKind? LegacyFakeKind(ReadOnlySpan<char> s)
    {
        if (s.Length < 3 || s.IndexOf(':') >= 0) return null;
        var kind = s[..2] switch
        {
            "tr" => EntityKind.Track,
            "al" => EntityKind.Album,
            "pl" => EntityKind.Playlist,
            "ar" => EntityKind.Artist,
            _ => EntityKind.Unknown,
        };
        if (kind == EntityKind.Unknown) return null;
        for (int i = 2; i < s.Length; i++) if (s[i] is < '0' or > '9') return null;
        return kind;
    }
}
