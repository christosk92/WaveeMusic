namespace Wavee.Core;

// Core domain records. A deliberately small, clean projection of the WaveeMusic domain
// (which today is UI DTOs over proto-backed *CacheEntry storage + a polymorphic ITrackItem).
// Framework-neutral: no FluentGpu / WinUI / Spotify-proto types leak in here.

/// <summary>An image reference (album art, avatar, …). <paramref name="BlurHash"/> backs a cheap placeholder.</summary>
// Url is the single cover. When Url is empty and MosaicTiles carries ≥4 album-cover URLs, renderers compose a 2×2 mosaic
// (a cover-less playlist, the way Spotify does). Carrying the tiles on Image lets every Surfaces.Artwork call site mosaic
// with no per-card plumbing.
public sealed record Image
{
    string _url = "";
    string? _largestUrl;
    System.Collections.Generic.IReadOnlyList<string>? _mosaicTiles;

    public Image(string Url, int? Width = null, int? Height = null, string? BlurHash = null,
        System.Collections.Generic.IReadOnlyList<string>? MosaicTiles = null, string? LargestUrl = null)
    {
        this.Url = Url;
        this.Width = Width;
        this.Height = Height;
        this.BlurHash = BlurHash;
        this.MosaicTiles = MosaicTiles;
        this.LargestUrl = LargestUrl;
    }

    public string Url
    {
        get => _url;
        init => _url = ImageSource.Normalize(value) ?? "";
    }

    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? BlurHash { get; init; }

