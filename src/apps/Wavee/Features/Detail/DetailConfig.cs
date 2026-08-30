using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core;
using Wavee.Features.Detail;

namespace Wavee;

// The shared detail surface: ONE component (playlist / album / single / liked) parameterized by a per-context config.
// See docs/superpowers/specs/2026-06-18-shared-detail-page-design.md. This file holds the closed value set — the unified
// view model the rail/rows/trailing read from, and the four DetailConfig literals that flip the per-context knobs.

/// <summary>Which detail surface a route resolves to. (Single is the Album path with ≤2 tracks — resolved post-load.)</summary>
public enum DetailKind { Album, Playlist, Liked, Show }

/// <summary>The right-column content the shared detail surface renders — music tracks vs podcast episodes.</summary>
public enum DetailContent { Tracks, Episodes }

/// <summary>The left-rail badge row style.</summary>
public enum BadgeStyle { None, TypeYear, OwnerRow }

/// <summary>The heart affordance semantics (no Core mutation command exists yet — optimistic-local until one lands).</summary>
public enum HeartMode { None, Save, Follow }

/// <summary>
/// The unified detail view model — one shape the rail, the track rows, and the trailing sections all read. The loader
/// maps each <see cref="IMusicLibrary"/> domain record (Album / Playlist / liked-songs) onto this, so the view code is
/// context-agnostic and the per-context differences live entirely in <see cref="DetailConfig"/>.
/// </summary>
public sealed record DetailModel(
    string Title,
    Image? Cover,
    string? ContextUri,                  // album/playlist Uri (or the liked collection Uri) — what PlayAsync plays
    string? BadgeType,                   // "ALBUM" / "EP" / "SINGLE" (album/single)
    string? Year,                        // album/single release year
    string? OwnerName,                   // playlist owner
    Image? OwnerImage,                   // playlist owner avatar
    IReadOnlyList<ArtistRef> Artists,    // billed artists (album/single) — also the owner-row name source
    string? Description,                 // playlist description / release blurb
    string MetaLine,                     // "50 songs · 2 hr 59 min · 2024"
    IReadOnlyList<Track> Tracks,
    Artist? AboutArtist,                 // album/single trailing: About-the-artist + More-by shelf (TopAlbums)
    bool HasDateAdded = false,           // playlist: any track carries an AddedAt → show the Date-added column + sort
    bool HasAddedBy = false,             // playlist: ≥2 distinct contributors → show the Added-by column (collaborative)
    bool HasVideo = false,               // any track has a video → offer the "Videos only" filter + the row indicator
    AlbumKind ReleaseKind = AlbumKind.Album,   // album path: which release type (drives badge + config)
    IReadOnlyList<Artist>? Fans = null,        // album trailing: "Fans also like" artist chips
    IReadOnlyList<PlaylistSummary>? FeaturedOn = null,   // (legacy) album "Featured on"; AlbumTrailing now loads its own — kept for compat
    IReadOnlyList<Album>? MoreByArtist = null,           // album trailing: "More by <artist>" shelf (carried by the getAlbum payload)
    PlaylistCapabilities Capabilities = default,   // playlist: what the user may do (drives read-only vs editable UI when edit lands)
    // Podcast show fields — the surface renders Episodes (not Tracks) when DetailConfig.Content == Episodes.
    IReadOnlyList<Episode>? Episodes = null, string? Publisher = null, IReadOnlyList<string>? Topics = null, double? Rating = null,
    // "About this release" (album): label / copyright / formatted release date; + the album's primary artists WITH
    // avatars for the stacked face-pile header (the count badge folds in the distinct track artists).
    string? Label = null, string? Copyright = null, string? ReleaseDate = null, IReadOnlyList<Artist>? AlbumArtists = null,
    IReadOnlyList<Album>? OtherVersions = null,   // alternate editions of this album (deluxe/remaster/…)
    string? CourtesyLine = null, string? ReleaseDatePrecision = null, int DiscCount = 1,
    string? ShareUrl = null, bool IsPreRelease = false, DateTimeOffset? PreReleaseEnd = null,
    // Playlist-only read model: resolved collaborators plus a lookup used by Added-by cells. Tracks keep the raw wire id.
    IReadOnlyList<Owner>? Collaborators = null, IReadOnlyDictionary<string, Owner>? UserProfilesById = null,
    bool IsPublic = true, string? BasePermissionRevision = null,
    PlaylistTuning? Tuning = null,
    // Daylist rollover window (unix ms): from playlist4 format_attributes when the wire carries them, else the Home
    // card's Pathfinder attributes carried in on the nav preview (DetailPreview.FromPlaylist / the DetailPage merge).
    // 0 = not a daylist / unknown. Drives the FlipCountdown row on the rail and the vertical hero.
    long ExpiresAtMs = 0, long CreatedAtMs = 0,
    // Chart playlist header facts (playlist4 format_attributes, format=="chart"): how many rows are new since the last
    // update, and when that update happened (unix ms). ChartNewEntries == 0 = not a chart / nothing new — drives the
    // "N new entries · <date>" caption on the rail and vertical hero, same gate shape as the daylist countdown above.
    int ChartNewEntries = 0, long ChartUpdatedAtMs = 0,
    // Podcast show: how many episodes the show HAS (its membership baseline), against Episodes.Count = how many are
    // resident. A 700-episode show opens with 300 rows, so the difference is the episode list's load-more affordance.
    int TotalEpisodes = 0)
{
    /// <summary>Podcast show: how far into the show's membership the library has already ASKED (Show.PagedThrough).
    /// The episode list's load-more gate — <c>PagedThrough &lt; TotalEpisodes</c> — and the offset its next page starts
    /// at. Init-only rather than positional for the reason the two below give: exactly one decision point writes it
    /// (MapShow), and every existing `with` expression and DetailModel.Empty would otherwise have to be rewritten.</summary>
    public int PagedThrough { get; init; }

    /// <summary>Shared-element (connected-animation) key for the cover art — set by <c>DetailPage</c> from the route
    /// ("album:"+uri / "pl:"+uri) so the cover flies to/from the like-tagged Home card. Null = no Hero.</summary>
    public string? MorphKey { get; init; }

    /// <summary>Home-card <c>extractedColors.colorDark</c> as opaque ARGB (0 = none). Rides the nav preview so the
    /// detail hero Play / countdown / heart agree with the Home daylist card before CoverColorPlane grades the art.</summary>
    public uint Accent { get; init; }

    // ── the page's own bad news (playlist path) ──────────────────────────────────────────────────────────────────────
    // Init-only, deliberately NOT positional: DetailModel.Empty and every `with` expression in the mappers would have to
    // be rewritten for a new positional parameter, and these two are written by exactly one decision point each.

    /// <summary>The playlist header's tombstone flag (<c>Playlist.DeletedByOwner</c>) — the INPUT the notice rule reads.
    /// Kept separate from <see cref="Notice"/> so the rule stays pure and the fact stays a fact.</summary>
    public bool DeletedByOwner { get; init; }

    /// <summary>Why the page is showing a notice strip instead of its edit affordances (<see cref="PlaylistPageNoticeRules"/>).
    /// <see cref="DetailNotice.None"/> for every ordinary page — and the ONE gate every playlist edit affordance is
    /// routed through (<c>PlaylistInlineEdit.Editable</c>).</summary>
    public DetailNotice Notice { get; init; }

    /// <summary>Has a membership snapshot been adopted for this context (the store holds a baseline for the uri)?
    /// FALSE only for a playlist mapped from a thin header — rootlist-seeded, or a decorate reply without rows — whose
    /// empty <see cref="Tracks"/> is "not loaded yet", not "empty". The list renders shimmer rows and the rail a
    /// shimmer meta line instead of "0 songs · 1 min" / "Nothing here yet" (<see cref="Features.Detail.PlaylistListState"/>).
    /// Default TRUE: every other surface (album / Liked / show) carries its rows with the model.</summary>
    public bool MembershipLoaded { get; init; } = true;

    // ── Upcoming release (album path) ────────────────────────────────────────────────────────────────────────────────
    // Init-only, deliberately NOT positional: every `with` expression and DetailModel.Empty above would have to be
    // rewritten for a new positional parameter, and these three are set by exactly one mapper (MapAlbum).

    /// <summary>The raw release instant (album path only) — the FACT the "Released"/"Releases" tile keys its tense on.
    /// <see cref="ReleaseDate"/> is already formatted for display, so the tense cannot be recovered from it.</summary>
    public DateTimeOffset? ReleaseInstant { get; init; }

    /// <summary>The effective countdown target: the moment SOMETHING about this release is still ahead of us. Null =
    /// fully out. Covers three wire shapes — a declared <c>preReleaseEndDateTime</c>, a partly-released album whose
    /// remaining rows carry a future <c>earliest_live_timestamp</c>, and an album whose date is simply in the future
    /// with no prerelease flag at all (the vaultboy case: <c>IsPreRelease=false</c>, <c>ReleaseDate=2026-09-04</c>).
    /// See <see cref="PreReleaseDerivation"/> for the precedence between them.</summary>
    public DateTimeOffset? UpcomingAt { get; init; }

    /// <summary>The <c>spotify:prerelease:</c> entity for this album, when one exists — the target a PRE-SAVE writes
    /// to. Null when unresolved/offline/already out, in which case the heart falls back to saving the album itself.</summary>
    public string? PreReleaseUri { get; init; }

    public static readonly DetailModel Empty = new(
        "", null, null, null, null, null, null,
        Array.Empty<ArtistRef>(), null, "", Array.Empty<Track>(), null);
}

