// ── Entities/LocalCollection.Page.Panes.cs ─────────────────────────────────────────────────────────────────────────
// "On this PC"'s detail panes: an album's and an artist's, beside the Albums and Artists lists where the library splits (#240)
//
// Role: CORE hosts (the data, the keys, the verbs); the picture is the structure's own library album pane (`ViewKeys.LibraryAlbumPane`:
//       Classic's `ClassicLibraryAlbumPane`, which WinUI draws too), so a local album reads exactly as a library album does: its header (the
//       cover, the eyebrow, the title, the artist, the meta), the command row, the embedded track table and the "Also by" strip.
// Spec: the owner (2026-11-24): "the Albums and Artists list view should behave like Library's: select a row, its detail on the right".
//
// THE DATA. An album and an artist here are NAMES from the files' tags (`LocalCollectionView`), never Spotify entities, so the pane
// adapts them: the rows are the files the restate staged as tracks (`wavee:local:file:~<LocalId>`, `LocalRowsSource`), played in the Local
// Files list's context as `PlayFiles` plays them; the cover is the folded one; nothing can be saved (`Saveable` false) and an artist has no
// page (`Openable` false: no link, the header is no target). An album's "Open album" opens today's detail (its folder, the drill-in). The
// artist pane is the same anatomy over every song of the artist, its albums as the trailing strip ("Albums"); the library's artist reader
// is a Spotify-catalogue reader (release blocks, a spine, the catalogue gate) and has no local counterpart.
//
// SELECTION NEVER REMOUNTS. The theme mounts ONE pane per pivot and re-pushes its props (the key); the view keys its own child per
// selection (`BodyKey`), so a pick is the library pane's crossfade, never a remount of the table host.

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using ViewSlots = FluentGpu.Theming.Slots;
using P = Wavee.Strings.LocalFiles.Page;

namespace Wavee;

public static partial class LocalCollectionPage
{
    /// <summary>The pane's props: the subject's key; the page's verbs and its in-place pick (stable for the page's life) ride along, ignored by equality.</summary>
    sealed record PaneProps(string Key, LocalCollectionVerbs Verbs, Action<LocalPivot, string> Pick)
    {
        public bool Equals(PaneProps? o) => o is not null && string.Equals(Key, o.Key, StringComparison.Ordinal);
        public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>The album pane for <paramref name="key"/> (a <see cref="LocalAlbumCard.Key"/>).</summary>
    static Element AlbumPane(string key, LocalCollectionVerbs verbs, Action<LocalPivot, string> pick)
        => Embed.Comp(new PaneProps(key, verbs, pick), static () => new LocalAlbumPane());

    /// <summary>The artist pane for <paramref name="key"/> (a <see cref="LocalArtistCard.Key"/>).</summary>
    static Element ArtistPane(string key, LocalCollectionVerbs verbs, Action<LocalPivot, string> pick)
        => Embed.Comp(new PaneProps(key, verbs, pick), static () => new LocalArtistPane());

    /// <summary>The embedded table's profile: no heart (a file is not a library item), no recommendations, local rows, and the trailing band kept (the
    /// "Also by" strip rides it, so the pane scrolls once). <paramref name="album"/>: an album's rows (no Album lane); else an artist's (the Album lane).</summary>
    static Track.TableProfile PaneProfile(bool album) => Track.TableProfile.From(
        (album ? Detail.Config.For(DetailKind.Album, AlbumKind.Album) with { ShowPlays = false } : Detail.Config.Playlist)
        with { Heart = HeartMode.None, Recommendations = false, LocalRows = true, HasTrailing = true });

    static readonly Track.TableProfile s_albumRows = PaneProfile(album: true), s_artistRows = PaneProfile(album: false);

    /// <summary>What both panes keep: the rows' source for the subject and view on screen, the stable thunks the view's region and table are handed,
    /// and the cached row metrics.</summary>
    abstract class PaneBase : Component
    {
        protected PaneProps? Props;
        protected LocalRowsSource? Source;
        string _sourceKey = "";
        int _sourceVersion = -1;
        protected readonly Loadable<int> Ready = Loadable<int>.Ready(0);
        protected readonly Func<Func<Element>?, Element> TableFn;
        protected readonly Action Retry = static () => { };

        protected PaneBase() => TableFn = trailing => Source is { } src
            ? Track.Table(new Track.TableArgs
            {
                Source = src, Profile = Rows, ShowToolbar = false, Embedded = true, ScrollKey = "local-collection:pane:" + src.Key, Trailing = trailing,
            })
            : new BoxEl();

