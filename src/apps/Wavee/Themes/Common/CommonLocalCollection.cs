// ── Themes/Common/CommonLocalCollection.cs — "On this PC", the collection page WinUI and Classic share (#240 P2, boards 18-20) ─────────
//
// The owner's rule: Classic uses the WinUI layout (no Classic-only design), so the page lives here and both structures register it
// (`Register`), each with its own page gutter and painting it with its own tokens and type ramp. Zune draws its own head and reuses the
// body parts (`CommonLocalCollectionParts`).
//
//   Pages.LocalCollection
//   └─ Host "lc-page" (re-renders on a fold, a pivot or a sort; the props never change)
//      ├─ SkelRegionEl  pending until the first fold lands; the shimmer is this page over a seed view (one real UI)
//      │   ONE column for every pivot, so the pivot bar never remounts and a switch animates in place (the owner, 2026-11-24: "the layout
//      │   should animate in place"):
//      │     "lc-head"   the head: Home's hero (mosaic or calm gradient, title, stats, folder chips + activity, actions), else the compact
//      │                 head (thumb, title, stats, actions); the two cross-fade and the slot's height eases (the bar under it glides)
//      │     the pivot   one node across every switch
//      │     "lc-body"   the pivot's body, keyed per pivot: the structure's library pivot recipe (WinUI's: the new body slides in from the
//      │                 side of its tab and decelerates, the old one fades fast; PageMotionPolicy.LibraryPivot), else the core's quick fade
//      │   Home:   ScrollView "lc-scroll:home" [ Recently added (Today / This week / Earlier this month) · Lossless · Shuffle by decade ]
//      │   others: the pivot's body:
//      │           Albums  sort chips + count · virtual cover wall          Artists  virtual grid of artist cards
//      │           Songs   the core's track table over Local Files            Folders  breadcrumb · folder tiles · the folder's songs
//      │           Insights formats, quality, covers, tag health, can't play, folders, the private line
//      └─ the empty state (S4) when there is no music folder and nothing indexed
//
// Jump back in, Rediscover and From your playlists (board 18) have no data source yet (no local play history, no playlist link count):
// they are left out, never faked.

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
using static FluentGpu.Dsl.Ui;

using L = Wavee.Themes.Common.CommonLocalCollectionRules;
using P = Wavee.Strings.LocalFiles.Page;
using X = Wavee.Themes.Common.CommonLocalCollectionParts;

namespace Wavee.Themes.Common;

/// <summary>The shared "On this PC" page (WinUI and Classic).</summary>
internal static class CommonLocalCollection
{
    /// <summary>Register the page as <paramref name="templateId"/>'s <see cref="ViewKeys.LocalCollectionPage"/>, on the theme's page gutter.</summary>
    public static void Register(ViewRegistryBuilder views, string templateId, Func<TierFacts, float> gutterOf, string titleKey)
        => views.Add(ViewKeys.LocalCollectionPage, (LocalCollectionPageProps p, in ViewContext ctx) => Template(p, in ctx, gutterOf, titleKey), templateId);

    static Element Template(LocalCollectionPageProps p, in ViewContext ctx, Func<TierFacts, float> gutterOf, string titleKey)
    {
        var tiers = ctx.Tier();
        var lay = CommonLocalCollectionLayout.For(tiers.Content);
        return Embed.Comp(new HostProps(p, gutterOf(tiers), lay, titleKey), static () => new Host()) with { Key = L.PageKey };
    }

    sealed record HostProps(LocalCollectionPageProps Page, float Gutter, CommonLocalCollectionLayout Lay, string TitleKey);

    sealed class Host : Component
    {
        Func<bool>? _pending;
        static readonly Func<bool> s_never = static () => false;
        Func<Element>? _content, _shimmer;
        HostProps? _hp;
        // The pivot on screen and the body switch that brought it (null: the first pivot, which the page's own entrance presents).
        LocalPivot _shown;
        bool _hasShown;
        LayoutTransition? _swap;
        IReadSignal<ThemeFacts>? _facts;

