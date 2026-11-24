// ── Entities/LocalCollection.Page.cs ───────────────────────────────────────────────────────────────────────────────
// the Local Files route's page, "On this PC": the core host of the collection (#240 P2-page, boards 18-21)
//
// Role: UI (core host: hooks, the fold's trigger, the remembered pivot, the verbs; the active theme draws it)
// Spec: C:\WAVEE\fgpu\handoff\request-plans\240-local-files-page-concept.md; 240-local-files-ux.md S1, S4, S10;
//       240-local-playlist-files-implementation.md §3.10 (the Local Files list becomes the Songs pivot, unchanged); 240-p2-contract.md
//
// The `local` route (sidebar label "Local Files", unchanged) renders this page instead of the playlist page's local arm. The page:
//   · asks `LocalCollection` for a fold whenever `LocalLibrary.Epoch` or `LocalArt.Epoch` moves (the fold runs off the UI thread);
//   · remembers the last pivot (setting `local.page.pivot`), and keeps the album sort and the open folder for the session;
//   · hands the theme the Songs pivot as the existing track table over the Local Files list (`wavee:local:playlist:all`), embedded,
//     exactly the profile the playlist page's local arm used (no heart, no recommendations), settled locally like that arm did;
//   · publishes row 2 (the pivots as views; Play, Shuffle and "Add music" as actions) for a band that draws it (Zune).
// Play and Shuffle play the Local Files list; an album, an artist, a decade or a folder plays its own files, by the rows P2-data's
// restate stages for them (`wavee:local:file:~<LocalId>`, `LocalLibrary.UriOf`). The empty list's onboarding (S4), the first scan's
// line and its toast, and the "N new songs" pill (S5) are the Local Files list's own (`LocalFilesList`), hosted here unchanged.

using System.Runtime.InteropServices;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;
using P = Wavee.Strings.LocalFiles.Page;

namespace Wavee;

/// <summary>The "On this PC" page (see the file header).</summary>
public static class LocalCollectionPage
{
    /// <summary>The last pivot, by name (home, albums, artists, songs, folders, insights).</summary>
    static readonly SettingKey<string> s_pivotKey = new("local.page.pivot", "home");

    /// <summary>The <see cref="Shell.RouteKind.Local"/> page.</summary>
    public static Element PageFor(in Shell.Route route)
    {
        string routeKey = Shell.NameOf(route);
        return Embed.Comp(new PageProps(routeKey), static () => new Screen()) with { Key = "local-collection:" + routeKey };
    }

    sealed record PageProps(string RouteKey);

    /// <summary>The playlist page's Local Files profile, unchanged: no heart, no recommendations, the drawer, and P2-data's empty body (S4) and the "N new
    /// songs" pill (S5).</summary>
    static readonly Track.TableProfile s_songsProfile = Track.TableProfile.From(Detail.Config.Playlist with { Heart = HeartMode.None, Recommendations = false, LocalRows = true })
        with { Drawer = Track.DrawerSeam, EmptyBody = LocalFilesList.EmptyBody, ListOverlay = LocalFilesList.PillOverlay };

    /// <summary>The Local Files onboarding body while there is no music folder (its `EmptyBody` answers it then), else null. Tracked.</summary>
    static readonly Func<Element?> s_onboarding = static () => { _ = LocalLibrary.Epoch.Value; return LocalFilesList.Onboarding ? LocalFilesList.EmptyBody() : null; };

    static LocalPivot PivotOf(string name) => Enum.TryParse(name, ignoreCase: true, out LocalPivot p) ? p : LocalPivot.Home;

    [CoreHost]
    sealed class Screen : Component
    {
        readonly Signal<LocalPivot> _pivot = new(PivotOf(Platform.Settings.Get(s_pivotKey)));
        readonly Signal<LocalAlbumSort> _sort = new(LocalAlbumSort.Added);
        readonly Signal<string> _folder = new("");
        readonly Action<LocalPivot> _setPivot;
        readonly Action<LocalAlbumSort> _setSort;
        readonly Action<string> _openFolder;
        readonly Func<Element> _songs;
        readonly LocalCollectionVerbs _verbs;
        readonly Action _refold, _settle, _publishChrome, _withdrawChrome, _watchScan;
        bool _sawFirstScan;
        PageChromeSink _sink = PageChromeSink.None;
        PageChromeRow? _chromeRow;
        LocalCollectionPageProps? _props;
        string _routeKey = "";

