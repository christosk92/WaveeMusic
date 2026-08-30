using FluentGpu.Controls;

namespace Wavee;

/// <summary>
/// The action icon-KEY table: actions carry a semantic key, and the ONE <see cref="Resolve"/> maps it to an
/// <see cref="IconRef"/> — a layered ThemedIcon name where the starter set covers it (with a Segoe Fluent glyph
/// fallback that renders until the harvest is complete), else a plain glyph. Only this table changes as the ThemedIcon
/// harvest grows — action definitions never do.
/// </summary>
public static class ActionIcons
{
    public const string Play = "play";
    public const string PlayNext = "play-next";
    public const string Queue = "queue";
    public const string Like = "like";
    public const string Save = "save";
    /// <summary>Legacy semantic key retained for existing app actions; new descriptors choose Like or Save explicitly.</summary>
    public const string Heart = "heart";
    public const string Add = "add";
    public const string Album = "album";
    public const string Artist = "artist";
    public const string Link = "link";
    public const string Remove = "remove";
    public const string Delete = "delete";
    public const string Open = "open";
    public const string Rename = "rename";
    public const string People = "people";
    public const string Globe = "globe";
    public const string Credits = "credits";
    public const string Share = "share";
    public const string CopyUri = "copy-uri";
    public const string OpenWeb = "open-web";
    public const string Radio = "radio";
    public const string Video = "video";
    public const string Replace = "replace";
    public const string Locate = "locate";
    public const string RevealFolder = "reveal-folder";
    public const string Pin = "pin";
    public const string Unpin = "unpin";
    public const string Folder = "folder";

    /// <summary>Key → an <see cref="IconRef"/>. Keys the ThemedIcon starter set covers resolve to a layered vector name
    /// with a glyph fallback (<c>IconRef.Themed</c>); the rest are plain Segoe Fluent glyphs (implicit string → IconRef).
    /// <paramref name="isChecked"/> picks the filled variant for stateful icons (the saved heart).</summary>
    public static IconRef Resolve(string key, bool isChecked = false) => key switch
    {
        Play => IconRef.Themed("Play", Icons.Play),
        // PlayNext / AddToQueue: the two-tone LAYERED themed marks (list lines + accent play mark), authored to mirror
        // the app's CUSTOM wavee-icons rail glyphs. The rail glyph is kept as the fallback (rendered until the themed
        // registry is warm) — hence the explicit Font so the fallback resolves against the WaveeIcons font.
        PlayNext => new IconRef { ThemedName = "PlayNext", Glyph = WaveeIcons.PlayNext, Font = WaveeIcons.Font },
        Queue => new IconRef { ThemedName = "AddToQueue", Glyph = WaveeIcons.PlayAfter, Font = WaveeIcons.Font },   // AddToQueue == the rail's "Play after"
        // Heart: the layered two-tone Like toggle — a neutral outline "Heart" when unliked, the accent-filled
        // "HeartFill" when liked (Segoe glyph fallbacks until the themed registry is warm). The strip renders the
        // accent from the icon's own Accent layer; the checked-strip fg tint covers the glyph-fallback case.
        Like or Heart => isChecked ? IconRef.Themed("HeartFill", Icons.HeartFill) : IconRef.Themed("Heart", Icons.Heart),
        // Saving a container is not liking a track. The add/check pair keeps both registry rows visually distinct.
        Save => isChecked ? Icons.Check : IconRef.Themed("Add", Icons.Add),
        Add => IconRef.Themed("Add", Icons.Add),
        Album => Icons.Album,
        Artist => Icons.Contact,
        Link => IconRef.Themed("Link", Icons.Link),
        Remove => Icons.Remove,
        Delete => IconRef.Themed("Delete", Icons.Delete),
        Open => IconRef.Themed("Open", Icons.OpenInNewWindow),
        Rename => IconRef.Themed("Rename", Icons.Edit),
        People => Icons.Friends,
        Globe => Icons.Globe,
        Credits => Icons.Document,
        Share => Icons.Share,
        CopyUri => Icons.Copy,
        OpenWeb => Icons.Globe,
        Radio => Icons.RadioTower,
        // The local video-override verbs. Movie is the SAME mark the row indicator and the player-bar video button use,
        // so "this playable has a video" reads identically everywhere.
        Video => Icons.Movie,
        Replace => Icons.Refresh,
        Locate => Icons.Search,
        RevealFolder => Icons.FolderOpen,
        // Pin / Unpin (the sidebar pin pair). Segoe Fluent's UnPin glyph (E77A) is in the engine table.
        Pin => Icons.Pin,
        Unpin => Icons.UnPin,
        Folder => IconRef.Themed("Folder", Icons.Folder),
        _ => Icons.More,
    };
}