        public override Element Render()
        {
            var hp = UseProps<HostProps>();
            _hp = hp;
            var p = hp.Page;
            _facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
            _pending ??= () => p.View.Value.Version == 0;
            _content ??= () => Page(_hp!, _hp!.Page.View.Value, _swap);
            _shimmer ??= () => Page(_hp!, X.Seed, null);
            var pivot = p.Pivot.Value;
            if (!_hasShown || pivot != _shown)
            {
                // The structure's library pivot recipe by the pivot's order (a tab to the right comes in from the right), as the library family's
                // body switch reads it; none for the first pivot.
                _swap = _hasShown ? SwapOf(pivot > _shown, _facts.Peek()) : null;
                _shown = pivot;
                _hasShown = true;
            }
            _ = p.Sort.Value;
            _ = p.Folder.Value;
            _ = p.Shelves.Value;
            _ = p.Link?.Reviewing.Value;
            return new BoxEl
            {
                Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, DropTarget = p.Drop,
                Children = [new SkelRegionEl(_pending, s_never, _content, _shimmer, null, SkelReveal.Soft, Design.Skeleton, null, SmoothResize: false)],
            };
        }
    }

    /// <summary>The body switch for a pivot change: the structure's library pivot recipe (<see cref="PageMotionPolicy.LibraryPivot"/>; WinUI's slides the
    /// new body in from the side of its tab and decelerates, the old one fades fast, and answers its quick fade under reduced motion), else the
    /// core's quick same-kind fade.</summary>
    static LayoutTransition? SwapOf(bool fromRight, ThemeFacts facts)
        => facts.Policies.PageMotion.LibraryPivot is { } pivot ? pivot(fromRight, facts.Tiers) : Design.Nav.SameKindSwap;

    /// <summary>The head's two forms cross-fade; the body's switch moves on the same clock: WinUI's incoming 300 ms on an ease-out (its
    /// (0, 0, 0, 1) decelerate carried most of a short Compact slide in one frame: the trace's "instant"), which also takes the outgoing node
    /// most of its way out early. A node carries one clock for both legs, so the recipe's enter leg (<see cref="SwapOf"/>: the side, the travel)
    /// rides it as the node's Enter.</summary>
    static FluentGpu.Animation.MotionTokenDef SwapClock => FluentGpu.Animation.MotionTokenDef.Eased(L.HeadInMs, Easing.EaseOut);
    static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

