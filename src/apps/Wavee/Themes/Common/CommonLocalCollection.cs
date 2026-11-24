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
//      │     "lc-head"   the head, ONE form on every pivot (the library page's head, W-Library): the title in the library's page-title
//      │                 voice with the actions beside it, the meta line, the folders and the library's state; a pivot switch never moves it
//      │     the pivot   one node across every switch
//      │     "lc-body"   the pivot's body, keyed per pivot: the structure's library pivot recipe (WinUI's: the new body slides in from the
//      │                 side of its tab and decelerates, the old one fades fast; PageMotionPolicy.LibraryPivot), else the core's quick fade
//      │                 └─ its list, or the detail open over it (LocalOpened: a folder, an album's folder), keyed per step: list -> detail is
//      │                    the structure's drill (WinUI's drill in, Back its drill back; CommonLocalCollectionParts.DetailMotion) while the
//      │                    tile's cover flies into the detail's header and home again (Shell.FlyInPage; the swap then keeps only its fades)
//      │   Home:   ScrollView "lc-scroll:home" [ Recently added (Today / This week / Earlier this month) · Lossless · Shuffle by decade ]
//      │   others: the pivot's body:
//      │           Albums  the library's toolbar (sort, Filter, view) · house cards or rows      Artists  the same, round
//      │                   (the list views split where the library splits: the list beside the pick's library pane, CommonLocalCollection.Split.cs)
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
        // The structure's library controls (WinUI's view pair and its menu); Classic registers none and gets the page's own pair.
        var registry = ctx.Hooks.UseContext(ViewRegistry.Context);
        var controls = registry is not null && registry.TryGetStyle(StyleKeys.LibraryControls, out var c) ? c : null;
        return Embed.Comp(new HostProps(p, gutterOf(tiers), lay, titleKey, controls), static () => new Host()) with { Key = L.PageKey };
    }

    sealed record HostProps(LocalCollectionPageProps Page, float Gutter, CommonLocalCollectionLayout Lay, string TitleKey, LibraryControlsStyle? Controls);

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
        readonly X.DetailMotion _detail = new();
        (string Key, LayoutTransition? Enter) _step;
        // Home's shelves as last rendered: a change is "Edit home" showing or hiding one (X.HomeBody's reveal).
        IReadOnlyList<LocalShelfSlot>? _shelves;
        bool _reveal;

        public override Element Render()
        {
            var hp = UseProps<HostProps>();
            _hp = hp;
            var p = hp.Page;
            _facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal));
            _pending ??= () => p.View.Value.Version == 0;
            _content ??= () => Page(_hp!, _hp!.Page.View.Value, _swap, _step, _facts!.Peek(), _reveal);
            _shimmer ??= () => Page(_hp!, X.Seed, null, default, _facts!.Peek(), false);
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
            // The body under the pivot: its list or the open detail, and the step's drill when it changed under the same pivot.
            _step = _detail.Step(CommonLocalCollectionRules.BodyKey, pivot, p.Opened.Value, _facts.Peek());
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

    /// <summary>The body switch for a pivot change: the structure's library pivot recipe (<see cref="PageMotionPolicy.LibraryPivot"/>; WinUI's slides the
    /// new body in from the side of its tab and decelerates, the old one fades fast, and answers its quick fade under reduced motion), else the
    /// core's quick same-kind fade.</summary>
    static LayoutTransition? SwapOf(bool fromRight, ThemeFacts facts)
        => facts.Policies.PageMotion.LibraryPivot is { } pivot ? pivot(fromRight, facts.Tiers) : Design.Nav.SameKindSwap;

    /// <summary>The body's switch clock: WinUI's incoming 300 ms on an ease-out (its
    /// (0, 0, 0, 1) decelerate carried most of a short Compact slide in one frame: the trace's "instant"), which also takes the outgoing node
    /// most of its way out early. A node carries one clock for both legs, so the recipe's enter leg (<see cref="SwapOf"/>: the side, the travel)
    /// rides it as the node's Enter.</summary>
    static FluentGpu.Animation.MotionTokenDef SwapClock => FluentGpu.Animation.MotionTokenDef.Eased(L.HeadInMs, Easing.EaseOut);
    static readonly EnterExit s_fade = new(Opacity: 0f, Active: true);

    static Element Page(HostProps hp, LocalCollectionView view, LayoutTransition? swap, (string Key, LayoutTransition? Enter) step, ThemeFacts facts, bool reveal)
    {
        var p = hp.Page;
        var pivot = p.Pivot.Peek();
        var opened = p.Opened.Peek();
        bool detail = opened?.IsOpenOver(pivot) == true;
        // No music folder: the Local Files onboarding (S4), shared with the list.
        if (p.Onboarding() is { } onboarding) return X.Gut("lc-empty", hp.Gutter, onboarding, grow: true);
        float g = hp.Gutter;
        // "Your files and Spotify": the review takes the page's place while it is open (board 29: On this PC › Review matches).
        if (p.Link is { } link && link.Reviewing.Peek() is not null) return X.LinkReview(link, g, p.RouteKey);
        Element bar = Pivot(p, pivot, g);
        Element head = Head(p, view, hp.Lay, g, hp.TitleKey);
        Element body = detail ? X.FolderBrowser(view, opened, p.OpenFolder, p.Verbs, hp.Lay, g, CrumbRoot(p, pivot), p.RouteKey) : pivot switch
        {
            LocalPivot.Home => ScrollView(new BoxEl
            {
                Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, L.PageBottom),
                Children = [X.LinkOffer(p.Link, g), .. X.HomeBody(view, p.Verbs, p.SetPivot, hp.Lay, g, p.Shelves.Peek(), reveal, house: true)],
            }) with { Key = L.ScrollKey + ":home", Grow = 1f, MinWidth = 0f, ScrollKey = p.RouteKey + ":home" },
            LocalPivot.Albums => X.LibraryAlbums(view, p, hp.Lay, g, hp.Controls),
            LocalPivot.Artists => X.LibraryArtists(view, p, hp.Lay, g, hp.Controls),
            LocalPivot.Songs => X.Gut("lc-songs", g, p.Songs(), grow: true),
            LocalPivot.Folders => X.FolderBrowser(view, null, p.OpenFolder, p.Verbs, hp.Lay, g, p.Title, p.RouteKey),
            _ => X.Insights(view, hp.Lay, g, p.RouteKey),
        };
        // The shimmer has no step (its key is the list's).
        string slot = step.Key ?? L.BodyKey + ":" + pivot.ToString() + ":list";
        body = X.DetailSlot(slot, body, step.Enter, X.DetailMotion.Exit(pivot, opened, facts));
        return new BoxEl
        {
            Direction = 1, Grow = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                // One head on every pivot: a switch moves the body alone. Its height eases where its content re-wraps (a folder chip landing,
                // the first scan's line), so the pivot under it glides.
                new BoxEl { Key = L.HeadKey, Direction = 1, MinWidth = 0f, Shrink = 0f, ClipToBounds = true, Layout = L.HeightReflow, Children = [head] },
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

    // ── the head (the library page's head, W-Library) ──────────────────────────────────────────────────────────────

    /// <summary>The page's head, one form on every pivot: "On this PC" in the library's page-title voice (Title Large, one line), the actions beside it
    /// (under the words at Compact), the meta line under the title ("2 songs · 1 artist · 4 minutes"), and the music folders with the library's state
    /// (or the first scan's line and bar) under that. The title row carries the page-title key: its left edge is the page's gutter.</summary>
    static Element Head(LocalCollectionPageProps p, LocalCollectionView view, CommonLocalCollectionLayout lay, float g, string titleKey)
    {
        Element words = new BoxEl
        {
            Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
            Children =
            [
                R.Type.SurfaceDisplay(p.Title) with { Key = titleKey, Color = Tok.TextPrimary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
                R.Type.TrackTitle(X.StatsText(view, withSongs: true)) with
                {
                    Key = "lc-meta", Color = Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    Margin = new Edges4(0f, L.MetaGap, 0f, 0f),
                },
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, Margin = new Edges4(0f, L.StatusTop, 0f, 0f),
                    Children = [X.Chips(view, p.Verbs, lay.ControlH, p.FirstScan() ? p.ScanLine(float.NaN, true) : null)],
                },
            ],
        };
        Element actions = Actions(p.Verbs, view, lay, play: p.Pivot.Peek() != LocalPivot.Songs);
        return X.Gut("lc-head", g, new BoxEl
        {
            Key = L.HeroKey, Direction = (byte)(lay.ActionsBeside ? 0 : 1), Gap = lay.ActionsBeside ? L.ThumbGap : L.ActionsUnderGap,
            AlignItems = lay.ActionsBeside ? FlexAlign.Start : FlexAlign.Stretch, MinWidth = 0f,
            Children = [words, actions],
        }, top: lay.HeadTop, bottom: L.HeadBottom);
    }

    /// <summary>Play all · Shuffle · Add music, the house buttons. <paramref name="play"/> false leaves Play and Shuffle to the Songs table's own bar (one
    /// home per verb). Beside the title they stand on its line's middle.</summary>
    static Element Actions(LocalCollectionVerbs v, LocalCollectionView view, CommonLocalCollectionLayout lay, bool play)
    {
        bool any = view.Songs > 0;
        var size = lay.Tier == WidthTier.Compact ? ControlSize.Large : ControlSize.Medium;   // the 40 target at Compact
        Element add = Button.Create(Loc.Get(P.AddMusic), v.AddMusic, ButtonAppearance.Standard, size, glyph: Icons.Add);
        return new BoxEl
        {
            Key = "lc-actions", Direction = 0, Gap = L.ActionGap, AlignItems = FlexAlign.Center, Wrap = !lay.ActionsBeside, Shrink = 0f, MinWidth = 0f,
            MinHeight = lay.ActionsBeside ? R.Type.SurfaceDisplay("").LineHeight : 0f,
            Children = play
                ?
                [
                    Button.Create(Loc.Get(P.PlayAll), v.PlayAll, ButtonAppearance.Accent, size, glyph: Icons.Play, isEnabled: any),
                    Button.Create(Loc.Get(P.Shuffle), v.Shuffle, ButtonAppearance.Standard, size, glyph: Icons.Shuffle, isEnabled: any),
                    add,
                ]
                : [add],
        };
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
            RestColor = Tok.TextSecondary, SelectedColor = Tok.TextPrimary, ItemHeight = L.PivotItemH, ItemGap = L.PivotGap, LeadingInset = L.PivotLead,
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

    /// <summary>The breadcrumb's first word over a detail: the Folders browser's own ("On this PC"); an album's folder opened over Albums or Home
    /// starts at that pivot.</summary>
    static string CrumbRoot(LocalCollectionPageProps p, LocalPivot pivot) => pivot == LocalPivot.Folders ? p.Title : PivotWord(pivot);

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