        public Screen()
        {
            _setPivot = p =>
            {
                _pivot.Value = p;
                Platform.Settings.Set(s_pivotKey, p.ToString().ToLowerInvariant());
            };
            _setSort = s => _sort.Value = s;
            _openFolder = key => { _folder.Value = key; _setPivot(LocalPivot.Folders); };
            _songs = static () => Track.Table(new Track.TableArgs
            {
                Source = Track.TableSource.ForPlaylist(Playlist.LocalFiles), Profile = s_songsProfile, Embedded = true, ScrollKey = "local-collection:songs",
            });
            _refold = static () =>
            {
                _ = LocalLibrary.Epoch.Value;
                _ = LocalLibrary.Restated.Value;
                _ = LocalArt.Epoch.Value;
                LocalCollection.Request();
            };
            // The end of a first scan is toasted once (S4), as the Local Files list did.
            _watchScan = () =>
            {
                bool now = LocalFilesList.FirstScanNow();
                if (now) { _sawFirstScan = true; return; }
                if (!_sawFirstScan) return;
                _sawFirstScan = false;
                LocalFilesList.SayScanned();
            };
            _settle = SettleLocalFiles;
            _publishChrome = () => _sink.Publish(_chromeRow);
            _withdrawChrome = () => _sink.Publish(null);
            _verbs = new LocalCollectionVerbs
            {
                PlayAll = static () => PlayList(shuffle: false),
                Shuffle = static () => PlayList(shuffle: true),
                AddMusic = static () => InContextDoors.OpenSettings("storage.music.folders"),
                PlayAlbum = static key => PlayFiles(LocalCollection.AlbumFiles(key), shuffle: false),
                OpenAlbum = key => { foreach (var a in LocalCollection.View.Peek().Albums) if (a.Key == key) { _openFolder(a.FolderKey); return; } },
                ShuffleArtist = static key => PlayFiles(LocalCollection.FilesWhere(e => LocalCollection.IsArtist(e, key)), shuffle: true),
                ShuffleDecade = static decade => PlayFiles(LocalCollection.FilesWhere(e => LocalCollection.InDecade(e, decade)), shuffle: true),
                PlayFolder = static key => PlayFiles(LocalCollection.FilesWhere(e => LocalCollection.InFolder(e, key)), shuffle: false),
                ShuffleFolder = static key => PlayFiles(LocalCollection.FilesWhere(e => LocalCollection.InFolder(e, key)), shuffle: true),
                PlayFile = static id => PlayFiles([id], shuffle: false),
            };
        }

        public override Element Render()
        {
            var p = UseProps<PageProps>();
            _routeKey = p.RouteKey;
            UseEffect(_refold);
            UseEffect(_settle, DepKey.Empty);
            UseEffect(_watchScan);

            // Row 2 (a band draws it; Classic and WinUI draw their own head): the pivots as views, Play, Shuffle and Add music as actions.
            _sink = UseContext(PageChromeSink.Context);
            var pivot = _pivot.Value;
            _chromeRow = ChromeOf(pivot);
            UseEffect(_publishChrome, DepKey.From((int)pivot));
            UseEffect(() => _withdrawChrome, DepKey.Empty);

            _props ??= new LocalCollectionPageProps
            {
                View = LocalCollection.View,
                Pivot = _pivot,
                SetPivot = _setPivot,
                Sort = _sort,
                SetSort = _setSort,
                Folder = _folder,
                OpenFolder = _openFolder,
                Songs = _songs,
                Verbs = _verbs,
                Onboarding = s_onboarding,
                FirstScan = LocalFilesList.FirstScanNow,
                ScanLine = LocalFilesList.ScanSlot,
                Shelves = LocalHome.Shelves,
                ShowShelf = LocalHome.Show,
                MoveShelf = LocalHome.Move,
                Drop = DropAnywhere.LocalFiles,
                Title = Loc.Get(P.Title),
                RouteKey = _routeKey,
            };
            return ViewSlots.View(ViewKeys.LocalCollectionPage, _props);
        }

