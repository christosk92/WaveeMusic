// ── Theming/Contracts/HomeBoardProps.cs — the props of the pinned Home board and its tiles (contract 1.117) ─────────────────────
//
// The core owns the document, the projection, the verbs and every tile's data (`Home/Board/Hosts`); a theme's board template packs the
// entries with `PackGrid` (metrics from its own rules record), draws its group heads, its tile chrome and its own edit flow, and calls
// verbs. It never mutates the document (spec 8.0). A tile host re-renders only when the form its box carries changes; a tile's live
// values travel as signals or Funcs the view binds.
//
// The model types (`BoardEntry`, `BoardModel`, `HomeSize`, `HomeSourceDef`, `HomeCandidate`, `HomeUndoState`...) are core types in
// `Home/Board` (namespace `Wavee`), the way `Zone` is read by `HomeZoneProps`. `HomeBoardView` is the board's view state
// (`Home/Board/Hosts/HomeBoardView.cs`): never saved.

using System.Collections.Generic;
using System.Collections.Immutable;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using Wavee.HomeUi;

namespace Wavee.Theming.Contracts;

/// <summary>The form a tile draws: the one its packed box can carry (spec 4.2), never only the class it was given.</summary>
public enum HomeTileForm : byte
{
    /// <summary>A row tile / speed-dial cell / small square.</summary>
    S,

    /// <summary>A card.</summary>
    M,

    /// <summary>A double card / wide card / wide tile.</summary>
    L,

    /// <summary>The whole row.</summary>
    Full,
}

/// <summary>The props of <see cref="ViewKeys.HomeBoard"/>.</summary>
public sealed record HomeBoardProps
{
    /// <summary>The board as projected right now (document x Spotify's answer): group heads, items, feed sections, fill cells.</summary>
    public required IReadSignal<BoardModel> Model { get; init; }

    /// <summary>The board's view state: the open tile, Zune's selection, the edit mode, the ring, the add surface and its query.</summary>
    public required HomeBoardView View { get; init; }

    /// <summary>Every flow ends in one of these.</summary>
    public required IHomeBoardVerbs Verbs { get; init; }

    /// <summary>The core tile host of an entry. The theme passes the packed cell and its form rule (the form a box can carry); the host
    /// re-renders only when that form changes.</summary>
    public required Func<BoardEntry, PackCell, Func<PackBox, HomeTileForm>, Element> Tile { get; init; }

    /// <summary>The options body of the open tile (<see cref="ViewKeys.HomeTileOptions"/> through its host).</summary>
    public required Func<BoardEntry, Element> Options { get; init; }

    /// <summary>What Home can hold: the candidates, the search and the counts (live reads), and a candidate's live preview.</summary>
    public required HomeCatalogView Catalog { get; init; }

    /// <summary>The undo ring's state (can undo, can redo, the label, where the last change was).</summary>
    public required IReadSignal<HomeUndoState> Undo { get; init; }

    /// <summary>The grid's controller: the live pack, rects, neighbours, bring-into-view.</summary>
    public required PackGridController Grid { get; init; }

    /// <summary>The page's scroll handle (the board is a child of the page's scroller).</summary>
    public required ScrollHandle Scroll { get; init; }

    /// <summary>The store reads a document newer than this build: Home shows the default and nothing is saved.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The overlay service the menus open on.</summary>
    public IOverlayService? Overlay { get; init; }
}

/// <summary>Every flow of spec section 8 ends in one of these. Each mutating verb is one document command and one undo entry (or
/// coalesced by its tag); the confirmation follows the theme's policy (`HomeConfirm`).</summary>
public interface IHomeBoardVerbs
{
    /// <summary>Pin an entity; it lands after the user's pins, before the fill.</summary>
    void Pin(string pinId, string name, string cover, string? groupId = null);

    /// <summary>Unpin an entity.</summary>
    void Unpin(string pinId);

    /// <summary>Add a candidate at an entry index (-1: its group's natural place) at a size.</summary>
    void Add(HomeCandidate candidate, int atEntryIndex = -1, HomeSize size = HomeSize.Auto);

    /// <summary>Remove a widget or a pin; hide a section; hide a shortcut.</summary>
    void Remove(string entryId);

    /// <summary>Set a size the source declares.</summary>
    void SetSize(string entryId, HomeSize size);

    /// <summary>The next size the source declares.</summary>
    void CycleSize(string entryId);

    /// <summary>Move onto another entry (after a later one, before an earlier one).</summary>
    void MoveOnto(string entryId, string targetEntryId);

    /// <summary>One or more steps in order (tag <c>step:&lt;id&gt;</c>).</summary>
    void MoveBy(string entryId, int delta);

    /// <summary>To the front or the end of its group.</summary>
    void MoveToEdge(string entryId, bool toStart);

    /// <summary>A drop from the pack reorder.</summary>
    void Commit(PackMove move);

    /// <summary>Open a tile for its options (closes any other; begins one undo batch).</summary>
    void Open(string entryId);

