// ── Entities/LocalCollection.Page.Folders.cs ───────────────────────────────────────────────────────────────────────
// the Folders browser's management (#240 folders): one folder menu and one empty-area menu, their verbs, dialogs and toasts
//
// Role: UI (core host part: the theme only says where a menu opens, `LocalFolderMenus`)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-files-ux.md S3 (a folder's "⋯": Show in Explorer, Rescan, Remove), S11 (a subfolder's
//       Remove is "skip it": it stays on disk); 240-local-files-page-concept.md §5 (Folders: play or shuffle any folder)
//
// A folder's menu, in the app's grammar (transport, then where it is, then the library, then the disk, then facts):
//   Play · Shuffle · Play next · Add to queue
//   Open · Show in File Explorer
//   Rescan this folder · Hide from Local Files (a folder inside a music folder) | Remove from music folders (a music folder)
//   New folder… · Rename… · Delete… (a folder inside a music folder)
//   Folder info
// The open folder's header "⋯" leaves out Play, Shuffle (its buttons stand beside it) and Open. The empty area and the breadcrumb: Add a music
// folder… · New folder… (inside the open folder) · Rescan · Show in File Explorer · View: Grid | List (where the theme lists folders).
//
// "Your files are yours": the library verbs never touch the disk (Hide is `local.skip`, Remove leaves the files; both toast an Undo). The disk
// verbs ask first in a house dialog, run off the UI thread (`LocalLibrary.Disk.cs`) and say what went wrong in the dialog in plain words, so
// the user can fix the name or close it; their toasts offer an Undo where one exists (a rename back, an empty new folder removed). Delete
// only ever moves to the Recycle Bin. A folder whose drive is away greys its disk verbs (the reachability rules: nothing waits on a dead share).

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using F = Wavee.Strings.LocalFiles.Folders;
using P = Wavee.Strings.LocalFiles.Page;

namespace Wavee;

public static partial class LocalCollectionPage
{
    /// <summary>The Folders browser's verbs for one page host: its menus (<see cref="Menus"/>) and what they run.</summary>
    sealed class FolderVerbs
    {
        readonly Func<IOverlayService?> _overlay;
        readonly Action<string> _open;
        readonly Action<string, string> _moved;
        readonly Action<string> _gone;
        readonly Signal<int> _view;
        readonly LocalCollectionVerbs _verbs;

        public LocalFolderMenus Menus { get; }

        /// <param name="overlay">The page's overlay host (dialogs, sheets).</param>
        /// <param name="open">The page's OpenFolder.</param>
        /// <param name="moved">A folder's key changed (old, new: a rename and its Undo): an open detail on it follows once the new key lands.</param>
        /// <param name="gone">A folder left the browser (hidden, recycled, its music folder removed): an open detail on it or under it steps out.</param>
        public FolderVerbs(Func<IOverlayService?> overlay, Action<string> open, Action<string, string> moved, Action<string> gone, Signal<int> view, LocalCollectionVerbs verbs)
        {
            _overlay = overlay;
            _open = open;
            _moved = moved;
            _gone = gone;
            _view = view;
            _verbs = verbs;
            Menus = new LocalFolderMenus { Folder = FolderMenu, Area = AreaMenu };
        }

        // ── the menus ───────────────────────────────────────────────────────────────────────────────────────────────

