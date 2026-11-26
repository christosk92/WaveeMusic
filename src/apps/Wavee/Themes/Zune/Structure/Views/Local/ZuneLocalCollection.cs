// ── Themes/Zune/Structure/Views/Local/ZuneLocalCollection.cs — Zune's "On this PC" collection page (#240 P2, board 21) ──────────────
//
// Board 21: "collection" in the display voice over a full-bleed cover mosaic, the song count in display numbers, the rest of the stats and
// the library's state under it, then the shelves (lowercase captions, big covers). The pivots (home · albums · artists · songs · folders ·
// insights) and the verbs (play, shuffle, add music) are the band's row 2, published by the core host (`LocalCollectionPage`), so the page
// draws neither (one home per verb). Display type is never shrunk: the title wraps (it is one word) and the count keeps its size.
//
//   Pages.LocalCollection
//   └─ Host "zlc-page"
//      └─ SkelRegionEl (the first fold)
//          home:   ScrollView [ head (mosaic · "collection" · count + "songs" · stats · state) · the shared Home shelves ]
//          others: the shared body of the pivot (wall, artists, songs table, folders, insights) on the frame's gutter
//          a detail open over a pivot (LocalOpened: a folder, an album's folder): the shared folder view, its step keyed and moved by the
//          structure's drill (CommonLocalCollectionParts.DetailMotion: Zune declares none, so the core's quick fade)

using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;

using H = Wavee.Themes.Zune.ZuneHomeRules;
using P = Wavee.Strings.LocalFiles.Page;
using X = Wavee.Themes.Common.CommonLocalCollectionParts;
using Z = Wavee.Themes.Zune.ZuneLocalCollectionRules;

namespace Wavee.Themes.Zune;

/// <summary>Zune's collection page.</summary>
internal static class ZuneLocalCollection
{
    /// <summary>The registered template of <see cref="ViewKeys.LocalCollectionPage"/>.</summary>
    public static Element Template(LocalCollectionPageProps p, in ViewContext ctx)
    {
        var tiers = ctx.Tier();
        // The page's lead: the frame's gutter, or the centred 2400 column's edge on a wider window (the band's and every page title's x).
        float lead = ZuneFrameRules.ColumnLead(tiers.ContentWidth, ZuneTiers.GutterOf(in tiers));
        return Embed.Comp(new HostProps(p, lead, ZuneLocalCollectionTiers.For(tiers.Content)), static () => new Host()) with { Key = Z.PageKey };
    }

    sealed record HostProps(LocalCollectionPageProps Page, float Gutter, CommonLocalCollectionLayout Lay);

    sealed class Host : Component
    {
        static readonly Func<bool> s_never = static () => false;
        Func<bool>? _pending;
        Func<Element>? _content, _shimmer;
        HostProps? _hp;
        IReadSignal<ThemeFacts>? _facts;
        readonly X.DetailMotion _detail = new();
        (string Key, LayoutTransition? Enter) _step;
        IReadOnlyList<LocalShelfSlot>? _shelves;
        bool _reveal;