        protected abstract Track.TableProfile Rows { get; }

        /// <summary>The subject's files, folded once per subject and view (a selection or a new fold).</summary>
        protected LocalRowsSource SourceFor(string key, int version, Func<List<long>> files)
        {
            if (Source is null || _sourceVersion != version || !string.Equals(_sourceKey, key, StringComparison.Ordinal))
            {
                Source = new LocalRowsSource(key, files());
                _sourceKey = key;
                _sourceVersion = version;
            }
            return Source;
        }

        /// <summary>The library pane's props around the subject, the rows' metrics as the library's own album pane takes them (the row style's).</summary>
        protected static LibraryAlbumPaneProps PropsOf(string bodyKey, int rows, Func<Func<Element>?, Element> table, Loadable<int> ready, Action retry,
            Func<LibraryAlsoByModel?> alsoBy, Action play, Action shuffle, Action open, Func<ContextMenuModel?> more)
        {
            bool classicRows = ShellPolicy.Tracked.Lists.ClassicRows;
            var sizes = Track.RowMetrics.Rows();
            return new LibraryAlbumPaneProps
            {
                State = AlbumPaneState.Ready, BodyKey = bodyKey, Region = ready, ShimRows = Math.Max(1, rows),
                RowHeight = classicRows ? sizes.ClassicRow : sizes.ModernRow, Columns = Track.EmbeddedColumns(classicRows, showTrackArtist: false),
                Table = table, AlsoBy = alsoBy, Play = play, Shuffle = shuffle, Open = open, More = more, Retry = retry,
                Notice = new BoxEl(), Saveable = false,
            };
        }

        protected static LibraryAlbumPaneProps Vacant(Func<Func<Element>?, Element> table, Loadable<int> ready, Action retry) => new()
        {
            Vacant = true, State = AlbumPaneState.Header, BodyKey = "", Region = ready, ShimRows = 1, RowHeight = Track.RowMetrics.Rows().ModernRow,
            Table = table, AlsoBy = static () => null, Play = retry, Shuffle = retry, Open = retry, More = static () => null, Retry = retry,
        };

        /// <summary>The ⋯ menu: the pane's own verbs (Play, Shuffle and, where there is one, Open).</summary>
        protected static ContextMenuModel Menu(Action play, Action shuffle, Action? open, string? openLabel)
        {
            var rows = new List<MenuFlyoutItem>(3)
            {
                new(Loc.Get(P.PlayAll), Icons.Play, true, play),
                new(Loc.Get(P.Shuffle), Icons.Shuffle, true, shuffle),
            };
            if (open is not null && openLabel is not null) rows.Add(new MenuFlyoutItem(openLabel, Icons.OpenInNewWindow, true, open));
            return new ContextMenuModel(rows);
        }

        /// <summary>One strip tile: the shared surface's card for a local album; its click picks the album in place (Albums), its play disc plays it.</summary>
        protected static LibraryAlsoByTile Tile(LocalAlbumCard a, int slot, Action<string> pick, LocalCollectionVerbs verbs)
        {
            string key = a.Key;
            var data = new Controls.CardData("wavee:local:album:" + key, a.Title, null, a.Cover, OnClick: () => pick(key), OnPlay: () => verbs.PlayAlbum(key), ShowMenu: false)
            {
                Caption = a.Year > 0 ? a.Year.ToString(CultureInfo.InvariantCulture) : null,
            };
            return new LibraryAlsoByTile(slot, data);
        }

        /// <summary>The artist key an album is filed under (<see cref="LocalCollection.AlbumKey"/>: "ARTIST / ALBUM").</summary>
        protected static string ArtistKeyOf(string albumKey)
        {
            int at = albumKey.IndexOf(" / ", StringComparison.Ordinal);
            return at < 0 ? "" : albumKey[..at];
        }

        /// <summary>The albums filed under <paramref name="artistKey"/>, in the view's newest-first order, each with its slot in that order (the strip's tile key).</summary>
        protected static List<(LocalAlbumCard Album, int Slot)> AlbumsOf(LocalCollectionView v, string artistKey, string? except)
        {
            var list = new List<(LocalAlbumCard, int)>();
            if (artistKey.Length == 0) return list;
            for (int i = 0; i < v.Albums.Count; i++)
            {
                var a = v.Albums[i];
                if (a.Key.Length > artistKey.Length + 3 && a.Key.StartsWith(artistKey, StringComparison.Ordinal) && string.CompareOrdinal(a.Key, artistKey.Length, " / ", 0, 3) == 0
                    && !string.Equals(a.Key, except, StringComparison.Ordinal))
                    list.Add((a, i));
            }
            return list;
        }

