using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend.Playlists;
using Wavee.Core;

namespace Wavee.Backend;

// ── THE STORE — the queryable spine (the plan's §1) ──────────────────────────────────────────────────────────────────
// Single source of truth. The plan's durable layer is SQLite with indexed columns; this is the in-memory backing behind
// the same IStore seam (a SqliteStore is the one swap-in). Entities are QUERYABLE by field (title/artist) — the offline
// search/sort/filter index — and every mutation bumps a per-uri version that drives change signals (→ the UI bridges).

public enum SyncState { Confirmed, Pending, Failed }
public enum TrackSort { None, Title, Artist, DurationAsc }

public readonly record struct StoreChange(string Uri, bool IsBulk = false, CollectionKind? Kind = null)   // struct → no heap alloc per Bump, no boxing through SimpleSubject<StoreChange>
{
    public static readonly StoreChange Bulk = new("", true);   // one signal for a bulk load; subscribers re-read
}

/// <summary>One rootlist row in the queryable spine: a playlist uri or a start/end-group marker (Kind 0=item, 1=start, 2=end).</summary>
/// <summary>One rootlist row. <paramref name="AddedAtMs"/> is the row's server ADD timestamp (playlist4
/// <c>ItemAttributes.timestamp</c>, unix ms; 0 = not captured yet): a folder RENAME has to re-send the marker's
/// ORIGINAL create timestamp, so it has to survive the round trip through the store.</summary>
public readonly record struct RootlistEntry(int Position, int Kind, string Uri, string? GroupName, int Depth, long AddedAtMs = 0);

/// <summary>One library-set member with its server add timestamp (unix ms; 0 = unknown) — the Liked-songs/collections
/// default order (added-date descending) reads this; <see cref="IStore.SavedUris"/> stays the unordered fast path.</summary>
public readonly record struct SavedItem(string Uri, long AddedAtMs);

/// <summary>One user-attached LOCAL video override: "when this playable plays, show THIS file instead of whatever video
/// the source would serve". Keyed by the exact playable uri (any namespace — track, episode, a future local file), so the
/// override system is source-agnostic like the rest of the playable seam. The file is LINKED, never copied: <see
/// cref="Path"/> is an absolute path and a missing file is a fall-through at play time, not a broken record.
/// <para><see cref="Id"/> is the first 16 hex chars of SHA-256 over the case-folded normalized path — the stable identity
/// the video source key (<c>"local:video:" + Id</c>) is built from, which is what every surface/host keys its remount on.
/// <see cref="SizeBytes"/>/<see cref="MTimeUnix"/> are STALENESS HINTS only (multi-GB files are never hashed);
/// <see cref="DurationMs"/> is 0 until the media engine reports the file's real duration.</para></summary>
public readonly record struct VideoOverride(
    string Uri, string Path, string Id, long DurationMs, long SizeBytes, long MTimeUnix, long AddedAtUnix)
{
    /// <summary>The store <see cref="IStore.Changes"/> sentinel a roster-level change (any attach/replace/remove) bumps,
    /// alongside the per-uri bump — so a "what have I attached?" view can subscribe without watching every uri.</summary>
    public const string ChangeKey = "video-overrides";

    /// <summary>The <c>PopOutVideoSource.Key</c> namespace prefix an override resolves to. Never a routed uri (the bare
    /// <c>local:</c> namespace is already claimed by LocalSource, and the playing uri publishes verbatim to Connect).</summary>
    public const string SourceKeyPrefix = "local:video:";

    /// <summary>The stable video-source key for this override (<c>"local:video:" + Id</c>).</summary>
    public string SourceKey => SourceKeyPrefix + Id;
}

public interface IStore
{
    // entities (queryable)
    void UpsertTrack(Track t);
    Track? GetTrack(string uri);
    IReadOnlyList<Track> QueryTracks(string? text = null, TrackSort sort = TrackSort.None, int limit = 200);
    // other entity kinds — the metadata layer projects EVERY entity type here, not just tracks
    void UpsertAlbum(Album a);
    Album? GetAlbum(string uri);
    void UpsertArtist(Artist a);
    Artist? GetArtist(string uri);
    void UpsertPlaylist(Playlist p);
    Playlist? GetPlaylist(string uri);
    void UpsertShow(Show s);
    Show? GetShow(string uri);
    void UpsertEpisode(Episode e);
    Episode? GetEpisode(string uri);
    // Playlist owners / added-by contributors (design §2.3 UserHydration). An owner is a first-class ENTITY, not a
    // service-private cache: `UserHydration` writes it here and every surface that renders a name+avatar reads it back,
    // so a profile that lands late repaints through the ordinary change stream instead of a bespoke `Changed` event.
    // KEY: the canonical `spotify:user:<lowercased id>` uri (`UserProfileIds.Normalize`). Both members accept EITHER a
    // bare id or a user uri and normalize internally — the wire spells owners both ways, and two spellings must never
    // become two rows. A non-normalizable input is a silent no-op / null, never an exception.
    void UpsertOwner(Owner owner);
    Owner? GetOwner(string userUriOrId);
    // video↔audio associations (the music-video data side; persisted + etag-revalidated). NOT an entity kind — it is
    // keyed by the SAME entity uri as the Track, so it lives in its own side table rather than the entity store.
    void UpsertVideoAssociation(VideoAssociation a);
    VideoAssociation? GetVideoAssociation(string uri);
    // user-attached local video overrides (the custom-mp4 curation) — the SAME side-table shape as the association map,
    // but user-owned and never revalidated away by a Spotify fetch. Keyed by the exact playable uri.
    void UpsertVideoOverride(VideoOverride o);
    void RemoveVideoOverride(string uri);
    VideoOverride? GetVideoOverride(string uri);
    IReadOnlyList<VideoOverride> VideoOverrides();
    // library sets (collections) + per-item sync state (+ the server add timestamp; 0 = unknown → preserve existing)
    void SetSaved(string setId, string uri, bool saved, SyncState sync);
    void SetSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs);
    bool IsSaved(string setId, string uri);
    IReadOnlyList<string> SavedUris(string setId);
    IReadOnlyList<SavedItem> SavedItems(string setId);
    // ordered playlist membership + the rootlist (the queryable lists the catalog joins onto the shared entities at read)
    void SetMembership(string playlistUri, IReadOnlyList<PlaylistMember> rows, byte[]? baseRev);
    /// <summary>True when a playlist has a known membership baseline, including a valid empty playlist.</summary>
    bool HasMembership(string playlistUri);
    IReadOnlyList<PlaylistMember> Membership(string playlistUri);
    byte[]? PlaylistRevision(string playlistUri);
    void SetRootlist(IReadOnlyList<RootlistEntry> entries);
    /// <summary>Set the rootlist AND its opaque revision. The 1-arg overload preserves the stored revision (header
    /// hydration must not wipe it); this overload sets it (null clears). See §2.6.</summary>
    void SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev);
    byte[]? RootlistRevision();
    IReadOnlyList<RootlistEntry> Rootlist();
    // reactivity
    long Version(string uri);
    void Bump(string uri, CollectionKind? kind = null);
    IObservable<StoreChange> Changes { get; }
    /// <summary>Coalesce a burst of writes (e.g. a 10k-entity metadata sync) into ONE change signal, not one per entity.</summary>
    IDisposable BeginBulk();

    /// <summary>Record that a DETAIL SURFACE was opened for this entity — one of the `recent_surfaces` pin reasons, so
    /// the newest opened surfaces survive the cache-tier TTL/budget and repaint offline after a restart.
    /// <para>A DEFAULT member because it is a CACHE concern, not a query: only the persisted store has a pin table to
    /// write, and the in-memory store legitimately has nothing to do. Putting it on the interface is what lets the show
    /// ladder pin the membership it just paid for without knowing which store it is talking to (design §2.3).</para>
    /// <param name="kind">The transport's <see cref="Wavee.Backend.Metadata.EntityKind"/> — the persisted column.</param></summary>
    void RecordRecentSurface(string uri, int kind) { }
}