        public override Element Render()
        {
            var hp = UseProps<HostProps>();
            _hp = hp;
            var p = hp.Page;
            _pending ??= () => p.View.Value.Version == 0;
            _facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
            _content ??= () => Page(_hp!, _hp!.Page.View.Value, _step, _facts!.Peek(), _reveal);
            _shimmer ??= () => Page(_hp!, X.Seed, default, _facts!.Peek(), false);
            var pivot = p.Pivot.Value;
            _ = p.Sort.Value;
            _step = _detail.Step("zlc-body", pivot, p.Opened.Value, _facts.Peek());
            // Home's shelves as last rendered: a change is "Edit home" showing or hiding one (X.HomeBody's reveal).
            var shelves = p.Shelves.Value;
            _reveal = _shelves is not null && !ReferenceEquals(shelves, _shelves);
            _shelves = shelves;
            _ = p.Link?.Reviewing.Value;
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, DropTarget = p.Drop,
                Children = [new SkelRegionEl(_pending, s_never, _content, _shimmer, null, SkelReveal.Soft, Design.Skeleton, null, SmoothResize: false)],
            };
        }
    }

    static Element Page(HostProps hp, LocalCollectionView view, (string Key, LayoutTransition? Enter) step, ThemeFacts facts, bool reveal)
    {
        var p = hp.Page;
        float g = hp.Gutter;
        var lay = hp.Lay;
        // No music folder: the Local Files onboarding (S4), shared with the list.
        if (p.Onboarding() is { } onboarding) return X.Gut("lc-empty", g, onboarding, grow: true);
        var pivot = p.Pivot.Peek();
        var opened = p.Opened.Peek();
        // "Your files and Spotify": the review takes the page's place while it is open (the shared picture, in Zune's type and case).
        bool reviewing = p.Link is { } link && link.Reviewing.Peek() is not null;
        // A detail open over the pivot: the shared folder view, its breadcrumb starting at "collection" (Folders) or at the pivot it was opened from.
        Element body = reviewing ? X.LinkReview(p.Link!, g, p.RouteKey)
            : opened?.IsOpenOver(pivot) == true
            ? X.FolderBrowser(view, opened, p, lay, g, pivot == LocalPivot.Folders ? Loc.Get(P.Collection) : CommonLocalCollection.PivotWord(pivot))
            : pivot switch
        {
            LocalPivot.Home => ScrollView(new BoxEl
            {
                Key = Z.ColumnKey, Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, H.PageBottom),
                Children = [Head(p, view, lay, g), X.LinkOffer(p.Link, 0f), .. X.HomeBody(view, p.Verbs, p.SetPivot, lay, g, p.Shelves.Peek(), reveal)],
            }) with { Key = "zlc-scroll:home", Grow = 1f, MinWidth = 0f, ScrollKey = p.RouteKey + ":home" },
            LocalPivot.Albums => X.AlbumWall(view, p.Sort.Peek(), p.SetSort, p.Verbs, lay, g, p.RouteKey),
            LocalPivot.Artists => X.ArtistGrid(view, p.Verbs, lay, g, p.RouteKey),
            LocalPivot.Songs => X.Gut("lc-songs", g, p.Songs(), grow: true),
            LocalPivot.Folders => X.FolderBrowser(view, null, p, lay, g, Loc.Get(P.Collection)),
            _ => X.Insights(view, lay, g, p.RouteKey),
        };
        body = X.DetailSlot(step.Key ?? "zlc-body:" + pivot.ToString() + ":list", body, step.Enter, X.DetailMotion.Exit(pivot, opened, facts));
        return new BoxEl
        {
            Key = "zlc-body:" + (reviewing ? "review" : pivot.ToString()), Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, Padding = new Edges4(0f, H.PageTop, 0f, 0f),
            Children = [body],
        };
    }

    /// <summary>Zune's "edit home": a lowercase text verb in the accent, its hit rect the Compact target's height.</summary>
    static readonly Func<Action, Action<NodeHandle>, Element> s_editTrigger = static (toggle, realize) => new BoxEl
    {
        Key = CommonLocalEditHome.TriggerKey, Direction = 0, AlignItems = FlexAlign.Center, Height = Z.VerbH, Shrink = 0f, Margin = new Edges4(Z.VerbGap, 0f, 0f, 0f),
        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, OnClick = toggle, OnRealized = realize,
        Children = [R.Type.DenseMeta(Loc.Get(P.EditHome)) with { Case = R.Case.For(LabelKind.Label), Color = Tok.AccentTextPrimary }],
    };

    /// <summary>The state line's mark: a ring while reading, else the dot (the attention ink when every folder is away).</summary>
    static readonly Func<LibraryActivity.LineKind, Element> s_mark = static kind => kind == LibraryActivity.LineKind.Scanning
        ? ProgressRing.Create(isActive: true, size: Z.StatusDot * 2f)
        : new BoxEl
        {
            Width = Z.StatusDot, Height = Z.StatusDot, Corners = CornerRadius4.All(Z.StatusDot / 2f),
            Fill = kind == LibraryActivity.LineKind.Offline ? Tok.SystemFillAttention : Tok.AccentDefault,
        };

    /// <summary>The head (board 21): the mosaic bleeding behind "collection", the count in display numbers, the stats and the library's state.</summary>
    static Element Head(LocalCollectionPageProps p, LocalCollectionView view, CommonLocalCollectionLayout lay, float g)
    {
        Element words = X.Gut("zlc-head-words", g, new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                // One word in the panorama voice, never shrunk: where the column is narrower than the word (Compact) it bleeds past the edge under
                // the scroller's edge fade (the owner's "bleed past the edge with a fade").
                new ScrollEl
                {
                    Horizontal = true, ContentSized = true, MinWidth = 0f, SuppressScrollBar = true, AutoEdgeFade = true,
                    Content = new BoxEl
                    {
                        Direction = 0, Shrink = 0f,
                        Children = [R.Type.Panorama(Loc.Get(P.Collection)) with { Key = ZuneFrameRules.PageTitleKey, Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary }],
                    },
                },
                new BoxEl
                {
                    Direction = 0, Gap = Z.CountWordGap, AlignItems = FlexAlign.End, Wrap = true, MinWidth = 0f, Margin = new Edges4(0f, Z.CountGap, 0f, 0f),
                    Children =
                    [
                        R.Type.Hero(view.Songs.ToString("N0", CultureInfo.CurrentCulture)) with { Color = Tok.TextPrimary },
                        R.Type.Heading(Loc.Get(view.Songs == 1 ? P.SongWord : P.SongsWord)) with { Case = R.Case.For(LabelKind.Heading), Color = Tok.TextSecondary },
                    ],
                },
                R.Type.TrackMeta(X.StatsText(view, withSongs: false)) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f, Margin = new Edges4(0f, Z.StatsGap, 0f, 0f) },
                new BoxEl
                {
                    // One height in every state (the "edit home" verb's 40, which the first scan's line fits inside): a scan starting never moves the shelves.
                    Key = "lc-status", Direction = 0, Gap = Z.StatusGap, AlignItems = FlexAlign.Center, MinWidth = 0f, MinHeight = Z.VerbH, Margin = new Edges4(0f, Z.StatusGap, 0f, 0f),
                    Children =
                    [
                        // A first scan shows the shared scan line and bar (S4) where the state word stands; else the state, from the facts the
                        // band's activity reads (LibraryActivity), so the two flip together.
                        new BoxEl
                        {
                            // Its width eases when the scan's line gives way to the state's (and back), so nothing beside it moves at once.
                            Key = "lc-activity", Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = 1f, Layout = CommonLocalCollectionRules.SizeReflow,
                            Children = [p.FirstScan() ? p.ScanLine(float.NaN, false) : X.Activity(s_mark, R.Type.DenseMeta("") with { Case = R.Case.For(LabelKind.Label), Color = Tok.TextSecondary })],
                        },
                        // "edit home": Zune's text verb at the state line's end (the shelves are under this head). It stays in every state and glides
                        // to its place when the line before it changes width (the scan's words giving way to the state's), never a jump.
                        new BoxEl
                        {
                            Key = "lc-edit-home:slot", Direction = 0, Shrink = 0f, Layout = CommonLocalCollectionRules.ShelfMove,
                            Children =
                            [
                                Embed.Comp(new CommonLocalEditHome.Props(p, s_editTrigger, FlyoutPlacement.BottomEdgeAlignedLeft), static () => new CommonLocalEditHome())
                                    with { Key = CommonLocalEditHome.TriggerKey + ":host" },
                            ],
                        },
                    ],
                },
            ],
        }, top: Z.HeadTop, bottom: Z.HeadBottom);
        return new BoxEl
        {
            Key = Z.HeadKey, ZStack = true, MinWidth = 0f, ClipToBounds = true,
            Children = [X.Backdrop(view, Z.MosaicTile, leftFade: false, (R.Layer.ShellGround, Z.VeilTop, Z.VeilMid, Z.MosaicStrength)), words],
        };
    }
}
