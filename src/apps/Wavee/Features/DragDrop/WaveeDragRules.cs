using Wavee.Core;

namespace Wavee;

/// <summary>
/// The PURE decisions behind Wavee's drag sources and its playlist drop refusals — engine-free, so <c>Wavee.Tests</c>
/// can compile them (the same split that put the chip's resolution rules in <see cref="WaveeDragChipModel"/>).
///
/// <para>Two things live here. First the KIND MAP: every card surface names its entity with a different enum
/// (<see cref="HomeCardKind"/> on the home feed, <see cref="SearchHitKind"/> in search, <see cref="SidebarEntryKind"/>
/// in the sidebar), and a payload that mislabels its kind silently loses a capability three layers away — an album
/// drag that says "route" cannot resolve tracks, a playlist that says "album" cannot be filed into a folder. One table
/// per source enum, tested, beats a switch re-typed at twenty call sites.</para>
/// </summary>
static class WaveeDragKindMap
{
    /// <summary>Home-feed card kind → drag kind. <see cref="HomeCardKind.Liked"/> is the Liked Songs pseudo-playlist:
    /// it navigates and pins like a playlist and its tracks resolve through the playlist reader, so it maps there.</summary>
    public static WaveeResourceKind Of(HomeCardKind kind) => kind switch
    {
        HomeCardKind.Album => WaveeResourceKind.Album,
        HomeCardKind.Artist => WaveeResourceKind.Artist,
        HomeCardKind.Track => WaveeResourceKind.Track,
        // A podcast and an AUDIOBOOK are both a show here: both arrive under a spotify:show: uri and both resolve
        // through the show reader — the same reasoning the SearchHitKind arm below spells out.
        HomeCardKind.Podcast or HomeCardKind.Audiobook => WaveeResourceKind.Show,
        // Episode never reaches this map: HomePage.CardDrag refuses a drag for it (no by-uri episode read exists, so the
        // payload could be neither pinned nor deposited).
        _ => WaveeResourceKind.Playlist,   // Playlist + Liked
    };

    /// <summary>Search hit kind → drag kind. An AUDIOBOOK is a show in every way this payload cares about (it pins as
    /// one and carries no resolvable track list); <see cref="SearchHitKind.Author"/>/<see cref="SearchHitKind.User"/>
    /// are people with no Wavee resource behind them, so they fall to <see cref="WaveeResourceKind.Route"/> — pinnable,
    /// never depositable.</summary>
    public static WaveeResourceKind Of(SearchHitKind kind) => kind switch
    {
        SearchHitKind.Track => WaveeResourceKind.Track,
        SearchHitKind.Artist => WaveeResourceKind.Artist,
        SearchHitKind.Album => WaveeResourceKind.Album,
        SearchHitKind.Playlist => WaveeResourceKind.Playlist,
        SearchHitKind.Podcast or SearchHitKind.Audiobook => WaveeResourceKind.Show,
        SearchHitKind.Episode => WaveeResourceKind.Episode,
        _ => WaveeResourceKind.Route,
    };

    /// <summary>Spotify URI → drag kind, for the surfaces whose card carries nothing BUT a uri (an artist's pinned
    /// item can target any entity). Mirrors <c>RichText.RouteForUri</c>'s discrimination order — the more specific
    /// <c>:prerelease:</c> scheme before <c>:album:</c> — so a card's drag payload can never disagree with the route
    /// its click navigates to. An unrecognised uri is a <see cref="WaveeResourceKind.Route"/>: pinnable, inert
    /// everywhere else, which is the safe reading of "we don't know what this is".</summary>
    public static WaveeResourceKind OfUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return WaveeResourceKind.Route;
        if (uri == "spotify:collection:tracks") return WaveeResourceKind.Playlist;   // Liked Songs reads as a playlist
        // Kind comes from the ONE parser (hydration-facade-design.md §1.1) instead of seven substring probes, so the
        // "more specific scheme wins" ordering is structural: a prerelease IS its own kind and still drags as an Album.
        return EntityUri.KindOf(uri) switch
        {
            EntityKind.Playlist => WaveeResourceKind.Playlist,
            EntityKind.Prerelease or EntityKind.Album => WaveeResourceKind.Album,
            EntityKind.Artist => WaveeResourceKind.Artist,
            EntityKind.Show => WaveeResourceKind.Show,
            EntityKind.Episode => WaveeResourceKind.Episode,
            EntityKind.Track => WaveeResourceKind.Track,
            _ => WaveeResourceKind.Route,
        };
    }

    /// <summary>Sidebar projection kind → drag kind (the one the sidebar's own payload factory uses).</summary>
    public static WaveeResourceKind Of(SidebarEntryKind kind) => kind switch
    {
        SidebarEntryKind.Playlist => WaveeResourceKind.Playlist,
        SidebarEntryKind.Album => WaveeResourceKind.Album,
        SidebarEntryKind.Artist => WaveeResourceKind.Artist,
        SidebarEntryKind.Show => WaveeResourceKind.Show,
        SidebarEntryKind.Folder => WaveeResourceKind.Folder,
        SidebarEntryKind.Track => WaveeResourceKind.Track,
        _ => WaveeResourceKind.Route,
    };
}

