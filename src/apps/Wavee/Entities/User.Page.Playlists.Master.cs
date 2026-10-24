// ── Entities/User.Page.Playlists.Master.cs — the playlists library as master-detail (themes P5 Z7; Z-Library-Playlists*) ───────────────────────
//
// A structure that draws the playlists page as the board does asks for it with `LibraryTierPolicy.PlaylistsMasterDetail`: the rootlist GROUPED (made by you, made for you,
// followed; a folder and its children stay together) beside the picked playlist's pane (its cover, who made it, the verbs, then its songs as the shared embedded table). The list is
// the same data the flat page reads (`Sidebar.Model.Rootlist`, the cover, the subtitle words, the persisted folder expansion), so the two pictures never disagree. Core-drawn in the
// active style's roles, like the flat page. A structure that does not ask (Classic) keeps the flat list exactly as it was.
//
// THE PANE is a small core host (`PlaylistPane`): it asks for the playlist's identity, opens its list through the one open rule (`ListOpen.Open`, the surface the page uses), asks
// for the rows it shows, and hands the embedded table a plain playlist source. It owns no editing: "open playlist" goes to the page, where the whole model lives.

using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public readonly partial struct User
{
    /// <summary>The master-detail's numbers: the list column per content tier, the gap, the pane's cover, the group label's slot.</summary>
    public static class LibraryPlaylistsMasterRules
    {
        public const string ListColumnKey = "library-playlists-master-list", PaneKey = "library-playlists-master-pane", GroupPrefix = "library-playlists-group:";

        /// <summary>The list column's width at Small / Standard / Huge (the boards' 240 / 340 / 380) and the gap to the pane.</summary>
        public const float ListSmall = 240f, ListStandard = 340f, ListHuge = 380f, GapSmall = 24f, GapStandard = 40f;

        /// <summary>The pane's cover edge, its gap to the words, the verb row's top, the title slot.</summary>
        public const float HeadH = 28f, BodyGap = 16f, Cover = 150f, CoverGap = 20f, VerbsTop = 12f, HeaderBottom = 12f;

        /// <summary>The selected row's plate corner is the cover role's; a group label's slot is a row high so the list keeps one pitch.</summary>
        public const int Groups = 3;
    }

    /// <summary>Which group a rootlist entry stands under: made by you (yours, and the folders), made for you (Spotify's), followed (the rest).</summary>
    static int PlaylistGroupOf(in SidebarLibraryEntry e) => e.Kind == SidebarEntryKind.Folder ? 0
        : e.Flavor switch { SidebarPlaylistFlavor.ByYou => 0, SidebarPlaylistFlavor.BySpotify => 1, _ => 2 };

    sealed partial class LibraryPlaylistsPage
    {
        readonly Signal<string> _picked = new("");
        readonly Signal<string> _find = new("");
        /// <summary>Huge's group labels are the 22 light heading (the board), narrower tiers' the small tertiary word.</summary>
        bool _bigGroups;
        /// <summary>The page's sort word (a <see cref="SidebarLibrarySort"/> code: recents, recently added, alphabetical, custom order), persisted like every library sort.</summary>
        readonly Signal<int> _sort = new(Platform.Settings.Get(Platform.Keys.LibrarySort("playlists")));
        readonly List<SidebarLibraryEntry> _grouped = new(64);
        readonly List<int> _groupHeads = new(4);

        /// <summary>The visible rows grouped by provenance: every group's rows in the rootlist's own order, a folder's children following it (they take its group). Group
        /// heads are returned as the index in <see cref="_grouped"/> they stand before (-1 entries are never stored).</summary>
        void Group()
        {
            _grouped.Clear();
            _groupHeads.Clear();
            var sort = (SidebarLibrarySort)Math.Clamp(_sort.Value, 0, (int)SidebarLibrarySort.CustomOrder);
            for (int g = 0; g < LibraryPlaylistsMasterRules.Groups; g++)
            {
                int start = _grouped.Count;
                // A block is a top-level entry and its folder's children; the sort word orders the blocks by their head.
                var blocks = new List<List<SidebarLibraryEntry>>();
                int current = -1;
                for (int i = 0; i < _rows.Count; i++)
                {
                    var e = _rows[i];
                    if (e.Depth == 0) current = PlaylistGroupOf(in e);
                    if (current != g) continue;
                    if (e.Depth == 0 || blocks.Count == 0) blocks.Add(new List<SidebarLibraryEntry>(2));
                    blocks[^1].Add(e);
                }
                blocks.Sort((a, b) => CompareBlocks(sort, a[0], b[0]));
                foreach (var block in blocks) _grouped.AddRange(block);
                _groupHeads.Add(_grouped.Count > start ? start : -1);
            }
        }

        static int CompareBlocks(SidebarLibrarySort sort, in SidebarLibraryEntry a, in SidebarLibraryEntry b)
        {
            int c = sort switch
            {
                SidebarLibrarySort.Alphabetical => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
                SidebarLibrarySort.RecentlyAdded => b.SortStamp.CompareTo(a.SortStamp),
                SidebarLibrarySort.CustomOrder => a.SourceOrder.CompareTo(b.SourceOrder),
                _ => b.LastVisitedTicksUtc.CompareTo(a.LastVisitedTicksUtc),
            };
            return c != 0 ? c : a.SourceOrder.CompareTo(b.SourceOrder);
        }

        static readonly SidebarLibrarySort[] s_sortWords =
            [SidebarLibrarySort.Recents, SidebarLibrarySort.RecentlyAdded, SidebarLibrarySort.Alphabetical, SidebarLibrarySort.CustomOrder];

        /// <summary>The board's head: "sort" and its four words (the lit one primary), the two create words, then the find field and the count at the end.</summary>
        Element MasterHead(NavModel model, string count)
        {
            int current = _sort.Value;
            var kids = new List<Element>(10)
            {
                R.Type.DenseMeta(Loc.Get(Strings.Library.SortBy)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f },
            };
            foreach (var word in s_sortWords)
            {
                var w = word;
                bool on = (int)w == current;
                kids.Add(new BoxEl
                {
                    Key = "library-playlists-sort:" + (int)w, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Height = LibraryPlaylistsMasterRules.HeadH,
                    Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false,
                    OnClick = () => { _sort.Value = (int)w; Platform.Settings.Set(Platform.Keys.LibrarySort("playlists"), (int)w); },
                    Children =
                    [
                        R.Type.DenseMeta(Loc.Get(SidebarLibraryHeadRules.SortKey(w))) with
                        {
                            Color = on ? Tok.TextPrimary : Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                        },
                    ],
                });
            }
            kids.Add(Word(LibraryPlaylistsRules.NewPlaylistKey, Loc.Get(Strings.Sidebar.CreatePlaylistTooltip), () => model.CreatePlaylist(null, true)));
            kids.Add(Word(LibraryPlaylistsRules.NewFolderKey, Loc.Get(Strings.Sidebar.CreateFolder), () => model.CreateFolder(null)));
            kids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
            kids.Add(new BoxEl
            {
                Key = "library-playlists-find", Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Shrink = 0f, Height = LibraryPlaylistsMasterRules.HeadH,
                Children =
                [
                    Icon(Icons.Search, 14f, Tok.TextTertiary),
                    TextBox.Create(_find, options: new TextBox.TextBoxOptions
                    {
                        Placeholder = Strings.Theme.Zune.Collection.Library.FindIn(Loc.Get(Strings.Sidebar.Playlists).ToLower(System.Globalization.CultureInfo.CurrentCulture)),
                        Width = 160f, Height = LibraryPlaylistsMasterRules.HeadH,
                    }),
                ],
            });
            kids.Add(R.Type.DenseMeta(count) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f });
            return new BoxEl
            {
                Direction = 0, Height = LibraryPlaylistsMasterRules.HeadH, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 24f, MinWidth = 0f, Children = kids.ToArray(),
            };
        }

        string PickedKey()
        {
            string key = _picked.Value;
            for (int i = 0; i < _grouped.Count; i++) if (!_grouped[i].IsFolder && _grouped[i].Id == key) return key;
            for (int i = 0; i < _grouped.Count; i++) if (!_grouped[i].IsFolder) return _grouped[i].Id;
            return "";
        }

        /// <summary>The master-detail body under the head: the grouped list column, the pane.</summary>
        Element Master(NavModel model, float listW, float gap)
        {
            _bigGroups = listW >= LibraryPlaylistsMasterRules.ListHuge;
            Group();
            string picked = PickedKey();
            // The list's items: a group label, then its rows (every item one row high, so the virtual list keeps one pitch).
            var items = new List<(int Group, int Index)>(_grouped.Count + LibraryPlaylistsMasterRules.Groups);
            for (int g = 0; g < _groupHeads.Count; g++)
            {
                if (_groupHeads[g] < 0) continue;
                items.Add((g, -1));
                int end = g + 1 < _groupHeads.Count ? NextHead(g + 1) : _grouped.Count;
                for (int i = _groupHeads[g]; i < end; i++) items.Add((g, i));
            }
            var snapshot = items.ToArray();
            var grouped = _grouped.ToArray();
            Element list = new VirtualListEl
            {
                ItemCount = snapshot.Length,
                ItemLayout = new StackVirtualLayout(LibraryPlaylistsRules.RowH),
                RenderItem = i => (uint)i < (uint)snapshot.Length ? Item(model, snapshot[i], grouped, picked) : new BoxEl(),
                KeyOf = i => (uint)i < (uint)snapshot.Length
                    ? (snapshot[i].Index < 0 ? LibraryPlaylistsMasterRules.GroupPrefix + snapshot[i].Group : grouped[snapshot[i].Index].Id)
                    : i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Grow = 1f, Shrink = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
            } with { Key = LibraryPlaylistsRules.ListKey };

            Element pane = picked.Length == 0
                ? new BoxEl { Key = LibraryPlaylistsMasterRules.PaneKey, Grow = 1f, Basis = 0f }
                : new BoxEl
                {
                    Key = LibraryPlaylistsMasterRules.PaneKey, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                    Children = [Embed.Comp(new PlaylistPaneProps(EntryOf(picked)), static () => new PlaylistPane()) with { Key = "playlist-pane:" + picked }],
                };
            return new BoxEl
            {
                Direction = 0, Grow = 1f, MinHeight = 0f, MinWidth = 0f, Gap = gap, AlignItems = FlexAlign.Stretch, Margin = new Edges4(0f, LibraryPlaylistsMasterRules.BodyGap, 0f, 0f),
                Children =
                [
                    new BoxEl
                    {
                        Key = LibraryPlaylistsMasterRules.ListColumnKey, Direction = 1, Width = listW, MinWidth = listW, Shrink = 0f, MinHeight = 0f, ClipToBounds = true,
                        Children = [list],
                    },
                    pane,
                ],
            };
        }

        int NextHead(int from)
        {
            for (int g = from; g < _groupHeads.Count; g++) if (_groupHeads[g] >= 0) return _groupHeads[g];
            return _grouped.Count;
        }

        SidebarLibraryEntry EntryOf(string id)
        {
            for (int i = 0; i < _grouped.Count; i++) if (_grouped[i].Id == id) return _grouped[i];
            return default;
        }

        Element Item(NavModel model, (int Group, int Index) item, SidebarLibraryEntry[] grouped, string picked)
        {
            if (item.Index < 0)
                return new BoxEl
                {
                    Direction = 0, Height = LibraryPlaylistsRules.RowH, AlignItems = FlexAlign.End, Padding = new Edges4(0f, 0f, 0f, 8f), HitTestVisible = false,
                    Children =
                    [
                        _bigGroups
                            ? R.Type.QuickplayHead(GroupLabel(item.Group)) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap }
                            : R.Type.MicroMeta(GroupLabel(item.Group)) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap },
                    ],
                };
            var e = grouped[item.Index];
            return MasterRow(model, in e, selected: !e.IsFolder && e.Id == picked, pick: () => _picked.Value = e.Id);
        }

        static string GroupLabel(int group) => group switch
        {
            0 => Loc.Get(Strings.Library.PlaylistGroup.MadeByYou),
            1 => Loc.Get(Strings.Library.PlaylistGroup.MadeForYou),
            _ => Loc.Get(Strings.Library.PlaylistGroup.Followed),
        };

        static Element MasterRow(NavModel model, in SidebarLibraryEntry e, bool selected, Action pick)
        {
            bool folder = e.IsFolder;
            string id = e.Id, name = e.Name, folderId = e.FolderId;
            bool open = folder && Sidebar.IsFolderExpanded(folderId);
            string? sub = Sidebar.PaneText.SubtitleOf(in e);
            Element title = name.Length > 0
                ? R.Type.TrackTitle(name) with { Color = selected ? Tok.AccentTextPrimary : Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
                : Controls.PendingBar(160f, 14f);
            var words = new List<Element>(2) { title };
            if (sub is { Length: > 0 })
                words.Add(R.Type.MicroMeta(sub) with { Color = Tok.TextTertiary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f });
            var kids = new List<Element>(3)
            {
                Sidebar.Cover.ForEntry(in e, LibraryPlaylistsRules.Art),
                new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 1f, Children = words.ToArray() },
            };
            if (folder) kids.Add(Icon(open ? Icons.ChevronDown : Icons.ChevronRight, 12f, Tok.TextTertiary));
            return new BoxEl
            {
                Key = LibraryPlaylistsRules.RowPrefix + id, Direction = 0, Height = LibraryPlaylistsRules.RowH, AlignItems = FlexAlign.Center,
                Gap = LibraryPlaylistsRules.ArtGap, MinWidth = 0f, Corners = R.Shape.All(ShapeRole.Cover),
                Fill = selected ? Tok.FillSubtleTertiary : ColorF.Transparent,
                Padding = new Edges4(e.Depth * LibraryPlaylistsRules.Indent + 8f, 0f, 8f, 0f),
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                OnClick = folder ? () => model.ToggleFolder(folderId) : pick,
                Children = kids.ToArray(),
            }.Interactive(Interaction.Subtle);
        }
    }

    // ══ THE PANE ═════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The picked playlist's rootlist entry (its name, cover, owner words, uri). Equality by id: a re-push of the same playlist is not a new pane.</summary>
    sealed record PlaylistPaneProps(SidebarLibraryEntry Entry)
    {
        public bool Equals(PlaylistPaneProps? o) => o is not null && Entry.Id == o.Entry.Id && Entry.Name == o.Entry.Name && Entry.ChildCount == o.Entry.ChildCount;
        public override int GetHashCode() => HashCode.Combine(Entry.Id, Entry.Name, Entry.ChildCount);
    }

    sealed class PlaylistPane : Component
    {
        static readonly Track.TableProfile s_profile = Track.TableProfile.From(Detail.Config.Playlist with { Heart = HeartMode.None, Recommendations = false });

        int _slot = Table.None;

        public override Element Render()
        {
            var e = UseProps<PlaylistPaneProps>().Entry;
            uint epoch = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Playlists.Changed.Value; _ = scope.Edges.PlaylistTracks.Changed.Value; _ = scope.Tracks.Changed.Value;
            _slot = e.Uri.Length > 0 && scope.Playlists.TryGetSlot(EntityId.Parse(e.Uri), out int s) ? s : Table.None;
            UseEffect(Demand, DepKey.From(_slot, (int)epoch));
            UseEffect(DemandRows);

            var pl = new Playlist(_slot);
            string route = e.RouteKey ?? "", name = e.Name;
            Action play = () => { var p = new Playlist(_slot); if (p.IsValid) Playback.PlayOrToggleContext(p.Id); };
            Action shuffle = () => { var p = new Playlist(_slot); if (!p.IsValid) return; Playback.SetShuffle(true); Playback.PlayContext(p.Id); };
            Action open = () => { if (route.Length > 0) Sidebar.Model.Navigate(route, name); };
            string meta = e.Creator.Length > 0 ? e.Creator : "";
            string? count = Sidebar.PaneText.SubtitleOf(in e);
            if (count is { Length: > 0 }) meta = meta.Length > 0 ? meta + " · " + count : count;

            Element header = new BoxEl
            {
                Direction = 0, Gap = LibraryPlaylistsMasterRules.CoverGap, AlignItems = FlexAlign.Center, Shrink = 0f, MinWidth = 0f,
                Padding = new Edges4(0f, 0f, 0f, LibraryPlaylistsMasterRules.HeaderBottom),
                Children =
                [
                    Sidebar.Cover.ForEntry(in e, LibraryPlaylistsMasterRules.Cover),
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 4f,
                        Children =
                        [
                            R.Type.QuickplayHead(name) with { Color = Tok.TextPrimary, MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            R.Type.DenseMeta(meta) with { Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                            new BoxEl
                            {
                                Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, Padding = new Edges4(0f, LibraryPlaylistsMasterRules.VerbsTop, 0f, 0f), Shrink = 0f, MinWidth = 0f,
                                Children =
                                [
                                    Button.Accent(Loc.Get(Strings.Home.Play), play),
                                    Button.Standard(Loc.Get(Strings.Detail.Shuffle), shuffle),
                                    Button.Standard(Loc.Get(Strings.Library.OpenPlaylist), open),
                                ],
                            },
                        ],
                    },
                ],
            };
            Element rows = pl.IsValid
                ? Track.Table(new Track.TableArgs
                {
                    Source = Track.TableSource.ForPlaylist(pl), Profile = s_profile, ShowToolbar = false, Embedded = true,
                    ScrollKey = "library-playlists-pane:" + e.Id,
                })
                : new BoxEl();
            return new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f,
                Children = [header, new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, Children = [rows] }],
            };
        }

        /// <summary>Once per playlist per scope: its identity, and its list through the page's own open rule.</summary>
        void Demand()
        {
            var pl = new Playlist(_slot);
            if (!pl.IsValid) return;
            Entities.Ensure(pl, PlaylistFields.Identity | PlaylistFields.Visibility | PlaylistFields.Saves | PlaylistFields.TrackCount);
            ListOpen.Open(pl, ListOpenPolicy.Surface.Page);
        }

        /// <summary>As the list lands: the rows it shows, and the next page of a partial one.</summary>
        void DemandRows()
        {
            _ = Entities.ScopeEpoch.Value;
            var scope = Entities.Current;
            _ = scope.Edges.PlaylistTracks.Changed.Value;
            _ = scope.Playlists.Changed.Value;
            var pl = new Playlist(_slot);
            if (!pl.IsValid) return;
            var slots = pl.TrackSlots;
            if (slots.Length > 0)
                Entities.Ensure(MemoryMarshal.Cast<int, Track>(slots), TrackFields.Row | TrackFields.Audio | TrackFields.Tags | TrackFields.Video);
            if (pl.MembershipState == EdgeState.Partial) Entities.EnsureEdge(FetchEdge.PlaylistTracks, pl.Slot, pl.TrackSlots.Length);
            pl.Refold();
        }
    }
}
