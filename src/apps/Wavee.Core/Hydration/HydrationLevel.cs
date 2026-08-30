namespace Wavee.Core;

// ── The upgrade ladder (docs/plans/wavee/hydration-facade-design.md §1.2, plan §1.2) ─────────────────────────────────
// One meaning per rung, fixed per kind by HydrationLevels.Of. This subsumes IsAlbumOpenReady, IsAlbumComplete, the
// four-clause artist gate, HasMembership, NowPlayingReady (both copies), ArtistStatsCache.IsFresh and LibrarySync's
// "unnamed ⇒ cold" — the ≥6 unshared "is it cold?" predicates the inventory found, each of which had drifted.

/// <summary>How far up the ladder an entity is. PRESENCE only — age/freshness is the engine's ledger (TTL per
/// (kind, level)) plus <c>Artist.OverviewFetchedAt</c>, never a field on this enum.</summary>
public enum HydrationLevel : byte
{
    /// <summary>Nothing resident (or a row so thin it cannot even be named). When supplied as a request level, this
    /// means a strict resident-store read: no ledger ask, revalidation, pump enqueue, or external I/O.</summary>
    None = 0,
    /// <summary>Named. Enough for a link, a chip, a queue label.</summary>
    Identity = 1,
    /// <summary>The entity's OWN surface can paint its primary content.</summary>
    Open = 2,
    /// <summary>Open plus the second-transport header facets (©/℗, the artist overview).</summary>
    Rich = 3,
    /// <summary>The complete envelope (getAlbum / getTrack files / every show member).</summary>
    Full = 4,
}

/// <summary>The per-kind rung predicates — PURE (no store, no clock, no I/O), so a test can table-drive every rung and
/// every ladder can share the same "did we get there?" answer.
/// <para><b>Of returns the HIGHEST rung whose predicate holds.</b> Where a kind's rungs are equivalent (a playable's
/// Rich ≡ Open; a playlist's Rich ≡ Full ≡ Open) the higher NAME is returned, so a caller that asks for the higher rung
/// is satisfiable at all — otherwise <c>EnsureAsync(trackUri, Rich)</c> could never terminate.</para></summary>
public static class HydrationLevels
{
    // ── Playables ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Identity: a row with a real title. Open (≡ Rich): + named artists, a named album, a usable image and a
    /// duration — i.e. the old <c>NowPlayingReady</c> plus the duration the player bar needs. Full: + a playability
    /// verdict (<c>Availability</c>), which only getTrack/TrackV4 files.</summary>
    public static HydrationLevel Of(Track? t)
    {
        if (t is null || TitleMissing(t.Title, t.Uri)) return HydrationLevel.None;
        bool open = t.Artists.Count > 0 && t.Artists[0].Name.Length > 0
                    && t.Album.Name.Length > 0
                    && ImageSource.IsUsable(t.Image)
                    && t.DurationMs > 0;
        if (!open) return HydrationLevel.Identity;
        return t.Availability is not null ? HydrationLevel.Full : HydrationLevel.Rich;
    }

    /// <summary>Identity: a real title. Open (≡ Rich): + show name, image, duration. Full: + the description the
    /// episode page renders.</summary>
    public static HydrationLevel Of(Episode? e)
    {
        if (e is null || TitleMissing(e.Title, e.Uri)) return HydrationLevel.None;
        bool open = e.ShowName.Length > 0 && ImageSource.IsUsable(e.Image) && e.DurationMs > 0;
        if (!open) return HydrationLevel.Identity;
        return e.Description is { Length: > 0 } ? HydrationLevel.Full : HydrationLevel.Rich;
    }