    /// <summary>Close the open tile (ends its batch); <paramref name="refocus"/> returns focus to its ⋯.</summary>
    void Close(bool refocus);

    /// <summary>A choice on the open tile, applied at once inside its batch.</summary>
    void SetOption(string key, string value);

    /// <summary>Pause or resume a live source's updates.</summary>
    void ToggleLive(string entryId);

    /// <summary>One step back.</summary>
    void Undo();

    /// <summary>One step forward.</summary>
    void Redo();

    /// <summary>Reset Home (pins stay).</summary>
    void Reset(HomeResetScope scope);

    /// <summary>Bring an entry into view and ring it.</summary>
    void Reveal(string entryId, bool ring = true);

    /// <summary>One announcement.</summary>
    void Say(string text);

    /// <summary>"position 3 of 8 in Pinned".</summary>
    string Where(string entryId);
}

/// <summary>The catalogue as the board's add surfaces read it.</summary>
public sealed record HomeCatalogView
{
    /// <summary>Everything Home can hold right now (a subscribing read: the document, the answer, the library).</summary>
    public required Func<IReadOnlyList<HomeCandidate>> All { get; init; }

    /// <summary>The candidates every word of the query is found in (pure).</summary>
    public required Func<IReadOnlyList<HomeCandidate>, string, IReadOnlyList<HomeCandidate>> Find { get; init; }

    /// <summary>The per-group counts of a hit list.</summary>
    public required Func<IReadOnlyList<HomeCandidate>, HomeCatalogCounts> Count { get; init; }

    /// <summary>The sizes a candidate's source declares (empty: one fixed size).</summary>
    public required Func<HomeCandidate, ImmutableArray<HomeSize>> Sizes { get; init; }

    /// <summary>The size `auto` resolves to in this theme for a candidate.</summary>
    public required Func<HomeCandidate, HomeSize> AutoSize { get; init; }

    /// <summary>A candidate's live preview: the real tile at a size, in a box of the given width and height (DIP).</summary>
    public required Func<HomeCandidate, HomeSize, float, float, Element> Preview { get; init; }
}

/// <summary>What every tile knows about itself: its entry, its packed cell, the form it draws, the verbs and the overlay.</summary>
/// <param name="Entry">The board entry.</param>
/// <param name="Cell">The packed cell (its box and phase as signals).</param>
/// <param name="Form">The form the box carries.</param>
/// <param name="Verbs">The board's verbs (a tile's own ⋯ calls them).</param>
/// <param name="Overlay">The overlay service its menus open on.</param>
public sealed record HomeTileFrame(BoardEntry Entry, PackCell Cell, HomeTileForm Form, IHomeBoardVerbs Verbs, IOverlayService? Overlay);

/// <summary>The props of <see cref="ViewKeys.HomeTilePin"/>.</summary>
public sealed record HomePinTileProps
{
    /// <summary>The tile's frame.</summary>
    public required HomeTileFrame Frame { get; init; }

    /// <summary>The name (a subscribing read: the entity table, else the document's hint).</summary>
    public required Func<string> Name { get; init; }

    /// <summary>The kind and owner line ("Album · Fleetwood Mac").</summary>
    public required Func<string> Line { get; init; }

    /// <summary>The cover, or null.</summary>
    public required Func<string?> Cover { get; init; }

    /// <summary>A round cover (an artist, a profile).</summary>
    public bool Round { get; init; }

    /// <summary>The user's pin (false: one of Spotify's shortcut fill cells).</summary>
    public bool UserPin { get; init; }

    /// <summary>This entity is the context playing.</summary>
    public required Func<bool> Playing { get; init; }

    /// <summary>Open its page.</summary>
    public required Action Open { get; init; }

    /// <summary>Play it (null: nothing to play).</summary>
    public Action? Play { get; init; }