/// <summary>
/// The closed per-context difference set carried by value (a pure app value — no engine type). The shared
/// <c>DetailPage</c>/<c>DetailShell</c> holds everything structural; this flips the knobs. <see cref="Columns"/> is a
/// SHARED array instance read by both the column header and every row, which is the column-alignment invariant
/// (reference-equal by construction — see <c>DetailTracks</c>).
/// </summary>
public readonly record struct DetailConfig(
    bool TwoColumn,                 // false → single-column: no rail, no backdrop wash
    float RailWidth,
    BadgeStyle Badges,
    bool ShowArtThumb,              // playlist/liked: a small art thumb in the title cell
    bool ShowAlbumColumn,           // playlist/liked: a dedicated Album-name column
    TrackSize[] Columns,            // SHARED by header + rows (the alignment invariant)
    bool CapTitle,                  // playlist/liked: clamp the hero-title width
    ItemsSelectionMode Selection,   // Extended (playlist/album/liked) | None (single)
    bool HasTrailing,               // album/single: About/Fans/More-by (and selects the outer-scroll composition)
    HeartMode Heart,
    bool ShowPlays = false,         // album/single/EP/compilation: a Plays column + per-row video indicator + top-track star
    bool ShowTrackArtist = false,   // show the per-track artist subline (playlist/liked, and compilations — various artists)
    DetailContent Content = DetailContent.Tracks,   // tracks (music) vs episodes (podcast show) for the right column
    bool Recommendations = false,   // owned/collaborative playlist: append the "Recommended songs" extender at the list bottom
    // Tempo + musical key column (extended-metadata kind 222). On for the surfaces where a listener is choosing what
    // to play next from a long list (playlists, Liked); off for album pages, where the running order is the point and
    // the Plays lane already occupies that width.
    bool ShowTempo = false,
    // The expand chevron + versions drawer (alternate recordings, music videos, per-item audio format). On wherever
    // a listener is choosing what to play from a long list; off on podcast/episode surfaces, which have no versions.
    bool ShowVersions = false,
    // Whether this surface may OFFER the Plays column as a user opt-in (the More flyout's toggle + the column itself,
    // gated on WaveeSettings.PlaysColumn). True on playlist / Liked, whose rows carry no count until kind 185 is asked
    // for them. Deliberately NOT the same knob as ShowPlays: ShowPlays is "this profile always has a Plays lane", and
    // it is also what makes the top-track star and DetailTrailing.SeedTrack album-only — an opt-in column must not drag
    // those album semantics onto a playlist.
    bool PlaysColumnOptIn = false,
    // Which persisted rail width/collapsed pair this surface reads and writes (DetailRailPolicy) — Liked is its own
    // scope so it never shares the album's 280 and collapse flag by fallthrough — and whether the wide two-column arm
    // offers the rail grip at all. Every two-column literal below is resizable; the decision is data so a config can
    // opt out without a kind test in the shell.
    RailScope RailScope = RailScope.Album,
    bool RailResizable = true)
{
    // Column track sets. Two shared instances → the header and rows are reference-equal (the alignment invariant).
    // playlist/liked: [ #, TITLE(+thumb+artist), ALBUM, ♥, DUR ]   album/single: [ #, TITLE(+artist), ♥, DUR ]
    // (The Plays lane is not in either track set: it is inserted by the tier-driven ColumnSet, always on album surfaces
    // and on playlist/Liked while the PlaysColumnOptIn setting is on.)
    internal static readonly TrackSize[] ListColumns =
        [TrackSize.Px(36), TrackSize.Star(), TrackSize.Px(200), TrackSize.Px(40), TrackSize.Px(52)];
    internal static readonly TrackSize[] AlbumColumns =
        [TrackSize.Px(36), TrackSize.Star(), TrackSize.Px(40), TrackSize.Px(52)];

    public static DetailConfig Playlist => new(
        TwoColumn: true, RailWidth: WaveeSize.RailPlaylist, Badges: BadgeStyle.OwnerRow,
        ShowArtThumb: true, ShowAlbumColumn: true, Columns: ListColumns, CapTitle: true,
        Selection: ItemsSelectionMode.Extended, HasTrailing: false, Heart: HeartMode.Follow, ShowTrackArtist: true,
        Recommendations: true, ShowTempo: true, ShowVersions: true, PlaysColumnOptIn: true,
        RailScope: RailScope.Playlist);

    public static DetailConfig Album => new(
        TwoColumn: true, RailWidth: WaveeSize.RailAlbum, Badges: BadgeStyle.TypeYear,
        ShowArtThumb: false, ShowAlbumColumn: false, Columns: AlbumColumns, CapTitle: false,
        Selection: ItemsSelectionMode.Extended, HasTrailing: true, Heart: HeartMode.Save, ShowPlays: true,
        ShowVersions: true, RailScope: RailScope.Album);

    // A single == the album surface (trailing sections included) but with no multi-select (1–2 tracks).
    public static DetailConfig Single => Album with { Selection = ItemsSelectionMode.None };

    // A compilation == the album surface but various-artists, so the rows show the per-track artist subline.
    public static DetailConfig Compilation => Album with { ShowTrackArtist = true };

    public static DetailConfig Liked => new(
        TwoColumn: true, RailWidth: WaveeSize.RailPlaylist, Badges: BadgeStyle.None,
        ShowArtThumb: true, ShowAlbumColumn: true, Columns: ListColumns, CapTitle: true,
        Selection: ItemsSelectionMode.Extended, HasTrailing: false, Heart: HeartMode.None, ShowTrackArtist: true,
        ShowTempo: true, ShowVersions: true, PlaysColumnOptIn: true,
        RailScope: RailScope.Liked);

    // A podcast show: the album-style two-column rail (cover · PODCAST pill · title · publisher/episode-count meta ·
    // Play + Follow), with the right column rendering EPISODES (EpisodeList) instead of a track table.
    public static DetailConfig Show => new(
        TwoColumn: true, RailWidth: WaveeSize.RailAlbum, Badges: BadgeStyle.TypeYear,
        ShowArtThumb: false, ShowAlbumColumn: false, Columns: AlbumColumns, CapTitle: false,
        Selection: ItemsSelectionMode.None, HasTrailing: false, Heart: HeartMode.Follow,
        Content: DetailContent.Episodes, RailScope: RailScope.Show);
}