    // ── Containers ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Identity: a name. Open: the track list is really here (= the old <c>IsAlbumOpenReady</c>). Rich: + the
    /// ©/℗ facets extension kind 183 carries — OR the getAlbum envelope, which subsumes them. Full: + the getAlbum
    /// envelope (<c>Hydration == Full</c>).
    /// <para>The rungs are tested HIGHEST-FIRST, and that ordering is load-bearing: a Full envelope for a release that
    /// carries no publishing facet at all (no ©/℗ line, no release date — plenty of them exist) used to fall out of the
    /// Rich short-circuit and report <b>Open</b>, so <c>DetailTrailing</c>'s <c>GetAlbumAsync(uri, Full)</c> could never
    /// see its own answer and re-ran getAlbum every <c>AlbumFullTtl</c> forever. Full is "we have the envelope", full
    /// stop; Rich is "we have the header facets, from EITHER transport".</para>
    /// <para>A RESTORED album reads back at <b>Identity</b>, not Rich: <c>CachedStore.PersistAlbum</c> strips
    /// <c>Tracks</c> along with the three facet lists (and caps <c>Hydration</c> at <c>Tracks</c>), and the tracklist is
    /// the Open predicate. So the first open after a restart really does re-run the ladder — cheap online (the
    /// extended-metadata cache answers the AlbumV4 conditionally) and, until the persisted rows are re-joined into
    /// <c>Album.Tracks</c> at restore, not possible at all offline: an album the user saved and then opened offline
    /// paints a header with no tracklist. The escape hatch is on the PERSISTENCE side, not here — persist
    /// <c>Tracks</c> (uris, not whole records) for PINNED albums, or re-join the persisted track rows into
    /// <c>Album.Tracks</c> at restore (plan §4 risk 3, and the note on <c>CachedStore.PersistAlbum</c>). This predicate
    /// is deliberately not the place to paper over it: reporting Open for an album with no rows would make every
    /// caller that asks for Open paint an empty list and never re-ask.</para></summary>
    public static HydrationLevel Of(Album? a)
    {
        if (a is null || a.Name.Length == 0) return HydrationLevel.None;
        if (a.Hydration < AlbumHydrationLevel.Tracks || a.Tracks is not { Count: > 0 } tracks) return HydrationLevel.Identity;
        for (int i = 0; i < tracks.Count; i++) if (TrackUnnamed(tracks[i])) return HydrationLevel.Identity;
        if (a.Hydration == AlbumHydrationLevel.Full) return HydrationLevel.Full;
        return a.Copyright is { Length: > 0 } || a.ReleaseDate is { Length: > 0 }
            ? HydrationLevel.Rich : HydrationLevel.Open;
    }

    /// <summary>Identity: a name. Open: an ASSEMBLED discography — the own-discography stubs are named and every facet
    /// total is covered by what we hold (a total larger than the held count means pages are still missing). Rich: + the
    /// overview (Popular + the releases column). Full: + an EXTENDED chart, i.e. more top tracks than
    /// <see cref="ArtistPopularTracks.OverviewSeedCap"/> — the count IS the "already extended" gate, so no second
    /// timestamp column is needed.</summary>
    public static HydrationLevel Of(Artist? a)
    {
        if (a is null || a.Name.Length == 0) return HydrationLevel.None;
        if (a.TopAlbums is not { Count: > 0 } albums || albums[0].Name.Length == 0) return HydrationLevel.Identity;
        if (a.AlbumsTotal + a.SinglesTotal + a.CompilationsTotal > albums.Count) return HydrationLevel.Identity;
        bool rich = a.TopTracks is { Count: > 0 } && (a.LatestRelease is not null || a.PopularReleases is { Count: > 0 });
        if (!rich) return HydrationLevel.Open;
        return a.TopTracks!.Count > ArtistPopularTracks.OverviewSeedCap ? HydrationLevel.Full : HydrationLevel.Rich;
    }

    /// <summary>Identity: a header name. Open ≡ Rich ≡ Full: + a membership baseline. LibrarySync stays the freshness
    /// authority for playlists (its in-flight set, 5-minute window and dirty set) — the ledger never TTL-seals this.</summary>
    public static HydrationLevel Of(Playlist? p, bool hasMembership)
    {
        if (p is null || p.Name.Length == 0) return HydrationLevel.None;
        return hasMembership ? HydrationLevel.Full : HydrationLevel.Identity;
    }

