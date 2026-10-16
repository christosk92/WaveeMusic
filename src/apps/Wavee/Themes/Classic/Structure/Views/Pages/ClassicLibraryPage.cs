// ── Themes/Classic/Structure/Views/Pages/ClassicLibraryPage.cs — Classic's `Pages.Library` (themes plan 6.1, T10c1) ───────────────────────────
//
// From `Entities\User.Page.Library.cs` (`LibraryPage.Render`, `LeftColumn`, the grips, `DetailColumn`, `ReaderColumn`, `Collapsed`) and `Entities\User.UI.cs`
// §5 (the crumb bar, the column grip, the two panel rungs): the master-detail page's frame. The navigator column (the toolbar and the bound list: `ClassicLibraryPage.Toolbar.cs`,
// `.Nav.cs`), the two seams, the detail pane beside it, the three search columns (`.Search.cs`) and the collapsed breadcrumb drill-in. The host (`User.LibraryPage`, a `[CoreHost]`)
// keeps the persisted signals, the shape memo, the selection, the search matcher, the collapse state and every verb.
//
// ── THE SHAPE ────────────────────────────────────────────────────────────────────────────────────────────────────────
//
//  root BoxEl (OnBoundsChanged → `SetPageWidth` → the host's UseTier over the structure's layout ladder → `Collapsed`)
//  ├─ WIDE "lib:wide": Section "lib:nav"[Toolbar · ListBody | LeftSearchBody] │ "lib:grip" ColumnGrip(LeftW) │ right
//  │        right = DetailColumn "lib:detail" (albums, podcasts) | ReaderColumn "lib:reader" (artists, no third pane)
//  │                | the two SEARCH column shapes (MidW + "lib:midgrip")
//  ├─ WIDE "lib:wide:saved": Toolbar · the Your Episodes page
//  └─ COLLAPSED "lib:collapsed": CrumbBar "lib:crumbs" · depth 0 (toolbar + list) | depth 1 (the pane / the reader) | depth 2 (the artists search's track hits)
//
// EVERY BRANCH ROOT ON THAT DIAGRAM IS KEYED, and the collapse arm is why. The reconciler matches a KEYED child only against the same key and an UNKEYED child by ordinal among
// its unkeyed siblings, so two unkeyed branch roots at the same ordinal are ONE node that survives the swap, subtree and all. That is what happened here: `lib:wide` and
// `lib:collapsed` paired, and one child deeper the crumb bar paired with the nav column. The nav column's `Width` is the persisted `LeftW` SIGNAL — a bound channel, and bound wiring
// runs at MOUNT — so the reused node came out of the collapse holding the crumb bar's static `Width = NaN`, took the whole page and pushed the reader off screen. Keys, not props, are what
// turn a branch swap into the remount a bind needs.
//
// THE COLLAPSE IS THE STRUCTURE'S. The layout ladder (`Rules\Tiers.Library.cs`: split from 600 of the page's own width, held 24 either way) is resolved by the host's `UseTier` over the width
// this view reports (`SetPageWidth`): the page's FIRST frame is already in its tier (a same-frame seed), and a crossing commits on the first non-resized frame, where the two arms cross-fade
// (`s_arm`) instead of snapping. The host owns the answer (`Collapsed`) because the selection, the crumbs and the persisted pick all read it; the tier's numbers are the record
// (`ClassicLibraryTiers.Of`). The persisted column widths are never written by a tier. Stable elements and delegates the old component kept as fields (the picker, the two grips, the sticky letter and the strip, the
// slot templates) live in a per-template-instance cache (`ctx.Hooks.UseRef`), so keys, binds and mounts are identical to the old tree.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's library page.</summary>
internal static partial class ClassicLibraryPage
{
    /// <summary>The registered template of <see cref="ViewKeys.LibraryPage"/>.</summary>
    public static Element Template(LibraryPageProps props, in ViewContext ctx)
    {
        var cache = ctx.Hooks.UseRef(new PageCache()).Value;
        cache.Props = props;
        // The layout tier's record: the structure's ladder (the host's tier signal, `props.Collapsed`) decides the one-column drill-in, the content tier the split's numbers.
        cache.Layout = ClassicLibraryTiers.Of(ctx.Tier(), props.Collapsed);
        cache.Prepare();

        Element inner;
        if (props.Collapsed)
            inner = Collapsed(props, cache);
        // Your Episodes (A2 plan §3.5): the standard toolbar's row-1 words ARE the mode switch for Podcasts. Selecting the word swaps the WHOLE body for the flat episode table, but
        // the toolbar (and so the word that switches back) stays put.
        else if (props.SavedEpisodesMode)
            inner = new BoxEl
            {
                Key = "lib:wide:saved", Animate = s_arm,
                Direction = 1, Grow = 1f, MinHeight = 0f, AlignItems = FlexAlign.Stretch,
                Children = [Toolbar(props, cache), props.SavedEpisodes()],
            };
        else
        {
            bool artists = props.Entity == EntityKind.Artist;
            Element right = props.FullSearch
                ? (artists ? SearchArtistColumns(props, cache) : SearchAlbumDetail(props, cache))
                : (artists ? ReaderColumn(props, cache.Layout) : DetailColumn(props, cache.Layout));
            // KEYED, and so is `Collapsed`'s root: see the note in the file header.
            inner = new BoxEl
            {
                Key = "lib:wide", Animate = s_arm,
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Stretch,
                Children = [LeftColumn(props, cache), cache.LeftGrip ??= LeftGrip(props), right],
            };
        }
        // The page publishes no shell material and tints nothing: the library is the app's accent-neutral browser.
        return new BoxEl { Direction = 1, Grow = 1f, AlignItems = FlexAlign.Stretch, OnBoundsChanged = cache.OnBounds, Children = [inner] };
    }