// PreReleaseDerivation lives in its own engine-free file (PreReleaseDerivation.cs) so Wavee.Tests can source-include it.

/// <summary>Shared formatting + small helpers for the detail surface.</summary>
internal static class DetailFormat
{
    /// <summary>A compact release date for a track that is not out yet — "4 Sep", or "4 Sep 2027" once it crosses a
    /// year boundary (a bare day/month a year out reads as though it were imminent). Culture-formatted, since this
    /// lands in the duration lane where every other value is numeric and short.</summary>
    public static string ShortDate(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        return local.Year == DateTimeOffset.Now.Year
            ? local.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>Per-track duration "m:ss" (or "h:mm:ss"). Forwards to <see cref="TrackExpandedFacts.TrackTime"/>, the
    /// engine-free copy Wavee.Tests source-includes: the duration LANE and the expanded row's Duration fact are one
    /// formatter, so a row and its own drawer can never disagree about a track's length.</summary>
    public static string TrackTime(long ms) => TrackExpandedFacts.TrackTime(ms);

    /// <summary>Duration cell: a real length, or an em dash when the row has none yet. Forwards to
    /// <see cref="TrackExpandedFacts.DurationCell"/> so the table, the art card and Popular cannot spell unknown as
    /// <c>0:00</c>.</summary>
    public static string DurationCell(long ms) => TrackExpandedFacts.DurationCell(ms);

    /// <summary>Tempo readout — "101" for a whole BPM, "101.5" when the fraction is meaningful. Spotify reports tempo
    /// as a double (101.0099…), and a full-precision figure in a narrow lane is noise; one decimal is the most a
    /// listener can act on. Invariant culture: this is a technical figure, not a localised quantity, and a comma
    /// decimal separator next to the key label reads as a list.</summary>
    public static string Bpm(double bpm) => TrackExpandedFacts.Bpm(bpm);

    /// <summary>Total-duration phrase "2 hr 59 min" / "47 min".</summary>
    public static string TotalTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        int h = (int)t.TotalHours, m = t.Minutes;
        return h >= 1 ? Strings.Detail.DurationHrMin(h, m) : Strings.Detail.DurationMin(Math.Max(1, m));
    }