static class StoreEntityMerge
{
    public static Track Track(Track? current, Track incoming)
    {
        if (current is null) return incoming;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            // Title == Uri is the synthetic placeholder every thin writer seeds — treat it as missing so a resolved
            // name can never be blanked by a cluster/library echo (lifted from the old MergeClusterTrack).
            Title = TitleMissing(incoming.Title, incoming.Uri) ? current.Title : incoming.Title,
            // Name-aware, not blanket-Has: a lean TrackV4 upsert carries uri-only ArtistRefs (Name==""), and Has() alone
            // would let that thin row clobber names a richer earlier write already resolved. Same discipline as
            // MergeNamedByUri (album cards / discography stubs) — a named incoming artist still replaces, so a real
            // featured-artist correction (or a name fix) lands; only the uri-only stub is protected against.
            Artists = MergeNamedByUri(current.Artists, incoming.Artists, static a => a.Uri, static a => a.Name,
                static (rich, _) => rich) ?? incoming.Artists,   // Track.Artists is non-nullable; MergeNamedByUri can
                                                                  // only return null if BOTH sides were, which can't
                                                                  // happen here — the ?? is belt-and-suspenders.
            Album = MergeAlbumRef(current.Album, incoming.Album),
            DurationMs = incoming.DurationMs > 0 ? incoming.DurationMs : current.DurationMs,
            IsExplicit = incoming.IsExplicit || current.IsExplicit,
            // SameSource → prefer incoming (genuine cover refresh of the same identity). Else quality-rank.
            Image = ImageSource.SameSource(incoming.Image, current.Image)
                ? incoming.Image ?? current.Image
                : ImageSource.ChooseBetter(incoming.Image, current.Image),
            AddedAt = incoming.AddedAt ?? current.AddedAt,
            AddedBy = incoming.AddedBy ?? current.AddedBy,
            // (has-video had an OR-merge here, for a field that no longer exists: it lived on the row so every writer
            // could clobber it, and the OR was the guard against exactly that. The answer now has one home — the
            // VideoAssociation plane — so there is nothing here to defend.)
            PlayCount = incoming.PlayCount > 0 ? incoming.PlayCount : current.PlayCount,
            Origin = incoming.Origin != TrackOrigin.Streamed || current.Origin == TrackOrigin.Streamed ? incoming.Origin : current.Origin,
            // The nullable-adornment rule, like Isrc/Tint below. The old form ("keep incoming only when it is not
            // Playable") was one-way: once a track went Unavailable it could NEVER return, which is precisely backwards
            // for a feature whose whole point is rows becoming available as a record ships.
            Availability = incoming.Availability ?? current.Availability,
            AvailableAt = incoming.AvailableAt ?? current.AvailableAt,
            Source = incoming.Source ?? current.Source,
            Isrc = incoming.Isrc ?? current.Isrc,   // keep a known ISRC across a later thin upsert (cluster/library write)
            // Adornments from extended-metadata kind 222 (tempo/key). These arrive on their OWN pass, long after the
            // thin cluster/library upsert that created the row — so a later thin write must never blank them.
            // Null-coalesce, exactly like Isrc: null means "this writer didn't know", not "clear it". (The cover
            // colour is NOT here: it is image-keyed in CoverColorPlane, so no row write can clobber it.)
            TempoBpm = incoming.TempoBpm ?? current.TempoBpm,
            // Same 0-is-unknown discipline as the other adornments: tags land on their own pass long after the thin
            // cluster upsert, so a later thin write must never blank them.
            Tags = incoming.Tags ?? current.Tags,
            MusicalKey = incoming.MusicalKey ?? current.MusicalKey,
            CamelotCode = incoming.CamelotCode ?? current.CamelotCode,
            CamelotColor = incoming.CamelotColor ?? current.CamelotColor,
            CanonicalUri = incoming.CanonicalUri ?? current.CanonicalUri,
            Year = incoming.Year > 0 ? incoming.Year : current.Year,
        };
    }

    /// <summary>Blank OR <c>title == uri</c> (the synthetic placeholder thin writers seed before real metadata resolves).
    /// ONE body, in <see cref="HydrationLevels.TitleMissing"/> - the merge and the ladder gates must never disagree about
    /// what "thin" means, and StoreEntityGaps (the second copy) was deleted with the legacy hydration paths.</summary>
    public static bool TitleMissing(string? title, string uri) => HydrationLevels.TitleMissing(title, uri);

    public static Album Album(Album? current, Album incoming)
    {
        if (current is null) return incoming;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            Name = NonEmpty(incoming.Name, current.Name),
            Cover = incoming.Cover ?? current.Cover,
            Artists = Has(incoming.Artists) ? incoming.Artists : current.Artists,
            Year = incoming.Year > 0 ? incoming.Year : current.Year,
            TrackCount = incoming.TrackCount > 0 ? incoming.TrackCount : current.TrackCount,
            Tracks = MergeTrackRows(current.Tracks, incoming.Tracks),
            MoreByArtist = Has(incoming.MoreByArtist) ? incoming.MoreByArtist : current.MoreByArtist,
            Label = incoming.Label ?? current.Label,
            Copyright = incoming.Copyright ?? current.Copyright,
            ReleaseDate = incoming.ReleaseDate ?? current.ReleaseDate,
            ArtistsDetailed = MergeArtists(current.ArtistsDetailed, incoming.ArtistsDetailed),
            OtherVersions = Has(incoming.OtherVersions) ? incoming.OtherVersions : current.OtherVersions,
            CourtesyLine = incoming.CourtesyLine ?? current.CourtesyLine,
            ReleaseDatePrecision = incoming.ReleaseDatePrecision ?? current.ReleaseDatePrecision,
            DiscCount = incoming.Hydration == AlbumHydrationLevel.Full
                ? Math.Max(1, incoming.DiscCount)
                : Math.Max(current.DiscCount, incoming.DiscCount),
            ShareUrl = incoming.ShareUrl ?? current.ShareUrl,
            IsPreRelease = incoming.Hydration == AlbumHydrationLevel.Full ? incoming.IsPreRelease : current.IsPreRelease,
            PreReleaseEnd = incoming.PreReleaseEnd ?? current.PreReleaseEnd,
            Hydration = incoming.Hydration > current.Hydration ? incoming.Hydration : current.Hydration,
        };
    }

    /// <summary>Album.Tracks per-uri fold, never shrink. Two writers both used to replace the list wholesale under a
    /// blanket <c>Has(incoming) ? incoming : current</c>: an AlbumV4 disc-stub pass (rows named "" / DurationMs==0 next
    /// to a fully-named album+artists) and the getAlbum envelope, which caps at 50 rows — either one landing after a
    /// richer 100-row list used to truncate or blank it outright. This is the same stub discipline
    /// <see cref="MergeNamedByUri{T}"/> already gives album cards / discography stubs, applied per ROW instead of per
    /// card: fold each incoming row against its prior (a blank/thin incoming row keeps the richer current row's
    /// Title/DurationMs/PlayCount/etc via <see cref="Track"/>), then re-append any current row incoming didn't mention
    /// so a shorter incoming page can never drop rows a fuller earlier write already resolved.</summary>
    public static IReadOnlyList<Track>? MergeTrackRows(IReadOnlyList<Track>? current, IReadOnlyList<Track>? incoming)
    {
        if (!Has(incoming)) return current;
        if (!Has(current)) return incoming;
        var prior = new Dictionary<string, Track>(StringComparer.Ordinal);
        for (int i = 0; i < current!.Count; i++)
        {
            var row = current[i];
            if (row.Uri.Length > 0) prior[row.Uri] = row;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<Track>(Math.Max(current.Count, incoming!.Count));
        for (int i = 0; i < incoming.Count; i++)
        {
            var row = incoming[i];
            if (row.Uri.Length == 0) { merged.Add(row); continue; }
            seen.Add(row.Uri);
            merged.Add(prior.TryGetValue(row.Uri, out var priorRow) ? Track(priorRow, row) : row);
        }
        // Tail: current rows incoming never mentioned — order preserved — so a shorter incoming page (the getAlbum
        // envelope's first 50 of a 100-row AlbumV4 list) never truncates the album.
        for (int i = 0; i < current.Count; i++)
        {
            var row = current[i];
            if (row.Uri.Length > 0 && !seen.Contains(row.Uri)) merged.Add(row);
        }
        return merged;
    }

    public static Artist Artist(Artist? current, Artist incoming)
    {
        if (current is null) return incoming;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            Name = NonEmpty(incoming.Name, current.Name),
            Image = incoming.Image ?? current.Image,
            TopAlbums = MergeAlbumCards(current.TopAlbums, incoming.TopAlbums),
            MonthlyListeners = incoming.MonthlyListeners > 0 ? incoming.MonthlyListeners : current.MonthlyListeners,
            Followers = incoming.Followers > 0 ? incoming.Followers : current.Followers,
            Bio = incoming.Bio ?? current.Bio,
            Verified = incoming.Verified || current.Verified,
            WorldRank = incoming.WorldRank > 0 ? incoming.WorldRank : current.WorldRank,
            HeaderImage = incoming.HeaderImage ?? current.HeaderImage,
            TopTracks = Has(incoming.TopTracks) ? incoming.TopTracks : current.TopTracks,
            AppearsOn = MergeAlbumCards(current.AppearsOn, incoming.AppearsOn),
            // Pinned deliberately does NOT take MergeExtras' null-back rule, even though a pin can now announce an
            // upcoming release. A thin write also carries Pinned = null, so telling "the pin was removed" from "this
            // write doesn't know" would need the same overviewAuthoritative discriminator plumbed in here — and a stale
            // pin is far less harmful than a stale "Coming soon": every pre-release surface gates on
            // PinnedItem.IsUpcoming, a wall-clock test, so a pin whose record has shipped silently reverts to an
            // ordinary promo card on the next render.
            Pinned = incoming.Pinned ?? current.Pinned,
            // A FRESH overview write is authoritative about what the artist no longer has — see MergeExtras.PreRelease.
            // OverviewFetchedAt is the discriminator, NOT FetchedAt: FetchedAt is a max-of stamp every writer may raise
            // (the chart step, a thin V4 upsert that happens to carry one), so a non-overview write that merely bumped
            // it would claim authority over absences it knows nothing about and silently clear the pre-release card.
            // Only the queryArtistOverview write stamps OverviewFetchedAt.
            Extras = MergeExtras(current.Extras, incoming.Extras,
                                 overviewAuthoritative: incoming.OverviewFetchedAt > current.OverviewFetchedAt),
            // Per-facet discography totals: a thin write (0 = unknown) must not drop a full-overview's real total.
            AlbumsTotal = incoming.AlbumsTotal > 0 ? incoming.AlbumsTotal : current.AlbumsTotal,
            SinglesTotal = incoming.SinglesTotal > 0 ? incoming.SinglesTotal : current.SinglesTotal,
            CompilationsTotal = incoming.CompilationsTotal > 0 ? incoming.CompilationsTotal : current.CompilationsTotal,
            LatestRelease = incoming.LatestRelease ?? current.LatestRelease,
            PopularReleases = Has(incoming.PopularReleases) ? incoming.PopularReleases : current.PopularReleases,
            // Keep the newer freshness stamp: a full-overview write carries UtcNow; a thin write carries default → keeps current.
            FetchedAt = incoming.FetchedAt > current.FetchedAt ? incoming.FetchedAt : current.FetchedAt,
            // Same max-of rule, its own clock: the overview stamp can only ever move forward, and only an overview
            // write moves it. That is what lets the Rich age gate ask "when did the OVERVIEW last land?" instead of
            // "when did anyone last write this row?" — the question a chart write or a V4 upsert would answer wrong.
            OverviewFetchedAt = incoming.OverviewFetchedAt > current.OverviewFetchedAt
                ? incoming.OverviewFetchedAt : current.OverviewFetchedAt,
            // …and the CHART's clock, on exactly the same max-of terms. Only ArtistHydration.EnsureChartAsync stamps
            // it, so it answers "when did artist-top-tracks-extensions last speak?" — the question the Full rung's
            // freshness has to ask, because an artist with a genuinely short chart can never satisfy the presence test
            // (TopTracks.Count > OverviewSeedCap) and would otherwise re-GET forever.
            ChartFetchedAt = incoming.ChartFetchedAt > current.ChartFetchedAt
                ? incoming.ChartFetchedAt : current.ChartFetchedAt,
        };
    }

    /// <summary>Playlist merge is NOT blanket-NonEmpty. The header writer
    /// (<c>OpRebaseStrategy.ApplyHeaderPatch</c>) is AUTHORITATIVE for Description/Cover/Capabilities — absence
    /// means clear (ClearDescription / ClearPicture / dead-letter rollback). Name is the one exception to "absence
    /// clears": Spotify playlists cannot legitimately have an empty name, so a playlist4 header patch that omits it
    /// (producing incoming.Name == "") must not blank an already-resolved name — NonEmpty, not blanket-authoritative.
    /// IsPublic adopts only when the permission writer stamped <see cref="Playlist.BasePermissionRevision"/> (both
    /// fields always travel together). Everything else is per-field NonEmpty / null-coalesce / Has.</summary>
    public static Playlist Playlist(Playlist? current, Playlist incoming)
    {
        if (current is null) return incoming;
        bool permissionWrite = incoming.BasePermissionRevision is not null;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            Name = NonEmpty(incoming.Name, current.Name),            // "" cannot blank a resolved name (see above)
            Description = incoming.Description,                      // null = ClearDescription
            Cover = incoming.Cover,                                  // null = ClearPicture
            Capabilities = incoming.Capabilities,                    // header writer authoritative
            OwnerName = NonEmpty(incoming.OwnerName, current.OwnerName),
            Owner = incoming.Owner ?? current.Owner,
            // Collaborators and Tracks are READ-MODEL lists joined at compose time (StoreLibrarySource.ComposePlaylist
            // → BuildCollaborators / JoinMembership) — no writer ever stamps them onto the stored entity. Always keep
            // current rather than the Album.Tracks-style per-uri fold: a non-empty incoming here would mean a caller
            // upserted a composed read-model BACK into the store, which is a bug the merge should refuse, not paper
            // over by adopting a possibly-partial list.
            Collaborators = current.Collaborators,
            Tracks = current.Tracks,
            TrackCount = incoming.TrackCount > 0 ? incoming.TrackCount : current.TrackCount,
            Format = incoming.Format ?? current.Format,
            Source = incoming.Source ?? current.Source,
            Tuning = incoming.Tuning ?? current.Tuning,
            IsPublic = permissionWrite ? incoming.IsPublic : current.IsPublic,
            BasePermissionRevision = incoming.BasePermissionRevision ?? current.BasePermissionRevision,
            // Tombstones LATCH: a delete observed once can never be un-observed by a later thin header write (the
            // rootlist/header writers all default the flag to false). Only a real un-delete path would clear it.
            DeletedByOwner = incoming.DeletedByOwner || current.DeletedByOwner,
        };
    }

    public static Show Show(Show? current, Show incoming)
    {
        if (current is null) return incoming;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            Name = NonEmpty(incoming.Name, current.Name),
            Publisher = NonEmpty(incoming.Publisher, current.Publisher),
            Cover = incoming.Cover ?? current.Cover,
            Description = incoming.Description ?? current.Description,
            // Episodes is a READ-MODEL list joined at compose time (StoreLibrarySource.ComposeShow, from the show's
            // ordered membership) — no writer ever stamps it onto the stored entity, same discipline as
            // Playlist.Tracks/Collaborators above. Always keep current: a non-empty incoming would mean a composed
            // read-model got upserted back into the store, which the merge refuses rather than adopting.
            Episodes = current.Episodes,
            // A READ-MODEL count (0 = this writer doesn't know) — no store writer stamps it, but the same
            // 0-is-unknown discipline keeps a hypothetical one from blanking a known total.
            TotalEpisodes = incoming.TotalEpisodes > 0 ? incoming.TotalEpisodes : current.TotalEpisodes,
        };
    }

    /// <summary>An owner is three fields and two writers (the kind-15 batch and the REST fallback), so the merge is the
    /// minimal protective one: a real Name never loses to a blank, and a null Avatar means "this writer didn't know",
    /// never "clear it" (the REST body carries no image for some accounts while kind 15 does).</summary>
    public static Owner Owner(Owner? current, Owner incoming)
    {
        if (current is null) return incoming;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            Name = NonEmpty(incoming.Name, current.Name),
            Avatar = incoming.Avatar ?? current.Avatar,
        };
    }

    public static Episode Episode(Episode? current, Episode incoming)
    {
        if (current is null) return incoming;
        return incoming with
        {
            Id = NonEmpty(incoming.Id, current.Id),
            Title = NonEmpty(incoming.Title, current.Title),
            ShowName = NonEmpty(incoming.ShowName, current.ShowName),
            // The show LINK is additive, never subtractive: a writer that knows the name but not the gid (a cluster
            // row, a restored blob from before the field existed) must not strip the uri a catalogue write already
            // landed — that is the same "thin write must not downgrade a rich row" rule the rest of this file applies.
            ShowUri = incoming.ShowUri ?? current.ShowUri,
            Image = incoming.Image ?? current.Image,
            DurationMs = incoming.DurationMs > 0 ? incoming.DurationMs : current.DurationMs,
            PublishedAt = incoming.PublishedAt != default && incoming.PublishedAt != DateTimeOffset.UnixEpoch
                ? incoming.PublishedAt : current.PublishedAt,
            Description = incoming.Description ?? current.Description,
            // ProgressMs inventory (S1): the ONLY writer today is ProjectEpisode, which always lands the default 0 —
            // there is no resume path yet. Adopt incoming always so a future resume writer can represent an explicit
            // reset to 0; do NOT use `>0 ? incoming : current` (that would make resets unrepresentable).
            ProgressMs = incoming.ProgressMs,
        };
    }

    // Discography merge: the incoming list is the authoritative group order + Kind (a fresh ArtistV4 write), so incoming
    // order wins — but a name-less incoming STUB must never downgrade an already-hydrated card (the "discography flickers
    // empty" bug). Per URI, a stub keeps the prior rich card (adopting only the incoming Kind); a hydrated incoming card
    // upgrades. A GraphQL-stats write passes TopAlbums:null → Has=false → keeps current wholesale.
    public static IReadOnlyList<Album>? MergeAlbumCards(IReadOnlyList<Album>? current, IReadOnlyList<Album>? incoming)
        => MergeNamedByUri(current, incoming, static a => a.Uri, static a => a.Name,
            static (rich, incoming) => rich with { Kind = incoming.Kind });

    /// <summary>Shared stub-fold for album cards and <c>ArtistAlbumStub</c> lists — same uri/name/Kind discipline,
    /// different record shapes. A name-less incoming keeps the prior rich row (adopting only Kind).</summary>
    public static IReadOnlyList<T>? MergeNamedByUri<T>(
        IReadOnlyList<T>? current, IReadOnlyList<T>? incoming,
        Func<T, string> uriOf, Func<T, string> nameOf, Func<T, T, T> keepRichWithIncomingKind)
    {
        if (!Has(incoming)) return current;
        if (!Has(current)) return incoming;
        var prior = new Dictionary<string, T>(StringComparer.Ordinal);
        for (int i = 0; i < current!.Count; i++) prior[uriOf(current[i])] = current[i];
        var merged = new List<T>(incoming!.Count);
        for (int i = 0; i < incoming.Count; i++)
        {
            var a = incoming[i];
            merged.Add(nameOf(a).Length == 0 && prior.TryGetValue(uriOf(a), out var rich)
                ? keepRichWithIncomingKind(rich, a) : a);
        }
        return merged;
    }

    static IReadOnlyList<Artist>? MergeArtists(IReadOnlyList<Artist>? current, IReadOnlyList<Artist>? incoming)
    {
        if (!Has(incoming)) return current;
        if (!Has(current)) return incoming;
        var existing = new Dictionary<string, Artist>(StringComparer.Ordinal);
        for (int i = 0; i < current!.Count; i++) existing[current[i].Uri] = current[i];
        var merged = new List<Artist>(incoming!.Count);
        for (int i = 0; i < incoming.Count; i++)
        {
            var artist = incoming[i];
            existing.TryGetValue(artist.Uri, out var prior);
            merged.Add(Artist(prior, artist));
        }
        return merged;
    }

    /// <param name="overviewAuthoritative">True when <paramref name="incoming"/> is a fresh full-overview write, whose
    /// ABSENCES are meaningful. Most fields here use "keep the richer of the two" because a thin write simply does not
    /// carry them; <see cref="ArtistExtras.PreRelease"/> is the exception — see below.</param>
    static ArtistExtras? MergeExtras(ArtistExtras? current, ArtistExtras? incoming, bool overviewAuthoritative)
    {
        if (incoming is null) return current;
        if (current is null) return incoming;
        return new ArtistExtras(
            Concerts: Has(incoming.Concerts) ? incoming.Concerts : current.Concerts,
            Merch: Has(incoming.Merch) ? incoming.Merch : current.Merch,
            Playlists: Has(incoming.Playlists) ? incoming.Playlists : current.Playlists,
            MusicVideos: Has(incoming.MusicVideos) ? incoming.MusicVideos : current.MusicVideos,
            TopCities: Has(incoming.TopCities) ? incoming.TopCities : current.TopCities,
            ExternalLinks: Has(incoming.ExternalLinks) ? incoming.ExternalLinks : current.ExternalLinks,
            Gallery: Has(incoming.Gallery) ? incoming.Gallery : current.Gallery,
            Related: Has(incoming.Related) ? incoming.Related : current.Related,
            Tour: incoming.Tour ?? current.Tour,
            // Positional ctor: every field of ArtistExtras MUST appear here. WatchFeed was added to the record without
            // being added to this merge, so it defaulted to null on every non-first artist write — the artist page does
            // ≥2 writes (thin V4 upsert, then the overview upsert), which nulled the watch feed before the hero read it
            // and reduced the portrait to a bare avatar with no ring, no scrim and no click-through.
            WatchFeed: incoming.WatchFeed ?? current.WatchFeed,
            // The ONE field that must be able to go back to null. Every other field here is additive — a thin write
            // lacking it means "I don't know", so keeping the old value is right. A pre-release is different: it is a
            // temporary state that ENDS, and the server signals the end by dropping preReleaseV2 from the overview.
            // With a plain `?? current` the album ships and the artist page still says "Coming soon" forever.
            PreRelease: overviewAuthoritative ? incoming.PreRelease : (incoming.PreRelease ?? current.PreRelease));
    }

    static AlbumRef MergeAlbumRef(AlbumRef current, AlbumRef incoming) => new(
        NonEmpty(incoming.Id, current.Id),
        NonEmpty(incoming.Uri, current.Uri),
        NonEmpty(incoming.Name, current.Name));

    static bool Has<T>(IReadOnlyList<T>? value) => value is { Count: > 0 };
    static string NonEmpty(string value, string fallback) => value.Length > 0 ? value : fallback;
}

