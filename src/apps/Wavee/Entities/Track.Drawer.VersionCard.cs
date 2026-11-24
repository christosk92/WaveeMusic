// ── Entities/Track.Drawer.VersionCard.cs ───────────────────────────────────────────────────────────────────────────
// A version card in Track details is a thing you can act on (#240 version cards; owner: "these cards should be right clickable to edit and
// also the title for custom items should be hoverable and editable").
//
//   · Its menu: right-click, Shift+F10 or the menu key on the focused card, or its "⋯" (a context-invoker, so one handler serves all four).
//     It is the version's own row menu (Play, Play next, Play after, its kind's verbs: Rename…, Show in folder, Find on Spotify, Remove link
//     for a version of mine; Go to album, Share, Save for Spotify's), headed by "Play by default" checked on the version the song plays, and,
//     for a video of mine, Watch and Play as audio.
//   · Its title: a version of mine (a file, renamable since phase 2) is editable in place (Platform/InlineTitle.cs): a pencil and
//     the text cursor on hover, a click (or F2 on the card) edits it, Enter or a click elsewhere renames (with the rename's Undo toast).
//     Spotify's versions show their title as it is.
//
// Role: SHELL (UI thread)

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Theming.Contracts;

namespace Wavee;

public readonly partial struct Track
{
    /// <summary>One version card's verbs (<see cref="TrackDrawerBodyArgs.VersionCard"/>). Built per render (human rate): the menu is built when it
    /// opens.</summary>
    static TrackDrawerCardWiring CardWiring(Track version, Track parent, byte kind, IOverlayService overlay)
    {
        long mine = version.IsValid && LocalLibrary.TryLocalIdOf(version.Uri.Text, out long id) ? id : 0;
        string key = "ver:" + version.Uri.Text;
        // The app's one context-menu decision (Platform/Menus.Sheet.cs): the menu at the pointer or the card, or the structure's sheet in the bands
        // its policy names (a row's menu is a bottom sheet at Compact: so is the card's).
        Func<ContextMenuModel?> factory = () => VersionMenu(version, parent, kind, mine, overlay);
        var stock = ContextMenu.Handler(null, overlay, factory);
        Action<ContextRequestEventArgs> context = e =>
        {
            if (ContextSheets.SheetNow()) ContextSheets.Open(overlay, factory);
            else stock(e);
        };
        Action<KeyEventArgs> keys = e =>
        {
            if (e.KeyCode != Keys.F2 || mine == 0 || e.Handled) return;
            InlineTitle.Begin(key);
            e.Handled = true;
        };
        Func<TextEl, string, Element> title = mine == 0
            ? static (look, _) => look
            : (look, text) => InlineTitle.Of(key, text, look, renamed => LocalLinkPage.RenameTitle(mine, renamed));
        string name = version.IsValid && version.Knows(TrackFields.Title) ? version.Title : "";
        return new TrackDrawerCardWiring(context, keys, title, Strings.Detail.Versions.MoreFor(name));
    }

    /// <summary>The card's menu (see the file header). Null for a reserved card (no version yet).</summary>
    static ContextMenuModel? VersionMenu(Track version, Track parent, byte kind, long mine, IOverlayService overlay)
    {
        if (!version.IsValid) return null;
        var model = Menu([version], new MenuOptions(ShowGoToAlbum: mine == 0, PickerOverlay: overlay, VersionCard: true));
        var rows = new List<MenuFlyoutItem>(8);
        // "Play by default", checked on the version the song plays: the song of Spotify's version is itself, a file's is the song it is linked to.
        string song = mine == 0 ? (version.IsLocal ? "" : version.Uri.Text) : LocalLink.LinkOf(mine)?.Uri ?? "";
        if (song.Length > 0 && LocalLink.FilesFor(song).Count > 0)
        {
            long current = LocalLink.DefaultOf(song);
            bool plays = current == mine;
            string songTitle = mine == 0 ? version.Title : parent.IsValid && !parent.IsLocal ? parent.Title : LocalLink.LinkOf(mine)?.Title ?? version.Title;
            rows.Add(MenuFlyoutItem.Toggle(Loc.Get(Strings.LocalFiles.Link.PlayDefault), plays,
                () => { if (!plays) LocalLinkPage.Choose(song, songTitle, mine); }));
        }
        // A video of mine (its own file, or a Spotify song whose linked video version it plays): watch it, or hear it.
        uint videoBits = version.FlagBits & (uint)(TrackFlags.LocalVideoFile | TrackFlags.LinkedVideo);
        if (videoBits != 0)
        {
            var row = new EntityRef(EntityKind.Track, version.Slot);
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Versions.Watch), Icons.Movie, true, () => Playback.WatchLocalVideo(row)));
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Detail.Versions.PlayAsAudio), Icons.Headphones, true, () => PlayAsAudio(row)));
        }
        if (model is not { } m) return rows.Count > 0 ? new ContextMenuModel(rows) : null;
        if (rows.Count > 0 && m.Rows.Count > 0) rows.Add(MenuFlyoutItem.Separator);
        rows.AddRange(m.Rows);
        return new ContextMenuModel(m.Primary, rows, m.Header);
    }

    /// <summary>Play a video version as sound only: the row plays (or keeps playing) and the video mode turns off for it (the user's "audio only"
    /// holds while it plays).</summary>
    static void PlayAsAudio(EntityRef row)
    {
        if (!Playback.CurrentId.Peek().Equals(new Track(row.Slot).Id)) Playback.PlayNow(row, default, QueueCursor.None, Playback.PlayableKind.Audio);
        Playback.SwitchVideo(false);
    }
}