        ContextMenuModel? FolderMenu(string key, bool header)
        {
            var view = LocalCollection.View.Peek();
            if (!view.Folders.TryGetValue(key, out var n)) return null;
            bool root = n.ParentKey is null;
            string rootPath = RootPathOf(view, n);
            bool there = n.Path.Length > 0 && !LocalLibrary.IsUnreachable(n.Path);
            bool disk = there && LocalLibrary.DiskReady(rootPath);
            bool songs = n.Songs > 0;
            var rows = new List<MenuFlyoutItem>(18);
            if (!header)
            {
                rows.Add(new MenuFlyoutItem(Loc.Get(F.Play), ActionIcons.Resolve(ActionIcons.Play), songs, () => _verbs.PlayFolder(key)));
                rows.Add(new MenuFlyoutItem(Loc.Get(F.Shuffle), Icons.Shuffle, songs, () => _verbs.ShuffleFolder(key)));
            }
            rows.Add(new MenuFlyoutItem(Loc.Get(F.PlayNext), ActionIcons.Resolve(ActionIcons.PlayNext), songs, () => Queue(key, next: true)));
            rows.Add(new MenuFlyoutItem(Loc.Get(F.AddToQueue), ActionIcons.Resolve(ActionIcons.Queue), songs, () => Queue(key, next: false)));
            rows.Add(MenuFlyoutItem.Separator);
            if (!header) rows.Add(new MenuFlyoutItem(Loc.Get(F.Open), ActionIcons.Resolve(ActionIcons.Folder), true, () => _open(key)));
            string path = n.Path;
            rows.Add(new MenuFlyoutItem(Loc.Get(F.ShowInExplorer), Icons.FolderOpen, there, () => LocalLibrary.Reveal(path)));
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(new MenuFlyoutItem(Loc.Get(F.Rescan), Icons.Refresh, disk, () => LocalLibrary.Rescan(rootPath)));
            rows.Add(root
                ? new MenuFlyoutItem(Loc.Get(F.RemoveRoot), Icons.Remove, true, () => RemoveRoot(key, rootPath))
                : new MenuFlyoutItem(Loc.Get(F.Hide), Icons.Remove, path.Length > 0, () => Hide(key, path, n.Name)));
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(new MenuFlyoutItem(Loc.Get(F.NewFolder), ActionIcons.Resolve(ActionIcons.Add), disk, () => NewFolder(key)));
            if (!root)
            {
                rows.Add(new MenuFlyoutItem(Loc.Get(F.Rename), ActionIcons.Resolve(ActionIcons.Rename), disk, () => Rename(key)));
                rows.Add(new MenuFlyoutItem(Loc.Get(F.Delete), ActionIcons.Resolve(ActionIcons.Delete), disk, () => Delete(key)));
            }
            rows.Add(MenuFlyoutItem.Separator);
            rows.Add(new MenuFlyoutItem(Loc.Get(F.Info), Icons.Info, true, () => Info(key)));
            string? art = n.Cover ?? (n.Mosaic.Count > 0 ? n.Mosaic[0] : null);
            return new ContextMenuModel(rows, Actions.Menu.Header(art, n.Name, P.Songs(n.Songs)));
        }

        ContextMenuModel? AreaMenu(string openKey, bool views)
        {
            var view = LocalCollection.View.Peek();
            LocalFolderNode? n = openKey.Length > 0 && view.Folders.TryGetValue(openKey, out var found) ? found : null;
            var rows = new List<MenuFlyoutItem>(10)
            {
                new(Loc.Get(F.AddFolder), ActionIcons.Resolve(ActionIcons.Add), true, static () => SettingsVerbs.AddMusicFolder()),
            };
            if (n is not null)
            {
                string rootPath = RootPathOf(view, n);
                bool there = n.Path.Length > 0 && !LocalLibrary.IsUnreachable(n.Path);
                bool disk = there && LocalLibrary.DiskReady(rootPath);
                string key = n.Key, path = n.Path;
                rows.Add(new MenuFlyoutItem(Loc.Get(F.NewFolder), Icons.Folder, disk, () => NewFolder(key)));
                rows.Add(MenuFlyoutItem.Separator);
                rows.Add(new MenuFlyoutItem(Loc.Get(F.Rescan), Icons.Refresh, disk, () => LocalLibrary.Rescan(rootPath)));
                rows.Add(new MenuFlyoutItem(Loc.Get(F.ShowInExplorer), Icons.FolderOpen, there, () => LocalLibrary.Reveal(path)));
            }
            else
            {
                rows.Add(MenuFlyoutItem.Separator);
                rows.Add(new MenuFlyoutItem(Loc.Get(F.RescanAll), Icons.Refresh, LocalLibrary.Folders.Count > 0, static () => LocalLibrary.Rescan()));
            }
            if (views)
            {
                // The Albums and Artists view code: 0 compact list, 1 list, 2 compact grid, 3 grid. A pick keeps the compactness.
                int code = _view.Peek();
                var cell = _view;
                rows.Add(MenuFlyoutItem.Separator);
                rows.Add(MenuFlyoutItem.Header(Loc.Get(F.View)));
                rows.Add(MenuFlyoutItem.RadioItem(Loc.Get(F.ViewGrid), code >= 2, () => cell.Value = cell.Peek() is 0 or 2 ? 2 : 3, Icons.ViewGrid));
                rows.Add(MenuFlyoutItem.RadioItem(Loc.Get(F.ViewList), code < 2, () => cell.Value = cell.Peek() is 0 or 2 ? 0 : 1, Icons.ViewList));
            }
            return new ContextMenuModel(rows);
        }

        /// <summary>The music folder a node sits in (its own path for a music folder).</summary>
        static string RootPathOf(LocalCollectionView view, LocalFolderNode n)
        {
            var at = n;
            while (at.ParentKey is { } up && view.Folders.TryGetValue(up, out var parent)) at = parent;
            return at.Path;
        }