public sealed class InMemoryStore : IStore
{
    readonly object _gate = new();
    readonly Dictionary<string, Track> _tracks = new();
    readonly Dictionary<string, Album> _albums = new();
    readonly Dictionary<string, Artist> _artists = new();
    readonly Dictionary<string, Playlist> _playlists = new();
    readonly Dictionary<string, Show> _shows = new();
    readonly Dictionary<string, Episode> _episodes = new();
    // Owners are keyed by the canonical `spotify:user:<id>` uri and are deliberately OUTSIDE the six-kind residency /
    // eviction model: an Owner is ~150 B of strings, the live set is bounded by the users the library actually surfaces
    // (playlist owners + added-by contributors), and evicting one would only cost a re-resolve of something free.
    readonly Dictionary<string, Owner> _owners = new(StringComparer.Ordinal);
    readonly Dictionary<string, VideoAssociation> _videoAssoc = new();
    readonly Dictionary<string, VideoOverride> _videoOverrides = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _versions = new();
    readonly Dictionary<(string set, string uri), (SyncState Sync, long AddedAt)> _saved = new();
    readonly Dictionary<string, HashSet<string>> _savedBySet = new();   // set → uris, so SavedUris is O(set), not O(all-saved)
    readonly Dictionary<string, (IReadOnlyList<PlaylistMember> Rows, byte[]? Rev)> _membership = new();
    IReadOnlyList<RootlistEntry> _rootlist = Array.Empty<RootlistEntry>();
    byte[]? _rootlistRev;
    readonly SimpleSubject<StoreChange> _changes = new();