    /// <summary>Identity: a name. Open ≡ Rich: + a membership baseline whose first <c>min(300, memberCount)</c>
    /// episodes are resident at Episode.Open. Full: ALL members are (the remaining pages land on the pump).</summary>
    /// <param name="residentOpenEpisodes">How many of the FIRST <see cref="ShowOpenPage"/> members are resident at
    /// <see cref="HydrationLevel.Open"/> or better — the head, because the head is the page the show renders.</param>
    /// <param name="residentOpenTotal">How many members ANYWHERE in the list are resident at Open or better; the Full
    /// rung is the only one that asks. Defaults to the head count, which is the whole list for a show that fits in one
    /// page. Keeping the two counts APART matters: a tail page that landed first (a Liked-Episodes pass, a playlist
    /// holding this show's episodes) could otherwise push one number past the head threshold and report the show as
    /// paintable while the rows the user is actually looking at were still missing — and, being reported as satisfied,
    /// they would never be fetched.</param>
    public static HydrationLevel Of(Show? s, bool hasMembership, int residentOpenEpisodes, int memberCount,
                                    int residentOpenTotal = -1)
    {
        if (s is null || s.Name.Length == 0) return HydrationLevel.None;
        if (!hasMembership) return HydrationLevel.Identity;
        int total = residentOpenTotal < 0 ? residentOpenEpisodes : residentOpenTotal;
        if (memberCount > 0 && total >= memberCount) return HydrationLevel.Full;
        int firstPage = memberCount < ShowOpenPage ? memberCount : ShowOpenPage;
        if (residentOpenEpisodes < firstPage) return HydrationLevel.Identity;
        // An EMPTY show with a baseline is complete by construction — there is nothing left to page.
        return memberCount == 0 ? HydrationLevel.Full : HydrationLevel.Rich;
    }

    /// <summary>How many episodes a show's Open rung requires up front. Deliberately the same number as the transport's
    /// per-POST entity ceiling (<c>MetadataChunking.MaxEntitiesPerRequest</c>, app-side — Wavee.Core takes no
    /// dependency on it) so the first page is exactly ONE request.</summary>
    public const int ShowOpenPage = 300;

    /// <summary>Identity ≡ every rung: an owner is a name (+ optional avatar); there is no second transport for it.</summary>
    public static HydrationLevel Of(Owner? o)
        => o is null || o.Name.Length == 0 ? HydrationLevel.None : HydrationLevel.Full;

    // ── Row-gap primitives ───────────────────────────────────────────────────────────────────────────────────────────
    // THE bodies. StoreEntityGaps is deleted and StoreEntityMerge.TitleMissing now delegates here, so the whole app
    // shares ONE notion of "thin" - a merge that keeps a resolved name and a ladder gate that asks for one cannot drift.

    /// <summary>Blank OR <c>title == uri</c> — the synthetic placeholder every thin writer seeds before real metadata
    /// resolves. Treating it as missing is what keeps a resolved name from being blanked by a cluster/library echo.</summary>
    public static bool TitleMissing(string? title, string uri)
        => string.IsNullOrEmpty(title) || title == uri;

    /// <summary>Thin enough that a TrackV4 re-fetch is owed: an unnamed title, or artist uris with no names.</summary>
    public static bool TrackUnnamed(Track t)
        => TitleMissing(t.Title, t.Uri) || ArtistsNeedNames(t);

    /// <summary>A denormalized ref that points somewhere real but carries no name — the ref-closure's fetch trigger.</summary>
    public static bool RefNeedsName(in AlbumRef r) => r.Uri.Length > 0 && r.Name.Length == 0;

    static bool ArtistsNeedNames(Track t)
    {
        for (int i = 0; i < t.Artists.Count; i++)
            if (t.Artists[i].Uri.Length > 0 && t.Artists[i].Name.Length == 0) return true;
        return false;
    }
}
