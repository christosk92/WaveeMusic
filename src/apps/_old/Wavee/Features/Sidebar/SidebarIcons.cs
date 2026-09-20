using FluentGpu.Controls;
using Wavee.Core.Sidebar;

namespace Wavee;

// The glyph whitelist for hand-placed sidebar items. A layout document is a plain JSON file the user can edit by hand,
// so IconOverride is a NAME ("Heart"), never a codepoint — this map is the only thing that turns a name into a glyph, so
// no hand-edited document can inject an arbitrary codepoint (or a private-use char the icon font does not carry).
//
// LAYERING: the NAME list lives in Wavee.Core (SidebarIconNames) because the pure reducer validates IconOverride against
// it and Wavee.Core may not reference FluentGpu.Controls.Icons. This file owns the name -> glyph half. Every name in the
// list resolves here; the switch is exhaustive by construction (SidebarIconTests-free because Glyph falls back safely).

static class SidebarIcons
{
    /// <summary>Ordered, stable — this IS the icon-picker order in the property panel.</summary>
    public static readonly string[] Allowed = SidebarIconNames.Allowed;

    public static bool IsAllowed(string? name) => SidebarIconNames.IsAllowed(name);

    /// <summary>Maps a whitelisted icon name to its glyph. An unknown, non-whitelisted or null name yields
    /// <paramref name="fallback"/> — a hand-edited document degrades to the row's natural glyph, never to a blank box.</summary>
    public static string Glyph(string? name, string fallback) => name switch
    {
        "MusicNote" => Icons.MusicNote,
        "Heart" => Icons.Heart,
        "Album" => Icons.Album,
        "Contact" => Icons.Contact,
        "RadioTower" => Icons.RadioTower,
        "Folder" => Icons.Folder,
        "FolderOpen" => Icons.FolderOpen,
        "Home" => Icons.Home,
        "Search" => Icons.Search,
        "Clock" => Icons.Clock,
        "Star" => Icons.Star,
        "FavoriteStar" => Icons.FavoriteStar,
        "Tag" => Icons.Tag,
        "Headphones" => Icons.Headphones,
        "Microphone" => Icons.Microphone,
        "Movie" => Icons.Movie,
        "Picture" => Icons.Picture,
        "Queue" => Icons.Queue,
        "Shuffle" => Icons.Shuffle,
        "Link" => Icons.Link,
        "Grid" => Icons.Grid,
        "List" => Icons.List,
        "Pin" => Icons.Pin,
        "Settings" => Icons.Settings,
        "Code" => Icons.Code,
        "Globe" => Icons.Globe,
        "Device" => Icons.Device,
        "Friends" => Icons.Friends,
        "Equalizer" => Icons.Equalizer,
        "Download" => Icons.Download,
        _ => fallback,
    };

    /// <summary>The glyph for an item, preferring its override and falling back to the entity family's natural mark.
    /// Route items fall back to their <c>ShellNav.Dest</c> glyph, which the caller passes in.</summary>
    public static string For(SidebarItemSpec item, string fallback)
        => Glyph(item.IconOverride, fallback);

    /// <summary>The natural mark for an entity family — the placeholder glyph a row wears before (or instead of) art.</summary>
    public static string ForEntityKind(SidebarEntityKind kind) => kind switch
    {
        SidebarEntityKind.Playlist => Icons.MusicNote,
        SidebarEntityKind.Album => Icons.Album,
        SidebarEntityKind.Artist => Icons.Contact,
        SidebarEntityKind.Show => Icons.RadioTower,
        SidebarEntityKind.PlaylistFolder => Icons.Folder,
        SidebarEntityKind.Track => Icons.MusicNote,
        _ => Icons.MusicNote,
    };
}