        // ── the queue ───────────────────────────────────────────────────────────────────────────────────────────────

        void Queue(string key, bool next)
        {
            if (Shell.QueueModel.IsViewer()) { Notify.Say(Loc.Get(F.QueueOnThisPc), InfoBarSeverity.Warning); return; }
            var rows = StagedRows(LocalCollection.FilesWhere(e => LocalCollection.InFolder(e, key)));
            if (rows.Count == 0) return;
            int added = global::Wavee.Queue.InsertUser(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(rows), Playback.Snap().Cursor, next ? 0 : int.MaxValue);
            Shell.QueueVerbs.Changed();
            if (added > 0) Notify.Say(F.AddedToQueue(added), InfoBarSeverity.Success);
        }

        // ── the library verbs (nothing leaves the disk) ─────────────────────────────────────────────────────────────

        void Hide(string key, string path, string name)
        {
            if (LocalLibrary.HideFolder(path) is not { } hidden) return;
            _gone(key);
            Notify.Say(F.Hidden(name), actionLabel: Loc.Get(F.Undo), onAction: () => LocalLibrary.UnhideFolder(hidden), dedupeKey: "local.folder.hide");
        }

        void RemoveRoot(string key, string rootPath)
        {
            _gone(key);
            SettingsVerbs.RemoveMusicFolder(rootPath);
        }

        // ── the disk verbs ──────────────────────────────────────────────────────────────────────────────────────────

        void Rename(string key)
        {
            if (Controls.IsNullOverlay(_overlay()) || !LocalCollection.View.Peek().Folders.TryGetValue(key, out var n)) return;
            string oldName = n.Name, oldPath = n.Path;
            NameDialog(_overlay()!, Loc.Get(F.RenameTitle), Loc.Get(F.RenameVerb), oldName, (name, finish) =>
            {
                string trimmed = name.Trim();
                if (trimmed == oldName) { finish(null); return; }
                LocalLibrary.RenameFolder(oldPath, trimmed, (op, newPath) =>
                {
                    if (op != FolderOp.Done) { finish(Why(op, op == FolderOp.Exists ? trimmed : oldName)); return; }
                    finish(null);
                    string newKey = Sibling(key, trimmed);
                    _moved(key, newKey);
                    Notify.Say(F.Renamed(trimmed), InfoBarSeverity.Success, actionLabel: Loc.Get(F.Undo), dedupeKey: "local.folder.rename", onAction: () =>
                        LocalLibrary.RenameFolder(newPath, oldName, (back, _) =>
                        {
                            if (back == FolderOp.Done) _moved(newKey, key);
                            else Notify.Say(Why(back, trimmed), InfoBarSeverity.Warning);
                        }));
                });
            });
        }

        void NewFolder(string key)
        {
            if (Controls.IsNullOverlay(_overlay()) || !LocalCollection.View.Peek().Folders.TryGetValue(key, out var n)) return;
            string parent = n.Path;
            NameDialog(_overlay()!, Loc.Get(F.NewFolderTitle), Loc.Get(F.Create), Loc.Get(F.NewFolderName), (name, finish) =>
            {
                string trimmed = name.Trim();
                LocalLibrary.CreateFolder(parent, trimmed, (op, made) =>
                {
                    if (op != FolderOp.Done) { finish(Why(op, trimmed)); return; }
                    finish(null);
                    Notify.Say(F.Created(trimmed), InfoBarSeverity.Success, actionLabel: Loc.Get(F.Undo), dedupeKey: "local.folder.create", onAction: () =>
                        LocalLibrary.RemoveEmptyFolder(made, back => { if (back is not (FolderOp.Done or FolderOp.NotFound)) Notify.Say(Why(back, trimmed), InfoBarSeverity.Warning); }));
                });
            });
        }