    // ── entity residency (bounded string floor) ──────────────────────────────────────────────────────────────────────
    // Per-entity LRU stamp: a strict-monotonic Seq (deterministic recency ordering for eviction) + a wall-clock Tick
    // (Environment.TickCount64 ms; the upsert-time backstop uses it to never evict an entity touched in the last window).
    // Touched under _gate on every entity getter HIT and every entity upsert. Only the six real entity kinds are stamped.
    readonly Dictionary<string, (long Seq, long Tick)> _lastUse = new();
    long _useClock;

    /// <summary>True once any entity has been evicted this session. CachedStore gates its cold-fallback reads on this so a
    /// fresh session (hot == cold, nothing evicted) keeps the old zero-disk read semantics; only after the first eviction
    /// can a hot miss be an evicted-but-recoverable entity worth a cold read.</summary>
    public bool HasEvictedEntities { get; private set; }

    // Rough per-entity retained-bytes model for census attribution + the bytes-freed return values (order-of-magnitude,
    // never a hard budget): a Track averages ~0.6 KB of UTF-16 strings + refs; an Album ~0.9 KB; an ACCRETED Artist ~4 KB
    // (bio + discography cards + extras merge in over a session); a Playlist header ~0.4 KB; Show/Episode ~0.5 KB; and each
    // _versions row ~48 B (interned uri key + a long). Deliberately coarse — the census is for attribution, not accounting.
    const int TrackBytes = 640, AlbumBytes = 900, ArtistBytes = 4096, PlaylistBytes = 420, ShowBytes = 512, EpisodeBytes = 512, VersionBytes = 48;
    static readonly int[] s_kindBytes = { TrackBytes, AlbumBytes, ArtistBytes, PlaylistBytes, ShowBytes, EpisodeBytes };