        /// <summary>The strip's ceiling (the library pane's): its allocation and decodes stay bounded at a selection edge.</summary>
        protected const int StripCap = 12;
    }

    /// <summary>An album's pane (see the file header).</summary>
    [CoreHost]
    sealed class LocalAlbumPane : PaneBase
    {
        LocalAlbumCard? _album;
        readonly Action _play, _shuffle, _open;
        readonly Func<ContextMenuModel?> _more;
        readonly Func<LibraryAlsoByModel?> _alsoBy;

        public LocalAlbumPane()
        {
            _play = () => { if (_album is { } a) Props?.Verbs.PlayAlbum(a.Key); };
            _shuffle = () => { if (_album is { } a) PlayFiles(LocalCollection.AlbumFiles(a.Key), shuffle: true); };
            _open = () => { if (_album is { } a) Props?.Verbs.OpenAlbum(a.Key); };
            _more = () => Menu(_play, _shuffle, _open, Loc.Get(Strings.Library.OpenAlbum));
            _alsoBy = AlsoBy;
        }

        protected override Track.TableProfile Rows => s_albumRows;

        public override Element Render()
        {
            var p = UseProps<PaneProps>();
            Props = p;
            var view = LocalCollection.View.Value;
            LocalAlbumCard? album = null;
            foreach (var a in view.Albums) if (string.Equals(a.Key, p.Key, StringComparison.Ordinal)) { album = a; break; }
            _album = album;
            if (album is null) return ViewSlots.View(ViewKeys.LibraryAlbumPane, Vacant(TableFn, Ready, Retry));
            string key = album.Key;
            // Every file of it, the ones Wavee cannot play listed unavailable (as the Songs list keeps them): the rows match the header's count.
            var src = SourceFor(key, view.Version, () => LocalCollection.AlbumFiles(key, unplayable: true));
            string eyebrow = Detail.Text.KindLabel(AlbumKind.Album) + (album.Year > 0 ? " · " + album.Year.ToString(CultureInfo.InvariantCulture) : "");
            string meta = Detail.Text.AlbumMeta(album.Tracks, album.DurationMs, durationsKnown: album.DurationMs > 0) ?? P.Songs(album.Tracks);
            if (album.Quality.Length > 0) meta += " · " + album.Quality;
            string artistKey = ArtistKeyOf(key);
            var pick = p.Pick;
            IReadOnlyList<Controls.Face>? faces = album.Artist.Length == 0 ? null
                : [new Controls.Face(album.Artist, null, artistKey.Length > 0 ? () => pick(LocalPivot.Artists, artistKey) : null)];
            return ViewSlots.View(ViewKeys.LibraryAlbumPane, PropsOf("lc-pane:album:" + key, src.ExpectedRows, TableFn, Ready, Retry, _alsoBy, _play, _shuffle, _open, _more) with
            {
                CoverUrl = album.Cover, Eyebrow = eyebrow, Title = album.Title, Meta = meta, Artists = faces, Uri = "wavee:local:album:" + key,
            });
        }

        /// <summary>The other albums filed under this album's artist, newest first, capped; null when there are none.</summary>
        LibraryAlsoByModel? AlsoBy()
        {
            if (_album is not { } album || Props is not { } p) return null;
            var view = LocalCollection.View.Value;
            var others = AlbumsOf(view, ArtistKeyOf(album.Key), album.Key);
            if (others.Count == 0) return null;
            var tiles = new List<LibraryAlsoByTile>(Math.Min(others.Count, StripCap));
            var pick = p.Pick;
            Action<string> select = k => pick(LocalPivot.Albums, k);
            for (int i = 0; i < others.Count && tiles.Count < StripCap; i++) tiles.Add(Tile(others[i].Album, others[i].Slot, select, p.Verbs));
            return new LibraryAlsoByModel(album.Artist, tiles);
        }
    }

    /// <summary>An artist's pane: the same anatomy over every song of the artist, the artist's albums as the strip (see the file header).</summary>
    [CoreHost]
    sealed class LocalArtistPane : PaneBase
    {
        LocalArtistCard? _artist;
        readonly Action _play, _shuffle;
        readonly Func<ContextMenuModel?> _more;
        readonly Func<LibraryAlsoByModel?> _albums;