        void Delete(string key)
        {
            var overlay = _overlay();
            if (Controls.IsNullOverlay(overlay) || !LocalCollection.View.Peek().Folders.TryGetValue(key, out var n)) return;
            string name = n.Name, path = n.Path;
            var status = new Signal<(string Text, bool Bad)>(("", false));
            bool busy = false;
            OverlayHandle? handle = null;
            handle = ContentDialog.Show(overlay!, d =>
            {
                d.Title = F.DeleteTitle(name);
                d.PrimaryText = Loc.Get(F.DeleteVerb);
                d.CloseText = Loc.Get(Strings.Auth.Cancel);
                // The destructive verb is never the default: Enter cancels (Controls.Confirm's rule).
                d.DefaultButton = ContentDialog.DefaultBtn.Close;
                OverlayFit.Dialog(d, Controls.DialogWidthCompact);
                // The dialog's Content replaces its Message: the body and the status line are one column.
                d.Content = new BoxEl
                {
                    Key = "lc-folder-delete", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                    Children = [Ui.Body(F.DeleteBody(n.Songs, name)) with { Color = Tok.TextPrimary, MinWidth = 0f }, StatusLine(status)],
                };
                d.PrimaryButtonClick = args =>
                {
                    args.Cancel = true;
                    if (busy) return;
                    busy = true;
                    status.Value = (Loc.Get(F.Working), false);
                    LocalLibrary.RecycleFolder(path, op =>
                    {
                        busy = false;
                        if (op != FolderOp.Done) { status.Value = (Why(op, name), true); return; }
                        handle?.Close();
                        _gone(key);
                        Notify.Say(F.Deleted(name), InfoBarSeverity.Success, dedupeKey: "local.folder.delete");
                    });
                };
            });
        }

        /// <summary>A folder's facts from the index (no disk): where it is, its songs and length, the bytes, the formats, the folders inside, when
        /// its music folder was last walked.</summary>
        void Info(string key)
        {
            var overlay = _overlay();
            var view = LocalCollection.View.Peek();
            if (Controls.IsNullOverlay(overlay) || !view.Folders.TryGetValue(key, out var n)) return;
            long bytes = 0, ms = 0;
            int songs = 0, missing = 0, rootId = 0;
            var formats = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in LocalLibrary.Index.Snapshot())
            {
                if (e.Tags.Kind != LocalKind.Audio || !LocalCollection.InFolder(e, key)) continue;
                rootId = e.RootId;
                songs++;
                bytes += e.Size;
                ms += e.Tags.DurationMs;
                if (e.State == LocalState.Missing) { missing++; continue; }
                string f = LocalCollection.FormatName(e.Format, e.RelativePath);
                formats[f] = formats.GetValueOrDefault(f) + 1;
            }
            var fmt = new List<KeyValuePair<string, int>>(formats);
            fmt.Sort(static (a, b) => b.Value.CompareTo(a.Value));
            string formatsText = fmt.Count == 0 ? "–" : string.Join(" · ", fmt.ConvertAll(static kv => kv.Key + " " + kv.Value.ToString("N0", CultureInfo.CurrentCulture)));
            string songsText = P.Songs(songs) + " · " + Minutes(ms) + (missing > 0 ? " · " + F.InfoMissing(missing) : "");
            long walked = 0;
            int bar = key.IndexOf('|');
            if (bar > 0 && int.TryParse(key.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out int id)) rootId = id;
            if (rootId > 0 && LocalLibrary.Index.TryGetRoot(rootId, out var root)) walked = root.LastWalkTicks;
            string scanned = walked > 0 ? new DateTime(walked, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : Loc.Get(F.InfoNever);
            Element body = new BoxEl
            {
                Key = "lc-folder-info", Direction = 1, Gap = Spacing.M, MinWidth = 0f,
                Children =
                [
                    Fact(Loc.Get(F.InfoLocation), n.Path),
                    Fact(Loc.Get(F.InfoSongs), songsText),
                    Fact(Loc.Get(F.InfoSize), Bytes(bytes)),
                    Fact(Loc.Get(F.InfoFormats), formatsText),
                    Fact(Loc.Get(F.InfoFolders), n.Children.Count.ToString("N0", CultureInfo.CurrentCulture)),
                    Fact(Loc.Get(F.InfoScanned), scanned),
                ],
            };
            Controls.Sheet(overlay, n.Name, body);
        }