    // Upsert-time backstop: when a week-long session's entity count climbs past BackstopHigh, immediately shed (LRU-only,
    // pin-set-free) down to BackstopLow — but never evicting anything touched within BackstopProtectMs (the live working
    // set). This is the safety net for when the 30 s governor poll never fires eviction (the entity arena is priority 3,
    // shed only under CRITICAL OS pressure). Suppressed during a bulk sync (see MaybeBackstopEvict / EndBulk).
    const int BackstopHigh = 12_000, BackstopLow = 8_000, BackstopCheckStride = 256;
    // The live-working-set guard: the backstop never evicts an entity touched within this window. An instance field (not a
    // const) purely so a test can zero it to exercise the shed path deterministically (production is always 60 s).
    internal long BackstopProtectMs = 60_000;
    // Throttle the (O(n log n)) shed to at most once per BackstopCheckStride upserts past the high-water, so a session that
    // hovers just above 12k with an all-recent (protected) working set doesn't pay a gather+sort on every single upsert.
    int _backstopCheckAt;

    void TouchUse(string uri) => _lastUse[uri] = (++_useClock, Environment.TickCount64);   // caller holds _gate

    int TotalEntitiesLocked() => _tracks.Count + _albums.Count + _artists.Count + _playlists.Count + _shows.Count + _episodes.Count;   // caller holds _gate