    static Element Page(HostProps hp, LocalCollectionView view, LayoutTransition? swap)
    {
        var p = hp.Page;
        var pivot = p.Pivot.Peek();
        // No music folder: the Local Files onboarding (S4), shared with the list.
        if (p.Onboarding() is { } onboarding) return X.Gut("lc-empty", hp.Gutter, onboarding, grow: true);
        float g = hp.Gutter;
        // "Your files and Spotify": the review takes the page's place while it is open (board 29: On this PC › Review matches).
        if (p.Link is { } link && link.Reviewing.Peek() is not null) return X.LinkReview(link, g, p.RouteKey);
        bool home = pivot == LocalPivot.Home;
        Element bar = Pivot(p, pivot, g);
        Element head = home ? Hero(p, view, hp.Lay, g, hp.TitleKey) : CompactHead(p, view, hp.Lay, g, hp.TitleKey);
        Element body = pivot switch
        {
            LocalPivot.Home => ScrollView(new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, L.PageBottom),
                Children = [X.LinkOffer(p.Link, g), .. X.HomeBody(view, p.Verbs, p.SetPivot, hp.Lay, g, p.Shelves.Peek())],
            }) with { Key = L.ScrollKey + ":home", Grow = 1f, MinWidth = 0f, ScrollKey = p.RouteKey + ":home" },
            LocalPivot.Albums => X.AlbumWall(view, p.Sort.Peek(), p.SetSort, p.Verbs, hp.Lay, g, p.RouteKey),
            LocalPivot.Artists => X.ArtistGrid(view, p.Verbs, hp.Lay, g, p.RouteKey),
            LocalPivot.Songs => X.Gut("lc-songs", g, p.Songs(), grow: true),
            LocalPivot.Folders => X.FolderBrowser(view, p.Folder.Peek(), p.OpenFolder, p.Verbs, hp.Lay, g, p.Title, p.RouteKey),
            _ => X.Insights(view, hp.Lay, g, p.RouteKey),
        };
        bool motion = swap is not null;
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                // The head's slot eases its height between the hero and the compact head, so the pivot under it glides, never jumps.
                new BoxEl
                {
                    Key = L.HeadKey, Direction = 1, MinWidth = 0f, Shrink = 0f, ClipToBounds = true, Layout = motion ? MotionRecipes.CardResizeHeight : null,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = home ? L.HeadKey + ":hero" : L.HeadKey + ":compact", Direction = 1, MinWidth = 0f, Children = [head],
                            Enter = motion ? s_fade : null, Exit = s_fade, Transition = SwapClock,
                        },
                    ],
                },
                bar,
                new BoxEl
                {
                    Key = L.BodyKey, Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                    Children =
                    [
                        new BoxEl
                        {
                            Key = L.BodyKey + ":" + pivot.ToString(), Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f, Children = [body],
                            // The new body comes in from its tab's side (the recipe's enter leg); the old one fades where it stands: a node's exit is the
                            // one it last rendered with, which cannot know the side of the NEXT switch.
                            Enter = swap?.Enter, Exit = s_fade, Transition = SwapClock,
                        },
                    ],
                },
            ],
        };
    }

    // ── the hero (board 18) ──────────────────────────────────────────────────────────────────────────────────────────

    static Element Hero(LocalCollectionPageProps p, LocalCollectionView view, CommonLocalCollectionLayout lay, float g, string titleKey)
    {
        Element words = X.Gut("lc-hero-words", g, new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                R.Type.PageHero(p.Title) with { Key = titleKey, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                X.HeroStats(view) with { Margin = new Edges4(0f, L.TitleGap, 0f, 0f) },
                new BoxEl { Margin = new Edges4(0f, L.ChipsGap, 0f, 0f), Children = [X.Chips(view, p.Verbs, lay.ChipH, p.FirstScan() ? p.ScanLine(float.NaN, true) : null)] },
                Actions(p.Verbs, view, wrap: true) with { Margin = new Edges4(0f, L.ActionsGap, 0f, 0f) },
            ],
        }, top: L.HeroTop, bottom: L.HeroBottom);
        return new BoxEl
        {
            Key = L.HeroKey, ZStack = true, MinWidth = 0f, ClipToBounds = true,
            Children = [X.Backdrop(view, L.MosaicTile, leftFade: true), words],
        };
    }

    /// <summary>Play all · Shuffle · Add music. <paramref name="play"/> false leaves Play and Shuffle to the Songs table's own bar (one home per verb).</summary>
    static BoxEl Actions(LocalCollectionVerbs v, LocalCollectionView view, bool wrap, bool play = true)
    {
        bool any = view.Songs > 0;
        Element add = Button.Create(Loc.Get(P.AddMusic), v.AddMusic, ButtonAppearance.Standard, glyph: Icons.Add);
        return new BoxEl
        {
            Direction = 0, Gap = L.ActionGap, AlignItems = FlexAlign.Center, Wrap = wrap, MinWidth = 0f,
            Children = play
                ?
                [
                    Button.Create(Loc.Get(P.PlayAll), v.PlayAll, ButtonAppearance.Accent, glyph: Icons.Play, isEnabled: any),
                    Button.Create(Loc.Get(P.Shuffle), v.Shuffle, ButtonAppearance.Standard, glyph: Icons.Shuffle, isEnabled: any),
                    add,
                ]
                : [add],
        };
    }

    /// <summary>The head of every pivot but Home (boards 19, 20): the mosaic thumb, the title over the stats, the actions beside (under at Compact).</summary>
    static Element CompactHead(LocalCollectionPageProps p, LocalCollectionView view, CommonLocalCollectionLayout lay, float g, string titleKey)
    {
        // The head row carries the page-title key: its left edge (the thumb's) is the page's gutter.
        Element ident = new BoxEl
        {
            Key = titleKey, Direction = 0, Gap = L.ThumbGap, AlignItems = FlexAlign.Center, Grow = 1f, Shrink = 1f, Basis = 0f, MinWidth = 0f,
            Children =
            [
                X.Thumb(view, L.ThumbSize),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        R.Type.HeroTitle(p.Title) with { Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                        X.StatsLine(view) with { MaxLines = 2, Wrap = TextWrap.WrapWholeWords },
                    ],
                },
            ],
        };
        return X.Gut("lc-compact-head", g, new BoxEl
        {
            Key = L.HeroKey, Direction = (byte)(lay.ActionsBeside ? 0 : 1), Gap = L.ThumbGap, AlignItems = lay.ActionsBeside ? FlexAlign.Center : FlexAlign.Stretch,
            MinWidth = 0f,
            Children = [ident, Actions(p.Verbs, view, wrap: !lay.ActionsBeside, play: p.Pivot.Peek() != LocalPivot.Songs) with { Shrink = 0f }],
        }, top: L.CompactTop, bottom: L.CompactBottom);
    }

    // ── the pivot ────────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly TemplateParts s_pivotParts = new();
    static readonly string[] s_pivotKeys = ["lc-pivot:home", "lc-pivot:albums", "lc-pivot:artists", "lc-pivot:songs", "lc-pivot:folders", "lc-pivot:insights"];

    static Element Pivot(LocalCollectionPageProps p, LocalPivot current, float g)
    {
        var labels = new string[6];
        for (int i = 0; i < labels.Length; i++) labels[i] = PivotWord((LocalPivot)i);
        var style = new SelectorBarStyle
        {
            RestColor = Tok.TextSecondary, SelectedColor = Tok.TextPrimary, ItemHeight = L.PivotItemH, ItemGap = L.PivotGap,
        };
        var set = p.SetPivot;
        Element bar = SelectorBar.Create(labels, new Signal<int>((int)current), onChange: i => set((LocalPivot)i), parts: s_pivotParts, style: style, itemKeys: s_pivotKeys);
        // "Edit home" at the row's end (board 18). Its slot stays on every pivot (faded out and inert off Home), so the pivot never reflows.
        bool home = current == LocalPivot.Home;
        Element edit = Embed.Comp(new CommonLocalEditHome.Props(p, s_editTrigger, FlyoutPlacement.BottomEdgeAlignedRight), static () => new CommonLocalEditHome())
            with { Key = CommonLocalEditHome.TriggerKey + ":host" };
        return X.Gut("lc-pivot-gut", g, new BoxEl
        {
            Key = L.PivotKey, Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Gap = L.ChipGap,
                    Children =
                    [
                        new ScrollEl
                        {
                            Horizontal = true, ContentSized = true, Grow = 1f, Shrink = 1f, MinWidth = 0f, SuppressScrollBar = true, AutoEdgeFade = true,
                            Content = new BoxEl { Direction = 0, Shrink = 0f, Children = [bar] },
                        },
                        new BoxEl { Shrink = 0f, Opacity = home ? 1f : 0f, HitTestVisible = home, Children = [edit] },
                    ],
                },
                new BoxEl { Height = L.PivotHairline, Fill = Tok.StrokeDividerDefault },
            ],
        });
    }

    /// <summary>WinUI/Classic's "Edit home": a subtle button with the sliders glyph, the board's.</summary>
    static readonly Func<Action, Action<NodeHandle>, Element> s_editTrigger = static (toggle, realize)
        => Button.Create(Loc.Get(P.EditHome), toggle, ButtonAppearance.Subtle, glyph: Icons.Equalizer) with { Key = CommonLocalEditHome.TriggerKey, OnRealized = realize };

    /// <summary>A pivot's word (the core's words; the theme's case role applies).</summary>
    public static string PivotWord(LocalPivot p) => p switch
    {
        LocalPivot.Albums => Loc.Get(P.PivotAlbums),
        LocalPivot.Artists => Loc.Get(P.PivotArtists),
        LocalPivot.Songs => Loc.Get(P.PivotSongs),
        LocalPivot.Folders => Loc.Get(P.PivotFolders),
        LocalPivot.Insights => Loc.Get(P.PivotInsights),
        _ => Loc.Get(P.PivotHome),
    };
}