        public LocalArtistPane()
        {
            _play = () => { if (_artist is { } a) PlayFiles(LocalCollection.FilesWhere(e => LocalCollection.IsArtist(e, a.Key)), shuffle: false); };
            _shuffle = () => { if (_artist is { } a) Props?.Verbs.ShuffleArtist(a.Key); };
            _more = () => Menu(_play, _shuffle, null, null);
            _albums = Albums;
        }

        protected override Track.TableProfile Rows => s_artistRows;

        public override Element Render()
        {
            var p = UseProps<PaneProps>();
            Props = p;
            var view = LocalCollection.View.Value;
            LocalArtistCard? artist = null;
            foreach (var a in view.Artists) if (string.Equals(a.Key, p.Key, StringComparison.Ordinal)) { artist = a; break; }
            _artist = artist;
            if (artist is null) return ViewSlots.View(ViewKeys.LibraryAlbumPane, Vacant(TableFn, Ready, Retry));
            string key = artist.Key;
            var src = SourceFor(key, view.Version, () => LocalCollection.FilesWhere(e => LocalCollection.IsArtist(e, key), unplayable: true));
            string meta = artist.Albums > 0 ? P.Albums(artist.Albums) + " · " + P.Songs(artist.Tracks) : P.Songs(artist.Tracks);
            return ViewSlots.View(ViewKeys.LibraryAlbumPane, PropsOf("lc-pane:artist:" + key, src.ExpectedRows, TableFn, Ready, Retry, _albums, _play, _shuffle, _play, _more) with
            {
                CoverUrl = artist.Cover, Eyebrow = Loc.Get(Strings.Detail.Artist), Title = artist.Name, Meta = meta, Uri = "wavee:local:artist:" + key, Openable = false,
            });
        }

        /// <summary>The artist's albums, newest first, capped, headed "Albums"; a tile picks the album on the Albums pivot.</summary>
        LibraryAlsoByModel? Albums()
        {
            if (_artist is not { } artist || Props is not { } p) return null;
            var view = LocalCollection.View.Value;
            var albums = AlbumsOf(view, artist.Key, null);
            if (albums.Count == 0) return null;
            var tiles = new List<LibraryAlsoByTile>(Math.Min(albums.Count, StripCap));
            var pick = p.Pick;
            Action<string> select = k => pick(LocalPivot.Albums, k);
            for (int i = 0; i < albums.Count && tiles.Count < StripCap; i++) tiles.Add(Tile(albums[i].Album, albums[i].Slot, select, p.Verbs));
            return new LibraryAlsoByModel(artist.Name, tiles) { Heading = Loc.Get(P.PivotAlbums) };
        }
    }

    /// <summary>A pane's rows: the files the restate staged as tracks (<see cref="LocalLibrary.UriOf"/>), in the order given, played in the Local Files
    /// list's context. The slots re-resolve when the restate or the scope moves (a file staged later joins its rows).</summary>
    sealed class LocalRowsSource(string key, List<long> ids) : Track.TableSource
    {
        readonly List<long> _ids = ids;
        int[] _slots = [];
        int _restated = int.MinValue;
        uint _epoch = uint.MaxValue, _version;

        public string Key { get; } = key;

        void Resolve()
        {
            int restated = LocalLibrary.Restated.Peek();
            uint epoch = Entities.ScopeEpoch.Peek();
            if (restated == _restated && epoch == _epoch) return;
            _restated = restated;
            _epoch = epoch;
            var tracks = Entities.Current.Tracks;
            var slots = new List<int>(_ids.Count);
            foreach (long id in _ids)
                if (tracks.TryGetSlot(LocalLibrary.UriOf(id).AsSpan(), out int slot) && new Track(slot).IsValid) slots.Add(slot);
            _slots = slots.ToArray();
            _version++;
        }

        public override EntityUri Context => Playlist.LocalFiles.Uri;
        public override int Count { get { Resolve(); return _slots.Length; } }
        public override int Total => Count;
        public override int ExpectedRows => Math.Max(_ids.Count, 1);
        public override EdgeState State => EdgeState.Complete;
        public override uint Version { get { Resolve(); return _version; } }
        public override Track At(int index) { Resolve(); return (uint)index < (uint)_slots.Length ? new Track(_slots[index]) : default; }
        public override void Subscribe()
        {
            _ = Entities.ScopeEpoch.Value;
            _ = LocalLibrary.Restated.Value;
            _ = Entities.Current.Tracks.Changed.Value;
        }
        public override bool Equals(object? obj) => obj is LocalRowsSource o && ReferenceEquals(o._ids, _ids);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_ids);
    }
}
