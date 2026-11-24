// ── Entities/LocalLink.Versions.Page.cs ─────────────────────────────────────────────────────────────────────────────
// the verbs of "Your files and Spotify" phase 2 as the UI offers them: Rename… (a dialog, Undo on its toast), and "Play by default" (a submenu of
// the song's versions), in every theme (#240)
//
// Role: UI (core: builds the dialog and the menu items; the dialog's body is the theme's `ViewKeys.LocalRename`)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-link-phase2.md (R1, D1, D2)
//
// RENAME (R1). From a file's row menu, its version row in a Spotify song's drawer, and the drawer's own menu: a house ContentDialog titled
// "Rename" with the theme's body (title, artist, album, each starting at what the song shows, the file's own value as its hint) and "Use the
// file's names". Saving keeps only the fields that differ from what the song shows without a rename; an all-unchanged save clears the rename.
// One toast "Renamed to “…”" with Undo (the rename before).
//
// PLAY BY DEFAULT (D1). One submenu, built here for every home it has: a Spotify song's row menu (when files are linked to it), a linked file's
// row menu (its song's versions), and a drawer version row's caret. Radio items: "Spotify's version", then each file by its (renamed) name and
// quality. A pick sets the song's default (LocalLink.SetDefault) and says so in one toast with Undo.

using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;
using W = Wavee.Strings.LocalFiles.Link;

namespace Wavee;

public static partial class LocalLinkPage
{
    // ── rename ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The inline rename of a version's title (a version card in Track details: InlineTitle): the title only, the artist and album
    /// renames kept; the same toast and Undo as the dialog. UI thread.</summary>
    public static void RenameTitle(long localId, string title)
    {
        if (!LocalLibrary.Index.TryGet(localId, out LocalEntry? e)) return;
        LinkTrack? shown = LocalLink.Shown.TryGetValue(localId, out LinkTrack? s) ? s : null;
        LocalRename? before = LocalLink.RenameOf(localId);
        var own = LocalNames.Face(e, shown, null);
        var now = LocalNames.Face(e, shown, before);
        Save(localId, before, own, title, now.Artists, now.Album);
    }

    /// <summary>THE rename (the dialog's Save, the inline title): only the fields that differ from the file's own are kept; toast with Undo.</summary>
    static void Save(long id, LocalRename? before, LocalFace own, string title, string artist, string album)
    {
        static string Keep(string value, string ownValue) => value.Trim() is var v && (v.Length == 0 || v == ownValue) ? "" : v;
        var rename = new LocalRename(Keep(title, own.Title), Keep(artist, own.Artists), Keep(album, own.Album));
        if (rename == (before ?? new LocalRename("", "", ""))) return;
        LocalLink.Rename(id, rename);
        string shownTitle = rename.Title.Length > 0 ? rename.Title : own.Title;
        Notify.Say(W.Renamed(shownTitle), actionLabel: Loc.Get(W.Undo), onAction: () => LocalLink.Rename(id, before), dedupeKey: "local.rename");
    }

    /// <summary>"Rename…" on a file in the music folders: the dialog over the page (see the file header).</summary>
    public static void OpenRename(long localId, IOverlayService? overlay)
    {
        if (overlay is null || Controls.IsNullOverlay(overlay) || !LocalLibrary.Index.TryGet(localId, out LocalEntry? e)) return;
        LinkTrack? shown = LocalLink.Shown.TryGetValue(localId, out LinkTrack? s) ? s : null;
        LocalRename? before = LocalLink.RenameOf(localId);
        var now = LocalNames.Face(e, shown, before);
        var own = LocalNames.Face(e, shown, null);   // what the song shows without a rename (its tags, its split, an accepted match)
        string title = now.Title, artist = now.Artists, album = now.Album;
        var body = new LocalRenameProps(Loc.Get(W.RenameBody),
        [
            new LocalRenameField(Loc.Get(W.FieldTitle), now.Title, own.Title, v => title = v),
            new LocalRenameField(Loc.Get(W.FieldArtist), now.Artists, own.Artists, v => artist = v),
            new LocalRenameField(Loc.Get(W.FieldAlbum), now.Album, own.Album, v => album = v),
        ], Loc.Get(W.RenameReset));
        ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(W.RenameTitle);
            d.PrimaryText = Loc.Get(W.RenameSave);
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.PrimaryClick = () => Save(localId, before, own, title, artist, album);
            d.Content = ViewSlots.View(ViewKeys.LocalRename, body);
            OverlayFit.Dialog(d, Controls.DialogWidthCompact);
        });

    }

    // ── play by default ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The "Play by default" submenu of a Spotify song with files linked to it (its versions), or null when it has none.</summary>
    public static MenuFlyoutItem? DefaultMenu(string spotifyUri, string songTitle)
    {
        var files = LocalLink.FilesFor(spotifyUri);
        if (files.Count == 0) return null;
        return MenuFlyoutItem.SubMenu(Loc.Get(W.PlayDefault), DefaultItems(spotifyUri, songTitle, files), Icons.Play);
    }

    /// <summary>The submenu's radio items, flat (a drawer row's caret lists them after its own).</summary>
    public static List<MenuFlyoutItem> DefaultItems(string spotifyUri, string songTitle, IReadOnlyList<long> files)
    {
        long current = LocalLink.PlayAsFile(spotifyUri);
        var items = new List<MenuFlyoutItem>(files.Count + 1)
        {
            MenuFlyoutItem.RadioItem(Loc.Get(W.PlaysSpotify), current == 0, () => Choose(spotifyUri, songTitle, 0)),
        };
        foreach (long file in files)
        {
            long id = file;
            items.Add(MenuFlyoutItem.RadioItem(FileName(id) + " · " + LocalLinkWords.FileLabel(id), current == id, () => Choose(spotifyUri, songTitle, id)));
        }
        return items;
    }

    /// <summary>What a file is called in Wavee now (its rename, else what its row shows).</summary>
    public static string FileName(long localId)
    {
        if (!LocalLibrary.Index.TryGet(localId, out LocalEntry? e)) return "";
        return LocalNames.Face(e, LocalLink.Shown.TryGetValue(localId, out LinkTrack? s) ? s : null, LocalLink.RenameOf(localId)).Title;
    }

    /// <summary>Which version of <paramref name="spotifyUri"/> plays by default (0: Spotify's), with its toast and Undo.</summary>
    internal static void Choose(string spotifyUri, string songTitle, long file)
    {
        long before = LocalLink.PlayAsFile(spotifyUri);
        bool hadChoice = LocalLink.HasChosenDefault(spotifyUri);
        if (before == file && hadChoice) return;
        LocalLink.SetDefault(spotifyUri, file);
        string said = file == 0 ? W.DefaultSpotify(songTitle) : W.DefaultFile(songTitle, FileName(file));
        Notify.Say(said, actionLabel: Loc.Get(W.Undo), onAction: () => LocalLink.SetDefault(spotifyUri, before, clear: !hadChoice), dedupeKey: "local.default");
    }
}