    // ══ THE PER-INSTANCE CACHE ═══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>What the old page kept in fields: the latest props, the elements built once (the picker, the grips, the sticky letter and the strip) and the stable delegates the
    /// navigator's region and list were handed. Built lazily by <see cref="Prepare"/>; one per mounted template.</summary>
    sealed class PageCache
    {
        public LibraryPageProps Props = null!;
        public ClassicLibraryLayout Layout = null!;
        public Element? Picker, PickerCompact, LeftGrip, MidGrip, Sticky, Strip;
        public Action<RectF>? OnBounds;
        public Func<int, Element>? NavContent;
        public Func<Element>? NavNoMatch, NavShimmer;
        Func<BoundItemScope<LibraryNavItem>, Element>? _slotT, _slotCompactT, _cardT, _cardCompactT;

        public void Prepare()
        {
            OnBounds ??= Measured;
            NavContent ??= _ => NavList(this);
            NavNoMatch ??= (static () => EmptyCompact(Loc.Get(Strings.Library.NoMatch)));
            // The persisted view cell, never the mount: a pending navigator is never asked for its mount.
            NavShimmer ??= () => ClassicLibraryPage.NavShimmer(Props.View().Value, Props.Size().Value, Props.Entity);
        }

        /// <summary>The page's measured bounds, reported to the host: its layout ladder (`ClassicLibraryTiers.Layout`) over this width decides the one-column drill-in, in the
        /// page's first frame from a same-frame seed and on a crossing on the first non-resized frame (the host's value gate drops every report that did not move).</summary>
        void Measured(RectF r)
        {
            if (r.W > 0f) Props.SetPageWidth(r.W);
        }

        /// <summary>The slot template of a mount's template kind: one stable delegate per kind for the page's life.</summary>
        public Func<BoundItemScope<LibraryNavItem>, Element> TemplateOf(LibraryNavTemplate template)
        {
            var entity = Props.Entity;
            return template switch
            {
                LibraryNavTemplate.RowCompact => _slotCompactT ??= scope => NavSlot(scope, entity, compact: true),
                LibraryNavTemplate.Card => _cardT ??= scope => NavCard(scope, entity, compact: false),
                LibraryNavTemplate.CardCompact => _cardCompactT ??= scope => NavCard(scope, entity, compact: true),
                _ => _slotT ??= scope => NavSlot(scope, entity, compact: false),
            };
        }
    }

    // ══ THE LEFT COLUMN AND THE SEAMS ════════════════════════════════════════════════════════════════════════════════