/// <summary>Why a playlist destination turned a drag away. A refusing drop target is TRANSPARENT by design (discovery
/// walks past it to an accepting ancestor), so without a named reason the user sees nothing at all happen — the
/// "cannot drop in this mode" report. Each value maps to one caption the drag chip shows beside its not-allowed
/// glyph.</summary>
enum PlaylistDropRefusal : byte
{
    /// <summary>Nothing to explain — the drop is allowed.</summary>
    None = 0,
    /// <summary>An editorial/daylist/someone-else's playlist: the user simply cannot write to it.</summary>
    NotEditable,
    /// <summary>The track list has not arrived yet, so there is no membership to insert into.</summary>
    Loading,
    /// <summary>The payload carries no tracks and can resolve none — an artist, a route, a podcast show.</summary>
    NoTracks,
    /// <summary>A same-list reorder under a non-natural SORT: display positions no longer name membership rows.</summary>
    Sorted,
    /// <summary>A same-list reorder under a search/filter: the display is a SUBSET, so a slot is ambiguous.</summary>
    Filtered,
    /// <summary>A same-list reorder whose rows (or whose landing anchor) have no membership <c>item_id</c> yet: our own
    /// add is still in flight, so the rows the server knows about are not the rows on screen. Transient by nature — the
    /// ids land with the ack — which is why it says "try again in a moment" rather than naming something to fix.</summary>
    Syncing,
}

/// <summary>The single decision table behind <c>DetailTracks</c>'s insertion <c>CanAccept</c> and its refusal caption:
/// one function answers BOTH, so a refusal can never be cued with a reason the accept test did not actually use (the
/// two drifting apart is how a "cannot drop" ends up unexplained).</summary>
static class PlaylistDropRefusalRules
{
    /// <summary>Evaluate a drop against the destination's live state.
    /// <para>Order matters and is deliberate: page-level write capability first (nothing else can rescue a read-only
    /// playlist), then whether the destination is even loaded, then the payload's own ability to produce tracks, and
    /// only then the same-list-move ambiguities — a foreign COPY is legal under any sort or filter, because it
    /// appends/inserts by display position without having to name existing membership rows.</para></summary>
    /// <param name="editable">The destination is a playlist this user can write to (<c>CanEditItems</c> + a context uri).</param>
    /// <param name="loading">The destination's track list is still Pending (a shimmer, not a list).</param>
    /// <param name="payloadHasTracks">The payload carries a track snapshot or can resolve one.</param>
    /// <param name="sameList">The payload's rows came from THIS playlist — a MOVE, not a copy.</param>
    /// <param name="naturalOrder">The display order IS the membership order (sort = Index, ascending).</param>
    /// <param name="filtered">A search query or a filter chip is narrowing the display.</param>
    /// <param name="rowsKeyed">Every dragged row carries its membership <c>item_id</c> (<c>PlaylistReorderRules.RowsAreKeyed</c>).
    /// Reported LAST of the same-list arms on purpose: sorting and filtering are states the user can act on and fix,
    /// while "still syncing" is a wait — naming a wait first would hide the two refusals that have a remedy.</param>
    public static PlaylistDropRefusal Evaluate(bool editable, bool loading, bool payloadHasTracks,
                                               bool sameList, bool naturalOrder, bool filtered, bool rowsKeyed)
    {
        if (!editable) return PlaylistDropRefusal.NotEditable;
        if (loading) return PlaylistDropRefusal.Loading;
        if (!payloadHasTracks) return PlaylistDropRefusal.NoTracks;
        if (!sameList) return PlaylistDropRefusal.None;
        if (!naturalOrder) return PlaylistDropRefusal.Sorted;
        if (filtered) return PlaylistDropRefusal.Filtered;
        if (!rowsKeyed) return PlaylistDropRefusal.Syncing;
        return PlaylistDropRefusal.None;
    }