        static Element Fact(string label, string value) => new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                R.Type.DenseMeta(label) with { Color = Tok.TextSecondary },
                R.Type.TrackTitle(value) with { Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MinWidth = 0f },
            ],
        };

        static string Minutes(long ms)
        {
            long minutes = ms / 60_000;
            return minutes >= 60 ? P.Hours((int)(minutes / 60)) : P.Minutes((int)minutes);
        }

        static string Bytes(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB"
            : b >= 1L << 20 ? (b / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB"
            : (b / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " KB";

        /// <summary>The key of a folder's sibling named <paramref name="name"/> ("root|Artist\Album" → "root|Artist\name").</summary>
        static string Sibling(string key, string name)
        {
            int bar = key.IndexOf('|');
            int slash = key.LastIndexOf('\\');
            return slash > bar ? key[..(slash + 1)] + name : key[..(bar + 1)] + name;
        }

        /// <summary>A disk verb's failure in plain words.</summary>
        static string Why(FolderOp op, string name) => op switch
        {
            FolderOp.Exists => F.ErrExists(name),
            FolderOp.InUse => F.ErrInUse(name),
            FolderOp.Denied => F.ErrDenied(name),
            FolderOp.NotFound => F.ErrNotFound(name),
            FolderOp.Offline => Loc.Get(F.ErrOffline),
            FolderOp.NoRecycleBin => Loc.Get(F.ErrNoBin),
            FolderOp.Cancelled => Loc.Get(F.ErrCancelled),
            _ => Loc.Get(F.ErrFailed),
        };

        static string NameWhy(string problem) => problem switch
        {
            "empty" => Loc.Get(F.ErrEmpty),
            "chars" => Loc.Get(F.ErrChars),
            "reserved" => Loc.Get(F.ErrReserved),
            "dots" => Loc.Get(F.ErrDots),
            _ => Loc.Get(F.ErrLong),
        };

        /// <summary>A one-field name dialog (Rename, New folder): the house ContentDialog with a TextBox and a status line under it that says what is
        /// wrong (a name Windows refuses, a folder by that name, a folder in use) or that the verb is running. <paramref name="commit"/> runs the verb
        /// and calls its <c>finish</c> with null (done: the dialog closes) or the words that keep it open.</summary>
        static void NameDialog(IOverlayService overlay, string title, string verb, string initial, Action<string, Action<string?>> commit)
        {
            var text = new Signal<string>(initial);
            var status = new Signal<(string Text, bool Bad)>(("", false));
            bool busy = false;
            OverlayHandle? handle = null;
            void Submit()
            {
                if (busy) return;
                string name = text.Peek();
                string problem = LocalLibrary.FolderNameProblem(name);
                if (problem.Length > 0) { status.Value = (NameWhy(problem), true); return; }
                busy = true;
                status.Value = (Loc.Get(F.Working), false);
                commit(name, why =>
                {
                    busy = false;
                    if (why is null) handle?.Close();
                    else status.Value = (why, true);
                });
            }
            handle = ContentDialog.Show(overlay, d =>
            {
                d.Title = title;
                d.PrimaryText = verb;
                d.CloseText = Loc.Get(Strings.Auth.Cancel);
                d.DefaultButton = ContentDialog.DefaultBtn.Primary;
                OverlayFit.Dialog(d, Controls.DialogWidthCompact);
                d.Content = new BoxEl
                {
                    Key = "lc-folder-name", Direction = 1, Gap = Spacing.S, MinWidth = 0f,
                    Children =
                    [
                        TextBox.Create(text, v => { if (status.Peek().Text.Length > 0 && !busy) status.Value = ("", false); },
                            new TextBox.TextBoxOptions { Width = float.NaN, Height = FieldHeight(), OnCommit = _ => Submit() }) with { Key = "lc-folder-name:box" },
                        StatusLine(status),
                    ],
                };
                d.PrimaryButtonClick = args => { args.Cancel = true; Submit(); };
            });
        }

        /// <summary>The name field's height: the stock 32, the 40 touch target in the Compact window band (its clear button is a target too).</summary>
        static float FieldHeight() => global::Wavee.Theming.ThemeHost.Facts.Peek().Tiers.Window == WidthTier.Compact ? CompactField : Field;

        const float Field = 32f, CompactField = 40f;

        /// <summary>The dialog's status line: two lines reserved, so an error or "Working…" never moves the buttons.</summary>
        static Element StatusLine(Signal<(string Text, bool Bad)> status)
        {
            var line = R.Type.DenseMeta("");
            return new BoxEl
            {
                Key = "lc-folder-status", Direction = 1, MinWidth = 0f, MinHeight = 2f * line.LineHeight,
                Children =
                [
                    line with
                    {
                        Text = Prop.Of(() => status.Value.Text),
                        Color = Prop.Of(() => status.Value.Bad ? Tok.SystemFillCritical : Tok.TextSecondary),
                        Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }
    }

    /// <summary>Stage-checked rows for local files (the restate stages every indexed file as `wavee:local:file:~<LocalId>`; one not staged yet is left
    /// out), at most <see cref="MaxQueued"/>.</summary>
    static List<EntityRef> StagedRows(List<long> ids)
    {
        if (ids.Count > MaxQueued) ids.RemoveRange(MaxQueued, ids.Count - MaxQueued);
        var scope = Entities.Current;
        var rows = new List<EntityRef>(ids.Count);
        foreach (long id in ids)
            if (scope.Tracks.TryGetSlot(LocalLibrary.UriOf(id).AsSpan(), out int slot) && new Track(slot).IsValid) rows.Add(new EntityRef(EntityKind.Track, slot));
        return rows;
    }
}