    /// <summary>The card's surface data (its context menu: play, queue, Pin to sidebar / Home ...), or null.</summary>
    public Controls.CardData? Card { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeTileNowPlaying"/>.</summary>
public sealed record HomeNowPlayingTileProps
{
    /// <summary>The tile's frame.</summary>
    public required HomeTileFrame Frame { get; init; }

    /// <summary>Something is loaded.</summary>
    public required Func<bool> HasTrack { get; init; }

    /// <summary>The track's title.</summary>
    public required Func<string> Title { get; init; }

    /// <summary>The artists line.</summary>
    public required Func<string> Line { get; init; }

    /// <summary>The cover.</summary>
    public required Func<string?> Cover { get; init; }

    /// <summary>The position as a hairline (the player bar's own seek rail in its hairline form; bound, repaint only).</summary>
    public required Func<Element> ProgressLine { get; init; }

    /// <summary>Playing (the primary button's glyph).</summary>
    public required IReadSignal<bool> Playing { get; init; }

    /// <summary>The lyric reel, when the "lyric" option is on (null: off): the playing track's lines and active index (the mini player's own
    /// reel, `MiniLyricsFrame`), drawn by the theme's function; it ticks only while the tile shows.</summary>
    public Func<Func<MiniLyricsFrame, Element>, Element>? Lyric { get; init; }

    /// <summary>Play or pause.</summary>
    public required Action PlayPause { get; init; }

    /// <summary>Next track.</summary>
    public required Action Next { get; init; }

    /// <summary>Previous track.</summary>
    public required Action Previous { get; init; }

    /// <summary>Open the now-playing page.</summary>
    public required Action Open { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeTileUpNext"/>.</summary>
public sealed record HomeUpNextTileProps
{
    /// <summary>The tile's frame.</summary>
    public required HomeTileFrame Frame { get; init; }

    /// <summary>The next tracks (the queue panel's own rows; call it inside the render: it subscribes the queue).</summary>
    public required Func<NextUpRow[]> Rows { get; init; }

    /// <summary>Open the queue.</summary>
    public required Action OpenQueue { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeTileRecents"/>.</summary>
public sealed record HomeRecentsTileProps
{
    /// <summary>The tile's frame.</summary>
    public required HomeTileFrame Frame { get; init; }

    /// <summary>The Recents zone's grid props (cells, week, playing); its full form is today's `Home.Zone.RecentGrid` view.</summary>
    public required HomeRecentsProps Recents { get; init; }

    /// <summary>Open the listening history.</summary>
    public required Action OpenHistory { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeTileReleases"/>.</summary>
public sealed record HomeReleasesTileProps
{
    /// <summary>The tile's frame.</summary>
    public required HomeTileFrame Frame { get; init; }

    /// <summary>The release list's zone ("From artists you follow").</summary>
    public required Zone Zone { get; init; }

    /// <summary>The zone's own "See all" (null: none).</summary>
    public Action? SeeAll { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeTileDaylist"/>.</summary>
public sealed record HomeDaylistTileProps
{
    /// <summary>The tile's frame.</summary>
    public required HomeTileFrame Frame { get; init; }

    /// <summary>The daylist zone (the card is its first item).</summary>
    public required Zone Zone { get; init; }

    /// <summary>Today's daylist card through its core host (`Home.Daylist`): the full form places it as it is.</summary>
    public required Element Card { get; init; }

    /// <summary>"Good evening, Chris · your daylist".</summary>
    public required Func<string> Eyebrow { get; init; }

    /// <summary>Play the daylist (null while it has no card).</summary>
    public Action? Play { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeTileSection"/>.</summary>
public sealed record HomeSectionTileProps
{
    /// <summary>The tile's frame.</summary>
    public required HomeTileFrame Frame { get; init; }

    /// <summary>The section as one zone of one kind (its body is today's `Home.Zone.*` key, `HomeZoneKeys.TryKeyOf`).</summary>
    public required Zone Zone { get; init; }

    /// <summary>The template the chooser picked.</summary>
    public SectionTemplate Template { get; init; }

    /// <summary>The section's own "See all" (null: none).</summary>
    public Action? SeeAll { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.HomeTileOptions"/>: the open tile's options, one field per option.</summary>
public sealed record HomeTileOptionsProps
{
    /// <summary>The open entry.</summary>
    public required BoardEntry Entry { get; init; }

    /// <summary>The entry's display name ("Now playing").</summary>
    public required string Title { get; init; }

    /// <summary>The options the source declares.</summary>
    public required ImmutableArray<OptionDef> Options { get; init; }

    /// <summary>An option's current value (a subscribing read of the document).</summary>
    public required Func<string, string> Value { get; init; }

    /// <summary>Apply a choice at once.</summary>
    public required Action<string, string> Set { get; init; }

    /// <summary>The source is live: the "live updates" toggle shows.</summary>
    public bool Live { get; init; }

    /// <summary>Live updates are paused (a subscribing read).</summary>
    public Func<bool>? Paused { get; init; }

    /// <summary>Pause or resume live updates.</summary>
    public Action? ToggleLive { get; init; }

    /// <summary>Close the tile (Done).</summary>
    public required Action Done { get; init; }
}

/// <summary>Why a tile is vacant.</summary>
public enum HomeTileVacancy : byte
{
    /// <summary>Its data has not landed: a flat placeholder at its box.</summary>
    Loading,

    /// <summary>The pinned thing is gone (removed, region-locked, no rights).</summary>
    Unavailable,

    /// <summary>Live updates are paused: the still face.</summary>
    Paused,

    /// <summary>Offline with nothing cached.</summary>
    Offline,

    /// <summary>A source this build does not know (kept in the document, drawn as nothing).</summary>
    Unknown,
}

/// <summary>The props of <see cref="ViewKeys.HomeTileVacant"/>.</summary>
public sealed record HomeTileVacantProps
{
    /// <summary>The tile's frame.</summary>
    public required HomeTileFrame Frame { get; init; }

    /// <summary>Why it is vacant.</summary>
    public required HomeTileVacancy Reason { get; init; }

    /// <summary>The name it had (the document's hint), or empty.</summary>
    public string Name { get; init; } = "";
}