    public static long TotalMs(IReadOnlyList<Track> tracks)
    {
        long ms = 0;
        for (int i = 0; i < tracks.Count; i++) ms += tracks[i].DurationMs;
        return ms;
    }

    /// <summary>The Date-added column label: relative for the last week ("Today" / "3 days ago"), else an absolute
    /// date — same calendar year omits the year ("MMM d") so the narrowed Date track stays readable.</summary>
    public static string DateAddedLabel(DateTimeOffset? at)
    {
        if (at is not { } d) return "";
        int days = (int)(DateTimeOffset.Now.Date - d.Date).TotalDays;
        if (days <= 0) return Loc.Get(Strings.Detail.Today);
        if (days == 1) return Loc.Get(Strings.Detail.Yesterday);
        if (days < 7) return Strings.Detail.DaysAgo(days);
        return d.Year == DateTimeOffset.Now.Year
            ? d.ToString("MMM d")
            : d.ToString("MMM d, yyyy");
    }

    /// <summary>The chart-header "last updated" date — an absolute "MMM d" (same-year) / "MMM d, yyyy" date, matching
    /// <see cref="DateAddedLabel"/>'s absolute branch. Never relative ("3 days ago"): the header states WHEN the chart
    /// rolled over, not how long ago that was.</summary>
    public static string ChartUpdatedDateLabel(long unixMs)
    {
        if (unixMs <= 0) return "";
        var d = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        return d.Year == DateTimeOffset.Now.Year ? d.ToString("MMM d") : d.ToString("MMM d, yyyy");
    }

    /// <summary>"· "-joined billed-artist names.</summary>
    public static string ArtistNames(IReadOnlyList<ArtistRef> artists)
    {
        if (artists.Count == 0) return "";
        if (artists.Count == 1) return artists[0].Name;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < artists.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(artists[i].Name); }
        return sb.ToString();
    }
}