        PageChromeRow ChromeOf(LocalPivot current)
        {
            var views = new PageView[6];
            for (int i = 0; i < views.Length; i++)
            {
                var pv = (LocalPivot)i;
                views[i] = new PageView(pv.ToString().ToLowerInvariant(), PivotLabel(pv), pv == current, _pivotGo[i] ??= () => _setPivot(pv));
            }
            return new PageChromeRow(views,
            [
                new PageAction("play", Loc.Get(P.PlayAll), _verbs.PlayAll, PageActionKind.Play),
                new PageAction("shuffle", Loc.Get(P.Shuffle), _verbs.Shuffle, PageActionKind.Shuffle),
                new PageAction("add", Loc.Get(P.AddMusic), _verbs.AddMusic),
            ], Title: Loc.Get(P.Title));
        }

        readonly Action?[] _pivotGo = new Action?[6];
    }

    /// <summary>A pivot's word.</summary>
    public static string PivotLabel(LocalPivot p) => p switch
    {
        LocalPivot.Albums => Loc.Get(P.PivotAlbums),
        LocalPivot.Artists => Loc.Get(P.PivotArtists),
        LocalPivot.Songs => Loc.Get(P.PivotSongs),
        LocalPivot.Folders => Loc.Get(P.PivotFolders),
        LocalPivot.Insights => Loc.Get(P.PivotInsights),
        _ => Loc.Get(P.PivotHome),
    };

    /// <summary>The Local Files header and membership are settled locally (the playlist page's local arm did the same; P2-data's restate fills it).</summary>
    static void SettleLocalFiles()
    {
        Playlist.EnsureLocalFilesHeader();
        var local = Playlist.LocalFiles;
        var e = Entities.Current.Edges.PlaylistTracks;
        if (e.State(local.Slot) != EdgeState.Unknown) return;
        e.Replace(local.Slot, ReadOnlySpan<int>.Empty, ReadOnlySpan<PlaylistTrackEdge>.Empty, EdgeState.Complete, 0);
        Entities.Publish();
    }

    static void PlayList(bool shuffle)
    {
        var local = Playlist.LocalFiles;
        if (!local.IsValid) return;
        if (local.TrackSlots.Length == 0)
        {
            // The list has not been restated from the index yet: play the files themselves.
            PlayFiles(LocalCollection.FilesWhere(static _ => true), shuffle);
            return;
        }
        Playback.SetShuffle(shuffle);
        Playback.PlayContext(local.Id);
    }

    /// <summary>The most files one verb stages and queues at once (a decade or a big folder plays a random sample of this many).</summary>
    const int MaxQueued = 500;

    static void PlayFiles(List<long> ids, bool shuffle)
    {
        if (ids.Count == 0) return;
        if (shuffle) Random.Shared.Shuffle(CollectionsMarshal.AsSpan(ids));
        if (ids.Count > MaxQueued) ids.RemoveRange(MaxQueued, ids.Count - MaxQueued);
        var scope = Entities.Current;
        var rows = new List<EntityRef>(ids.Count);
        foreach (long id in ids)
        {
            // The restate stages every indexed file as `wavee:local:file:~<LocalId>`; a file it has not staged yet is left out.
            if (scope.Tracks.TryGetSlot(LocalLibrary.UriOf(id).AsSpan(), out int slot) && new Track(slot).IsValid) rows.Add(new EntityRef(EntityKind.Track, slot));
        }
        if (rows.Count == 0) return;
        Playback.SetShuffle(false);
        Playback.PlayRows(CollectionsMarshal.AsSpan(rows), 0, Playlist.LocalFiles.Id);
    }
}