    /// <summary>Largest rendition known for this artwork. Normal cards keep using <see cref="Url"/>; large immersive
    /// surfaces opt into this source so a lightweight preview URL never becomes a blurry full-width hero.</summary>
    public string? LargestUrl
    {
        get => _largestUrl;
        init
        {
            var normalized = ImageSource.Normalize(value);
            _largestUrl = string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }

    public System.Collections.Generic.IReadOnlyList<string>? MosaicTiles
    {
        get => _mosaicTiles;
        init => _mosaicTiles = ImageSource.NormalizeAll(value);
    }

    public void Deconstruct(out string Url, out int? Width, out int? Height, out string? BlurHash,
        out System.Collections.Generic.IReadOnlyList<string>? MosaicTiles)
    {
        Url = this.Url;
        Width = this.Width;
        Height = this.Height;
        BlurHash = this.BlurHash;
        MosaicTiles = this.MosaicTiles;
    }
}

public sealed record ArtistRef(string Id, string Uri, string Name);
public sealed record AlbumRef(string Id, string Uri, string Name);

public sealed record Artist(
    string Id, string Uri, string Name, Image? Image,
    IReadOnlyList<Album>? TopAlbums = null,
    // Artist-detail facets (docs/plans/wavee/architecture.md §2 "Album & artist"): monthly listeners, follower count, a short bio,
    // and the verified flag. All defaulted/additive — synthesized by the fake source, mapped where a real source has them.
    long MonthlyListeners = 0, long Followers = 0, string? Bio = null, bool Verified = false,
    // The "magazine" facets (the WinUI Spotify-style artist page). All additive/nullable: null/0/empty ⇒ the section
    // hides (the real Spotify export maps what it has; the fake source synthesizes the rest). HeaderImage is a landscape
    // hero backdrop (vs the square avatar Image); TopTracks is the real "Popular" list; Pinned is the hero promo card.
    int WorldRank = 0, Image? HeaderImage = null,
    IReadOnlyList<Track>? TopTracks = null, IReadOnlyList<Album>? AppearsOn = null,
    PinnedItem? Pinned = null, ArtistExtras? Extras = null,
    // Per-facet discography totals (data.artistUnion.discography.<facet>.totalCount). 0 = unknown → callers fall back to
    // the in-memory TopAlbums count. Additive/defaulted; carried so the virtualized grid can size a facet up front and
    // "See all N" gates on the true total rather than the ~10-item overview slice.
    int AlbumsTotal = 0, int SinglesTotal = 0, int CompilationsTotal = 0,
    // Pathfinder discography.latest + popularReleasesAlbums — the artist-page Releases column (masthead + strip). Distinct
    // from TopAlbums (the full facet grids). Null ⇒ UI falls back / hides.
    Album? LatestRelease = null,
    IReadOnlyList<Album>? PopularReleases = null,
    // SWR freshness stamp: when the rich overview (TopTracks/stats/palette/bio) was last fetched. default = never/old →
    // treated as stale and re-fetched on next open (which heals records persisted by an earlier build, whose deserialized
    // FetchedAt is default). Set ONLY by a full-overview write; the store merge keeps the newer value so a thin
    // extended-metadata / NPV / album-derived write never resets the clock.
    DateTimeOffset FetchedAt = default,
    // The OVERVIEW's own stamp (design §2.3: "Age: Rich/Full fresh iff now - OverviewFetchedAt ≤ ArtistRichTtl").
    // FetchedAt is a max-of across every writer, so it answers "when did anything touch this artist?" — which is NOT the
    // question the Rich age gate asks, and NOT the question `MergeExtras`' authoritative-absence discriminator asks.
    // Both need "when did queryArtistOverview last speak?", and only the overview write stamps this field. Optional and
    // last ⇒ documents persisted before it existed deserialize with default (= never) and simply re-fetch once.
    DateTimeOffset OverviewFetchedAt = default,
    // The extended CHART's own stamp — when artist-top-tracks-extensions last answered for this artist, whatever it
    // answered. It exists because PRESENCE cannot express "the chart step ran": HydrationLevels.Of(Artist) calls an
    // artist Full only when TopTracks.Count > ArtistPopularTracks.OverviewSeedCap, so a niche artist whose real chart
    // is 6 tracks long can never reach Full — and ArtistHydration's "already extended AND fresh" gate, keyed on the
    // rung, therefore re-fired the spclient GET on every ask past ExhaustedPlayableTtl (10 min), forever. A chart that
    // legitimately has ≤ the seed cap of rows counts as REACHED once it has been fetched, and this is the fact that
    // says so. Only EnsureChartAsync stamps it (never a V4/overview/merge write), so it answers exactly "when did the
    // chart transport last speak?". Optional and last ⇒ older persisted documents deserialize with default = never.
    DateTimeOffset ChartFetchedAt = default);

/// <summary>Per-facet discography helpers on <see cref="Artist"/> (kept next to the model; the facet split itself is
/// <see cref="DiscographyKind"/> in Library.cs).</summary>
public static class ArtistFacets
{
    /// <summary>The known total for a discography facet (0 = unknown → the caller uses the in-memory count).</summary>
    public static int FacetTotal(this Artist artist, DiscographyKind kind) => kind switch
    {
        DiscographyKind.Singles => artist.SinglesTotal,
        DiscographyKind.Compilations => artist.CompilationsTotal,
        _ => artist.AlbumsTotal,
    };
}

/// <summary>The optional "magazine" facet bundle for an artist (concerts, merch, playlists, videos, top cities, links,
/// gallery, related artists, a derived tour banner). Any empty list ⇒ that section is omitted from the page.</summary>
public sealed record ArtistExtras(
    IReadOnlyList<Concert>? Concerts = null,
    IReadOnlyList<MerchItem>? Merch = null,
    IReadOnlyList<PlaylistRef>? Playlists = null,
    IReadOnlyList<MusicVideo>? MusicVideos = null,
    IReadOnlyList<TopCity>? TopCities = null,
    IReadOnlyList<ExternalLink>? ExternalLinks = null,
    IReadOnlyList<Image>? Gallery = null,
    IReadOnlyList<RelatedArtist>? Related = null,
    TourBanner? Tour = null,
    ArtistWatchFeed? WatchFeed = null,
    ArtistPreRelease? PreRelease = null);

/// <summary>An announced-but-unreleased album on the artist page (Spotify <c>artistUnion.preReleaseV2</c>, which only
/// populates when the request opts in with <c>preReleaseV2: true</c>).
///
/// <paramref name="ReleaseAt"/> is the moment it unlocks — the countdown's target. Null on artists with nothing
/// upcoming, which is the common case, so the card is absent rather than empty.</summary>
public sealed record ArtistPreRelease(
    string Uri,
    string Name,
    Image? Cover,
    DateTimeOffset? ReleaseAt,
    string? Type = null)
{
    /// <summary>Whether this is still genuinely ahead of us, and therefore worth announcing.
    ///
    /// A stored pre-release outlives its own release: an offline session, or any read before the next overview write
    /// clears the field, can hand back a record that already shipped. Every surface that announces an upcoming release
    /// gates on this rather than on the field merely being non-null.
    ///
    /// A null <see cref="ReleaseAt"/> counts as upcoming — that is an announcement without a date, not a lapsed one.</summary>
    public bool IsUpcoming => ReleaseAt is not { } due || due > DateTimeOffset.UtcNow;
}

/// <summary>The resolved identity of an upcoming release — extended-metadata kind 138, the only thing on the wire that
/// links the two schemes (see <see cref="PreReleaseUris"/>).
///
/// The PAIR is the point: <paramref name="PreReleaseUri"/> is the entity a COLLECTION WRITE (a pre-save) addresses,
/// <paramref name="AlbumUri"/> is the entity the app NAVIGATES to. Neither can be derived from the other — their ids
/// differ — so a half-resolved link (one uri without the other) is not a link and must be rejected at the resolver
/// rather than carried here as a null.</summary>
public sealed record PreReleaseLink(
    string PreReleaseUri,
    string AlbumUri,
    DateTimeOffset? ReleaseAt,
    string? Name = null,
    string? Type = null,            // "ALBUM"
    ArtistRef? Artist = null,
    Image? Cover = null)
{
    /// <summary>Gate every announce surface on this, never on the link merely existing — the kind-138 payload has a
    /// 30-day offline TTL, so a cached link outlives its own release and would keep announcing an album that shipped
    /// a fortnight ago.
    ///
    /// A null <see cref="ReleaseAt"/> counts as upcoming: that is announced-but-undated, not lapsed (the same polarity
    /// as <see cref="ArtistPreRelease.IsUpcoming"/>, and the OPPOSITE of <see cref="PinnedItem.IsUpcoming"/> — see the
    /// note there for why the pin has to differ).</summary>
    public bool IsUpcoming => ReleaseAt is not { } due || due > DateTimeOffset.UtcNow;
}

/// <summary>The artist's watch-feed entry point (Spotify <c>artistUnion.watchFeedEntrypoint</c>) — a short looping
/// clip Spotify uses as the artist's moving identity.
///
/// The <c>video</c> node is present on SOME entities and <c>null</c> on others (null for IU, maroon5, icedamericano
/// and every album entrypoint in the capture corpus; populated for others) — so both shapes are live and neither may
/// be assumed. When present it carries a <c>videoType</c> discriminator:
/// <list type="bullet">
/// <item><c>"URL"</c> — <c>fileId</c> is a ready-to-play <c>https://canvaz.scdn.co/…/*.cnvs.mp4</c> URL, surfaced
/// here as <paramref name="CanvasUrl"/>. This is the only shape observed on the wire.</item>
/// <item>anything else — <c>fileId</c> stays an opaque Spotify file id in <paramref name="VideoFileId"/> and is NOT
/// played; resolving it would go through the normal video pipeline.</item>
/// </list>
/// Splitting the two means the "is this playable?" test is the discriminator, never a <c>StartsWith("http")</c> guess.
/// <paramref name="Thumbnail"/> is the still to show before (or instead of) the clip — under reduced motion it is the
/// ONLY thing shown.</summary>
public sealed record ArtistWatchFeed(
    string EntrypointUri,
    Image? Thumbnail,
    string? VideoFileId,
    long StartMs = 0,
    long EndMs = 0,
    string? CanvasUrl = null);

/// <summary>A music video on the artist page (16:9 thumbnail + duration). <paramref name="TrackUri"/> plays it.</summary>
public sealed record MusicVideo(string TrackUri, string Title, Image? Thumbnail, long DurationMs, bool IsExplicit = false);

/// <summary>An upcoming concert/tour date. <paramref name="Venue"/> is the venue name; <paramref name="City"/> the city.
/// The additive location/lineup fields keep the existing compact artist-overview contract source-compatible while
/// allowing the dedicated ArtistConcerts and concertFeed responses to retain their richer summary data.</summary>
public sealed record Concert(
    string Uri,
    string? Title,
    string Venue,
    string City,
    DateTimeOffset Date,
    bool IsFestival = false,
    bool IsNearUser = false,
    string? Region = null,
    string? Country = null,
    IReadOnlyList<ConcertArtist>? Artists = null,
    Image? Image = null,
    // Cover/lineup-extracted dark accent (opaque ARGB; WaveePalette.ToColor lifts it to a renderer colour at the UI
    // boundary). Concerts are NOT Spotify covers, so this stays on the record instead of coming from CoverColorPlane.
    // Null = no extracted colour ⇒ the surface keeps its neutral default.
    uint? AccentColor = null);

/// <summary>A merch product (image + name + display price). <paramref name="ShopUrl"/> opens the external shop.</summary>
public sealed record MerchItem(string Name, string Price, string? Description, Image? Image, string? ShopUrl);

/// <summary>A "Playlists and discovery" entry. <paramref name="Subtitle"/> is the source/owner label (e.g. "Spotify").</summary>
public sealed record PlaylistRef(string Uri, string Name, Image? Cover, string Subtitle);

/// <summary>A "Listened to most in" city. The page draws a proportional bar from <paramref name="Listeners"/>/max.</summary>
public sealed record TopCity(string City, string? Country, long Listeners);

/// <summary>An external/social link. <paramref name="Kind"/> drives the icon glyph.</summary>
public sealed record ExternalLink(string Name, string Url, ExternalLinkKind Kind);
public enum ExternalLinkKind { Generic, Twitter, Instagram, Facebook, YouTube, Wikipedia, TikTok }

/// <summary>A related/"fans also like" artist (carries its own avatar, unlike a bare <see cref="ArtistRef"/>).</summary>
public sealed record RelatedArtist(string Id, string Uri, string Name, Image? Image);

/// <summary>The hero "Pinned" promo card (the artist's spotlighted release + a short comment).
///
/// <paramref name="Uri"/> is the PIN's own uri; <paramref name="ItemUri"/> is the uri of the thing pinned
/// (<c>itemV2.data.uri</c>). They are not always the same entity, which is why navigation goes through
/// <see cref="TargetUri"/> instead of reading either field directly.
///
/// Everything past the original six is nullable WITH a null default on purpose: <c>ArtistOverviewDoc</c> persists this
/// record through STJ positional-record binding (see Backend/Persistence/ArtistOverview.cs), so a document written by
/// an older build has only six values in its JSON. Nullable-with-default is what lets those legacy docs bind to
/// <c>default(T)</c> instead of failing — adding a non-nullable or non-defaulted parameter here is a cache migration.</summary>
public sealed record PinnedItem(
    string Eyebrow, string Title, string Subtitle, string Comment, Image? Cover, string Uri,
    string? ItemUri = null,               // itemV2.data.uri — what the pin points at
    string? ItemType = null,              // itemV2.data.type — "ALBUM" | "SINGLE" | "EP" | …
    string? ItemTypename = null,          // itemV2.data.__typename — "Album" | "PreRelease" | …
    DateTimeOffset? ReleaseAt = null,     // itemV2.data.preReleaseEndDateTime
    Image? BackgroundImage = null)        // profile.pinnedItem.backgroundImageV2.data.sources
{
    /// <summary>Whether this pin is announcing something that has not dropped yet.
    ///
    /// DELIBERATE polarity difference vs <see cref="ArtistPreRelease.IsUpcoming"/> and
    /// <see cref="PreReleaseLink.IsUpcoming"/>: there, a null date means "announced without a date" and counts as
    /// upcoming. Here it must NOT. A pre-release record only exists because something is upcoming, but almost every
    /// pin is an ordinary released album that simply carries no <c>preReleaseEndDateTime</c> — treating those as
    /// upcoming would put a countdown on essentially every artist page.</summary>
    public bool IsUpcoming => ReleaseAt is { } due && due > DateTimeOffset.UtcNow;

    /// <summary>Where clicking the pin should go: the pinned item when the wire named one, else the pin's own uri
    /// (which is what every payload captured before <c>itemV2</c> was mapped hands back).</summary>
    public string TargetUri => ItemUri is { Length: > 0 } t ? t : Uri;
}

/// <summary>The "On tour now" banner copy, derived from the concert list (eyebrow/headline/subline + a live flag).</summary>
public sealed record TourBanner(string Eyebrow, string Headline, string Subline, bool IsLive);

/// <summary>The release type — drives the detail-page badge, the layout (a single is a one-track release), and whether
/// the track rows show a per-track artist (compilations are various-artists).</summary>
public enum AlbumKind { Single, EP, Album, Compilation }

/// <summary>How complete an album read-model is. Summary rows come from search/home, Tracks from extended metadata,
/// and Full from Pathfinder getAlbum. Stores must never replace a higher level with a lower one.</summary>
public enum AlbumHydrationLevel { Summary, Tracks, Full }

public sealed record Album(
    string Id, string Uri, string Name, Image? Cover,
    IReadOnlyList<ArtistRef> Artists, int Year, int TrackCount,
    IReadOnlyList<Track>? Tracks = null, AlbumKind Kind = AlbumKind.Album,
    IReadOnlyList<Album>? MoreByArtist = null,
    // "About this release" facets (getAlbum: label, copyright.items[].text, date.isoString) + the album's primary artists
    // WITH avatars (albumUnion.artists.visuals) for the stacked face-pile header. All additive/nullable.
    string? Label = null, string? Copyright = null, string? ReleaseDate = null,
    IReadOnlyList<Artist>? ArtistsDetailed = null,
    // "Other versions" — alternate editions of THIS album (deluxe/remaster/anniversary), from albumUnion.releases.items.
    IReadOnlyList<Album>? OtherVersions = null,
    // Remaining getAlbum envelope fields used by the release panel and actions.
    string? CourtesyLine = null, string? ReleaseDatePrecision = null, int DiscCount = 1,
    string? ShareUrl = null, bool IsPreRelease = false, DateTimeOffset? PreReleaseEnd = null,
    AlbumHydrationLevel Hydration = AlbumHydrationLevel.Summary);

public sealed record Track(
    string Id, string Uri, string Title,
    IReadOnlyList<ArtistRef> Artists, AlbumRef Album,
    long DurationMs, bool IsExplicit, Image? Image,
    // Per-playlist membership metadata (null outside a user playlist): when a track was added, and by whom. The detail
    // page surfaces these as optional columns. Editorial lists DO stamp AddedAt (a bulk republish instant); they omit
    // AddedBy. 0/missing membership timestamps stay null via JoinMembership.
    DateTimeOffset? AddedAt = null, string? AddedBy = null,
    // NOTE: "has a music video" is deliberately NOT a field here. It is a property of the CATALOGUE ENTRY, answered by
    // the VideoAssociation plane (extended-metadata kind 99) and read through VideoPresence. A row is written by half a
    // dozen sources that know nothing about videos, so a copy on this record needed an OR-merge to survive them and
    // still drifted out of step with the association a detect pass had just stored — a row saying "no video" while its
    // own expand drawer listed one.
    long PlayCount = 0,       // stream count (album pages show a Plays column; the top-played track gets a star)
    // Source-agnostic seam (see docs/plans/wavee/architecture.md §5): which provider this track came from, how it plays, and
    // whether it is playable in this context. Default = a streamed, playable, source-unspecified track.
    TrackOrigin Origin = TrackOrigin.Streamed,
    // NULLABLE on purpose: null = "nobody has told us", which is NOT the same as "playable". Only getAlbum/getTrack
    // carry a playability verdict; every other write (cluster, library, extended-metadata) leaves it unknown. Defaulting
    // those to Playable made "field absent" and "confirmed playable" indistinguishable, so an unreleased track on a
    // partially-released album was indistinguishable from a normal one.
    Availability? Availability = null,
    // When this track goes (or went) live, from TrackV4's earliest_live_timestamp — present on every payload in the
    // capture, so it is the per-track release instant. A value in the FUTURE means the track is announced but not out,
    // which is what lets a partially-released album say WHEN each pending row arrives instead of just that it is
    // pending. Null = the metadata plane has not landed for this row.
    DateTimeOffset? AvailableAt = null,
    string? Source = null,
    // Per-context membership uid (PlaylistMember.ItemId) for Connect skip_to.track_uid + embedded page uids. READ-MODEL
    // ONLY: stamped on the JoinMembership copy, never passed to UpsertTrack (EntityJson omits nulls → never persisted).
    string? ContextUid = null,
    // ISRC recording id (e.g. "USRC17607839"), sourced from the extended-metadata Track.external_id (type "isrc"). Drives
    // the lyrics search's exact-recording fast-path (Musixmatch track_isrc). Null when unknown (thin cluster / Pathfinder).
    string? Isrc = null,
    // Tempo + musical key from extension kind 222 (playlistmixing audio_attributes v2), prefetched alongside the row
    // bundle so the track-row tempo column never pops in late. Null = unknown (never rendered as "0 BPM").
    double? TempoBpm = null,
    string? MusicalKey = null,     // the key name as the server spells it: "A", "F", "B"
    string? CamelotCode = null,    // Camelot-wheel slot: 1B=B, 7B=F, 11B=A (self-validated against the wheel)
    uint? CamelotColor = null,     // the wheel-slot colour Spotify pairs with CamelotCode (opaque ARGB)
    // Descriptor tags from extension kind 6 (TRACK_DESCRIPTOR): the genre/mood/activity concepts Spotify itself uses
    // for the Liked Songs content-filter chips. Presentation form ("K-Pop", "Energetic"), in the server's own order,
    // which is descending weight. Null = not fetched; empty is a real "this track has none".
    IReadOnlyList<string>? Tags = null,
    // Linked-URI canonical playable (TrackV4 canonical_uri). Null = unknown-or-self. Null-coalesce merge like Isrc;
    // EntityJson omit-null → free persist. Video miss-bridge + recovery promotion stamp it.
    string? CanonicalUri = null,
    // Album release year from TrackV4 nested album.date.year. 0 = unknown (lean used to drop field 6). Same 0-is-unknown
    // merge as Album.Year: a later thin upsert must not blank a year the catalogue already wrote.
    int Year = 0,
    // Chart-playlist rank movement (playlist4 ItemAttributes.format_attributes on a format=="chart" list). READ-MODEL
    // ONLY, exactly like ContextUid: stamped by StoreLibrarySource.JoinMembership from the owning PlaylistMember, never
    // passed to UpsertTrack. Null = not a chart row (or chart facts not yet joined).
    ChartEntry? Chart = null);

/// <summary>A chart playlist's per-row rank-movement status (playlist4 <c>ItemAttributes.format_attributes</c> on a
/// <c>format=="chart"</c> list — desktop-verified wire values <c>EQUAL</c>/<c>UP</c>/<c>DOWN</c>/<c>NEW</c>). Any other
/// string (a status this client has never observed) maps to <see cref="Unknown"/>, which renders as no glyph — the
/// "we don't understand this" fallback, never a guessed arrow.</summary>
public enum ChartEntryStatus : byte { Unknown, Equal, Up, Down, New }

/// <summary>One track's chart-rank facts for the row it currently occupies on a chart playlist. <see cref="PreviousPos"/>
/// is <c>0</c> when the wire carries none (the <see cref="ChartEntryStatus.New"/> case — a fresh entry has no prior
/// position). <see cref="Rank"/> is the chart's own ranking metric (a play-count-like number on the observed
/// <c>rank_type=="plays"</c> wire, e.g. 41545) — opaque to us; carried for completeness, not currently rendered.</summary>
public readonly record struct ChartEntry(ChartEntryStatus Status, int CurrentPos, int PreviousPos, long Rank);

/// <summary>A per-track credit. <paramref name="RoleGroup"/> groups rows such as composition, production, or performers.</summary>
public sealed record TrackCredit(string Name, string Role, string? RoleGroup = null,
    string? ArtistUri = null, bool Linkable = false);

/// <summary>The Spotify Canvas short looping cover video. Modelled ahead of video rendering.</summary>
public sealed record TrackCanvas(string? FileId, string? Type, string? EntityUri, string? Url);

/// <summary>The track half of the NPV payload. The raw response is TTL-cached by PathfinderResource; this is not persisted.</summary>
public sealed record TrackNpvInfo(string TrackUri, IReadOnlyList<TrackCredit> Credits,
    IReadOnlyList<string> CreditSources, TrackCanvas? Canvas, IReadOnlyList<MerchItem> Merch);

/// <summary>The uncapped credits drawer for one track. Same rows as <see cref="TrackNpvInfo.Credits"/>, but complete:
/// the NPV path caps contributors at 10, this one carries the whole liner note in the server's own grouping and order.
/// <paramref name="Sources"/> is the attribution line (the record label) — the same shape as
/// <see cref="TrackNpvInfo.CreditSources"/>, so both feed the one renderer. Not persisted.</summary>
public sealed record TrackCredits(IReadOnlyList<TrackCredit> Credits, IReadOnlyList<string> Sources);

/// <summary>Everything the Now Playing rail needs for one track: a merged artist plus track-scoped extras.</summary>
public sealed record NowPlayingInfo(Artist? About, TrackNpvInfo? Track);

/// <summary>How a track plays — streamed from a remote source (CDN) or decoded from a local file. The seam routes
/// playback by this; default is Streamed (the synthetic catalog's shape).</summary>
public enum TrackOrigin { Streamed, Local }

/// <summary>Whether a track can be played in the current context (account tier / market / delisting). First-class so
/// the UI dims+disables rather than failing at play time. Extensible to a reason later.</summary>
public enum Availability { Playable, Unavailable }

/// <summary>The one "is this row out yet?" predicate (kept beside <see cref="Availability"/>, the enum it reads).</summary>
public static class TrackAvailability
{
    /// <summary>Is this row announced but not out? The ONE definition the greyed row (Components/TrackRow.cs), the
    /// play gate (Features/Detail/DetailTracks.PlayRow) and the "N of M songs" fact tile (DetailTrailing) all share,
    /// so those three can never disagree about which rows are pending.
    ///
    /// The <see cref="Track.AvailableAt"/> clause is what makes the RELEASE-DROP transition correct without a refetch:
    /// the server's <see cref="Availability"/> verdict is frozen at fetch time, but <c>earliest_live_timestamp</c> is
    /// an INSTANT, so a row whose moment has passed becomes playable the next time the page renders instead of staying
    /// grey until the album is read again.</summary>
    public static bool IsNotYetOut(this Track t)
        => t.Availability is Availability.Unavailable
           && !(t.AvailableAt is { } at && at <= DateTimeOffset.UtcNow);
}

/// <summary>A content owner/curator (e.g. a playlist owner) — richer than a bare name; carries an avatar image.</summary>
public sealed record Owner(string Id, string Name, Image? Avatar);

/// <summary>What the current user may do to a playlist (mirrors Spotify's currentUserCapabilities). The UI gates its
/// edit affordances on these — an editorial / not-owned playlist renders read-only. <c>default</c> = no rights AND
/// <see cref="Known"/> false: a header that has not carried a capabilities block yet (a rootlist-seeded thin row) is
/// "unknown", which the page must never read as "revoked" — only a writer that saw the real block sets Known.</summary>
public readonly record struct PlaylistCapabilities(
    bool CanView, bool CanEditItems, bool CanEditMetadata, bool IsCollaborative, bool IsOwner,
    bool CanAdministratePermissions = false, bool Known = false);

/// <summary>Stable playlist row identity for duplicate-safe remove/move commands.</summary>
public readonly record struct PlaylistRowRef(int Index, string Uri, string ItemId);

/// <summary>Playlist permission level for invite/base-level writes (mirrors playlist_permission.proto).</summary>
public enum PlaylistPermissionLevel { Blocked = 1, Viewer = 2, Contributor = 3 }

/// <summary>Base permission state from GET/POST <c>/permission/base</c>.</summary>
public readonly record struct PlaylistBasePermission(PlaylistPermissionLevel Level, string Revision)
{
    public bool IsPublic => Level != PlaylistPermissionLevel.Blocked;
}

/// <summary>A server-advertised playlist session-control choice. Identifiers are opaque provider values.</summary>
public enum PlaylistTuningOptionKind : byte { Choice, Reset }

public sealed record PlaylistTuningOption(
    string Identifier,
    string? DisplayName,
    PlaylistTuningOptionKind Kind);

/// <summary>Revision-coherent playlist session controls carried by a full playlist snapshot.</summary>
public sealed record PlaylistTuning(
    byte[] Revision,
    IReadOnlyList<PlaylistTuningOption> Available,
    string? SelectedIdentifier);

public sealed record Playlist(
    string Id, string Uri, string Name, string? Description, string OwnerName,
    Image? Cover, int TrackCount, IReadOnlyList<Track>? Tracks = null,
    // Source-agnostic seam (see docs/plans/wavee/architecture.md §5): the real owner, the user's capabilities (drives the
    // read-only vs editable UI), the recommender format (daily-mix/editorial/…), and which provider this came from.
    Owner? Owner = null, PlaylistCapabilities Capabilities = default, string? Format = null, string? Source = null,
    // Playlist-context user overlay, projected at read time from owner + added_by values. The store wire rows keep raw ids.
    IReadOnlyList<Owner>? Collaborators = null,
    // Owner visibility from permission/base (authoritative; default true until fetched).
    bool IsPublic = true, string? BasePermissionRevision = null,
    // Server-driven automatic-playlist controls. Exposed only while this revision matches membership.
    PlaylistTuning? Tuning = null,
    // Daylist rollover window (format_attributes `expires`/`created`, unix ms). 0 = unknown / not a daylist.
    long DaylistExpiresAtMs = 0, long DaylistCreatedAtMs = 0,
    // TOMBSTONE (P1): the owner deleted this playlist remotely (playlist4 `ListAttributes.deleted_by_owner`). Latching:
    // the store merge is `incoming || current`, so once a delete is observed no later header write can un-delete it.
    bool DeletedByOwner = false,
    // Chart-playlist header facts (format_attributes on a format=="chart" list: `new_entries_count`, `last_updated`,
    // `rank_type`). 0 / null = unknown or not a chart. Drives the "N new entries · <date>" caption on the detail rail
    // and vertical hero, mirroring the daylist window fields above.
    int ChartNewEntries = 0, long ChartUpdatedAtMs = 0, string? ChartRankType = null);

public enum QueueBucket { NowPlaying, UserQueue, NextUp, History }

/// <summary>A session-stable queue-item identity: a monotonic <see cref="ulong"/> minted once at insertion (never
/// index-derived, never reused, survives reorder/remove/continuation-append). <c>0</c> is the sentinel "no id".</summary>
public readonly record struct QueueItemId(ulong Value)
{
    public bool IsNone => Value == 0;
    public static readonly QueueItemId None = default;
}

/// <summary>The wire provider of a queue row — a context continuation, a user-queued item, or an autoplay-station tail row.</summary>
public enum QueueProvider : byte { Context, Queue, Autoplay }

/// <summary>Wire-token ↔ <see cref="QueueProvider"/> bridge (the Connect protocol carries "context"/"queue"/"autoplay").</summary>
public static class QueueProviderExtensions
{
    public static string ToWire(this QueueProvider p) => p switch
    {
        QueueProvider.Queue => "queue",
        QueueProvider.Autoplay => "autoplay",
        _ => "context",
    };