    /// <summary>The navigator column. KEYED: its <c>Width</c> is the persisted <c>LeftW</c> SIGNAL — a bound channel, wired once at mount — so this node may only ever be paired with
    /// another <c>lib:nav</c>. Anything else reusing it inherits a width nothing will ever write again.</summary>
    static Element LeftColumn(LibraryPageProps p, PageCache c) => Section with
    {
        Key = "lib:nav",
        // The remembered width (`library.<kind>.leftw`, written only by the grip's release), which gives way by flex, down to the grip's floor, when the pane's floor
        // (`RightMin`) would not fit beside it: the tier never rewrites the stored width.
        Width = p.LeftW(), Shrink = 1f, MinWidth = ClassicLibraryRules.LeftMin,
        Children = [Toolbar(p, c), p.FullSearch ? LeftSearchBody(p, c) : ListBody(p, c)],
    };

    /// <summary>The two column seams, each built ONCE into its cached field — so the key goes on at construction rather than through a per-render <c>with</c> clone. Both carry a bound
    /// <c>Fill</c> and a <c>Splitter</c> bound to their own width signal, which is the reason they may not be paired by ordinal with anything else.</summary>
    static Element LeftGrip(LibraryPageProps p)
        => ColumnGrip(p.LeftW(), ClassicLibraryRules.LeftMin, ClassicLibraryRules.LeftMax, p.CommitLeft) with { Key = "lib:grip" };

    /// <inheritdoc cref="LeftGrip"/>
    static Element MidGrip(LibraryPageProps p)
        => ColumnGrip(p.MidW(), ClassicLibraryRules.MidMin, ClassicLibraryRules.MidMax, p.CommitMid) with { Key = "lib:midgrip" };

    /// <summary>THE column seam (W18, Y1): a 16-DIP grip strip with a 1-DIP <c>StrokeDividerDefault</c> hairline centred behind the splitter's hover thumb — the only thing that splits
    /// the navigator from the detail on the one content card. The seam brush is a LIVE thunk, so a theme swap re-resolves it without a render.</summary>
    static Element ColumnGrip(Signal<float> width, float min, float max, Action onCommit) => new BoxEl
    {
        Width = Splitter.StripW, Shrink = 0f, ZStack = true,
        Children =
        [
            new BoxEl { Width = ClassicLibraryRules.SeamHairline, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Center, HitTestVisible = false,
                        Fill = Prop.Of(static () => Tok.StrokeDividerDefault) },
            new BoxEl { Direction = 1, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch,
                        Children = [Splitter.Create(width, onCommit, new Splitter.SplitterOptions { Min = min, Max = max })] },
        ],
    };

    /// <summary>A column of the page: the navigator and every reading pane are PLAIN sections on the one content card (Y1) — no layer or card rung of their own, so the card shows one
    /// tint and the seam's divider does the separating.</summary>
    static BoxEl Section => new() { Direction = 1, ClipToBounds = true };

    static Element EmptyCompact(string text)
        => Controls.Vacancy(Controls.VacancyVoice.NoMatch, Controls.VacancyScale.Compact, title: text, subtitle: "");

    // ══ THE RIGHT COLUMN (BROWSE) ════════════════════════════════════════════════════════════════════════════════════

    static Element DetailColumn(LibraryPageProps p, ClassicLibraryLayout layout)
    {
        if (!p.HasSelection) return Placeholder(p.Entity == EntityKind.Show ? Strings.Library.SelectShow : Strings.Library.SelectAlbum);
        // NO `Key` on the pane: a selection change RE-PUSHES its props and the pane re-skins in place (it owns its own crossfade).
        return Section with { Key = "lib:detail", Grow = 1f, Basis = 0f, MinWidth = layout.RightMin, Children = [p.Pane()] };
    }

    /// <summary>The artists view's ONE right-hand rung (W3): the reader. There is no MidW here and no third pane — the reader is the leaf.</summary>
    static Element ReaderColumn(LibraryPageProps p, ClassicLibraryLayout layout)
    {
        if (!p.HasSelection) return Placeholder(Strings.Library.SelectArtist);
        return Section with { Key = "lib:reader", Grow = 1f, Basis = 0f, MinWidth = layout.RightMin, Children = [p.Pane()] };
    }

    static Element Placeholder(string key) => Section with { Key = "lib:empty", Grow = 1f, Children = [EmptyCompact(Loc.Get(key))] };

    // ══ THE COLLAPSED DRILL-IN (W7) ══════════════════════════════════════════════════════════════════════════════════