    /// <summary>Live entity-dictionary counts (census attribution) — the six queryable kinds plus the version side-table.</summary>
    public (int Tracks, int Albums, int Artists, int Playlists, int Shows, int Episodes, int Versions) EntityCounts
    {
        get { lock (_gate) return (_tracks.Count, _albums.Count, _artists.Count, _playlists.Count, _shows.Count, _episodes.Count, _versions.Count); }
    }

    /// <summary>A coarse estimate of the retained bytes held by the resident entities (the per-kind byte model above ×
    /// counts). Cheap — no deep walk. Attribution only.</summary>
    public long EstimatedEntityBytes { get { lock (_gate) return EstimateBytesLocked(); } }

    long EstimateBytesLocked() =>
        (long)_tracks.Count * TrackBytes + (long)_albums.Count * AlbumBytes + (long)_artists.Count * ArtistBytes
        + (long)_playlists.Count * PlaylistBytes + (long)_shows.Count * ShowBytes + (long)_episodes.Count * EpisodeBytes
        + (long)_versions.Count * VersionBytes;

    /// <summary>Pin up to <paramref name="perSet"/> members of each saved (library) set — the collection-page heads the
    /// entity evictor must not drop. Order is the set's hash order (arbitrary but bounded); it only needs to keep SOME of
    /// each collection resident so its page paints without a cold round-trip.</summary>
    public void CollectSavedHeads(ISet<string> pins, int perSet)
    {
        if (perSet <= 0) return;
        lock (_gate)
            foreach (var kv in _savedBySet)
            {
                int n = 0;
                foreach (var uri in kv.Value) { if (n++ >= perSet) break; pins.Add(uri); }
            }
    }

    /// <summary>Evict oldest-first (by LRU Seq) unpinned entities until the resident entity count drops to
    /// <paramref name="maxResident"/>, returning the estimated bytes freed (the byte model above). A no-op returning 0 when
    /// already at/under the target. Removes the entity from its kind dictionary, its <c>_versions</c> row, and its LRU
    /// stamp; the cold tier still holds it (offline-first), so CachedStore's cold-fallback rehydrates it on next access.</summary>
    public long EvictEntities(ISet<string> pinned, int maxResident) => EvictCore(pinned, maxResident, protectTickFloor: 0);

