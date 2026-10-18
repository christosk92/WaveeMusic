// ── Entities/User.Page.Playlists.cs — the library's playlists list (RouteKind.LibraryPlaylists; themes P5 Z7) ─────────────────────────
//
// A structure without a nav tree (Zune: the band is the navigation) has nowhere to list the user's playlists and folders: the sidebar's
// Playlists section is the only place, and its pane does not mount. This is the page for them, one of the library's row-2 kinds
// (`User.LibraryChromeViews`: liked · playlists · albums · artists · podcasts · audiobooks).
//
// CORE-DRAWN, one picture for every structure: it reads the SAME data the sidebar plans from (`Sidebar.Model.Rootlist`, the rootlist tree
// depth-first with its folders and their depths, hydrated by the binder the model pumps), the same cover (`Sidebar.Cover`), the same
// subtitle words (`Sidebar.PaneText.SubtitleOf`), the same folder expansion (`Sidebar.Model.ToggleFolder`, persisted) and the same create
// verbs. Its look is the active style's roles (`R.Type`, `R.Shape`, `R.Case`, the page gutter of `StyleMetrics`), so it reads as Zune under
// Zune and as Classic under Classic. Classic has no link to it (its sidebar lists them) but it works if navigated to.
//
// The model's pump normally runs from the docked pane's effect; with no pane (Zune) this page starts it, so the rootlist loads here.

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
    /// <summary>Registers the playlists list as the page of <see cref="Shell.RouteKind.LibraryPlaylists"/> (the lazy group of one, <c>Shell.InstallLazyGroupFor</c>).</summary>
    public static void InstallLibraryPlaylists() => Shell.SetPage(Shell.RouteKind.LibraryPlaylists, LibraryPlaylistsFor);

    /// <summary><see cref="Shell.RouteKind.LibraryPlaylists"/>: one component keyed by the route's name.</summary>
    public static Element LibraryPlaylistsFor(in Shell.Route route)
        => Embed.Comp(static () => new LibraryPlaylistsPage()) with { Key = "library-playlists:" + Shell.NameOf(route) };

    /// <summary>The numbers of the playlists list.</summary>
    public static class LibraryPlaylistsRules
    {
        /// <summary>The node keys: the page's column, the list, the two create words.</summary>
        public const string ColumnKey = "library-playlists-column", ListKey = "library-playlists-list",
            NewPlaylistKey = "library-playlists-new-playlist", NewFolderKey = "library-playlists-new-folder", RowPrefix = "library-playlists-row:";

        /// <summary>A row, its art, the gap between art and words, the indent a folder level adds, the head's height.</summary>
        public const float RowH = 52f, Art = 40f, ArtGap = 12f, Indent = 20f, HeadH = 40f;

        /// <summary>The column's cap and the page's top.</summary>
        public const float ColumnMax = 760f, PageTop = 8f, PageBottom = 16f;

        /// <summary>The seed rows the shimmer draws.</summary>
        public const int ShimmerRows = 8;
    }

    sealed partial class LibraryPlaylistsPage : Component
    {
        readonly List<SidebarLibraryEntry> _rows = new(64);
        int _playlists, _folders;

        public override Element Render()
        {
            var model = Sidebar.Model;
            // The pane's job when it is mounted; idempotent, so both may run.
            UseSignalEffect(static () =>
            {
                var m = Sidebar.Model;
                m.SyncAccount();
                m.Pump();
            });
            _ = model.LibraryItems.Version.Value;
            _ = model.InputVersion.Value;
            _ = model.BinderEpoch.Value;
            _ = Sidebar.FolderVersion.Value;
            var readiness = model.Readiness.Value.Tree;

            var tiers = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            float gutter = tiers.Policy is null ? ThemeHost.Metrics.Value.Gutter : tiers.Window switch
            {
                WidthTier.Compact => 16f,
                WidthTier.Small => 24f,
                _ => ThemeHost.Metrics.Value.Gutter,
            };

            Collect(model.Rootlist);
            // The grouped master-detail (the structure asked, and the content is not one column wide): the list column and the playlist pane share the width the flat page caps.
            var content = tiers.Content;
            bool master = ShellPolicy.Tracked.LibraryTiers.PlaylistsMasterDetail && content >= WidthTier.Small && _rows.Count > 0;
            Element body;
            if (master)
            {
                float listW = content >= WidthTier.Huge ? LibraryPlaylistsMasterRules.ListHuge : content == WidthTier.Small ? LibraryPlaylistsMasterRules.ListSmall : LibraryPlaylistsMasterRules.ListStandard;
                float gap = content == WidthTier.Small ? LibraryPlaylistsMasterRules.GapSmall : LibraryPlaylistsMasterRules.GapStandard;
                body = Master(model, listW, gap);
            }
            else if (_rows.Count == 0)
            {
                body = readiness switch
                {
                    NavReadiness.Failed => Controls.Vacancy(Controls.VacancyVoice.Error, onAction: model.Invalidate),
                    NavReadiness.Pending => Shimmer(),
                    _ => Controls.Vacancy(Controls.VacancyVoice.Empty, Controls.VacancyScale.Page, title: Loc.Get(Strings.Sidebar.Empty.Playlists), subtitle: ""),
                };
                body = new BoxEl { Direction = 1, Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Stretch, Children = [body] };
            }
            else
            {
                var rows = _rows.ToArray();
                body = new VirtualListEl
                {
                    ItemCount = rows.Length,
                    ItemLayout = new StackVirtualLayout(LibraryPlaylistsRules.RowH),
                    RenderItem = i => (uint)i < (uint)rows.Length ? Row(model, in rows[i]) : new BoxEl(),
                    KeyOf = i => (uint)i < (uint)rows.Length ? rows[i].Id : i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, AlignSelf = FlexAlign.Stretch,
                } with { Key = LibraryPlaylistsRules.ListKey };
            }

            return new BoxEl
            {
                Key = LibraryPlaylistsRules.ColumnKey, Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                MaxWidth = master ? float.NaN : LibraryPlaylistsRules.ColumnMax + 2f * gutter, AlignItems = FlexAlign.Stretch,
                Padding = new Edges4(gutter, LibraryPlaylistsRules.PageTop, gutter, LibraryPlaylistsRules.PageBottom),
                Children = [Head(model, master ? Strings.Library.PlaylistsCount(_playlists, _folders) : null), body],
            };
        }

        /// <summary>The visible rows: the rootlist tree depth-first, a collapsed folder's children left out; counts for the head.</summary>
        void Collect(IReadOnlyList<SidebarLibraryEntry>? tree)
        {
            _rows.Clear();
            _playlists = _folders = 0;
            if (tree is null) return;
            int skipBelow = -1;
            for (int i = 0; i < tree.Count; i++)
            {
                var e = tree[i];
                if (e.Kind is not (SidebarEntryKind.Playlist or SidebarEntryKind.Folder)) continue;
                if (e.IsFolder) _folders++; else _playlists++;
                if (skipBelow >= 0)
                {
                    if (e.Depth > skipBelow) continue;
                    skipBelow = -1;
                }
                _rows.Add(e);
                if (e.IsFolder && !Sidebar.IsFolderExpanded(e.FolderId)) skipBelow = e.Depth;
            }
        }

        // ── the head: the create words ──

        static Element Head(NavModel model, string? count) => new BoxEl
        {
            Direction = 0, Height = LibraryPlaylistsRules.HeadH, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 24f, MinWidth = 0f,
            Children = count is null
                ?
                [
                    Word(LibraryPlaylistsRules.NewPlaylistKey, Loc.Get(Strings.Sidebar.CreatePlaylistTooltip), () => model.CreatePlaylist(null, true)),
                    Word(LibraryPlaylistsRules.NewFolderKey, Loc.Get(Strings.Sidebar.CreateFolder), () => model.CreateFolder(null)),
                ]
                :
                [
                    Word(LibraryPlaylistsRules.NewPlaylistKey, Loc.Get(Strings.Sidebar.CreatePlaylistTooltip), () => model.CreatePlaylist(null, true)),
                    Word(LibraryPlaylistsRules.NewFolderKey, Loc.Get(Strings.Sidebar.CreateFolder), () => model.CreateFolder(null)),
                    new BoxEl { Grow = 1f, MinWidth = 0f },
                    R.Type.DenseMeta(count) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f },
                ],
        };

        static Element Word(string key, string text, Action go) => new BoxEl
        {
            Key = key, Direction = 0, Shrink = 0f, AlignItems = FlexAlign.Center, Height = LibraryPlaylistsRules.HeadH,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = go,
            Children =
            [
                R.Type.DenseMeta(text) with
                {
                    Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap,
                },
            ],
        };

        // ── a row ──

        static Element Row(NavModel model, in SidebarLibraryEntry e)
        {
            bool folder = e.IsFolder;
            string id = e.Id, route = e.RouteKey ?? "", name = e.Name, folderId = e.FolderId;
            bool open = folder && Sidebar.IsFolderExpanded(folderId);
            string? sub = Sidebar.PaneText.SubtitleOf(in e);

            Element title = name.Length > 0
                ? R.Type.TrackTitle(name) with { Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f }
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
                Gap = LibraryPlaylistsRules.ArtGap, MinWidth = 0f,
                Padding = new Edges4(e.Depth * LibraryPlaylistsRules.Indent, 0f, 8f, 0f),
                Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
                OnClick = folder ? () => model.ToggleFolder(folderId) : () => { if (route.Length > 0) model.Navigate(route, name); },
                Children = kids.ToArray(),
            }.Interactive(Interaction.Subtle);
        }

        // ── the shimmer ──

        static Element Shimmer()
        {
            var rows = new Element[LibraryPlaylistsRules.ShimmerRows];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl
                {
                    Direction = 0, Height = LibraryPlaylistsRules.RowH, AlignItems = FlexAlign.Center, Gap = LibraryPlaylistsRules.ArtGap, MinWidth = 0f,
                    Children =
                    [
                        Controls.PendingBar(LibraryPlaylistsRules.Art, LibraryPlaylistsRules.Art),
                        new BoxEl
                        {
                            Direction = 1, Grow = 1f, Basis = 0f, Gap = 4f, MinWidth = 0f,
                            Children = [Controls.PendingBar(180f, 14f), Controls.PendingBar(90f, 12f)],
                        },
                    ],
                };
            return new BoxEl { Direction = 1, AlignItems = FlexAlign.Stretch, Children = rows };
        }
    }
}