    /// <summary>The drill: opacity only, 160 ms, and the arriving leaf slides 12 DIP in from the right. A depth swap is a mount/unmount of a keyed wrapper, so the engine's enter/exit
    /// terminals do the work — no frame clock.</summary>
    static readonly LayoutTransition s_drillIn = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(ClassicLibraryRules.DrillMs, Easing.SmoothOut),
        Enter: new EnterExit(Dx: ClassicLibraryRules.DrillDx, Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ClassicLibraryRules.DrillMs, Easing.SmoothOut));

    /// <summary>The two arms (the split columns and the one-column drill-in) cross-fade when the layout tier crosses: the arriving arm fades in while the leaving one fades out
    /// as an exit orphan under it, never a snap (themes plan 3.3). Opacity only, the drill's own 160 ms.</summary>
    static readonly LayoutTransition s_arm = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(ClassicLibraryRules.DrillMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ClassicLibraryRules.DrillMs, Easing.SmoothOut));

    static readonly LayoutTransition s_drillRoot = new(
        TransitionChannels.Opacity, TransitionDynamics.Tween(ClassicLibraryRules.DrillMs, Easing.SmoothOut),
        Enter: new EnterExit(Opacity: 0f, Active: true), Exit: new EnterExit(Opacity: 0f, Active: true),
        ExitDynamics: TransitionDynamics.Tween(ClassicLibraryRules.DrillMs, Easing.SmoothOut));

    /// <summary>TWO rungs when BROWSING, for every kind: depth 0 the navigator, depth 1 the pane (albums / podcasts) or the reader (artists). SEARCH mode keeps its three columns, so the
    /// artists view keeps a third rung for them: depth 0 artist hits, 1 album hits, 2 track hits (§0.9-§0.10 — search is untouched by the rework).</summary>
    static Element Collapsed(LibraryPageProps p, PageCache c)
    {
        int depth = p.Depth;
        bool artists = p.Entity == EntityKind.Artist;
        Element body;
        if (depth == 0)
            body = new BoxEl
            {
                Key = "col:nav",   // keyed like its three alternatives; the "depth:N" wrapper already separates them
                Direction = 1, Grow = 1f,
                Children = [Toolbar(p, c), p.SavedEpisodesMode
                    ? p.SavedEpisodes()
                    : (p.FullSearch ? LeftSearchBody(p, c) : ListBody(p, c))],
            };
        else if (depth >= 2)
            body = Section with { Key = "col:tracks", Grow = 1f, Basis = 0f, Children = [TrackHits(p)] };
        else if (artists)
            body = Section with
            {
                Key = "col:reader", Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children = [p.FullSearch ? AlbumHits(p, explain: false) : p.Pane()],
            };
        else
            body = Section with
            {
                Key = "col:detail", Grow = 1f, Basis = 0f,
                Children = [p.FullSearch ? TrackHits(p) : p.Pane()],
            };
        Element wrapper = new BoxEl
        {
            Key = "depth:" + depth, Animate = depth > 0 ? s_drillIn : s_drillRoot,
            Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, MinWidth = 0f, Children = [body],
        };
        // KEYED against `Template`'s "lib:wide" — see the file header: two unkeyed branch roots paired by ordinal, and the crumb bar inherited the nav column's node (and lost its bound
        // Width) on the way back out.
        return new BoxEl
        {
            Key = "lib:collapsed", Animate = s_arm, Direction = 1, Grow = 1f, ClipToBounds = true,
            // The root rung's head is the kind chips and the title (C-Library-Compact), so the breadcrumb stands only over a rung that is a leaf.
            Children = depth == 0 ? [wrapper] : [CrumbBar(p.Crumbs, p.PickDepth), wrapper],
        };
    }

    /// <summary>The collapsed layout's breadcrumb (W14-W17): a plain band on the content card (Y1), a 1-DIP divider under it.
    /// <para>KEYED. It is the first child of the collapsed library's root and the nav column is the first child of the wide one, so with both unkeyed the reconciler paired the two by
    /// ordinal and reused ONE node for both — which handed the nav column this box's static <c>Width</c> in place of its own bound one (a static↔bound flip on a reused node is never
    /// re-wired — the engine's BindContract flags it). The key is what makes the crossing a remount.</para></summary>
    static Element CrumbBar(IReadOnlyList<string> crumbs, Action<int> onPick) => new BoxEl
    {
        Key = "lib:crumbs",
        Direction = 1, Shrink = 0f,
        Children =
        [
            new BoxEl { Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S), Children = [BreadcrumbBar.Create(crumbs, onPick)] },
            new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };
}