    // Shared eviction core. protectTickFloor > 0 skips any entity whose last-touch Tick is >= the floor (the backstop's
    // "never evict the live working set" guard); pinned (nullable) skips reachability-pinned entities (the governor arena).
    long EvictCore(ISet<string>? pinned, int target, long protectTickFloor)
    {
        lock (_gate)
        {
            int total = TotalEntitiesLocked();
            if (total <= target) return 0;
            var cands = new List<(string Uri, int Kind, long Seq)>(total);
            GatherCandidatesLocked(cands);
            cands.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));   // oldest (smallest Seq) first
            long freed = 0;
            for (int i = 0; i < cands.Count && total > target; i++)
            {
                var (uri, kind, _) = cands[i];
                if (pinned is not null && pinned.Contains(uri)) continue;
                if (protectTickFloor > 0 && _lastUse.TryGetValue(uri, out var lu) && lu.Tick >= protectTickFloor) continue;
                if (RemoveEntityLocked(uri, kind)) { freed += s_kindBytes[kind]; total--; }
            }
            if (freed > 0) HasEvictedEntities = true;
            return freed;
        }
    }

    void GatherCandidatesLocked(List<(string Uri, int Kind, long Seq)> cands)
    {
        foreach (var k in _tracks.Keys) cands.Add((k, 0, SeqOf(k)));
        foreach (var k in _albums.Keys) cands.Add((k, 1, SeqOf(k)));
        foreach (var k in _artists.Keys) cands.Add((k, 2, SeqOf(k)));
        foreach (var k in _playlists.Keys) cands.Add((k, 3, SeqOf(k)));
        foreach (var k in _shows.Keys) cands.Add((k, 4, SeqOf(k)));
        foreach (var k in _episodes.Keys) cands.Add((k, 5, SeqOf(k)));
        long SeqOf(string uri) => _lastUse.TryGetValue(uri, out var v) ? v.Seq : 0;   // never-stamped ⇒ oldest
    }

    bool RemoveEntityLocked(string uri, int kind)
    {
        bool removed = kind switch
        {
            0 => _tracks.Remove(uri), 1 => _albums.Remove(uri), 2 => _artists.Remove(uri),
            3 => _playlists.Remove(uri), 4 => _shows.Remove(uri), 5 => _episodes.Remove(uri), _ => false,
        };
        if (removed) { _lastUse.Remove(uri); _versions.Remove(uri); }
        return removed;
    }

    // Called after every entity upsert (and once at the close of a bulk scope). Cheap on the common path (a count read
    // under the lock); only when past BackstopHigh AND outside a bulk does it run the actual LRU shed. Suppressed during a
    // bulk sync so a 10k-entity load doesn't trigger an O(n log n) shed per upsert (and doesn't evict the just-loaded set).
    void MaybeBackstopEvict()
    {
        lock (_gate)
        {
            if (_bulkDepth != 0) return;   // a bulk sync closes with one backstop pass (EndBulk), never per-upsert
            int total = TotalEntitiesLocked();
            if (total <= BackstopHigh) { _backstopCheckAt = 0; return; }   // back under the line → re-arm the throttle
            if (total < _backstopCheckAt) return;                          // already checked at this level; wait for growth
            _backstopCheckAt = total + BackstopCheckStride;                // next attempt only after another stride of upserts
        }
        long floor = BackstopProtectMs > 0 ? Environment.TickCount64 - BackstopProtectMs : 0;   // 0 ⇒ guard disabled
        EvictCore(pinned: null, target: BackstopLow, protectTickFloor: floor);
    }

    public IObservable<StoreChange> Changes => _changes;

    public void UpsertTrack(Track t)
    {
        lock (_gate)
        {
            _tracks.TryGetValue(t.Uri, out var current);
            _tracks[t.Uri] = StoreEntityMerge.Track(current, t);
            TouchUse(t.Uri);
        }
        Bump(t.Uri);
        MaybeBackstopEvict();
    }

    public Track? GetTrack(string uri)
    {
        lock (_gate) { if (_tracks.TryGetValue(uri, out var t)) { TouchUse(uri); return t; } return null; }
    }

    public IReadOnlyList<Track> QueryTracks(string? text = null, TrackSort sort = TrackSort.None, int limit = 200)
    {
        if (limit <= 0) return Array.Empty<Track>();   // guard: a non-positive limit is an empty result, not an exception
        bool hasText = !string.IsNullOrEmpty(text);

        // Fast path (the default): no sort → stream the table, filter inline, stop at `limit`. No whole-table copy, no sort —
        // a default limit=200 query over 100k tracks touches ~200 rows instead of materializing+sorting 100k.
        if (sort == TrackSort.None)
        {
            var picked = new List<Track>(Math.Min(limit, 256));
            lock (_gate)
                foreach (var t in _tracks.Values)
                {
                    if (hasText && !MatchesText(t, text!)) continue;
                    picked.Add(t);
                    if (picked.Count >= limit) break;
                }
            return picked;
        }

        // Sorted path: gather matches under the lock, sort OUTSIDE it. (The indexed SqliteStore swap does this in SQL.)
        List<Track> rows;
        lock (_gate)
        {
            rows = new List<Track>(_tracks.Count);
            foreach (var t in _tracks.Values)
                if (!hasText || MatchesText(t, text!)) rows.Add(t);
        }
        rows = sort switch
        {
            TrackSort.Title => rows.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            TrackSort.Artist => rows.OrderBy(t => t.Artists.Count > 0 ? t.Artists[0].Name : "", StringComparer.OrdinalIgnoreCase).ToList(),
            TrackSort.DurationAsc => rows.OrderBy(t => t.DurationMs).ToList(),
            _ => rows,
        };
        return rows.Count > limit ? rows.GetRange(0, limit) : rows;
    }

    static bool MatchesText(Track t, string text)
    {
        if (t.Title.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        var artists = t.Artists;
        for (int i = 0; i < artists.Count; i++)   // manual loop — no .Any() closure per row
            if (artists[i].Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public void UpsertAlbum(Album a)
    {
        lock (_gate)
        {
            _albums.TryGetValue(a.Uri, out var current);
            _albums[a.Uri] = StoreEntityMerge.Album(current, a);
            TouchUse(a.Uri);
        }
        Bump(a.Uri);
        MaybeBackstopEvict();
    }
    public Album? GetAlbum(string uri) { lock (_gate) { if (_albums.TryGetValue(uri, out var a)) { TouchUse(uri); return a; } return null; } }
    public void UpsertArtist(Artist a)
    {
        lock (_gate)
        {
            _artists.TryGetValue(a.Uri, out var current);
            _artists[a.Uri] = StoreEntityMerge.Artist(current, a);
            TouchUse(a.Uri);
        }
        Bump(a.Uri);
        MaybeBackstopEvict();
    }
    public Artist? GetArtist(string uri) { lock (_gate) { if (_artists.TryGetValue(uri, out var a)) { TouchUse(uri); return a; } return null; } }
    public void UpsertPlaylist(Playlist p)
    {
        lock (_gate)
        {
            _playlists.TryGetValue(p.Uri, out var current);
            _playlists[p.Uri] = StoreEntityMerge.Playlist(current, p);
            TouchUse(p.Uri);
        }
        Bump(p.Uri);
        MaybeBackstopEvict();
    }
    public Playlist? GetPlaylist(string uri) { lock (_gate) { if (_playlists.TryGetValue(uri, out var p)) { TouchUse(uri); return p; } return null; } }
    public void UpsertShow(Show s)
    {
        lock (_gate)
        {
            _shows.TryGetValue(s.Uri, out var current);
            _shows[s.Uri] = StoreEntityMerge.Show(current, s);
            TouchUse(s.Uri);
        }
        Bump(s.Uri);
        MaybeBackstopEvict();
    }
    public Show? GetShow(string uri) { lock (_gate) { if (_shows.TryGetValue(uri, out var s)) { TouchUse(uri); return s; } return null; } }
    public void UpsertEpisode(Episode e)
    {
        lock (_gate)
        {
            _episodes.TryGetValue(e.Uri, out var current);
            _episodes[e.Uri] = StoreEntityMerge.Episode(current, e);
            TouchUse(e.Uri);
        }
        Bump(e.Uri);
        MaybeBackstopEvict();
    }
    public Episode? GetEpisode(string uri) { lock (_gate) { if (_episodes.TryGetValue(uri, out var e)) { TouchUse(uri); return e; } return null; } }

    public void UpsertOwner(Owner owner)
    {
        if (owner is null || UserProfileIds.Normalize(owner.Id) is not { } key) return;
        lock (_gate)
        {
            _owners.TryGetValue(key, out var current);
            _owners[key] = StoreEntityMerge.Owner(current, owner);
        }
        Bump(key);   // an owner landing is what makes a playlist row's byline readable — see StoreLibrarySource
    }

    public Owner? GetOwner(string userUriOrId)
    {
        if (UserProfileIds.Normalize(userUriOrId) is not { } key) return null;
        lock (_gate) return _owners.TryGetValue(key, out var o) ? o : null;
    }

    // A full replace (each fetch yields the complete association; a 304 keeps the prior record with a bumped FetchedAt,
    // handled by the caller). This DOES Bump: the association is now the has-video answer every surface renders
    // (VideoPresence), so a detect landing mid-list is precisely the edge a row indicator must repaint on. It used to
    // be silent side-table data behind a mirrored Track.HasVideo — that mirror is gone, and with it the ability of a
    // row and the expand drawer to disagree about the same fetched fact.
    public void UpsertVideoAssociation(VideoAssociation a) { lock (_gate) _videoAssoc[a.Uri] = a; Bump(a.Uri); }
    public VideoAssociation? GetVideoAssociation(string uri)
    {
        lock (_gate)
        {
            if (_videoAssoc.TryGetValue(uri, out var a)) return a;
            // Miss-bridge (S4): never-detected aliases may only have CanonicalUri stamped — retry once under the
            // already-held lock. Hearts bridge deliberately NOT shipped until unsave targets row.CanonicalUri.
            if (_tracks.TryGetValue(uri, out var t) && t.CanonicalUri is { Length: > 0 } canon
                && !string.Equals(canon, uri, StringComparison.Ordinal)
                && _videoAssoc.TryGetValue(canon, out var bridged))
                return bridged;
            return null;
        }
    }

    // User video overrides DO Bump — unlike the association side table, an attach/remove is a user edit whose availability
    // edge must reach the player (has-video → the audio↔video swap) and any roster view. Two signals per write: the
    // playable's own uri (row indicators, the now-playing recompute) and the roster sentinel (the Settings list).
    public void UpsertVideoOverride(VideoOverride o)
    {
        lock (_gate) _videoOverrides[o.Uri] = o;
        Bump(o.Uri);
        Bump(VideoOverride.ChangeKey);
    }
    public void RemoveVideoOverride(string uri)
    {
        bool removed;
        lock (_gate) removed = _videoOverrides.Remove(uri);
        if (!removed) return;   // no-op elision (§7.4): an absent removal is literal silence
        Bump(uri);
        Bump(VideoOverride.ChangeKey);
    }
    public VideoOverride? GetVideoOverride(string uri) { lock (_gate) return _videoOverrides.TryGetValue(uri, out var o) ? o : null; }
    public IReadOnlyList<VideoOverride> VideoOverrides() { lock (_gate) return new List<VideoOverride>(_videoOverrides.Values); }

    public void SetSaved(string setId, string uri, bool saved, SyncState sync) => SetSavedCore(setId, uri, saved, sync, 0);
    public void SetSaved(string setId, string uri, bool saved, SyncState sync, long addedAtMs) => SetSavedCore(setId, uri, saved, sync, addedAtMs);

    /// <summary>The SetSaved core with no-op elision (§7.4): returns whether the write actually changed the store. A save
    /// that repeats the SAME (set,uri,SyncState) — or an unsave of an already-absent (set,uri) — writes nothing and does
    /// NOT Bump/emit, turning every idempotent echo/delta-overlap into literal silence. A same-key write with a DIFFERENT
    /// SyncState (Pending→Confirmed) still writes + bumps. <paramref name="addedAtMs"/> 0 preserves the existing add
    /// timestamp; a non-zero refinement of an otherwise-identical row updates the timestamp silently (metadata, not a
    /// state change). CachedStore calls this so it can skip the cold dual-write on a pure no-op too. The change decision
    /// is made under _gate; the Bump (emit) fires outside it (the cardinal rule).</summary>
    internal bool SetSavedCore(string setId, string uri, bool saved, SyncState sync, long addedAtMs)
    {
        bool changed;
        lock (_gate)
        {
            bool present = _saved.TryGetValue((setId, uri), out var cur);
            if (saved)
            {
                changed = !present || cur.Sync != sync;   // new, or a state transition (e.g. Pending→Confirmed)
                long at = addedAtMs != 0 ? addedAtMs : (present ? cur.AddedAt : 0);
                if (changed || (present && at != cur.AddedAt))
                {
                    _saved[(setId, uri)] = (sync, at);
                    if (!_savedBySet.TryGetValue(setId, out var set)) _savedBySet[setId] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(uri);
                }
            }
            else
            {
                changed = present;                    // no-op when already absent
                if (changed)
                {
                    _saved.Remove((setId, uri));
                    if (_savedBySet.TryGetValue(setId, out var set)) set.Remove(uri);
                }
            }
        }
        if (changed) Bump(uri, KindForSet(setId));
        return changed;
    }

    public bool IsSaved(string setId, string uri)
    {
        lock (_gate) return _saved.ContainsKey((setId, uri));
    }

    /// <summary>The library sets that have at least one member (the warm-set + pin-gate enumeration seam). A handful of
    /// ids ("liked"/"albums"/"artists"/"shows"/"episodes"/…), so a copy is free.</summary>
    public IReadOnlyList<string> SavedSetIds()
    {
        lock (_gate) return new List<string>(_savedBySet.Keys);
    }

    /// <summary>True when <paramref name="uri"/> is a member of ANY library set — the in-memory mirror of
    /// <c>SELECT 1 FROM collection_items WHERE item_uri=?</c>, which is the first leg of the cold-write pin gate. O(number
    /// of sets), not O(saved), because each set is its own hash set.</summary>
    public bool IsSavedAnywhere(string uri)
    {
        lock (_gate)
        {
            foreach (var kv in _savedBySet) if (kv.Value.Contains(uri)) return true;
            return false;
        }
    }

    public IReadOnlyList<string> SavedUris(string setId)
    {
        lock (_gate) return _savedBySet.TryGetValue(setId, out var set) ? new List<string>(set) : new List<string>();
    }

    public IReadOnlyList<SavedItem> SavedItems(string setId)
    {
        lock (_gate)
        {
            if (!_savedBySet.TryGetValue(setId, out var set)) return Array.Empty<SavedItem>();
            var list = new List<SavedItem>(set.Count);
            foreach (var uri in set)
                list.Add(new SavedItem(uri, _saved.TryGetValue((setId, uri), out var v) ? v.AddedAt : 0));
            return list;
        }
    }

    public void SetMembership(string playlistUri, IReadOnlyList<PlaylistMember> rows, byte[]? baseRev)
    {
        lock (_gate) _membership[playlistUri] = (rows, baseRev);
        Bump(playlistUri);
    }

    public IReadOnlyList<PlaylistMember> Membership(string playlistUri)
    {
        lock (_gate) return _membership.TryGetValue(playlistUri, out var m) ? m.Rows : Array.Empty<PlaylistMember>();
    }

    public bool HasMembership(string playlistUri)
    {
        lock (_gate) return _membership.ContainsKey(playlistUri);
    }

    /// <summary>Drop a resident membership baseline (the WARM-tier evictor calls this); the cold tier keeps it, so the
    /// next access rehydrates it.</summary>
    public void EvictMembership(string playlistUri) { lock (_gate) _membership.Remove(playlistUri); }
    public int ResidentMembershipCount { get { lock (_gate) return _membership.Count; } }

    public byte[]? PlaylistRevision(string playlistUri)
    {
        lock (_gate) return _membership.TryGetValue(playlistUri, out var m) ? m.Rev : null;
    }

    // 1-arg: PRESERVE the stored revision (header hydration re-writes the rootlist rows without touching the rev).
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries)
    {
        lock (_gate) _rootlist = entries;
        Bump("rootlist");
    }

    // 2-arg: set the rootlist AND its revision (null clears).
    public void SetRootlist(IReadOnlyList<RootlistEntry> entries, byte[]? rev)
    {
        lock (_gate) { _rootlist = entries; _rootlistRev = rev; }
        Bump("rootlist");
    }

    public byte[]? RootlistRevision() { lock (_gate) return _rootlistRev; }

    public IReadOnlyList<RootlistEntry> Rootlist()
    {
        lock (_gate) return _rootlist;
    }

    public long Version(string uri)
    {
        lock (_gate) return _versions.TryGetValue(uri, out var v) ? v : 0;
    }

    public void Bump(string uri, CollectionKind? kind = null)
    {
        bool suppressed;
        lock (_gate) { _versions[uri] = _versions.TryGetValue(uri, out var v) ? v + 1 : 1; suppressed = _bulkDepth > 0; }
        if (!suppressed) _changes.OnNext(new StoreChange(uri, Kind: kind));   // during a bulk the per-uri signals are coalesced
    }

    static CollectionKind? KindForSet(string setId) => setId switch
    {
        "albums" => CollectionKind.Albums,
        "artists" => CollectionKind.Artists,
        "shows" or "episodes" => CollectionKind.Shows,
        "playlists" => CollectionKind.Playlists,
        "liked" => CollectionKind.Liked,
        _ => null,
    };

    int _bulkDepth;

    /// <summary>Opens a bulk scope: per-URI change signals are suppressed until the outermost scope closes, then ONE
    /// StoreChange.Bulk fires (subscribers full-recompute). NOTE — suppression is store-wide: a concurrent unrelated write
    /// (e.g. a user save) during a bulk sync is also folded into that single Bulk signal rather than emitting its own
    /// per-URI change. Correct (the Bulk recompute covers it), just coarser; acceptable since bulk syncs are short.</summary>
    public IDisposable BeginBulk()
    {
        lock (_gate) _bulkDepth++;
        return new BulkScope(this);
    }

    void EndBulk()
    {
        bool fire;
        lock (_gate) fire = --_bulkDepth == 0;
        if (fire) { _changes.OnNext(StoreChange.Bulk); MaybeBackstopEvict(); }   // one signal for the whole bulk, then the suppressed backstop
    }

    sealed class BulkScope(InMemoryStore store) : IDisposable
    {
        bool _done;
        public void Dispose() { if (_done) return; _done = true; store.EndBulk(); }
    }
}