    /// <summary>The accept test, expressed against the same table so the two can never disagree.</summary>
    public static bool Accepts(bool editable, bool loading, bool payloadHasTracks,
                               bool sameList, bool naturalOrder, bool filtered, bool rowsKeyed)
        => Evaluate(editable, loading, payloadHasTracks, sameList, naturalOrder, filtered, rowsKeyed) == PlaylistDropRefusal.None;
}

/// <summary>Whether a TAB in the strip is a deposit destination for a resource drag, and whether the payload may land
/// on it. A tab stands for the page behind it, so a tab whose destination is an editable playlist can take tracks the
/// same way that playlist's page body can — that is what makes a cross-tab deposit possible without navigating away
/// mid-gesture. Every other tab stays the pure spring-load waypoint it already was.
/// <para>Engine-free on purpose (the same split as the tables above): "is this tab a destination" is a DECISION, the
/// <c>DropTargetSpec</c> it configures is not.</para></summary>
static class TabDropRules
{
    /// <summary>Only a REAL, writable Spotify playlist is a deposit destination: pseudo-playlists (Liked Songs, an
    /// editorial daylist) navigate like playlists but are not membership lists this app writes to.
    /// <para>DELEGATES to <see cref="PlaylistDepositTargets.IsDepositable"/> — the one predicate. This used to be a third
    /// hand-written copy alongside <c>PlaylistPicker.IsRealPlaylist</c> and <c>Menus.PlaylistDepositItem</c>, with a
    /// comment on each warning that changing one meant changing all three. Kept as a named member here because that is
    /// what reads correctly at the tab-strip call sites.</para></summary>
    public static bool IsDepositablePlaylistUri(string? uri) => PlaylistDepositTargets.IsDepositable(uri);

    /// <summary>May this payload be deposited on the tab standing for <paramref name="targetUri"/>?
    /// <para>The SAME-playlist exclusions are the point of this rule rather than a nicety. A tab drop can only ever
    /// APPEND (there is no slot in a tab), so <c>DepositTracksAsync</c>'s same-list MOVE arm — which needs an insertion
    /// index — cannot engage: a row dragged out of playlist P onto P's own tab would fall through to the copy arm and
    /// duplicate the user's rows into their own playlist. Its container-on-itself guard covers the playlist-onto-itself
    /// case, but only as a SILENT no-op. Refusing here instead means the tab never lights up for a gesture that has
    /// nothing to do, which is the honest cue.</para></summary>
    public static bool AcceptsDeposit(string targetUri, bool targetEditable, bool payloadHasTracks,
                                      string? payloadSourcePlaylistUri, string? payloadUri)
    {
        if (!targetEditable || !payloadHasTracks || !IsDepositablePlaylistUri(targetUri)) return false;
        if (string.Equals(payloadSourcePlaylistUri, targetUri, System.StringComparison.Ordinal)) return false;
        return !string.Equals(payloadUri, targetUri, System.StringComparison.Ordinal);
    }
}

/// <summary>D16 — the COLLAPSED RAIL's transparency rule. A 56-DIP strip of covers is a corridor as much as a set of
/// destinations, and the question a tile has to answer while a drag crosses it is which of the two "no"s it owes.
/// <para>Engine-free like its siblings above, so <c>WaveeDragRulesTests</c> drives the real decision rather than a copy
/// of it (the rail's own <c>DropTargetSpec</c> lives in <c>SidebarPane.ResourceDropSpec</c>).</para></summary>
static class SidebarRailDropRules
{
    /// <summary>Should a rail PLAYLIST tile sit this gesture out entirely?
    ///
    /// <para>TRUE for a rootlist payload that carries no tracks — a FOLDER being re-filed. It has nothing it could add
    /// to a playlist and nothing it could file into one, and it is on its way to a folder tile or to the peeked pane, so
    /// the tile is merely on the route. The landed behaviour was a refusal reading "Nothing to add", which is an
    /// accusation aimed at a drag that was only passing through.</para>
    ///
    /// <para>FALSE for everything else, which keeps both other answers intact: a track-bearing payload over an editable
    /// playlist tile is a real deposit, and a track-bearing payload the tile cannot take still owes the user a
    /// reason.</para></summary>
    public static bool TileTransparent(bool payloadIsRootlistItem, bool payloadCanCopyTracks)
        => payloadIsRootlistItem && !payloadCanCopyTracks;
}
