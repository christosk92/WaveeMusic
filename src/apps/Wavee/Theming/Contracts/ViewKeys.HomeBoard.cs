// ── Theming/Contracts/ViewKeys.HomeBoard.cs — the pinned Home board's published view keys (contract 1.117) ───────────────────
//
// The board replaces the zone column on the All facet (pinned-home-tech.md T1): `Home.Board` is the theme's pack grid with its group
// heads and its own edit chrome; every tile is a `Home.Tile.*` key mounted by the core tile host of its source (`Home/Board/Hosts`),
// which picks the form the packed box can carry. Edit chrome (WinUI's add band, Classic's edit bar and chips, Zune's app bar) is
// theme code and has no key (T6).
//
// Node keys are part of the contract so verbs, traces and lint find things in every theme: `home:board` (the grid),
// `home:group:<groupId>` (a group head), `home:tile:<entryId>` (a tile wrapper), `home:tile:<entryId>:tools` (its edit tools),
// `home:slot` (the drop outline), `home:add` (the open add surface), `home:edit` (Classic's bar, Zune's app bar), `home:undo` (Zune's word).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The pinned Home board on the All facet: the pack grid, the group heads, the empty Pinned row and the theme's edit
    /// chrome. Mounted by `HomeBoardHost` through <see cref="HomePageProps.Board"/>. Props: <see cref="HomeBoardProps"/>.</summary>
    public static readonly ViewKey<HomeBoardProps> HomeBoard = new("Home.Board");

    /// <summary>A pinned entity (album, playlist, artist, show, Liked Songs) or one of Spotify's shortcut fill cells. Mounted by `PinTileHost`.</summary>
    public static readonly ViewKey<HomePinTileProps> HomeTilePin = new("Home.Tile.Pin");

    /// <summary>The Now playing widget. Mounted by `NowPlayingTileHost`.</summary>
    public static readonly ViewKey<HomeNowPlayingTileProps> HomeTileNowPlaying = new("Home.Tile.NowPlaying");

    /// <summary>The Up next widget. Mounted by `UpNextTileHost`.</summary>
    public static readonly ViewKey<HomeUpNextTileProps> HomeTileUpNext = new("Home.Tile.UpNext");

    /// <summary>The Recently played widget (the grid and the week). Mounted by `RecentsTileHost`.</summary>
    public static readonly ViewKey<HomeRecentsTileProps> HomeTileRecents = new("Home.Tile.Recents");

    /// <summary>The New releases widget. Mounted by `ReleasesTileHost`.</summary>
    public static readonly ViewKey<HomeReleasesTileProps> HomeTileReleases = new("Home.Tile.Releases");

    /// <summary>The Daylist widget. Mounted by `DaylistTileHost`.</summary>
    public static readonly ViewKey<HomeDaylistTileProps> HomeTileDaylist = new("Home.Tile.Daylist");

    /// <summary>One Spotify section; its full form mounts today's `Home.Zone.*` body. Mounted by `SectionTileHost`.</summary>
    public static readonly ViewKey<HomeSectionTileProps> HomeTileSection = new("Home.Tile.Section");

    /// <summary>The options body of the open tile (one field per option; Done). Mounted by `HomeBoardHost` through <see cref="HomeBoardProps.Options"/>.</summary>
    public static readonly ViewKey<HomeTileOptionsProps> HomeTileOptions = new("Home.Tile.Options");

    /// <summary>A tile with nothing to show at its box: still loading, unavailable, paused, offline. Mounted by `HomeTileHost`.</summary>
    public static readonly ViewKey<HomeTileVacantProps> HomeTileVacant = new("Home.Tile.Vacant");
}

/// <summary>The node keys of the board (contract, see the file header).</summary>
public static class HomeBoardKeys
{
    /// <summary>The pack grid.</summary>
    public const string Board = "home:board";

    /// <summary>The open add surface.</summary>
    public const string Add = "home:add";

    /// <summary>The drop outline.</summary>
    public const string Slot = "home:slot";

    /// <summary>Classic's edit bar, Zune's app bar.</summary>
    public const string Edit = "home:edit";

    /// <summary>Zune's undo word.</summary>
    public const string Undo = "home:undo";

    /// <summary>A group head's key.</summary>
    public static string Group(string groupId) => "home:group:" + groupId;

    /// <summary>A tile wrapper's key.</summary>
    public static string Tile(string entryId) => "home:tile:" + entryId;

    /// <summary>A tile's edit tools' key.</summary>
    public static string Tools(string entryId) => "home:tile:" + entryId + ":tools";
}