    public static QueueProvider FromWire(string? wire) => wire switch
    {
        "queue" => QueueProvider.Queue,
        "autoplay" => QueueProvider.Autoplay,
        _ => QueueProvider.Context,
    };
}

// QueueEntry carries the session-stable id + provider enum; EntryId is DERIVED ("i{ItemId}") for wire/diagnostics and is
// never parsed for position (F5). IsAutoplay is redundant with Provider == Autoplay but kept for the display path.
public sealed record QueueEntry(
    QueueItemId ItemId,
    string EntryId,
    Track Track,
    QueueBucket Bucket,
    QueueProvider Provider,
    bool IsAutoplay,
    string Uid = "",
    System.Collections.Generic.IReadOnlyDictionary<string, string>? Metadata = null);

// ── Podcasts (docs/plans/wavee/architecture.md §2 "Podcasts / shows / episodes") ──────────────────────────────────────────────────
/// <summary>A podcast episode. <paramref name="ProgressMs"/> is the resume position (0 = unplayed); a real source also
/// carries paywall/preview, transcripts and chapters — modelled as additive fields when they arrive.
/// <para><paramref name="ShowUri"/> is the owning show's uri when the catalogue told us (EpisodeV4 embeds a show ref:
/// gid + name). It is what makes an episode row's subtitle a LINK — <see cref="EpisodeAsTrack"/> puts it in the album
/// slot, where every list surface already resolves "go to the container". Null = the writer did not know; persisted
/// blobs written before the field existed deserialize to null, which is exactly the old uri-less behaviour.</para></summary>
public sealed record Episode(
    string Id, string Uri, string Title, string ShowName, Image? Image,
    long DurationMs, DateTimeOffset PublishedAt, string? Description = null, long ProgressMs = 0,
    string? ShowUri = null);

/// <summary>A podcast show. Episodes hydrate on the detail read (like an album's tracks).</summary>
/// <param name="Episodes">The RESIDENT episodes, joined at read from the show's ordered membership — the first
/// <c>HydrationLevels.ShowOpenPage</c> after an Open, more after each load-more page (design §2.3).</param>
/// <param name="TotalEpisodes">How many episodes the show's membership baseline actually has, whether or not they are
/// resident yet. 0 = unknown (no baseline / a fake source). It is what tells the episode list there IS a next page —
/// a READ-MODEL field stamped by <c>StoreLibrarySource.GetShowAsync</c>, never written into the stored entity.</param>
/// <param name="PagedThrough">THE paging cursor: how far into the membership list the source has already ASKED, as an
/// offset (so <c>PagedThrough == TotalEpisodes</c> means "nothing left to ask for"). Also a READ-MODEL field stamped by
/// <c>StoreLibrarySource</c>, never stored. It replaces comparing the RESIDENT count to the membership count as the
/// load-more gate: members that cannot hydrate at all (a region-locked or withdrawn episode) leave the resident count
/// permanently short of the total, which pinned the "Load more" pill on screen forever and made every tap re-ask the
/// same unanswerable page. A cursor advances whether or not the page produced rows.</param>
public sealed record Show(
    string Id, string Uri, string Name, string Publisher, Image? Cover, string? Description = null,
    IReadOnlyList<Episode>? Episodes = null, int TotalEpisodes = 0, int PagedThrough = 0);
